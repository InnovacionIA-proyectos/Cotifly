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
/// Licitaciones desde Excel (HU-011.2, ADR-017): analizar el pliego en Nexo, crear la cotización con sus N líneas y enviarla a Nexo
/// como UNA solicitud, y verla línea por línea. Solo cotizaciones propias (<c>COTIZACION_NO_ENCONTRADA</c> → 404).
/// </summary>
public interface IClsLicitacionService
{
    /// <summary>
    /// Reenvía el Excel al análisis de Nexo (no guarda nada). <c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c> y
    /// <c>ARCHIVO_DEMASIADO_GRANDE</c> se validan antes de llamar a Nexo; los errores de lectura los devuelve Nexo. HU-023.5 (#160): cada
    /// fila trae el <c>codigoMercancia</c> de la columna de código del Excel o, si no hay, el más parecido del catálogo del cubicador,
    /// marcado como sugerido (<see cref="clsHojaExcel.Mercancias"/>). Sin catálogo, el análisis no falla.
    /// </summary>
    Task<clsResultado<clsAnalisisExcel>> AnalizarAsync(Guid usuarioId, clsArchivoSubido archivo, CancellationToken cancellationToken = default);

    /// <summary>
    /// Valida TODA la licitación (reporte por fila si algo falla, nada guardado), crea la cotización <c>licitacion</c> con sus líneas,
    /// posiciones y el Excel original en <c>pendiente_envio</c>, la confirma y la envía a Nexo con N líneas. Si Nexo falla la
    /// cotización se devuelve igual, en <c>error_envio</c> (se reintenta con <c>POST /web/cotizaciones/{folio}/costeo/reintentar</c>).
    /// En USD (ADR-014 §5) consulta antes la TRM del día y la guarda en <c>trmAplicada</c>; sin TRM, <c>TRM_NO_DISPONIBLE</c> y nada se guarda.
    /// HU-013.11: cada fila se valida además por código de catálogo (G2, <c>CODIGO_NO_CATALOGO</c>) y con las reglas condicionales de
    /// G3, y su origen y destino se resuelven a ids de municipio de Nexo (G6, <see cref="clsMunicipiosLicitacion"/>). Sin catálogo
    /// maestro utilizable, 503 <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>; sin catálogo de municipios (si alguna fila lo necesita), 503
    /// <c>MUNICIPIOS_NO_DISPONIBLE</c>; en ambos casos nada se guarda. Si además hay errores que se detectan sin ese catálogo, se
    /// devuelve el reporte con todos ellos y el código del catálogo caído en <c>error.detalle.motivo</c>.
    /// </summary>
    Task<clsResultadoLicitacion> CrearAsync(Guid usuarioId, clsEntradaLicitacion entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// La licitación con el estado y el precio de cada línea (nunca el costo de Nexo). HU-023.5 (#160): con los camiones de cada ruta, el
    /// progreso de la cubicación en segundo plano y los avisos del cubicador.
    /// </summary>
    Task<clsResultado<clsLicitacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Excel de la licitación con los resultados (HU-011.3): el original del cliente con las columnas de Cotifly agregadas (o una hoja
    /// nueva si no hay original). Se puede exportar en cualquier momento: las líneas sin costeo quedan marcadas. <c>COTIZACION_NO_ENCONTRADA</c>
    /// si no es suya o no tiene líneas.
    /// </summary>
    Task<clsResultado<clsArchivoExportado>> ExportarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-023.5 (#160), para "Ver detalle" (HU-018.1, parte C): el mismo Excel de <see cref="ExportarAsync"/> (una fila por ruta con sus
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
    IOptions<clsOpcionesCaptura>? opcionesCaptura = null,
    IClsCubicador? cubicador = null,
    IOptions<clsOpcionesCubicacionLicitacion>? opcionesCubicacion = null) : IClsLicitacionService
{
    public const string TipoXlsx = clsArchivoLicitacion.TipoXlsx;

    public Task<clsResultado<clsArchivoExportado>> ExportarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default) =>
        ExportarAsync(usuarioId, folio, soloLicitacion: true, cancellationToken);

    public Task<clsResultado<clsArchivoExportado>> ExportarCotizacionAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default) =>
        ExportarAsync(usuarioId, folio, soloLicitacion: false, cancellationToken);

    private async Task<clsResultado<clsArchivoExportado>> ExportarAsync(
        Guid usuarioId, string folio, bool soloLicitacion, CancellationToken cancellationToken)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null || cotizacion.Rutas.Count == 0 || (soloLicitacion && cotizacion.TipoSolicitud != TipoSolicitud.Licitacion))
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var archivo = cotizacion.TipoSolicitud == TipoSolicitud.Licitacion
            ? await cotizaciones.ObtenerArchivoLicitacionAsync(cotizacion.Id, cancellationToken)
            : null;
        var nombres = await NombresCubicadorAsync(cotizacion, cancellationToken);
        var datos = DatosExportacion(cotizacion, archivo, reloj.AhoraUtc, nombres.Vehiculos, nombres.Carrocerias);
        var generado = exportador.Generar(datos);
        if (generado.EsFallo)
        {
            return generado.Error;
        }

