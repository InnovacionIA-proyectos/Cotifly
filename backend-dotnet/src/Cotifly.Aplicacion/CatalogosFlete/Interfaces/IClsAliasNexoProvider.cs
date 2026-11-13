using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.CatalogosFlete.Interfaces;

/// <summary>
/// El diccionario de alias de Nexo (<c>GET /api/catalogos/alias</c>, cualquier usuario autenticado de Nexo)
/// con <c>X-Portal-clsUsuario-Id</c> del comercial. La implementación real lo guarda en memoria (<c>clsAliasNexoEnCache</c>): vigente
/// <c>Nexo:MinutosCacheCatalogos</c> y, si Nexo no responde, la copia vencida hasta <c>Nexo:HorasCatalogosVencidos</c>.
/// </summary>
/// <remarks>
/// Quien lo usa para enviar a Nexo o para validar la captura NUNCA falla por él: sin diccionario sigue sin alias y deja un
/// warning. Solo <c>GET /web/alias/resolver</c> responde 503 (<c>CATALOGOS_FLETE_NO_DISPONIBLE</c>) si no lo hay.
/// </remarks>
public interface IClsAliasNexoProvider
{
    /// <summary>El diccionario, o <c>CATALOGOS_FLETE_NO_DISPONIBLE</c> si Nexo no lo entrega y no hay una copia utilizable.</summary>
    Task<clsResultado<clsDiccionarioAliasNexo>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
