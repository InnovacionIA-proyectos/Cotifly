using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>Caso de uso <c>obtener_margen</c>: margen propuesto de SAP TM con respaldo en la política de la línea.</summary>
public interface IClsMargenResolverService
{
    /// <summary>
    /// Resuelve el margen de la línea. Si SAP TM no se puede leer, usa la política de la línea. Error:
    /// <c>MARGEN_LINEA_DESCONOCIDA</c> (sin política o línea inactiva). Sin política no hay TrqType, así que una caída de SAP siempre
    /// tiene la política de respaldo.
    /// </summary>
    Task<clsResultado<clsMargenResuelto>> ResolverAsync(clsEntradaMargen entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// Política de la línea con la misma resolución tolerante que <see cref="ResolverAsync"/> (sin tildes, mayúsculas ni
    /// espacios), sin consultar SAP TM. La usa <c>generar_pdf</c> para <c>Cotizaciones.lineaNegocioId</c>.
    /// <c>null</c> si la línea no existe en <c>MargenesPolitica</c>.
    /// </summary>
    Task<clsMargenPolitica?> ResolverLineaAsync(string? lineaNegocio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nombres de las líneas de negocio ACTIVAS de Cotifly (<c>MargenesPolitica</c>), en orden: las que se pueden elegir para cotizar.
    /// Las usa el error <c>MARGEN_LINEA_DESCONOCIDA</c> de las licitaciones para decir qué líneas usar.
    /// </summary>
    Task<IReadOnlyList<string>> LineasActivasAsync(CancellationToken cancellationToken = default);
}
