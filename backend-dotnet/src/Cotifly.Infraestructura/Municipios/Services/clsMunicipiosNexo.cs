using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.Municipios.Services;

/// <summary>
/// Catálogo de municipios contra Nexo en vivo: <c>GET {Nexo:UrlBase}/api/captura-solicitudes/municipios</c>, el mismo
/// catálogo (sin paginar) que usa el formulario "Nueva solicitud" de Nexo para el origen/destino. Nexo no tiene login
/// propio: cada petición identifica al comercial con <c>X-Portal-clsUsuario-Id: {idUsuarioExterno}</c>
/// (<c>UsuariosWeb.idUsuarioExterno</c>, la misma fila de <c>tblSAPUsuariosApp</c> que usa el login de ClientePro) —
/// nunca un token de Cotifly, igual que
/// <see cref="Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo"/>. Se registra vía
/// <c>clsFabricaMunicipios</c>: sin <c>Nexo:UrlBase</c> configurada se usa <see cref="clsMunicipiosNoDisponible"/> en
/// su lugar. Comparte el cliente HTTP con nombre
/// <see cref="Cotifly.Infraestructura.Clientes.Services.clsBuscadorClientesNexo.NombreCliente"/> y su misma tubería de
/// resiliencia (mismo servicio Nexo, ver <c>clsDependenciasInfraestructura</c>).
/// </summary>
/// <remarks>
/// <para><b>Fallos.</b> Nunca lanza: usuario sin <c>idUsuarioExterno</c>, HTTP, red o tiempo agotado devuelven
/// <c>MUNICIPIOS_NO_DISPONIBLE</c> (Result), igual que <see cref="clsMunicipiosNoDisponible"/> — el llamante
/// (<c>GET /web/municipios</c>) decide qué mostrar. Una sola línea de warning por fallo, sin la traza completa ni el
/// header <c>X-Portal-clsUsuario-Id</c> (no es secreto, pero no aporta al log).</para>
/// </remarks>
public sealed class clsMunicipiosNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsMunicipiosNexo> logger)
    : IClsMunicipiosProvider
{
    public const string Mensaje = "El catálogo de municipios de Nexo no está disponible en este momento: intenta de nuevo más tarde.";

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public bool EstaConfigurada => _urlBase is not null;

    public Task<clsResultado<IReadOnlyList<clsMunicipio>>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        _urlBase is null
            ? Task.FromResult(NoDisponible("Nexo sin configurar (Nexo:UrlBase)"))
            : ConsultarAsync(usuarioId, ConstruirUrl(_urlBase), null, cancellationToken);

    /// <summary>
    /// <c>GET /api/captura-solicitudes/ciudades?q=&amp;departamento=</c> (contrato con Nexo §3.1: de aquí salen los
    /// <c>origenMunicipioId</c>/<c>destinoMunicipioId</c>). Nexo devuelve como mucho 50 filas de Colombia (<c>CiudadBusquedaDto</c>, sin
    /// <c>pais</c>: se completa con <see cref="PaisCiudades"/>).
    /// </summary>
    public Task<clsResultado<IReadOnlyList<clsMunicipio>>> BuscarAsync(
        Guid usuarioId, string? texto, string? departamento, CancellationToken cancellationToken = default) =>
        _urlBase is null
            ? Task.FromResult(NoDisponible("Nexo sin configurar (Nexo:UrlBase)"))
            : ConsultarAsync(usuarioId, ConstruirUrlCiudades(_urlBase, texto, departamento), PaisCiudades, cancellationToken);

    /// <summary>País de las filas de <c>/ciudades</c> (Nexo las filtra a Colombia y no devuelve la columna).</summary>
    public const string PaisCiudades = "Colombia";

    /// <summary>Largo máximo de los filtros de la búsqueda que viajan a Nexo.</summary>
    public const int LargoMaximoFiltro = 100;

    private async Task<clsResultado<IReadOnlyList<clsMunicipio>>> ConsultarAsync(
        Guid usuarioId, Uri url, string? paisPorOmision, CancellationToken cancellationToken)
    {
        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario?.IdUsuarioExterno is not { } idUsuarioExterno)
        {
            return NoDisponible(usuario is null ? "el comercial no existe" : "el comercial no tiene idUsuarioExterno (almacén corporativo)");
        }

        using var peticion = new HttpRequestMessage(HttpMethod.Get, url);
        clsIdentidadNexo.Identificar(peticion, idUsuarioExterno);
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
            if (documento.RootElement.ValueKind != JsonValueKind.Array)
            {
                // Un 200 que no es un arreglo no es un catálogo vacío: es una respuesta que no se entiende.
                return NoDisponible("la respuesta no es un arreglo");
            }

            return clsResultado<IReadOnlyList<clsMunicipio>>.Exito(LeerMunicipios(documento.RootElement, paisPorOmision));
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

    /// <summary>Sin parámetros: Nexo devuelve el catálogo completo.</summary>
    public static Uri ConstruirUrl(string urlBase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        return new Uri($"{urlBase.TrimEnd('/')}/api/captura-solicitudes/municipios", UriKind.Absolute);
    }

    /// <summary><c>GET /api/captura-solicitudes/ciudades</c> con los filtros que traigan texto (recortados a <see cref="LargoMaximoFiltro"/>).</summary>
    public static Uri ConstruirUrlCiudades(string urlBase, string? texto, string? departamento)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlBase);
        var filtros = new List<string>();
        if (Filtro(departamento) is { } d)
        {
            filtros.Add($"departamento={Uri.EscapeDataString(d)}");
        }

        if (Filtro(texto) is { } q)
        {
            filtros.Add($"q={Uri.EscapeDataString(q)}");
        }

        var consulta = filtros.Count == 0 ? string.Empty : "?" + string.Join('&', filtros);
        return new Uri($"{urlBase.TrimEnd('/')}/api/captura-solicitudes/ciudades{consulta}", UriKind.Absolute);
    }

    private static string? Filtro(string? valor) =>
        string.IsNullOrWhiteSpace(valor) ? null : valor.Trim() is var t && t.Length > LargoMaximoFiltro ? t[..LargoMaximoFiltro] : valor.Trim();

    /// <summary>
    /// <c>MunicipioDto[]</c> o <c>CiudadBusquedaDto[]</c> de Nexo: filas sin <c>id</c> numérico se descartan (no identifican nada sin id).
    /// </summary>
    private static IReadOnlyList<clsMunicipio> LeerMunicipios(JsonElement raiz, string? paisPorOmision)
    {
        var municipios = new List<clsMunicipio>();
        foreach (var fila in raiz.EnumerateArray())
        {
            if (!fila.TryGetProperty("id", out var idElemento) || idElemento.ValueKind != JsonValueKind.Number || !idElemento.TryGetInt32(out var id))
            {
                continue;
            }

            municipios.Add(new clsMunicipio(
                id, Texto(fila, "codigoDane"), Texto(fila, "municipio"), Texto(fila, "departamento"), Texto(fila, "pais") ?? paisPorOmision));
        }

        return municipios;
    }

    private static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private clsResultado<IReadOnlyList<clsMunicipio>> NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Catálogo de municipios de Nexo no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo del catálogo de municipios de Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.MunicipiosNoDisponible, Mensaje);
    }
}
