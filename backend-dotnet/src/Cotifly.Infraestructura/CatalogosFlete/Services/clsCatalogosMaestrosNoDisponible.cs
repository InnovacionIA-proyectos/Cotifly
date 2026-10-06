using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// Catálogos maestros cuando Nexo no está configurado (<c>Nexo:UrlBase</c> vacía): siempre <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>.
/// Elegido por <c>clsFabricaCatalogosMaestros</c>.
/// </summary>
public sealed class clsCatalogosMaestrosNoDisponible : IClsCatalogosMaestrosProvider
{
    public clsCatalogosMaestros? UltimaCopia => null;

    public Task<clsResultado<clsCatalogosMaestros>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
        Task.FromResult<clsResultado<clsCatalogosMaestros>>(
            new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, clsCatalogosFleteNoDisponible.Mensaje));
}
