using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Camiones de una ruta según el cubicador (HU-021.5, #133; <c>CAMBIOS_BACKEND</c> §6; ADR-019, decisión 7): manda la ruta a
/// <c>POST /recomendaciones</c> (<see cref="IClsCubicador.RecomendarAsync"/>) y crea <b>un camión por fila</b> de la respuesta con el
/// vehículo, la carrocería, el peso total del vehículo (C2) y el id de la recomendación (C3).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Sin selección automática</b> (C6): el camión queda sin vehículo ni carrocería; se envía igual a Nexo y lo resuelve la Mesa
/// de Costeo.</item>
/// <item><b>Condicionada</b> (C6): no se parte ni se bloquea nada; los camiones se crean como vienen y la solicitud se envía completa a la
/// Mesa. Solo queda una línea informativa en el log.</item>
/// <item><b>Respaldo</b> (<see cref="clsPlanificadorCamionSinVehiculo"/>, un camión sin vehículo con el peso total de la ruta): sin
/// cubicador configurado (hoy, C1), sin mercancías en la ruta (no hay qué cubicar) o, con <see cref="IClsCubicador.RespaldoSiFalla"/>, si
/// el cubicador falla (caído, sin respuesta a tiempo, respuesta inválida). La falla deja un warning y las demás rutas de la misma
/// captura van directo al respaldo (el planificador es por petición), para no esperar el tiempo límite en cada una. Con
/// <c>Cubicador:RespaldoSiFalla = false</c> la falla es <c>CUBICADOR_NO_DISPONIBLE</c> y la captura no guarda nada (regla de la issue).</item>
/// <item><b>Respuesta inválida</b>: sin filas, con un peso fuera de rango, con un código de más de
/// <see cref="clsSolicitudCosteo.LargoCatalogo"/> caracteres o con varias filas y alguna sin peso (sin C2 no hay qué mandarle a Nexo
/// en cada línea). Con una sola fila sin peso se usa el peso total de la ruta.</item>
/// </list>
/// </remarks>
public sealed class clsPlanificadorCamionesCubicador(ILogger<clsPlanificadorCamionesCubicador> logger, IClsCubicador? cubicador = null)
    : IClsPlanificadorCamiones
{
    public const string MensajeNoDisponible =
        "No se pudo calcular los vehículos de la ruta: el cubicador no está disponible. Intenta de nuevo más tarde.";

    /// <summary>¿Ya falló el cubicador en esta petición? Las rutas siguientes van directo al respaldo.</summary>
    private bool _caido;

    public async Task<clsResultado<IReadOnlyList<clsCamionPlaneado>>> PlanificarAsync(
        Guid usuarioId, Guid rutaId, clsLineaSolicitudCosteo ruta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        if (cubicador is not { EstaConfigurado: true })
        {
            logger.LogInformation("Cubicador sin configurar (Cubicador:UrlBase): la ruta {RutaId} queda con un camión sin vehículo (respaldo).", rutaId);
            return Respaldo(ruta);
        }

        var mercancias = ruta.Posiciones.Where(p => p is not null).Select(clsMercanciaCubicador.Desde).ToList();
        if (mercancias.Count == 0)
        {
            logger.LogInformation("La ruta {RutaId} no trae mercancías para cubicar: queda con un camión sin vehículo (respaldo).", rutaId);
            return Respaldo(ruta);
        }

        if (_caido)
        {
            return Respaldo(ruta);
        }

        var recomendacion = await cubicador.RecomendarAsync(rutaId, mercancias, cancellationToken);
        var error = recomendacion.EsFallo ? recomendacion.Error : null;
        if (error is null)
        {
            var (camiones, motivo) = Camiones(rutaId, ruta, recomendacion.Valor);
            if (camiones is not null)
            {
                return clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito(camiones);
            }

            error = new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoDisponible, $"respuesta inválida: {motivo}");
        }

        if (!cubicador.RespaldoSiFalla)
        {
            logger.LogWarning("El cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Detalle}): no se guarda la captura.",
                rutaId, error.Detalle);
            return new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, MensajeNoDisponible, error.Detalle);
        }

        _caido = true;
        logger.LogWarning(
            "El cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Detalle}): la ruta y las siguientes de esta captura quedan con un camión sin vehículo (respaldo; lo resuelve la Mesa de Costeo de Nexo).",
            rutaId, error.Detalle);
        return Respaldo(ruta);
    }

    private static clsResultado<IReadOnlyList<clsCamionPlaneado>> Respaldo(clsLineaSolicitudCosteo ruta) =>
        clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito(clsPlanificadorCamionSinVehiculo.Camiones(ruta));

    /// <summary>Un camión por fila de la recomendación, o <c>null</c> y el motivo si la respuesta no se puede usar.</summary>
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
                logger.LogInformation("El cubicador no seleccionó vehículo en la ruta {RutaId} ({Motivo}): el camión queda sin vehículo para la Mesa de Costeo de Nexo.",
                    rutaId, fila.Motivo ?? "sin motivo");
            }
            else if (fila.Condicionada)
            {
                logger.LogInformation("Mercancía condicionada en la ruta {RutaId} ({Motivo}): la solicitud se envía completa a la Mesa de Costeo de Nexo.",
                    rutaId, fila.Motivo ?? "sin motivo");
            }

            camiones.Add(new clsCamionPlaneado(
                tipo, carroceria, peso is { } total ? Math.Round(total, clsSolicitudCosteo.Decimales) : null, fila.IdRecomendacionVehiculo));
        }

        return (camiones, null);
    }

    private static string? Codigo(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
