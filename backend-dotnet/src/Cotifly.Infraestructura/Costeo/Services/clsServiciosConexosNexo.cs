using System.Globalization;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Servicios conexos contra Nexo en vivo (HU-013.3, ADR-018 §4): <c>GET /api/servicios-conexos</c>, <c>GET /api/cotizaciones/{id}</c>
/// y <c>POST /api/cotizaciones/{id}/conexos</c>. Reutiliza el envío de <see cref="clsCosteoNexo"/> (cliente <c>NexoCosteo</c>: 30 s,
/// reintentos solo en GET, el POST nunca se reintenta; identidad <c>X-Portal-Usuario-Id</c> del comercial; una línea de warning por
/// fallo) con un límite de <see cref="LimiteRespuestaBytes"/> por respuesta, y traduce los errores al vocabulario del contrato:
/// 403 → <c>NEXO_SIN_PERMISO</c>; al agregar, otro 4xx → <c>CONEXO_INVALIDO</c> con el código de Nexo en el detalle; todo lo demás
/// (sin configurar, caído, 5xx, 401, tiempo agotado, comercial sin <c>idUsuarioExterno</c>, respuesta demasiado grande o sin forma)
/// → <c>NEXO_NO_DISPONIBLE</c>. Nunca lee <c>costoUnitario</c> del catálogo.
/// </summary>
public sealed class clsServiciosConexosNexo(clsCosteoNexo costeo) : IClsServiciosConexosNexo
{
    /// <summary>Tamaño máximo de una respuesta de Nexo (una página de 50 servicios o la cotización de un formulario caben de sobra).</summary>
    public const long LimiteRespuestaBytes = 1024 * 1024;

    public const string MensajeNoDisponible = "Nexo no está disponible en este momento: intenta de nuevo más tarde.";

    public const string MensajeSinPermiso =
        "Tu usuario no tiene permiso en Nexo para los servicios conexos: pide al administrador de Nexo el rol Comercial, CoordinadorFletes o Admin.";

    public const string MensajeConexoInvalido = "Nexo no aceptó el servicio conexo: revisa que el servicio exista, esté activo y la cantidad sea válida.";

    public async Task<clsResultado<clsPaginaCatalogoConexosNexo>> BuscarAsync(
        Guid usuarioId, clsFiltroCatalogoConexos filtro, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var respuesta = await costeo.EnviarAsync(usuarioId, HttpMethod.Get, RutaCatalogo(filtro), null, cancellationToken, LimiteRespuestaBytes);
        if (respuesta.EsFallo)
        {
            return Traducir(respuesta.Error, agregando: false);
        }

        using var documento = respuesta.Valor;
        var raiz = documento.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object)
        {
            return NoDisponible("respuesta del catálogo sin forma de página");
        }

        var items = new List<clsServicioConexoNexo>();
        if (raiz.TryGetProperty("items", out var lista) && lista.ValueKind == JsonValueKind.Array)
        {
            foreach (var fila in lista.EnumerateArray())
            {
                if (clsCosteoNexo.Texto(fila, "id") is not { } id)
                {
                    continue;
                }

                items.Add(new clsServicioConexoNexo(
                    id,
                    clsCosteoNexo.Texto(fila, "codigoServicio"),
                    clsCosteoNexo.Texto(fila, "tipoServicio"),
                    clsCosteoNexo.Texto(fila, "categoria"),
                    clsCosteoNexo.Texto(fila, "unidad"),
                    clsCosteoNexo.Texto(fila, "origen"),
                    clsCosteoNexo.Texto(fila, "destino"),
                    clsCosteoNexo.Texto(fila, "puerto"),
                    clsCosteoNexo.Texto(fila, "ruta"),
                    clsCosteoNexo.Texto(fila, "tipoMaquina"),
                    Fecha(clsCosteoNexo.Texto(fila, "vigencia")),
                    !fila.TryGetProperty("activo", out var activo) || activo.ValueKind != JsonValueKind.False));
            }
        }

