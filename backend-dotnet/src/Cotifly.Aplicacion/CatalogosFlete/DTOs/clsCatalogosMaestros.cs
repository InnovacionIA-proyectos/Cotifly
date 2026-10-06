namespace Cotifly.Aplicacion.CatalogosFlete.DTOs;

/// <summary>
/// Catálogos maestros de Nexo (<c>GET /api/catalogos/*</c>, HU-013.10, #106, gap G1): la fuente de las opciones de
/// <c>GET /web/catalogos-flete</c> y de la validación por código de <c>POST /web/solicitudes-costeo</c> (G2). En cada opción,
/// <see cref="clsOpcionCatalogo.Valor"/> es lo que viaja a Nexo.
/// </summary>
/// <param name="TiposVehiculo"><c>GET /api/catalogos/tipos-vehiculo</c>: <c>valor</c> = <c>codigo</c> (SAP, ej. <c>2S3</c>), <c>etiqueta</c> = <c>descripcion</c>.</param>
/// <param name="ClasesViaje"><c>GET /api/catalogos/clases-viaje</c>: <c>valor</c> y <c>etiqueta</c> = <c>descripcion</c> (ej. <c>Carga Suelta</c>).</param>
/// <param name="ClasesContenedor"><c>GET /api/catalogos/clases-contenedor</c>: <c>valor</c> y <c>etiqueta</c> = <c>descripcion</c>.</param>
/// <param name="LineasNegocio"><c>GET /api/catalogos/lineas-negocio</c>: solo para revisar <c>MargenesPolitica.mapeoLineasNexo</c> (G11).</param>
public sealed record clsCatalogosMaestros(
    IReadOnlyList<clsOpcionCatalogo> TiposVehiculo,
    IReadOnlyList<clsOpcionCatalogo> ClasesViaje,
    IReadOnlyList<clsOpcionCatalogo> ClasesContenedor,
    IReadOnlyList<clsOpcionCatalogo> LineasNegocio);
