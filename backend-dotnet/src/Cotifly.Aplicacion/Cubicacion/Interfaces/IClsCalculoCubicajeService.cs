using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Cubicacion.Interfaces;

/// <summary>
/// "Calcular cubicaje" del formulario (<c>POST /web/cubicacion</c>, HU-023.2, #157): una vista previa contra el cubicador. Llama a
/// <c>POST /recomendaciones</c> por <see cref="IClsCubicador"/> con <c>idLineaSolicitud = "PRUEBA-&lt;guid&gt;"</c> y devuelve la lista de
/// vehículos. Cotifly no guarda nada de esa llamada. Reemplaza la cubicación rápida que pasaba por <c>POST /api/cubicacion</c> de Nexo
/// (retirada en el corte del cubicador).
/// </summary>
public interface IClsCalculoCubicajeService
{
    /// <summary>
    /// Pide al cubicador los vehículos para <paramref name="items"/>. <paramref name="usuarioId"/> es el <c>UsuariosWeb.id</c> del
    /// comercial autenticado (viaja su <c>idUsuarioExterno</c>); <paramref name="claseViaje"/> es una preferencia opcional.
    /// </summary>
    /// <remarks>
    /// Errores: 503 <c>CUBICADOR_NO_DISPONIBLE</c> si el cubicador no está configurado o no responde (sin respaldo: es solo una vista
    /// previa); 400 <c>MERCANCIA_DESCONOCIDA</c> si un ítem no trae código ni descripción o la descripción no se parece a ninguna mercancía
    /// del catálogo; los rechazos 400 del cubicador (<c>MERCANCIA_DESCONOCIDA</c>, <c>MERCANCIA_NO_CUBICABLE</c>,
    /// <c>DIMENSIONES_INVALIDAS</c>, <c>MERCANCIAS_REQUERIDAS</c>) y los 503 <c>CUBICADOR_NO_AUTORIZADO</c> y
    /// <c>CUBICADOR_RESPUESTA_INVALIDA</c> llegan con su código.
    /// </remarks>
    Task<clsResultado<clsResultadoCubicacion>> CalcularAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default);
}
