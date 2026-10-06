using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Opciones de la resolución de margen (sección <c>SapTm</c>, variable <c>SapTm__SalesOrgDefault</c>).
/// Infraestructura la enlaza y la valida al arrancar. El modo de la fuente y sus credenciales (<c>SapTm:Modo</c>,
/// <c>ExportPath</c>, <c>Url</c>, <c>User</c>, <c>Pass</c>) viven en la misma sección pero en <c>clsOpcionesSapTm</c> de
/// Infraestructura (#13): Aplicación no los necesita y la clave usa <c>[clsSecreto]</c>.
/// </summary>
public sealed class clsOpcionesMargen
{
    public const string Seccion = "SapTm";

    /// <summary>Organización de ventas por defecto para consultar SAP TM (<c>sap_sales_org_default</c> del Python).</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(12)]
    public string SalesOrgDefault { get; set; } = "50000005";
}
