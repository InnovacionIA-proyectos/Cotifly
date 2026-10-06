using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Precio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Envío de la solicitud a costeo en Nexo (HU-005.5, ADR-015 §3): <c>POST /web/solicitudes-costeo</c> y
/// <c>POST /web/cotizaciones/{folio}/costeo/reintentar</c>.
/// </summary>
public interface IClsEnvioCosteoService
{
    /// <summary>
    /// Crea la cotización con el formulario ampliado (confirmada antes de llamar a Nexo) y la envía. Errores (nada queda guardado):
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> (también una <c>moneda</c> que no es COP ni USD), <c>MARGEN_LINEA_DESCONOCIDA</c>,
    /// <c>TRM_NO_DISPONIBLE</c> (en USD sin TRM del día, HU-013.7), <c>FOLIO_CONSECUTIVO_AGOTADO</c> y <c>ERROR_INTERNO</c>.
    /// Si Nexo falla la cotización se devuelve igual, en <c>error_envio</c> con el motivo.
    /// </summary>
    Task<clsResultado<clsCotizacion>> EnviarAsync(Guid usuarioId, clsEntradaSolicitudCosteo entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reenvía la solicitud guardada. <c>COTIZACION_NO_ENCONTRADA</c> si no existe o no es del comercial;
    /// <c>COSTEO_NO_REINTENTABLE</c> si no está en <c>error_envio</c> ni en un <c>pendiente_envio</c> abandonado.
    /// </summary>
    Task<clsResultado<clsCotizacion>> ReintentarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envía a Nexo una cotización ya guardada y confirmada con sus líneas (licitación, HU-011.2, ADR-017) y guarda el resultado
    /// (<c>pendiente_costeo</c>/<c>autocosteo</c> o <c>error_envio</c>), igual que el envío de HU-005.5. Sin el token de la petición.
    /// <paramref name="notaTrazabilidad"/> (re-solicitud, HU-013.4) se agrega a las <c>notas</c> que viajan a Nexo, sin guardarse en la
    /// cotización ni sobrepasar el límite de Nexo (<see cref="ConNota"/>).
    /// </summary>
    Task EnviarGuardadaAsync(
        clsCotizacion cotizacion,
        IReadOnlyList<clsCotizacionLineaSolicitud> lineas,
        IReadOnlyList<clsCotizacionPosicion> posiciones,
        clsMargenPolitica? politica,
        Guid usuarioId,
        string? notaTrazabilidad = null);
}

/// <summary>Implementación de <see cref="IClsEnvioCosteoService"/>.</summary>
/// <remarks>
/// <list type="number">
/// <item>Valida el formulario (mismos límites que Nexo), exige el NIT y resuelve la línea en <c>MargenesPolitica</c>.</item>
/// <item>Fija la moneda y, en USD, la TRM del día (HU-013.7, ADR-014 §5, <see cref="IClsFijacionMonedaService"/>): sin TRM no se guarda
/// nada ni se llama a Nexo. El polling, el ajuste de margen y el PDF convierten con esa TRM fijada; el reintento la conserva.</item>
/// <item>Crea la cotización con folio del servidor (ADR-011, con el NIT), el formulario y sus posiciones en <c>pendiente_envio</c>, y
/// confirma la transacción: lo que llenó el comercial nunca se pierde aunque Nexo no responda.</item>
/// <item>Resuelve el BP en el catálogo de Nexo por el NIT (ADR-005 §4: el BP solo sale del catálogo y no se persiste).</item>
/// <item>Envía a Nexo y guarda el resultado: <c>pendiente_costeo</c>/<c>autocosteo</c> con <c>costeoId</c> y la primera consulta del
/// polling, o <c>error_envio</c> con el motivo. Cada intento queda en la auditoría (<c>enviar_costeo_nexo</c>).</item>
/// </list>
/// </remarks>
public sealed class clsEnvioCosteoService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsMargenResolverService margen,
    IClsGeneradorFolioService folios,
    IClsBuscadorClientes buscadorClientes,
    IClsCosteoNexo nexo,
    IClsValidadorCatalogosCaptura catalogos,
    IClsFijacionMonedaService monedas,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IOptions<clsOpcionesCosteoNexo> opciones,
    ILogger<clsEnvioCosteoService> logger) : IClsEnvioCosteoService
{
    /// <summary>Herramienta con la que se audita cada intento de envío (<c>EventosHerramienta</c>).</summary>
    public const string HerramientaAuditoria = "enviar_costeo_nexo";

    public async Task<clsResultado<clsCotizacion>> EnviarAsync(Guid usuarioId, clsEntradaSolicitudCosteo entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(entrada.Solicitud);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "El envío a costeo exige el comercial autenticado.");
        }

        var nit = entrada.NitCliente?.Trim();
        var error = string.IsNullOrEmpty(nit)
            ? Invalida("El campo 'nit_cliente' es obligatorio: elige el cliente en el buscador.")
            : nit.Length > clsEntradaSolicitudCosteo.LargoMaximoNit
                ? Invalida($"El campo 'nit_cliente' supera {clsEntradaSolicitudCosteo.LargoMaximoNit} caracteres.")
                : entrada.ClienteNombre is { Length: > clsEntradaSolicitudCosteo.LargoMaximoNombre }
                    ? Invalida($"El campo 'cliente_nombre' supera {clsEntradaSolicitudCosteo.LargoMaximoNombre} caracteres.")
                    : string.IsNullOrWhiteSpace(entrada.LineaNegocio)
                        ? Invalida("El campo 'linea_negocio' es obligatorio.")
                        : ValidarMoneda(entrada.Moneda) ?? entrada.Solicitud.Validar();
        if (error is not null)
        {
            return error;
        }

        // G2 (HU-013.10): solo códigos de catálogo; sin catálogo maestro utilizable se rechaza antes de guardar (nunca texto libre).
        if (await catalogos.ValidarAsync(usuarioId, entrada.Solicitud.ALinea(), cancellationToken) is { } noCatalogo)
        {
            return noCatalogo;
        }

        var politica = await margen.ResolverLineaAsync(entrada.LineaNegocio, cancellationToken);
        if (politica is null)
        {
            return new clsErrorDominio(clsCodigosError.MargenLineaDesconocida, $"La línea de negocio '{entrada.LineaNegocio!.Trim()}' no existe en Cotifly.");
        }

        if (!politica.Activa)
        {
            return clsMargenResolverService.LineaInactiva(politica.LineaNegocio);
        }

        // G11: advertencia (no bloquea) si el mapeo a la línea de Nexo no está en su catálogo de líneas de negocio.
        catalogos.RevisarMapeoLineaNexo(politica);

        // HU-013.7 (ADR-014 §5): en USD la TRM del día de creación queda fija ANTES de guardar; sin TRM no se guarda ni se envía nada.
        var fijada = await monedas.FijarAsync(entrada.Moneda, cancellationToken);
        if (fijada.EsFallo)
        {
            return fijada.Error;
        }

        IReadOnlyList<clsCotizacionLineaSolicitud> lineas = [];
        IReadOnlyList<clsCotizacionPosicion> posiciones = [];
        var creada = await folios.CrearConFolioNuevoAsync<clsCotizacion>(usuarioId, nit, async (folio, ct) =>
        {
            var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion
            {
                Folio = folio.Valor,
                Version = folio.Version,
                LineaNegocioId = politica.Id,
                ClienteNombre = entrada.ClienteNombre,
                NitCliente = nit,
                UsuarioId = usuarioId,
                Moneda = fijada.Valor.Moneda,
                TrmAplicada = fijada.Valor.TrmAplicada,
            });
            if (cotizacion.EsFallo)
            {
                return cotizacion.Error;
            }

            // ADR-017: la única línea también va en CotizacionLineasSolicitud (las columnas de línea de Cotizaciones quedan como
            // espejo de la línea 1 para métricas, "Mis cotizaciones" y el PDF de una línea).
            cotizacion.Valor.RegistrarSolicitudCosteo(entrada.Solicitud);
            lineas = clsCotizacionLineaSolicitud.Desde(cotizacion.Valor.Id, [entrada.Solicitud.ALinea()]);
            cotizaciones.Agregar(cotizacion.Valor);
            cotizaciones.AgregarLineas(lineas);
            posiciones = [.. clsCotizacionPosicion.Desde(cotizacion.Valor.Id, entrada.Solicitud.Posiciones, lineas[0].Id, ordenInicial: 1)];
            cotizaciones.AgregarPosiciones(posiciones);
            await unidadTrabajo.GuardarCambiosAsync(ct);
            return cotizacion.Valor;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return creada.Error;
        }

        // Desde aquí, sin el token de la petición (ver EnviarANexoAsync). Se envía lo GUARDADO (normalizado: textos recortados), no la
        // entrada: el primer envío y el reintento mandan exactamente lo mismo a Nexo (G2: " 3S3 " nunca llega con espacios).
        var encabezado = creada.Valor.EncabezadoSolicitud ?? entrada.Solicitud.Encabezado;
        await EnviarANexoAsync(creada.Valor, encabezado, LineasConPosiciones(lineas, posiciones), lineas, politica, usuarioId, reintento: false);
        return creada.Valor;
    }

    public Task EnviarGuardadaAsync(
        clsCotizacion cotizacion,
        IReadOnlyList<clsCotizacionLineaSolicitud> lineas,
        IReadOnlyList<clsCotizacionPosicion> posiciones,
        clsMargenPolitica? politica,
        Guid usuarioId,
        string? notaTrazabilidad = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(posiciones);
        var encabezado = ConNota(cotizacion.EncabezadoSolicitud
            ?? throw new ArgumentException("La cotización no tiene el encabezado de la solicitud.", nameof(cotizacion)), notaTrazabilidad);
        return EnviarANexoAsync(cotizacion, encabezado, LineasConPosiciones(lineas, posiciones), lineas, politica, usuarioId, reintento: false);
    }

    /// <summary>
    /// Encabezado con <paramref name="nota"/> agregada a las <c>notas</c> (en una línea nueva). Si juntas superan
    /// <see cref="clsSolicitudCosteo.LargoNotas"/>, se recortan las notas existentes y la nota de trazabilidad se conserva completa.
    /// </summary>
    public static clsEncabezadoSolicitudCosteo ConNota(clsEncabezadoSolicitudCosteo encabezado, string? nota)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        if (string.IsNullOrWhiteSpace(nota))
        {
            return encabezado;
        }

        var agregada = nota.Trim();
        if (agregada.Length >= clsSolicitudCosteo.LargoNotas)
        {
            return encabezado with { Notas = agregada[..clsSolicitudCosteo.LargoNotas] };
        }

        var existentes = encabezado.Notas?.Trim();
        if (string.IsNullOrEmpty(existentes))
        {
            return encabezado with { Notas = agregada };
        }

        var disponible = clsSolicitudCosteo.LargoNotas - agregada.Length - 1;
        if (existentes.Length > disponible)
        {
            existentes = disponible <= 1 ? string.Empty : existentes[..(disponible - 1)] + "…";
        }

        return encabezado with { Notas = existentes.Length == 0 ? agregada : existentes + "\n" + agregada };
    }

    /// <summary>
    /// Líneas guardadas como datos de la solicitud, cada una con sus posiciones. Las posiciones sin línea (filas anteriores al
    /// backfill del 0005) se asignan a la línea 1.
    /// </summary>
    public static IReadOnlyList<clsLineaSolicitudCosteo> LineasConPosiciones(
        IReadOnlyList<clsCotizacionLineaSolicitud> lineas, IReadOnlyList<clsCotizacionPosicion> posiciones)
    {
        ArgumentNullException.ThrowIfNull(lineas);
        ArgumentNullException.ThrowIfNull(posiciones);
        return [.. lineas.OrderBy(l => l.Orden).Select(l => l.ALinea(posiciones.Where(p => p.LineaSolicitudId == l.Id || (p.LineaSolicitudId is null && l.Orden == 1))))];
    }

    public async Task<clsResultado<clsCotizacion>> ReintentarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var ahora = reloj.AhoraUtc;
        var posiciones = await cotizaciones.ListarPosicionesAsync(cotizacion.Id, cancellationToken);
        var guardadas = await cotizaciones.ListarLineasAsync(cotizacion.Id, cancellationToken) ?? [];
        var encabezado = cotizacion.EncabezadoSolicitud;
        IReadOnlyList<clsLineaSolicitudCosteo>? lineas = guardadas.Count > 0 && encabezado is not null
            ? LineasConPosiciones(guardadas, posiciones)
            : cotizacion.SolicitudCosteo(posiciones) is { } legado ? [legado.ALinea()] : null;
        if (lineas is null || encabezado is null || !cotizacion.PuedeReenviarCosteo(ahora))
        {
            return NoReintentable(cotizacion);
        }

        // Lo guardado se vuelve a validar (filas anteriores a una regla nueva): nunca se reenvía algo que Nexo rechazaría. Una línea
        // usa la validación del formulario (mismos mensajes que HU-005.5); varias, la de la licitación.
        if (Revalidar(encabezado, lineas) is { } invalida)
        {
            return invalida;
        }

        // Reclamo optimista: de dos reintentos simultáneos solo uno envía (el otro vería otro updatedAt).
        if (!await cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, ahora, cancellationToken))
        {
            return NoReintentable(cotizacion, "otro envío de esta cotización está en curso");
        }

        // Reclamada: desde aquí, sin el token de la petición (ver EnviarANexoAsync).
        await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
        var politica = cotizacion.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, CancellationToken.None) : null;
        await EnviarANexoAsync(cotizacion, encabezado, lineas, guardadas, politica, usuarioId, reintento: true);
        return cotizacion;
    }

    /// <summary>
    /// Valida lo que se va a (re)enviar a Nexo con las reglas de la captura (<c>SOLICITUD_COSTEO_INVALIDA</c>): una línea con la del
    /// formulario (<see cref="clsSolicitudCosteo.Validar"/>), varias con la de la licitación. Lo usan el reintento y la re-solicitud
    /// (HU-013.4), también sobre los cambios de carga de una versión nueva.
    /// </summary>
    public static clsErrorDominio? Revalidar(clsEncabezadoSolicitudCosteo encabezado, IReadOnlyList<clsLineaSolicitudCosteo> lineas)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        ArgumentNullException.ThrowIfNull(lineas);
        if (lineas.Count == 1)
        {
            return clsSolicitudCosteo.Desde(encabezado, lineas[0]).Validar();
        }

        var primero = encabezado.Errores().Select(e => (Linea: (int?)null, Error: e))
            .Concat(lineas.SelectMany((l, i) => l.Errores().Select(e => (Linea: (int?)(i + 1), Error: e))))
            .FirstOrDefault();
        return primero.Error is null
            ? null
            : new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                primero.Linea is { } numero ? $"Línea {numero}: {primero.Error.Mensaje}" : primero.Error.Mensaje, primero.Error.Codigo);
    }

    /// <summary>
    /// Resuelve el BP, llama a Nexo y guarda el resultado, todo con <see cref="CancellationToken.None"/>: si el cliente HTTP se
    /// desconecta cuando Nexo ya recibió el alta, la respuesta (el <c>costeoId</c>) no se pierde y un reintento no duplica la solicitud
    /// en Nexo. El límite lo pone el cliente <c>NexoCosteo</c> (30 s).
    /// </summary>
    private async Task EnviarANexoAsync(
        clsCotizacion cotizacion,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsLineaSolicitudCosteo> lineas,
        IReadOnlyList<clsCotizacionLineaSolicitud> entidades,
        clsMargenPolitica? politica,
        Guid usuarioId,
        bool reintento)
    {
        var cronometro = Stopwatch.StartNew();
        var resultado = await ResolverClienteYEnviarAsync(cotizacion, encabezado, lineas, politica, usuarioId, CancellationToken.None);
        var ahora = reloj.AhoraUtc;
        if (resultado.EsExitoso)
        {
            var creada = resultado.Valor;
            cotizacion.RegistrarEnvioCosteo(creada.Id, creada.CosteoCompleto, ahora, ahora.AddSeconds(opciones.Value.IntervaloInicialSegundos));

            // Cada línea arranca con el estado de la cotización; el polling deriva después el de cada una (ADR-017).
            foreach (var linea in entidades)
            {
                linea.RegistrarEnvio(cotizacion.EstadoCosteo!.Value);
            }
        }
        else
        {
            cotizacion.RegistrarErrorEnvioCosteo(Motivo(resultado.Error), definitivo: EsRechazoDefinitivo(resultado.Error));
        }

        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        if (resultado.EsFallo)
        {
            logger.LogWarning("La cotización {Folio} quedó en error_envio: {Codigo}.", cotizacion.Folio, resultado.Error.Codigo);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, reintento, linea_negocio_nexo = politica?.MapeoLineasNexo, lineas = lineas.Count },
            new
            {
                estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
                costeo_id = cotizacion.CosteoId,
                motivo = cotizacion.MotivoEstadoCosteo,
                error = resultado.EsFallo ? resultado.Error.Codigo : null,
            },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
    }

    private async Task<clsResultado<clsSolicitudCreadaNexo>> ResolverClienteYEnviarAsync(
        clsCotizacion cotizacion,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsLineaSolicitudCosteo> lineas,
        clsMargenPolitica? politica,
        Guid usuarioId,
        CancellationToken cancellationToken)
    {
        var nit = cotizacion.NitCliente;
        if (nit is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, "La cotización no tiene cliente (NIT): no se puede enviar a Nexo.");
        }

        var busqueda = await buscadorClientes.BuscarAsync(usuarioId, nit, cancellationToken);
        if (busqueda.EsFallo)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible,
                "No se pudo resolver el cliente en el catálogo de Nexo: intenta de nuevo más tarde.", busqueda.Error.Codigo);
        }

        var buscado = SoloDigitos(nit);
        var candidato = busqueda.Valor.Candidatos.FirstOrDefault(c => SoloDigitos(c.NitCliente) == buscado);
        if (candidato is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, $"El NIT {nit} no está en el catálogo de clientes de Nexo.");
        }

        cotizacion.ActualizarClienteDelCatalogo(
            candidato.Nombre, clsCodigoEnum.TryDesdeCodigo<OrigenCliente>(candidato.OrigenCliente, out var origen) ? origen : null);
        // G14 (HU-013.10): Nexo exige clienteBp o clienteId, nunca el NIT. Con BP (cliente de SAP) viaja el BP; sin BP (prospecto)
        // viaja el clienteId del catálogo de Nexo si lo trae. Sin ninguno de los dos no se puede crear la solicitud.
        var bp = string.IsNullOrWhiteSpace(candidato.Bp) ? null : candidato.Bp.Trim();
        var clienteId = bp is null && Guid.TryParse(candidato.ClienteId, out var id) && id != Guid.Empty ? id : (Guid?)null;
        if (bp is null && clienteId is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNexoRechazada,
                $"El cliente {candidato.Nombre} no tiene BP ni id en el catálogo de Nexo (prospecto): Nexo exige clienteBp o clienteId para "
                + "crear la solicitud. Pendiente de confirmar con el equipo de Nexo cómo enviar prospectos.");
        }

        await ResolverMargenAsync(cotizacion, politica, bp, cancellationToken);

        var cliente = bp is not null ? clsClienteNexo.PorBp(bp) : clsClienteNexo.PorId(clienteId!.Value);
        return await nexo.EnviarSolicitudAsync(usuarioId, new clsSolicitudNexo(cliente, encabezado, lineas, politica?.MapeoLineasNexo), cancellationToken);
    }

    /// <summary>
    /// Margen de la cotización al enviar (HU-005.7, ADR-015 §5): SAP TM con el BP del catálogo o la política de la línea
    /// (<see cref="IClsMargenResolverService"/>). Se guarda para aplicarlo cuando llegue el costeo; si no se puede resolver, el
    /// polling lo advierte y nunca muestra el costo sin margen. Un margen negociado (re-solicitud, HU-013.4) no se pisa, falle o no la
    /// resolución.
    /// </summary>
    private async Task ResolverMargenAsync(clsCotizacion cotizacion, clsMargenPolitica? politica, string? bp, CancellationToken cancellationToken)
    {
        if (politica is null)
        {
            return;
        }

        var resuelto = await margen.ResolverAsync(new Margen.DTOs.clsEntradaMargen(politica.LineaNegocio, bp), cancellationToken);
        if (resuelto.EsFallo || !clsCodigoEnum.TryDesdeCodigo<OrigenMargen>(resuelto.Valor.Salida.Origen, out var origen))
        {
            logger.LogWarning("No se resolvió el margen de la cotización {Folio} al enviarla a Nexo.", cotizacion.Folio);
            return;
        }

        // HU-013.4 (decisión del 2026-10-06): un margen ajustado por el comercial (negociado) se conserva sobre el costo nuevo; el
        // sugerido se refresca con SAP TM o la política, como referencia.
        if (cotizacion.TieneMargenNegociado)
        {
            cotizacion.RefrescarMargenSugerido(resuelto.Valor.MargenPolitica, resuelto.Valor.Salida.Margen, origen);
            return;
        }

        cotizacion.RegistrarMargenCosteo(resuelto.Valor.MargenPolitica, resuelto.Valor.Salida.Margen, origen, resuelto.Valor.MetodoCalculo);
    }

    /// <summary>
    /// ¿Nexo rechazó los datos del alta (400)? Con los mismos datos Nexo no cambia de opinión: el reintento queda bloqueado. Los
    /// códigos de captura de Nexo se traducen en <c>clsCosteoNexo</c> (G16): catálogo, línea o peso → <c>SOLICITUD_COSTEO_INVALIDA</c>;
    /// otros 400 → <c>COSTEO_NEXO_SOLICITUD_INVALIDA</c>. Los de cliente (<c>CLIENTE_*</c>) llegan como <c>COSTEO_NEXO_RECHAZADA</c> y
    /// se pueden reintentar: el reintento vuelve a resolver el BP en el catálogo de Nexo.
    /// </summary>
    public static bool EsRechazoDefinitivo(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.Codigo is clsCodigosError.CosteoNexoSolicitudInvalida or clsCodigosError.SolicitudCosteoInvalida;
    }

    /// <summary>Motivo legible para <c>motivoEstadoCosteo</c>: el mensaje y, si hay, el detalle (código y mensaje de Nexo).</summary>
    public static string Motivo(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return clsCotizacion.Recortar(string.IsNullOrWhiteSpace(error.Detalle) ? error.Mensaje : $"{error.Mensaje} ({error.Detalle})");
    }

    private static string SoloDigitos(string? valor) => new((valor ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosError.SolicitudCosteoInvalida, mensaje);

    /// <summary>
    /// <c>moneda</c> del formulario (HU-013.7, mismo contrato que <c>LicitacionWeb.moneda</c>): ausente es COP; si viene, <c>COP</c> o
    /// <c>USD</c> sin distinguir mayúsculas ni espacios al borde. Una cadena vacía u otra moneda es <c>SOLICITUD_COSTEO_INVALIDA</c>.
    /// </summary>
    private static clsErrorDominio? ValidarMoneda(string? moneda) =>
        moneda is not null && (string.IsNullOrWhiteSpace(moneda) || clsConversorMoneda.Normalizar(moneda).EsFallo)
            ? Invalida($"El campo 'moneda' debe ser {string.Join(" o ", clsConversorMoneda.Soportadas)} (llegó '{moneda.Trim()}').")
            : null;

    private static clsErrorDominio NoReintentable(clsCotizacion cotizacion, string? detalle = null) =>
        new(clsCodigosError.CosteoNoReintentable,
            $"La cotización {cotizacion.Folio} no se puede reenviar a Nexo en su estado actual "
            + $"({(cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : "sin costeo en Nexo")}).",
            detalle);
}
