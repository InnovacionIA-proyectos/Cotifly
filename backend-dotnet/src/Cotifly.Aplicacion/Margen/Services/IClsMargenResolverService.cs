using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>Caso de uso <c>obtener_margen</c>: margen propuesto de SAP TM con respaldo en la política de la línea.</summary>
public interface IClsMargenResolverService
{
    /// <summary>
    /// Resuelve el margen de la línea. Errores: <c>MARGEN_LINEA_DESCONOCIDA</c> (sin mapeo ni política) y
    /// <c>MARGEN_FUENTE_NO_DISPONIBLE</c> (SAP TM no se pudo leer).
    /// </summary>
    Task<clsResultado<clsMargenResuelto>> ResolverAsync(clsEntradaMargen entrada, CancellationToken cancellationToken = default);
}
