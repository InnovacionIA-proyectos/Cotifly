namespace Cotifly.Aplicacion.Licitaciones.DTOs;

// HU-044.5 (#256): plantillas de mapeo, formatos de lectura (recetas) y diccionario del cliente, guardados en NEXO. Lo que viaja a NEXO y
// lo que vuelve son los DTO del adaptador (clsDtosImportacionExcel.cs); aquí solo lo propio de los casos de uso de Cotifly.

/// <summary>
/// <c>PUT /web/licitaciones/recetas-lectura</c>: el MISMO Excel con el que se confirmó la receta (con su nombre original), la
/// <paramref name="Receta"/> como texto JSON opaco (llega a NEXO exactamente como vino), el NIT de la licitación (obligatorio), un nombre
/// opcional y las instrucciones que dio el comercial.
/// </summary>
public sealed record clsEntradaGuardarReceta(clsArchivoSubido Archivo, string? Receta, string? NitCliente, string? Nombre, string? Instrucciones);

/// <summary>
/// <c>GET /web/diccionario-cliente</c>: los términos que se pueden mostrar (con NIT, los del cliente y los generales; sin NIT, solo los
/// generales).
/// </summary>
public sealed record clsConsultaDiccionarioCliente(IReadOnlyList<clsTerminoCliente> Terminos);

/// <summary>Aviso de la consulta del diccionario (reservado en el contrato: hoy la respuesta lo trae siempre en <c>null</c>).</summary>
public sealed record clsAvisoDiccionario(string Codigo, string Mensaje);

/// <summary>
/// <c>POST</c> y <c>PUT /web/diccionario-cliente</c>: el NIT del cliente (obligatorio) y los campos del término tal como llegan; el caso de
/// uso los valida antes de NEXO. <paramref name="Activo"/> ausente = <c>true</c>.
/// </summary>
public sealed record clsEntradaTerminoCliente(
    string? NitCliente, string? Termino, string? Tipo, string? Concepto, string? Contexto, string? Ejemplo, bool? Activo);
