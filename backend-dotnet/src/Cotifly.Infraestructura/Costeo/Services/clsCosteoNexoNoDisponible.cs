using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Infraestructura.Costeo.Services;

/// <summary>
/// Costeo cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía, ADR-015): responde siempre <c>COSTEO_NEXO_NO_DISPONIBLE</c>.
/// Un envío deja la cotización en <c>error_envio</c> con ese motivo (se puede reintentar cuando Nexo se configure) y el polling no
/// consulta. Elegido por <c>clsFabricaCosteoNexo</c>; un adaptador registrado antes (pruebas) se respeta igual.
/// </summary>
public sealed class clsCosteoNexoNoDisponible : IClsCosteoNexo
{
    public const string Mensaje = "El costeo en Nexo no está configurado (Nexo:UrlBase): la cotización queda guardada para reenviarla después.";

    public bool EstaConfigurado => false;

    public Task<clsResultado<clsSolicitudCreadaNexo>> EnviarSolicitudAsync(Guid usuarioId, clsSolicitudNexo solicitud, CancellationToken cancellationToken = default) =>
        Task.FromResult(NoDisponible<clsSolicitudCreadaNexo>());

    public Task<clsResultado<clsCosteoConsultadoNexo>> ConsultarCosteoAsync(Guid usuarioId, string solicitudId, CancellationToken cancellationToken = default) =>
        Task.FromResult(NoDisponible<clsCosteoConsultadoNexo>());

    public Task<clsResultado<clsCotizacionNexo>> ObtenerCotizacionAsync(Guid usuarioId, string solicitudId, CancellationToken cancellationToken = default) =>
        Task.FromResult(NoDisponible<clsCotizacionNexo>());

    public Task<clsResultado<clsCotizacionNexo>> MarcarResultadoAsync(
        Guid usuarioId, string cotizacionNexoId, clsResultadoNexo resultado, CancellationToken cancellationToken = default) =>
        Task.FromResult(NoDisponible<clsCotizacionNexo>());

    public Task<clsResultado<ResultadoRechazoNexo>> RechazarSolicitudAsync(
        Guid usuarioId, string solicitudId, string motivo, string? comentario, CancellationToken cancellationToken = default) =>
        Task.FromResult(NoDisponible<ResultadoRechazoNexo>());

    private static clsResultado<T> NoDisponible<T>() => new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, Mensaje);
}