        return new clsPaginaCatalogoConexosNexo(clsCosteoNexo.Entero(raiz, "total") ?? items.Count, items);
    }

    public async Task<clsResultado<clsCotizacionNexo>> LeerCotizacionAsync(Guid usuarioId, string cotizacionNexoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cotizacionNexoId);
        var respuesta = await costeo.EnviarAsync(
            usuarioId, HttpMethod.Get, $"/api/cotizaciones/{Uri.EscapeDataString(cotizacionNexoId.Trim())}", null, cancellationToken, LimiteRespuestaBytes);
        if (respuesta.EsFallo)
        {
            return Traducir(respuesta.Error, agregando: false);
        }

        using var documento = respuesta.Valor;
        return clsCosteoNexo.LeerCotizacion(documento.RootElement) is { } cotizacion
            ? cotizacion
            : NoDisponible("respuesta de la cotización sin id");
    }

    /// <summary>
    /// <c>POST /api/cotizaciones/{id}/conexos</c> sin el token del llamador (no se corta a mitad) y sin reintentos. Todo 2xx es efecto
    /// (con la cotización devuelta si se pudo leer); tiempo agotado, red o 5xx es efecto incierto; no enviado o 4xx es error sin efecto.
    /// </summary>
    public async Task<clsResultado<clsAgregadoNexo>> AgregarAsync(
        Guid usuarioId, string cotizacionNexoId, IReadOnlyList<clsConexoParaNexo> items, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cotizacionNexoId);
        ArgumentNullException.ThrowIfNull(items);
        var cuerpo = new clsAgregarConexosWire([.. items.Select(i => new clsConexoItemWire(i.LineaId, i.ServicioConexoId, i.Cantidad))]);
        var respuesta = await costeo.EnviarPostConEfectoAsync(
            usuarioId, $"/api/cotizaciones/{Uri.EscapeDataString(cotizacionNexoId.Trim())}/conexos", cuerpo, LimiteRespuestaBytes);
        switch (respuesta.Fase)
        {
            case FasePostNexo.NoEnviado:
                return NoDisponible(respuesta.Detalle ?? "no enviado");
            case FasePostNexo.Rechazado:
                return Traducir(new clsErrorDominio(clsCodigosError.CosteoNexoRechazada, MensajeConexoInvalido, respuesta.Detalle), agregando: true);
            case FasePostNexo.Incierto:
                return new clsAgregadoNexo(false, null, respuesta.Detalle);
            default:
                using (respuesta.Json)
                {
                    var cotizacion = respuesta.Json is { } json ? clsCosteoNexo.LeerCotizacion(json.RootElement) : null;
                    return new clsAgregadoNexo(true, cotizacion, cotizacion is null ? respuesta.Detalle ?? "respuesta sin cotización" : null);
                }
        }
    }

    /// <summary><c>/api/servicios-conexos?soloActivos=true&amp;page=&amp;pageSize=[&amp;q=][&amp;categoria=]</c>.</summary>
    public static string RutaCatalogo(clsFiltroCatalogoConexos filtro)
    {
        ArgumentNullException.ThrowIfNull(filtro);
        var ruta = "/api/servicios-conexos?soloActivos=true"
                   + $"&page={filtro.Page.ToString(CultureInfo.InvariantCulture)}&pageSize={filtro.PageSize.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(filtro.Q))
        {
            ruta += "&q=" + Uri.EscapeDataString(filtro.Q.Trim());
        }

        if (!string.IsNullOrWhiteSpace(filtro.Categoria))
        {
            ruta += "&categoria=" + Uri.EscapeDataString(filtro.Categoria.Trim());
        }

        return ruta;
    }

    /// <summary>
    /// Error del envío de Nexo → error del contrato de HU-013.3. El detalle de <see cref="clsCosteoNexo"/> empieza por
    /// <c>HTTP {estado} {código de Nexo}: {mensaje}</c> en los 4xx (y "HTTP 403" en el 403, que allá es "no disponible").
    /// </summary>
    public static clsErrorDominio Traducir(clsErrorDominio error, bool agregando)
    {
        ArgumentNullException.ThrowIfNull(error);
        var detalle = error.Detalle ?? string.Empty;
        if (detalle.StartsWith("HTTP 403", StringComparison.Ordinal))
        {
            // Solo el código de Nexo (o "HTTP 403"): el texto crudo de Nexo no sale al comercial.
            return new clsErrorDominio(clsCodigosError.NexoSinPermiso, MensajeSinPermiso, CodigoNexo(detalle) ?? "HTTP 403");
        }

        if (agregando && detalle.StartsWith("HTTP 4", StringComparison.Ordinal) && !detalle.StartsWith("HTTP 401", StringComparison.Ordinal))
        {
            return new clsErrorDominio(clsCodigosError.ConexoInvalido, MensajeConexoInvalido, SoloCodigo(detalle));
        }

        return new clsErrorDominio(clsCodigosError.NexoNoDisponible, MensajeNoDisponible, error.Codigo == clsCodigosError.CosteoNexoTiempoAgotado
            ? "Nexo no respondió a tiempo"
            : detalle.Length == 0 ? error.Codigo : SoloCodigo(detalle));
    }

    /// <summary>
    /// Detalle de un error HTTP de Nexo sin su texto crudo (arrastre de #99 en #102): <c>HTTP 401 CODIGO: mensaje</c> → <c>CODIGO</c>, y
    /// sin código de Nexo, solo <c>HTTP 401</c>. Los demás detalles (propios de Cotifly, sin texto de Nexo) quedan igual.
    /// </summary>
    public static string SoloCodigo(string detalle)
    {
        ArgumentNullException.ThrowIfNull(detalle);
        if (!detalle.StartsWith("HTTP ", StringComparison.Ordinal))
        {
            return detalle;
        }

        return CodigoNexo(detalle) ?? string.Join(' ', detalle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)).TrimEnd(':');
    }

    /// <summary>El código de Nexo de un detalle <c>HTTP 404 CODIGO: mensaje</c> (<c>CODIGO</c>), o <c>null</c> si no trae.</summary>
    /// <remarks>
    /// Solo cuenta como código una sola palabra en MAYÚSCULAS (letras, dígitos y <c>_</c>, desde 3 caracteres) justo antes de los
    /// dos puntos: <c>HTTP 400 Bad request</c> o <c>HTTP 400 El servicio: no existe</c> no traen código (nunca se toma una palabra suelta
    /// del texto de Nexo).
    /// </remarks>
    public static string? CodigoNexo(string detalle)
    {
        ArgumentNullException.ThrowIfNull(detalle);
        var corte = detalle.IndexOf(':', StringComparison.Ordinal);
        if (corte < 0)
        {
            return null;
        }

        var partes = detalle[..corte].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return partes.Length == 3 && partes[0] == "HTTP" && EsCodigo(partes[2]) ? partes[2] : null;
    }

    private static bool EsCodigo(string texto) =>
        texto.Length >= 3 && char.IsAsciiLetterUpper(texto[0]) && texto.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_');

    private static DateOnly? Fecha(string? texto) =>
        texto is not null && DateOnly.TryParse(texto.Length >= 10 ? texto[..10] : texto, CultureInfo.InvariantCulture, out var fecha) ? fecha : null;

    private static clsErrorDominio NoDisponible(string motivo) => new(clsCodigosError.NexoNoDisponible, MensajeNoDisponible, motivo);

    // --- Forma exacta de clsAgregarConexosRequest / clsConexoItemRequest de Nexo (camelCase; sin valorAplicado) ---

    private sealed record clsAgregarConexosWire(IReadOnlyList<clsConexoItemWire> Items);

    private sealed record clsConexoItemWire(string LineaId, string ServicioConexoId, decimal Cantidad);
}

/// <summary>Servicios conexos cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía): siempre <c>NEXO_NO_DISPONIBLE</c>.</summary>
public sealed class clsServiciosConexosNoDisponible : IClsServiciosConexosNexo
{
    public const string Mensaje = "Nexo no está configurado (Nexo:UrlBase): los servicios conexos no están disponibles.";

    public Task<clsResultado<clsPaginaCatalogoConexosNexo>> BuscarAsync(Guid usuarioId, clsFiltroCatalogoConexos filtro, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsPaginaCatalogoConexosNexo>>(NoDisponible());

    public Task<clsResultado<clsCotizacionNexo>> LeerCotizacionAsync(Guid usuarioId, string cotizacionNexoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsCotizacionNexo>>(NoDisponible());

    public Task<clsResultado<clsAgregadoNexo>> AgregarAsync(
        Guid usuarioId, string cotizacionNexoId, IReadOnlyList<clsConexoParaNexo> items, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsAgregadoNexo>>(NoDisponible());

    private static clsErrorDominio NoDisponible() => new(clsCodigosError.NexoNoDisponible, Mensaje);
}
