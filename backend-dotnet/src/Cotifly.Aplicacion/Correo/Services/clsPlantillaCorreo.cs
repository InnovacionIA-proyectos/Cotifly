using System.Net;

namespace Cotifly.Aplicacion.Correo.Services;

/// <summary>
/// Plantilla del correo portada del Python: asunto por defecto de <c>enviar_cotizacion</c> (<c>orquestador/web.py</c>),
/// cuerpo <c>_PLANTILLA</c> y envoltura HTML corporativa <c>_cuerpo_html</c> (<c>mcp_server/correo.py</c>).
/// </summary>
public static class clsPlantillaCorreo
{
    /// <summary>Cuerpo por defecto (<c>_PLANTILLA</c>).</summary>
    public const string CuerpoPorDefecto =
        "Estimado cliente,\n\n"
        + "Adjuntamos la cotización solicitada. Quedamos atentos a cualquier inquietud.\n\n"
        + "Cordialmente,\nEquipo Comercial — Botero Soto";

    /// <summary>Asunto por defecto: <c>Cotización {folio} — Botero Soto</c>.</summary>
    public static string AsuntoPorDefecto(string folio) => $"Cotización {folio} — Botero Soto";

    /// <summary>
    /// Cuerpo en HTML: el texto escapado (un <c>&lt;script&gt;</c> del comercial llega como texto), saltos de línea como
    /// <c>&lt;br&gt;</c> y la firma corporativa en azul Botero Soto.
    /// </summary>
    public static string Html(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var cuerpo = WebUtility.HtmlEncode(texto.Replace("\r\n", "\n", StringComparison.Ordinal)).Replace("\n", "<br>", StringComparison.Ordinal);
        return "<div style=\"font-family:Arial,Helvetica,sans-serif;color:#1A2A3A;font-size:14px;line-height:1.5\">"
               + cuerpo
               + "<hr style=\"border:none;border-top:1px solid #D9DEE5;margin:16px 0\">"
               + "<div style=\"font-size:12px;color:#5F6B7A\">"
               + "<b style=\"color:#005AA8\">Botero Soto</b> · Soluciones Logísticas</div>"
               + "</div>";
    }
}
