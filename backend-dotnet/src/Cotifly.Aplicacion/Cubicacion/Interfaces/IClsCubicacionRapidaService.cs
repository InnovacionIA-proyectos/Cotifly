using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Cubicacion.Interfaces;

/// <summary>
/// Opción D (D3, E2E 2026-10-16): "Calcular cubicaje" (<c>POST /web/cubicacion</c>) sin colgar la UI. El cálculo
/// (<see cref="IClsCalculoCubicajeService.CalcularAsync"/>) corre en segundo plano con el token del host, no con el de la petición: si el
/// cliente corta, sigue. Se espera hasta <c>Cubicador:EsperaSincronaCubicacionSegundos</c>: si termina, responde como siempre (200 o el
/// error); si no, devuelve el id para consultar (<c>GET /web/cubicacion/{id}</c>).
/// </summary>
public interface IClsCubicacionRapidaService
{
    /// <summary>
    /// Lanza el cálculo de <paramref name="items"/> (ya validados con <see cref="IClsCalculoCubicajeService.Validar"/>) y espera la ventana
    /// síncrona. Error <c>CUBICADOR_OCUPADO</c> si no caben más cubicaciones en curso.
    /// </summary>
    Task<clsResultado<clsInicioCubicacionRapida>> IniciarAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default);

    /// <summary>La cubicación <paramref name="id"/> si es de <paramref name="usuarioId"/> y no venció; si no, <c>null</c> (404, sin IDOR).</summary>
    clsCubicacionRapida? Obtener(Guid usuarioId, Guid id);
}
