using Cotifly.Dominio.Clientes;

namespace Cotifly.Dominio.Agente;

/// <summary>
/// Cliente candidato de una búsqueda por similitud, pendiente de confirmación (ADR-004 §5). <see cref="Bp"/> es el BP de SAP
/// (nulo en prospectos): si el comercial confirma al candidato, pasa al estado para el margen SAP TM (ADR-005).
/// </summary>
public sealed record clsCandidatoCliente(string NitCliente, string Nombre, OrigenCliente OrigenCliente, string? Bp = null);
