namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// PDF invalidado de una cotización (<c>dbo_cotifly.CotizacionPdfsHistorial</c>, script 0006, ADR-018 §11): la referencia de su
/// archivo en el almacén (el archivo no se borra), la acción que lo invalidó, quién y cuándo. <see cref="CreatedAt"/> lo asigna la
/// persistencia.
/// </summary>
public sealed class clsCotizacionPdfHistorial
{
    /// <summary>Largo de <c>pdfPath</c> en el historial (<c>NVARCHAR(400)</c>).</summary>
    public const int LargoPdfPath = 400;

    private clsCotizacionPdfHistorial()
    {
    }

    public Guid Id { get; private set; }
    public Guid CotizacionId { get; private set; }
    public string PdfPath { get; private set; } = string.Empty;
    public MotivoInvalidacionPdf Motivo { get; private set; }
    public Guid? UsuarioId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Registra un PDF invalidado. La cotización y la referencia son obligatorias (máximo <see cref="LargoPdfPath"/>).</summary>
    public static clsCotizacionPdfHistorial Registrar(Guid cotizacionId, string pdfPath, MotivoInvalidacionPdf motivo, Guid? usuarioId)
    {
        if (cotizacionId == Guid.Empty)
        {
            throw new ArgumentException("El historial del PDF exige la cotización.", nameof(cotizacionId));
        }

        return new clsCotizacionPdfHistorial
        {
            Id = Guid.NewGuid(),
            CotizacionId = cotizacionId,
            PdfPath = Referencia(pdfPath),
            Motivo = motivo,
            UsuarioId = usuarioId,
        };
    }

    /// <summary>
    /// El archivo se movió dentro del almacén: al regenerar el PDF, el anterior se archiva con otro nombre para liberar el del folio
    /// (el archivo se conserva; solo cambia su referencia).
    /// </summary>
    public void Archivar(string pdfPath) => PdfPath = Referencia(pdfPath);

    private static string Referencia(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        var referencia = pdfPath.Trim();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(referencia.Length, LargoPdfPath, nameof(pdfPath));
        return referencia;
    }
}
