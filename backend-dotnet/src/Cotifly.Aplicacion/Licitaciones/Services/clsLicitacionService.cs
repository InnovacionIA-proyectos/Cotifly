using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Cubicacion.Services;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// Licitaciones desde Excel (ADR-017): analizar el pliego en Nexo, crear la cotización con sus N líneas y enviarla a Nexo
/// como UNA solicitud, y verla línea por línea. Solo cotizaciones propias (<c>COTIZACION_NO_ENCONTRADA</c> → 404), salvo la vista y el
/// Excel de la licitación: también el gerente de la regional del dueño y el director, en solo lectura.
/// </summary>
public interface IClsLicitacionService
{
    /// <summary>
    /// Reenvía el Excel al análisis de Nexo (no guarda nada). <c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c> y
    /// <c>ARCHIVO_DEMASIADO_GRANDE</c> se validan antes de llamar a Nexo; los errores de lectura los devuelve Nexo. Cada
    /// fila trae el <c>codigoMercancia</c> de la columna de código del Excel o, si no hay, el más parecido del catálogo del cubicador,
    /// marcado como sugerido (<see cref="clsHojaExcel.Mercancias"/>). Sin catálogo, el análisis no falla.
    /// </summary>
    Task<clsResultado<clsAnalisisExcel>> AnalizarAsync(Guid usuarioId, clsArchivoSubido archivo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Valida TODA la licitación (reporte por fila si algo falla, nada guardado), crea la cotización <c>licitacion</c> con sus líneas,
    /// posiciones y el Excel original en <c>pendiente_envio</c>, la confirma y la envía a Nexo con N líneas. Si Nexo falla la
    /// cotización se devuelve igual, en <c>error_envio</c> (se reintenta con <c>POST /web/cotizaciones/{folio}/costeo/reintentar</c>).
    /// En USD (ADR-014 §5) consulta antes la TRM del día y la guarda en <c>trmAplicada</c>; sin TRM, <c>TRM_NO_DISPONIBLE</c> y nada se guarda.
    /// Cada fila se valida además por código de catálogo (<c>CODIGO_NO_CATALOGO</c>) y con las reglas condicionales de
    /// captura, y su origen y destino se resuelven a ids de municipio de Nexo (<see cref="clsMunicipiosLicitacion"/>). Sin catálogo
    /// maestro utilizable, 503 <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>; sin catálogo de municipios (si alguna fila lo necesita), 503
    /// <c>MUNICIPIOS_NO_DISPONIBLE</c>; en ambos casos nada se guarda. Si además hay errores que se detectan sin ese catálogo, se
    /// devuelve el reporte con todos ellos y el código del catálogo caído en <c>error.detalle.motivo</c>.
    /// </summary>
    Task<clsResultadoLicitacion> CrearAsync(Guid usuarioId, clsEntradaLicitacion entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="CrearAsync"/> con <c>Idempotency-Key</c> opcional (<see cref="IClsAlmacenIdempotencia"/>).
    /// Sin clave (o sin almacén), llama directo a <see cref="CrearAsync"/>. Con clave, la repetición con la misma clave y el mismo
    /// cuerpo+archivo devuelve el mismo folio releyendo la licitación (201 con su estado de ahora). Solo el 201 (licitación creada)
    /// consume la clave: un reporte por fila (400) o un 503 NO la consumen, así la repetición se vuelve a evaluar. La misma clave con
    /// otro cuerpo u otro archivo es <c>IDEMPOTENCY_KEY_REUTILIZADA</c> (409) y fuera de largo (8 a 64), <c>SOLICITUD_INVALIDA</c> (400).
    /// La huella es el cuerpo más el nombre y el SHA-256 del archivo (no los bytes). Con <see cref="IClsRegistroCapturasIdempotentes"/>, el
    /// folio queda asociado a la clave en la misma transacción que crea la licitación: si la API se cae antes de responder, la repetición
    /// (pasada la toma en curso) relee esa licitación y responde 201 con el mismo folio, sin crear otra.
    /// </summary>
    Task<clsResultadoLicitacion> CrearUnaVezAsync(
        Guid usuarioId, clsEntradaLicitacion entrada, string? claveIdempotencia, CancellationToken cancellationToken = default);

    /// <summary>
    /// La licitación con el estado y el precio de cada línea (nunca el costo de Nexo), con los camiones de cada ruta, el
    /// progreso de la cubicación en segundo plano y los avisos del cubicador. Además del dueño, la ve en solo
    /// lectura (<see cref="clsLicitacion.SoloLectura"/>) el usuario autenticado si es el director o el gerente de la regional del dueño
    /// (<see cref="Cotizaciones.Services.IClsAccesoCotizacionAjena"/>, auditado con <see cref="clsLicitacionService.HerramientaConsulta"/>).
    /// </summary>
    Task<clsResultado<clsLicitacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Excel de la licitación con los resultados: el original del cliente con las columnas de Cotifly agregadas (o una hoja
    /// nueva si no hay original). Se puede exportar en cualquier momento: las líneas sin costeo quedan marcadas. <c>COTIZACION_NO_ENCONTRADA</c>
    /// si no es suya o no tiene líneas. El director o el gerente de la regional del dueño reciben el mismo
    /// archivo (nunca trae costos), auditado con <see cref="clsLicitacionService.HerramientaExcel"/>.
    /// </summary>
    Task<clsResultado<clsArchivoExportado>> ExportarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Para "Ver detalle": el mismo Excel de <see cref="ExportarAsync"/> (una fila por ruta con sus
    /// camiones, la hoja "Camiones Cotifly" y el resumen) para CUALQUIER cotización propia con rutas, no solo una licitación. Sin Excel
    /// original, arma la hoja con los campos de cada ruta. <c>COTIZACION_NO_ENCONTRADA</c> si no es suya o no tiene rutas.
    /// </summary>
    Task<clsResultado<clsArchivoExportado>> ExportarCotizacionAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsLicitacionService"/>.</summary>
public sealed class clsLicitacionService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenResolverService margen,
    IClsGeneradorFolioService folios,
    IClsUnitOfWork unidadTrabajo,
    IClsEnvioCosteoService envio,
    IClsAnalizadorExcelNexo analizador,
    IClsLectorExcelLicitacion lector,
    IClsExportadorExcelLicitacion exportador,
    IClsReloj reloj,
    IClsFijacionMonedaService monedas,
    IClsCapturaRutasService captura,
    ILogger<clsLicitacionService> logger,
    Cotizaciones.Services.IClsAccesoCotizacionAjena ajenas,
    IOptions<clsOpcionesCaptura>? opcionesCaptura = null,
    IClsCubicador? cubicador = null,
    IOptions<clsOpcionesCubicacionLicitacion>? opcionesCubicacion = null,
    IClsAlmacenIdempotencia? idempotencia = null,
    Costeo.Services.IClsCategoriasConexos? categoriasConexos = null,
    IClsRegistroCapturasIdempotentes? registroIdempotente = null) : IClsLicitacionService
{
    public const string TipoXlsx = clsArchivoLicitacion.TipoXlsx;

    /// <summary>Ámbito de las claves de idempotencia de crear una licitación (con el comercial).</summary>
    public const string OperacionIdempotencia = "crear-licitacion";

    /// <summary>Auditoría de la consulta de una licitación ajena (gerente o director), permitida o no.</summary>
    public const string HerramientaConsulta = "consultar_licitacion";

    /// <summary>Auditoría de la descarga del Excel de una licitación ajena (gerente o director), permitida o no.</summary>
    public const string HerramientaExcel = "exportar_excel_licitacion";

    public Task<clsResultado<clsArchivoExportado>> ExportarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default) =>
        ExportarAsync(usuarioId, folio, soloLicitacion: true, cancellationToken);

    public Task<clsResultado<clsArchivoExportado>> ExportarCotizacionAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default) =>
        ExportarAsync(usuarioId, folio, soloLicitacion: false, cancellationToken);

    /// <summary>
    /// La cotización propia de <paramref name="usuarioId"/> o, si no es suya y hay <paramref name="herramienta"/>, la ajena que puede ver en
    /// solo lectura (director o gerente de la regional del dueño, auditado). El dueño primero: un comercial ajeno nunca
    /// carga el agregado de otro.
    /// </summary>
    private async Task<(clsCotizacionSolicitud? Cotizacion, bool SoloLectura)> CargarAsync(
        Guid usuarioId, string folio, string? herramienta, CancellationToken cancellationToken)
    {
        if (usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio))
        {
            return (null, false);
        }

        var propia = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (propia is not null || herramienta is null)
        {
            return (propia, false);
        }

        // Solo licitaciones: un folio spot de la regional no se audita como "permitido" para responder 404 después.
        var ajena = await ajenas.ObtenerAsync(usuarioId, folio, herramienta, TipoSolicitud.Licitacion, cancellationToken);
        return (ajena, ajena is not null);
    }

    private async Task<clsResultado<clsArchivoExportado>> ExportarAsync(
        Guid usuarioId, string folio, bool soloLicitacion, CancellationToken cancellationToken)
    {
        // El Excel de la licitación también para el gerente de la regional y el director (el mismo archivo: no trae
        // costos); el de "Ver detalle" (ExportarCotizacionAsync) sigue siendo solo del dueño.
        var (cotizacion, _) = await CargarAsync(usuarioId, folio, soloLicitacion ? HerramientaExcel : null, cancellationToken);
        if (cotizacion is null || cotizacion.Rutas.Count == 0 || (soloLicitacion && cotizacion.TipoSolicitud != TipoSolicitud.Licitacion))
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var archivo = cotizacion.TipoSolicitud == TipoSolicitud.Licitacion
            ? await cotizaciones.ObtenerArchivoLicitacionAsync(cotizacion.Id, cancellationToken)
            : null;
        var nombres = await NombresCubicadorAsync(cotizacion, cancellationToken);
        // Los conexos heredados sin nombre guardado toman el nombre del catálogo de Nexo (nunca el id).
        var nombresConexos = await Costeo.Services.clsCosteoCotizacionService.NombresAsync(categoriasConexos, cotizacion, logger, cancellationToken);
        // Una licitación leída con la IA de NEXO (leer_estructura) se exporta en un Excel NUEVO (sin pasar el original).
        var leidaConIa = archivo is { OrigenLectura: clsOrigenLectura.LeerEstructura };
        var datos = DatosExportacion(cotizacion, leidaConIa ? null : archivo, reloj.AhoraUtc, nombres.Vehiculos, nombres.Carrocerias, nombresConexos, leidaConIa);
        var generado = await exportador.GenerarAsync(datos, cancellationToken);
        if (generado.EsFallo)
        {
            return generado.Error;
        }

        return new clsArchivoExportado(cotizacion.Folio + ".xlsx", generado.Valor, TipoXlsx);
    }

    /// <summary>
    /// Datos de la exportación (una fila por ruta): la tarifa del transporte de la ruta (sin conexos; el PDF del pliego, en cambio, imprime el valor final de la ruta con los conexos incluidos),
    /// estado legible y los servicios conexos de las rutas con su precio, aparte (cada uno con su línea, y su suma en la línea). El total es la suma de las tarifas (sin conexos):
    /// los conexos no se cobran dos veces; tarifas + conexos + seguro dan <c>precio_final</c>, que también va en los datos. Origen y destino son los nombres copiados de la ruta; el vehículo, el de su camión (o los de sus camiones). El costo de
    /// Nexo no entra a este objeto: el exportador no lo puede escribir. Cada línea lleva sus camiones con el nombre del
    /// vehículo y de la carrocería del catálogo del cubicador (<paramref name="vehiculos"/> y <paramref name="carrocerias"/>, por código; si
    /// no están, el código) y el estado de su cubicación.
    /// </summary>
    public static clsExportacionLicitacion DatosExportacion(
        clsCotizacionSolicitud cotizacion,
        clsArchivoLicitacion? archivo,
        DateTimeOffset ahora,
        IReadOnlyDictionary<string, string>? vehiculos = null,
        IReadOnlyDictionary<string, string>? carrocerias = null,
        IReadOnlyDictionary<string, string>? nombresConexos = null,
        bool leidaConIa = false)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var lineas = cotizacion.RutasOrdenadas;
        // Una ruta rechazada por la Mesa de Costeo sale "SIN COSTEO" (sin precio ni motivo), sin sus conexos, y no suma.
        var tarifas = lineas.Select(l => l.Rechazada ? null : Costeo.Services.clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)).ToList();
        var cotizables = lineas.Select((l, i) => (Ruta: l, Tarifa: tarifas[i])).Where(x => !x.Ruta.Rechazada).ToList();
        // ConexosConPrecio pobla Nombre con el guardado o el del catálogo de Nexo por id (nombresConexos)
        // y cae a la categoría como respaldo legible; el export nunca imprime el id interno.
        // Una ruta rechazada no tiene conexos con precio (ConexosConPrecio la deja vacía).
        var conexosPorLinea = lineas.Select(r => Costeo.Services.clsCosteoCotizacionService.ConexosConPrecio(cotizacion, r, nombres: nombresConexos)).ToList();
        var conexos = lineas
            .SelectMany((r, i) => conexosPorLinea[i].Select(s => new clsConexoExportacion(s.Nombre ?? s.Categoria, s.Categoria, s.Cantidad, s.Precio)
            {
                LineaOrden = r.Orden,
            }))
            .ToList();
        return new clsExportacionLicitacion(
            cotizacion.Folio,
            cotizacion.ClienteNombre,
            cotizacion.NitCliente,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            cotizacion.TrmAplicada,
            cotizables.Count > 0 && cotizables.All(x => x.Tarifa is not null) ? cotizables.Sum(x => x.Tarifa!.Value) : null,
            archivo?.Hoja,
            archivo?.Contenido,
            [.. lineas.Select((l, i) => new clsLineaExportacion(
                l.Orden, l.RutaOrigenIndice, l.OrigenNombre ?? string.Empty, l.DestinoNombre ?? string.Empty, TipoVehiculo(l), l.PesoKg, EstadoLegible(l), tarifas[i],
                CamionesExportacion(l, vehiculos, carrocerias), CubicacionLegible(l))
            {
                Rechazada = l.Rechazada,
                ValorSeguro = Costeo.Services.clsCosteoCotizacionService.ValorSeguroPdf(cotizacion, l),
                ValorConexos = conexosPorLinea[i].Count == 0 ? null : conexosPorLinea[i].Sum(c => c.Precio),
            })],
            conexos,
            ahora)
        {
            LeidaConIa = leidaConIa,
            PrecioFinal = cotizacion.PrecioFinal,
        };
    }

    /// <summary>Los camiones de la ruta para el Excel, con los nombres del catálogo del cubicador (o el código).</summary>
    public static IReadOnlyList<clsCamionExportacion> CamionesExportacion(
        clsLineaSolicitud ruta, IReadOnlyDictionary<string, string>? vehiculos = null, IReadOnlyDictionary<string, string>? carrocerias = null)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        static string? Nombre(string? codigo, IReadOnlyDictionary<string, string>? catalogo) =>
            clsNombresCatalogoCubicadorExtensiones.Nombre(catalogo, codigo);
        return [.. ruta.VehiculosOrdenados.Select((v, i) => new clsCamionExportacion(
            i + 1, Nombre(v.TipoVehiculo, vehiculos), Nombre(v.Carroceria, carrocerias), v.PesoKg, v.VolumenTotalM3,
            v.PorcentajeLlenadoVolumen, v.PorcentajeLlenadoPeso)
        {
            // La hoja "Camiones Cotifly" marca el camión rechazado por la Mesa (sin el motivo).
            Rechazado = v.Rechazado,
        })];
    }

    /// <summary>Estado de la cubicación en segundo plano para el Excel (<c>null</c> si la ruta se cubicó al guardarla).</summary>
    public static string? CubicacionLegible(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return ruta.EstadoCubicacion switch
        {
            EstadoCubicacion.Pendiente => "Cubicando",
            EstadoCubicacion.Fallida => "Cubicación fallida: " + (ruta.ErrorCubicacion ?? "sin detalle"),
            EstadoCubicacion.Cubicada => "Cubicada",
            _ => null,
        };
    }

    /// <summary>
    /// Nombres de los vehículos y carrocerías del catálogo del cubicador (por código), como el PDF. Sin cubicador, sin camiones
    /// con código o con el catálogo caído, vacíos: el Excel muestra el código.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, string>? Vehiculos, IReadOnlyDictionary<string, string>? Carrocerias)> NombresCubicadorAsync(
        clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (cubicador is not { EstaConfigurado: true }
            || !cotizacion.Rutas.SelectMany(r => r.Vehiculos).Any(v => v.TipoVehiculo is not null || v.Carroceria is not null))
        {
            return (null, null);
        }

        var vehiculos = await cubicador.ListarTiposVehiculoAsync(cancellationToken);
        var carrocerias = await cubicador.ListarTiposCarroceriaAsync(cancellationToken);
        return (
            vehiculos.EsExitoso ? PorCodigo(vehiculos.Valor.Select(v => (v.Codigo, v.Nombre))) : null,
            carrocerias.EsExitoso ? PorCodigo(carrocerias.Valor.Select(c => (c.Codigo, c.Nombre))) : null);
    }

    private static Dictionary<string, string> PorCodigo(IEnumerable<(string Codigo, string Nombre)> filas)
    {
        var diccionario = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (codigo, nombre) in filas)
        {
            if (!string.IsNullOrWhiteSpace(codigo) && !string.IsNullOrWhiteSpace(nombre))
            {
                diccionario.TryAdd(codigo, nombre.Trim());
            }
        }

        return diccionario;
    }

    /// <summary>Estado legible de una ruta rechazada por la Mesa de Costeo de Nexo (sin el motivo).</summary>
    public const string EstadoRechazadaEnNexo = "Rechazada en NEXO";

    /// <summary>
    /// Estado de la línea para el Excel: <c>Costeada</c> solo si tiene precio con margen. Una ruta rechazada por la Mesa
    /// (todos sus camiones) es <see cref="EstadoRechazadaEnNexo"/>, sin el motivo.
    /// </summary>
    public static string EstadoLegible(clsLineaSolicitud linea)
    {
        ArgumentNullException.ThrowIfNull(linea);
        if (linea.Rechazada)
        {
            return EstadoRechazadaEnNexo;
        }

        if (linea.PrecioFinalLinea is not null)
        {
            return "Costeada";
        }

        return linea.EstadoCosteo switch
        {
            EstadoCosteo.Descartada => "Descartada en Nexo",
            EstadoCosteo.CosteoVencido => "Costeo vencido",
            EstadoCosteo.ErrorEnvio => "No llegó a Nexo",
            EstadoCosteo.Entregada => "Entregada sin flete",
            _ => "Pendiente de costeo",
        };
    }

    public async Task<clsResultado<clsAnalisisExcel>> AnalizarAsync(Guid usuarioId, clsArchivoSubido archivo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "El análisis exige el comercial autenticado.");
        }

        if (clsArchivoLicitacion.ValidarArchivo(archivo.Nombre, archivo.Contenido) is { } invalido)
        {
            return invalido;
        }

        // La lectura local va ANTES de Nexo. Pasa por los controles del Excel subido (bomba zip, libro denso, XXE, .xls
        // renombrado, cupo de lecturas): un rechazo responde aquí y el archivo nunca sale a Nexo. Se lee una sola vez, con el cupo
        // liberado antes de llamar a Nexo (que puede tardar), y sus filas de Excel se cruzan después con el análisis de Nexo.
        var porHoja = await lector.FilasDeDatosPorHojaAsync(archivo.Contenido, cancellationToken);
        if (porHoja.EsFallo)
        {
            return porHoja.Error;
        }

        var analisis = await analizador.AnalizarAsync(usuarioId, clsArchivoLicitacion.NombreLimpio(archivo.Nombre), archivo.Contenido, cancellationToken);
        return analisis.EsFallo
            ? analisis
            : await ConMercanciasAsync(ConFilasExcel(analisis.Valor, porHoja.Valor), cancellationToken);
    }

    /// <summary>
    /// A cada hoja le agrega el código de mercancía de cada fila (<see cref="clsHojaExcel.Mercancias"/>): el de la columna
    /// de código del Excel tal cual o, si no hay, el más parecido del catálogo del cubicador (<see cref="clsSugeridorMercancia"/>),
    /// marcado como sugerido. El catálogo se lee una vez (con caché) y solo si alguna hoja tiene una columna de mercancía; si no responde,
    /// el análisis sigue sin sugerencias (<see cref="clsAnalisisExcel.CatalogoMercanciasDisponible"/> = <c>false</c>).
    /// </summary>
    private async Task<clsAnalisisExcel> ConMercanciasAsync(clsAnalisisExcel analisis, CancellationToken cancellationToken)
    {
        // La lectura del catálogo y la detección viven en clsDetectorMercanciaExcel (compartidas con el Excel de mercancías
        // y con las tablas de leer-estructura).
        var resultado = await clsDetectorMercanciaExcel.ConMercanciasAsync(analisis.Hojas, cubicador, logger, cancellationToken);
        return resultado.Aplicado
            ? analisis with { CatalogoMercanciasDisponible = resultado.CatalogoDisponible, Hojas = resultado.Hojas }
            : analisis;
    }

    /// <summary>Por cada fila con mercancía (texto o código), su código (<see cref="clsDetectorMercanciaExcel.Mercancias"/>).</summary>
    public static IReadOnlyList<clsCodigoMercanciaFila> MercanciasDeHoja(
        clsHojaExcel hoja,
        int? columnaTexto,
        int? columnaCodigo,
        IReadOnlyList<clsTipoMercanciaCubicador>? catalogo,
        IReadOnlyDictionary<string, clsTipoMercanciaCubicador>? porCodigo = null)
    {
        ArgumentNullException.ThrowIfNull(hoja);
        return clsDetectorMercanciaExcel.Mercancias(hoja.Filas, columnaTexto, columnaCodigo, catalogo, porCodigo);
    }

    /// <summary>La columna de la descripción de la mercancía (<see cref="clsDetectorMercanciaExcel.ColumnaMercancia"/>).</summary>
    public static int? ColumnaMercancia(IReadOnlyList<string> encabezados) => clsDetectorMercanciaExcel.ColumnaMercancia(encabezados);

    /// <summary>La columna del código de la mercancía (<see cref="clsDetectorMercanciaExcel.ColumnaCodigoMercancia"/>).</summary>
    public static int? ColumnaCodigoMercancia(IReadOnlyList<string> encabezados) => clsDetectorMercanciaExcel.ColumnaCodigoMercancia(encabezados);

    /// <summary>
    /// Agrega a cada hoja del análisis de Nexo el número de fila de Excel de cada una de sus filas (<c>filas_excel</c>),
    /// con la misma regla de detección (<see cref="IClsLectorExcelLicitacion"/>), para que el frontend muestre la numeración del archivo
    /// del comercial. <paramref name="porHoja"/> es la lectura local, hecha antes de llamar a Nexo (si Cotifly no puede
    /// leer el archivo, se rechaza antes de Nexo). Si una hoja no se pudo ubicar (o pasa de las 20 que se ubican) o no cuadra con lo que
    /// devolvió Nexo (otro total de filas), solo esa queda sin <c>filas_excel</c> y las demás lo conservan.
    /// </summary>
    private static clsAnalisisExcel ConFilasExcel(clsAnalisisExcel analisis, IReadOnlyDictionary<string, IReadOnlyList<int>> porHoja) =>
        analisis with
        {
            Hojas = [.. analisis.Hojas.Select(h => porHoja.TryGetValue(h.Nombre, out var filas) && Cuadra(filas.Count, h)
                ? h with { FilasExcel = [.. filas.Take(h.Filas.Count)] }
                : h)],
        };

    /// <summary>
    /// ¿Las filas con datos que ubicó Cotifly cuadran con la hoja de Nexo? Se compara con el total real (<c>TotalFilas</c>), no con
    /// <c>Filas</c>, que Nexo corta en 1000; como Cotifly también se detiene en 1000, una hoja de más de 1000 filas cuadra si los dos
    /// llegaron al tope. Además debe haber un número de fila por cada fila que devolvió Nexo.
    /// </summary>
    public static bool Cuadra(int ubicadas, clsHojaExcel hoja)
    {
        ArgumentNullException.ThrowIfNull(hoja);
        var mismoTotal = ubicadas == hoja.TotalFilas
            || (ubicadas == clsValidadorLicitacion.MaximoLineas && hoja.TotalFilas > clsValidadorLicitacion.MaximoLineas);
        return mismoTotal && ubicadas >= hoja.Filas.Count;
    }

    public async Task<clsResultadoLicitacion> CrearUnaVezAsync(
        Guid usuarioId, clsEntradaLicitacion entrada, string? claveIdempotencia, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (claveIdempotencia is null || idempotencia is null)
        {
            return await CrearAsync(usuarioId, entrada, cancellationToken);
        }

        // El almacén guarda solo el folio (texto). Si la acción corrió en esta petición se devuelve su resultado completo (incluido el
        // reporte por fila, que NO consume la clave); si es una repetición de un 201, la licitación guardada se relee por su folio.
        var ambito = $"{OperacionIdempotencia}:{usuarioId}";
        var huella = Huella(entrada);
        clsResultadoLicitacion? ejecutada = null;
        var guardado = await idempotencia.EjecutarUnaVezAsync<string>(
            ambito,
            claveIdempotencia,
            huella,
            async ct =>
            {
                // La clave ya tiene una licitación confirmada: la petición que la creó se cortó antes de cerrar la toma (la API se cayó
                // entre el folio y la respuesta). Se responde con ese folio y nunca se crea otra licitación ni se vuelve a enviar a Nexo.
                if (registroIdempotente is not null && await registroIdempotente.BuscarAsync(ambito, claveIdempotencia, ct) is { } previa)
                {
                    return string.Equals(previa.Huella, huella, StringComparison.OrdinalIgnoreCase)
                        ? previa.Folio
                        : new clsErrorDominio(clsCodigosAplicacion.IdempotencyKeyReutilizada, clsCodigosAplicacion.MensajeIdempotencyKeyReutilizada);
                }

                var resultado = await CrearInternoAsync(usuarioId, entrada, registroIdempotente is null
                    ? null
                    : (c, ctRegistro) => registroIdempotente.RegistrarAsync(ambito, claveIdempotencia,
                        new clsCapturaIdempotente(huella, c.Id, c.Folio, null, reloj.AhoraUtc), ctRegistro), ct);
                ejecutada = resultado;
                return resultado.Cotizacion is { } creada
                    ? (clsResultado<string>)creada.Folio
                    : resultado.Error ?? new clsErrorDominio(clsCodigosError.ErrorInterno, "No se pudo crear la licitación.");
            },
            // Solo una licitación creada (o la ya registrada con la clave) consume la clave; el reporte por fila (400) y el 503 NO.
            r => r.EsExitoso,
            cancellationToken);
        if (guardado.EsFallo)
        {
            // La acción corrió y falló (reporte por fila o error de negocio): se devuelve su resultado completo; si no corrió (misma clave
            // con otra huella → 409, o fuera de largo → 400), el error del almacén.
            return ejecutada ?? clsResultadoLicitacion.Fallo(guardado.Error);
        }

        if (ejecutada is { Cotizacion: not null })
        {
            return ejecutada;
        }

        // Repetición de un 201: se relee la licitación por su folio y se responde 201 con su estado de ahora.
        var cotizacion = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(guardado.Valor, usuarioId, cancellationToken);
        return cotizacion is null
            ? clsResultadoLicitacion.Fallo(new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya."))
            : clsResultadoLicitacion.Exito(cotizacion);
    }

    /// <summary>
    /// Huella (SHA-256) de la petición de crear una licitación: el cuerpo (sin los bytes del archivo) más el nombre y el
    /// SHA-256 del archivo. La misma clave con otro cuerpo u otro archivo es <c>IDEMPOTENCY_KEY_REUTILIZADA</c> (409).
    /// </summary>
    public static string Huella(clsEntradaLicitacion entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        var archivo = entrada.Archivo;
        return clsHuellaIdempotencia.Calcular(new
        {
            entrada.NitCliente,
            entrada.ClienteNombre,
            entrada.LineaNegocio,
            entrada.Encabezado,
            entrada.Hoja,
            entrada.Lineas,
            entrada.Moneda,
            entrada.OrigenLectura,
            archivo_nombre = archivo?.Nombre,
            archivo_sha256 = archivo is null ? null : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(archivo.Contenido)),
        });
    }

    public Task<clsResultadoLicitacion> CrearAsync(Guid usuarioId, clsEntradaLicitacion entrada, CancellationToken cancellationToken = default) =>
        CrearInternoAsync(usuarioId, entrada, null, cancellationToken);

    /// <summary>
    /// <see cref="CrearAsync"/> con <paramref name="alConfirmar"/>: se llama con la licitación recién guardada DENTRO de la transacción
    /// que confirma el folio, antes de enviarla a Nexo. Así la <c>Idempotency-Key</c> queda asociada al folio junto con la licitación
    /// (o no queda ninguna de las dos).
    /// </summary>
    private async Task<clsResultadoLicitacion> CrearInternoAsync(
        Guid usuarioId,
        clsEntradaLicitacion entrada,
        Func<clsCotizacionSolicitud, CancellationToken, Task>? alConfirmar,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(entrada.Encabezado);
        if (usuarioId == Guid.Empty)
        {
            return clsResultadoLicitacion.Fallo(new clsErrorDominio(clsCodigosError.ErrorInterno, "La licitación exige el comercial autenticado."));
        }

        if (entrada.Encabezado.Tipo != TipoSolicitud.Licitacion)
        {
            return clsResultadoLicitacion.Fallo(new clsErrorDominio(clsCodigosError.LicitacionInvalida, "El tipo de una licitación es 'licitacion'."));
        }

        // origen_lectura decide cómo se mapea la fila y cómo se exporta. Vacío = analizar_excel;
        // un valor desconocido es 400 VALIDATION_ERROR en origen_lectura (el mismo código del contrato), con detalle.campos.
        if (!clsOrigenLectura.EsValido(entrada.OrigenLectura))
        {
            return clsResultadoLicitacion.Fallo(new clsErrorDominio(
                clsOrigenLectura.CodigoValidacion,
                $"El campo 'origen_lectura' debe ser '{clsOrigenLectura.AnalizarExcel}' o '{clsOrigenLectura.LeerEstructura}'.")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["campos"] = new List<string> { "origen_lectura" } },
            });
        }

        var leerEstructura = clsOrigenLectura.EsLeerEstructura(entrada.OrigenLectura);

        // El vehículo elegido de cada fila es el respaldo del cubicador (y obligatorio en una fila sin mercancías
        // cubicables, salvo Contenedor). Con Captura:AceptarVehiculoCliente apagada se ignora.
        entrada = entrada with
        {
            Lineas = [.. clsEnvioCosteoService.SinVehiculo(entrada.Lineas ?? [], logger, opcionesCaptura?.Value.AceptarVehiculoCliente ?? true).Select(l => l!)],
        };

        // El archivo se valida primero (un archivo ilegible invalida todas las filas) y se cuenta su hoja con la regla de Nexo.
        // La lectura pasa por los controles del Excel subido. Con archivo la hoja es obligatoria (sin ella, el reporte
        // no deja guardar nada): todo original que se guarda, y que la exportación abre después con ClosedXML, los pasó.
        IReadOnlyList<int>? filasExcel = null;
        if (entrada.Archivo is { } archivo)
        {
            if (clsArchivoLicitacion.ValidarArchivo(archivo.Nombre, archivo.Contenido) is { } invalido)
            {
                return clsResultadoLicitacion.Fallo(invalido);
            }

            // Con leer_estructura la 'hoja' es el nombre de la tabla (solo referencia) y la 'fila' es el índice dentro de la
            // tabla, no de la hoja: NO se relee el Excel original ni se calcula filas_excel (el archivo se guarda para exportarlo en una hoja nueva).
            if (!leerEstructura && !string.IsNullOrWhiteSpace(entrada.Hoja) && entrada.Hoja.Trim().Length <= clsArchivoLicitacion.LargoHoja)
            {
                var filas = await lector.FilasDeDatosAsync(archivo.Contenido, entrada.Hoja.Trim(), cancellationToken);
                if (filas.EsFallo)
                {
                    return clsResultadoLicitacion.Fallo(filas.Error);
                }

                filasExcel = filas.Valor;
            }
        }

        var validadas = await ValidarFilasAsync(usuarioId, entrada, filasExcel, cancellationToken);
        if (validadas.Errores.Count > 0)
        {
            // Con un catálogo caído se reportan igual los errores que se detectan sin él, con el código del catálogo en
            // detalle.motivo (lo que dependía de ese catálogo se valida al volver a enviarla). Cada error lleva su fila de Excel.
            // En el mismo reporte va la línea de negocio que no da margen ("Licitaciones" de Nexo, una que no existe o
            // inactiva), con las líneas válidas en detalle.lineas_validas, para corregir todo en un solo intento.
            var (deLinea, lineasValidas) = await ErroresDeLineaNegocioAsync(entrada, cancellationToken);
            var invalida = clsResultadoLicitacion.Invalida(
                clsValidadorLicitacion.ConFilaExcel([.. validadas.Errores, .. deLinea], filasExcel), validadas.NoDisponible?.Codigo);
            return lineasValidas is null
                ? invalida
                : invalida with
                {
                    Error = invalida.Error! with
                    {
                        Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["lineas_validas"] = lineasValidas },
                    },
                };
        }

        if (validadas.NoDisponible is { } noDisponible)
        {
            return clsResultadoLicitacion.Fallo(noDisponible);
        }

        var entradas = validadas.Lineas;

        // Cada fila puede traer su línea de negocio; sin ella, la del encabezado.
        var porNombre = new Dictionary<string, Dominio.Margen.clsMargenPolitica>(StringComparer.OrdinalIgnoreCase);
        var politicasPorRuta = new List<Dominio.Margen.clsMargenPolitica>(entradas.Count);
        foreach (var nombre in entradas.Select(l => (l.LineaNegocio ?? entrada.LineaNegocio)!.Trim()).Prepend(entrada.LineaNegocio!.Trim()))
        {
            if (porNombre.ContainsKey(nombre))
            {
                continue;
            }

            var resuelta = await margen.ResolverLineaAsync(nombre, cancellationToken);
            if (resuelta is null)
            {
                // "Licitaciones" es una línea del catálogo de Nexo, no de Cotifly; el error dice qué líneas usar.
                return clsResultadoLicitacion.Fallo(clsMargenResolverService.LineaDesconocida(
                    nombre, await margen.LineasActivasAsync(cancellationToken) ?? [], esLicitacion: true));
            }

            if (!resuelta.Activa)
            {
                // También aquí el error trae las líneas que sí se pueden usar.
                return clsResultadoLicitacion.Fallo(clsMargenResolverService.LineaInactiva(resuelta.LineaNegocio) with
                {
                    Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["lineas_validas"] = await margen.LineasActivasAsync(cancellationToken) ?? [],
                    },
                });
            }

            // La línea de Nexo de las filas de esa línea debe ser una de las seis (se envía la de cada ruta).
            if (clsMargenResolverService.LineaNexoInvalida(resuelta) is { } lineaNexo)
            {
                return clsResultadoLicitacion.Fallo(lineaNexo);
            }

            porNombre[nombre] = resuelta;
        }

        politicasPorRuta.AddRange(entradas.Select(l => porNombre[(l.LineaNegocio ?? entrada.LineaNegocio)!.Trim()]));
        var politica = porNombre[entrada.LineaNegocio!.Trim()];

        // Conexos con el costo del catálogo de Nexo y los camiones de cada ruta (cubicador con presupuesto de tiempo, o respaldo).
        // Con el cubicador configurado, las rutas se guardan con un camión provisional y se cubican en segundo plano.
        var enSegundoPlano = CubicaEnSegundoPlano;
        var preparadas = await captura.PrepararAsync(
            usuarioId, [.. entradas.Select((l, i) => new clsRutaPorPreparar(l, politicasPorRuta[i].Id, l.Fila))], enSegundoPlano, cancellationToken);
        if (preparadas.Error is { } errorPreparacion)
        {
            return clsResultadoLicitacion.Fallo(errorPreparacion);
        }

        if (preparadas.Errores.Count > 0)
        {
            return clsResultadoLicitacion.Invalida(clsValidadorLicitacion.ConFilaExcel(preparadas.Errores, filasExcel));
        }

        // ADR-014 §5: en USD la TRM del día de creación queda fija en la licitación; el polling convierte cada precio con ella.
        var fijada = await monedas.FijarAsync(entrada.Moneda, cancellationToken);
        if (fijada.EsFallo)
        {
            return clsResultadoLicitacion.Fallo(fijada.Error);
        }

        var nit = entrada.NitCliente!.Trim();
        var creada = await folios.CrearConFolioNuevoAsync<clsCotizacionSolicitud>(usuarioId, nit, async (folio, ct) =>
        {
            var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
            {
                Folio = folio.Valor,
                Version = folio.Version,
                ClienteNombre = entrada.ClienteNombre,
                NitCliente = nit,
                UsuarioId = usuarioId,
                Moneda = fijada.Valor.Moneda,
                TrmAplicada = fijada.Valor.TrmAplicada,
            });
            if (cotizacion.EsFallo)
            {
                return cotizacion.Error;
            }

            // Una ruta por fila (rutaOrigenIndice = fila del Excel) con su línea de negocio, mercancías, camión y conexos.
            var id = cotizacion.Valor.Id;
            cotizacion.Valor.RegistrarCaptura(entrada.Encabezado, preparadas.Rutas);
            cotizaciones.Agregar(cotizacion.Valor);
            if (entrada.Archivo is { } original)
            {
                var guardado = clsArchivoLicitacion.Crear(id, original.Nombre, original.Contenido, entrada.Hoja, entrada.OrigenLectura);
                if (guardado.EsFallo)
                {
                    return guardado.Error;
                }

                cotizaciones.AgregarArchivoLicitacion(guardado.Valor);
            }

            await unidadTrabajo.GuardarCambiosAsync(ct);
            if (alConfirmar is not null)
            {
                // La clave se registra con el folio en la misma transacción que lo confirma (la del candado del folio).
                await alConfirmar(cotizacion.Valor, ct);
            }

            return cotizacion.Valor;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return clsResultadoLicitacion.Fallo(creada.Error);
        }

        if (enSegundoPlano)
        {
            // La solicitud sale a Nexo cuando la última ruta quede cubicada (clsCubicacionLicitacionService).
            logger.LogInformation("Licitación {Folio} guardada con {Rutas} ruta(s) por cubicar en segundo plano.", creada.Valor.Folio, creada.Valor.Rutas.Count);
            return clsResultadoLicitacion.Exito(creada.Valor);
        }

        // Confirmada: desde aquí, sin el token de la petición (ADR-015 §3).
        await envio.EnviarGuardadaAsync(creada.Valor, politica, usuarioId);
        return clsResultadoLicitacion.Exito(creada.Valor);
    }

    /// <summary>
    /// ¿La licitación se cubica en segundo plano? Con el cubicador configurado y <c>Cubicador:LicitacionEnSegundoPlano</c>
    /// (por defecto sí). Sin cubicador, el respaldo (un camión sin vehículo por ruta) no llama a nadie y la licitación sale de una vez.
    /// </summary>
    private bool CubicaEnSegundoPlano =>
        cubicador is { EstaConfigurado: true } && (opcionesCubicacion?.Value.LicitacionEnSegundoPlano ?? true);

    /// <summary>
    /// Todas las validaciones de las filas, juntas: las de forma y rango de <see cref="clsValidadorLicitacion"/> (con las condicionales) y
    /// las que dependen de catálogos (<see cref="IClsValidadorFilasLicitacion"/>: códigos de catálogo y municipios).
    /// </summary>
    private async Task<clsFilasLicitacionValidadas> ValidarFilasAsync(
        Guid usuarioId, clsEntradaLicitacion entrada, IReadOnlyList<int>? filasExcel, CancellationToken cancellationToken)
    {
        var errores = clsValidadorLicitacion.Validar(entrada, filasExcel?.Count, filasExcel);
        IReadOnlyList<clsLineaLicitacionEntrada?> lineas = entrada.Lineas ?? [];
        return lineas.Count is 0 or > clsValidadorLicitacion.MaximoLineas
            ? new clsFilasLicitacionValidadas([], errores, null)
            : await captura.ValidarAsync(usuarioId, lineas, errores, cancellationToken);
    }

    public async Task<clsResultado<clsLicitacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var (cotizacion, soloLectura) = await CargarAsync(usuarioId, folio, HerramientaConsulta, cancellationToken);
        // Un folio de formulario no es una licitación: 404, como reintentar y el Excel.
        if (cotizacion is null || cotizacion.TipoSolicitud != TipoSolicitud.Licitacion)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var archivo = await cotizaciones.ObtenerResumenArchivoLicitacionAsync(cotizacion.Id, cancellationToken);
        // La vista nunca trae costos; para el gerente o el director cambia solo solo_lectura.
        return Vista(cotizacion, archivo) with { SoloLectura = soloLectura };
    }

    /// <summary>
    /// Vista de la licitación, una fila por ruta (solo precios: el costo de Nexo de cada ruta nunca sale). Como el Excel (no como el PDF del
    /// pliego, que imprime el valor final con conexos), el precio de cada fila es la tarifa del transporte y el total su suma, sin servicios conexos (que se ven aparte en el
    /// costeo; el precio de la ruta ya los incluye).
    /// </summary>
    public static clsLicitacion Vista(clsCotizacionSolicitud cotizacion, clsArchivoLicitacionResumen? archivo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var ordenadas = cotizacion.RutasOrdenadas;
        // Una ruta rechazada por la Mesa no tiene precio, no es pendiente y no suma en el total.
        var tarifas = ordenadas.Select(l => l.Rechazada ? null : Costeo.Services.clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)).ToList();
        var cotizables = ordenadas.Select((l, i) => (Ruta: l, Tarifa: tarifas[i])).Where(x => !x.Ruta.Rechazada).ToList();
        return new clsLicitacion(
            cotizacion.Folio,
            cotizacion.ClienteNombre,
            cotizacion.NitCliente,
            // En UTC, como la captura, el costeo y "Mis cotizaciones" (la base lo guarda con el -05:00 de Bogotá).
            cotizacion.Deadline?.ToUniversalTime(),
            cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            cotizacion.MotivoEstadoCosteo,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            cotizacion.TrmAplicada,
            cotizacion.PrecioFinal is not null && cotizables.Count > 0 && cotizables.All(x => x.Tarifa is not null) ? cotizables.Sum(x => x.Tarifa!.Value) : null,
            cotizables.Count(x => x.Ruta.PrecioFinalLinea is not null),
            cotizables.Count(x => x.Ruta.PrecioFinalLinea is null),
            archivo?.NombreArchivo,
            archivo?.Hoja,
            [.. ordenadas.Select((l, i) => new clsLineaLicitacion(
                l.Orden, l.RutaOrigenIndice, l.OrigenNombre ?? string.Empty, l.DestinoNombre ?? string.Empty, TipoVehiculo(l), l.PesoKg,
                l.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null, l.PrecioFinalLinea is null ? null : tarifas[i],
                l.EstadoCubicacion is { } c ? clsCodigoEnum.ACodigo(c) : null, l.ErrorCubicacion, l.ResultadoCubicacion, l.MotivoSinSeleccion,
                l.Condicionada, clsCamionLicitacion.De(l))
            {
                MotivoRechazo = l.MotivoRechazo,
            })],
            clsProgresoCubicacion.De(cotizacion),
            Cotizaciones.DTOs.clsCubicacionRutaResumen.De(cotizacion))
        {
            LineasRechazadas = ordenadas.Count(l => l.Rechazada),
            FinalizadaAt = cotizacion.FinalizadaAt?.ToUniversalTime(),
            OrigenLectura = OrigenLecturaVista(archivo),
        };
    }

    /// <summary>
    /// El <c>origen_lectura</c> de la vista. <c>null</c> si la licitación no tiene archivo;
    /// <see cref="clsOrigenLectura.AnalizarExcel"/> si el archivo no guardó un <c>origenLectura</c> (anterior al script 22); si no, el valor.
    /// </summary>
    public static string? OrigenLecturaVista(clsArchivoLicitacionResumen? archivo) =>
        archivo is null ? null : clsOrigenLectura.Normalizar(archivo.OrigenLectura);

    /// <summary>
    /// Los errores de la línea de negocio para el reporte por fila, con la misma regla que el alta (sin política,
    /// <see cref="clsMargenResolverService.LineaDesconocida"/> con la explicación de "Licitaciones"; inactiva,
    /// <see cref="clsMargenResolverService.LineaInactiva"/>; activa pero con un mapeo a Nexo fuera de las seis líneas de Nexo,
    /// <see cref="clsMargenResolverService.LineaNexoInvalida(Dominio.Margen.clsMargenPolitica)"/>): la del encabezado con <c>indice = null</c> y la de cada fila que trae la suya
    /// con su índice, en el campo <c>linea_negocio</c>. Devuelve también las líneas activas si hubo algún error (<c>null</c> si no). Una
    /// línea vacía no se revisa aquí: ya la reporta la validación de las filas.
    /// </summary>
    private async Task<(List<clsErrorFilaLicitacion> Errores, IReadOnlyList<string>? LineasValidas)> ErroresDeLineaNegocioAsync(
        clsEntradaLicitacion entrada, CancellationToken cancellationToken)
    {
        var errores = new List<clsErrorFilaLicitacion>();
        var porNombre = new Dictionary<string, clsErrorDominio?>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string>? activas = null;
        var encabezado = entrada.LineaNegocio?.Trim();
        var candidatas = new List<(int? Indice, int? Fila, string? Nombre)> { (null, null, encabezado) };
        candidatas.AddRange((entrada.Lineas ?? []).Select((l, i) => ((int?)i, l?.Fila, l?.LineaNegocio)));
        foreach (var (indice, fila, nombre) in candidatas)
        {
            // Una fila que repite la línea del encabezado no suma otro error: ya lo dice el del encabezado.
            if (string.IsNullOrWhiteSpace(nombre)
                || (indice is not null && string.Equals(nombre.Trim(), encabezado, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var limpio = nombre.Trim();
            if (!porNombre.TryGetValue(limpio, out var error))
            {
                var resuelta = await margen.ResolverLineaAsync(limpio, cancellationToken);
                if (resuelta is null || !resuelta.Activa)
                {
                    activas ??= await margen.LineasActivasAsync(cancellationToken) ?? [];
                }

                error = resuelta is null
                    ? clsMargenResolverService.LineaDesconocida(limpio, activas!, esLicitacion: true)
                    : resuelta.Activa ? clsMargenResolverService.LineaNexoInvalida(resuelta) : clsMargenResolverService.LineaInactiva(resuelta.LineaNegocio);
                porNombre[limpio] = error;
            }

            if (error is not null)
            {
                errores.Add(new clsErrorFilaLicitacion(indice, fila, CampoLineaNegocio, error.Codigo, error.Mensaje));
            }
        }

        return (errores, errores.Count == 0 ? null : activas);
    }

    /// <summary>Campo de los errores de la línea de negocio en el reporte de la licitación.</summary>
    public const string CampoLineaNegocio = "linea_negocio";

    /// <summary>Vehículo de la ruta: el de su camión, o los de sus camiones separados por coma.</summary>
    private static string? TipoVehiculo(clsLineaSolicitud ruta)
    {
        var tipos = ruta.VehiculosOrdenados.Select(v => v.TipoVehiculo).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return tipos.Count == 0 ? null : string.Join(", ", tipos);
    }
}
