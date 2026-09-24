using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Ajuste (negociación) del margen frente a la política (<c>dbo_cotifly.MargenAjustes</c>).
/// <see cref="Diferencia"/> = aplicado − política, redondeada a 4 decimales (escala de la columna).
/// </summary>
public sealed class clsMargenAjuste
{
    public const string CodigoError = "AJUSTE_MARGEN_INVALIDO";

    private clsMargenAjuste()
    {
    }

    public Guid Id { get; private set; }
    public Guid? CotizacionId { get; private set; }
    public string? Folio { get; private set; }
    public decimal? MargenPolitica { get; private set; }
    public decimal? MargenAplicado { get; private set; }
    public decimal? Diferencia { get; private set; }
    public string? Motivo { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Registra un ajuste. El motivo es obligatorio y los márgenes deben estar en [0, 1).</summary>
    public static clsResultado<clsMargenAjuste> Registrar(
        decimal margenPolitica,
        decimal margenAplicado,
        string motivo,
        Guid? usuarioId = null,
        Guid? cotizacionId = null,
        string? folio = null)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "motivo", motivo),
            margenPolitica is < 0m or >= 1m ? new clsErrorDominio(CodigoError, "El margen de política debe estar en [0, 1).") : null,
            margenAplicado is < 0m or >= 1m ? new clsErrorDominio(CodigoError, "El margen aplicado debe estar en [0, 1).") : null);

        if (error is not null)
        {
            return error;
        }

        return new clsMargenAjuste
        {
            Id = Guid.NewGuid(),
            CotizacionId = cotizacionId,
            Folio = clsValidacion.Normalizar(folio),
            MargenPolitica = margenPolitica,
            MargenAplicado = margenAplicado,
            Diferencia = Math.Round(margenAplicado - margenPolitica, 4, MidpointRounding.ToEven),
            Motivo = motivo.Trim(),
            UsuarioId = usuarioId,
        };
    }
}
