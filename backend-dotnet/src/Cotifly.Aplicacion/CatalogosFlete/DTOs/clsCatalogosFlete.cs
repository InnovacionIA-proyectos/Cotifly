namespace Cotifly.Aplicacion.CatalogosFlete.DTOs;

/// <summary>
/// Catálogos de la biblioteca SAP TM histórico para el formulario de captura: tipos de vehículo, tipos de carrocería
/// (Nexo escribe el campo "Carroseria"; aquí se corrige a "Carroceria") y clases de viaje. Se omite
/// <c>lineasNegocio</c> de la respuesta real de Nexo: Cotifly ya tiene su propio catálogo de línea de negocio.
/// </summary>
/// <param name="TiposVehiculo">Opciones de tipo de vehículo.</param>
/// <param name="TiposCarroceria">Opciones de tipo de carrocería.</param>
/// <param name="ClasesViaje">Opciones de clase de viaje.</param>
public sealed record clsCatalogosFlete(
    IReadOnlyList<clsOpcionCatalogo> TiposVehiculo,
    IReadOnlyList<clsOpcionCatalogo> TiposCarroceria,
    IReadOnlyList<clsOpcionCatalogo> ClasesViaje);

/// <summary>Opción de un catálogo: valor interno y etiqueta legible para el comercial.</summary>
/// <param name="Valor">Valor interno de la opción (el que viaja en la cotización).</param>
/// <param name="Etiqueta">Texto legible para mostrar en el formulario.</param>
public sealed record clsOpcionCatalogo(string Valor, string Etiqueta);