        return new clsArchivoExportado(cotizacion.Folio + ".xlsx", generado.Valor, TipoXlsx);
    }

    /// <summary>
    /// Datos de la exportación (HU-021: una fila por ruta): la tarifa del transporte de la ruta (sin conexos; desde HU-033 el PDF del pliego, en cambio, imprime el valor final de la ruta con los conexos incluidos),
    /// estado legible y los servicios conexos de las rutas con su precio, aparte. El total es la suma de las tarifas (sin conexos), como
    /// el resumen anterior a HU-033: los conexos no se cobran dos veces. Origen y destino son los nombres copiados de la ruta; el vehículo, el de su camión (o los de sus camiones). El costo de
    /// Nexo no entra a este objeto: el exportador no lo puede escribir. HU-023.5 (#160): cada línea lleva sus camiones con el nombre del
    /// vehículo y de la carrocería del catálogo del cubicador (<paramref name="vehiculos"/> y <paramref name="carrocerias"/>, por código; si
    /// no están, el código) y el estado de su cubicación.
    /// </summary>
    public static clsExportacionLicitacion DatosExportacion(
        clsCotizacionSolicitud cotizacion,
        clsArchivoLicitacion? archivo,
        DateTimeOffset ahora,
        IReadOnlyDictionary<string, string>? vehiculos = null,
        IReadOnlyDictionary<string, string>? carrocerias = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var lineas = cotizacion.RutasOrdenadas;
        // HU-028.1 (#183): una ruta rechazada por la Mesa de Costeo sale "SIN COSTEO" (sin precio ni motivo), sin sus conexos, y no suma.
        var tarifas = lineas.Select(l => l.Rechazada ? null : Costeo.Services.clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)).ToList();
        var cotizables = lineas.Select((l, i) => (Ruta: l, Tarifa: tarifas[i])).Where(x => !x.Ruta.Rechazada).ToList();
        var conexos = lineas.Where(r => !r.Rechazada)
            .SelectMany(r => Costeo.Services.clsCosteoCotizacionService.ConexosConPrecio(cotizacion, r))
            .Select(s => new clsConexoExportacion(s.Nombre ?? s.Categoria, s.Categoria, s.Cantidad, s.Precio))
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
            })],
            conexos,
            ahora);
    }

    /// <summary>HU-023.5: los camiones de la ruta para el Excel, con los nombres del catálogo del cubicador (o el código).</summary>
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
            // HU-028.1 (#183): la hoja "Camiones Cotifly" marca el camión rechazado por la Mesa (sin el motivo).
            Rechazado = v.Rechazado,
        })];
    }

    /// <summary>HU-023.5: estado de la cubicación en segundo plano para el Excel (<c>null</c> si la ruta se cubicó al guardarla).</summary>
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
    /// Nombres de los vehículos y carrocerías del catálogo del cubicador (por código), como el PDF (HU-023.1). Sin cubicador, sin camiones
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

    /// <summary>HU-028.1 (#183): estado legible de una ruta rechazada por la Mesa de Costeo de Nexo (sin el motivo).</summary>
    public const string EstadoRechazadaEnNexo = "Rechazada en NEXO";

    /// <summary>
    /// Estado de la línea para el Excel: <c>Costeada</c> solo si tiene precio con margen. HU-028.1 (#183): una ruta rechazada por la Mesa
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

        var analisis = await analizador.AnalizarAsync(usuarioId, clsArchivoLicitacion.NombreLimpio(archivo.Nombre), archivo.Contenido, cancellationToken);
        return analisis.EsFallo
            ? analisis
            : await ConMercanciasAsync(ConFilasExcel(analisis.Valor, archivo.Contenido, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// HU-023.5 (#160): a cada hoja le agrega el código de mercancía de cada fila (<see cref="clsHojaExcel.Mercancias"/>): el de la columna
    /// de código del Excel tal cual o, si no hay, el más parecido del catálogo del cubicador (<see cref="clsSugeridorMercancia"/>, el de la #157),
    /// marcado como sugerido. El catálogo se lee una vez (con caché) y solo si alguna hoja tiene una columna de mercancía; si no responde,
    /// el análisis sigue sin sugerencias (<see cref="clsAnalisisExcel.CatalogoMercanciasDisponible"/> = <c>false</c>).
    /// </summary>
    private async Task<clsAnalisisExcel> ConMercanciasAsync(clsAnalisisExcel analisis, CancellationToken cancellationToken)
    {
        var columnas = analisis.Hojas.Select(h => (Hoja: h, Texto: ColumnaMercancia(h.Encabezados), Codigo: ColumnaCodigoMercancia(h.Encabezados))).ToList();
        if (columnas.All(c => c.Texto is null && c.Codigo is null))
        {
            return analisis;
        }

        IReadOnlyList<clsTipoMercanciaCubicador>? catalogo = null;
        if (cubicador is { EstaConfigurado: true })
        {
            var leido = await cubicador.ListarTiposMercanciaAsync(cancellationToken);
            if (leido.EsExitoso)
            {
                catalogo = leido.Valor;
            }
            else
            {
                logger.LogInformation("El catálogo de tipos de mercancía del cubicador no respondió ({Codigo}): el análisis va sin sugerencias de código.",
                    leido.Error.Codigo);
            }
        }

        var porCodigo = new Dictionary<string, clsTipoMercanciaCubicador>(StringComparer.OrdinalIgnoreCase);
        foreach (var tipo in catalogo ?? [])
        {
            porCodigo.TryAdd(tipo.Codigo, tipo);
        }

        return analisis with
        {
            CatalogoMercanciasDisponible = catalogo is not null,
            Hojas = [.. columnas.Select(c => c.Texto is null && c.Codigo is null
                ? c.Hoja
                : c.Hoja with
                {
                    ColumnaMercancia = c.Texto is { } t ? c.Hoja.Encabezados[t] : null,
                    ColumnaCodigoMercancia = c.Codigo is { } k ? c.Hoja.Encabezados[k] : null,
                    Mercancias = MercanciasDeHoja(c.Hoja, c.Texto, c.Codigo, catalogo, porCodigo),
                })],
        };
    }

    /// <summary>Por cada fila con mercancía (texto o código), su código (ver <see cref="clsCodigoMercanciaFila"/>).</summary>
    public static IReadOnlyList<clsCodigoMercanciaFila> MercanciasDeHoja(
        clsHojaExcel hoja,
        int? columnaTexto,
        int? columnaCodigo,
        IReadOnlyList<clsTipoMercanciaCubicador>? catalogo,
        IReadOnlyDictionary<string, clsTipoMercanciaCubicador>? porCodigo = null)
    {
        ArgumentNullException.ThrowIfNull(hoja);
        static string? Celda(IReadOnlyList<string> fila, int? columna) =>
            columna is { } c && c < fila.Count && !string.IsNullOrWhiteSpace(fila[c]) ? fila[c].Trim() : null;

        // Solo los tipos cubicables: uno que no se puede cubicar se rechazaría al guardar (HU-023.3).
        var cubicables = catalogo?.Where(t => t.Cubicable).ToList();
        var sugerencias = new Dictionary<string, clsMercanciaSugerida?>(StringComparer.Ordinal);
        var resultado = new List<clsCodigoMercanciaFila>();
        for (var i = 0; i < hoja.Filas.Count; i++)
        {
            var texto = Celda(hoja.Filas[i], columnaTexto);
            var codigo = Celda(hoja.Filas[i], columnaCodigo);
            if (codigo is not null)
            {
                // La columna de código del Excel se usa tal cual (la validación de la licitación revisa que exista en el catálogo).
                var nombre = porCodigo is not null && porCodigo.TryGetValue(codigo, out var tipo) ? tipo.Nombre : null;
                resultado.Add(new clsCodigoMercanciaFila(i, texto, codigo, nombre, Sugerido: false, clsCodigoMercanciaFila.OrigenExcel, null));
                continue;
            }

            if (texto is null)
            {
                continue;
            }

            if (!sugerencias.TryGetValue(texto, out var sugerencia))
            {
                sugerencia = cubicables is null ? null : clsSugeridorMercancia.Sugerir(texto, cubicables);
                sugerencias[texto] = sugerencia;
            }

            resultado.Add(sugerencia is null
                ? new clsCodigoMercanciaFila(i, texto, null, null, Sugerido: false, null, null)
                : new clsCodigoMercanciaFila(i, texto, sugerencia.Tipo.Codigo, sugerencia.Tipo.Nombre, Sugerido: true, clsCodigoMercanciaFila.OrigenParecido,
                    Math.Round((decimal)sugerencia.Parecido, 4, MidpointRounding.AwayFromZero)));
        }

        return resultado;
    }

    /// <summary>
    /// HU-023.5: índice de la columna con la descripción de la mercancía, por su encabezado (sin tildes ni mayúsculas): primero uno que
    /// diga "mercancía", luego "PRODUCTO", "tipo de carga" o "carga", "commodity" y por último "descripción". <c>null</c> si no hay.
    /// La columna de código (<see cref="ColumnaCodigoMercancia"/>) no cuenta.
    /// </summary>
    public static int? ColumnaMercancia(IReadOnlyList<string> encabezados)
    {
        ArgumentNullException.ThrowIfNull(encabezados);
        var normalizados = encabezados.Select(e => clsSugeridorMercancia.Normalizar(e)).ToList();
        Func<string, bool>[] reglas =
        [
            e => Palabras(e).Any(p => p.StartsWith("MERCANC", StringComparison.Ordinal)),
            e => Palabras(e).Any(p => p.StartsWith("PRODUCTO", StringComparison.Ordinal)),
            e => e is "CARGA" or "TIPO DE CARGA" or "DESCRIPCION DE LA CARGA" or "DESCRIPCION CARGA",
            e => Palabras(e).Contains("COMMODITY"),
            e => Palabras(e).Any(p => p.StartsWith("DESCRIPCION", StringComparison.Ordinal)),
        ];
        foreach (var regla in reglas)
        {
            for (var i = 0; i < normalizados.Count; i++)
            {
                if (!EsCodigo(normalizados[i]) && regla(normalizados[i]))
                {
                    return i;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// HU-023.5: índice de la columna con el código de la mercancía ("Código mercancía", "codigo_mercancia", "Cód. producto"...), o
    /// <c>null</c>.
    /// </summary>
    public static int? ColumnaCodigoMercancia(IReadOnlyList<string> encabezados)
    {
        ArgumentNullException.ThrowIfNull(encabezados);
        for (var i = 0; i < encabezados.Count; i++)
        {
            var normalizado = clsSugeridorMercancia.Normalizar(encabezados[i]);
            if (EsCodigo(normalizado)
                && Palabras(normalizado).Any(p => p.StartsWith("MERCANC", StringComparison.Ordinal) || p.StartsWith("PRODUCTO", StringComparison.Ordinal)))
            {
                return i;
            }
        }

        return null;
    }

    private static string[] Palabras(string normalizado) => normalizado.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static bool EsCodigo(string normalizado) => Palabras(normalizado).Any(p => p is "CODIGO" or "COD" or "CODE");

    /// <summary>
    /// HU-013.15 (#124): agrega a cada hoja del análisis de Nexo el número de fila de Excel de cada una de sus filas (<c>filas_excel</c>),
    /// con la misma regla de detección (<see cref="IClsLectorExcelLicitacion"/>), para que el frontend muestre la numeración del archivo
    /// del comercial. Si Cotifly no puede leer el archivo, todas quedan sin <c>filas_excel</c>; si una hoja no se puede ubicar o no
    /// cuadra con lo que devolvió Nexo (otro total de filas), solo esa queda sin él y las demás lo conservan: el análisis nunca falla
    /// por esto.
    /// </summary>
    private clsAnalisisExcel ConFilasExcel(clsAnalisisExcel analisis, byte[] contenido, CancellationToken cancellationToken)
    {
        var porHoja = lector.FilasDeDatosPorHoja(contenido, cancellationToken);
        if (porHoja.EsFallo)
        {
            return analisis;
        }

        return analisis with
        {
            Hojas = [.. analisis.Hojas.Select(h => porHoja.Valor.TryGetValue(h.Nombre, out var filas) && Cuadra(filas.Count, h)
                ? h with { FilasExcel = [.. filas.Take(h.Filas.Count)] }
                : h)],
        };
    }

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

    public async Task<clsResultadoLicitacion> CrearAsync(Guid usuarioId, clsEntradaLicitacion entrada, CancellationToken cancellationToken = default)
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

        // HU-032 (#197): el vehículo elegido de cada fila es el respaldo del cubicador (y obligatorio en una fila sin mercancías
        // cubicables, salvo Contenedor). Con Captura:AceptarVehiculoCliente apagada se ignora (decisión 7 de HU-021).
        entrada = entrada with
        {
            Lineas = [.. clsEnvioCosteoService.SinVehiculo(entrada.Lineas ?? [], logger, opcionesCaptura?.Value.AceptarVehiculoCliente ?? true).Select(l => l!)],
        };

        // El archivo se valida primero (un archivo ilegible invalida todas las filas) y se cuenta su hoja con la regla de Nexo.
        IReadOnlyList<int>? filasExcel = null;
        if (entrada.Archivo is { } archivo)
        {
            if (clsArchivoLicitacion.ValidarArchivo(archivo.Nombre, archivo.Contenido) is { } invalido)
            {
                return clsResultadoLicitacion.Fallo(invalido);
            }

            if (!string.IsNullOrWhiteSpace(entrada.Hoja) && entrada.Hoja.Trim().Length <= clsArchivoLicitacion.LargoHoja)
            {
                var filas = lector.FilasDeDatos(archivo.Contenido, entrada.Hoja.Trim(), cancellationToken);
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
            // HU-013.11: con un catálogo caído se reportan igual los errores que se detectan sin él, con el código del catálogo en
            // detalle.motivo (lo que dependía de ese catálogo se valida al volver a enviarla). HU-013.15: cada error con su fila de Excel.
            return clsResultadoLicitacion.Invalida(clsValidadorLicitacion.ConFilaExcel(validadas.Errores, filasExcel), validadas.NoDisponible?.Codigo);
        }

        if (validadas.NoDisponible is { } noDisponible)
        {
            return clsResultadoLicitacion.Fallo(noDisponible);
        }

        var entradas = validadas.Lineas;

        // HU-021.6: cada fila puede traer su línea de negocio; sin ella, la del encabezado.
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
                // HU-013.15 (#124): "Licitaciones" es una línea del catálogo de Nexo, no de Cotifly; el error dice qué líneas usar.
                return clsResultadoLicitacion.Fallo(clsMargenResolverService.LineaDesconocida(
                    nombre, await margen.LineasActivasAsync(cancellationToken) ?? [], esLicitacion: true));
            }

            if (!resuelta.Activa)
            {
                // HU-013.15 (#124): también aquí el error trae las líneas que sí se pueden usar.
                return clsResultadoLicitacion.Fallo(clsMargenResolverService.LineaInactiva(resuelta.LineaNegocio) with
                {
                    Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["lineas_validas"] = await margen.LineasActivasAsync(cancellationToken) ?? [],
                    },
                });
            }

            porNombre[nombre] = resuelta;
        }

        politicasPorRuta.AddRange(entradas.Select(l => porNombre[(l.LineaNegocio ?? entrada.LineaNegocio)!.Trim()]));
        var politica = porNombre[entrada.LineaNegocio!.Trim()];

        // HU-021.6: conexos con el costo del catálogo de Nexo y los camiones de cada ruta (cubicador, #133, con presupuesto de tiempo; o respaldo).
        // HU-023.5 (#160): con el cubicador configurado, las rutas se guardan con un camión provisional y se cubican en segundo plano.
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

            // HU-021.6: una ruta por fila (rutaOrigenIndice = fila del Excel) con su línea de negocio, mercancías, camión y conexos.
            var id = cotizacion.Valor.Id;
            cotizacion.Valor.RegistrarCaptura(entrada.Encabezado, preparadas.Rutas);
            cotizaciones.Agregar(cotizacion.Valor);
            if (entrada.Archivo is { } original)
            {
                var guardado = clsArchivoLicitacion.Crear(id, original.Nombre, original.Contenido, entrada.Hoja);
                if (guardado.EsFallo)
                {
                    return guardado.Error;
                }

                cotizaciones.AgregarArchivoLicitacion(guardado.Valor);
            }

            await unidadTrabajo.GuardarCambiosAsync(ct);
            return cotizacion.Valor;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return clsResultadoLicitacion.Fallo(creada.Error);
        }

        if (enSegundoPlano)
        {
            // HU-023.5 (#160): la solicitud sale a Nexo cuando la última ruta quede cubicada (clsCubicacionLicitacionService).
            logger.LogInformation("Licitación {Folio} guardada con {Rutas} ruta(s) por cubicar en segundo plano.", creada.Valor.Folio, creada.Valor.Rutas.Count);
            return clsResultadoLicitacion.Exito(creada.Valor);
        }

        // Confirmada: desde aquí, sin el token de la petición (como HU-005.5, ADR-015 §3).
        await envio.EnviarGuardadaAsync(creada.Valor, politica, usuarioId);
        return clsResultadoLicitacion.Exito(creada.Valor);
    }

    /// <summary>
    /// HU-023.5 (#160): ¿la licitación se cubica en segundo plano? Con el cubicador configurado y <c>Cubicador:LicitacionEnSegundoPlano</c>
    /// (por defecto sí). Sin cubicador, el respaldo (un camión sin vehículo por ruta) no llama a nadie y la licitación sale de una vez.
    /// </summary>
    private bool CubicaEnSegundoPlano =>
        cubicador is { EstaConfigurado: true } && (opcionesCubicacion?.Value.LicitacionEnSegundoPlano ?? true);

    /// <summary>
    /// Todas las validaciones de las filas, juntas (HU-013.11): las de forma y rango de <see cref="clsValidadorLicitacion"/> (con G3) y
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
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var archivo = await cotizaciones.ObtenerResumenArchivoLicitacionAsync(cotizacion.Id, cancellationToken);
        return Vista(cotizacion, archivo);
    }

    /// <summary>
    /// Vista de la licitación, una fila por ruta (solo precios: el costo de Nexo de cada ruta nunca sale). Como el Excel (no como el PDF del
    /// pliego, que desde HU-033 imprime el valor final con conexos), el precio de cada fila es la tarifa del transporte y el total su suma, sin servicios conexos (que se ven aparte en el
    /// costeo): HU-021, el precio de la ruta ya los incluye.
    /// </summary>
    public static clsLicitacion Vista(clsCotizacionSolicitud cotizacion, clsArchivoLicitacionResumen? archivo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var ordenadas = cotizacion.RutasOrdenadas;
        // HU-028.1 (#183): una ruta rechazada por la Mesa no tiene precio, no es pendiente y no suma en el total.
        var tarifas = ordenadas.Select(l => l.Rechazada ? null : Costeo.Services.clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)).ToList();
        var cotizables = ordenadas.Select((l, i) => (Ruta: l, Tarifa: tarifas[i])).Where(x => !x.Ruta.Rechazada).ToList();
        return new clsLicitacion(
            cotizacion.Folio,
            cotizacion.ClienteNombre,
            cotizacion.NitCliente,
            // D5 (E2E 2026-10-16): en UTC, como la captura, el costeo y "Mis cotizaciones" (la base lo guarda con el -05:00 de Bogotá).
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
        };
    }

    /// <summary>Vehículo de la ruta: el de su camión, o los de sus camiones separados por coma (HU-021).</summary>
    private static string? TipoVehiculo(clsLineaSolicitud ruta)
    {
        var tipos = ruta.VehiculosOrdenados.Select(v => v.TipoVehiculo).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return tipos.Count == 0 ? null : string.Join(", ", tipos);
    }
}
