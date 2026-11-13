using System.Globalization;
using System.Text;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Prompt de sistema del asistente por folio (ADR-018 §2b): el recurso embebido
/// <c>Agente/Recursos/prompt_asistente_folio.md</c> (el LLM propone, no calcula, no ve ni promete montos y pide confirmación) precedido
/// de "# Contexto" con el nombre del comercial, la fecha de negocio y el folio de la conversación. No lleva ningún dato de dinero.
/// </summary>
public static class clsPromptAsistenteFolio
{
    public const string NombreRecurso = "Cotifly.Agente.PromptAsistenteFolio.md";

    private static readonly Lazy<string> _base = new(CargarBase);

    /// <summary>Las instrucciones sin el bloque de contexto.</summary>
    public static string Base => _base.Value;

    /// <summary>Prompt completo del turno.</summary>
    public static string Construir(string? nombreComercial, DateOnly hoy, string folio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        var texto = new StringBuilder();
        texto.AppendLine("# Contexto");
        if (!string.IsNullOrWhiteSpace(nombreComercial))
        {
            texto.Append("- Comercial: ").AppendLine(nombreComercial.Trim());
        }

        texto.Append("- Fecha de hoy: ").AppendLine(hoy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        texto.Append("- Cotización de esta conversación: ").AppendLine(folio.Trim());
        texto.AppendLine();
        texto.Append(Base);
        return texto.ToString();
    }

    private static string CargarBase()
    {
        using var flujo = typeof(clsPromptAsistenteFolio).Assembly.GetManifestResourceStream(NombreRecurso)
            ?? throw new InvalidOperationException($"No se encontró el recurso embebido {NombreRecurso}.");
        using var lector = new StreamReader(flujo, Encoding.UTF8);
        return lector.ReadToEnd().Trim();
    }
}
