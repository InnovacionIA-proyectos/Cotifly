using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>
/// ADR-025: <c>POST /api/auth/elegir-rol</c>. El ticket (firma y vigencia) está simulado: aquí se prueba el uso único, la
/// revalidación contra ClientePro y las asignaciones, la emisión con el rol elegido y la auditoría de cada elección.
/// </summary>
public sealed class clsEleccionRolServiceTests
{
    private const string Ticket = "ticket-de-prueba";
    private readonly clsEscenarioIdentidad _e = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConUnTicketValidoEmiteLaSesionConElRolElegidoYElRefreshLoRecuerda()
    {
        var usuario = MultiRol();
        var jti = _e.TicketValido(Ticket, usuario, RolInterno.Comercial, RolInterno.GerenteComercial);
        clsTokenRevocado? consumido = null;
        _e.Revocados.When(r => r.Agregar(Arg.Any<clsTokenRevocado>())).Do(c => consumido = c.Arg<clsTokenRevocado>());
        clsTokenRefresco? refresh = null;
        _e.Tokens.When(t => t.Agregar(Arg.Any<clsTokenRefresco>())).Do(c => refresh = c.Arg<clsTokenRefresco>());

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "comercial"), Ct);

        var sesion = resultado.Valor;
        sesion.Usuario.Rol.Should().Be("comercial", "el token queda limitado al rol elegido aunque la jerarquía diga gerente");
        sesion.Access.Should().Be("jwt-Comercial");
        refresh!.RolSesion.Should().Be(RolInterno.Comercial);
        refresh.UsuarioId.Should().Be(usuario.Id);
        consumido!.Jti.Should().Be(clsEleccionRolService.PrefijoJti + jti);
        consumido.ExpiraAt.Should().Be(_e.Reloj.AhoraUtc.AddMinutes(5), "se guarda hasta que el ticket vence; luego se purga");
        usuario.UltimaSesion.Should().Be(_e.Reloj.AhoraUtc);
        await _e.UnidadTrabajo.Received(2).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        var salida = _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol);
        salida.GetProperty("resultado").GetString().Should().Be("aceptado");
        salida.GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RolElegido);
        salida.GetProperty("rol").GetString().Should().Be("comercial");
        _e.EntradaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("roles_ofrecidos").EnumerateArray()
            .Select(r => r.GetString()).Should().Equal("comercial", "gerente_comercial");
        _e.EventoAuditado(clsCodigosAcceso.HerramientaEleccionRol).Should().Be((true, "auth/JEFE"));
    }

    [Fact]
    public async Task ConsumeElTicketAntesDeVerificarNada()
    {
        var usuario = MultiRol();
        _e.TicketValido(Ticket, usuario, RolInterno.Comercial, RolInterno.GerenteComercial);
        var orden = new List<string>();
        _e.Revocados.When(r => r.Agregar(Arg.Any<clsTokenRevocado>())).Do(_ => orden.Add("consumo"));
        _e.Almacen.When(a => a.BuscarUsuarioAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(_ => orden.Add("verificacion"));

        await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "gerente_comercial"), Ct);

        orden.Should().Equal("consumo", "verificacion");
    }

    [Theory]
    [InlineData("ticket_vencido")]
    [InlineData("ticket_invalido")]
    public async Task UnTicketVencidoOInvalidoEs401SinTocarLaBase(string motivo)
    {
        _e.Tickets.ValidarAsync(Ticket).Returns(((clsTicketEleccionValido?)null, motivo));

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "comercial"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("motivo").GetString().Should().Be(motivo);
        _e.EventoAuditado(clsCodigosAcceso.HerramientaEleccionRol).Exito.Should().BeFalse();
        _e.Revocados.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _e.UnidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task UnTicketYaUsadoEsTicketReutilizadoYNoEmiteNada()
    {
        var usuario = MultiRol();
        _e.TicketValido(Ticket, usuario, RolInterno.Comercial, RolInterno.GerenteComercial);
        _e.UnidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(
            new clsViolacionUnicidadException("pk_tokensRevocados", new clsErrorDominio("REGISTRO_DUPLICADO", "Ya revocado."), null));

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "comercial"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.TicketReutilizado);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
        _e.Emisor.DidNotReceiveWithAnyArgs().EmitirAcceso(default, default!, default);
        await _e.Almacen.DidNotReceiveWithAnyArgs().BuscarUsuarioAsync(default!, Ct);
    }

    [Fact]
    public async Task UnRolQueNoSeOfrecioEnElTicketEsRolNoDisponible()
    {
        var usuario = MultiRol();
        _e.TicketValido(Ticket, usuario, RolInterno.Comercial, RolInterno.GerenteComercial);

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "director"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RolNoDisponible);
        _e.Revocados.ReceivedWithAnyArgs(1).Agregar(default!);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
    }

    [Fact]
    public async Task SiLeQuitaronElRolEnClienteProEntreElLoginYLaEleccionEsRolExternoCambio()
    {
        var usuario = MultiRol();
        _e.TicketValido(Ticket, usuario, RolInterno.Comercial, RolInterno.GerenteComercial);
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial);

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "gerente_comercial"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RolExternoCambio);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
    }

    [Fact]
    public async Task SiLeRetiraronLaAsignacionEntreElLoginYLaEleccionElRolYaNoEstaDisponible()
    {
        // Datos retiró la asignación de gerente: le queda comercial (rol único). Elegir gerente con el ticket viejo no pasa.
        var usuario = MultiRol();
        _e.TicketValido(Ticket, usuario, RolInterno.Comercial, RolInterno.GerenteComercial);
        _e.Roles.ListarRolesAsignadosAsync(usuario.Id, Arg.Any<CancellationToken>()).Returns([RolInterno.Comercial]);
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial);

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "gerente_comercial"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RolNoDisponible);
    }

    [Fact]
    public async Task UnTicketDeOtroUsuarioNoSirveParaEntrarComoElDelTicketSiElNombreNoCoincide()
    {
        // El ticket dice "JEFE" pero el id del ticket es de otra cuenta (p. ej. un ticket armado con un id ajeno): identidad inconsistente.
        var jefe = MultiRol();
        var otro = _e.UsuarioLocal("OTRO", 300, RolInterno.Comercial);
        _e.Tickets.ValidarAsync(Ticket).Returns(((clsTicketEleccionValido?)new clsTicketEleccionValido(
            otro.Id, jefe.NombreUsuario!, Guid.NewGuid().ToString("N"), _e.Reloj.AhoraUtc.AddMinutes(5), [RolInterno.Comercial, RolInterno.GerenteComercial]), (string?)null));

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "gerente_comercial"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.IdentidadInconsistente);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
    }

    [Theory]
    [InlineData(null, "comercial")]
    [InlineData("", "comercial")]
    [InlineData(Ticket, null)]
    [InlineData(Ticket, "")]
    [InlineData(Ticket, "administrador")]
    public async Task UnCuerpoInvalidoEsSolicitudInvalidaSinValidarElTicket(string? ticket, string? rol)
    {
        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(ticket, rol), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SolicitudInvalida);
        await _e.Tickets.DidNotReceiveWithAnyArgs().ValidarAsync(default!);
    }

    [Fact]
    public async Task UnFalloInesperadoEs401YQuedaEnElLog()
    {
        var usuario = MultiRol();
        _e.TicketValido(Ticket, usuario, RolInterno.Comercial, RolInterno.GerenteComercial);
        _e.Usuarios.ObtenerPorIdAsync(usuario.Id, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("base caída"));

        var resultado = await _e.Eleccion().ElegirAsync(new clsSolicitudElegirRol(Ticket, "comercial"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SesionExpirada);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaEleccionRol).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.ErrorInterno);
        _e.LoggerEleccion.Registros.Should().Contain(r => r.Nivel == LogLevel.Error);
    }

    /// <summary>JEFE (ID externo 200): comercial y gerente en ClientePro, con las dos asignaciones activas en Cotifly.</summary>
    private clsUsuarioWeb MultiRol()
    {
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente);
        return _e.UsuarioLocal("JEFE", 200, RolInterno.Comercial, RolInterno.GerenteComercial);
    }
}
