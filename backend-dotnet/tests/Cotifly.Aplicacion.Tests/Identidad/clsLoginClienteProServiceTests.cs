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
/// Login SSO ClientePro con verificación doble (ADR-009): cada rama del flujo, el 401 genérico, el alta JIT, la regla de
/// precedencia de roles y la auditoría de acceso. Los puertos son simulados.
/// </summary>
public sealed class clsLoginClienteProServiceTests
{
    private readonly clsEscenarioIdentidad _e = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SinIdUsuarioEsSolicitudInvalidaSinConsultarElAlmacen(string? idUsuario)
    {
        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro(idUsuario), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.SolicitudInvalida);
        await _e.Almacen.DidNotReceiveWithAnyArgs().BuscarUsuarioAsync(default!, Ct);
    }

    [Fact]
    public async Task UnIdUsuarioDeMasDeCienCaracteresOUnNombreDeMasDeDoscientosEsSolicitudInvalida()
    {
        var largo = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro(new string('u', 101)), Ct);
        var nombreLargo = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ", new string('n', 201)), Ct);

        largo.Error.Codigo.Should().Be(clsCodigosAcceso.SolicitudInvalida);
        nombreLargo.Error.Codigo.Should().Be(clsCodigosAcceso.SolicitudInvalida);
    }

    [Fact]
    public async Task ElPrimerIngresoCreaElUsuarioConLosDatosDelAlmacenYLeAsignaElRol()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        clsUsuarioWeb? creado = null;
        clsUsuarioRol? asignado = null;
        clsTokenRefresco? refresh = null;
        _e.Usuarios.When(u => u.Agregar(Arg.Any<clsUsuarioWeb>())).Do(c => creado = c.Arg<clsUsuarioWeb>());
        _e.Roles.When(r => r.AgregarAsignacion(Arg.Any<clsUsuarioRol>())).Do(c => asignado = c.Arg<clsUsuarioRol>());
        _e.Tokens.When(t => t.Agregar(Arg.Any<clsTokenRefresco>())).Do(c => refresh = c.Arg<clsTokenRefresco>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ", "Ana (informativo)"), Ct);

        var sesion = resultado.Valor;
        creado!.NombreUsuario.Should().Be("APEREZ");
        creado.NombreCompleto.Should().Be("Nombre de APEREZ");
        creado.Email.Should().Be("aperez@boterosoto.com.co");
        creado.IdUsuarioExterno.Should().Be(101);
        creado.Regional.Should().BeNull();
        asignado!.UsuarioId.Should().Be(creado.Id);
        asignado.RolId.Should().Be(_e.Comercial.Id);
        asignado.Activo.Should().BeTrue();
        refresh!.UsuarioId.Should().Be(creado.Id);
        refresh.TokenHash.Should().Be(clsTokenOpaco.Hash(sesion.Refresh));
        refresh.ExpiraAt.Should().Be(_e.Reloj.AhoraUtc.AddDays(30));
        sesion.Access.Should().Be("jwt-Comercial");
        sesion.Usuario.Should().Be(new clsUsuarioSesion(creado.Id, "APEREZ", "aperez@boterosoto.com.co", "Nombre de APEREZ", "comercial", null));
        await _e.UnidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        _e.Emisor.Received(1).EmitirAcceso(creado.Id, "APEREZ", RolInterno.Comercial);
    }

    [Fact]
    public async Task ElUsuarioSeUbicaConElNombreDelAlmacenNuncaConElDelRequest()
    {
        // ClientePro manda el ID numérico: el almacén resuelve el NOMBREUSUARIO y ese es el que se usa en Cotifly.
        var persona = _e.PersonaExterna("LGOMEZ", 102, clsEscenarioIdentidad.RolExternoComercial);
        _e.Almacen.BuscarUsuarioAsync("102", Arg.Any<CancellationToken>()).Returns(persona);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("102", "OTRO NOMBRE"), Ct);

        resultado.Valor.Usuario.Username.Should().Be("LGOMEZ");
        await _e.Usuarios.Received(1).ObtenerPorNombreUsuarioAsync("LGOMEZ", Arg.Any<CancellationToken>());
        await _e.Usuarios.DidNotReceive().ObtenerPorNombreUsuarioAsync("OTRO NOMBRE", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnaPersonaQueNoExisteEnElAlmacenRecibeElMismo401Generico()
    {
        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("NADIE"), Ct);

        resultado.Error.Should().Be(new clsErrorDominio(clsCodigosAcceso.CredencialesInvalidas, "Credenciales inválidas"));
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.UsuarioNoEncontrado);
        _e.EventoAuditado(clsCodigosAcceso.HerramientaLogin).Should().Be((false, "auth/NADIE"));
        await _e.UnidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task ConElAlmacenCaidoFallaCerradoConElMismo401YUnWarning()
    {
        _e.Almacen.BuscarUsuarioAsync("APEREZ", Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("sin respuesta"));

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.AlmacenNoDisponible);
        _e.LoggerVerificador.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("TimeoutException"));
        _e.Usuarios.DidNotReceiveWithAnyArgs().Agregar(default!);
    }

    [Fact]
    public async Task SiFallaLaLecturaDeRolesExternosTambienFallaCerrado()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.Almacen.ObtenerRolesAsync(101, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("permiso denegado"));

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.AlmacenNoDisponible);
    }

    [Fact]
    public async Task SinUnRolExternoMapeadoNoSeEntraNiSeCreaElUsuario()
    {
        _e.PersonaExterna("CONSULTA", 150, clsEscenarioIdentidad.RolExternoSinMapeo);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("CONSULTA"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.SinRolAutorizado);
        _e.Usuarios.DidNotReceiveWithAnyArgs().Agregar(default!);
    }

    [Fact]
    public async Task MientrasLosRolesInternosNoTenganRolExternoNadieEntra()
    {
        // Semilla del 0002: idRolExterno NULL hasta que el DBA fije los IDROLES reales.
        _e.Comercial.MapearRolExterno(null);
        _e.Gerente.MapearRolExterno(null);
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
    }

    [Fact]
    public async Task LosRolesDelBodyNuncaDecidenElRol()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);

        var resultado = await _e.Login().IniciarSesionAsync(
            new clsSolicitudLoginClientePro("APEREZ", null, ["35", "gerente_comercial"]), Ct);

        resultado.Valor.Usuario.Rol.Should().Be("comercial");
        var entrada = _e.EntradaAuditada(clsCodigosAcceso.HerramientaLogin);
        entrada.GetProperty("roles_informados").EnumerateArray().Select(r => r.GetString()).Should().Equal("35", "gerente_comercial");
    }

    [Fact]
    public async Task ConLosDosRolesExternosGanaGerenteComercialYLaReglaQuedaAuditada()
    {
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente);
        clsUsuarioRol? asignado = null;
        _e.Roles.When(r => r.AgregarAsignacion(Arg.Any<clsUsuarioRol>())).Do(c => asignado = c.Arg<clsUsuarioRol>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Valor.Usuario.Rol.Should().Be("gerente_comercial");
        asignado!.RolId.Should().Be(_e.Gerente.Id);
        var salida = _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin);
        salida.GetProperty("regla").GetString().Should().Be(clsMotivosAcceso.ReglaPrecedenciaGerente);
        salida.GetProperty("resultado").GetString().Should().Be("aceptado");
        salida.GetProperty("rol").GetString().Should().Be("gerente_comercial");
    }

    [Fact]
    public async Task UnSegundoIngresoConElMismoRolNoCreaNadaYActualizaLaUltimaSesion()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Valor.Usuario.Id.Should().Be(usuario.Id);
        resultado.Valor.Usuario.Ubicacion.Should().Be("Antioquia");
        usuario.UltimaSesion.Should().Be(_e.Reloj.AhoraUtc);
        _e.Usuarios.DidNotReceiveWithAnyArgs().Agregar(default!);
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.UsuarioExistente);
        _e.EventoAuditado(clsCodigosAcceso.HerramientaLogin).Exito.Should().BeTrue();
    }

    [Fact]
    public async Task SiElRolExternoCambioSeRechazaSinResincronizarLaAsignacion()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoGerente);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        var sesionAnterior = usuario.UltimaSesion;

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RolExternoCambio);
        usuario.UltimaSesion.Should().Be(sesionAnterior);
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _e.UnidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task SinAsignacionActivaSeRechaza()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.UsuarioLocal("APEREZ", 101);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.SinAsignacionActiva);
    }

    [Fact]
    public async Task SiElNombreUsuarioCambioEnElAlmacenNoSeCreaOtraCuentaYSeRechaza()
    {
        // La cuenta existente está vinculada al ID 101 con el nombre anterior; el almacén ahora dice "APEREZ2".
        _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.PersonaExterna("APEREZ2", 101, clsEscenarioIdentidad.RolExternoComercial);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ2"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.NombreUsuarioCambio);
        _e.Usuarios.DidNotReceiveWithAnyArgs().Agregar(default!);
    }

    [Fact]
    public async Task SiElNombrePertenecioAOtraPersonaDelAlmacenEsIdentidadInconsistente()
    {
        _e.UsuarioLocal("APEREZ", 999, RolInterno.Comercial);
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.IdentidadInconsistente);
    }

    [Fact]
    public async Task UnaCuentaSinVinculoSeVinculaEnSuPrimerLoginVerificado()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", null, RolInterno.Comercial);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.EsExitoso.Should().BeTrue();
        usuario.IdUsuarioExterno.Should().Be(101);
    }

    [Fact]
    public async Task UnAltaConcurrenteDeLaMismaPersonaResponde401SinLanzar()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.UnidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(
            new clsViolacionUnicidadException("uq_usuariosWeb_nombreUsuario", new clsErrorDominio("REGISTRO_DUPLICADO", "Ya existe."), null));

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.AltaConcurrente);
    }

    [Fact]
    public async Task UnFalloInesperadoDeLaBaseDeCotiflyTambienResponde401YQuedaEnElLog()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.Usuarios.ObtenerPorNombreUsuarioAsync("APEREZ", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("base caída"));

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.ErrorInterno);
        _e.LoggerLogin.Registros.Should().Contain(r => r.Nivel == LogLevel.Error && r.Excepcion is InvalidOperationException);
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorSePropaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();
        _e.Almacen.BuscarUsuarioAsync("APEREZ", Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException(cancelacion.Token));

        var iniciar = () => _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), cancelacion.Token);

        await iniciar.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void LaReglaDeRolesEsExplicitaMapeoUnicoOPrecedenciaDelGerente()
    {
        var catalogo = _e.Catalogo;

        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(34, null)]).Should().Be((RolInterno.Comercial, clsMotivosAcceso.ReglaMapeoUnico));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(35, null)]).Should().Be((RolInterno.GerenteComercial, clsMotivosAcceso.ReglaMapeoUnico));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(35, null), new clsRolExterno(34, null)])
            .Should().Be((RolInterno.GerenteComercial, clsMotivosAcceso.ReglaPrecedenciaGerente));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(40, null)]).Should().Be(((RolInterno?)null, (string?)null));
        clsVerificadorIdentidad.ResolverRol(catalogo, []).Rol.Should().BeNull();
    }

    [Fact]
    public void UnRolInternoInactivoNoDaAcceso()
    {
        _e.Gerente.Desactivar();

        clsVerificadorIdentidad.ResolverRol(_e.Catalogo, [new clsRolExterno(35, null)]).Rol.Should().BeNull();
    }
}
