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
    /// <c>SOLICITUD_COSTEO_INVALIDA</c>, <c>MARGEN_LINEA_DESCONOCIDA</c>, <c>FOLIO_CONSECUTIVO_AGOTADO</c> y <c>ERROR_INTERNO</c>.
    /// Si Nexo falla la cotización se devuelve igual, en <c>error_envio</c> con el motivo.
    /// </summary>
    Task<clsResultado<clsCotizacion>> EnviarAsync(Guid usuarioId, clsEntradaSolicitudCosteo entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reenvía la solicitud guardada. <c>COTIZACION_NO_ENCONTRADA</c> si no existe o no es del comercial;
    /// <c>COSTEO_NO_REINTENTABLE</c> si no está en <c>error_envio</c> ni en un <c>pendiente_envio</c> abandonado.
    /// </summary>
    Task<clsResultado<clsCotizacion>> ReintentarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsEnvioCosteoService"/>.</summary>
/// <remarks>
/// <list type="number">
/// <item>Valida el formulario (mismos límites que Nexo), exige el NIT y resuelve la línea en <c>MargenesPolitica</c>.</item>
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
                        : entrada.Solicitud.Validar();
        if (error is not null)
        {
            return error;
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
            });
            if (cotizacion.EsFallo)
            {
                return cotizacion.Error;
            }

            cotizacion.Valor.RegistrarSolicitudCosteo(entrada.Solicitud);
            cotizaciones.Agregar(cotizacion.Valor);
            cotizaciones.AgregarPosiciones(clsCotizacionPosicion.Desde(cotizacion.Valor.Id, entrada.Solicitud.Posiciones));
            await unidadTrabajo.GuardarCambiosAsync(ct);
            return cotizacion.Valor;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return creada.Error;
        }

        // Desde aquí, sin el token de la petición (ver EnviarANexoAsync).
        await EnviarANexoAsync(creada.Valor, entrada.Solicitud, politica, usuarioId, reintento: false);
        return creada.Valor;
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
        var solicitud = cotizacion.SolicitudCosteo(await cotizaciones.ListarPosicionesAsync(cotizacion.Id, cancellationToken));
        if (solicitud is null || !cotizacion.PuedeReenviarCosteo(ahora))
        {
            return NoReintentable(cotizacion);
        }

        // Lo guardado se vuelve a validar (filas anteriores a una regla nueva): nunca se reenvía algo que Nexo rechazaría.
        if (solicitud.Validar() is { } invalida)
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
        await EnviarANexoAsync(cotizacion, solicitud, politica, usuarioId, reintento: true);
        return cotizacion;
    }

    /// <summary>
    /// Resuelve el BP, llama a Nexo y guarda el resultado, todo con <see cref="CancellationToken.None"/>: si el cliente HTTP se
    /// desconecta cuando Nexo ya recibió el alta, la respuesta (el <c>costeoId</c>) no se pierde y un reintento no duplica la solicitud
    /// en Nexo. El límite lo pone el cliente <c>NexoCosteo</c> (30 s).
    /// </summary>
    private async Task EnviarANexoAsync(clsCotizacion cotizacion, clsSolicitudCosteo solicitud, clsMargenPolitica? politica, Guid usuarioId, bool reintento)
    {
        var cronometro = Stopwatch.StartNew();
        var resultado = await ResolverClienteYEnviarAsync(cotizacion, solicitud, politica, usuarioId, CancellationToken.None);
        var ahora = reloj.AhoraUtc;
        if (resultado.EsExitoso)
        {
            var creada = resultado.Valor;
            cotizacion.RegistrarEnvioCosteo(creada.Id, creada.CosteoCompleto, ahora, ahora.AddSeconds(opciones.Value.IntervaloInicialSegundos));
        }
        else
        {
            cotizacion.RegistrarErrorEnvioCosteo(Motivo(resultado.Error), definitivo: resultado.Error.Codigo == clsCodigosError.CosteoNexoSolicitudInvalida);
        }

        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        if (resultado.EsFallo)
        {
            logger.LogWarning("La cotización {Folio} quedó en error_envio: {Codigo}.", cotizacion.Folio, resultado.Error.Codigo);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new { folio = cotizacion.Folio, reintento, linea_negocio_nexo = politica?.MapeoLineasNexo },
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
        clsCotizacion cotizacion, clsSolicitudCosteo solicitud, clsMargenPolitica? politica, Guid usuarioId, CancellationToken cancellationToken)
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
        if (string.IsNullOrWhiteSpace(candidato.Bp))
        {
            return new clsErrorDominio(clsCodigosError.CosteoNexoRechazada,
                $"El cliente {candidato.Nombre} no tiene BP en Nexo (prospecto): Nexo exige clienteBp o clienteId para crear la solicitud. "
                + "Pendiente de confirmar con el equipo de Nexo cómo enviar prospectos.");
        }

        return await nexo.EnviarSolicitudAsync(usuarioId, new clsSolicitudNexo(candidato.Bp.Trim(), solicitud, politica?.MapeoLineasNexo), cancellationToken);
    }

    /// <summary>Motivo legible para <c>motivoEstadoCosteo</c>: el mensaje y, si hay, el detalle (código y mensaje de Nexo).</summary>
    public static string Motivo(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return clsCotizacion.Recortar(string.IsNullOrWhiteSpace(error.Detalle) ? error.Mensaje : $"{error.Mensaje} ({error.Detalle})");
    }

    private static string SoloDigitos(string? valor) => new((valor ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosError.SolicitudCosteoInvalida, mensaje);

    private static clsErrorDominio NoReintentable(clsCotizacion cotizacion, string? detalle = null) =>
        new(clsCodigosError.CosteoNoReintentable,
            $"La cotización {cotizacion.Folio} no se puede reenviar a Nexo en su estado actual "
            + $"({(cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : "sin costeo en Nexo")}).",
            detalle);
}
