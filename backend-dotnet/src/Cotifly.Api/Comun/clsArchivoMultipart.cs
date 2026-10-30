using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Api.Comun;

/// <summary>
/// Lectura del Excel que llega en una parte de un <c>multipart/form-data</c> (HU-011.2; HU-035.1, #202, la sacó del controller de
/// licitaciones para compartirla con el Excel de mercancías). Un archivo de más de <see cref="clsArchivoExcel.TamanoMaximoBytes"/> no
/// se copia: se devuelve un marcador de ese tamaño más un byte para que la validación responda <c>ARCHIVO_DEMASIADO_GRANDE</c>.
/// </summary>
public static class clsArchivoMultipart
{
    /// <summary>
    /// El archivo de la parte <paramref name="parte"/> del formulario, o <c>null</c> si no viene o el multipart está mal formado (la
    /// validación lo reporta como <c>ARCHIVO_REQUERIDO</c>).
    /// </summary>
    public static async Task<clsArchivoSubido?> LeerParteAsync(HttpRequest request, string parte, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var formulario = await request.ReadFormAsync(cancellationToken);
            return formulario.Files.GetFile(parte) is { } subido ? await LeerAsync(subido, cancellationToken) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Lee el archivo hasta 10 MB menos 64 KB (lo que pase se reporta como <c>ARCHIVO_DEMASIADO_GRANDE</c>).</summary>
    public static async Task<clsArchivoSubido> LeerAsync(IFormFile subido, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subido);
        if (subido.Length > clsArchivoExcel.TamanoMaximoBytes)
        {
            return new clsArchivoSubido(subido.FileName, new byte[clsArchivoExcel.TamanoMaximoBytes + 1]);
        }

        using var memoria = new MemoryStream((int)Math.Max(subido.Length, 0));
        await using var flujo = subido.OpenReadStream();
        await flujo.CopyToAsync(memoria, cancellationToken);
        return new clsArchivoSubido(subido.FileName, memoria.ToArray());
    }
}
