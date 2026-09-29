using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Ubicación de la tabla de ejecutivos con su regional (sección <c>Regional</c>; conexión <c>ConnectionStrings:Regional</c>,
/// base <c>DWCubosEBSSAP</c>). Configurable porque el esquema real está pendiente de confirmar con el DBA (ADR-016 §6). Sin
/// conexión no se consulta nada y la regional sale de <c>UsuariosWeb.regional</c>.
/// </summary>
public sealed class clsOpcionesRegional
{
    public const string Seccion = "Regional";
    private const string PatronIdentificador = "^[A-Za-z_][A-Za-z0-9_]*$";

    /// <summary>Esquema de la tabla. Por defecto <c>dbo</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string Esquema { get; set; } = "dbo";

    /// <summary>Tabla de ejecutivos. Por defecto <c>DimEjecutivo</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string Tabla { get; set; } = "DimEjecutivo";

    /// <summary>Columna que coincide con <c>UsuariosWeb.nombreUsuario</c> (el <c>NOMBREUSUARIO</c> de ClientePro). Por defecto <c>ID</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string ColumnaUsuario { get; set; } = "ID";

    /// <summary>Columna con la regional. Por defecto <c>REGIONAL</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string ColumnaRegional { get; set; } = "REGIONAL";

    /// <summary>Límite de cada consulta en segundos (2 a 60). Por defecto 10.</summary>
    [Range(2, 60)]
    public int TiempoEsperaSegundos { get; set; } = 10;
}
