using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Web;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Estado HTTP de un escenario de <c>/web/*</c> (Reqnroll lo crea por escenario): la API real en memoria, las sesiones, las
/// regionales, las cotizaciones y las líneas por su alias del <c>.feature</c> (los valores reales son únicos para que los
/// escenarios no choquen en la base compartida) y la última respuesta.
/// </summary>
public sealed class clsContextoWeb : IDisposable
{
    private clsApiWebPruebas? _api;

    public clsApiWebPruebas Api => _api ?? throw new InvalidOperationException("Falta el paso 'Dado la API web de Cotifly'.");

    public Dictionary<string, clsSesionPrueba> Sesiones { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Regionales { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, clsCotizacionPrueba> Cotizaciones { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, (int Id, string Nombre)> Lineas { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HttpStatusCode UltimoEstado { get; private set; }

    public string UltimoCuerpo { get; private set; } = string.Empty;

    public byte[] UltimoContenido { get; private set; } = [];

    public string? UltimoTipo { get; private set; }

    /// <summary>Encabezado <c>ETag</c> de la última respuesta (concurrencia optimista, ADR-018 §8), o <c>null</c>.</summary>
    public string? UltimoEtag { get; private set; }

    public void Iniciar(bool correoHabilitado) => _api = new clsApiWebPruebas(clsEntornoAceptacion.BaseDatos, correoHabilitado);

    /// <summary>API ya armada por el escenario (ej. conectada al Nexo falso del costeo, ADR-015).</summary>
    public void Iniciar(clsApiWebPruebas api) => _api = api;

    /// <summary>API con configuración y reemplazos de prueba (el agente guionizado y Google falso, #12 parte C).</summary>
    public void Iniciar(IDictionary<string, string?>? extra, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> configurar) =>
        _api = new clsApiWebPruebas(clsEntornoAceptacion.BaseDatos, extra: extra, configurar: configurar);

    /// <summary>Valor real (único por escenario) de un alias de regional.</summary>
    public string Regional(string alias)
    {
        if (!Regionales.TryGetValue(alias, out var real))
        {
            real = $"{alias}-{Guid.NewGuid():N}"[..Math.Min(alias.Length + 9, 50)];
            Regionales[alias] = real;
        }

        return real;
    }

    public clsSesionPrueba Sesion(string alias) =>
        Sesiones.TryGetValue(alias, out var sesion) ? sesion : throw new InvalidOperationException($"No hay sesión para '{alias}'.");

    public clsCotizacionPrueba Cotizacion(string alias) =>
        Cotizaciones.TryGetValue(alias, out var cotizacion) ? cotizacion : throw new InvalidOperationException($"No existe la cotización '{alias}'.");

    /// <summary>Ejecuta la petición (con <c>If-Match</c> si viene) y guarda estado, cuerpo, tipo de contenido y <c>ETag</c>.</summary>
    public async Task EnviarAsync(HttpMethod metodo, string ruta, string? token, string? cuerpoJson = null, string? ifMatch = null)
    {
        using var respuesta = await Api.EnviarAsync(metodo, ruta, token, cuerpoJson, ifMatch);
        UltimoEstado = respuesta.StatusCode;
        UltimoEtag = respuesta.Headers.ETag?.Tag;
        UltimoContenido = await respuesta.Content.ReadAsByteArrayAsync();
        UltimoTipo = respuesta.Content.Headers.ContentType?.MediaType;
        UltimoCuerpo = UltimoTipo == "application/pdf" ? string.Empty : System.Text.Encoding.UTF8.GetString(UltimoContenido);
    }

    /// <summary>Igual que <see cref="EnviarAsync(HttpMethod, string, string?, string?)"/>, con un cuerpo cualquiera (multipart).</summary>
    public async Task EnviarAsync(HttpMethod metodo, string ruta, string? token, HttpContent contenido)
    {
        using var respuesta = await Api.EnviarContenidoAsync(metodo, ruta, token, contenido);
        UltimoEstado = respuesta.StatusCode;
        UltimoContenido = await respuesta.Content.ReadAsByteArrayAsync();
        UltimoTipo = respuesta.Content.Headers.ContentType?.MediaType;
        UltimoCuerpo = UltimoTipo is "application/pdf" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            ? string.Empty
            : System.Text.Encoding.UTF8.GetString(UltimoContenido);
    }

    public JsonElement UltimoJson()
    {
        using var documento = JsonDocument.Parse(UltimoCuerpo);
        return documento.RootElement.Clone();
    }

    public void Dispose() => _api?.Dispose();
}
