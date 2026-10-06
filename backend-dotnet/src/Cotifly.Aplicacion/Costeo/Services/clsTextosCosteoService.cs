using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
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
/// (<c>COTIZACION_NO_ENCONTRADA</c> → 404); exigen la versión de la fila (<c>CONFLICTO_CONCURRENCIA</c>) y siguen la regla del PDF
/// (<c>PDF_YA_ENVIADO</c>; si no se ha enviado, invalidan el PDF vigente).
/// </summary>
public interface IClsTextosCosteoService
{
    /// <summary>
    /// Redacta y guarda los textos (<c>fuente = ia</c>, o <c>estandar</c> si la IA no sirvió: nunca falla por la IA). Con textos editados
    /// por el comercial responde <c>TEXTOS_EDITADOS</c> sin llamar a la IA, salvo con <paramref name="sobrescribir"/>.
    /// </summary>
    Task<clsResultado<clsTextosGuardados>> RedactarAsync(
        Guid usuarioId, string folio, bool sobrescribir, int versionEsperada, CancellationToken cancellationToken = default);

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

    public async Task<clsResultado<clsTextosGuardados>> RedactarAsync(
        Guid usuarioId, string folio, bool sobrescribir, int versionEsperada, CancellationToken cancellationToken = default)
    {
        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersion) is { } entregada)
        {
            return entregada;
        }

        if (!sobrescribir && clsTextosCotizacionJson.Leer(cotizacion.TextosCotizacion) is { Editado: true })
        {
            return new clsErrorDominio(clsCodigosError.TextosEditados,
                $"Los textos de la cotización {cotizacion.Folio} los editó el comercial: confirma para reemplazarlos (sobrescribir=true).");
        }

        var linea = cotizacion.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, cancellationToken) : null;
        var lineas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var estandar = clsCosteoCotizacionService.TextosEstandar(cotizacion, lineas, linea?.LineaNegocio);
        var redactado = await redaccion.RedactarAsync(Contexto(cotizacion, linea?.LineaNegocio, lineas), estandar, cancellationToken);
        var textos = new clsTextosCotizacion(redactado.Asunto, redactado.Introduccion, redactado.Fuente, Editado: false, tiempo.GetUtcNow());
        var pdfInvalidado = cotizacion.PdfPath is not null;
        var resultado = await GuardarAsync(cotizacion, textos, usuarioId, versionEsperada, cancellationToken);

        // Se audita DESPUÉS de guardar: con CONFLICTO_CONCURRENCIA queda guardado = false (la fuente no llegó a la cotización).
        await auditoria.RegistrarAsync(
            clsRedaccionCotizacionService.HerramientaAuditoria,
            new { folio = cotizacion.Folio, sobrescribir },
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
        clsCotizacion cotizacion, Guid usuarioId, string? asunto, string? introduccion, int versionEsperada, CancellationToken cancellationToken)
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

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersion) is { } entregada)
        {
            return (entregada, false);
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
        clsCotizacion cotizacion, clsTextosCotizacion textos, Guid usuarioId, int versionEsperada, CancellationToken cancellationToken)
    {
        cotizacion.RegistrarTextos(clsTextosCotizacionJson.Escribir(textos));
        clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.Textos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken)
            ? new clsTextosGuardados(textos, clsCosteoCotizacionService.VersionEtag(cotizacion))
            : clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
    }

    /// <summary>
    /// Contexto de la IA desde la cotización (ADR-018 §2a): cliente, línea, rutas (las líneas de la solicitud, o el origen y destino de
    /// la cotización), tipo de vehículo, nombres de los conexos incluidos y moneda, cada texto recortado a
    /// <see cref="clsContextoRedaccion.LargoMaximoCampo"/>. Nada más: ni costos, ni margen, ni precios, ni la TRM, ni el folio, el NIT o
    /// el comercial.
    /// </summary>
    public static clsContextoRedaccion Contexto(
        clsCotizacion cotizacion, string? lineaNegocio, IReadOnlyList<clsCotizacionLineaSolicitud> lineas)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        var ordenadas = lineas.OrderBy(l => l.Orden).ToList();
        List<clsRutaRedaccion> rutas = ordenadas.Count > 0
            ? [.. ordenadas.Select(l => new clsRutaRedaccion(Recortar(l.Origen)!, Recortar(l.Destino)!)).Distinct().Take(clsContextoRedaccion.MaximoRutas)]
            : cotizacion.Origen is { } origen && cotizacion.Destino is { } destino ? [new clsRutaRedaccion(Recortar(origen)!, Recortar(destino)!)] : [];
        var vehiculos = ordenadas.Count > 0
            ? ordenadas.Select(l => l.TipoVehiculo).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : cotizacion.TipoVehiculo is { } vehiculo ? [vehiculo] : [];
        var conexos = clsServiciosConexosJson.Leer(cotizacion.ServiciosConexos)
            .Where(s => s.Incluido)
            .Select(s => Recortar(s.Nombre ?? s.Categoria)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new clsContextoRedaccion(
            Recortar(cotizacion.ClienteNombre),
            Recortar(lineaNegocio),
            rutas,
            Recortar(vehiculos.Count == 0 ? null : string.Join(", ", vehiculos)),
            conexos,
            cotizacion.Moneda ?? clsCotizacion.MonedaPesos);
    }

    private static string? Recortar(string? texto)
    {
        var limpio = texto?.Trim();
        return limpio is { Length: > clsContextoRedaccion.LargoMaximoCampo } ? limpio[..clsContextoRedaccion.LargoMaximoCampo] : limpio;
    }

    private async Task<clsCotizacion?> PropiaAsync(Guid usuarioId, string folio, CancellationToken cancellationToken) =>
        usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
}
