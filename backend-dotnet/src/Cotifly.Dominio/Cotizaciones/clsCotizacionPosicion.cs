namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Posición de cubicaje de una cotización (<c>dbo_cotifly.CotizacionPosiciones</c>, script 0004, HU-005.8): una fila por bulto
/// del formulario, en su orden. <see cref="CreatedAt"/> lo asigna la persistencia.
/// </summary>
public sealed class clsCotizacionPosicion
{
    private clsCotizacionPosicion()
    {
    }

    public Guid Id { get; private set; }
    public Guid CotizacionId { get; private set; }

    /// <summary>1 para la primera posición del formulario (único por cotización).</summary>
    public int Orden { get; private set; }
    public string? Descripcion { get; private set; }
    public int Cantidad { get; private set; }
    public decimal LargoCm { get; private set; }
    public decimal AnchoCm { get; private set; }
    public decimal AltoCm { get; private set; }
    public decimal? PesoKg { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Posiciones de la cotización a partir de las de la solicitud ya validada (<see cref="clsSolicitudCosteo.Validar"/>).</summary>
    public static IReadOnlyList<clsCotizacionPosicion> Desde(Guid cotizacionId, IEnumerable<clsPosicionCarga> posiciones)
    {
        ArgumentNullException.ThrowIfNull(posiciones);
        return [.. posiciones.Select((p, i) => new clsCotizacionPosicion
        {
            Id = Guid.NewGuid(),
            CotizacionId = cotizacionId,
            Orden = i + 1,
            Descripcion = string.IsNullOrWhiteSpace(p.Descripcion) ? null : p.Descripcion.Trim(),
            Cantidad = p.Cantidad,
            LargoCm = p.LargoCm,
            AnchoCm = p.AnchoCm,
            AltoCm = p.AltoCm,
            PesoKg = p.PesoKg,
        })];
    }

    /// <summary>La posición como dato de la solicitud (para reenviarla a Nexo sin volver a llenar el formulario).</summary>
    public clsPosicionCarga APosicion() => new(Descripcion, Cantidad, LargoCm, AnchoCm, AltoCm, PesoKg);
}
