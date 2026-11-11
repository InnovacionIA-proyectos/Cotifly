using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// #173: categoría de cada conexo de la cotización según el catálogo de Nexo (<c>logistico</c>, <c>adicional</c>...). HU-014 (#188): desde el
/// script 05 la categoría se guarda con el conexo (<c>categoriaServicioConexo</c>) y el costeo usa esa; este servicio solo resuelve los
/// conexos SIN categoría guardada (filas anteriores a #188), por el id del catálogo si la fila lo tiene (script 03) o por el guardado y el
/// nombre. Nunca falla ni espera a Nexo: responde con el índice en memoria; si está frío o le falta un
/// conexo, lo calienta EN SEGUNDO PLANO (la respuesta sale con la categoría de respaldo
/// <see cref="clsCosteoCotizacionService.CategoriaConexo"/> y la siguiente ya trae la del catálogo). Así un GET o un PUT del costeo nunca
/// paga las hasta 20 páginas del catálogo de Nexo.
/// </summary>
public interface IClsCategoriasConexos
{
    /// <summary>Categoría por id guardado (<c>idServicioConexo</c>) de los conexos de <paramref name="cotizacion"/> que se pudieron resolver.</summary>
    Task<IReadOnlyDictionary<string, string>> CategoriasAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Corrección conexos (nombre nunca id): nombre del catálogo de Nexo por id guardado (<c>idServicioConexo</c>) de los conexos de
    /// <paramref name="cotizacion"/> que NO tienen el nombre guardado (filas migradas del JSON). Mismo mecanismo que
    /// <see cref="CategoriasAsync"/>: responde con el índice en memoria y lo calienta en segundo plano si está frío. Las superficies de
    /// usuario (asistente de IA, vista web del costeo) usan este nombre como respaldo cuando el guardado es <c>null</c>, antes de caer a la
    /// categoría; nunca muestran el id interno.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> NombresAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);
}

/// <summary>
/// Índice en memoria (singleton) del catálogo de conexos de Nexo: categoría por id y por nombre. Vale <see cref="Vigencia"/>; después de
/// un intento fallido no se vuelve a leer el catálogo durante <see cref="EsperaTrasFallo"/> (un GET del costeo no espera a Nexo caído
/// en cada petición).
/// </summary>
public sealed class clsAlmacenCategoriasConexos
{
    /// <summary>Vigencia del índice leído.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(30);

    /// <summary>Espera antes de volver a leer el catálogo después de un fallo o de un conexo que no estaba.</summary>
    public static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromMinutes(1);

    private readonly object _candado = new();
    private clsIndiceCategorias? _indice;
    private DateTimeOffset? _ultimoIntento;

    /// <summary>La última lectura del catálogo en segundo plano (o una tarea completada): las pruebas la esperan.</summary>
    public Task UltimaLectura { get; set; } = Task.CompletedTask;

    /// <summary>El índice vigente, o <c>null</c>.</summary>
    public clsIndiceCategorias? Vigente(DateTimeOffset ahora)
    {
        lock (_candado)
        {
            return _indice is { } i && ahora - i.LeidoAt < Vigencia ? i : null;
        }
    }

    /// <summary>El último índice leído aunque esté vencido (respaldo si el catálogo no responde), o <c>null</c>.</summary>
    public clsIndiceCategorias? Ultimo()
    {
        lock (_candado)
        {
            return _indice;
        }
    }

    /// <summary>¿Se puede volver a leer el catálogo? Marca el intento (solo uno por <see cref="EsperaTrasFallo"/>).</summary>
    public bool ReservarIntento(DateTimeOffset ahora)
    {
        lock (_candado)
        {
            if (_ultimoIntento is { } previo && ahora - previo < EsperaTrasFallo)
            {
                return false;
            }

            _ultimoIntento = ahora;
            return true;
        }
    }

    /// <summary>Guarda el índice leído.</summary>
    public void Guardar(clsIndiceCategorias indice)
    {
        lock (_candado)
        {
            _indice = indice;
        }
    }
}

