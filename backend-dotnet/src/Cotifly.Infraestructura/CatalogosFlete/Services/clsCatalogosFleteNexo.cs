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
/// Catálogos de la biblioteca SAP TM histórico contra Nexo en vivo:
/// <c>GET {Nexo:UrlBase}/api/biblioteca-sap-historico/filtros</c> — tipos de vehículo, tipos de carrocería (Nexo
/// escribe el campo "Carroseria") y clases de viaje para el formulario de captura. Nexo no tiene login propio: cada
/// petición identifica al comercial con <c>X-Portal-Usuario-Id: {idUsuarioExterno}</c>
/// (<c>UsuariosWeb.idUsuarioExterno</c>), igual que
/// <see cref="Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo"/>. Se registra vía
/// <c>clsFabricaCatalogosFlete</c>: sin <c>Nexo:UrlBase</c> configurada se usa
/// <see cref="clsCatalogosFleteNoDisponible"/> en su lugar. Comparte el cliente HTTP con nombre
/// <see cref="Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo.NombreCliente"/> y su misma tubería de
/// resiliencia (mismo servicio Nexo, ver <c>clsDependenciasInfraestructura</c>).
/// </summary>
/// <remarks>
/// <para><b>Descarta</b> <c>lineasNegocio</c> de la respuesta de Nexo: Cotifly ya tiene su propio catálogo de línea de
/// negocio.</para>
/// <para><b>Fallos.</b> Nunca lanza: usuario sin <c>idUsuarioExterno</c>, HTTP, red o tiempo agotado devuelven
/// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> (Result), igual que <see cref="clsCatalogosFleteNoDisponible"/>. Una sola
/// línea de warning por fallo, sin la traza completa ni el header <c>X-Portal-Usuario-Id</c>.</para>
/// </remarks>
public sealed class clsCatalogosFleteNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsCatalogosFleteNexo> logger)
    : IClsCatalogosFleteProvider
{
    public const string Mensaje = "Los catálogos de flete de Nexo no están disponibles en este momento: intenta de nuevo más tarde.";

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaConfigurada => _urlBase is not null;

    public async Task<clsResultado<clsCatalogosFlete>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
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

        using var peticion = new HttpRequestMessage(HttpMethod.Get, ConstruirUrl(_urlBase));
        peticion.Headers.Add("X-Portal-Usuario-Id", idUsuarioExterno.ToString(CultureInfo.InvariantCulture));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            await using var cuerpo = await respuesta.Content.ReadAsStreamAsync(cancellationToken);
            using var documento = await JsonDocument.ParseAsync(cuerpo, cancellationToken: cancellationToken);
            return LeerCatalogos(documento.RootElement);
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
            return NoDisponible(motivo, ex);
        }
        catch (JsonException ex)
        {
            return NoDisponible("JSON inválido", ex);
        }
    }

    /// <summary>Sin parámetros: Nexo devuelve los catálogos completos.</summary>
    public static Uri ConstruirUrl(string urlBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        return new Uri($"{urlBase.TrimEnd('/')}/api/biblioteca-sap-historico/filtros", UriKind.Absolute);
    }

    /// <summary>
    /// <c>FiltrosBibliotecaSapHistoricoDto</c> de Nexo: descarta <c>lineasNegocio</c> y corrige el nombre del campo
    /// de carrocería ("Carroseria" en Nexo → <see cref="clsCatalogosFlete.TiposCarroceria"/> aquí).
    /// </summary>
    private static clsCatalogosFlete LeerCatalogos(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object)
        {
            return new clsCatalogosFlete([], [], []);
        }

        return new clsCatalogosFlete(
            LeerOpciones(raiz, "tiposVehiculo"),
            LeerOpciones(raiz, "tiposCarroseria"),
            LeerOpciones(raiz, "clasesViaje"));
    }

    /// <summary><c>OpcionDto[]</c> de Nexo: filas sin <c>valor</c> o <c>etiqueta</c> se descartan.</summary>
    private static IReadOnlyList<clsOpcionCatalogo> LeerOpciones(JsonElement raiz, string propiedad)
    {
        if (!raiz.TryGetProperty(propiedad, out var lista) || lista.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var opciones = new List<clsOpcionCatalogo>();
        foreach (var fila in lista.EnumerateArray())
        {
            var valor = Texto(fila, "valor");
            var etiqueta = Texto(fila, "etiqueta");
            if (valor is null || etiqueta is null)
            {
                continue;
            }

            opciones.Add(new clsOpcionCatalogo(valor, etiqueta));
        }

        return opciones;
    }

    private static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private clsResultado<clsCatalogosFlete> NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Catálogos de flete de Nexo no disponibles ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo de los catálogos de flete de Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, Mensaje);
    }
}
