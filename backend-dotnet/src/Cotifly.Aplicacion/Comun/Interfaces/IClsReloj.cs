namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// Puerto de reloj de la aplicación. Los casos de uso nunca leen <c>DateTime.Now</c>:
/// así las reglas que dependen del tiempo (dedupe de correo, folio, expiración de tokens)
/// se prueban con un reloj falso.
/// </summary>
public interface IClsReloj
{
    /// <summary>Instante actual en UTC.</summary>
    DateTimeOffset AhoraUtc { get; }
}
