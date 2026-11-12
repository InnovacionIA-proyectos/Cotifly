namespace Cotifly.Aplicacion.Licitaciones.DTOs;

// Plantillas de mapeo, formatos de lectura (recetas) y diccionario del cliente, guardados en NEXO. Lo que viaja a NEXO y
// lo que vuelve son los DTO del adaptador (clsDtosImportacionExcel.cs); aquí solo lo propio de los casos de uso de Cotifly.

/// <summary>
/// <c>PUT /web/licitaciones/recetas-lectura</c>: el MISMO Excel con el que se confirmó la receta (con su nombre original), la
/// <paramref name="Receta"/> como texto JSON opaco (llega a NEXO exactamente como vino), el NIT de la licitación (obligatorio), un nombre
/// opcional y las instrucciones que dio el comercial.
/// </summary>
public sealed record clsEntradaGuardarReceta(clsArchivoSubido Archivo, string? Receta, string? NitCliente, string? Nombre, string? Instrucciones);

/// <summary>
/// <c>GET /web/diccionario-cliente</c>: los términos que se pueden mostrar (solo los generales) y, si se pidió con NIT, el aviso de que
/// los del cliente todavía no están disponibles.
/// </summary>
public sealed record clsConsultaDiccionarioCliente(IReadOnlyList<clsTerminoCliente> Terminos, clsAvisoDiccionario? Aviso);

/// <summary>Aviso de la consulta del diccionario: un código estable para la interfaz y el mensaje para el comercial.</summary>
public sealed record clsAvisoDiccionario(string Codigo, string Mensaje);
