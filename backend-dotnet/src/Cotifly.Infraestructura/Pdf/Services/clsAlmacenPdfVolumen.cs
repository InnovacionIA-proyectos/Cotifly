using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Pdf.Services;

/// <summary>
/// Almacén de PDFs en el volumen del contenedor (<c>AlmacenVolumen</c> de <c>shared/almacen_pdf.py</c>), carpeta
/// <c>Pdf:StorageDir</c> (por defecto <c>/data/pdfs</c>). Solo acepta nombres <c>^[A-Za-z0-9_-]+\.pdf$</c> y verifica que la
/// ruta final quede dentro de la carpeta (sin <c>..</c> ni rutas absolutas). Guardar nunca reemplaza un archivo existente.
/// </summary>
/// <remarks>
/// Diferencia con el Python: <c>leer_por_ruta</c> leía cualquier archivo local cuya ruta estuviera en <c>pdfPath</c>; aquí
/// solo se toma el nombre del archivo y se lee dentro del almacén.
/// </remarks>
public sealed partial class clsAlmacenPdfVolumen(IOptions<clsOpcionesAlmacenPdf> opciones, ILogger<clsAlmacenPdfVolumen> logger) : IClsAlmacenPdf
{
    private readonly string _carpeta = Path.GetFullPath(opciones.Value.StorageDir);

    /// <summary>¿El nombre es un nombre de PDF aceptado (<c>^[A-Za-z0-9_-]+\.pdf$</c>)?</summary>
    public static bool EsNombreValido(string? nombre) => nombre is not null && ExpresionNombre().IsMatch(nombre);

    public async Task<string?> GuardarNuevoAsync(string nombre, byte[] contenido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        var ruta = Ruta(nombre) ?? throw new ArgumentException($"Nombre de PDF inválido: '{nombre}'.", nameof(nombre));
        Directory.CreateDirectory(_carpeta);

        FileStream archivo;
        try
        {
            archivo = new FileStream(ruta, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        }
        catch (IOException) when (File.Exists(ruta))
        {
            return null;
        }

        await using (archivo)
        {
            try
            {
                await archivo.WriteAsync(contenido, cancellationToken);
                await archivo.FlushAsync(cancellationToken);
            }
            catch
            {
                await archivo.DisposeAsync();
                Borrar(ruta);
                throw;
            }
        }

        return ruta;
    }

    public async Task<byte[]?> LeerAsync(string nombre, CancellationToken cancellationToken = default)
    {
        var ruta = Ruta(nombre);
        if (ruta is null || !File.Exists(ruta))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(ruta, cancellationToken);
    }

    public Task<byte[]?> LeerPorReferenciaAsync(string? pdfPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pdfPath))
        {
            return Task.FromResult<byte[]?>(null);
        }

        // Solo el nombre: la referencia puede venir de otra máquina (\ o /) y nunca debe sacar la lectura del almacén.
        var nombre = pdfPath.Replace('\\', '/').Split('/').Last();
        return LeerAsync(nombre, cancellationToken);
    }

    public Task EliminarAsync(string nombre, CancellationToken cancellationToken = default)
    {
        var ruta = Ruta(nombre);
        if (ruta is not null)
        {
            Borrar(ruta);
        }

        return Task.CompletedTask;
    }

    /// <summary>Ruta absoluta dentro de la carpeta, o <c>null</c> si el nombre no es válido o saldría de ella.</summary>
    private string? Ruta(string? nombre)
    {
        if (!EsNombreValido(nombre))
        {
            return null;
        }

        var ruta = Path.GetFullPath(Path.Combine(_carpeta, nombre!));
        return string.Equals(Path.GetDirectoryName(ruta), _carpeta.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal)
            ? ruta
            : null;
    }

    private void Borrar(string ruta)
    {
        try
        {
            File.Delete(ruta);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("No se pudo borrar el PDF {Ruta}: {Motivo}", ruta, ex.Message);
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.pdf\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionNombre();
}
