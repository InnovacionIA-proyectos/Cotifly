using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Precio.Services;

/// <summary>
/// TRM oficial desde los datos abiertos de la Superintendencia Financiera (ADR-014): conjunto "Tasa de Cambio
/// Representativa del Mercado - Histórico" (<c>Trm:Url</c>, por defecto <c>https://www.datos.gov.co/resource/32sa-8pi3.json</c>),
/// API SODA de Socrata sin credenciales. Consulta SoQL:
/// <c>?$select=valor,vigenciadesde,vigenciahasta&amp;$where=vigenciadesde &lt;= 'F' AND vigenciahasta &gt;= 'F'&amp;$order=vigenciadesde DESC&amp;$limit=1</c>
/// con <c>F = AAAA-MM-DDT00:00:00</c>: la fila cuyo rango de vigencia cubre la fecha (así un sábado, domingo o festivo recibe la TRM
/// del rango que lo incluye) y, si hubiera rangos solapados, la más reciente.
/// </summary>
/// <remarks>
/// <para><b>Nunca lanza</b> por la fuente: HTTP, red, tiempo agotado, JSON inválido, sin fila o valor no positivo devuelven
/// <c>TRM_NO_DISPONIBLE</c> con una sola línea de warning (la traza completa, en Debug). La caché por día la pone
/// <see cref="clsProveedorTrmEnCache"/>.</para>
/// <para><b>Resiliencia</b> (registrada en <c>AddInfraestructura</c>): <c>Trm:TiempoEsperaSegundos</c> para toda la consulta,
/// un tercio por intento y <see cref="clsResilienciaTrm.Reintentos"/> reintentos (<see cref="clsResilienciaTrm"/>) ante 5xx, 408, 429, fallos de red o intento agotado (GET, idempotente).</para>
/// <para><b>Inyección.</b> La fecha llega como <see cref="DateOnly"/> y se formatea con cultura invariante; aun así va como literal
/// SoQL (comilla simple duplicada) y cada parámetro se codifica para URL.</para>
/// </remarks>
public sealed class clsProveedorTrmSuperfinanciera(
    IHttpClientFactory fabrica, IOptions<clsOpcionesTrm> opciones, ILogger<clsProveedorTrmSuperfinanciera> logger) : IClsProveedorTrm
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "Trm";

    /// <summary>Identificador de la fuente en <see cref="clsTrm.Fuente"/>.</summary>
    public const string Fuente = "superfinanciera";

    public const string Mensaje = "No se pudo obtener la TRM oficial del día (Superintendencia Financiera): la cotización en dólares no está disponible en este momento. Intenta de nuevo en unos minutos o cotiza en pesos.";

    private readonly string _url = (opciones.Value.Url ?? clsOpcionesTrm.UrlPorDefecto).Trim();

    public async Task<clsResultado<clsTrm>> ObtenerAsync(DateOnly fecha, CancellationToken cancellationToken = default)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Get, ConstruirUrl(_url, fecha));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var http = fabrica.CreateClient(NombreCliente);
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible(fecha, $"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            await using var cuerpo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(cuerpo, cancellationToken: cancellationToken);
            if (Leer(documento.RootElement, fecha) is { } trm)
            {
                return trm;
            }

            return NoDisponible(fecha, "sin TRM vigente para la fecha o fila inválida");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de la fuente
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ExecutionRejectedException or JsonException)
        {
            var motivo = ex switch
            {
                OperationCanceledException or Polly.Timeout.TimeoutRejectedException => "tiempo agotado",
                Polly.CircuitBreaker.BrokenCircuitException => "circuito abierto tras fallos repetidos",
                JsonException => "JSON inválido",
                _ => "error de red",
            };
            logger.LogDebug(ex, "Detalle del fallo de la fuente de TRM ({Motivo}).", motivo);
            return NoDisponible(fecha, motivo);
        }
    }

    /// <summary>
    /// URL de la consulta SoQL para <paramref name="fecha"/>: cada parámetro codificado con <see cref="Uri.EscapeDataString"/> y la fecha
    /// como literal SoQL.
    /// </summary>
    public static Uri ConstruirUrl(string url, DateOnly fecha)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var literal = Literal(fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00");
        var filtro = $"vigenciadesde <= {literal} AND vigenciahasta >= {literal}";
        return new Uri(
            $"{url.Trim()}?$select={Uri.EscapeDataString("valor,vigenciadesde,vigenciahasta")}"
            + $"&$where={Uri.EscapeDataString(filtro)}"
            + $"&$order={Uri.EscapeDataString("vigenciadesde DESC")}"
            + "&$limit=1",
            UriKind.Absolute);
    }

    /// <summary>
    /// Primera fila del arreglo SODA (<c>[{"valor":"3306.86","vigenciadesde":"AAAA-MM-DDT00:00:00.000",...}]</c>) si es válida: valor
    /// positivo (texto o número, redondeado a 4 decimales, la escala de <c>trmAplicada</c>) y rango de vigencia que cubre la fecha. Un valor
    /// cero o negativo, o cualquier otra cosa → <c>null</c> (<c>TRM_NO_DISPONIBLE</c>).
    /// </summary>
    public static clsTrm? Leer(JsonElement raiz, DateOnly fecha)
    {
        if (raiz.ValueKind != JsonValueKind.Array || raiz.GetArrayLength() == 0 || raiz[0].ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fila = raiz[0];
        if (Numero(fila, "valor") is not { } valor || Math.Round(valor, 4, MidpointRounding.ToEven) <= 0m
            || Fecha(fila, "vigenciadesde") is not { } desde
            || Fecha(fila, "vigenciahasta") is not { } hasta)
        {
            return null;
        }

        // La columna Cotizaciones.trmAplicada es DECIMAL(18,4): se guarda exactamente lo que se usa para convertir.
        var trm = new clsTrm(Math.Round(valor, 4, MidpointRounding.ToEven), desde, hasta, Fuente);
        return trm.VigenteEn(fecha) ? trm : null;
    }

    private static decimal? Numero(JsonElement fila, string campo)
    {
        if (!fila.TryGetProperty(campo, out var valor))
        {
            return null;
        }

        return valor.ValueKind switch
        {
            JsonValueKind.Number when valor.TryGetDecimal(out var numero) => numero,
            JsonValueKind.String when decimal.TryParse(valor.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var numero) => numero,
            _ => null,
        };
    }

    /// <summary>
    /// Fecha de vigencia: los 10 primeros caracteres (<c>AAAA-MM-DD</c>) como <see cref="DateOnly"/>, sin conversión de zona horaria (la
    /// fuente publica fechas de calendario, <c>AAAA-MM-DDT00:00:00.000</c>; un parseo como instante podría correr el día).
    /// </summary>
    private static DateOnly? Fecha(JsonElement fila, string campo) =>
        fila.TryGetProperty(campo, out var valor)
        && valor.ValueKind == JsonValueKind.String
        && valor.GetString() is { Length: >= 10 } texto
        && DateOnly.TryParseExact(texto[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha)
            ? fecha
            : null;

    private static string Literal(string valor) => $"'{valor.Replace("'", "''", StringComparison.Ordinal)}'";

    private clsErrorDominio NoDisponible(DateOnly fecha, string motivo)
    {
        // Una sola línea de warning, sin traza; los eventos de Polly de cada intento bajan a Debug (ver AddInfraestructura).
        logger.LogWarning("Fuente de TRM no disponible ({Motivo}) consultando {Fecha}.", motivo, fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return new clsErrorDominio(clsCodigosError.TrmNoDisponible, Mensaje, motivo);
    }
}
