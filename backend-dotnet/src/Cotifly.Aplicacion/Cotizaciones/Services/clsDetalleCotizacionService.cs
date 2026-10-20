using System.Diagnostics;
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
using Cotifly.Dominio.Identidad;
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
    /// <item>un <c>gerente_comercial</c> cuya regional efectiva incluye al dueño, solo consulta (<c>solo_lectura = true</c>), con la misma
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
    IOptions<clsOpcionesPdf> opcionesPdf) : IClsDetalleCotizacionService
{
    /// <summary>Herramienta de la auditoría de las consultas de un gerente a una cotización ajena (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "consultar_detalle_cotizacion";

    public async Task<clsResultado<clsDetalleCotizacion>> ObtenerAsync(string folio, CancellationToken cancellationToken = default)
    {
        if (usuario.UsuarioId is not { } usuarioId)
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
        }

        if (string.IsNullOrWhiteSpace(folio))
        {
            return NoEncontrada();
        }

        // El dueño primero (la consulta filtra por dueño): un comercial ajeno nunca carga el agregado de otro.
        var cotizacion = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        var soloLectura = false;
        if (cotizacion is null)
        {
            if (usuario.Rol != RolInterno.GerenteComercial)
            {
                return NoEncontrada();
            }

            cotizacion = await cotizaciones.ObtenerPorFolioAsync(folio, cancellationToken);
            if (cotizacion is null || !await PermitidoAlGerenteAsync(usuarioId, folio, cotizacion, cancellationToken))
            {
                return NoEncontrada();
            }

            soloLectura = true;
        }

        return await DetalleAsync(cotizacion, soloLectura, cancellationToken);
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
            cotizacion, opcionesPdf.Value.BaseUrl, estadosNexo, clsCategoriasConexosService.EnMemoria(categorias, cotizacion));

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
                Utc(cotizacion.MarcadoAt)),
            resumen,
            costeo,
            captura,
            Totales(cotizacion, costeo));
    }

    /// <summary>
    /// Totales de la solicitud con las reglas del costeo y del PDF: la tarifa de cada ruta es <see cref="clsCosteoCotizacionService.PrecioTransporte"/>
    /// y los conexos los de <c>costeo.rutas[].servicios_conexos</c>. Sin precio en alguna ruta, los precios salen <c>null</c>.
    /// </summary>
    internal static clsTotalesCotizacion Totales(clsCotizacionSolicitud cotizacion, clsCosteoCotizacion costeo)
    {
        var rutas = cotizacion.RutasOrdenadas;
        var pesos = rutas.Select(r => r.PesoKg).OfType<decimal>().ToList();
        var conPrecio = costeo.PrecioFinal is not null && rutas.All(r => r.PrecioFinalLinea is not null);
        var transportes = rutas.Select(r => clsCosteoCotizacionService.PrecioTransporte(cotizacion, r)).ToList();
        return new clsTotalesCotizacion(
            rutas.Count,
            rutas.Sum(r => r.VehiculosQueCuentan.Count),
            pesos.Count == 0 ? null : pesos.Sum(),
            conPrecio && transportes.All(t => t is not null) ? transportes.Sum() : null,
            conPrecio ? costeo.Rutas.SelectMany(r => r.ServiciosConexos).Sum(c => c.Precio) : null,
            costeo.PrecioFinal);
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

    /// <summary>
    /// ¿El dueño es miembro de la regional efectiva del gerente? La misma regla que el listado regional y el PDF; cualquier error de la
    /// regional niega (fail-closed). Queda en la auditoría, permitido o no.
    /// </summary>
    private async Task<bool> PermitidoAlGerenteAsync(Guid gerenteId, string folio, clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        var reloj = Stopwatch.StartNew();
        var (permitido, motivo) = await EsDeSuRegionalAsync(gerenteId, cotizacion, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio, usuario_id = gerenteId, rol = clsCodigoEnum.ACodigo(RolInterno.GerenteComercial) },
            new { permitido, dueno_id = cotizacion.UsuarioId, motivo },
            permitido,
            (int)reloj.ElapsedMilliseconds,
            null);
        return permitido;
    }

    private async Task<(bool Permitido, string? Motivo)> EsDeSuRegionalAsync(Guid gerenteId, clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        if (cotizacion.UsuarioId is not { } duenoId)
        {
            return (false, null);
        }

        var gerente = await usuarios.ObtenerPorIdAsync(gerenteId, cancellationToken);
        if (gerente is null)
        {
            return (false, null);
        }

        var miembro = await regionales.EsMiembroDeMiRegionalAsync(gerente, duenoId, cancellationToken);
        return miembro.EsFallo ? (false, miembro.Error.Codigo) : (miembro.Valor, null);
    }

    private static DateTimeOffset? Utc(DateTimeOffset? fecha) => fecha?.ToUniversalTime();

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
