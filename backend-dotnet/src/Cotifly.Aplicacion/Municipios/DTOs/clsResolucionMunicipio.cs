using Cotifly.Dominio.Agente;

namespace Cotifly.Aplicacion.Municipios.DTOs;

/// <summary>Cómo coincidió el texto del comercial con el catálogo de municipios (HU-005.13).</summary>
public enum CoincidenciaMunicipio
{
    /// <summary>Un solo municipio: se usa directo.</summary>
    Unica,

    /// <summary>Varios municipios (o una sola coincidencia aproximada): el comercial elige antes de seguir.</summary>
    Varias,

    /// <summary>Ningún municipio del catálogo: se le dice al comercial, sin inventar uno.</summary>
    Ninguna,
}

/// <summary>
/// Resultado de resolver un origen o destino escrito en texto libre contra el catálogo de municipios de Nexo
/// (<see cref="Services.clsResolvedorMunicipios"/>).
/// </summary>
/// <param name="Texto">Texto que escribió el comercial.</param>
/// <param name="Coincidencia">Una, varias o ninguna.</param>
/// <param name="Candidatos">
/// Con <see cref="CoincidenciaMunicipio.Unica"/>, el municipio resuelto; con <see cref="CoincidenciaMunicipio.Varias"/>, los
/// candidatos (hasta <see cref="Services.clsResolvedorMunicipios.MaximoCandidatos"/>); vacío con
/// <see cref="CoincidenciaMunicipio.Ninguna"/>.
/// </param>
/// <param name="Total">Coincidencias en el catálogo (puede ser mayor que <see cref="Candidatos"/>.Count si se recortaron).</param>
public sealed record clsResolucionMunicipio(string Texto, CoincidenciaMunicipio Coincidencia, IReadOnlyList<clsMunicipioResuelto> Candidatos, int Total)
{
    /// <summary>El municipio resuelto, solo con una coincidencia única.</summary>
    public clsMunicipioResuelto? Resuelto => Coincidencia == CoincidenciaMunicipio.Unica ? Candidatos[0] : null;
}
