using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.AspNetCore.Mvc;

namespace Cotifly.Api.Cubicacion.Controllers;

/// <summary>
/// Catálogos del cubicador para el formulario de captura (HU-021.5, #133; HU-023.1, #156): tipos de mercancía, carrocerías y tipos de
/// vehículo. Cotifly solo los lee (con la caché de <c>Cubicador:MinutosCacheCatalogos</c>); el navegador nunca llama al cubicador.
/// </summary>
[Route("web")]
public sealed class clsCatalogosCubicadorController(IClsContextoUsuario usuario, IClsCubicador cubicador) : clsControladorWebBase(usuario)
{
    /// <summary>
    /// 200 el catálogo de tipos de mercancía (<c>[{codigo, nombre, categoria, cubicable, condicionada_cubicacion, comentario_condicion,
    /// activo, id}]</c>, ordenado por nombre, incluidos los inactivos); 503 <c>CUBICADOR_NO_DISPONIBLE</c> si el cubicador no está
    /// configurado o no responde.
    /// </summary>
    [HttpGet("tipos-mercancia")]
    [ProducesResponseType<IReadOnlyList<clsTipoMercanciaCubicador>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListarTiposMercancia(CancellationToken cancellationToken) =>
        Responder(await cubicador.ListarTiposMercanciaAsync(cancellationToken));

    /// <summary>
    /// HU-044.8 (#291): 200 con el catálogo de carrocerías <b>de NEXO</b> (<c>[{codigo, nombre, activo}]</c>), que son las que
    /// propone <c>normalizar-valores</c> y las que valida la captura. Ya no sale del cubicador: Nexo aún no publica un endpoint de
    /// carrocerías, así que la fuente es la lista de carrocerías de Nexo de Cotifly (<see cref="clsCatalogosCaptura.Carrocerias"/>),
    /// la misma que <c>GET /web/catalogos-flete</c>. Siempre responde 200 (no depende del cubicador).
    /// </summary>
    [HttpGet("tipos-carroceria")]
    [ProducesResponseType<IReadOnlyList<clsTipoCarroceriaCubicador>>(StatusCodes.Status200OK)]
    public IActionResult ListarTiposCarroceria() => Ok(_carroceriasNexo);

    /// <summary>Las carrocerías de Nexo (<see cref="clsCatalogosCaptura.Carrocerias"/>) con el código como nombre (el front muestra el texto amigable).</summary>
    private static readonly IReadOnlyList<clsTipoCarroceriaCubicador> _carroceriasNexo =
        [.. clsCatalogosCaptura.Carrocerias.Select(c => new clsTipoCarroceriaCubicador(c, c, true))];

    /// <summary>
    /// 200 el catálogo de tipos de vehículo (<c>[{codigo, nombre, carrocerias, activo}]</c>): el nombre que se muestra en pantalla y en el
    /// PDF; 503 <c>CUBICADOR_NO_DISPONIBLE</c> (también mientras el cubicador no publique <c>GET /tipos-vehiculo</c>, petición P1).
    /// </summary>
    [HttpGet("tipos-vehiculo")]
    [ProducesResponseType<IReadOnlyList<clsTipoVehiculoCubicador>>(StatusCodes.Status200OK)]
    [ProducesResponseType<clsErrorNegocioWeb>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListarTiposVehiculo(CancellationToken cancellationToken) =>
        Responder(await cubicador.ListarTiposVehiculoAsync(cancellationToken));

    /// <summary>El detalle del fallo (estado HTTP, ruta del cubicador) queda en el log del adaptador; al cliente solo va el código y el mensaje.</summary>
    private IActionResult Responder<T>(clsResultado<IReadOnlyList<T>> resultado) =>
        resultado.Coincidir<IActionResult>(t => Ok(t), e => Error(new clsErrorDominio(e.Codigo, e.Mensaje)));
}
