using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Costeo.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Licitaciones.Services;

/// <summary>
/// Análisis del Excel de licitación en Nexo (HU-011.2, ADR-017): <c>POST {Nexo:UrlBase}/api/captura-solicitudes/analizar-excel</c>,
/// multipart con el campo <c>archivo</c>, identidad <c>X-Portal-Usuario-Id</c> del comercial. Usa el cliente HTTP
/// <see cref="clsCosteoNexo.NombreCliente"/> (30 s, POST sin reintentos). Nexo no persiste nada. Nunca lanza por Nexo: 400 con
/// <c>ARCHIVO_REQUERIDO</c>, <c>FORMATO_NO_SOPORTADO</c>, <c>EXCEL_INVALIDO</c> o <c>EXCEL_SIN_DATOS</c> pasa con ese código;
/// tiempo agotado → <c>COSTEO_NEXO_TIEMPO_AGOTADO</c>; 401/403, 5xx, red o JSON inválido → <c>COSTEO_NEXO_NO_DISPONIBLE</c>; otro
/// 4xx → <c>COSTEO_NEXO_RECHAZADA</c>.
/// </summary>
public sealed class clsAnalizadorExcelNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsAnalizadorExcelNexo> logger)
    : IClsAnalizadorExcelNexo
{
    public const string Ruta = "/api/captura-solicitudes/analizar-excel";

    private static readonly HashSet<string> _codigosArchivo = new(StringComparer.Ordinal)
    {
        clsCodigosError.ArchivoRequerido, clsCodigosError.FormatoNoSoportado, clsCodigosError.ExcelInvalido, clsCodigosError.ExcelSinDatos,
    };

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public async Task<clsResultado<clsAnalisisExcel>> AnalizarAsync(
        Guid usuarioId, string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        if (_urlBase is null)
        {
            return NoDisponible("Nexo sin configurar (Nexo:UrlBase)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno");
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Post, clsCosteoNexo.ConstruirUrl(_urlBase, Ruta));
        var multipart = new MultipartFormDataContent();
        var archivo = new ByteArrayContent(contenido);
        archivo.Headers.ContentType = new MediaTypeHeaderValue(
            nombreArchivo.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase) ? clsArchivoLicitacion.TipoXlsm : clsArchivoLicitacion.TipoXlsx);
        multipart.Add(archivo, "archivo", string.IsNullOrWhiteSpace(nombreArchivo) ? "licitacion.xlsx" : nombreArchivo);
        peticion.Content = multipart;
        peticion.Headers.Add("X-Portal-Usuario-Id", idUsuarioExterno.ToString(CultureInfo.InvariantCulture));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var texto = await respuesta.Content.ReadAsStringAsync(cancellationToken);
            if ((int)respuesta.StatusCode is >= 400 and < 500)
            {
                var (codigo, mensaje) = LeerError(texto);
                var detalle = $"HTTP {(int)respuesta.StatusCode}" + (codigo is null ? string.Empty : $" {codigo}") + (mensaje is null ? string.Empty : $": {mensaje}");
                logger.LogWarning("Nexo rechazó el análisis del Excel: {Detalle}", detalle.Length <= 400 ? detalle : detalle[..400]);
                if (respuesta.StatusCode == HttpStatusCode.BadRequest && codigo is not null && _codigosArchivo.Contains(codigo))
                {
                    return new clsErrorDominio(codigo, mensaje ?? "Nexo no pudo leer el archivo.");
                }

                return respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, clsCosteoNexo.MensajeNoDisponible, detalle)
                    : new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, "Nexo rechazó el análisis del archivo.", detalle);
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"HTTP {(int)respuesta.StatusCode}");
            }

            if (Leer(texto, nombreArchivo) is { } analisis)
            {
                return analisis;
            }

            return NoDisponible("respuesta del análisis sin hojas");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or Polly.Timeout.TimeoutRejectedException)
        {
            logger.LogWarning("Nexo no respondió a tiempo al análisis del Excel.");
            return new clsErrorDominio(clsCodigosError.CosteoNexoTiempoAgotado, "Nexo no respondió a tiempo al analizar el archivo: intenta de nuevo.");
        }
        catch (Exception ex) when (ex is HttpRequestException or ExecutionRejectedException)
        {
            return NoDisponible("error de red", ex);
        }
        catch (JsonException ex)
        {
            return NoDisponible("JSON inválido", ex);
        }
    }

    /// <summary><c>AnalisisExcelDto</c> de Nexo; <c>null</c> si no trae <c>hojas</c>.</summary>
    internal static clsAnalisisExcel? Leer(string json, string nombreArchivo)
    {
        using var documento = JsonDocument.Parse(json);
        var raiz = documento.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("hojas", out var hojas) || hojas.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lista = new List<clsHojaExcel>();
        foreach (var hoja in hojas.EnumerateArray())
        {
            var encabezados = Textos(hoja, "encabezados");
            var filas = hoja.TryGetProperty("filas", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => (IReadOnlyList<string>)(x.ValueKind == JsonValueKind.Array
                    ? [.. x.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : c.ToString())]
                    : [])).ToList()
                : [];
            lista.Add(new clsHojaExcel(
                clsCosteoNexo.Texto(hoja, "nombre") ?? string.Empty,
                clsCosteoNexo.Entero(hoja, "filaEncabezado") ?? 0,
                encabezados,
                filas,
                clsCosteoNexo.Entero(hoja, "totalFilas") ?? filas.Count));
        }

        return new clsAnalisisExcel(clsCosteoNexo.Texto(raiz, "archivo") ?? nombreArchivo, lista);
    }

    private static IReadOnlyList<string> Textos(JsonElement objeto, string propiedad) =>
        objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.Array
            ? [.. valor.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? string.Empty : x.ToString())]
            : [];

    private static (string? Codigo, string? Mensaje) LeerError(string texto)
    {
        try
        {
            using var documento = JsonDocument.Parse(texto);
            return documento.RootElement.ValueKind == JsonValueKind.Object
                ? (clsCosteoNexo.Texto(documento.RootElement, "code"), clsCosteoNexo.Texto(documento.RootElement, "message"))
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, string.IsNullOrWhiteSpace(texto) ? null : texto.Trim());
        }
    }

    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Análisis del Excel en Nexo no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo del análisis en Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, clsCosteoNexo.MensajeNoDisponible, motivo);
    }
}

/// <summary>Análisis cuando Nexo no está configurado: siempre <c>COSTEO_NEXO_NO_DISPONIBLE</c>.</summary>
public sealed class clsAnalizadorExcelNoDisponible : IClsAnalizadorExcelNexo
{
    public Task<clsResultado<clsAnalisisExcel>> AnalizarAsync(Guid usuarioId, string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsAnalisisExcel>>(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible,
            "El análisis del Excel en Nexo no está configurado (Nexo:UrlBase)."));
}
