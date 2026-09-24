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
    public string? EstadoCosteo { get; private set; }
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
            EstadoCosteo = clsValidacion.Normalizar(datos.EstadoCosteo),
            EstadoEnvio = Cotizaciones.EstadoEnvio.SinEnviar,
            UsuarioId = datos.UsuarioId,
            SpaceId = clsValidacion.Normalizar(datos.SpaceId),
            ThreadId = clsValidacion.Normalizar(datos.ThreadId),
            CosteoListoAt = datos.CosteoListoAt,
        };
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
