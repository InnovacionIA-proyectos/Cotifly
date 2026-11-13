using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Ubicación de las tablas del almacén corporativo en la base de identidad del portal (QA: <c>dbSolucionesTI_Prueba</c>) (sección <c>Identidad</c>): usuarios, roles de
/// cada usuario y catálogo de roles de ClientePro. Configurable por si el DBA cambia el esquema
/// (ADR-008). Se leen solo las columnas que usa el login (nunca <c>CLAVE</c>).
/// </summary>
public sealed class clsOpcionesIdentidad
{
    public const string Seccion = "Identidad";
    private const string PatronIdentificador = "^[A-Za-z_][A-Za-z0-9_]*$";

    /// <summary>Esquema de las tres tablas. Por defecto <c>dbo</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string Esquema { get; set; } = "dbo";

    /// <summary>Usuarios (<c>ID</c>, <c>NOMBREUSUARIO</c>, <c>DESCRIPCIONUSUARIO</c>, <c>EMAIL</c>). Por defecto <c>tblSAPUsuariosApp</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string TablaUsuarios { get; set; } = "tblSAPUsuariosApp";

    /// <summary>Roles de cada usuario (<c>ID</c>, <c>IDUSUARIO</c>, <c>IDROLES</c>). Por defecto <c>tblSAPUsuarioRoles</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string TablaUsuarioRoles { get; set; } = "tblSAPUsuarioRoles";

    /// <summary>Catálogo de roles de ClientePro (<c>IDROLES</c>, <c>DESCRIPCION</c>). Por defecto <c>tblSAPRoles</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string TablaRoles { get; set; } = "tblSAPRoles";
}
