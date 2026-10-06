using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Identidad;

/// <summary>
/// <c>jti</c> de un access token revocado en logout (<c>dbo_cotifly.TokensRevocados</c>, script 0002,
/// ADR-009). Se conserva hasta <see cref="ExpiraAt"/> (vencimiento del JWT) y luego se purga.
/// </summary>
public sealed class clsTokenRevocado
{
    public const string CodigoError = "TOKEN_REVOCADO_INVALIDO";
    public const int LargoMaximoJti = 64;

    private clsTokenRevocado()
    {
    }

    public string Jti { get; private set; } = string.Empty;
    public Guid? UsuarioId { get; private set; }
    public DateTimeOffset ExpiraAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static clsResultado<clsTokenRevocado> Crear(string jti, DateTimeOffset expiraAt, Guid? usuarioId = null)
    {
        var error = clsValidacion.Requerido(CodigoError, "jti", jti, LargoMaximoJti);
        if (error is not null)
        {
            return error;
        }

        return new clsTokenRevocado
        {
            Jti = jti.Trim(),
            ExpiraAt = expiraAt,
            UsuarioId = usuarioId,
        };
    }

    public bool PuedePurgarse(DateTimeOffset ahora) => ahora >= ExpiraAt;
}
