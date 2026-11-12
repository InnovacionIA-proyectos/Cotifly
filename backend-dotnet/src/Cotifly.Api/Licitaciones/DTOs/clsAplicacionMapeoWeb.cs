using Cotifly.Aplicacion.Licitaciones.DTOs;

namespace Cotifly.Api.Licitaciones.DTOs;

/// <summary>
/// <c>AplicarMapeoWeb</c> (snake_case): el NIT de la licitación (opcional), el mapeo confirmado (<c>columnas[]</c> con
/// <c>indice</c>, <c>campo</c> de NEXO y <c>transformacion</c>), TODAS las filas de la hoja sin el encabezado (máx. 5000, celdas como texto)
/// y <c>primera_fila</c> (la fila del encabezado + 1; por defecto 2). La forma la valida el caso de uso con los códigos de NEXO.
/// </summary>
public sealed record clsAplicarMapeoWebRequest(
    string? NitCliente,
    IReadOnlyList<clsColumnaMapeoWebRequest?>? Columnas,
    IReadOnlyList<IReadOnlyList<string?>?>? Filas,
    int? PrimeraFila)
{
    public clsEntradaAplicacionMapeo AEntrada() =>
        new(NitCliente, Columnas?.Select(c => c is null ? null : new clsColumnaEntradaMapeo(c.Indice, c.Campo, c.Transformacion)).ToList(), Filas, PrimeraFila);
}

/// <summary><c>ColumnaMapeoConfirmada</c>: índice base 0 de la columna, <c>campo</c> de NEXO (o <c>ignorar</c>) y <c>transformacion</c> de NEXO.</summary>
public sealed record clsColumnaMapeoWebRequest(int? Indice, string? Campo, string? Transformacion);
