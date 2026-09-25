namespace Cotifly.Dominio.Comun;

/// <summary>
/// Códigos de error de negocio que devuelven las herramientas del agente (esquema <c>ErrorNegocio</c>
/// de <c>specs/openapi.yaml</c>, mismos códigos que el Python). <c>Cotifly.Api.Tests</c> verifica que
/// cada constante exista en el enum del contrato. Cada módulo agrega aquí los suyos.
/// </summary>
public static class clsCodigosError
{
    /// <summary>No hay fuente de costeo disponible (el tarifario de Drive se retiró; Nexo llega con HU-005).</summary>
    public const string TarifaFuenteNoDisponible = "TARIFA_FUENTE_NO_DISPONIBLE";

    /// <summary>La fuente de margen (SAP TM) no respondió o no está configurada.</summary>
    public const string MargenFuenteNoDisponible = "MARGEN_FUENTE_NO_DISPONIBLE";

    /// <summary>La línea de negocio no tiene mapeo TrqType ni política de margen.</summary>
    public const string MargenLineaDesconocida = "MARGEN_LINEA_DESCONOCIDA";

    /// <summary>Entrada que no permite calcular un precio (costo ≤ 0, margen fuera de rango, método desconocido).</summary>
    public const string PrecioInvalido = "PRECIO_INVALIDO";

    /// <summary>
    /// El margen negociado está fuera de rango para su método (negativo, o ≥ 1 con <c>margen</c>), el método no es
    /// <c>margen</c>/<c>markup</c> o falta el motivo. Código nuevo del backend .NET (agregado al contrato en #10).
    /// </summary>
    public const string AjusteMargenInvalido = "AJUSTE_MARGEN_INVALIDO";

    /// <summary>El ajuste de margen apunta a una cotización que no existe (o no es del comercial).</summary>
    public const string AjusteCotizacionInvalida = "AJUSTE_COTIZACION_INVALIDA";
}
