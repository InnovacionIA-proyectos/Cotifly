using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Redaccion.Services;

/// <summary>Redactor del asunto y la introducción del PDF (HU-013.5, sección <c>IA</c>, la misma del proveedor y el modelo).</summary>
public sealed class clsOpcionesRedaccion
{
    public const string Seccion = "IA";

    /// <summary>
    /// Segundos que se espera al modelo (<c>IA__TiempoRedaccionSegundos</c>, ADR-018 §2a). Por defecto 15: pasado ese tiempo se usan
    /// los textos estándar y la respuesta sale igual.
    /// </summary>
    [Range(1, 120)]
    public int TiempoRedaccionSegundos { get; set; } = 15;
}
