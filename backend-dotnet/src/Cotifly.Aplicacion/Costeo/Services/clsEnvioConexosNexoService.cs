using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Envío diferido de los conexos de las rutas a la cotización de Nexo (decisión 10 del usuario, HU-021.9, #136). Los conexos se capturan
/// con la ruta (#132) o se copian en una re-solicitud o una <c>-vN</c>, pero el <c>POST /api/cotizaciones/{id}/conexos</c> exige la
/// cotización de Nexo y la línea del camión, que solo existen cuando Nexo costea: el polling los envía la primera vez que ve esa línea.
/// </summary>
public interface IClsEnvioConexosNexoService
{
    /// <summary>
    /// Envía UNA sola vez, a la línea del primer camión vigente de cada ruta, los conexos de la ruta que todavía no están en la cotización
    /// de Nexo <paramref name="cotizacionNexo"/> (la que acaba de leer el polling). Nunca lanza por Nexo: un fallo queda en el log y se
    /// reintenta (hasta <c>CosteoNexo:MaximoReintentosConexos</c>), salvo que el POST haya sido incierto.
    /// </summary>
    Task<clsResultadoEnvioConexos> EnviarPendientesAsync(
        clsCotizacionSolicitud cotizacion, clsCotizacionNexo? cotizacionNexo, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// ¿Tiene conexos por reintentar: <see cref="clsEnvioConexosNexo.Fallido"/> (por debajo del tope) o <see cref="clsEnvioConexosNexo.EnEspera"/>?
    /// #259: con la cotización de Nexo ya creada, también los que nunca se intentaron (sin registro).
    /// </summary>
    Task<bool> TieneReintentosAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reintento de una cotización que el polling ya no consulta (entregada en la misma consulta en que falló el envío): lee SOLO la
    /// cotización de Nexo (<c>GET /api/cotizaciones/{id}</c>, sin costeo ni montos de Cotifly) y vuelve a intentar los pendientes. Si la
    /// lectura falla se espera al siguiente ciclo sin consumir un intento (<see cref="clsEnvioConexosNexo.EnEspera"/>).
    /// </summary>
    Task<clsResultadoEnvioConexos> ReintentarAsync(clsCotizacionSolicitud cotizacion, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Estado visible del envío de cada conexo, por ruta y por id (<see cref="EstadoVisible"/>), para el costeo. Vacío si la cotización no
    /// tiene solicitud de Nexo.
    /// </summary>
    Task<IReadOnlyDictionary<(Guid Ruta, string Conexo), string>> EstadosAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);

    /// <summary>
    /// #228: deja como <see cref="clsEnvioConexosNexo.Enviado"/> los conexos de las rutas que <paramref name="cotizacionNexo"/> (recién leída
    /// después de <c>POST .../costeo/servicios-conexos</c>) ya muestra y que todavía no figuraban enviados: Nexo los creó en ese momento, así
    /// que no quedan <c>pendiente</c> esperando un polling que, en una cotización entregada, ya no pasa. No guarda: el registro sale con la
    /// unidad de trabajo del llamador. Nunca llama a Nexo.
    /// </summary>
    Task RegistrarConfirmadosAsync(clsCotizacionSolicitud cotizacion, clsCotizacionNexo cotizacionNexo, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsEnvioConexosNexoService"/>. Reglas:
/// <list type="bullet">
/// <item><b>Una sola vez por solicitud de Nexo.</b> Cada paso queda en <c>EventosHerramienta</c> (<see cref="clsEnvioConexosNexo"/>)
/// atado al folio y a <c>solicitudNexoId</c>: una re-solicitud o una <c>-vN</c> es otra solicitud y otra cotización de Nexo, así que sus
/// conexos se vuelven a enviar una vez.</item>
/// <item><b>Sin duplicados.</b> Un conexo que la cotización de Nexo ya muestra (por <c>servicioConexoId</c> o por su id) no se envía y
/// queda como enviado. Antes del POST se guarda <see cref="clsEnvioConexosNexo.EnCurso"/> con los conexos que Nexo ya tenía (el mismo
/// criterio del registro de #135): si el proceso muere a mitad, el siguiente ciclo lo trata como incierto y no repite el POST. Un POST
/// incierto (sin respuesta) se relee: si Nexo lo muestra queda enviado; si no, queda <see cref="clsEnvioConexosNexo.Incierto"/>, no se
/// vuelve a enviar (Nexo no tiene DELETE ni idempotencia en ese POST) y el costeo lo muestra para que se revise en Nexo.</item>
/// <item><b>Tope.</b> Tras <c>CosteoNexo:MaximoReintentosConexos</c> POST rechazados por Nexo queda
/// <see cref="clsEnvioConexosNexo.FallidoDefinitivo"/> (warning y visible en el costeo). Si Nexo no responde ANTES del POST (catálogo de
/// conexos o lectura de la cotización) el conexo queda <see cref="clsEnvioConexosNexo.EnEspera"/>: no consume un intento ni llega al
/// tope.</item>
/// <item><b>Primer camión vigente.</b> Van pegados a la línea del primer camión no descartado de la ruta (decisión 3); mientras esa línea
/// no esté en la cotización de Nexo y la cotización se siga costeando, se espera. #259: con varias líneas para el mismo camión va a la
/// primera por id; con la cotización ya fuera del costeo, al primer camión vigente que tenga línea (variante 4a) o, si ninguno la tiene,
/// queda <see cref="clsEnvioConexosNexo.FallidoDefinitivo"/> (<see cref="LineaDestino"/>).</item>
/// <item><b>El precio no cambia.</b> En Cotifly el conexo sigue con el costo del catálogo de Nexo que se guardó en la ruta; la lectura no lo
/// importa otra vez (<c>clsLecturaCosteo.ConexosNuevos</c> lo reconoce por su <c>servicioConexoId</c>).</item>
/// <item><b>Id del catálogo.</b> El id guardado puede ser el de una fila de Nexo (agregado con el POST o importado): a Nexo viaja el del catálogo (<see cref="IClsResolutorConexosCatalogo"/>); uno que el catálogo SÍ respondió sin encontrar (no existe o el nombre es ambiguo) queda <see cref="clsEnvioConexosNexo.FallidoDefinitivo"/> y sale del lote antes del POST (todo o nada); uno cuya búsqueda no se pudo hacer queda <see cref="clsEnvioConexosNexo.EnEspera"/>, y los que sí se resolvieron se envían igual.</item>
/// </list>
/// El polling la llama con la cotización reclamada (<c>ReclamarConsultaCosteoAsync</c>): dos réplicas nunca envían a la vez.
/// </summary>
public sealed class clsEnvioConexosNexoService(
    IClsCotizacionRepository cotizaciones,
    IClsServiciosConexosNexo nexo,
    IClsResolutorConexosCatalogo resolutor,
    IClsUnitOfWork unidadTrabajo,
    IOptions<clsOpcionesCosteoNexo> opciones,
    IClsReloj reloj,
    ILogger<clsEnvioConexosNexoService> logger) : IClsEnvioConexosNexoService
{
    /// <summary>Espaciado del reintento mientras un conexo está <see cref="clsEnvioConexosNexo.EnEspera"/>: 5, 15 y 60 minutos (máximo).</summary>
    public static readonly TimeSpan[] EsperasCatalogo = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    /// <summary>Estados que ve el comercial en <c>servicios_conexos[].estado_nexo</c>.</summary>
    public const string VisiblePendiente = "pendiente";

    public const string VisibleEnviado = "enviado";
    public const string VisibleIncierto = "incierto";
    public const string VisibleFallidoDefinitivo = "fallido_definitivo";

    public async Task<clsResultadoEnvioConexos> EnviarPendientesAsync(
        clsCotizacionSolicitud cotizacion, clsCotizacionNexo? cotizacionNexo, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (cotizacionNexo is null || cotizacion.SolicitudNexoId is not { } solicitud || cotizacion.CotizacionNexoId is not { } cotizacionNexoId
            || cotizacion.Rutas.All(r => r.Conexos.Count == 0))
        {
            return clsResultadoEnvioConexos.Nada;
        }

        var registros = await cotizaciones.ListarEnviosConexosNexoAsync(cotizacion.Folio, solicitud, cancellationToken);
        var total = clsResultadoEnvioConexos.Nada;
        foreach (var ruta in cotizacion.RutasOrdenadas.Where(r => r.Conexos.Count > 0))
        {
            total = total.Mas(await EnviarRutaAsync(cotizacion, ruta, cotizacionNexo, cotizacionNexoId, solicitud, usuarioId,
                [.. registros.Where(r => r.LineaSolicitudId == ruta.Id)]));
        }

        return total;
    }

    public async Task<bool> TieneReintentosAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (cotizacion.SolicitudNexoId is not { } solicitud || cotizacion.Rutas.All(r => r.Conexos.Count == 0))
        {
            return false;
        }

        // #259: con la cotización de Nexo ya creada, un conexo que nunca se intentó (sin registro) también espera: si no, una cotización que
        // salió de la agenda con él nunca lo procesaría.
        var registros = await cotizaciones.ListarEnviosConexosNexoAsync(cotizacion.Folio, solicitud, cancellationToken);
        var conCotizacionNexo = cotizacion.CotizacionNexoId is not null;
        return cotizacion.Rutas.Any(r =>
        {
            var estados = Estados([.. registros.Where(x => x.LineaSolicitudId == r.Id)]);
            return r.Conexos.Any(c => estados.GetValueOrDefault(c.IdServicioConexo) is { } estado ? PorReintentar(estado) : conCotizacionNexo);
        });
    }

    /// <summary>¿El conexo espera otro ciclo? Un POST rechazado (<see cref="clsEnvioConexosNexo.Fallido"/>) o Nexo sin responder antes del POST.</summary>
    private static bool PorReintentar(string? estado) => estado is clsEnvioConexosNexo.Fallido or clsEnvioConexosNexo.EnEspera;

    public async Task<clsResultadoEnvioConexos> ReintentarAsync(clsCotizacionSolicitud cotizacion, Guid usuarioId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        if (cotizacion.CotizacionNexoId is not { } cotizacionNexoId || cotizacion.SolicitudNexoId is not { } solicitud)
        {
            return clsResultadoEnvioConexos.Nada;
        }

        var leida = await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, CancellationToken.None);
        if (leida.EsExitoso)
        {
            return await EnviarPendientesAsync(cotizacion, leida.Valor, usuarioId, cancellationToken);
        }

        // Sin la cotización de Nexo no hay POST: se espera al siguiente ciclo sin consumir un intento (el tope cuenta solo POST rechazados).
        logger.LogWarning("No se pudo leer la cotización de Nexo de {Folio} para reintentar sus conexos ({Codigo}): se espera al siguiente ciclo.",
            cotizacion.Folio, leida.Error.Codigo);
        var registros = await cotizaciones.ListarEnviosConexosNexoAsync(cotizacion.Folio, solicitud, cancellationToken);
        var total = clsResultadoEnvioConexos.Nada;
        var guardar = false;
        foreach (var ruta in cotizacion.RutasOrdenadas)
        {
            var propios = registros.Where(r => r.LineaSolicitudId == ruta.Id).ToList();
            var estados = Estados(propios);
            // #259: también los que nunca se intentaron (sin registro): quedan en espera, con su tope, y no huérfanos.
            var pendientes = ruta.Conexos.Select(c => c.IdServicioConexo)
                .Where(id => estados.GetValueOrDefault(id) is not { } estado || PorReintentar(estado)).ToList();
            if (pendientes.Count == 0)
            {
                continue;
            }

            var espera = Esperar(cotizacion, solicitud, ruta, propios, estados, propios.Count == 0 ? 1 : propios.Max(r => r.Intento) + 1, pendientes);
            guardar |= espera.Guardo;
            total = total.Mas(new clsResultadoEnvioConexos(0, espera.Quedan) { EsperaReintento = espera.Reintento });
        }

        if (guardar)
        {
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }

        return total;
    }

    public async Task<IReadOnlyDictionary<(Guid Ruta, string Conexo), string>> EstadosAsync(
        clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var visibles = new Dictionary<(Guid, string), string>();
        if (cotizacion.SolicitudNexoId is not { } solicitud || cotizacion.Rutas.All(r => r.Conexos.Count == 0))
        {
            return visibles;
        }

        var registros = await cotizaciones.ListarEnviosConexosNexoAsync(cotizacion.Folio, solicitud, cancellationToken);
        foreach (var ruta in cotizacion.Rutas)
        {
            var estados = Estados([.. registros.Where(r => r.LineaSolicitudId == ruta.Id)]);
            foreach (var conexo in ruta.Conexos)
            {
                visibles[(ruta.Id, conexo.IdServicioConexo)] = EstadoVisible(estados.GetValueOrDefault(conexo.IdServicioConexo));
            }
        }

        return visibles;
    }

    public async Task RegistrarConfirmadosAsync(
        clsCotizacionSolicitud cotizacion, clsCotizacionNexo cotizacionNexo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(cotizacionNexo);
        if (cotizacion.SolicitudNexoId is not { } solicitud || cotizacion.Rutas.All(r => r.Conexos.Count == 0))
        {
            return;
        }

        var registros = await cotizaciones.ListarEnviosConexosNexoAsync(cotizacion.Folio, solicitud, cancellationToken);
        foreach (var ruta in cotizacion.RutasOrdenadas.Where(r => r.Conexos.Count > 0))
        {
            var deRuta = registros.Where(r => r.LineaSolicitudId == ruta.Id).ToList();
            var estados = Estados(deRuta);
            var confirmados = ruta.Conexos
                .Where(c => estados.GetValueOrDefault(c.IdServicioConexo) != clsEnvioConexosNexo.Enviado
                            && EstaEnNexo(cotizacion, ruta, cotizacionNexo, c.IdServicioConexo))
                .Select(c => c.IdServicioConexo)
                .ToList();
            if (confirmados.Count > 0)
            {
                var intento = (deRuta.Count == 0 ? 0 : deRuta.Max(r => r.Intento)) + 1;
                cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Enviado, confirmados, cotizacionNexo));
            }
        }
    }

    /// <summary>
    /// Estado que ve el comercial: <c>enviado</c>; <c>incierto</c> (POST sin respuesta que Nexo no muestra, o cortado a mitad: hay que
    /// revisar Nexo, no se reintenta solo); <c>fallido_definitivo</c> (se agotaron los reintentos); <c>pendiente</c> (todavía no se envía o
    /// se reintentará).
    /// </summary>
    public static string EstadoVisible(string? estado) => estado switch
    {
        clsEnvioConexosNexo.Enviado => VisibleEnviado,
        clsEnvioConexosNexo.Incierto or clsEnvioConexosNexo.EnCurso => VisibleIncierto,
        clsEnvioConexosNexo.FallidoDefinitivo => VisibleFallidoDefinitivo,
        _ => VisiblePendiente,
    };

    private async Task<clsResultadoEnvioConexos> EnviarRutaAsync(
        clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, clsCotizacionNexo cotizacionNexo, string cotizacionNexoId, string solicitud,
        Guid usuarioId, IReadOnlyList<clsEnvioConexosNexo> registros)
    {
        var intento = registros.Count == 0 ? 0 : registros.Max(r => r.Intento);
        var estados = Estados(registros);
        var conexos = ruta.Conexos.ToList();
        var guardar = false;

        // Los que Nexo ya muestra quedan enviados (también los inciertos o definitivos que una lectura confirmó).
        // Un envío incierto se confirma también por el id del catálogo con que viajó (el guardado puede ser el de una fila de Nexo).
        var confirmados = conexos
            .Where(c => estados.GetValueOrDefault(c.IdServicioConexo) != clsEnvioConexosNexo.Enviado
                        && (EstaEnNexo(cotizacion, ruta, cotizacionNexo, c.IdServicioConexo)
                            || (IdCatalogoEnviado(registros, c.IdServicioConexo) is { } enviadoComo && EstaEnNexo(cotizacion, ruta, cotizacionNexo, enviadoComo))))
            .Select(c => c.IdServicioConexo)
            .ToList();
        if (confirmados.Count > 0)
        {
            intento++;
            cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Enviado, confirmados, cotizacionNexo));
            guardar = true;
        }

        var bloqueados = conexos
            .Where(c => !confirmados.Contains(c.IdServicioConexo)
                        && estados.GetValueOrDefault(c.IdServicioConexo) is clsEnvioConexosNexo.Incierto or clsEnvioConexosNexo.EnCurso)
            .ToList();
        if (bloqueados.Count > 0)
        {
            logger.LogWarning(
                "Conexos de la ruta {Ruta} de {Folio} con un envío incierto a Nexo: no se reenvían ({Conexos}). Revísalos en la cotización de Nexo.",
                ruta.Orden, cotizacion.Folio, string.Join(", ", bloqueados.Select(c => c.IdServicioConexo)));
        }

        var candidatos = conexos
            .Where(c => !confirmados.Contains(c.IdServicioConexo) && !bloqueados.Contains(c)
                        && (estados.GetValueOrDefault(c.IdServicioConexo) is null || PorReintentar(estados.GetValueOrDefault(c.IdServicioConexo))))
            .ToList();

        // Tope de intentos fallidos: quedan fallido_definitivo (warning y visible en el costeo).
        var agotados = AlTope(registros, [.. candidatos.Select(c => c.IdServicioConexo)]);
        if (agotados.Count > 0)
        {
            intento++;
            Definitivo(cotizacion, solicitud, ruta, intento, agotados);
            guardar = true;
        }

        var porEnviar = candidatos.Where(c => !agotados.Contains(c.IdServicioConexo, StringComparer.OrdinalIgnoreCase)).ToList();
        var lineaId = porEnviar.Count == 0 ? null : LineaDestino(cotizacion, ruta, cotizacionNexo);
        if (lineaId is null)
        {
            // #259: con la cotización ya fuera del costeo (entregada, descartada, vencida) la línea no va a aparecer: sin ningún camión
            // vigente con línea en Nexo, los conexos quedan fallido_definitivo (warning y aviso en el costeo, no bloquean finalizar). Nunca
            // se sale en silencio dejándolos pendiente sin nadie que los procese.
            if (porEnviar.Count > 0 && !cotizacion.SeConsultaEnNexo)
            {
                intento++;
                var sinLinea = porEnviar.Select(c => c.IdServicioConexo).ToList();
                cotizaciones.RegistrarEnvioConexosNexo(new clsEnvioConexosNexo(
                    cotizacion.Folio, solicitud, ruta.Id, intento, clsEnvioConexosNexo.FallidoDefinitivo, sinLinea, []));
                logger.LogWarning(
                    "La ruta {Ruta} de {Folio} ya no se costea en Nexo y ningún camión vigente tiene línea en la cotización de Nexo: sus conexos quedan fallido_definitivo ({Conexos}). Revísalos en Nexo.",
                    ruta.Orden, cotizacion.Folio, string.Join(", ", sinLinea));
                guardar = true;
            }

            if (guardar)
            {
                await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            }

            // Nada que enviar, o (en costeo) la línea del primer camión vigente todavía no está en la cotización de Nexo: no cuenta como
            // reintento; el polling, que sigue consultando la cotización, lo intenta cuando la línea aparezca.
            return clsResultadoEnvioConexos.Nada;
        }

        // El id guardado puede ser el de la FILA de otra cotización de Nexo (agregado con el POST o importado): a Nexo va el id del
        // CATÁLOGO, resuelto por id o por nombre (clsResolutorConexosCatalogo). Los que ya traen un id del catálogo no dependen de la
        // búsqueda de los demás.
        var resolucion = await resolutor.ResolverAsync(
            usuarioId, [.. porEnviar.Select(c => (c.IdServicioConexo, c.IdServicioConexoCatalogo, c.Nombre))], CancellationToken.None);
        var catalogo = resolucion.Resueltos;

        // Catálogo caído para algunos: esos esperan al siguiente ciclo SIN consumir un intento (no hubo POST) y nunca pasan a
        // fallido_definitivo por eso; los demás siguen.
        var enEspera = porEnviar
            .Where(c => !catalogo.ContainsKey(c.IdServicioConexo) && resolucion.SinCatalogo.Contains(c.IdServicioConexo))
            .Select(c => c.IdServicioConexo)
            .ToList();

        // Los que la cotización de Nexo ya tiene por su id del catálogo (p. ej. agregados a mano en esta misma solicitud) quedan enviados.
        var yaEstan = porEnviar
            .Where(c => catalogo.TryGetValue(c.IdServicioConexo, out var deCatalogo) && EstaEnNexo(cotizacion, ruta, cotizacionNexo, deCatalogo.Id))
            .Select(c => c.IdServicioConexo)
            .ToList();

        // Aislar fallos: un conexo que el catálogo SÍ respondió sin encontrar (no existe o el nombre es ambiguo) no tumba el lote (el POST
        // de Nexo es todo o nada): queda fallido_definitivo.
        var irresolubles = porEnviar
            .Where(c => !catalogo.ContainsKey(c.IdServicioConexo) && !resolucion.SinCatalogo.Contains(c.IdServicioConexo))
            .Select(c => c.IdServicioConexo)
            .ToList();
        if (enEspera.Count > 0)
        {
            logger.LogWarning(
                "El catálogo de conexos de Nexo no respondió para la ruta {Ruta} de {Folio} ({Codigo}): {Conexos} esperan al siguiente ciclo sin consumir un intento.",
                ruta.Orden, cotizacion.Folio, resolucion.ErrorCatalogo?.Codigo, string.Join(", ", enEspera));
        }

        // La espera tiene tope (CosteoNexo:EsperaMaximaCatalogoConexosHoras): pasado, el conexo queda fallido_definitivo.
        var espera = enEspera.Count == 0 ? (Quedan: 0, Reintento: (TimeSpan?)null, Guardo: false)
            : Esperar(cotizacion, solicitud, ruta, registros, estados, intento + 1, enEspera);
        if (yaEstan.Count > 0 || irresolubles.Count > 0 || espera.Guardo)
        {
            intento++;   // el mismo número con que Esperar registró
            if (yaEstan.Count > 0)
            {
                cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Enviado, yaEstan, cotizacionNexo));
            }

            if (irresolubles.Count > 0)
            {
                cotizaciones.RegistrarEnvioConexosNexo(new clsEnvioConexosNexo(
                    cotizacion.Folio, solicitud, ruta.Id, intento, clsEnvioConexosNexo.FallidoDefinitivo, irresolubles, []));
                logger.LogWarning(
                    "Conexos de la ruta {Ruta} de {Folio} sin servicio en el catálogo de Nexo (ni por id ni por nombre): no se envían ({Conexos}). Revísalos en Nexo.",
                    ruta.Orden, cotizacion.Folio, string.Join(", ", irresolubles));
            }

            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        }

        var enEsperaResultado = new clsResultadoEnvioConexos(0, espera.Quedan) { EsperaReintento = espera.Reintento };
        var pendientes = porEnviar
            .Where(c => !yaEstan.Contains(c.IdServicioConexo) && !irresolubles.Contains(c.IdServicioConexo) && !enEspera.Contains(c.IdServicioConexo))
            .ToList();
        if (pendientes.Count == 0)
        {
            return new clsResultadoEnvioConexos(yaEstan.Count, 0).Mas(enEsperaResultado);
        }

        // El intento queda guardado ANTES del POST (que no es idempotente): si el proceso muere a mitad, nadie lo repite a ciegas.
        intento++;
        var ids = pendientes.Select(c => c.IdServicioConexo).ToList();
        cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.EnCurso, ids, cotizacionNexo) with
        {
            // El id del catálogo con que viajó cada uno: con él se confirma después un envío incierto.
            ConexosCatalogo = [.. ids.Select(id => catalogo[id].Id)],
        });
        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);

        var agregado = await nexo.AgregarAsync(
            usuarioId, cotizacionNexoId, [.. pendientes.Select(c => new clsConexoParaNexo(lineaId, catalogo[c.IdServicioConexo].Id, c.Cantidad))], CancellationToken.None);
        if (agregado.EsFallo)
        {
            // Nexo no lo aplicó: se reintenta en el siguiente ciclo (o queda definitivo si llegó al tope).
            var fallido = Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Fallido, ids, cotizacionNexo);
            cotizaciones.RegistrarEnvioConexosNexo(fallido);
            var alTope = AlTope([.. registros, fallido], ids);
            if (alTope.Count > 0)
            {
                Definitivo(cotizacion, solicitud, ruta, intento + 1, alTope);
            }

            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            logger.LogWarning("No se pudieron enviar a Nexo los conexos de la ruta {Ruta} de {Folio} ({Codigo}); se reintenta en el siguiente ciclo.",
                ruta.Orden, cotizacion.Folio, agregado.Error.Codigo);
            return new clsResultadoEnvioConexos(0, ids.Count - alTope.Count).Mas(enEsperaResultado);
        }

        var enviados = ids;
        if (!agregado.Valor.Seguro)
        {
            // Sin respuesta: se relee la cotización de Nexo; lo que muestra quedó enviado y lo demás queda incierto (no se reenvía).
            var releida = agregado.Valor.Cotizacion is { } devuelta
                ? clsResultado<clsCotizacionNexo>.Exito(devuelta)
                : await nexo.LeerCotizacionAsync(usuarioId, cotizacionNexoId, CancellationToken.None);
            enviados = releida.EsExitoso
                ? [.. ids.Where(id => EstaEnNexo(cotizacion, ruta, releida.Valor, id) || EstaEnNexo(cotizacion, ruta, releida.Valor, catalogo[id].Id))]
                : [];
            var inciertos = ids.Except(enviados, StringComparer.OrdinalIgnoreCase).ToList();
            if (inciertos.Count > 0)
            {
                cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Incierto, inciertos, cotizacionNexo));
                logger.LogWarning(
                    "Envío incierto a Nexo de los conexos de la ruta {Ruta} de {Folio} ({Conexos}): no se reenvían hasta que la cotización de Nexo los muestre.",
                    ruta.Orden, cotizacion.Folio, string.Join(", ", inciertos));
            }
        }

        if (enviados.Count > 0)
        {
            cotizaciones.RegistrarEnvioConexosNexo(Registro(cotizacion, solicitud, ruta, intento, clsEnvioConexosNexo.Enviado, enviados, cotizacionNexo));
        }

        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        return new clsResultadoEnvioConexos(enviados.Count, 0).Mas(enEsperaResultado);
    }

    /// <summary>
    /// Línea de la cotización de Nexo a la que van los conexos de <paramref name="ruta"/>: la del primer camión vigente (decisión 3). #259
    /// (variante 4a, ADR-029, decisión del usuario del 2026-11-06, extiende la decisión 3): si ese camión no tiene línea y la cotización ya no se
    /// costea en Nexo (<see cref="clsCotizacionSolicitud.SeConsultaEnNexo"/> falso: la línea ya no va a aparecer), la del primer camión
    /// vigente que SÍ tiene línea; un camión entregado sin flete se trata como uno rechazado. <c>null</c> si no hay ninguna (o si, en
    /// costeo, el primer camión todavía no la tiene: se espera).
    /// </summary>
    public static string? LineaDestino(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, clsCotizacionNexo cotizacionNexo)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(ruta);
        ArgumentNullException.ThrowIfNull(cotizacionNexo);
        var primera = clsServiciosConexosCosteoService.LineaDeNexo(cotizacionNexo, clsServiciosConexosCosteoService.PrimerCamion(ruta));
        if (primera is not null || cotizacion.SeConsultaEnNexo)
        {
            return primera;
        }

        return ruta.VehiculosOrdenados
            .Where(v => v.EstadoCosteo is not (EstadoCosteo.Descartada or EstadoCosteo.Rechazada))
            .Select(v => clsServiciosConexosCosteoService.LineaDeNexo(cotizacionNexo, v))
            .FirstOrDefault(l => l is not null);
    }

    /// <summary>El id del catálogo con que viajó <paramref name="id"/> en su último intento (registro en curso), o <c>null</c>.</summary>
    private static string? IdCatalogoEnviado(IReadOnlyList<clsEnvioConexosNexo> registros, string id)
    {
        var registro = registros
            .Where(r => r.ConexosCatalogo is { } catalogo && catalogo.Count == r.Conexos.Count && r.Conexos.Contains(id, StringComparer.OrdinalIgnoreCase))
            .MaxBy(r => r.Intento);
        if (registro is null)
        {
            return null;
        }

        var indice = registro.Conexos.Select((c, i) => (c, i)).First(x => string.Equals(x.c, id, StringComparison.OrdinalIgnoreCase)).i;
        return registro.ConexosCatalogo![indice];
    }

    /// <summary>
    /// Nexo no respondió antes del POST para los conexos de <paramref name="ids"/>: los que todavía no esperan quedan
    /// <see cref="clsEnvioConexosNexo.EnEspera"/> desde ahora (una sola fila por espera, no una por ciclo); los que llevan más de
    /// <c>CosteoNexo:EsperaMaximaCatalogoConexosHoras</c> esperando quedan <see cref="clsEnvioConexosNexo.FallidoDefinitivo"/> (warning y
    /// aviso en el costeo). Devuelve cuántos siguen esperando, la espera hasta el siguiente reintento (<see cref="EsperaReintento"/>) y si
    /// registró algo (con el número <paramref name="intento"/>).
    /// </summary>
    private (int Quedan, TimeSpan? Reintento, bool Guardo) Esperar(
        clsCotizacionSolicitud cotizacion, string solicitud, clsLineaSolicitud ruta, IReadOnlyList<clsEnvioConexosNexo> registros,
        IReadOnlyDictionary<string, string> estados, int intento, IReadOnlyList<string> ids)
    {
        var ahora = reloj.AhoraUtc;
        var maxima = opciones.Value.EsperaMaximaCatalogoConexos;
        var desde = ids.ToDictionary(
            id => id,
            id => estados.GetValueOrDefault(id) == clsEnvioConexosNexo.EnEspera ? EsperaDesde(registros, id) ?? ahora : ahora,
            StringComparer.OrdinalIgnoreCase);
        var vencidos = ids.Where(id => ahora - desde[id] >= maxima).ToList();
        var nuevos = ids.Where(id => estados.GetValueOrDefault(id) != clsEnvioConexosNexo.EnEspera && !vencidos.Contains(id)).ToList();
        if (nuevos.Count > 0)
        {
            cotizaciones.RegistrarEnvioConexosNexo(new clsEnvioConexosNexo(
                cotizacion.Folio, solicitud, ruta.Id, intento, clsEnvioConexosNexo.EnEspera, nuevos, []) { EsperaDesde = ahora });
        }

        if (vencidos.Count > 0)
        {
            cotizaciones.RegistrarEnvioConexosNexo(new clsEnvioConexosNexo(
                cotizacion.Folio, solicitud, ruta.Id, intento, clsEnvioConexosNexo.FallidoDefinitivo, vencidos, []));
            logger.LogWarning(
                "Los conexos de la ruta {Ruta} de {Folio} llevan más de {Horas} h esperando a que Nexo responda (catálogo de conexos o cotización) ({Conexos}): quedan fallido_definitivo. Revísalos en Nexo.",
                ruta.Orden, cotizacion.Folio, opciones.Value.EsperaMaximaCatalogoConexosHoras, string.Join(", ", vencidos));
        }

        var siguen = ids.Except(vencidos, StringComparer.OrdinalIgnoreCase).ToList();
        TimeSpan? reintento = siguen.Count == 0 ? null : EsperaReintento(siguen.Min(id => ahora - desde[id]));
        return (siguen.Count, reintento, nuevos.Count > 0 || vencidos.Count > 0);
    }

    /// <summary>Espaciado del reintento según cuánto lleva la espera: 5 min, luego 15 y luego 60 (máximo).</summary>
    public static TimeSpan EsperaReintento(TimeSpan transcurrido)
    {
        var acumulado = TimeSpan.Zero;
        foreach (var espera in EsperasCatalogo)
        {
            acumulado += espera;
            if (transcurrido < acumulado)
            {
                return espera;
            }
        }

        return EsperasCatalogo[^1];
    }

    /// <summary>Cuándo empezó la espera vigente de <paramref name="id"/>: el registro en espera más reciente que lo incluye.</summary>
    private static DateTimeOffset? EsperaDesde(IReadOnlyList<clsEnvioConexosNexo> registros, string id) =>
        registros
            .Where(r => r.Estado == clsEnvioConexosNexo.EnEspera && r.Conexos.Contains(id, StringComparer.OrdinalIgnoreCase))
            .MaxBy(r => r.Intento)?.EsperaDesde;

    /// <summary>Los conexos de <paramref name="ids"/> que ya tienen <c>MaximoReintentosConexos</c> POST rechazados por Nexo.</summary>
    private List<string> AlTope(IReadOnlyList<clsEnvioConexosNexo> registros, IReadOnlyList<string> ids)
    {
        var maximo = opciones.Value.MaximoReintentosConexos;
        return [.. ids.Where(id => registros.Count(r => r.Estado == clsEnvioConexosNexo.Fallido
            && r.Conexos.Contains(id, StringComparer.OrdinalIgnoreCase)) >= maximo)];
    }

    private void Definitivo(clsCotizacionSolicitud cotizacion, string solicitud, clsLineaSolicitud ruta, int intento, IReadOnlyList<string> ids)
    {
        cotizaciones.RegistrarEnvioConexosNexo(new clsEnvioConexosNexo(
            cotizacion.Folio, solicitud, ruta.Id, intento, clsEnvioConexosNexo.FallidoDefinitivo, ids, []));
        logger.LogWarning(
            "Los conexos de la ruta {Ruta} de {Folio} no llegaron a Nexo tras {Maximo} intentos ({Conexos}): quedan fallido_definitivo. Revísalos en Nexo.",
            ruta.Orden, cotizacion.Folio, opciones.Value.MaximoReintentosConexos, string.Join(", ", ids));
    }

    /// <summary>
    /// Estado vigente de cada conexo: el de su intento más alto; dentro del mismo intento, un resultado (enviado, incierto o fallido) manda
    /// sobre <see cref="clsEnvioConexosNexo.EnCurso"/>. Un intento que se quedó en curso (el proceso murió a mitad del POST) es incierto.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Estados(IReadOnlyList<clsEnvioConexosNexo> registros)
    {
        ArgumentNullException.ThrowIfNull(registros);
        var estados = new Dictionary<string, (int Intento, string Estado)>(StringComparer.OrdinalIgnoreCase);
        foreach (var registro in registros)
        {
            foreach (var id in registro.Conexos)
            {
                if (!estados.TryGetValue(id, out var actual) || registro.Intento > actual.Intento
                    || (registro.Intento == actual.Intento && actual.Estado == clsEnvioConexosNexo.EnCurso))
                {
                    estados[id] = (registro.Intento, registro.Estado);
                }
            }
        }

        return estados.ToDictionary(e => e.Key, e => e.Value.Estado, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ¿La cotización de Nexo ya tiene el conexo <paramref name="idServicio"/> en esta ruta? Por su <c>servicioConexoId</c> (o por su id, si
    /// Cotifly lo guardó con el id de Nexo), en una línea de un camión de la ruta o en una línea que no se puede ubicar (por las dudas: no se
    /// arriesga un duplicado).
    /// </summary>
    public static bool EstaEnNexo(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta, clsCotizacionNexo cotizacionNexo, string idServicio)
    {
        ArgumentNullException.ThrowIfNull(cotizacionNexo);
        ArgumentNullException.ThrowIfNull(ruta);
        return cotizacionNexo.Conexos.Any(c =>
        {
            if (!string.Equals(c.ServicioConexoId, idServicio, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.Id, idServicio, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suRuta = clsServiciosConexosCosteoService.RutaDelConexo(cotizacion, cotizacionNexo, c);
            return suRuta is null || suRuta.Id == ruta.Id;
        });
    }

    private static clsEnvioConexosNexo Registro(
        clsCotizacionSolicitud cotizacion, string solicitud, clsLineaSolicitud ruta, int intento, string estado, IReadOnlyList<string> conexos,
        clsCotizacionNexo cotizacionNexo) =>
        new(cotizacion.Folio, solicitud, ruta.Id, intento, estado, conexos, [.. cotizacionNexo.Conexos.Select(c => c.Id)]);
}
