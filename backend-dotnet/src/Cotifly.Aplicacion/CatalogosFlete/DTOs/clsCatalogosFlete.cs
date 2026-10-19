namespace Cotifly.Aplicacion.CatalogosFlete.DTOs;

/// <summary>
/// Catálogos de <c>GET /web/catalogos-flete</c> para el formulario de captura: tipos de vehículo, tipos de carrocería (en código
/// hasta que Nexo publique su catálogo), clases de viaje y clases de contenedor. Se omite <c>lineasNegocio</c> de Nexo: Cotifly ya
/// tiene su propio catálogo de línea de negocio.
/// </summary>
/// <param name="TiposVehiculo">Opciones de tipo de vehículo.</param>
/// <param name="TiposCarroceria">Opciones de tipo de carrocería.</param>
/// <param name="ClasesViaje">Opciones de clase de viaje.</param>
/// <param name="ClasesContenedor">
/// HU-027.1 (#179): opciones de clase de contenedor, solo las de <c>GET /api/catalogos/clases-contenedor</c> de Nexo (no la lista
/// provisional G15). <c>valor</c> = <c>etiqueta</c> = <c>descripcion</c> de Nexo, que es el código (<c>CNT40.HC</c>): el mismo
/// texto con que <c>clsValidadorCatalogosCaptura</c> valida <c>clase_contenedor</c>. Vacía si Nexo no tiene la base SAP
/// configurada (responde <c>[]</c>) o si ese catálogo opcional falla: no es un error.
/// </param>
public sealed record clsCatalogosFlete(
    IReadOnlyList<clsOpcionCatalogo> TiposVehiculo,
    IReadOnlyList<clsOpcionCatalogo> TiposCarroceria,
    IReadOnlyList<clsOpcionCatalogo> ClasesViaje,
    IReadOnlyList<clsOpcionCatalogo> ClasesContenedor);

/// <summary>Opción de un catálogo: valor interno y etiqueta legible para el comercial.</summary>
/// <param name="Valor">Valor interno de la opción (el que viaja en la cotización).</param>
/// <param name="Etiqueta">Texto legible para mostrar en el formulario.</param>
public sealed record clsOpcionCatalogo(string Valor, string Etiqueta);
