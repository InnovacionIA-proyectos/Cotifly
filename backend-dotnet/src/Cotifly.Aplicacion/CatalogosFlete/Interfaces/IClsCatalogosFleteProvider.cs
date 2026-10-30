using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.CatalogosFlete.Interfaces;

/// <summary>
/// Puerto de los catálogos de flete de <c>GET /web/catalogos-flete</c>: tipos de vehículo, tipos de carrocería,
/// clases de viaje y clases de contenedor del formulario de captura. La implementación real (<c>clsCatalogosFleteNexo</c>) usa
/// los catálogos maestros de Nexo (<c>GET /api/catalogos/*</c>, vía <c>IClsCatalogosMaestrosProvider</c>), identificando al comercial ante Nexo con
/// <c>X-Portal-clsUsuario-Id: {UsuariosWeb.idUsuarioExterno}</c> — de ahí que reciba <paramref name="usuarioId"/>. Sin
/// adaptador real, Infraestructura registra uno que responde <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>.
/// </summary>
public interface IClsCatalogosFleteProvider
{
    /// <summary>
    /// Los cuatro catálogos (<c>ClasesContenedor</c> puede venir vacía). <paramref name="usuarioId"/> es el <c>UsuariosWeb.id</c> de Cotifly del comercial
    /// autenticado (del token en <c>/web/*</c>); nunca lo decide el llamante remoto.
    /// </summary>
    Task<clsResultado<clsCatalogosFlete>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
