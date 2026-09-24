namespace Cotifly.Aplicacion.Margen.Repositories;

/// <summary>
/// Puerto del mapeo TrqType ↔ línea de negocio (<c>SapMapeoTrqType</c>). Solo filas activas; una fila
/// con la organización de ventas pedida tiene prioridad sobre el comodín <c>salesOrg = ''</c>.
/// </summary>
public interface IClsSapMapeoRepository
{
    /// <summary>Línea de negocio de un TrqType, o <c>null</c> si no hay mapeo activo.</summary>
    Task<string?> LineaDeAsync(string trqType, string salesOrg = "", CancellationToken cancellationToken = default);

    /// <summary>TrqType de una línea de negocio, o <c>null</c> si no hay mapeo activo.</summary>
    Task<string?> TrqTypeDeAsync(string lineaNegocio, string salesOrg = "", CancellationToken cancellationToken = default);
}
