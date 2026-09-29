namespace Cotifly.Aplicacion.Precio.DTOs;

/// <summary>
/// TRM que aplica a una moneda el día de negocio (HU-012.2): en COP, <see cref="Valor"/> 0 y sin vigencia; en USD, la oficial con su rango.
/// </summary>
public sealed record clsTrmDelDia(decimal Valor, DateOnly? VigenciaDesde, DateOnly? VigenciaHasta)
{
    /// <summary>La de una cotización en pesos: sin TRM.</summary>
    public static clsTrmDelDia Pesos { get; } = new(0m, null, null);

    public static clsTrmDelDia Desde(clsTrm trm)
    {
        ArgumentNullException.ThrowIfNull(trm);
        return new clsTrmDelDia(trm.Valor, trm.VigenciaDesde, trm.VigenciaHasta);
    }
}
