using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Rol interno de Cotifly y su rol externo de ClientePro (<c>dbo_cotifly.Roles</c>, script 0002, ADR-009).
/// <see cref="IdRolExterno"/> es el <c>IDROLES</c> de <c>tblSAPRoles</c> en la base de identidad del portal (QA: <c>dbSolucionesTI_Prueba</c>, #172); mientras sea <c>null</c>
/// ningún rol externo da acceso con este rol (fail-closed). El script siembra <c>comercial</c> y
/// <c>gerente_comercial</c> sin rol externo: los IDROLES reales los fija el DBA.
/// <para>
/// HU-040.3 (#258, ADR-009): <see cref="RequiereAltaManual"/> marca los roles SENSIBLES (por ejemplo <c>director</c>): el login NO los
/// auto-otorga aunque el rol externo los respalde (siguen con alta manual y auditoría), pero sí los auto-revoca si pierden su respaldo
/// (quitar privilegio es fail-safe). El flag es dato del catálogo (script 23), no una lista fija en el código.
/// </para>
/// </summary>
public sealed class clsRol
{
    public const string CodigoError = "ROL_INVALIDO";
    public const int LargoMaximoDescripcion = 200;

    private clsRol()
    {
    }

    public int Id { get; private set; }
    public RolInterno Codigo { get; private set; }
    public string? Descripcion { get; private set; }
    public int? IdRolExterno { get; private set; }
    public bool Activo { get; private set; }

    /// <summary>
    /// HU-040.3 (#258): rol SENSIBLE que NO se auto-otorga en el login (sigue con alta manual y auditoría); la auto-revocación sí aplica.
    /// Dato del catálogo (<c>dbo_cotifly.Roles.requiereAltaManual</c>, script 23), no una lista en el código. Lo marca el DBA (hoy <c>director</c>).
    /// </summary>
    public bool RequiereAltaManual { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static clsResultado<clsRol> Crear(RolInterno codigo, string? descripcion, int? idRolExterno = null, bool requiereAltaManual = false)
    {
        var error = clsValidacion.Primero(
            Enum.IsDefined(codigo) ? null : new clsErrorDominio(CodigoError, "Rol interno desconocido."),
            clsValidacion.LargoMaximo(CodigoError, "descripcion", descripcion, LargoMaximoDescripcion));

        if (error is not null)
        {
            return error;
        }

        return new clsRol
        {
            Codigo = codigo,
            Descripcion = clsValidacion.Normalizar(descripcion),
            IdRolExterno = idRolExterno,
            Activo = true,
            RequiereAltaManual = requiereAltaManual,
        };
    }

    /// <summary>Mapea (o desmapea con <c>null</c>) el rol externo de ClientePro.</summary>
    public void MapearRolExterno(int? idRolExterno) => IdRolExterno = idRolExterno;

    public void Activar() => Activo = true;

    public void Desactivar() => Activo = false;
}
