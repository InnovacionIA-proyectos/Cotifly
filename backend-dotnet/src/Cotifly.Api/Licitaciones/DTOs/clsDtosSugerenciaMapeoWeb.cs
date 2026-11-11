using System.Text.Json;
using Cotifly.Aplicacion.Licitaciones.DTOs;

namespace Cotifly.Api.Licitaciones.DTOs;

/// <summary>
/// <c>SugerirMapeoWeb</c> (HU-044.2, #253): los encabezados de UNA hoja, en el orden del archivo, y una muestra de sus filas (el frontend
/// manda hasta 200). <c>nit_cliente</c> es el NIT de la licitación, con o sin puntos y dígito de verificación (opcional: sin él NEXO usa
/// solo el diccionario general). Una celda o un encabezado <c>null</c> viaja como texto vacío.
/// </summary>
public sealed class clsSugerirMapeoWebRequest
{
    public string? NitCliente { get; init; }

    public IReadOnlyList<string?>? Encabezados { get; init; }

    public IReadOnlyList<IReadOnlyList<string?>?>? Filas { get; init; }

    public clsPeticionSugerirMapeo APeticion() => new(
        NitCliente,
        [.. (Encabezados ?? []).Select(e => e ?? string.Empty)],
        [.. (Filas ?? []).Select(f => (IReadOnlyList<string>)[.. (f ?? []).Select(c => c ?? string.Empty)])]);
}

/// <summary>
/// <c>LecturaEstructura</c> (HU-044.2, #253): la lectura de <c>leer-estructura</c> de NEXO tal cual, en snake_case. La <c>receta</c> es el
/// JSON de NEXO sin tocar (sus claves no pasan a snake_case: es opaca) y cada elemento de <c>tablas</c> tiene la forma de las hojas de
/// <c>analizar-excel</c>, para mandarlo a <c>sugerir-mapeo</c> sin transformarlo.
/// </summary>
public sealed record clsLecturaEstructuraWeb(
    string Archivo,
    string OrigenReceta,
    double? Similitud,
    Guid? RecetaGuardadaId,
    bool IaUsada,
    string? MotivoSinIa,
    JsonElement? Receta,
    string? Resumen,
    IReadOnlyList<string> Dudas,
    IReadOnlyList<clsHojaDelArchivo> HojasDelArchivo,
    IReadOnlyList<clsHojaExcel> Tablas,
    IReadOnlyList<string> Avisos,
    clsCambiosDeFormato? Cambios)
{
    public static clsLecturaEstructuraWeb Desde(clsLecturaEstructura lectura)
    {
        ArgumentNullException.ThrowIfNull(lectura);
        return new(
            lectura.Archivo,
            lectura.OrigenReceta,
            lectura.Similitud,
            lectura.RecetaGuardadaId,
            lectura.IaUsada,
            lectura.MotivoSinIa,
            lectura.Receta.ValueKind == JsonValueKind.Undefined ? null : lectura.Receta,
            lectura.Resumen,
            lectura.Dudas,
            lectura.HojasDelArchivo,
            lectura.Tablas,
            lectura.Avisos,
            lectura.Cambios);
    }
}
