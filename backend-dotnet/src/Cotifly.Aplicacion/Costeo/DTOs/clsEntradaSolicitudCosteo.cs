using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Entrada de <c>POST /web/solicitudes-costeo</c> (HU-005.5): el cliente elegido en el buscador (<paramref name="NitCliente"/>; el
/// nombre es solo una referencia visible hasta que llega el del catálogo), la línea de negocio de Cotifly y el formulario ampliado.
/// El comercial sale del token, nunca de la entrada. <paramref name="Moneda"/> (HU-013.7, ADR-014 §5): <c>COP</c> si no viene, o
/// <c>USD</c>; en USD la TRM del día se fija al crear.
/// </summary>
public sealed record clsEntradaSolicitudCosteo(
    string? NitCliente, string? ClienteNombre, string? LineaNegocio, clsSolicitudCosteo Solicitud, string? Moneda = null)
{
    public const int LargoMaximoNit = 20;
    public const int LargoMaximoNombre = 300;
}
