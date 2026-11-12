namespace Cotifly.Aplicacion.Comun.Exceptions;

/// <summary>
/// El render del PDF esperó su turno más de <c>Pdf:TiempoEsperaRenderSegundos</c>: otros PDF ocupaban el generador, que
/// renderiza uno a la vez por proceso (PDFsharp-MigraDoc no es thread-safe). Los casos de uso la traducen a
/// <c>PDF_RENDER_NO_DISPONIBLE</c> (503).
/// </summary>
public sealed class clsRenderPdfOcupadoException : Exception
{
    public clsRenderPdfOcupadoException()
        : this(TimeSpan.Zero)
    {
    }

    public clsRenderPdfOcupadoException(string message)
        : base(message)
    {
    }

    public clsRenderPdfOcupadoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public clsRenderPdfOcupadoException(TimeSpan espera)
        : base($"El generador de PDF siguió ocupado durante {espera.TotalSeconds:0.#} s.")
    {
        Espera = espera;
    }

    /// <summary>Tiempo que se esperó el turno.</summary>
    public TimeSpan Espera { get; }
}
