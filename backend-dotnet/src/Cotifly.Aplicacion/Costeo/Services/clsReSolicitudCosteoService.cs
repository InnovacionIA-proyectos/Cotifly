using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// "Cotizar costo" (HU-013.4, ADR-018 §5): <c>POST /web/cotizaciones/{folio}/costeo/re-solicitar</c>. Es el botón de la pantalla, el
/// Confirmar de la acción propuesta <c>re_solicitar_costeo</c> y la versión nueva que pide <c>PDF_YA_ENVIADO</c>.
/// </summary>
public interface IClsReSolicitudCosteoService
{
    /// <summary>
    /// Vuelve a pedir el costeo a Nexo, en el mismo folio (sin cambios de carga ni <c>nueva_version</c>) o en la versión <c>-vN</c>.
    /// Errores: <c>COTIZACION_NO_ENCONTRADA</c> (404, también si no es suya), <c>SOLICITUD_COSTEO_INVALIDA</c> (400: motivo, cambios
    /// de carga o reglas de la captura), <c>SOLICITUD_INVALIDA</c> (400 <c>ErrorWeb</c>: <c>Idempotency-Key</c> inválida o reutilizada
    /// con otra petición), <c>CONFLICTO_CONCURRENCIA</c>, <c>ESTADO_NO_PERMITE</c> y <c>PDF_YA_ENVIADO</c> (409). Si Nexo falla, la
    /// cotización (o la versión nueva) se devuelve igual, en <c>error_envio</c> con el motivo.
    /// </summary>
    Task<clsResultado<clsReSolicitudCosteo>> ReSolicitarAsync(
        Guid usuarioId,
        string folio,
        clsEntradaReSolicitudCosteo entrada,
        int versionEsperada,
        string? claveIdempotencia,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Las MISMAS validaciones de <see cref="ReSolicitarAsync"/> sin escribir nada ni enviar a Nexo (HU-013.6, #102): entrada, estado,
    /// regla del PDF, líneas guardadas, política activa y, con cambios de carga, el catálogo de Nexo (G2/G3, solo lectura) y las reglas
    /// de la captura sobre la combinación (rangos, peso frente a las posiciones). La usa la propuesta <c>re_solicitar_costeo</c> del
    /// asistente: una propuesta que el endpoint rechazaría nunca se arma. No valida el <c>If-Match</c> ni la TRM del día (cambian
    /// entre proponer y confirmar). <c>null</c> si pasa.
    /// </summary>
    Task<clsErrorDominio?> ValidarAsync(Guid usuarioId, clsCotizacionSolicitud cotizacion, clsEntradaReSolicitudCosteo entrada, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsReSolicitudCosteoService"/>. Nexo no tiene una operación para reabrir ni para re-solicitar una
/// solicitud (<c>docs/conexion_nexo/HU-013-preguntas-nexo.md</c>, pregunta 4): re-solicitar es una <b>captura nueva</b>
/// (<c>POST /api/captura-solicitudes</c>) con lo guardado, enviada con <see cref="IClsEnvioCosteoService.EnviarGuardadaAsync"/> (el
/// mismo envío normalizado del alta y de las licitaciones), con una nota de trazabilidad en <c>notas</c>.
/// <list type="bullet">
/// <item>Todo lo que se valida (entrada, <c>If-Match</c>, estado, regla del PDF, política, catálogos y reglas de la captura) se valida
/// ANTES de escribir: un 400 o un 409 no cambian nada.</item>
/// <item><b>Reserva</b>: la cotización original sube su versión de negocio y se guarda con <c>GuardarCambiosSiVersionAsync</c>, junto
/// con la instantánea completa del costeo anterior en <c>EventosHerramienta</c>, en la MISMA transacción. Solo la petición que gana la
/// reserva continúa; las simultáneas con el mismo <c>If-Match</c> reciben <c>CONFLICTO_CONCURRENCIA</c> y nunca llaman a Nexo.</item>
/// <item><b>Mismo folio</b>: la reserva además olvida el costeo anterior y deja la cotización en <c>pendiente_envio</c> con el PDF
/// invalidado; después se envía a Nexo y el polling se reanuda con el id de la solicitud nueva.</item>
/// <item><b>Versión nueva</b>: después de la reserva crea <c>{folio base}-vN</c> con
/// <see cref="IClsGeneradorFolioService.CrearNuevaVersionAsync{TValor}"/> (formulario, líneas, posiciones y, si había, el margen
/// negociado; cambios de carga validados por catálogo, #106 G2) y la envía. La original solo cambia su versión de negocio.</item>
/// <item>Un margen negociado se conserva sobre el costo nuevo (decisión del 2026-10-06, <c>clsEnvioCosteoService</c>).</item>
/// <item><c>Idempotency-Key</c> (<see cref="IClsAlmacenIdempotencia"/>): la repetición devuelve el resultado de la primera.</item>
/// <item>Los eventos de auditoría van SIN <c>ThreadId</c>: traen costos y no deben quedar al alcance del asistente (#102, ADR-018).</item>
/// <item><b>Por ruta (HU-021.9, #136).</b> La versión nueva copia cada ruta con su línea de negocio, sus mercancías, sus camiones
/// vigentes y sus conexos; si los cambios de carga cambian el peso o las mercancías de la ruta, sus camiones se borran y se vuelven a
/// planificar con <see cref="IClsPlanificadorCamiones"/> (el cubicador cuando exista, #133). Los conexos son de Cotifly (decisión 3):
/// se conservan en el mismo folio y se copian a la versión nueva (<c>conexos_conservados</c>).</item>
/// </list>
/// </summary>
public sealed class clsReSolicitudCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsGeneradorFolioService folios,
    IClsEnvioCosteoService envio,
    IClsValidadorCatalogosCaptura catalogos,
    IClsValidadorFilasLicitacion filasLicitacion,
    IClsFijacionMonedaService monedas,
    IClsMisCotizacionesService misCotizaciones,
    IClsAlmacenIdempotencia idempotencia,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaRepository registroAuditoria,
    IClsAuditoriaService auditoria,
    IClsPlanificadorCamiones planificador,
    IClsResolutorConexosCatalogo resolutorConexos,
    ILogger<clsReSolicitudCosteoService> logger) : IClsReSolicitudCosteoService
{
    /// <summary>Motivo de <c>ESTADO_NO_PERMITE</c>: cambios de carga en una licitación de varias rutas.</summary>
    public const string MotivoLicitacion = "LICITACION";

    /// <summary>Motivo de <c>ESTADO_NO_PERMITE</c> (D9, #139): cambios de carga en un formulario de varias rutas.</summary>
    public const string MotivoVariasRutas = "VARIAS_RUTAS";

    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>, contrato).</summary>
    public const string HerramientaAuditoria = "costeo_nexo_re_solicitud";

    /// <summary>Ámbito de las claves de idempotencia (con el comercial).</summary>
    public const string OperacionIdempotencia = "re_solicitar_costeo";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c> desde la re-solicitud.</summary>
    public const string MotivoVersion = "Re-solicitar el costeo después del envío";

    private static readonly JsonSerializerOptions _jsonHuella = new(JsonSerializerDefaults.Web);

    public Task<clsResultado<clsReSolicitudCosteo>> ReSolicitarAsync(
        Guid usuarioId,
        string folio,
        clsEntradaReSolicitudCosteo entrada,
        int versionEsperada,
        string? claveIdempotencia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return claveIdempotencia is null
            ? EjecutarAsync(usuarioId, folio, entrada, versionEsperada, cancellationToken)
            : idempotencia.EjecutarUnaVezAsync(
                $"{OperacionIdempotencia}:{usuarioId}",
                claveIdempotencia,
                Huella(folio, entrada),
                ct => EjecutarAsync(usuarioId, folio, entrada, versionEsperada, ct),
                SeGuarda,
                cancellationToken);
    }

    /// <summary>
    /// Qué consume la <c>Idempotency-Key</c>: los éxitos con Nexo. Un 200 con <c>error_envio</c> (Nexo falló) no la consume: repetir
    /// con la misma clave vuelve a ejecutar (y, como la versión ya subió, responde <c>CONFLICTO_CONCURRENCIA</c> si el <c>If-Match</c>
    /// es el de antes; el comercial recarga y reintenta). Contrato: <c>IdempotencyKey</c>.
    /// </summary>
    public static bool SeGuarda(clsResultado<clsReSolicitudCosteo> resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        return resultado.EsExitoso && resultado.Valor.Cotizacion.EstadoCosteo != "error_envio";
    }

    /// <summary>Nota de trazabilidad que viaja en <c>notas</c> de la captura nueva.</summary>
    public static string NotaTrazabilidad(string folio, string? solicitudAnterior) => solicitudAnterior is null
        ? $"Re-solicitud de {folio}."
        : $"Re-solicitud de {folio}; reemplaza la solicitud {solicitudAnterior}.";

    private async Task<clsResultado<clsReSolicitudCosteo>> EjecutarAsync(
        Guid usuarioId, string folio, clsEntradaReSolicitudCosteo entrada, int versionEsperada, CancellationToken cancellationToken)
    {
        var cronometro = Stopwatch.StartNew();
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var entradaAuditoria = new
        {
            folio = cotizacion.Folio,
            usuario_id = usuarioId,
            motivo = Motivo(entrada.Motivo),
            version_nueva = entrada.VersionNueva,
            datos_carga = entrada.DatosCarga?.Presentes.Order(StringComparer.Ordinal).ToArray(),
        };
        var (resultado, resultante, pdfInvalidado) =
            await ReSolicitarAsync(cotizacion, usuarioId, entrada, entradaAuditoria, versionEsperada, cronometro, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            entradaAuditoria,
            resultado.EsExitoso && resultante is not null
                ? new
                {
                    fase = "resultado",
                    folio_origen = cotizacion.Folio,
                    folio_resultante = resultante.Folio,
                    version = resultante.Version,
                    estado_costeo = resultante.EstadoCosteo is { } estadoNuevo ? clsCodigoEnum.ACodigo(estadoNuevo) : null,
                    costeo_id = resultante.SolicitudNexoId,
                    motivo_estado = resultante.MotivoEstadoCosteo,
                    pdf_invalidado = pdfInvalidado,
                    conexos_conservados = resultado.Valor.ConexosConservados,
                    rutas_replanificadas = resultado.Valor.RutasReplanificadas,
                }
                : new { fase = "rechazada", error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            threadId: null);
        return resultado;
    }

    private async Task<(clsResultado<clsReSolicitudCosteo> Resultado, clsCotizacionSolicitud? Resultante, bool PdfInvalidado)> ReSolicitarAsync(
        clsCotizacionSolicitud cotizacion,
        Guid usuarioId,
        clsEntradaReSolicitudCosteo entrada,
        object entradaAuditoria,
        int versionEsperada,
        Stopwatch cronometro,
        CancellationToken cancellationToken)
    {
        if (ValidarEntrada(entrada) is { } invalida)
        {
            return (invalida, null, false);
        }

        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), null, false);
        }

        var validada = await ValidarReglasAsync(usuarioId, cotizacion, entrada, cancellationToken);
        if (validada.EsFallo)
        {
            return (validada.Error, null, false);
        }

        // Instantánea completa del costeo anterior: se toma antes de cualquier cambio y se guarda con la reserva.
        var v = validada.Valor;
        var instantanea = Instantanea(cotizacion);
        var conservados = ConexosConservados(cotizacion);
        var nota = NotaTrazabilidad(cotizacion.Folio, cotizacion.SolicitudNexoId);
        var (resultado, resultante, pdfInvalidado) = entrada.VersionNueva
            ? await CrearVersionAsync(cotizacion, usuarioId, v, versionEsperada,
                Reserva(entradaAuditoria, instantanea, cronometro), nota, cancellationToken)
            : await ReenviarAsync(cotizacion, usuarioId, versionEsperada, v.Lineas, v.Politica,
                Reserva(entradaAuditoria, instantanea, cronometro), nota, cancellationToken);
        return (resultado.EsExitoso
            ? clsResultado<clsReSolicitudCosteo>.Exito(resultado.Valor with { ConexosDescartados = [], ConexosConservados = conservados })
            : resultado, resultante, pdfInvalidado);
    }

