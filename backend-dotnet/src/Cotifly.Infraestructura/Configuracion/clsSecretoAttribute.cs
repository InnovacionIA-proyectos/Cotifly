namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Marca una propiedad <see cref="string"/> de una clase de opciones como secreto.
/// En Production, <see cref="clsValidadorSecretosOpciones{TOpciones}"/> impide arrancar si el valor
/// está vacío, es un placeholder o es más corto que <see cref="LargoMinimo"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class clsSecretoAttribute : Attribute
{
    /// <summary>Largo mínimo aceptado en Production. Por defecto 16 caracteres.</summary>
    public int LargoMinimo { get; init; } = 16;
}
