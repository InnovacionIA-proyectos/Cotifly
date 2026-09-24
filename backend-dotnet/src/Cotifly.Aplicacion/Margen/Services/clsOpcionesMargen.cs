using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Opciones de la resolución de margen (sección <c>SapTm</c>, variable <c>SapTm__SalesOrgDefault</c>).
/// Infraestructura la enlaza y la valida al arrancar; #13 agrega en la misma sección el modo de la fuente
/// (<c>SapTm:Modo</c>) y sus credenciales.
/// </summary>
public sealed class clsOpcionesMargen
{
    public const string Seccion = "SapTm";

    /// <summary>Organización de ventas por defecto para consultar SAP TM (<c>sap_sales_org_default</c> del Python).</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(12)]
    public string SalesOrgDefault { get; set; } = "50000005";
}
