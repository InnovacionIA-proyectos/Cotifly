using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Cubicación rápida cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía): responde siempre
/// <c>CUBICACION_NO_DISPONIBLE</c>. Elegido por <c>clsFabricaCubicacion</c> cuando falta configuración; un adaptador
/// registrado antes (pruebas) se respeta igual.
/// </summary>
public sealed class clsCubicacionNoDisponible : IClsCubicacionProvider
{
    public const string Mensaje = "La cubicación no está disponible (Nexo sin configurar).";

    public Task<clsResultado<clsResultadoCubicacion>> CubicarAsync(
        Guid usuarioId, IReadOnlyList<clsItemCubicacion> items, string? claseViaje, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsResultadoCubicacion>>(new clsErrorDominio(clsCodigosError.CubicacionNoDisponible, Mensaje));
}
