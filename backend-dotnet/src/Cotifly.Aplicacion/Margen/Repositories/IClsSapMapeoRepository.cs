namespace Cotifly.Aplicacion.Margen.Repositories;

/// <summary>
/// Puerto del mapeo TrqType ↔ línea de negocio (<c>SapMapeoTrqType</c>). Solo filas activas; una fila
/// con la organización de ventas pedida tiene prioridad sobre el comodín <c>salesOrg = ''</c>.
/// HU-042.4 (#247): la línea se relaciona por su id de <c>MargenesPolitica</c> (<c>lineaNegocioId</c>), nunca por el texto.
/// </summary>
public interface IClsSapMapeoRepository
{
    /// <summary>
    /// Nombre de la línea de negocio de un TrqType (el de <c>MargenesPolitica</c>, cruzando por <c>lineaNegocioId</c>), o <c>null</c>
    /// si no hay mapeo activo.
    /// </summary>
    Task<string?> LineaDeAsync(string trqType, string salesOrg = "", CancellationToken cancellationToken = default);

    /// <summary>TrqType de la línea de negocio <paramref name="lineaNegocioId"/> (id de <c>MargenesPolitica</c>), o <c>null</c> si no hay mapeo activo.</summary>
    Task<string?> TrqTypeDeAsync(int lineaNegocioId, string salesOrg = "", CancellationToken cancellationToken = default);
}
