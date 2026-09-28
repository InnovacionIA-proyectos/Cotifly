using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Opciones del costeo en Nexo (sección <c>CosteoNexo</c>, ADR-015), validadas al arrancar. La URL de Nexo es la de siempre
/// (<c>Nexo:UrlBase</c>); aquí solo va el ritmo del polling (HU-005.6).
/// </summary>
public sealed class clsOpcionesCosteoNexo
{
    public const string Seccion = "CosteoNexo";

    /// <summary>Primera consulta tras el envío y espera mínima entre consultas (s). Por defecto 5 s, como el frontend de Nexo.</summary>
    [Range(1, 3600)]
    public int IntervaloInicialSegundos { get; set; } = 5;
}
