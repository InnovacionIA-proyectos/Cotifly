using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-036 (#205, #206; script <c>db/scripts/14_cotizacion_finalizada.sql</c>): la cotización FINALIZADA. El comercial acepta el precio con
/// <c>POST /web/cotizaciones/{folio}/finalizar</c>; desde ahí el precio queda congelado. Va en columnas aparte de
/// <see cref="EstadoCosteo"/> a propósito: el polling deriva ese estado de Nexo en cada vuelta y la finalización no debe depender de él (una
/// cotización finalizada sigue avanzando, p. ej. de <c>autocosteo</c> a <c>entregada</c>).
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary>Cuándo se finalizó; <c>null</c> = sin finalizar (las anteriores al script 14 no tienen backfill).</summary>
    public DateTimeOffset? FinalizadaAt { get; private set; }

    /// <summary>Quién la finalizó (<c>UsuariosWeb.id</c>); <c>null</c> sin finalizar.</summary>
    public Guid? FinalizadaPor { get; private set; }

    /// <summary>¿El comercial ya aceptó el precio? (<see cref="FinalizadaAt"/> con valor.)</summary>
    public bool Finalizada => FinalizadaAt is not null;

    /// <summary>
    /// ¿El estado admite finalizar? Solo con el costeo de Nexo listo: <c>autocosteo</c> o <c>entregada</c>. Que todas las rutas tengan
    /// precio ya lo garantiza ese resumen (<see cref="clsResumenEstadoCosteo"/>, las rutas <c>rechazada</c> no cuentan): no se repite aquí.
    /// </summary>
    public bool AdmiteFinalizar => EstadoCosteo is Cotizaciones.EstadoCosteo.Autocosteo or Cotizaciones.EstadoCosteo.Entregada;

    /// <summary>
    /// Finaliza la cotización por <paramref name="usuarioId"/> en <paramref name="ahora"/>. Devuelve <c>true</c> si la finalizó,
    /// <c>false</c> si ya estaba finalizada (idempotente: no cambia nada, conserva la fecha y el comercial de la primera vez) o
    /// <c>COTIZACION_NO_FINALIZABLE</c> si el estado no lo admite (<see cref="AdmiteFinalizar"/>), con <c>detalle.estado_costeo</c>.
    /// </summary>
    public clsResultado<bool> Finalizar(Guid usuarioId, DateTimeOffset ahora)
    {
        if (usuarioId == Guid.Empty)
        {
            throw new ArgumentException("La finalización exige el comercial que la hace.", nameof(usuarioId));
        }

        if (Finalizada)
        {
            return false;
        }

        if (!AdmiteFinalizar)
        {
            var estado = EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null;
            return new clsErrorDominio(clsCodigosError.CotizacionNoFinalizable,
                $"La cotización {Folio} no se puede finalizar en su estado actual ({estado ?? "sin costeo en Nexo"}): solo se finaliza con el "
                + "costeo de Nexo listo (autocosteo o entregada).")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["estado_costeo"] = estado },
            };
        }

        FinalizadaAt = ahora;
        FinalizadaPor = usuarioId;
        return true;
    }
}