    /// <summary>
    /// Regla de los servicios conexos al re-solicitar (HU-021.9, #136; rehace el aviso de #124 con la decisión 3 del 2026-10-08). Los
    /// conexos son filas de la ruta en Cotifly, con el costo de Nexo como precio, y Cotifly es su fuente: el precio al cliente sale de
    /// Cotifly. Por eso NO se descartan: en el mismo folio la ruta los conserva (solo se olvidan los costos de los camiones) y la versión
    /// <c>-vN</c> los copia a la ruta del mismo orden, con la misma cantidad y el costo unitario del catálogo de hoy. En Nexo:
    /// <list type="bullet">
    /// <item>la captura nueva es otra solicitud (y otra cotización) de Nexo, que empieza sin conexos; no se agregan en el momento: el
    /// envío diferido de la decisión 10 (<see cref="IClsEnvioConexosNexoService"/>) los manda UNA vez desde el polling, cuando Nexo costea
    /// la línea del primer camión vigente de la ruta, con la protección contra duplicados del POST incierto;</item>
    /// <item>la cotización anterior de Nexo queda como estaba (con los conexos que se le agregaron): no se puede borrar nada en ella, y la
    /// nota de la captura nueva dice que la reemplaza.</item>
    /// </list>
    /// Devuelve los nombres que se conservan, sin repetir y en orden, sin montos (<c>conexos_conservados</c> y el resumen de la propuesta
    /// del asistente); un conexo migrado sin nombre se muestra con su id de Nexo. <c>conexos_descartados</c> queda vacío (compatibilidad).
    /// </summary>
    public static IReadOnlyList<string> ConexosConservados(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return [.. cotizacion.RutasOrdenadas
            .SelectMany(r => r.Conexos.OrderBy(c => c.CreatedAt).ThenBy(c => c.IdServicioConexo, StringComparer.Ordinal))
            .Select(c => (string.IsNullOrWhiteSpace(c.Nombre) ? c.IdServicioConexo : c.Nombre).Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<clsErrorDominio?> ValidarAsync(
        Guid usuarioId, clsCotizacionSolicitud cotizacion, clsEntradaReSolicitudCosteo entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(entrada);
        if (ValidarEntrada(entrada) is { } invalida)
        {
            return invalida;
        }

        var validada = await ValidarReglasAsync(usuarioId, cotizacion, entrada, cancellationToken);
        return validada.EsFallo ? validada.Error : null;
    }

    /// <summary>
    /// Única fuente de las reglas de la re-solicitud (todo lo que se valida antes de escribir, salvo la entrada y el <c>If-Match</c>):
    /// estado y regla del PDF, líneas guardadas, política activa y, en la versión nueva con cambios de carga, que sea de una línea, el
    /// catálogo (G2) y las reglas de la captura sobre la combinación; en el mismo folio, las reglas sobre lo guardado.
    /// </summary>
    private async Task<clsResultado<clsReSolicitudValidada>> ValidarReglasAsync(
        Guid usuarioId, clsCotizacionSolicitud cotizacion, clsEntradaReSolicitudCosteo entrada, CancellationToken cancellationToken)
    {
        if (cotizacion.ValidarReSolicitud(entrada.VersionNueva) is { } noPermitida)
        {
            return noPermitida.Codigo == clsCodigosError.PdfYaEnviado
                ? clsReglaPdfCosteo.ConVersionNueva(noPermitida, cotizacion.Folio, MotivoVersion)
                : noPermitida;
        }

        if (cotizacion.Rutas.Count == 0 || cotizacion.EncabezadoSolicitud is not { } encabezado)
        {
            return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"La cotización {cotizacion.Folio} no tiene guardadas las líneas de la solicitud: no se puede volver a enviar a Nexo.");
        }

        // HU-021: cada ruta tiene su línea de negocio (y su margen); ninguna puede estar inactiva. La de la ruta 1 es el respaldo del envío.
        clsMargenPolitica? politica = null;
        foreach (var lineaId in cotizacion.RutasOrdenadas.Select(r => r.LineaNegocioId).OfType<int>().Distinct())
        {
            var deRuta = await politicas.ObtenerPorIdAsync(lineaId, cancellationToken);
            if (deRuta is { Activa: false })
            {
                return clsMargenResolverService.LineaInactiva(deRuta.LineaNegocio);
            }

            politica ??= deRuta;
        }

        var lineas = clsEnvioCosteoService.LineasGuardadas(cotizacion);
        var replanificar = false;
        if (entrada.VersionNueva && entrada.DatosCarga is { } pedidos)
        {
            if (lineas.Count != 1)
            {
                // D9 (#139): datos_carga trae UNA carga, sin ruta: con varias rutas no se sabe a cuál aplica. El mensaje dice lo que es la
                // cotización (licitación o formulario de varias rutas) y el motivo los distingue.
                var esLicitacion = cotizacion.TipoSolicitud == TipoSolicitud.Licitacion;
                return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                    (esLicitacion
                        ? $"La cotización {cotizacion.Folio} es una licitación de {lineas.Count} rutas"
                        : $"La cotización {cotizacion.Folio} tiene {lineas.Count} rutas")
                    + ": los cambios de carga solo aplican a una cotización de una ruta. Crea la versión nueva con nueva_version y sin "
                    + "datos_carga, o cotiza la ruta que cambia en una solicitud nueva.",
                    esLicitacion ? MotivoLicitacion : MotivoVariasRutas);
            }

            var cambios = clsEnvioCosteoService.SinVehiculo(pedidos, logger);
            var anterior = lineas[0];
            encabezado = cambios.Aplicar(encabezado);
            lineas = [cambios.Aplicar(anterior)];
            replanificar = CambiaLaCarga(anterior, lineas[0]);

            // G2 (#106): los cambios de carga solo traen códigos de catálogo, igual que la captura (nunca texto libre a Nexo).
            if (await catalogos.ValidarAsync(usuarioId, lineas[0], cancellationToken) is { } noCatalogo)
            {
                return noCatalogo;
            }
        }

        // Lo guardado (o la combinación con los cambios) se valida con las reglas de la captura: nunca se envía algo que Nexo rechazaría.
        // Una licitación sin cambios de carga pasa por todas las reglas, también catálogos y municipios (HU-013.11).
        if (!(entrada.VersionNueva && entrada.DatosCarga is not null) && cotizacion.TipoSolicitud == TipoSolicitud.Licitacion)
        {
            var revalidada = await filasLicitacion.RevalidarGuardadasAsync(usuarioId, cotizacion.Folio, encabezado, lineas, Filas(cotizacion), cancellationToken);
            if (revalidada.EsFallo)
            {
                return revalidada.Error;
            }

            lineas = revalidada.Valor;
        }
        else if (clsEnvioCosteoService.Revalidar(encabezado, lineas) is { } reglas)
        {
            return reglas;
        }

        return new clsReSolicitudValidada(encabezado, lineas, politica, replanificar);
    }

    /// <summary>
    /// ¿Los cambios de carga cambian el peso o las mercancías de la ruta? Entonces sus camiones ya no sirven: se borran y se vuelven a
    /// planificar (HU-021.9). Un campo que viene con el mismo valor no cuenta como cambio.
    /// </summary>
    public static bool CambiaLaCarga(clsLineaSolicitudCosteo anterior, clsLineaSolicitudCosteo nueva)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        ArgumentNullException.ThrowIfNull(nueva);
        return anterior.PesoKg != nueva.PesoKg || !anterior.Posiciones.SequenceEqual(nueva.Posiciones);
    }

