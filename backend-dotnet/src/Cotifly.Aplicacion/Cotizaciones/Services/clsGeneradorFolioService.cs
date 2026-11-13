using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Genera el folio de una cotización y la crea en la misma transacción (ADR-011). El folio lo decide
/// siempre el servidor, nunca el LLM: <c>PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]</c> (<see cref="clsFolio"/>).
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
    /// Folio de una cotización nueva (versión 1) del comercial: el NIT (solo dígitos) si viene, sus iniciales
    /// (<see cref="clsInicialesComercial"/> sobre <c>UsuariosWeb.nombreCompleto</c> o <c>nombreUsuario</c>), la fecha de hoy en la
    /// zona de negocio y el siguiente consecutivo de esas iniciales en el día. <c>FOLIO_CONSECUTIVO_AGOTADO</c> después del 999;
    /// <c>ERROR_INTERNO</c> si el comercial no existe en <c>UsuariosWeb</c>.
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
/// <item><b>Candado por iniciales y día</b> (<c>cotifly:folio:DDMMAAAA:INICIALES</c>): serializa solo a quienes comparten la
/// secuencia; comerciales con iniciales distintas generan en paralelo.</item>
/// <item><b>Consecutivo por iniciales</b>: el mayor <c>NNN</c> del día entre todos los folios base con esas iniciales (con y sin
/// NIT, de cualquier comercial) + 1. En la práctica es el consecutivo diario del comercial; si dos comerciales tienen las mismas
/// iniciales, comparten la secuencia (<c>001</c> uno, <c>002</c> el otro). Como el conteo incluye toda fila con esa forma, el
/// número siguiente nunca está tomado y no hace falta buscar huecos: <c>uq_cotizaciones_folio</c> queda como última defensa.</item>
/// <item><b>Versiones</b> (<c>cotifly:folio-version:{base}</c>): la familia se serializa por su folio base.</item>
/// </list>
/// </summary>
public sealed class clsGeneradorFolioService(
    IClsCandadoTransaccional candado,
    IClsCotizacionRepository cotizaciones,
    IClsUsuarioWebRepository usuarios,
    IClsCalendarioNegocio calendario) : IClsGeneradorFolioService
{
    public const string PrefijoCandadoDia = "cotifly:folio:";
    public const string PrefijoCandadoVersion = "cotifly:folio-version:";

    /// <summary>Recurso del candado del consecutivo: <c>cotifly:folio:DDMMAAAA:INICIALES</c>.</summary>
    public static string RecursoCandadoDia(string segmentoFecha, string iniciales) => $"{PrefijoCandadoDia}{segmentoFecha}:{iniciales}";

    public async Task<clsResultado<TValor>> CrearConFolioNuevoAsync<TValor>(
        Guid usuarioId,
        string? nitCliente,
        Func<clsFolio, CancellationToken, Task<clsResultado<TValor>>> crear,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(crear);
        var comercial = usuarioId == Guid.Empty ? null : await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (comercial is null)
        {
            return SinComercial();
        }

        var iniciales = clsInicialesComercial.Calcular(comercial.NombreCompleto, comercial.NombreUsuario);
        var fecha = calendario.Hoy;
        var segmento = clsFolio.SegmentoFecha(fecha);
        return await candado.EjecutarAsync(RecursoCandadoDia(segmento, iniciales), async ct =>
        {
            var ultimo = await cotizaciones.ObtenerUltimoConsecutivoDelDiaAsync(iniciales, segmento, ct);
            var folio = clsFolio.Crear(fecha, iniciales, ultimo + 1, nitCliente);
            return folio.EsFallo ? folio.Error : await crear(folio.Valor, ct);
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
