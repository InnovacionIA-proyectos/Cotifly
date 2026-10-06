using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Cotizaciones.Services;

/// <summary>
/// Moneda y TRM que quedan fijas al crear una cotización (ADR-014 §5): hoy la usan las licitaciones y luego el formulario de costeo
/// (HU-013.7). En COP la TRM es 0; en USD es la TRM del día de negocio (la cadena DolarApi con respaldo y la caché del día), y si no hay
/// TRM devuelve <c>TRM_NO_DISPONIBLE</c> para que el caso de uso no guarde nada.
/// </summary>
public interface IClsFijacionMonedaService
{
    /// <summary>Moneda normalizada (<c>COP</c> si viene vacía) y su TRM; otra moneda → <c>PRECIO_INVALIDO</c>.</summary>
    Task<clsResultado<clsMonedaFijada>> FijarAsync(string? moneda, CancellationToken cancellationToken = default);
}

/// <summary>Moneda de la cotización y la TRM con que se convierten sus precios (0 en COP).</summary>
public sealed record clsMonedaFijada(string Moneda, decimal TrmAplicada);

/// <summary>Implementación de <see cref="IClsFijacionMonedaService"/> sobre <see cref="IClsPrecioService.TrmDelDiaAsync"/>.</summary>
public sealed class clsFijacionMonedaService(IClsPrecioService precios) : IClsFijacionMonedaService
{
    public async Task<clsResultado<clsMonedaFijada>> FijarAsync(string? moneda, CancellationToken cancellationToken = default)
    {
        var normalizada = clsConversorMoneda.Normalizar(moneda);
        if (normalizada.EsFallo)
        {
            return normalizada.Error;
        }

        var trm = await precios.TrmDelDiaAsync(normalizada.Valor, cancellationToken);
        return trm.EsFallo ? trm.Error : new clsMonedaFijada(normalizada.Valor, trm.Valor.Valor);
    }
}
