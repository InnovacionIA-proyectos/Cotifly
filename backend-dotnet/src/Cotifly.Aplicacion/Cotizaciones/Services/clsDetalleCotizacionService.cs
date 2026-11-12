using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// HU-027.1 (#179): <c>GET /web/cotizaciones/{folio}</c>, el detalle de una cotización en una sola llamada (<see cref="clsDetalleCotizacion"/>).
/// Solo lee lo que Cotifly tiene guardado: no consulta Nexo (ni el catálogo de conexos), no dispara el polling ni escribe en la cotización.
/// </summary>
public interface IClsDetalleCotizacionService
{
    /// <summary>
    /// El detalle de <paramref name="folio"/> para el usuario autenticado. Acceso:
    /// <list type="bullet">
    /// <item>el dueño, siempre;</item>
    /// <item>el <c>director</c> (cualquier regional, HU-037.3 #211) o un <c>gerente_comercial</c> cuya regional efectiva incluye al dueño
    /// (<see cref="IClsRegionalUsuario.PuedeVerDeAsync"/>), solo consulta (<c>solo_lectura = true</c>), con la misma
    /// regla que <c>GET /web/cotizaciones/regional</c> y el PDF (<see cref="IClsRegionalUsuario.EsMiembroDeMiRegionalAsync"/>). Si la
    /// regional no se puede comprobar (sin regional o fuente caída) se niega (fail-closed). Cada consulta de un gerente a una cotización
    /// ajena, permitida o no, queda en la auditoría (<see cref="clsDetalleCotizacionService.HerramientaAuditoria"/>);</item>
    /// <item>cualquier otro caso: <c>COTIZACION_NO_ENCONTRADA</c> (404, igual que si no existiera).</item>
    /// </list>
    /// </summary>
    Task<clsResultado<clsDetalleCotizacion>> ObtenerAsync(string folio, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsDetalleCotizacionService"/>. Compone las proyecciones existentes sin reglas nuevas: la cabecera de
/// <see cref="IClsMisCotizacionesService.ResumirAsync"/>, el costeo de <see cref="clsCosteoCotizacionService.Vista"/> (el de
/// <c>GET .../costeo</c>) y las rutas capturadas de la edición (<c>RutaEditable</c>). Consultas acotadas: el agregado en consultas
/// divididas (rutas, camiones, mercancías y conexos), las políticas de margen una vez, el dueño, y el estado de los conexos en Nexo
/// (registro local) solo si la solicitud llegó a Nexo y tiene conexos.
/// </summary>
public sealed class clsDetalleCotizacionService(
    IClsContextoUsuario usuario,
    IClsCotizacionRepository cotizaciones,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
    IClsMargenPoliticaRepository politicas,
    IClsMisCotizacionesService misCotizaciones,
    IClsEnvioConexosNexoService envioConexos,
    clsAlmacenCategoriasConexos categorias,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf,
    IClsSituacionLineasNexo? situacionesNexo = null,
    IClsTarifaSeguroRepository? tarifasSeguro = null,
    Microsoft.Extensions.Logging.ILogger<clsDetalleCotizacionService>? logger = null,
    IClsPoliticaSegurosRepository? politicaSeguros = null) : IClsDetalleCotizacionService
{
    /// <summary>Herramienta de la auditoría de las consultas de un gerente a una cotización ajena (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "consultar_detalle_cotizacion";

    /// <summary>La regla de acceso del detalle, compartida con el Excel de la cotización (HU-018.1, #155).</summary>
    private readonly clsAccesoLecturaCotizacion _acceso = new(usuario, cotizaciones, usuarios, regionales, auditoria);

    public async Task<clsResultado<clsDetalleCotizacion>> ObtenerAsync(string folio, CancellationToken cancellationToken = default)
    {
        var leida = await _acceso.ObtenerAsync(folio, HerramientaAuditoria, cancellationToken);
        return leida.EsFallo ? leida.Error : await DetalleAsync(leida.Valor.Cotizacion, leida.Valor.SoloLectura, cancellationToken);
    }

    private async Task<clsDetalleCotizacion> DetalleAsync(clsCotizacionSolicitud cotizacion, bool soloLectura, CancellationToken cancellationToken)
    {
        var resumen = await misCotizaciones.ResumirAsync(cotizacion, cancellationToken);
        var dueno = cotizacion.UsuarioId is { } duenoId ? await usuarios.ObtenerPorIdAsync(duenoId, cancellationToken) : null;
        if (dueno is not null)
        {
            resumen = resumen with { Comercial = new clsComercialCotizacion(dueno.Id, dueno.NombreUsuario, dueno.NombreCompleto) };
        }

        // Lo mismo que GET .../costeo, salvo las categorías de los conexos antiguos: aquí solo las que ya están en memoria (no se lee el
        // catálogo de Nexo ni se calienta en segundo plano).
        var estadosNexo = await envioConexos.EstadosAsync(cotizacion, cancellationToken);
        var costeo = clsCosteoCotizacionService.Vista(
            cotizacion, opcionesPdf.Value.BaseUrl, estadosNexo, clsCategoriasConexosService.EnMemoria(categorias, cotizacion),
            situacionesNexo?.Obtener(cotizacion.Id))
            with
            {
                // HU-045.2 (#264): la tasa vigente del seguro, como en GET .../costeo.
                TasaSeguroVigente = await clsCosteoCotizacionService.TasaSeguroVigenteSinFallarAsync(
                    tarifasSeguro, (Microsoft.Extensions.Logging.ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                    cotizacion.Folio, cancellationToken),
            };

        // HU-046.3 (#269): el bloque politica_seguros de cada ruta, como en GET .../costeo (sin completar códigos: el detalle no escribe ni
        // lee el catálogo de Nexo; un conexo sin codigoServicio no cumple hasta que el costeo lo complete).
        costeo = clsPoliticaSegurosCosteo.ConPolitica(costeo, cotizacion, await clsPoliticaSegurosCosteo.LeerSinFallarAsync(
            politicaSeguros, (Microsoft.Extensions.Logging.ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            cotizacion.Folio, cancellationToken));

        // HU-039.1 (#218): el costo bruto de las rutas es solo del dueño; el gerente en solo consulta lo recibe en null (decisión
        // conservadora: hasta HU-039 el detalle nunca le mostraba costos).
        if (soloLectura)
        {
            costeo = costeo.SinCostoFinal();
        }

        var politicasRuta = await PoliticasAsync(cotizacion, cancellationToken);
        var sinCatalogo = new Dictionary<string, clsCostoConexoNexo>();
        var captura = cotizacion.RutasOrdenadas.Select(r => clsEdicionSolicitudService.Editable(r, politicasRuta, sinCatalogo)).ToList();

        return new clsDetalleCotizacion(
            cotizacion.Folio,
            costeo.RowVersion,
            soloLectura,
            cotizacion.TipoSolicitud is { } tipo ? clsCodigoEnum.ACodigo(tipo) : null,
            cotizacion.Notas,
            new clsFechasCotizacion(
                Utc(cotizacion.CreatedAt == default ? null : cotizacion.CreatedAt),
                Utc(cotizacion.UpdatedAt == default ? null : cotizacion.UpdatedAt),
                Utc(cotizacion.CosteoEnviadoAt),
                Utc(cotizacion.CosteoListoAt),
                Utc(cotizacion.MarcadoAt)) { FinalizadaAt = Utc(cotizacion.FinalizadaAt) },
            resumen,
            costeo,
            captura,
            Totales(cotizacion, costeo));
    }

    /// <summary>
    /// Totales de la solicitud con las reglas del costeo y del PDF: la tarifa de cada ruta es <see cref="clsCosteoCotizacionService.PrecioTransporte"/>,
    /// los conexos los de <c>costeo.rutas[].servicios_conexos</c> y (HU-045.3, #265) el seguro el <c>valor_seguro</c> de cada ruta. Sin precio en
    /// alguna ruta, los precios salen <c>null</c>.
    /// </summary>
    internal static clsTotalesCotizacion Totales(clsCotizacionSolicitud cotizacion, clsCosteoCotizacion costeo)
    {
        var rutas = cotizacion.RutasOrdenadas;
        var pesos = rutas.Select(r => r.PesoKg).OfType<decimal>().ToList();
        // HU-028.1 (#183): las rutas rechazadas por la Mesa no cuentan en los precios (ni sus conexos).
        var cotizables = cotizacion.RutasCotizables;
        var conPrecio = costeo.PrecioFinal is not null && cotizables.Count > 0 && cotizables.All(r => r.PrecioFinalLinea is not null);
        var transportes = cotizables.Select(r => clsCosteoCotizacionService.PrecioTransporte(cotizacion, r)).ToList();
        var ordenesCotizables = cotizables.Select(r => r.Orden).ToHashSet();
        return new clsTotalesCotizacion(
            rutas.Count,
            rutas.Sum(r => r.VehiculosQueCuentan.Count),
            pesos.Count == 0 ? null : pesos.Sum(),
            conPrecio && transportes.All(t => t is not null) ? transportes.Sum() : null,
            conPrecio ? costeo.Rutas.Where(r => ordenesCotizables.Contains(r.LineaOrden)).SelectMany(r => r.ServiciosConexos).Sum(c => c.Precio) : null,
            costeo.PrecioFinal)
        {
            RutasRechazadas = rutas.Count(r => r.Rechazada),
            CamionesRechazados = rutas.Sum(r => r.Vehiculos.Count(v => v.Rechazado)),
            PrecioSeguro = conPrecio ? costeo.Rutas.Where(r => ordenesCotizables.Contains(r.LineaOrden)).Sum(r => r.ValorSeguro ?? 0m) : null,
        };
    }

    /// <summary>Las políticas de las líneas de las rutas, con una sola lectura de <c>MargenesPolitica</c> (tabla pequeña).</summary>
    private async Task<IReadOnlyDictionary<int, clsMargenPolitica?>> PoliticasAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        var lineas = cotizacion.LineasNegocioDeRutas;
        if (lineas.Count == 0)
        {
            return new Dictionary<int, clsMargenPolitica?>();
        }

        var todas = await politicas.ListarAsync(cancellationToken);
        return lineas.ToDictionary(id => id, id => todas.FirstOrDefault(p => p.Id == id));
    }

    private static DateTimeOffset? Utc(DateTimeOffset? fecha) => fecha?.ToUniversalTime();
}
