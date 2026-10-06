using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Cotización (<c>dbo_cotifly.Cotizaciones</c>). Una nueva versión es una fila nueva con
/// <see cref="Version"/> + 1 y folio <c>-vN</c>, sin FK a la original (HU-002.4 Fase 2).
/// <see cref="CreatedAt"/> y <see cref="UpdatedAt"/> los asigna la persistencia con el reloj de la aplicación.
/// </summary>
public sealed class clsCotizacion
{
    public const string CodigoError = "COTIZACION_INVALIDA";
    public const string MonedaPesos = "COP";
    public const int LargoMaximoFolio = 50;
    public const int LargoMaximoMotivoCosteo = 1000;

    /// <summary>
    /// Una cotización que quedó en <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/> solo se puede reenviar pasado este
    /// tiempo: antes, el envío original puede seguir esperando la respuesta de Nexo (hasta 30 s) y reenviar duplicaría la solicitud.
    /// </summary>
    public static readonly TimeSpan EsperaReenvioPendiente = TimeSpan.FromMinutes(2);

    private clsCotizacion()
    {
    }

    public Guid Id { get; private set; }
    public string Folio { get; private set; } = string.Empty;
    public int? LineaNegocioId { get; private set; }
    public string? ClienteNombre { get; private set; }
    public string? NitCliente { get; private set; }
    public OrigenCliente? OrigenCliente { get; private set; }
    public string? CosteoId { get; private set; }
    public decimal? Costo { get; private set; }
    public decimal? MargenPolitica { get; private set; }
    public decimal? MargenAplicado { get; private set; }
    public OrigenMargen? OrigenMargen { get; private set; }
    public string? MetodoCalculo { get; private set; }
    public decimal? PrecioFinal { get; private set; }
    public string? Moneda { get; private set; }
    public decimal TrmAplicada { get; private set; }
    public string? PdfPath { get; private set; }
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

    // Formulario ampliado (script 0004, HU-005.8): encabezado y única línea de la solicitud de Nexo.
    public TipoSolicitud? TipoSolicitud { get; private set; }
    public DateTimeOffset? Deadline { get; private set; }
    public decimal? ValorEstimado { get; private set; }
    public string? Referencia { get; private set; }
    public decimal? HorasCargue { get; private set; }
    public decimal? HorasDescargue { get; private set; }

    /// <summary>Encabezado: <c>notas</c> para la Mesa de Nexo (script 0007, G7 de HU-013.10).</summary>
    public string? Notas { get; private set; }

    public string? Origen { get; private set; }
    public string? Destino { get; private set; }
    public string? TipoVehiculo { get; private set; }
    public string? Carroceria { get; private set; }
    public string? Tipologia { get; private set; }
    public string? ClaseViaje { get; private set; }
    public string? ClaseContenedor { get; private set; }
    public bool? Exclusivo { get; private set; }
    public bool? Consolidado { get; private set; }
    public bool? RetornaContenedor { get; private set; }
    public string? CiudadDevolucionContenedor { get; private set; }
    public bool? LevanteContenedorDiferente { get; private set; }
    public string? CiudadLevanteContenedor { get; private set; }
    public decimal? ValorAprovisionamiento { get; private set; }
    public decimal? PesoKg { get; private set; }
    public decimal? ValorMercancia { get; private set; }

    // Seguimiento de la integración con Nexo (script 0004, ADR-015).

    /// <summary>Id de la cotización (borrador o entregada) de Nexo; lo exige <c>POST /api/cotizaciones/{id}/resultado</c>.</summary>
    public string? CotizacionNexoId { get; private set; }

    /// <summary>Motivo legible de <see cref="EstadoCosteo"/> (error al enviar, rechazo de Nexo, costeo vencido...).</summary>
    public string? MotivoEstadoCosteo { get; private set; }
    public DateTimeOffset? CosteoEnviadoAt { get; private set; }
    public DateTimeOffset? CosteoUltimaConsultaAt { get; private set; }

    /// <summary>Agenda del polling (HU-005.6): <c>null</c> = no se consulta más.</summary>
    public DateTimeOffset? CosteoSiguienteConsultaAt { get; private set; }
    public int CosteoConsultasFallidas { get; private set; }

    /// <summary>JSON con los servicios conexos del costeo, su precio y si el comercial los incluye (HU-005.7).</summary>
    public string? ServiciosConexos { get; private set; }

    // Script 0006 (ADR-018 §8 y §11): concurrencia optimista, textos del PDF y regla del PDF.

    /// <summary>
    /// Versión de negocio (<c>Cotizaciones.versionNegocio</c>, ADR-018 §12): sube solo con los cambios que ve el comercial (margen,
    /// conexos, textos, re-solicitud, PDF y el polling cuando cambia el estado, el costo o el precio), nunca con el reclamo ni la
    /// agenda del polling. Viaja como <c>ETag</c> y vuelve en <c>If-Match</c>.
    /// </summary>
    public int VersionNegocio { get; private set; }

