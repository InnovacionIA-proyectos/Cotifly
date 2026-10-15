namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Ajustes de la base <c>dbo_cotifly</c> que no son la cadena de conexión (sección <c>BaseDatos</c>, HU-023 #176).
/// </summary>
public sealed class clsOpcionesBaseDatos
{
    public const string Seccion = "BaseDatos";

    /// <summary>Detecta el script 07 leyendo el CHECK del motivo en <c>sys.check_constraints</c> (por defecto).</summary>
    public const string ModoAuto = "auto";

    /// <summary>Asume el script 07 aplicado sin consultarlo: <c>BUSQUEDA_AGOTADA</c> se guarda tal cual.</summary>
    public const string ModoHabilitado = "habilitado";

    /// <summary>Asume que el 07 NO está aplicado: <c>BUSQUEDA_AGOTADA</c> se guarda en NULL con el aviso de la ruta.</summary>
    public const string ModoDeshabilitado = "deshabilitado";

    /// <summary>Valores admitidos en <see cref="MotivoBusquedaAgotada"/>.</summary>
    public static IReadOnlyList<string> Modos { get; } = [ModoAuto, ModoHabilitado, ModoDeshabilitado];

    /// <summary>
    /// ¿La base admite el motivo <c>BUSQUEDA_AGOTADA</c> (script <c>db/scripts/07_motivo_busqueda_agotada.sql</c>)?
    /// <see cref="ModoAuto"/> (por defecto) lo detecta; si el login no puede leer la definición del CHECK (le falta
    /// <c>VIEW DEFINITION</c>) no se puede verificar y se guarda en NULL con un aviso. <see cref="ModoHabilitado"/> y
    /// <see cref="ModoDeshabilitado"/> lo fijan sin consultar (variable <c>BaseDatos__MotivoBusquedaAgotada</c>, sin distinguir mayúsculas).
    /// Con <see cref="ModoHabilitado"/> y la base sin el 07, guardar una ruta con <c>BUSQUEDA_AGOTADA</c> falla: úselo solo con el 07 aplicado.
    /// </summary>
    public string MotivoBusquedaAgotada { get; set; } = ModoAuto;

    /// <summary><see cref="MotivoBusquedaAgotada"/> sin espacios y en minúsculas.</summary>
    public string MotivoBusquedaAgotadaNormalizado => (MotivoBusquedaAgotada ?? string.Empty).Trim().ToLowerInvariant();
}
