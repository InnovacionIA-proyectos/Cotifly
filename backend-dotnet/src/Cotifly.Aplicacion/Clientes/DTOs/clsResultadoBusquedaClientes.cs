using System.Text.Json.Serialization;

namespace Cotifly.Aplicacion.Clientes.DTOs;

/// <summary>
/// Resultado de la búsqueda de clientes (<c>GET /web/clientes</c>, ADR-005; antes también de la herramienta <c>buscar_cliente</c>,
/// retirada con el agente legado en HU-013.9, #105): si hubo una coincidencia exacta y los candidatos del catálogo de Nexo
/// (clientes de <c>DimBpSolicitante</c> y prospectos de <c>tblProspectos</c>, HU-005.2).
/// </summary>
/// <param name="Exacta">True si <paramref name="Candidatos"/> trae exactamente el cliente buscado (un solo candidato).</param>
/// <param name="Candidatos">Clientes encontrados, del más al menos parecido.</param>
public sealed record clsResultadoBusquedaClientes(bool Exacta, IReadOnlyList<clsClienteEncontrado> Candidatos);

/// <summary>Cliente o prospecto del catálogo.</summary>
/// <param name="NitCliente">NIT (identificador único de cliente en Cotifly, ADR-004).</param>
/// <param name="Nombre">Nombre oficial del catálogo.</param>
/// <param name="OrigenCliente"><c>cliente</c> | <c>prospecto</c>.</param>
/// <param name="Bp">BP de SAP (<c>OrderPartyId</c> de SAP TM); nulo en prospectos. Solo vive en el estado de la conversación.</param>
/// <param name="ClienteId">
/// Id (uuid) del cliente o prospecto en el catálogo de Nexo (<c>clienteId</c> de <c>ClienteBusquedaDto</c>). Lo usa el envío a costeo
/// cuando no hay BP (HU-013.10, G14). No se expone en <c>GET /web/clientes</c> (el contrato no lo tiene).
/// </param>
public sealed record clsClienteEncontrado(
    string NitCliente, string Nombre, string OrigenCliente, string? Bp = null, [property: JsonIgnore] string? ClienteId = null);
