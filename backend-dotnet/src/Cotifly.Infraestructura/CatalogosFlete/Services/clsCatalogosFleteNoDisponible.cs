using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// Catálogos de flete cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía): responde siempre
/// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>. Elegido por <c>clsFabricaCatalogosFlete</c> cuando falta configuración; un
/// adaptador registrado antes (pruebas) se respeta igual.
/// </summary>
public sealed class clsCatalogosFleteNoDisponible : IClsCatalogosFleteProvider
{
    public const string Mensaje = "Los catálogos de flete no están disponibles (Nexo sin configurar).";

    public Task<clsResultado<clsCatalogosFlete>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsCatalogosFlete>>(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, Mensaje));
}