    /// <summary>
    /// Margen sugerido (<c>Cotizaciones.margenSugerido</c>): el que se resolvió al enviar o crear la cotización (SAP TM con el BP o la
    /// política de la línea). El ajuste del comercial no lo cambia.
    /// </summary>
    public decimal? MargenSugerido { get; private set; }

    /// <summary>Fuente de <see cref="MargenSugerido"/>: <c>sap_tm</c> o <c>politica_linea</c> (nunca <c>negociado</c>).</summary>
    public OrigenMargen? OrigenMargenSugerido { get; private set; }

    /// <summary>Asunto e introducción del PDF guardados (JSON, HU-013.5, #101); <c>null</c> si todavía no hay.</summary>
    public string? TextosCotizacion { get; private set; }

    /// <summary>
    /// Una acción posterior al PDF (margen, conexos, textos, re-solicitud) lo invalidó: <see cref="PdfPath"/> quedó en <c>null</c> y el
    /// comercial lo regenera (ADR-018 §11). Generar el PDF lo apaga.
    /// </summary>
    public bool PdfDesactualizado { get; private set; }

    /// <summary>Margen máximo del ajuste del comercial sobre el costeo (ADR-018 §3): 0,99.</summary>
    public const decimal MargenMaximoAjuste = 0.99m;

    /// <summary>Largo máximo de la justificación de un ajuste de margen (contrato <c>AjusteMargenWeb</c>).</summary>
    public const int LargoMaximoJustificacion = 500;

    /// <summary>¿Tiene el costeo de Nexo con precio? (<c>autocosteo</c> o <c>entregada</c> y <see cref="PrecioFinal"/>.)</summary>
    public bool TieneCostoDisponible =>
        PrecioFinal is not null && EstadoCosteo is { } estado && clsDerivacionEstadoCosteo.TieneCosteo(estado);

    /// <summary>
    /// ¿Ya se entregó al cliente? (enviada o reenviada por correo, o con resultado ganada/perdida.) Entonces el PDF es la foto de lo
    /// entregado y la cotización solo cambia con una versión nueva (ADR-018 §11).
    /// </summary>
    public bool EntregadaAlCliente =>
        EstadoEnvio is Cotizaciones.EstadoEnvio.Enviada or Cotizaciones.EstadoEnvio.Reenviada || Ganada is not null;

    /// <summary>
    /// Margen sugerido vigente para la regla de la justificación: <see cref="MargenSugerido"/>; en filas anteriores al 0006 sin él,
    /// el margen aplicado si nadie lo ajustó o la política de la línea si ya se negoció.
    /// </summary>
    public decimal? SugeridoVigente => MargenSugerido
        ?? (OrigenMargen == Margen.OrigenMargen.Negociado ? MargenPolitica : MargenAplicado);

    /// <summary>
    /// Invariantes del ajuste de margen sobre el costeo (ADR-018 §3 y §11), en este orden:
    /// <list type="number">
    /// <item><paramref name="margen"/> entre 0 y <see cref="MargenMaximoAjuste"/> y justificación de hasta
    /// <see cref="LargoMaximoJustificacion"/> caracteres (<c>AJUSTE_MARGEN_INVALIDO</c>);</item>
    /// <item>costo disponible (<c>COSTEO_NO_LISTO</c>);</item>
    /// <item>sin envío al cliente ni resultado (<c>PDF_YA_ENVIADO</c>);</item>
    /// <item>justificación no vacía si el margen queda por debajo del sugerido (<c>JUSTIFICACION_REQUERIDA</c>).</item>
    /// </list>
    /// </summary>
    public clsErrorDominio? ValidarAjusteMargen(decimal margen, string? justificacion)
    {
        if (margen < 0m || margen > MargenMaximoAjuste)
        {
            return new clsErrorDominio(clsCodigosError.AjusteMargenInvalido,
                $"El margen debe estar entre 0 y {MargenMaximoAjuste.ToString(System.Globalization.CultureInfo.InvariantCulture)} (fracción: 0.18 = 18 %).");
        }

        if (justificacion is { Length: > LargoMaximoJustificacion })
        {
            return new clsErrorDominio(clsCodigosError.AjusteMargenInvalido, $"La justificación admite hasta {LargoMaximoJustificacion} caracteres.");
        }

        if (!TieneCostoDisponible || MetodoCalculo is null)
        {
            return new clsErrorDominio(clsCodigosError.CosteoNoListo, $"La cotización {Folio} todavía no tiene el costeo de Nexo con margen aplicado.");
        }

        if (ValidarCambioEtapaFinal() is { } entregada)
        {
            return entregada;
        }

        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        return SugeridoVigente is { } sugerido && redondeado < sugerido && clsMargenAjuste.LimpiarJustificacion(justificacion) is null
            ? new clsErrorDominio(clsCodigosError.JustificacionRequerida,
                $"El margen {clsReglasMargen.Texto(redondeado)} queda por debajo del sugerido ({clsReglasMargen.Texto(sugerido)}): escribe la justificación.")
            : null;
    }

