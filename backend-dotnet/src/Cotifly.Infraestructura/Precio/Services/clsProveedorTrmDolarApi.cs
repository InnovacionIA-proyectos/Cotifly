using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Precio.Services;

/// <summary>
/// TRM desde DolarApi (ADR-014 §1): <c>GET Trm:UrlDolarApi</c> (por defecto
/// <c>https://co.dolarapi.com/v1/trm</c>), sin credenciales, responde la última TRM certificada que publicó la Superintendencia:
/// <c>{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"AAAA-MM-DDTHH:mm:ss.fffZ"}</c>.
/// </summary>
/// <remarks>
/// <para><b>Semántica (ADR-014 §1).</b> <c>fechaActualizacion</c> es la hora en que DolarApi refrescó el dato, no
/// la vigencia de la TRM: el valor es la última TRM publicada, que en la tarde puede ser ya la del día siguiente. Por eso solo se acepta
/// para la fecha pedida si DolarApi la refrescó ESE día en Bogotá antes de <see cref="HoraCorte"/>, cuando la TRM del día siguiente todavía
/// no se ha publicado; si no, es <c>TRM_NO_DISPONIBLE</c> y la cadena usa la Superintendencia, que consulta la vigencia exacta.</para>
/// <para><b>Validación.</b> <c>unidad</c> = <c>COP</c>, <c>nombre</c> = <c>TRM</c>; <c>valor</c> (número o texto) redondeado a 4 decimales
/// half-to-even en [<see cref="ValorMinimo"/>, <see cref="ValorMaximo"/>] y a no más de <see cref="DesviacionMaxima"/> de la última TRM
/// válida conocida (<see cref="clsMemoriaTrm"/>), si hay; cuerpo de máximo <see cref="TamanoMaximoCuerpo"/> bytes.</para>
/// <para><b>Vigencia.</b> DolarApi no publica rangos: la TRM se toma como vigente solo el día pedido (desde = hasta = fecha).</para>
/// <para><b>Nunca lanza</b> por la fuente: HTTP, red, tiempo agotado, circuito abierto, JSON inválido o una validación que falla devuelven
/// <c>TRM_NO_DISPONIBLE</c> con una línea de warning. Es un agregador no oficial: por eso la cadena por defecto tiene respaldo
/// (<see cref="clsProveedorTrmConRespaldo"/>).</para>
/// <para><b>Resiliencia</b>: <see cref="clsResilienciaTrm"/> con <c>Trm:TiempoEsperaDolarApiSegundos</c> (cliente <see cref="NombreCliente"/>).</para>
/// </remarks>
public sealed class clsProveedorTrmDolarApi(
    IHttpClientFactory fabrica,
    IOptions<clsOpcionesTrm> opciones,
    IClsCalendarioNegocio calendario,
    clsMemoriaTrm memoria,
    ILogger<clsProveedorTrmDolarApi> logger) : IClsProveedorTrm
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "TrmDolarApi";

    /// <summary>Identificador de la fuente en <see cref="clsTrm.Fuente"/>.</summary>
    public const string Fuente = "dolarapi";

    /// <summary>TRM mínima creíble (COP por USD).</summary>
    public const decimal ValorMinimo = 500m;

    /// <summary>TRM máxima creíble (COP por USD).</summary>
    public const decimal ValorMaximo = 20000m;

    /// <summary>Desviación máxima (fracción) frente a la última TRM válida conocida.</summary>
    public const decimal DesviacionMaxima = 0.10m;

    /// <summary>Tamaño máximo del cuerpo de la respuesta (bytes).</summary>
    public const int TamanoMaximoCuerpo = 16 * 1024;

    /// <summary>
    /// Hora de Bogotá hasta la que un dato refrescado ese día es con seguridad la TRM vigente ese día (la del día siguiente se publica en la
    /// tarde, después del cierre del mercado).
    /// </summary>
    public static readonly TimeSpan HoraCorte = TimeSpan.FromHours(12);

    public const string Mensaje = "No se pudo obtener la TRM del día (DolarApi): la cotización en dólares no está disponible en este momento. Intenta de nuevo en unos minutos o cotiza en pesos.";

    private readonly Uri _url = new((opciones.Value.UrlDolarApi ?? clsOpcionesTrm.UrlDolarApiPorDefecto).Trim(), UriKind.Absolute);

    public async Task<clsResultado<clsTrm>> ObtenerAsync(DateOnly fecha, CancellationToken cancellationToken = default)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Get, _url);
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var http = fabrica.CreateClient(NombreCliente);
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible(fecha, $"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            if (respuesta.Content.Headers.ContentLength is > TamanoMaximoCuerpo)
            {
                return NoDisponible(fecha, "respuesta demasiado grande");
            }

            var bytes = await LeerAcotadoAsync(respuesta.Content, cancellationToken);
            if (bytes is null)
            {
                return NoDisponible(fecha, "respuesta demasiado grande");
            }

            using var documento = JsonDocument.Parse(bytes);
            var inicio = calendario.InicioDelDia(fecha);
            var leida = Leer(documento.RootElement, fecha, inicio, memoria.Ultima?.Valor, out var motivo);
            return leida is null ? NoDisponible(fecha, motivo) : leida;
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
            logger.LogDebug(ex, "Detalle del fallo de DolarApi ({Motivo}).", motivo);
            return NoDisponible(fecha, motivo);
        }
    }

    /// <summary>
    /// TRM del objeto de DolarApi si pasa todas las validaciones (ver la clase); si no, <c>null</c> y el <paramref name="motivo"/>.
    /// </summary>
    /// <param name="raiz">Cuerpo de la respuesta.</param>
    /// <param name="fecha">Fecha de negocio pedida (America/Bogota).</param>
    /// <param name="inicioDelDia">00:00 de <paramref name="fecha"/> en Bogotá (<see cref="IClsCalendarioNegocio.InicioDelDia"/>).</param>
    /// <param name="referencia">Última TRM válida conocida, o <c>null</c> si no hay.</param>
    /// <param name="motivo">Por qué se rechazó (vacío si se aceptó).</param>
    public static clsTrm? Leer(JsonElement raiz, DateOnly fecha, DateTimeOffset inicioDelDia, decimal? referencia, out string motivo)
    {
        if (raiz.ValueKind != JsonValueKind.Object)
        {
            motivo = "la respuesta no es un objeto JSON";
            return null;
        }

        if (!Texto(raiz, "unidad", "COP"))
        {
            motivo = "la unidad no es COP";
            return null;
        }

        if (!Texto(raiz, "nombre", "TRM"))
        {
            motivo = "el nombre no es TRM";
            return null;
        }

        if (Numero(raiz, "valor") is not { } bruto)
        {
            motivo = "sin valor numérico";
            return null;
        }

        var valor = Math.Round(bruto, 4, MidpointRounding.ToEven);
        if (valor < ValorMinimo || valor > ValorMaximo)
        {
            motivo = $"valor fuera de rango ({valor.ToString(CultureInfo.InvariantCulture)})";
            return null;
        }

        if (referencia is > 0m and var anterior && Math.Abs(valor - anterior) / anterior > DesviacionMaxima)
        {
            motivo = $"valor {valor.ToString(CultureInfo.InvariantCulture)} se desvía más del 10 % de la última TRM conocida ({anterior.ToString(CultureInfo.InvariantCulture)})";
            return null;
        }

        if (!raiz.TryGetProperty("fechaActualizacion", out var texto) || texto.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(texto.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var actualizada))
        {
            motivo = "sin fechaActualizacion válida";
            return null;
        }

        if (actualizada < inicioDelDia || actualizada >= inicioDelDia + HoraCorte)
        {
            motivo = $"actualizada fuera de la ventana segura del {fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} (antes de las 12:00 en Bogotá): puede ser la TRM de otra vigencia";
            return null;
        }

        motivo = string.Empty;
        return new clsTrm(valor, fecha, fecha, Fuente);
    }

    private static bool Texto(JsonElement objeto, string campo, string esperado) =>
        objeto.TryGetProperty(campo, out var valor) && valor.ValueKind == JsonValueKind.String
        && string.Equals(valor.GetString()?.Trim(), esperado, StringComparison.Ordinal);

    private static decimal? Numero(JsonElement objeto, string campo)
    {
        if (!objeto.TryGetProperty(campo, out var valor))
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

    /// <summary>Cuerpo completo si cabe en <see cref="TamanoMaximoCuerpo"/>; <c>null</c> si lo supera.</summary>
    private static async Task<byte[]?> LeerAcotadoAsync(HttpContent contenido, CancellationToken cancellationToken)
    {
        await using var cuerpo = await contenido.ReadAsStreamAsync(cancellationToken);
        var bufer = new byte[TamanoMaximoCuerpo + 1];
        var leidos = 0;
        int n;
        while (leidos < bufer.Length && (n = await cuerpo.ReadAsync(bufer.AsMemory(leidos), cancellationToken)) > 0)
        {
            leidos += n;
        }

        return leidos > TamanoMaximoCuerpo ? null : bufer[..leidos];
    }

    private clsErrorDominio NoDisponible(DateOnly fecha, string motivo)
    {
        logger.LogWarning("DolarApi no entregó una TRM válida ({Motivo}) consultando {Fecha}.", motivo, fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return new clsErrorDominio(clsCodigosError.TrmNoDisponible, Mensaje, motivo);
    }
}
