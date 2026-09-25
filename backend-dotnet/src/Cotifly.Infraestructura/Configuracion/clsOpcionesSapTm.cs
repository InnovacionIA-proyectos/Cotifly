namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Fuente de la rentabilidad por cliente de SAP TM (sección <c>SapTm</c>, la misma de <c>clsOpcionesMargen</c>, que solo
/// lleva <c>SalesOrgDefault</c> porque la consume Aplicación). Port de <c>sap_tm_*</c> de <c>shared/config.py</c> (#13).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>SapTm:Modo</c> vacío (por defecto): fuente no configurada, el resolver usa la política de la línea (#10).
/// A diferencia del Python, cuyo valor por defecto era <c>export_json</c> con el archivo de muestra.</item>
/// <item><c>export_json</c>: exige <see cref="ExportPath"/>.</item>
/// <item><c>odata</c>: exige <see cref="Url"/> (http/https absoluta, sin usuario ni consulta), <see cref="User"/> y
/// <see cref="Pass"/> en cualquier entorno; en Production la clave no puede ser un placeholder ni tener menos de 8 caracteres.
/// <see cref="SapClient"/> es opcional (3 dígitos).</item>
/// </list>
/// Reglas en <see cref="clsValidadorOpcionesSapTm"/>. Ningún mensaje ni log incluye <see cref="User"/> o <see cref="Pass"/>.
/// </remarks>
public sealed class clsOpcionesSapTm
{
    public const string Seccion = "SapTm";
    public const string ModoExportJson = "export_json";
    public const string ModoOData = "odata";

    /// <summary><c>export_json</c> | <c>odata</c> | vacío (no configurada). Sin distinguir mayúsculas ni espacios.</summary>
    public string? Modo { get; set; }

    /// <summary>Ruta del export JSON de <c>RentabClienteSet</c> (<c>{"d":{"results":[...]}}</c>), preferiblemente absoluta.</summary>
    public string? ExportPath { get; set; }

    /// <summary>URL base del servicio OData, sin <c>/RentabClienteSet</c> (ej. <c>https://host/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV</c>).</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Mandante de SAP (<c>sap-client</c>, 3 dígitos), opcional. Con valor se agrega <c>&amp;sap-client=NNN</c> a la consulta
    /// OData; sin valor SAP usa el mandante por defecto del servicio.
    /// </summary>
    public string? SapClient { get; set; }

    /// <summary>Usuario de servicio para autenticación Basic.</summary>
    public string? User { get; set; }

    /// <summary>Clave del usuario de servicio. Obligatoria solo con <c>odata</c> (la exige el validador de la opción).</summary>
    [clsSecreto(LargoMinimo = 8, Opcional = true)]
    public string? Pass { get; set; }

    /// <summary><see cref="Modo"/> sin espacios y en minúsculas (<c>""</c> si no hay modo).</summary>
    public string ModoNormalizado => (Modo ?? string.Empty).Trim().ToLowerInvariant();
}
