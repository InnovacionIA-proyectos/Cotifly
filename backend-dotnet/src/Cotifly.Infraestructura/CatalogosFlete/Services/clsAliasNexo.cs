using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// HU-031.1 (#194): el diccionario de alias de Nexo en vivo, <c>GET {Nexo:UrlBase}/api/catalogos/alias</c> (NEXO #321, sin <c>tipo</c>: los
/// dos tipos en una sola petición; <c>[Authorize]</c> sin rol, cualquier usuario de Nexo; política <c>listados-estandar</c>). Cada fila es
/// <c>clsAliasDto {id, alias, aliasNormalizado, tipoElemento ("Vehiculo" | "Contenedor"), codigo, activo}</c>; las inactivas, las de otro
/// tipo y las incompletas se descartan. Identifica al comercial con <c>X-Portal-clsUsuario-Id</c>. Comparte el cliente HTTP con nombre
/// <see cref="Clientes.Services.clsBuscadorClientesNexo.NombreCliente"/> (y su tubería de resiliencia y su cupo) con los catálogos maestros.
/// </summary>
/// <remarks>
/// Nunca lanza por Nexo: sin configurar, comercial sin <c>idUsuarioExterno</c>, HTTP (también 404 de un Nexo sin #321), red, tiempo
/// agotado, JSON inválido o una respuesta de más de <see cref="clsCatalogosMaestrosNexo.MaximoBytes"/> o
/// <see cref="clsCatalogosMaestrosNexo.MaximoFilas"/> → <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>, con una línea de warning sin la traza ni el
/// header. No guarda nada: la caché es <see cref="clsAliasNexoEnCache"/>.
/// </remarks>
public sealed class clsAliasNexo(
    HttpClient http, IOptions<clsOpcionesNexo> opciones, IClsUsuarioWebRepository usuarios, ILogger<clsAliasNexo> logger)
    : IClsAliasNexoProvider
{
    public const string RutaAlias = "/api/catalogos/alias";

    public const string Mensaje = "El diccionario de alias de Nexo no está disponible en este momento: intenta de nuevo más tarde.";

    private readonly string? _urlBase = string.IsNullOrWhiteSpace(opciones.Value.UrlBase) ? null : opciones.Value.UrlBase.Trim().TrimEnd('/');

    public async Task<clsResultado<clsDiccionarioAliasNexo>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
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

        using var peticion = new HttpRequestMessage(HttpMethod.Get, clsCatalogosMaestrosNexo.ConstruirUrl(_urlBase, RutaAlias));
        clsIdentidadNexo.Identificar(peticion, idUsuarioExterno.ToString(CultureInfo.InvariantCulture));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!respuesta.IsSuccessStatusCode)
            {
                return NoDisponible($"HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})");
            }

            if (respuesta.Content.Headers.ContentLength > clsCatalogosMaestrosNexo.MaximoBytes
                || await clsCatalogosMaestrosNexo.LeerConLimiteAsync(respuesta.Content, cancellationToken) is not { } bytes)
            {
                return NoDisponible($"la respuesta supera {clsCatalogosMaestrosNexo.MaximoBytes} bytes");
            }

            using var documento = JsonDocument.Parse(bytes);
            if (documento.RootElement.ValueKind != JsonValueKind.Array)
            {
                return NoDisponible("la respuesta no es un arreglo");
            }

            if (documento.RootElement.GetArrayLength() > clsCatalogosMaestrosNexo.MaximoFilas)
            {
                return NoDisponible($"la respuesta supera {clsCatalogosMaestrosNexo.MaximoFilas} filas");
            }

            return new clsDiccionarioAliasNexo(Filas(documento.RootElement));
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

    /// <summary>Las filas activas y completas de la respuesta (falta <c>activo</c> = activa, como el valor por defecto de Nexo).</summary>
    internal static IEnumerable<clsFilaAliasNexo> Filas(JsonElement arreglo)
    {
        foreach (var fila in arreglo.EnumerateArray())
        {
            if (fila.ValueKind != JsonValueKind.Object
                || (fila.TryGetProperty("activo", out var activo) && activo.ValueKind == JsonValueKind.False)
                || !clsDiccionarioAliasNexo.TryParseTipo(Texto(fila, "tipoElemento"), out var tipo)
                || Texto(fila, "alias") is not { } alias
                || Texto(fila, "codigo") is not { } codigo)
            {
                continue;
            }

            yield return new clsFilaAliasNexo(tipo, alias, Texto(fila, "aliasNormalizado"), codigo);
        }
    }

    private static string? Texto(JsonElement objeto, string propiedad) =>
        objeto.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(valor.GetString())
            ? valor.GetString()!.Trim()
            : null;

    private clsErrorDominio NoDisponible(string motivo, Exception? causa = null)
    {
        logger.LogWarning("Diccionario de alias de Nexo no disponible ({Motivo}).", motivo);
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo del diccionario de alias de Nexo ({Motivo}).", motivo);
        }

        return new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, Mensaje);
    }
}

