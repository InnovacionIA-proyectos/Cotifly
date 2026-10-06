using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;

namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// Respuesta de <c>POST /web/chat</c> (<c>RespuestaChat</c> de <c>specs/openapi.yaml</c>, paridad con <c>_datos_de</c> de
/// <c>orquestador/web.py</c>): el texto del agente y, de las herramientas del turno, la última salida exitosa de
/// <c>calcular_precio</c>, <c>obtener_margen</c> y <c>generar_pdf</c>. Los números nunca salen del LLM (ADR-003). <see cref="Errores"/>
/// (HU-012.4): los errores de negocio de las herramientas del turno, para que el frontend no interprete el texto.
/// </summary>
public sealed record clsRespuestaChat(
    string Texto,
    IReadOnlyList<string> Herramientas,
    clsSalidaPrecio? Precio,
    clsSalidaMargen? Margen,
    clsSalidaPdf? Pdf,
    IReadOnlyList<clsErrorHerramienta>? Errores = null)
{
    public static clsRespuestaChat Desde(clsResultadoTurno resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        return new clsRespuestaChat(
            resultado.Texto,
            resultado.HerramientasInvocadas,
            resultado.UltimaSalida<clsSalidaPrecio>(clsNombresHerramientas.CalcularPrecio),
            resultado.UltimaSalida<clsSalidaMargen>(clsNombresHerramientas.ObtenerMargen),
            resultado.UltimaSalida<clsSalidaPdf>(clsNombresHerramientas.GenerarPdf),
            [.. resultado.Salidas.Where(s => !s.EsExitoso).Select(s => new clsErrorHerramienta(s.Herramienta, s.Error!.Codigo))]);
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
