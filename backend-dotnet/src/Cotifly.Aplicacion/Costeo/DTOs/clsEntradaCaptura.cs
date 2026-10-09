using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Entrada de <c>POST /web/solicitudes-costeo</c> por rutas (HU-021.6): el cliente del buscador, la línea de negocio por defecto (la de
/// cada ruta la reemplaza), el encabezado (tipo, deadline, notas y horas por defecto), las rutas y la moneda. Cada ruta es una
/// <see cref="clsLineaLicitacionEntrada"/> sin fila de Excel: origen y destino (id de municipio + nombre), condiciones del contenedor,
/// horas, levante, valor de la mercancía, peso, mercancías (con <c>id_tipo_mercancia</c>) y servicios conexos. Sin vehículo ni
/// carrocería (decisión 7): si llegan, se ignoran.
/// </summary>
public sealed record clsEntradaCaptura(
    string? NitCliente,
    string? ClienteNombre,
    string? LineaNegocio,
    clsEncabezadoSolicitudCosteo Encabezado,
    IReadOnlyList<clsLineaLicitacionEntrada?> Rutas,
    string? Moneda = null)
{
    /// <summary>Máximo de rutas de una solicitud del formulario (una licitación admite más, por Excel).</summary>
    public const int MaximoRutas = 100;

    /// <summary>El formulario de una línea (formato plano anterior, HU-005.5) como una captura de una sola ruta.</summary>
    public static clsEntradaCaptura Desde(clsEntradaSolicitudCosteo entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        ArgumentNullException.ThrowIfNull(entrada.Solicitud);
        return new clsEntradaCaptura(
            entrada.NitCliente,
            entrada.ClienteNombre,
            entrada.LineaNegocio,
            entrada.Solicitud.Encabezado,
            [new clsLineaLicitacionEntrada(null, entrada.Solicitud.ALinea())],
            entrada.Moneda);
    }
}
