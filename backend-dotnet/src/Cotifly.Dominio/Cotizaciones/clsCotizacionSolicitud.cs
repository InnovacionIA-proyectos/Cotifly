using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Cabecera de la solicitud (<c>dbo_cotifly.CotizacionesSolicitud</c>, ex <c>Cotizaciones</c>; script 0009, HU-021, ADR-019): el
/// cliente, el estado general, el PDF, el seguimiento del costeo con Nexo y el precio total. Es la raíz del agregado: sus
/// <see cref="Rutas"/> (con sus camiones, mercancías y conexos) se leen y se guardan con ella.
/// <list type="bullet">
/// <item><see cref="PrecioFinal"/> = la suma de <see cref="clsLineaSolicitud.PrecioFinalLinea"/> de sus rutas, solo cuando todas tienen
/// precio (<see cref="RecalcularPrecio"/>); se guarda y se recalcula en la misma transacción que cambia una ruta.</item>
/// <item><see cref="EstadoCosteo"/> resume el de sus rutas (<see cref="clsResumenEstadoCosteo"/>), que a su vez resumen el de sus
/// camiones. Los estados de toda la solicitud (envío, error de envío, vencimiento) llegan a cada camión.</item>
/// <item>El margen es de cada ruta (de su línea de negocio). El ajuste del comercial es POR RUTA (HU-021.8, #135, decisión 6): la
/// petición indica la ruta (<see cref="ResolverRuta"/>), se valida contra el sugerido de esa ruta y solo se recalculan esa ruta y la
/// cabecera (<see cref="AplicarMargenNegociado"/>).</item>
/// </list>
/// Una nueva versión es una fila nueva con <see cref="Version"/> + 1 y folio <c>-vN</c>, sin FK a la original (HU-002.4 Fase 2).
/// <see cref="CreatedAt"/> y <see cref="UpdatedAt"/> los asigna la persistencia con el reloj de la aplicación.
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    public const string CodigoError = "COTIZACION_INVALIDA";
    public const string MonedaPesos = "COP";
    public const int LargoMaximoFolio = 50;
    public const int LargoMaximoMotivoCosteo = 1000;
    public const int LargoMaximoIdNexo = 64;

    /// <summary>
    /// Una cotización que quedó en <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/> solo se puede reenviar pasado este
    /// tiempo: antes, el envío original puede seguir esperando la respuesta de Nexo (hasta 30 s) y reenviar duplicaría la solicitud.
    /// </summary>
    public static readonly TimeSpan EsperaReenvioPendiente = TimeSpan.FromMinutes(2);

    private readonly List<clsLineaSolicitud> _rutas = [];

    private clsCotizacionSolicitud()
    {
    }

    public Guid Id { get; private set; }
    public string Folio { get; private set; } = string.Empty;
    public string? ClienteNombre { get; private set; }
    public string? NitCliente { get; private set; }
    public OrigenCliente? OrigenCliente { get; private set; }

    /// <summary>Precio al cliente de toda la solicitud: la suma del precio de sus rutas.</summary>
    public decimal? PrecioFinal { get; private set; }
    public string? Moneda { get; private set; }
    public decimal TrmAplicada { get; private set; }
    public string? PdfPath { get; private set; }

    /// <summary>Estado del costeo de TODA la solicitud: resumen de sus rutas (§9.2).</summary>
    public EstadoCosteo? EstadoCosteo { get; private set; }
    public EstadoEnvio? EstadoEnvio { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public string? SpaceId { get; private set; }
    public string? ThreadId { get; private set; }
    public bool? Ganada { get; private set; }
    public string? MotivoRechazo { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset? MarcadoAt { get; private set; }
    public DateTimeOffset? CosteoListoAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Tipo de la solicitud (<c>spot</c> o <c>licitacion</c>); <c>null</c> si no pasó por el formulario de costeo.</summary>
    public TipoSolicitud? TipoSolicitud { get; private set; }
    public DateTimeOffset? Deadline { get; private set; }

    /// <summary>Encabezado: <c>notas</c> para la Mesa de Nexo (script 0007, G7 de HU-013.10).</summary>
    public string? Notas { get; private set; }

    // Seguimiento de la integración con Nexo (scripts 0004 y 0010, ADR-015). Cotifly consulta a Nexo una vez por solicitud completa.

    /// <summary>
    /// Id de la SOLICITUD en Nexo (<c>solicitudNexoId</c>, script 0010; ex <c>costeoId</c>): lo devuelve
    /// <c>POST /api/captura-solicitudes</c> y lo consulta el polling (<c>GET /api/solicitudes/{id}</c>).
    /// </summary>
    public string? SolicitudNexoId { get; private set; }

    /// <summary>Id de la COTIZACIÓN (borrador o entregada) de Nexo; lo exigen el resultado y los conexos.</summary>
    public string? CotizacionNexoId { get; private set; }

    /// <summary>
    /// HU-018.1 (#155): <c>scorePrioridad</c> que Nexo devolvió al crear la solicitud (<c>201 CapturaResultDto</c> de
    /// <c>POST /api/captura-solicitudes</c>, script <c>db/scripts/09</c>). <c>null</c> si Nexo no lo mandó, si todavía no se envió o si
    /// se envió antes del 09. Se guarda redondeado a <see cref="DecimalesScorePrioridad"/> decimales; uno fuera de
    /// ±<see cref="MaximoScorePrioridad"/> (lo que cabe en <c>decimal(9,4)</c>) se descarta. Consultarlo después por solicitud
    /// (<c>scoreUrgencia</c>) sigue bloqueado por el contrato de Nexo (ADR-016).
    /// </summary>
    public decimal? ScorePrioridad { get; private set; }

    /// <summary>Decimales con que se guarda <see cref="ScorePrioridad"/> (<c>decimal(9,4)</c>).</summary>
    public const int DecimalesScorePrioridad = 4;

    /// <summary>Valor absoluto máximo de <see cref="ScorePrioridad"/> que cabe en <c>decimal(9,4)</c>.</summary>
    public const decimal MaximoScorePrioridad = 99999.9999m;

    /// <summary>Motivo legible de <see cref="EstadoCosteo"/> (error al enviar, rechazo de Nexo, costeo vencido...).</summary>
    public string? MotivoEstadoCosteo { get; private set; }
    public DateTimeOffset? CosteoEnviadoAt { get; private set; }

    /// <summary>Agenda del polling (HU-005.6): <c>null</c> = no se consulta más.</summary>
    public DateTimeOffset? CosteoSiguienteConsultaAt { get; private set; }

    /// <summary>Consultas seguidas a Nexo que fallaron (espaciado del polling y tope de la relectura, HU-013.14).</summary>
    public int CosteoConsultasFallidas { get; private set; }

    /// <summary>
    /// Versión de negocio (<c>versionNegocio</c>, ADR-018 §12): sube solo con los cambios que ve el comercial (margen, conexos, textos,
    /// re-solicitud, PDF y el polling cuando cambia el estado, el costo o el precio), nunca con el reclamo ni la agenda del polling. Viaja
    /// como <c>ETag</c> y vuelve en <c>If-Match</c>.
    /// </summary>
    public int VersionNegocio { get; private set; }

    /// <summary>Asunto e introducción del PDF guardados (JSON, HU-013.5, #101); <c>null</c> si todavía no hay.</summary>
    public string? TextosCotizacion { get; private set; }

    /// <summary>
    /// Una acción posterior al PDF (margen, conexos, textos, re-solicitud) lo invalidó: <see cref="PdfPath"/> quedó en <c>null</c> y el
    /// comercial lo regenera (ADR-018 §11). Generar el PDF lo apaga.
    /// </summary>
    public bool PdfDesactualizado { get; private set; }

    /// <summary>Rutas de la solicitud (ver <see cref="RutasOrdenadas"/>).</summary>
    public IReadOnlyList<clsLineaSolicitud> Rutas => _rutas;

    /// <summary>Rutas en su orden.</summary>
    public IReadOnlyList<clsLineaSolicitud> RutasOrdenadas => [.. _rutas.OrderBy(r => r.Orden)];

    /// <summary>
    /// La ruta 1 (o <c>null</c> sin rutas). Lo que la solicitud muestra como "su" línea de negocio, margen y ruta mientras el contrato no
    /// sea por ruta (adaptación de HU-021, #132-#137).
    /// </summary>
    public clsLineaSolicitud? PrimeraRuta => _rutas.Count == 0 ? null : _rutas.MinBy(r => r.Orden);

    /// <summary>Todos los camiones de la solicitud, por ruta y en orden.</summary>
    public IReadOnlyList<clsVehiculoLineaSolicitud> Vehiculos => [.. RutasOrdenadas.SelectMany(r => r.VehiculosOrdenados)];

    /// <summary>¿Tiene el costeo de Nexo con precio? (<c>autocosteo</c> o <c>entregada</c> y <see cref="PrecioFinal"/>.)</summary>
    public bool TieneCostoDisponible =>
        PrecioFinal is not null && EstadoCosteo is { } estado && clsDerivacionEstadoCosteo.TieneCosteo(estado);

    /// <summary>
    /// ¿Ya se entregó al cliente? (enviada o reenviada por correo, o con resultado ganada/perdida.) Entonces el PDF es la foto de lo
    /// entregado y la cotización solo cambia con una versión nueva (ADR-018 §11).
    /// </summary>
    public bool EntregadaAlCliente =>
        EstadoEnvio is Cotizaciones.EstadoEnvio.Enviada or Cotizaciones.EstadoEnvio.Reenviada || Ganada is not null;

    /// <summary>¿Alguna ruta tiene margen negociado por el comercial?</summary>
    public bool TieneMargenNegociado => _rutas.Any(r => r.TieneMargenNegociado);

    /// <summary>
    /// Crea la cabecera validando las invariantes baratas: folio obligatorio (máx. 50), versión ≥ 1 y TRM 0 en pesos (&gt; 0 en otra
    /// moneda). Sin rutas: el caso de uso las agrega (<see cref="RegistrarSolicitudCosteo"/>, <see cref="RegistrarSolicitudLicitacion"/>,
    /// <see cref="RegistrarCotizacionChat"/>).
    /// </summary>
    public static clsResultado<clsCotizacionSolicitud> Crear(clsDatosCotizacion datos)
    {
        ArgumentNullException.ThrowIfNull(datos);

        var moneda = string.IsNullOrWhiteSpace(datos.Moneda) ? MonedaPesos : datos.Moneda.Trim().ToUpperInvariant();
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "folio", datos.Folio, LargoMaximoFolio),
            datos.Version < 1 ? new clsErrorDominio(CodigoError, "La versión de la cotización debe ser 1 o mayor.") : null,
            moneda.Length != 3 ? new clsErrorDominio(CodigoError, "La moneda debe ser un código ISO de 3 letras (ej. COP).") : null,
            ValidarTrm(moneda, datos.TrmAplicada));

        if (error is not null)
        {
            return error;
        }

        return new clsCotizacionSolicitud
        {
            Id = Guid.NewGuid(),
            Folio = datos.Folio.Trim(),
            Version = datos.Version,
            ClienteNombre = clsValidacion.Normalizar(datos.ClienteNombre),
            NitCliente = clsValidacion.Normalizar(datos.NitCliente),
            OrigenCliente = datos.OrigenCliente,
            Moneda = moneda,
            TrmAplicada = datos.TrmAplicada,
            PdfPath = clsValidacion.Normalizar(datos.PdfPath),
            EstadoCosteo = datos.EstadoCosteo,
            EstadoEnvio = Cotizaciones.EstadoEnvio.SinEnviar,
            UsuarioId = datos.UsuarioId,
            SpaceId = clsValidacion.Normalizar(datos.SpaceId),
            ThreadId = clsValidacion.Normalizar(datos.ThreadId),
            CosteoListoAt = datos.CosteoListoAt,
        };
    }

    // ------------------------------------------------------------------ Captura

    /// <summary>
    /// Guarda el formulario de una línea (ya validado con <see cref="clsSolicitudCosteo.Validar"/>): el encabezado en la cabecera y la
    /// ruta 1 con su línea de negocio, sus mercancías y un camión con el vehículo del formulario (adaptación "una ruta, un camión" hasta el
    /// cubicador, #133). Todo queda en <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/>: lo que llenó el comercial nunca se pierde
    /// aunque Nexo no responda (HU-005.5). Devuelve la ruta.
    /// </summary>
    public clsLineaSolicitud RegistrarSolicitudCosteo(clsSolicitudCosteo solicitud, int? lineaNegocioId)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        if (solicitud.Validar() is { } error)
        {
            throw new ArgumentException(error.Mensaje, nameof(solicitud));
        }

        RegistrarEncabezado(solicitud.Encabezado);
        var ruta = AgregarRuta(solicitud.ALinea(), rutaOrigenIndice: 0, lineaNegocioId, solicitud.Encabezado);
        RegistrarEstadoSolicitud(Cotizaciones.EstadoCosteo.PendienteEnvio);
        return ruta;
    }

    /// <summary>
    /// Guarda una licitación (HU-011.2, ADR-017; validada fila por fila): el encabezado y una ruta por línea, cada una con
    /// <see cref="clsLineaSolicitud.RutaOrigenIndice"/> = su fila del Excel (<paramref name="filas"/>, en el mismo orden; <c>null</c> sin
    /// archivo), la línea de negocio, sus mercancías y un camión. Todo queda en <c>pendiente_envio</c>. Devuelve las rutas.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitud> RegistrarSolicitudLicitacion(
        clsEncabezadoSolicitudCosteo encabezado, IReadOnlyList<clsLineaSolicitudCosteo> lineas, IReadOnlyList<int?>? filas, int? lineaNegocioId)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        ArgumentNullException.ThrowIfNull(lineas);
        if (encabezado.Errores() is { Count: > 0 } errores)
        {
            throw new ArgumentException(errores[0].Mensaje, nameof(encabezado));
        }

        if (filas is not null && filas.Count != lineas.Count)
        {
            throw new ArgumentException("Debe haber una fila (o null) por línea.", nameof(filas));
        }

        RegistrarEncabezado(encabezado);
        var rutas = lineas.Select((l, i) => AgregarRuta(l, filas?[i], lineaNegocioId, encabezado)).ToList();
        RegistrarEstadoSolicitud(Cotizaciones.EstadoCosteo.PendienteEnvio);
        return rutas;
    }

    /// <summary>
    /// Captura por rutas (HU-021.6, formulario y licitación): guarda el encabezado y, por cada ruta en orden, la ruta con su línea de
    /// negocio, sus mercancías, sus camiones (<see cref="clsRutaCapturada.Camiones"/>, al menos uno: sin vehículo mientras no haya
    /// cubicador) y sus conexos (precio = costo unitario de Nexo, decisión 3), y recalcula sus totales (los conexos ya suman al costo
    /// de la ruta; sin flete no hay precio). Todo queda en <c>pendiente_envio</c>. Las líneas deben venir validadas
    /// (<see cref="clsLineaSolicitudCosteo.Errores"/> y <see cref="clsLineaSolicitudCosteo.ErroresCaptura"/> vacías): si no, lanza.
    /// <paramref name="reglasCaptura"/> en <c>false</c> = copia de rutas ya guardadas (versión <c>-vN</c> de la re-solicitud, HU-021.9):
    /// solo se exigen los límites de Nexo, porque las rutas migradas no tienen los ids que pide la captura nueva. Devuelve las rutas creadas.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitud> RegistrarCaptura(
        clsEncabezadoSolicitudCosteo encabezado, IReadOnlyList<clsRutaCapturada> rutas, bool reglasCaptura = true)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        ArgumentNullException.ThrowIfNull(rutas);
        if (_rutas.Count > 0)
        {
            throw new InvalidOperationException($"La cotización {Folio} ya tiene rutas.");
        }

        if (rutas.Count == 0)
        {
            throw new ArgumentException("La captura debe traer al menos una ruta.", nameof(rutas));
        }

        if (encabezado.Errores() is { Count: > 0 } erroresEncabezado)
        {
            throw new ArgumentException(erroresEncabezado[0].Mensaje, nameof(encabezado));
        }

        foreach (var (capturada, i) in rutas.Select((r, i) => (r, i)))
        {
            ArgumentNullException.ThrowIfNull(capturada);
            if (capturada.Linea.Errores().Concat(reglasCaptura ? capturada.Linea.ErroresCaptura() : []).FirstOrDefault() is { } error)
            {
                throw new ArgumentException($"Ruta {i + 1}: {error.Mensaje}", nameof(rutas));
            }

            if (capturada.Camiones is not { Count: > 0 })
            {
                throw new ArgumentException($"Ruta {i + 1}: debe tener al menos un camión.", nameof(rutas));
            }
        }

        RegistrarEncabezado(encabezado);
        var creadas = new List<clsLineaSolicitud>(rutas.Count);
        foreach (var capturada in rutas)
        {
            var orden = _rutas.Count == 0 ? 1 : _rutas.Max(r => r.Orden) + 1;
            var ruta = clsLineaSolicitud.Desde(
                Id, orden, capturada.RutaOrigenIndice, capturada.LineaNegocioId, capturada.Linea, encabezado, capturada.RutaId);
            foreach (var camion in capturada.Camiones)
            {
                ruta.AgregarVehiculo(camion);
            }

            ruta.RegistrarCubicacion(capturada.Cubicacion, capturada.AvisosVehiculo);
            if (capturada.CubicacionPendiente)
            {
                ruta.MarcarCubicacionPendiente();
            }

            foreach (var conexo in capturada.Conexos ?? [])
            {
                ruta.AgregarOActualizarConexo(conexo);
            }

            _rutas.Add(ruta);
            creadas.Add(ruta);
        }

        RegistrarEstadoSolicitud(Cotizaciones.EstadoCosteo.PendienteEnvio);
        foreach (var ruta in creadas)
        {
            ruta.RecalcularTotales(Moneda, TrmAplicada);
        }

        return creadas;
    }

    /// <summary>
    /// Cotización del agente (chat, sin Nexo, <c>CAMBIOS_BACKEND</c> §2): una ruta con la línea de negocio, el margen y el precio, y un
    /// camión con el costo del flete. Sin estado de costeo.
    /// </summary>
    public clsLineaSolicitud RegistrarCotizacionChat(
        int? lineaNegocioId, decimal? costo, decimal? margenPolitica, decimal? margenAplicado, OrigenMargen? origen, string? metodoCalculo,
        decimal? precioFinal)
    {
        if (_rutas.Count > 0)
        {
            throw new InvalidOperationException($"La cotización {Folio} ya tiene rutas.");
        }

        if (clsValidacion.Primero(
                clsValidacion.NoNegativo(CodigoError, "costo", costo),
                clsValidacion.NoNegativo(CodigoError, "precioFinal", precioFinal),
                clsReglaMargen.Validar(CodigoError, "margenPolitica", margenPolitica, metodoCalculo),
                clsReglaMargen.Validar(CodigoError, "margenAplicado", margenAplicado, metodoCalculo)) is { } error)
        {
            throw new ArgumentException(error.Mensaje, nameof(costo));
        }

        var ruta = clsLineaSolicitud.DelChat(Id, lineaNegocioId, costo, margenPolitica, margenAplicado, origen, metodoCalculo, precioFinal);
        _rutas.Add(ruta);
        PrecioFinal = precioFinal;
        return ruta;
    }

    /// <summary>Valida los datos de la cotización del chat sin crearla (mismas reglas que <see cref="RegistrarCotizacionChat"/>).</summary>
    public static clsErrorDominio? ValidarCotizacionChat(decimal? costo, decimal? margenPolitica, decimal? margenAplicado, string? metodoCalculo, decimal? precioFinal) =>
        clsValidacion.Primero(
            clsValidacion.NoNegativo(CodigoError, "costo", costo),
            clsValidacion.NoNegativo(CodigoError, "precioFinal", precioFinal),
            clsReglaMargen.Validar(CodigoError, "margenPolitica", margenPolitica, metodoCalculo),
            clsReglaMargen.Validar(CodigoError, "margenAplicado", margenAplicado, metodoCalculo));

    /// <summary>
    /// Agrega una ruta (orden siguiente) a partir de una línea de la captura ya validada, con sus mercancías y un camión con el vehículo,
    /// la carrocería y el peso de la línea. Devuelve la ruta.
    /// </summary>
    public clsLineaSolicitud AgregarRuta(clsLineaSolicitudCosteo linea, int? rutaOrigenIndice, int? lineaNegocioId, clsEncabezadoSolicitudCosteo? encabezado = null)
    {
        ArgumentNullException.ThrowIfNull(linea);
        var orden = _rutas.Count == 0 ? 1 : _rutas.Max(r => r.Orden) + 1;
        var ruta = clsLineaSolicitud.Desde(Id, orden, rutaOrigenIndice, lineaNegocioId, linea, encabezado);
        ruta.AgregarVehiculo(linea.TipoVehiculo, linea.Carroceria, linea.PesoKg);
        _rutas.Add(ruta);
        return ruta;
    }

    private void RegistrarEncabezado(clsEncabezadoSolicitudCosteo encabezado)
    {
        TipoSolicitud = encabezado.Tipo;
        Deadline = encabezado.Deadline;
        Notas = clsValidacion.Normalizar(encabezado.Notas);
    }

    /// <summary>
    /// Encabezado de la solicitud guardada, o <c>null</c> si la cotización no pasó por el costeo en Nexo. Las horas son de cada ruta: el
    /// encabezado guardado no las trae.
    /// </summary>
    public clsEncabezadoSolicitudCosteo? EncabezadoSolicitud => TipoSolicitud is { } tipo
        ? new clsEncabezadoSolicitudCosteo { Tipo = tipo, Deadline = Deadline, Notas = Notas }
        : null;

    /// <summary>
    /// Las líneas que viajan a Nexo: una por camión, ruta por ruta y en orden (<c>CAMBIOS_BACKEND</c> §5.3), cada una con el camión al
    /// que corresponde. HU-028.1 (#183, decisión del 2026-10-20): los camiones rechazados por la Mesa NO viajan
    /// (<see cref="clsLineaSolicitud.VehiculosQueViajan"/>), y una ruta con todos rechazados no aporta ninguna línea.
    /// </summary>
    public IReadOnlyList<(clsVehiculoLineaSolicitud Camion, clsLineaSolicitudCosteo Linea)> LineasNexo() =>
        [.. RutasOrdenadas.SelectMany(r => r.VehiculosOrdenados.Where(v => r.VehiculosQueViajan.Contains(v)).Select(v => (v, r.ALineaNexo(v))))];

    /// <summary>
    /// HU-028.1 (#183): ¿alguna ruta manda líneas a Nexo? Con todas sus rutas sin camiones que viajen (todos rechazados por la Mesa de Costeo)
    /// no hay nada que enviar: la re-solicitud y la edición responden 409 <c>SIN_RUTAS_COTIZABLES</c> antes de escribir.
    /// </summary>
    public bool TieneLineasQueViajan => _rutas.Any(r => r.ViajaANexo);

    // ------------------------------------------------------------------ Envío y polling (HU-005.5, HU-005.6)

    /// <summary>
    /// ¿Se puede (re)enviar la solicitud a Nexo? Sí desde <see cref="Cotizaciones.EstadoCosteo.ErrorEnvio"/>, y desde
    /// <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/> cuando el último cambio tiene más de
    /// <see cref="EsperaReenvioPendiente"/> (un envío que se cortó a medias, HU-005.5).
    /// </summary>
    public bool PuedeReenviarCosteo(DateTimeOffset ahora) =>
        Ganada is null
        && !CubicacionEnCurso
        && ((EstadoCosteo == Cotizaciones.EstadoCosteo.ErrorEnvio && !ErrorEnvioDefinitivo)
            || (EstadoCosteo == Cotizaciones.EstadoCosteo.PendienteEnvio && ahora - UpdatedAt >= EsperaReenvioPendiente));

    /// <summary>
    /// HU-023.3 (#158): ¿se pueden reemplazar los camiones (recalcular una ruta con el cubicador)? Solo si la solicitud nunca llegó a
    /// Nexo (<see cref="SolicitudNexoId"/> vacío) y no hay un envío en curso ni incierto: la misma regla que
    /// <see cref="PuedeReenviarCosteo"/> (un <c>pendiente_envio</c> reciente puede estar esperando a Nexo).
    /// </summary>
    public bool PuedeRecalcularCamiones(DateTimeOffset ahora) => SolicitudNexoId is null && PuedeReenviarCosteo(ahora);

    // ------------------------------------------------------------------ Cubicación en segundo plano (HU-023.5, #160)

    /// <summary>
    /// HU-023.5 (#160): ¿alguna ruta espera la cubicación en segundo plano o falló? Mientras tanto la solicitud no viaja a Nexo (ni por el
    /// reintento ni por el recálculo de una ruta): "todo o nada" hacia Nexo.
    /// </summary>
    public bool CubicacionEnCurso =>
        _rutas.Any(r => r.EstadoCubicacion is Cotizaciones.EstadoCubicacion.Pendiente or Cotizaciones.EstadoCubicacion.Fallida);

    /// <summary>
    /// HU-023.5: ¿la cubicación en segundo plano terminó y la solicitud todavía no salió a Nexo? (alguna ruta pasó por la cola, todas
    /// quedaron cubicadas y sigue en <c>pendiente_envio</c> sin <see cref="SolicitudNexoId"/>.)
    /// </summary>
    public bool ListaParaEnviarTrasCubicar =>
        Ganada is null && EstadoCosteo == Cotizaciones.EstadoCosteo.PendienteEnvio && SolicitudNexoId is null
        && _rutas.Any(r => r.EstadoCubicacion is not null) && !CubicacionEnCurso;

    /// <summary>
    /// HU-023.5: ¿la ruta se puede cubicar en segundo plano? La solicitud no ha salido a Nexo, sigue en <c>pendiente_envio</c> y la ruta
    /// está pendiente. Si no, el procesador la marca fallida (la cotización cambió por otro camino).
    /// </summary>
    public bool AdmiteCubicacionEnSegundoPlano(Guid rutaId) =>
        Ganada is null && EstadoCosteo == Cotizaciones.EstadoCosteo.PendienteEnvio && SolicitudNexoId is null
        && _rutas.Any(r => r.Id == rutaId && r.EstadoCubicacion == Cotizaciones.EstadoCubicacion.Pendiente);

    /// <summary>
    /// HU-023.5: guarda el plan del cubicador en la ruta pendiente <paramref name="rutaId"/> (reemplaza el camión provisional) y recalcula
    /// los totales. <c>ESTADO_NO_PERMITE</c> si la ruta ya no se puede cubicar (<see cref="AdmiteCubicacionEnSegundoPlano"/>).
    /// </summary>
    public clsResultado<clsLineaSolicitud> CompletarCubicacionDeRuta(Guid rutaId, clsPlanRuta plan, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!AdmiteCubicacionEnSegundoPlano(rutaId))
        {
            return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"La ruta {rutaId} de la cotización {Folio} ya no espera la cubicación (la solicitud cambió de estado o la ruta ya se cubicó).");
        }

        var ruta = _rutas.First(r => r.Id == rutaId);
        ruta.CompletarCubicacion(plan);
        var recalculo = RecalcularTotales(ahora);
        return recalculo.EsFallo ? recalculo.Error : ruta;
    }

    /// <summary>
    /// HU-023.5: registra un intento fallido de cubicar la ruta <paramref name="rutaId"/> (ver <see cref="clsLineaSolicitud.RegistrarFalloCubicacion"/>).
    /// No hace nada si la ruta no es de esta cotización o ya no está pendiente.
    /// </summary>
    public bool RegistrarFalloCubicacionDeRuta(Guid rutaId, string codigo, string mensaje, DateTimeOffset? reintentarEn)
    {
        if (_rutas.FirstOrDefault(r => r.Id == rutaId) is not { EstadoCubicacion: Cotizaciones.EstadoCubicacion.Pendiente } ruta)
        {
            return false;
        }

        ruta.RegistrarFalloCubicacion(codigo, mensaje, reintentarEn);
        return true;
    }

    /// <summary>
    /// Opción C: el cubicador estaba ocupado para la ruta <paramref name="rutaId"/>; vuelve a la cola desde <paramref name="disponibleAt"/>
    /// sin gastar un intento (<see cref="clsLineaSolicitud.PosponerCubicacion"/>). No hace nada si la ruta no es de esta cotización o ya no
    /// está pendiente.
    /// </summary>
    public bool PosponerCubicacionDeRuta(Guid rutaId, DateTimeOffset disponibleAt)
    {
        if (_rutas.FirstOrDefault(r => r.Id == rutaId) is not { EstadoCubicacion: Cotizaciones.EstadoCubicacion.Pendiente } ruta)
        {
            return false;
        }

        ruta.PosponerCubicacion(disponibleAt);
        return true;
    }

    /// <summary>
    /// HU-023.5: el comercial reintenta las rutas fallidas (vuelven a la cola) o, con <paramref name="usarRespaldo"/>, las acepta con el
    /// respaldo de cada una en <paramref name="respaldos"/> (por id de ruta: el camión con el vehículo elegido y su aviso, HU-032) o, si no
    /// trae, con el camión sin vehículo. Devuelve cuántas cambió (0 = no había fallidas o la solicitud ya salió a Nexo).
    /// </summary>
    public int ReintentarCubicacionFallida(bool usarRespaldo, IReadOnlyDictionary<Guid, clsPlanRuta>? respaldos = null)
    {
        if (!AdmiteReintentoCubicacion)
        {
            return 0;
        }

        return _rutas.Count(r => r.ReintentarCubicacion(usarRespaldo, respaldos is not null && respaldos.TryGetValue(r.Id, out var plan) ? plan : null));
    }

    /// <summary>HU-023.5: ¿se pueden reintentar las rutas fallidas? Sin resultado, sin enviar a Nexo y en <c>pendiente_envio</c>.</summary>
    public bool AdmiteReintentoCubicacion =>
        Ganada is null && SolicitudNexoId is null && EstadoCosteo == Cotizaciones.EstadoCosteo.PendienteEnvio;

    /// <summary>¿La cotización nació del flujo de costeo en Nexo? (<see cref="EstadoCosteo"/> con valor).</summary>
    public bool EnFlujoCosteo => EstadoCosteo is not null;

    /// <summary>
    /// ¿El resultado ganada/perdida se marca solo en Cotifly? Sí fuera del flujo de costeo (chat) y en <c>descartada</c> o
    /// <c>costeo_vencido</c>, donde Nexo no acepta un resultado (HU-005.14).
    /// </summary>
    public bool ResultadoSoloLocal =>
        EstadoCosteo is null or Cotizaciones.EstadoCosteo.Descartada or Cotizaciones.EstadoCosteo.CosteoVencido;

    /// <summary>Prefijo del motivo de un <c>error_envio</c> definitivo (Nexo rechazó los datos con 400: reenviarlos igual no sirve).</summary>
    public const string PrefijoRechazoDefinitivo = "Rechazo definitivo de Nexo (corrige el formulario en una versión nueva): ";

    /// <summary>¿El <c>error_envio</c> es definitivo? (400 de validación de Nexo: no se reintenta.)</summary>
    public bool ErrorEnvioDefinitivo =>
        EstadoCosteo == Cotizaciones.EstadoCosteo.ErrorEnvio
        && MotivoEstadoCosteo?.StartsWith(PrefijoRechazoDefinitivo, StringComparison.Ordinal) == true;

    /// <summary>Nombre y origen oficiales del catálogo de Nexo (ADR-005 §4: el cliente sale del catálogo, no del formulario).</summary>
    public void ActualizarClienteDelCatalogo(string nombre, Clientes.OrigenCliente? origen)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nombre);
        ClienteNombre = nombre.Trim();
        OrigenCliente = origen ?? OrigenCliente;
    }

    /// <summary>
    /// Nexo recibió la solicitud (HU-005.5): guarda su id (<see cref="SolicitudNexoId"/>), deja la solicitud y sus camiones en
    /// <see cref="Cotizaciones.EstadoCosteo.Autocosteo"/> si Nexo ya costeó todo o en <see cref="Cotizaciones.EstadoCosteo.PendienteCosteo"/>
    /// si no, y agenda la primera consulta del polling (HU-005.6).
    /// </summary>
    /// <param name="scorePrioridad">HU-018.1 (#155): el <c>scorePrioridad</c> de la respuesta de Nexo (ver <see cref="ScorePrioridad"/>);
    /// <c>null</c> lo deja vacío.</param>
    public void RegistrarEnvioCosteo(
        string solicitudNexoId, bool costeoCompleto, DateTimeOffset enviadoAt, DateTimeOffset primeraConsulta, decimal? scorePrioridad = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solicitudNexoId);
        SolicitudNexoId = RecortarId(solicitudNexoId);
        ScorePrioridad = NormalizarScorePrioridad(scorePrioridad);
        RegistrarEstadoSolicitud(costeoCompleto ? Cotizaciones.EstadoCosteo.Autocosteo : Cotizaciones.EstadoCosteo.PendienteCosteo);
        MotivoEstadoCosteo = null;
        CosteoEnviadoAt = enviadoAt;
        CosteoSiguienteConsultaAt = primeraConsulta;
        CosteoConsultasFallidas = 0;
    }

    /// <summary>
    /// HU-018.1 (#155): <paramref name="score"/> redondeado a <see cref="DecimalesScorePrioridad"/> decimales, o <c>null</c> si no vino o
    /// no cabe en <c>decimal(9,4)</c> (un valor raro de Nexo no debe tumbar el registro del envío).
    /// </summary>
    public static decimal? NormalizarScorePrioridad(decimal? score)
    {
        if (score is not { } valor)
        {
            return null;
        }

        var redondeado = Math.Round(valor, DecimalesScorePrioridad, MidpointRounding.AwayFromZero);
        return Math.Abs(redondeado) <= MaximoScorePrioridad ? redondeado : null;
    }

    /// <summary>
    /// Nexo no recibió la solicitud (tiempo agotado, 4xx, 5xx, red, cliente sin BP): la solicitud y sus camiones quedan en
    /// <see cref="Cotizaciones.EstadoCosteo.ErrorEnvio"/> con el motivo (recortado a <see cref="LargoMaximoMotivoCosteo"/>) y sin
    /// polling; el comercial la reintenta sin volver a llenar el formulario.
    /// </summary>
    public void RegistrarErrorEnvioCosteo(string motivo, bool definitivo = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        RegistrarEstadoSolicitud(Cotizaciones.EstadoCosteo.ErrorEnvio);
        MotivoEstadoCosteo = Recortar(definitivo ? PrefijoRechazoDefinitivo + motivo.Trim() : motivo);
        CosteoSiguienteConsultaAt = null;
    }

    /// <summary>Código del motivo cuando la solicitud quedó "en Nexo" sin su id (migración sin respaldo, ADR-019).</summary>
    public const string CodigoSolicitudNexoDesconocida = "SOLICITUD_NEXO_DESCONOCIDA";

    /// <summary>Motivo de <see cref="MarcarSolicitudNexoDesconocida"/>.</summary>
    public const string MotivoSolicitudNexoDesconocida =
        CodigoSolicitudNexoDesconocida + ": Cotifly no tiene el id de la solicitud en Nexo (cotización migrada sin respaldo), así que no "
        + "puede consultar el costeo. Reintenta el envío o re-solicita el costeo; si el cliente desistió, márcala perdida.";

    /// <summary>¿Está "en Nexo" (<c>pendiente_costeo</c> o <c>autocosteo</c>) sin <see cref="SolicitudNexoId"/>? Nadie la puede consultar.</summary>
    public bool SolicitudNexoDesconocida =>
        SolicitudNexoId is null && EstadoCosteo is Cotizaciones.EstadoCosteo.PendienteCosteo or Cotizaciones.EstadoCosteo.Autocosteo;

    /// <summary>
    /// Sale del callejón de <see cref="SolicitudNexoDesconocida"/> (ADR-019): la deja en <c>error_envio</c> (no definitivo) con
    /// <see cref="MotivoSolicitudNexoDesconocida"/>, y desde ahí funcionan el reintento, la re-solicitud y la perdida solo en Cotifly
    /// (#127). Devuelve <c>true</c> si la marcó.
    /// </summary>
    public bool MarcarSolicitudNexoDesconocida()
    {
        if (!SolicitudNexoDesconocida)
        {
            return false;
        }

        RegistrarErrorEnvioCosteo(MotivoSolicitudNexoDesconocida);
        return true;
    }

    /// <summary>¿El polling debe consultar esta cotización? (tiene <see cref="SolicitudNexoId"/> y no está en un estado terminal.)</summary>
    public bool SeConsultaEnNexo =>
        SolicitudNexoId is not null
        && EstadoCosteo is Cotizaciones.EstadoCosteo.PendienteCosteo or Cotizaciones.EstadoCosteo.Autocosteo;

    /// <summary>
    /// ¿Quedó en <c>entregada</c> sin precio y sin PDF? (HU-013.14, ADR-018 §17.) El polling la vuelve a leer una vez (aunque
    /// <c>entregada</c> sea terminal y aunque haya pasado la edad máxima) para aplicarle el precio.
    /// </summary>
    public bool EntregadaSinPrecio =>
        SolicitudNexoId is not null && EstadoCosteo == Cotizaciones.EstadoCosteo.Entregada && PrecioFinal is null && PdfPath is null
        && !TodasLasRutasRechazadas; // HU-028.1 (#183): sin rutas que cotizar no hay precio que completar (no se relee)

    // ------------------------------------------------------------------ Rechazo de líneas en la Mesa de Costeo (HU-028.1, #183)

    /// <summary>
    /// HU-028.1 (#183): las rutas que cuentan en el precio, el PDF y el Excel: todas menos las <c>rechazada</c> (todos sus camiones
    /// rechazados por la Mesa de Costeo de Nexo), en su orden.
    /// </summary>
    public IReadOnlyList<clsLineaSolicitud> RutasCotizables => [.. RutasOrdenadas.Where(r => !r.Rechazada)];

    /// <summary>HU-028.1 (#183): ¿todas sus rutas quedaron rechazadas? (con al menos una ruta). Sin precio; el PDF responde <c>SIN_RUTAS_COTIZABLES</c>.</summary>
    public bool TodasLasRutasRechazadas => _rutas.Count > 0 && _rutas.All(r => r.Rechazada);

    /// <summary>HU-028.1 (#183): motivo visible en "Mis cotizaciones" cuando la Mesa de Costeo rechazó todas las rutas.</summary>
    public const string MotivoTodasLasRutasRechazadas =
        "La Mesa de Costeo de Nexo rechazó todas las rutas: la cotización no tiene precio. Revisa el motivo de cada ruta en el costeo.";

    /// <summary>
    /// HU-028.1 (#183): deja (o quita) en <see cref="MotivoEstadoCosteo"/> el aviso de <see cref="MotivoTodasLasRutasRechazadas"/>. Solo lo
    /// escribe si no hay otro motivo (no tapa una advertencia del costeo) y solo quita el suyo. Devuelve si cambió.
    /// </summary>
    public bool AvisarRutasRechazadas()
    {
        var propio = string.Equals(MotivoEstadoCosteo, MotivoTodasLasRutasRechazadas, StringComparison.Ordinal);
        if (TodasLasRutasRechazadas && MotivoEstadoCosteo is null)
        {
            MotivoEstadoCosteo = MotivoTodasLasRutasRechazadas;
            return true;
        }

        if (!TodasLasRutasRechazadas && propio)
        {
            MotivoEstadoCosteo = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// HU-028.1 (#183): el estado de la cabecera a partir de sus rutas (§9.2) y de lo que deriva Nexo de la solicitud
    /// (<paramref name="estadoNexo"/>). Sin rutas, o sin estado en ellas, el de Nexo. Si TODAS las rutas quedaron <c>rechazada</c>, la
    /// cabecera no queda <c>rechazada</c>: queda <c>entregada</c> (o <c>descartada</c>) si Nexo ya lo dice, y si no <c>pendiente_costeo</c>
    /// (la Mesa puede deshacer un rechazo o entregar; el polling sigue).
    /// </summary>
    public Cotizaciones.EstadoCosteo EstadoCabecera(Cotizaciones.EstadoCosteo estadoNexo)
    {
        if (_rutas.Count == 0 || ResumirRutas() is not { } resumen)
        {
            return estadoNexo;
        }

        return resumen != Cotizaciones.EstadoCosteo.Rechazada
            ? resumen
            : estadoNexo is Cotizaciones.EstadoCosteo.Entregada or Cotizaciones.EstadoCosteo.Descartada ? estadoNexo : Cotizaciones.EstadoCosteo.PendienteCosteo;
    }

    /// <summary>
    /// ¿El polling la consulta solo para completar el precio? (HU-013.14.) Una <see cref="EntregadaSinPrecio"/>, o una con resultado
    /// registrado en Nexo cuyo costo Cotifly aún no leyó.
    /// </summary>
    public bool RequiereRelecturaCosteo =>
        EntregadaSinPrecio || (Ganada is not null && !PerdidaSoloEnCotifly && SeConsultaEnNexo && PrecioFinal is null && PdfPath is null);

    /// <summary>Prefijo de <see cref="MotivoEstadoCosteo"/> de una perdida registrada solo en Cotifly (HU-013.17, #127).</summary>
    public const string PrefijoPerdidaSoloEnCotifly = "Perdida solo en Cotifly (el cliente desistió antes del costeo de Nexo): ";

    /// <summary>¿Se marcó perdida solo en Cotifly porque el cliente desistió antes del costeo? (HU-013.17.)</summary>
    public bool PerdidaSoloEnCotifly =>
        Ganada == false && MotivoEstadoCosteo?.StartsWith(PrefijoPerdidaSoloEnCotifly, StringComparison.Ordinal) == true;

    /// <summary>¿Admite la perdida solo en Cotifly? (<c>pendiente_costeo</c> o <c>error_envio</c>, sin resultado.)</summary>
    public bool AdmitePerdidaSinCosteo =>
        Ganada is null && EstadoCosteo is Cotizaciones.EstadoCosteo.PendienteCosteo or Cotizaciones.EstadoCosteo.ErrorEnvio;

    /// <summary>
    /// El cliente desistió antes del costeo (HU-013.17, ADR-018 §17): perdida SOLO en Cotifly con el motivo del comercial. Detiene el
    /// polling y deja el estado de costeo como estaba (con el motivo visible); si Nexo costea después, el resultado no cambia.
    /// </summary>
    public void RegistrarPerdidaSinCosteo(string motivo, DateTimeOffset cuando)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        if (!AdmitePerdidaSinCosteo)
        {
            throw new InvalidOperationException($"La cotización {Folio} no admite la perdida solo en Cotifly en su estado actual.");
        }

        MarcarSeguimiento(false, motivo, cuando);
        CosteoSiguienteConsultaAt = null;
        MotivoEstadoCosteo = Recortar(PrefijoPerdidaSoloEnCotifly + motivo.Trim());
    }

    /// <summary>
    /// Agenda una relectura del costeo en Nexo para <paramref name="cuando"/> (HU-013.14): el resultado se registró pero el costeo no se
    /// pudo leer en la misma operación, así que el polling la completa. No cambia el estado ni la versión de negocio.
    /// </summary>
    public void ProgramarRelecturaCosteo(DateTimeOffset cuando) => CosteoSiguienteConsultaAt = cuando;

    /// <summary>
    /// Resultado de una consulta del polling (HU-005.6), con los camiones ya actualizados: el estado de la solicitud pasa a ser el
    /// resumen de sus rutas (§9.2; sin rutas, <paramref name="estadoNexo"/>, el que deriva la solicitud de Nexo), guarda el id de la
    /// cotización de Nexo y la siguiente consulta (<c>null</c> = no se consulta más) y reinicia los fallos seguidos. Devuelve <c>true</c>
    /// si el estado cambió.
    /// </summary>
    public bool RegistrarConsultaCosteo(Cotizaciones.EstadoCosteo estadoNexo, string? cotizacionNexoId, DateTimeOffset? siguiente)
    {
        var estado = EstadoCabecera(estadoNexo);
        var cambio = EstadoCosteo != estado;
        EstadoCosteo = estado;
        CotizacionNexoId = string.IsNullOrWhiteSpace(cotizacionNexoId) ? CotizacionNexoId : RecortarId(cotizacionNexoId);
        CosteoSiguienteConsultaAt = siguiente;
        CosteoConsultasFallidas = 0;
        if (cambio)
        {
            MotivoEstadoCosteo = null;
        }

        return cambio;
    }

    /// <summary>Una consulta del polling falló (Nexo caído, 4xx, 5xx, id no reconocido): suma un fallo y reprograma, sin cambiar el estado.</summary>
    public void RegistrarFalloConsultaCosteo(DateTimeOffset? siguiente)
    {
        CosteoSiguienteConsultaAt = siguiente;
        CosteoConsultasFallidas++;
    }

    /// <summary>
    /// Se llegó a la edad máxima del polling (HU-005.6): sin costeo, la solicitud y sus camiones sin flete quedan en
    /// <see cref="Cotizaciones.EstadoCosteo.CosteoVencido"/> con el motivo; con costeo (<c>autocosteo</c>) solo deja de consultarse.
    /// Devuelve <c>true</c> si el estado cambió.
    /// </summary>
    public bool VencerPollingCosteo(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        CosteoSiguienteConsultaAt = null;
        if (EstadoCosteo != Cotizaciones.EstadoCosteo.PendienteCosteo)
        {
            return false;
        }

        Vencer();
        MotivoEstadoCosteo = Recortar(motivo);
        return true;
    }

    /// <summary>
    /// Nexo ya no reconoce la solicitud (404): se deja de consultar con el motivo; sin costeo queda en
    /// <see cref="Cotizaciones.EstadoCosteo.CosteoVencido"/> (también sus camiones sin flete). Devuelve <c>true</c> si el estado cambió.
    /// </summary>
    public bool DetenerPorSolicitudDesconocida(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        CosteoSiguienteConsultaAt = null;
        MotivoEstadoCosteo = Recortar(motivo);
        if (EstadoCosteo != Cotizaciones.EstadoCosteo.PendienteCosteo)
        {
            return false;
        }

        Vencer();
        return true;
    }

    private void Vencer()
    {
        foreach (var ruta in _rutas)
        {
            ruta.VencerCamionesSinFlete();
        }

        EstadoCosteo = Cotizaciones.EstadoCosteo.CosteoVencido;
    }

    /// <summary>Deja de consultar a Nexo (estado que ya no se sigue, o datos incompletos).</summary>
    public void DetenerPollingCosteo() => CosteoSiguienteConsultaAt = null;

    /// <summary>Advertencia sobre el costeo (sin margen, costeo cambiado después del PDF...), visible en "Mis cotizaciones".</summary>
    public void AdvertirCosteo(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        MotivoEstadoCosteo = Recortar(motivo);
    }

    /// <summary>Motivo que marca conexos agregados en Nexo que Cotifly todavía no pudo leer (HU-013.15, #124; HU-021).</summary>
    public const string MotivoConexosPendientes =
        "Nexo agregó un servicio conexo que Cotifly todavía no pudo leer: aparecerá con su precio en la siguiente actualización del costeo.";

    /// <summary>¿Hay conexos agregados en Nexo pendientes de traer a la ruta? (la siguiente lectura del costeo los importa).</summary>
    public bool ConexosPendientesDeLeer => string.Equals(MotivoEstadoCosteo, MotivoConexosPendientes, StringComparison.Ordinal);

    /// <summary>Marca que Nexo agregó conexos que no se pudieron leer: la siguiente lectura del costeo los trae (y quita la marca).</summary>
    public void MarcarConexosPendientesDeLeer() => MotivoEstadoCosteo = MotivoConexosPendientes;

    /// <summary>Quita la marca de <see cref="ConexosPendientesDeLeer"/> (sin tocar otro motivo).</summary>
    public void QuitarMarcaConexosPendientes()
    {
        if (ConexosPendientesDeLeer)
        {
            MotivoEstadoCosteo = null;
        }
    }

    /// <summary>Id de la cotización de Nexo recuperado fuera del polling (al marcar el resultado, HU-005.14).</summary>
    public void RegistrarCotizacionNexo(string cotizacionNexoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cotizacionNexoId);
        CotizacionNexoId = RecortarId(cotizacionNexoId);
    }

    /// <summary>
    /// Entrega de la cotización en Nexo vista al marcar el resultado (entrega implícita, HU-005.14) con el precio ya aplicado: la
    /// solicitud y sus camiones pasan a <c>entregada</c> y se deja de consultar.
    /// </summary>
    public bool RegistrarEntregaImplicita(string? cotizacionNexoId)
    {
        foreach (var ruta in _rutas)
        {
            ruta.RegistrarEstadoCamiones(Cotizaciones.EstadoCosteo.Entregada);
        }

        return RegistrarConsultaCosteo(Cotizaciones.EstadoCosteo.Entregada, cotizacionNexoId, siguiente: null);
    }

    /// <summary>Un estado de TODA la solicitud llega a la cabecera, a cada ruta y a cada camión.</summary>
    private void RegistrarEstadoSolicitud(Cotizaciones.EstadoCosteo estado)
    {
        foreach (var ruta in _rutas)
        {
            ruta.RegistrarEstadoCamiones(estado);
        }

        EstadoCosteo = estado;
    }

    /// <summary>
    /// Resumen del estado de las rutas (§9.2), con cada ruta resumida antes desde sus camiones. Un camión (o una ruta) sin estado cuenta
    /// como <c>pendiente_costeo</c> si la solicitud ya se envió a Nexo (<see cref="SolicitudNexoId"/>) y como <c>pendiente_envio</c> si no.
    /// </summary>
    public Cotizaciones.EstadoCosteo? ResumirRutas()
    {
        var enviada = SolicitudNexoId is not null;
        foreach (var ruta in _rutas)
        {
            ruta.ResumirEstado(enviada);
        }

        return clsResumenEstadoCosteo.Resumir(_rutas.Select(r => r.EstadoCosteo), enviada);
    }

    // ------------------------------------------------------------------ Lectura de Nexo y conexos (invariantes de la raíz, ADR-019)

    /// <summary>Asocia el camión <paramref name="camionId"/> con su línea de Nexo (primera vez que el polling la ve). Devuelve si cambió.</summary>
    public bool AsignarLineaNexo(Guid camionId, string lineaSolicitudNexoId) => Camion(camionId).AsignarLineaNexo(lineaSolicitudNexoId);

    /// <summary>
    /// La Mesa de Costeo borró en Nexo la línea del camión <paramref name="camionId"/> (#134): el camión queda <c>descartada</c>, sin línea
    /// ni costos de Nexo, y su ruta resume el estado sin él. Los totales los recalcula la siguiente <see cref="AplicarFotoNexo"/>.
    /// Devuelve si cambió.
    /// </summary>
    public bool DescartarCamionSinLineaEnNexo(Guid camionId)
    {
        var camion = Camion(camionId);
        if (!camion.DescartarPorLineaBorrada())
        {
            return false;
        }

        _rutas.First(r => r.Id == camion.LineaSolicitudId).ResumirEstado(SolicitudNexoId is not null);
        return true;
    }

    /// <summary>
    /// Aplica una lectura de Nexo a los camiones (HU-005.6): a cada uno su estado y, si <see cref="clsCosteoCamionNexo.ConCostos"/>, el
    /// flete, el retorno y el vehículo que puso la Mesa de Costeo. Después cada ruta resume su estado y, si algún camión cambió de costos,
    /// se recalculan los totales y el precio de las rutas y de la solicitud (siempre: nadie toca los camiones por fuera). Con PDF el
    /// llamador manda solo estados (el precio de la versión se conserva, ADR-015 §5). Devuelve si algo cambió, o el error del módulo de
    /// precio (el cambio de los camiones queda aplicado).
    /// </summary>
    public clsResultado<bool> AplicarFotoNexo(IEnumerable<clsCosteoCamionNexo> camiones, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(camiones);
        var cambio = false;
        var conCostos = false;
        foreach (var lectura in camiones)
        {
            var camion = Camion(lectura.CamionId);
            if (lectura.Estado == Cotizaciones.EstadoCosteo.Rechazada)
            {
                // HU-028.1 (#183): la Mesa rechazó su línea: sin flete (si se leen costos) y con el motivo. Con PDF solo estado y motivo.
                conCostos |= lectura.ConCostos;
                cambio |= camion.RegistrarRechazo(lectura.MotivoRechazo, ahora, lectura.ConCostos);
                continue;
            }

            if (lectura.LineaNoRechazada)
            {
                // HU-028.1 (#183): el detalle de Nexo dice que la línea ya no está rechazada (la Mesa lo deshizo antes de entregar): el
                // camión lo deshace aunque la lectura ya diga "entregada" (deshacer, costear y entregar entre dos consultas del polling).
                cambio |= camion.DeshacerRechazo();
            }

            if (!lectura.ConCostos)
            {
                cambio |= camion.RegistrarEstado(lectura.Estado);
                continue;
            }

            conCostos = true;
            cambio |= camion.RegistrarCosteo(lectura.Estado, lectura.Flete, lectura.Retorno);
            if (lectura.TipoVehiculo is not null || lectura.Carroceria is not null)
            {
                cambio |= camion.AsignarVehiculo(lectura.TipoVehiculo, lectura.Carroceria);
            }
        }

        var enviada = SolicitudNexoId is not null;
        foreach (var ruta in _rutas)
        {
            cambio |= ruta.ResumirEstado(enviada);
        }

        if (!conCostos)
        {
            return cambio;
        }

        var recalculo = RecalcularTotales(ahora);
        return recalculo.EsFallo ? recalculo.Error : cambio | recalculo.Valor;
    }

    /// <summary>
    /// Agrega o actualiza conexos en la ruta <paramref name="ordenRuta"/> (precio = costo unitario de Nexo) y recalcula SIEMPRE los
    /// totales y el precio. Devuelve cuántos agregó o cambió, o el error del módulo de precio.
    /// </summary>
    public clsResultado<int> CambiarConexos(int ordenRuta, IEnumerable<clsCambioConexo> conexos, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(conexos);
        var ruta = RutaDeOrden(ordenRuta);
        var cambios = conexos.Count(ruta.AgregarOActualizarConexo);
        var recalculo = RecalcularRuta(ruta, ahora);
        return recalculo.EsFallo ? recalculo.Error : cambios;
    }

    /// <summary>
    /// Deja en la ruta <paramref name="ordenRuta"/> exactamente los conexos de <paramref name="cantidades"/> (id del conexo → cantidad
    /// nueva; HU-021.8, #135, decisión 3): los que no están se BORRAN de la ruta (la tabla no tiene "incluido") y a los demás se les cambia
    /// la cantidad, con el mismo precio unitario de Nexo. Después recalcula esa ruta y la cabecera. Todos los ids deben ser conexos de la
    /// ruta (el caso de uso lo valida antes: aquí un id ajeno es un error de programación). Devuelve los movimientos (quitados y con
    /// cantidad nueva) para la auditoría, o el error del módulo de precio.
    /// </summary>
    public clsResultado<IReadOnlyList<clsMovimientoConexo>> AjustarConexosRuta(
        int ordenRuta, IReadOnlyDictionary<string, decimal> cantidades, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(cantidades);
        var ruta = RutaDeOrden(ordenRuta);
        var movimientos = ruta.AjustarConexos(cantidades);
        if (movimientos.Count == 0)
        {
            return clsResultado<IReadOnlyList<clsMovimientoConexo>>.Exito(movimientos);
        }

        var recalculo = RecalcularRuta(ruta, ahora);
        return recalculo.EsFallo ? recalculo.Error : clsResultado<IReadOnlyList<clsMovimientoConexo>>.Exito(movimientos);
    }

    /// <summary>
    /// El comercial cambia el valor de UN levante de contenedor de la ruta <paramref name="ordenRuta"/> (decisión 4): se guarda en la
    /// ruta, se copia a cada camión y se recalculan esa ruta y la cabecera. Solo mientras la cotización no se haya entregado al cliente
    /// (<c>PDF_YA_ENVIADO</c>, ADR-018 §11). Devuelve si cambió, o el error.
    /// <para><b>Sin llamadores en producción</b> (solo sus pruebas): hoy el levante cambia en la versión nueva de la re-solicitud, que crea
    /// los camiones con la copia. La captura por rutas (#132, integrada) solo copia el levante al crear los camiones y no edita rutas.</para>
    /// </summary>
    public clsResultado<bool> CambiarValorLevante(int ordenRuta, decimal? valor, DateTimeOffset ahora)
    {
        if (valor is < 0m)
        {
            return new clsErrorDominio(CodigoError, "El valor del levante del contenedor no puede ser negativo.");
        }

        if (ValidarCambioEtapaFinal() is { } entregada)
        {
            return entregada;
        }

        var ruta = RutaDeOrden(ordenRuta);
        if (!ruta.CambiarValorLevante(valor))
        {
            return false;
        }

        var recalculo = RecalcularRuta(ruta, ahora);
        return recalculo.EsFallo ? recalculo.Error : true;
    }

    // ------------------------------------------------------------------ Ruta indicada por la petición (HU-021.8, #135)

    /// <summary>
    /// La ruta a la que aplica una petición de la etapa final (margen o conexos), por su <c>linea_orden</c> o su id
    /// (<c>linea_solicitud_id</c>). Sin ninguno de los dos: la única ruta si hay una; con varias, <c>RUTA_REQUERIDA</c> con las rutas en
    /// <c>detalle.rutas</c> (orden, id, origen y destino, sin montos). Una ruta que no es de la cotización, o un orden y un id que no son la
    /// misma ruta: <c>RUTA_INEXISTENTE</c>.
    /// </summary>
    public clsResultado<clsLineaSolicitud> ResolverRuta(int? lineaOrden, Guid? lineaSolicitudId)
    {
        if (lineaSolicitudId is { } id)
        {
            var porId = _rutas.FirstOrDefault(r => r.Id == id);
            if (porId is null || (lineaOrden is { } orden && orden != porId.Orden))
            {
                return new clsErrorDominio(clsCodigosError.RutaInexistente,
                    porId is null
                        ? $"La ruta {id} no es de la cotización {Folio}."
                        : $"linea_orden {lineaOrden} y linea_solicitud_id {id} no son la misma ruta de la cotización {Folio}.");
            }

            return porId;
        }

        if (lineaOrden is { } soloOrden)
        {
            return _rutas.FirstOrDefault(r => r.Orden == soloOrden) is { } porOrden
                ? porOrden
                : new clsErrorDominio(clsCodigosError.RutaInexistente, $"La cotización {Folio} no tiene la ruta {soloOrden}.");
        }

        return _rutas.Count switch
        {
            1 => _rutas[0],
            0 => new clsErrorDominio(clsCodigosError.RutaInexistente, $"La cotización {Folio} no tiene rutas."),
            _ => new clsErrorDominio(clsCodigosError.RutaRequerida,
                $"La cotización {Folio} tiene {_rutas.Count} rutas: indica a cuál aplica (linea_orden o linea_solicitud_id).")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["rutas"] = DescribirRutas() },
            },
        };
    }

    /// <summary>Las rutas en orden, sin montos: <c>linea_orden</c>, <c>linea_solicitud_id</c>, origen y destino (detalle de <c>RUTA_REQUERIDA</c>).</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> DescribirRutas() =>
        [.. RutasOrdenadas.Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["linea_orden"] = r.Orden,
            ["linea_solicitud_id"] = r.Id,
            ["origen"] = r.OrigenNombre,
            ["destino"] = r.DestinoNombre,
        })];

    private clsLineaSolicitud RutaDeOrden(int ordenRuta) =>
        _rutas.FirstOrDefault(r => r.Orden == ordenRuta)
        ?? throw new ArgumentOutOfRangeException(nameof(ordenRuta), $"La cotización {Folio} no tiene la ruta {ordenRuta}.");

    /// <summary>Recalcula los totales y el precio de UNA ruta y el precio de la cabecera (§9.1: cambió esa ruta y nada más).</summary>
    private clsResultado<bool> RecalcularRuta(clsLineaSolicitud ruta, DateTimeOffset ahora)
    {
        var recalculo = ruta.RecalcularTotales(Moneda, TrmAplicada);
        var cambioCabecera = RecalcularPrecio(ahora);
        return recalculo.EsFallo
            ? new clsErrorDominio(recalculo.Error.Codigo, $"Ruta {ruta.Orden}: {recalculo.Error.Mensaje}", recalculo.Error.Detalle)
            : recalculo.Valor | cambioCabecera;
    }

    /// <summary>
    /// Quita de todas las rutas los conexos que no están en <paramref name="conservar"/> (vacío = todos) y recalcula SIEMPRE los totales y
    /// el precio. Devuelve cuántos quitó, o el error del módulo de precio.
    /// </summary>
    public clsResultado<int> ConservarConexos(IReadOnlySet<string> conservar, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(conservar);
        var quitados = _rutas.Sum(r => r.ConservarConexos(conservar).Count);
        var recalculo = RecalcularTotales(ahora);
        return recalculo.EsFallo ? recalculo.Error : quitados;
    }

    /// <summary>
    /// Persistencia (recarga del agregado, <c>IClsCotizacionRepository.RecargarAsync</c>): reemplaza las rutas en memoria, con sus
    /// camiones, mercancías y conexos, por las de <paramref name="leida"/>, recién leída de la base. No es una operación de negocio.
    /// </summary>
    internal void ReemplazarRutasLeidas(clsCotizacionSolicitud leida)
    {
        ArgumentNullException.ThrowIfNull(leida);
        if (leida.Id != Id)
        {
            throw new ArgumentException("La cotización leída no es esta.", nameof(leida));
        }

        _rutas.Clear();
        _rutas.AddRange(leida._rutas);
    }

    private clsVehiculoLineaSolicitud Camion(Guid camionId) =>
        _rutas.SelectMany(r => r.Vehiculos).FirstOrDefault(v => v.Id == camionId)
        ?? throw new ArgumentOutOfRangeException(nameof(camionId), $"El camión {camionId} no es de la cotización {Folio}.");

    /// <summary>
    /// HU-023.3 (#158): reemplaza los camiones de la ruta <paramref name="rutaId"/> por el plan nuevo del cubicador (con su resultado) y
    /// recalcula los totales. <c>PDF_YA_ENVIADO</c> si ya se entregó al cliente o está finalizada (<see cref="ValidarCambioEtapaFinal"/>,
    /// HU-036.3); solo con <see cref="PuedeRecalcularCamiones"/> (nunca enviada a Nexo y sin envío en curso): <c>ESTADO_NO_PERMITE</c> si
    /// no; <c>RUTA_INEXISTENTE</c> si la ruta no es de esta cotización.
    /// </summary>
    public clsResultado<clsLineaSolicitud> ReemplazarCamionesDeRuta(Guid rutaId, clsPlanRuta plan, DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (ValidarCambioEtapaFinal() is { } cerrada)
        {
            // HU-036.3 (#207): entregada o finalizada, los camiones solo cambian en una versión nueva.
            return cerrada;
        }

        if (!PuedeRecalcularCamiones(ahora))
        {
            return new clsErrorDominio(clsCodigosError.EstadoNoPermite,
                $"Los camiones de la cotización {Folio} ya están en Nexo o se están enviando: para cambiarlos pide una versión nueva.");
        }

        if (_rutas.FirstOrDefault(r => r.Id == rutaId) is not { } ruta)
        {
            return new clsErrorDominio(clsCodigosError.RutaInexistente, $"La ruta {rutaId} no es de la cotización {Folio}.");
        }

        ruta.ReemplazarCamiones(plan.Camiones, plan.Cubicacion, plan.Avisos);
        var recalculo = RecalcularTotales(ahora);
        return recalculo.EsFallo ? recalculo.Error : ruta;
    }

    // ------------------------------------------------------------------ Totales y precio (§9.1)

    /// <summary>
    /// Recalcula los totales y el precio de cada ruta (en la moneda y con la TRM fijadas en la solicitud) y el precio de la solicitud
    /// (<see cref="RecalcularPrecio"/>). <see cref="CosteoListoAt"/> queda en la primera vez que hay precio. Devuelve si algo cambió, o el
    /// primer error del módulo de precio (esa ruta queda sin precio y la solicitud también).
    /// </summary>
    public clsResultado<bool> RecalcularTotales(DateTimeOffset ahora)
    {
        var cambio = false;
        clsErrorDominio? primero = null;
        foreach (var ruta in RutasOrdenadas)
        {
            var recalculo = ruta.RecalcularTotales(Moneda, TrmAplicada);
            if (recalculo.EsFallo)
            {
                primero ??= new clsErrorDominio(recalculo.Error.Codigo, $"Ruta {ruta.Orden}: {recalculo.Error.Mensaje}", recalculo.Error.Detalle);
                cambio = true;
                continue;
            }

            cambio |= recalculo.Valor;
        }

        cambio |= RecalcularPrecio(ahora);
        return primero is null ? cambio : primero;
    }

    /// <summary>
    /// <see cref="PrecioFinal"/> = la suma de <see cref="clsLineaSolicitud.PrecioFinalLinea"/> de sus rutas cuando TODAS tienen precio; si
    /// alguna no, la solicitud tampoco tiene (§9.1). HU-028.1 (#183): las rutas <c>rechazada</c> no cuentan (<see cref="RutasCotizables"/>);
    /// con todas rechazadas, sin precio. Devuelve <c>true</c> si cambió.
    /// </summary>
    public bool RecalcularPrecio(DateTimeOffset ahora)
    {
        var cotizables = RutasCotizables;
        decimal? precio = cotizables.Count > 0 && cotizables.All(r => r.PrecioFinalLinea is not null) ? cotizables.Sum(r => r.PrecioFinalLinea!.Value) : null;
        var cambio = PrecioFinal != precio;
        PrecioFinal = precio;
        if (precio is not null)
        {
            CosteoListoAt ??= ahora;
        }

        return cambio;
    }

    /// <summary>
    /// Costo total de la solicitud (interno): la suma del costo final de sus rutas, o <c>null</c> si alguna no lo tiene. HU-028.1 (#183): sin
    /// las rutas <c>rechazada</c> (<see cref="RutasCotizables"/>).
    /// </summary>
    public decimal? CostoFinal
    {
        get
        {
            var cotizables = RutasCotizables;
            return cotizables.Count > 0 && cotizables.All(r => r.CostoFinal is not null) ? cotizables.Sum(r => r.CostoFinal!.Value) : null;
        }
    }

    // ------------------------------------------------------------------ Margen (ADR-018 §3): por ruta (HU-021.8, #135, decisión 6)

    /// <summary>
    /// Invariantes del ajuste de margen de la ruta <paramref name="ordenRuta"/> (ADR-018 §3 y §11, por ruta desde HU-021.8): forma
    /// (<c>AJUSTE_MARGEN_INVALIDO</c>), ruta de la cotización (<c>RUTA_INEXISTENTE</c>), precio en ESA ruta (<c>COSTEO_NO_LISTO</c>;
    /// las demás rutas no lo necesitan), regla del PDF (<c>PDF_YA_ENVIADO</c>) y justificación si queda por debajo del sugerido DE ESA RUTA
    /// (<c>JUSTIFICACION_REQUERIDA</c>).
    /// </summary>
    public clsErrorDominio? ValidarAjusteMargen(int ordenRuta, decimal margen, string? justificacion)
    {
        if (clsLineaSolicitud.ValidarForma(margen, justificacion) is { } forma)
        {
            return forma;
        }

        var ruta = _rutas.FirstOrDefault(r => r.Orden == ordenRuta);
        if (ruta is null)
        {
            return new clsErrorDominio(clsCodigosError.RutaInexistente, $"La cotización {Folio} no tiene la ruta {ordenRuta}.");
        }

        if (ValidarRutaNoRechazada(ruta) is { } rechazada)
        {
            return rechazada;
        }

        // Decisión del usuario (2026-10-08, #135): el ajuste es ruta por ruta y solo ESA ruta necesita precio; las demás pueden seguir en
        // costeo (el PDF sí exige todas, clsCosteoCotizacionService.ValidarTotal). La ruta debe venir del costeo de Nexo
        // (autocosteo o entregada): una cotización del chat anterior a HU-013 no se ajusta por aquí.
        if (!ruta.TienePrecio || ruta.EstadoCosteo is not { } estadoRuta || !clsDerivacionEstadoCosteo.TieneCosteo(estadoRuta))
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {Folio} todavía no tiene el costeo de Nexo con margen aplicado.");
        }

        return ValidarCambioEtapaFinal() ?? ruta.ValidarJustificacion(margen, justificacion);
    }

    /// <summary>
    /// Aplica el margen negociado SOLO a la ruta <paramref name="ordenRuta"/> y recalcula esa ruta y la cabecera (las demás rutas no
    /// cambian). Devuelve si algo cambió, o el error del módulo de precio.
    /// </summary>
    public clsResultado<bool> AplicarMargenNegociado(int ordenRuta, decimal margen, DateTimeOffset ahora)
    {
        var ruta = RutaDeOrden(ordenRuta);
        var cambio = ruta.AplicarMargenNegociado(margen);
        var recalculo = RecalcularRuta(ruta, ahora);
        return recalculo.EsFallo ? recalculo.Error : cambio | recalculo.Valor;
    }

    // ------------------------------------------------------------------ Regla del PDF y etapa final (ADR-018 §11)

    /// <summary>
    /// Regla del PDF (ADR-018 §11) para toda acción que cambia el precio en la etapa final (margen, conexos, conexos en Nexo, re-solicitud
    /// en el mismo folio, recálculo de camiones y edición en sitio): ya entregada al cliente (<see cref="EntregadaAlCliente"/>) o, desde
    /// HU-036.3 (#207), ya finalizada (<see cref="Finalizada"/>: el comercial aceptó el precio) responde <c>PDF_YA_ENVIADO</c>; si no,
    /// <c>null</c>. Los textos y el PDF NO usan esta regla: se permiten (y se exigen) con la cotización finalizada
    /// (<see cref="ValidarEntregaAlCliente"/> y <see cref="ValidarFinalizadaAntesDe"/>).
    /// </summary>
    public clsErrorDominio? ValidarCambioEtapaFinal() =>
        ValidarEntregaAlCliente()
        ?? (Finalizada
            ? new clsErrorDominio(clsCodigosError.PdfYaEnviado, "La cotización ya está finalizada: crea una versión nueva para cambiarla.")
            : null);

    /// <summary>
    /// <c>PDF_YA_ENVIADO</c> solo si ya se entregó al cliente (enviada o con resultado): la regla de ADR-018 §11 tal como era antes de
    /// HU-036, para lo que se hace DESPUÉS de finalizar y antes de entregar (el PDF y sus textos).
    /// </summary>
    public clsErrorDominio? ValidarEntregaAlCliente() => EntregadaAlCliente
        ? new clsErrorDominio(clsCodigosError.PdfYaEnviado,
            Ganada is null
                ? "La cotización ya se envió al cliente: crea una versión nueva para cambiarla."
                : "La cotización ya tiene resultado registrado: crea una versión nueva para cambiarla.")
        : null;

    /// <summary>
    /// Invalida el PDF actual (ADR-018 §11): <see cref="PdfPath"/> pasa a <c>null</c> y <see cref="PdfDesactualizado"/> a <c>true</c>.
    /// Devuelve la referencia del PDF anterior para el historial, o <c>null</c> si no había PDF (nada cambia).
    /// </summary>
    public string? InvalidarPdf()
    {
        if (PdfPath is not { } anterior)
        {
            return null;
        }

        PdfPath = null;
        PdfDesactualizado = true;
        return anterior;
    }

    /// <summary>
    /// Guarda el asunto y la introducción del PDF (JSON de <c>textosCotizacion</c>, HU-013.5, #101). Los valida Aplicación; aquí solo se
    /// exige que no venga vacío.
    /// </summary>
    public void RegistrarTextos(string textosJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textosJson);
        TextosCotizacion = textosJson;
    }

    /// <summary>Recorta un motivo a <see cref="LargoMaximoMotivoCosteo"/> caracteres (la columna es <c>NVARCHAR(1000)</c>).</summary>
    public static string Recortar(string motivo)
    {
        ArgumentNullException.ThrowIfNull(motivo);
        var limpio = motivo.Trim();
        return limpio.Length <= LargoMaximoMotivoCosteo ? limpio : limpio[..(LargoMaximoMotivoCosteo - 1)] + "…";
    }

    /// <summary>¿La cotización pertenece al comercial? (aislamiento por dueño, ADR-009).</summary>
    public bool EsDe(Guid usuarioId) => UsuarioId == usuarioId;

    /// <summary>Valor de <c>motivoRechazo</c> de una cotización ganada (propuesta de refactor de BD §2.1, HU-021.13).</summary>
    public const string MotivoRechazoNoAplica = "No aplica";

    /// <summary>
    /// Marca la cotización como ganada o perdida. Regla de la propuesta §2.1 (HU-021.13): ganada deja <c>motivoRechazo</c> en
    /// <see cref="MotivoRechazoNoAplica"/> (no <c>null</c>), perdida guarda el motivo recibido y sin resultado (<c>ganada = null</c>)
    /// queda vacío. Es la única puerta de escritura del motivo, así que cubre <c>/seguimiento</c>, <c>/resultado</c> y la
    /// sincronización desde Nexo. A Nexo se le sigue enviando el motivo tal como llega (su contrato no cambia).
    /// </summary>
    public void MarcarSeguimiento(bool? ganada, string? motivoRechazo, DateTimeOffset cuando)
    {
        Ganada = ganada;
        MotivoRechazo = ganada switch
        {
            true => MotivoRechazoNoAplica,
            false => clsValidacion.Normalizar(motivoRechazo),
            null => null,
        };
        MarcadoAt = cuando;
    }

    /// <summary>Registra un envío exitoso: el primero la deja <c>enviada</c>; los siguientes, <c>reenviada</c>.</summary>
    public void RegistrarEnvio() =>
        EstadoEnvio = EstadoEnvio is null or Cotizaciones.EstadoEnvio.SinEnviar
            ? Cotizaciones.EstadoEnvio.Enviada
            : Cotizaciones.EstadoEnvio.Reenviada;

    /// <summary>
    /// Deshace la reserva de un envío que no salió (el transporte falló): vuelve al estado de envío anterior (ADR-018 §12).
    /// </summary>
    public void RestaurarEstadoEnvio(EstadoEnvio? anterior) => EstadoEnvio = anterior ?? Cotizaciones.EstadoEnvio.SinEnviar;

    /// <summary>Asocia la ruta del PDF generado (el PDF vuelve a estar vigente: <see cref="PdfDesactualizado"/> pasa a <c>false</c>).</summary>
    public void AsignarPdf(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        PdfPath = pdfPath.Trim();
        PdfDesactualizado = false;
    }

    /// <summary>¿La versión leída por el cliente (<c>If-Match</c>) es la versión de negocio vigente? Una negativa (inválida) nunca coincide.</summary>
    public bool TieneVersion(int version) => version >= 0 && VersionNegocio == version;

    /// <summary>Sube la versión de negocio: lo llaman los casos de uso al confirmar un cambio que ve el comercial (ADR-018 §12).</summary>
    public void IncrementarVersionNegocio() => VersionNegocio = checked(VersionNegocio + 1);

    // ------------------------------------------------------------------ Re-solicitud y versión nueva (HU-013.4, ADR-018 §5)

    /// <summary>
    /// Invariantes de "Cotizar costo" (re-solicitar el costeo a Nexo, ADR-018 §5 y §11), en este orden:
    /// <list type="number">
    /// <item>la cotización nació del formulario de costeo (si no, <c>ESTADO_NO_PERMITE</c>);</item>
    /// <item>sin un costeo en curso (<c>pendiente_envio</c> o <c>pendiente_costeo</c>: <c>ESTADO_NO_PERMITE</c>);</item>
    /// <item>en el mismo folio: sin envío al cliente ni resultado (<c>PDF_YA_ENVIADO</c>) y con <c>estado_costeo</c> en
    /// <c>costeo_vencido</c>, <c>entregada</c> o un <c>error_envio</c> que no sea un rechazo definitivo de Nexo.</item>
    /// </list>
    /// La versión nueva se permite también ya enviada o con resultado (es la salida de <c>PDF_YA_ENVIADO</c>).
    /// </summary>
    public clsErrorDominio? ValidarReSolicitud(bool versionNueva)
    {
        if (EstadoCosteo is not { } estado || EncabezadoSolicitud is null)
        {
            return NoPermite("no nació del formulario de costeo en Nexo");
        }

        if (estado is Cotizaciones.EstadoCosteo.PendienteEnvio or Cotizaciones.EstadoCosteo.PendienteCosteo)
        {
            return NoPermite("tiene un costeo en curso en Nexo");
        }

        if (versionNueva)
        {
            return null;
        }

        if (ValidarCambioEtapaFinal() is { } entregada)
        {
            return entregada;
        }

        if (ErrorEnvioDefinitivo)
        {
            return NoPermite("Nexo rechazó sus datos: corrige la carga en una versión nueva (datos_carga)");
        }

        return estado is Cotizaciones.EstadoCosteo.CosteoVencido or Cotizaciones.EstadoCosteo.Entregada or Cotizaciones.EstadoCosteo.ErrorEnvio
            ? null
            : NoPermite("solo se re-solicita con el costeo vencido, entregado o con error de envío");
    }

    /// <summary>
    /// Deja la cotización lista para volver a enviarla a Nexo en el mismo folio (HU-013.4): sin el costeo anterior (ids de la solicitud y
    /// de la cotización de Nexo, precio y fecha de costeo; el caso de uso los deja antes en la auditoría), sin polling y en
    /// <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/>. Cada ruta olvida sus costos de Nexo y su precio (conserva sus conexos y su
    /// levante, que son de Cotifly). El margen se vuelve a resolver al enviar.
    /// </summary>
    public void PrepararReSolicitud()
    {
        SolicitudNexoId = null;
        ScorePrioridad = null;
        CotizacionNexoId = null;
        PrecioFinal = null;
        CosteoListoAt = null;
        CosteoSiguienteConsultaAt = null;
        CosteoConsultasFallidas = 0;
        foreach (var ruta in _rutas)
        {
            ruta.ReiniciarCosteo();
        }

        EstadoCosteo = Cotizaciones.EstadoCosteo.PendienteEnvio;
        MotivoEstadoCosteo = null;
    }

    /// <summary>
    /// Cabecera de la versión <paramref name="folio"/> (<c>-vN</c>, ADR-011) con la identidad de esta: cliente, comercial y moneda. La TRM
    /// NO se copia (decisión del 2026-10-06, ADR-014 §5): la versión nueva fija la del día (<paramref name="trmDelDia"/>). Las rutas las
    /// registra el caso de uso y el margen negociado se copia por ruta (<see cref="CopiarMargenesNegociadosDe"/>).
    /// </summary>
    public clsResultado<clsCotizacionSolicitud> CrearVersion(clsFolio folio, decimal trmDelDia)
    {
        ArgumentNullException.ThrowIfNull(folio);
        if (folio.Version <= Version)
        {
            throw new ArgumentException($"La versión nueva debe ser mayor que {Version}.", nameof(folio));
        }

        return Crear(new clsDatosCotizacion
        {
            Folio = folio.Valor,
            Version = folio.Version,
            ClienteNombre = ClienteNombre,
            NitCliente = NitCliente,
            OrigenCliente = OrigenCliente,
            UsuarioId = UsuarioId,
            Moneda = Moneda ?? MonedaPesos,
            TrmAplicada = trmDelDia,
        });
    }

    /// <summary>
    /// Versión nueva: el margen ajustado por el comercial en cada ruta de <paramref name="anterior"/> se copia a la ruta del mismo orden
    /// (decisión del 2026-10-06: el envío lo conserva y solo refresca el sugerido).
    /// </summary>
    public void CopiarMargenesNegociadosDe(clsCotizacionSolicitud anterior)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        var porOrden = anterior.Rutas.ToDictionary(r => r.Orden);
        foreach (var ruta in _rutas)
        {
            if (porOrden.TryGetValue(ruta.Orden, out var previa))
            {
                ruta.CopiarMargenNegociado(previa);
            }
        }
    }

    /// <summary>
    /// HU-028.1 (#183): <c>RUTA_RECHAZADA</c> (409) si la Mesa de Costeo de Nexo rechazó la ruta (todos sus camiones): no tiene precio, así
    /// que no admite margen ni conexos. <c>null</c> si no está rechazada.
    /// </summary>
    public clsErrorDominio? ValidarRutaNoRechazada(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return ruta.Rechazada
            ? new clsErrorDominio(clsCodigosError.RutaRechazada,
                $"La ruta {ruta.Orden} de la cotización {Folio} fue rechazada por la Mesa de Costeo de Nexo: no tiene precio, así que no admite margen "
                + "ni servicios conexos.")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["linea_orden"] = ruta.Orden,
                    ["linea_solicitud_id"] = ruta.Id,
                    ["motivo_rechazo"] = ruta.MotivoRechazo,
                },
            }
            : null;
    }

    private clsErrorDominio NoPermite(string motivo) => new(clsCodigosError.EstadoNoPermite,
        $"La cotización {Folio} no permite re-solicitar el costeo en su estado actual "
        + $"({(EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : "sin costeo en Nexo")}): {motivo}.");

    private static string RecortarId(string id)
    {
        var limpio = id.Trim();
        return limpio.Length <= LargoMaximoIdNexo ? limpio : throw new ArgumentException($"Un id de Nexo admite hasta {LargoMaximoIdNexo} caracteres.", nameof(id));
    }

    private static clsErrorDominio? ValidarTrm(string moneda, decimal trm)
    {
        if (moneda == MonedaPesos && trm != 0m)
        {
            return new clsErrorDominio(CodigoError, "La TRM aplicada debe ser 0 cuando la moneda es COP.");
        }

        if (moneda != MonedaPesos && trm <= 0m)
        {
            return new clsErrorDominio(CodigoError, $"La TRM aplicada debe ser mayor que 0 cuando la moneda es {moneda}.");
        }

        return null;
    }
}
