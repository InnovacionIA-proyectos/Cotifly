using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Auditoria.Services;

/// <summary>
/// Implementación de <see cref="IClsAuditoriaService"/>. Singleton: cada evento abre un ámbito de DI propio (repositorio y
/// unidad de trabajo nuevos), así no arrastra ni confirma cambios pendientes del caso de uso auditado y un fallo de la base
/// no afecta su transacción.
/// </summary>
public sealed class clsAuditoriaService(IServiceScopeFactory ambitos, ILogger<clsAuditoriaService> logger) : IClsAuditoriaService
{
    /// <summary>Límite del JSON auditado (<c>_MAX_RESUMEN_CHARS</c> del Python).</summary>
    public const int MaxCaracteres = 4000;

    public const string HerramientaGenerarPdf = "generar_pdf";
    public const string HerramientaEnviarCorreo = "enviar_correo";
    public const string HerramientaAjusteMargen = "registrar_ajuste_margen";

    /// <summary>Herramientas que audita su propio caso de uso: el filtro del agente no debe auditarlas otra vez.</summary>
    public static readonly IReadOnlySet<string> HerramientasAuditadasPorCasoDeUso =
        new HashSet<string>(StringComparer.Ordinal) { HerramientaGenerarPdf, HerramientaEnviarCorreo, HerramientaAjusteMargen };

    public async Task RegistrarAsync(string herramienta, object? entrada, object? salida, bool exito, int duracionMs, string? threadId)
    {
        try
        {
            var evento = clsEventoHerramienta.Registrar(
                herramienta,
                entrada is null ? null : AJson(entrada),
                ResumenSalida(salida),
                exito,
                Math.Max(0, duracionMs),
                threadId);
            if (evento.EsFallo)
            {
                logger.LogWarning("Evento de auditoría inválido para '{Herramienta}': {Motivo}", herramienta, evento.Error.Mensaje);
                return;
            }

            await using var ambito = ambitos.CreateAsyncScope();
            ambito.ServiceProvider.GetRequiredService<IClsAuditoriaRepository>().Registrar(evento.Valor);
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // La auditoría nunca interrumpe al llamador (igual que _persistir del Python).
            logger.LogWarning("No se pudo registrar la auditoría de '{Herramienta}' ({Tipo}): {Mensaje}", herramienta, ex.GetType().Name, ex.Message);
        }
    }

    public async Task<clsResultado<TValor>> AuditarAsync<TValor>(string herramienta, object? entrada, string? threadId, Func<Task<clsResultado<TValor>>> accion)
    {
        ArgumentNullException.ThrowIfNull(accion);
        var inicio = Stopwatch.GetTimestamp();
        var exito = false;
        object? salida = new { error = new { codigo = "EXCEPCION" } };
        try
        {
            var resultado = await accion();
            exito = resultado.EsExitoso;
            salida = resultado.Coincidir<object>(
                valor => valor!,
                error => new { error = new { codigo = error.Codigo, mensaje = error.Mensaje, detalle = error.Detalle } });
            return resultado;
        }
        finally
        {
            var duracion = (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
            await RegistrarAsync(herramienta, entrada, salida, exito, duracion, threadId);
        }
    }

    /// <summary>
    /// JSON del valor en snake_case; si supera <see cref="MaxCaracteres"/> se guarda <c>{"truncado":true,"preview":...}</c>
    /// (el recorte a mano dejaría un JSON inválido y el CHECK <c>ISJSON</c> lo rechazaría). Si no se puede serializar,
    /// <c>{"repr": ...}</c> (<c>_jsonable</c> del Python).
    /// </summary>
    public static string AJson(object valor)
    {
        ArgumentNullException.ThrowIfNull(valor);
        string texto;
        try
        {
            texto = JsonSerializer.Serialize(valor, valor.GetType(), clsJsonAplicacion.SnakeCase);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return JsonSerializer.Serialize(new { repr = Recortar(valor.ToString() ?? string.Empty) }, clsJsonAplicacion.SnakeCase);
        }

        return texto.Length <= MaxCaracteres
            ? texto
            : JsonSerializer.Serialize(new { truncado = true, preview = texto[..MaxCaracteres] }, clsJsonAplicacion.SnakeCase);
    }

    /// <summary>El resumen siempre es un objeto JSON: un valor que no lo es se envuelve en <c>{"valor": ...}</c>.</summary>
    private static string? ResumenSalida(object? salida)
    {
        if (salida is null)
        {
            return null;
        }

        var json = AJson(salida);
        return JsonNode.Parse(json) is JsonObject
            ? json
            : AJson(new { valor = JsonNode.Parse(json) });
    }

    private static string Recortar(string texto) => texto.Length <= MaxCaracteres ? texto : texto[..MaxCaracteres];
}
