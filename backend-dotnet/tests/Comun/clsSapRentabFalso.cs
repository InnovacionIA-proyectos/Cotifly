using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// SAP TM falso para la sincronización del margen (HU-042.2, #243): un <see cref="HttpMessageHandler"/> que nunca sale a la red y, como el
/// real, IGNORA <c>$filter</c> y cualquier otro parámetro: siempre devuelve el set completo de <c>RentabClienteSet</c> en OData v2, con
/// <c>__metadata</c> en cada fila y <c>Value</c> como texto (<c>Edm.Decimal</c>), incluso con artefactos de flotante como
/// <c>"13.592356781"</c>. Registra cada petición (URL y cabeceras) y permite encolar fallos y respuestas crudas.
/// </summary>
public sealed class clsSapRentabFalso : HttpMessageHandler
{
    /// <summary>URL del servicio en las pruebas (la real es <c>http://sap-tm-prd.boterosoto.local:8000/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV</c>).</summary>
    public const string Url = "http://sap-tm-falso.pruebas.local:8000/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV";

    public const string Usuario = "svc_cotifly_pruebas";

    public const string Clave = "Clave-SAP-de-pruebas-9x7";

    private readonly ConcurrentQueue<Func<HttpRequestMessage, HttpResponseMessage>> _respuestas = new();
    private readonly ConcurrentQueue<clsPeticionSap> _peticiones = new();
    private readonly object _candado = new();
    private List<(string SalesOrg, string TrqType, string Bp, string Valor)> _filas = [];

    /// <summary>Peticiones recibidas, en orden.</summary>
    public IReadOnlyList<clsPeticionSap> Peticiones => [.. _peticiones];

    /// <summary>Fija el set completo que devuelve (<c>Valor</c> como texto, tal cual llega en el JSON).</summary>
    public clsSapRentabFalso ConFilas(IEnumerable<(string SalesOrg, string TrqType, string Bp, string Valor)> filas)
    {
        lock (_candado)
        {
            _filas = [.. filas];
        }

        return this;
    }

    /// <summary>Encola una respuesta cruda (se consume una por petición; sin cola, responde el set completo).</summary>
    public clsSapRentabFalso Responder(HttpStatusCode estado, string cuerpo = "")
    {
        _respuestas.Enqueue(_ => new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") });
        return this;
    }

    /// <summary>Encola una respuesta 200 cuyo cuerpo sale del flujo dado (por ejemplo, uno que se corta a mitad).</summary>
    public clsSapRentabFalso ResponderFlujo(Func<Stream> flujo)
    {
        ArgumentNullException.ThrowIfNull(flujo);
        _respuestas.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(flujo()) });
        return this;
    }

    /// <summary>Encola un fallo de red (la excepción se lanza al enviar).</summary>
    public clsSapRentabFalso Fallar(Exception excepcion)
    {
        _respuestas.Enqueue(_ => throw excepcion);
        return this;
    }

    /// <summary>Cuerpo OData v2 del set, con <c>__metadata</c> y <c>Value</c> como texto.</summary>
    public static string Cuerpo(IEnumerable<(string SalesOrg, string TrqType, string Bp, string Valor)> filas)
    {
        var resultados = filas.Select(f => new Dictionary<string, object>
        {
            ["__metadata"] = new Dictionary<string, string>
            {
                ["id"] = $"{Url}/RentabClienteSet(SalesOrgId='{f.SalesOrg}',TrqType='{f.TrqType}',OrderPartyId='{f.Bp}')",
                ["uri"] = $"{Url}/RentabClienteSet(SalesOrgId='{f.SalesOrg}',TrqType='{f.TrqType}',OrderPartyId='{f.Bp}')",
                ["type"] = "ZGW_RENTAB_CLI_SRV.RentabCliente",
            },
            ["SalesOrgId"] = f.SalesOrg,
            ["TrqType"] = f.TrqType,
            ["OrderPartyId"] = f.Bp,
            ["Value"] = f.Valor,
        });
        return JsonSerializer.Serialize(new { d = new { results = resultados } });
    }

    /// <summary>Set de <paramref name="cantidad"/> combinaciones OPTN de la organización 50000005 con BP <c>{prefijo}{i}</c> y valor con artefactos.</summary>
    public static List<(string SalesOrg, string TrqType, string Bp, string Valor)> Set(string prefijo, int cantidad, decimal baseValor = 10m) =>
        [.. Enumerable.Range(1, cantidad).Select(i => ("50000005", "OPTN", $"{prefijo}{i}",
            (baseValor + i / 1000m).ToString(CultureInfo.InvariantCulture) + "56781"))];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        _peticiones.Enqueue(new clsPeticionSap(
            request.RequestUri!,
            request.Headers.Authorization?.ToString(),
            string.Join(",", request.Headers.AcceptEncoding.Select(e => e.Value))));
        if (_respuestas.TryDequeue(out var respuesta))
        {
            return Task.FromResult(respuesta(request));
        }

        string cuerpo;
        lock (_candado)
        {
            cuerpo = Cuerpo(_filas);
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") });
    }
}

/// <summary>Una petición recibida por <see cref="clsSapRentabFalso"/>.</summary>
public sealed record clsPeticionSap(Uri Url, string? Autorizacion, string AcceptEncoding);
