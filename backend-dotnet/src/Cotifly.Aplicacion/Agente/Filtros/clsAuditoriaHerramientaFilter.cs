using System.Diagnostics;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Microsoft.SemanticKernel;

namespace Cotifly.Aplicacion.Agente.Filtros;

/// <summary>
/// Filtro de auditoría de las herramientas del agente (port del middleware de <c>mcp_server/auditoria.py</c>): cada invocación
/// queda en <c>EventosHerramienta</c> con la entrada (ya con lo que inyectó el orquestador), la salida que vio el modelo, el éxito,
/// la duración y el hilo.
/// </summary>
/// <remarks>
/// No vuelve a auditar lo que audita su propio caso de uso (<see cref="clsAuditoriaService.HerramientasAuditadasPorCasoDeUso"/>:
/// <c>generar_pdf</c>, <c>enviar_correo</c>, <c>registrar_ajuste_margen</c>) cuando la herramienta llegó a llamarlo
/// (<see cref="clsResultadoHerramienta.AuditadoPorCasoDeUso"/>); un rechazo del orquestador antes del caso de uso sí se audita aquí.
/// Se registra antes que <c>clsInyeccionEstadoFilter</c> (queda por fuera), así ve también los rechazos de ese filtro.
/// </remarks>
public sealed class clsAuditoriaHerramientaFilter(IClsAuditoriaService auditoria, clsContextoTurnoAgente contexto) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var herramienta = context.Function.Name;
        var reloj = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await auditoria.RegistrarAsync(
                herramienta,
                Entrada(context.Arguments),
                new { error = new { codigo = "EXCEPCION", mensaje = ex.GetType().Name } },
                false,
                (int)reloj.ElapsedMilliseconds,
                contexto.ThreadId);
            throw;
        }

        if (context.Result.GetValue<object>() is not clsResultadoHerramienta resultado)
        {
            return;
        }

        if (resultado.AuditadoPorCasoDeUso && clsAuditoriaService.HerramientasAuditadasPorCasoDeUso.Contains(herramienta))
        {
            return;
        }

        await auditoria.RegistrarAsync(
            herramienta,
            Entrada(context.Arguments),
            resultado.ParaModelo(),
            resultado.EsExitoso,
            (int)reloj.ElapsedMilliseconds,
            contexto.ThreadId);
    }

    /// <summary>Argumentos como diccionario serializable (los <see cref="JsonElement"/> se escriben tal cual).</summary>
    private static Dictionary<string, object?> Entrada(KernelArguments argumentos) =>
        argumentos.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
}
