namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Cadenas de conexión (sección <c>ConnectionStrings</c>, variables <c>ConnectionStrings__Cotifly</c> y
/// <c>ConnectionStrings__Identidad</c>). Siempre SQL login (<c>cotifly_app</c>), nunca autenticación
/// integrada: el backend corre en un contenedor Linux (ADR-008).
/// </summary>
/// <remarks>
/// Fuera de Production pueden quedar vacías: la API arranca y <c>GET /health/ready</c> responde 503.
/// En Production, <see cref="clsValidadorSecretosOpciones{TOpciones}"/> exige que existan y
/// <see cref="clsValidadorOpcionesConexiones"/> rechaza la autenticación integrada y las contraseñas de ejemplo.
/// </remarks>
public sealed class clsOpcionesConexiones
{
    public const string Seccion = "ConnectionStrings";

    /// <summary>Lectura y escritura sobre <c>dbo_cotifly</c> en <c>dbTarifario_Nexo_Pruebas</c>.</summary>
    [clsSecreto(LargoMinimo = 20)]
    public string? Cotifly { get; set; }

    /// <summary>Solo lectura de <c>tblSAPUsuariosApp</c> en <c>dbsolucionesti</c> (login, #12).</summary>
    [clsSecreto(LargoMinimo = 20)]
    public string? Identidad { get; set; }

    /// <summary>
    /// Opcional: solo lectura de la tabla de ejecutivos con su regional en <c>DWCubosEBSSAP</c> (<c>DimEjecutivo</c>, HU-010.1 y
    /// #81, ADR-016): la usan el tablero del gerente y el login (que llena y refresca <c>UsuariosWeb.regional</c> sin bloquearse).
    /// Vacía: no se consulta y la regional sale de <c>UsuariosWeb.regional</c>. Con valor, se valida como las otras en Production.
    /// </summary>
    [clsSecreto(LargoMinimo = 20, Opcional = true)]
    public string? Regional { get; set; }
}
