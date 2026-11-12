using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// HU-018.1 (#155, parte C): <c>GET /web/cotizaciones/{folio}/excel</c>, el Excel de una cotización desde "Ver detalle". Acceso con la
/// regla del detalle (<see cref="clsAccesoLecturaCotizacion"/>):
/// <list type="bullet">
/// <item>el dueño recibe la versión interna: por ruta y por camión, con sus costos de Nexo (COP), su margen, su precio, los conexos
/// (precio y costo) y los totales;</item>
/// <item>el gerente de su regional y el director (HU-037) reciben el mismo Excel en solo lectura, SIN ninguna columna de costo ni de
/// margen (<see cref="clsExcelCotizacion.SinCostos"/>: con el margen y el precio se deduce el costo; criterio de <c>SinCostoFinal()</c>
/// de HU-039.1, #218). Cada consulta de un gerente o director queda en la auditoría (<see cref="HerramientaAuditoria"/>);</item>
/// <item>cualquier otro: <c>COTIZACION_NO_ENCONTRADA</c> (404). Sin rutas también 404; sin precio, <c>COSTEO_NO_LISTO</c> (409).</item>
/// </list>
/// Solo lee lo guardado (ni Nexo ni el cubicador): con la cotización finalizada o con PDF los precios y los costos son los congelados
/// (HU-036). Las rutas rechazadas por la Mesa salen sin precio, sin conexos y no suman (HU-028.1).
/// </summary>
public interface IClsExportacionCotizacionService
{
    Task<clsResultado<clsArchivoExportado>> ExportarAsync(string folio, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsExportacionCotizacionService"/>.</summary>
public sealed class clsExportacionCotizacionService(
    IClsContextoUsuario usuario,
    IClsCotizacionRepository cotizaciones,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
    IClsAuditoriaService auditoria,
    IClsExportadorExcelCotizacion exportador,
    IClsReloj reloj,
    ILogger<clsExportacionCotizacionService> logger,
    IClsCategoriasConexos? categoriasConexos = null) : IClsExportacionCotizacionService
{
    /// <summary>Herramienta de la auditoría de las exportaciones de un gerente o director a una cotización ajena.</summary>
    public const string HerramientaAuditoria = "exportar_excel_cotizacion";

    public const string TipoXlsx = clsLicitacionService.TipoXlsx;

    private readonly clsAccesoLecturaCotizacion _acceso = new(usuario, cotizaciones, usuarios, regionales, auditoria);

    public async Task<clsResultado<clsArchivoExportado>> ExportarAsync(string folio, CancellationToken cancellationToken = default)
    {
        var leida = await _acceso.ObtenerAsync(folio, HerramientaAuditoria, cancellationToken);
        if (leida.EsFallo)
        {
            return leida.Error;
        }

        var (cotizacion, soloLectura) = leida.Valor;
        if (cotizacion.Rutas.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        if (cotizacion.PrecioFinal is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} todavía no tiene precio: el Excel se descarga cuando el costeo esté listo.");
        }

        var dueno = cotizacion.UsuarioId is { } duenoId ? await usuarios.ObtenerPorIdAsync(duenoId, cancellationToken) : null;
        // Corrección conexos (nombre nunca id): el nombre del catálogo de Nexo de los conexos heredados sin nombre guardado, para que el Excel
        // imprima el nombre y no caiga a la categoría (ni al id). Reutiliza el índice en memoria de categorías; nunca falla (cae a la categoría).
        var nombresConexos = await clsCosteoCotizacionService.NombresAsync(categoriasConexos, cotizacion, logger, cancellationToken);
        var datos = Datos(cotizacion, dueno?.NombreCompleto ?? dueno?.NombreUsuario, reloj.AhoraUtc, nombresConexos);
        return new clsArchivoExportado(cotizacion.Folio + ".xlsx", exportador.Generar(soloLectura ? datos.SinCostos() : datos), TipoXlsx);
    }

    /// <summary>
    /// La versión interna (la del dueño) a partir de lo guardado. Precios con las reglas del costeo y del PDF
    /// (<see cref="clsCosteoCotizacionService.PrecioTransporte"/> y <see cref="clsCosteoCotizacionService.ConexosConPrecio"/>); el precio
    /// de cada camión es su costo con el margen de la ruta. Como en el costeo (#218), nunca un costo sin su precio: los costos de una ruta
    /// o de un camión sin precio (rechazado, descartado o sin costeo) salen vacíos.
    /// </summary>
    public static clsExcelCotizacion Datos(
        clsCotizacionSolicitud cotizacion, string? comercial, DateTimeOffset ahora,
        IReadOnlyDictionary<string, string>? nombresConexos = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var moneda = cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos;
        var rutas = cotizacion.RutasOrdenadas.Select(r => Ruta(cotizacion, r, moneda, nombresConexos)).ToList();
        var cotizables = rutas.Where(r => !r.Rechazada).ToList();
        var conPrecio = cotizacion.PrecioFinal is not null && cotizables.Count > 0 && cotizables.All(r => r.PrecioRuta is not null);
        var pesos = rutas.Select(r => r.PesoKg).OfType<decimal>().ToList();
        var totales = new clsExcelTotales(
            rutas.Count,
            rutas.Sum(r => r.CamionesQueCuentan),
            rutas.Count(r => r.Rechazada),
            pesos.Count == 0 ? null : pesos.Sum(),
            conPrecio && cotizables.All(r => r.PrecioTransporte is not null) ? cotizables.Sum(r => r.PrecioTransporte!.Value) : null,
            conPrecio ? cotizables.Sum(r => r.PrecioConexos ?? 0m) : null,
            cotizacion.PrecioFinal,
            new clsExcelInternoTotales(
                conPrecio && cotizables.All(r => r.Interno?.CostoBruto is not null) ? cotizables.Sum(r => r.Interno!.CostoBruto!.Value) : null))
        {
            // HU-045.3 (#265): el seguro de la mercancía aparte del transporte y de los conexos.
            PrecioSeguro = conPrecio ? cotizables.Sum(r => r.ValorSeguro ?? 0m) : null,
        };

        return new clsExcelCotizacion(
            cotizacion.Folio,
            cotizacion.ClienteNombre,
            cotizacion.NitCliente,
            comercial,
            cotizacion.TipoSolicitud is { } tipo ? clsCodigoEnum.ACodigo(tipo) : null,
            moneda,
            cotizacion.TrmAplicada,
            cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            cotizacion.Ganada switch { true => "ganada", false => "perdida", null => null },
            cotizacion.FinalizadaAt?.ToUniversalTime(),
            cotizacion.PrecioCongelado,
            ahora,
            SoloLectura: false,
            rutas,
            totales);
    }

    private static clsExcelRuta Ruta(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, string moneda, IReadOnlyDictionary<string, string>? nombresConexos = null)
    {
        var conPrecio = !ruta.Rechazada && ruta.PrecioFinalLinea is not null;
        // Corrección conexos (nombre nunca id): ConexosConPrecio pobla Nombre con el guardado o el del catálogo de Nexo por id (nombresConexos)
        // y cae a la categoría como respaldo legible; el Excel nunca imprime el id interno.
        var precios = clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta, nombres: nombresConexos);
        var conexos = precios
            .Select(c => new clsExcelConexo(
                c.Nombre ?? c.Categoria, c.Categoria, c.Cantidad, c.Precio,
                ruta.Conexos.FirstOrDefault(d => string.Equals(d.IdServicioConexo, c.Id, StringComparison.Ordinal)) is { } guardado
                    ? new clsExcelInternoConexo(guardado.Costo)
                    : null))
            .ToList();
        var cuentan = ruta.VehiculosQueCuentan;
        var camiones = ruta.VehiculosOrdenados.Select((v, i) => Camion(cotizacion, ruta, v, i + 1, cuentan.Contains(v) && conPrecio, moneda)).ToList();
        return new clsExcelRuta(
            ruta.Orden,
            ruta.OrigenNombre ?? string.Empty,
            ruta.DestinoNombre ?? string.Empty,
            clsLicitacionService.EstadoLegible(ruta),
            ruta.Rechazada,
            ruta.MotivoRechazo,
            ruta.PesoKg,
            cuentan.Count,
            ruta.Rechazada ? null : clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta),
            conPrecio ? conexos.Sum(c => c.Precio) : null,
            ruta.Rechazada ? null : ruta.PrecioFinalLinea,
            camiones,
            conexos,
            new clsExcelInternoRuta(
                conPrecio ? ruta.CostoFletesTotal : null,
                conPrecio ? ruta.CostoRetornoContenedorTotal : null,
                conPrecio ? ruta.CostoLevanteContenedorTotal : null,
                conPrecio ? ruta.CostoServiciosConexos : null,
                conPrecio ? ruta.CostoFinal : null,
                conPrecio ? ruta.MargenAplicado : null,
                ruta.MargenSugeridoMostrado,
                ruta.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
                ruta.MetodoCalculo))
        {
            ValorSeguro = clsCosteoCotizacionService.ValorSeguroPdf(cotizacion, ruta),
            SeguroSinValor = cotizacion.RutasConSeguroSinValor().Contains(ruta),
        };
    }

