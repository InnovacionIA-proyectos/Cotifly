using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Auditoria.Services;

/// <summary>
/// Auditoría transversal (port de <c>mcp_server/auditoria.py</c>): cada acción queda en <c>EventosHerramienta</c> con
/// herramienta, entrada, resumen de la salida (JSON de máximo 4000 caracteres), éxito, duración y hilo. Escribe en su
/// propio ámbito de DI y unidad de trabajo, y nunca lanza ni interrumpe al llamador: si la base falla, deja un warning.
/// </summary>
/// <remarks>
/// Quién audita qué: los casos de uso <c>generar_pdf</c>, <c>enviar_correo</c> y <c>registrar_ajuste_margen</c> se auditan
/// solos (decoradores <c>cls*Auditado</c>), así quedan registrados también cuando los llama un endpoint web. El filtro de
/// invocación de herramientas del agente (#12) usa <see cref="RegistrarAsync"/> para el resto de herramientas y debe omitir
/// esas tres para no duplicar el evento (<see cref="clsAuditoriaService.HerramientasAuditadasPorCasoDeUso"/>).
/// </remarks>
public interface IClsAuditoriaService
{
    /// <summary>Registra un evento. <paramref name="entrada"/> y <paramref name="salida"/> se serializan a JSON snake_case.</summary>
    Task RegistrarAsync(string herramienta, object? entrada, object? salida, bool exito, int duracionMs, string? threadId);

    /// <summary>
    /// Ejecuta la acción y la audita: éxito si el resultado es exitoso; salida = valor o <c>{"error":{codigo,mensaje,detalle}}</c>;
    /// si la acción lanza se audita como fallo (<c>{"error":{"codigo":"EXCEPCION"}}</c>) y la excepción se propaga.
    /// </summary>
    Task<clsResultado<TValor>> AuditarAsync<TValor>(string herramienta, object? entrada, string? threadId, Func<Task<clsResultado<TValor>>> accion);
}
