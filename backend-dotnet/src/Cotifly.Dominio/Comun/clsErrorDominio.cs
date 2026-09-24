namespace Cotifly.Dominio.Comun;

/// <summary>
/// Error de negocio con código estable (ej. <c>PRECIO_INVALIDO</c>, <c>MARGEN_LINEA_DESCONOCIDA</c>).
/// Hacia afuera se serializa como <c>{"error": {"codigo", "mensaje", "detalle?"}}</c>
/// (esquema ErrorNegocio del contrato #17).
/// </summary>
public sealed record clsErrorDominio
{
    public clsErrorDominio(string codigo, string mensaje, string? detalle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codigo);
        ArgumentException.ThrowIfNullOrWhiteSpace(mensaje);

        Codigo = codigo;
        Mensaje = mensaje;
        Detalle = detalle;
    }

    /// <summary>Código estable en MAYÚSCULAS_CON_GUION_BAJO que consumen el agente y el frontend.</summary>
    public string Codigo { get; }

    /// <summary>Mensaje legible para el usuario.</summary>
    public string Mensaje { get; }

    /// <summary>Contexto técnico opcional (ej. el motivo de una caída a política de línea).</summary>
    public string? Detalle { get; }
}
