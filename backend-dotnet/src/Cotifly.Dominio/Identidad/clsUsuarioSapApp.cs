namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Fila de solo lectura de <c>dbsolucionesti.tblSAPUsuariosApp</c> (almacén corporativo de usuarios,
/// ADR-008/ADR-009). Solo expone <c>ID</c>, <c>NOMBREUSUARIO</c>, <c>DESCRIPCIONUSUARIO</c> y <c>EMAIL</c>:
/// la columna <c>CLAVE</c> nunca se mapea.
/// </summary>
public sealed class clsUsuarioSapApp
{
    private clsUsuarioSapApp()
    {
    }

    public int Id { get; private set; }
    public string? NombreUsuario { get; private set; }
    public string? DescripcionUsuario { get; private set; }
    public string? Email { get; private set; }
}
