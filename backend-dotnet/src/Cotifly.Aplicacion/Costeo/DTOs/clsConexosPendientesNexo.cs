namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Conexos agregados en Nexo que Cotifly no pudo leer (POST con efecto incierto o sin relectura; HU-013.15, #124; corregido en HU-021.8,
/// #135). Se guarda en la MISMA transacción que la marca <c>ConexosPendientesDeLeer</c>:
/// <list type="bullet">
/// <item><see cref="LineaSolicitudId"/>: la ruta a la que se pidieron (la siguiente lectura los trae a ESA ruta, no a la ruta 1);</item>
/// <item><see cref="Previos"/>: los ids de los conexos que la cotización de Nexo YA tenía justo antes del POST. La lectura importa solo
/// los que no están aquí ni en ninguna ruta: un conexo que el comercial quitó en Cotifly (Nexo no tiene DELETE y lo conserva) nunca
/// vuelve a aparecer, y uno de otra ruta no se duplica.</item>
/// </list>
/// Sin este registro la lectura no importa nada (lo seguro: nunca reimporta conexos quitados).
/// </summary>
public sealed record clsConexosPendientesNexo(string Folio, Guid LineaSolicitudId, IReadOnlyList<string> Previos)
{
    /// <summary>Herramienta con que se guarda en <c>EventosHerramienta</c> (sin columna nueva en el esquema).</summary>
    public const string Herramienta = "conexos_pendientes_nexo";
}