/// <summary>
/// Categoría por id del catálogo y por nombre (solo los nombres con una sola categoría), nombre por id del catálogo (corrección conexos) y
/// cuándo se leyó.
/// </summary>
public sealed record clsIndiceCategorias(
    IReadOnlyDictionary<string, string> PorId, IReadOnlyDictionary<string, string> PorNombre, DateTimeOffset LeidoAt,
    IReadOnlyDictionary<string, string>? NombresPorId = null)
{
    /// <summary>Categoría de un conexo guardado: por su id (del catálogo) o, si es el id de una fila de Nexo, por su nombre.</summary>
    public string? De(string id, string? nombre) =>
        PorId.TryGetValue(id, out var porId) ? porId
        : nombre is not null && PorNombre.TryGetValue(nombre.Trim(), out var porNombre) ? porNombre
        : null;

    /// <summary>
    /// Corrección conexos (nombre nunca id): nombre del catálogo de un conexo guardado, por el id del catálogo si la fila lo tiene (script
    /// 03) o por el id guardado (que en un conexo capturado ya es el del catálogo). <c>null</c> si no está en el índice.
    /// </summary>
    public string? Nombre(string id, string? idCatalogo)
    {
        if (NombresPorId is not { } nombres)
        {
            return null;
        }

        return idCatalogo is { } catalogo && nombres.TryGetValue(catalogo, out var porCatalogo) ? porCatalogo
            : nombres.TryGetValue(id, out var porId) ? porId
            : null;
    }
}

