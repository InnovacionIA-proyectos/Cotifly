using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Genera el folio de una cotización y la crea en la misma transacción (ADR-011, aprobado el 2026-09-25). El folio lo decide
/// siempre el servidor, nunca el LLM: <c>PCN-[{nitCliente}-]DDMMAAAA-{NNN}[-vN]</c> (<see cref="clsFolio"/>).
/// </summary>
/// <remarks>
/// La creación se pasa como función (<c>crear</c>): recibe el folio, agrega la cotización con <c>version = folio.Version</c> y
/// guarda con <see cref="IClsUnitOfWork"/>. Todo corre dentro de <see cref="IClsCandadoTransaccional"/>, así el consecutivo
/// calculado queda reservado hasta confirmar y dos peticiones simultáneas nunca obtienen el mismo folio. Si <c>crear</c>
/// devuelve un fallo, la transacción se deshace y el número queda libre.
/// </remarks>
public interface IClsGeneradorFolioService
{
    /// <summary>
    /// Folio de una cotización nueva (versión 1) del comercial: la fecha de hoy en la zona de negocio, el NIT (solo dígitos) si
    /// viene y el siguiente consecutivo del comercial en el día. <c>FOLIO_CONSECUTIVO_AGOTADO</c> después del 999.
    /// </summary>
    Task<clsResultado<TValor>> CrearConFolioNuevoAsync<TValor>(
        Guid usuarioId,
        string? nitCliente,
        Func<clsFolio, CancellationToken, Task<clsResultado<TValor>>> crear,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Folio de una versión nueva de una cotización del comercial: folio base de la v1 con <c>-vN</c>, N = versión máxima de la
    /// familia + 1. <c>COTIZACION_NO_ENCONTRADA</c> si la cotización no existe o no es suya.
    /// </summary>
    Task<clsResultado<TValor>> CrearNuevaVersionAsync<TValor>(
        Guid usuarioId,
        Guid cotizacionOrigenId,
        Func<clsFolio, CancellationToken, Task<clsResultado<TValor>>> crear,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsGeneradorFolioService"/>.
/// <list type="bullet">
/// <item><b>Candado del día</b> (<c>cotifly:folio:DDMMAAAA</c>): uno para todos los comerciales, no por comercial. El formato no
/// lleva un segmento del comercial, así que dos comerciales pueden llegar al mismo folio (sin NIT, o con el mismo NIT el mismo
/// día); con un candado global el segundo salta los números ya tomados sin colisionar nunca. Con el volumen esperado (decenas
/// de cotizaciones al día) la espera es despreciable.</item>
/// <item><b>Consecutivo por comercial</b>: el mayor <c>NNN</c> de sus folios v1 del día (con y sin NIT) + 1; si ese folio ya lo
/// tiene otra cotización (de otro comercial), se toma el siguiente libre.</item>
/// <item><b>Versiones</b> (<c>cotifly:folio-version:{base}</c>): la familia se serializa por su folio base.</item>
/// </list>
/// </summary>
public sealed class clsGeneradorFolioService(
    IClsCandadoTransaccional candado,
    IClsCotizacionRepository cotizaciones,
    IClsCalendarioNegocio calendario) : IClsGeneradorFolioService
{
    public const string PrefijoCandadoDia = "cotifly:folio:";
    public const string PrefijoCandadoVersion = "cotifly:folio-version:";

    public Task<clsResultado<TValor>> CrearConFolioNuevoAsync<TValor>(
        Guid usuarioId,
        string? nitCliente,
        Func<clsFolio, CancellationToken, Task<clsResultado<TValor>>> crear,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crear);
        if (usuarioId == Guid.Empty)
        {
            return Task.FromResult<clsResultado<TValor>>(SinComercial());
        }

        var fecha = calendario.Hoy;
        var segmento = clsFolio.SegmentoFecha(fecha);
        return candado.EjecutarAsync(PrefijoCandadoDia + segmento, async ct =>
        {
            var ultimo = await cotizaciones.ObtenerUltimoConsecutivoDelDiaAsync(usuarioId, segmento, ct);
            for (var consecutivo = ultimo + 1; ; consecutivo++)
            {
                var folio = clsFolio.Crear(fecha, consecutivo, nitCliente);
                if (folio.EsFallo)
                {
                    return folio.Error;
                }

                if (!await cotizaciones.ExisteFolioAsync(folio.Valor.Valor, ct))
                {
                    return await crear(folio.Valor, ct);
                }
            }
        }, cancellationToken);
    }

    public async Task<clsResultado<TValor>> CrearNuevaVersionAsync<TValor>(
        Guid usuarioId,
        Guid cotizacionOrigenId,
        Func<clsFolio, CancellationToken, Task<clsResultado<TValor>>> crear,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crear);
        if (usuarioId == Guid.Empty)
        {
            return SinComercial();
        }

        var origen = cotizacionOrigenId == Guid.Empty
            ? null
            : await cotizaciones.ObtenerPorIdDelUsuarioAsync(cotizacionOrigenId, usuarioId, cancellationToken);
        if (origen is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var folioOrigen = clsFolio.Desde(origen.Folio);
        if (folioOrigen.EsFallo)
        {
            return folioOrigen.Error;
        }

        var folioBase = folioOrigen.Valor;
        var versionOrigen = origen.Version;
        return await candado.EjecutarAsync(PrefijoCandadoVersion + folioBase.Base, async ct =>
        {
            var maxima = await cotizaciones.ObtenerVersionMaximaAsync(folioBase.Base, ct);
            var nuevo = folioBase.ConVersion(Math.Max(maxima, versionOrigen) + 1);
            return nuevo.EsFallo ? nuevo.Error : await crear(nuevo.Valor, ct);
        }, cancellationToken);
    }

    private static clsErrorDominio SinComercial() =>
        new(clsCodigosError.ErrorInterno, "No se puede generar el folio sin el comercial autenticado.");
}
