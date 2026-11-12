using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Captura por rutas, común al formulario (<c>POST /web/solicitudes-costeo</c>) y a la licitación (<c>POST /web/licitaciones</c>):
/// valida TODAS las rutas juntas y las arma para <see cref="clsCotizacionSolicitud.RegistrarCaptura"/>.
/// </summary>
public interface IClsCapturaRutasService
{
    /// <summary>
    /// Todas las validaciones que dependen de catálogos o que solo aplican a una captura nueva, acumuladas sobre
    /// <paramref name="erroresPrevios"/> (los de forma y rango que ya calculó el llamador): forma de los conexos, códigos de catálogo,
    /// municipios (origen, destino y ciudades del contenedor: id y nombre, nunca adivina), <see cref="clsLineaSolicitudCosteo.ErroresCaptura"/>
    /// y, con el catálogo del cubicador disponible, que cada <c>codigo_mercancia</c> esté activo y sea cubicable o que el
    /// <c>id_tipo_mercancia</c> obsoleto exista (<c>TIPO_MERCANCIA_INEXISTENTE</c>).
    /// Devuelve las rutas con los municipios resueltos, todos los errores (encabezado primero, luego por ruta) y, si un catálogo de Nexo
    /// no respondió, ese error.
    /// </summary>
    Task<clsFilasLicitacionValidadas> ValidarAsync(
        Guid usuarioId,
        IReadOnlyList<clsLineaLicitacionEntrada?> rutas,
        IReadOnlyList<clsErrorFilaLicitacion> erroresPrevios,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Arma las rutas ya validadas: el id de cada ruta (viaja al cubicador), el nombre de cada mercancía copiado del catálogo del cubicador
    /// por su <c>codigo_mercancia</c> (o el <c>id_tipo_mercancia</c> obsoleto; sin catálogo, el que mandó el cliente), sus conexos con el costo unitario del catálogo de
    /// Nexo como precio y sus camiones (<see cref="IClsPlanificadorCamiones"/>). Un conexo que no está en el catálogo es un error de su ruta
    /// (<c>CONEXO_NO_ENCONTRADO</c>); Nexo caído o el planificador sin respuesta son <see cref="clsRutasPreparadas.Error"/>.
    /// Con <paramref name="cubicarDespues"/> (licitación con cubicador) no se llama al planificador: cada ruta queda con
    /// un camión provisional sin vehículo (el del respaldo) y marcada para la cubicación en segundo plano.
    /// </summary>
    Task<clsRutasPreparadas> PrepararAsync(
        Guid usuarioId,
        IReadOnlyList<clsRutaPorPreparar> rutas,
        bool cubicarDespues = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Una ruta validada para <see cref="IClsCapturaRutasService.PrepararAsync"/>: la entrada, su línea de negocio e índice de origen.
/// En la edición, <paramref name="RutaId"/> fija el id de la ruta (el de la ruta guardada que se edita; <c>null</c> = uno nuevo)
/// y <paramref name="PlanVigente"/> trae sus camiones vigentes cuando la carga no cambió: entonces no se llama al planificador.
/// </summary>
public sealed record clsRutaPorPreparar(
    clsLineaLicitacionEntrada Ruta, int LineaNegocioId, int? RutaOrigenIndice, Guid? RutaId = null, clsPlanRuta? PlanVigente = null);

/// <summary>Resultado de <see cref="IClsCapturaRutasService.PrepararAsync"/>: las rutas listas, o errores por ruta, o un error general.</summary>
public sealed record clsRutasPreparadas(IReadOnlyList<clsRutaCapturada> Rutas, IReadOnlyList<clsErrorFilaLicitacion> Errores, clsErrorDominio? Error);

/// <summary>Implementación de <see cref="IClsCapturaRutasService"/>.</summary>
public sealed class clsCapturaRutasService(
    IClsValidadorFilasLicitacion filas,
    IClsCostosConexosNexo costosConexos,
    IClsPlanificadorCamiones planificador,
    ILogger<clsCapturaRutasService> logger,
    IClsCubicador? cubicador = null,
    IOptions<clsOpcionesCaptura>? opcionesCaptura = null) : IClsCapturaRutasService
{
    /// <summary>
    /// ¿El vehículo elegido cuenta? (<c>Captura:AceptarVehiculoCliente</c>, encendida por defecto). Apagada es el
    /// interruptor de reversa: el elegido se ignora (lo borra la captura) y no se exige <c>VEHICULO_REQUERIDO</c>.
    /// </summary>
    private bool AceptaVehiculo => opcionesCaptura?.Value.AceptarVehiculoCliente ?? true;

    /// <summary>
    /// ¿El vehículo elegido de la ruta se valida contra Nexo ANTES de guardar? Solo si se sabe que se va a usar: la ruta no
    /// se puede cubicar (no todas sus mercancías son cubicables) o no hay cubicador configurado. Si la ruta va al cubicador, el elegido es
    /// solo el respaldo: se valida tarde, si se llega a usar (<see cref="clsPlanificadorCamionesCubicador"/>).
    /// </summary>
    private bool ValidaVehiculoAntes(clsLineaSolicitudCosteo linea) =>
        linea.Cubicabilidad != CubicabilidadRuta.Cubicable || cubicador is not { EstaConfigurado: true };
    /// <summary>Campo de la clase de viaje en los errores por ruta.</summary>
    private const string CampoClaseViaje = "clase_viaje";

    /// <summary>Máximo de servicios conexos por ruta.</summary>
    public const int MaximoConexosPorRuta = 20;

    /// <summary>Cantidad máxima de un conexo (Nexo la maneja con 2 decimales).</summary>
    public const decimal CantidadMaximaConexo = 99_999.99m;

    /// <summary>¿Ya se leyó el catálogo del cubicador en esta captura? (Una sola lectura, con éxito o sin él.)</summary>
    private bool _catalogoLeido;

    /// <summary>El catálogo de tipos de mercancía leído en esta captura (<c>null</c> si no estuvo disponible).</summary>
    private clsCatalogoMercancias? _catalogo;

    /// <summary>Campo del id de municipio → campo del nombre: si el nombre ya tiene un error, el id no se reporta otra vez.</summary>
    private static readonly IReadOnlyDictionary<string, string> _nombreDelId = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["origen_municipio_id"] = "origen",
        ["destino_municipio_id"] = "destino",
        ["ciudad_devolucion_municipio_id"] = "ciudad_devolucion_contenedor",
        ["ciudad_levante_municipio_id"] = "ciudad_levante_contenedor",
    };

    public async Task<clsFilasLicitacionValidadas> ValidarAsync(
        Guid usuarioId,
        IReadOnlyList<clsLineaLicitacionEntrada?> rutas,
        IReadOnlyList<clsErrorFilaLicitacion> erroresPrevios,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rutas);
        ArgumentNullException.ThrowIfNull(erroresPrevios);
        var previos = new List<clsErrorFilaLicitacion>(erroresPrevios);
        for (var i = 0; i < rutas.Count; i++)
        {
            if (rutas[i] is { } ruta)
            {
                previos.AddRange(ErroresConexos(ruta.Conexos).Select(e => new clsErrorFilaLicitacion(i, ruta.Fila, e.Campo, e.Codigo, e.Mensaje)));
            }
        }

        // El vehículo elegido de una ruta que va al cubicador no se valida aquí (validación tardía): se aparta de la línea
        // que se valida contra los catálogos de Nexo y se devuelve tal cual después.
        var diferidos = rutas
            .Select(r => r?.Linea is { } l && !ValidaVehiculoAntes(l) && (l.TipoVehiculo is not null || l.Carroceria is not null) ? l : null)
            .ToList();
        IReadOnlyList<clsLineaLicitacionEntrada?> paraValidar = diferidos.All(d => d is null)
            ? rutas
            : [.. rutas.Select((r, i) => diferidos[i] is { } l ? r! with { Linea = l with { TipoVehiculo = null, Carroceria = null } } : r)];
        var validadas = await filas.ValidarAsync(usuarioId, paraValidar, previos, cancellationToken);
        if (!ReferenceEquals(paraValidar, rutas))
        {
            validadas = validadas with
            {
                Lineas = [.. validadas.Lineas.Select((r, i) => i < diferidos.Count && diferidos[i] is { } original && r.Linea is { } linea
                    ? r with { Linea = linea with { TipoVehiculo = Limpio(original.TipoVehiculo), Carroceria = Limpio(original.Carroceria) } }
                    : r)],
            };
        }

        var errores = new List<clsErrorFilaLicitacion>(validadas.Errores);

        // VEHICULO_REQUERIDO va después de los errores de catálogo y municipios de su ruta (el orden final es por ruta). Una ruta
        // cuya clase de viaje ya tiene un error (mal escrita o fuera del catálogo) no se juzga: la exención depende de la clase, y
        // el comercial vería dos errores por uno solo.
        var claseConError = validadas.Errores
            .Where(e => e.Indice is not null && e.Campo == CampoClaseViaje)
            .Select(e => e.Indice!.Value)
            .ToHashSet();
        for (var i = 0; i < rutas.Count; i++)
        {
            if (rutas[i] is { } ruta && !claseConError.Contains(i)
                && !ruta.Linea.ErroresCaptura().Any(e => e.Campo == CampoClaseViaje)
                && VehiculoRequerido(ruta.Linea) is { } requerido)
            {
                errores.Add(new clsErrorFilaLicitacion(i, ruta.Fila, requerido.Campo, requerido.Codigo, requerido.Mensaje));
            }
        }
        // Con un catálogo de Nexo caído (el de municipios, o el maestro que se consultó primero y oculta si el de municipios respondió) los
        // ids que faltan no se exigen aquí: la captura responde 503 y se validan al volver a enviarla. Con el catálogo de municipios
        // disponible, un nombre que no se pudo resolver ya trae su propio error.
        var sinMunicipios = validadas.NoDisponible is not null;
        for (var i = 0; i < validadas.Lineas.Count; i++)
        {
            var indice = i;
            var ruta = validadas.Lineas[i];
            if (ruta?.Linea is null)
            {
                continue;
            }

            var campos = errores.Where(e => e.Indice == indice).Select(e => e.Campo).ToHashSet(StringComparer.Ordinal);
            foreach (var error in ruta.Linea.ErroresCaptura())
            {
                // Sin catálogo de municipios los ids que faltan se exigen al volver a enviarla; un id cuyo nombre ya falló no se repite.
                var esMunicipio = _nombreDelId.TryGetValue(error.Campo, out var nombre);
                if (esMunicipio && (sinMunicipios || campos.Contains(nombre!) || campos.Contains(error.Campo)))
                {
                    continue;
                }

                errores.Add(new clsErrorFilaLicitacion(indice, ruta.Fila, error.Campo, error.Codigo, error.Mensaje));
            }
        }

        // El código solo es obligatorio con el cubicador configurado. Con el catálogo disponible, el codigo_mercancia debe estar activo y ser cubicable; el
        // id_tipo_mercancia obsoleto debe existir (se traduce al código). Sin catálogo (cubicador sin configurar o caído) el código se
        // acepta tal cual (el cubicador lo valida al recomendar), pero un id solo no se puede traducir: se pide el código.
        var tipos = await TiposMercanciaAsync(validadas.Lineas.Select(r => r?.Linea), cancellationToken);
        var exigeCodigo = cubicador is { EstaConfigurado: true };
        var sinCodigo = 0;
        for (var i = 0; i < validadas.Lineas.Count; i++)
        {
            var posiciones = validadas.Lineas[i]?.Linea?.Posiciones ?? [];
            sinCodigo += posiciones.Count(p => p is not null && string.IsNullOrWhiteSpace(p.CodigoMercancia));
            for (var m = 0; m < posiciones.Count; m++)
            {
                if (ErrorMercancia(posiciones[m], m, tipos, exigeCodigo) is { } error)
                {
                    errores.Add(new clsErrorFilaLicitacion(i, validadas.Lineas[i]?.Fila, error.Campo, error.Codigo, error.Mensaje));
                }
            }
        }

        // Los conexos que no están en el catálogo de Nexo se reportan junto con los demás errores (la lectura queda en la caché de 5 min
        // del adaptador y PrepararAsync la reutiliza). Sin Nexo, 503 como cualquier catálogo caído.
        var noDisponible = validadas.NoDisponible;
        var conexosMalFormados = errores
            .Where(e => e.Indice is not null && e.Campo.StartsWith("servicios_conexos", StringComparison.Ordinal))
            .Select(e => e.Indice!.Value)
            .ToHashSet();
        var pedidos = validadas.Lineas
            .SelectMany((r, i) => (r?.Conexos ?? []).Select((c, j) => (Ruta: i, Fila: r?.Fila, Indice: j, Id: c?.IdServicioConexo?.Trim())))
            .Where(c => !conexosMalFormados.Contains(c.Ruta) && !string.IsNullOrEmpty(c.Id))
            .ToList();
        if (pedidos.Count > 0)
        {
            var costos = await costosConexos.CostosAsync(usuarioId, [.. pedidos.Select(p => p.Id!).Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken);
            if (costos.EsFallo)
            {
                noDisponible ??= costos.Error;
            }
            else
            {
                var encontrados = new Dictionary<string, clsCostoConexoNexo>(costos.Valor, StringComparer.OrdinalIgnoreCase);
                errores.AddRange(pedidos
                    .Where(p => !encontrados.TryGetValue(p.Id!, out var costo) || costo.CostoUnitario < 0m)
                    .Select(p => ConexoNoEncontrado(p.Ruta, p.Fila, p.Indice, p.Id!)));
            }
        }

        if (!exigeCodigo && sinCodigo > 0)
        {
            logger.LogWarning(
                "MERCANCIA_SIN_CODIGO: {Mercancias} mercancía(s) sin codigo_mercancia y el cubicador no está configurado: se guardan sin código y sus rutas van al respaldo (un camión sin vehículo).",
                sinCodigo);
        }

        return validadas with { Errores = [.. errores.OrderBy(e => e.Indice ?? -1)], NoDisponible = noDisponible };
    }

    /// <summary>
    /// <c>VEHICULO_REQUERIDO</c> en <c>tipo_vehiculo</c> según <see cref="FaltaVehiculo"/>; <c>null</c> si no aplica o la
    /// bandera <c>Captura:AceptarVehiculoCliente</c> está apagada.
    /// </summary>
    private clsErrorCampo? VehiculoRequerido(clsLineaSolicitudCosteo? linea) =>
        AceptaVehiculo && linea is not null
        && FaltaVehiculo(linea, opcionesCaptura?.Value.ClasesSinVehiculo, cubicador is { EstaConfigurado: true })
            ? new clsErrorCampo("tipo_vehiculo", clsCodigosLineaSolicitud.VehiculoRequerido, MensajeVehiculoRequerido)
            : null;

    /// <summary>Mensaje de <c>VEHICULO_REQUERIDO</c> (la captura, la edición y la re-solicitud con <c>datos_carga</c>).</summary>
    public const string MensajeVehiculoRequerido =
        "La ruta no se puede cubicar (solo trae el peso o mercancías sin medidas) y no trae tipo_vehiculo: elige el vehículo "
        + "(GET /web/catalogos-flete). Solo una ruta de una clase de viaje exenta (por defecto, Contenedor) puede ir sin vehículo.";

    /// <summary>
    /// La regla ÚNICA de <c>VEHICULO_REQUERIDO</c> (captura, edición y re-solicitud con <c>datos_carga</c>): la ruta no se
    /// puede cubicar (solo el peso o mercancías sin medidas, también una ruta mixta), su clase de viaje no es una de
    /// <paramref name="clasesSinVehiculo"/> (<c>Captura:ClasesSinVehiculoObligatorio</c>, por defecto Contenedor) y no trae vehículo. La
    /// carrocería es opcional: Nexo autocostea Cupo con el tipo de vehículo. Una mercancía mal digitada (o, con
    /// <paramref name="cubicadorConfigurado"/>, una con medidas y sin código: <c>CAMPO_REQUERIDO</c> en su código) ya trae su error: la ruta
    /// no se juzga hasta corregirla. La edición solo la aplica a una ruta nueva o que cambia.
    /// </summary>
    public static bool FaltaVehiculo(clsLineaSolicitudCosteo linea, IReadOnlyCollection<string>? clasesSinVehiculo, bool cubicadorConfigurado)
    {
        ArgumentNullException.ThrowIfNull(linea);
        return linea.ExigeVehiculoElegido(clasesSinVehiculo) && string.IsNullOrWhiteSpace(linea.TipoVehiculo)
               && linea.Posiciones.All(p => p is not null && p.PrimerError(1) is null && !(cubicadorConfigurado && p.ConMedidas && !p.VaAlCubicador()));
    }

    private static string? Limpio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static clsErrorFilaLicitacion ConexoNoEncontrado(int ruta, int? fila, int indice, string id) =>
        new(ruta, fila, $"servicios_conexos[{indice}].id_servicio_conexo", clsCodigosLineaSolicitud.ConexoNoEncontrado,
            $"El servicio conexo '{id}' no está activo en el catálogo de Nexo (GET /web/servicios-conexos).");

    public async Task<clsRutasPreparadas> PrepararAsync(
        Guid usuarioId, IReadOnlyList<clsRutaPorPreparar> rutas, bool cubicarDespues = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rutas);
        var ids = rutas
            .SelectMany(r => r.Ruta.Conexos ?? [])
            .Select(c => c?.IdServicioConexo?.Trim())
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        IReadOnlyDictionary<string, clsCostoConexoNexo> costos = new Dictionary<string, clsCostoConexoNexo>();
        if (ids.Count > 0)
        {
            var leidos = await costosConexos.CostosAsync(usuarioId, ids, cancellationToken);
            if (leidos.EsFallo)
            {
                return new clsRutasPreparadas([], [], leidos.Error);
            }

            costos = new Dictionary<string, clsCostoConexoNexo>(leidos.Valor, StringComparer.OrdinalIgnoreCase);
        }

        var tipos = await TiposMercanciaAsync(rutas.Select(r => r.Ruta.Linea), cancellationToken);
        var errores = new List<clsErrorFilaLicitacion>();
        var preparadas = new List<clsRutaCapturada>(rutas.Count);
        for (var i = 0; i < rutas.Count; i++)
        {
            var (entrada, lineaNegocioId, indiceOrigen, _, _) = rutas[i];
            var linea = ConNombresDelCatalogo(entrada.Linea, tipos);
            var conexos = new List<clsCambioConexo>();
            var pedidos = entrada.Conexos ?? [];
            for (var c = 0; c < pedidos.Count; c++)
            {
                var id = pedidos[c]!.IdServicioConexo!.Trim();
                if (!costos.TryGetValue(id, out var costo) || costo.CostoUnitario < 0m)
                {
                    errores.Add(ConexoNoEncontrado(i, entrada.Fila, c, id));
                    continue;
                }

                // El id pedido es el del catálogo (scripts 03 y 05: se guardan el id del catálogo y la categoría).
                // La venta es precioVenta ?? costoUnitario del catálogo, sin margen; con su codigoServicio.
                conexos.Add(new clsCambioConexo(
                    costo.Id, costo.Nombre, costo.CostoUnitario, pedidos[c]!.Cantidad!.Value, costo.Id, costo.Categoria, costo.VentaUnitaria, costo.Codigo));
            }

            var rutaId = rutas[i].RutaId ?? Guid.NewGuid();
            if (rutas[i].PlanVigente is { } vigente)
            {
                // La carga y la clase de viaje no cambiaron: se conservan los camiones vigentes (sin llamar al cubicador).
                preparadas.Add(new clsRutaCapturada(linea, lineaNegocioId, indiceOrigen, vigente.Camiones, conexos, rutaId, vigente.Cubicacion)
                {
                    AvisosVehiculo = vigente.Avisos,
                });
                continue;
            }

            if (cubicarDespues && linea.Cubicabilidad == CubicabilidadRuta.Cubicable)
            {
                // El camión provisional (sin vehículo, con el peso de la ruta) lo reemplaza la cubicación en segundo plano.
                // Va sin el vehículo elegido (no se ha validado: se valida si el cubicador no escoge); la ruta lo guarda.
                preparadas.Add(new clsRutaCapturada(
                    linea, lineaNegocioId, indiceOrigen, [new clsCamionPlaneado(null, null, clsPlanificadorCamionSinVehiculo.PesoTotal(linea))],
                    conexos, rutaId, CubicacionPendiente: true));
                continue;
            }

            // Una fila sin mercancías cubicables no espera al cubicador (no lo llama): se planifica ya, con el vehículo elegido.
            var plan = await planificador.PlanificarAsync(usuarioId, rutaId, linea, cancellationToken);
            if (plan.EsFallo)
            {
                // Todo o nada: si una ruta falla no se guarda ninguna ni se envía nada a Nexo. Las corridas que el cubicador ya
                // creó para las rutas anteriores quedan huérfanas allá (se acepta; ADR-019/020).
                logger.LogWarning("No se pudieron planificar los camiones de la ruta {Ruta} ({Codigo}): no se guarda la captura.", i + 1, plan.Error.Codigo);
                return new clsRutasPreparadas([], [], ConRuta(plan.Error, i));
            }

            preparadas.Add(new clsRutaCapturada(linea, lineaNegocioId, indiceOrigen, plan.Valor.Camiones, conexos, rutaId, plan.Valor.Cubicacion)
            {
                AvisosVehiculo = plan.Valor.Avisos,
            });
        }

        return errores.Count > 0 ? new clsRutasPreparadas([], errores, null) : new clsRutasPreparadas(preparadas, [], null);
    }

    /// <summary>
    /// Un rechazo del cubicador que señala la mercancía (<c>detalle.item</c>, base 0) lleva también la ruta (<c>detalle.ruta</c>,
    /// base 0, como en <c>detalle.errores</c>) para que la UI marque la mercancía de esa ruta. Sin mercancía señalada queda igual.
    /// </summary>
    private static clsErrorDominio ConRuta(clsErrorDominio error, int ruta)
    {
        if (error.Datos is not { } datos || !datos.ContainsKey("item"))
        {
            return error;
        }

        return error with { Datos = new Dictionary<string, object?>(datos, StringComparer.Ordinal) { ["ruta"] = ruta } };
    }

    /// <summary>
    /// Catálogo de tipos de mercancía del cubicador por id, o <c>null</c> si no hay cubicador configurado, ninguna mercancía trae tipo o
    /// el catálogo no respondió (queda un warning: la captura conserva lo actual).
    /// </summary>
    private async Task<clsCatalogoMercancias?> TiposMercanciaAsync(
        IEnumerable<clsLineaSolicitudCosteo?> lineas, CancellationToken cancellationToken)
    {
        if (cubicador is not { EstaConfigurado: true }
            || !lineas.Any(l => l?.Posiciones.Any(p => p is not null
                && (!string.IsNullOrWhiteSpace(p.CodigoMercancia) || (p.IdTipoMercancia is { } id && id != Guid.Empty))) == true))
        {
            return null;
        }

        // Una sola lectura por captura (el servicio es por petición): la validación y la preparación comparten el resultado, también el
        // fallo, para no pagar dos veces los reintentos con el catálogo caído.
        if (_catalogoLeido)
        {
            return _catalogo;
        }

        _catalogoLeido = true;
        var catalogo = await cubicador.ListarTiposMercanciaAsync(cancellationToken);
        if (catalogo.EsFallo)
        {
            logger.LogWarning("El catálogo de tipos de mercancía del cubicador no está disponible ({Detalle}): se conservan los nombres que mandó el cliente.",
                catalogo.Error.Detalle ?? catalogo.Error.Codigo);
            return null;
        }

        // El catálogo es por código; el uuid (si el cubicador lo publica) solo traduce el id_tipo_mercancia obsoleto.
        var porId = new Dictionary<Guid, clsTipoMercanciaCubicador>();
        var porCodigo = new Dictionary<string, clsTipoMercanciaCubicador>(StringComparer.OrdinalIgnoreCase);
        foreach (var tipo in catalogo.Valor)
        {
            porCodigo.TryAdd(tipo.Codigo, tipo);
            if (tipo.Id is { } id)
            {
                porId.TryAdd(id, tipo);
            }
        }

        _catalogo = new clsCatalogoMercancias(porId, porCodigo);
        return _catalogo;
    }

    /// <summary>
    /// La línea con el nombre de cada mercancía copiado del catálogo del cubicador por su <c>id_tipo_mercancia</c> (copia, como el
    /// municipio y el vehículo; un nombre distinto del cliente se ignora y queda en el log). Sin catálogo o sin el id, la mercancía queda
    /// como llegó.
    /// </summary>
    private clsLineaSolicitudCosteo ConNombresDelCatalogo(clsLineaSolicitudCosteo linea, clsCatalogoMercancias? tipos)
    {
        if (tipos is null || linea.Posiciones.Count == 0)
        {
            return linea;
        }

        var distintos = 0;
        var posiciones = linea.Posiciones.Select(clsPosicionCarga (p) =>
        {
            if (p is null || tipos.De(p) is not { } tipo || string.IsNullOrWhiteSpace(tipo.Nombre))
            {
                return p!;
            }

            var copia = tipo.Nombre.Trim();
            copia = copia.Length > clsPosicionCarga.LargoDescripcion ? copia[..clsPosicionCarga.LargoDescripcion] : copia;
            if (!string.IsNullOrWhiteSpace(p.Descripcion) && !string.Equals(p.Descripcion.Trim(), copia, StringComparison.Ordinal))
            {
                distintos++;
            }

            // El código del catálogo es lo que viaja al cubicador (POST /recomendaciones).
            return p with { Descripcion = copia, CodigoMercancia = tipo.Codigo };
        }).ToList();
        if (distintos > 0)
        {
            logger.LogInformation("{Mercancias} mercancía(s) traen un nombre distinto del catálogo del cubicador: se guarda el del catálogo.", distintos);
        }

        return linea with { Posiciones = posiciones };
    }

    /// <summary>
    /// El error de la mercancía <paramref name="m"/> contra el catálogo del cubicador (<paramref name="tipos"/>; <c>null</c> =
    /// no disponible), o <c>null</c>. Una mercancía sin código ni id ya la reporta <see cref="clsLineaSolicitudCosteo.ErroresCaptura"/>.
    /// </summary>
    private static clsErrorCampo? ErrorMercancia(clsPosicionCarga? p, int m, clsCatalogoMercancias? tipos, bool exigeCodigo)
    {
        if (p is null)
        {
            return null;
        }

        var campoCodigo = $"mercancias[{m}].codigo_mercancia";
        if (!string.IsNullOrWhiteSpace(p.CodigoMercancia))
        {
            var codigo = p.CodigoMercancia.Trim();
            if (tipos is null)
            {
                return null;
            }

            if (!tipos.PorCodigo.TryGetValue(codigo, out var tipo) || !tipo.Activo)
            {
                return new clsErrorCampo(campoCodigo, clsCodigosLineaSolicitud.MercanciaDesconocida,
                    $"La mercancía {m + 1} ('{codigo}') no está activa en el catálogo del cubicador (GET /web/tipos-mercancia).");
            }

            return tipo.Cubicable
                ? null
                : new clsErrorCampo(campoCodigo, clsCodigosLineaSolicitud.MercanciaNoCubicable,
                    $"La mercancía {m + 1} ('{codigo}') no se puede cubicar según el catálogo del cubicador.");
        }

        if (p.IdTipoMercancia is not { } id || id == Guid.Empty)
        {
            // Sin cubicador configurado no hay de dónde sacar el código (GET /web/tipos-mercancia responde 503): se guarda NULL y la
            // ruta va al respaldo. Con cubicador es obligatorio en una mercancía con medidas. Una mercancía sin medidas ni
            // código es "no cubicable": se acepta (la ruta usa el vehículo elegido).
            return exigeCodigo && !p.EsNoCubicable && p.PrimerError(m + 1) is null
                ? new clsErrorCampo(campoCodigo, clsCodigosLineaSolicitud.CampoRequerido,
                    $"La mercancía {m + 1} debe traer el código del catálogo del cubicador (codigo_mercancia, GET /web/tipos-mercancia).")
                : null;
        }

        if (!exigeCodigo)
        {
            return null;
        }

        if (tipos is null)
        {
            return new clsErrorCampo(campoCodigo, clsCodigosLineaSolicitud.CampoRequerido,
                $"La mercancía {m + 1} trae solo id_tipo_mercancia (obsoleto) y el catálogo del cubicador no respondió para traducirlo: envía codigo_mercancia.");
        }

        return tipos.PorId.ContainsKey(id)
            ? null
            : new clsErrorCampo($"mercancias[{m}].id_tipo_mercancia", clsCodigosLineaSolicitud.TipoMercanciaInexistente,
                $"El tipo de mercancía '{id}' de la mercancía {m + 1} no está en el catálogo del cubicador (GET /web/tipos-mercancia).");
    }

    /// <summary>
    /// Forma de los conexos de una ruta: id obligatorio (máx. <see cref="clsServicioConexoRuta.LargoMaximoId"/>), cantidad &gt; 0 con 2
    /// decimales, sin repetir el mismo servicio y hasta <see cref="MaximoConexosPorRuta"/>.
    /// </summary>
    public static IReadOnlyList<clsErrorCampo> ErroresConexos(IReadOnlyList<clsConexoCapturaEntrada?>? conexos)
    {
        var errores = new List<clsErrorCampo>();
        if (conexos is null || conexos.Count == 0)
        {
            return errores;
        }

        if (conexos.Count > MaximoConexosPorRuta)
        {
            errores.Add(new clsErrorCampo("servicios_conexos", clsCodigosLineaSolicitud.DemasiadosConexos,
                $"Una ruta admite máximo {MaximoConexosPorRuta} servicios conexos."));
            return errores;
        }

        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < conexos.Count; i++)
        {
            var conexo = conexos[i];
            var id = conexo?.IdServicioConexo?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].id_servicio_conexo", clsCodigosLineaSolicitud.CampoRequerido,
                    $"El servicio conexo {i + 1} debe traer su id del catálogo de Nexo (id_servicio_conexo)."));
            }
            else if (id.Length > clsServicioConexoRuta.LargoMaximoId)
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].id_servicio_conexo", clsCodigosLineaSolicitud.CampoDemasiadoLargo,
                    $"El id del servicio conexo {i + 1} supera {clsServicioConexoRuta.LargoMaximoId} caracteres."));
            }
            else if (!vistos.Add(id))
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].id_servicio_conexo", clsCodigosLineaSolicitud.ConexoDuplicado,
                    $"El servicio conexo '{id}' está repetido en la ruta: indica la cantidad en una sola fila."));
            }

            if (conexo?.Cantidad is not { } cantidad)
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].cantidad", clsCodigosLineaSolicitud.CampoRequerido,
                    $"El servicio conexo {i + 1} debe traer la cantidad."));
            }
            else if (cantidad <= 0m || cantidad > CantidadMaximaConexo || cantidad != Math.Round(cantidad, clsSolicitudCosteo.Decimales))
            {
                errores.Add(new clsErrorCampo($"servicios_conexos[{i}].cantidad", clsCodigosLineaSolicitud.ValorFueraDeRango,
                    $"La cantidad del servicio conexo {i + 1} debe ser mayor que 0 y hasta {CantidadMaximaConexo} (máximo {clsSolicitudCosteo.Decimales} decimales)."));
            }
        }

        return errores;
    }
}

/// <summary>Catálogo de tipos de mercancía del cubicador leído en una captura: por código y por el uuid obsoleto.</summary>
internal sealed record clsCatalogoMercancias(
    IReadOnlyDictionary<Guid, clsTipoMercanciaCubicador> PorId, IReadOnlyDictionary<string, clsTipoMercanciaCubicador> PorCodigo)
{
    /// <summary>El tipo de la mercancía: por su código o, si no trae, por el <c>id_tipo_mercancia</c> obsoleto; <c>null</c> si no está.</summary>
    public clsTipoMercanciaCubicador? De(clsPosicionCarga posicion) =>
        !string.IsNullOrWhiteSpace(posicion.CodigoMercancia)
            ? PorCodigo.GetValueOrDefault(posicion.CodigoMercancia.Trim())
            : posicion.IdTipoMercancia is { } id ? PorId.GetValueOrDefault(id) : null;
}