/// <summary>Implementación de <see cref="IClsCategoriasConexos"/> sobre <see cref="IClsServiciosConexosNexo.BuscarAsync"/>.</summary>
public sealed class clsCategoriasConexosService(
    IServiceScopeFactory ambitos,
    clsAlmacenCategoriasConexos almacen,
    IClsReloj reloj,
    ILogger<clsCategoriasConexosService> logger) : IClsCategoriasConexos
{
    /// <summary>Tiempo máximo de la lectura del catálogo en segundo plano.</summary>
    public static readonly TimeSpan TiempoMaximoLectura = TimeSpan.FromMinutes(2);

    /// <summary>Tamaño de página con que se recorre el catálogo.</summary>
    public const int TamanoPagina = 100;

    /// <summary>Máximo de páginas leídas (el catálogo de QA tiene unas 940 filas).</summary>
    public const int MaximoPaginas = 20;

    public Task<IReadOnlyDictionary<string, string>> CategoriasAsync(
        clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        // HU-014 (#188): los que ya traen la categoría guardada (script 05) no dependen del catálogo.
        var conexos = cotizacion.Rutas.SelectMany(r => r.Conexos).Where(c => c.Categoria is null).ToList();
        if (conexos.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        var ahora = reloj.AhoraUtc;
        var indice = almacen.Vigente(ahora);
        if ((indice is null || conexos.Any(c => De(indice, c) is null))
            && cotizacion.UsuarioId is { } usuarioId && almacen.ReservarIntento(ahora))
        {
            // #173 (observación del validador): no se espera a Nexo; se calienta el índice en segundo plano con su propio ámbito.
            almacen.UltimaLectura = Task.Run(() => CalentarAsync(usuarioId, cotizacion.Folio, ahora), CancellationToken.None);
        }

        return Task.FromResult(Resolver(indice ?? almacen.Ultimo(), conexos));
    }

    public Task<IReadOnlyDictionary<string, string>> NombresAsync(
        clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        // Corrección conexos: solo los que NO tienen el nombre guardado (filas migradas del JSON) dependen del catálogo; los demás ya lo traen.
        var conexos = cotizacion.Rutas.SelectMany(r => r.Conexos).Where(c => string.IsNullOrWhiteSpace(c.Nombre)).ToList();
        if (conexos.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        }

        var ahora = reloj.AhoraUtc;
        var indice = almacen.Vigente(ahora);
        if ((indice is null || conexos.Any(c => Nombre(indice, c) is null))
            && cotizacion.UsuarioId is { } usuarioId && almacen.ReservarIntento(ahora))
        {
            almacen.UltimaLectura = Task.Run(() => CalentarAsync(usuarioId, cotizacion.Folio, ahora), CancellationToken.None);
        }

        return Task.FromResult(ResolverNombres(indice ?? almacen.Ultimo(), conexos));
    }

    /// <summary>
    /// HU-027.1 (#179): las categorías que ya se conocen, sin leer el catálogo de Nexo NI calentarlo en segundo plano (a diferencia de
    /// <see cref="CategoriasAsync"/>): el detalle por folio solo lee lo que Cotifly tiene guardado o en memoria. Los conexos con la
    /// categoría guardada (script 05) no la necesitan; los demás que no estén en el índice usan la de respaldo.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EnMemoria(clsAlmacenCategoriasConexos almacen, clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(almacen);
        ArgumentNullException.ThrowIfNull(cotizacion);
        return Resolver(almacen.Ultimo(), [.. cotizacion.Rutas.SelectMany(r => r.Conexos).Where(c => c.Categoria is null)]);
    }

    /// <summary>
    /// Corrección conexos (nombre nunca id): los nombres del catálogo que ya están en memoria, sin leer el catálogo de Nexo ni calentarlo
    /// (como <see cref="EnMemoria"/>, para el detalle por folio). Solo los conexos sin nombre guardado que estén en el índice.
    /// </summary>
    public static IReadOnlyDictionary<string, string> NombresEnMemoria(clsAlmacenCategoriasConexos almacen, clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(almacen);
        ArgumentNullException.ThrowIfNull(cotizacion);
        return ResolverNombres(almacen.Ultimo(), [.. cotizacion.Rutas.SelectMany(r => r.Conexos).Where(c => string.IsNullOrWhiteSpace(c.Nombre))]);
    }

    private static IReadOnlyDictionary<string, string> Resolver(clsIndiceCategorias? indice, IReadOnlyList<clsServicioConexoRuta> conexos)
    {
        var categorias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (indice is null)
        {
            return categorias;
        }

        foreach (var conexo in conexos)
        {
            if (De(indice, conexo) is { } categoria)
            {
                categorias[conexo.IdServicioConexo] = categoria;
            }
        }

        return categorias;
    }

    private static IReadOnlyDictionary<string, string> ResolverNombres(clsIndiceCategorias? indice, IReadOnlyList<clsServicioConexoRuta> conexos)
    {
        var nombres = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (indice is null)
        {
            return nombres;
        }

        foreach (var conexo in conexos)
        {
            if (Nombre(indice, conexo) is { } nombre)
            {
                nombres[conexo.IdServicioConexo] = nombre;
            }
        }

        return nombres;
    }

    /// <summary>Categoría de un conexo en el índice: por el id del catálogo si la fila lo tiene (script 03), si no por el guardado o el nombre.</summary>
    private static string? De(clsIndiceCategorias indice, clsServicioConexoRuta conexo) =>
        (conexo.IdServicioConexoCatalogo is { } catalogo ? indice.De(catalogo, null) : null) ?? indice.De(conexo.IdServicioConexo, conexo.Nombre);

    /// <summary>Corrección conexos: nombre de un conexo en el índice, por el id del catálogo (script 03) o por el id guardado.</summary>
    private static string? Nombre(clsIndiceCategorias indice, clsServicioConexoRuta conexo) =>
        indice.Nombre(conexo.IdServicioConexo, conexo.IdServicioConexoCatalogo);

    private async Task CalentarAsync(Guid usuarioId, string folio, DateTimeOffset ahora)
    {
        try
        {
            using var limite = new CancellationTokenSource(TiempoMaximoLectura);
            await using var ambito = ambitos.CreateAsyncScope();
            await LeerAsync(ambito.ServiceProvider.GetRequiredService<IClsServiciosConexosNexo>(), usuarioId, folio, ahora, limite.Token);
        }
        catch (Exception ex)
        {
            _logCatalogoNoDisponible(logger, folio, ex.GetType().Name, ex);
        }
    }

    private async Task<clsIndiceCategorias?> LeerAsync(
        IClsServiciosConexosNexo catalogo, Guid usuarioId, string folio, DateTimeOffset ahora, CancellationToken cancellationToken)
    {
        var porId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var porNombre = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        // Corrección conexos: nombre del catálogo por id, para resolver el nombre de los conexos migrados sin nombre (no muestra el id crudo).
        var nombresPorId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var pagina = 1; pagina <= MaximoPaginas; pagina++)
        {
            var leida = await catalogo.BuscarAsync(usuarioId, new clsFiltroCatalogoConexos(null, null, pagina, TamanoPagina), cancellationToken);
            if (leida.EsFallo)
            {
                _logCatalogoNoDisponible(logger, folio, leida.Error.Codigo, null);
                return null;
            }

            foreach (var servicio in leida.Valor.Items)
            {
                var nombre = servicio.Nombre?.Trim() is { Length: > 0 } limpio ? limpio : null;
                if (nombre is not null)
                {
                    nombresPorId[servicio.Id] = nombre;
                }

                if (string.IsNullOrWhiteSpace(servicio.Categoria))
                {
                    continue;
                }

                var categoria = servicio.Categoria.Trim();
                porId[servicio.Id] = categoria;
                if (nombre is not null)
                {
                    // Un nombre con dos categorías distintas no sirve para resolver: queda en null y se ignora.
                    porNombre[nombre] = porNombre.TryGetValue(nombre, out var previa) && previa != categoria ? null : categoria;
                }
            }

            if (leida.Valor.Items.Count < TamanoPagina || pagina * TamanoPagina >= leida.Valor.Total)
            {
                break;
            }
        }

        var indice = new clsIndiceCategorias(
            porId, porNombre.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase), ahora,
            nombresPorId);
        almacen.Guardar(indice);
        return indice;
    }

    private static readonly Action<ILogger, string, string, Exception?> _logCatalogoNoDisponible = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(1732, nameof(_logCatalogoNoDisponible)),
        "No se pudo leer el catálogo de conexos de Nexo para las categorías de {Folio} ({Codigo}); se usa la categoría por defecto.");
}
