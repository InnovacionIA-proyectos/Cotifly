using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Precio.Services;

/// <summary>
/// Implementación de <see cref="IClsPrecioService"/>: delega en la regla pura <see cref="clsCalculadoraPrecio"/>
/// (paridad con <c>mcp_server/precio.py</c>) y adapta el resultado al contrato <c>SalidaPrecio</c>.
/// </summary>
public sealed class clsPrecioService : IClsPrecioService
{
    public clsResultado<clsSalidaPrecio> Calcular(decimal costo, decimal margen, string? metodo = "margen") =>
        clsCalculadoraPrecio.Calcular(costo, margen, metodo)
            .Coincidir<clsResultado<clsSalidaPrecio>>(p => clsSalidaPrecio.Desde(p), e => e);
}
