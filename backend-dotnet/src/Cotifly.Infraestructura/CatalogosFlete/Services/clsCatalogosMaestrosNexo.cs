using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// Catálogos maestros de Nexo en vivo (HU-013.10, #106, G1): <c>GET {Nexo:UrlBase}/api/catalogos/tipos-vehiculo</c>
/// (<c>clsTipoVehiculoDto {codigo, descripcion, esCatalogoOficial}</c>), <c>clases-viaje</c> (<c>{id, descripcion}</c>),
/// <c>clases-contenedor</c> (<c>{id, descripcion, ...}</c>) y <c>lineas-negocio</c> (<c>{id, descripcion}</c>), verificados en el
/// swagger de Nexo QA el 2026-10-05. Reemplaza a <c>GET /api/biblioteca-sap-historico/filtros</c> (histórico de tarifas, no el
/// catálogo maestro). Cada petición identifica al comercial con <c>X-Portal-Usuario-Id: {idUsuarioExterno}</c> (Nexo responde 401
/// <c>SESION_INVALIDA</c> sin él). Comparte el cliente HTTP con nombre
/// <see cref="Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo.NombreCliente"/> y su tubería de resiliencia.
/// </summary>
/// <remarks>
/// <para><b>Todo o nada.</b> Los cuatro catálogos se piden juntos y, si uno falla, el resultado es
/// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>: una foto a medias haría rechazar códigos válidos. Filas sin <c>codigo</c> (tipos de
/// vehículo) o sin <c>descripcion</c> se descartan.</para>
/// <para><b>Fallos.</b> Nunca lanza por Nexo: usuario sin <c>idUsuarioExterno</c>, HTTP, red, tiempo agotado o JSON inválido
/// devuelven <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>. Una línea de warning por fallo, sin la traza ni el header.</para>
/// <para>No guarda nada: la caché es <see cref="clsCatalogosMaestrosEnCache"/>.</para>
/// </remarks>
public sealed class clsCatalogosMaestrosNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsCatalogosMaestrosNexo> logger)
    : IClsCatalogosMaestrosProvider
{
    public const string Mensaje = "Los catálogos de flete de Nexo no están disponibles en este momento: intenta de nuevo más tarde.";

    public const string RutaTiposVehiculo = "/api/catalogos/tipos-vehiculo";
    public const string RutaClasesViaje = "/api/catalogos/clases-viaje";
    public const string RutaClasesContenedor = "/api/catalogos/clases-contenedor";
    public const string RutaLineasNegocio = "/api/catalogos/lineas-negocio";

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public clsCatalogosMaestros? UltimaCopia => null;

    public async Task<clsResultado<clsCatalogosMaestros>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (_urlBase is null)
        {
            return NoDisponible("Nexo sin configurar (Nexo:UrlBase)");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno (almacén corporativo)");
        }

        var identidad = idUsuarioExterno.ToString(CultureInfo.InvariantCulture);
        var tipos = LeerAsync(_urlBase, RutaTiposVehiculo, identidad, f => Texto(f, "codigo"), cancellationToken);
        var clases = LeerAsync(_urlBase, RutaClasesViaje, identidad, f => Texto(f, "descripcion"), cancellationToken);
        var contenedores = LeerAsync(_urlBase, RutaClasesContenedor, identidad, f => Texto(f, "descripcion"), cancellationToken);
        var lineas = LeerAsync(_urlBase, RutaLineasNegocio, identidad, f => Texto(f, "descripcion"), cancellationToken);
        await Task.WhenAll(tipos, clases, contenedores, lineas);

        foreach (var parte in new[] { tipos.Result, clases.Result, contenedores.Result, lineas.Result })
        {
            if (parte.EsFallo)
            {
                return parte.Error;
            }
        }

        return new clsCatalogosMaestros(tipos.Result.Valor, clases.Result.Valor, contenedores.Result.Valor, lineas.Result.Valor);
    }

    /// <summary><c>{urlBase}{ruta}</c>, con <paramref name="ruta"/> empezando por <c>/api/</c>.</summary>
    public static Uri ConstruirUrl(string urlBase, string ruta)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        return new Uri($"{urlBase.TrimEnd('/')}{ruta}", UriKind.Absolute);
    }

    private async Task<clsResultado<IReadOnlyList<clsOpcionCatalogo>>> LeerAsync(
        string urlBase, string ruta, string identidad, Func<JsonElement, string?> valor, CancellationToken cancellationToken)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Get, ConstruirUrl(urlBase, ruta));
        peticion.Headers.Add("X-Portal-Usuario-Id", identidad);
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"{ruta}: HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            await using var cuerpo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(cuerpo, cancellationToken: cancellationToken);
            if (documento.RootElement.ValueKind != JsonValueKind.Array)
            {
                return NoDisponible($"{ruta}: la respuesta no es un arreglo");
            }

            var opciones = new List<clsOpcionCatalogo>();
            foreach (var fila in documento.RootElement.EnumerateArray())
            {
                var codigo = valor(fila)?.Trim();
                var etiqueta = Texto(fila, "descripcion")?.Trim();
                if (string.IsNullOrEmpty(codigo) || string.IsNullOrEmpty(etiqueta) || opciones.Exists(o => o.Valor == codigo))
                {
                    continue;
                }

                opciones.Add(new clsOpcionCatalogo(codigo, etiqueta));
            }

            return opciones;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama: no es una caída de Nexo
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or ExecutionRejectedException)
        {
            var motivo = ex switch
            {
                OperationCanceledException or Polly.Timeout.TimeoutRejectedException => "tiempo agotado",
                Polly.CircuitBreaker.BrokenCircuitException => "circuito abierto tras fallos repetidos",
                _ => "error de red",
            };
            return NoDisponible($"{ruta}: {motivo}", ex);
        }
        catch (JsonException ex)
        {
            return NoDisponible($"{ruta}: JSON inválido", ex);
        }
    }

    private static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.ValueKind == JsonValueKind.Object && objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;

    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Catálogos maestros de Nexo no disponibles ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo de los catálogos maestros de Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, Mensaje);
    }
}
