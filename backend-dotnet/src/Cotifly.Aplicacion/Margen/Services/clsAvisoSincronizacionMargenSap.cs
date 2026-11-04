using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// HU-042.2 (#243, decisión del 2026-11-04): ¿la copia del margen de SAP TM está vieja para una cotización con rutas cuyo margen sugerido
/// viene de SAP? Lo usa la vista del costeo (<c>aviso_margen</c>, junto al aviso del margen anómalo). Se evalúa al leer el costeo (la ruta
/// no guarda cuándo se resolvió su margen): el aviso dice que la copia de la que salió el sugerido no se ha vuelto a sincronizar.
/// </summary>
public interface IClsAvisoSincronizacionMargenSap
{
    /// <summary>
    /// La sincronización vieja si alguna ruta tiene un sugerido de SAP TM (aplicado o anómalo) y la última corrida <c>ok</c> supera el
    /// umbral; <c>null</c> si no, si nunca hubo una corrida <c>ok</c> o si la copia no se pudo leer (el aviso no bloquea el costeo).
    /// </summary>
    Task<clsSincronizacionSapVieja?> ConsultarAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsAvisoSincronizacionMargenSap"/>.</summary>
public sealed class clsAvisoSincronizacionMargenSap(IClsFuenteMargen fuente, IOptions<clsOpcionesMargen> opciones, IClsReloj reloj)
    : IClsAvisoSincronizacionMargenSap
{
    public async Task<clsSincronizacionSapVieja?> ConsultarAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (!cotizacion.RutasOrdenadas.Any(TieneSugeridoDeSap))
        {
            return null;
        }

        var horas = opciones.Value.AvisoSincronizacionHoras;
        return await fuente.UltimaSincronizacionAsync(cancellationToken) is { } ultima && reloj.AhoraUtc - ultima > TimeSpan.FromHours(horas)
            ? new clsSincronizacionSapVieja(ultima, horas)
            : null;
    }

    /// <summary>El sugerido de la ruta salió de SAP TM: aplicado (<c>sap_tm</c>) o anómalo (se aplicó la política, #139).</summary>
    public static bool TieneSugeridoDeSap(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return ruta.MargenSapAnomalo || ruta.OrigenMargenSugerido == OrigenMargen.SapTm;
    }

    /// <summary>Consulta sin fallar: sin servicio (pruebas) o ante cualquier error, <c>null</c>.</summary>
    public static async Task<clsSincronizacionSapVieja?> ConsultarSinFallarAsync(
        IClsAvisoSincronizacionMargenSap? servicio, clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (servicio is null)
        {
            return null;
        }

        try
        {
            return await servicio.ConsultarAsync(cotizacion, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