    /// <summary>
    /// Regla del PDF (ADR-018 §11) para toda acción de la etapa final (margen, conexos, textos, re-solicitud en el mismo folio): ya
    /// entregada al cliente (<see cref="EntregadaAlCliente"/>) responde <c>PDF_YA_ENVIADO</c>; si no, <c>null</c> (se permite e invalida
    /// el PDF con <see cref="InvalidarPdf"/>).
    /// </summary>
    public clsErrorDominio? ValidarCambioEtapaFinal() => EntregadaAlCliente
        ? new clsErrorDominio(clsCodigosError.PdfYaEnviado,
            Ganada is null
                ? "La cotización ya se envió al cliente: crea una versión nueva para cambiarla."
                : "La cotización ya tiene resultado registrado: crea una versión nueva para cambiarla.")
        : null;

    /// <summary>
    /// Aplica el margen negociado (ya validado con <see cref="ValidarAjusteMargen"/>, redondeado a 4 decimales como
    /// <c>margenAplicado</c>) y el origen pasa a <c>negociado</c>. No toca <see cref="MargenPolitica"/> ni el sugerido (en una fila sin
    /// sugerido lo fija con el vigente, para no perderlo). Devuelve <c>false</c> (sin cambios) si es el margen vigente.
    /// </summary>
    public bool AplicarMargenNegociado(decimal margen)
    {
        var redondeado = clsMargenAjuste.RedondearMargen(margen);
        if (redondeado < 0m || redondeado > MargenMaximoAjuste)
        {
            throw new ArgumentOutOfRangeException(nameof(margen), margen, "El margen negociado debe estar entre 0 y 0,99.");
        }

        if (MargenAplicado == redondeado)
        {
            return false;
        }

        if (MargenSugerido is null && SugeridoVigente is { } vigente)
        {
            MargenSugerido = vigente;
            OrigenMargenSugerido = OrigenMargen is Margen.OrigenMargen.SapTm ? Margen.OrigenMargen.SapTm : Margen.OrigenMargen.PoliticaLinea;
        }

        MargenAplicado = redondeado;
        OrigenMargen = Margen.OrigenMargen.Negociado;
        return true;
    }

