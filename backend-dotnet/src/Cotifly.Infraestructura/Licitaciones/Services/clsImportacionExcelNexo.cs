using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Costeo.Services;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Licitaciones.Services;

/// <summary>
/// Importación inteligente del Excel con NEXO (HU-044.1, #252, ADR-028): puente sin estado hacia <c>/api/captura-solicitudes/*</c> y
/// <c>/api/diccionario-cliente</c> de NEXO (CONTRATO.md §3.3 y §3.19.1). Identidad <c>X-Portal-clsUsuario-Id</c> del comercial
/// (<see cref="clsIdentidadNexo"/>) y cliente por <c>clienteNit</c> normalizado como lo compara NEXO (<see cref="NormalizarNit"/>); Cotifly
/// no busca ni guarda el <c>clienteId</c> de NEXO, no abre el Excel (lo valida NEXO) y nunca tipa ni modifica la <c>receta</c> (JSON opaco).
/// </summary>
/// <remarks>
/// <para><b>Cliente HTTP propio (<see cref="NombreCliente"/>).</b> Su tubería (<see cref="ConfigurarResiliencia"/>) da a cada intento
/// <c>ImportacionInteligente:TimeoutSegundos</c> (90 s) o, en <c>leer-estructura</c> y <c>PUT recetas-lectura</c>,
/// <c>ImportacionInteligente:TimeoutLecturaSegundos</c> (240 s; mínimo 180). Solo se reintentan los GET y <c>aplicar-mapeo</c> (sin IA ni efecto); las
/// llamadas que pueden gastar IA (<c>sugerir-mapeo</c>, <c>normalizar-valores</c>, <c>leer-estructura</c>, <c>PUT recetas-lectura</c>) y las
/// demás escrituras nunca. Un 429 no cuenta para el circuito. El cuerpo se lee dentro del tiempo del intento y con el tope
/// <c>ImportacionInteligente:LimiteRespuestaMegabytes</c>.</para>
/// <para><b>Errores.</b> 400/404 con un código de <see cref="CodigosNexo"/> → ese código con el mensaje de NEXO; otro 4xx →
/// <c>COSTEO_NEXO_RECHAZADA</c>; <c>429 IA_TOPE_DIARIO</c> → <c>IA_TOPE_DIARIO</c> sin tocar el bloqueo global de
/// <see cref="clsCupoNexoHandler"/> (estas rutas no son de listados limitados); otro 429 (<c>DEMASIADAS_SOLICITUDES</c>) → no disponible
/// con el <c>Retry-After</c> en el detalle; 401/403 → <c>NEXO_SIN_PERMISO</c>; 413 → <c>ARCHIVO_DEMASIADO_GRANDE</c>; 5xx, red, tiempo
/// agotado, circuito abierto, JSON inválido (o un null dentro de una lista), respuesta demasiado grande, comercial sin
/// <c>idUsuarioExterno</c> o sin configurar → <c>NEXO_NO_DISPONIBLE</c>. La cancelación del llamador se propaga. Una línea de warning por fallo, sin el header de identidad, la consulta ni el mensaje de NEXO.</para>
/// </remarks>
public sealed class clsImportacionExcelNexo(
    HttpClient http,
    IOptions<clsOpcionesNexo> opcionesNexo,
    IOptions<clsOpcionesImportacionInteligente> opciones,
    IClsUsuarioWebRepository usuarios,
    ILogger<clsImportacionExcelNexo> logger)
    : IClsImportacionExcelNexo
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "NexoImportacion";

    public const string RutaSugerirMapeo = "/api/captura-solicitudes/sugerir-mapeo";
    public const string RutaAplicarMapeo = "/api/captura-solicitudes/aplicar-mapeo";
    public const string RutaNormalizarValores = "/api/captura-solicitudes/normalizar-valores";
    public const string RutaPlantillasMapeo = "/api/captura-solicitudes/plantillas-mapeo";
    public const string RutaLeerEstructura = "/api/captura-solicitudes/leer-estructura";
    public const string RutaRecetasLectura = "/api/captura-solicitudes/recetas-lectura";
    public const string RutaDiccionarioCliente = "/api/diccionario-cliente";
    public const string RutaOpcionesDiccionario = "/api/diccionario-cliente/opciones";

    /// <summary>Largo máximo del <c>clienteNit</c> que acepta NEXO.</summary>
    public const int LargoMaximoNit = 30;

    /// <summary>Reintentos de las peticiones marcadas con <see cref="OpcionReintentable"/> (los GET y <c>aplicar-mapeo</c>): uno, como pide NEXO.</summary>
    public const int ReintentosLectura = 1;

    /// <summary>Marca de la petición que la tubería puede reintentar ante un fallo transitorio (sin ella, nunca).</summary>
    public static readonly HttpRequestOptionsKey<bool> OpcionReintentable = new("Cotifly.NexoImportacion.Reintentable");

    /// <summary>Marca de la petición que usa el tiempo largo (<c>leer-estructura</c> y <c>PUT recetas-lectura</c>).</summary>
    public static readonly HttpRequestOptionsKey<bool> OpcionLectura = new("Cotifly.NexoImportacion.Lectura");

    public const string MensajeNoDisponible =
        "La importación inteligente de NEXO no está disponible en este momento: intenta de nuevo más tarde o mapea las columnas a mano.";

    public const string MensajeRechazada = "NEXO rechazó la petición de importación.";

    public const string MensajeClienteRequerido = "Falta el NIT del cliente de la licitación.";

    public const string MensajeNitInvalido = "El NIT del cliente de la licitación no es válido.";

    /// <summary>
    /// Códigos de error de NEXO de estos endpoints que llegan tal cual (CONTRATO.md §3.3 y §3.19.1). <c>VALIDATION_ERROR</c> no está: es
    /// una petición de Cotifly mal armada y sale como <c>COSTEO_NEXO_RECHAZADA</c> con el código en el detalle.
    /// </summary>
    public static readonly IReadOnlySet<string> CodigosNexo = new HashSet<string>(StringComparer.Ordinal)
    {
        clsCodigosError.ArchivoRequerido, clsCodigosError.FormatoNoSoportado, clsCodigosError.ExcelInvalido, clsCodigosError.ExcelSinDatos,
        clsCodigosError.EncabezadosRequeridos, clsCodigosError.DemasiadasColumnas, clsCodigosError.DemasiadasFilas,
        clsCodigosError.EncabezadoDemasiadoLargo, clsCodigosError.ColumnaFueraDeRango, clsCodigosError.ColumnaInvalida,
        clsCodigosError.MapeoRequerido, clsCodigosError.CampoInvalido, clsCodigosError.CampoDuplicado, clsCodigosError.CampoRequerido,
        clsCodigosError.TransformacionInvalida, clsCodigosError.TransformacionNoAplica, clsCodigosError.ValoresRequeridos,
        clsCodigosError.DemasiadosValores, clsCodigosError.ValorDemasiadoLargo, clsCodigosError.ClienteRequerido,
        clsCodigosError.ClienteNoEncontrado, clsCodigosError.ClienteNitInvalido, clsCodigosError.NombreDemasiadoLargo,
        clsCodigosError.PlantillaNoEncontrada, clsCodigosError.InstruccionesDemasiadoLargas, clsCodigosError.RecetaRequerida,
        clsCodigosError.RecetaInvalida, clsCodigosError.RecetaSinFilas, clsCodigosError.RecetaNoEncontrada,
        clsCodigosError.TipoTerminoInvalido,
    };

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _urlBase =
        string.IsNullOrWhiteSpace(opcionesNexo.Value.UrlBase) ? null : opcionesNexo.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaHabilitada => _urlBase is not null && opciones.Value.Habilitada;

    /// <summary>
    /// El NIT como lo compara NEXO (<c>clsNit.Normalizar</c>): lo que va antes del primer guion (el resto es el dígito de verificación), solo
    /// letras y dígitos, en mayúsculas. <c>900.123.456-7</c> y <c>900 123 456</c> dan <c>900123456</c>. <c>null</c> si no queda nada.
    /// </summary>
    public static string? NormalizarNit(string? nit)
    {
        if (string.IsNullOrWhiteSpace(nit))
        {
            return null;
        }

        var antesDelGuion = nit.Trim().Split('-')[0];
        var limpio = new string(antesDelGuion.Where(char.IsLetterOrDigit).ToArray());
        return limpio.Length == 0 ? null : limpio.ToUpperInvariant();
    }

    public async Task<clsResultado<clsSugerenciaMapeo>> SugerirMapeoAsync(
        Guid usuarioId, clsPeticionSugerirMapeo peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        if (NitOpcional(peticion.ClienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        var cuerpo = new clsSugerirWire(nit, peticion.Encabezados, peticion.Filas);
        return await EnviarYLeerAsync<clsSugerenciaWire, clsSugerenciaMapeo>(
            usuarioId, HttpMethod.Post, RutaSugerirMapeo, Json(cuerpo), reintentable: false, lectura: false, MapearSugerencia, cancellationToken);
    }

    public async Task<clsResultado<clsResultadoAplicarMapeo>> AplicarMapeoAsync(
        Guid usuarioId, clsPeticionAplicarMapeo peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        if (NitOpcional(peticion.ClienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        var cuerpo = new clsAplicarWire(nit, [.. peticion.Columnas.Select(ColumnaWire)], peticion.Filas, peticion.PrimeraFila);
        return await EnviarYLeerAsync<clsResultadoAplicarWire, clsResultadoAplicarMapeo>(
            usuarioId, HttpMethod.Post, RutaAplicarMapeo, Json(cuerpo), reintentable: true, lectura: false, MapearAplicar, cancellationToken);
    }

    public async Task<clsResultado<clsResultadoNormalizacion>> NormalizarValoresAsync(
        Guid usuarioId, string? clienteNit, IReadOnlyList<clsValorANormalizar> valores, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(valores);
        if (NitOpcional(clienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        var cuerpo = new clsNormalizarWire(nit, [.. valores.Select(v => new clsValorWire(v.Campo, v.Valor))]);
        return await EnviarYLeerAsync<clsResultadoNormalizacionWire, clsResultadoNormalizacion>(
            usuarioId, HttpMethod.Post, RutaNormalizarValores, Json(cuerpo), reintentable: false, lectura: false, MapearNormalizacion,
            cancellationToken);
    }

    public async Task<clsResultado<clsPlantillaGuardada>> GuardarPlantillaMapeoAsync(
        Guid usuarioId, clsPeticionPlantillaMapeo peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        if (NitObligatorio(peticion.ClienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        var cuerpo = new clsPlantillaWire(nit, peticion.Nombre, peticion.Encabezados, [.. peticion.Columnas.Select(ColumnaWire)]);
        var respuesta = await EnviarAsync(usuarioId, HttpMethod.Put, RutaPlantillasMapeo, Json(cuerpo), reintentable: false, lectura: false, cancellationToken);
        return respuesta.EsFallo
            ? respuesta.Error
            : Leer<clsPlantillaMapeoWire, clsPlantillaGuardada>(respuesta.Valor, p => new clsPlantillaGuardada(MapearPlantilla(p), respuesta.Valor.Estado == HttpStatusCode.Created));
    }

    public async Task<clsResultado<IReadOnlyList<clsPlantillaMapeo>>> ListarPlantillasMapeoAsync(
        Guid usuarioId, string clienteNit, CancellationToken cancellationToken = default)
    {
        if (NitObligatorio(clienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        return await EnviarYLeerAsync<List<clsPlantillaMapeoWire>, IReadOnlyList<clsPlantillaMapeo>>(
            usuarioId, HttpMethod.Get, $"{RutaPlantillasMapeo}?clienteNit={Uri.EscapeDataString(nit)}", null, reintentable: true, lectura: false,
            lista => [.. SinNulos(lista).Select(MapearPlantilla)], cancellationToken);
    }

    public Task<clsResultado<bool>> EliminarPlantillaMapeoAsync(Guid usuarioId, Guid plantillaId, CancellationToken cancellationToken = default) =>
        EliminarAsync(usuarioId, $"{RutaPlantillasMapeo}/{plantillaId:D}", cancellationToken);

    public async Task<clsResultado<IReadOnlyList<clsTerminoCliente>>> ObtenerDiccionarioClienteAsync(
        Guid usuarioId, string? clienteNit, clsFiltroDiccionarioCliente? filtro = null, CancellationToken cancellationToken = default)
    {
        if (NitOpcional(clienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        filtro ??= new clsFiltroDiccionarioCliente();
        var parametros = new List<string>();
        if (nit is not null)
        {
            // NEXO QA (2026-11-06) aún no acepta clienteNit en este GET (solo clienteId): lo ignora y responde los términos de todos los
            // clientes. Se manda igual para que tome efecto cuando NEXO lo agregue (ADR-028, Consecuencias).
            parametros.Add("clienteNit=" + Uri.EscapeDataString(nit));
        }

        parametros.Add("incluirGenerales=" + (filtro.IncluirGenerales ? "true" : "false"));
        if (!string.IsNullOrWhiteSpace(filtro.Tipo))
        {
            parametros.Add("tipo=" + Uri.EscapeDataString(filtro.Tipo.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(filtro.Q))
        {
            parametros.Add("q=" + Uri.EscapeDataString(filtro.Q.Trim()));
        }

        if (filtro.SoloActivos)
        {
            parametros.Add("soloActivos=true");
        }

        return await EnviarYLeerAsync<List<clsTerminoWire>, IReadOnlyList<clsTerminoCliente>>(
            usuarioId, HttpMethod.Get, $"{RutaDiccionarioCliente}?{string.Join('&', parametros)}", null, reintentable: true, lectura: false,
            lista => [.. SinNulos(lista).Select(t => new clsTerminoCliente(
                t.Id, t.ClienteId, t.ClienteNombre, t.Termino ?? string.Empty, t.Tipo ?? string.Empty, t.Concepto ?? string.Empty, t.Contexto,
                t.Ejemplo, t.Activo, t.CreatedAt, t.UpdatedAt))],
            cancellationToken);
    }

    public Task<clsResultado<clsOpcionesDiccionarioCliente>> ObtenerOpcionesDiccionarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        EnviarYLeerAsync<clsOpcionesDiccionarioWire, clsOpcionesDiccionarioCliente>(
            usuarioId, HttpMethod.Get, RutaOpcionesDiccionario, null, reintentable: true, lectura: false,
            o => new clsOpcionesDiccionarioCliente(
                [.. SinNulos(o.Tipos)], [.. SinNulos(o.CamposDestino)], [.. SinNulos(o.Unidades)], [.. SinNulos(o.Contextos)]), cancellationToken);

    public async Task<clsResultado<clsLecturaEstructura>> LeerEstructuraAsync(
        Guid usuarioId, clsPeticionLeerEstructura peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        ArgumentNullException.ThrowIfNull(peticion.Archivo);
        if (NitOpcional(peticion.ClienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        var formulario = Formulario(peticion.Archivo);
        Campo(formulario, "clienteNit", nit);
        Campo(formulario, "instrucciones", peticion.Instrucciones);
        Campo(formulario, "receta", peticion.RecetaJson); // tal cual: JSON opaco
        return await EnviarYLeerAsync<clsLecturaWire, clsLecturaEstructura>(
            usuarioId, HttpMethod.Post, RutaLeerEstructura, formulario, reintentable: false, lectura: true,
            l => MapearLectura(l, peticion.Archivo.NombreArchivo), cancellationToken);
    }

    public async Task<clsResultado<clsRecetaGuardadaResultado>> GuardarRecetaLecturaAsync(
        Guid usuarioId, clsPeticionGuardarReceta peticion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        ArgumentNullException.ThrowIfNull(peticion.Archivo);
        if (NitObligatorio(peticion.ClienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        var formulario = Formulario(peticion.Archivo);
        Campo(formulario, "receta", peticion.RecetaJson); // tal cual: JSON opaco (NEXO responde RECETA_REQUERIDA si viene vacía)
        Campo(formulario, "clienteNit", nit);
        Campo(formulario, "nombre", peticion.Nombre);
        Campo(formulario, "instrucciones", peticion.Instrucciones);
        var respuesta = await EnviarAsync(usuarioId, HttpMethod.Put, RutaRecetasLectura, formulario, reintentable: false, lectura: true, cancellationToken);
        return respuesta.EsFallo
            ? respuesta.Error
            : Leer<clsRecetaGuardadaWire, clsRecetaGuardadaResultado>(respuesta.Valor,
                r => new clsRecetaGuardadaResultado(MapearReceta(r), respuesta.Valor.Estado == HttpStatusCode.Created));
    }

    public async Task<clsResultado<IReadOnlyList<clsRecetaGuardada>>> ListarRecetasLecturaAsync(
        Guid usuarioId, string clienteNit, CancellationToken cancellationToken = default)
    {
        // Sin NIT NEXO devuelve los formatos de TODOS los clientes: se corta aquí, sin llamar a NEXO.
        if (NitObligatorio(clienteNit, out var nit) is { } errorNit)
        {
            return errorNit;
        }

        return await EnviarYLeerAsync<List<clsRecetaGuardadaWire>, IReadOnlyList<clsRecetaGuardada>>(
            usuarioId, HttpMethod.Get, $"{RutaRecetasLectura}?clienteNit={Uri.EscapeDataString(nit)}", null, reintentable: true, lectura: false,
            lista => [.. SinNulos(lista).Select(MapearReceta)], cancellationToken);
    }

    public Task<clsResultado<bool>> EliminarRecetaLecturaAsync(Guid usuarioId, Guid recetaId, CancellationToken cancellationToken = default) =>
        EliminarAsync(usuarioId, $"{RutaRecetasLectura}/{recetaId:D}", cancellationToken);

    /// <summary>
    /// Tubería del cliente <see cref="NombreCliente"/>: cada intento con <paramref name="tiempo"/>, o con <paramref name="tiempoLectura"/> si la
    /// petición lleva <see cref="OpcionLectura"/>; reintentos (<see cref="ReintentosLectura"/>) solo de las peticiones con
    /// <see cref="OpcionReintentable"/> y nunca de un 429 (como el resto de Nexo, <see cref="clsCupoNexoHandler.NoReintentar429"/>). En total,
    /// lo mayor entre la lectura y un intento con sus reintentos; el circuito con una ventana de al menos el doble del intento más largo.
    /// </summary>
    public static void ConfigurarResiliencia(HttpStandardResilienceOptions resiliencia, TimeSpan tiempo, TimeSpan tiempoLectura)
    {
        ArgumentNullException.ThrowIfNull(resiliencia);
        var conReintentos = (tiempo * (ReintentosLectura + 1)) + TimeSpan.FromSeconds(2);
        var lectura = tiempoLectura + TimeSpan.FromSeconds(2);
        resiliencia.TotalRequestTimeout.Timeout = conReintentos > lectura ? conReintentos : lectura;
        resiliencia.AttemptTimeout.Timeout = tiempo;
        resiliencia.AttemptTimeout.TimeoutGenerator = argumentos =>
            ValueTask.FromResult(Marcada(argumentos.Context.GetRequestMessage(), OpcionLectura) ? tiempoLectura : tiempo);
        resiliencia.Retry.MaxRetryAttempts = ReintentosLectura;
        resiliencia.Retry.Delay = TimeSpan.FromMilliseconds(300);
        resiliencia.Retry.BackoffType = DelayBackoffType.Exponential;
        resiliencia.Retry.UseJitter = true;
        clsCupoNexoHandler.NoReintentar429(resiliencia);
        var transitorio = resiliencia.Retry.ShouldHandle;
        resiliencia.Retry.ShouldHandle = argumentos =>
            Marcada(argumentos.Context.GetRequestMessage(), OpcionReintentable) ? transitorio(argumentos) : ValueTask.FromResult(false);
        var mayor = tiempoLectura > tiempo ? tiempoLectura : tiempo;
        resiliencia.CircuitBreaker.SamplingDuration = mayor * 2;
        // Un 429 (IA_TOPE_DIARIO del comercial o el cupo por minuto) no es una caída de NEXO: no suma a la tasa de fallos del circuito, que
        // es de todos los comerciales.
        resiliencia.CircuitBreaker.ShouldHandle = argumentos => ValueTask.FromResult(
            argumentos.Outcome.Result?.StatusCode != HttpStatusCode.TooManyRequests && HttpClientResiliencePredicates.IsTransient(argumentos.Outcome));
    }

    private static bool Marcada(HttpRequestMessage? peticion, HttpRequestOptionsKey<bool> opcion) =>
        peticion is not null && peticion.Options.TryGetValue(opcion, out var valor) && valor;

    // --- Envío y traducción de errores ---

    private async Task<clsResultado<bool>> EliminarAsync(Guid usuarioId, string ruta, CancellationToken cancellationToken)
    {
        var respuesta = await EnviarAsync(usuarioId, HttpMethod.Delete, ruta, null, reintentable: false, lectura: false, cancellationToken);
        return respuesta.EsFallo ? clsResultado<bool>.Fallo(respuesta.Error) : clsResultado<bool>.Exito(true);
    }

    private async Task<clsResultado<TSalida>> EnviarYLeerAsync<TWire, TSalida>(
        Guid usuarioId, HttpMethod metodo, string ruta, HttpContent? contenido, bool reintentable, bool lectura, Func<TWire, TSalida> mapear,
        CancellationToken cancellationToken)
        where TWire : class
        where TSalida : notnull
    {
        var respuesta = await EnviarAsync(usuarioId, metodo, ruta, contenido, reintentable, lectura, cancellationToken);
        return respuesta.EsFallo ? clsResultado<TSalida>.Fallo(respuesta.Error) : Leer(respuesta.Valor, mapear);
    }

    /// <summary>El cuerpo de un 2xx de NEXO convertido a Aplicación; JSON inválido, vacío o con otra forma → no disponible.</summary>
    private clsResultado<TSalida> Leer<TWire, TSalida>(clsRespuestaNexo respuesta, Func<TWire, TSalida> mapear)
        where TWire : class
        where TSalida : notnull
    {
        try
        {
            var wire = JsonSerializer.Deserialize<TWire>(respuesta.Cuerpo, _json);
            if (wire is null)
            {
                return NoDisponible($"respuesta vacía de {respuesta.Ruta}");
            }

            return clsResultado<TSalida>.Exito(mapear(wire));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return NoDisponible($"JSON inválido en {respuesta.Ruta}", ex);
        }
    }

    private async Task<clsResultado<clsRespuestaNexo>> EnviarAsync(
        Guid usuarioId, HttpMethod metodo, string ruta, HttpContent? contenido, bool reintentable, bool lectura, CancellationToken cancellationToken)
    {
        using var contenidoPropio = contenido;
        if (_urlBase is null || !opciones.Value.Habilitada)
        {
            return NoDisponible(_urlBase is null ? "Nexo sin configurar (Nexo:UrlBase)" : "importación inteligente apagada (ImportacionInteligente:Habilitada)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno (almacén corporativo)");
        }

        var rutaSinConsulta = ruta.Split('?')[0];
        using var peticion = new HttpRequestMessage(metodo, clsCosteoNexo.ConstruirUrl(_urlBase, ruta)) { Content = contenido };
        peticion.Options.Set(OpcionReintentable, reintentable);
        peticion.Options.Set(OpcionLectura, lectura);
        clsIdentidadNexo.Identificar(peticion, idUsuarioExterno);
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var limite = opciones.Value.LimiteRespuestaMegabytes * 1024L * 1024L;
            if (respuesta.Content.Headers.ContentLength is { } largo && largo > limite)
            {
                return NoDisponible($"respuesta demasiado grande ({largo.ToString(CultureInfo.InvariantCulture)} bytes) en {metodo} {rutaSinConsulta}");
            }

            // Con ResponseHeadersRead el tiempo de la tubería solo cubre las cabeceras: el cuerpo se lee con el mismo tiempo del intento.
            using var tiempoCuerpo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tiempoCuerpo.CancelAfter(TimeSpan.FromSeconds(lectura ? opciones.Value.TimeoutLecturaSegundos : opciones.Value.TimeoutSegundos));
            await using var flujo = await respuesta.Content.ReadAsStreamAsync(tiempoCuerpo.Token);
            using var acotado = await clsCosteoNexo.LeerAcotadoAsync(flujo, limite, tiempoCuerpo.Token);
            if (acotado is null)
            {
                return NoDisponible($"respuesta demasiado grande (más de {limite.ToString(CultureInfo.InvariantCulture)} bytes) en {metodo} {rutaSinConsulta}");
            }

            var texto = Encoding.UTF8.GetString(acotado.GetBuffer(), 0, (int)acotado.Length);
            if (respuesta.IsSuccessStatusCode)
            {
                return new clsRespuestaNexo(respuesta.StatusCode, texto, rutaSinConsulta);
            }

            if ((int)respuesta.StatusCode < 400 || (int)respuesta.StatusCode >= 500)
            {
                return NoDisponible($"HTTP {((int)respuesta.StatusCode).ToString(CultureInfo.InvariantCulture)} en {metodo} {rutaSinConsulta}");
            }

            return TraducirRechazo(respuesta, texto, metodo, rutaSinConsulta);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de NEXO
        }
        catch (Exception ex) when (ex is OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            return NoDisponible($"tiempo agotado en {metodo} {rutaSinConsulta}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException or IOException)
        {
            return NoDisponible(
                (ex is Polly.CircuitBreaker.BrokenCircuitException ? "circuito abierto tras fallos repetidos" : "error de red") + $" en {metodo} {rutaSinConsulta}",
                ex);
        }
    }

    /// <summary>Un 4xx de NEXO como error de negocio (ver la tabla de la clase).</summary>
    private clsErrorDominio TraducirRechazo(HttpResponseMessage respuesta, string texto, HttpMethod metodo, string ruta)
    {
        var (codigo, mensaje) = LeerError(texto);
        var estado = (int)respuesta.StatusCode;
        var detalle = $"HTTP {estado.ToString(CultureInfo.InvariantCulture)}" + (codigo is null ? string.Empty : $" {codigo}");
        // Solo el estado y el código: el mensaje de NEXO puede repetir un encabezado del Excel o el NIT del cliente.
        logger.LogWarning("NEXO rechazó {Metodo} {Ruta}: {Detalle}", metodo, ruta, detalle);

        if (respuesta.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // #278 (DU §2.1): el cupo DIARIO de IA del comercial. Es suyo: no bloquea a nadie más (no toca clsCupoNexo) y no se reintenta.
            // Otro 429 (DEMASIADAS_SOLICITUDES, el cupo por minuto) sigue como en el resto de Nexo: no disponible, tras el Retry-After.
            return codigo == clsCodigosError.IaTopeDiario
                ? new clsErrorDominio(clsCodigosError.IaTopeDiario, mensaje ?? clsCosteoNexo.MensajeIaTopeDiario, detalle)
                : NoDisponible(clsCosteoNexo.ConEspera(detalle, respuesta));
        }

        if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            logger.LogError("NEXO respondió {Estado} a {Metodo} {Ruta}: revisar la identidad X-Portal-clsUsuario-Id del comercial y su rol (Comercial o Admin).",
                estado, metodo, ruta);
            return new clsErrorDominio(clsCodigosError.NexoSinPermiso, clsCosteoNexo.MensajeSinPermiso, detalle);
        }

        if (respuesta.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            return new clsErrorDominio(clsCodigosError.ArchivoDemasiadoGrande, "NEXO rechazó el archivo por tamaño (máximo 10 MB con el envío).", detalle);
        }

        if (respuesta.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound && codigo is not null && CodigosNexo.Contains(codigo))
        {
            return new clsErrorDominio(codigo, mensaje ?? MensajeRechazada, detalle);
        }

        return new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, MensajeRechazada, detalle);
    }

    private static (string? Codigo, string? Mensaje) LeerError(string texto)
    {
        try
        {
            using var documento = JsonDocument.Parse(texto);
            return documento.RootElement.ValueKind == JsonValueKind.Object
                ? (clsCosteoNexo.Texto(documento.RootElement, "code"), clsCosteoNexo.Texto(documento.RootElement, "message"))
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null); // 413 de Kestrel y otros cuerpos que no son el error de NEXO
        }
    }

    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Importación inteligente de NEXO no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo de la importación en NEXO ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.NexoNoDisponible, MensajeNoDisponible, motivo);
    }

    // --- Cliente por NIT ---

    /// <summary>NIT opcional: vacío → sin cliente (<c>null</c>); inválido → <c>CLIENTE_NIT_INVALIDO</c> sin llamar a NEXO.</summary>
    private static clsErrorDominio? NitOpcional(string? nit, out string? normalizado)
    {
        normalizado = NormalizarNit(nit);
        if (normalizado is null)
        {
            return string.IsNullOrWhiteSpace(nit) ? null : new clsErrorDominio(clsCodigosError.ClienteNitInvalido, MensajeNitInvalido);
        }

        return normalizado.Length > LargoMaximoNit ? new clsErrorDominio(clsCodigosError.ClienteNitInvalido, MensajeNitInvalido) : null;
    }

    /// <summary>NIT obligatorio: vacío → <c>CLIENTE_REQUERIDO</c>; inválido → <c>CLIENTE_NIT_INVALIDO</c>. Ninguno de los dos llama a NEXO.</summary>
    private static clsErrorDominio? NitObligatorio(string? nit, out string normalizado)
    {
        normalizado = string.Empty;
        if (string.IsNullOrWhiteSpace(nit))
        {
            return new clsErrorDominio(clsCodigosError.ClienteRequerido, MensajeClienteRequerido);
        }

        if (NitOpcional(nit, out var limpio) is { } error)
        {
            return error;
        }

        if (limpio is null)
        {
            return new clsErrorDominio(clsCodigosError.ClienteNitInvalido, MensajeNitInvalido);
        }

        normalizado = limpio;
        return null;
    }

    // --- Cuerpos ---

    private static StringContent Json(object cuerpo) =>
        new(JsonSerializer.Serialize(cuerpo, cuerpo.GetType(), _json), Encoding.UTF8, "application/json"); // re-enviable en un reintento

    private static clsColumnaWire ColumnaWire(clsColumnaConfirmada c) =>
        new(c.Indice, c.Campo, string.IsNullOrWhiteSpace(c.Transformacion) ? null : c.Transformacion);

    /// <summary>El formulario con el Excel en <c>archivo</c> (su nombre original); el contenido no se abre ni se valida aquí.</summary>
    private static MultipartFormDataContent Formulario(clsArchivoImportacion archivo)
    {
        ArgumentNullException.ThrowIfNull(archivo.Contenido);
        var formulario = new MultipartFormDataContent();
        var parte = new ByteArrayContent(archivo.Contenido);
        parte.Headers.ContentType = new MediaTypeHeaderValue(
            archivo.NombreArchivo?.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase) == true ? clsArchivoExcel.TipoXlsm : clsArchivoExcel.TipoXlsx);
        formulario.Add(parte, "archivo", string.IsNullOrWhiteSpace(archivo.NombreArchivo) ? "licitacion.xlsx" : archivo.NombreArchivo);
        return formulario;
    }

    private static void Campo(MultipartFormDataContent formulario, string nombre, string? valor)
    {
        if (!string.IsNullOrEmpty(valor))
        {
            formulario.Add(new StringContent(valor, Encoding.UTF8), nombre);
        }
    }

    // --- Mapeo de las respuestas ---

    /// <summary>
    /// Los elementos de una lista de NEXO; un <c>null</c> dentro de ella es una respuesta inválida (<see cref="JsonException"/>, que
    /// <see cref="Leer{TWire, TSalida}"/> traduce a no disponible), no una <see cref="NullReferenceException"/> al mapearla.
    /// </summary>
    private static IEnumerable<T> SinNulos<T>(IEnumerable<T?>? lista)
        where T : class =>
        (lista ?? []).Select(elemento => elemento ?? throw new JsonException("elemento null en una lista de la respuesta de NEXO"));

    private static clsSugerenciaMapeo MapearSugerencia(clsSugerenciaWire s) => new(
        s.IaUsada, s.MotivoSinIa,
        [.. SinNulos(s.Columnas).Select(c => new clsColumnaMapeada(
            c.Indice, c.Columna ?? string.Empty, c.Campo ?? string.Empty, c.Transformacion ?? string.Empty, c.Confianza, c.Origen ?? string.Empty, c.Nota))],
        [.. SinNulos(s.Dudas)], s.PlantillaId);

    private static clsResultadoAplicarMapeo MapearAplicar(clsResultadoAplicarWire r) => new(
        r.TotalFilas, r.FilasVacias, r.FilasConErrores,
        [.. SinNulos(r.Lineas).Select(l => new clsLineaImportada(
            l.Fila, l.Origen ?? string.Empty, l.Destino ?? string.Empty, l.TipoVehiculo, l.Carroceria, l.Tipologia, l.LineaNegocio, l.ClaseViaje,
            l.ClaseContenedor, l.Exclusivo, l.Consolidado, l.RetornaContenedor, l.CiudadDevolucionContenedor, l.LevanteContenedorDiferente,
            l.CiudadLevanteContenedor, l.HorasCargue, l.HorasDescargue, l.Peso, l.ValorMercancia,
            l.Posiciones is null ? null : SinNulos(l.Posiciones).Select(p => new clsPosicionImportada(
                p.Descripcion, p.Cantidad, p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg, p.CodigoTipoMercancia)).ToList(),
            l.Referencia, l.Deadline))],
        [.. SinNulos(r.Errores).Select(e => new clsErrorDeFila(e.Fila, e.Campo, e.Codigo ?? string.Empty, e.Mensaje ?? string.Empty))],
        r.TotalErrores);

    private static clsResultadoNormalizacion MapearNormalizacion(clsResultadoNormalizacionWire r) => new(
        r.IaUsada, r.MotivoSinIa, r.TotalDistintos,
        [.. SinNulos(r.Valores).Select(v => new clsValorNormalizado(
            v.Campo ?? string.Empty, v.Valor ?? string.Empty, v.Estado ?? string.Empty, v.Codigo, v.Descripcion, v.MunicipioId,
            v.Origen ?? string.Empty, v.Confianza, v.Nota,
            v.Candidatos is null ? null : SinNulos(v.Candidatos).Select(c => new clsCandidatoCiudad(
                c.MunicipioId, c.CodigoDane ?? string.Empty, c.Municipio ?? string.Empty, c.Departamento ?? string.Empty)).ToList()))]);

    private static clsPlantillaMapeo MapearPlantilla(clsPlantillaMapeoWire p) => new(
        p.Id, p.ClienteId, p.ClienteNombre, p.Nombre, [.. SinNulos(p.Encabezados)],
        [.. SinNulos(p.Columnas).Select(c => new clsColumnaConfirmada(c.Indice, c.Campo ?? string.Empty, c.Transformacion))],
        p.CreatedAt, p.UpdatedAt);

    /// <summary>La receta se copia tal cual (<see cref="JsonElement.Clone"/>): nunca se tipa ni se cambia.</summary>
    private static clsRecetaGuardada MapearReceta(clsRecetaGuardadaWire r) => new(
        r.Id, r.ClienteId, r.ClienteNombre, r.Nombre, r.Receta.Clone(), r.Instrucciones, r.Usos, r.UltimoUsoAt, r.CreatedAt, r.UpdatedAt);

    private static clsLecturaEstructura MapearLectura(clsLecturaWire l, string nombreArchivo) => new(
        string.IsNullOrWhiteSpace(l.Archivo) ? nombreArchivo : l.Archivo,
        l.OrigenReceta ?? string.Empty,
        l.Similitud,
        l.RecetaGuardadaId,
        l.IaUsada,
        l.MotivoSinIa,
        l.Receta.Clone(),
        l.Resumen,
        [.. SinNulos(l.Dudas)],
        [.. SinNulos(l.HojasDelArchivo).Select(h => new clsHojaDelArchivo(h.Nombre ?? string.Empty, h.Oculta, h.Filas, h.Columnas, h.Usada))],
        [.. SinNulos(l.Tablas).Select(t =>
        {
            var filas = (t.Filas ?? []).Select(f => (IReadOnlyList<string>)(f ?? []).Select(c => c ?? string.Empty).ToList()).ToList();
            return new clsHojaExcel(t.Nombre ?? string.Empty, t.FilaEncabezado, [.. (t.Encabezados ?? []).Select(e => e ?? string.Empty)], filas,
                t.TotalFilas ?? filas.Count);
        })],
        Avisos(l.Avisos),
        Cambios(l.Cambios));

    /// <summary>
    /// <c>avisos</c> de la lectura, con tolerancia (el documento de NEXO no fija su forma): una lista de textos tal cual; de un objeto, su
    /// <c>mensaje</c>/<c>message</c> si lo trae o su JSON; un texto suelto, una lista de uno. Otra forma no tumba la lectura.
    /// </summary>
    private static IReadOnlyList<string> Avisos(JsonElement avisos) => avisos.ValueKind switch
    {
        JsonValueKind.Array => [.. avisos.EnumerateArray().Where(a => a.ValueKind != JsonValueKind.Null).Select(Aviso)],
        JsonValueKind.String or JsonValueKind.Object => [Aviso(avisos)],
        _ => [],
    };

    private static string Aviso(JsonElement aviso) =>
        aviso.ValueKind == JsonValueKind.String ? aviso.GetString() ?? string.Empty
        : aviso.ValueKind == JsonValueKind.Object && (clsCosteoNexo.Texto(aviso, "mensaje") ?? clsCosteoNexo.Texto(aviso, "message")) is { } texto
            ? texto
            : aviso.GetRawText();

    /// <summary><c>cambios</c> con la misma tolerancia: solo un objeto se lee (<c>faltan</c>, <c>nuevos</c>); otra forma → sin cambios.</summary>
    private static clsCambiosDeFormato? Cambios(JsonElement cambios) =>
        cambios.ValueKind != JsonValueKind.Object ? null : new clsCambiosDeFormato(Titulos(cambios, "faltan"), Titulos(cambios, "nuevos"));

    private static IReadOnlyList<string> Titulos(JsonElement cambios, string propiedad) =>
        cambios.TryGetProperty(propiedad, out var lista) && lista.ValueKind == JsonValueKind.Array
            ? [.. lista.EnumerateArray().Where(t => t.ValueKind != JsonValueKind.Null).Select(Aviso)]
            : [];

    /// <summary>Un 2xx de NEXO: el estado (201 frente a 200 al guardar), el cuerpo y la ruta sin consulta (para el log).</summary>
    private sealed record clsRespuestaNexo(HttpStatusCode Estado, string Cuerpo, string Ruta);

    // --- Forma exacta de los cuerpos de NEXO (camelCase con JsonSerializerDefaults.Web): privados del adaptador ---

    private sealed record clsSugerirWire(string? ClienteNit, IReadOnlyList<string> Encabezados, IReadOnlyList<IReadOnlyList<string>>? Filas);

    private sealed record clsColumnaWire(int Indice, string Campo, string? Transformacion);

    private sealed record clsAplicarWire(
        string? ClienteNit, IReadOnlyList<clsColumnaWire> Columnas, IReadOnlyList<IReadOnlyList<string>> Filas, int? PrimeraFila);

    private sealed record clsValorWire(string Campo, string? Valor);

    private sealed record clsNormalizarWire(string? ClienteNit, IReadOnlyList<clsValorWire> Valores);

    private sealed record clsPlantillaWire(string ClienteNit, string? Nombre, IReadOnlyList<string> Encabezados, IReadOnlyList<clsColumnaWire> Columnas);

    private sealed record clsSugerenciaWire(
        bool IaUsada, string? MotivoSinIa, List<clsColumnaMapeadaWire>? Columnas, List<string>? Dudas, Guid? PlantillaId);

    private sealed record clsColumnaMapeadaWire(
        int Indice, string? Columna, string? Campo, string? Transformacion, double Confianza, string? Origen, string? Nota);

    private sealed record clsResultadoAplicarWire(
        int TotalFilas, int FilasVacias, int FilasConErrores, List<clsLineaWire>? Lineas, List<clsErrorFilaWire>? Errores, int TotalErrores);

    private sealed record clsLineaWire(
        int Fila, string? Origen, string? Destino, string? TipoVehiculo, string? Carroceria, string? Tipologia, string? LineaNegocio,
        string? ClaseViaje, string? ClaseContenedor, bool? Exclusivo, bool? Consolidado, bool? RetornaContenedor,
        string? CiudadDevolucionContenedor, bool? LevanteContenedorDiferente, string? CiudadLevanteContenedor, decimal? HorasCargue,
        decimal? HorasDescargue, decimal? Peso, decimal? ValorMercancia, List<clsPosicionWire>? Posiciones, string? Referencia,
        DateTimeOffset? Deadline);

    private sealed record clsPosicionWire(
        string? Descripcion, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg, string? CodigoTipoMercancia);

    private sealed record clsErrorFilaWire(int Fila, string? Campo, string? Codigo, string? Mensaje);

    private sealed record clsResultadoNormalizacionWire(bool IaUsada, string? MotivoSinIa, int TotalDistintos, List<clsValorNormalizadoWire>? Valores);

    private sealed record clsValorNormalizadoWire(
        string? Campo, string? Valor, string? Estado, string? Codigo, string? Descripcion, int? MunicipioId, string? Origen, double Confianza,
        string? Nota, List<clsCandidatoWire>? Candidatos);

    private sealed record clsCandidatoWire(int MunicipioId, string? CodigoDane, string? Municipio, string? Departamento);

    private sealed record clsPlantillaMapeoWire(
        Guid Id, Guid ClienteId, string? ClienteNombre, string? Nombre, List<string>? Encabezados, List<clsColumnaPlantillaWire>? Columnas,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

    private sealed record clsColumnaPlantillaWire(int Indice, string? Campo, string? Transformacion);

    private sealed record clsTerminoWire(
        Guid Id, Guid? ClienteId, string? ClienteNombre, string? Termino, string? Tipo, string? Concepto, string? Contexto, string? Ejemplo,
        bool Activo, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

    private sealed record clsOpcionesDiccionarioWire(List<string>? Tipos, List<string>? CamposDestino, List<string>? Unidades, List<string>? Contextos);

    private sealed record clsLecturaWire(
        string? Archivo, string? OrigenReceta, double? Similitud, Guid? RecetaGuardadaId, bool IaUsada, string? MotivoSinIa, JsonElement Receta,
        string? Resumen, List<string>? Dudas, List<clsHojaDelArchivoWire>? HojasDelArchivo, List<clsTablaWire>? Tablas, JsonElement Avisos,
        JsonElement Cambios);

    private sealed record clsHojaDelArchivoWire(string? Nombre, bool Oculta, int Filas, int Columnas, bool Usada);

    private sealed record clsTablaWire(string? Nombre, int FilaEncabezado, List<string?>? Encabezados, List<List<string?>?>? Filas, int? TotalFilas);

    private sealed record clsRecetaGuardadaWire(
        Guid Id, Guid ClienteId, string? ClienteNombre, string? Nombre, JsonElement Receta, string? Instrucciones, int Usos,
        DateTimeOffset? UltimoUsoAt, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);
}

/// <summary>
/// Importación inteligente apagada (<c>ImportacionInteligente:Habilitada = false</c>) o sin <c>Nexo:UrlBase</c> (HU-044.1, #252): todo
/// responde <c>NEXO_NO_DISPONIBLE</c> sin llamar a NEXO. El comercial sigue con el mapeo manual.
/// </summary>
public sealed class clsImportacionExcelNoDisponible : IClsImportacionExcelNexo
{
    public const string Mensaje =
        "La importación inteligente de NEXO no está habilitada en este ambiente (ImportacionInteligente:Habilitada o Nexo:UrlBase): mapea las columnas a mano.";

    public bool EstaHabilitada => false;

    public Task<clsResultado<clsSugerenciaMapeo>> SugerirMapeoAsync(Guid usuarioId, clsPeticionSugerirMapeo peticion, CancellationToken cancellationToken = default) =>
        NoDisponible<clsSugerenciaMapeo>();

    public Task<clsResultado<clsResultadoAplicarMapeo>> AplicarMapeoAsync(Guid usuarioId, clsPeticionAplicarMapeo peticion, CancellationToken cancellationToken = default) =>
        NoDisponible<clsResultadoAplicarMapeo>();

    public Task<clsResultado<clsResultadoNormalizacion>> NormalizarValoresAsync(
        Guid usuarioId, string? clienteNit, IReadOnlyList<clsValorANormalizar> valores, CancellationToken cancellationToken = default) =>
        NoDisponible<clsResultadoNormalizacion>();

    public Task<clsResultado<clsPlantillaGuardada>> GuardarPlantillaMapeoAsync(Guid usuarioId, clsPeticionPlantillaMapeo peticion, CancellationToken cancellationToken = default) =>
        NoDisponible<clsPlantillaGuardada>();

    public Task<clsResultado<IReadOnlyList<clsPlantillaMapeo>>> ListarPlantillasMapeoAsync(Guid usuarioId, string clienteNit, CancellationToken cancellationToken = default) =>
        NoDisponible<IReadOnlyList<clsPlantillaMapeo>>();

    public Task<clsResultado<bool>> EliminarPlantillaMapeoAsync(Guid usuarioId, Guid plantillaId, CancellationToken cancellationToken = default) =>
        NoDisponible<bool>();

    public Task<clsResultado<IReadOnlyList<clsTerminoCliente>>> ObtenerDiccionarioClienteAsync(
        Guid usuarioId, string? clienteNit, clsFiltroDiccionarioCliente? filtro = null, CancellationToken cancellationToken = default) =>
        NoDisponible<IReadOnlyList<clsTerminoCliente>>();

    public Task<clsResultado<clsOpcionesDiccionarioCliente>> ObtenerOpcionesDiccionarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        NoDisponible<clsOpcionesDiccionarioCliente>();

    public Task<clsResultado<clsLecturaEstructura>> LeerEstructuraAsync(Guid usuarioId, clsPeticionLeerEstructura peticion, CancellationToken cancellationToken = default) =>
        NoDisponible<clsLecturaEstructura>();

    public Task<clsResultado<clsRecetaGuardadaResultado>> GuardarRecetaLecturaAsync(Guid usuarioId, clsPeticionGuardarReceta peticion, CancellationToken cancellationToken = default) =>
        NoDisponible<clsRecetaGuardadaResultado>();

    public Task<clsResultado<IReadOnlyList<clsRecetaGuardada>>> ListarRecetasLecturaAsync(Guid usuarioId, string clienteNit, CancellationToken cancellationToken = default) =>
        NoDisponible<IReadOnlyList<clsRecetaGuardada>>();

    public Task<clsResultado<bool>> EliminarRecetaLecturaAsync(Guid usuarioId, Guid recetaId, CancellationToken cancellationToken = default) =>
        NoDisponible<bool>();

    private static Task<clsResultado<T>> NoDisponible<T>() =>
        Task.FromResult(clsResultado<T>.Fallo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, Mensaje, "importación inteligente no habilitada")));
}
