using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cotifly.Infraestructura.IA.Services;

/// <summary>
/// Política del pipeline del SDK de OpenAI para <c>openai_compatible</c> (HU-003.6, issue #84): agrega <c>"stream": false</c> al
/// cuerpo de <c>POST .../chat/completions</c> cuando no lo trae. El SDK no envía <c>stream</c> en el modo sin streaming (la API de
/// OpenAI asume false), pero gateways como OmniRoute asumen true y responden SSE (<c>text/event-stream</c>), que el SDK no puede
/// leer como un <c>chat.completion</c>. Una petición que ya trae <c>stream</c> (el modo streaming) o cuyo cuerpo no es un objeto JSON
/// pasa sin cambios.
/// </summary>
public sealed class clsPoliticaSinStreaming : PipelinePolicy
{
    public const string RutaChat = "/chat/completions";
    public const string CampoStream = "stream";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(message);
        Preparar(message.Request);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ArgumentNullException.ThrowIfNull(message);
        Preparar(message.Request);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    /// <summary>Reescribe el cuerpo de la petición si corresponde (ver la clase). Nunca lanza por un cuerpo que no entiende.</summary>
    public static void Preparar(PipelineRequest peticion)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        if (!string.Equals(peticion.Method, "POST", StringComparison.OrdinalIgnoreCase)
            || peticion.Content is null
            || peticion.Uri is null
            || !peticion.Uri.AbsolutePath.TrimEnd('/').EndsWith(RutaChat, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var copia = new MemoryStream();
        peticion.Content.WriteTo(copia);
        JsonNode? cuerpo;
        try
        {
            cuerpo = JsonNode.Parse(copia.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        if (cuerpo is not JsonObject objeto || objeto.ContainsKey(CampoStream))
        {
            return;
        }

        objeto[CampoStream] = false;
        peticion.Content = BinaryContent.Create(BinaryData.FromString(objeto.ToJsonString()));
    }
}
