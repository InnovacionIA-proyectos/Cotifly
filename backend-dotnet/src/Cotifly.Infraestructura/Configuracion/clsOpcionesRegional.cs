using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Ubicación de la tabla de ejecutivos con su regional (sección <c>Regional</c>; conexión <c>ConnectionStrings:Regional</c>,
/// base <c>DWCubosEBSSAP</c>, tabla <c>dbo.DimEjecutivo</c>, verificada en #81). Sigue siendo configurable por si el DBA mueve la
/// tabla o renombra columnas (ADR-016 §6). Sin conexión no se consulta nada y la regional sale de <c>UsuariosWeb.regional</c>.
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

    /// <summary>
    /// Columna que coincide con <c>UsuariosWeb.nombreUsuario</c> (el <c>NOMBREUSUARIO</c> de ClientePro). Por defecto <c>USUARIO</c>,
    /// que viene con el dominio pegado (<c>boterosoto\usuario</c>): el adaptador quita todo hasta la primera <c>\</c> antes de
    /// comparar (#81). Un valor sin <c>\</c> se compara tal cual.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string ColumnaUsuario { get; set; } = "USUARIO";

    /// <summary>Columna con la regional. Por defecto <c>REGIONAL</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression(PatronIdentificador)]
    public string ColumnaRegional { get; set; } = "REGIONAL";

    /// <summary>Límite de cada consulta en segundos (2 a 60). Por defecto 10.</summary>
    [Range(2, 60)]
    public int TiempoEsperaSegundos { get; set; } = 10;

    /// <summary>
    /// Límite total (conexión y consulta) de la búsqueda de la regional durante el login, en segundos (1 a 10; por defecto 3). Se
    /// usa el menor entre este valor y <see cref="TiempoEsperaSegundos"/>. Si se agota, el login sigue sin refrescar la regional (#81).
    /// </summary>
    [Range(1, 10)]
    public int TiempoEsperaLoginSegundos { get; set; } = 3;

    /// <summary>Límite efectivo del login: el menor entre <see cref="TiempoEsperaLoginSegundos"/> y <see cref="TiempoEsperaSegundos"/>.</summary>
    public TimeSpan LimiteLogin => TimeSpan.FromSeconds(Math.Min(TiempoEsperaLoginSegundos, TiempoEsperaSegundos));
}
