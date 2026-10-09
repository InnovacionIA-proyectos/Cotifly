using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Cubicacion.Interfaces;

/// <summary>
/// Puerto del cubicador (motor de selección de vehículos, HU-021.5, #133; <c>CAMBIOS_BACKEND</c> §6). A diferencia de
/// <see cref="IClsCubicacionProvider"/> (la cubicación rápida que pasa por Nexo), este habla directo con el cubicador, autenticado con
/// <c>Authorization: Bearer {Cubicador:ApiKey}</c> (C5). La implementación HTTP es <c>clsCubicadorHttp</c>; sin
/// <c>Cubicador:UrlBase</c>, Infraestructura registra la versión "no disponible" (<see cref="EstaConfigurado"/> = <c>false</c>). Nunca
/// lanza por el cubicador: todo vía <see cref="clsResultado{TValor}"/> con <c>CUBICADOR_NO_DISPONIBLE</c>.
/// </summary>
public interface IClsCubicador
{
    /// <summary>¿Hay un cubicador configurado? Sin él, la captura usa el respaldo (un camión sin vehículo por ruta).</summary>
    bool EstaConfigurado { get; }

    /// <summary>
    /// <c>Cubicador:RespaldoSiFalla</c>: si el cubicador configurado falla, ¿la captura sigue con el respaldo (un camión sin vehículo y
    /// un aviso en el log) en vez de fallar con <c>CUBICADOR_NO_DISPONIBLE</c>? Por defecto <c>true</c> mientras el cubicador real no
    /// exista (C1).
    /// </summary>
    bool RespaldoSiFalla { get; }

    /// <summary>
    /// <c>Cubicador:PresupuestoCapturaSegundos</c>: tiempo máximo de llamadas al cubicador en una misma captura (una licitación hace un
    /// POST por fila). Pasado, las rutas restantes no lo llaman: van al respaldo o, sin <see cref="RespaldoSiFalla"/>, la captura falla.
    /// </summary>
    TimeSpan PresupuestoCaptura { get; }

    /// <summary>
    /// <c>POST /recomendaciones</c> con <c>IdLineaSolicitud</c> = <paramref name="rutaId"/>, <c>OrigenSistema = COTIFLY</c> y las
    /// mercancías de la ruta. Devuelve un registro por vehículo recomendado (al menos uno) o <c>CUBICADOR_NO_DISPONIBLE</c>. Un POST
    /// nunca se reintenta automáticamente.
    /// </summary>
    Task<clsResultado<clsRecomendacionCubicador>> RecomendarAsync(
        Guid rutaId, IReadOnlyList<clsMercanciaCubicador> mercancias, CancellationToken cancellationToken = default);

    /// <summary><c>GET /tipos-mercancia</c> (con caché en memoria): el catálogo para el desplegable del formulario y para copiar el nombre.</summary>
    Task<clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>> ListarTiposMercanciaAsync(CancellationToken cancellationToken = default);
}
