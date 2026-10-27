using System.Diagnostics;
using System.Globalization;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-041.1 (#231, ADR-025): margen y servicios conexos para varias rutas (o todas) de la cotización en UNA operación. Cada operación
/// cumple las mismas reglas que N llamadas a los endpoints por ruta (<c>PUT .../costeo/margen</c>, <c>POST</c> y <c>PUT
/// .../costeo/servicios-conexos</c>), pero es atómica: si una ruta no admite el cambio no cambia ninguna y el error dice cuál la bloqueó
/// (<c>detalle.linea_orden</c> y <c>detalle.linea_solicitud_id</c>). El PDF vigente se invalida una sola vez y queda una sola entrada de
/// auditoría por operación.
/// </summary>
public interface IClsCosteoLineasService
{
    /// <summary>
    /// <c>PUT /web/cotizaciones/{folio}/costeo/margen/lineas</c>: el mismo margen manual para las rutas elegidas, o cada una de vuelta a su
    /// margen sugerido (<c>revertir_sugerido</c>). Errores: <c>SOLICITUD_INVALIDA</c> (400, forma), <c>COTIZACION_NO_ENCONTRADA</c> (404),
    /// <c>CONFLICTO_CONCURRENCIA</c> (409), <c>PDF_YA_ENVIADO</c> (409, enviada, con resultado o finalizada), <c>SIN_RUTAS_COTIZABLES</c>
    /// (409, todas rechazadas) y, de la ruta que bloquea: <c>RUTA_INEXISTENTE</c>, <c>AJUSTE_MARGEN_INVALIDO</c>,
    /// <c>JUSTIFICACION_REQUERIDA</c> (400), <c>RUTA_RECHAZADA</c> y <c>COSTEO_NO_LISTO</c> (409).
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> AjustarMargenAsync(
        Guid usuarioId, string folio, clsMargenLineasEntrada entrada, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /web/cotizaciones/{folio}/costeo/servicios-conexos/lineas</c>: agrega un servicio del catálogo de Nexo, con su costo del
    /// catálogo, a las rutas elegidas (las que ya lo tienen no cambian). Queda <c>estado_nexo = pendiente</c> y el envío diferido lo lleva a
    /// Nexo (ADR-025). Además de los de <see cref="AjustarMargenAsync"/>: <c>ESTADO_NO_PERMITE</c> (409, licitación),
    /// <c>LINEA_SIN_COTIZACION_NEXO</c> (409), <c>CONEXO_INVALIDO</c> (400, el servicio no está activo en el catálogo) y los de Nexo al leer
    /// el catálogo (<c>NEXO_NO_DISPONIBLE</c> 503, <c>NEXO_SIN_PERMISO</c> 403), sin cambiar nada.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> AgregarConexoAsync(
        Guid usuarioId, string folio, clsConexoLineasEntrada entrada, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST /web/cotizaciones/{folio}/costeo/servicios-conexos/lineas/quitar</c>: quita de las rutas elegidas el conexo con ese
    /// <c>servicio_id</c> del catálogo (se borra su fila, como el <c>PUT</c> por ruta; Nexo no cambia). Las rutas que no lo tienen no
    /// cambian. Errores como <see cref="AjustarMargenAsync"/>.
    /// </summary>
    Task<clsResultado<clsCosteoCotizacion>> QuitarConexoAsync(
        Guid usuarioId, string folio, clsConexoLineasEntrada entrada, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsCosteoLineasService"/> (ver ADR-025).</summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Todo o nada.</b> Primero se validan TODAS las rutas sin tocar la cotización (con las mismas reglas del dominio que usan los
/// endpoints por ruta: <see cref="clsCotizacionSolicitud.ValidarAjusteMargen"/>, <see cref="clsAjusteMargenCosteoService.Recalcular"/>,
/// <see cref="clsServiciosConexosCosteoService.ValidarPrecondiciones"/>...); solo si todas pasan se aplican y se guardan en UNA escritura
/// con el control de versión (<see cref="IClsUnitOfWork.GuardarCambiosSiVersionAsync"/>, el <c>If-Match</c>).</item>
/// <item><b>Nexo.</b> El margen y quitar conexos no tocan Nexo (igual que por ruta). Agregar no llama al <c>POST</c> de conexos de Nexo
/// dentro de la petición: el conexo se guarda en Cotifly con el costo del catálogo (como un conexo capturado con la ruta) y el envío
/// diferido (<see cref="IClsEnvioConexosNexoService"/>) lo lleva a la línea del primer camión de cada ruta, una sola vez, con su registro
/// y sus estados (<c>pendiente</c>, <c>enviado</c>, <c>incierto</c>, <c>fallido_definitivo</c>). En la misma transacción queda el registro
/// <c>en_espera</c> de cada ruta y, si el polling estaba detenido (entregada), la cotización vuelve a la agenda solo para ese envío.</item>
/// <item><b>Idempotencia.</b> <c>Idempotency-Key</c> opcional (<see cref="IClsAlmacenIdempotencia"/>, persistente): la repetición con la
/// misma clave devuelve la respuesta guardada sin ejecutar ni auditar otra vez. Sin clave, repetir la misma operación responde 200 sin
/// cambios (las rutas ya están como se pidió).</item>
/// </list>
/// </remarks>
public sealed class clsCosteoLineasService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenAjusteRepository ajustes,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IOptions<clsOpcionesPdf> opcionesPdf,
    IClsAlmacenIdempotencia idempotencia,
    IClsEnvioConexosNexoService envioConexos,
    IClsCostosConexosNexo costosConexos,
    IClsCategoriasConexos categoriasConexos,
    IClsSituacionLineasNexo situacionesNexo,
    IClsReloj reloj,
    ILogger<clsCosteoLineasService> logger) : IClsCosteoLineasService
{
    /// <summary>Herramienta de la auditoría del margen masivo (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaMargen = "ajustar_margen_costeo_lineas";

    /// <summary>Herramienta de la auditoría de agregar un conexo a varias rutas.</summary>
    public const string HerramientaAgregarConexo = "agregar_servicio_conexo_lineas";

    /// <summary>Herramienta de la auditoría de quitar un conexo de varias rutas.</summary>
    public const string HerramientaQuitarConexo = "quitar_servicio_conexo_lineas";

    /// <summary>Ámbitos de la <c>Idempotency-Key</c> (<c>"{operacion}:{usuarioId}"</c>).</summary>
    public const string OperacionMargen = "margen_lineas";

    public const string OperacionAgregarConexo = "conexo_lineas";
    public const string OperacionQuitarConexo = "quitar_conexo_lineas";

    /// <summary>
    /// Tope de rutas por operación: el mismo de una cotización (<c>clsValidadorLicitacion.MaximoLineas</c>, 1000). Una lista más larga es
    /// <c>SOLICITUD_INVALIDA</c>; con <c>todas</c> nunca se pasa (una cotización no tiene más rutas).
    /// </summary>
    public const int MaximoLineas = Licitaciones.Validators.clsValidadorLicitacion.MaximoLineas;

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c> en el margen masivo.</summary>
    public const string MotivoVersionMargen = clsAjusteMargenCosteoService.MotivoVersion;

    /// <summary>Motivo de la versión nueva que sugiere <c>PDF_YA_ENVIADO</c> en los conexos masivos.</summary>
    public const string MotivoVersionConexos = clsCosteoCotizacionService.MotivoVersionConexos;

    // ------------------------------------------------------------------ Margen

    public Task<clsResultado<clsCosteoCotizacion>> AjustarMargenAsync(
        Guid usuarioId, string folio, clsMargenLineasEntrada entrada, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return UnaVezAsync(OperacionMargen, usuarioId, folio, entrada, claveIdempotencia,
            ct => EjecutarMargenAsync(usuarioId, folio, entrada, versionEsperada, ct), cancellationToken);
    }

    private async Task<clsResultado<clsCosteoCotizacion>> EjecutarMargenAsync(
        Guid usuarioId, string folio, clsMargenLineasEntrada entrada, int versionEsperada, CancellationToken cancellationToken)
    {
        var lineas = ValidarSeleccion(entrada.Seleccion);
        if (lineas.EsFallo)
        {
            return lineas.Error;
        }

        var revertir = entrada.RevertirSugerido == true;
        if (revertir == (entrada.Margen is not null))
        {
            return Invalida("Envía 'margen' (el mismo para todas las rutas elegidas) o 'revertir_sugerido: true', no los dos.");
        }

        if (entrada.Margen is { } margenPedido && clsLineaSolicitud.ValidarForma(margenPedido, entrada.Justificacion) is { } forma)
        {
            return forma;
        }

        if (entrada.Justificacion is { Length: > clsLineaSolicitud.LargoMaximoJustificacion })
        {
            return new clsErrorDominio(clsCodigosError.AjusteMargenInvalido,
                $"La justificación admite hasta {clsLineaSolicitud.LargoMaximoJustificacion} caracteres.");
        }

        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var cronometro = Stopwatch.StartNew();
        var (resultado, cambios, pdfInvalidado) = await AplicarMargenAsync(
            cotizacion, usuarioId, lineas.Valor, entrada.Margen, revertir, entrada.Justificacion, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaMargen,
            new
            {
                folio = cotizacion.Folio,
                lineas = lineas.Valor,
                todas = lineas.Valor.Count == 0,
                margen = entrada.Margen,
                revertir_sugerido = revertir,
                justificacion = clsMargenAjuste.LimpiarJustificacion(entrada.Justificacion),
            },
            resultado.EsExitoso
                ? (object)new
                {
                    rutas = cambios.Select(c => new
                    {
                        linea_orden = c.Orden,
                        linea_solicitud_id = c.RutaId,
                        margen_anterior = c.Anterior,
                        margen_aplicado = c.Aplicado,
                        margen_sugerido = c.Sugerido,
                        origen_margen = c.Origen,
                    }).ToList(),
                    rutas_cambiadas = cambios.Count,
                    pdf_invalidado = pdfInvalidado,
                }
                : ErrorAuditado(resultado.Error),
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, IReadOnlyList<clsCambioMargenRuta> Cambios, bool PdfInvalidado)> AplicarMargenAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, IReadOnlyList<int> lineas, decimal? margen, bool revertir, string? justificacion,
        int versionEsperada, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        // La regla del PDF y de la cotización finalizada es de la cotización, no de una ruta: va antes que las rutas.
        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersionMargen) is { } cerrada)
        {
            return (cerrada, [], false);
        }

        var rutas = Rutas(cotizacion, lineas);
        if (rutas.EsFallo)
        {
            return (rutas.Error, [], false);
        }

        // 1) Validar TODAS las rutas sin tocar nada (las mismas invariantes del ajuste por ruta).
        var plan = new List<(clsLineaSolicitud Ruta, decimal Margen)>();
        foreach (var ruta in rutas.Valor)
        {
            if (cotizacion.ValidarRutaNoRechazada(ruta) is { } rechazada)
            {
                return (rechazada, [], false);
            }

            decimal margenRuta;
            if (revertir)
            {
                if (ruta.MargenSugeridoParaRestaurar is not { } sugerido)
                {
                    return (ConRuta(new clsErrorDominio(clsCodigosError.AjusteMargenInvalido,
                        $"La ruta {ruta.Orden} de la cotización {cotizacion.Folio} no tiene margen sugerido al que volver."), ruta), [], false);
                }

                margenRuta = sugerido.Margen;
            }
            else
            {
                margenRuta = margen!.Value;
            }

            if (cotizacion.ValidarAjusteMargen(ruta.Orden, margenRuta, justificacion) is { } invalido)
            {
                return (ConRuta(invalido, ruta), [], false);
            }

            var redondeado = clsMargenAjuste.RedondearMargen(margenRuta);
            if (!CambiaMargen(ruta, redondeado, revertir))
            {
                continue;
            }

            // Vista previa: si el módulo de precio rechaza el margen de esta ruta, la cotización queda intacta.
            if (clsAjusteMargenCosteoService.Recalcular(cotizacion, ruta, redondeado) is { EsFallo: true } previa)
            {
                return (ConRuta(previa.Error, ruta), [], false);
            }

            plan.Add((ruta, redondeado));
        }

        if (plan.Count == 0)
        {
            // Idempotente: todas las rutas ya tienen ese margen; ni ajuste, ni PDF invalidado, ni versión nueva.
            return (await VistaAsync(cotizacion, cancellationToken), [], false);
        }

        // 2) Aplicar (ya validado) y guardar todo junto.
        var ahora = cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt;
        var cambios = new List<clsCambioMargenRuta>();
        foreach (var (ruta, nuevo) in plan)
        {
            var anterior = ruta.MargenAplicado;
            var sugerido = ruta.SugeridoVigente;
            if (revertir)
            {
                var restaurado = cotizacion.RestaurarMargenSugerido(ruta.Orden, ahora);
                if (restaurado.EsFallo)
                {
                    return (ConRuta(restaurado.Error, ruta), [], false);
                }
            }
            else
            {
                // HU-021.8: el ajuste queda con la ruta REAL (lineaSolicitudId) y el sugerido de esa ruta, como por ruta.
                var registro = clsMargenAjuste.RegistrarNegociacionCosteo(
                    sugerido ?? anterior ?? nuevo, nuevo, justificacion, usuarioId, cotizacion.Id, cotizacion.Folio, ruta.MetodoCalculo, ruta.Id);
                if (registro.EsFallo)
                {
                    return (ConRuta(registro.Error, ruta), [], false);
                }

                var aplicado = cotizacion.AplicarMargenNegociado(ruta.Orden, nuevo, ahora);
                if (aplicado.EsFallo)
                {
                    return (ConRuta(aplicado.Error, ruta), [], false);
                }

                ajustes.Agregar(registro.Valor);
            }

            cambios.Add(new clsCambioMargenRuta(ruta.Orden, ruta.Id, anterior, ruta.MargenAplicado, sugerido,
                ruta.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null));
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.AjusteMargen, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        return (await VistaAsync(cotizacion, CancellationToken.None), cambios, pdfInvalidado);
    }

    /// <summary>¿El margen <paramref name="redondeado"/> (o volver al sugerido) cambia la ruta?</summary>
    private static bool CambiaMargen(clsLineaSolicitud ruta, decimal redondeado, bool revertir) =>
        revertir
            ? ruta.MargenAplicado != redondeado || ruta.OrigenMargen != ruta.MargenSugeridoParaRestaurar?.Origen
            : ruta.MargenAplicado != redondeado;

    // ------------------------------------------------------------------ Conexos

    public Task<clsResultado<clsCosteoCotizacion>> AgregarConexoAsync(
        Guid usuarioId, string folio, clsConexoLineasEntrada entrada, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return UnaVezAsync(OperacionAgregarConexo, usuarioId, folio, entrada, claveIdempotencia,
            ct => EjecutarConexoAsync(usuarioId, folio, entrada, versionEsperada, agregar: true, ct), cancellationToken);
    }

    public Task<clsResultado<clsCosteoCotizacion>> QuitarConexoAsync(
        Guid usuarioId, string folio, clsConexoLineasEntrada entrada, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return UnaVezAsync(OperacionQuitarConexo, usuarioId, folio, entrada, claveIdempotencia,
            ct => EjecutarConexoAsync(usuarioId, folio, entrada, versionEsperada, agregar: false, ct), cancellationToken);
    }

    private async Task<clsResultado<clsCosteoCotizacion>> EjecutarConexoAsync(
        Guid usuarioId, string folio, clsConexoLineasEntrada entrada, int versionEsperada, bool agregar, CancellationToken cancellationToken)
    {
        var lineas = ValidarSeleccion(entrada.Seleccion);
        if (lineas.EsFallo)
        {
            return lineas.Error;
        }

        if (!Guid.TryParse(entrada.ServicioId, out var servicioGuid))
        {
            return Invalida("'servicio_id' debe ser el id (UUID) de un servicio del catálogo de Nexo (GET /web/servicios-conexos).");
        }

        var servicio = servicioGuid.ToString("D");
        if (agregar)
        {
            if (entrada.Cantidad is not { } cantidad || cantidad < clsServiciosConexosCosteoService.CantidadMinima
                || cantidad > clsServiciosConexosCosteoService.CantidadMaxima || decimal.Round(cantidad, 2) != cantidad)
            {
                return Invalida($"'cantidad' debe estar entre {clsServiciosConexosCosteoService.CantidadMinima.ToString(CultureInfo.InvariantCulture)} y "
                    + $"{clsServiciosConexosCosteoService.CantidadMaxima.ToString(CultureInfo.InvariantCulture)}, con hasta 2 decimales.");
            }
        }
        else if (entrada.Cantidad is not null)
        {
            return Invalida("Quitar un conexo no lleva 'cantidad'.");
        }

        var cotizacion = await PropiaAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var cronometro = Stopwatch.StartNew();
        var (resultado, rutas, pdfInvalidado) = agregar
            ? await AgregarAsync(cotizacion, usuarioId, lineas.Valor, servicio, entrada.Cantidad!.Value, versionEsperada, cancellationToken)
            : await QuitarAsync(cotizacion, usuarioId, lineas.Valor, servicio, versionEsperada, cancellationToken);
        await auditoria.RegistrarAsync(
            agregar ? HerramientaAgregarConexo : HerramientaQuitarConexo,
            new { folio = cotizacion.Folio, lineas = lineas.Valor, todas = lineas.Valor.Count == 0, servicio_id = servicio, cantidad = entrada.Cantidad },
            resultado.EsExitoso
                // Sin montos: en qué rutas se agregó o se quitó.
                ? (object)new
                {
                    rutas = rutas.Select(r => new { linea_orden = r.Orden, linea_solicitud_id = r.RutaId, cantidad_anterior = r.Anterior, cantidad_nueva = r.Nueva }).ToList(),
                    rutas_cambiadas = rutas.Count,
                    pdf_invalidado = pdfInvalidado,
                }
                : ErrorAuditado(resultado.Error),
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
        return resultado;
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, IReadOnlyList<clsCambioConexoRuta> Rutas, bool PdfInvalidado)> AgregarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, IReadOnlyList<int> lineas, string servicio, decimal cantidad, int versionEsperada,
        CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        var rutas = Rutas(cotizacion, lineas);
        if (rutas.EsFallo)
        {
            return (rutas.Error, [], false);
        }

        // Las mismas precondiciones del POST por ruta (licitación, costeo con margen, regla del PDF, ruta rechazada, línea de Nexo), ruta
        // por ruta y sin tocar nada. Las de la cotización salen igual en cualquier ruta: sin detalle de ruta.
        var conexo = new clsConexoValidado(null, servicio, cantidad);
        foreach (var ruta in rutas.Valor)
        {
            if (clsServiciosConexosCosteoService.ValidarPrecondiciones(cotizacion, [conexo with { LineaSolicitudId = ruta.Id }]) is { } noPermitido)
            {
                return (DeRuta(noPermitido) ? ConRuta(noPermitido, ruta) : noPermitido, [], false);
            }
        }

        var destino = rutas.Valor.Where(r => !TieneServicio(r, servicio)).ToList();
        if (destino.Count == 0)
        {
            // Idempotente: todas las rutas elegidas ya tienen el conexo (para cambiar su cantidad: PUT .../costeo/servicios-conexos).
            return (await VistaAsync(cotizacion, cancellationToken), [], false);
        }

        // El costo es el del catálogo de Nexo, como un conexo capturado con la ruta (decisión 3). Nexo se lee ANTES de cambiar nada.
        var costos = await costosConexos.CostosAsync(usuarioId, [servicio], cancellationToken);
        if (costos.EsFallo)
        {
            return (costos.Error, [], false);
        }

        var deCatalogo = costos.Valor.Values.FirstOrDefault(c => string.Equals(c.Id, servicio, StringComparison.OrdinalIgnoreCase));
        if (deCatalogo is null || deCatalogo.CostoUnitario < 0m)
        {
            return (new clsErrorDominio(clsCodigosError.ConexoInvalido,
                $"El servicio conexo '{servicio}' no está activo en el catálogo de Nexo (GET /web/servicios-conexos).", clsCodigosLineaSolicitud.ConexoNoEncontrado), [], false);
        }

        var ahora = cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt;
        var cambio = new clsCambioConexo(deCatalogo.Id, deCatalogo.Nombre, deCatalogo.CostoUnitario, cantidad, deCatalogo.Id, deCatalogo.Categoria);
        var cambios = new List<clsCambioConexoRuta>();
        foreach (var ruta in destino)
        {
            var agregado = cotizacion.CambiarConexos(ruta.Orden, [cambio], ahora);
            if (agregado.EsFallo)
            {
                return (ConRuta(agregado.Error, ruta), [], false);
            }

            cambios.Add(new clsCambioConexoRuta(ruta.Orden, ruta.Id, null, cantidad));
        }

        // ADR-025: el envío a Nexo es el diferido (outbox): un registro en_espera por ruta en esta misma escritura y, con el polling detenido,
        // la cotización vuelve a la agenda solo para ese envío.
        await ProgramarEnvioAsync(cotizacion, destino, deCatalogo.Id, cancellationToken);
        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        return (await VistaAsync(cotizacion, CancellationToken.None), cambios, pdfInvalidado);
    }

    private async Task ProgramarEnvioAsync(
        clsCotizacionSolicitud cotizacion, IReadOnlyList<clsLineaSolicitud> rutas, string servicio, CancellationToken cancellationToken)
    {
        var solicitud = cotizacion.SolicitudNexoId!;   // ValidarPrecondiciones exige la cotización de Nexo y la línea del primer camión
        var registros = await cotizaciones.ListarEnviosConexosNexoAsync(cotizacion.Folio, solicitud, cancellationToken);
        var ahora = reloj.AhoraUtc;
        foreach (var ruta in rutas)
        {
            var intento = registros.Where(r => r.LineaSolicitudId == ruta.Id).Select(r => r.Intento).DefaultIfEmpty(0).Max() + 1;
            cotizaciones.RegistrarEnvioConexosNexo(new clsEnvioConexosNexo(
                cotizacion.Folio, solicitud, ruta.Id, intento, clsEnvioConexosNexo.EnEspera, [servicio], []) { EsperaDesde = ahora });
        }

        // Solo si el polling estaba detenido (entregada): mover una consulta ya agendada podría cruzarse con una réplica que la reclamó.
        if (cotizacion.CosteoSiguienteConsultaAt is null)
        {
            cotizacion.ProgramarRelecturaCosteo(ahora);
        }

        logger.LogInformation("Conexo {Servicio} agregado a {Rutas} rutas de {Folio}: queda en espera del envío diferido a Nexo.",
            servicio, rutas.Count, cotizacion.Folio);
    }

    private async Task<(clsResultado<clsCosteoCotizacion> Resultado, IReadOnlyList<clsCambioConexoRuta> Rutas, bool PdfInvalidado)> QuitarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, IReadOnlyList<int> lineas, string servicio, int versionEsperada,
        CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        // Las mismas reglas del PUT por ruta: costeo con precio y regla del PDF (de la cotización), ruta existente y no rechazada.
        if (!cotizacion.TieneCostoDisponible)
        {
            return (new clsErrorDominio(clsCodigosError.CosteoNoListo,
                $"La cotización {cotizacion.Folio} todavía no tiene el costeo de Nexo con margen aplicado."), [], false);
        }

        if (clsReglaPdfCosteo.Validar(cotizacion, MotivoVersionConexos) is { } cerrada)
        {
            return (cerrada, [], false);
        }

        var rutas = Rutas(cotizacion, lineas);
        if (rutas.EsFallo)
        {
            return (rutas.Error, [], false);
        }

        if (rutas.Valor.Select(cotizacion.ValidarRutaNoRechazada).FirstOrDefault(e => e is not null) is { } rechazada)
        {
            return (rechazada, [], false);
        }

        var ahora = cotizacion.CosteoListoAt ?? cotizacion.UpdatedAt;
        var cambios = new List<clsCambioConexoRuta>();
        foreach (var ruta in rutas.Valor)
        {
            var quitar = ruta.Conexos.Where(c => EsServicio(c, servicio)).ToList();
            if (quitar.Count == 0)
            {
                continue;
            }

            var quedan = ruta.Conexos.Where(c => !quitar.Contains(c))
                .ToDictionary(c => c.IdServicioConexo, c => c.Cantidad, clsCosteoCotizacionService.ComparadorIdConexo);
            var ajustados = cotizacion.AjustarConexosRuta(ruta.Orden, quedan, ahora);
            if (ajustados.EsFallo)
            {
                return (ConRuta(ajustados.Error, ruta), [], false);
            }

            cambios.Add(new clsCambioConexoRuta(ruta.Orden, ruta.Id, quitar.Sum(c => c.Cantidad), null));
        }

        if (cambios.Count == 0)
        {
            // Idempotente: ninguna ruta elegida tiene el conexo.
            return (await VistaAsync(cotizacion, cancellationToken), [], false);
        }

        var pdfInvalidado = clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ServiciosConexos, usuarioId, cotizaciones);
        cotizacion.IncrementarVersionNegocio();
        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, cancellationToken))
        {
            return (clsReglaPdfCosteo.Conflicto(cotizacion.Folio), [], false);
        }

        return (await VistaAsync(cotizacion, CancellationToken.None), cambios, pdfInvalidado);
    }

    /// <summary>
    /// ¿El conexo de la ruta es el servicio <paramref name="servicio"/> del catálogo? Por el id del catálogo guardado (#188) o por su id, si
    /// se guardó con el del catálogo (un conexo capturado o agregado en lote).
    /// </summary>
    public static bool EsServicio(clsServicioConexoRuta conexo, string servicio)
    {
        ArgumentNullException.ThrowIfNull(conexo);
        return string.Equals(conexo.IdServicioConexoCatalogo, servicio, StringComparison.OrdinalIgnoreCase)
               || string.Equals(conexo.IdServicioConexo, servicio, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TieneServicio(clsLineaSolicitud ruta, string servicio) => ruta.Conexos.Any(c => EsServicio(c, servicio));

    /// <summary>Errores de <see cref="clsServiciosConexosCosteoService.ValidarPrecondiciones"/> que son de la ruta (llevan su detalle).</summary>
    private static bool DeRuta(clsErrorDominio error) =>
        error.Codigo is clsCodigosError.RutaRechazada or clsCodigosError.LineaSinCotizacionNexo;

    // ------------------------------------------------------------------ Comunes

    /// <summary>
    /// Forma de la selección: exactamente una de <c>lineas</c> (1 a <see cref="MaximoLineas"/> <c>linea_orden</c> ≥ 1, sin repetir) o
    /// <c>todas: true</c>. Devuelve las líneas pedidas en orden, o ninguna (lista vacía) = todas.
    /// </summary>
    public static clsResultado<IReadOnlyList<int>> ValidarSeleccion(clsSeleccionLineas? seleccion)
    {
        var todas = seleccion?.Todas == true;
        var lista = seleccion?.Lineas;
        if (todas == (lista is not null))
        {
            return Invalida("Envía 'lineas' (los linea_orden de las rutas) o 'todas: true', no los dos.");
        }

        if (todas)
        {
            return clsResultado<IReadOnlyList<int>>.Exito([]);
        }

        if (lista!.Count == 0 || lista.Count > MaximoLineas)
        {
            return Invalida($"'lineas' debe traer de 1 a {MaximoLineas} rutas.");
        }

        var vistas = new HashSet<int>();
        for (var i = 0; i < lista.Count; i++)
        {
            if (lista[i] is not { } orden || orden < 1)
            {
                return Invalida($"lineas[{i}] debe ser un linea_orden de 1 o mayor.");
            }

            if (!vistas.Add(orden))
            {
                return Invalida($"lineas[{i}] está repetida.");
            }
        }

        return clsResultado<IReadOnlyList<int>>.Exito([.. vistas.Order()]);
    }

    /// <summary>
    /// Las rutas de la operación: las pedidas (cada una debe existir: si no, <c>RUTA_INEXISTENTE</c> con su <c>linea_orden</c>) o, con
    /// <c>todas</c>, las cotizables (sin las rechazadas por la Mesa; si no queda ninguna, <c>SIN_RUTAS_COTIZABLES</c>).
    /// </summary>
    private static clsResultado<IReadOnlyList<clsLineaSolicitud>> Rutas(clsCotizacionSolicitud cotizacion, IReadOnlyList<int> lineas)
    {
        if (lineas.Count == 0)
        {
            var cotizables = cotizacion.RutasCotizables;
            return cotizables.Count == 0
                ? new clsErrorDominio(clsCodigosError.SinRutasCotizables,
                    $"La cotización {cotizacion.Folio} no tiene rutas que cotizar: la Mesa de Costeo de Nexo las rechazó todas.")
                : clsResultado<IReadOnlyList<clsLineaSolicitud>>.Exito(cotizables);
        }

        var rutas = new List<clsLineaSolicitud>(lineas.Count);
        foreach (var orden in lineas)
        {
            var ruta = cotizacion.Rutas.FirstOrDefault(r => r.Orden == orden);
            if (ruta is null)
            {
                return new clsErrorDominio(clsCodigosError.RutaInexistente, $"La cotización {cotizacion.Folio} no tiene la ruta {orden}.")
                {
                    Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["linea_orden"] = orden },
                };
            }

            rutas.Add(ruta);
        }

        return rutas;
    }

    /// <summary>
    /// El error de la ruta que bloqueó la operación, con <c>detalle.linea_orden</c> y <c>detalle.linea_solicitud_id</c> (se conservan los
    /// datos que ya traía, p. ej. <c>margen_sugerido</c>). Los errores de la cotización (<c>PDF_YA_ENVIADO</c>...) se validan antes que
    /// las rutas y no pasan por aquí.
    /// </summary>
    private static clsErrorDominio ConRuta(clsErrorDominio error, clsLineaSolicitud ruta)
    {
        var datos = new Dictionary<string, object?>(error.Datos ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["linea_orden"] = ruta.Orden,
            ["linea_solicitud_id"] = ruta.Id,
        };
        if (error.Codigo == clsCodigosError.JustificacionRequerida)
        {
            datos["margen_sugerido"] = ruta.SugeridoVigente;
        }

        return error with { Datos = datos };
    }

    private Task<clsResultado<clsCosteoCotizacion>> UnaVezAsync<TEntrada>(
        string operacion, Guid usuarioId, string folio, TEntrada entrada, string? claveIdempotencia,
        Func<CancellationToken, Task<clsResultado<clsCosteoCotizacion>>> accion, CancellationToken cancellationToken) =>
        claveIdempotencia is null
            ? accion(cancellationToken)
            // Solo los éxitos consumen la clave: un error no tocó nada y la repetición se vuelve a evaluar.
            : idempotencia.EjecutarUnaVezAsync(
                $"{operacion}:{usuarioId}", claveIdempotencia, clsHuellaIdempotencia.Calcular(new { folio = folio?.Trim(), entrada }), accion, null,
                cancellationToken);

    private async Task<clsCotizacionSolicitud?> PropiaAsync(Guid usuarioId, string folio, CancellationToken cancellationToken) =>
        usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);

    /// <summary>La misma vista que <c>GET .../costeo</c> y los endpoints por ruta (con el <c>estado_nexo</c> de cada conexo).</summary>
    private Task<clsCosteoCotizacion> VistaAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken) =>
        clsCosteoCotizacionService.VistaConEstadosAsync(
            cotizacion, opcionesPdf.Value.BaseUrl, envioConexos, logger, cancellationToken, categoriasConexos, situacionesNexo.Obtener(cotizacion.Id));

    private static object ErrorAuditado(clsErrorDominio error) =>
        new { error = new { codigo = error.Codigo, mensaje = error.Mensaje, detalle = error.Datos } };

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosAplicacion.SolicitudInvalida, mensaje);

    private sealed record clsCambioMargenRuta(int Orden, Guid RutaId, decimal? Anterior, decimal? Aplicado, decimal? Sugerido, string? Origen);

    private sealed record clsCambioConexoRuta(int Orden, Guid RutaId, decimal? Anterior, decimal? Nueva);
}
