using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Precio.Services;

/// <summary>Caso de uso <c>calcular_precio</c>: precio al cliente a partir del costo y el margen.</summary>
public interface IClsPrecioService
{
    /// <summary>Precio por <c>margen</c> o <c>markup</c>; <c>PRECIO_INVALIDO</c> si la entrada no permite calcularlo.</summary>
    clsResultado<clsSalidaPrecio> Calcular(decimal costo, decimal margen, string? metodo = "margen");
}
