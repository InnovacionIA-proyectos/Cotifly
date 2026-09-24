namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// Entrada de <c>obtener_margen</c> (port de <c>EntradaObtenerMargen</c>).
/// </summary>
/// <param name="LineaNegocio">Nombre de la línea tal como lo escribió el comercial (se toleran tildes, mayúsculas y espacios).</param>
/// <param name="BpCliente">
/// BP del cliente en SAP (<c>OrderPartyId</c>), que el orquestador inyecta desde el estado (ADR-004).
/// Sin BP (prospecto o cliente no identificado) no se consulta SAP TM: nunca se usa el NIT.
/// </param>
/// <param name="SalesOrg">Organización de ventas; si no viene se usa <c>SapTm:SalesOrgDefault</c>.</param>
public sealed record clsEntradaMargen(string LineaNegocio, string? BpCliente = null, string? SalesOrg = null);
