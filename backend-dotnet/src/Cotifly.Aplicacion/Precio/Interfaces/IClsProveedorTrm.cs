using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Precio.Interfaces;

/// <summary>
/// Puerto de la TRM oficial del día (HU-012.1, ADR-014). El adaptador de Infraestructura consulta la fuente oficial (datos abiertos de
/// la Superintendencia Financiera) con caché por día: la TRM no cambia intradía. Nunca lanza por fallos de la fuente: devuelve
/// <c>TRM_NO_DISPONIBLE</c> y quien llama decide (no se cotiza en USD, HU-012.2). La TRM nunca la aporta el LLM (ADR-003).
/// </summary>
public interface IClsProveedorTrm
{
    /// <summary>TRM vigente en <paramref name="fecha"/> (fecha de negocio en America/Bogota), o <c>TRM_NO_DISPONIBLE</c>.</summary>
    Task<clsResultado<clsTrm>> ObtenerAsync(DateOnly fecha, CancellationToken cancellationToken = default);
}
