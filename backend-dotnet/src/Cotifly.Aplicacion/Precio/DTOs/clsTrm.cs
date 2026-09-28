namespace Cotifly.Aplicacion.Precio.DTOs;

/// <summary>
/// Tasa Representativa del Mercado (COP por 1 USD) vigente para una fecha (HU-012.1, ADR-013). La publica la Superintendencia
/// Financiera por rangos de vigencia: la de un sábado, domingo o festivo es la del rango que cubre ese día.
/// </summary>
/// <param name="Valor">Pesos colombianos por un dólar (&gt; 0), con los decimales que publica la fuente (hasta 4 en la columna <c>trmAplicada</c>).</param>
/// <param name="VigenciaDesde">Primer día de vigencia.</param>
/// <param name="VigenciaHasta">Último día de vigencia (incluido).</param>
/// <param name="Fuente">Identificador de la fuente (ej. <c>superfinanciera</c>), para trazabilidad y logs.</param>
public sealed record clsTrm(decimal Valor, DateOnly VigenciaDesde, DateOnly VigenciaHasta, string Fuente)
{
    /// <summary>¿La TRM está vigente en <paramref name="fecha"/>?</summary>
    public bool VigenteEn(DateOnly fecha) => fecha >= VigenciaDesde && fecha <= VigenciaHasta;
}
