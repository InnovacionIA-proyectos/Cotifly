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
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// HU-028 (#186, ADR-021): el comercial edita la solicitud. <c>GET /web/cotizaciones/{folio}/solicitud</c> (el modelo editable) y
/// <c>PUT /web/cotizaciones/{folio}/solicitud</c> (la edición, con <c>If-Match</c> e <c>Idempotency-Key</c>).
/// </summary>
public interface IClsEdicionSolicitudService
{
    /// <summary>
    /// El modelo editable de la solicitud (rutas con mercancías, conexos, condiciones y municipios por id y nombre), los campos bloqueados y
    /// cómo se aplicaría la edición ahora (modo, motivo si no se permite y avisos). <c>COTIZACION_NO_ENCONTRADA</c> si no existe o no es suya.
    /// </summary>
    Task<clsResultado<clsSolicitudEditable>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Aplica la edición según el modo del estado: <c>en_sitio</c>, <c>reemplazo</c> (rechaza la solicitud anterior en Nexo y captura de
    /// nuevo en el mismo folio) o <c>nueva_version</c> (<c>-vN</c>; la original no cambia). Errores: 404; 400 <c>CAMPO_NO_EDITABLE</c> o
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> (con <c>detalle.errores</c>); 409 <c>CONFLICTO_CONCURRENCIA</c>, <c>ESTADO_NO_PERMITE</c> o
    /// <c>IDEMPOTENCY_KEY_REUTILIZADA</c>; 503 si un catálogo, el cubicador, la TRM o el rechazo en Nexo no responden (nada se capturó).
    /// Si la captura en Nexo falla, la cotización queda en <c>error_envio</c> y se responde igual.
    /// </summary>
    Task<clsResultado<clsEdicionAplicada>> EditarAsync(
        Guid usuarioId, string folio, clsEntradaEdicionSolicitud entrada, int versionEsperada, string? claveIdempotencia,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementación de <see cref="IClsEdicionSolicitudService"/>. Reutiliza la validación y la preparación de la captura
/// (<see cref="IClsCapturaRutasService"/>: catálogos de Nexo, municipios, mercancías del cubicador, conexos y el planificador de camiones
/// con el cupo del cubicador) y el envío normalizado (<see cref="IClsEnvioCosteoService.EnviarGuardadaAsync"/>).
/// <list type="bullet">
/// <item>Todo lo que puede fallar se valida ANTES de escribir: <c>If-Match</c>, modo, campos bloqueados, reglas de la captura,
/// catálogos, TRM y la cubicación de las rutas que cambian. Un 400, un 409 o un 503 no cambian nada.</item>
/// <item>Solo se vuelve a cubicar una ruta nueva o una cuya carga (peso, mercancías) o clase de viaje cambió
/// (<see cref="clsReglaRecubicacion"/>); las demás conservan sus camiones vigentes, también el vehículo que la Mesa de Costeo haya
/// cambiado en Nexo (D2: la Mesa manda).</item>
/// <item><b>Reemplazo</b>: se reserva la versión (con la instantánea del costeo anterior en la auditoría, sin <c>ThreadId</c>), se rechaza
/// la solicitud anterior en Nexo y solo si quedó sin vida (204, 409 ya descartada o 404) se aplica la edición y se captura de nuevo. Si el
/// rechazo falla, no se captura (nunca quedan dos solicitudes vivas en Nexo) y se responde 503 (o 403 sin permiso).</item>
/// <item>Las rutas quitadas desvinculan sus ajustes de margen (<c>MargenAjustes.lineaSolicitudId = NULL</c>).</item>
/// </list>
/// </summary>
public sealed class clsEdicionSolicitudService(
    IClsCotizacionRepository cotizaciones,
    IClsMargenPoliticaRepository politicas,
    IClsMargenAjusteRepository ajustes,
    IClsCapturaRutasService capturaRutas,
    IClsGeneradorFolioService folios,
    IClsEnvioCosteoService envio,
    IClsCosteoNexo nexo,
    IClsFijacionMonedaService monedas,
    IClsMisCotizacionesService misCotizaciones,
    IClsResolutorConexosCatalogo resolutorConexos,
    IClsAlmacenIdempotencia idempotencia,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaRepository registroAuditoria,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    ILogger<clsEdicionSolicitudService> logger) : IClsEdicionSolicitudService
{
    /// <summary>Herramienta de la auditoría (<c>EventosHerramienta.herramienta</c>).</summary>
    public const string HerramientaAuditoria = "editar_solicitud";

    /// <summary>Ámbito de las claves de idempotencia (con el comercial).</summary>
    public const string OperacionIdempotencia = "editar_solicitud";

    /// <summary>Motivo (1-100) del rechazo de la solicitud anterior en Nexo.</summary>
    public const string MotivoRechazoNexo = "Edición de la solicitud desde Cotifly";

    /// <summary>Motivo de <c>ESTADO_NO_PERMITE</c>: el modo cambió desde que se leyó la solicitud (<c>modo_esperado</c>).</summary>
    public const string MotivoModoCambio = "MODO_CAMBIO";

    /// <summary><c>detalle.motivo</c> del 503 cuando el rechazo en Nexo falló: la edición no se aplicó.</summary>
    public const string MotivoRechazoFallido = "RECHAZO_NEXO_FALLIDO";

    /// <summary>Código de <c>detalle.errores</c>: la ruta indicada viene dos veces.</summary>
    public const string CodigoRutaRepetida = "RUTA_REPETIDA";

    /// <summary>Código de <c>detalle.errores</c>: las rutas que se conservan cambiaron su orden relativo (no se reordenan).</summary>
    public const string CodigoRutasReordenadas = "RUTAS_REORDENADAS";

    private static readonly JsonSerializerOptions _jsonHuella = new(JsonSerializerDefaults.Web);

    public async Task<clsResultado<clsSolicitudEditable>> ObtenerAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = await CargarAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var politicasRuta = await PoliticasAsync(cotizacion, cancellationToken);
        var catalogo = await ConexosDelCatalogoAsync(usuarioId, cotizacion, cancellationToken);
        var modo = cotizacion.ModoEdicion(reloj.AhoraUtc);
        var edicion = modo.EsExitoso
            ? new clsEstadoEdicion(clsCodigoEnum.ACodigo(modo.Valor), true, null, null, Avisos(cotizacion, modo.Valor))
            : new clsEstadoEdicion(null, false, modo.Error.Detalle, modo.Error.Mensaje, []);
        var rutas = cotizacion.RutasOrdenadas.Select(r => Editable(r, politicasRuta, catalogo)).ToList();
        return new clsSolicitudEditable(
            cotizacion.Folio,
            cotizacion.Version,
            cotizacion.VersionNegocio,
            new clsBloqueadosSolicitud(
                cotizacion.TipoSolicitud is { } tipo ? clsCodigoEnum.ACodigo(tipo) : null,
                cotizacion.NitCliente,
                cotizacion.ClienteNombre,
                cotizacion.OrigenCliente is { } origen ? clsCodigoEnum.ACodigo(origen) : null),
            ["tipo", "nit_cliente", "cliente_nombre", "origen_cliente", "rutas[].linea_negocio"],
            edicion,
            cotizacion.Deadline,
            cotizacion.Notas,
            cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos,
            rutas.FirstOrDefault()?.LineaNegocio,
            [.. politicasRuta.Values.OfType<clsMargenPolitica>().Select(p => p.LineaNegocio).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)],
            rutas);
    }

    public Task<clsResultado<clsEdicionAplicada>> EditarAsync(
        Guid usuarioId, string folio, clsEntradaEdicionSolicitud entrada, int versionEsperada, string? claveIdempotencia,
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
    /// Qué consume la <c>Idempotency-Key</c>: las ediciones aplicadas sin <c>error_envio</c> (como la re-solicitud). Un error (también el
    /// rechazo fallido en Nexo) o un 200 en <c>error_envio</c> no la consumen: repetir con la misma clave vuelve a ejecutar.
    /// </summary>
    public static bool SeGuarda(clsResultado<clsEdicionAplicada> resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        return resultado.EsExitoso && resultado.Valor.Respuesta.Cotizacion.EstadoCosteo != "error_envio";
    }

    /// <summary>Huella de la petición para la idempotencia: el folio y el cuerpo completo (SHA-256).</summary>
    public static string Huella(string folio, clsEntradaEdicionSolicitud entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        var cuerpo = JsonSerializer.Serialize(new { folio = folio?.Trim(), entrada }, _jsonHuella);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cuerpo)));
    }

    /// <summary>Nota de trazabilidad que viaja en <c>notas</c> de la captura (no se guarda en la cotización).</summary>
    public static string NotaTrazabilidad(string folio, string? solicitudAnterior, bool versionNueva) =>
        versionNueva ? $"Versión editada de {folio}."
        : solicitudAnterior is null ? $"Edición de {folio}."
        : $"Edición de {folio}; reemplaza la solicitud {solicitudAnterior}.";

    private async Task<clsResultado<clsEdicionAplicada>> EjecutarAsync(
        Guid usuarioId, string folio, clsEntradaEdicionSolicitud entrada, int versionEsperada, CancellationToken cancellationToken)
    {
        var cronometro = Stopwatch.StartNew();
        var cotizacion = await CargarAsync(usuarioId, folio, cancellationToken);
        if (cotizacion is null)
        {
            return NoEncontrada();
        }

        var entradaAuditoria = new
        {
            folio = cotizacion.Folio,
            usuario_id = usuarioId,
            motivo = Limpio(entrada.Motivo),
            rutas = entrada.Rutas?.Count ?? 0,
            modo_esperado = entrada.ModoEsperado,
        };
        var resultado = await EditarAsync(cotizacion, usuarioId, entrada, versionEsperada, entradaAuditoria, cronometro, cancellationToken);
        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            entradaAuditoria,
            resultado.EsExitoso
                ? new
                {
                    fase = "resultado",
                    modo = resultado.Valor.Respuesta.Modo,
                    folio_origen = cotizacion.Folio,
                    folio_resultante = resultado.Valor.Respuesta.Folio,
                    sin_cambios = resultado.Valor.Respuesta.SinCambios,
                    estado_costeo = resultado.Valor.Respuesta.Cotizacion.EstadoCosteo,
                    rutas_replanificadas = resultado.Valor.Respuesta.RutasReplanificadas,
                    rutas_agregadas = resultado.Valor.Respuesta.RutasAgregadas,
                    rutas_quitadas = resultado.Valor.Respuesta.RutasQuitadas,
                    solicitud_nexo_reemplazada = resultado.Valor.Respuesta.SolicitudNexoReemplazada,
                }
                : new { fase = "rechazada", error = new { codigo = resultado.Error.Codigo, mensaje = resultado.Error.Mensaje } },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            threadId: null);
        return resultado;
    }

    private async Task<clsResultado<clsEdicionAplicada>> EditarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsEntradaEdicionSolicitud entrada, int versionEsperada, object entradaAuditoria,
        Stopwatch cronometro, CancellationToken cancellationToken)
    {
        if (!cotizacion.TieneVersion(versionEsperada))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        var modo = cotizacion.ModoEdicion(reloj.AhoraUtc);
        if (modo.EsFallo)
        {
            return modo.Error;
        }

        if (entrada.ModoEsperado is { } esperado && !string.Equals(esperado.Trim(), clsCodigoEnum.ACodigo(modo.Valor), StringComparison.OrdinalIgnoreCase))
        {
            return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"La solicitud {cotizacion.Folio} ahora se edita como '{clsCodigoEnum.ACodigo(modo.Valor)}' (no '{esperado.Trim()}'): recárgala y "
                + "confirma de nuevo.", MotivoModoCambio);
        }

        if (Limpio(entrada.Motivo) is { Length: > clsEntradaEdicionSolicitud.LargoMaximoMotivo })
        {
            return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida,
                $"El campo 'motivo' admite hasta {clsEntradaEdicionSolicitud.LargoMaximoMotivo} caracteres.");
        }

        var preparada = await PrepararAsync(cotizacion, usuarioId, entrada, modo.Valor, cancellationToken);
        if (preparada.EsFallo)
        {
            return preparada.Error;
        }

        var p = preparada.Valor;
        if (p.SinCambios)
        {
            return new clsEdicionAplicada(
                await RespuestaAsync(cotizacion, cotizacion, modo.Valor, sinCambios: true, [new clsAvisoEdicion(clsAvisoEdicion.SinCambios,
                    "La solicitud no cambió: no se envió nada a Nexo ni se invalidó el PDF.")], [], [], [], null),
                cotizacion.VersionNegocio.ToString(CultureInfo.InvariantCulture), Creada: false);
        }

        var reserva = Reserva(entradaAuditoria, clsReSolicitudCosteoService.Instantanea(cotizacion), cronometro);
        return modo.Valor switch
        {
            ModoEdicionSolicitud.EnSitio => await EnSitioAsync(cotizacion, usuarioId, p, versionEsperada, reserva),
            ModoEdicionSolicitud.Reemplazo => await ReemplazoAsync(cotizacion, usuarioId, p, versionEsperada, reserva, Limpio(entrada.Motivo)),
            _ => await VersionNuevaAsync(cotizacion, usuarioId, p, versionEsperada, reserva, cancellationToken),
        };
    }

    // ------------------------------------------------------------------ Validación y preparación (antes de escribir)

    /// <summary>
    /// Lo que dejó lista la preparación: el encabezado, las rutas (cada una con la ruta guardada que edita, o <c>null</c>), si cambió algo,
    /// la moneda fijada (si cambió o es una versión nueva), la política de la ruta 1 y el orden de las rutas que se cubicaron.
    /// </summary>
    private sealed record clsEdicionPreparada(
        clsEncabezadoSolicitudCosteo Encabezado,
        IReadOnlyList<clsRutaEditada> Rutas,
        bool SinCambios,
        clsMonedaFijada? Moneda,
        clsMargenPolitica? Politica,
        IReadOnlyList<int> Replanificadas);

    private async Task<clsResultado<clsEdicionPreparada>> PrepararAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsEntradaEdicionSolicitud entrada, ModoEdicionSolicitud modo, CancellationToken cancellationToken)
    {
        var entradas = entrada.Rutas ?? [];
        var guardadas = cotizacion.RutasOrdenadas.ToDictionary(r => r.Id);
        var politicasRuta = await PoliticasAsync(cotizacion, cancellationToken);
        var porNombre = politicasRuta.Values.OfType<clsMargenPolitica>()
            .GroupBy(p => p.LineaNegocio, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // 1. Campos bloqueados: el cliente, el tipo y la línea de negocio de cada ruta (CAMPO_NO_EDITABLE, antes que cualquier otra regla).
        var bloqueados = new List<clsErrorFilaLicitacion>(cotizacion.CamposBloqueadosCambiados(
                clsCodigoEnum.TryDesdeCodigo<TipoSolicitud>(entrada.Tipo, out var tipo) ? tipo : null,
                entrada.NitCliente,
                entrada.ClienteNombre,
                clsCodigoEnum.TryDesdeCodigo<OrigenCliente>(entrada.OrigenCliente, out var origen) ? origen : null)
            .Select(e => new clsErrorFilaLicitacion(null, null, e.Campo, e.Codigo, e.Mensaje)));
        if (entrada.Tipo is not null && !clsCodigoEnum.TryDesdeCodigo<TipoSolicitud>(entrada.Tipo, out _))
        {
            bloqueados.Add(new clsErrorFilaLicitacion(null, null, "tipo", clsCodigosError.CampoNoEditable,
                "El tipo de la solicitud (spot o licitación) no se puede cambiar al editarla."));
        }

        if (entrada.OrigenCliente is not null && !clsCodigoEnum.TryDesdeCodigo<OrigenCliente>(entrada.OrigenCliente, out _))
        {
            bloqueados.Add(new clsErrorFilaLicitacion(null, null, "origen_cliente", clsCodigosError.CampoNoEditable,
                "El origen del cliente (cliente o prospecto) no se puede cambiar al editar la solicitud."));
        }

        // 2. Cada ruta: la guardada que edita (existe, no se repite y no se reordena) y su línea de negocio (la propia, o una de la solicitud).
        var errores = new List<clsErrorFilaLicitacion>();
        var lineas = new int?[entradas.Count];
        var rutasCaptura = new List<clsLineaLicitacionEntrada?>(entradas.Count);
        var vistos = new HashSet<Guid>();
        var ordenAnterior = 0;
        var disponibles = string.Join(", ", porNombre.Keys.Order(StringComparer.Ordinal));
        for (var i = 0; i < entradas.Count; i++)
        {
            var e = entradas[i];
            if (e?.Ruta is not { } ruta)
            {
                rutasCaptura.Add(null);
                continue;
            }

            var pedida = Limpio(ruta.LineaNegocio);
            if (e.LineaSolicitudId is { } id)
            {
                if (!guardadas.TryGetValue(id, out var guardada))
                {
                    errores.Add(new clsErrorFilaLicitacion(i, null, "linea_solicitud_id", clsCodigosError.RutaInexistente,
                        $"La ruta {id} no es de la cotización {cotizacion.Folio}: quita linea_solicitud_id para agregarla como ruta nueva."));
                    rutasCaptura.Add(ruta);
                    continue;
                }

                if (!vistos.Add(id))
                {
                    errores.Add(new clsErrorFilaLicitacion(i, null, "linea_solicitud_id", CodigoRutaRepetida, $"La ruta {id} viene repetida."));
                }

                if (guardada.Orden < ordenAnterior)
                {
                    errores.Add(new clsErrorFilaLicitacion(i, null, "linea_solicitud_id", CodigoRutasReordenadas,
                        "Las rutas que se conservan no se reordenan: déjalas en su orden (puedes quitar rutas y agregar nuevas en cualquier posición)."));
                }

                ordenAnterior = Math.Max(ordenAnterior, guardada.Orden);
                if (guardada.LineaNegocioId is null)
                {
                    // Ruta anterior a HU-021 sin línea de negocio guardada: la línea no se edita, así que no hay con qué enviarla.
                    errores.Add(new clsErrorFilaLicitacion(i, null, "linea_negocio", clsCodigosLineaSolicitud.CampoRequerido,
                        $"La ruta {guardada.Orden} no tiene línea de negocio guardada y la línea no se edita: crea una solicitud nueva para esta ruta."));
                    rutasCaptura.Add(ruta);
                    continue;
                }

                var propia = politicasRuta.GetValueOrDefault(guardada.LineaNegocioId.Value);
                if (pedida is not null && propia is not null && !string.Equals(pedida, propia.LineaNegocio, StringComparison.OrdinalIgnoreCase))
                {
                    bloqueados.Add(new clsErrorFilaLicitacion(i, null, $"rutas[{i}].linea_negocio", clsCodigosError.CampoNoEditable,
                        $"La línea de negocio de una ruta no se cambia: la ruta {guardada.Orden} es '{propia.LineaNegocio}'."));
                }

                lineas[i] = guardada.LineaNegocioId;
                rutasCaptura.Add(ruta with { LineaNegocio = propia?.LineaNegocio ?? pedida });
                continue;
            }

            pedida ??= Limpio(entrada.LineaNegocio) ?? (porNombre.Count == 1 ? porNombre.Keys.First() : null);
            if (pedida is null)
            {
                errores.Add(new clsErrorFilaLicitacion(i, null, "linea_negocio", clsCodigosLineaSolicitud.CampoRequerido,
                    $"Una ruta nueva debe traer su linea_negocio, una de la solicitud: {disponibles}."));
                rutasCaptura.Add(ruta);
                continue;
            }

            if (!porNombre.TryGetValue(pedida, out var deLaSolicitud))
            {
                bloqueados.Add(new clsErrorFilaLicitacion(i, null, $"rutas[{i}].linea_negocio", clsCodigosError.CampoNoEditable,
                    $"Una ruta nueva solo puede usar una línea de negocio que ya exista en la solicitud ({disponibles}); '{pedida}' no está."));
                rutasCaptura.Add(ruta);
                continue;
            }

            lineas[i] = deLaSolicitud.Id;
            rutasCaptura.Add(ruta with { LineaNegocio = deLaSolicitud.LineaNegocio });
        }

        if (bloqueados.Count > 0)
        {
            return CampoNoEditable(bloqueados);
        }

        // 3. Las reglas de la captura (forma, límites de Nexo, catálogos, municipios, mercancías del cubicador y conexos), todas juntas.
        var encabezado = new clsEncabezadoSolicitudCosteo
        {
            Tipo = cotizacion.TipoSolicitud ?? TipoSolicitud.Spot,
            Deadline = entrada.Deadline,
            HorasCargue = entrada.HorasCargue,
            HorasDescargue = entrada.HorasDescargue,
            Notas = entrada.Notas,
        };
        var moneda = (Limpio(entrada.Moneda) ?? cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos).ToUpperInvariant();
        var sinVehiculo = clsEnvioCosteoService.SinVehiculo(rutasCaptura, logger);
        var captura = new clsEntradaCaptura(
            cotizacion.NitCliente, cotizacion.ClienteNombre, sinVehiculo.FirstOrDefault(r => r?.LineaNegocio is not null)?.LineaNegocio ?? "-",
            encabezado, sinVehiculo, entrada.Moneda);
        var previos = clsEnvioCosteoService.ErroresFormulario(captura, sinVehiculo).Concat(errores).ToList();
        var validadas = await capturaRutas.ValidarAsync(usuarioId, sinVehiculo, previos, cancellationToken);
        if (validadas.Errores.Count > 0)
        {
            return clsEnvioCosteoService.ErrorCaptura(validadas.Errores, validadas.NoDisponible?.Codigo, sinVehiculo.Count);
        }

        if (validadas.NoDisponible is { } noDisponible)
        {
            return noDisponible;
        }

        foreach (var lineaId in lineas.OfType<int>().Distinct())
        {
            if (politicasRuta.GetValueOrDefault(lineaId) is { Activa: false } inactiva)
            {
                return clsMargenResolverService.LineaInactiva(inactiva.LineaNegocio);
            }
        }

        // 4. ¿Cambió algo? Sin cambios no se toca nada (ni Nexo, ni el PDF, ni la versión). Un conexo agregado en Nexo está guardado con el
        // id de la fila de Nexo y el GET lo muestra con el del catálogo: se comparan por el id del catálogo (solo se consulta si hace falta).
        var pedidos = validadas.Lineas.SelectMany(l => l.Conexos ?? []).Select(c => Limpio(c?.IdServicioConexo)).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogoGuardados = cotizacion.Rutas.SelectMany(r => r.Conexos).All(c => pedidos.Contains(c.IdServicioConexo))
            ? new Dictionary<string, clsCostoConexoNexo>()
            : await ConexosDelCatalogoAsync(usuarioId, cotizacion, cancellationToken);
        if (MismoContenido(cotizacion, encabezado, moneda, entradas, validadas.Lineas, catalogoGuardados))
        {
            return new clsEdicionPreparada(encabezado, [], true, null, null, []);
        }

        // 5. La moneda: la TRM del día si cambia o si es una versión nueva (sin TRM no se escribe nada).
        clsMonedaFijada? fijada = null;
        if (modo == ModoEdicionSolicitud.NuevaVersion || !string.Equals(moneda, cotizacion.Moneda, StringComparison.OrdinalIgnoreCase))
        {
            var resultadoMoneda = await monedas.FijarAsync(moneda, cancellationToken);
            if (resultadoMoneda.EsFallo)
            {
                return resultadoMoneda.Error;
            }

            fijada = resultadoMoneda.Valor;
        }

        // 6. Los camiones: se vuelve a cubicar una ruta nueva o una cuya carga o clase de viaje cambió (defecto C); las demás conservan los
        // vigentes. En la -vN cada ruta es otra (id nuevo).
        var porPreparar = new List<clsRutaPorPreparar>(validadas.Lineas.Count);
        var originales = new List<clsLineaSolicitud?>(validadas.Lineas.Count);
        var replanificadas = new List<int>();
        for (var i = 0; i < validadas.Lineas.Count; i++)
        {
            var validada = validadas.Lineas[i];
            var guardada = entradas[i]!.LineaSolicitudId is { } id ? guardadas[id] : null;
            var rutaId = modo == ModoEdicionSolicitud.NuevaVersion || guardada is null ? Guid.NewGuid() : guardada.Id;
            var vigente = guardada is not null && guardada.VehiculosQueCuentan.Count > 0 && !clsReglaRecubicacion.Exige(guardada.ALinea(), validada.Linea)
                ? PlanVigente(guardada)
                : null;
            if (vigente is null)
            {
                replanificadas.Add(i + 1);
            }

            porPreparar.Add(new clsRutaPorPreparar(validada, lineas[i]!.Value, i, rutaId, vigente));
            originales.Add(guardada);
        }

        var preparadas = await capturaRutas.PrepararAsync(usuarioId, porPreparar, cancellationToken: cancellationToken);
        if (preparadas.Error is { } errorPreparacion)
        {
            return errorPreparacion;
        }

        if (preparadas.Errores.Count > 0)
        {
            return clsEnvioCosteoService.ErrorCaptura(preparadas.Errores, null, porPreparar.Count);
        }

        var rutas = preparadas.Rutas.Select((r, i) => new clsRutaEditada(originales[i]?.Id, r, Replanificada: replanificadas.Contains(i + 1))).ToList();
        var politica = lineas[0] is { } primera ? politicasRuta.GetValueOrDefault(primera) : null;
        return new clsEdicionPreparada(encabezado, rutas, false, fijada, politica, replanificadas);
    }

    // ------------------------------------------------------------------ Modos

    /// <summary>En sitio: nunca llegó a Nexo. Se aplica, se guarda con el control de versión y se envía.</summary>
    private async Task<clsResultado<clsEdicionAplicada>> EnSitioAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsEdicionPreparada p, int versionEsperada, clsEventoHerramienta? reserva)
    {
        if (reserva is not null)
        {
            registroAuditoria.Registrar(reserva);
        }

        var aplicada = await AplicarYGuardarAsync(cotizacion, usuarioId, p, versionEsperada);
        if (aplicada is null)
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        // Confirmada: desde aquí, sin el token de la petición (como el alta, ADR-015 §3).
        await envio.EnviarGuardadaAsync(cotizacion, p.Politica, usuarioId, NotaTrazabilidad(cotizacion.Folio, null, versionNueva: false));
        var avisos = new List<clsAvisoEdicion>(aplicada.Value.Avisos);
        return new clsEdicionAplicada(
            await RespuestaAsync(cotizacion, cotizacion, ModoEdicionSolicitud.EnSitio, false, avisos, p.Replanificadas,
                aplicada.Value.Resultado.RutasAgregadas, [.. aplicada.Value.Resultado.RutasQuitadas.Select(r => r.Id)], null),
            cotizacion.VersionNegocio.ToString(CultureInfo.InvariantCulture), Creada: false);
    }

    /// <summary>
    /// Reemplazo en el mismo folio: reserva la versión (con la instantánea), rechaza la solicitud anterior en Nexo y solo si quedó sin vida
    /// aplica la edición, invalida el PDF y captura de nuevo. Si el polling guardó entretanto (por ejemplo, vio la solicitud descartada), se
    /// recarga y se aplica una vez más mientras siga siendo la misma solicitud.
    /// </summary>
    private async Task<clsResultado<clsEdicionAplicada>> ReemplazoAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsEdicionPreparada p, int versionEsperada, clsEventoHerramienta? reserva, string? motivo)
    {
        var anterior = cotizacion.SolicitudNexoId!;
        var avisos = Avisos(cotizacion, ModoEdicionSolicitud.Reemplazo).Where(a => a.Codigo != clsAvisoEdicion.InvalidaPdf).ToList();
        if (!await ReservarAsync(cotizacion, versionEsperada, reserva))
        {
            return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
        }

        var comentario = $"Reemplazada por la edición de {cotizacion.Folio} en Cotifly." + (motivo is null ? string.Empty : $" {motivo}");
        var rechazo = await nexo.RechazarSolicitudAsync(usuarioId, anterior, MotivoRechazoNexo, comentario, CancellationToken.None);
        if (rechazo.EsFallo)
        {
            logger.LogWarning("No se pudo rechazar en Nexo la solicitud {Solicitud} de {Folio} ({Codigo}): la edición no se aplica ni se captura otra.",
                anterior, cotizacion.Folio, rechazo.Error.Codigo);
            return RechazoFallido(cotizacion, anterior, rechazo.Error);
        }

        var version = cotizacion.VersionNegocio;
        var aplicada = await AplicarYGuardarAsync(cotizacion, usuarioId, p, version);
        if (aplicada is null)
        {
            // El polling (u otra escritura) guardó entre la reserva y ahora. Se aplica otra vez sobre lo guardado si sigue siendo la misma
            // solicitud y se puede reemplazar; si no, conflicto (la anterior ya está descartada en Nexo: el comercial vuelve a editar).
            await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
            if (cotizacion.SolicitudNexoId != anterior || cotizacion.ModoEdicion(reloj.AhoraUtc) is not { EsExitoso: true, Valor: ModoEdicionSolicitud.Reemplazo })
            {
                return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
            }

            aplicada = await AplicarYGuardarAsync(cotizacion, usuarioId, p, cotizacion.VersionNegocio);
            if (aplicada is null)
            {
                return clsReglaPdfCosteo.Conflicto(cotizacion.Folio);
            }
        }

        await envio.EnviarGuardadaAsync(cotizacion, p.Politica, usuarioId, NotaTrazabilidad(cotizacion.Folio, anterior, versionNueva: false));
        avisos.AddRange(aplicada.Value.Avisos);
        return new clsEdicionAplicada(
            await RespuestaAsync(cotizacion, cotizacion, ModoEdicionSolicitud.Reemplazo, false, avisos, p.Replanificadas,
                aplicada.Value.Resultado.RutasAgregadas, [.. aplicada.Value.Resultado.RutasQuitadas.Select(r => r.Id)], clsCodigoEnum.ACodigo(rechazo.Valor)),
            cotizacion.VersionNegocio.ToString(CultureInfo.InvariantCulture), Creada: false);
    }

    /// <summary>
    /// Versión nueva (<c>-vN</c>): reserva la versión de la original (la original no cambia nada más ni se rechaza en Nexo: es la que tiene
    /// el cliente), crea la versión con las rutas editadas y el margen negociado de las que conserva, y la envía.
    /// </summary>
    private async Task<clsResultado<clsEdicionAplicada>> VersionNuevaAsync(
        clsCotizacionSolicitud original, Guid usuarioId, clsEdicionPreparada p, int versionEsperada, clsEventoHerramienta? reserva,
        CancellationToken cancellationToken)
    {
        if (!await ReservarAsync(original, versionEsperada, reserva))
        {
            return clsReglaPdfCosteo.Conflicto(original.Folio);
        }

        var moneda = p.Moneda ?? new clsMonedaFijada(original.Moneda ?? clsCotizacionSolicitud.MonedaPesos, original.TrmAplicada);
        var anteriorDe = p.Rutas.Where(r => r.RutaExistenteId is not null).ToDictionary(r => r.Ruta.RutaId!.Value, r => r.RutaExistenteId!.Value);
        var creada = await folios.CrearNuevaVersionAsync<clsCotizacionSolicitud>(usuarioId, original.Id, async (folio, ct) =>
        {
            var version = original.CrearVersionEditada(folio, moneda.Moneda, moneda.TrmAplicada);
            if (version.EsFallo)
            {
                return version.Error;
            }

            var nueva = version.Valor;
            nueva.RegistrarCaptura(p.Encabezado, [.. p.Rutas.Select(r => r.Ruta)]);
            nueva.CopiarMargenesNegociadosDe(original, anteriorDe);
            cotizaciones.Agregar(nueva);
            await unidadTrabajo.GuardarCambiosAsync(ct);
            return nueva;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return creada.Error;
        }

        await envio.EnviarGuardadaAsync(creada.Valor, p.Politica, usuarioId, NotaTrazabilidad(original.Folio, null, versionNueva: true));
        var nuevas = p.Rutas.Select((r, i) => (r, i)).Where(x => x.r.RutaExistenteId is null).Select(x => x.i + 1).ToList();
        var conservadas = p.Rutas.Select(r => r.RutaExistenteId).OfType<Guid>().ToHashSet();
        return new clsEdicionAplicada(
            await RespuestaAsync(creada.Valor, original, ModoEdicionSolicitud.NuevaVersion, false, Avisos(original, ModoEdicionSolicitud.NuevaVersion),
                p.Replanificadas, nuevas, [.. original.RutasOrdenadas.Where(r => !conservadas.Contains(r.Id)).Select(r => r.Id)], null),
            creada.Valor.VersionNegocio.ToString(CultureInfo.InvariantCulture), Creada: true);
    }

    /// <summary>
    /// Aplica la edición en el agregado, desvincula los ajustes de margen de las rutas quitadas, invalida el PDF (motivo
    /// <c>re_solicitud</c>, sin script de BD), sube la versión y guarda con el control de versión. <c>null</c> si otra escritura ganó.
    /// </summary>
    private async Task<(clsResultadoEdicion Resultado, IReadOnlyList<clsAvisoEdicion> Avisos)?> AplicarYGuardarAsync(
        clsCotizacionSolicitud cotizacion, Guid usuarioId, clsEdicionPreparada p, int versionEsperada)
    {
        var resultado = cotizacion.AplicarEdicion(new clsEdicionSolicitud(p.Encabezado, p.Rutas, p.Moneda?.Moneda, p.Moneda?.TrmAplicada ?? 0m), reloj.AhoraUtc);
        var avisos = new List<clsAvisoEdicion>();
        if (resultado.RutasQuitadas.Count > 0)
        {
            var desvinculados = await ajustes.DesvincularRutasAsync([.. resultado.RutasQuitadas.Select(r => r.Id)], CancellationToken.None);
            if (desvinculados > 0 || resultado.RutasQuitadas.Any(r => r.TieneMargenNegociado))
            {
                avisos.Add(new clsAvisoEdicion(clsAvisoEdicion.MargenDesvinculado,
                    "Se quitaron rutas con margen ajustado: sus ajustes quedan en el historial de la cotización sin ruta."));
            }
        }

        if (clsReglaPdfCosteo.InvalidarPdf(cotizacion, MotivoInvalidacionPdf.ReSolicitud, usuarioId, cotizaciones))
        {
            avisos.Add(new clsAvisoEdicion(clsAvisoEdicion.InvalidaPdf, "El PDF quedó desactualizado: genéralo de nuevo cuando llegue el costeo."));
        }

        cotizacion.IncrementarVersionNegocio();
        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, CancellationToken.None) ? (resultado, avisos) : null;
    }

    /// <summary>Sube la versión de negocio y guarda con la instantánea; <c>false</c> si otra escritura ganó (nada se escribe).</summary>
    private async Task<bool> ReservarAsync(clsCotizacionSolicitud cotizacion, int versionEsperada, clsEventoHerramienta? reserva)
    {
        cotizacion.IncrementarVersionNegocio();
        if (reserva is not null)
        {
            registroAuditoria.Registrar(reserva);
        }

        return await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, versionEsperada, CancellationToken.None);
    }

    private static clsErrorDominio RechazoFallido(clsCotizacionSolicitud cotizacion, string anterior, clsErrorDominio error)
    {
        var datos = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["motivo"] = MotivoRechazoFallido,
            ["solicitud_nexo_id"] = anterior,
            ["version_negocio"] = cotizacion.VersionNegocio,
            ["causa"] = error.Codigo,
        };
        return error.Codigo == clsCodigosError.NexoSinPermiso
            ? error with { Datos = datos }
            : new clsErrorDominio(clsCodigosError.NexoNoDisponible,
                $"No se pudo rechazar en Nexo la solicitud anterior de {cotizacion.Folio}: la edición no se aplicó y no se creó otra solicitud. "
                + "Intenta de nuevo en unos minutos (recarga la solicitud: su versión cambió).") { Datos = datos };
    }

    // ------------------------------------------------------------------ Lectura y respuesta

    private async Task<clsResultadoEdicionSolicitud> RespuestaAsync(
        clsCotizacionSolicitud resultante, clsCotizacionSolicitud origen, ModoEdicionSolicitud modo, bool sinCambios,
        IReadOnlyList<clsAvisoEdicion> avisos, IReadOnlyList<int> replanificadas, IReadOnlyList<int> agregadas, IReadOnlyList<Guid> quitadas,
        string? reemplazada) =>
        new(clsCodigoEnum.ACodigo(modo), resultante.Folio, origen.Folio, sinCambios,
            await misCotizaciones.ResumirAsync(resultante, CancellationToken.None), avisos, replanificadas, agregadas, quitadas, reemplazada);

    /// <summary>Los avisos que el comercial confirma antes de editar, según el modo y el estado actual.</summary>
    private static IReadOnlyList<clsAvisoEdicion> Avisos(clsCotizacionSolicitud cotizacion, ModoEdicionSolicitud modo)
    {
        var avisos = new List<clsAvisoEdicion>();
        if (modo == ModoEdicionSolicitud.NuevaVersion)
        {
            avisos.Add(new clsAvisoEdicion(clsAvisoEdicion.CreaVersionNueva,
                "La cotización ya se entregó al cliente o tiene resultado: la edición crea una versión nueva y la original no cambia."));
            return avisos;
        }

        if (modo == ModoEdicionSolicitud.Reemplazo)
        {
            avisos.Add(new clsAvisoEdicion(clsAvisoEdicion.ReemplazaSolicitudNexo,
                "La solicitud actual se rechaza en Nexo y se captura de nuevo en el mismo folio."));
            if (cotizacion.EstadoCosteo == EstadoCosteo.PendienteCosteo)
            {
                avisos.Add(new clsAvisoEdicion(clsAvisoEdicion.PierdeTrabajoMesa,
                    "La Mesa de Costeo está costeando esta solicitud: se pierde el trabajo que lleve y vuelve a empezar."));
            }
            else if (cotizacion.EstadoCosteo is { } estado && clsDerivacionEstadoCosteo.TieneCosteo(estado))
            {
                avisos.Add(new clsAvisoEdicion(clsAvisoEdicion.PierdeCosteoActual, "Se pierde el costeo actual: el precio vuelve con el costeo nuevo."));
            }
        }

        if (cotizacion.PdfPath is not null)
        {
            avisos.Add(new clsAvisoEdicion(clsAvisoEdicion.InvalidaPdf, "El PDF quedará desactualizado: genéralo de nuevo cuando llegue el costeo."));
        }

        return avisos;
    }

    private static clsRutaEditable Editable(
        clsLineaSolicitud r, IReadOnlyDictionary<int, clsMargenPolitica?> politicasRuta, IReadOnlyDictionary<string, clsCostoConexoNexo> catalogo) =>
        new(
            r.Id,
            r.Orden,
            r.LineaNegocioId is { } lineaId ? politicasRuta.GetValueOrDefault(lineaId)?.LineaNegocio : null,
            LineaNegocioBloqueada: true,
            r.OrigenMunicipioId,
            r.OrigenNombre,
            r.DestinoMunicipioId,
            r.DestinoNombre,
            r.Tipologia,
            r.ClaseViaje,
            r.ClaseContenedor,
            r.Exclusivo,
            r.Consolidado,
            r.RetornaContenedor,
            r.CiudadDevolucionMunicipioId,
            r.CiudadDevolucionNombre,
            r.LevanteContenedorDiferente,
            r.CiudadLevanteMunicipioId,
            r.CiudadLevanteNombre,
            r.HorasCargue,
            r.HorasDescargue,
            r.ValorLevanteContenedor,
            r.ValorMercancia,
            r.PesoKg,
            [.. r.MercanciasOrdenadas.Select(m => new clsMercanciaEditable(m.CodigoMercancia, m.NombreMercancia, m.Cantidad, m.LargoCm, m.AnchoCm, m.AltoCm, m.PesoKg))],
            ConexosEditables(r, catalogo),
            r.MargenAplicado,
            r.OrigenMargen is { } origen ? clsCodigoEnum.ACodigo(origen) : null,
            r.TieneMargenNegociado);

    /// <summary>
    /// Los conexos de la ruta como los edita el comercial: con el id del catálogo de Nexo (si se resolvió) y UNA fila por servicio. Un
    /// servicio capturado y el mismo agregado después en Nexo (guardado con el id de la fila de Nexo) se suman, como en la versión nueva de
    /// la re-solicitud: si no, el GET devolvería el id repetido y el PUT con ese mismo cuerpo respondería <c>CONEXO_DUPLICADO</c>.
    /// </summary>
    private static IReadOnlyList<clsConexoEditable> ConexosEditables(clsLineaSolicitud ruta, IReadOnlyDictionary<string, clsCostoConexoNexo> catalogo) =>
        [.. ruta.Conexos.OrderBy(c => c.CreatedAt).ThenBy(c => c.IdServicioConexo, StringComparer.Ordinal)
            .Select(c => catalogo.TryGetValue(c.IdServicioConexo, out var deCatalogo)
                ? new clsConexoEditable(deCatalogo.Id, deCatalogo.Nombre ?? c.Nombre, c.Cantidad)
                : new clsConexoEditable(c.IdServicioConexo, c.Nombre, c.Cantidad))
            .GroupBy(c => c.IdServicioConexo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1 ? g.First() : g.First() with { Cantidad = g.Sum(c => c.Cantidad) })];

    /// <summary>
    /// Los conexos guardados con el id del catálogo de Nexo de hoy (un conexo agregado en Nexo está guardado con el id de la fila de la
    /// cotización de Nexo: se resuelve por id o por nombre). Sin catálogo, los ids guardados (la edición los valida al guardar).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, clsCostoConexoNexo>> ConexosDelCatalogoAsync(
        Guid usuarioId, clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        var conexos = cotizacion.Rutas.SelectMany(r => r.Conexos).Select(c => (c.IdServicioConexo, c.Nombre)).ToList();
        if (conexos.Count == 0)
        {
            return new Dictionary<string, clsCostoConexoNexo>();
        }

        var resueltos = await resolutorConexos.ResolverAsync(usuarioId, conexos, cancellationToken);
        if (resueltos.ErrorCatalogo is { } error)
        {
            logger.LogWarning("El catálogo de conexos de Nexo no respondió al leer la solicitud {Folio} para editarla ({Codigo}).", cotizacion.Folio, error.Codigo);
        }

        return resueltos.Resueltos;
    }

    /// <summary>La política (<c>MargenesPolitica</c>) de cada línea de negocio de las rutas, leída una vez por línea.</summary>
    private async Task<IReadOnlyDictionary<int, clsMargenPolitica?>> PoliticasAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<int, clsMargenPolitica?>();
        foreach (var lineaId in cotizacion.LineasNegocioDeRutas)
        {
            resultado[lineaId] = await politicas.ObtenerPorIdAsync(lineaId, cancellationToken);
        }

        return resultado;
    }

    /// <summary>Los camiones vigentes de la ruta (los descartados en Nexo no, salvo que lo estén todos) y su resultado del cubicador.</summary>
    private static clsPlanRuta PlanVigente(clsLineaSolicitud ruta) =>
        new([.. ruta.VehiculosQueCuentan.OrderBy(v => v.CreatedAt).ThenBy(v => v.OrdenCreacion).ThenBy(v => v.Id).Select(v => new clsCamionPlaneado(
                v.TipoVehiculo, v.Carroceria, v.PesoKg, v.IdRecomendacionVehiculo, v.VolumenTotalM3, v.PorcentajeLlenadoVolumen, v.PorcentajeLlenadoPeso))],
            ruta.ResultadoCubicacion is { } resultado ? new clsCubicacionRuta(ruta.IdRecomendacion, resultado, ruta.MotivoSinSeleccion, ruta.Condicionada) : null);

    /// <summary>
    /// ¿La edición deja todo como está? Compara el encabezado, la moneda y, ruta por ruta y en orden, la misma ruta guardada, los municipios
    /// por id, las condiciones, las horas, los valores, el peso, las mercancías (por código, cantidad, medidas y peso) y los conexos (id y
    /// cantidad). Los nombres copiados de los catálogos no cuentan.
    /// </summary>
    private static bool MismoContenido(
        clsCotizacionSolicitud cotizacion, clsEncabezadoSolicitudCosteo encabezado, string moneda, IReadOnlyList<clsRutaEdicionEntrada?> entradas,
        IReadOnlyList<clsLineaLicitacionEntrada> validadas, IReadOnlyDictionary<string, clsCostoConexoNexo> catalogo)
    {
        var guardadas = cotizacion.RutasOrdenadas;
        if (guardadas.Count != validadas.Count || cotizacion.Deadline != encabezado.Deadline
            || !string.Equals(cotizacion.Notas, Limpio(encabezado.Notas), StringComparison.Ordinal)
            || !string.Equals(cotizacion.Moneda, moneda, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 0; i < guardadas.Count; i++)
        {
            var guardada = guardadas[i];
            if (entradas[i]?.LineaSolicitudId != guardada.Id)
            {
                return false;
            }

            var nueva = validadas[i].Linea with
            {
                HorasCargue = validadas[i].Linea.HorasCargue ?? encabezado.HorasCargue,
                HorasDescargue = validadas[i].Linea.HorasDescargue ?? encabezado.HorasDescargue,
            };
            var conexos = (validadas[i].Conexos ?? []).Select(c => (Limpio(c?.IdServicioConexo)?.ToUpperInvariant(), c?.Cantidad));
            if (Contenido(guardada.ALinea(), ConexosEditables(guardada, catalogo).Select(c => ((string?)c.IdServicioConexo.ToUpperInvariant(), (decimal?)c.Cantidad)))
                != Contenido(nueva, conexos))
            {
                return false;
            }
        }

        return true;
    }

    private static string Contenido(clsLineaSolicitudCosteo l, IEnumerable<(string? Id, decimal? Cantidad)> conexos) =>
        JsonSerializer.Serialize(new object?[]
        {
            l.OrigenMunicipioId, l.DestinoMunicipioId, Texto(l.Tipologia), Texto(l.ClaseViaje), Texto(l.ClaseContenedor), l.Exclusivo, l.Consolidado,
            l.RetornaContenedor ?? false, l.CiudadDevolucionMunicipioId, l.LevanteContenedorDiferente ?? false, l.CiudadLevanteMunicipioId,
            l.ValorAprovisionamiento, l.HorasCargue, l.HorasDescargue, l.PesoKg,
            l.ValorMercancia,
            l.Posiciones.Select(p => new object?[] { Texto(p.CodigoMercancia), p.Cantidad, p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg }).ToArray(),
            conexos.OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => new object?[] { c.Id, c.Cantidad }).ToArray(),
        }, _jsonHuella);

    private static string? Texto(string? valor) => Limpio(valor)?.ToUpperInvariant();

    private static string? Limpio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private async Task<clsCotizacionSolicitud?> CargarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken) =>
        usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);

    private static clsErrorDominio NoEncontrada() => new(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");

    /// <summary><c>CAMPO_NO_EDITABLE</c> (400) con el primer campo en <c>detalle.campo</c>, todos en <c>detalle.campos</c> y el detalle por ruta.</summary>
    private static clsErrorDominio CampoNoEditable(IReadOnlyList<clsErrorFilaLicitacion> bloqueados)
    {
        var primero = bloqueados[0];
        IReadOnlyList<IReadOnlyDictionary<string, object?>> errores = [.. bloqueados.Select(e => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ruta"] = e.Indice,
            ["campo"] = e.Campo,
            ["codigo"] = e.Codigo,
            ["mensaje"] = e.Mensaje,
        })];
        return new clsErrorDominio(clsCodigosError.CampoNoEditable,
            bloqueados.Count == 1 ? primero.Mensaje : $"{primero.Mensaje} (y {bloqueados.Count - 1} campo(s) bloqueado(s) más en detalle.campos)")
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["campo"] = primero.Campo,
                ["campos"] = bloqueados.Select(e => e.Campo).ToList(),
                ["errores"] = errores,
            },
        };
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
}
