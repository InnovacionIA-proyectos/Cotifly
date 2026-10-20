using System.Diagnostics;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
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
    Task<clsResultado<clsCotizacionSolicitud>> EnviarAsync(Guid usuarioId, clsEntradaSolicitudCosteo entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// Captura por rutas (HU-021.6): valida TODO junto (<c>SOLICITUD_COSTEO_INVALIDA</c> con <c>detalle.errores</c>: cliente, moneda,
    /// encabezado, y por ruta línea de negocio, límites de Nexo, códigos de catálogo, municipios con id obligatorio, mercancías con
    /// <c>id_tipo_mercancia</c> y conexos del catálogo de Nexo), resuelve la línea de negocio de cada ruta (<c>MARGEN_LINEA_DESCONOCIDA</c>),
    /// crea la cotización con sus rutas, sus camiones (uno por vehículo que recomiende el cubicador, #133, o uno sin vehículo de respaldo) y
    /// sus conexos con el costo de Nexo, la
    /// confirma y la envía a Nexo como en <see cref="EnviarAsync"/>. 503 si un catálogo de Nexo no responde (<c>CATALOGOS_FLETE_NO_DISPONIBLE</c>,
    /// <c>MUNICIPIOS_NO_DISPONIBLE</c> o <c>NEXO_NO_DISPONIBLE</c> para los conexos): nada se guarda.
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> CapturarAsync(Guid usuarioId, clsEntradaCaptura entrada, CancellationToken cancellationToken = default);

    /// <summary>
    /// #173: <see cref="CapturarAsync"/> con <c>Idempotency-Key</c> (<see cref="IClsAlmacenIdempotencia"/>, ámbito
    /// <c>capturar_costeo:{usuarioId}</c>). Una repetición con la misma clave y el mismo cuerpo en 24 h devuelve la cotización de la
    /// primera sin crear otro folio ni otra solicitud en Nexo (el cliente reintenta tras un timeout o un 500); con otro cuerpo,
    /// <c>IDEMPOTENCY_KEY_REUTILIZADA</c> (409, D4). Toda captura exitosa (también la que quedó en <c>error_envio</c>, que ya tiene folio) consume la clave.
    /// Sin clave se comporta igual que <see cref="CapturarAsync"/>.
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> CapturarUnaVezAsync(
        Guid usuarioId, clsEntradaCaptura entrada, string? claveIdempotencia, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reenvía la solicitud guardada. <c>COTIZACION_NO_ENCONTRADA</c> si no existe o no es del comercial;
    /// <c>COSTEO_NO_REINTENTABLE</c> si no está en <c>error_envio</c> ni en un <c>pendiente_envio</c> abandonado. Una licitación se
    /// revalida completa con las reglas de captura (HU-013.11, <see cref="IClsValidadorFilasLicitacion.RevalidarGuardadasAsync"/>): si una
    /// fila guardada ya no pasa, <c>LICITACION_INVALIDA</c> con <c>detalle.motivo = LICITACION_REQUIERE_NUEVA_CARGA</c> y nada se envía.
    /// </summary>
    Task<clsResultado<clsCotizacionSolicitud>> ReintentarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envía a Nexo una solicitud ya guardada y confirmada con sus rutas y camiones (licitación, HU-011.2, ADR-017; re-solicitud) y guarda
    /// el resultado (<c>pendiente_costeo</c>/<c>autocosteo</c> o <c>error_envio</c>), igual que el envío de HU-005.5. Sin el token de la
    /// petición. <paramref name="notaTrazabilidad"/> (re-solicitud, HU-013.4) se agrega a las <c>notas</c> que viajan a Nexo, sin guardarse
    /// en la cotización ni sobrepasar el límite de Nexo (<see cref="ConNota"/>).
    /// </summary>
    Task EnviarGuardadaAsync(clsCotizacionSolicitud cotizacion, clsMargenPolitica? politica, Guid usuarioId, string? notaTrazabilidad = null);
}

/// <summary>Implementación de <see cref="IClsEnvioCosteoService"/>.</summary>
/// <remarks>
/// <list type="number">
/// <item>Valida el formulario por rutas (HU-021.6, <see cref="IClsCapturaRutasService"/>: mismos límites que Nexo, códigos de catálogo,
/// municipios con id obligatorio, mercancías con su tipo y conexos), todos los errores juntos, exige el NIT y resuelve la línea de
/// negocio de cada ruta en <c>MargenesPolitica</c>.</item>
/// <item>Fija la moneda y, en USD, la TRM del día (HU-013.7, ADR-014 §5, <see cref="IClsFijacionMonedaService"/>): sin TRM no se guarda
/// nada ni se llama a Nexo. El polling, el ajuste de margen y el PDF convierten con esa TRM fijada; el reintento la conserva.</item>
/// <item>Crea la solicitud con folio del servidor (ADR-011, con el NIT): la cabecera y cada ruta con su línea de negocio, sus mercancías,
/// sus conexos (costo de Nexo) y los camiones de <see cref="IClsPlanificadorCamiones"/> (uno por vehículo recomendado por el cubicador,
/// #133; uno sin vehículo de respaldo si el cubicador no está configurado o falla), en
/// <c>pendiente_envio</c>, y confirma la transacción: lo que llenó el comercial nunca se pierde aunque Nexo no responda.</item>
/// <item>Resuelve el BP en el catálogo de Nexo por el NIT (ADR-005 §4: el BP solo sale del catálogo y no se persiste).</item>
/// <item>Envía a Nexo y guarda el resultado: <c>pendiente_costeo</c> con <c>costeoId</c> y la primera consulta del polling, o
/// <c>error_envio</c> con el motivo. Cada intento queda en la auditoría (<c>enviar_costeo_nexo</c>).</item>
/// <item>Si Nexo costeó todas las líneas en la misma petición (<c>fletesCalculados</c>), lee el costeo enseguida y aplica el precio
/// con la regla del polling (HU-013.14, ADR-018 §17): la respuesta sale en <c>autocosteo</c> CON <c>precio_final</c>. Si esa lectura
/// falla, queda en <c>pendiente_costeo</c> y el polling la completa: <c>autocosteo</c> nunca se expone sin haber leído el costo.</item>
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
    IClsValidadorFilasLicitacion filasLicitacion,
    IClsFijacionMonedaService monedas,
    IClsUnitOfWork unidadTrabajo,
    IClsAuditoriaService auditoria,
    IClsReloj reloj,
    IOptions<clsOpcionesCosteoNexo> opciones,
    ILogger<clsEnvioCosteoService> logger,
    IClsPrecioCosteoService precioCosteo,
    IClsCapturaRutasService capturaRutas,
    IOptions<clsOpcionesCaptura>? opcionesCaptura = null,
    IClsAlmacenIdempotencia? idempotencia = null,
    IClsRegistroCapturasIdempotentes? registroCapturas = null) : IClsEnvioCosteoService
{
    /// <summary>Herramienta con la que se audita cada intento de envío (<c>EventosHerramienta</c>).</summary>
    public const string HerramientaAuditoria = "enviar_costeo_nexo";

    /// <summary>Ámbito de las claves de idempotencia de la captura (con el comercial, #173).</summary>
    public const string OperacionIdempotencia = "capturar_costeo";

    public async Task<clsResultado<clsCotizacionSolicitud>> CapturarUnaVezAsync(
        Guid usuarioId, clsEntradaCaptura entrada, string? claveIdempotencia, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        if (claveIdempotencia is null || idempotencia is null)
        {
            return await CapturarAsync(usuarioId, entrada, cancellationToken);
        }

        var ambito = $"{OperacionIdempotencia}:{usuarioId}";
        var huella = Huella(entrada);

        // #191: el almacén guarda la captura hecha (id y folio), no la entidad: la tabla del 06 la guarda como JSON. Si la acción corrió
        // en esta petición se devuelve la cotización que dejó; si es una repetición, la guardada se lee por su folio.
        clsCotizacionSolicitud? ejecutada = null;
        var hecha = await idempotencia.EjecutarUnaVezAsync<clsCapturaHecha>(
            ambito,
            claveIdempotencia,
            huella,
            async ct =>
            {
                var previa = registroCapturas is null ? null : await registroCapturas.BuscarAsync(ambito, claveIdempotencia, ct);
                var resultado = previa is not null
                    ? await ReanudarAsync(previa, huella, usuarioId, ct)
                    : await CapturarInternoAsync(usuarioId, entrada, ct, registroCapturas is null
                        ? null
                        : (c, ctRegistro) => registroCapturas.RegistrarAsync(ambito, claveIdempotencia,
                            new clsCapturaIdempotente(huella, c.Id, c.Folio, c.SolicitudNexoId, reloj.AhoraUtc, c.ScorePrioridad), ctRegistro));
                if (resultado.EsFallo)
                {
                    return resultado.Error;
                }

                ejecutada = resultado.Valor;
                return new clsCapturaHecha(resultado.Valor.Id, resultado.Valor.Folio);
            },
            r => r.EsExitoso,
            cancellationToken);
        if (hecha.EsFallo)
        {
            return hecha.Error;
        }

        return ejecutada
            ?? await cotizaciones.ObtenerPorFolioDelUsuarioAsync(hecha.Valor.Folio, usuarioId, cancellationToken)
            ?? (clsResultado<clsCotizacionSolicitud>)new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
    }

    /// <summary>Motivo (<c>detalle.motivo</c>) de <c>ESTADO_NO_PERMITE</c> cuando la primera captura con la clave no terminó (#173).</summary>
    public const string MotivoCapturaEnCurso = clsCodigosAplicacion.MotivoCapturaEnCurso;

    /// <summary>
    /// #173: la clave ya tiene una captura registrada (folio confirmado) cuyo resultado no quedó guardado: la primera petición falló
    /// después de confirmar el folio (por ejemplo, al guardar el <c>solicitudNexoId</c>). NUNCA vuelve a llamar a Nexo. Si la cotización
    /// guardada ya refleja el envío (no está en <c>pendiente_envio</c> y, si se conoce, tiene el mismo <c>solicitudNexoId</c>), responde con
    /// ella; si no, <c>ESTADO_NO_PERMITE</c> con <c>detalle.motivo = CAPTURA_EN_CURSO</c>, el folio y el id de Nexo, para revisar el folio
    /// en "Mis cotizaciones" en lugar de reintentar.
    /// </summary>
    private async Task<clsResultado<clsCotizacionSolicitud>> ReanudarAsync(
        clsCapturaIdempotente previa, string huella, Guid usuarioId, CancellationToken cancellationToken)
    {
        if (previa.Huella != huella)
        {
            return new clsErrorDominio(clsCodigosAplicacion.IdempotencyKeyReutilizada, clsCodigosAplicacion.MensajeIdempotencyKeyReutilizada);
        }

        var guardada = await cotizaciones.ObtenerPorFolioDelUsuarioAsync(previa.Folio, usuarioId, cancellationToken);
        if (guardada is not null && guardada.EstadoCosteo != EstadoCosteo.PendienteEnvio
            && (previa.SolicitudNexoId is null || guardada.SolicitudNexoId == previa.SolicitudNexoId))
        {
            return guardada;
        }

        var datos = new Dictionary<string, object?> { ["folio"] = previa.Folio, ["solicitud_nexo_id"] = previa.SolicitudNexoId };
        return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
            $"La captura de esta Idempotency-Key ya creó el folio {previa.Folio} y su envío a Nexo no terminó de registrarse. No la repitas: "
            + "revisa el folio en Mis cotizaciones.",
            MotivoCapturaEnCurso) { Datos = datos };
    }

    /// <summary>Huella (SHA-256) del cuerpo de la captura: la misma clave con otro cuerpo es <c>IDEMPOTENCY_KEY_REUTILIZADA</c> (409, D4).</summary>
    public static string Huella(clsEntradaCaptura entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        return clsHuellaIdempotencia.Calcular(entrada);
    }

    public Task<clsResultado<clsCotizacionSolicitud>> EnviarAsync(Guid usuarioId, clsEntradaSolicitudCosteo entrada, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(entrada.Solicitud);
        return CapturarAsync(usuarioId, clsEntradaCaptura.Desde(entrada), cancellationToken);
    }

    public Task<clsResultado<clsCotizacionSolicitud>> CapturarAsync(Guid usuarioId, clsEntradaCaptura entrada, CancellationToken cancellationToken = default) =>
        CapturarInternoAsync(usuarioId, entrada, cancellationToken, null);

    /// <summary>
    /// <see cref="CapturarAsync"/> con <paramref name="alConfirmar"/> (#173): se llama con la cotización recién guardada DENTRO de la
    /// transacción que confirma el folio (#191: el folio y su clave quedan juntos o no queda ninguno), ANTES de llamar a Nexo, y otra vez
    /// después del envío (con el <c>solicitudNexoId</c>, aunque el guardado posterior falle).
    /// </summary>
    private async Task<clsResultado<clsCotizacionSolicitud>> CapturarInternoAsync(
        Guid usuarioId, clsEntradaCaptura entrada, CancellationToken cancellationToken, Func<clsCotizacionSolicitud, CancellationToken, Task>? alConfirmar)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(entrada.Encabezado);
        ArgumentNullException.ThrowIfNull(entrada.Rutas);
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(clsCodigosError.ErrorInterno, "El envío a costeo exige el comercial autenticado.");
        }

        var rutas = SinVehiculo(entrada.Rutas, logger, opcionesCaptura?.Value.AceptarVehiculoCliente ?? false);
        var nit = entrada.NitCliente?.Trim();

        // HU-021.6: todas las validaciones juntas (encabezado y cada ruta); el error trae el primero y detalle.errores trae todos.
        var validadas = await capturaRutas.ValidarAsync(usuarioId, rutas, ErroresFormulario(entrada, rutas), cancellationToken);
        if (validadas.Errores.Count > 0)
        {
            return ErrorCaptura(validadas.Errores, validadas.NoDisponible?.Codigo, rutas.Count);
        }

        if (validadas.NoDisponible is { } noDisponible)
        {
            return noDisponible;
        }

        // La línea de negocio es de cada ruta (la del encabezado si la ruta no trae).
        var politicasPorRuta = new List<clsMargenPolitica>(validadas.Lineas.Count);
        var porNombre = new Dictionary<string, clsMargenPolitica>(StringComparer.OrdinalIgnoreCase);
        foreach (var ruta in validadas.Lineas)
        {
            var nombre = (ruta.LineaNegocio ?? entrada.LineaNegocio)!.Trim();
            if (!porNombre.TryGetValue(nombre, out var deRuta))
            {
                var resuelta = await margen.ResolverLineaAsync(nombre, cancellationToken);
                if (resuelta is null)
                {
                    return new clsErrorDominio(clsCodigosError.MargenLineaDesconocida, $"La línea de negocio '{nombre}' no existe en Cotifly.");
                }

                if (!resuelta.Activa)
                {
                    return clsMargenResolverService.LineaInactiva(resuelta.LineaNegocio);
                }

                // G11: advertencia (no bloquea) si el mapeo a la línea de Nexo no está en su catálogo de líneas de negocio.
                catalogos.RevisarMapeoLineaNexo(resuelta);
                porNombre[nombre] = deRuta = resuelta;
            }

            politicasPorRuta.Add(deRuta);
        }

        var politica = politicasPorRuta[0];

        // Conexos con el costo del catálogo de Nexo y camiones del planificador (los del cubicador o, de respaldo, uno sin vehículo; #133).
        var preparadas = await capturaRutas.PrepararAsync(
            usuarioId, [.. validadas.Lineas.Select((r, i) => new clsRutaPorPreparar(r, politicasPorRuta[i].Id, i))], cancellationToken: cancellationToken);
        if (preparadas.Error is { } errorPreparacion)
        {
            return errorPreparacion;
        }

        if (preparadas.Errores.Count > 0)
        {
            return ErrorCaptura(preparadas.Errores, null, rutas.Count);
        }

        // HU-013.7 (ADR-014 §5): en USD la TRM del día de creación queda fija ANTES de guardar; sin TRM no se guarda ni se envía nada.
        var fijada = await monedas.FijarAsync(entrada.Moneda, cancellationToken);
        if (fijada.EsFallo)
        {
            return fijada.Error;
        }

        var creada = await folios.CrearConFolioNuevoAsync<clsCotizacionSolicitud>(usuarioId, nit!, async (folio, ct) =>
        {
            var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
            {
                Folio = folio.Valor,
                Version = folio.Version,
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

            // HU-021.6: la cabecera y cada ruta con su línea de negocio, mercancías, camiones y conexos, en una sola escritura.
            cotizacion.Valor.RegistrarCaptura(entrada.Encabezado, preparadas.Rutas);
            cotizaciones.Agregar(cotizacion.Valor);
            await unidadTrabajo.GuardarCambiosAsync(ct);
            if (alConfirmar is not null)
            {
                // #191: la clave se registra con el folio en la misma transacción que lo confirma (la del candado del folio).
                await alConfirmar(cotizacion.Valor, ct);
            }

            return cotizacion.Valor;
        }, cancellationToken);
        if (creada.EsFallo)
        {
            return creada.Error;
        }

        // Desde aquí, sin el token de la petición (ver EnviarANexoAsync). Se envía lo GUARDADO (normalizado: textos recortados), no la
        // entrada: el primer envío y el reintento mandan exactamente lo mismo a Nexo.
        // HU-021: las horas viajan en cada línea (la ruta las guarda); la cabecera no las tiene, así el reintento manda lo mismo.
        var encabezado = creada.Valor.EncabezadoSolicitud ?? entrada.Encabezado;
        try
        {
            await EnviarANexoAsync(creada.Valor, encabezado, politica, usuarioId, reintento: false);
        }
        finally
        {
            // #173: el solicitudNexoId queda registrado con la clave aunque falle el guardado posterior a Nexo (sin el token de la
            // petición, como el envío).
            if (alConfirmar is not null)
            {
                await alConfirmar(creada.Valor, CancellationToken.None);
            }
        }

        return creada.Valor;
    }

    /// <summary>
    /// Decisión 7 de HU-021 (HU-021.6): el comercial ya no digita vehículo ni carrocería (los define el cubicador o la Mesa de Costeo de
    /// Nexo). Si una ruta los trae (cliente con el contrato anterior), se ignoran y queda un aviso en el log; la captura sigue.
    /// Con la bandera de respaldo <see cref="clsOpcionesCaptura.AceptarVehiculoCliente"/> (#138) se conservan: los valida el catálogo de
    /// Nexo como cualquier código y solo los usa el camión del respaldo (<see cref="clsPlanificadorCamionSinVehiculo"/>); si el cubicador
    /// recomienda, manda el cubicador.
    /// </summary>
    public static IReadOnlyList<clsLineaLicitacionEntrada?> SinVehiculo(
        IReadOnlyList<clsLineaLicitacionEntrada?> rutas, ILogger logger, bool aceptarVehiculoCliente = false)
    {
        ArgumentNullException.ThrowIfNull(rutas);
        ArgumentNullException.ThrowIfNull(logger);
        if (aceptarVehiculoCliente)
        {
            var conVehiculo = rutas.Count(r => r?.Linea is { } l && (l.TipoVehiculo is not null || l.Carroceria is not null));
            if (conVehiculo > 0)
            {
                logger.LogInformation(
                    "{Rutas} ruta(s) traen tipo_vehiculo o carroceria y Captura:AceptarVehiculoCliente está encendida: se conservan para el camión del respaldo (sin cubicador).",
                    conVehiculo);
            }

            return rutas;
        }

        var ignoradas = 0;
        var limpias = rutas.Select(r =>
        {
            if (r?.Linea is not { } linea || (linea.TipoVehiculo is null && linea.Carroceria is null))
            {
                return r;
            }

            ignoradas++;
            return r with { Linea = linea with { TipoVehiculo = null, Carroceria = null } };
        }).ToList();
        if (ignoradas > 0)
        {
            logger.LogWarning(
                "{Rutas} ruta(s) traen tipo_vehiculo o carroceria: se ignoran (HU-021, decisión 7: los define el cubicador o la Mesa de Costeo de Nexo).",
                ignoradas);
        }

        return limpias;
    }

    /// <summary>
    /// La misma regla (decisión 7) sobre los cambios de carga de la re-solicitud (<c>datos_carga</c>, HU-021.9): <c>tipo_vehiculo</c> y
    /// <c>carroceria</c> se ignoran con un aviso en el log. Está junto a la de la captura. La bandera <see cref="clsOpcionesCaptura.AceptarVehiculoCliente"/> (#138) no aplica aquí: los camiones de la versión nueva salen del planificador o se copian.
    /// </summary>
    public static clsCambiosCarga SinVehiculo(clsCambiosCarga cambios, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(cambios);
        ArgumentNullException.ThrowIfNull(logger);
        if (!cambios.Presentes.Contains(clsCambiosCarga.CampoTipoVehiculo) && !cambios.Presentes.Contains(clsCambiosCarga.CampoCarroceria))
        {
            return cambios;
        }

        logger.LogWarning(
            "datos_carga trae tipo_vehiculo o carroceria: se ignoran (HU-021, decisión 7: los define el cubicador o la Mesa de Costeo de Nexo).");
        var presentes = new HashSet<string>(cambios.Presentes, StringComparer.Ordinal);
        presentes.Remove(clsCambiosCarga.CampoTipoVehiculo);
        presentes.Remove(clsCambiosCarga.CampoCarroceria);
        return cambios with { Presentes = presentes, TipoVehiculo = null, Carroceria = null };
    }

    /// <summary>
    /// Validaciones de forma del formulario por rutas que no dependen de catálogos (HU-021.6), todas juntas: cliente, moneda, encabezado,
    /// número de rutas, línea de negocio de cada ruta (o la del encabezado) y los límites de Nexo de cada ruta
    /// (<see cref="clsLineaSolicitudCosteo.Errores"/>).
    /// </summary>
    public static IReadOnlyList<clsErrorFilaLicitacion> ErroresFormulario(clsEntradaCaptura entrada, IReadOnlyList<clsLineaLicitacionEntrada?> rutas)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(rutas);
        var errores = new List<clsErrorFilaLicitacion>();
        void Encabezado(string campo, string codigo, string mensaje) => errores.Add(new clsErrorFilaLicitacion(null, null, campo, codigo, mensaje));

        var nit = entrada.NitCliente?.Trim();
        if (string.IsNullOrEmpty(nit))
        {
            Encabezado("nit_cliente", clsCodigosLineaSolicitud.CampoRequerido, "El campo 'nit_cliente' es obligatorio: elige el cliente en el buscador.");
        }
        else if (nit.Length > clsEntradaSolicitudCosteo.LargoMaximoNit)
        {
            Encabezado("nit_cliente", clsCodigosLineaSolicitud.CampoDemasiadoLargo, $"El campo 'nit_cliente' supera {clsEntradaSolicitudCosteo.LargoMaximoNit} caracteres.");
        }

        if (entrada.ClienteNombre is { Length: > clsEntradaSolicitudCosteo.LargoMaximoNombre })
        {
            Encabezado("cliente_nombre", clsCodigosLineaSolicitud.CampoDemasiadoLargo, $"El campo 'cliente_nombre' supera {clsEntradaSolicitudCosteo.LargoMaximoNombre} caracteres.");
        }

        if (ValidarMoneda(entrada.Moneda) is { } moneda)
        {
            Encabezado("moneda", clsCodigosLineaSolicitud.ValorFueraDeRango, moneda.Mensaje);
        }

        foreach (var error in entrada.Encabezado.Errores())
        {
            Encabezado(error.Campo, error.Codigo, error.Mensaje);
        }

        if (rutas.Count == 0)
        {
            Encabezado("rutas", clsCodigosLineaSolicitud.LineasRequeridas, "La solicitud debe traer al menos una ruta.");
            return errores;
        }

        if (rutas.Count > clsEntradaCaptura.MaximoRutas)
        {
            Encabezado("rutas", clsCodigosLineaSolicitud.DemasiadasLineas, $"Una solicitud admite máximo {clsEntradaCaptura.MaximoRutas} rutas (llegaron {rutas.Count}).");
            return errores;
        }

        var lineaPorDefecto = !string.IsNullOrWhiteSpace(entrada.LineaNegocio);
        for (var i = 0; i < rutas.Count; i++)
        {
            var indice = i;
            if (rutas[i]?.Linea is not { } linea)
            {
                errores.Add(new clsErrorFilaLicitacion(i, null, "rutas", clsCodigosLineaSolicitud.LineaIncompleta, $"La ruta {i + 1} está vacía."));
                continue;
            }

            if (!lineaPorDefecto && string.IsNullOrWhiteSpace(rutas[i]!.LineaNegocio))
            {
                errores.Add(new clsErrorFilaLicitacion(i, null, "linea_negocio", clsCodigosLineaSolicitud.CampoRequerido, "El campo 'linea_negocio' es obligatorio."));
            }

            errores.AddRange(linea.Errores().Select(e => new clsErrorFilaLicitacion(indice, null, e.Campo, e.Codigo, e.Mensaje)));
        }

        return errores;
    }

    /// <summary>
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> con TODOS los errores en <c>detalle.errores</c> (<c>ruta</c> = índice base 0 de la ruta, o
    /// <c>null</c> si es del encabezado; <c>campo</c>, <c>codigo</c> y <c>mensaje</c>). El mensaje es el del primero (con su ruta si hay
    /// varias) y <c>detalle.motivo</c> su código, o el del catálogo de Nexo que no respondió (lo que dependía de él se valida al volver a
    /// enviarla).
    /// </summary>
    public static clsErrorDominio ErrorCaptura(IReadOnlyList<clsErrorFilaLicitacion> errores, string? catalogoNoDisponible, int rutas)
    {
        ArgumentNullException.ThrowIfNull(errores);
        var primero = errores[0];
        var mensaje = rutas > 1 && primero.Indice is { } indice ? $"Ruta {indice + 1}: {primero.Mensaje}" : primero.Mensaje;
        if (errores.Count > 1)
        {
            mensaje += $" (y {errores.Count - 1} error(es) más en detalle.errores)";
        }

        IReadOnlyList<IReadOnlyDictionary<string, object?>> lista = [.. errores.Select(e => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ruta"] = e.Indice,
            ["campo"] = e.Campo,
            ["codigo"] = e.Codigo,
            ["mensaje"] = e.Mensaje,
        })];
        return new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, mensaje, catalogoNoDisponible ?? primero.Codigo)
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["errores"] = lista },
        };
    }

    public Task EnviarGuardadaAsync(clsCotizacionSolicitud cotizacion, clsMargenPolitica? politica, Guid usuarioId, string? notaTrazabilidad = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var encabezado = ConNota(cotizacion.EncabezadoSolicitud
            ?? throw new ArgumentException("La cotización no tiene el encabezado de la solicitud.", nameof(cotizacion)), notaTrazabilidad);
        return EnviarANexoAsync(cotizacion, encabezado, politica, usuarioId, reintento: false);
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
    /// La política (<c>MargenesPolitica</c>) de cada línea de negocio de las rutas, leída una vez por línea (#134); <paramref name="conocida"/>
    /// no se vuelve a leer. Una línea que ya no existe queda en <c>null</c>.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, clsMargenPolitica?>> PoliticasDeRutasAsync(clsCotizacionSolicitud cotizacion, clsMargenPolitica? conocida)
    {
        var resultado = new Dictionary<int, clsMargenPolitica?>();
        foreach (var lineaId in cotizacion.RutasOrdenadas.Select(r => r.LineaNegocioId).OfType<int>().Distinct())
        {
            resultado[lineaId] = lineaId == conocida?.Id ? conocida : await politicas.ObtenerPorIdAsync(lineaId, CancellationToken.None);
        }

        return resultado;
    }

    /// <summary><c>mapeoLineasNexo</c> de la línea de negocio de la ruta del camión, o <c>null</c> si la ruta no tiene o no se conoce.</summary>
    private static string? MapeoNexo(clsCotizacionSolicitud cotizacion, clsVehiculoLineaSolicitud camion, IReadOnlyDictionary<int, clsMargenPolitica?> politicasRuta) =>
        cotizacion.Rutas.FirstOrDefault(r => r.Id == camion.LineaSolicitudId)?.LineaNegocioId is { } lineaId
            ? politicasRuta.GetValueOrDefault(lineaId)?.MapeoLineasNexo
            : null;

    /// <summary>
    /// Aviso de la nota a la Mesa (HU-021.5, #133): las líneas de una ruta con varios camiones viajan sin posiciones (repartir las
    /// mercancías por camión exige la Tabla 8 del cubicador); cada una lleva el peso de su camión.
    /// </summary>
    public const string AvisoSinPosiciones =
        "Las rutas con varios camiones viajan sin el detalle de mercancías: cada línea lleva el peso de su camión.";

    /// <summary>Largo máximo de <see cref="NotaCamionesPorRuta"/> (el resto de <c>notas</c> es del comercial).</summary>
    public const int LargoMaximoNotaCamiones = 500;

    /// <summary>
    /// Nota para la Mesa de Costeo cuando alguna ruta viaja partida en varios camiones (#134): Nexo no tiene un campo de ruta por línea
    /// (N1), así que el encabezado dice cuántas líneas son de cada ruta, TODAS en su orden (también las de un camión), p. ej.
    /// <c>"Cotifly: una línea por camión, en este orden. Ruta 1 (Buenaventura → Bogotá D.C.): 3 camiones. Ruta 2 (Cali → Yumbo): 1
    /// camión."</c>, más el aviso <see cref="AvisoSinPosiciones"/> (#133: con varios camiones las mercancías no viajan por línea).
    /// <c>null</c> si todas las rutas tienen un solo camión (cada línea es una ruta: no hay nada que agrupar). Recortada a
    /// <see cref="LargoMaximoNotaCamiones"/>.
    /// </summary>
    public static string? NotaCamionesPorRuta(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasOrdenadas;
        // HU-021.9: solo los camiones que viajan (un descartado en Nexo no se reenvía).
        if (rutas.All(r => r.VehiculosQueCuentan.Count <= 1))
        {
            return null;
        }

        var nota = "Cotifly: una línea por camión, en este orden. "
            + string.Join(" ", rutas.Select(r => $"Ruta {r.Orden} ({r.Ruta}): {r.VehiculosQueCuentan.Count} {(r.VehiculosQueCuentan.Count == 1 ? "camión" : "camiones")}."))
            + " " + AvisoSinPosiciones;
        return nota.Length <= LargoMaximoNotaCamiones ? nota : nota[..(LargoMaximoNotaCamiones - 1)] + "…";
    }

    /// <summary>
    /// Las rutas guardadas como líneas de la captura (en orden, cada una con sus mercancías y el vehículo de su camión): lo que se
    /// revalida con las reglas de la captura antes de reenviar (HU-021, "una ruta y un camión").
    /// </summary>
    /// <remarks>
    /// HU-014 (#188, revisión de M1): el vehículo y la carrocería de un camión del CUBICADOR (con <c>idRecomendacionVehiculo</c>) no son
    /// datos digitados: son códigos del cubicador (<c>2S2</c>, <c>CAMA_BAJA</c>...) que Nexo puede no conocer, y lo que viaja de ellos lo
    /// decide <c>Nexo:EnviarCodigosCubicador</c> al enviar (desde los camiones, no desde estas líneas). Por eso no pasan por el catálogo de
    /// Nexo de la captura (M1): si pasaran, el reintento o la re-solicitud de una licitación cubicada respondería
    /// <c>LICITACION_INVALIDA</c> ("requiere nueva carga") por un código que Cotifly nunca digitó. El vehículo del camión del respaldo
    /// (sin recomendación, el digitado) sí se revalida.
    /// </remarks>
    public static IReadOnlyList<clsLineaSolicitudCosteo> LineasGuardadas(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return [.. cotizacion.RutasOrdenadas.Select(r => r.Vehiculos is [{ IdRecomendacionVehiculo: not null }]
            ? r.ALinea() with { TipoVehiculo = null, Carroceria = null }
            : r.ALinea())];
    }

    public async Task<clsResultado<clsCotizacionSolicitud>> ReintentarAsync(Guid usuarioId, string folio, CancellationToken cancellationToken = default)
    {
        var cotizacion = usuarioId == Guid.Empty || string.IsNullOrWhiteSpace(folio)
            ? null
            : await cotizaciones.ObtenerPorFolioDelUsuarioAsync(folio, usuarioId, cancellationToken);
        if (cotizacion is null)
        {
            return new clsErrorDominio(clsCodigosError.CotizacionNoEncontrada, "Cotización no encontrada o no es tuya.");
        }

        var ahora = reloj.AhoraUtc;
        var encabezado = cotizacion.EncabezadoSolicitud;
        var lineas = LineasGuardadas(cotizacion);
        if (lineas.Count == 0 || encabezado is null || !cotizacion.PuedeReenviarCosteo(ahora))
        {
            return NoReintentable(cotizacion);
        }

        // #173: si la captura llegó a Nexo y el guardado posterior falló, el folio quedó en pendiente_envio sin solicitudNexoId. Si el
        // registro de la Idempotency-Key conoce el id de Nexo, se vincula (pendiente_costeo, el polling sigue) y NO se reenvía: reenviar
        // duplicaría la solicitud. Con la tabla del 06 (#191) el registro sobrevive a un reinicio y lo ven todas las réplicas.
        // HU-018.1 (#155): el scorePrioridad que Nexo devolvió con ese id viaja en el registro; si no lo tiene, queda null.
        if (cotizacion.SolicitudNexoId is null
            && registroCapturas is not null
            && await registroCapturas.BuscarPorFolioAsync(cotizacion.Folio, cancellationToken) is { SolicitudNexoId: { } enNexo } capturaPrevia)
        {
            cotizacion.RegistrarEnvioCosteo(
                enNexo, costeoCompleto: false, ahora, ahora.AddSeconds(opciones.Value.IntervaloInicialSegundos), capturaPrevia.ScorePrioridad);
            await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
            logger.LogWarning("Reintento de {Folio}: Nexo ya tenía la solicitud {Solicitud} (el guardado posterior había fallado); se vincula sin reenviar.",
                cotizacion.Folio, enNexo);
            return cotizacion;
        }

        // Lo guardado se vuelve a validar (filas anteriores a una regla nueva): nunca se reenvía algo que Nexo rechazaría. Una licitación
        // pasa por todas las reglas de captura, también catálogos y municipios (HU-013.11: nunca texto libre a Nexo); el formulario de
        // una línea usa su validación (mismos mensajes que HU-005.5).
        IReadOnlyList<clsLineaSolicitudCosteo>? revalidadas = null;
        if (cotizacion.TipoSolicitud == TipoSolicitud.Licitacion)
        {
            var revalidada = await filasLicitacion.RevalidarGuardadasAsync(
                usuarioId, cotizacion.Folio, encabezado, lineas, [.. cotizacion.RutasOrdenadas.Select(r => r.RutaOrigenIndice)], cancellationToken);
            if (revalidada.EsFallo)
            {
                return revalidada.Error;
            }

            revalidadas = revalidada.Valor;
        }
        else if (Revalidar(encabezado, lineas) is { } invalida)
        {
            return invalida;
        }
        else if (lineas.Any(l => l.OrigenMunicipioId is null || l.DestinoMunicipioId is null))
        {
            // HU-021.6 (decisión 2): un formulario migrado sin id de origen o destino los resuelve por el nombre guardado, igual que la
            // licitación (nunca adivina); si no se puede, error claro y nada viaja a Nexo.
            var resuelta = await filasLicitacion.RevalidarGuardadasAsync(
                usuarioId, cotizacion.Folio, encabezado, lineas, [.. cotizacion.RutasOrdenadas.Select(r => r.RutaOrigenIndice)], cancellationToken);
            if (resuelta.EsFallo)
            {
                return ErrorDeFormulario(resuelta.Error);
            }

            revalidadas = resuelta.Valor;
        }

        // Reclamo optimista: de dos reintentos simultáneos solo uno envía (el otro vería otro updatedAt).
        if (!await cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, ahora, cancellationToken))
        {
            return NoReintentable(cotizacion, "otro envío de esta cotización está en curso");
        }

        // Reclamada: desde aquí, sin el token de la petición (ver EnviarANexoAsync).
        await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
        if (revalidadas is not null)
        {
            // Los ids de municipio resueltos por la revalidación se guardan con el envío (HU-013.11).
            CompletarMunicipios(cotizacion.RutasOrdenadas, revalidadas);
        }

        var politica = cotizacion.PrimeraRuta?.LineaNegocioId is { } lineaId ? await politicas.ObtenerPorIdAsync(lineaId, CancellationToken.None) : null;
        await EnviarANexoAsync(cotizacion, encabezado, politica, usuarioId, reintento: true);
        return cotizacion;
    }

    /// <summary>
    /// El error de la revalidación de filas (<c>LICITACION_INVALIDA</c>, que nombra la licitación) como error del formulario:
    /// <c>SOLICITUD_COSTEO_INVALIDA</c> con el primer error de la ruta y todos en <c>detalle.errores</c>. Un catálogo caído pasa igual.
    /// </summary>
    public static clsErrorDominio ErrorDeFormulario(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error.Codigo != clsCodigosError.LicitacionInvalida
            || error.Datos?.GetValueOrDefault("errores") is not IReadOnlyList<IReadOnlyDictionary<string, object?>> { Count: > 0 } filas)
        {
            return error;
        }

        IReadOnlyList<clsErrorFilaLicitacion> errores = [.. filas.Select(f => new clsErrorFilaLicitacion(
            f.GetValueOrDefault("indice") as int?, null, f.GetValueOrDefault("campo") as string ?? string.Empty,
            f.GetValueOrDefault("codigo") as string ?? string.Empty, f.GetValueOrDefault("mensaje") as string ?? string.Empty))];
        return ErrorCaptura(errores, null, errores.Count);
    }

    /// <summary>
    /// Copia a las rutas guardadas (en orden) los ids de municipio resueltos por la revalidación de una licitación (HU-013.11): se
    /// guardan con el envío, así el siguiente reenvío manda lo mismo.
    /// </summary>
    public static void CompletarMunicipios(IReadOnlyList<clsLineaSolicitud> guardadas, IReadOnlyList<clsLineaSolicitudCosteo> revalidadas)
    {
        ArgumentNullException.ThrowIfNull(guardadas);
        ArgumentNullException.ThrowIfNull(revalidadas);
        var ordenadas = guardadas.OrderBy(l => l.Orden).ToList();
        for (var i = 0; i < ordenadas.Count && i < revalidadas.Count; i++)
        {
            ordenadas[i].CompletarMunicipios(revalidadas[i].OrigenMunicipioId, revalidadas[i].DestinoMunicipioId);
        }
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
        clsCotizacionSolicitud cotizacion,
        clsEncabezadoSolicitudCosteo encabezado,
        clsMargenPolitica? politica,
        Guid usuarioId,
        bool reintento)
    {
        var cronometro = Stopwatch.StartNew();

        // HU-021 (CAMBIOS_BACKEND §5.3, #134): una línea por camión, ruta por ruta y en orden, cada una con los datos de su ruta y de su
        // camión. Sin vehículo (sin selección automática) la línea viaja igual y la resuelve la Mesa: la solicitud nunca se parte. Mientras
        // Nexo no tenga el campo de ruta por línea (N1), la nota del encabezado le dice a la Mesa qué líneas son la misma ruta.
        // Cada línea lleva la línea de negocio de Nexo de SU ruta (#134); `politica` (la de la ruta 1 o la del formulario) solo evita volver a
        // leerla y sirve de respaldo para una ruta sin línea de negocio.
        var politicasRuta = await PoliticasDeRutasAsync(cotizacion, politica);
        IReadOnlyList<clsLineaSolicitudCosteo> lineas = [.. cotizacion.LineasNexo().Select(l => l.Linea with
        {
            LineaNegocioNexo = MapeoNexo(cotizacion, l.Camion, politicasRuta) ?? politica?.MapeoLineasNexo,
        })];
        var conNota = ConNota(encabezado, NotaCamionesPorRuta(cotizacion));
        var resultado = await ResolverClienteYEnviarAsync(cotizacion, conNota, lineas, politica, politicasRuta, usuarioId, CancellationToken.None);
        var ahora = reloj.AhoraUtc;
        if (resultado.EsExitoso)
        {
            // HU-013.14: siempre "pendiente_costeo" hasta leer el costo; si Nexo ya costeó, la lectura inmediata (abajo) la promueve.
            var creada = resultado.Valor;
            // La solicitud y cada camión arrancan en pendiente_costeo; el polling deriva después el de cada camión (ADR-017, HU-021).
            // HU-018.1 (#155): el scorePrioridad de Nexo se guarda con el id (antes se descartaba).
            cotizacion.RegistrarEnvioCosteo(
                creada.Id, costeoCompleto: false, ahora, ahora.AddSeconds(opciones.Value.IntervaloInicialSegundos), creada.ScorePrioridad);

            // HU-023.4 (#159): lo que viajó (códigos del cubicador o no), en la misma transacción que el solicitudNexoId: el polling compara
            // contra esto y no contra la bandera actual.
            cotizaciones.RegistrarEnvioNexo(new clsEnvioNexoRegistrado(cotizacion.Folio, creada.Id, creada.CodigosCubicadorEnviados)
            {
                EquivalenciasVehiculo = creada.EquivalenciasVehiculo,
            });
        }
        else
        {
            cotizacion.RegistrarErrorEnvioCosteo(Motivo(resultado.Error), definitivo: EsRechazoDefinitivo(resultado.Error));
        }

        // Nexo ya tiene la solicitud: este guardado se repite ante un interbloqueo (IClsUnitOfWork.GuardarCambiosAsync) para no perder el
        // solicitudNexoId (sin él, el reintento la duplicaría en Nexo).
        await unidadTrabajo.GuardarCambiosAsync(CancellationToken.None);
        if (resultado.EsFallo)
        {
            logger.LogWarning("La cotización {Folio} quedó en error_envio: {Codigo}.", cotizacion.Folio, resultado.Error.Codigo);
        }
        else if (resultado.Valor.CosteoCompleto)
        {
            await LeerCosteoInmediatoAsync(cotizacion, usuarioId);
        }

        await auditoria.RegistrarAsync(
            HerramientaAuditoria,
            new
            {
                folio = cotizacion.Folio,
                reintento,
                linea_negocio_nexo = string.Join(", ", lineas.Select(l => l.LineaNegocioNexo).OfType<string>().Distinct(StringComparer.Ordinal)),
                lineas = lineas.Count,
            },
            new
            {
                estado_costeo = cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : null,
                costeo_id = cotizacion.SolicitudNexoId,
                motivo = cotizacion.MotivoEstadoCosteo,
                error = resultado.EsFallo ? resultado.Error.Codigo : null,
            },
            resultado.EsExitoso,
            (int)cronometro.ElapsedMilliseconds,
            cotizacion.ThreadId);
    }

    /// <summary>
    /// Nexo costeó todas las líneas en el alta (HU-013.14, opción A de ADR-018 §17): lee el costeo y aplica el precio con la regla del
    /// polling, ya con el <c>costeoId</c> guardado (si el proceso muere aquí, el polling la retoma). Guarda con la versión de negocio;
    /// si la lectura falla o el polling ganó la carrera, la cotización queda como está guardada y el polling la completa.
    /// </summary>
    private async Task LeerCosteoInmediatoAsync(clsCotizacionSolicitud cotizacion, Guid usuarioId)
    {
        var version = cotizacion.VersionNegocio;

        // Tiempo de espera corto y propio (CosteoNexo:LecturaInmediataSegundos): el alta ya esperó a Nexo; si no alcanza, el polling sigue.
        clsResultado<clsCosteoConsultadoNexo> consulta;
        using (var limite = new CancellationTokenSource(opciones.Value.LecturaInmediata))
        {
            try
            {
                consulta = await nexo.ConsultarCosteoAsync(usuarioId, cotizacion.SolicitudNexoId!, limite.Token);
            }
            catch (OperationCanceledException) when (limite.IsCancellationRequested)
            {
                logger.LogWarning("La lectura inmediata del costeo de {Folio} superó {Segundos} s: queda en pendiente_costeo y la completa el polling.",
                    cotizacion.Folio, opciones.Value.LecturaInmediataSegundos);
                return;
            }
        }

        if (consulta.EsFallo)
        {
            logger.LogWarning("No se pudo leer el costeo inmediato de {Folio} ({Codigo}): queda en pendiente_costeo y la completa el polling.",
                cotizacion.Folio, consulta.Error.Codigo);
            return;
        }

        if (clsLecturaCosteo.AplicarFoto(cotizacion, consulta.Valor, reloj.AhoraUtc, opciones.Value, precioCosteo))
        {
            cotizacion.IncrementarVersionNegocio();
        }

        if (!await unidadTrabajo.GuardarCambiosSiVersionAsync(cotizacion.Id, version, CancellationToken.None))
        {
            logger.LogInformation("La cotización {Folio} cambió durante el costeo inmediato: la completa el polling.", cotizacion.Folio);
            await cotizaciones.RecargarAsync(cotizacion, CancellationToken.None);
        }
    }

    private async Task<clsResultado<clsSolicitudCreadaNexo>> ResolverClienteYEnviarAsync(
        clsCotizacionSolicitud cotizacion,
        clsEncabezadoSolicitudCosteo encabezado,
        IReadOnlyList<clsLineaSolicitudCosteo> lineas,
        clsMargenPolitica? politica,
        IReadOnlyDictionary<int, clsMargenPolitica?> politicasRuta,
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

        await ResolverMargenAsync(cotizacion, politica, politicasRuta, bp, cancellationToken);

        var cliente = bp is not null ? clsClienteNexo.PorBp(bp) : clsClienteNexo.PorId(clienteId!.Value);
        return await nexo.EnviarSolicitudAsync(usuarioId, new clsSolicitudNexo(cliente, encabezado, lineas, politica?.MapeoLineasNexo), cancellationToken);
    }

    /// <summary>
    /// Margen de cada ruta al enviar (HU-005.7, ADR-015 §5; HU-021: el margen es de la línea de negocio de cada ruta): SAP TM con el BP
    /// del catálogo o la política de la línea (<see cref="IClsMargenResolverService"/>), una vez por línea de negocio. Se guarda para
    /// aplicarlo cuando llegue el costeo; si no se puede resolver, el polling lo advierte y nunca muestra el costo sin margen. Un margen
    /// negociado (re-solicitud, HU-013.4) no se pisa, falle o no la resolución.
    /// </summary>
    private async Task ResolverMargenAsync(
        clsCotizacionSolicitud cotizacion, clsMargenPolitica? politica, IReadOnlyDictionary<int, clsMargenPolitica?> politicasRuta, string? bp,
        CancellationToken cancellationToken)
    {
        var resueltos = new Dictionary<int, Margen.DTOs.clsMargenResuelto?>();
        foreach (var ruta in cotizacion.RutasOrdenadas)
        {
            var deRuta = ruta.LineaNegocioId is { } lineaId ? politicasRuta.GetValueOrDefault(lineaId) : politica;
            if (deRuta is null)
            {
                continue;
            }

            if (!resueltos.TryGetValue(deRuta.Id, out var resuelto))
            {
                var resultado = await margen.ResolverAsync(new Margen.DTOs.clsEntradaMargen(deRuta.LineaNegocio, bp), cancellationToken);
                resuelto = resultado.EsExitoso ? resultado.Valor : null;
                resueltos[deRuta.Id] = resuelto;
            }

            if (resuelto is null || !clsCodigoEnum.TryDesdeCodigo<OrigenMargen>(resuelto.Salida.Origen, out var origen))
            {
                logger.LogWarning("No se resolvió el margen de la ruta {Orden} de la cotización {Folio} al enviarla a Nexo.", ruta.Orden, cotizacion.Folio);
                continue;
            }

            // #139: el margen de SAP se redondea a la precisión de la columna (decimal(9,4), half-to-even como los demás márgenes) ANTES de
            // evaluar si es anómalo: lo que se evalúa es lo que se guarda (60,004 % → 0,6000, válido; -0,004 % → 0, válido).
            var margenSalida = origen == OrigenMargen.SapTm ? MargenDeColumna(resuelto.Salida.Margen) : resuelto.Salida.Margen;

            // HU-009 (#139, B1): un margen de SAP TM fuera de [0, 60 %] no se aplica a ciegas: la ruta usa el de la política de su línea y
            // el de SAP queda marcado como anómalo (visible en GET .../costeo con un aviso). Vale para la captura, la licitación, la
            // re-solicitud y la versión nueva, que pasan todas por aquí.
            if (origen == OrigenMargen.SapTm && clsReglasMargen.EsAnomalo(margenSalida) && resuelto.MargenPolitica is { } margenPolitica)
            {
                logger.LogWarning(
                    "SAP TM propuso un margen anómalo ({MargenSap}) para la ruta {Orden} de la cotización {Folio}: se aplica el de la política de la línea ({MargenPolitica}).",
                    margenSalida, ruta.Orden, cotizacion.Folio, margenPolitica);
                if (ruta.TieneMargenNegociado)
                {
                    ruta.RefrescarMargenSugerido(margenPolitica, margenPolitica, OrigenMargen.PoliticaLinea);
                }
                else
                {
                    ruta.RegistrarMargenCosteo(margenPolitica, margenPolitica, OrigenMargen.PoliticaLinea, resuelto.MetodoCalculo);
                }

                ruta.RegistrarMargenSapAnomalo(margenSalida);
                continue;
            }

            // HU-013.4 (decisión del 2026-10-06): un margen ajustado por el comercial (negociado) se conserva sobre el costo nuevo; el
            // sugerido se refresca con SAP TM o la política, como referencia.
            if (ruta.TieneMargenNegociado)
            {
                ruta.RefrescarMargenSugerido(resuelto.MargenPolitica, margenSalida, origen);
                continue;
            }

            ruta.RegistrarMargenCosteo(resuelto.MargenPolitica, margenSalida, origen, resuelto.MetodoCalculo);
        }
    }

    /// <summary>Margen a la precisión de la columna (<c>decimal(9,4)</c>, half-to-even), sin cero negativo.</summary>
    private static decimal MargenDeColumna(decimal margen)
    {
        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        return redondeado == 0m ? 0m : redondeado;
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
        return clsCotizacionSolicitud.Recortar(string.IsNullOrWhiteSpace(error.Detalle) ? error.Mensaje : $"{error.Mensaje} ({error.Detalle})");
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

    private static clsErrorDominio NoReintentable(clsCotizacionSolicitud cotizacion, string? detalle = null) =>
        new(clsCodigosError.CosteoNoReintentable,
            $"La cotización {cotizacion.Folio} no se puede reenviar a Nexo en su estado actual "
            + $"({(cotizacion.EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : "sin costeo en Nexo")}).",
            detalle);
}

/// <summary>
/// #191: lo que el almacén de idempotencia guarda de una captura exitosa (la tabla del 06 lo guarda como JSON): la repetición lee la
/// cotización por <see cref="Folio"/>.
/// </summary>
public sealed record clsCapturaHecha(Guid CotizacionId, string Folio);
