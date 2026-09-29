using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Fuente oficial de la TRM (sección <c>Trm</c>, variables <c>Trm__Url</c> y <c>Trm__TiempoEsperaSegundos</c>, HU-012.1, ADR-014):
/// el conjunto "Tasa de Cambio Representativa del Mercado - Histórico" de la Superintendencia Financiera en datos abiertos
/// (Socrata, <c>datos.gov.co</c>). Es información pública: no lleva credenciales. Reglas en <see cref="clsValidadorOpcionesTrm"/>.
/// </summary>
public sealed class clsOpcionesTrm
{
    public const string Seccion = "Trm";

    /// <summary>Endpoint SODA del conjunto <c>32sa-8pi3</c> (campos <c>valor</c>, <c>vigenciadesde</c>, <c>vigenciahasta</c>).</summary>
    public const string UrlPorDefecto = "https://www.datos.gov.co/resource/32sa-8pi3.json";

    /// <summary>URL del recurso JSON del conjunto de datos, sin consulta (la consulta SoQL la arma el adaptador).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Url { get; set; } = UrlPorDefecto;

    /// <summary>Límite de toda la consulta, reintentos incluidos (segundos). Por defecto 10.</summary>
    [Range(2, 60)]
    public int TiempoEsperaSegundos { get; set; } = 10;
}
