using System.ClientModel.Primitives;
using System.Text.Json;

namespace Cotifly.Infraestructura.IA.Services;

/// <summary>
/// Reintentos del SDK de OpenAI para <c>openai_compatible</c>. Igual que la política por defecto (3 reintentos
/// con espera exponencial ante 408, 429 y 5xx), salvo un 429 de enfriamiento del gateway: OmniRoute responde
/// <c>{"error": {"code": "model_cooldown", "reset_seconds": 33311}}</c> cuando todas sus credenciales del modelo están en
/// enfriamiento (horas). Reintentar no sirve y solo alarga el turno; sin reintento, <c>clsChatClientConRespaldo</c> pasa de
/// inmediato a <c>IA:Fallbacks</c>. También se corta si <c>reset_seconds</c> supera el tiempo máximo de la llamada.
/// </summary>
public sealed class clsPoliticaReintentoGateway(int timeoutSeg, int maxReintentos = 3) : ClientRetryPolicy(maxReintentos)
{
    public const string CodigoEnfriamiento = "model_cooldown";

    protected override bool ShouldRetry(PipelineMessage message, Exception? exception) =>
        !EsEnfriamiento(message, timeoutSeg) && base.ShouldRetry(message, exception);

    protected override async ValueTask<bool> ShouldRetryAsync(PipelineMessage message, Exception? exception) =>
        !EsEnfriamiento(message, timeoutSeg) && await base.ShouldRetryAsync(message, exception).ConfigureAwait(false);

    /// <summary>¿La respuesta es un 429 de enfriamiento (código <see cref="CodigoEnfriamiento"/> o espera mayor que la llamada)?</summary>
    public static bool EsEnfriamiento(PipelineMessage message, int timeoutSeg)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Response is not { Status: 429 } respuesta)
        {
            return false;
        }

        BinaryData contenido;
        try
        {
            contenido = respuesta.Content;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return EsCuerpoDeEnfriamiento(contenido.ToString(), timeoutSeg);
    }

    /// <summary>Lee el cuerpo de error de OmniRoute. Nunca lanza: un cuerpo que no entiende no es enfriamiento.</summary>
    public static bool EsCuerpoDeEnfriamiento(string? cuerpo, int timeoutSeg)
    {
        if (string.IsNullOrWhiteSpace(cuerpo))
        {
            return false;
        }

        try
        {
            using var documento = JsonDocument.Parse(cuerpo);
            if (documento.RootElement.ValueKind != JsonValueKind.Object
                || !documento.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (error.TryGetProperty("code", out var codigo) && codigo.ValueKind == JsonValueKind.String
                && string.Equals(codigo.GetString(), CodigoEnfriamiento, StringComparison.Ordinal))
            {
                return true;
            }

            return error.TryGetProperty("reset_seconds", out var espera) && espera.ValueKind == JsonValueKind.Number
                && espera.TryGetDouble(out var segundos) && segundos > timeoutSeg;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
