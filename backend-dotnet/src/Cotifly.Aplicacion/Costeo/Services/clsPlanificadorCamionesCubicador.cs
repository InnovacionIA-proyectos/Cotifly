using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Camiones de una ruta según el cubicador (HU-021.5, #133; <c>CAMBIOS_BACKEND</c> §6; ADR-019, decisión 7): manda la ruta a
/// <c>POST /recomendaciones</c> (<see cref="IClsCubicador.RecomendarAsync"/>) y crea <b>un camión por fila</b> de la respuesta, en su orden,
/// con el vehículo, la carrocería, el peso total del vehículo (C2) y el id de la recomendación (C3).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Sin selección automática</b> (C6): el camión queda sin vehículo ni carrocería; se envía igual a Nexo y lo resuelve la Mesa
/// de Costeo.</item>
/// <item><b>Condicionada</b> (C6): no se parte ni se bloquea nada; los camiones se crean como vienen y la solicitud se envía completa a la
/// Mesa. Solo queda una línea informativa en el log.</item>
/// <item><b>Respaldo sin llamar al cubicador</b> (<see cref="clsPlanificadorCamionSinVehiculo"/>, un camión sin vehículo con el peso total
/// de la ruta): sin cubicador configurado (hoy, C1: warning una vez por captura) o con una ruta sin mercancías o con alguna mercancía sin
/// <c>id_tipo_mercancia</c> (rutas migradas o re-solicitudes que no aplican las reglas de captura nueva).</item>
/// <item><b>Falla del cubicador</b> (caído, tiempo agotado, rechazo, respuesta inválida o presupuesto de la captura agotado): con
/// <see cref="IClsCubicador.RespaldoSiFalla"/> la ruta va al respaldo, con un warning de código estable <see cref="EventoRespaldo"/> y el
/// contador <see cref="MetricaRespaldos"/> (etiqueta <c>motivo</c>); sin él, <c>CUBICADOR_NO_DISPONIBLE</c> (sin el detalle interno, que
/// queda en el log) y la captura no guarda nada. Solo una <b>caída</b> (tiempo agotado, 5xx, red) o el presupuesto agotado mandan al
/// respaldo las rutas siguientes de la misma captura sin volver a llamar (el planificador es por petición); un rechazo o una respuesta
/// inválida afectan solo a su ruta.</item>
/// <item><b>Presupuesto</b> (<see cref="IClsCubicador.PresupuestoCaptura"/>): una licitación hace un POST por fila; pasado el presupuesto
/// desde la primera llamada, las filas restantes no llaman al cubicador.</item>
/// <item><b>Respuesta inválida</b>: sin filas, con un peso fuera de rango, con un código de más de
/// <see cref="clsSolicitudCosteo.LargoCatalogo"/> caracteres o con varias filas y alguna sin peso (sin C2 no hay qué mandarle a Nexo
/// en cada línea). Con una sola fila sin peso se usa el peso total de la ruta.</item>
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

    private readonly ILogger<clsPlanificadorCamionesCubicador> _logger;
    private readonly IClsCubicador? _cubicador;
    private readonly Counter<long>? _respaldos;

    /// <summary>¿Ya cayó el cubicador (o se agotó el presupuesto) en esta petición? Las rutas siguientes van directo al respaldo.</summary>
    private bool _caido;

    private bool _avisoSinConfigurar;
    private long? _inicio;

    public clsPlanificadorCamionesCubicador(
        ILogger<clsPlanificadorCamionesCubicador> logger, IClsCubicador? cubicador = null, IMeterFactory? medidores = null)
    {
        _logger = logger;
        _cubicador = cubicador;
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
        if (posiciones.Count == 0 || posiciones.Any(p => p.IdTipoMercancia is null || p.IdTipoMercancia == Guid.Empty))
        {
            _logger.LogInformation(
                "La ruta {RutaId} no trae mercancías con tipo del cubicador (ruta migrada o re-solicitud): queda con un camión sin vehículo (respaldo).", rutaId);
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
        var recomendacion = await cubicador.RecomendarAsync(rutaId, [.. posiciones.Select(clsMercanciaCubicador.Desde)], cancellationToken);
        if (recomendacion.EsFallo)
        {
            var caida = clsErroresCubicador.EsCaida(recomendacion.Error);
            return Falla(cubicador, rutaId, ruta, caida ? "caida" : "rechazo", recomendacion.Error.Detalle ?? recomendacion.Error.Codigo, caida);
        }

        var (camiones, motivo) = Camiones(rutaId, ruta, recomendacion.Valor);
        return camiones is not null
            ? clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito(camiones)
            : Falla(cubicador, rutaId, ruta, "respuesta_invalida", $"respuesta inválida: {motivo}", caida: false);
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

    /// <summary>Un camión por fila de la recomendación (en su orden), o <c>null</c> y el motivo si la respuesta no se puede usar.</summary>
    private (IReadOnlyList<clsCamionPlaneado>? Camiones, string? Motivo) Camiones(
        Guid rutaId, clsLineaSolicitudCosteo ruta, clsRecomendacionCubicador recomendacion)
    {
        var filas = recomendacion.Vehiculos;
        if (filas.Count == 0)
        {
            return (null, "sin vehículos");
        }

        var camiones = new List<clsCamionPlaneado>(filas.Count);
        foreach (var fila in filas)
        {
            var peso = fila.PesoTotalKg ?? (filas.Count == 1 ? clsPlanificadorCamionSinVehiculo.PesoTotal(ruta) : null);
            if (fila.PesoTotalKg is null && filas.Count > 1)
            {
                return (null, "varios vehículos y alguno sin peso total (C2)");
            }

            if (peso is { } p && (p <= 0m || p > clsSolicitudCosteo.MaximoPeso))
            {
                return (null, "peso del vehículo fuera de rango");
            }

            var tipo = fila.SinSeleccion ? null : Codigo(fila.TipoVehiculo);
            var carroceria = fila.SinSeleccion ? null : Codigo(fila.Carroceria);
            if (tipo?.Length > clsSolicitudCosteo.LargoCatalogo || carroceria?.Length > clsSolicitudCosteo.LargoCatalogo)
            {
                return (null, "código de vehículo o carrocería demasiado largo");
            }

            if (fila.SinSeleccion)
            {
                _logger.LogInformation("El cubicador no seleccionó vehículo en la ruta {RutaId} ({Motivo}): el camión queda sin vehículo para la Mesa de Costeo de Nexo.",
                    rutaId, fila.Motivo ?? "sin motivo");
            }
            else if (fila.Condicionada)
            {
                _logger.LogInformation("Mercancía condicionada en la ruta {RutaId} ({Motivo}): la solicitud se envía completa a la Mesa de Costeo de Nexo.",
                    rutaId, fila.Motivo ?? "sin motivo");
            }

            camiones.Add(new clsCamionPlaneado(
                tipo, carroceria, peso is { } total ? Math.Round(total, clsSolicitudCosteo.Decimales) : null, fila.IdRecomendacionVehiculo));
        }

        return (camiones, null);
    }

    private static string? Codigo(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
