using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Comercial de Cotifly (<c>dbo_cotifly.UsuariosWeb</c>). <see cref="Id"/> es un UUID generado por
/// Cotifly (no el id de SAP); la clave real es <see cref="NombreUsuario"/> (índice único filtrado).
/// El rol no se persiste aquí (ADR-009).
/// </summary>
public sealed class clsUsuarioWeb
{
    public const string CodigoError = "USUARIO_INVALIDO";
    public const int LargoMaximoNombreUsuario = 255;

    private clsUsuarioWeb()
    {
    }

    public Guid Id { get; private set; }
    public string? NombreUsuario { get; private set; }
    public string? NombreCompleto { get; private set; }
    public string? Email { get; private set; }
    public string? Telefono { get; private set; }
    public string? Regional { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UltimaSesion { get; private set; }

    /// <summary>Alta JIT del comercial a partir de su fila en <c>tblSAPUsuariosApp</c> (HU-003.6).</summary>
    public static clsResultado<clsUsuarioWeb> Crear(
        string nombreUsuario, string? nombreCompleto, string? email, string? regional, DateTimeOffset ahora)
    {
        var error = clsValidacion.Requerido(CodigoError, "nombreUsuario", nombreUsuario, LargoMaximoNombreUsuario);
        if (error is not null)
        {
            return error;
        }

        return new clsUsuarioWeb
        {
            Id = Guid.NewGuid(),
            NombreUsuario = nombreUsuario.Trim(),
            NombreCompleto = clsValidacion.Normalizar(nombreCompleto),
            Email = clsValidacion.Normalizar(email),
            Regional = clsValidacion.Normalizar(regional),
            UltimaSesion = ahora,
        };
    }

    public void RegistrarSesion(DateTimeOffset ahora) => UltimaSesion = ahora;

    /// <summary>Actualiza los datos corporativos; los valores vacíos no borran lo existente.</summary>
    public void ActualizarDatos(string? nombreCompleto, string? email, string? regional)
    {
        NombreCompleto = clsValidacion.Normalizar(nombreCompleto) ?? NombreCompleto;
        Email = clsValidacion.Normalizar(email) ?? Email;
        Regional = clsValidacion.Normalizar(regional) ?? Regional;
    }

    /// <summary>Teléfono de contacto del comercial (<c>PUT /web/usuario/telefono</c>); vacío lo borra.</summary>
    public void CambiarTelefono(string? telefono) => Telefono = clsValidacion.Normalizar(telefono);
}
