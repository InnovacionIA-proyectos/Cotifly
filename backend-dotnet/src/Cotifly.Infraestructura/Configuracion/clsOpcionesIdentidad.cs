using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Ubicación de la tabla de usuarios corporativos en <c>dbsolucionesti</c> (sección <c>Identidad</c>).
/// Configurable porque el esquema real está pendiente de confirmar con el DBA (ADR-008).
/// </summary>
public sealed class clsOpcionesIdentidad
{
    public const string Seccion = "Identidad";
    private const string PatronIdentificador = "^[A-Za-z_][A-Za-z0-9_]*$";

    /// <summary>Esquema de la tabla. Por defecto <c>dbo</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string Esquema { get; set; } = "dbo";

    /// <summary>Nombre de la tabla. Por defecto <c>tblSAPUsuariosApp</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string Tabla { get; set; } = "tblSAPUsuariosApp";
}
