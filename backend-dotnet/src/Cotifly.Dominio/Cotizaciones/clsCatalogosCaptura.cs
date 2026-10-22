namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Catálogos de la captura hacia Nexo que Nexo no publica en <c>GET /api/catalogos/*</c> (HU-013.10, #106,
/// <c>docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md</c>): carrocería (respaldo del vehículo digitado), tipología y la lista provisional de clases de contenedor que resuelve
/// <c>POST /api/captura-solicitudes</c>. Los códigos van tal cual a Nexo y se comparan exactos, distinguiendo mayúsculas (Nexo
/// resuelve el texto por coincidencia aproximada y crea un vehículo duplicado si no coincide). Los tipos de vehículo y las clases de viaje salen del catálogo
/// maestro de Nexo, no de aquí.
/// </summary>
public static class clsCatalogosCaptura
{
    /// <summary>Clase de viaje que exige <c>clase_contenedor</c> (y la única que la admite).</summary>
    public const string ClaseViajeContenedor = "Contenedor";

    /// <summary>
    /// HU-032 (#197): las clases de viaje de Nexo (<c>descripcion</c> de <c>GET /api/catalogos/clases-viaje</c>, el <c>valor</c> de
    /// <c>clases_viaje</c> en <c>GET /web/catalogos-flete</c>), con la escritura exacta de Nexo ("Carga Suelta" con espacio). La captura
    /// valida <c>clase_viaje</c> contra el catálogo vivo de Nexo; esta lista solo valida la configuración al arrancar
    /// (<c>Captura:ClasesSinVehiculoObligatorio</c>), que no puede consultar Nexo.
    /// </summary>
    public static readonly IReadOnlyList<string> ClasesViaje = [ClaseViajeContenedor, "Cupo", "Carga Suelta", "Extradimensionado"];

    /// <summary>HU-032 (#197): clases de viaje en las que el vehículo elegido no es obligatorio si no se configura otra cosa: solo Contenedor.</summary>
    public static readonly IReadOnlyList<string> ClasesSinVehiculoPorDefecto = [ClaseViajeContenedor];

    /// <summary>Carrocerías de Nexo, en código hasta que Nexo publique su catálogo (pregunta 12 de <c>HU-013-preguntas-nexo.md</c>).</summary>
    public static readonly IReadOnlyList<string> Carrocerias =
        ["ESTACAS", "ESTIBAS", "FURGON", "FURGON REFRIGERADO", "PLATAFORMA", "PORTACONTENEDORES", "TANQUE", "TRAYLER", "VOLCO"];

    /// <summary>Enum <c>Tipologia</c> de Nexo (7 valores, solo a nivel de línea).</summary>
    public static readonly IReadOnlyList<string> Tipologias =
        ["Contenedor", "CargaSuelta", "GranelSolido", "GranelLiquido", "Alimentos", "Refrigerados", "CargaSeca"];

    /// <summary>
    /// HU-023.1 (#156; lo que le quedaba a HU-015.1): ya no hay lista fija de clases de contenedor. Se validan contra
    /// <c>GET /api/catalogos/clases-contenedor</c> de Nexo (<c>clsValidadorCatalogosCaptura</c>), más esta lista provisional.
    /// </summary>
    /// <remarks>
    /// Lista provisional de Nexo para extradimensionado nacional (G15), marcada como provisional en el código de Nexo y fuera del
    /// catálogo maestro: se acepta hasta que Nexo la formalice.
    /// </remarks>
    public static readonly IReadOnlyList<string> ClasesContenedorProvisionales = ["CNT20.FT", "CNT20.OP", "CNT40.FT", "CNT40.OP"];

    /// <summary>¿<paramref name="valor"/> es exactamente uno de <paramref name="catalogo"/>? (comparación ordinal).</summary>
    public static bool Contiene(IEnumerable<string> catalogo, string valor)
    {
        ArgumentNullException.ThrowIfNull(catalogo);
        return catalogo.Contains(valor, StringComparer.Ordinal);
    }
}
