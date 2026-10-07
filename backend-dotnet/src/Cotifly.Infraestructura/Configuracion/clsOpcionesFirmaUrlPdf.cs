using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Firma de la URL del PDF (sección <c>Pdf</c>, ADR-011 §3). Variables <c>Pdf__ClaveFirmaUrl</c> y <c>Pdf__MinutosUrlFirmada</c>.
/// La emitía la tarjeta de Google Chat, retirado en HU-013.9 (#105): la descarga con URL firmada (<c>GET /pdfs/{archivo}?t=...</c>)
/// se conserva mientras el frontend no la retire, pero hoy ningún canal emite enlaces firmados.
/// </summary>
public sealed class clsOpcionesFirmaUrlPdf
{
    public const string Seccion = "Pdf";

    /// <summary>
    /// Clave HMAC (secreto, al menos 32 caracteres). Vacía: clave aleatoria por proceso (los enlaces dejan de valer al reiniciar y
    /// no sirven entre réplicas; queda un warning). Con varias réplicas todas deben compartirla.
    /// </summary>
    [clsSecreto(LargoMinimo = 32, Opcional = true)]
    public string? ClaveFirmaUrl { get; set; }

    /// <summary>Vigencia del enlace firmado en minutos. Por defecto 15.</summary>
    [Range(1, 120)]
    public int MinutosUrlFirmada { get; set; } = 15;
}
