namespace Cotifly.Dominio.Agente;

/// <summary>
/// Municipio del catálogo de Nexo (HU-005.13) que resolvió <c>resolver_municipio</c> para el origen o el destino, o un candidato
/// pendiente de confirmar cuando el texto del comercial coincide con varios (mismo nombre en más de un departamento). Solo vive
/// en el estado de la conversación (ADR-004); no se persiste en <c>Cotizaciones</c>.
/// </summary>
/// <param name="Id">Id del municipio en el catálogo de Nexo.</param>
/// <param name="Municipio">Nombre del municipio tal como lo trae el catálogo.</param>
/// <param name="Departamento">Departamento, si el catálogo lo trae.</param>
/// <param name="Pais">País, si el catálogo lo trae.</param>
/// <param name="CodigoDane">Código Dane, si el catálogo lo trae.</param>
public sealed record clsMunicipioResuelto(int Id, string Municipio, string? Departamento = null, string? Pais = null, string? CodigoDane = null)
{
    /// <summary>Texto canónico del municipio: <c>Municipio, Departamento</c> (solo el municipio si el catálogo no trae departamento).</summary>
    public string Canonico() =>
        string.IsNullOrWhiteSpace(Departamento) ? Municipio.Trim() : $"{Municipio.Trim()}, {Departamento.Trim()}";
}
