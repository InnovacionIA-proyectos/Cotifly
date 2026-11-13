namespace Cotifly.Aplicacion.Administracion.DTOs;

/// <summary>
/// Una línea de negocio activa para el desplegable de la captura (esquema
/// <c>LineaNegocioOpcion</c>; la Api la serializa como <c>linea_negocio</c> y <c>etiqueta</c>). <b>Sin margen ni método</b>: la política
/// de margen solo la ve la administración (<see cref="clsLineaNegocioAdmin"/>).
/// </summary>
/// <param name="LineaNegocio"><c>MargenesPolitica.lineaNegocio</c>, el valor que se envía en <c>RutaCapturaWeb.linea_negocio</c>.</param>
/// <param name="Etiqueta">Texto para mostrar (<see cref="EtiquetaDe"/>).</param>
public sealed record clsLineaNegocioOpcion(string LineaNegocio, string Etiqueta)
{
    /// <summary>Textos con tildes y espacios de las líneas sembradas (<c>db/seeder/01_lineas_negocio.sql</c>).</summary>
    private static readonly Dictionary<string, string> _etiquetas = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nacional"] = "Nacional",
        ["Distribucion"] = "Distribución",
        ["Importacion"] = "Importación",
        ["Exportacion"] = "Exportación",
        ["Internacional"] = "Internacional",
        ["DevolucionContenedor"] = "Devolución Contenedor",
    };

    /// <summary>La etiqueta conocida de <paramref name="lineaNegocio"/> (sin distinguir mayúsculas) o, si no la hay, el mismo nombre.</summary>
    public static string EtiquetaDe(string lineaNegocio)
    {
        ArgumentNullException.ThrowIfNull(lineaNegocio);
        var nombre = lineaNegocio.Trim();
        return _etiquetas.GetValueOrDefault(nombre) ?? nombre;
    }
}
