using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Margen.Services;

/// <summary>
/// Caso de uso del margen negociado (HU-003.4, <c>registrar_ajuste_margen</c>): acepta el margen manual del
/// comercial, lo registra en <c>MargenAjustes</c> y lo devuelve con origen <c>negociado</c>.
/// </summary>
public interface IClsAjusteMargenService
{
    /// <summary>
    /// Valida el margen manual según el método, registra el ajuste (con el comercial) y, si viene costo, calcula el
    /// precio con ese margen. Errores: <c>AJUSTE_MARGEN_INVALIDO</c> (margen fuera de rango o método desconocido), <c>PRECIO_INVALIDO</c> (costo
    /// inválido) y <c>AJUSTE_COTIZACION_INVALIDA</c> (la cotización no existe o no es del comercial).
    /// </summary>
    Task<clsResultado<clsSalidaAjusteMargen>> RegistrarAsync(clsEntradaAjusteMargen entrada, CancellationToken cancellationToken = default);
}
