using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;

namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// Respuesta de <c>POST /web/chat</c> (<c>RespuestaChat</c> de <c>specs/openapi.yaml</c>): el texto del asistente, las herramientas
/// del turno, <see cref="Errores"/> (HU-012.4: los errores de negocio de las herramientas, para que el frontend no interprete el texto)
/// y <see cref="AccionPropuesta"/> (HU-013.6: la acción que propuso el asistente por folio). <see cref="Precio"/>, <see cref="Margen"/> y
/// <see cref="Pdf"/> están obsoletos (deprecated en el contrato) y siempre van en <c>null</c>: los llenaba el agente legado, retirado
/// en HU-013.9 (#105); se conservan para no romper a clientes que todavía los leen.
/// </summary>
public sealed record clsRespuestaChat(
    string Texto,
    IReadOnlyList<string> Herramientas,
    clsSalidaPrecio? Precio,
    clsSalidaMargen? Margen,
    clsSalidaPdf? Pdf,
    IReadOnlyList<clsErrorHerramienta>? Errores = null,
    clsAccionPropuesta? AccionPropuesta = null)
{
    /// <summary>
    /// Respuesta del asistente por folio (HU-013.6, #102, ADR-018 §2b): <c>precio</c>, <c>margen</c> y <c>pdf</c> siempre en <c>null</c>
    /// (obsoletos) y la acción propuesta armada por el servidor, si la hubo. Si el validador no obtuvo un texto válido del modelo pero
    /// el turno sí armó una propuesta, el texto es <paramref name="textoSinRespuesta"/> y la propuesta sale igual.
    /// </summary>
    public static clsRespuestaChat DesdeAsistente(clsResultadoTurno resultado, string? textoSinRespuesta = null)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        return new clsRespuestaChat(
            resultado.EsRespaldo && textoSinRespuesta is not null ? textoSinRespuesta : resultado.Texto,
            resultado.HerramientasInvocadas,
            null,
            null,
            null,
            [.. resultado.Salidas.Where(s => !s.EsExitoso).Select(s => new clsErrorHerramienta(s.Herramienta, s.Error!.Codigo))],
            resultado.AccionPropuesta);
    }

}

/// <summary><c>ErrorHerramienta</c>: herramienta del turno que terminó con un error de negocio y su código de <c>ErrorNegocio</c>.</summary>
public sealed record clsErrorHerramienta(string Herramienta, string Codigo);

/// <summary>
/// Error controlado de <c>POST /web/chat</c> (<c>RespuestaChatError</c>): 200 con <c>error: true</c> y un texto legible
/// (paridad con el Python; el frontend lo muestra tal cual).
/// </summary>
public sealed record clsRespuestaChatError(string Texto)
{
    public bool Error { get; } = true;
}
