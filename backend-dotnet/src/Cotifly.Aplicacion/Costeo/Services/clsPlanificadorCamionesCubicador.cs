using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Camiones de una ruta según el cubicador (ADR-019): manda la ruta a
/// <c>POST /recomendaciones</c> (<see cref="IClsCubicador.RecomendarAsync"/>: el id de la ruta, la clase de viaje como preferencia, el
/// <c>idUsuarioExterno</c> del comercial y cada mercancía con su <c>codigoMercancia</c>) y crea <b>un camión por vehículo</b> de la
/// respuesta, en su orden, con el código de vehículo y de carrocería, el peso que lleva, el volumen, los dos porcentajes de llenado y el
/// <c>idRecomendacionVehiculo</c>.
/// </summary>
/// <remarks>
/// El vehículo que eligió el comercial (ADR-022) (<c>tipo_vehiculo</c>/<c>carroceria</c> de la ruta) es el RESPALDO del
/// cubicador. Orden de decisión por ruta:
/// <list type="number">
/// <item><b>Sin mercancías cubicables</b> (<see cref="clsLineaSolicitudCosteo.Cubicabilidad"/> distinta de <c>Cubicable</c>: solo el
/// peso, mercancías sin medidas ni tipo, o una ruta mixta): NO se llama al cubicador; un camión con el vehículo elegido (validado contra
/// Nexo en la captura) y el peso de la ruta. La ruta mixta lleva el aviso <c>MERCANCIAS_NO_CUBICABLES</c>.</item>
/// <item><b>Sin cubicador configurado</b> (warning una vez por captura): igual, con el elegido (la captura ya lo validó: sin cubicador se
/// sabe que se usa; se revalida como en el punto 4) y el aviso <c>VEHICULO_ELEGIDO_USADO</c> si había uno.</item>
/// <item><b>ASIGNADO</b>: manda el cubicador (un camión por vehículo recomendado). Si había un vehículo elegido distinto, aviso
/// <c>VEHICULO_AJUSTADO_POR_CUBICADOR</c> con el elegido y los finales. El elegido no se valida.</item>
/// <item><b>Sin selección automática, caída, ocupado o presupuesto agotado</b> (con <see cref="IClsCubicador.RespaldoSiFalla"/>): un
/// camión con el vehículo elegido y el aviso <c>VEHICULO_ELEGIDO_USADO</c>. Validación TARDÍA: el elegido no se validó en la captura (la
/// ruta iba al cubicador), así que se valida aquí contra el catálogo y el alias de Nexo (<see cref="IClsValidadorCatalogosCaptura"/>); si
/// no es un código de Nexo, el camión va sin vehículo (o sin la carrocería) con el aviso <c>VEHICULO_ELEGIDO_INVALIDO</c> y la captura no
/// se pierde. Con el catálogo de Nexo caído (sin copia utilizable) va tal cual: el envío lo resuelve contra la copia vencida o el alias y,
/// si no, Nexo lo recibe para normalizarlo (ADR-022). Sin elegido, el camión va sin vehículo (lo resuelve la Mesa de Costeo).</item>
/// </list>
/// <list type="bullet">
/// <item><b>Condicionada</b>: no se parte ni se bloquea nada; los camiones se crean como vienen. Una línea informativa en el log.</item>
/// <item>Una mercancía con el <c>id_tipo_mercancia</c> obsoleto se traduce al código con el catálogo; si no se
/// puede, la ruta va al respaldo con el elegido.</item>
/// <item><b>Rechazo de la petición, respuesta inválida o llave rechazada</b> (<c>MERCANCIAS_REQUERIDAS</c>, <c>MERCANCIA_DESCONOCIDA</c>,
/// <c>MERCANCIA_NO_CUBICABLE</c>, <c>DIMENSIONES_INVALIDAS</c>, <c>CUBICADOR_RESPUESTA_INVALIDA</c>, <c>CUBICADOR_NO_AUTORIZADO</c>): el
/// error vuelve tal cual y la captura no guarda nada. Nunca van al respaldo: esconderían un dato malo (contra el fallo silencioso).</item>
/// <item><b>Caída del cubicador</b> (tiempo agotado, 5xx, red, otro rechazo no previsto o presupuesto de la captura
/// agotado): con <see cref="IClsCubicador.RespaldoSiFalla"/> la ruta va al respaldo, con un warning de código estable
/// <see cref="EventoRespaldo"/> y el contador <see cref="MetricaRespaldos"/> (etiqueta <c>motivo</c>); sin él,
/// <c>CUBICADOR_NO_DISPONIBLE</c> y la captura no guarda nada. Solo una caída o el presupuesto agotado mandan al respaldo las rutas
/// siguientes de la misma captura sin volver a llamar (el planificador es por petición).</item>
/// <item><b>Presupuesto</b> (<see cref="IClsCubicador.PresupuestoCaptura"/>): una licitación hace un POST por fila; pasado el presupuesto
/// desde la primera llamada, las filas restantes no llaman al cubicador.</item>
/// <item><b>Vehículos</b>: un código de más de <see cref="clsSolicitudCosteo.LargoCatalogo"/> caracteres o un peso fuera del rango de la
/// captura es una respuesta inválida.</item>
/// </list>
/// </remarks>
public sealed class clsPlanificadorCamionesCubicador : IClsPlanificadorCamiones
{
    public const string MensajeNoDisponible =
        "No se pudo calcular los vehículos de la ruta: el cubicador no está disponible. Intenta de nuevo más tarde.";

