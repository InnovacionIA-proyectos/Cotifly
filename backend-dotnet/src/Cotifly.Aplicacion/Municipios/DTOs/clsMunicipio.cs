namespace Cotifly.Aplicacion.Municipios.DTOs;

/// <summary>
/// Municipio del catálogo de Nexo, para el selector de origen/destino del formulario de captura. Se omiten
/// <c>nombreTarifasPlaza</c> y <c>zonaTarificacion</c> de la respuesta real de Nexo: son de costeo interno (SAP TM),
/// el frontend no los necesita.
/// </summary>
/// <param name="Id">Id del municipio en el catálogo de Nexo.</param>
/// <param name="CodigoDane">Código Dane del municipio (puede faltar en algunas filas del catálogo).</param>
/// <param name="Municipio">Nombre del municipio.</param>
/// <param name="Departamento">Departamento al que pertenece.</param>
/// <param name="Pais">País.</param>
public sealed record clsMunicipio(int Id, string? CodigoDane, string? Municipio, string? Departamento, string? Pais);
