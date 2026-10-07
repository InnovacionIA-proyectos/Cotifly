using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

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
}

/// <summary>
/// Implementación de <see cref="IClsReSolicitudCosteoService"/>. Nexo no tiene una operación para reabrir ni para re-solicitar una
/// solicitud (<c>docs/HU-013-preguntas-nexo.md</c>, pregunta 4): re-solicitar es una <b>captura nueva</b>
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
    IClsAuditoriaService auditoria) : IClsReSolicitudCosteoService
{
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
                    costeo_id = resultante.CosteoId,
                    motivo_estado = resultante.MotivoEstadoCosteo,
                    pdf_invalidado = pdfInvalidado,
                }
                : new { fase = "rechazada", error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            threadId: null);
        return resultado;
    }

    private async Task<(clsResultado<clsReSolicitudCosteo> Resultado, clsCotizacion? Resultante, bool PdfInvalidado)> ReSolicitarAsync(
        clsCotizacion cotizacion,
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

        if (cotizacion.ValidarReSolicitud(entrada.VersionNueva) is { } noPermitida)
        {
            return (noPermitida.Codigo == clsCodigosError.PdfYaEnviado
                ? clsReglaPdfCosteo.ConVersionNueva(noPermitida, cotizacion.Folio, MotivoVersion)
                : noPermitida, null, false);
        }

        var guardadas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var posiciones = await cotizaciones.ListarPosicionesAsync(cotizacion.Id, cancellationToken);
        if (guardadas.Count == 0 || cotizacion.EncabezadoSolicitud is not { } encabezado)
        {
            return (new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"La cotización {cotizacion.Folio} no tiene guardadas las líneas de la solicitud: no se puede volver a enviar a Nexo."), null, false);
        }

        var politica = cotizacion.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, cancellationToken) : null;
        if (politica is { Activa: false })
        {
            return (clsMargenResolverService.LineaInactiva(politica.LineaNegocio), null, false);
        }

        // Instantánea completa del costeo anterior: se toma antes de cualquier cambio y se guarda con la reserva.
        var instantanea = Instantanea(cotizacion, guardadas);
        var nota = NotaTrazabilidad(cotizacion.Folio, cotizacion.CosteoId);
        return entrada.VersionNueva
            ? await CrearVersionAsync(cotizacion, usuarioId, entrada.DatosCarga, encabezado, guardadas, posiciones, politica, versionEsperada,
                Reserva(entradaAuditoria, instantanea, cronometro), nota, cancellationToken)
            : await ReenviarAsync(cotizacion, usuarioId, versionEsperada, guardadas, posiciones, politica,
                Reserva(entradaAuditoria, instantanea, cronometro), nota, cancellationToken);
    }

    /// <summary>Mismo folio: reserva (con el costeo anterior olvidado y su instantánea auditada) y vuelve a capturar en Nexo.</summary>
    private async Task<(clsResultado<clsReSolicitudCosteo>, clsCotizacion?, bool)> ReenviarAsync(
        clsCotizacion cotizacion,
        Guid usuarioId,
        int versionEsperada,
        IReadOnlyList<clsCotizacionLineaSolicitud> guardadas,
        IReadOnlyList<clsCotizacionPosicion> posiciones,
        clsMargenPolitica? politica,
        clsEventoHerramienta? reserva,
        string nota,
        CancellationToken cancellationToken)
    {
        // Lo guardado se vuelve a validar (filas anteriores a una regla nueva): nunca se reenvía algo que Nexo rechazaría. Una licitación
        // pasa por todas las reglas de captura, también catálogos y municipios (HU-013.11).
        if (cotizacion.TipoSolicitud == TipoSolicitud.Licitacion)
        {
            var revalidada = await filasLicitacion.RevalidarGuardadasAsync(usuarioId, cotizacion.Folio, cotizacion.EncabezadoSolicitud!,
                clsEnvioCosteoService.LineasConPosiciones(guardadas, posiciones), Filas(guardadas), cancellationToken);
            if (revalidada.EsFallo)
            {
                return (revalidada.Error, null, false);
            }

            clsEnvioCosteoService.CompletarMunicipios(guardadas, revalidada.Valor);
        }
        else if (clsEnvioCosteoService.Revalidar(cotizacion.EncabezadoSolicitud!, clsEnvioCosteoService.LineasConPosiciones(guardadas, posiciones)) is { } invalida)
        {
            return (invalida, null, false);
        }

        cotizacion.PrepararReSolicitud();
        foreach (var linea in guardadas)
        {
            linea.ReiniciarCosteo();
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ReSolicitud, usuarioId, cotizaciones);
        if (!await ReservarAsync(cotizacion, versionEsperada, reserva, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), null, false);
        }

        // Reservada y confirmada: desde aquí, sin el token de la petición (como el alta, ADR-015 §3).
        await envio.EnviarGuardadaAsync(cotizacion, guardadas, posiciones, politica, usuarioId, nota);
        return (await ResultadoAsync(cotizacion, versionNueva: false), cotizacion, pdfInvalidado);
    }

    /// <summary>
    /// Versión nueva: valida los cambios de carga (catálogo y reglas de la captura), reserva la versión de la original (de varias
    /// peticiones simultáneas con el mismo <c>If-Match</c> solo una sigue), crea la versión <c>-vN</c> y la envía.
    /// </summary>
    private async Task<(clsResultado<clsReSolicitudCosteo>, clsCotizacion?, bool)> CrearVersionAsync(
        clsCotizacion original,
        Guid usuarioId,
        clsCambiosCarga? cambios,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsCotizacionLineaSolicitud> guardadas,
        IReadOnlyList<clsCotizacionPosicion> posiciones,
        clsMargenPolitica? politica,
        int versionEsperada,
        clsEventoHerramienta? reserva,
        string nota,
        CancellationToken cancellationToken)
    {
        var lineas = clsEnvioCosteoService.LineasConPosiciones(guardadas, posiciones);
        if (cambios is not null)
        {
            if (lineas.Count != 1)
            {
                return (new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                    $"La cotización {original.Folio} es una licitación de {lineas.Count} líneas: los cambios de carga solo aplican a una "
                    + "cotización de una línea (crea la versión con nueva_version y sin datos_carga).", "LICITACION"), null, false);
            }

            encabezado = cambios.Aplicar(encabezado);
            lineas = [cambios.Aplicar(lineas[0])];

            // G2 (#106): los cambios de carga solo traen códigos de catálogo, igual que la captura (nunca texto libre a Nexo).
            if (await catalogos.ValidarAsync(usuarioId, lineas[0], cancellationToken) is { } noCatalogo)
            {
                return (noCatalogo, null, false);
            }
        }

        if (cambios is null && original.TipoSolicitud == TipoSolicitud.Licitacion)
        {
            // HU-013.11: la -vN copia las filas guardadas; si ya no cumplen las reglas de captura, tampoco la versión se puede enviar.
            var revalidada = await filasLicitacion.RevalidarGuardadasAsync(usuarioId, original.Folio, encabezado, lineas, Filas(guardadas), cancellationToken);
            if (revalidada.EsFallo)
            {
                return (revalidada.Error, null, false);
            }

            lineas = revalidada.Valor;
        }
        else if (clsEnvioCosteoService.Revalidar(encabezado, lineas) is { } invalida)
        {
            return (invalida, null, false);
        }

        // La versión nueva es una cotización nueva (decisión del 2026-10-06): misma moneda, TRM del día. Sin TRM no se crea nada (ni
        // la reserva de la original, ni la versión, ni la captura en Nexo), igual que el formulario y las licitaciones.
        var moneda = await monedas.FijarAsync(original.Moneda, cancellationToken);
        if (moneda.EsFallo)
        {
            return (moneda.Error, null, false);
        }

        if (!await ReservarAsync(original, versionEsperada, reserva, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(original.Folio), null, false);
        }

        IReadOnlyList<clsCotizacionLineaSolicitud> nuevasLineas = [];
        List<clsCotizacionPosicion> nuevasPosiciones = [];
        var creada = await folios.CrearNuevaVersionAsync<clsCotizacion>(usuarioId, original.Id, async (folio, ct) =>
        {
            var version = original.CrearVersion(folio, moneda.Valor.TrmAplicada);
            if (version.EsFallo)
            {
                return version.Error;
            }

            var nueva = version.Valor;
            if (lineas.Count == 1)
            {
                nueva.RegistrarSolicitudCosteo(clsSolicitudCosteo.Desde(encabezado, lineas[0]));
            }
            else
            {
                nueva.RegistrarSolicitudLicitacion(encabezado);
            }

            nuevasLineas = clsCotizacionLineaSolicitud.Desde(nueva.Id, lineas);
            nuevasPosiciones = [];
            var orden = 1;
            for (var i = 0; i < nuevasLineas.Count; i++)
            {
                var deLinea = clsCotizacionPosicion.Desde(nueva.Id, lineas[i].Posiciones, nuevasLineas[i].Id, orden);
                nuevasPosiciones.AddRange(deLinea);
                orden += deLinea.Count;
            }

            cotizaciones.Agregar(nueva);
            cotizaciones.AgregarLineas(nuevasLineas);
            cotizaciones.AgregarPosiciones(nuevasPosiciones);
            await unidadTrabajo.GuardarCambiosAsync(ct);
            return nueva;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return (creada.Error, null, false);
        }

        // Confirmada: desde aquí, sin el token de la petición (como el alta, ADR-015 §3).
        await envio.EnviarGuardadaAsync(creada.Valor, nuevasLineas, nuevasPosiciones, politica, usuarioId, nota);
        return (await ResultadoAsync(creada.Valor, versionNueva: true), creada.Valor, false);
    }

    /// <summary>Filas del pliego de las líneas guardadas, en el orden en que viajan a Nexo.</summary>
    private static IReadOnlyList<int?> Filas(IReadOnlyList<clsCotizacionLineaSolicitud> guardadas) =>
        [.. guardadas.OrderBy(l => l.Orden).Select(l => l.FilaOrigen)];

    /// <summary>
    /// Sube la versión de negocio y guarda, con la instantánea de auditoría, en la transacción del control de versión. <c>false</c> si
    /// otra petición cambió la cotización entretanto (nada se escribe, ni siquiera la instantánea).
    /// </summary>
    private async Task<bool> ReservarAsync(clsCotizacion cotizacion, int versionEsperada, clsEventoHerramienta? reserva, CancellationToken cancellationToken)
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
    /// Instantánea completa del costeo (lo que se pierde al re-solicitar en el mismo folio): estado, ids de Nexo, costo, precio, margen
    /// (aplicado, sugerido, política, origen y método), conexos, cada línea (id de Nexo, estado, costo y precio), PDF y versión.
    /// </summary>
    public static object Instantanea(clsCotizacion cotizacion, IReadOnlyList<clsCotizacionLineaSolicitud> lineas)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        return new
        {
            estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            motivo_estado = cotizacion.MotivoEstadoCosteo,
            costeo_id = cotizacion.CosteoId,
            cotizacion_nexo_id = cotizacion.CotizacionNexoId,
            costo = cotizacion.Costo,
            precio_final = cotizacion.PrecioFinal,
            moneda = cotizacion.Moneda,
            trm_aplicada = cotizacion.TrmAplicada,
            margen_aplicado = cotizacion.MargenAplicado,
            margen_sugerido = cotizacion.MargenSugerido,
            margen_politica = cotizacion.MargenPolitica,
            origen_margen = cotizacion.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            origen_margen_sugerido = cotizacion.OrigenMargenSugerido is { } origenSugerido ? clsCodigoEnum.ACodigo(origenSugerido) : null,
            metodo_calculo = cotizacion.MetodoCalculo,
            servicios_conexos = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos)
                .Select(c => new { c.Id, c.Nombre, c.Categoria, c.Cantidad, c.Costo, c.Precio, c.Incluido }).ToList(),
            costeo_listo_at = cotizacion.CosteoListoAt,
            lineas = lineas.OrderBy(l => l.Orden).Select(l => new
            {
                orden = l.Orden,
                linea_solicitud_nexo_id = l.LineaSolicitudNexoId,
                estado_costeo = l.EstadoCosteo is { } estadoLinea ? clsCodigoEnum.ACodigo(estadoLinea) : null,
                costo = l.Costo,
                precio_final = l.PrecioFinal,
            }).ToList(),
            pdf = cotizacion.PdfPath,
            pdf_desactualizado = cotizacion.PdfDesactualizado,
            version_negocio = cotizacion.VersionNegocio,
        };
    }

    private async Task<clsResultado<clsReSolicitudCosteo>> ResultadoAsync(clsCotizacion cotizacion, bool versionNueva) =>
        new clsReSolicitudCosteo(
            versionNueva,
            await misCotizaciones.ResumirAsync(cotizacion, CancellationToken.None),
            cotizacion.VersionNegocio.ToString(CultureInfo.InvariantCulture));

    /// <summary>Motivo obligatorio (1 a 300 caracteres) y, si vienen, cambios de carga con al menos un campo.</summary>
    private static clsErrorDominio? ValidarEntrada(clsEntradaReSolicitudCosteo entrada)
    {
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