    /// <summary>Nombre del medidor (<c>dotnet-counters</c>).</summary>
    public const string NombreMedidor = "Cotifly.Cubicador";

    /// <summary>Rutas que quedaron con el camión del respaldo por una falla del cubicador (etiqueta <c>motivo</c>).</summary>
    public const string MetricaRespaldos = "cotifly.cubicador.respaldos";

    /// <summary>Evento estable del warning de respaldo por falla (<c>CUBICADOR_RESPALDO</c>, id 2101).</summary>
    public static readonly EventId EventoRespaldo = new(2101, "CUBICADOR_RESPALDO");

    /// <summary>Evento estable del warning "cubicador sin configurar" (<c>CUBICADOR_SIN_CONFIGURAR</c>, id 2100).</summary>
    public static readonly EventId EventoSinConfigurar = new(2100, "CUBICADOR_SIN_CONFIGURAR");

    /// <summary>Códigos que vuelven tal cual al comercial (rechazos del contrato y respuesta inválida): nunca van al respaldo.</summary>
    public static readonly IReadOnlySet<string> CodigosSinRespaldo = new HashSet<string>(StringComparer.Ordinal)
    {
        clsCodigosError.MercanciasRequeridas,
        clsCodigosError.MercanciaDesconocida,
        clsCodigosError.MercanciaNoCubicable,
        clsCodigosError.DimensionesInvalidas,
        // Más de 10.000 unidades (lo valida el adaptador antes de llamar, o el 400 del cubicador): se parte la carga.
        clsCodigosError.CubicacionDemasiadoGrande,
        clsCodigosError.CubicadorRespuestaInvalida,
        // El 422 del cubicador es un rechazo de los datos de la carga (con la mercancía si la señala).
        clsCodigosLineaSolicitud.PosicionInvalida,
        clsCodigosError.CubicadorPeticionRechazada,
        // Una llave mala no puede convertirse en silencio en rutas sin vehículo.
        clsCodigosError.CubicadorNoAutorizado,
    };

    private readonly ILogger<clsPlanificadorCamionesCubicador> _logger;
    private readonly IClsCubicador? _cubicador;
    private readonly IClsUsuarioWebRepository? _usuarios;
    private readonly IClsValidadorCatalogosCaptura? _catalogos;
    private readonly Counter<long>? _respaldos;

