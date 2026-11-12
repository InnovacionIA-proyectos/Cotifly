using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Caso de uso de seguimiento (<c>POST /web/cotizaciones/{folio}/seguimiento</c>): marca la
/// cotización del comercial como ganada o perdida con el motivo de rechazo y la fecha (<c>marcadoAt</c>). Las del flujo de costeo de
/// Nexo marcan el resultado con <c>POST .../resultado</c>.
/// </summary>
public interface IClsSeguimientoService
{
    /// <summary>
    /// <c>COTIZACION_NO_ENCONTRADA</c> si no existe o no es del comercial (el endpoint responde 404);
    /// <see cref="clsSeguimientoService.CodigoSeguimientoInvalido"/> si el motivo supera 2000 caracteres (400).
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(Guid usuarioId, string folio, bool? ganada, string? motivoRechazo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="MarcarAsync(Guid, string, bool?, string?, CancellationToken)"/> con el <paramref name="comentario"/> del motivo:
    /// en una perdida el motivo es de la lista (<see cref="IClsCatalogoMotivosPerdida"/>) y <c>Otro</c> exige el
    /// comentario (<c>MOTIVO_NO_PERMITIDO</c>, 400).
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(
        Guid usuarioId, string folio, bool? ganada, string? motivoRechazo, string? comentario, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsSeguimientoService"/>. Guarda <c>ganada</c> tal como llega
/// (un <c>ganada = null</c> deja la cotización sin resultado) y el motivo según la regla que aplica el dominio
/// (<see cref="clsCotizacionSolicitud.MarcarSeguimiento"/>): ganada → <c>"No aplica"</c>, perdida → el motivo, sin resultado → vacío.
/// </summary>
/// <remarks>
/// Una perdida exige un motivo de la lista configurada y, con <c>Otro</c>, el comentario (se guarda
/// <c>"Otro: comentario"</c>: estas cotizaciones nunca van a Nexo, así que el comentario no se pierde). Siguen marcándose aquí las del chat,
/// las <c>descartada</c> y las de <c>costeo_vencido</c>. Los resultados ya guardados (motivo libre o sin motivo) no se revalidan.
/// </remarks>
public sealed class clsSeguimientoService(
    IClsCotizacionRepository cotizaciones,
    IClsUnitOfWork unidadTrabajo,
    IClsReloj reloj,
    IClsCatalogoMotivosPerdida? motivosPerdida = null) : IClsSeguimientoService
{
    public const string CodigoSeguimientoInvalido = "SEGUIMIENTO_INVALIDO";
    public const int MaxMotivoRechazo = 2000;

    /// <summary>Largo máximo del comentario del motivo (el mismo de <c>/resultado</c>, límite de Nexo).</summary>
    public const int MaxComentario = 500;

    private readonly IClsCatalogoMotivosPerdida _motivosPerdida = motivosPerdida ?? clsCatalogoMotivosPerdida.PorDefecto;

    public Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(Guid usuarioId, string folio, bool? ganada, string? motivoRechazo, CancellationToken cancellationToken = default) =>
        MarcarAsync(usuarioId, folio, ganada, motivoRechazo, comentario: null, cancellationToken);

    public async Task<clsResultado<clsCotizacionSolicitud>> MarcarAsync(
        Guid usuarioId, string folio, bool? ganada, string? motivoRechazo, string? comentario, CancellationToken cancellationToken = default)
    {
        if (motivoRechazo is { Length: > MaxMotivoRechazo } || comentario is { Length: > MaxComentario })
        {
            return new clsErrorDominio(CodigoSeguimientoInvalido,
                $"El motivo de rechazo admite máximo {MaxMotivoRechazo} caracteres y el comentario {MaxComentario}.");
        }

        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (!cotizacion.ResultadoSoloLocal)
        {
            // En el flujo de costeo de Nexo: marcarla solo en Cotifly desincronizaría el resultado. Las del chat, las
            // descartadas y las de costeo vencido sí se marcan aquí (Nexo no les acepta resultado).
            // El detalle dice al frontend a dónde ir, sin que tenga que adivinarlo.
            var ruta = $"/web/cotizaciones/{cotizacion.Folio}/resultado";
            return new clsErrorDominio(clsCodigosError.UsarResultadoNexo,
                $"La cotización {cotizacion.Folio} está en Nexo: marca el resultado con POST {ruta} (con If-Match).")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["metodo"] = "POST",
                    ["endpoint"] = ruta,
                    ["requiere_if_match"] = true,
                },
            };
        }

        if (ganada == false)
        {
            var validado = _motivosPerdida.Validar(motivoRechazo, comentario);
            if (validado.EsFallo)
            {
                return validado.Error;
            }

            motivoRechazo = validado.Valor.ParaCotifly;
        }

        cotizacion.MarcarSeguimiento(ganada, motivoRechazo, reloj.AhoraUtc);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        return cotizacion;
    }
}
