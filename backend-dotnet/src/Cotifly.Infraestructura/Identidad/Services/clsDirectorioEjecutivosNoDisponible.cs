using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Identidad.Services;

/// <summary>
/// <see cref="IClsDirectorioEjecutivos"/> sin conexión (<c>ConnectionStrings:Regional</c> vacía): no hay fuente viva y la
/// regional sale de <c>UsuariosWeb.regional</c> (ADR-016 §6).
/// </summary>
public sealed class clsDirectorioEjecutivosNoDisponible : IClsDirectorioEjecutivos
{
    public bool Configurado => false;

    /// <summary>Sin fuente no hay consulta que limitar.</summary>
    public TimeSpan LimiteLogin => TimeSpan.Zero;

    public Task<IReadOnlyDictionary<string, string>> ObtenerRegionalesAsync(
        IReadOnlyCollection<string> nombresUsuario, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public Task<IReadOnlyList<string>> ListarNombresUsuarioAsync(string regional, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>
/// Elige el directorio de ejecutivos según <c>ConnectionStrings:Regional</c>: con valor → <see cref="clsDirectorioEjecutivosDimEjecutivo"/>;
/// vacía → <see cref="clsDirectorioEjecutivosNoDisponible"/>. Mismo patrón que <c>clsFabricaBuscadorClientes</c>.
/// </summary>
internal static class clsFabricaDirectorioEjecutivos
{
    public static IClsDirectorioEjecutivos Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        var configurada = !string.IsNullOrWhiteSpace(servicios.GetRequiredService<IOptions<clsOpcionesConexiones>>().Value.Regional);
        return configurada
            ? servicios.GetRequiredService<clsDirectorioEjecutivosDimEjecutivo>()
            : servicios.GetRequiredService<clsDirectorioEjecutivosNoDisponible>();
    }
}
