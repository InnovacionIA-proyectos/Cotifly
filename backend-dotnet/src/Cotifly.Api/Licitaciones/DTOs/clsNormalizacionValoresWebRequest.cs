using Cotifly.Aplicacion.Licitaciones.DTOs;

namespace Cotifly.Api.Licitaciones.DTOs;

/// <summary>
/// <c>NormalizarValoresWeb</c> (snake_case, HU-044.4, #255): el NIT de la licitación (opcional) y los textos distintos del Excel. Los
/// límites (máx. 1000 valores de 150 caracteres, campos cerrados) los valida el caso de uso con los códigos de NEXO.
/// </summary>
public sealed record clsNormalizacionValoresWebRequest(string? NitCliente, IReadOnlyList<clsValorNormalizarWebRequest?>? Valores)
{
    public clsEntradaNormalizacion AEntrada() =>
        new(NitCliente, Valores?.Select(v => v is null ? null : new clsValorEntradaNormalizacion(v.Campo, v.Valor)).ToList());
}

/// <summary>Un texto del Excel: <c>campo</c> = <c>tipo_vehiculo</c> | <c>carroceria</c> | <c>ciudad</c> | <c>linea_negocio</c>.</summary>
public sealed record clsValorNormalizarWebRequest(string? Campo, string? Valor);
