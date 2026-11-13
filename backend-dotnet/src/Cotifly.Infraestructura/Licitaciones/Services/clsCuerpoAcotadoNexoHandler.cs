using System.Globalization;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Costeo.Services;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Licitaciones.Services;

/// <summary>
/// Manejador del cliente <see cref="clsImportacionExcelNexo.NombreCliente"/> (ADR-028), por dentro de la tubería de
/// resiliencia: lee el cuerpo completo de la respuesta de NEXO antes de devolverla, así el tiempo de cada intento cubre también el cuerpo (un
/// NEXO que manda las cabeceras y se queda colgado agota el intento, no espera sin límite) y el tamaño queda acotado a
/// <c>ImportacionInteligente:LimiteRespuestaMegabytes</c>. Una respuesta mayor lanza <see cref="InvalidDataException"/>, que la tubería no
/// reintenta ni cuenta para el circuito y el adaptador traduce a <c>NEXO_NO_DISPONIBLE</c>.
/// </summary>
public sealed class clsCuerpoAcotadoNexoHandler(IOptions<clsOpcionesImportacionInteligente> opciones) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var respuesta = await base.SendAsync(request, cancellationToken);
        try
        {
            var limite = opciones.Value.LimiteRespuestaMegabytes * 1024L * 1024L;
            if (respuesta.Content.Headers.ContentLength is { } largo && largo > limite)
            {
                throw new InvalidDataException($"respuesta demasiado grande ({largo.ToString(CultureInfo.InvariantCulture)} bytes)");
            }

            await using var flujo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var acotado = await clsCosteoNexo.LeerAcotadoAsync(flujo, limite, cancellationToken)
                ?? throw new InvalidDataException($"respuesta demasiado grande (más de {limite.ToString(CultureInfo.InvariantCulture)} bytes)");
            var leido = new ByteArrayContent(acotado.ToArray());
            foreach (var (nombre, valores) in respuesta.Content.Headers)
            {
                leido.Headers.TryAddWithoutValidation(nombre, valores);
            }

            leido.Headers.ContentLength = acotado.Length;
            respuesta.Content.Dispose();
            respuesta.Content = leido;
            return respuesta;
        }
        catch
        {
            respuesta.Dispose();
            throw;
        }
    }
}
