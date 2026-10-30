using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Agente.DTOs;

/// <summary>
/// Lo que devuelve una herramienta del asistente (<c>KernelFunction</c> del plugin <c>asistente</c>, ADR-003 y ADR-018). Separa lo
/// que ve el modelo (<see cref="Salida"/>, sin montos de dinero, o el error estructurado) de los datos tipados del turno que nunca
/// van al modelo (<see cref="Detalle"/>).
/// </summary>
public sealed record clsResultadoHerramienta
{
    /// <summary>Nombre de la herramienta (<c>buscar_cliente</c>, <c>obtener_margen</c>...).</summary>
    public required string Herramienta { get; init; }

    /// <summary>Salida del contrato hacia el modelo si terminó bien (por ejemplo la consulta de la cotización o la propuesta).</summary>
    public object? Salida { get; init; }

    /// <summary>Error de negocio (<c>{"error": {codigo, mensaje, detalle?}}</c> hacia el modelo), o <c>null</c> si terminó bien.</summary>
    public clsErrorDominio? Error { get; init; }

    /// <summary>Datos tipados del turno (por defecto, la misma salida); nunca van al modelo.</summary>
    public object? Detalle { get; init; }

    /// <summary>
    /// True si la herramienta llegó a llamar a un caso de uso que se audita solo (<c>generar_pdf</c>, <c>enviar_correo</c>,
    /// <c>registrar_ajuste_margen</c>): el filtro de auditoría no vuelve a registrar el evento. Un rechazo del orquestador antes de
    /// llegar al caso de uso (confirmación pendiente, payload ilegible) sí lo audita el filtro.
    /// </summary>
    public bool AuditadoPorCasoDeUso { get; init; }

    public bool EsExitoso => Error is null;

    public static clsResultadoHerramienta Exito(string herramienta, object salida, object? detalle = null, bool auditadoPorCasoDeUso = false) =>
        new() { Herramienta = herramienta, Salida = salida, Detalle = detalle ?? salida, AuditadoPorCasoDeUso = auditadoPorCasoDeUso };

    public static clsResultadoHerramienta Fallo(string herramienta, clsErrorDominio error, bool auditadoPorCasoDeUso = false) =>
        new() { Herramienta = herramienta, Error = error, AuditadoPorCasoDeUso = auditadoPorCasoDeUso };

    public static clsResultadoHerramienta Fallo(string herramienta, string codigo, string mensaje, string? detalle = null) =>
        Fallo(herramienta, new clsErrorDominio(codigo, mensaje, detalle));

    /// <summary>
    /// Lo que recibe el modelo como resultado de la herramienta: la salida del contrato o el error estructurado
    /// <c>{"error": {"codigo", "mensaje", "detalle": {"motivo"}}}</c> (mismo contrato de error que el Python y que <c>ErrorNegocio</c>).
    /// </summary>
    public object ParaModelo()
    {
        if (Error is null)
        {
            return Salida ?? new Dictionary<string, object?>();
        }

        var cuerpo = new Dictionary<string, object?> { ["codigo"] = Error.Codigo, ["mensaje"] = Error.Mensaje };
        if (!string.IsNullOrWhiteSpace(Error.Detalle))
        {
            cuerpo["detalle"] = new Dictionary<string, object?> { ["motivo"] = Error.Detalle };
        }

        return new Dictionary<string, object?> { ["error"] = cuerpo };
    }
}
