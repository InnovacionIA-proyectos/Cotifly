using System.Collections.Concurrent;
using Cotifly.Aplicacion.Comun.Interfaces;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// Caché corta (<see cref="Vigencia"/>) de las respuestas exitosas de la tabla de ejecutivos (ADR-016 §6): cada cambio del
/// selector de comercial en el tablero no vuelve a consultar <c>DimEjecutivo</c>. Guarda también los "no aparece" de un usuario.
/// Los fallos nunca se guardan (el adaptador lanza antes de escribir aquí). Claves sin distinguir mayúsculas. Singleton.
/// </summary>
public sealed class clsCacheDirectorioEjecutivos(IClsReloj reloj)
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, (string? Regional, DateTimeOffset Expira)> _usuarios = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Nombres, DateTimeOffset Expira)> _regionales = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>true</c> si hay entrada vigente para <paramref name="nombreUsuario"/>; <paramref name="regional"/> es <c>null</c> si el
    /// usuario no aparece (o aparece con dos regionales) en la tabla.
    /// </summary>
    public bool IntentarUsuario(string nombreUsuario, out string? regional)
    {
        regional = null;
        if (_usuarios.TryGetValue(nombreUsuario, out var entrada) && entrada.Expira > reloj.AhoraUtc)
        {
            regional = entrada.Regional;
            return true;
        }

        return false;
    }

    public void GuardarUsuario(string nombreUsuario, string? regional) => _usuarios[nombreUsuario] = (regional, reloj.AhoraUtc + Vigencia);

    public bool IntentarRegional(string regional, out IReadOnlyList<string> nombres)
    {
        nombres = [];
        if (_regionales.TryGetValue(regional, out var entrada) && entrada.Expira > reloj.AhoraUtc)
        {
            nombres = entrada.Nombres;
            return true;
        }

        return false;
    }

    public void GuardarRegional(string regional, IReadOnlyList<string> nombres) => _regionales[regional] = (nombres, reloj.AhoraUtc + Vigencia);
}
