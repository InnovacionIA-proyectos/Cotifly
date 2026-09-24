using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Auditoria;

/// <summary>
/// Auditoría de una invocación de herramienta del agente (<c>dbo_cotifly.EventosHerramienta</c>).
/// <see cref="Input"/> y <see cref="OutputResumen"/> son JSON (CHECK <c>ISJSON</c>).
/// </summary>
public sealed class clsEventoHerramienta
{
    public const string CodigoError = "EVENTO_HERRAMIENTA_INVALIDO";

    private clsEventoHerramienta()
    {
    }

    public Guid Id { get; private set; }
    public string Herramienta { get; private set; } = string.Empty;
    public string? Input { get; private set; }
    public string? OutputResumen { get; private set; }
    public bool? Exito { get; private set; }
    public int? DuracionMs { get; private set; }
    public string? ThreadId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static clsResultado<clsEventoHerramienta> Registrar(
        string herramienta,
        string? inputJson,
        string? outputResumenJson,
        bool exito,
        int duracionMs,
        string? threadId = null)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "herramienta", herramienta),
            duracionMs < 0 ? new clsErrorDominio(CodigoError, "La duración no puede ser negativa.") : null);

        if (error is not null)
        {
            return error;
        }

        return new clsEventoHerramienta
        {
            Id = Guid.NewGuid(),
            Herramienta = herramienta.Trim(),
            Input = clsValidacion.Normalizar(inputJson),
            OutputResumen = clsValidacion.Normalizar(outputResumenJson),
            Exito = exito,
            DuracionMs = duracionMs,
            ThreadId = clsValidacion.Normalizar(threadId),
        };
    }
}
