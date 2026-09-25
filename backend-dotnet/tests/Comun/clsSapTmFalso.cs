using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// SAP TM falso para el adaptador OData (equivale a <c>respx</c> en <c>tests/test_margen.py</c>): un
/// <see cref="HttpMessageHandler"/> que nunca sale a la red. Por defecto responde <c>RentabClienteSet</c> filtrando la
/// muestra del Python (<see cref="RutaMuestra"/>) con el <c>$filter</c> recibido, así el export y el OData ven los
/// mismos datos. Registra cada petición (URL y cabecera <c>Authorization</c>) y permite encolar respuestas o fallos.
/// </summary>
public sealed partial class clsSapTmFalso : HttpMessageHandler
{
    /// <summary>Ruta de <c>tests/fixtures/sap/rentab_cliente_muestra.json</c> copiada a la salida de las pruebas.</summary>
    public static readonly string RutaMuestra = Path.Combine(AppContext.BaseDirectory, "fixtures", "sap", "rentab_cliente_muestra.json");

    /// <summary>URL base del servicio en las pruebas (la misma de <c>_SAP_BASE</c> del Python).</summary>
    public const string UrlBase = "http://sap-tm-test.local/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV";

    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _respuestas = new();
    private readonly ConcurrentQueue<clsPeticionSap> _peticiones = new();

    /// <summary>Peticiones recibidas, en orden.</summary>
    public IReadOnlyList<clsPeticionSap> Peticiones => [.. _peticiones];

    /// <summary>Encola una respuesta fija (se consume una por petición; sin cola se responde desde la muestra).</summary>
    public clsSapTmFalso Responder(HttpStatusCode estado, string? json = null)
    {
        _respuestas.Enqueue(_ => new HttpResponseMessage(estado)
        {
            Content = new StringContent(json ?? string.Empty, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    /// <summary>Encola una respuesta 200 con <c>{"d":{"results":[filas]}}</c>.</summary>
    public clsSapTmFalso ResponderFilas(params object[] filas) =>
        Responder(HttpStatusCode.OK, JsonSerializer.Serialize(new { d = new { results = filas } }));

    /// <summary>Encola un fallo de red o de tiempo (la excepción se lanza al enviar).</summary>
    public clsSapTmFalso Fallar(Exception excepcion)
    {
        _respuestas.Enqueue(_ => throw excepcion);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        _peticiones.Enqueue(new clsPeticionSap(
            request.Method,
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            request.Headers.Accept.ToString()));

        if (_respuestas.TryDequeue(out var respuesta))
        {
            return respuesta(request);
        }

        return await ResponderDesdeMuestraAsync(request.RequestUri!, cancellationToken);
    }

    /// <summary>Extrae y decodifica el <c>$filter</c> de la URL (sin tocar las comillas escapadas de OData).</summary>
    public static string? FiltroDe(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        foreach (var parte in url.Query.TrimStart('?').Split('&'))
        {
            if (parte.StartsWith("$filter=", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(parte["$filter=".Length..]);
            }
        }

        return null;
    }

    private static async Task<HttpResponseMessage> ResponderDesdeMuestraAsync(Uri url, CancellationToken cancellationToken)
    {
        var filtro = FiltroDe(url);
        var coincidencia = filtro is null ? null : PatronFiltro().Match(filtro);
        if (!url.AbsolutePath.EndsWith("/RentabClienteSet", StringComparison.Ordinal) || coincidencia is not { Success: true })
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        static string Literal(Group grupo) => grupo.Value.Replace("''", "'", StringComparison.Ordinal);
        var (salesOrg, trqType, bp) = (Literal(coincidencia.Groups[1]), Literal(coincidencia.Groups[2]), Literal(coincidencia.Groups[3]));

        var muestra = JsonNode.Parse(await File.ReadAllTextAsync(RutaMuestra, cancellationToken))!;
        var filas = new JsonArray();
        foreach (var fila in muestra["d"]!["results"]!.AsArray())
        {
            if ((string?)fila!["SalesOrgId"] == salesOrg && (string?)fila["TrqType"] == trqType && (string?)fila["OrderPartyId"] == bp)
            {
                filas.Add(fila.DeepClone());
            }
        }

        var cuerpo = new JsonObject { ["d"] = new JsonObject { ["results"] = filas } };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }

    // Literales OData: una comilla dentro del valor llega duplicada ('').
    [GeneratedRegex("^SalesOrgId eq '((?:[^']|'')*)' and TrqType eq '((?:[^']|'')*)' and OrderPartyId eq '((?:[^']|'')*)'$")]
    private static partial Regex PatronFiltro();
}

/// <summary>Petición recibida por <see cref="clsSapTmFalso"/>.</summary>
public sealed record clsPeticionSap(HttpMethod Metodo, Uri Url, string? Autorizacion, string Accept);