    /// <summary>
    /// Lo que dejó listo la validación: el encabezado y las líneas a enviar (las rutas guardadas, con los cambios de carga), la política
    /// de la ruta 1 y si los cambios de carga obligan a volver a planificar los camiones.
    /// </summary>
    private sealed record clsReSolicitudValidada(
        clsEncabezadoSolicitudCosteo Encabezado,
        IReadOnlyList<clsLineaSolicitudCosteo> Lineas,
        clsMargenPolitica? Politica,
        bool Replanificar);

    /// <summary>Mismo folio: reserva (con el costeo anterior olvidado y su instantánea auditada) y vuelve a capturar en Nexo.</summary>
    private async Task<(clsResultado<clsReSolicitudCosteo>, clsCotizacionSolicitud?, bool)> ReenviarAsync(
        clsCotizacionSolicitud cotizacion,
        Guid usuarioId,
        int versionEsperada,
        IReadOnlyList<clsLineaSolicitudCosteo> lineasValidadas,
        clsMargenPolitica? politica,
        clsEventoHerramienta? reserva,
        string nota,
        CancellationToken cancellationToken)
    {
        // Lo guardado ya se revalidó en ValidarReglasAsync (filas anteriores a una regla nueva).
        if (cotizacion.TipoSolicitud == TipoSolicitud.Licitacion)
        {
            clsEnvioCosteoService.CompletarMunicipios(cotizacion.RutasOrdenadas, lineasValidadas);
        }

        // HU-021: la solicitud y cada ruta olvidan el costeo anterior (camiones en pendiente_envio, sin costos de Nexo ni precio). Los
        // conexos y el margen ajustado de cada ruta se conservan (HU-021.9: los conexos son de Cotifly, decisión 3; el margen negociado,
        // decisión del 2026-10-06): el precio vuelve con el costeo nuevo.
        cotizacion.PrepararReSolicitud();

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ReSolicitud, usuarioId, cotizaciones);
        if (!await ReservarAsync(cotizacion, versionEsperada, reserva, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), null, false);
        }

        // Reservada y confirmada: desde aquí, sin el token de la petición (como el alta, ADR-015 §3).
        await envio.EnviarGuardadaAsync(cotizacion, politica, usuarioId, nota);
        return (await ResultadoAsync(cotizacion, versionNueva: false), cotizacion, pdfInvalidado);
    }

    /// <summary>
    /// Versión nueva: valida los cambios de carga (catálogo y reglas de la captura), reserva la versión de la original (de varias
    /// peticiones simultáneas con el mismo <c>If-Match</c> solo una sigue), crea la versión <c>-vN</c> y la envía.
    /// </summary>
    private async Task<(clsResultado<clsReSolicitudCosteo>, clsCotizacionSolicitud?, bool)> CrearVersionAsync(
        clsCotizacionSolicitud original,
        Guid usuarioId,
        clsReSolicitudValidada validada,
        int versionEsperada,
        clsEventoHerramienta? reserva,
        string nota,
        CancellationToken cancellationToken)
    {
        // Encabezado y líneas ya validados (con los cambios de carga aplicados) por ValidarReglasAsync.
        // La versión nueva es una cotización nueva (decisión del 2026-10-06): misma moneda, TRM del día. Sin TRM no se crea nada (ni
        // la reserva de la original, ni la versión, ni la captura en Nexo), igual que el formulario y las licitaciones.
        var moneda = await monedas.FijarAsync(original.Moneda, cancellationToken);
        if (moneda.EsFallo)
        {
            return (moneda.Error, null, false);
        }

        // HU-021.9: las rutas de la versión (con sus camiones copiados o vueltos a planificar) se arman ANTES de reservar: si el
        // planificador (el cubicador, #133) falla, no se reserva ni se crea nada.
        var rutas = await RutasDeLaVersionAsync(original, usuarioId, validada, cancellationToken);
        if (rutas.EsFallo)
        {
            return (rutas.Error, null, false);
        }

        if (!await ReservarAsync(original, versionEsperada, reserva, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(original.Folio), null, false);
        }

        var creada = await folios.CrearNuevaVersionAsync<clsCotizacionSolicitud>(usuarioId, original.Id, async (folio, ct) =>
        {
            var version = original.CrearVersion(folio, moneda.Valor.TrmAplicada);
            if (version.EsFallo)
            {
                return version.Error;
            }

            // HU-021.9: la versión nueva copia cada ruta (con los cambios de carga) con su línea de negocio, sus mercancías, sus camiones
            // y sus conexos; el costeo empieza de cero y el margen negociado de cada ruta se conserva. Las reglas de la captura nueva no
            // se exigen: lo guardado ya se revalidó y las rutas migradas no tienen los ids que pide la captura nueva.
            var nueva = version.Valor;
            nueva.RegistrarCaptura(validada.Encabezado, rutas.Valor.Rutas, reglasCaptura: false);
            nueva.CopiarMargenesNegociadosDe(original);
            cotizaciones.Agregar(nueva);
            await unidadTrabajo.GuardarCambiosAsync(ct);
            return nueva;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return (creada.Error, null, false);
        }

        // Confirmada: desde aquí, sin el token de la petición (como el alta, ADR-015 §3).
        await envio.EnviarGuardadaAsync(creada.Valor, validada.Politica, usuarioId, nota);
        var resultado = await ResultadoAsync(creada.Valor, versionNueva: true);
        return (resultado.Valor with { RutasReplanificadas = rutas.Valor.Replanificadas }, creada.Valor, false);
    }

    /// <summary>Las rutas de la versión nueva y el orden de las que volvieron a planificar sus camiones.</summary>
    private sealed record clsRutasVersion(IReadOnlyList<clsRutaCapturada> Rutas, IReadOnlyList<int> Replanificadas);

    /// <summary>
    /// Las rutas de la versión nueva (HU-021.9), en el orden de la original: cada una con su línea validada (sin vehículo: es del camión),
    /// su línea de negocio, su fila del pliego, sus conexos (mismo costo unitario de Nexo y cantidad, decisión 3) y sus camiones:
    /// <list type="bullet">
    /// <item>si los cambios de carga cambian el peso o las mercancías (o la ruta no tiene camiones), se vuelven a planificar con
    /// <see cref="IClsPlanificadorCamiones"/>: los anteriores ya no sirven;</item>
    /// <item>si no, se copian los vigentes (los <c>descartada</c> en Nexo no, salvo que lo estén todos), con su vehículo, carrocería, peso y
    /// recomendación; el levante se copia de la ruta (decisión 4).</item>
    /// </list>
    /// </summary>
    private async Task<clsResultado<clsRutasVersion>> RutasDeLaVersionAsync(
        clsCotizacionSolicitud original, Guid usuarioId, clsReSolicitudValidada validada, CancellationToken cancellationToken)
    {
        var originales = original.RutasOrdenadas;
        if (originales.Count != validada.Lineas.Count)
        {
            throw new InvalidOperationException($"La cotización {original.Folio} tiene {originales.Count} rutas y se validaron {validada.Lineas.Count}.");
        }

        // HU-021.9: la -vN es una cotización nueva: cada conexo se copia con el id y el costo unitario del CATÁLOGO de Nexo de hoy, igual
        // que en la captura. Un conexo agregado con el POST o importado desde Nexo está guardado con el id de la fila de la cotización
        // anterior de Nexo: se resuelve por id o por nombre (clsResolutorConexosCatalogo). Sin catálogo no se crea nada (503, antes de
        // reservar). Uno que no se puede resolver conserva lo guardado (con un aviso en el log) y el envío diferido lo deja
        // fallido_definitivo, visible en el costeo.
        var conexosOriginales = originales.SelectMany(r => r.Conexos).Select(c => (c.IdServicioConexo, c.Nombre)).ToList();
        IReadOnlyDictionary<string, clsCostoConexoNexo> catalogo = new Dictionary<string, clsCostoConexoNexo>();
        if (conexosOriginales.Count > 0)
        {
            var resueltos = await resolutorConexos.ResolverAsync(usuarioId, conexosOriginales, cancellationToken);
            if (resueltos.SinCatalogo.Count > 0 && resueltos.ErrorCatalogo is { } sinCatalogo)
            {
                return sinCatalogo;
            }

            catalogo = resueltos.Resueltos;
        }

        var rutas = new List<clsRutaCapturada>(originales.Count);
        var replanificadas = new List<int>();
        for (var i = 0; i < originales.Count; i++)
        {
            var ruta = originales[i];
            var linea = validada.Lineas[i] with { TipoVehiculo = null, Carroceria = null };
            // En el orden estable de la ruta (createdAt con el desfase de OrdenCreacion, #133): el primero lleva los conexos (decisión 3).
            var vigentes = ruta.VehiculosQueCuentan.OrderBy(v => v.CreatedAt).ThenBy(v => v.OrdenCreacion).ThenBy(v => v.Id).ToList();
            // La ruta de la versión nueva es otra: un id nuevo, el mismo para el cubicador (idLineaSolicitud, #133) y para la ruta guardada.
            var rutaId = Guid.NewGuid();
            IReadOnlyList<clsCamionPlaneado> camiones;
            clsCubicacionRuta? cubicacion = null;
            if ((validada.Replanificar || vigentes.Count == 0) && linea.Posiciones.Any(p => string.IsNullOrWhiteSpace(p.CodigoMercancia)))
            {
                // Ruta con mercancías sin código del catálogo del cubicador (anteriores al script 02): el cubicador no las puede cubicar.
                // Respaldo: un camión sin vehículo con el peso de la ruta (lo resuelve la Mesa de Costeo de Nexo) y un aviso. Para pedir
                // los camiones al cubicador, el comercial vuelve a escoger las mercancías (HU-023.3).
                logger.LogWarning(
                    "La ruta {Ruta} de {Folio} tiene mercancías sin codigo_mercancia: la versión nueva lleva un camión sin vehículo (respaldo) en vez de planificarlos.",
                    ruta.Orden, original.Folio);
                camiones = [new clsCamionPlaneado(null, null, clsPlanificadorCamionSinVehiculo.PesoTotal(linea))];
                replanificadas.Add(ruta.Orden);
            }
            else if (validada.Replanificar || vigentes.Count == 0)
            {
                var plan = await planificador.PlanificarAsync(usuarioId, rutaId, linea, cancellationToken);
                if (plan.EsFallo)
                {
                    return plan.Error;
                }

                if (plan.Valor.Camiones.Count == 0)
                {
                    return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                        $"No se pudieron calcular los vehículos de la ruta {ruta.Orden} de la cotización {original.Folio}.");
                }

                camiones = plan.Valor.Camiones;
                cubicacion = plan.Valor.Cubicacion;
                replanificadas.Add(ruta.Orden);
            }
            else
            {
                // Se copian los camiones con su recomendación, su volumen y sus porcentajes, y el resultado del cubicador de la ruta.
                camiones = [.. vigentes.Select(v => new clsCamionPlaneado(
                    v.TipoVehiculo, v.Carroceria, v.PesoKg, v.IdRecomendacionVehiculo, v.VolumenTotalM3, v.PorcentajeLlenadoVolumen, v.PorcentajeLlenadoPeso))];
                cubicacion = ruta.ResultadoCubicacion is { } resultado
                    ? new clsCubicacionRuta(ruta.IdRecomendacion, resultado, ruta.MotivoSinSeleccion, ruta.Condicionada)
                    : null;
            }

            var conexos = new List<clsCambioConexo>();
            foreach (var c in ruta.Conexos.OrderBy(x => x.CreatedAt).ThenBy(x => x.IdServicioConexo, StringComparer.Ordinal))
            {
                if (catalogo.TryGetValue(c.IdServicioConexo, out var costo) && costo.CostoUnitario >= 0m)
                {
                    // Con el id del catálogo; dos filas del mismo servicio (capturada y agregada en Nexo) se suman en una, con su cantidad.
                    var previo = conexos.FindIndex(x => string.Equals(x.IdServicioConexo, costo.Id, StringComparison.OrdinalIgnoreCase));
                    if (previo >= 0)
                    {
                        conexos[previo] = conexos[previo] with { Cantidad = conexos[previo].Cantidad + c.Cantidad };
                        continue;
                    }

                    conexos.Add(new clsCambioConexo(costo.Id, costo.Nombre ?? c.Nombre, costo.CostoUnitario, c.Cantidad));
                    continue;
                }

                logger.LogWarning("El conexo {Conexo} de la ruta {Ruta} de {Folio} no está en el catálogo de Nexo (ni por id ni por nombre): la versión nueva conserva lo guardado.",
                    c.IdServicioConexo, ruta.Orden, original.Folio);
                conexos.Add(new clsCambioConexo(c.IdServicioConexo, c.Nombre, c.Precio, c.Cantidad));
            }

            rutas.Add(new clsRutaCapturada(linea, ruta.LineaNegocioId ?? validada.Politica?.Id, ruta.RutaOrigenIndice, camiones, conexos, rutaId, cubicacion));
        }

        return new clsRutasVersion(rutas, replanificadas);
    }

    /// <summary>Filas del pliego de las rutas guardadas, en el orden en que viajan a Nexo.</summary>
    private static IReadOnlyList<int?> Filas(clsCotizacionSolicitud cotizacion) =>
        [.. cotizacion.RutasOrdenadas.Select(r => r.RutaOrigenIndice)];

    /// <summary>
    /// Sube la versión de negocio y guarda, con la instantánea de auditoría, en la transacción del control de versión. <c>false</c> si
    /// otra petición cambió la cotización entretanto (nada se escribe, ni siquiera la instantánea).
    /// </summary>
    private async Task<bool> ReservarAsync(clsCotizacionSolicitud cotizacion, int versionEsperada, clsEventoHerramienta? reserva, CancellationToken cancellationToken)
    {
        if (cotizacion.VersionNegocio == versionEsperada)
        {
            cotizacion.IncrementarVersionNegocio();
        }

        if (reserva is not null)
        {
            registroAuditoria.Registrar(reserva);
        }

        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken);
    }

    /// <summary>Evento de la reserva (fase <c>reservada</c>) con la instantánea del costeo anterior; sin <c>ThreadId</c> (#102).</summary>
    private static clsEventoHerramienta? Reserva(object entradaAuditoria, object instantanea, Stopwatch cronometro)
    {
        var evento = clsEventoHerramienta.Registrar(
            HerramientaAuditoria,
            clsAuditoriaService.AJson(entradaAuditoria),
            clsAuditoriaService.AJson(new { fase = "reservada", anterior = instantanea }),
            exito: true,
            (int)cronometro.ElapsedMilliseconds,
            threadId: null);
        return evento.EsExitoso ? evento.Valor : null;
    }

    /// <summary>
    /// Instantánea completa del costeo (lo que se pierde al re-solicitar en el mismo folio): estado, ids de Nexo, precio, moneda y, por
    /// ruta (HU-021), su margen (aplicado, sugeridos, origen y método), sus totales de costo, su precio, sus conexos y cada camión (id de
    /// Nexo, estado y costos); PDF y versión.
    /// </summary>
    public static object Instantanea(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return new
        {
            estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            motivo_estado = cotizacion.MotivoEstadoCosteo,
            costeo_id = cotizacion.SolicitudNexoId,
            cotizacion_nexo_id = cotizacion.CotizacionNexoId,
            costo = cotizacion.CostoFinal,
            precio_final = cotizacion.PrecioFinal,
            moneda = cotizacion.Moneda,
            trm_aplicada = cotizacion.TrmAplicada,
            costeo_listo_at = cotizacion.CosteoListoAt,
            rutas = cotizacion.RutasOrdenadas.Select(r => new
            {
                orden = r.Orden,
                estado_costeo = r.EstadoCosteo is { } estadoRuta ? clsCodigoEnum.ACodigo(estadoRuta) : null,
                margen_aplicado = r.MargenAplicado,
                margen_politica_sugerido = r.MargenPoliticaSugerido,
                margen_sap_sugerido = r.MargenSAPSugerido,
                origen_margen = r.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
                metodo_calculo = r.MetodoCalculo,
                costo_fletes_total = r.CostoFletesTotal,
                costo_retorno_contenedor_total = r.CostoRetornoContenedorTotal,
                costo_levante_contenedor_total = r.CostoLevanteContenedorTotal,
                costo_servicios_conexos = r.CostoServiciosConexos,
                costo_final = r.CostoFinal,
                precio_final_linea = r.PrecioFinalLinea,
                servicios_conexos = r.Conexos.Select(c => new { id = c.IdServicioConexo, nombre = c.Nombre, precio = c.Precio, cantidad = c.Cantidad }).ToList(),
                camiones = r.VehiculosOrdenados.Select(v => new
                {
                    linea_solicitud_nexo_id = v.LineaSolicitudNexoId,
                    estado_costeo = v.EstadoCosteo is { } estadoCamion ? clsCodigoEnum.ACodigo(estadoCamion) : null,
                    costo_flete = v.CostoFlete,
                    costo_retorno_contenedor = v.CostoRetornoContenedor,
                    costo_levante_contenedor = v.CostoLevanteContenedor,
                }).ToList(),
            }).ToList(),
            pdf = cotizacion.PdfPath,
            pdf_desactualizado = cotizacion.PdfDesactualizado,
            version_negocio = cotizacion.VersionNegocio,
        };
    }

    private async Task<clsResultado<clsReSolicitudCosteo>> ResultadoAsync(clsCotizacionSolicitud cotizacion, bool versionNueva) =>
        new clsReSolicitudCosteo(
            versionNueva,
            await misCotizaciones.ResumirAsync(cotizacion, CancellationToken.None),
            cotizacion.VersionNegocio.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Motivo (1 a 300 caracteres) y <c>datos_carga</c> no vacío (<c>SOLICITUD_COSTEO_INVALIDA</c>). Pública: la usa también la propuesta
    /// <c>re_solicitar_costeo</c> del asistente (HU-013.6) antes de armar el cuerpo.
    /// </summary>
    public static clsErrorDominio? ValidarEntrada(clsEntradaReSolicitudCosteo entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        var motivo = Motivo(entrada.Motivo);
        if (motivo is null)
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "El campo 'motivo' es obligatorio: escribe por qué vuelves a pedir el costeo.");
        }

        if (motivo.Length > clsEntradaReSolicitudCosteo.LargoMaximoMotivo)
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                $"El campo 'motivo' admite hasta {clsEntradaReSolicitudCosteo.LargoMaximoMotivo} caracteres.");
        }

        return entrada.DatosCarga is { TieneCambios: false }
            ? new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                "El campo 'datos_carga' está vacío: envía solo los campos que cambian, u omítelo para re-solicitar sin cambios de carga.")
            : null;
    }

    private static string? Motivo(string? motivo) => string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim();

    /// <summary>Huella de la petición para la idempotencia: folio, motivo, <c>nueva_version</c> y cambios de carga (SHA-256).</summary>
    public static string Huella(string folio, clsEntradaReSolicitudCosteo entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        var cuerpo = JsonSerializer.Serialize(new
        {
            folio = folio?.Trim(),
            motivo = Motivo(entrada.Motivo),
            entrada.NuevaVersion,
            datos = entrada.DatosCarga is { } datos ? datos with { Presentes = new SortedSet<string>(datos.Presentes, StringComparer.Ordinal) } : null,
        }, _jsonHuella);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cuerpo)));
    }
}
