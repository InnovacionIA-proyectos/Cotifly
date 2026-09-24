using Cotifly.Dominio.Clientes;

namespace Cotifly.Dominio.Agente;

/// <summary>Cliente candidato de una búsqueda por similitud, pendiente de confirmación (ADR-004 §5).</summary>
public sealed record clsCandidatoCliente(string NitCliente, string Nombre, OrigenCliente OrigenCliente);
