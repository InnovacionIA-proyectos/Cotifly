namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Una tasa del seguro de la mercancía (<c>dbo_cotifly.TarifasSeguro</c>, script <c>db/scripts/20_seguro_mercancia.sql</c>), como
/// fracción (0.003450 = 0,345 %). Solo una está activa (índice único filtrado <c>uq_tarifasSeguro_activa</c>): es la que
/// se copia en la ruta al marcar el seguro. El backend solo la lee; se cambia con
/// <c>db/scripts/operacion/cambiar_tasa_seguro.sql</c> (desactiva la vigente e inserta la nueva).
/// </summary>
public sealed class clsTarifaSeguro
{
    private clsTarifaSeguro()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Fracción mayor que 0 y menor que 1 (CHECK <c>ck_tarifasSeguro_tasa</c>).</summary>
    public decimal Tasa { get; private set; }

    /// <summary>Desde cuándo rige (informativo: se usa la activa).</summary>
    public DateTimeOffset VigenteDesde { get; private set; }

    /// <summary><c>isActive</c>: la vigente.</summary>
    public bool Activa { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}
