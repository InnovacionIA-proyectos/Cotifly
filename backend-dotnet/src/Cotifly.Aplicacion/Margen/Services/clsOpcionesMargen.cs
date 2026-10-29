using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Opciones de la resolución de margen (sección <c>SapTm</c>: <c>SapTm__SalesOrgDefault</c> y <c>SapTm__AvisoSincronizacionHoras</c>).
/// Infraestructura la enlaza y la valida al arrancar. HU-042.2 (#243, ADR-027): el margen de SAP TM sale de la copia local que sincroniza
/// el job de Datos; las variables del modo en vivo (<c>SapTm__Modo</c>, <c>Url</c>, <c>User</c>, <c>Pass</c>, <c>SapClient</c>,
/// <c>ExportPath</c>) se retiraron: si llegan, la API arranca igual y avisa que las ignora.
/// </summary>
public sealed class clsOpcionesMargen
{
    public const string Seccion = "SapTm";

    /// <summary>Organización de ventas por defecto para consultar SAP TM (<c>sap_sales_org_default</c> del Python).</summary>
    [Required(AllowEmptyStrings = false)]
    [MaxLength(12)]
    public string SalesOrgDefault { get; set; } = "50000005";

    /// <summary>
    /// HU-042.2 (#243): horas desde la última sincronización exitosa de la copia local a partir de las cuales el detalle del margen de
    /// SAP TM avisa que el dato puede estar desactualizado. El dato se sigue usando (la copia no caduca). Debe ser mayor que cero.
    /// </summary>
    [Range(1, 24 * 365)]
    public int AvisoSincronizacionHoras { get; set; } = 48;
}