    /// <summary>Precio recalculado por un ajuste del comercial (el costo de Nexo no cambia).</summary>
    public void RegistrarPrecioAjustado(decimal precioFinal, string? serviciosConexos)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(precioFinal);
        PrecioFinal = precioFinal;
        ServiciosConexos = serviciosConexos;
    }

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
    ///
    /// Crea una cotización validando las invariantes baratas: folio obligatorio (máx. 50),
    /// versión ≥ 1, montos no negativos, márgenes ≥ 0 (y &lt; 1 con el método <c>margen</c>, ver <see cref="clsReglaMargen"/>) y TRM 0 en pesos (&gt; 0 en otra moneda).
    /// </summary>
    public static clsResultado<clsCotizacion> Crear(clsDatosCotizacion datos)
    {
        ArgumentNullException.ThrowIfNull(datos);

        var moneda = string.IsNullOrWhiteSpace(datos.Moneda) ? MonedaPesos : datos.Moneda.Trim().ToUpperInvariant();
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "folio", datos.Folio, LargoMaximoFolio),
            datos.Version < 1 ? new clsErrorDominio(CodigoError, "La versión de la cotización debe ser 1 o mayor.") : null,
            clsValidacion.NoNegativo(CodigoError, "costo", datos.Costo),
            clsValidacion.NoNegativo(CodigoError, "precioFinal", datos.PrecioFinal),
            clsReglaMargen.Validar(CodigoError, "margenPolitica", datos.MargenPolitica, datos.MetodoCalculo),
            clsReglaMargen.Validar(CodigoError, "margenAplicado", datos.MargenAplicado, datos.MetodoCalculo),
            moneda.Length != 3 ? new clsErrorDominio(CodigoError, "La moneda debe ser un código ISO de 3 letras (ej. COP).") : null,
            ValidarTrm(moneda, datos.TrmAplicada));

        if (error is not null)
        {
            return error;
        }

        return new clsCotizacion
        {
            Id = Guid.NewGuid(),
            Folio = datos.Folio.Trim(),
            Version = datos.Version,
            LineaNegocioId = datos.LineaNegocioId,
            ClienteNombre = clsValidacion.Normalizar(datos.ClienteNombre),
            NitCliente = clsValidacion.Normalizar(datos.NitCliente),
            OrigenCliente = datos.OrigenCliente,
            CosteoId = clsValidacion.Normalizar(datos.CosteoId),
            Costo = datos.Costo,
            MargenPolitica = datos.MargenPolitica,
            MargenAplicado = datos.MargenAplicado,
            OrigenMargen = datos.OrigenMargen,
            MargenSugerido = datos.OrigenMargen is Margen.OrigenMargen.Negociado ? datos.MargenPolitica
                : datos.OrigenMargen is null ? null : datos.MargenAplicado,
            OrigenMargenSugerido = datos.OrigenMargen switch
            {
                Margen.OrigenMargen.SapTm when datos.MargenAplicado is not null => Margen.OrigenMargen.SapTm,
                Margen.OrigenMargen.PoliticaLinea when datos.MargenAplicado is not null => Margen.OrigenMargen.PoliticaLinea,
                Margen.OrigenMargen.Negociado when datos.MargenPolitica is not null => Margen.OrigenMargen.PoliticaLinea,
                _ => null,
            },
            MetodoCalculo = clsValidacion.Normalizar(datos.MetodoCalculo),
            PrecioFinal = datos.PrecioFinal,
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

    /// <summary>
    /// Guarda los datos del formulario ampliado (ya validados con <see cref="clsSolicitudCosteo.Validar"/>) y deja la cotización
    /// en <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/>: así nunca se pierde lo que llenó el comercial aunque Nexo no
    /// responda (HU-005.5). Las posiciones van en <see cref="clsCotizacionPosicion"/>.
    /// </summary>
    public void RegistrarSolicitudCosteo(clsSolicitudCosteo solicitud)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        if (solicitud.Validar() is { } error)
        {
            throw new ArgumentException(error.Mensaje, nameof(solicitud));
        }

        TipoSolicitud = solicitud.Tipo;
        Deadline = solicitud.Deadline;
        ValorEstimado = solicitud.ValorEstimado;
        Referencia = clsValidacion.Normalizar(solicitud.Referencia);
        HorasCargue = solicitud.HorasCargue;
        HorasDescargue = solicitud.HorasDescargue;
        Notas = clsValidacion.Normalizar(solicitud.Notas);
        Origen = solicitud.Origen.Trim();
        Destino = solicitud.Destino.Trim();
        TipoVehiculo = clsValidacion.Normalizar(solicitud.TipoVehiculo);
        Carroceria = clsValidacion.Normalizar(solicitud.Carroceria);
        Tipologia = clsValidacion.Normalizar(solicitud.Tipologia);
        ClaseViaje = clsValidacion.Normalizar(solicitud.ClaseViaje);
        ClaseContenedor = clsValidacion.Normalizar(solicitud.ClaseContenedor);
        Exclusivo = solicitud.Exclusivo;
        Consolidado = solicitud.Consolidado;
        RetornaContenedor = solicitud.RetornaContenedor;
        CiudadDevolucionContenedor = clsValidacion.Normalizar(solicitud.CiudadDevolucionContenedor);
        LevanteContenedorDiferente = solicitud.LevanteContenedorDiferente;
        CiudadLevanteContenedor = clsValidacion.Normalizar(solicitud.CiudadLevanteContenedor);
        ValorAprovisionamiento = solicitud.ValorAprovisionamiento;
        PesoKg = solicitud.PesoKg;
        ValorMercancia = solicitud.ValorMercancia;
        EstadoCosteo = Cotizaciones.EstadoCosteo.PendienteEnvio;
        MotivoEstadoCosteo = null;
    }

    /// <summary>
    /// Guarda el encabezado de una licitación (HU-011.2, ADR-017; las N líneas van en <see cref="clsCotizacionLineaSolicitud"/>) y la
    /// deja en <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/>. Las columnas de línea de <c>Cotizaciones</c> (obsoletas desde
    /// el 0005) quedan en <c>null</c>: una licitación no tiene "la" línea.
    /// </summary>
    public void RegistrarSolicitudLicitacion(clsEncabezadoSolicitudCosteo encabezado)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        if (encabezado.Errores() is { Count: > 0 } errores)
        {
            throw new ArgumentException(errores[0].Mensaje, nameof(encabezado));
        }

        TipoSolicitud = encabezado.Tipo;
        Deadline = encabezado.Deadline;
        ValorEstimado = encabezado.ValorEstimado;
        Referencia = clsValidacion.Normalizar(encabezado.Referencia);
        HorasCargue = encabezado.HorasCargue;
        HorasDescargue = encabezado.HorasDescargue;
        Notas = clsValidacion.Normalizar(encabezado.Notas);
        EstadoCosteo = Cotizaciones.EstadoCosteo.PendienteEnvio;
        MotivoEstadoCosteo = null;
    }

    /// <summary>Encabezado de la solicitud guardada, o <c>null</c> si la cotización no pasó por el costeo en Nexo.</summary>
    public clsEncabezadoSolicitudCosteo? EncabezadoSolicitud => TipoSolicitud is { } tipo
        ? new clsEncabezadoSolicitudCosteo
        {
            Tipo = tipo,
            Deadline = Deadline,
            ValorEstimado = ValorEstimado,
            Referencia = Referencia,
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
            Notas = Notas,
        }
        : null;

    /// <summary>
    /// La solicitud guardada, para reenviarla a Nexo sin volver a llenar el formulario (HU-005.5); <c>null</c> si la cotización
    /// no nació del formulario ampliado (no tiene tipo, origen o destino).
    /// </summary>
    public clsSolicitudCosteo? SolicitudCosteo(IEnumerable<clsCotizacionPosicion> posiciones)
    {
        ArgumentNullException.ThrowIfNull(posiciones);
        if (TipoSolicitud is not { } tipo || Origen is null || Destino is null)
        {
            return null;
        }

        return new clsSolicitudCosteo
        {
            Tipo = tipo,
            Deadline = Deadline,
            ValorEstimado = ValorEstimado,
            Referencia = Referencia,
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
            Notas = Notas,
            Origen = Origen,
            Destino = Destino,
            TipoVehiculo = TipoVehiculo,
            Carroceria = Carroceria,
            Tipologia = Tipologia,
            ClaseViaje = ClaseViaje,
            ClaseContenedor = ClaseContenedor,
            Exclusivo = Exclusivo,
            Consolidado = Consolidado,
            RetornaContenedor = RetornaContenedor,
            CiudadDevolucionContenedor = CiudadDevolucionContenedor,
            LevanteContenedorDiferente = LevanteContenedorDiferente,
            CiudadLevanteContenedor = CiudadLevanteContenedor,
            ValorAprovisionamiento = ValorAprovisionamiento,
            PesoKg = PesoKg,
            ValorMercancia = ValorMercancia,
            Posiciones = [.. posiciones.OrderBy(p => p.Orden).Select(p => p.APosicion())],
        };
    }

    /// <summary>
    /// ¿Se puede (re)enviar la solicitud a Nexo? Sí desde <see cref="Cotizaciones.EstadoCosteo.ErrorEnvio"/>, y desde
    /// <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/> cuando el último cambio tiene más de
    /// <see cref="EsperaReenvioPendiente"/> (un envío que se cortó a medias, HU-005.5).
    /// </summary>
    public bool PuedeReenviarCosteo(DateTimeOffset ahora) =>
        Ganada is null
        && ((EstadoCosteo == Cotizaciones.EstadoCosteo.ErrorEnvio && !ErrorEnvioDefinitivo)
            || (EstadoCosteo == Cotizaciones.EstadoCosteo.PendienteEnvio && ahora - UpdatedAt >= EsperaReenvioPendiente));

    /// <summary>
    /// ¿La cotización nació del flujo de costeo en Nexo? (formulario ampliado: <see cref="EstadoCosteo"/> con valor). No se usa
    /// <see cref="CosteoId"/>: también lo llena el chat (referencia de la tarifa) y lo traen filas del Python.
    /// </summary>
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
    /// Nexo recibió la solicitud (HU-005.5): guarda su id (<see cref="CosteoId"/>), la deja en
    /// <see cref="Cotizaciones.EstadoCosteo.Autocosteo"/> si Nexo ya costeó todas las líneas o en
    /// <see cref="Cotizaciones.EstadoCosteo.PendienteCosteo"/> si no, y agenda la primera consulta del polling (HU-005.6).
    /// </summary>
    public void RegistrarEnvioCosteo(string costeoId, bool costeoCompleto, DateTimeOffset enviadoAt, DateTimeOffset primeraConsulta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(costeoId);
        CosteoId = costeoId.Trim();
        EstadoCosteo = costeoCompleto ? Cotizaciones.EstadoCosteo.Autocosteo : Cotizaciones.EstadoCosteo.PendienteCosteo;
        MotivoEstadoCosteo = null;
        CosteoEnviadoAt = enviadoAt;
        CosteoSiguienteConsultaAt = primeraConsulta;
        CosteoConsultasFallidas = 0;
    }

    /// <summary>
    /// Nexo no recibió la solicitud (tiempo agotado, 4xx, 5xx, red, cliente sin BP): queda en
    /// <see cref="Cotizaciones.EstadoCosteo.ErrorEnvio"/> con el motivo (recortado a <see cref="LargoMaximoMotivoCosteo"/>) y sin
    /// polling; el comercial la reintenta sin volver a llenar el formulario.
    /// </summary>
    public void RegistrarErrorEnvioCosteo(string motivo, bool definitivo = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        EstadoCosteo = Cotizaciones.EstadoCosteo.ErrorEnvio;
        MotivoEstadoCosteo = Recortar(definitivo ? PrefijoRechazoDefinitivo + motivo.Trim() : motivo);
        CosteoSiguienteConsultaAt = null;
    }

    /// <summary>¿El polling debe consultar esta cotización? (tiene <see cref="CosteoId"/> y no está en un estado terminal.)</summary>
    public bool SeConsultaEnNexo =>
        CosteoId is not null
        && EstadoCosteo is Cotizaciones.EstadoCosteo.PendienteCosteo or Cotizaciones.EstadoCosteo.Autocosteo;

    /// <summary>
    /// Resultado de una consulta del polling (HU-005.6): el estado derivado, el id de la cotización de Nexo, la última consulta y la
    /// siguiente (<c>null</c> = no se consulta más). Reinicia los fallos seguidos. Devuelve <c>true</c> si el estado cambió.
    /// </summary>
    public bool RegistrarConsultaCosteo(Cotizaciones.EstadoCosteo estado, string? cotizacionNexoId, DateTimeOffset consultadaAt, DateTimeOffset? siguiente)
    {
        var cambio = EstadoCosteo != estado;
        EstadoCosteo = estado;
        CotizacionNexoId = string.IsNullOrWhiteSpace(cotizacionNexoId) ? CotizacionNexoId : cotizacionNexoId.Trim();
        CosteoUltimaConsultaAt = consultadaAt;
        CosteoSiguienteConsultaAt = siguiente;
        CosteoConsultasFallidas = 0;
        if (cambio)
        {
            MotivoEstadoCosteo = null;
        }

        return cambio;
    }

    /// <summary>Una consulta del polling falló (Nexo caído, 4xx, 5xx, id no reconocido): suma un fallo y reprograma, sin cambiar el estado.</summary>
    public void RegistrarFalloConsultaCosteo(DateTimeOffset consultadaAt, DateTimeOffset? siguiente)
    {
        CosteoUltimaConsultaAt = consultadaAt;
        CosteoSiguienteConsultaAt = siguiente;
        CosteoConsultasFallidas++;
    }

    /// <summary>
    /// Se llegó a la edad máxima del polling (HU-005.6): sin costeo queda en <see cref="Cotizaciones.EstadoCosteo.CosteoVencido"/>
    /// con el motivo (visible en "Mis cotizaciones"); con costeo (<c>autocosteo</c>) solo deja de consultarse. Devuelve <c>true</c> si el
    /// estado cambió.
    /// </summary>
    public bool VencerPollingCosteo(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        CosteoSiguienteConsultaAt = null;
        if (EstadoCosteo != Cotizaciones.EstadoCosteo.PendienteCosteo)
        {
            return false;
        }

        EstadoCosteo = Cotizaciones.EstadoCosteo.CosteoVencido;
        MotivoEstadoCosteo = Recortar(motivo);
        return true;
    }

    /// <summary>
    /// Margen resuelto al enviar la solicitud a Nexo (HU-005.7, ADR-015 §5: con el BP del catálogo): se guarda para aplicarlo cuando
    /// llegue el costeo. Valida el margen con su método (<see cref="clsReglaMargen"/>).
    /// </summary>
    public void RegistrarMargenCosteo(decimal? margenPolitica, decimal margenAplicado, OrigenMargen origen, string metodoCalculo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metodoCalculo);
        var metodo = metodoCalculo.Trim().ToLowerInvariant();
        if (clsValidacion.Primero(
                clsReglaMargen.Validar(CodigoError, "margenPolitica", margenPolitica, metodo),
                clsReglaMargen.Validar(CodigoError, "margenAplicado", margenAplicado, metodo)) is { } error)
        {
            throw new ArgumentException(error.Mensaje, nameof(margenAplicado));
        }

        MargenPolitica = margenPolitica;
        MargenSugerido = origen == Margen.OrigenMargen.Negociado ? margenPolitica : margenAplicado;
        OrigenMargenSugerido = origen == Margen.OrigenMargen.SapTm ? Margen.OrigenMargen.SapTm
            : origen == Margen.OrigenMargen.Negociado && margenPolitica is null ? null : Margen.OrigenMargen.PoliticaLinea;
        MargenAplicado = margenAplicado;
        OrigenMargen = origen;
        MetodoCalculo = metodo;
    }

    /// <summary>
    /// Precio sobre el costeo de Nexo (HU-005.7): <paramref name="costo"/> es el costo de Nexo (interno) y
    /// <paramref name="precioFinal"/> el que salió del módulo de Precio con el margen guardado. <see cref="CosteoListoAt"/> queda en
    /// la primera vez.
    /// </summary>
    public void RegistrarPrecioCosteo(decimal costo, decimal precioFinal, string? serviciosConexos, DateTimeOffset listoAt)
    {
        if (costo < 0m || precioFinal < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(costo), "El costo y el precio no pueden ser negativos.");
        }

        Costo = costo;
        PrecioFinal = precioFinal;
        ServiciosConexos = serviciosConexos;
        CosteoListoAt ??= listoAt;
    }

    /// <summary>Advertencia sobre el costeo (sin margen, costeo cambiado después del PDF...), visible en "Mis cotizaciones".</summary>
    public void AdvertirCosteo(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        MotivoEstadoCosteo = Recortar(motivo);
    }

    /// <summary>Guarda la selección de servicios conexos del comercial (JSON de <c>serviciosConexos</c>).</summary>
    public void SeleccionarServiciosConexos(string? serviciosConexos) => ServiciosConexos = serviciosConexos;

    /// <summary>
    /// Guarda el asunto y la introducción del PDF (JSON de <c>textosCotizacion</c>, HU-013.5, #101). Los valida Aplicación (longitud,
    /// sin URL ni correos y, si los redactó la IA, sin montos); aquí solo se exige que no venga vacío.
    /// </summary>
    public void RegistrarTextos(string textosJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(textosJson);
        TextosCotizacion = textosJson;
    }

    /// <summary>Id de la cotización de Nexo recuperado fuera del polling (al marcar el resultado, HU-005.14).</summary>
    public void RegistrarCotizacionNexo(string cotizacionNexoId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cotizacionNexoId);
        CotizacionNexoId = cotizacionNexoId.Trim();
    }

    /// <summary>
    /// Nexo ya no reconoce la solicitud (404): se deja de consultar con el motivo; sin costeo queda en
    /// <see cref="Cotizaciones.EstadoCosteo.CosteoVencido"/>. Devuelve <c>true</c> si el estado cambió.
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

        EstadoCosteo = Cotizaciones.EstadoCosteo.CosteoVencido;
        return true;
    }

    /// <summary>Deja de consultar a Nexo (estado que ya no se sigue, o datos incompletos).</summary>
    public void DetenerPollingCosteo() => CosteoSiguienteConsultaAt = null;

    /// <summary>Recorta un motivo a <see cref="LargoMaximoMotivoCosteo"/> caracteres (la columna es <c>NVARCHAR(1000)</c>).</summary>
    public static string Recortar(string motivo)
    {
        ArgumentNullException.ThrowIfNull(motivo);
        var limpio = motivo.Trim();
        return limpio.Length <= LargoMaximoMotivoCosteo ? limpio : limpio[..(LargoMaximoMotivoCosteo - 1)] + "…";
    }

    /// <summary>¿La cotización pertenece al comercial? (aislamiento por dueño, ADR-009).</summary>
    public bool EsDe(Guid usuarioId) => UsuarioId == usuarioId;

    /// <summary>Marca la cotización como ganada o perdida (seguimiento interino hasta HU-005.14).</summary>
    public void MarcarSeguimiento(bool? ganada, string? motivoRechazo, DateTimeOffset cuando)
    {
        Ganada = ganada;
        MotivoRechazo = clsValidacion.Normalizar(motivoRechazo);
        MarcadoAt = cuando;
    }

    /// <summary>Registra un envío exitoso: el primero la deja <c>enviada</c>; los siguientes, <c>reenviada</c>.</summary>
    public void RegistrarEnvio() =>
        EstadoEnvio = EstadoEnvio is null or Cotizaciones.EstadoEnvio.SinEnviar
            ? Cotizaciones.EstadoEnvio.Enviada
            : Cotizaciones.EstadoEnvio.Reenviada;

    /// <summary>
    /// Deshace la reserva de un envío que no salió (el transporte falló): vuelve al estado de envío anterior (ADR-018 §12: el correo
    /// marca la cotización como enviada ANTES de enviar, para que ningún ajuste invalide el PDF mientras sale).
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

    /// <summary>
    /// Invariantes de "Cotizar costo" (re-solicitar el costeo a Nexo, ADR-018 §5 y §11), en este orden:
    /// <list type="number">
    /// <item>la cotización nació del formulario de costeo (si no, <c>ESTADO_NO_PERMITE</c>);</item>
    /// <item>sin un costeo en curso (<c>pendiente_envio</c> o <c>pendiente_costeo</c>: <c>ESTADO_NO_PERMITE</c>), en el mismo folio y en
    /// la versión nueva;</item>
    /// <item>en el mismo folio (<paramref name="versionNueva"/> falso): sin envío al cliente ni resultado (<c>PDF_YA_ENVIADO</c>) y con
    /// <c>estado_costeo</c> en <c>costeo_vencido</c>, <c>entregada</c> o un <c>error_envio</c> que no sea un rechazo definitivo de Nexo
    /// (reenviar los mismos datos no sirve: se corrigen en una versión nueva); en otro estado, <c>ESTADO_NO_PERMITE</c>.</item>
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
    /// Deja la cotización lista para volver a enviarla a Nexo en el mismo folio (HU-013.4): sin el costeo anterior (id de la solicitud y
    /// de la cotización de Nexo, costo, precio, conexos y fecha de costeo; el caso de uso los deja antes en la auditoría), sin polling y
    /// en <see cref="Cotizaciones.EstadoCosteo.PendienteEnvio"/> mientras se envía. El margen se vuelve a resolver al enviar.
    /// </summary>
    public void PrepararReSolicitud()
    {
        CosteoId = null;
        CotizacionNexoId = null;
        Costo = null;
        PrecioFinal = null;
        ServiciosConexos = null;
        CosteoListoAt = null;
        CosteoUltimaConsultaAt = null;
        CosteoSiguienteConsultaAt = null;
        CosteoConsultasFallidas = 0;
        EstadoCosteo = Cotizaciones.EstadoCosteo.PendienteEnvio;
        MotivoEstadoCosteo = null;
    }

    /// <summary>
    /// Cotización de la versión <paramref name="folio"/> (<c>-vN</c>, ADR-011) con la identidad de esta: cliente, línea de negocio,
    /// comercial y moneda. La TRM NO se copia (decisión del 2026-10-06, ADR-014 §5 y ADR-018 §5): la versión nueva es una cotización
    /// nueva y el caso de uso fija la TRM del día (<paramref name="trmDelDia"/>, 0 en COP) antes de crearla. El formulario, las líneas y
    /// las posiciones los registra el caso de uso; el costeo, el PDF, los textos y el resultado empiezan de cero (la versión nueva pasa
    /// otra vez por Nexo, ADR-018 §11).
    /// </summary>
    public clsResultado<clsCotizacion> CrearVersion(clsFolio folio, decimal trmDelDia)
    {
        ArgumentNullException.ThrowIfNull(folio);
        if (folio.Version <= Version)
        {
            throw new ArgumentException($"La versión nueva debe ser mayor que {Version}.", nameof(folio));
        }

        // Un margen ajustado por el comercial se copia (decisión del 2026-10-06): el envío lo conserva y solo refresca el sugerido.
        var negociado = TieneMargenNegociado;
        return Crear(new clsDatosCotizacion
        {
            Folio = folio.Valor,
            Version = folio.Version,
            LineaNegocioId = LineaNegocioId,
            ClienteNombre = ClienteNombre,
            NitCliente = NitCliente,
            OrigenCliente = OrigenCliente,
            UsuarioId = UsuarioId,
            Moneda = Moneda ?? MonedaPesos,
            TrmAplicada = trmDelDia,
            MargenPolitica = negociado ? MargenPolitica : null,
            MargenAplicado = negociado ? MargenAplicado : null,
            OrigenMargen = negociado ? Margen.OrigenMargen.Negociado : null,
            MetodoCalculo = negociado ? MetodoCalculo : null,
        });
    }

    /// <summary>¿El comercial ajustó el margen? (<c>origen_margen = negociado</c> con margen y método de cálculo.)</summary>
    public bool TieneMargenNegociado =>
        OrigenMargen == Margen.OrigenMargen.Negociado && MargenAplicado is not null && MetodoCalculo is not null;

    /// <summary>
    /// Re-solicitud con margen negociado (HU-013.4, decisión del 2026-10-06): conserva <see cref="MargenAplicado"/> y el origen
    /// <c>negociado</c>, y refresca el margen sugerido y su fuente (SAP TM o la política de la línea) como referencia.
    /// </summary>
    public void RefrescarMargenSugerido(decimal? margenPolitica, decimal sugerido, OrigenMargen origen)
    {
        if (sugerido < 0m || origen == Margen.OrigenMargen.Negociado)
        {
            throw new ArgumentOutOfRangeException(nameof(sugerido), "El sugerido es no negativo y sale de SAP TM o de la política.");
        }

        MargenPolitica = margenPolitica ?? MargenPolitica;
        MargenSugerido = sugerido;
        OrigenMargenSugerido = origen;
    }

    private clsErrorDominio NoPermite(string motivo) => new(clsCodigosError.EstadoNoPermite,
        $"La cotización {Folio} no permite re-solicitar el costeo en su estado actual "
        + $"({(EstadoCosteo is { } estado ? clsCodigoEnum.ACodigo(estado) : "sin costeo en Nexo")}): {motivo}.");

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
