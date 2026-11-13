using Cotifly.Aplicacion.Cubicacion.DTOs;

namespace Cotifly.Api.Cubicacion.DTOs;

/// <summary>
/// <c>CubicacionRequest</c>: <c>{items, clase_viaje}</c> (snake_case por <see cref="Comun.clsJsonSnakeCaseAttribute"/>). Cada ítem lleva
/// <c>codigo_mercancia</c> (o, si el frontend no lo tiene, <c>descripcion</c>). <c>clase_viaje</c> viaja
/// al cubicador como preferencia (<c>claseViajeSeleccionada</c>).
/// <c>items</c> es obligatorio en negocio (400 <c>ITEMS_REQUERIDOS</c> si falta o viene vacío) pero se tipa
/// <c>IReadOnlyList&lt;...&gt;?</c> a propósito: con Nullable Reference Types, <c>[ApiController]</c> trataría un parámetro de body
/// no-nullable como <c>[Required]</c> y respondería su propio 400 genérico antes de la validación explícita del controller.
/// </summary>
public sealed record clsCubicacionWebRequest(IReadOnlyList<clsItemCubicacion>? Items, string? ClaseViaje);
