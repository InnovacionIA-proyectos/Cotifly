using System.ComponentModel.DataAnnotations;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Opciones del costeo en Nexo (sección <c>CosteoNexo</c>, ADR-015 §4), validadas al arrancar. La URL de Nexo es la de siempre
/// (<c>Nexo:UrlBase</c>); aquí va el ritmo del polling (HU-005.6). Los valores por defecto (5 s al inicio, hasta 15 min, 72 h de
/// edad máxima) quedan pendientes de confirmar con el equipo de Nexo.
/// </summary>
public sealed class clsOpcionesCosteoNexo : IValidatableObject
{
    public const string Seccion = "CosteoNexo";

    /// <summary>Apaga el polling sin quitar <c>Nexo:UrlBase</c> (sin URL, el polling no corre de todos modos).</summary>
    public bool Habilitado { get; set; } = true;

    /// <summary>Primera consulta tras el envío y espera mínima entre consultas (s). Por defecto 5 s, como el frontend de Nexo.</summary>
    [Range(1, 3600)]
    public int IntervaloInicialSegundos { get; set; } = 5;

    /// <summary>Espera máxima entre consultas de una misma cotización (min).</summary>
    [Range(1, 1440)]
    public int IntervaloMaximoMinutos { get; set; } = 15;

    /// <summary>Fracción de la edad de la solicitud que se espera hasta la siguiente consulta (espaciado por edad).</summary>
    [Range(0.01, 1.0)]
    public double FactorEdad { get; set; } = 0.25;

    /// <summary>Edad máxima del polling (h): pasada, una cotización sin costeo queda en <c>costeo_vencido</c>.</summary>
    [Range(1, 720)]
    public int EdadMaximaHoras { get; set; } = 72;

    /// <summary>Cada cuánto revisa el worker la agenda (s).</summary>
    [Range(1, 300)]
    public int IntervaloTickSegundos { get; set; } = 5;

    /// <summary>Cotizaciones que procesa el worker por vuelta como máximo.</summary>
    [Range(1, 500)]
    public int Lote { get; set; } = 20;

    /// <summary>
    /// Reclamo de una cotización mientras se consulta (s): si el proceso muere a mitad, otra réplica la retoma pasado este tiempo.
    /// Debe superar el tiempo de la consulta a Nexo (hasta 60 s: dos llamadas de 30 s).
    /// </summary>
    [Range(61, 3600)]
    public int ReclamoSegundos { get; set; } = 120;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IntervaloMaximoMinutos * 60 < IntervaloInicialSegundos)
        {
            yield return new ValidationResult(
                "CosteoNexo:IntervaloMaximoMinutos debe ser mayor o igual que CosteoNexo:IntervaloInicialSegundos.",
                [nameof(IntervaloMaximoMinutos), nameof(IntervaloInicialSegundos)]);
        }
    }

    public TimeSpan IntervaloInicial => TimeSpan.FromSeconds(IntervaloInicialSegundos);

    public TimeSpan IntervaloMaximo => TimeSpan.FromMinutes(IntervaloMaximoMinutos);

    public TimeSpan EdadMaxima => TimeSpan.FromHours(EdadMaximaHoras);

    public TimeSpan Reclamo => TimeSpan.FromSeconds(ReclamoSegundos);
}
