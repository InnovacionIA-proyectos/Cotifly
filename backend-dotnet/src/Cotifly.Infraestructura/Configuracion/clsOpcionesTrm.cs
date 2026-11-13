using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Fuentes de la TRM (sección <c>Trm</c>, ADR-014). <c>Trm__Proveedor</c> elige la cadena:
/// <list type="bullet">
/// <item><c>dolarapi_con_respaldo</c> (por defecto): DolarApi (<c>Trm__UrlDolarApi</c>) y, si falla, la Superintendencia Financiera.</item>
/// <item><c>dolarapi</c>: solo DolarApi.</item>
/// <item><c>superfinanciera</c>: solo los datos abiertos de la Superintendencia Financiera (<c>Trm__Url</c>).</item>
/// </list>
/// Las dos fuentes son públicas y solo por https: no llevan credenciales. <c>Trm__TiempoEsperaSegundos</c> limita la consulta a la
/// Superintendencia y <c>Trm__TiempoEsperaDolarApiSegundos</c> la de DolarApi. Reglas en
/// <see cref="clsValidadorOpcionesTrm"/>.
/// </summary>
public sealed class clsOpcionesTrm
{
    public const string Seccion = "Trm";

    /// <summary>DolarApi como fuente principal y la Superintendencia Financiera como respaldo.</summary>
    public const string ProveedorDolarApiConRespaldo = "dolarapi_con_respaldo";

    /// <summary>Solo DolarApi.</summary>
    public const string ProveedorDolarApi = "dolarapi";

    /// <summary>Solo los datos abiertos de la Superintendencia Financiera.</summary>
    public const string ProveedorSuperfinanciera = "superfinanciera";

    /// <summary>Valores admitidos en <see cref="Proveedor"/>.</summary>
    public static IReadOnlyList<string> Proveedores { get; } = [ProveedorDolarApiConRespaldo, ProveedorDolarApi, ProveedorSuperfinanciera];

    /// <summary>Endpoint SODA del conjunto <c>32sa-8pi3</c> (campos <c>valor</c>, <c>vigenciadesde</c>, <c>vigenciahasta</c>).</summary>
    public const string UrlPorDefecto = "https://www.datos.gov.co/resource/32sa-8pi3.json";

    /// <summary>Endpoint de la TRM de DolarApi (<c>{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"..."}</c>).</summary>
    public const string UrlDolarApiPorDefecto = "https://co.dolarapi.com/v1/trm";

    /// <summary>
    /// Cadena de fuentes: <see cref="ProveedorDolarApiConRespaldo"/> (por defecto), <see cref="ProveedorDolarApi"/> o
    /// <see cref="ProveedorSuperfinanciera"/> (sin distinguir mayúsculas).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Proveedor { get; set; } = ProveedorDolarApiConRespaldo;

    /// <summary>URL del recurso JSON de la Superintendencia Financiera, sin consulta (la consulta SoQL la arma el adaptador).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Url { get; set; } = UrlPorDefecto;

    /// <summary>URL de la TRM de DolarApi, sin consulta.</summary>
    [Required(AllowEmptyStrings = false)]
    public string UrlDolarApi { get; set; } = UrlDolarApiPorDefecto;

    /// <summary>Límite de toda la consulta a la Superintendencia, reintentos incluidos (segundos). Por defecto 10.</summary>
    [Range(2, 60)]
    public int TiempoEsperaSegundos { get; set; } = 10;

    /// <summary>
    /// Límite de toda la consulta a DolarApi, reintentos incluidos (segundos). Por defecto 4: corto para que, si DolarApi no responde, el
    /// respaldo llegue pronto.
    /// </summary>
    [Range(2, 60)]
    public int TiempoEsperaDolarApiSegundos { get; set; } = 4;

    /// <summary><see cref="Proveedor"/> sin espacios y en minúsculas.</summary>
    public string ProveedorNormalizado => (Proveedor ?? string.Empty).Trim().ToLowerInvariant();
}
