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
/// Camiones de una ruta según el cubicador (HU-021.5, #133, con el contrato de HU-023.1, #156; ADR-019, decisión 7): manda la ruta a
/// <c>POST /recomendaciones</c> (<see cref="IClsCubicador.RecomendarAsync"/>: el id de la ruta, la clase de viaje como preferencia, el
/// <c>idUsuarioExterno</c> del comercial y cada mercancía con su <c>codigoMercancia</c>) y crea <b>un camión por vehículo</b> de la
/// respuesta, en su orden, con el código de vehículo y de carrocería, el peso que lleva, el volumen, los dos porcentajes de llenado y el
/// <c>idRecomendacionVehiculo</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Sin selección automática</b>: un camión sin vehículo ni carrocería con el peso total de la ruta; se envía igual a Nexo y lo
/// resuelve la Mesa de Costeo.</item>
/// <item><b>Condicionada</b>: no se parte ni se bloquea nada; los camiones se crean como vienen. Una línea informativa en el log.</item>
/// <item><b>Respaldo sin llamar al cubicador</b> (<see cref="clsPlanificadorCamionSinVehiculo"/>): sin cubicador configurado (warning
/// una vez por captura) o con una ruta sin mercancías o con alguna mercancía sin <c>codigoMercancia</c> (rutas migradas, agente de chat).
/// Durante la transición, una mercancía con el <c>id_tipo_mercancia</c> anterior se traduce al código con el catálogo.</item>
/// <item><b>Rechazo de la petición o respuesta inválida</b> (<c>MERCANCIAS_REQUERIDAS</c>, <c>MERCANCIA_DESCONOCIDA</c>,
/// <c>MERCANCIA_NO_CUBICABLE</c>, <c>DIMENSIONES_INVALIDAS</c>, <c>CUBICADOR_RESPUESTA_INVALIDA</c>): el error vuelve tal cual y la
/// captura no guarda nada. Nunca van al respaldo: esconderían un dato malo (HU-023.1, contra el fallo silencioso).</item>
/// <item><b>Caída del cubicador</b> (tiempo agotado, 5xx, red, llave rechazada, otro rechazo no previsto o presupuesto de la captura
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
        clsCodigosError.CubicadorRespuestaInvalida,
    };

    private readonly ILogger<clsPlanificadorCamionesCubicador> _logger;
    private readonly IClsCubicador? _cubicador;
    private readonly IClsUsuarioWebRepository? _usuarios;
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
        IClsUsuarioWebRepository? usuarios = null)
    {
        _logger = logger;
        _cubicador = cubicador;
        _usuarios = usuarios;
        _respaldos = medidores?.Create(NombreMedidor).CreateCounter<long>(MetricaRespaldos,
            description: "Rutas que quedaron con un camión sin vehículo porque el cubicador falló (etiqueta motivo).");
    }

    public async Task<clsResultado<IReadOnlyList<clsCamionPlaneado>>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        if (_cubicador is not { EstaConfigurado: true } cubicador)
        {
            if (!_avisoSinConfigurar)
            {
                _avisoSinConfigurar = true;
                _logger.LogWarning(EventoSinConfigurar,
                    "CUBICADOR_SIN_CONFIGURAR: Cubicador:UrlBase vacía; las rutas de esta captura quedan con un camión sin vehículo (respaldo).");
            }

            return Respaldo(ruta);
        }

        var posiciones = ruta.Posiciones.Where(p => p is not null).ToList();
        if (!_caido && posiciones.Any(p => string.IsNullOrWhiteSpace(p.CodigoMercancia) && p.IdTipoMercancia is { } id && id != Guid.Empty))
        {
            var traducidas = await TraducirIdsAsync(cubicador, posiciones, cancellationToken);
            if (traducidas.EsFallo)
            {
                var caidaCatalogo = clsErroresCubicador.EsCaida(traducidas.Error);
                return Falla(cubicador, rutaId, ruta, caidaCatalogo ? "caida" : "rechazo", traducidas.Error.Detalle ?? traducidas.Error.Codigo, caidaCatalogo);
            }

            posiciones = [.. traducidas.Valor];
        }

        var mercancias = posiciones.Select(clsMercanciaCubicador.Desde).ToList();
        if (mercancias.Count == 0 || mercancias.Any(m => m is null))
        {
            _logger.LogInformation(
                "La ruta {RutaId} no trae mercancías con codigoMercancia (ruta migrada, re-solicitud o chat): queda con un camión sin vehículo (respaldo).", rutaId);
            return Respaldo(ruta);
        }

        if (_caido)
        {
            return Respaldo(ruta);
        }

        if (_inicio is { } inicio && Stopwatch.GetElapsedTime(inicio) > cubicador.PresupuestoCaptura)
        {
            return Falla(cubicador, rutaId, ruta, "presupuesto", "presupuesto de tiempo de la captura agotado", caida: true);
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
            if (CodigosSinRespaldo.Contains(recomendacion.Error.Codigo))
            {
                _logger.LogWarning("El cubicador no aceptó la ruta {RutaId} ({Codigo}: {Detalle}): no se guarda la captura.",
                    rutaId, recomendacion.Error.Codigo, recomendacion.Error.Detalle);
                // Solo el código y el mensaje: el detalle interno (qué campo faltó, estado HTTP) queda en el log.
                return new clsErrorDominio(recomendacion.Error.Codigo, recomendacion.Error.Mensaje);
            }

            var caida = clsErroresCubicador.EsCaida(recomendacion.Error);
            return Falla(cubicador, rutaId, ruta, caida ? "caida" : "rechazo", recomendacion.Error.Detalle ?? recomendacion.Error.Codigo, caida);
        }

        return Camiones(rutaId, ruta, recomendacion.Valor);
    }

    /// <summary>
    /// Transición de HU-023: una mercancía con el <c>id_tipo_mercancia</c> anterior (uuid) y sin <c>codigoMercancia</c> se traduce al
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

    private clsResultado<IReadOnlyList<clsCamionPlaneado>> Falla(
        IClsCubicador cubicador, Guid rutaId, clsLineaSolicitudCosteo ruta, string motivo, string detalle, bool caida)
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
                "CUBICADOR_RESPALDO: el cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Motivo}: {Detalle}); esta ruta y las siguientes de la captura quedan con un camión sin vehículo (lo resuelve la Mesa de Costeo de Nexo).",
                rutaId, motivo, detalle);
        }
        else if (!caida)
        {
            _logger.LogWarning(EventoRespaldo,
                "CUBICADOR_RESPALDO: el cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Motivo}: {Detalle}); la ruta queda con un camión sin vehículo (lo resuelve la Mesa de Costeo de Nexo).",
                rutaId, motivo, detalle);
        }

        _respaldos?.Add(1, new KeyValuePair<string, object?>("motivo", motivo));
        return Respaldo(ruta);
    }

    private static clsResultado<IReadOnlyList<clsCamionPlaneado>> Respaldo(clsLineaSolicitudCosteo ruta) =>
        clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito(clsPlanificadorCamionSinVehiculo.Camiones(ruta));

    /// <summary>Un camión por vehículo recomendado (en su orden); "sin selección" es un camión sin vehículo con el peso de la ruta.</summary>
    private clsResultado<IReadOnlyList<clsCamionPlaneado>> Camiones(Guid rutaId, clsLineaSolicitudCosteo ruta, clsRecomendacionCubicador recomendacion)
    {
        if (recomendacion.Condicionada)
        {
            _logger.LogInformation("Ruta {RutaId} condicionada por el cubicador ({Mercancias} mercancía(s)): la solicitud se envía completa a la Mesa de Costeo de Nexo.",
                rutaId, recomendacion.MercanciasCondicionadas.Count);
        }

        if (!recomendacion.Asignado)
        {
            _logger.LogInformation("El cubicador no seleccionó vehículo en la ruta {RutaId} ({Motivo}): el camión queda sin vehículo para la Mesa de Costeo de Nexo.",
                rutaId, recomendacion.MotivoSinSeleccion ?? "sin motivo");
            return Respaldo(ruta);
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

        return clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito(camiones);
    }
}

/// <summary>Mensajes de los errores del cubicador que arma Aplicación (los del adaptador están en Infraestructura).</summary>
public static class clsMensajesCubicador
{
    public const string RespuestaInvalida =
        "El cubicador respondió algo incompleto o fuera del contrato: no se guardó nada. Intenta de nuevo o avisa a soporte.";
}
