using Cotifly.Aplicacion.Mercancias.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Mercancias.Interfaces;

/// <summary>
/// Lectura local de un Excel como grilla cruda (HU-035.1, #202, ADR-024): hojas, encabezados y filas como texto, sin Nexo. La implementa
/// Infraestructura (ClosedXML), acotada contra archivos maliciosos. Nunca lanza por el contenido del archivo: <c>EXCEL_INVALIDO</c> si
/// no es un libro legible, <c>ARCHIVO_DEMASIADO_GRANDE</c> si descomprimido pasa el tope, <c>EXCEL_SIN_DATOS</c> si ninguna hoja tiene
/// datos y <c>SERVICIO_OCUPADO</c> si el cupo de lecturas simultáneas no se libera a tiempo. Las mercancías sugeridas no son suyas.
/// </summary>
public interface IClsLectorGrillaExcel
{
    Task<clsResultado<clsAnalisisExcelMercancias>> LeerAsync(string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default);
}
