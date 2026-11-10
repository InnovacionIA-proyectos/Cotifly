using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Aplicacion.Redaccion.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Asunto e introducción del PDF del costeo (HU-013.5, ADR-018 §2a y §11): <c>POST /web/cotizaciones/{folio}/costeo/textos/redactar</c>
/// (la IA, con el texto estándar de respaldo) y <c>PUT .../costeo/textos</c> (el comercial). Solo el dueño
/// (<c>COTIZACION_NO_ENCONTRADA</c> → 404); exigen la versión de la fila (<c>CONFLICTO_CONCURRENCIA</c>), la cotización finalizada
/// (HU-036.3, #207: <c>COTIZACION_NO_FINALIZADA</c>) y siguen la regla del PDF (<c>PDF_YA_ENVIADO</c> si ya se entregó al cliente; si no,
/// invalidan el PDF vigente).
/// </summary>
public interface IClsTextosCosteoService
{
    /// <summary>
    /// Redacta y guarda los textos (<c>fuente = ia</c>, o <c>estandar</c> si la IA no sirvió: nunca falla por la IA). Con textos editados
    /// por el comercial responde <c>TEXTOS_EDITADOS</c> sin llamar a la IA, salvo con <paramref name="sobrescribir"/>.
    /// <paramref name="indicacionesRedaccion"/> (HU-013.18, #296) son las indicaciones opcionales del comercial para orientar a la IA
    /// (recortadas; vacías o solo espacios equivalen a sin indicaciones); si pasan de <see cref="clsContextoRedaccion.LargoMaximoIndicaciones"/>
    /// caracteres responde <c>SOLICITUD_INVALIDA</c> (400) sin llamar a la IA. No se guardan en la cotización.
    /// </summary>
    Task<clsResultado<clsTextosGuardados>> RedactarAsync(
        Guid usuarioId, string folio, bool sobrescribir, int versionEsperada, string? indicacionesRedaccion = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Guarda los textos del comercial (<c>editado = true</c>; la fuente conserva el origen del texto que editó, o <c>estandar</c>).
    /// <c>TEXTOS_COTIZACION_INVALIDOS</c> (400) si están vacíos, son muy largos o traen URL o correos; los montos se permiten.
    /// </summary>
    Task<clsResultado<clsTextosGuardados>> EditarAsync(
        Guid usuarioId, string folio, string? asunto, string? introduccion, int versionEsperada, CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsTextosCosteoService"/>. El contexto que recibe la IA lo arma <see cref="Contexto"/> desde la
/// cotización, sin ningún monto. La IA se llama ANTES de abrir la transacción y la escritura se confirma con el control de versión: si
/// la cotización cambió mientras la IA redactaba, responde <c>CONFLICTO_CONCURRENCIA</c> y no guarda nada.
/// </summary>
public sealed class clsTextosCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsRedaccionCotizacionService redaccion,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    TimeProvider tiempo) : IClsTextosCosteoService
{
    /// <summary>Herramienta de la auditoría de la edición del comercial (<c>EventosHerramienta.herramienta</c>; sin los textos).</summary>
    public const string HerramientaEdicion = "editar_textos_costeo";

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c>.</summary>
    public const string MotivoVersion = "Cambio de los textos del PDF después del envío";

    /// <summary>HU-036.3 (#207): cierre del mensaje de <c>COTIZACION_NO_FINALIZADA</c> al redactar.</summary>
    public const string AccionRedactar = "redactar los textos del PDF";

    /// <summary>HU-036.3 (#207): cierre del mensaje de <c>COTIZACION_NO_FINALIZADA</c> al editar los textos.</summary>
    public const string AccionEditar = "editar los textos del PDF";

    public async Task<clsResultado<clsTextosGuardados>> RedactarAsync(
        Guid usuarioId, string folio, bool sobrescribir, int versionEsperada, string? indicacionesRedaccion = null, CancellationToken cancellationToken = default)
    {
        // HU-013.18 (#296): las indicaciones del comercial se normalizan antes de nada; si pasan del tope, 400 SOLICITUD_INVALIDA sin llamar a la IA.
        if (!NormalizarIndicaciones(indicacionesRedaccion, out var indicaciones, out var errorIndicaciones))
        {
            return errorIndicaciones!;
        }

        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        if (clsReglaPdfCosteo.ValidarEntrega(cotizacion, MotivoVersion) is { } entregada)
        {
            return entregada;
        }

        // HU-036.3 (#207): los textos del PDF se redactan con el precio ya aceptado.
        if (cotizacion.ValidarFinalizadaAntesDe(AccionRedactar) is { } sinFinalizar)
        {
            return sinFinalizar;
        }

        if (!sobrescribir && clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion) is { Editado: true })
        {
            return new clsErrorDominio(clsCodigosError.TextosEditados,
                $"Los textos de la cotización {cotizacion.Folio} los editó el comercial: confirma para reemplazarlos (sobrescribir=true).");
        }

        // HU-021.9 (#136): la línea de negocio sale de las rutas (sin respaldo a la cabecera): el estándar lleva la de la solicitud si
        // todas sus rutas tienen la misma (como el PDF) y la IA recibe las de sus rutas.
        var nombres = await clsLineasNegocioRutas.NombresAsync(cotizacion, politicas, cancellationToken);
        var estandar = clsCosteoCotizacionService.TextosEstandar(cotizacion, clsLineasNegocioRutas.Comun(cotizacion, nombres));
        // HU-013.18 (#296): las indicaciones del comercial, si vienen, se adjuntan al contexto como una preferencia (no se guardan).
        var contexto = Contexto(cotizacion, clsLineasNegocioRutas.ParaRedaccion(cotizacion, nombres)) with { IndicacionesComercial = indicaciones };
        var redactado = await redaccion.RedactarAsync(contexto, estandar, cancellationToken);
        var textos = new clsTextosCotizacion(redactado.Asunto, redactado.Introduccion, redactado.Fuente, Editado: false, tiempo.GetUtcNow());
        var pdfInvalidado = cotizacion.PdfPath is not null;
        var resultado = await GuardarAsync(cotizacion, textos, usuarioId, versionEsperada, cancellationToken);

        // Se audita DESPUÉS de guardar: con CONFLICTO_CONCURRENCIA queda guardado = false (la fuente no llegó a la cotización).
        await auditoria.RegistrarAsync(
            clsRedaccionCotizacionService.HerramientaAuditoria,
            // HU-013.18 (#296): la auditoría solo registra SI hubo indicaciones (con_indicaciones), nunca el texto.
            new { folio = cotizacion.Folio, sobrescribir, con_indicaciones = indicaciones is not null },
            new
            {
                proveedor = redactado.Proveedor,
                modelo = redactado.Modelo,
                version_prompt = redactado.VersionPrompt,
                duracion_ms = redactado.DuracionMs,
                fuente = redactado.Fuente,
                respaldo = redactado.Respaldo,
                motivo_respaldo = redactado.MotivoRespaldo,
                sha256 = clsRedaccionCotizacionService.Huella(textos.Asunto, textos.Introduccion),
                guardado = resultado.EsExitoso,
                pdf_invalidado = resultado.EsExitoso && pdfInvalidado,
            },
            exito: resultado.EsExitoso,
            redactado.DuracionMs,
            threadId: null);
        return resultado;
    }

    public async Task<clsResultado<clsTextosGuardados>> EditarAsync(
        Guid usuarioId, string folio, string? asunto, string? introduccion, int versionEsperada, CancellationToken cancellationToken = default)
    {
        var cronometro = Stopwatch.StartNew();
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var (resultado, pdfInvalidado) = await EditarAsync(cotizacion, usuarioId, asunto, introduccion, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaEdicion,
            new { folio = cotizacion.Folio },
            resultado.EsExitoso
                ? (object)new
                {
                    fuente = resultado.Valor.Textos.Fuente,
                    editado = true,
                    pdf_invalidado = pdfInvalidado,
                    sha256 = clsRedaccionCotizacionService.Huella(resultado.Valor.Textos.Asunto, resultado.Valor.Textos.Introduccion),
                    version_prompt = redaccion.VersionPrompt,
                }
                : new { error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            threadId: null);
        return resultado;
    }

    private async Task<(clsResultado<clsTextosGuardados> Resultado, bool PdfInvalidado)> EditarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, string? asunto, string? introduccion, int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), false);
        }

        var validacion = clsReglasTextosCotizacion.Validar(asunto, introduccion, permitirMontos: true);
        if (!validacion.EsValida)
        {
            return (new clsErrorDominio(clsCodigosError.TextosCotizacionInvalidos, validacion.Mensaje!), false);
        }

        if (clsReglaPdfCosteo.ValidarEntrega(cotizacion, MotivoVersion) is { } entregada)
        {
            return (entregada, false);
        }

        if (cotizacion.ValidarFinalizadaAntesDe(AccionEditar) is { } sinFinalizar)
        {
            return (sinFinalizar, false);
        }

        var anteriores = clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion);
        if (anteriores is { Editado: true } && anteriores.Asunto == validacion.Asunto && anteriores.Introduccion == validacion.Introduccion)
        {
            // Idempotente: los mismos textos ya editados no cambian nada (ni invalidan el PDF).
            return (new clsTextosGuardados(anteriores, clsCosteoCotizacionService.VersionEtag(cotizacion)), false);
        }

        var textos = new clsTextosCotizacion(
            validacion.Asunto!, validacion.Introduccion!, anteriores?.Fuente ?? clsTextosCotizacion.FuenteEstandar, Editado: true, tiempo.GetUtcNow());
        var pdfInvalidado = cotizacion.PdfPath is not null;
        return (await GuardarAsync(cotizacion, textos, usuarioId, versionEsperada, cancellationToken), pdfInvalidado);
    }

    /// <summary>Guarda los textos, invalida el PDF vigente (queda en el historial) y sube la versión, con el control de versión.</summary>
    private async Task<clsResultado<clsTextosGuardados>> GuardarAsync(
        clsCotizacionSolicitud cotizacion, clsTextosCotizacion textos, Guid usuarioId, int versionEsperada, CancellationToken cancellationToken)
    {
        cotizacion.RegistrarTextos(clsTextosCotizacionJson.Escribir(textos));
        clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.Textos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? new clsTextosGuardados(textos, clsCosteoCotizacionService.VersionEtag(cotizacion))
            : clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
    }

    /// <summary>
    /// Contexto de la IA desde la cotización (ADR-018 §2a; por ruta desde HU-021.9, #136): cliente, líneas de negocio de las rutas
    /// (<paramref name="lineaNegocio"/>), rutas (origen y destino de cada ruta; HU-028.1, #183: sin las rechazadas por la Mesa de Costeo),
    /// tipos de vehículo de sus camiones vigentes (sin repetir; un camión descartado o rechazado en Nexo no cuenta), nombres de los conexos de las rutas y moneda, cada texto recortado a <see cref="clsContextoRedaccion.LargoMaximoCampo"/>.
    /// Nada sale de la cabecera salvo el cliente y la moneda. Nada más: ni costos, ni margen, ni precios, ni la TRM, ni el folio, el NIT o
    /// el comercial.
    /// </summary>
    public static clsContextoRedaccion Contexto(clsCotizacionSolicitud cotizacion, string? lineaNegocio)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        // HU-028.1 (#183): sin las rutas que la Mesa de Costeo de Nexo rechazó (el PDF las muestra "Sin costeo"): el texto de la IA no debe
        // ofrecer al cliente una ruta ni unos conexos que no se cotizaron.
        var ordenadas = cotizacion.RutasCotizables;
        List<clsRutaRedaccion> rutas = [.. ordenadas
            .Where(r => !string.IsNullOrWhiteSpace(r.OrigenNombre) && !string.IsNullOrWhiteSpace(r.DestinoNombre))
            .Select(r => new clsRutaRedaccion(Recortar(r.OrigenNombre)!, Recortar(r.DestinoNombre)!)).Distinct().Take(clsContextoRedaccion.MaximoRutas)];
        var vehiculos = ordenadas.SelectMany(r => clsVehiculoLineaSolicitud.EnOrden(r.VehiculosQueCuentan))
            .Select(v => v.TipoVehiculo?.Trim()).OfType<string>().Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var conexos = ordenadas.SelectMany(r => r.Conexos)
            .Select(s => Recortar(s.Nombre ?? s.IdServicioConexo)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new clsContextoRedaccion(
            Recortar(cotizacion.ClienteNombre),
            Recortar(lineaNegocio),
            rutas,
            Recortar(vehiculos.Count == 0 ? null : string.Join(", ", vehiculos)),
            conexos,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos);
    }

    /// <summary>
    /// HU-013.18 (#296): normaliza las indicaciones del comercial. Recorta los espacios; vacío o solo espacios queda en <c>null</c> (sin
    /// indicaciones); por encima de <see cref="clsContextoRedaccion.LargoMaximoIndicaciones"/> devuelve <c>false</c> con 400
    /// <c>SOLICITUD_INVALIDA</c> (el llamador no debe seguir a la IA).
    /// </summary>
    public static bool NormalizarIndicaciones(string? entrada, out string? indicaciones, out clsErrorDominio? error)
    {
        var limpio = entrada?.Trim();
        if (string.IsNullOrEmpty(limpio))
        {
            indicaciones = null;
            error = null;
            return true;
        }

        if (limpio.Length > clsContextoRedaccion.LargoMaximoIndicaciones)
        {
            indicaciones = null;
            error = new clsErrorDominio(clsCodigosAplicacion.SolicitudInvalida,
                $"Las indicaciones para el redactor no pueden pasar de {clsContextoRedaccion.LargoMaximoIndicaciones} caracteres.");
            return false;
        }

        indicaciones = limpio;
        error = null;
        return true;
    }

    private static string? Recortar(string? texto)
    {
        var limpio = texto?.Trim();
        return limpio is { Length: > clsContextoRedaccion.LargoMaximoCampo } ? limpio[..clsContextoRedaccion.LargoMaximoCampo] : limpio;
    }

    private async Task<clsCotizacionSolicitud?> PropiaAsync(Guid usuarioId, string folio, CancellationToken cancellationToken) =>
        usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
