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
/// recortadas y sin distinguir mayúsculas. También decide el alcance por rol (director: todas o la elegida; gerente:
/// la suya), para que el tablero, el listado regional, el detalle y el PDF ajenos no repitan esa regla.
/// </summary>
public sealed class clsRegionalUsuarioService(
    IClsDirectorioEjecutivos directorio,
    IClsUsuarioWebRepository usuarios,
    ILogger<clsRegionalUsuarioService> logger) : IClsRegionalUsuario
{
    public const string MensajeNoAsignada = "Tu usuario no tiene una regional asignada. Pide al administrador que la registre.";
    public const string MensajeNoDisponible = "No se pudo consultar la regional en este momento. Intenta de nuevo en unos minutos.";
    public const string MensajeNoPermitida = "Solo puedes consultar tu propia regional.";
    public const string MensajeSinPermiso = "No tienes permiso para esta operación.";

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

    public async Task<clsResultado<bool>> EsMiembroDeMiRegionalAsync(clsUsuarioWeb gerente, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var regional = await ResolverAsync(gerente, cancellationToken);
        if (regional.EsFallo)
        {
            return regional.Error;
        }

        var miembros = await ListarMiembrosAsync(regional.Valor.Regional, cancellationToken);
        return miembros.EsFallo ? miembros.Error : miembros.Valor.Any(m => m.Id == usuarioId);
    }

    public Task<clsResultado<clsAlcanceRegional>> ResolverAlcanceAsync(
        clsUsuarioWeb usuario, RolInterno rol, string? regional = null, CancellationToken cancellationToken = default) =>
        ComponerAlcanceAsync(this, usuarios, usuario, rol, regional, cancellationToken);

    public Task<clsResultado<bool>> PuedeVerDeAsync(clsUsuarioWeb usuario, RolInterno rol, Guid duenoId, CancellationToken cancellationToken = default) =>
        ComponerPermisoAsync(this, usuario, rol, duenoId, cancellationToken);

    /// <summary>
    /// La regla del alcance por rol (<see cref="IClsRegionalUsuario.ResolverAlcanceAsync"/>) sobre las operaciones básicas de
    /// <paramref name="regionales"/> (<see cref="IClsRegionalUsuario.ResolverAsync"/> y <see cref="IClsRegionalUsuario.ListarMiembrosAsync"/>).
    /// Es pública para que las pruebas de los consumidores apliquen la misma regla sobre un puerto simulado, sin copiarla.
    /// </summary>
    public static async Task<clsResultado<clsAlcanceRegional>> ComponerAlcanceAsync(
        IClsRegionalUsuario regionales, IClsUsuarioWebRepository repositorio, clsUsuarioWeb usuario, RolInterno rol, string? regional,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regionales);
        ArgumentNullException.ThrowIfNull(repositorio);
        ArgumentNullException.ThrowIfNull(usuario);
        var pedida = Normalizar(regional);
        if (rol == RolInterno.Director)
        {
            if (pedida is null)
            {
                return new clsAlcanceRegional(null, null, await repositorio.ListarTodosAsync(cancellationToken));
            }

            var deLaPedida = await regionales.ListarMiembrosAsync(pedida, cancellationToken);
            return deLaPedida.EsFallo ? deLaPedida.Error : new clsAlcanceRegional(pedida, null, deLaPedida.Valor);
        }

        if (rol != RolInterno.GerenteComercial)
        {
            return ErrorSinPermiso();
        }

        var propia = await regionales.ResolverAsync(usuario, cancellationToken);
        if (propia.EsFallo)
        {
            return propia.Error;
        }

        if (pedida is not null && !Igual(pedida, propia.Valor.Regional))
        {
            return new clsErrorDominio(clsCodigosAplicacion.RegionalNoPermitida, MensajeNoPermitida);
        }

        var miembros = await regionales.ListarMiembrosAsync(propia.Valor.Regional, cancellationToken);
        return miembros.EsFallo ? miembros.Error : new clsAlcanceRegional(propia.Valor.Regional, propia.Valor.Fuente, miembros.Valor);
    }

    /// <summary>
    /// La regla de <see cref="IClsRegionalUsuario.PuedeVerDeAsync"/> sobre <see cref="IClsRegionalUsuario.EsMiembroDeMiRegionalAsync"/> de
    /// <paramref name="regionales"/> (pública por la misma razón que <see cref="ComponerAlcanceAsync"/>).
    /// </summary>
    public static async Task<clsResultado<bool>> ComponerPermisoAsync(
        IClsRegionalUsuario regionales, clsUsuarioWeb usuario, RolInterno rol, Guid duenoId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regionales);
        ArgumentNullException.ThrowIfNull(usuario);
        if (rol == RolInterno.Director)
        {
            return clsResultado<bool>.Exito(true);
        }

        return rol == RolInterno.GerenteComercial
            ? await regionales.EsMiembroDeMiRegionalAsync(usuario, duenoId, cancellationToken)
            : clsResultado<bool>.Exito(false);
    }

    public async Task<clsResultado<IReadOnlyList<string>>> ListarRegionalesAsync(clsUsuarioWeb usuario, RolInterno rol, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (rol == RolInterno.GerenteComercial)
        {
            var propia = await ResolverAsync(usuario, cancellationToken);
            return propia.EsFallo ? propia.Error : clsResultado<IReadOnlyList<string>>.Exito([propia.Valor.Regional]);
        }

        if (rol != RolInterno.Director)
        {
            return ErrorSinPermiso();
        }

        var todos = await usuarios.ListarTodosAsync(cancellationToken);
        IReadOnlyDictionary<string, string> mapa = new Dictionary<string, string>();
        var nombres = todos.Select(u => u.NombreUsuario).OfType<string>().ToList();
        if (directorio.Configurado && nombres.Count > 0)
        {
            var consultado = await ConsultarAsync(() => directorio.ObtenerRegionalesAsync(nombres, cancellationToken), cancellationToken);
            if (consultado is null)
            {
                return ErrorNoDisponible();
            }

            mapa = consultado;
        }

        IReadOnlyList<string> regionales = [.. todos
            .Select(u => Normalizar(u.NombreUsuario is null ? null : Buscar(mapa, u.NombreUsuario)) ?? Normalizar(u.Regional))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        return clsResultado<IReadOnlyList<string>>.Exito(regionales);
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

    private static clsErrorDominio ErrorSinPermiso() => new(clsCodigosAplicacion.SinPermiso, MensajeSinPermiso);

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
