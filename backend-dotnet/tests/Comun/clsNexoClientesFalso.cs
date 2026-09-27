using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Nexo falso para el buscador de clientes (HU-005.2): un <see cref="HttpMessageHandler"/> que nunca sale a la red.
/// Registra cada petición (URL y cabecera <c>X-Portal-Usuario-Id</c>) y permite encolar respuestas o fallos.
/// </summary>
public sealed class clsNexoClientesFalso : HttpMessageHandler
{
    /// <summary>URL base del servicio en las pruebas.</summary>
    public const string UrlBase = "http://nexo-test.local/api";

    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _respuestas = new();
    private readonly ConcurrentQueue<clsPeticionNexo> _peticiones = new();

    /// <summary>Peticiones recibidas, en orden.</summary>
    public IReadOnlyList<clsPeticionNexo> Peticiones => [.. _peticiones];

    /// <summary>Encola una respuesta fija (se consume una por petición).</summary>
    public clsNexoClientesFalso Responder(HttpStatusCode estado, string? json = null)
    {
        _respuestas.Enqueue(_ => new HttpResponseMessage(estado)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    /// <summary>Encola una respuesta 200 con el array <c>ClienteBusquedaDto[]</c> de Nexo.</summary>
    public clsNexoClientesFalso ResponderCandidatos(string json) => Responder(HttpStatusCode.OK, json);

    /// <summary>Encola un fallo de red o de tiempo (la excepción se lanza al enviar).</summary>
    public clsNexoClientesFalso Fallar(Exception excepcion)
    {
        _respuestas.Enqueue(_ => throw excepcion);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _peticiones.Enqueue(new clsPeticionNexo(
            request.Method,
            request.RequestUri!,
            request.Headers.TryGetValues("X-Portal-Usuario-Id", out var valores) ? valores.FirstOrDefault() : null,
            request.Headers.Accept.ToString()));

        var respuesta = _respuestas.TryDequeue(out var fabrica) ? fabrica(request) : new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(respuesta);
    }
}

/// <summary>Petición recibida por <see cref="clsNexoClientesFalso"/>.</summary>
public sealed record clsPeticionNexo(HttpMethod Metodo, Uri Url, string? IdPortalUsuario, string Accept);