/// <summary>Diccionario de alias cuando Nexo no está configurado: siempre <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>.</summary>
public sealed class clsAliasNexoNoDisponible : IClsAliasNexoProvider
{
    public Task<clsResultado<clsDiccionarioAliasNexo>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsDiccionarioAliasNexo>>(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, clsAliasNexo.Mensaje));
}

/// <summary>Foto en memoria (singleton) del diccionario de alias de Nexo: uno por proceso, igual para todos los comerciales.</summary>
public sealed class clsCacheAliasNexo
{
    private clsFoto? _foto;
    private DateTimeOffset? _ultimoFallo;

    internal object Candado { get; } = new();

    /// <summary>La consulta a Nexo en curso, compartida por todos los que llegan mientras dura.</summary>
    internal Task<clsResultado<clsDiccionarioAliasNexo>>? EnCurso { get; set; }

    internal clsFoto? Foto
    {
        get => Volatile.Read(ref _foto);
        set => Volatile.Write(ref _foto, value);
    }

    /// <summary>Cuándo falló la última consulta (para no volver a Nexo en cada envío mientras está caído).</summary>
    internal DateTimeOffset? UltimoFallo
    {
        get { lock (Candado) { return _ultimoFallo; } }
        set { lock (Candado) { _ultimoFallo = value; } }
    }

    internal sealed record clsFoto(clsDiccionarioAliasNexo Diccionario, DateTimeOffset ObtenidaAt);
}

