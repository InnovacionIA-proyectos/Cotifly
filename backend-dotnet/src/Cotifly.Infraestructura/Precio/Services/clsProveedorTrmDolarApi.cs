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
/// TRM desde DolarApi (HU-012.1, ADR-014 §1, requisito de negocio del 2026-10-05): <c>GET Trm:UrlDolarApi</c> (por defecto
/// <c>https://co.dolarapi.com/v1/trm</c>), sin credenciales, responde la última TRM publicada:
/// <c>{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-10-05T19:01:18.476Z"}</c>.
/// </summary>
/// <remarks>
/// <para><b>Validación.</b> <c>unidad</c> debe ser <c>COP</c>; <c>valor</c> (número o texto) redondeado a 4 decimales half-to-even (la escala de
/// <c>trmAplicada</c>) debe estar en [<see cref="ValorMinimo"/>, <see cref="ValorMaximo"/>]; <c>fechaActualizacion</c> (instante ISO 8601),
/// llevada a la fecha de negocio de America/Bogota, no puede ser anterior en más de <see cref="DiasAntiguedadMaxima"/> días a la fecha pedida
/// ni posterior en más de <see cref="DiasFuturoMaximo"/> (DolarApi publica en la tarde la TRM del día siguiente).</para>
/// <para><b>Vigencia.</b> DolarApi no publica rangos de vigencia: la TRM se toma como vigente solo el día pedido (desde = hasta = fecha). Así la
/// caché por día y la regla de "TRM vencida" del estado de la conversación vuelven a consultar al día siguiente.</para>
/// <para><b>Nunca lanza</b> por la fuente: HTTP, red, tiempo agotado, circuito abierto, JSON inválido o una validación que falla devuelven
/// <c>TRM_NO_DISPONIBLE</c> con una línea de warning. Es un agregador no oficial: por eso la cadena por defecto tiene respaldo
/// (<see cref="clsProveedorTrmConRespaldo"/>).</para>
/// <para><b>Resiliencia</b>: la misma tubería que <see cref="clsProveedorTrmSuperfinanciera"/> (cliente con nombre <see cref="NombreCliente"/>).</para>
/// </remarks>
public sealed class clsProveedorTrmDolarApi(
    IHttpClientFactory fabrica,
    IOptions<clsOpcionesTrm> opciones,
    IClsCalendarioNegocio calendario,
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

    /// <summary>Días que puede tener de antigüedad la publicación respecto a la fecha pedida (cubre fines de semana con festivo).</summary>
    public const int DiasAntiguedadMaxima = 4;

    /// <summary>Días que puede adelantarse la publicación respecto a la fecha pedida.</summary>
    public const int DiasFuturoMaximo = 1;

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

            await using var cuerpo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(cuerpo, cancellationToken: cancellationToken);
            var leida = Leer(documento.RootElement, fecha, calendario.FechaDe, out var motivo);
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
    /// <param name="fechaNegocio">Fecha de negocio de un instante (<see cref="IClsCalendarioNegocio.FechaDe"/>).</param>
    /// <param name="motivo">Por qué se rechazó (vacío si se aceptó).</param>
    public static clsTrm? Leer(JsonElement raiz, DateOnly fecha, Func<DateTimeOffset, DateOnly> fechaNegocio, out string motivo)
    {
        ArgumentNullException.ThrowIfNull(fechaNegocio);
        if (raiz.ValueKind != JsonValueKind.Object)
        {
            motivo = "la respuesta no es un objeto JSON";
            return null;
        }

        if (!raiz.TryGetProperty("unidad", out var unidad) || unidad.ValueKind != JsonValueKind.String
            || !string.Equals(unidad.GetString()?.Trim(), "COP", StringComparison.Ordinal))
        {
            motivo = "la unidad no es COP";
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

        if (!raiz.TryGetProperty("fechaActualizacion", out var texto) || texto.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(texto.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var actualizada))
        {
            motivo = "sin fechaActualizacion válida";
            return null;
        }

        var publicada = fechaNegocio(actualizada);
        if (publicada < fecha.AddDays(-DiasAntiguedadMaxima))
        {
            motivo = $"TRM desactualizada (publicada el {Dia(publicada)})";
            return null;
        }

        if (publicada > fecha.AddDays(DiasFuturoMaximo))
        {
            motivo = $"fecha de publicación futura ({Dia(publicada)})";
            return null;
        }

        motivo = string.Empty;
        return new clsTrm(valor, fecha, fecha, Fuente);
    }

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

    private static string Dia(DateOnly fecha) => fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private clsErrorDominio NoDisponible(DateOnly fecha, string motivo)
    {
        logger.LogWarning("DolarApi no entregó una TRM válida ({Motivo}) consultando {Fecha}.", motivo, Dia(fecha));
        return new clsErrorDominio(clsCodigosError.TrmNoDisponible, Mensaje, motivo);
    }
}