    /// <summary>¿Ya cayó el cubicador (o se agotó el presupuesto) en esta petición? Las rutas siguientes van directo al respaldo.</summary>
    private bool _caido;

    private bool _avisoSinConfigurar;
    private long? _inicio;

    /// <summary>El <c>idUsuarioExterno</c> del comercial (en texto) ya leído en esta petición.</summary>
    private (Guid Usuario, string? Externo)? _usuarioExterno;

    /// <summary>Catálogo de tipos de mercancía por uuid (transición de <c>id_tipo_mercancia</c>), leído una vez por petición.</summary>
    private IReadOnlyDictionary<Guid, string>? _codigosPorId;

    public clsPlanificadorCamionesCubicador(
        ILogger<clsPlanificadorCamionesCubicador> logger,
        IClsCubicador? cubicador = null,
        IMeterFactory? medidores = null,
        IClsUsuarioWebRepository? usuarios = null,
        IClsValidadorCatalogosCaptura? catalogos = null)
    {
        _logger = logger;
        _cubicador = cubicador;
        _usuarios = usuarios;
        _catalogos = catalogos;
        _respaldos = medidores?.Create(NombreMedidor).CreateCounter<long>(MetricaRespaldos,
            description: "Rutas que quedaron con un camión sin vehículo porque el cubicador falló (etiqueta motivo).");
    }

