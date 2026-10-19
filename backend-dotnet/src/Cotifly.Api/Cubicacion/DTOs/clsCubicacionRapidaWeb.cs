using System.Text.Json.Serialization;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Api.Cubicacion.DTOs;

/// <summary>
/// Opción D (D3, E2E 2026-10-16): cuerpo del 202 de <c>POST /web/cubicacion</c> (<c>CubicacionEnProceso</c>):
/// <c>{id, estado: "en_proceso", consultar_en: "/web/cubicacion/{id}"}</c>. Va con <c>Location</c> y <c>Retry-After</c>.
/// </summary>
public sealed record clsCubicacionEnProcesoWeb(Guid Id, string Estado, string ConsultarEn);

/// <summary>
/// Opción D: <c>GET /web/cubicacion/{id}</c> (<c>EstadoCubicacion</c>): <c>{id, estado, resultado?, error?}</c>. <c>resultado</c> (la
/// misma forma del 200 de <c>POST /web/cubicacion</c>) solo con <c>completada</c>; <c>error</c> solo con <c>fallida</c>.
/// </summary>
public sealed record clsEstadoCubicacionWeb(
    Guid Id,
    string Estado,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] clsResultadoCubicacion? Resultado,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] clsErrorCubicacionWeb? Error)
{
    public static clsEstadoCubicacionWeb Desde(clsCubicacionRapida cubicacion)
    {
        ArgumentNullException.ThrowIfNull(cubicacion);
        return new clsEstadoCubicacionWeb(
            cubicacion.Id,
            cubicacion.Estado,
            cubicacion.Estado == clsEstadosCubicacionRapida.Completada ? cubicacion.Resultado : null,
            cubicacion.Estado == clsEstadosCubicacionRapida.Fallida && cubicacion.Error is { } error ? clsErrorCubicacionWeb.Desde(error) : null);
    }
}

/// <summary>
/// Opción D: el error de una cubicación <c>fallida</c>: <c>{codigo, mensaje, detalle?}</c>, los mismos códigos que respondería
/// <c>POST /web/cubicacion</c> (por ejemplo <c>DIMENSIONES_INVALIDAS</c> con <c>detalle.item</c>, o <c>CUBICADOR_NO_DISPONIBLE</c>). Las marcas
/// internas (caída, sin respuesta) no salen.
/// </summary>
public sealed record clsErrorCubicacionWeb(
    string Codigo,
    string Mensaje,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, object?>? Detalle)
{
    private static readonly string[] _visibles = ["item", "campo", "descripcion"];

    public static clsErrorCubicacionWeb Desde(clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var detalle = error.Datos?.Where(d => _visibles.Contains(d.Key, StringComparer.Ordinal))
            .ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
        return new clsErrorCubicacionWeb(error.Codigo, error.Mensaje, detalle is { Count: > 0 } ? detalle : null);
    }
}
