using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Municipios.Services;

/// <summary>
/// Foto en memoria (singleton) del catálogo de municipios de Nexo (HU-005.13). Una sola por proceso: el catálogo es el mismo para
/// todos los comerciales (Nexo lo devuelve completo, sin filtrar por usuario) y no es sensible (municipios y departamentos).
/// </summary>
public sealed class clsCacheCatalogoMunicipios : IDisposable
{
    private clsFoto? _foto;

    /// <summary>Una sola recarga a la vez: los turnos simultáneos esperan la misma respuesta de Nexo.</summary>
    internal SemaphoreSlim Candado { get; } = new(1, 1);

    internal clsFoto? Foto
    {
        get => Volatile.Read(ref _foto);
        set => Volatile.Write(ref _foto, value);
    }

    public void Dispose() => Candado.Dispose();

    internal sealed record clsFoto(IReadOnlyList<clsMunicipio> Municipios, DateTimeOffset VenceAt);
}

/// <summary>
/// Decorador de <see cref="IClsMunicipiosProvider"/> que reutiliza el catálogo de Nexo durante <c>Nexo:MinutosCacheMunicipios</c>
/// (HU-005.13). Solo se guardan respuestas exitosas: un <c>MUNICIPIOS_NO_DISPONIBLE</c> no se cachea y la siguiente consulta vuelve
/// a Nexo. <c>clsFabricaMunicipios</c> lo pone delante de <see cref="clsMunicipiosNexo"/> cuando Nexo está configurado y los
/// minutos son mayores que cero. Lo usan <c>GET /web/municipios</c> y la herramienta <c>resolver_municipio</c> del agente.
/// </summary>
/// <remarks>
/// La primera consulta (o la que llega con la foto vencida) sí exige un comercial con <c>idUsuarioExterno</c>, porque va a Nexo;
/// mientras la foto esté vigente se sirve a cualquier comercial autenticado (el catálogo no depende del usuario).
/// </remarks>
public sealed class clsMunicipiosEnCache(
    IClsMunicipiosProvider interno, clsCacheCatalogoMunicipios cache, IClsReloj reloj, IOptions<clsOpcionesNexo> opciones)
    : IClsMunicipiosProvider
{
    public async Task<clsResultado<IReadOnlyList<clsMunicipio>>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (Vigente(cache.Foto) is { } foto)
        {
            return clsResultado<IReadOnlyList<clsMunicipio>>.Exito(foto.Municipios);
        }

        await cache.Candado.WaitAsync(cancellationToken);
        try
        {
            if (Vigente(cache.Foto) is { } recargada)
            {
                return clsResultado<IReadOnlyList<clsMunicipio>>.Exito(recargada.Municipios);
            }

            var resultado = await interno.ObtenerAsync(usuarioId, cancellationToken);
            if (resultado.EsExitoso)
            {
                cache.Foto = new clsCacheCatalogoMunicipios.clsFoto(resultado.Valor, reloj.AhoraUtc.AddMinutes(opciones.Value.MinutosCacheMunicipios));
            }

            return resultado;
        }
        finally
        {
            cache.Candado.Release();
        }
    }

    private clsCacheCatalogoMunicipios.clsFoto? Vigente(clsCacheCatalogoMunicipios.clsFoto? foto) =>
        foto is not null && reloj.AhoraUtc < foto.VenceAt ? foto : null;
}
