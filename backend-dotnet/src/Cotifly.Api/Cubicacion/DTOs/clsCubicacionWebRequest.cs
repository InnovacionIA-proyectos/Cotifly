using Cotifly.Aplicacion.Cubicacion.DTOs;

namespace Cotifly.Api.Cubicacion.DTOs;

/// <summary>
/// <c>CubicacionRequest</c>: <c>{items, clase_viaje}</c> (snake_case por <see cref="Comun.clsJsonSnakeCaseAttribute"/>).
/// <c>items</c> es obligatorio en negocio (400 <c>ITEMS_REQUERIDOS</c> si falta o viene vacío) pero se tipa
/// <c>IReadOnlyList&lt;...&gt;?</c> y no sin el <c>?</c> a propósito: con Nullable Reference Types habilitado,
/// <c>[ApiController]</c> trataría un parámetro de body no-nullable como implícitamente <c>[Required]</c> y
/// respondería su propio 400 genérico antes de llegar a la validación explícita del controller (mismo problema que
/// documenta Nexo en <c>clsCubicarRequest</c>). No se expone <c>unidad_ids</c>: Cotifly siempre evalúa contra todo el
/// catálogo estándar de vehículos.
/// </summary>
public sealed record clsCubicacionWebRequest(IReadOnlyList<clsItemCubicacion>? Items, string? ClaseViaje);
