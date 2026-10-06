using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>
/// Implementación de <see cref="IClsRegionalUsuario"/>: consulta <see cref="IClsDirectorioEjecutivos"/> (fuente viva) y cae a
/// <c>UsuariosWeb.regional</c> para los usuarios que no aparecen en ella. Si la fuente viva está configurada y falla, responde
/// <see cref="clsCodigosAplicacion.RegionalNoDisponible"/>: no mezcla las dos fuentes a medias. Las regionales se comparan
/// recortadas y sin distinguir mayúsculas.
/// </summary>
public sealed class clsRegionalUsuarioService(
    IClsDirectorioEjecutivos directorio,
    IClsUsuarioWebRepository usuarios,
    ILogger<clsRegionalUsuarioService> logger) : IClsRegionalUsuario
{
    public const string MensajeNoAsignada = "Tu usuario no tiene una regional asignada. Pide al administrador que la registre.";
    public const string MensajeNoDisponible = "No se pudo consultar la regional en este momento. Intenta de nuevo en unos minutos.";

    public async Task<clsResultado<clsRegionalResuelta>> ResolverAsync(clsUsuarioWeb usuario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);

        if (directorio.Configurado && usuario.NombreUsuario is { } nombre)
        {
            var mapa = await ConsultarAsync(() => directorio.ObtenerRegionalesAsync([nombre], cancellationToken), cancellationToken);
            if (mapa is null)
            {
                return ErrorNoDisponible();
            }

            if (Normalizar(Buscar(mapa, nombre)) is { } deDirectorio)
            {
                return new clsRegionalResuelta(deDirectorio, clsRegionalResuelta.FuenteDimEjecutivo);
            }
        }

        return Normalizar(usuario.Regional) is { } guardada
            ? new clsRegionalResuelta(guardada, clsRegionalResuelta.FuenteUsuariosWeb)
            : new clsErrorDominio(clsCodigosAplicacion.RegionalNoAsignada, MensajeNoAsignada);
    }

    public async Task<clsResultado<IReadOnlyList<clsUsuarioWeb>>> ListarMiembrosAsync(string regional, CancellationToken cancellationToken = default)
    {
        var buscada = Normalizar(regional) ?? throw new ArgumentException("La regional es obligatoria.", nameof(regional));

        IReadOnlyList<string> delDirectorio = [];
        if (directorio.Configurado)
        {
            var nombres = await ConsultarAsync(() => directorio.ListarNombresUsuarioAsync(buscada, cancellationToken), cancellationToken);
            if (nombres is null)
            {
                return ErrorNoDisponible();
            }

            delDirectorio = nombres;
        }

        var candidatos = await usuarios.ListarPorRegionalONombresAsync(buscada, delDirectorio, cancellationToken);
        if (!directorio.Configurado || candidatos.Count == 0)
        {
            return clsResultado<IReadOnlyList<clsUsuarioWeb>>.Exito(candidatos);
        }

        // Un candidato por UsuariosWeb.regional puede tener otra regional en la fuente viva, que manda.
        var nombresCandidatos = candidatos.Select(u => u.NombreUsuario).OfType<string>().ToList();
        var mapa = await ConsultarAsync(() => directorio.ObtenerRegionalesAsync(nombresCandidatos, cancellationToken), cancellationToken);
        if (mapa is null)
        {
            return ErrorNoDisponible();
        }

        IReadOnlyList<clsUsuarioWeb> miembros = [.. candidatos.Where(u =>
            Igual(Normalizar(u.NombreUsuario is null ? null : Buscar(mapa, u.NombreUsuario)) ?? Normalizar(u.Regional), buscada))];
        return clsResultado<IReadOnlyList<clsUsuarioWeb>>.Exito(miembros);
    }

    private async Task<T?> ConsultarAsync<T>(Func<Task<T>> consulta, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await consulta();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "No se pudo consultar la regional en el directorio de ejecutivos.");
            return null;
        }
    }

    private static clsErrorDominio ErrorNoDisponible() => new(clsCodigosAplicacion.RegionalNoDisponible, MensajeNoDisponible);

    private static string? Buscar(IReadOnlyDictionary<string, string> mapa, string nombre)
    {
        var clave = nombre.Trim();
        return mapa.TryGetValue(clave, out var valor)
            ? valor
            : mapa.FirstOrDefault(p => string.Equals(p.Key, clave, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string? Normalizar(string? regional) => string.IsNullOrWhiteSpace(regional) ? null : regional.Trim();

    private static bool Igual(string? una, string otra) => string.Equals(una, otra, StringComparison.OrdinalIgnoreCase);
}
