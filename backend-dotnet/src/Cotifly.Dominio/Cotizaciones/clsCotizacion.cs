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

    /// <summary>
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
        EstadoCosteo == Cotizaciones.EstadoCosteo.ErrorEnvio
        || (EstadoCosteo == Cotizaciones.EstadoCosteo.PendienteEnvio && ahora - UpdatedAt >= EsperaReenvioPendiente);

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
    public void RegistrarErrorEnvioCosteo(string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        EstadoCosteo = Cotizaciones.EstadoCosteo.ErrorEnvio;
        MotivoEstadoCosteo = Recortar(motivo);
        CosteoSiguienteConsultaAt = null;
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

    /// <summary>Asocia la ruta del PDF generado.</summary>
    public void AsignarPdf(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        PdfPath = pdfPath.Trim();
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
