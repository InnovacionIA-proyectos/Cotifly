using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;

namespace Cotifly.Aplicacion.Precio.Services;

/// <summary>
/// Implementación de <see cref="IClsPrecioService"/>: delega en la regla pura <see cref="clsCalculadoraPrecio"/>
/// (paridad con <c>mcp_server/precio.py</c>, precio en pesos) y, en USD, convierte con <see cref="clsConversorMoneda"/> y la TRM
/// oficial del día de negocio (<see cref="IClsProveedorTrm"/> con la fecha de <see cref="IClsCalendarioNegocio"/>, HU-012.2, ADR-013).
/// La TRM nunca viene del LLM.
/// </summary>
public sealed class clsPrecioService(IClsProveedorTrm trm, IClsCalendarioNegocio calendario) : IClsPrecioService
{
    public clsResultado<clsSalidaPrecio> Calcular(decimal costo, decimal margen, string? metodo = "margen") =>
        clsCalculadoraPrecio.Calcular(costo, margen, metodo)
            .Coincidir<clsResultado<clsSalidaPrecio>>(p => clsSalidaPrecio.Desde(p), e => e);

    public async Task<clsResultado<clsSalidaPrecio>> CalcularAsync(
        decimal costo, decimal margen, string? metodo, string? moneda, CancellationToken cancellationToken = default)
    {
        var normalizada = clsConversorMoneda.Normalizar(moneda);
        if (normalizada.EsFallo)
        {
            return normalizada.Error;
        }

        // Primero el precio en pesos: una entrada inválida no consulta la TRM.
        var enPesos = Calcular(costo, margen, metodo);
        if (enPesos.EsFallo || normalizada.Valor == clsConversorMoneda.Pesos)
        {
            return enPesos;
        }

        var tasa = await TrmDelDiaAsync(normalizada.Valor, cancellationToken);
        return tasa.EsFallo ? tasa.Error : EnMoneda(enPesos.Valor, normalizada.Valor, tasa.Valor);
    }

    public async Task<clsResultado<decimal>> TrmDelDiaAsync(string? moneda, CancellationToken cancellationToken = default)
    {
        var normalizada = clsConversorMoneda.Normalizar(moneda);
        if (normalizada.EsFallo)
        {
            return normalizada.Error;
        }

        if (normalizada.Valor == clsConversorMoneda.Pesos)
        {
            return 0m;
        }

        var vigente = await trm.ObtenerAsync(calendario.Hoy, cancellationToken);
        return vigente.EsFallo ? vigente.Error : vigente.Valor.Valor;
    }

    public clsResultado<clsSalidaPrecio> EnMoneda(clsSalidaPrecio enPesos, string? moneda, decimal trm)
    {
        ArgumentNullException.ThrowIfNull(enPesos);
        var normalizada = clsConversorMoneda.Normalizar(moneda);
        if (normalizada.EsFallo)
        {
            return normalizada.Error;
        }

        var precioPesos = enPesos.PrecioEnPesos();
        if (normalizada.Valor == clsConversorMoneda.Pesos)
        {
            return enPesos with { Precio = precioPesos, Moneda = clsConversorMoneda.Pesos, TrmAplicada = 0m, PrecioCop = null };
        }

        var convertido = clsConversorMoneda.PesosADolares(precioPesos, trm);
        return convertido.EsFallo
            ? convertido.Error
            : enPesos with { Precio = convertido.Valor, Moneda = normalizada.Valor, TrmAplicada = trm, PrecioCop = precioPesos };
    }
}
