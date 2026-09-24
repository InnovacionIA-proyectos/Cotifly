using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Traducción de un rol externo de ClientePro a un rol interno de Cotifly
/// (<c>dbo_cotifly.RolesMapeo</c>, script 0002, ADR-009). Editable y activable; la semilla está
/// pendiente de negocio y del equipo ClientePro.
/// </summary>
public sealed class clsRolMapeo
{
    public const string CodigoError = "ROL_MAPEO_INVALIDO";
    public const int LargoMaximoRolExterno = 100;

    private clsRolMapeo()
    {
    }

    public int Id { get; private set; }
    public string RolExterno { get; private set; } = string.Empty;
    public RolInterno RolInterno { get; private set; }
    public bool Activo { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static clsResultado<clsRolMapeo> Crear(string rolExterno, RolInterno rolInterno)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "rolExterno", rolExterno, LargoMaximoRolExterno),
            Enum.IsDefined(rolInterno) ? null : new clsErrorDominio(CodigoError, "Rol interno desconocido."));

        if (error is not null)
        {
            return error;
        }

        return new clsRolMapeo
        {
            RolExterno = rolExterno.Trim(),
            RolInterno = rolInterno,
            Activo = true,
        };
    }

    public void CambiarRolInterno(RolInterno rolInterno)
    {
        if (!Enum.IsDefined(rolInterno))
        {
            throw new ArgumentOutOfRangeException(nameof(rolInterno), rolInterno, "Rol interno desconocido.");
        }

        RolInterno = rolInterno;
    }

    public void Activar() => Activo = true;

    public void Desactivar() => Activo = false;
}
