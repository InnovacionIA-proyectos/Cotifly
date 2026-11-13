namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Catálogos de la captura hacia Nexo que Nexo no publica en <c>GET /api/catalogos/*</c> (ver
/// <c>docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md</c>): carrocería (respaldo del vehículo digitado), tipología y la lista provisional de clases de contenedor que resuelve
/// <c>POST /api/captura-solicitudes</c>. Los códigos van tal cual a Nexo y se comparan exactos, distinguiendo mayúsculas (Nexo
/// resuelve el texto por coincidencia aproximada y crea un vehículo duplicado si no coincide). Los tipos de vehículo y las clases de viaje salen del catálogo
/// maestro de Nexo, no de aquí.
/// </summary>
public static class clsCatalogosCaptura
{
    /// <summary>Clase de viaje que exige <c>clase_contenedor</c> (obligatoria) y admite cualquier clase del catálogo.</summary>
    public const string ClaseViajeContenedor = "Contenedor";

    /// <summary>
    /// Clase de viaje de carga extradimensionada nacional. Admite <c>clase_contenedor</c> como OPCIONAL y solo una de
    /// las clases provisionales G15 (flat track / open top, <see cref="ClasesContenedorProvisionales"/>); otra clase es
    /// <c>VALOR_FUERA_DE_RANGO</c>.
    /// </summary>
    public const string ClaseViajeExtradimensionado = "Extradimensionado";

    /// <summary>
    /// Las clases de viaje de Nexo (<c>descripcion</c> de <c>GET /api/catalogos/clases-viaje</c>, el <c>valor</c> de
    /// <c>clases_viaje</c> en <c>GET /web/catalogos-flete</c>), con la escritura exacta de Nexo ("Carga Suelta" con espacio). La captura
    /// valida <c>clase_viaje</c> contra el catálogo vivo de Nexo; esta lista solo valida la configuración al arrancar
    /// (<c>Captura:ClasesSinVehiculoObligatorio</c>), que no puede consultar Nexo.
    /// </summary>
    public static readonly IReadOnlyList<string> ClasesViaje = [ClaseViajeContenedor, "Cupo", "Carga Suelta", ClaseViajeExtradimensionado];

    /// <summary>
    /// La clase de viaje de <see cref="ClasesViaje"/> que coincide con <paramref name="valor"/> recortado y sin distinguir
    /// mayúsculas, con la escritura de Nexo (<c>carga suelta</c> → <c>Carga Suelta</c>); <c>null</c> si no es ninguna. Solo para la
    /// configuración al arrancar: la captura sigue comparando exacto contra el catálogo vivo de Nexo.
    /// </summary>
    public static string? ClaseViajeNormalizada(string? valor) =>
        string.IsNullOrWhiteSpace(valor)
            ? null
            : ClasesViaje.FirstOrDefault(c => string.Equals(c, valor.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Clases de viaje en las que el vehículo elegido no es obligatorio si no se configura otra cosa: solo Contenedor.</summary>
    public static readonly IReadOnlyList<string> ClasesSinVehiculoPorDefecto = [ClaseViajeContenedor];

    /// <summary>Carrocerías de Nexo, en código hasta que Nexo publique su catálogo (pregunta 12 de <c>HU-013-preguntas-nexo.md</c>).</summary>
    public static readonly IReadOnlyList<string> Carrocerias =
        ["ESTACAS", "ESTIBAS", "FURGON", "FURGON REFRIGERADO", "PLATAFORMA", "PORTACONTENEDORES", "TANQUE", "TRAYLER", "VOLCO"];

    /// <summary>Enum <c>Tipologia</c> de Nexo (7 valores, solo a nivel de línea).</summary>
    public static readonly IReadOnlyList<string> Tipologias =
        ["Contenedor", "CargaSuelta", "GranelSolido", "GranelLiquido", "Alimentos", "Refrigerados", "CargaSeca"];

    /// <summary>
    /// No hay lista fija de clases de contenedor: se validan contra
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
