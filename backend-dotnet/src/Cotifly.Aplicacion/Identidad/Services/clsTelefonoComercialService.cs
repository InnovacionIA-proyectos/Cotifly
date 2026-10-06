using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>Teléfono del comercial (<c>TelefonoComercial</c> de <c>specs/openapi.yaml</c>); <c>null</c> si nunca se capturó.</summary>
public sealed record clsTelefonoComercial(string? Telefono);

/// <summary>
/// Confirmación del teléfono antes de generar el PDF (HU-003.5, <c>GET/PUT /web/usuario/telefono</c>): el valor queda en
/// <c>UsuariosWeb.telefono</c> y es el que imprime la firma ("Cel") en los PDF siguientes. Errores:
/// <see cref="clsTelefonoComercialService.CodigoTelefonoInvalido"/> (el endpoint responde 400 con <c>ErrorWeb</c>) y
/// <see cref="clsTelefonoComercialService.CodigoUsuarioNoEncontrado"/>.
/// </summary>
public interface IClsTelefonoComercialService
{
    Task<clsResultado<clsTelefonoComercial>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Guarda el teléfono (patrón <c>^\+?[0-9 ]{7,20}$</c>, recortado); vacío o <c>null</c> lo borra.</summary>
    Task<clsResultado<clsTelefonoComercial>> GuardarAsync(Guid usuarioId, string? telefono, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsTelefonoComercialService"/>.</summary>
public sealed partial class clsTelefonoComercialService(IClsUsuarioWebRepository usuarios, IClsUnitOfWork unidadTrabajo) : IClsTelefonoComercialService
{
    public const string CodigoTelefonoInvalido = "TELEFONO_INVALIDO";
    public const string CodigoUsuarioNoEncontrado = "USUARIO_NO_ENCONTRADO";

    public async Task<clsResultado<clsTelefonoComercial>> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken = default)
    {
        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        return usuario is null ? NoEncontrado() : new clsTelefonoComercial(usuario.Telefono);
    }

    public async Task<clsResultado<clsTelefonoComercial>> GuardarAsync(Guid usuarioId, string? telefono, CancellationToken cancellationToken = default)
    {
        var valor = string.IsNullOrWhiteSpace(telefono) ? null : telefono.Trim();
        if (valor is not null && !ExpresionTelefono().IsMatch(valor))
        {
            return new clsErrorDominio(CodigoTelefonoInvalido, "El teléfono debe tener entre 7 y 20 dígitos o espacios, con '+' opcional al inicio.");
        }

        var usuario = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (usuario is null)
        {
            return NoEncontrado();
        }

        usuario.CambiarTelefono(valor);
        await unidadTrabajo.GuardarCambiosAsync(cancellationToken);
        return new clsTelefonoComercial(usuario.Telefono);
    }

    private static clsErrorDominio NoEncontrado() => new(CodigoUsuarioNoEncontrado, "No se encontró el comercial autenticado.");

    /// <summary>Patrón de <c>TelefonoComercial.telefono</c> en <c>specs/openapi.yaml</c>.</summary>
    [GeneratedRegex(@"^\+?[0-9 ]{7,20}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExpresionTelefono();
}
