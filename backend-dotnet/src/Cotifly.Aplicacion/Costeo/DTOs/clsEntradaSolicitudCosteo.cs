using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Entrada de <c>POST /web/solicitudes-costeo</c> (HU-005.5): el cliente elegido en el buscador (<paramref name="NitCliente"/>; el
/// nombre es solo una referencia visible hasta que llega el del catálogo), la línea de negocio de Cotifly y el formulario ampliado.
/// El comercial sale del token, nunca de la entrada.
/// </summary>
public sealed record clsEntradaSolicitudCosteo(string? NitCliente, string? ClienteNombre, string? LineaNegocio, clsSolicitudCosteo Solicitud)
{
    public const int LargoMaximoNit = 20;
    public const int LargoMaximoNombre = 300;
}
