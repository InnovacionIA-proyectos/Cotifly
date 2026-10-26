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
    /// ¿Se puede finalizar? Solo con el costeo de Nexo listo: <c>autocosteo</c> o <c>entregada</c> con su precio
    /// (<see cref="TieneCostoDisponible"/>, la misma propiedad que exige el PDF). Que todas las rutas tengan precio ya lo garantiza el
    /// resumen del estado (<see cref="clsResumenEstadoCosteo"/>, las rutas <c>rechazada</c> no cuentan): no se repite aquí. El precio de
    /// la cabecera solo falta en una <c>entregada</c> que Cotifly todavía no leyó (<see cref="EntregadaSinPrecio"/>, HU-013.14): finalizarla
    /// congelaría un precio vacío (#208), así que espera a la relectura.
    /// </summary>
    public bool AdmiteFinalizar => TieneCostoDisponible;

    /// <summary><c>detalle.motivo</c> de <c>COTIZACION_NO_FINALIZABLE</c> cuando Nexo agregó un conexo que Cotifly todavía no leyó.</summary>
    public const string CodigoConexosPendientesDeLeer = "conexos_pendientes_de_leer";

    /// <summary>Mensaje de <c>COTIZACION_NO_FINALIZABLE</c> con conexos de Nexo pendientes de leer (<see cref="ConexosPendientesDeLeer"/>).</summary>
    public const string MotivoNoFinalizableConexosPendientes =
        "Nexo agregó un servicio conexo que Cotifly todavía no leyó: espera la siguiente actualización del costeo para finalizar la cotización "
        + "con su precio.";

    /// <summary>
    /// Finaliza la cotización por <paramref name="usuarioId"/> en <paramref name="ahora"/>. Devuelve <c>true</c> si la finalizó,
    /// <c>false</c> si ya estaba finalizada (idempotente: no cambia nada, conserva la fecha y el comercial de la primera vez) o
    /// <c>COTIZACION_NO_FINALIZABLE</c> si el estado no lo admite (<see cref="AdmiteFinalizar"/>), con <c>detalle.estado_costeo</c>, o si
    /// Nexo agregó un conexo que Cotifly todavía no leyó (<see cref="ConexosPendientesDeLeer"/>; <c>detalle.motivo</c> =
    /// <see cref="CodigoConexosPendientesDeLeer"/>): el precio todavía no es el definitivo.
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
                + "costeo de Nexo listo (autocosteo o entregada) y su precio.")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["estado_costeo"] = estado },
            };
        }

        if (ConexosPendientesDeLeer)
        {
            // Validación HU-036: Nexo ya cobra un conexo que Cotifly todavía no leyó (HU-013.15, #135). Finalizar ahora congelaría un precio
            // sin ese conexo y la siguiente lectura ya no lo importaría (#208): se espera a que el polling lo traiga.
            return new clsErrorDominio(clsCodigosError.CotizacionNoFinalizable, MotivoNoFinalizableConexosPendientes)
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["estado_costeo"] = EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null,
                    ["motivo"] = CodigoConexosPendientesDeLeer,
                },
            };
        }

        FinalizadaAt = ahora;
        FinalizadaPor = usuarioId;
        return true;
    }
}
