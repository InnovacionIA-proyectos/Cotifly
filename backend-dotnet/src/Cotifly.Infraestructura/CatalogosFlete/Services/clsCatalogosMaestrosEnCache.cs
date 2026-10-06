using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// Foto en memoria (singleton) de los catálogos maestros de Nexo y la consulta en curso (HU-013.10). Una sola por proceso: los
/// catálogos son los mismos para todos los comerciales y no son sensibles.
/// </summary>
public sealed class clsCacheCatalogosMaestros
{
    private clsFoto? _foto;

    internal object Candado { get; } = new();

    /// <summary>La consulta a Nexo en curso, compartida por todos los que llegan mientras dura.</summary>
    internal Task<clsResultado<clsCatalogosMaestros>>? EnCurso { get; set; }

    internal clsFoto? Foto
    {
        get => Volatile.Read(ref _foto);
        set => Volatile.Write(ref _foto, value);
    }

    /// <param name="Catalogos">Los catálogos de la última consulta exitosa.</param>
    /// <param name="ObtenidaAt">Cuándo se obtuvieron (UTC).</param>
    internal sealed record clsFoto(clsCatalogosMaestros Catalogos, DateTimeOffset ObtenidaAt);
}

/// <summary>
/// Decorador de <see cref="IClsCatalogosMaestrosProvider"/> con caché (HU-013.10, G1 y G2). <c>clsFabricaCatalogosMaestros</c> lo
/// pone delante de <see cref="clsCatalogosMaestrosNexo"/> cuando Nexo está configurado.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Vigente</b> (<c>Nexo:MinutosCacheCatalogos</c>, 30 por defecto): se sirve la foto sin ir a Nexo.</item>
/// <item><b>Vencida</b>: se consulta Nexo. Si Nexo falla y la foto tiene menos de <c>Nexo:HorasCatalogosVencidos</c> (24 por
/// defecto), se sirve la foto vencida y se registra un warning: los catálogos maestros cambian poco y un código que era válido hace
/// unas horas lo sigue siendo. Pasado ese límite, o sin foto, responde <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> (nunca se ofrece ni se
/// envía texto libre: ver ADR-018 y <c>docs/HU-013-preguntas-nexo.md</c> G2).</item>
/// <item><b>Una consulta a la vez</b> y <b>sin caché negativa</b>, igual que <c>clsMunicipiosEnCache</c>: un fallo por un comercial
/// sin <c>idUsuarioExterno</c> no deja sin catálogos a los demás.</item>
/// <item><b>Solo fotos completas:</b> se guarda una respuesta exitosa con tipos de vehículo y clases de viaje no vacíos.</item>
/// </list>
/// </remarks>
public sealed class clsCatalogosMaestrosEnCache(
    IClsCatalogosMaestrosProvider interno,
    clsCacheCatalogosMaestros cache,
    IClsReloj reloj,
    IOptions<clsOpcionesNexo> opciones,
    ILogger<clsCatalogosMaestrosEnCache> logger)
    : IClsCatalogosMaestrosProvider
{
    public clsCatalogosMaestros? UltimaCopia => cache.Foto?.Catalogos;

    public Task<clsResultado<clsCatalogosMaestros>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (Vigente() is { } foto)
        {
            return Task.FromResult(clsResultado<clsCatalogosMaestros>.Exito(foto.Catalogos));
        }

        Task<clsResultado<clsCatalogosMaestros>> tarea;
        lock (cache.Candado)
        {
            if (Vigente() is { } recargada)
            {
                return Task.FromResult(clsResultado<clsCatalogosMaestros>.Exito(recargada.Catalogos));
            }

            tarea = cache.EnCurso ??= CargarAsync(usuarioId);
        }

        return tarea.WaitAsync(cancellationToken);
    }

    private async Task<clsResultado<clsCatalogosMaestros>> CargarAsync(Guid usuarioId)
    {
        // Sale del lock antes de consultar: la tarea se publica en EnCurso antes de que pueda terminar.
        await Task.Yield();
        try
        {
            var resultado = await interno.ObtenerAsync(usuarioId, CancellationToken.None);
            if (resultado.EsExitoso && resultado.Valor.TiposVehiculo.Count > 0 && resultado.Valor.ClasesViaje.Count > 0)
            {
                cache.Foto = new clsCacheCatalogosMaestros.clsFoto(resultado.Valor, reloj.AhoraUtc);
                return resultado;
            }

            return Respaldo() ?? (resultado.EsFallo
                ? resultado
                : new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, clsCatalogosMaestrosNexo.Mensaje));
        }
        catch (ObjectDisposedException ex)
        {
            // El ámbito del comercial que disparó la consulta terminó antes (petición cancelada): para los demás es "no disponible".
            logger.LogWarning(ex, "La consulta compartida de los catálogos maestros se interrumpió (ámbito liberado).");
            return Respaldo() ?? new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, clsCatalogosMaestrosNexo.Mensaje);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falló la consulta compartida de los catálogos maestros de Nexo.");
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

    /// <summary>La foto vencida si todavía no supera <c>Nexo:HorasCatalogosVencidos</c>; <c>null</c> si no hay o es muy vieja.</summary>
    private clsResultado<clsCatalogosMaestros>? Respaldo()
    {
        if (cache.Foto is not { } foto || reloj.AhoraUtc - foto.ObtenidaAt >= TimeSpan.FromHours(opciones.Value.HorasCatalogosVencidos))
        {
            return null;
        }

        logger.LogWarning("Nexo no entregó los catálogos maestros: se usa la copia del {ObtenidaAt:O}.", foto.ObtenidaAt);
        return foto.Catalogos;
    }

    private clsCacheCatalogosMaestros.clsFoto? Vigente() =>
        cache.Foto is { } foto && reloj.AhoraUtc < foto.ObtenidaAt.AddMinutes(opciones.Value.MinutosCacheCatalogos) ? foto : null;
}
