using System.Globalization;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// HU-045.3 (#265, decisión del usuario del 2026-11-09): aviso de las rutas que cotizan con el seguro de la mercancía marcado y SIN valor de
/// la mercancía (una edición quitó el valor). No bloquea nada: el seguro no suma al precio y el PDF sale sin su fila en esas rutas. Va en la
/// vista del costeo (y por ella en el detalle por folio) y en la respuesta de generar el PDF, para que el frontend lo muestre.
/// </summary>
/// <param name="Codigo">Siempre <see cref="clsCodigosError.SeguroSinValorMercancia"/>.</param>
/// <param name="Mensaje">Texto para el comercial, con las rutas.</param>
/// <param name="Rutas">Las rutas afectadas, en orden.</param>
public sealed record clsAvisoSeguro(string Codigo, string Mensaje, IReadOnlyList<clsRutaAvisoSeguro> Rutas)
{
    /// <summary>El aviso de <paramref name="cotizacion"/>, o <c>null</c> si ninguna ruta tiene el seguro marcado sin valor de la mercancía.</summary>
    public static clsAvisoSeguro? De(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.RutasConSeguroSinValor();
        if (rutas.Count == 0)
        {
            return null;
        }

        var ordenes = string.Join(", ", rutas.Select(r => r.Orden.ToString(CultureInfo.InvariantCulture)));
        var cuales = rutas.Count == 1 ? $"la ruta {ordenes}" : $"las rutas {ordenes}";
        return new clsAvisoSeguro(
            clsCodigosError.SeguroSinValorMercancia,
            $"El seguro de la mercancía está marcado en {cuales} sin valor de la mercancía: no se suma al precio ni sale en el PDF. Para "
            + "cobrarlo, edita la solicitud con el valor de la mercancía (en una versión nueva si ya está finalizada); si no aplica, quita el seguro.",
            [.. rutas.Select(r => new clsRutaAvisoSeguro(r.Orden, r.Id))]);
    }
}

/// <summary>HU-045.3 (#265): una ruta del <see cref="clsAvisoSeguro"/>.</summary>
public sealed record clsRutaAvisoSeguro(int LineaOrden, Guid LineaSolicitudId);
