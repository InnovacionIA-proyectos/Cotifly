using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Única función de precio del costeo (ADR-018 §3): la comparten el polling (<see cref="clsPrecioCosteoService"/>) y el
/// ajuste de margen (<see cref="clsAjusteMargenCosteoService"/>). El costo de Nexo (siempre en pesos) pasa por
/// <see cref="IClsPrecioService.Calcular"/> con el margen y el método; en otra moneda el precio se convierte con la
/// <c>trmAplicada</c> FIJADA en la cotización (<see cref="IClsPrecioService.EnMoneda"/>), nunca con la TRM del día.
/// </summary>
public static class clsPrecioMonedaCosteo
{
    /// <summary>Precio en la moneda de <paramref name="cotizacion"/>; los errores del módulo de Precio (<c>PRECIO_INVALIDO</c>, <c>TRM_NO_DISPONIBLE</c>).</summary>
    public static clsResultado<decimal> Calcular(IClsPrecioService precios, clsCotizacionSolicitud cotizacion, decimal costo, decimal margen, string metodo)
    {
        ArgumentNullException.ThrowIfNull(precios);
        ArgumentNullException.ThrowIfNull(cotizacion);
        var enPesos = precios.Calcular(costo, margen, metodo);
        if (enPesos.EsFallo)
        {
            return enPesos.Error;
        }

        var moneda = cotizacion.Moneda ?? clsCotizacionSolicitud.MonedaPesos;
        if (string.Equals(moneda, clsCotizacionSolicitud.MonedaPesos, StringComparison.OrdinalIgnoreCase))
        {
            return enPesos.Valor.Precio;
        }

        var enMoneda = precios.EnMoneda(enPesos.Valor, moneda, new clsTrmDelDia(cotizacion.TrmAplicada, null, null));
        return enMoneda.EsFallo ? enMoneda.Error : enMoneda.Valor.Precio;
    }
}
