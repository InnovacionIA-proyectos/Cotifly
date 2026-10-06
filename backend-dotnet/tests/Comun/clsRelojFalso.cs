using Cotifly.Aplicacion.Comun.Interfaces;

namespace Cotifly.Pruebas.Comun;

/// <summary>Reloj controlable para probar reglas que dependen del tiempo (dedupe de correo, updatedAt).</summary>
public sealed class clsRelojFalso(DateTimeOffset inicio) : IClsReloj
{
    public clsRelojFalso()
        : this(new DateTimeOffset(2026, 9, 24, 13, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset AhoraUtc { get; set; } = inicio.ToUniversalTime();

    public void Avanzar(TimeSpan lapso) => AhoraUtc += lapso;
}
