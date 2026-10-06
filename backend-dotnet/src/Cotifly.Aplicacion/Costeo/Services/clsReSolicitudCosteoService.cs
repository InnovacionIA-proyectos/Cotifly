using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
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
/// mismo envío del alta y de las licitaciones).
/// <list type="bullet">
/// <item><b>Mismo folio</b>: valida el <c>If-Match</c>, el estado y la regla del PDF (<see cref="clsCotizacion.ValidarReSolicitud"/>);
/// olvida el costeo anterior (que queda en la auditoría), invalida el PDF, sube la versión de negocio y lo confirma con el control de
/// versión (el reclamo: de dos peticiones simultáneas solo una envía); después envía a Nexo, guarda el id de la solicitud nueva y el
/// polling se reanuda.</item>
/// <item><b>Versión nueva</b>: crea <c>{folio base}-vN</c> con <see cref="IClsGeneradorFolioService.CrearNuevaVersionAsync{TValor}"/>
/// copiando el formulario, las líneas y las posiciones (con los cambios de carga validados como en la captura) y la envía como
/// solicitud nueva. La original no se toca.</item>
/// <item><c>Idempotency-Key</c> (<see cref="IClsAlmacenIdempotencia"/>): la repetición devuelve el resultado de la primera.</item>
/// <item>Cada intento queda en <c>EventosHerramienta</c> (<see cref="HerramientaAuditoria"/>) con el motivo y el comercial.</item>
/// </list>
/// </summary>
public sealed class clsReSolicitudCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsGeneradorFolioService folios,
    IClsEnvioCosteoService envio,
    IClsMisCotizacionesService misCotizaciones,
    IClsAlmacenIdempotencia idempotencia,
    IClsUnitOfWork unidadTrabajo,
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
                $"{OperacionIdempotencia}:{usuarioId:N}",
                claveIdempotencia,
                Huella(folio, entrada),
                ct => EjecutarAsync(usuarioId, folio, entrada, versionEsperada, ct),
                cancellationToken);
    }

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

        var anterior = new
        {
            estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
            costeo_id = cotizacion.CosteoId,
            cotizacion_nexo_id = cotizacion.CotizacionNexoId,
            costo = cotizacion.Costo,
            precio_final = cotizacion.PrecioFinal,
            pdf = cotizacion.PdfPath,
        };
        var (resultado, resultante, pdfInvalidado) = await ReSolicitarAsync(cotizacion, usuarioId, entrada, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new
            {
                folio = cotizacion.Folio,
                usuario_id = usuarioId,
                motivo = Motivo(entrada.Motivo),
                version_nueva = entrada.VersionNueva,
                datos_carga = entrada.DatosCarga?.Presentes.Order(StringComparer.Ordinal).ToArray(),
            },
            resultado.EsExitoso && resultante is not null
                ? new
                {
                    folio_resultante = resultante.Folio,
                    version = resultante.Version,
                    estado_costeo = resultante.EstadoCosteo is { } estadoNuevo ? clsCodigoEnum.ACodigo(estadoNuevo) : null,
                    costeo_id = resultante.CosteoId,
                    motivo_estado = resultante.MotivoEstadoCosteo,
                    pdf_invalidado = pdfInvalidado,
                    anterior,
                }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje }, anterior },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<(clsResultado<clsReSolicitudCosteo> Resultado, clsCotizacion? Resultante, bool PdfInvalidado)> ReSolicitarAsync(
        clsCotizacion cotizacion, Guid usuarioId, clsEntradaReSolicitudCosteo entrada, int versionEsperada, CancellationToken cancellationToken)
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
        return entrada.VersionNueva
            ? await CrearVersionAsync(cotizacion, usuarioId, entrada.DatosCarga, encabezado, guardadas, posiciones, politica, cancellationToken)
            : await ReenviarAsync(cotizacion, usuarioId, versionEsperada, encabezado, guardadas, posiciones, politica, cancellationToken);
    }

    /// <summary>Mismo folio: reclama con el control de versión, olvida el costeo anterior y vuelve a capturar en Nexo.</summary>
    private async Task<(clsResultado<clsReSolicitudCosteo>, clsCotizacion?, bool)> ReenviarAsync(
        clsCotizacion cotizacion,
        Guid usuarioId,
        int versionEsperada,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsCotizacionLineaSolicitud> guardadas,
        IReadOnlyList<clsCotizacionPosicion> posiciones,
        clsMargenPolitica? politica,
        CancellationToken cancellationToken)
    {
        // Lo guardado se vuelve a validar (filas anteriores a una regla nueva): nunca se reenvía algo que Nexo rechazaría.
        if (clsEnvioCosteoService.Revalidar(encabezado, clsEnvioCosteoService.LineasConPosiciones(guardadas, posiciones)) is { } invalida)
        {
            return (invalida, null, false);
        }

        cotizacion.PrepararReSolicitud();
        foreach (var linea in guardadas)
        {
            linea.ReiniciarCosteo();
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ReSolicitud, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), null, false);
        }

        // Reclamada y confirmada: desde aquí, sin el token de la petición (como el alta, ADR-015 §3).
        await envio.EnviarGuardadaAsync(cotizacion, guardadas, posiciones, politica, usuarioId);
        return (await ResultadoAsync(cotizacion, versionNueva: false), cotizacion, pdfInvalidado);
    }

    /// <summary>Versión nueva: copia la solicitud (con los cambios de carga), la confirma con su folio <c>-vN</c> y la envía.</summary>
    private async Task<(clsResultado<clsReSolicitudCosteo>, clsCotizacion?, bool)> CrearVersionAsync(
        clsCotizacion original,
        Guid usuarioId,
        clsCambiosCarga? cambios,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsCotizacionLineaSolicitud> guardadas,
        IReadOnlyList<clsCotizacionPosicion> posiciones,
        clsMargenPolitica? politica,
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
        }

        if (clsEnvioCosteoService.Revalidar(encabezado, lineas) is { } invalida)
        {
            return (invalida, null, false);
        }

        if (politica is { Activa: false })
        {
            return (clsMargenResolverService.LineaInactiva(politica.LineaNegocio), null, false);
        }

        IReadOnlyList<clsCotizacionLineaSolicitud> nuevasLineas = [];
        List<clsCotizacionPosicion> nuevasPosiciones = [];
        var creada = await folios.CrearNuevaVersionAsync<clsCotizacion>(usuarioId, original.Id, async (folio, ct) =>
        {
            var version = original.CrearVersion(folio);
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
        await envio.EnviarGuardadaAsync(creada.Valor, nuevasLineas, nuevasPosiciones, politica, usuarioId);
        return (await ResultadoAsync(creada.Valor, versionNueva: true), creada.Valor, false);
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
