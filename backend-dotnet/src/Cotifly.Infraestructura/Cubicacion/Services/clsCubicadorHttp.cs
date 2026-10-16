using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Cubicador en vivo (HU-021.5, #133, con el contrato de HU-023.1, #156), mismo patrón que <c>clsCosteoNexo</c>: cliente HTTP propio
/// (<see cref="NombreCliente"/>), <b>sin reintentar los POST</b>, opciones validadas (<see cref="clsOpcionesCubicador"/>) y todo vía
/// <see cref="clsResultado{TValor}"/>. Rutas bajo <c>{Cubicador:UrlBase}</c>:
/// <list type="bullet">
/// <item><c>POST /recomendaciones</c> con <c>{idLineaSolicitud, origenSistema: "COTIFLY", claseViajeSeleccionada?, usuarioId,
/// mercancias: [{codigoMercancia, cantidad, largoCm, anchoCm, altoCm, pesoKg}]}</c>; responde <c>{idRecomendacion, resultado,
/// motivoSinSeleccion, condicionada, mercanciasCondicionadas[], vehiculos[], advertencias[], extradimension}</c>.</item>
/// <item><c>GET /tipos-mercancia</c>, <c>GET /tipos-carroceria</c> y <c>GET /vehiculos-catalogo</c> (HU-023, #176), con caché
/// (<see cref="clsCacheCatalogoCubicador{TFila}"/>).</item>
/// <item>HU-023 (#176): tope de 10.000 unidades antes de llamar (<c>CUBICACION_DEMASIADO_GRANDE</c>); un POST sin respuesta (tiempo agotado
/// o red) no se reintenta: se reconsulta <c>GET /recomendaciones?idLineaSolicitud=…&amp;origenSistema=COTIFLY</c> y, si la corrida existe y
/// su huella coincide con la petición (<see cref="DiferenciaConLaCarga"/>), se usa; un 422 del POST es un rechazo de los datos (D1: 400
/// <c>DIMENSIONES_INVALIDAS</c>, <c>POSICION_INVALIDA</c> o <c>CUBICADOR_PETICION_RECHAZADA</c>) y el de una consulta
/// <c>CUBICADOR_RESPUESTA_INVALIDA</c> (los dos visibles, nunca al respaldo); el
/// <c>comentarioCondicion</c> de las mercancías condicionadas sale de <c>GET /tipos-mercancia</c>. Los códigos se comparan sin distinguir
/// mayúsculas.</item>
/// </list>
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Contra el fallo silencioso</b> (<see cref="Validar"/>): con <c>ASIGNADO</c> cada vehículo debe traer
/// <c>idRecomendacionVehiculo</c>, <c>codigoVehiculo</c>, <c>codigoCarroceria</c>, <c>pesoTotalKg</c>, <c>volumenTotalM3</c>,
/// <c>porcentajeLlenadoVolumen</c> y <c>porcentajeLlenadoPeso</c>; un resultado o un motivo desconocido, un porcentaje fuera de 0 a 100 o
/// un código de más de <see cref="LargoCodigo"/> caracteres también rechazan la respuesta con <c>CUBICADOR_RESPUESTA_INVALIDA</c>.</item>
/// <item><b>Errores</b>: 400 con <c>{codigo, mensaje}</c> del contrato → el mismo código en <c>clsCodigosError</c> (rechazo, no caída);
/// 401/403 → <c>CUBICADOR_NO_AUTORIZADO</c> (caída: revisar la llave); 404, 5xx, red, circuito abierto y tiempo agotado →
/// <c>CUBICADOR_NO_DISPONIBLE</c> (caída); otro 4xx → <c>CUBICADOR_NO_DISPONIBLE</c> (rechazo). Una línea de warning por fallo, sin la
/// llave ni la traza. La cancelación del llamador se propaga.</item>
/// <item><b>Observabilidad</b>: el resultado de cada recomendación queda en un log informativo con la ruta, el resultado, el motivo, la
/// marca de condicionada y el número de vehículos (sin datos del cliente).</item>
/// </list>
/// </remarks>
public sealed class clsCubicadorHttp(
    HttpClient http,
    IOptions<clsOpcionesCubicador> opciones,
    clsCacheCatalogoCubicador<clsTipoMercanciaCubicador> cacheMercancias,
    clsCacheCatalogoCubicador<clsTipoCarroceriaCubicador> cacheCarrocerias,
    clsCacheCatalogoCubicador<clsTipoVehiculoCubicador> cacheVehiculos,
    ILogger<clsCubicadorHttp> logger) : IClsCubicador
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> del cubicador (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "Cubicador";

    /// <summary>Valor de <c>origenSistema</c> en <c>POST /recomendaciones</c>.</summary>
    public const string OrigenSistema = clsPeticionRecomendacion.OrigenSistema;

    public const string RutaRecomendaciones = "/recomendaciones";
    public const string RutaTiposMercancia = "/tipos-mercancia";
    public const string RutaTiposCarroceria = "/tipos-carroceria";
    /// <summary>HU-023 (#176): el catálogo canónico de vehículos (<c>2L2</c>, <c>C2</c>, <c>3S3</c>…); <c>/vehiculos</c> es el del motor 3D.</summary>
    public const string RutaTiposVehiculo = "/vehiculos-catalogo";

    public const string MensajeNoDisponible = "El cubicador no está disponible en este momento: intenta de nuevo más tarde.";

    public const string MensajeRespuestaInvalida =
        "El cubicador respondió algo incompleto o fuera del contrato: no se guardó nada. Intenta de nuevo o avisa a soporte.";

    public const string MensajeNoAutorizado = "El cubicador rechazó la llave de Cotifly: avisa a soporte (Cubicador:ApiKey).";

    /// <summary>
    /// D1 (E2E 2026-10-16): mensaje de un 422 de <c>POST /recomendaciones</c> que no señala una mercancía (<c>CUBICADOR_PETICION_RECHAZADA</c>,
    /// 400). Un 422 de una consulta (GET) sigue siendo <c>CUBICADOR_RESPUESTA_INVALIDA</c> con <see cref="MensajeConsultaRechazada"/>.
    /// </summary>
    public const string MensajePeticionRechazada =
        "El cubicador rechazó los datos de la carga (medidas, peso o cantidad): revísalos y vuelve a intentarlo. No se guardó nada.";

    /// <summary>HU-023 (#176): mensaje de un 422 de una consulta al cubicador (la petición de Cotifly no cumple su esquema).</summary>
    public const string MensajeConsultaRechazada =
        "El cubicador rechazó el formato de la petición de Cotifly (422): no se guardó nada. Avisa a soporte.";

    /// <summary>Reintentos de las consultas GET (los POST no se reintentan nunca).</summary>
    public const int ReintentosLectura = 2;

    /// <summary>Tamaño máximo de una respuesta del cubicador.</summary>
    public const long LimiteBytes = 1024 * 1024;

    /// <summary>Filas máximas que se leen de una respuesta (vehículos o catálogo).</summary>
    public const int MaximoFilas = 5_000;

    /// <summary>Largo máximo de un código del cubicador (vehículo, carrocería o mercancía; columnas de 64 a 100 caracteres).</summary>
    public const int LargoCodigo = 64;

    /// <summary>Largo máximo del mensaje del cubicador que se copia en un error para el comercial.</summary>
    public const int LargoMensajeCubicador = 300;

    /// <summary>Peso máximo de un vehículo (kg), el mismo tope que la captura.</summary>
    public const decimal MaximoPesoKg = 999_999_999m;

    /// <summary>Mensajes de Cotifly de los rechazos 400 del contrato (el código del cubicador se conserva tal cual).</summary>
    public static readonly IReadOnlyDictionary<string, string> Rechazos = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [clsCodigosError.MercanciasRequeridas] = "El cubicador necesita al menos una mercancía en la ruta.",
        [clsCodigosError.MercanciaDesconocida] = "El cubicador no reconoce una de las mercancías (codigo_mercancia): escógela del catálogo.",
        [clsCodigosError.MercanciaNoCubicable] = "Una de las mercancías no se puede cubicar: revisa el tipo de mercancía.",
        [clsCodigosError.DimensionesInvalidas] = "El cubicador rechazó las medidas o el peso de una mercancía: revísalos.",
        [clsCodigosError.CubicacionDemasiadoGrande] = clsMercanciaCubicador.MensajeDemasiadoGrande,
    };

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaConfigurado => _urlBase is not null && !string.IsNullOrWhiteSpace(opciones.Value.ApiKey);

    public bool RespaldoSiFalla => opciones.Value.RespaldoSiFalla;

    public TimeSpan PresupuestoCaptura => TimeSpan.FromSeconds(opciones.Value.PresupuestoCapturaSegundos);

    public async Task<clsResultado<clsRecomendacionCubicador>> RecomendarAsync(
        clsPeticionRecomendacion peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        if (clsMercanciaCubicador.ExcedeTope(peticion.Mercancias))
        {
            // HU-023 (#176): el tope de 10.000 unidades se valida antes de llamar (el cubicador respondería el mismo 400).
            logger.LogWarning("La ruta {IdLineaSolicitud} supera las {Maximo} unidades del cubicador: no se le llama.",
                peticion.IdLineaSolicitud, clsMercanciaCubicador.MaximoUnidades);
            return new clsErrorDominio(clsCodigosError.CubicacionDemasiadoGrande, clsMercanciaCubicador.MensajeDemasiadoGrande);
        }

        var cuerpo = new clsRecomendacionWire(
            peticion.IdLineaSolicitud,
            OrigenSistema,
            string.IsNullOrWhiteSpace(peticion.ClaseViajeSeleccionada) ? null : peticion.ClaseViajeSeleccionada.Trim(),
            string.IsNullOrWhiteSpace(peticion.UsuarioId) ? null : peticion.UsuarioId.Trim(),
            [.. peticion.Mercancias.Select(m => new clsMercanciaWire(m.CodigoMercancia, m.Cantidad, m.LargoCm, m.AnchoCm, m.AltoCm, m.PesoKg))]);
        var respuesta = await EnviarAsync(HttpMethod.Post, RutaRecomendaciones, cuerpo, cancellationToken);
        if (respuesta.EsFallo)
        {
            if (!SinRespuesta(respuesta.Error))
            {
                return respuesta.Error;
            }

            // HU-023 (#176, guía §4 y §7): el POST no se reintenta a ciegas. Sin respuesta (tiempo agotado o red), la corrida pudo quedar
            // creada: se reconsulta la última de la línea y, si existe y corresponde a esta carga, se usa. Si no, sigue la caída.
            var reconsultada = await ReconsultarAsync(peticion, cancellationToken);
            if (reconsultada is null)
            {
                return respuesta.Error;
            }

            return await CompletarAsync(peticion, reconsultada, "reconsulta tras un POST sin respuesta", cancellationToken);
        }

        clsResultado<clsRecomendacionCubicador> leida;
        using (var documento = respuesta.Valor)
        {
            leida = Validar(documento.RootElement);
        }

        if (leida.EsFallo)
        {
            logger.LogWarning("El cubicador respondió una recomendación inválida para {IdLineaSolicitud} ({Motivo}): se rechaza y no se guarda nada.",
                peticion.IdLineaSolicitud, leida.Error.Detalle);
            return leida.Error;
        }

        return await CompletarAsync(peticion, leida.Valor, "POST", cancellationToken);
    }

    /// <summary>Clave de <see cref="clsErrorDominio.Datos"/> que marca un fallo sin respuesta del cubicador (tiempo agotado o red).</summary>
    public const string ClaveSinRespuesta = "sinRespuesta";

    /// <summary>¿El error es de una petición que salió y no tuvo respuesta (el cubicador pudo haberla procesado)?</summary>
    private static bool SinRespuesta(clsErrorDominio error) =>
        error.Datos is { } datos && datos.TryGetValue(ClaveSinRespuesta, out var valor) && valor is true;

    /// <summary>
    /// <c>GET /recomendaciones?idLineaSolicitud=…&amp;origenSistema=COTIFLY</c> (guía §7): la última corrida de la línea, o <c>null</c> si no
    /// existe, no responde, no cumple el contrato o su huella no coincide con esta petición (<see cref="DiferenciaConLaCarga"/>: una corrida
    /// anterior de la misma ruta con otra carga u otro peso, o una que no se puede comprobar).
    /// </summary>
    private async Task<clsRecomendacionCubicador?> ReconsultarAsync(clsPeticionRecomendacion peticion, CancellationToken cancellationToken)
    {
        var ruta = RutaReconsulta(peticion.IdLineaSolicitud);
        var respuesta = await EnviarAsync(HttpMethod.Get, ruta, null, cancellationToken);
        if (respuesta.EsFallo)
        {
            logger.LogWarning("Reconsulta de la recomendación de {IdLineaSolicitud} sin corrida ({Codigo}: {Detalle}).",
                peticion.IdLineaSolicitud, respuesta.Error.Codigo, respuesta.Error.Detalle);
            return null;
        }

        using var documento = respuesta.Valor;
        var raiz = documento.RootElement;
        if (raiz.ValueKind == JsonValueKind.Array)
        {
            // Algunas versiones devuelven una lista: la última corrida es la última fila.
            raiz = raiz.GetArrayLength() == 0 ? default : raiz[raiz.GetArrayLength() - 1];
        }

        if (raiz.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Texto(raiz, "idLineaSolicitud") is { } linea && !string.Equals(linea.Trim(), peticion.IdLineaSolicitud, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("La reconsulta de {IdLineaSolicitud} devolvió otra línea: se descarta.", peticion.IdLineaSolicitud);
            return null;
        }

        var leida = Validar(raiz);
        var diferencia = leida.EsFallo ? leida.Error.Detalle : DiferenciaConLaCarga(raiz, peticion);
        if (diferencia is not null)
        {
            // Auditoría A3: nunca se acepta una corrida que no se pueda atribuir a ESTA petición (otra carga de la misma ruta, una
            // SIN_SELECCION anterior, el mismo número de unidades con otro peso). Se sigue como si no hubiera respuesta.
            logger.LogWarning("La reconsulta de {IdLineaSolicitud} no sirve ({Motivo}): se trata como caída.", peticion.IdLineaSolicitud, diferencia);
            return null;
        }

        logger.LogInformation("La reconsulta de {IdLineaSolicitud} encontró la corrida {IdRecomendacion}: se usa sin volver a pedirla.",
            peticion.IdLineaSolicitud, leida.Valor.IdRecomendacion);
        return leida.Valor;
    }

    /// <summary>Ruta de la reconsulta con la línea y <c>origenSistema=COTIFLY</c> en la query.</summary>
    public static string RutaReconsulta(string idLineaSolicitud) =>
        $"{RutaRecomendaciones}?idLineaSolicitud={Uri.EscapeDataString(idLineaSolicitud)}&origenSistema={OrigenSistema}";

    /// <summary>
    /// Tolerancia del peso por fila del detalle (kg): solo el redondeo del <c>pesoKg</c> de cada fila. El cubicador
    /// confirmó (2026-10-15) que redondea el <c>pesoKg</c> a 2 decimales (la guía no lo fijaba y el ejemplo trae <c>10000.0</c>): la
    /// diferencia por fila es de 0,005 kg como mucho, así que el medio kilo por fila que se tolera tiene margen de sobra. Se dejó en
    /// medio kilo porque también cubre un redondeo a kilo entero, a un decimal y el ruido de un <c>double</c> serializado (con 0,05 justo en el borde, <c>12.299999999999999</c> por
    /// <c>12.35</c> se rechazaba). Se suma por fila de la posición; nada relativo: un cambio de peso de medio kilo o más por fila no pasa,
    /// y uno menor no cambia ni el camión ni el costeo (auditoría de f3c79d5).
    /// </summary>
    public const decimal ToleranciaPesoFilaKg = 0.5m;

    /// <summary>Tolerancia de una medida o de un peso por unidad (cm o kg) cuando la respuesta los trae.</summary>
    public const decimal ToleranciaUnidad = 0.01m;

    /// <summary>
    /// HU-023 (#176, auditoría A3): ¿la corrida reconsultada es la de ESTA petición? Devuelve <c>null</c> si su huella coincide, o por qué
    /// no (para el log). La guía del cubicador (§5.1, §5.2, §7) dice que el <c>GET</c> devuelve la última corrida con la misma forma del
    /// <c>POST</c>: por camión, <c>mercancias[]</c> con <c>indicePosicion</c>, <c>codigoMercancia</c>, <c>cantidad</c>, <c>pesoKg</c>
    /// (total de la fila) y <c>volumenM3</c>; <b>no devuelve medidas</b>, y una corrida anterior al cambio trae <c>indicePosicion</c> en
    /// <c>null</c>. La huella se comprueba con lo que haya:
    /// <list type="bullet">
    /// <item>Con <c>ASIGNADO</c>, el detalle por camión es obligatorio: cada fila con <c>indicePosicion</c> dentro de la petición,
    /// <c>cantidad</c> y <c>pesoKg</c>; por posición, la cantidad debe ser la pedida y el peso <c>cantidad × pesoKg</c> de la petición
    /// (tolerancia de <see cref="ToleranciaPesoFilaKg"/> por fila); el código y las
    /// medidas por unidad (<c>largoCm</c>, <c>anchoCm</c>, <c>altoCm</c>), si vienen, también.</item>
    /// <item>Si la respuesta repite las mercancías de la petición en la raíz (<c>mercancias[]</c>, por posición), se comparan código,
    /// cantidad, peso por unidad y medidas (si vienen).</item>
    /// <item>Sin nada que comparar (una <c>SIN_SELECCION_AUTOMATICA</c> sin el eco de la petición, o un <c>ASIGNADO</c> sin detalle por
    /// posición) no se puede atribuir: se rechaza. El <c>volumenM3</c> no se compara (el ejemplo de la guía no cuadra con largo × ancho
    /// × alto).</item>
    /// </list>
    /// </summary>
    public static string? DiferenciaConLaCarga(JsonElement raiz, clsPeticionRecomendacion peticion)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        if (raiz.ValueKind != JsonValueKind.Object)
        {
            return "la respuesta no es un objeto";
        }

        var comprobada = false;
        if (Propiedad(raiz, "mercancias") is { ValueKind: JsonValueKind.Array } eco)
        {
            if (DiferenciaEco(eco, peticion) is { } motivo)
            {
                return motivo;
            }

            comprobada = true;
        }

        if (string.Equals(Texto(raiz, "resultado")?.Trim(), clsResultadosRecomendacion.Asignado, StringComparison.OrdinalIgnoreCase))
        {
            if (DiferenciaDetalle(raiz, peticion) is { } motivo)
            {
                return motivo;
            }

            comprobada = true;
        }

        return comprobada ? null : "no se puede comprobar que la corrida sea de esta carga (sin detalle por posición)";
    }

    /// <summary>Compara el detalle por camión de un <c>ASIGNADO</c> (cantidad y peso por posición) con la petición.</summary>
    private static string? DiferenciaDetalle(JsonElement raiz, clsPeticionRecomendacion peticion)
    {
        var mercancias = peticion.Mercancias;
        var cantidades = new long[mercancias.Count];
        var pesos = new decimal[mercancias.Count];
        var filas = new int[mercancias.Count];
        var hayFilas = false;
        foreach (var fila in Arreglo(raiz, "vehiculos").SelectMany(v => Arreglo(v, "mercancias")))
        {
            hayFilas = true;
            if (Entero(fila, "indicePosicion") is not { } indice)
            {
                return "una fila del detalle no trae indicePosicion (corrida anterior): no se puede comprobar";
            }

            if (indice < 0 || indice >= mercancias.Count)
            {
                return $"la posición {indice} no existe en la petición";
            }

            if (Entero(fila, "cantidad") is not { } cantidad || Numero(fila, "pesoKg") is not { } peso)
            {
                return $"una fila de la posición {indice} no trae cantidad o pesoKg: no se puede comprobar";
            }

            if (DiferenciaCodigoYMedidas(fila, mercancias[indice], indice) is { } motivo)
            {
                return motivo;
            }

            cantidades[indice] += cantidad;
            pesos[indice] += peso;
            filas[indice]++;
        }

        if (!hayFilas)
        {
            return "ASIGNADO sin detalle por posición: no se puede comprobar";
        }

        for (var i = 0; i < mercancias.Count; i++)
        {
            var pedida = mercancias[i];
            if (cantidades[i] != pedida.Cantidad)
            {
                return $"la posición {i} lleva {cantidades[i]} unidad(es) y se pidieron {pedida.Cantidad}";
            }

            if (pedida.PesoKg is not { } pesoUnidad)
            {
                return $"la petición no trae el peso de la posición {i}: no se puede comprobar";
            }

            var esperado = pedida.Cantidad * pesoUnidad;
            if (Math.Abs(pesos[i] - esperado) > ToleranciaPesoFilaKg * filas[i])
            {
                return string.Create(CultureInfo.InvariantCulture, $"la posición {i} pesa {pesos[i]:0.##} kg y se pidieron {esperado:0.##} kg");
            }
        }

        return null;
    }

    /// <summary>Compara el eco de la petición (<c>mercancias[]</c> en la raíz de la respuesta) con la petición, posición por posición.</summary>
    private static string? DiferenciaEco(JsonElement eco, clsPeticionRecomendacion peticion)
    {
        var filas = eco.EnumerateArray().Take(MaximoFilas + 1).ToList();
        if (filas.Count != peticion.Mercancias.Count)
        {
            return $"la corrida tiene {filas.Count} posición(es) y la petición {peticion.Mercancias.Count}";
        }

        for (var i = 0; i < filas.Count; i++)
        {
            var pedida = peticion.Mercancias[i];
            var fila = filas[i];
            if (Entero(fila, "cantidad") is not { } cantidad || cantidad != pedida.Cantidad)
            {
                return $"la posición {i} de la corrida no tiene la cantidad pedida ({pedida.Cantidad})";
            }

            if (Numero(fila, "pesoKg") is not { } peso || pedida.PesoKg is not { } pesoPedido
                || Math.Abs(peso - pesoPedido) > ToleranciaUnidad)
            {
                return $"la posición {i} de la corrida no tiene el peso por unidad pedido";
            }

            if (DiferenciaCodigoYMedidas(fila, pedida, i) is { } motivo)
            {
                return motivo;
            }
        }

        return null;
    }

    /// <summary>El código y las medidas por unidad de una fila, si los trae, deben ser los de la posición pedida.</summary>
    private static string? DiferenciaCodigoYMedidas(JsonElement fila, clsMercanciaCubicador pedida, int indice)
    {
        if (Codigo(fila, "codigoMercancia") is { } codigo && !string.Equals(codigo, pedida.CodigoMercancia.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return $"la posición {indice} es {codigo} y se pidió {pedida.CodigoMercancia}";
        }

        foreach (var (nombre, valor) in new[] { ("largoCm", pedida.LargoCm), ("anchoCm", pedida.AnchoCm), ("altoCm", pedida.AltoCm) })
        {
            if (Numero(fila, nombre) is { } medida && Math.Abs(medida - valor) > ToleranciaUnidad)
            {
                return string.Create(CultureInfo.InvariantCulture, $"la posición {indice} mide {nombre} = {medida} y se pidió {valor}");
            }
        }

        return null;
    }

    /// <summary>
    /// Completa la recomendación ya validada: el <c>comentarioCondicion</c> de cada mercancía condicionada sale de <c>GET /tipos-mercancia</c>
    /// (con caché; si el catálogo no responde, queda sin comentario) y deja el log informativo.
    /// </summary>
    private async Task<clsResultado<clsRecomendacionCubicador>> CompletarAsync(
        clsPeticionRecomendacion peticion, clsRecomendacionCubicador r, string via, CancellationToken cancellationToken)
    {
        if (r.MercanciasCondicionadas.Any(m => m.Comentario is null))
        {
            var catalogo = await ListarTiposMercanciaAsync(cancellationToken);
            if (catalogo.EsExitoso)
            {
                var comentarios = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var tipo in catalogo.Valor)
                {
                    comentarios.TryAdd(tipo.Codigo, tipo.ComentarioCondicion);
                }

                r = r with
                {
                    MercanciasCondicionadas = [.. r.MercanciasCondicionadas.Select(m =>
                        m.Comentario is null && comentarios.TryGetValue(m.CodigoMercancia, out var comentario) ? m with { Comentario = comentario } : m)],
                };
            }
            else
            {
                logger.LogInformation("El catálogo de mercancías no respondió: las mercancías condicionadas quedan sin comentario.");
            }
        }

        logger.LogInformation(
            "Recomendación del cubicador para {IdLineaSolicitud} ({Via}): {Resultado} (motivo {Motivo}, condicionada {Condicionada}, {Vehiculos} vehículo(s), {Advertencias} advertencia(s)).",
            peticion.IdLineaSolicitud, via, r.Resultado, r.MotivoSinSeleccion ?? "-", r.Condicionada, r.Vehiculos.Count, r.Advertencias.Count);
        return r;
    }

    /// <summary>
    /// Lee y valida la respuesta de <c>POST /recomendaciones</c> contra el contrato. Público para las pruebas del contrato; el detalle del
    /// error (qué campo faltó) va en <see cref="clsErrorDominio.Detalle"/>, solo para el log.
    /// </summary>
    public static clsResultado<clsRecomendacionCubicador> Validar(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object)
        {
            return Invalida("la respuesta no es un objeto");
        }

        var resultado = Texto(raiz, "resultado")?.Trim().ToUpperInvariant();
        if (resultado is null || !clsResultadosRecomendacion.Todos.Contains(resultado))
        {
            return Invalida($"resultado desconocido '{Recortar(resultado)}'");
        }

        Guid? idRecomendacion = null;
        if (Texto(raiz, "idRecomendacion") is { } idTexto)
        {
            if (!Guid.TryParse(idTexto, out var guid) || guid == Guid.Empty)
            {
                return Invalida("idRecomendacion no es un uuid");
            }

            idRecomendacion = guid;
        }

        var motivo = Texto(raiz, "motivoSinSeleccion")?.Trim().ToUpperInvariant();
        var asignado = resultado == clsResultadosRecomendacion.Asignado;
        if (asignado && motivo is not null)
        {
            return Invalida("ASIGNADO con motivoSinSeleccion");
        }

        if (!asignado && (motivo is null || !clsMotivosSinSeleccion.Todos.Contains(motivo)))
        {
            return Invalida($"SIN_SELECCION_AUTOMATICA con motivo desconocido '{Recortar(motivo)}'");
        }

        var vehiculos = new List<clsVehiculoRecomendado>();
        if (asignado)
        {
            if (Propiedad(raiz, "vehiculos") is not { ValueKind: JsonValueKind.Array } arreglo || arreglo.GetArrayLength() == 0)
            {
                return Invalida("ASIGNADO sin vehiculos");
            }

            if (arreglo.GetArrayLength() > MaximoFilas)
            {
                return Invalida("demasiados vehiculos");
            }

            var indice = 0;
            foreach (var fila in arreglo.EnumerateArray())
            {
                var vehiculo = Vehiculo(fila, indice++);
                if (vehiculo.EsFallo)
                {
                    return vehiculo.Error;
                }

                vehiculos.Add(vehiculo.Valor);
            }
        }

        var condicionada = Propiedad(raiz, "condicionada") is { } marca && marca.ValueKind is JsonValueKind.True;
        return new clsRecomendacionCubicador(
            idRecomendacion,
            resultado,
            asignado ? null : motivo,
            condicionada,
            [.. Arreglo(raiz, "mercanciasCondicionadas").Select(MercanciaCondicionada).OfType<clsMercanciaCondicionada>()],
            vehiculos,
            [.. Arreglo(raiz, "advertencias").Select(Advertencia).OfType<clsAdvertenciaCubicador>()],
            Extradimension(Propiedad(raiz, "extradimension")));
    }

    public Task<clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>> ListarTiposMercanciaAsync(CancellationToken cancellationToken = default) =>
        LeerCatalogoAsync(RutaTiposMercancia, "tipos de mercancía", cacheMercancias, ["items", "tiposMercancia"], fila =>
        {
            var codigo = Codigo(fila, "codigoMercancia", "codigo");
            var nombre = Texto(fila, "nombre", "nombreMercancia", "descripcion")?.Trim();
            if (codigo is null || string.IsNullOrEmpty(nombre))
            {
                return null;
            }

            var id = Texto(fila, "id", "idTipoMercancia");
            return new clsTipoMercanciaCubicador(
                codigo,
                nombre,
                Texto(fila, "categoria")?.Trim(),
                Booleano(fila, true, "cubicable"),
                Booleano(fila, false, "condicionadaCubicacion", "condicionada"),
                Texto(fila, "comentarioCondicion")?.Trim(),
                Booleano(fila, true, "activo"),
                Guid.TryParse(id, out var guid) && guid != Guid.Empty ? guid : null);
        }, t => t.Codigo, t => t.Nombre, cancellationToken);

    public Task<clsResultado<IReadOnlyList<clsTipoCarroceriaCubicador>>> ListarTiposCarroceriaAsync(CancellationToken cancellationToken = default) =>
        LeerCatalogoAsync(RutaTiposCarroceria, "carrocerías", cacheCarrocerias, ["items", "tiposCarroceria"], fila =>
        {
            var codigo = Codigo(fila, "codigoCarroceria", "codigo");
            return codigo is null
                ? null
                : new clsTipoCarroceriaCubicador(
                    codigo, Texto(fila, "nombre", "nombreCarroceria", "descripcion")?.Trim() is { Length: > 0 } n ? n : codigo,
                    Booleano(fila, true, "activo"));
        }, t => t.Codigo, t => t.Nombre, cancellationToken);

    public Task<clsResultado<IReadOnlyList<clsTipoVehiculoCubicador>>> ListarTiposVehiculoAsync(CancellationToken cancellationToken = default) =>
        LeerCatalogoAsync(RutaTiposVehiculo, "tipos de vehículo", cacheVehiculos, ["items", "vehiculos", "vehiculosCatalogo", "tiposVehiculo"], fila =>
        {
            var codigo = Codigo(fila, "codigoVehiculo", "codigo", "configuracion");
            if (codigo is null)
            {
                return null;
            }

            var carrocerias = Arreglo(fila, "carrocerias", "carroceriasCompatibles")
                .Select(c => c.ValueKind == JsonValueKind.String ? Recortar(c.GetString()) : Codigo(c, "codigoCarroceria", "codigo"))
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new clsTipoVehiculoCubicador(
                codigo, Texto(fila, "nombreVehiculo", "nombre", "descripcion")?.Trim() is { Length: > 0 } n ? n : codigo, carrocerias,
                Booleano(fila, true, "activo"));
        }, t => t.Codigo, t => t.Nombre, cancellationToken);

    /// <summary>
    /// GET liviano a <c>{UrlBase}{Cubicador:RutaSalud}</c> (petición P7) para la sonda de <c>/health/ready</c>: <c>null</c> si responde
    /// 2xx; si no, el motivo (sin la llave). Sin <c>RutaSalud</c> no consulta y responde <c>null</c>.
    /// </summary>
    public async Task<string?> VerificarSaludAsync(CancellationToken cancellationToken = default)
    {
        if (!EstaConfigurado || string.IsNullOrWhiteSpace(opciones.Value.RutaSalud))
        {
            return null;
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Get, ConstruirUrl(_urlBase!, opciones.Value.RutaSalud.Trim()));
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opciones.Value.ApiKey!.Trim());
        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return respuesta.IsSuccessStatusCode ? null : $"HTTP {(int)respuesta.StatusCode}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or ExecutionRejectedException or IOException)
        {
            return ex is OperationCanceledException or Polly.Timeout.TimeoutRejectedException ? "tiempo agotado" : "error de red";
        }
    }

    /// <summary><c>{urlBase}{ruta}</c>, con <paramref name="ruta"/> empezando por <c>/</c>.</summary>
    public static Uri ConstruirUrl(string urlBase, string ruta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        return new Uri($"{urlBase.TrimEnd('/')}{ruta}", UriKind.Absolute);
    }

    /// <summary>
    /// Tubería del cliente <see cref="NombreCliente"/>: <paramref name="tiempo"/> por intento de cada GET (catálogos, reconsulta, salud) y
    /// <paramref name="tiempoRecomendacion"/> para el POST (D3b, E2E 2026-10-16: <c>Cubicador:TiempoEsperaRecomendacionSegundos</c>; el
    /// motor tarda hasta ~30 s con cargas grandes). En total, lo que caben el intento y los <see cref="ReintentosLectura"/> reintentos de un
    /// GET con sus esperas (<see cref="TiempoTotal"/>) o el POST, lo que sea mayor (un POST no se reintenta). El circuito con una ventana
    /// de al menos el doble del intento más largo (lo exige la tubería estándar). Sin <paramref name="tiempoRecomendacion"/>, el POST usa
    /// <paramref name="tiempo"/>.
    /// </summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia, TimeSpan tiempo, TimeSpan? tiempoRecomendacion = null)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        var post = tiempoRecomendacion ?? tiempo;
        var totalPost = post + TimeSpan.FromSeconds(2);
        resiliencia.TotalRequestTimeout.Timeout = totalPost > TiempoTotal(tiempo) ? totalPost : TiempoTotal(tiempo);
        resiliencia.AttemptTimeout.Timeout = tiempo;
        resiliencia.AttemptTimeout.TimeoutGenerator = argumentos =>
            ValueTask.FromResult(argumentos.Context.GetRequestMessage()?.Method == HttpMethod.Post ? post : tiempo);
        resiliencia.Retry.MaxRetryAttempts = ReintentosLectura;
        resiliencia.Retry.Delay = TimeSpan.FromMilliseconds(300);
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        resiliencia.Retry.DisableForUnsafeHttpMethods();
        var mayor = post > tiempo ? post : tiempo;
        resiliencia.CircuitBreaker.SamplingDuration = mayor * 2 > TimeSpan.FromSeconds(30) ? mayor * 2 : TimeSpan.FromSeconds(30);
    }

    /// <summary>Límite total de una petición: el intento más los reintentos de un GET y sus esperas (300 ms, 600 ms y holgura del jitter).</summary>
    public static TimeSpan TiempoTotal(TimeSpan intento) => (intento * (ReintentosLectura + 1)) + TimeSpan.FromSeconds(2);

    /// <summary>
    /// Un catálogo con caché (<c>Cubicador:MinutosCacheCatalogos</c>) y tope propio (<c>Cubicador:TiempoMaximoCatalogoSegundos</c>,
    /// reintentos incluidos): filas sin código se descartan, los códigos repetidos quedan una vez y se ordena por nombre.
    /// </summary>
    private async Task<clsResultado<IReadOnlyList<TFila>>> LeerCatalogoAsync<TFila>(
        string ruta, string nombre, clsCacheCatalogoCubicador<TFila> cache, string[] contenedores, Func<JsonElement, TFila?> mapear,
        Func<TFila, string> codigo, Func<TFila, string> orden, CancellationToken cancellationToken)
        where TFila : class
    {
        var minutos = opciones.Value.MinutosCacheCatalogos;
        if (minutos > 0 && cache.Obtener() is { } enCache)
        {
            return clsResultado<IReadOnlyList<TFila>>.Exito(enCache);
        }

        clsResultado<JsonDocument> respuesta;
        using (var tope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            tope.CancelAfter(TimeSpan.FromSeconds(opciones.Value.TiempoMaximoCatalogoSegundos));
            try
            {
                respuesta = await EnviarAsync(HttpMethod.Get, ruta, null, tope.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return NoDisponible(
                    $"tiempo máximo del catálogo agotado ({opciones.Value.TiempoMaximoCatalogoSegundos.ToString(CultureInfo.InvariantCulture)} s) en GET {ruta}",
                    caida: true);
            }
        }

        if (respuesta.EsFallo)
        {
            return respuesta.Error;
        }

        using var documento = respuesta.Valor;
        var filas = Filas(documento.RootElement, contenedores);
        if (filas is null)
        {
            return NoDisponible($"catálogo de {nombre} sin filas");
        }

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lista = filas.Take(MaximoFilas).Select(mapear).OfType<TFila>().Where(f => vistos.Add(codigo(f))).ToList();
        if (lista.Count == 0)
        {
            return NoDisponible($"catálogo de {nombre} vacío");
        }

        IReadOnlyList<TFila> ordenada = [.. lista.OrderBy(orden, StringComparer.CurrentCultureIgnoreCase)];
        if (minutos > 0)
        {
            cache.Guardar(ordenada, TimeSpan.FromMinutes(minutos));
        }

        return clsResultado<IReadOnlyList<TFila>>.Exito(ordenada);
    }

    private async Task<clsResultado<JsonDocument>> EnviarAsync(HttpMethod metodo, string ruta, object? cuerpo, CancellationToken cancellationToken)
    {
        if (!EstaConfigurado)
        {
            return NoDisponible("cubicador sin configurar (Cubicador:UrlBase y Cubicador:ApiKey)");
        }

        using var peticion = new HttpRequestMessage(metodo, ConstruirUrl(_urlBase!, ruta));
        if (cuerpo is not null)
        {
            peticion.Content = JsonContent.Create(cuerpo, cuerpo.GetType(), options: _json);
        }

        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opciones.Value.ApiKey!.Trim());
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                logger.LogError("El cubicador respondió {Estado} a {Metodo} {Ruta}: revisar Cubicador:ApiKey.", (int)respuesta.StatusCode, metodo, ruta);
                // La llave no vale para ninguna ruta: se trata como caída (las demás rutas de la captura no vuelven a llamar).
                return new clsErrorDominio(clsCodigosError.CubicadorNoAutorizado, MensajeNoAutorizado, $"HTTP {(int)respuesta.StatusCode}")
                {
                    Datos = new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true },
                };
            }

            if (respuesta.StatusCode == HttpStatusCode.BadRequest)
            {
                return await RechazoAsync(respuesta, metodo, ruta, cancellationToken);
            }

            if (respuesta.StatusCode == HttpStatusCode.UnprocessableEntity && metodo == HttpMethod.Post)
            {
                // D1 (E2E 2026-10-16): el 422 del POST es la validación de los DATOS de la carga (medida, peso o cantidad fuera de rango):
                // un 400 para el comercial con la mercancía, no "avisa a soporte". Cotifly ya valida antes; esto cubre lo que se escape.
                return await PeticionRechazadaAsync(respuesta, ruta, cancellationToken);
            }

            if (respuesta.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                // HU-023 (#176): un 422 es la validación del esquema del cubicador (falta un campo o llega uno viejo): un error de contrato
                // de Cotifly, visible y nunca al respaldo.
                logger.LogError("El cubicador respondió 422 a {Metodo} {Ruta}: la petición de Cotifly no cumple su esquema.", metodo, ruta);
                return new clsErrorDominio(clsCodigosError.CubicadorRespuestaInvalida, MensajeConsultaRechazada, $"HTTP 422 en {metodo} {ruta}");
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                // 404 (la ruta no existe todavía: el cubicador no la ha publicado), 5xx y 503 son caídas; otro 4xx es un rechazo.
                var estado = (int)respuesta.StatusCode;
                return NoDisponible($"HTTP {estado} ({respuesta.StatusCode}) en {metodo} {ruta}",
                    caida: estado is < 400 or >= 500 or 404 or 408 or 429);
            }

            var documento = await LeerJsonAsync(respuesta, cancellationToken);
            return documento is null
                ? NoDisponible($"respuesta de más de {LimiteBytes.ToString(CultureInfo.InvariantCulture)} bytes")
                : documento;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída del cubicador
        }
        catch (Exception ex) when (ex is OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            return ConRespuestaPerdida(NoDisponible($"tiempo agotado en {metodo} {ruta}", ex, caida: true));
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException or IOException)
        {
            var circuito = ex is Polly.CircuitBreaker.BrokenCircuitException;
            var error = NoDisponible(circuito ? "circuito abierto tras fallos repetidos" : $"error de red en {metodo} {ruta}", ex, caida: true);
            // Con el circuito abierto la petición no salió: no hay corrida que reconsultar.
            return circuito ? error : ConRespuestaPerdida(error);
        }
        catch (JsonException ex) when (ruta == RutaRecomendaciones)
        {
            logger.LogDebug(ex, "JSON inválido en la recomendación del cubicador.");
            return Invalida("JSON inválido");
        }
        catch (JsonException ex)
        {
            return NoDisponible("JSON inválido", ex);
        }
    }

    /// <summary>
    /// Un 400 del cubicador: con <c>{codigo, mensaje}</c> de uno de los rechazos del contrato (<see cref="Rechazos"/>), ese código con el
    /// mensaje de Cotifly y el del cubicador; si no, <c>CUBICADOR_NO_DISPONIBLE</c> (rechazo, no caída).
    /// </summary>
    private async Task<clsErrorDominio> RechazoAsync(HttpResponseMessage respuesta, HttpMethod metodo, string ruta, CancellationToken cancellationToken)
    {
        string? codigo = null;
        string? mensaje = null;
        try
        {
            using var documento = await LeerJsonAsync(respuesta, cancellationToken);
            if (documento?.RootElement is { ValueKind: JsonValueKind.Object } raiz)
            {
                var error = Propiedad(raiz, "error") is { ValueKind: JsonValueKind.Object } anidado ? anidado : raiz;
                codigo = Texto(error, "codigo", "code")?.Trim().ToUpperInvariant();
                mensaje = Recortar(Texto(error, "mensaje", "message"));
            }
        }
        catch (JsonException)
        {
            // Cuerpo ilegible: se trata como rechazo genérico.
        }

        if (codigo is not null && Rechazos.TryGetValue(codigo, out var propio))
        {
            logger.LogWarning("El cubicador rechazó {Metodo} {Ruta} con {Codigo}.", metodo, ruta, codigo);
            var texto = mensaje is null ? propio : $"{propio} Detalle del cubicador: {(mensaje.Length > LargoMensajeCubicador ? mensaje[..LargoMensajeCubicador] : mensaje)}";
            return new clsErrorDominio(codigo, texto, $"HTTP 400 {codigo} en {metodo} {ruta}");
        }

        return NoDisponible($"HTTP 400 ({codigo ?? "sin código"}) en {metodo} {ruta}");
    }

    /// <summary>
    /// D1 (E2E 2026-10-16): un 422 de <c>POST /recomendaciones</c> (FastAPI: <c>{"detail": [{"loc": ["body", "mercancias", 0, "largoCm"],
    /// "msg": "..."}]}</c>). Si señala una mercancía: <c>DIMENSIONES_INVALIDAS</c> (medidas o peso) o <c>POSICION_INVALIDA</c> (cantidad), con
    /// <c>item</c> (base 0) y <c>campo</c> en los datos; si no, <c>CUBICADOR_PETICION_RECHAZADA</c>. Los tres son 400 para el comercial y
    /// nunca van al respaldo (no son una caída).
    /// </summary>
    private async Task<clsErrorDominio> PeticionRechazadaAsync(HttpResponseMessage respuesta, string ruta, CancellationToken cancellationToken)
    {
        int? item = null;
        string? campo = null;
        string? motivo = null;
        try
        {
            using var documento = await LeerJsonAsync(respuesta, cancellationToken);
            if (documento?.RootElement is { ValueKind: JsonValueKind.Object } raiz
                && Propiedad(raiz, "detail") is { ValueKind: JsonValueKind.Array } detalles && detalles.GetArrayLength() > 0
                && detalles[0] is { ValueKind: JsonValueKind.Object } primero)
            {
                motivo = Recortar(Texto(primero, "msg"));
                if (Propiedad(primero, "loc") is { ValueKind: JsonValueKind.Array } loc)
                {
                    var partes = loc.EnumerateArray().ToList();
                    var mercancias = partes.FindIndex(p => p.ValueKind == JsonValueKind.String && p.GetString() == "mercancias");
                    if (mercancias >= 0 && mercancias + 1 < partes.Count && partes[mercancias + 1].ValueKind == JsonValueKind.Number
                        && partes[mercancias + 1].TryGetInt32(out var indice) && indice >= 0)
                    {
                        item = indice;
                        campo = mercancias + 2 < partes.Count && partes[mercancias + 2].ValueKind == JsonValueKind.String
                            ? partes[mercancias + 2].GetString()
                            : null;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Cuerpo ilegible: rechazo sin mercancía.
        }

        logger.LogWarning("El cubicador rechazó con 422 POST {Ruta} (mercancía {Item}, campo {Campo}).", ruta, item, campo);
        var detalle = motivo is null ? string.Empty : $" Detalle del cubicador: {(motivo.Length > LargoMensajeCubicador ? motivo[..LargoMensajeCubicador] : motivo)}";
        if (item is not { } i)
        {
            return new clsErrorDominio(clsCodigosError.CubicadorPeticionRechazada, MensajePeticionRechazada + detalle, $"HTTP 422 en POST {ruta}");
        }

        var esCantidad = string.Equals(campo, "cantidad", StringComparison.OrdinalIgnoreCase);
        var numero = (i + 1).ToString(CultureInfo.InvariantCulture);
        var mensaje = esCantidad
            ? $"El cubicador rechazó la cantidad de la mercancía {numero}: debe ser un entero mayor que 0.{detalle}"
            : $"El cubicador rechazó las medidas o el peso de la mercancía {numero}{(campo is null ? string.Empty : $" ({campo})")}: deben ser mayores que 0.{detalle}";
        var datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["item"] = i };
        if (campo is not null)
        {
            datos["campo"] = campo;
        }

        return new clsErrorDominio(esCantidad ? clsCodigosLineaSolicitud.PosicionInvalida : clsCodigosError.DimensionesInvalidas, mensaje, $"HTTP 422 en POST {ruta}")
        {
            Datos = datos,
        };
    }

    private static async Task<JsonDocument?> LeerJsonAsync(HttpResponseMessage respuesta, CancellationToken cancellationToken)
    {
        if (respuesta.Content.Headers.ContentLength is { } largo && largo > LimiteBytes)
        {
            return null;
        }

        await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
        using var acotado = await LeerAcotadoAsync(flujo, LimiteBytes, cancellationToken);
        return acotado is null ? null : await JsonDocument.ParseAsync(acotado, cancellationToken: cancellationToken);
    }

    private static clsResultado<clsVehiculoRecomendado> Vehiculo(JsonElement fila, int indice)
    {
        if (fila.ValueKind != JsonValueKind.Object)
        {
            return Invalida($"vehiculos[{indice}] no es un objeto");
        }

        var faltan = new List<string>();
        var id = Texto(fila, "idRecomendacionVehiculo");
        if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty)
        {
            faltan.Add("idRecomendacionVehiculo");
        }

        var vehiculo = Codigo(fila, "codigoVehiculo");
        var carroceria = Codigo(fila, "codigoCarroceria");
        var peso = Numero(fila, "pesoTotalKg");
        var volumen = Numero(fila, "volumenTotalM3");
        var llenadoVolumen = Numero(fila, "porcentajeLlenadoVolumen");
        var llenadoPeso = Numero(fila, "porcentajeLlenadoPeso");
        foreach (var (campo, valor) in new (string, object?)[]
                 {
                     ("codigoVehiculo", vehiculo), ("codigoCarroceria", carroceria), ("pesoTotalKg", peso), ("volumenTotalM3", volumen),
                     ("porcentajeLlenadoVolumen", llenadoVolumen), ("porcentajeLlenadoPeso", llenadoPeso),
                 })
        {
            if (valor is null)
            {
                faltan.Add(campo);
            }
        }

        if (faltan.Count > 0)
        {
            return Invalida($"vehiculos[{indice}] sin {string.Join(", ", faltan)}");
        }

        if (peso is not > 0m || peso > MaximoPesoKg)
        {
            return Invalida($"vehiculos[{indice}].pesoTotalKg fuera de rango");
        }

        if (volumen < 0m || volumen > 999_999_999m)
        {
            return Invalida($"vehiculos[{indice}].volumenTotalM3 fuera de rango");
        }

        if (llenadoVolumen is < 0m or > 100m || llenadoPeso is < 0m or > 100m)
        {
            return Invalida($"vehiculos[{indice}] con un porcentaje de llenado fuera de 0 a 100");
        }

        var mercancias = new List<clsMercanciaVehiculo>();
        foreach (var m in Arreglo(fila, "mercancias"))
        {
            // HU-023 (#176): una fila por posición; dos filas pueden compartir código (no se agrupan aquí: quien agrupe suma).
            if (m.ValueKind == JsonValueKind.Object && Codigo(m, "codigoMercancia") is { } codigoMercancia)
            {
                mercancias.Add(new clsMercanciaVehiculo(
                    Entero(m, "indicePosicion"), Recortar(Texto(m, "idTipoMercancia")), codigoMercancia, Entero(m, "cantidad") ?? 0,
                    Math.Round(Numero(m, "pesoKg") ?? 0m, 2), Math.Round(Numero(m, "volumenM3") ?? 0m, 3)));
            }
        }

        return new clsVehiculoRecomendado(
            guid, vehiculo!, carroceria!, Math.Round(peso!.Value, 2), Math.Round(volumen!.Value, 3), Math.Round(llenadoVolumen!.Value, 2),
            Math.Round(llenadoPeso!.Value, 2), Texto(fila, "nombreVehiculo")?.Trim() is { Length: > 0 } nombre ? nombre : null,
            Entero(fila, "numeroVehiculo"), Booleano(fila, false, "alertaBajoLlenado"), Booleano(fila, false, "heterogenea"), mercancias.Count == 0 ? null : mercancias);
    }

    private static int? Entero(JsonElement objeto, params string[] nombres) => Propiedad(objeto, nombres) switch
    {
        { ValueKind: JsonValueKind.Number } valor when valor.TryGetInt32(out var numero) => numero,
        _ => null,
    };

    private static clsMercanciaCondicionada? MercanciaCondicionada(JsonElement fila) => fila.ValueKind switch
    {
        JsonValueKind.String when Recortar(fila.GetString()) is { } codigo => new clsMercanciaCondicionada(codigo, null),
        JsonValueKind.Object when Codigo(fila, "codigoMercancia", "codigo") is { } codigo =>
            new clsMercanciaCondicionada(codigo, Recortar(Texto(fila, "comentarioCondicion", "comentario", "mensaje"))),
        _ => null,
    };

    private static clsAdvertenciaCubicador? Advertencia(JsonElement fila) => fila.ValueKind switch
    {
        JsonValueKind.String when Recortar(fila.GetString()) is { } mensaje => new clsAdvertenciaCubicador(null, mensaje),
        JsonValueKind.Object when Recortar(Texto(fila, "mensaje", "descripcion", "detalle")) is { } mensaje =>
            new clsAdvertenciaCubicador(Recortar(Texto(fila, "codigo", "tipo")), mensaje),
        JsonValueKind.Object when Recortar(Texto(fila, "codigo", "tipo")) is { } codigo => new clsAdvertenciaCubicador(codigo, codigo),
        _ => null,
    };

    /// <summary>
    /// <c>extradimension</c> con la estructura vigente (HU-023, #176; guía §5.3): <c>{hayExtradimension, medidasEstandar{largoMaxCm,
    /// anchoMaxCm, altoMaxCm, pesoMaxKg}, piezas[]{indicePosicion, idTipoMercancia, codigoMercancia, excesos[]}}</c>. Un booleano suelto o
    /// la marca vieja <c>extradimensionado</c> se leen como <c>hayExtradimension</c> sin piezas.
    /// </summary>
    private static clsExtradimensionCubicador? Extradimension(JsonElement? valor) => valor switch
    {
        { ValueKind: JsonValueKind.True } => new clsExtradimensionCubicador(true, null, []),
        { ValueKind: JsonValueKind.False } => new clsExtradimensionCubicador(false, null, []),
        { ValueKind: JsonValueKind.Object } objeto => new clsExtradimensionCubicador(
            Booleano(objeto, Arreglo(objeto, "piezas").Any(), "hayExtradimension", "extradimensionado"),
            Propiedad(objeto, "medidasEstandar") is { ValueKind: JsonValueKind.Object } medidas
                ? new clsMedidasEstandarCubicador(
                    Numero(medidas, "largoMaxCm"), Numero(medidas, "anchoMaxCm"), Numero(medidas, "altoMaxCm"), Numero(medidas, "pesoMaxKg"))
                : null,
            [.. Arreglo(objeto, "piezas").Where(p => p.ValueKind == JsonValueKind.Object).Select(p => new clsPiezaExtradimension(
                Entero(p, "indicePosicion"), Recortar(Texto(p, "idTipoMercancia")), Codigo(p, "codigoMercancia"),
                [.. Arreglo(p, "excesos").Select(e => e.ValueKind == JsonValueKind.String ? Recortar(e.GetString()) : null).OfType<string>()]))]),
        _ => null,
    };

    /// <summary>Las filas de la respuesta: el arreglo raíz o el de la primera propiedad de <paramref name="contenedores"/> que lo traiga.</summary>
    private static List<JsonElement>? Filas(JsonElement raiz, params string[] contenedores)
    {
        if (raiz.ValueKind == JsonValueKind.Array)
        {
            return [.. raiz.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object)];
        }

        foreach (var nombre in contenedores)
        {
            if (Propiedad(raiz, nombre) is { ValueKind: JsonValueKind.Array } arreglo)
            {
                return [.. arreglo.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.Object)];
            }
        }

        return null;
    }

    private static IEnumerable<JsonElement> Arreglo(JsonElement objeto, params string[] nombres) =>
        Propiedad(objeto, nombres) is { ValueKind: JsonValueKind.Array } arreglo ? arreglo.EnumerateArray().Take(MaximoFilas).ToList() : [];

    /// <summary>La primera de <paramref name="nombres"/> presente en <paramref name="objeto"/>, sin distinguir mayúsculas.</summary>
    private static JsonElement? Propiedad(JsonElement objeto, params string[] nombres)
    {
        if (objeto.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var nombre in nombres)
        {
            foreach (var propiedad in objeto.EnumerateObject())
            {
                if (string.Equals(propiedad.Name, nombre, StringComparison.OrdinalIgnoreCase) && propiedad.Value.ValueKind != JsonValueKind.Null)
                {
                    return propiedad.Value;
                }
            }
        }

        return null;
    }

    private static string? Texto(JsonElement objeto, params string[] nombres) => Propiedad(objeto, nombres) switch
    {
        { ValueKind: JsonValueKind.String } valor => valor.GetString(),
        { ValueKind: JsonValueKind.Number } valor => valor.GetRawText(),
        _ => null,
    };

    /// <summary>Un código: texto no vacío de hasta <see cref="LargoCodigo"/> caracteres, recortado; si no, <c>null</c>.</summary>
    private static string? Codigo(JsonElement objeto, params string[] nombres) =>
        Recortar(Texto(objeto, nombres)) is { Length: <= LargoCodigo } codigo ? codigo : null;

    private static decimal? Numero(JsonElement objeto, params string[] nombres) => Propiedad(objeto, nombres) switch
    {
        { ValueKind: JsonValueKind.Number } valor when valor.TryGetDecimal(out var numero) => numero,
        _ => null,
    };

    private static bool Booleano(JsonElement objeto, bool omision, params string[] nombres) => Propiedad(objeto, nombres) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => omision,
    };

    private static string? Recortar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static async Task<MemoryStream?> LeerAcotadoAsync(Stream flujo, long limite, CancellationToken cancellationToken)
    {
        var memoria = new MemoryStream();
        var bufer = new byte[16 * 1024];
        int leidos;
        while ((leidos = await flujo.ReadAsync(bufer, cancellationToken)) > 0)
        {
            if (memoria.Length + leidos > limite)
            {
                await memoria.DisposeAsync();
                return null;
            }

            memoria.Write(bufer, 0, leidos);
        }

        memoria.Position = 0;
        return memoria;
    }

    /// <summary>Marca un fallo sin respuesta (<see cref="ClaveSinRespuesta"/>), conservando la marca de caída.</summary>
    private static clsErrorDominio ConRespuestaPerdida(clsErrorDominio error) => error with
    {
        Datos = new Dictionary<string, object?>(error.Datos ?? new Dictionary<string, object?>()) { [ClaveSinRespuesta] = true },
    };

    /// <summary><c>CUBICADOR_RESPUESTA_INVALIDA</c> con el motivo en <see cref="clsErrorDominio.Detalle"/> (solo para el log).</summary>
    private static clsErrorDominio Invalida(string motivo) =>
        new(clsCodigosError.CubicadorRespuestaInvalida, MensajeRespuestaInvalida, motivo);

    /// <summary>
    /// Error <c>CUBICADOR_NO_DISPONIBLE</c> con el motivo en <see cref="clsErrorDominio.Detalle"/> (solo para el log: quien lo expone por
    /// HTTP lo quita) y, si es una caída, la marca <see cref="clsErroresCubicador.ClaveCaida"/>.
    /// </summary>
    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null, bool caida = false)
    {
        logger.LogWarning("Cubicador no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo del cubicador ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoDisponible, motivo)
        {
            Datos = caida ? new Dictionary<string, object?> { [clsErroresCubicador.ClaveCaida] = true } : null,
        };
    }

    // --- Forma de la petición de POST /recomendaciones (camelCase, contrato de HU-023) ---

    private sealed record clsRecomendacionWire(
        string IdLineaSolicitud, string OrigenSistema, string? ClaseViajeSeleccionada, string? UsuarioId, IReadOnlyList<clsMercanciaWire> Mercancias);

    private sealed record clsMercanciaWire(string CodigoMercancia, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg);
}
