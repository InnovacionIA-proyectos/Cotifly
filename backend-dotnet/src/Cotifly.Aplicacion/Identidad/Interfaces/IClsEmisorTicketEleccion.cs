using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Dominio.Identidad;

namespace Cotifly.Aplicacion.Identidad.Interfaces;

/// <summary>
/// HU-040.2 (#230, ADR-025): ticket de elección del rol. Lo emite el login cuando la persona verificada tiene varios roles, y es lo único
/// que acepta <c>POST /api/auth/elegir-rol</c>. Es un JWT HS256 firmado con una clave DERIVADA de la del access (otra clave, otra
/// audiencia y otro <c>typ</c>): no sirve como access token ni un access sirve como ticket. Vigencia corta (<see cref="Vigencia"/>); el
/// uso único lo controla el caso de uso (consume el <c>jti</c>).
/// </summary>
public interface IClsEmisorTicketEleccion
{
    /// <summary>Vigencia del ticket (5 minutos).</summary>
    TimeSpan Vigencia { get; }

    clsTicketEleccionEmitido Emitir(Guid usuarioId, string nombreUsuario, IReadOnlyList<RolInterno> roles);

    /// <summary>
    /// Valida firma, emisor, audiencia, tipo y vigencia (con el reloj de la aplicación). Devuelve el ticket o el motivo del rechazo
    /// (<c>ticket_vencido</c> o <c>ticket_invalido</c>). Nunca lanza por un ticket mal formado.
    /// </summary>
    Task<(clsTicketEleccionValido? Ticket, string? Motivo)> ValidarAsync(string ticket);
}