    private static clsExcelCamion Camion(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, clsVehiculoLineaSolicitud camion, int numero, bool cuenta, string moneda)
    {
        decimal? costo = camion.CostoFlete is { } flete ? flete + (camion.CostoRetornoContenedor ?? 0m) + (camion.CostoLevanteContenedor ?? 0m) : null;
        decimal? precio = null;
        if (cuenta && ruta.PrecioDe(costo, moneda, cotizacion.TrmAplicada, out var calculado) is null)
        {
            precio = calculado;
        }

        var conPrecio = precio is not null;
        return new clsExcelCamion(
            numero,
            camion.TipoVehiculo,
            camion.Carroceria,
            camion.PesoKg,
            EstadoCamion(camion),
            cuenta,
            precio,
            new clsExcelInternoCamion(
                conPrecio ? camion.CostoFlete : null,
                conPrecio ? camion.CostoRetornoContenedor : null,
                conPrecio ? camion.CostoLevanteContenedor : null,
                conPrecio ? costo : null,
                conPrecio ? ruta.MargenAplicado : null));
    }

    /// <summary>El estado del camión para leer (como el de la ruta en el Excel de la licitación).</summary>
    public static string EstadoCamion(clsVehiculoLineaSolicitud camion)
    {
        ArgumentNullException.ThrowIfNull(camion);
        return camion.EstadoCosteo switch
        {
            EstadoCosteo.Rechazada => clsLicitacionService.EstadoRechazadaEnNexo,
            EstadoCosteo.Descartada => "Descartada en Nexo",
            EstadoCosteo.CosteoVencido => "Costeo vencido",
            EstadoCosteo.ErrorEnvio => "No llegó a Nexo",
            _ when camion.CostoFlete is not null => "Costeado",
            _ => "Pendiente de costeo",
        };
    }
}
