using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Identidad;

/// <summary>
/// Refresh token opaco (<c>dbo_cotifly.TokensRefresco</c>, script 0002, ADR-009). Solo se guarda el
/// SHA-256 en hexadecimal (64 caracteres, minúsculas). Todos los tokens de una misma cadena de
/// rotación comparten <see cref="FamiliaId"/>: si se reutiliza uno ya rotado se revoca la familia.
/// La rotación atómica (<c>UPDATE</c> condicional) la hace la persistencia.
/// <see cref="RolSesion"/> (script 16) guarda el rol interno con el que se emitió la sesión, para que el refresh lo conserve.
/// </summary>
public sealed class clsTokenRefresco
{
    public const string CodigoError = "TOKEN_REFRESCO_INVALIDO";
    public const int LargoHash = 64;

    private clsTokenRefresco()
    {
    }

    public Guid Id { get; private set; }
    public Guid UsuarioId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Guid FamiliaId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiraAt { get; private set; }
    public DateTimeOffset? RevocadoAt { get; private set; }
    public Guid? ReemplazadoPorId { get; private set; }

    /// <summary>
    /// Rol interno de la sesión (columna <c>rolSesion</c>, script 16). <c>null</c> en los refresh emitidos antes
    /// del 16. La rotación lo hereda.
    /// </summary>
    public RolInterno? RolSesion { get; private set; }

    /// <summary>
    /// Emite un token para el usuario. <paramref name="familiaId"/> nulo inicia una familia nueva (login).
    /// <paramref name="rolSesion"/> es el rol interno de la sesión.
    /// </summary>
    public static clsResultado<clsTokenRefresco> Emitir(
        Guid usuarioId, string tokenHash, DateTimeOffset ahora, TimeSpan vigencia, Guid? familiaId = null, RolInterno? rolSesion = null)
    {
        if (usuarioId == Guid.Empty)
        {
            return new clsErrorDominio(CodigoError, "El token debe pertenecer a un usuario.");
        }

        if (vigencia <= TimeSpan.Zero)
        {
            return new clsErrorDominio(CodigoError, "La vigencia del token debe ser positiva.");
        }

        if (!EsHashValido(tokenHash))
        {
            return new clsErrorDominio(CodigoError, "El hash debe ser un SHA-256 en hexadecimal (64 caracteres).");
        }

        return new clsTokenRefresco
        {
            Id = Guid.NewGuid(),
            UsuarioId = usuarioId,
            TokenHash = tokenHash.ToLowerInvariant(),
            FamiliaId = familiaId ?? Guid.NewGuid(),
            CreatedAt = ahora,
            ExpiraAt = ahora + vigencia,
            RolSesion = rolSesion,
        };
    }

    public bool EstaVigente(DateTimeOffset ahora) => RevocadoAt is null && ReemplazadoPorId is null && ahora < ExpiraAt;

    /// <summary>Marca el token como rotado por <paramref name="nuevoId"/>.</summary>
    public void MarcarReemplazado(Guid nuevoId, DateTimeOffset ahora)
    {
        ReemplazadoPorId = nuevoId;
        RevocadoAt ??= ahora;
    }

    public void Revocar(DateTimeOffset ahora) => RevocadoAt ??= ahora;

    public static bool EsHashValido(string? hash) =>
        hash is { Length: LargoHash } && hash.All(char.IsAsciiHexDigit);
}
