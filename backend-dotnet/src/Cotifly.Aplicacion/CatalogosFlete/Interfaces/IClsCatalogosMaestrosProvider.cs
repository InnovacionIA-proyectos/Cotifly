using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.CatalogosFlete.Interfaces;

/// <summary>
/// Puerto de los catálogos maestros de Nexo (HU-013.10, G1). La implementación real consulta <c>GET /api/catalogos/tipos-vehiculo</c>,
/// <c>clases-viaje</c>, <c>clases-contenedor</c> y <c>lineas-negocio</c> con <c>X-Portal-Usuario-Id</c> del comercial y los guarda en
/// memoria (ver <c>clsCatalogosMaestrosEnCache</c>). Sin Nexo configurado responde <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>.
/// </summary>
public interface IClsCatalogosMaestrosProvider
{
    /// <summary>
    /// Los cuatro catálogos, o <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> si Nexo no los entrega y no hay una copia utilizable.
    /// <paramref name="usuarioId"/> es el <c>UsuariosWeb.id</c> del comercial autenticado.
    /// </summary>
    Task<clsResultado<clsCatalogosMaestros>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>La última copia obtenida, sin ir a Nexo (<c>null</c> si todavía no hay). Para revisiones que no deben bloquear (G11).</summary>
    clsCatalogosMaestros? UltimaCopia { get; }
}
