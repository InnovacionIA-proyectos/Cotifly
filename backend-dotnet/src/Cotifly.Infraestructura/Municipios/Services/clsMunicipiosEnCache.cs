using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Municipios.Services;

/// <summary>
/// Foto en memoria (singleton) del catálogo de municipios de Nexo (HU-005.13) y la consulta en curso. Una sola por proceso: el
/// catálogo es el mismo para todos los comerciales (Nexo lo devuelve completo, sin filtrar por usuario) y no es sensible
/// (municipios y departamentos).
/// </summary>
public sealed class clsCacheCatalogoMunicipios
{
    private clsFoto? _foto;

    internal object Candado { get; } = new();

    /// <summary>La consulta a Nexo en curso, compartida por todos los que llegan mientras dura.</summary>
    internal Task<clsResultado<IReadOnlyList<clsMunicipio>>>? EnCurso { get; set; }

    internal clsFoto? Foto
    {
        get => Volatile.Read(ref _foto);
        set => Volatile.Write(ref _foto, value);
    }

    internal sealed record clsFoto(IReadOnlyList<clsMunicipio> Municipios, DateTimeOffset VenceAt);
}

/// <summary>
/// Decorador de <see cref="IClsMunicipiosProvider"/> que reutiliza el catálogo de Nexo durante <c>Nexo:MinutosCacheMunicipios</c>
/// (HU-005.13). <c>clsFabricaMunicipios</c> lo pone delante de <see cref="clsMunicipiosNexo"/> cuando Nexo está configurado y los
/// minutos son mayores que cero. Lo usan <c>GET /web/municipios</c> y la herramienta <c>resolver_municipio</c> del agente.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Qué se guarda:</b> solo un catálogo exitoso y no vacío. Un <c>MUNICIPIOS_NO_DISPONIBLE</c> o un arreglo vacío no se
/// guardan: la siguiente consulta vuelve a Nexo.</item>
/// <item><b>Una consulta a la vez:</b> quien llega mientras hay una consulta a Nexo en curso espera esa misma y recibe su mismo
/// resultado, sea éxito o fallo; no se encolan consultas una detrás de otra. La consulta compartida no usa la cancelación de
/// ningún llamador (la limita el tiempo total de 8 s del cliente de Nexo): si un llamador cancela, deja de esperar y los demás
/// siguen.</item>
/// <item><b>Sin caché negativa:</b> un fallo solo se comparte con los que esperaban esa consulta. No se guarda porque Nexo
/// responde el mismo código cuando el comercial que consulta no tiene <c>idUsuarioExterno</c>, y ese fallo no debe dejar sin
/// catálogo a los demás.</item>
/// <item><b>Excepciones:</b> una excepción inesperada de la consulta compartida se registra una vez en <c>Warning</c> y llega a
/// todos los que esperaban.</item>
/// <item><b>Identidad:</b> la consulta que va a Nexo usa al comercial que la disparó (exige su <c>idUsuarioExterno</c>); mientras
/// la foto esté vigente se sirve a cualquier comercial autenticado.</item>
/// </list>
/// </remarks>
public sealed class clsMunicipiosEnCache(
    IClsMunicipiosProvider interno,
    clsCacheCatalogoMunicipios cache,
    IClsReloj reloj,
    IOptions<clsOpcionesNexo> opciones,
    ILogger<clsMunicipiosEnCache> logger)
    : IClsMunicipiosProvider
{
    public Task<clsResultado<IReadOnlyList<clsMunicipio>>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (Vigente() is { } foto)
        {
            return Task.FromResult(clsResultado<IReadOnlyList<clsMunicipio>>.Exito(foto.Municipios));
        }

        Task<clsResultado<IReadOnlyList<clsMunicipio>>> tarea;
        lock (cache.Candado)
        {
            if (Vigente() is { } recargada)
            {
                return Task.FromResult(clsResultado<IReadOnlyList<clsMunicipio>>.Exito(recargada.Municipios));
            }

            tarea = cache.EnCurso ??= CargarAsync(usuarioId);
        }

        return tarea.WaitAsync(cancellationToken);
    }

    private async Task<clsResultado<IReadOnlyList<clsMunicipio>>> CargarAsync(Guid usuarioId)
    {
        // Sale del lock antes de consultar: la tarea se publica en EnCurso antes de que pueda terminar.
        await Task.Yield();
        try
        {
            var resultado = await interno.ObtenerAsync(usuarioId, CancellationToken.None);
            if (resultado.EsExitoso && resultado.Valor.Count > 0)
            {
                cache.Foto = new clsCacheCatalogoMunicipios.clsFoto(resultado.Valor, reloj.AhoraUtc.AddMinutes(opciones.Value.MinutosCacheMunicipios));
            }

            return resultado;
        }
        catch (ObjectDisposedException ex)
        {
            // El ámbito del comercial que disparó la consulta terminó antes (petición cancelada): para los demás es "no disponible".
            logger.LogWarning(ex, "La consulta compartida del catálogo de municipios se interrumpió (ámbito liberado).");
            return new clsErrorDominio(clsCodigosError.MunicipiosNoDisponible, clsMunicipiosNexo.Mensaje);
        }
        catch (Exception ex)
        {
            // Una sola vez, aunque ningún llamador siga esperando (todos cancelaron): la excepción no queda sin observar.
            logger.LogWarning(ex, "Falló la consulta compartida del catálogo de municipios de Nexo.");
            throw;
        }
        finally
        {
            lock (cache.Candado)
            {
                cache.EnCurso = null;
            }
        }
    }

    private clsCacheCatalogoMunicipios.clsFoto? Vigente() =>
        cache.Foto is { } foto && reloj.AhoraUtc < foto.VenceAt ? foto : null;
}
