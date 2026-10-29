using Cotifly.Aplicacion.Cotizaciones.DTOs;

namespace Cotifly.Aplicacion.Cotizaciones.Interfaces;

/// <summary>HU-018.1 (#155, parte C): escribe el <c>.xlsx</c> de una cotización (ClosedXML en Infraestructura). Solo escribe: no lee Excel.</summary>
public interface IClsExportadorExcelCotizacion
{
    /// <summary>
    /// El libro con las hojas "Resumen", "Rutas", "Camiones" y "Conexos". Con <see cref="clsExcelCotizacion.SoloLectura"/> no escribe
    /// ninguna columna de costo ni de margen (tampoco las recibe: <see cref="clsExcelCotizacion.SinCostos"/>).
    /// </summary>
    byte[] Generar(clsExcelCotizacion datos);
}
