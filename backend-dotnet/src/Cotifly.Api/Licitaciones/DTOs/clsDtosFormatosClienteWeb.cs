using System.Text.Json;
using Cotifly.Aplicacion.Licitaciones.DTOs;

namespace Cotifly.Api.Licitaciones.DTOs;

// HU-044.5 (#256): plantillas de mapeo, formatos de lectura (recetas) y diccionario del cliente en /web/*, en snake_case. Ninguno expone el
// id local del cliente en NEXO (Cotifly trabaja con el NIT de la licitación y no lo guarda).

/// <summary>
/// <c>PlantillaMapeoWeb</c>: <c>PUT /web/licitaciones/plantillas-mapeo</c>. <c>nit_cliente</c> es el NIT de la licitación (obligatorio; con o
/// sin puntos y dígito de verificación), <c>nombre</c> es opcional (máx. 100) y <c>encabezados</c> van en el orden del archivo con el mapeo
/// confirmado en <c>columnas</c>. Un encabezado <c>null</c> viaja como texto vacío; las reglas las valida NEXO.
/// </summary>
public sealed class clsPlantillaMapeoWebRequest
{
    public string? NitCliente { get; init; }

    public string? Nombre { get; init; }

    public IReadOnlyList<string?>? Encabezados { get; init; }

    public IReadOnlyList<clsColumnaPlantillaWebRequest?>? Columnas { get; init; }

    public clsPeticionPlantillaMapeo APeticion() => new(
        NitCliente ?? string.Empty,
        Nombre,
        [.. (Encabezados ?? []).Select(e => e ?? string.Empty)],
        [.. (Columnas ?? []).OfType<clsColumnaPlantillaWebRequest>().Select(c => new clsColumnaConfirmada(c.Indice, c.Campo ?? string.Empty, c.Transformacion))]);
}

/// <summary>
/// <c>ColumnaPlantilla</c>: una columna confirmada. <c>indice</c> base 0 sobre los encabezados, <c>campo</c> de NEXO (o <c>ignorar</c>) y
/// <c>transformacion</c> (vacía o ausente = <c>ninguna</c>).
/// </summary>
public sealed class clsColumnaPlantillaWebRequest
{
    public int Indice { get; init; }

    public string? Campo { get; init; }

    public string? Transformacion { get; init; }
}

/// <summary><c>PlantillaMapeo</c>: una plantilla del cliente guardada en NEXO.</summary>
public sealed record clsPlantillaMapeoWeb(
    Guid Id,
    string? ClienteNombre,
    string? Nombre,
    IReadOnlyList<string> Encabezados,
    IReadOnlyList<clsColumnaConfirmada> Columnas,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static clsPlantillaMapeoWeb Desde(clsPlantillaMapeo plantilla)
    {
        ArgumentNullException.ThrowIfNull(plantilla);
        return new(plantilla.Id, plantilla.ClienteNombre, plantilla.Nombre, plantilla.Encabezados, plantilla.Columnas, plantilla.CreatedAt,
            plantilla.UpdatedAt);
    }
}

/// <summary>
/// <c>RecetaLectura</c>: un formato de Excel guardado en NEXO para el cliente. <c>receta</c> es el JSON de NEXO sin tocar (opaco: sus claves no
/// pasan a snake_case); se reenvía tal cual en <c>leer-estructura</c> o al guardarlo otra vez.
/// </summary>
public sealed record clsRecetaLecturaWeb(
    Guid Id,
    string? ClienteNombre,
    string? Nombre,
    JsonElement? Receta,
    string? Instrucciones,
    int Usos,
    DateTimeOffset? UltimoUsoAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt)
{
    public static clsRecetaLecturaWeb Desde(clsRecetaGuardada receta)
    {
        ArgumentNullException.ThrowIfNull(receta);
        return new(receta.Id, receta.ClienteNombre, receta.Nombre, receta.Receta.ValueKind == JsonValueKind.Undefined ? null : receta.Receta,
            receta.Instrucciones, receta.Usos, receta.UltimoUsoAt, receta.CreatedAt, receta.UpdatedAt);
    }
}

/// <summary>
/// <c>DiccionarioCliente</c>: los términos del diccionario que Cotifly puede mostrar (hoy, solo los generales) y el <c>aviso</c> cuando se
/// pidió con NIT (<c>TERMINOS_CLIENTE_NO_DISPONIBLES</c>); <c>null</c> sin NIT.
/// </summary>
public sealed record clsDiccionarioClienteWeb(IReadOnlyList<clsTerminoDiccionarioWeb> Terminos, clsAvisoDiccionario? Aviso)
{
    public static clsDiccionarioClienteWeb Desde(clsConsultaDiccionarioCliente consulta)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        return new([.. consulta.Terminos.Select(clsTerminoDiccionarioWeb.Desde)], consulta.Aviso);
    }
}

/// <summary>
/// <c>TerminoDiccionario</c>: un término del diccionario de NEXO. <c>tipo</c> = <c>columna</c> | <c>unidad</c> | <c>valor</c>;
/// <c>general</c> = aplica a todos los clientes.
/// </summary>
public sealed record clsTerminoDiccionarioWeb(
    Guid Id, string Termino, string Tipo, string Concepto, string? Contexto, string? Ejemplo, bool Activo, bool General)
{
    public static clsTerminoDiccionarioWeb Desde(clsTerminoCliente termino)
    {
        ArgumentNullException.ThrowIfNull(termino);
        return new(termino.Id, termino.Termino, termino.Tipo, termino.Concepto, termino.Contexto, termino.Ejemplo, termino.Activo,
            termino.EsGeneral);
    }
}
