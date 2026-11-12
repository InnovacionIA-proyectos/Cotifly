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
    /// HU-046.3 (#269): <c>codigoServicio</c> del catálogo de Nexo por id guardado (<c>idServicioConexo</c>) de los conexos de
    /// <paramref name="cotizacion"/> que NO lo tienen guardado (filas anteriores al script 21 o importadas de Nexo), con el mismo índice y
    /// la misma regla que <see cref="CategoriasAsync"/>: nunca espera a Nexo; si el índice está frío o le falta un conexo, lo calienta en
    /// segundo plano. Quien lo llama completa las filas (<see cref="clsCotizacionSolicitud.CompletarCodigosConexos"/>).
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> CodigosAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);

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
/// Categoría por id del catálogo y por nombre (solo los nombres con una sola categoría) y cuándo se leyó. HU-046.3 (#269): también el
/// <c>codigoServicio</c> por id y por nombre (solo los nombres con un solo código; Nexo repite el mismo servicio en una fila por trayecto).
/// Corrección conexos: el nombre del catálogo por id (<see cref="NombresPorId"/>), para no mostrar nunca el id interno.
/// </summary>
public sealed record clsIndiceCategorias(
    IReadOnlyDictionary<string, string> PorId, IReadOnlyDictionary<string, string> PorNombre, DateTimeOffset LeidoAt,
    IReadOnlyDictionary<string, string>? CodigoPorId = null, IReadOnlyDictionary<string, string>? CodigoPorNombre = null,
    IReadOnlyDictionary<string, string>? NombresPorId = null)
{
    /// <summary>Categoría de un conexo guardado: por su id (del catálogo) o, si es el id de una fila de Nexo, por su nombre.</summary>
    public string? De(string id, string? nombre) => Buscar(PorId, PorNombre, id, nombre);

    /// <summary>HU-046.3 (#269): <c>codigoServicio</c> de un conexo guardado, con la misma regla que <see cref="De"/>.</summary>
    public string? CodigoDe(string id, string? nombre) => Buscar(CodigoPorId, CodigoPorNombre, id, nombre);

    private static string? Buscar(
        IReadOnlyDictionary<string, string>? porId, IReadOnlyDictionary<string, string>? porNombre, string id, string? nombre) =>
        porId is not null && porId.TryGetValue(id, out var deId) ? deId
        : nombre is not null && porNombre is not null && porNombre.TryGetValue(nombre.Trim(), out var deNombre) ? deNombre
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
        return Task.FromResult(Resolver(IndiceCalentando(cotizacion, conexos, De), conexos));
    }

    public Task<IReadOnlyDictionary<string, string>> CodigosAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        // HU-046.3 (#269): los que ya traen el codigoServicio guardado (script 21) no dependen del catálogo.
        var conexos = cotizacion.Rutas.SelectMany(r => r.Conexos).Where(c => c.CodigoServicio is null).ToList();
        // B6 de la auditoría: con el índice vigente no se recalienta por un código que no está (el servicio salió del catálogo o el
        // nombre es ambiguo): sería una lectura de Nexo por minuto para siempre. Solo con el índice frío o vencido.
        var indice = IndiceCalentando(cotizacion, conexos, (_, _) => null, recalentarSiFalta: false);
        var codigos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (indice is not null)
        {
            foreach (var conexo in conexos)
            {
                if (CodigoDe(indice, conexo) is { } codigo)
                {
                    codigos[conexo.IdServicioConexo] = codigo;
                }
            }
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(codigos);
    }

    /// <summary>
    /// El índice vigente (o el último, vencido) para <paramref name="conexos"/>; si está frío o no resuelve alguno con
    /// <paramref name="resolver"/>, lo calienta en segundo plano (a lo sumo un intento por <see cref="clsAlmacenCategoriasConexos.EsperaTrasFallo"/>).
    /// </summary>
    private clsIndiceCategorias? IndiceCalentando(
        clsCotizacionSolicitud cotizacion, IReadOnlyList<clsServicioConexoRuta> conexos, Func<clsIndiceCategorias, clsServicioConexoRuta, string?> resolver,
        bool recalentarSiFalta = true)
    {
        if (conexos.Count == 0)
        {
            return null;
        }

        var ahora = reloj.AhoraUtc;
        var indice = almacen.Vigente(ahora);
        if ((indice is null || (recalentarSiFalta && conexos.Any(c => resolver(indice, c) is null)))
            && cotizacion.UsuarioId is { } usuarioId && almacen.ReservarIntento(ahora))
        {
            // #173 (observación del validador): no se espera a Nexo; se calienta el índice en segundo plano con su propio ámbito.
            almacen.UltimaLectura = Task.Run(() => CalentarAsync(usuarioId, cotizacion.Folio, ahora), CancellationToken.None);
        }

        return indice ?? almacen.Ultimo();
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

    /// <summary>HU-046.3 (#269): <c>codigoServicio</c> de un conexo en el índice, con la misma regla que <see cref="De"/>.</summary>
    private static string? CodigoDe(clsIndiceCategorias indice, clsServicioConexoRuta conexo) =>
        (conexo.IdServicioConexoCatalogo is { } catalogo ? indice.CodigoDe(catalogo, null) : null) ?? indice.CodigoDe(conexo.IdServicioConexo, conexo.Nombre);

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
        var codigoPorId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var codigoPorNombre = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
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
                // Corrección conexos: nombre del catálogo por id, aunque el servicio no traiga categoría ni código.
                if (servicio.Nombre?.Trim() is { Length: > 0 } nombre)
                {
                    nombresPorId[servicio.Id] = nombre;
                }

                // Un nombre con dos categorías (o dos códigos) distintos no sirve para resolver: queda en null y se ignora.
                Indexar(porId, porNombre, servicio.Id, servicio.Nombre, servicio.Categoria);
                // HU-046.3 (#269): el codigoServicio (Nexo repite el mismo código en una fila por trayecto, con ids distintos).
                Indexar(codigoPorId, codigoPorNombre, servicio.Id, servicio.Nombre, servicio.Codigo);
            }

            if (leida.Valor.Items.Count < TamanoPagina || pagina * TamanoPagina >= leida.Valor.Total)
            {
                break;
            }
        }

        var indice = new clsIndiceCategorias(
            porId, SinAmbiguos(porNombre), ahora, codigoPorId, SinAmbiguos(codigoPorNombre), NombresPorId: nombresPorId);
        almacen.Guardar(indice);
        return indice;
    }

    private static void Indexar(Dictionary<string, string> porId, Dictionary<string, string?> porNombre, string id, string? nombre, string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
        {
            return;
        }

        var limpio = valor.Trim();
        porId[id] = limpio;
        if (nombre?.Trim() is { Length: > 0 } clave)
        {
            porNombre[clave] = porNombre.TryGetValue(clave, out var previo) && !string.Equals(previo, limpio, StringComparison.Ordinal) ? null : limpio;
        }
    }

    private static Dictionary<string, string> SinAmbiguos(Dictionary<string, string?> porNombre) =>
        porNombre.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase);

    private static readonly Action<ILogger, string, string, Exception?> _logCatalogoNoDisponible = LoggerMessage.Define<string, string>(
        LogLevel.Warning, new EventId(1732, nameof(_logCatalogoNoDisponible)),
        "No se pudo leer el catálogo de conexos de Nexo para las categorías de {Folio} ({Codigo}); se usa la categoría por defecto.");
}
