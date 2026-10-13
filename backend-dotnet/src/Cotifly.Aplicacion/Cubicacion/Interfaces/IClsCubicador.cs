using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Cubicacion.Interfaces;

/// <summary>
/// Puerto del cubicador (motor de selección de vehículos; HU-021.5, #133, adaptado al contrato de HU-023.1, #156). A diferencia de
/// <see cref="IClsCubicacionProvider"/> (la cubicación rápida que pasa por Nexo y se retira en el corte), este habla directo con el
/// cubicador, autenticado con <c>Authorization: Bearer {Cubicador:ApiKey}</c>. La implementación HTTP es <c>clsCubicadorHttp</c>; sin
/// <c>Cubicador:UrlBase</c>, Infraestructura registra la versión "no disponible" (<see cref="EstaConfigurado"/> = <c>false</c>). Nunca
/// lanza por el cubicador: todo vía <see cref="clsResultado{TValor}"/>.
/// </summary>
/// <remarks>
/// Errores (<c>clsCodigosError</c>): rechazos 400 del contrato (<c>MERCANCIAS_REQUERIDAS</c>, <c>MERCANCIA_DESCONOCIDA</c>,
/// <c>MERCANCIA_NO_CUBICABLE</c>, <c>DIMENSIONES_INVALIDAS</c>), llave rechazada (<c>CUBICADOR_NO_AUTORIZADO</c>, caída), respuesta que
/// no cumple el contrato (<c>CUBICADOR_RESPUESTA_INVALIDA</c>) y caídas (<c>CUBICADOR_NO_DISPONIBLE</c>: 503, 5xx, tiempo agotado, red).
/// </remarks>
public interface IClsCubicador
{
    /// <summary>¿Hay un cubicador configurado? Sin él, la captura usa el respaldo (un camión sin vehículo por ruta).</summary>
    bool EstaConfigurado { get; }

    /// <summary>
    /// <c>Cubicador:RespaldoSiFalla</c>: si el cubicador configurado se CAE (no responde, 5xx), ¿la captura sigue con el
    /// respaldo (un camión sin vehículo y un aviso en el log) en vez de fallar con <c>CUBICADOR_NO_DISPONIBLE</c>? Un rechazo de la
    /// petición, una respuesta inválida o la llave rechazada nunca van al respaldo.
    /// </summary>
    bool RespaldoSiFalla { get; }

    /// <summary>
    /// <c>Cubicador:PresupuestoCapturaSegundos</c>: tiempo máximo de llamadas al cubicador en una misma captura (una licitación hace un
    /// POST por fila). Pasado, las rutas restantes no lo llaman: van al respaldo o, sin <see cref="RespaldoSiFalla"/>, la captura falla.
    /// </summary>
    TimeSpan PresupuestoCaptura { get; }

    /// <summary>
    /// <c>POST /recomendaciones</c> para una ruta. Devuelve la respuesta ya validada contra el contrato (<c>ASIGNADO</c> con todos los
    /// campos de cada vehículo, o <c>SIN_SELECCION_AUTOMATICA</c> con su motivo) o el error traducido. Un POST nunca se reintenta.
    /// </summary>
    Task<clsResultado<clsRecomendacionCubicador>> RecomendarAsync(clsPeticionRecomendacion peticion, CancellationToken cancellationToken = default);

    /// <summary><c>GET /tipos-mercancia</c> (con caché en memoria): código, nombre, categoría, cubicable, condicionada y activo.</summary>
    Task<clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>> ListarTiposMercanciaAsync(CancellationToken cancellationToken = default);

    /// <summary><c>GET /tipos-carroceria</c> (con caché en memoria).</summary>
    Task<clsResultado<IReadOnlyList<clsTipoCarroceriaCubicador>>> ListarTiposCarroceriaAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>GET /tipos-vehiculo</c> (con caché en memoria; petición P1 al cubicador: mientras no exista responde
    /// <c>CUBICADOR_NO_DISPONIBLE</c>).
    /// </summary>
    Task<clsResultado<IReadOnlyList<clsTipoVehiculoCubicador>>> ListarTiposVehiculoAsync(CancellationToken cancellationToken = default);
}
