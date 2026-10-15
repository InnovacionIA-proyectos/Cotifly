using System.Text.Json;
using System.Text.Json.Serialization;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// HU-023.5 (#160): guarda <see cref="clsAvisosCubicacion"/> (mercancías condicionadas, advertencias y extradimensión del cubicador) como
/// JSON en <c>CotizacionLineasSolicitud.avisosCubicacion</c> (propuesta <c>db/scripts/pendientes/04</c>, con <c>CHECK (ISJSON(...) = 1)</c>). Cada lista se
/// recorta a <see cref="MaximoElementos"/> y cada texto a <see cref="LargoMaximoTexto"/>: los avisos los escribe un sistema externo y no
/// pueden crecer sin límite. Un JSON que ya no se puede leer (forma vieja) vale <c>null</c>: los avisos son informativos. EF no llama al
/// convertidor con <c>null</c>: sin avisos la columna queda en <c>NULL</c>.
/// </summary>
public sealed class clsConvertidorAvisosCubicacion : ValueConverter<clsAvisosCubicacion?, string>
{
    /// <summary>Elementos máximos que se guardan de cada lista.</summary>
    public const int MaximoElementos = 50;

    /// <summary>Largo máximo de cada texto guardado.</summary>
    public const int LargoMaximoTexto = 500;

    private static readonly JsonSerializerOptions _opciones = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public clsConvertidorAvisosCubicacion()
        : base(avisos => Serializar(avisos)!, json => Deserializar(json))
    {
    }

    public static string? Serializar(clsAvisosCubicacion? avisos)
    {
        if (avisos is null)
        {
            return null;
        }

        var acotados = new clsAvisosCubicacion(
            [.. (avisos.MercanciasCondicionadas ?? []).Take(MaximoElementos).Select(m => new clsMercanciaCondicionada(Recortar(m.CodigoMercancia)!, Recortar(m.Comentario)))],
            [.. (avisos.Advertencias ?? []).Take(MaximoElementos).Select(a => new clsAdvertenciaCubicador(Recortar(a.Codigo), Recortar(a.Mensaje)!))],
            avisos.Extradimension is { } e
                ? new clsExtradimensionCubicador(e.HayExtradimension, e.MedidasEstandar, [.. (e.Piezas ?? []).Take(MaximoElementos).Select(p => new clsPiezaExtradimension(
                    p.IndicePosicion, Recortar(p.IdTipoMercancia), Recortar(p.CodigoMercancia),
                    [.. (p.Excesos ?? []).Take(MaximoElementos).Select(x => Recortar(x)!)]))])
                : null);
        return JsonSerializer.Serialize(acotados, _opciones);
    }

    public static clsAvisosCubicacion? Deserializar(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var leidos = JsonSerializer.Deserialize<clsAvisosCubicacion>(json, _opciones);
            if (leidos is null)
            {
                return null;
            }

            var extradimension = leidos.Extradimension is { } e
                ? e with { HayExtradimension = e.HayExtradimension || MarcaVieja(json), Piezas = [.. (e.Piezas ?? []).Select(p => p with { Excesos = p.Excesos ?? [] })] }
                : null;
            return new clsAvisosCubicacion(leidos.MercanciasCondicionadas ?? [], leidos.Advertencias ?? [], extradimension);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>HU-023 (#176): la forma anterior de la extradimensión (<c>{extradimensionado: true, …}</c>) se lee como <c>hayExtradimension</c>.</summary>
    private static bool MarcaVieja(string json) =>
        System.Text.Json.Nodes.JsonNode.Parse(json)?["extradimension"]?["extradimensionado"] is System.Text.Json.Nodes.JsonValue marca
        && marca.TryGetValue<bool>(out var valor) && valor;

    private static string? Recortar(string? texto) =>
        texto is null ? null : texto.Length > LargoMaximoTexto ? texto[..LargoMaximoTexto] : texto;
}
