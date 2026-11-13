using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Cubicador sin configurar (<c>Cubicador:UrlBase</c> vacía): <see cref="EstaConfigurado"/> = <c>false</c>, así que la
/// captura usa el respaldo (un camión sin vehículo por ruta) sin llamarlo; si alguien lo llama igual, responde
/// <c>CUBICADOR_NO_DISPONIBLE</c>. Elegido por <c>clsFabricaCubicador</c>.
/// </summary>
public sealed class clsCubicadorNoDisponible : IClsCubicador
{
    public const string Mensaje = "El cubicador no está disponible (sin configurar).";

    public bool EstaConfigurado => false;

    public bool RespaldoSiFalla => true;

    public TimeSpan PresupuestoCaptura => TimeSpan.Zero;

    public Task<clsResultado<clsRecomendacionCubicador>> RecomendarAsync(
        clsPeticionRecomendacion peticion, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsRecomendacionCubicador>>(NoDisponible());

    public Task<clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>> ListarTiposMercanciaAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>>(NoDisponible());

    public Task<clsResultado<IReadOnlyList<clsTipoCarroceriaCubicador>>> ListarTiposCarroceriaAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<IReadOnlyList<clsTipoCarroceriaCubicador>>>(NoDisponible());

    public Task<clsResultado<IReadOnlyList<clsTipoVehiculoCubicador>>> ListarTiposVehiculoAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<IReadOnlyList<clsTipoVehiculoCubicador>>>(NoDisponible());

    private static clsErrorDominio NoDisponible() =>
        new(clsCodigosError.CubicadorNoDisponible, Mensaje, "cubicador sin configurar (Cubicador:UrlBase)");
}
