using System.Globalization;
using System.Net;

namespace Cotifly.Api.Sistema;

/// <summary>
/// Sonda del <c>HEALTHCHECK</c> del Dockerfile (ADR-007). La imagen runtime (chiseled) no trae
/// shell ni <c>curl</c>, así que el propio binario hace de sonda: <c>dotnet Cotifly.Api.dll --healthcheck</c>
/// hace <c>GET /health</c> contra el Kestrel del mismo contenedor y termina con 0 si responde 200, o con 1
/// en cualquier otro caso (equivale al <c>urllib</c> del HEALTHCHECK de <c>orquestador/Dockerfile</c>).
/// No construye el host web: no lee configuración, no valida opciones y no abre puertos.
/// </summary>
public static class clsSondaSalud
{
    public const string Argumento = "--healthcheck";
    public const int CodigoSano = 0;
    public const int CodigoNoSano = 1;
    public const int PuertoPorDefecto = 8080;
    public const string HostLocal = "127.0.0.1";

    /// <summary>URLs de Kestrel (<c>http://+:8080;https://+:8443</c>). Si está definida, manda sobre los puertos.</summary>
    public const string VariableUrls = "ASPNETCORE_URLS";

    /// <summary>Puertos HTTP de Kestrel en las imágenes .NET (<c>8080</c> o <c>8080;8081</c>).</summary>
    public const string VariablePuertos = "ASPNETCORE_HTTP_PORTS";

    /// <summary>Menor que el <c>--timeout=5s</c> del HEALTHCHECK, para que la sonda responda 1 y no la mate Docker.</summary>
    public static readonly TimeSpan TiempoLimite = TimeSpan.FromSeconds(3);

    private static readonly string[] _hostsComodin = ["+", "*", "0.0.0.0", "[::]"];

    /// <summary>La sonda se pide con el argumento exacto <c>--healthcheck</c> (sensible a mayúsculas).</summary>
    public static bool EsSolicitada(IEnumerable<string> args) =>
        args?.Contains(Argumento, StringComparer.Ordinal) ?? false;

    /// <summary>
    /// URL de <c>/health</c>, con el mismo orden que usa Kestrel:
    /// 1) la primera URL <c>http://</c> de <paramref name="urls"/> (<c>ASPNETCORE_URLS</c>; las <c>https://</c> se
    ///    ignoran y los comodines <c>+</c>, <c>*</c>, <c>0.0.0.0</c>, <c>[::]</c> pasan a 127.0.0.1);
    /// 2) el primer puerto válido de <paramref name="puertosHttp"/> (<c>ASPNETCORE_HTTP_PORTS</c>) en 127.0.0.1;
    /// 3) 127.0.0.1:8080.
    /// </summary>
    public static Uri ConstruirUrl(string? urls, string? puertosHttp) =>
        DesdeUrls(urls) ?? DesdePuertos(puertosHttp) ?? UrlSalud(HostLocal, PuertoPorDefecto);

    /// <summary>Sin proxy: la sonda siempre va al loopback, aunque el contenedor tenga <c>HTTP_PROXY</c>.</summary>
    public static SocketsHttpHandler CrearManejador() => new() { UseProxy = false };

    public static HttpClient CrearCliente() => new(CrearManejador(), disposeHandler: true) { Timeout = TiempoLimite };

    /// <summary>Punto de entrada desde <c>Program.cs</c>: usa el entorno del proceso y la consola.</summary>
    public static async Task<int> EjecutarAsync()
    {
        using var cliente = CrearCliente();
        var url = ConstruirUrl(
            Environment.GetEnvironmentVariable(VariableUrls), Environment.GetEnvironmentVariable(VariablePuertos));
        return await EjecutarAsync(cliente, url, Console.Out, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// GET a <paramref name="url"/>: 0 si responde 200; 1 si responde otro estado, no conecta o se agota el tiempo.
    /// Lo escrito en <paramref name="salida"/> queda en <c>docker inspect</c> (<c>State.Health.Log</c>).
    /// </summary>
    public static async Task<int> EjecutarAsync(HttpClient cliente, Uri url, TextWriter salida, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(salida);

        try
        {
            using var respuesta = await cliente.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            var codigo = (int)respuesta.StatusCode;
            await salida.WriteLineAsync(
                string.Create(CultureInfo.InvariantCulture, $"sonda {url.AbsolutePath}: {codigo}")).ConfigureAwait(false);

            return respuesta.StatusCode == HttpStatusCode.OK ? CodigoSano : CodigoNoSano;
        }
        catch (HttpRequestException ex)
        {
            await salida.WriteLineAsync($"sonda {url.AbsolutePath}: sin respuesta ({ex.Message})").ConfigureAwait(false);
            return CodigoNoSano;
        }
        catch (TaskCanceledException)
        {
            await salida.WriteLineAsync($"sonda {url.AbsolutePath}: se agotó el tiempo de espera").ConfigureAwait(false);
            return CodigoNoSano;
        }
    }

    private static Uri? DesdeUrls(string? urls)
    {
        const string Esquema = "http://";

        foreach (var entrada in Separar(urls))
        {
            if (!entrada.StartsWith(Esquema, StringComparison.OrdinalIgnoreCase))
            {
                continue; // https:// u otro esquema: la sonda solo habla HTTP.
            }

            var autoridad = entrada[Esquema.Length..].Split('/', 2)[0];
            var separador = autoridad.LastIndexOf(':');
            var tienePuerto = separador >= 0 && !autoridad.EndsWith(']');
            var host = tienePuerto ? autoridad[..separador] : autoridad;
            int? puerto = tienePuerto ? AnalizarPuerto(autoridad[(separador + 1)..]) : 80;

            if (host.Length == 0 || puerto is null)
            {
                continue;
            }

            if (_hostsComodin.Contains(host, StringComparer.Ordinal))
            {
                host = HostLocal;
            }

            return UrlSalud(host, puerto.Value);
        }

        return null;
    }

    private static Uri? DesdePuertos(string? puertosHttp) =>
        Separar(puertosHttp).Select(AnalizarPuerto).FirstOrDefault(p => p is not null) is { } puerto
            ? UrlSalud(HostLocal, puerto)
            : null;

    private static string[] Separar(string? valor) =>
        (valor ?? string.Empty).Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int? AnalizarPuerto(string texto) =>
        int.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var valor) && valor is > 0 and <= 65535
            ? valor
            : null;

    private static Uri UrlSalud(string host, int puerto) =>
        new($"http://{host}:{puerto.ToString(CultureInfo.InvariantCulture)}/health");
}
