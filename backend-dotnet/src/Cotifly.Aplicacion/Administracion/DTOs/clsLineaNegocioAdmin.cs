using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Administracion.DTOs;

/// <summary>
/// Línea de negocio con su política de margen (<c>LineaNegocio</c> de <c>specs/openapi.yaml</c>; la Api la serializa en
/// snake_case: <c>linea_negocio</c>, <c>metodo_calculo</c>, <c>is_active</c>, <c>updated_at</c>).
/// </summary>
public sealed record clsLineaNegocioAdmin(int Id, string LineaNegocio, decimal Margen, string MetodoCalculo, bool IsActive, DateTimeOffset? UpdatedAt)
{
    public static clsLineaNegocioAdmin Desde(clsMargenPolitica politica)
    {
        ArgumentNullException.ThrowIfNull(politica);
        return new clsLineaNegocioAdmin(
            politica.Id,
            politica.LineaNegocio,
            politica.Margen,
            politica.MetodoCalculo,
            politica.Activa,
            politica.UpdatedAt == default ? null : politica.UpdatedAt);
    }
}

/// <summary>
/// Cambio pedido sobre una línea (<c>ActualizarLineaNegocio</c>). Los tres campos son obligatorios en el contrato; llegan
/// anulables para que el caso de uso responda 400 con un mensaje propio cuando falta alguno.
/// </summary>
public sealed record clsCambioLineaNegocio(decimal? Margen, string? MetodoCalculo, bool? IsActive);
