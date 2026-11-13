using System.Globalization;
using System.Text;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Municipios.Interfaces;

/// <summary>
/// Puerto del catálogo de municipios de <c>GET /web/municipios</c>: origen/destino del formulario de captura. La
/// implementación real (<c>clsMunicipiosNexo</c>) identifica al comercial ante Nexo con
/// <c>X-Portal-clsUsuario-Id: {UsuariosWeb.idUsuarioExterno}</c> — de ahí que reciba <paramref name="usuarioId"/>. Sin adaptador real,
/// Infraestructura registra uno que responde <c>MUNICIPIOS_NO_DISPONIBLE</c>.
/// </summary>
/// <remarks>
/// Contrato con Nexo §3.1 y §7: el buscador de origen/destino usa <c>GET /api/captura-solicitudes/ciudades</c>
/// (<see cref="BuscarAsync"/>, máximo 50 filas, solo Colombia): de ahí salen los <c>origenMunicipioId</c>/<c>destinoMunicipioId</c>.
/// <see cref="ObtenerAsync"/> sigue siendo el catálogo completo (<c>GET /api/captura-solicitudes/municipios</c>, la misma tabla
/// <c>tbl_municipios</c> y los mismos ids) para lo que necesita todos los municipios a la vez: validar un id y resolver por nombre las
/// filas de una licitación. <c>/ciudades</c> no sirve para eso: corta en 50 filas y no filtra por id.
/// </remarks>
public interface IClsMunicipiosProvider
{
    /// <summary>
    /// Catálogo completo de municipios (Nexo no pagina esta respuesta). <paramref name="usuarioId"/> es el
    /// <c>UsuariosWeb.id</c> de Cotifly del comercial autenticado (del token en <c>/web/*</c>); nunca lo decide el
    /// llamante remoto.
    /// </summary>
    Task<clsResultado<IReadOnlyList<clsMunicipio>>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Búsqueda de ciudades para el origen/destino (<c>GET /api/captura-solicitudes/ciudades?q=&amp;departamento=</c>):
    /// el <paramref name="texto"/> dentro del nombre y el <paramref name="departamento"/> exacto, sin tildes ni mayúsculas; como mucho
    /// <see cref="MaximoBusqueda"/> filas, ordenadas por nombre. Por omisión filtra el catálogo completo con la misma regla (dobles de
    /// prueba); el adaptador de Nexo consulta <c>/ciudades</c>.
    /// </summary>
    async Task<clsResultado<IReadOnlyList<clsMunicipio>>> BuscarAsync(
        Guid usuarioId, string? texto, string? departamento, CancellationToken cancellationToken = default)
    {
        var catalogo = await ObtenerAsync(usuarioId, cancellationToken);
        if (catalogo.EsFallo)
        {
            return catalogo.Error;
        }

        var termino = Normalizar(texto);
        var depto = Normalizar(departamento);
        return clsResultado<IReadOnlyList<clsMunicipio>>.Exito([.. catalogo.Valor
            .Where(m => depto.Length == 0 || Normalizar(m.Departamento) == depto)
            .Where(m => termino.Length == 0 || Normalizar(m.Municipio).Contains(termino, StringComparison.Ordinal))
            .OrderBy(m => m.Municipio, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximoBusqueda)]);
    }

    /// <summary>Filas máximas de <see cref="BuscarAsync"/> (el <c>Top</c> de <c>/ciudades</c> en Nexo).</summary>
    public const int MaximoBusqueda = 50;

    /// <summary>Sin tildes, en minúsculas y sin espacios de sobra (la normalización de <c>/ciudades</c>).</summary>
    public static string Normalizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return string.Empty;
        }

        var sinTildes = new string([.. texto.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)]);
        return string.Join(' ', sinTildes.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
