using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Infraestructura.CatalogosFlete.Services;

/// <summary>
/// Catálogos de <c>GET /web/catalogos-flete</c> a partir de los catálogos maestros de Nexo (HU-013.10, #106, G1): los tipos de
/// vehículo (<c>valor</c> = <c>codigo</c>, <c>etiqueta</c> = <c>descripcion</c>) y las clases de viaje (<c>valor</c> y
/// <c>etiqueta</c> = <c>descripcion</c>) salen de <see cref="IClsCatalogosMaestrosProvider"/> (con caché); la carrocería sigue en
/// código (<see cref="clsCatalogosCaptura.Carrocerias"/>) hasta que Nexo publique su catálogo. Ya no consulta
/// <c>GET /api/biblioteca-sap-historico/filtros</c> (el histórico de tarifas).
/// HU-027.1 (#179): también expone las clases de contenedor del catálogo maestro (<c>valor</c> y <c>etiqueta</c> =
/// <c>descripcion</c>, que en Nexo es el código, ej. <c>CNT40.HC</c>), sin la lista provisional G15
/// (<see cref="clsCatalogosCaptura.ClasesContenedorProvisionales"/>). Si Nexo responde <c>[]</c> (base SAP sin configurar) o ese
/// catálogo opcional falla, la lista va vacía y la respuesta sigue siendo 200.
/// </summary>
/// <remarks>
/// Sin catálogos maestros (Nexo caído sin copia utilizable, comercial sin <c>idUsuarioExterno</c>) responde
/// <c>CATALOGOS_FLETE_NO_DISPONIBLE</c>: el formulario no ofrece texto libre. Se registra vía <c>clsFabricaCatalogosFlete</c>.
/// </remarks>
public sealed class clsCatalogosFleteNexo(IClsCatalogosMaestrosProvider maestros) : IClsCatalogosFleteProvider
{
    public const string Mensaje = clsCatalogosMaestrosNexo.Mensaje;

    /// <summary>Carrocerías en código, con el mismo código como etiqueta (el frontend muestra su texto amigable).</summary>
    public static readonly IReadOnlyList<clsOpcionCatalogo> Carrocerias = [.. clsCatalogosCaptura.Carrocerias.Select(c => new clsOpcionCatalogo(c, c))];

    public async Task<clsResultado<clsCatalogosFlete>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var resultado = await maestros.ObtenerAsync(usuarioId, cancellationToken);
        if (resultado.EsFallo)
        {
            return resultado.Error;
        }

        return new clsCatalogosFlete(resultado.Valor.TiposVehiculo, Carrocerias, resultado.Valor.ClasesViaje, resultado.Valor.ClasesContenedor);
    }
}
