using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Licitaciones.Services;

/// <summary>
/// Licitaciones desde Excel (HU-011.2, ADR-017): analizar el pliego en Nexo, crear la cotización con sus N líneas y enviarla a Nexo
/// como UNA solicitud, y verla línea por línea. Solo cotizaciones propias (<c>COTIZACION_NO_ENCONTRADA</c> → 404).
/// </summary>
public interface IClsLicitacionService
{
    /// <summary>
    /// Reenvía el Excel al análisis de Nexo (no guarda nada). <c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c> y
    /// <c>ARCHIVO_DEMASIADO_GRANDE</c> se validan antes de llamar a Nexo; los errores de lectura los devuelve Nexo.
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

    /// <summary>La licitación con el estado y el precio de cada línea (nunca el costo de Nexo).</summary>
    Task<clsResultado<clsLicitacion>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Excel de la licitación con los resultados (HU-011.3): el original del cliente con las columnas de Cotifly agregadas (o una hoja
    /// nueva si no hay original). Se puede exportar en cualquier momento: las líneas sin costeo quedan marcadas. <c>COTIZACION_NO_ENCONTRADA</c>
    /// si no es suya o no tiene líneas.
    /// </summary>
    Task<clsResultado<clsArchivoExportado>> ExportarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);
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
    ILogger<clsLicitacionService> logger) : IClsLicitacionService
{
    public const string TipoXlsx = clsArchivoLicitacion.TipoXlsx;

    public async Task<clsResultado<clsArchivoExportado>> ExportarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null || cotizacion.Rutas.Count == 0 || cotizacion.TipoSolicitud != TipoSolicitud.Licitacion)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var archivo = await cotizaciones.ObtenerArchivoLicitacionAsync(cotizacion.Id, cancellationToken);
        var datos = DatosExportacion(cotizacion, archivo, reloj.AhoraUtc);
        var generado = exportador.Generar(datos);
        if (generado.EsFallo)
        {
            return generado.Error;
        }

        return new clsArchivoExportado(cotizacion.Folio + ".xlsx", generado.Valor, TipoXlsx);
    }

    /// <summary>
    /// Datos de la exportación (HU-021: una fila por ruta): la tarifa del transporte de la ruta (sin conexos, la misma del PDF del pliego),
    /// estado legible y los servicios conexos de las rutas con su precio, aparte. El total es la suma de las tarifas (sin conexos), como
    /// el resumen del PDF: los conexos no se cobran dos veces. Origen y destino son los nombres copiados de la ruta; el vehículo, el de su camión (o los de sus camiones). El costo de
    /// Nexo no entra a este objeto: el exportador no lo puede escribir.
    /// </summary>
    public static clsExportacionLicitacion DatosExportacion(clsCotizacionSolicitud cotizacion, clsArchivoLicitacion? archivo, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var lineas = cotizacion.RutasOrdenadas;
        var tarifas = lineas.Select(l => Costeo.Services.clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)).ToList();
        var conexos = lineas
            .SelectMany(r => Costeo.Services.clsCosteoCotizacionService.ConexosConPrecio(cotizacion, r))
            .Select(s => new clsConexoExportacion(s.Nombre ?? s.Categoria, s.Categoria, s.Cantidad, s.Precio))
            .ToList();
        return new clsExportacionLicitacion(
            cotizacion.Folio,
            cotizacion.ClienteNombre,
            cotizacion.NitCliente,
            null,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            cotizacion.TrmAplicada,
            tarifas.All(t => t is not null) ? tarifas.Sum(t => t!.Value) : null,
            archivo?.Hoja,
            archivo?.Contenido,
            [.. lineas.Select((l, i) => new clsLineaExportacion(
                l.Orden, l.RutaOrigenIndice, l.OrigenNombre ?? string.Empty, l.DestinoNombre ?? string.Empty, TipoVehiculo(l), l.PesoKg, EstadoLegible(l), tarifas[i]))],
            conexos,
            ahora);
    }

    /// <summary>Estado de la línea para el Excel: <c>Costeada</c> solo si tiene precio con margen.</summary>
    public static string EstadoLegible(clsLineaSolicitud linea)
    {
        ArgumentNullException.ThrowIfNull(linea);
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
        return analisis.EsFallo ? analisis : ConFilasExcel(analisis.Valor, archivo.Contenido, cancellationToken);
    }

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

        // HU-021 (decisión 7): vehículo y carrocería ya no se mapean del Excel; si llegan, se ignoran con un aviso en el log.
        entrada = entrada with { Lineas = [.. clsEnvioCosteoService.SinVehiculo(entrada.Lineas ?? [], logger).Select(l => l!)] };

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
        var preparadas = await captura.PrepararAsync(
            usuarioId, [.. entradas.Select((l, i) => new clsRutaPorPreparar(l, politicasPorRuta[i].Id, l.Fila))], cancellationToken);
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

        // Confirmada: desde aquí, sin el token de la petición (como HU-005.5, ADR-015 §3).
        await envio.EnviarGuardadaAsync(creada.Valor, politica, usuarioId);
        return clsResultadoLicitacion.Exito(creada.Valor);
    }

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
    /// Vista de la licitación, una fila por ruta (solo precios: el costo de Nexo de cada ruta nunca sale). Como el Excel y el PDF del
    /// pliego, el precio de cada fila es la tarifa del transporte y el total su suma, sin servicios conexos (que se ven aparte en el
    /// costeo): HU-021, el precio de la ruta ya los incluye.
    /// </summary>
    public static clsLicitacion Vista(clsCotizacionSolicitud cotizacion, clsArchivoLicitacionResumen? archivo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var ordenadas = cotizacion.RutasOrdenadas;
        var tarifas = ordenadas.Select(l => Costeo.Services.clsCosteoCotizacionService.PrecioTransporte(cotizacion, l)).ToList();
        return new clsLicitacion(
            cotizacion.Folio,
            cotizacion.ClienteNombre,
            cotizacion.NitCliente,
            null,
            cotizacion.Deadline,
            cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            cotizacion.MotivoEstadoCosteo,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            cotizacion.TrmAplicada,
            cotizacion.PrecioFinal is not null && tarifas.All(t => t is not null) ? tarifas.Sum(t => t!.Value) : null,
            ordenadas.Count(l => l.PrecioFinalLinea is not null),
            ordenadas.Count(l => l.PrecioFinalLinea is null),
            archivo?.NombreArchivo,
            archivo?.Hoja,
            [.. ordenadas.Select((l, i) => new clsLineaLicitacion(
                l.Orden, l.RutaOrigenIndice, l.OrigenNombre ?? string.Empty, l.DestinoNombre ?? string.Empty, TipoVehiculo(l), l.PesoKg,
                l.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : null, l.PrecioFinalLinea is null ? null : tarifas[i]))]);
    }

    /// <summary>Vehículo de la ruta: el de su camión, o los de sus camiones separados por coma (HU-021).</summary>
    private static string? TipoVehiculo(clsLineaSolicitud ruta)
    {
        var tipos = ruta.VehiculosOrdenados.Select(v => v.TipoVehiculo).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return tipos.Count == 0 ? null : string.Join(", ", tipos);
    }
}