    public Task<clsResultado<clsPlanRuta>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default) =>
        PlanificarAsync(usuarioId, rutaId, ruta, ocupadoEsError: false, cancellationToken);

    /// <summary>
    /// En la licitación en segundo plano, <c>CUBICADOR_OCUPADO</c> vuelve tal
    /// cual (sin respaldo y sin marcar la captura como caída) para que la ruta vuelva a la cola sin gastar un intento.
    /// </summary>
    public Task<clsResultado<clsPlanRuta>> PlanificarEnSegundoPlanoAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default) =>
        PlanificarAsync(usuarioId, rutaId, ruta, ocupadoEsError: true, cancellationToken);

    /// <summary>
    /// El respaldo por caída del cubicador sin llamarlo (<c>usar_respaldo</c> de la licitación): el mismo
    /// <see cref="RespaldoAsync"/> que usa el camino en segundo plano, con la causa <see cref="CausaNoDisponible"/>.
    /// </summary>
    public Task<clsResultado<clsPlanRuta>> PlanificarRespaldoAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return RespaldoAsync(usuarioId, ruta, null, CausaNoDisponible, cancellationToken);
    }

    private async Task<clsResultado<clsPlanRuta>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, bool ocupadoEsError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ruta);

        // Sin mercancías cubicables no se llama al cubicador: la ruta usa el vehículo elegido (validado en la captura).
        var cubicabilidad = ruta.Cubicabilidad;
        if (cubicabilidad != CubicabilidadRuta.Cubicable)
        {
            _logger.LogInformation(
                "La ruta {RutaId} no tiene mercancías cubicables ({Cubicabilidad}): no se llama al cubicador y lleva el vehículo elegido.",
                rutaId, cubicabilidad);
            return Elegido(ruta, cubicabilidad == CubicabilidadRuta.Mixta ? [clsAvisosVehiculo.Mixta(VehiculoElegido(ruta) ?? _sinVehiculo)] : null);
        }

        if (_cubicador is not { EstaConfigurado: true } cubicador)
        {
            if (!_avisoSinConfigurar)
            {
                _avisoSinConfigurar = true;
                _logger.LogWarning(EventoSinConfigurar,
                    "CUBICADOR_SIN_CONFIGURAR: Cubicador:UrlBase vacía; las rutas de esta captura quedan con un camión con el vehículo elegido o sin vehículo (respaldo).");
            }

            // Sin cubicador el elegido se usa seguro (la captura ya lo validó); se vuelve a validar (con la caché del catálogo) porque la
            // versión nueva de la re-solicitud o la edición pueden traerlo sin pasar por la validación de la captura.
            return await RespaldoAsync(usuarioId, ruta, null, CausaSinConfigurar, cancellationToken);
        }

        var posiciones = ruta.Posiciones.Where(p => p is not null).ToList();
        if (!_caido && posiciones.Any(p => string.IsNullOrWhiteSpace(p.CodigoMercancia) && p.IdTipoMercancia is { } id && id != Guid.Empty))
        {
            var traducidas = await TraducirIdsAsync(cubicador, posiciones, cancellationToken);
            if (traducidas.EsFallo)
            {
                var caidaCatalogo = clsErroresCubicador.EsCaida(traducidas.Error);
                return await FallaAsync(usuarioId, cubicador, rutaId, ruta, caidaCatalogo ? "caida" : "rechazo",
                    traducidas.Error.Detalle ?? traducidas.Error.Codigo, caidaCatalogo, cancellationToken);
            }

            posiciones = [.. traducidas.Valor];
        }

        var mercancias = posiciones.Select(clsMercanciaCubicador.Desde).ToList();
        if (mercancias.Count == 0 || mercancias.Any(m => m is null))
        {
            // Transición: un id_tipo_mercancia que el catálogo del cubicador no tradujo deja la mercancía sin código.
            _logger.LogInformation(
                "La ruta {RutaId} trae una mercancía sin codigoMercancia que el catálogo no tradujo: lleva el vehículo elegido (respaldo).", rutaId);
            return await RespaldoAsync(usuarioId, ruta, null, CausaSinCodigo, cancellationToken);
        }

        if (_caido)
        {
            return await RespaldoAsync(usuarioId, ruta, null, CausaNoDisponible, cancellationToken);
        }

        if (_inicio is { } inicio && Stopwatch.GetElapsedTime(inicio) > cubicador.PresupuestoCaptura)
        {
            return await FallaAsync(usuarioId, cubicador, rutaId, ruta, "presupuesto", "presupuesto de tiempo de la captura agotado", caida: true,
                cancellationToken);
        }

        _inicio ??= Stopwatch.GetTimestamp();
        var peticion = new clsPeticionRecomendacion(
            rutaId.ToString("D"),
            string.IsNullOrWhiteSpace(ruta.ClaseViaje) ? null : ruta.ClaseViaje.Trim(),
            await UsuarioExternoAsync(usuarioId, cancellationToken),
            [.. mercancias.OfType<clsMercanciaCubicador>()]);
        var recomendacion = await cubicador.RecomendarAsync(peticion, cancellationToken);
        if (recomendacion.EsFallo)
        {
            if (ocupadoEsError && recomendacion.Error.Codigo == clsCodigosError.CubicadorOcupado)
            {
                _logger.LogInformation("El cubicador está ocupado para la ruta {RutaId} ({Detalle}): vuelve a la cola sin respaldo.",
                    rutaId, recomendacion.Error.Detalle);
                return new clsErrorDominio(recomendacion.Error.Codigo, recomendacion.Error.Mensaje);
            }

            if (CodigosSinRespaldo.Contains(recomendacion.Error.Codigo))
            {
                _logger.LogWarning("El cubicador no aceptó la ruta {RutaId} ({Codigo}: {Detalle}): no se guarda la captura.",
                    rutaId, recomendacion.Error.Codigo, recomendacion.Error.Detalle);
                // Solo el código, el mensaje y la mercancía señalada (item y campo): el detalle interno (estado HTTP) y las marcas
                // internas quedan en el log.
                return new clsErrorDominio(recomendacion.Error.Codigo, recomendacion.Error.Mensaje) { Datos = DatosMercancia(recomendacion.Error.Datos) };
            }

            var caida = clsErroresCubicador.EsCaida(recomendacion.Error);
            return await FallaAsync(usuarioId, cubicador, rutaId, ruta, caida ? "caida" : "rechazo",
                recomendacion.Error.Detalle ?? recomendacion.Error.Codigo, caida, cancellationToken);
        }

        return await CamionesAsync(usuarioId, rutaId, ruta, recomendacion.Valor, cancellationToken);
    }

    /// <summary>
    /// De los datos de un rechazo del cubicador solo pasan <c>item</c> (la mercancía, base 0) y <c>campo</c>; sin <c>item</c>,
    /// ninguno.
    /// </summary>
    public static Dictionary<string, object?>? DatosMercancia(IReadOnlyDictionary<string, object?>? datos)
    {
        if (datos is null || !datos.TryGetValue("item", out var item) || item is null)
        {
            return null;
        }

        var visibles = new Dictionary<string, object?>(StringComparer.Ordinal) { ["item"] = item };
        if (datos.TryGetValue("campo", out var campo) && campo is not null)
        {
            visibles["campo"] = campo;
        }

        return visibles;
    }

    /// <summary>
    /// Compatibilidad: una mercancía con el <c>id_tipo_mercancia</c> anterior (uuid) y sin <c>codigoMercancia</c> se traduce al
    /// código con el catálogo del cubicador (<c>GET /tipos-mercancia</c>, con caché; se lee una vez por petición). Un id que no está en el
    /// catálogo deja la mercancía sin código (la ruta va al respaldo).
    /// </summary>
    private async Task<clsResultado<IReadOnlyList<clsPosicionCarga>>> TraducirIdsAsync(
        IClsCubicador cubicador, IReadOnlyList<clsPosicionCarga> posiciones, CancellationToken cancellationToken)
    {
        if (_codigosPorId is null)
        {
            var catalogo = await cubicador.ListarTiposMercanciaAsync(cancellationToken);
            if (catalogo.EsFallo)
            {
                return catalogo.Error;
            }

            var porId = new Dictionary<Guid, string>();
            foreach (var tipo in catalogo.Valor)
            {
                if (tipo.Id is { } id)
                {
                    porId.TryAdd(id, tipo.Codigo);
                }
            }

            _codigosPorId = porId;
        }

        var codigos = _codigosPorId;
        return clsResultado<IReadOnlyList<clsPosicionCarga>>.Exito([.. posiciones.Select(p =>
            string.IsNullOrWhiteSpace(p.CodigoMercancia) && p.IdTipoMercancia is { } id && codigos.TryGetValue(id, out var codigo)
                ? p with { CodigoMercancia = codigo }
                : p)]);
    }

    /// <summary>El <c>idUsuarioExterno</c> del comercial en texto (<c>usuarioId</c> del contrato), o <c>null</c> si no se conoce.</summary>
    private async Task<string?> UsuarioExternoAsync(Guid usuarioId, CancellationToken cancellationToken)
    {
        if (_usuarios is null || usuarioId == Guid.Empty)
        {
            return null;
        }

        if (_usuarioExterno is { } leido && leido.Usuario == usuarioId)
        {
            return leido.Externo;
        }

        var usuario = await _usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        var externo = usuario?.IdUsuarioExterno?.ToString(CultureInfo.InvariantCulture);
        _usuarioExterno = (usuarioId, externo);
        return externo;
    }

    private async Task<clsResultado<clsPlanRuta>> FallaAsync(
        Guid usuarioId, IClsCubicador cubicador, Guid rutaId, clsLineaSolicitudCosteo ruta, string motivo, string detalle, bool caida,
        CancellationToken cancellationToken)
    {
        if (!cubicador.RespaldoSiFalla)
        {
            _logger.LogWarning(EventoRespaldo, "El cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Motivo}: {Detalle}): no se guarda la captura.",
                rutaId, motivo, detalle);
            return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoDisponible);
        }

        if (caida && !_caido)
        {
            _caido = true;
            _logger.LogWarning(EventoRespaldo,
                "CUBICADOR_RESPALDO: el cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Motivo}: {Detalle}); esta ruta y las siguientes de la captura quedan con un camión con el vehículo elegido o, sin él, sin vehículo (lo resuelve la Mesa de Costeo de Nexo).",
                rutaId, motivo, detalle);
        }
        else if (!caida)
        {
            _logger.LogWarning(EventoRespaldo,
                "CUBICADOR_RESPALDO: el cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Motivo}: {Detalle}); la ruta queda con un camión con el vehículo elegido o, sin él, sin vehículo (lo resuelve la Mesa de Costeo de Nexo).",
                rutaId, motivo, detalle);
        }

        _respaldos?.Add(1, new KeyValuePair<string, object?>("motivo", motivo));
        return await RespaldoAsync(usuarioId, ruta, null, motivo == "presupuesto" ? CausaPresupuesto : CausaNoDisponible, cancellationToken);
    }

    /// <summary>Causa del aviso del vehículo: el cubicador no seleccionó vehículo.</summary>
    public const string CausaSinSeleccion = "El cubicador no seleccionó vehículo";

    /// <summary>Causa del aviso del vehículo: el cubicador no respondió, falló o estaba ocupado.</summary>
    public const string CausaNoDisponible = "El cubicador no estuvo disponible";

    /// <summary>Causa del aviso del vehículo: se agotó el presupuesto de tiempo de la captura para el cubicador.</summary>
    public const string CausaPresupuesto = "Se agotó el tiempo para consultar el cubicador en esta captura";

    /// <summary>Causa del aviso del vehículo: el cubicador no está configurado.</summary>
    public const string CausaSinConfigurar = "El cubicador no está configurado";

    /// <summary>Causa del aviso del vehículo: una mercancía quedó sin código del catálogo del cubicador.</summary>
    public const string CausaSinCodigo = "Una mercancía no tiene código del catálogo del cubicador";

    private static readonly clsVehiculoAviso _sinVehiculo = new(null, null);

    /// <summary>El vehículo elegido de la ruta, o <c>null</c> si no trae tipo de vehículo ni carrocería.</summary>
    private static clsVehiculoAviso? VehiculoElegido(clsLineaSolicitudCosteo ruta) =>
        string.IsNullOrWhiteSpace(ruta.TipoVehiculo) && string.IsNullOrWhiteSpace(ruta.Carroceria)
            ? null
            : new clsVehiculoAviso(Limpio(ruta.TipoVehiculo), Limpio(ruta.Carroceria));

    private static string? Limpio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    /// <summary>Un camión con el vehículo elegido TAL CUAL (ya validado en la captura) y el peso de la ruta.</summary>
    private static clsResultado<clsPlanRuta> Elegido(clsLineaSolicitudCosteo ruta, IReadOnlyList<clsAdvertenciaCubicador>? avisos) =>
        clsResultado<clsPlanRuta>.Exito(new clsPlanRuta(clsPlanificadorCamionSinVehiculo.Camiones(ruta), null, avisos));

    /// <summary>
    /// El respaldo de una ruta que SÍ iba al cubicador (sin selección, caída, ocupado, presupuesto o mercancía sin traducir):
    /// un camión con el vehículo elegido validado TARDE contra Nexo (<see cref="ValidarElegidoAsync"/>) y su aviso; sin elegido, sin vehículo.
    /// </summary>
    private async Task<clsResultado<clsPlanRuta>> RespaldoAsync(
        Guid usuarioId, clsLineaSolicitudCosteo ruta, clsCubicacionRuta? cubicacion, string causa, CancellationToken cancellationToken)
    {
        var peso = clsPlanificadorCamionSinVehiculo.PesoTotal(ruta);
        if (VehiculoElegido(ruta) is not { } elegido)
        {
            return clsResultado<clsPlanRuta>.Exito(new clsPlanRuta([new clsCamionPlaneado(null, null, peso)], cubicacion));
        }

        var (final, valido) = await ValidarElegidoAsync(usuarioId, ruta, cancellationToken);
        var aviso = valido ? clsAvisosVehiculo.Usado(final, causa) : clsAvisosVehiculo.Invalido(elegido, final, causa);
        return clsResultado<clsPlanRuta>.Exito(new clsPlanRuta([new clsCamionPlaneado(final.TipoVehiculo, final.Carroceria, peso)], cubicacion, [aviso]));
    }

    /// <summary>
    /// Validación tardía del vehículo elegido contra el catálogo y el alias de Nexo (la misma regla de la captura,
    /// <see cref="IClsValidadorCatalogosCaptura.ValidarLineasAsync"/>): devuelve el vehículo que viaja, escrito como en Nexo, y si el elegido
    /// era válido. Un tipo de vehículo que no es de Nexo deja el camión sin vehículo; una carrocería que no lo es, sin carrocería. Con el
    /// catálogo maestro caído (sin copia utilizable) el tipo va TAL CUAL: el envío lo resuelve contra la copia vencida o el alias y, si no,
    /// Nexo lo recibe para normalizarlo (ADR-022).
    /// </summary>
    private async Task<(clsVehiculoAviso Final, bool Valido)> ValidarElegidoAsync(
        Guid usuarioId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken)
    {
        var tipo = Limpio(ruta.TipoVehiculo);
        var carroceria = Limpio(ruta.Carroceria);
        if (_catalogos is null)
        {
            return (new clsVehiculoAviso(tipo, carroceria), true);
        }

        var soloVehiculo = new clsLineaSolicitudCosteo { Origen = ruta.Origen, Destino = ruta.Destino, TipoVehiculo = tipo, Carroceria = carroceria };
        var validacion = await _catalogos.ValidarLineasAsync(usuarioId, [soloVehiculo], cancellationToken);
        var errores = validacion.Errores is [var deRuta, ..] ? deRuta : [];
        var canonica = validacion.Canonicas is [{ } primera, ..] ? primera : soloVehiculo;
        var tipoInvalido = errores.Any(e => e.Campo == "tipo_vehiculo");
        var carroceriaInvalida = errores.Any(e => e.Campo == "carroceria");
        if (validacion.NoDisponible is not null && tipo is not null)
        {
            _logger.LogWarning("El catálogo de vehículos de Nexo no está disponible para validar el vehículo elegido ({Codigo}): va tal cual.",
                validacion.NoDisponible.Codigo);
        }

        if (tipoInvalido || carroceriaInvalida)
        {
            _logger.LogWarning("El vehículo elegido no es un código de Nexo ({Campos}): la ruta va sin él (VEHICULO_ELEGIDO_INVALIDO).",
                string.Join(", ", errores.Select(e => e.Campo)));
        }

        var final = tipoInvalido
            ? _sinVehiculo
            : new clsVehiculoAviso(Limpio(canonica.TipoVehiculo) ?? tipo, carroceriaInvalida ? null : Limpio(canonica.Carroceria) ?? carroceria);
        return (final, !tipoInvalido && !carroceriaInvalida);
    }

    /// <summary>
    /// Un camión por vehículo recomendado (en su orden), con el aviso <c>VEHICULO_AJUSTADO_POR_CUBICADOR</c> si el elegido era otro; "sin
    /// selección" es el respaldo con el vehículo elegido (<see cref="RespaldoAsync"/>).
    /// </summary>
    private async Task<clsResultado<clsPlanRuta>> CamionesAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, clsRecomendacionCubicador recomendacion, CancellationToken cancellationToken)
    {
        if (recomendacion.Condicionada)
        {
            _logger.LogInformation("Ruta {RutaId} condicionada por el cubicador ({Mercancias} mercancía(s)): la solicitud se envía completa a la Mesa de Costeo de Nexo.",
                rutaId, recomendacion.MercanciasCondicionadas.Count);
        }

        if (!recomendacion.Asignado)
        {
            _logger.LogInformation(
                "El cubicador no seleccionó vehículo en la ruta {RutaId} ({Motivo}): el camión lleva el vehículo elegido o, sin él, queda sin vehículo para la Mesa de Costeo de Nexo.",
                rutaId, recomendacion.MotivoSinSeleccion ?? "sin motivo");
            // La ruta guarda el resultado y el motivo; el camión va con el vehículo elegido (validado aquí contra
            // Nexo: VEHICULO_ELEGIDO_USADO o VEHICULO_ELEGIDO_INVALIDO) o sin vehículo, y sin recomendación.
            return await RespaldoAsync(usuarioId, ruta, recomendacion.ACubicacionRuta(), CausaSinSeleccion, cancellationToken);
        }

        var camiones = new List<clsCamionPlaneado>(recomendacion.Vehiculos.Count);
        foreach (var v in recomendacion.Vehiculos)
        {
            if (v.CodigoVehiculo.Length > clsSolicitudCosteo.LargoCatalogo || v.CodigoCarroceria.Length > clsSolicitudCosteo.LargoCatalogo
                || v.PesoTotalKg <= 0m || v.PesoTotalKg > clsSolicitudCosteo.MaximoPeso)
            {
                _logger.LogWarning("El cubicador devolvió un vehículo fuera de los límites de la captura en la ruta {RutaId}: no se guarda la captura.", rutaId);
                return new clsErrorDominio(clsCodigosError.CubicadorRespuestaInvalida, clsMensajesCubicador.RespuestaInvalida,
                    "vehículo con un código demasiado largo o un peso fuera de rango");
            }

            camiones.Add(new clsCamionPlaneado(
                v.CodigoVehiculo, v.CodigoCarroceria, Math.Round(v.PesoTotalKg, clsSolicitudCosteo.Decimales), v.IdRecomendacionVehiculo,
                v.VolumenTotalM3, v.PorcentajeLlenadoVolumen, v.PorcentajeLlenadoPeso));
        }

        return clsResultado<clsPlanRuta>.Exito(new clsPlanRuta(camiones, recomendacion.ACubicacionRuta(), AvisoAjustado(ruta, camiones)));
    }

    /// <summary>
    /// <c>VEHICULO_AJUSTADO_POR_CUBICADOR</c> si había un vehículo elegido y algún camión del cubicador lleva otro tipo de
    /// vehículo (o, si se eligió la carrocería, otra carrocería); <c>null</c> si no.
    /// </summary>
    private static IReadOnlyList<clsAdvertenciaCubicador>? AvisoAjustado(clsLineaSolicitudCosteo ruta, IReadOnlyList<clsCamionPlaneado> camiones)
    {
        if (VehiculoElegido(ruta) is not { } elegido)
        {
            return null;
        }

        static bool Distinto(string? deseado, string? final) =>
            deseado is not null && (final is null || !clsVehiculoLineaSolicitud.MismoCodigo(deseado, final));

        return camiones.Any(c => Distinto(elegido.TipoVehiculo, c.TipoVehiculo) || Distinto(elegido.Carroceria, c.Carroceria))
            ? [clsAvisosVehiculo.Ajustado(elegido, [.. camiones.Select(c => new clsVehiculoAviso(c.TipoVehiculo, c.Carroceria))])]
            : null;
    }
}

/// <summary>Mensajes de los errores del cubicador que arma Aplicación (los del adaptador están en Infraestructura).</summary>
public static class clsMensajesCubicador
{
    public const string RespuestaInvalida =
        "El cubicador respondió algo incompleto o fuera del contrato: no se guardó nada. Intenta de nuevo o avisa a soporte.";
}
