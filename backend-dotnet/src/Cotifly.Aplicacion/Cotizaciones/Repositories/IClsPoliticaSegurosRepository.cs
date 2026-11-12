using Cotifly.Dominio.Seguros;

namespace Cotifly.Aplicacion.Cotizaciones.Repositories;

/// <summary>
/// HU-046.3 (#269): lectura de la política de seguros por valor de la mercancía (las 4 tablas <c>PoliticaSeguro*</c> del script
/// <c>db/scripts/21_politica_seguros.sql</c>, HU-046.2 #268). Solo lectura: la cambia Datos con
/// <c>db/scripts/operacion/cambiar_politica_seguros.sql</c>. SIN caché: cada llamada lee las tablas, así que un cambio de la política se
/// ve en la siguiente petición, sin desplegar.
/// </summary>
public interface IClsPoliticaSegurosRepository
{
    /// <summary>
    /// La política vigente: los tramos ACTIVOS con sus medidas ACTIVAS (nivel del tramo y los <c>codigoServicio</c> que cumplen cada una).
    /// <see cref="clsPoliticaSeguros.Vacia"/> si no hay tramos activos.
    /// </summary>
    Task<clsPoliticaSeguros> ObtenerAsync(CancellationToken cancellationToken = default);
}