/// <summary>
/// Decorador con caché de <see cref="IClsAliasNexoProvider"/> (HU-031.1, #194), con las mismas reglas que los catálogos maestros
/// (<see cref="clsCatalogosMaestrosEnCache"/>): vigente <c>Nexo:MinutosCacheCatalogos</c>; vencida, se consulta Nexo y, si falla, se sirve
/// la copia hasta <c>Nexo:HorasCatalogosVencidos</c>; una consulta a la vez. Además, tras un fallo no vuelve a consultar Nexo durante
/// <see cref="EsperaTrasFallo"/>: el diccionario se pide en cada envío que trae un código que el catálogo no conoce, y con Nexo caído
/// cada intento costaría los reintentos y el tiempo de espera del cliente <c>Nexo</c> antes del alta.
/// </summary>
public sealed class clsAliasNexoEnCache(
    IClsAliasNexoProvider interno, clsCacheAliasNexo cache, IClsReloj reloj, IOptions<clsOpcionesNexo> opciones, ILogger<clsAliasNexoEnCache> logger)
    : IClsAliasNexoProvider
{
    /// <summary>Tiempo sin volver a consultar Nexo después de un fallo (se sirve la copia, o "no disponible").</summary>
    public static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromMinutes(1);

    public Task<clsResultado<clsDiccionarioAliasNexo>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        if (Vigente() is { } foto)
        {
            return Task.FromResult(clsResultado<clsDiccionarioAliasNexo>.Exito(foto.Diccionario));
        }

        if (cache.UltimoFallo is { } fallo && reloj.AhoraUtc - fallo < EsperaTrasFallo)
        {
            return Task.FromResult(Respaldo(registrar: false) ?? NoDisponible());
        }

        Task<clsResultado<clsDiccionarioAliasNexo>> tarea;
        lock (cache.Candado)
        {
            if (Vigente() is { } recargada)
            {
                return Task.FromResult(clsResultado<clsDiccionarioAliasNexo>.Exito(recargada.Diccionario));
            }

            tarea = cache.EnCurso ??= CargarAsync(usuarioId);
        }

        return tarea.WaitAsync(cancellationToken);
    }

    private async Task<clsResultado<clsDiccionarioAliasNexo>> CargarAsync(Guid usuarioId)
    {
        await Task.Yield();
        try
        {
            var resultado = await interno.ObtenerAsync(usuarioId, CancellationToken.None);
            if (resultado.EsExitoso)
            {
                cache.Foto = new clsCacheAliasNexo.clsFoto(resultado.Valor, reloj.AhoraUtc);
                cache.UltimoFallo = null;
                return resultado;
            }

            cache.UltimoFallo = reloj.AhoraUtc;
            return Respaldo(registrar: true) ?? resultado;
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // El ámbito del comercial que disparó la consulta terminó antes: para los demás es "no disponible" (o la copia).
            logger.LogWarning(ex, "La consulta compartida del diccionario de alias de Nexo se interrumpió.");
            cache.UltimoFallo = reloj.AhoraUtc;
            return Respaldo(registrar: true) ?? NoDisponible();
        }
        finally
        {
            lock (cache.Candado)
            {
                cache.EnCurso = null;
            }
        }
    }

    private clsResultado<clsDiccionarioAliasNexo>? Respaldo(bool registrar)
    {
        if (cache.Foto is not { } foto || reloj.AhoraUtc - foto.ObtenidaAt >= TimeSpan.FromHours(opciones.Value.HorasCatalogosVencidos))
        {
            return null;
        }

        if (registrar)
        {
            logger.LogWarning("Nexo no entregó el diccionario de alias: se usa la copia del {ObtenidaAt:O}.", foto.ObtenidaAt);
        }

        return foto.Diccionario;
    }

    private static clsResultado<clsDiccionarioAliasNexo> NoDisponible() =>
        new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, clsAliasNexo.Mensaje);

    private clsCacheAliasNexo.clsFoto? Vigente() =>
        cache.Foto is { } foto && reloj.AhoraUtc < foto.ObtenidaAt.AddMinutes(opciones.Value.MinutosCacheCatalogos) ? foto : null;
}

/// <summary>
/// Elige el diccionario de alias según <c>Nexo:UrlBase</c>: configurada → <see cref="clsAliasNexo"/> envuelto en
/// <see cref="clsAliasNexoEnCache"/> si <c>Nexo:MinutosCacheCatalogos</c> es mayor que cero; vacía → <see cref="clsAliasNexoNoDisponible"/>.
/// </summary>
internal static class clsFabricaAliasNexo
{
    public static IClsAliasNexoProvider Crear(IServiceProvider servicios)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        var opciones = servicios.GetRequiredService<IOptions<clsOpcionesNexo>>();
        if (string.IsNullOrWhiteSpace(opciones.Value.UrlBase))
        {
            return new clsAliasNexoNoDisponible();
        }

        var nexo = servicios.GetRequiredService<clsAliasNexo>();
        return opciones.Value.MinutosCacheCatalogos > 0
            ? new clsAliasNexoEnCache(
                nexo,
                servicios.GetRequiredService<clsCacheAliasNexo>(),
                servicios.GetRequiredService<IClsReloj>(),
                opciones,
                servicios.GetRequiredService<ILogger<clsAliasNexoEnCache>>())
            : nexo;
    }
}
