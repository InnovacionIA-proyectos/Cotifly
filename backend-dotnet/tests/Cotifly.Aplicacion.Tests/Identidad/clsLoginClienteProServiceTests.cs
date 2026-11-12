using System.Text.Json;
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

        var sesion = resultado.Valor.Sesion!;
        resultado.Valor.Eleccion.Should().BeNull("con un solo rol no hay selector");
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

        resultado.Valor.Sesion!.Usuario.Username.Should().Be("LGOMEZ");
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
            new clsSolicitudLoginClientePro("APEREZ", null, ["31", "gerente_comercial"]), Ct);

        resultado.Valor.Sesion!.Usuario.Rol.Should().Be("comercial");
        var entrada = _e.EntradaAuditada(clsCodigosAcceso.HerramientaLogin);
        entrada.GetProperty("roles_informados").EnumerateArray().Select(r => r.GetString()).Should().Equal("31", "gerente_comercial");
    }

    [Fact]
    public async Task HU0402_UnAltaConDosRolesExternosAsignaAmbosYPideElegirSinEmitirSesion()
    {
        // HU-040.2 (#230, ADR-025): ya no gana gerente_comercial: el alta asigna los dos roles y el login devuelve el selector.
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente);
        var asignados = new List<clsUsuarioRol>();
        _e.Roles.When(r => r.AgregarAsignacion(Arg.Any<clsUsuarioRol>())).Do(c => asignados.Add(c.Arg<clsUsuarioRol>()));
        clsUsuarioWeb? creado = null;
        _e.Usuarios.When(u => u.Agregar(Arg.Any<clsUsuarioWeb>())).Do(c => creado = c.Arg<clsUsuarioWeb>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Valor.Sesion.Should().BeNull("con varios roles no hay sesión hasta elegir");
        var eleccion = resultado.Valor.Eleccion!;
        eleccion.Roles.Select(r => r.Codigo).Should().Equal("comercial", "gerente_comercial");
        eleccion.Roles[1].Descripcion.Should().Be("Gerente comercial");
        eleccion.Ticket.Should().Be($"ticket-{creado!.Id:N}-Comercial+GerenteComercial");
        eleccion.ExpiraAt.Should().Be(_e.Reloj.AhoraUtc.AddMinutes(5));
        eleccion.UsuarioId.Should().Be(creado.Id);
        asignados.Select(a => a.RolId).Should().BeEquivalentTo([_e.Comercial.Id, _e.Gerente.Id]);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
        _e.Emisor.DidNotReceiveWithAnyArgs().EmitirAcceso(default, default!, default);
        await _e.UnidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        var salida = _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin);
        salida.GetProperty("resultado").GetString().Should().Be("eleccion_pendiente");
        salida.GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.EleccionRolPendiente);
        salida.GetProperty("regla").GetString().Should().Be(clsMotivosAcceso.ReglaMultiRol);
        salida.GetProperty("roles_disponibles").EnumerateArray().Select(r => r.GetString()).Should().Equal("comercial", "gerente_comercial");
        salida.GetProperty("usuario_id").GetGuid().Should().Be(creado.Id);
        _e.EventoAuditado(clsCodigosAcceso.HerramientaLogin).Exito.Should().BeTrue();
    }

    [Fact]
    public async Task HU0403_SiElAlmacenFallaParaUnUsuarioExistenteElLoginSeRechazaYNoReconcilia()
    {
        // HU-040.3 (#258, B+): la reconciliación solo ocurre sobre una lectura EXITOSA del almacén. Si falla, se rechaza el login entero
        // (almacen_no_disponible) y NUNCA se revoca ni se otorga por una lectura parcial (fail-closed).
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente);
        var usuario = _e.UsuarioLocal("JEFE", 200, RolInterno.Comercial, RolInterno.GerenteComercial);
        var filas = _e.ConAsignaciones(usuario, (RolInterno.Comercial, true), (RolInterno.GerenteComercial, true));
        _e.Almacen.ObtenerRolesAsync(200, Arg.Any<CancellationToken>()).ThrowsAsync(new TimeoutException("sin respuesta"));

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.AlmacenNoDisponible);
        filas.Should().OnlyContain(f => f.Activo, "no se revoca nada cuando el almacén falla");
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
        await _e.UnidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task UnSegundoIngresoConElMismoRolNoCreaNadaYActualizaLaUltimaSesion()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Valor.Sesion!.Usuario.Id.Should().Be(usuario.Id);
        resultado.Valor.Sesion!.Usuario.Ubicacion.Should().Be("Antioquia");
        usuario.UltimaSesion.Should().Be(_e.Reloj.AhoraUtc);
        _e.Usuarios.DidNotReceiveWithAnyArgs().Agregar(default!);
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.UsuarioExistente);
        _e.EventoAuditado(clsCodigosAcceso.HerramientaLogin).Exito.Should().BeTrue();
    }

    [Fact]
    public async Task HU0403_SiElRolExternoOrdinarioCambioElLoginReconciliaOtorgaYRevoca()
    {
        // HU-040.3 (#258, B+): antes esto era rol_externo_cambio (rechazo). Ahora el login reconcilia: external gerente, interno comercial;
        // otorga gerente (crea la asignación) y revoca comercial (sobrante), y entra con gerente_comercial.
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoGerente);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        clsUsuarioRol? otorgado = null;
        _e.Roles.When(r => r.AgregarAsignacion(Arg.Any<clsUsuarioRol>())).Do(c => otorgado = c.Arg<clsUsuarioRol>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Valor.Sesion!.Usuario.Rol.Should().Be("gerente_comercial");
        resultado.Valor.Eleccion.Should().BeNull("queda un solo rol efectivo");
        usuario.UltimaSesion.Should().Be(_e.Reloj.AhoraUtc);
        otorgado!.RolId.Should().Be(_e.Gerente.Id, "se auto-otorga el rol ordinario que faltaba");
        var salida = _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin);
        salida.GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.UsuarioExistente);
        salida.GetProperty("roles_otorgados").EnumerateArray().Select(r => r.GetString()).Should().Equal("gerente_comercial");
        salida.GetProperty("roles_revocados").EnumerateArray().Select(r => r.GetString()).Should().Equal("comercial");
        salida.GetProperty("actor_reconciliacion").GetString().Should().Be(clsCodigosAcceso.ActorReconciliacion);
        await _e.UnidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HU0402_ConDosAsignacionesActivasRespaldadasPideElegirYNoEmiteSesion()
    {
        // HU-040.1 (#229) dejaba entrar con la jerarquía; HU-040.2 (#230): con dos asignaciones activas se elige.
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente);
        var usuario = _e.UsuarioLocal("JEFE", 200, RolInterno.Comercial, RolInterno.GerenteComercial);
        var sesionAnterior = usuario.UltimaSesion;

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Valor.Sesion.Should().BeNull();
        resultado.Valor.Eleccion!.Roles.Select(r => r.Codigo).Should().Equal("comercial", "gerente_comercial");
        _e.Tickets.Received(1).Emitir(usuario.Id, "JEFE", Arg.Is<IReadOnlyList<RolInterno>>(r => r.SequenceEqual(new[] { RolInterno.Comercial, RolInterno.GerenteComercial })));
        usuario.UltimaSesion.Should().Be(sesionAnterior);
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("regla").GetString().Should().Be(clsMotivosAcceso.ReglaMultiRol);
    }

    [Fact]
    public async Task HU0402_UnUsuarioConUnSoloRolExternoEntraSinSelectorYElRefreshLlevaElRolDeLaSesion()
    {
        // Rol único (external y asignación de gerente): sesión directa y el refresh con el rol de la sesión. Sin roles ordinarios de más que
        // reconciliar, el login no cambia nada (HU-040.3).
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoGerente);
        _e.UsuarioLocal("JEFE", 200, RolInterno.GerenteComercial);
        clsTokenRefresco? refresh = null;
        _e.Tokens.When(t => t.Agregar(Arg.Any<clsTokenRefresco>())).Do(c => refresh = c.Arg<clsTokenRefresco>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Valor.Eleccion.Should().BeNull();
        resultado.Valor.Sesion!.Usuario.Rol.Should().Be("gerente_comercial");
        refresh!.RolSesion.Should().Be(RolInterno.GerenteComercial);
        _e.Tickets.DidNotReceiveWithAnyArgs().Emitir(default, default!, default!);
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("regla").GetString().Should().Be(clsMotivosAcceso.ReglaMapeoUnico);
    }

    [Fact]
    public async Task HU0403_SiGanaUnRolOrdinarioExternoQueFaltabaElLoginLoOtorgaYPasaAMultiRol()
    {
        // HU-040.3 (#258, B+): external comercial+gerente, interno solo gerente. El login auto-otorga comercial (ordinario) y, al quedar dos
        // roles efectivos, pide elegir (ya no entra directo como gerente). Es el cambio frente a ADR-025 para los roles ORDINARIOS.
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente);
        var usuario = _e.UsuarioLocal("JEFE", 200, RolInterno.GerenteComercial);
        clsUsuarioRol? otorgado = null;
        _e.Roles.When(r => r.AgregarAsignacion(Arg.Any<clsUsuarioRol>())).Do(c => otorgado = c.Arg<clsUsuarioRol>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Valor.Sesion.Should().BeNull("con dos roles efectivos hay selector");
        resultado.Valor.Eleccion!.Roles.Select(r => r.Codigo).Should().Equal("comercial", "gerente_comercial");
        otorgado!.RolId.Should().Be(_e.Comercial.Id);
        var salida = _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin);
        salida.GetProperty("regla").GetString().Should().Be(clsMotivosAcceso.ReglaMultiRol);
        salida.GetProperty("roles_otorgados").EnumerateArray().Select(r => r.GetString()).Should().Equal("comercial");
        await _e.UnidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(new[] { clsEscenarioIdentidad.RolExternoGerente }, "gerente_comercial", "comercial")]
    [InlineData(new[] { clsEscenarioIdentidad.RolExternoComercial }, "comercial", "gerente_comercial")]
    public async Task HU0403_ConDosAsignacionesActivasElLoginRevocaLaSobranteYEntraConLaQueQueda(int[] externos, string rolEsperado, string revocado)
    {
        // HU-040.3 (#258, B+): antes esto era rol_externo_cambio. Ahora el login revoca la asignación cuyo rol externo desapareció y entra con
        // la que queda (rol único: sesión directa, sin selector).
        _e.PersonaExterna("JEFE", 200, externos);
        var usuario = _e.UsuarioLocal("JEFE", 200, RolInterno.Comercial, RolInterno.GerenteComercial);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Valor.Sesion!.Usuario.Rol.Should().Be(rolEsperado);
        resultado.Valor.Eleccion.Should().BeNull();
        usuario.UltimaSesion.Should().Be(_e.Reloj.AhoraUtc);
        var salida = _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin);
        salida.GetProperty("roles_revocados").EnumerateArray().Select(r => r.GetString()).Should().Equal(revocado);
        salida.TryGetProperty("roles_otorgados", out var otorgados).Should().BeTrue();
        otorgados.ValueKind.Should().Be(JsonValueKind.Null, "no se otorga nada: ambos roles ya estaban asignados");
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
    }

    [Fact]
    public async Task HU0403_UnRolSensibleDirectorQueFaltaNoSeAutoOtorgaSigueRechazoYAltaManual()
    {
        // HU-040.3 (#258, B+): director es SENSIBLE (requiereAltaManual). external comercial+gerente+director, interno comercial+gerente: la
        // jerarquía da director, que falta y NO se auto-otorga. Sigue el rechazo (rol_externo_cambio) + alta manual, sin tocar ninguna fila.
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial, clsEscenarioIdentidad.RolExternoGerente, clsEscenarioIdentidad.RolExternoDirector);
        var usuario = _e.UsuarioLocal("JEFE", 200, RolInterno.Comercial, RolInterno.GerenteComercial);
        var sesionAnterior = usuario.UltimaSesion;

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.RolExternoCambio);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("usuario_id").GetGuid().Should().Be(usuario.Id);
        usuario.UltimaSesion.Should().Be(sesionAnterior, "un login rechazado no toca nada");
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
        _e.Tokens.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _e.UnidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task HU0403_UnDirectorSobranteSeAutoRevocaAunqueSeaSensible()
    {
        // HU-040.3 (#258, B+): la auto-revocación aplica a TODOS los roles (fail-safe), también a director. external comercial, interno
        // comercial+director: revoca director y entra como comercial.
        _e.PersonaExterna("JEFE", 200, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("JEFE", 200, RolInterno.Comercial, RolInterno.Director);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("JEFE"), Ct);

        resultado.Valor.Sesion!.Usuario.Rol.Should().Be("comercial");
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("roles_revocados").EnumerateArray()
            .Select(r => r.GetString()).Should().Equal("director");
    }

    [Fact]
    public async Task HU0403_SinAsignacionActivaElLoginAutoOtorgaElRolOrdinarioRespaldado()
    {
        // HU-040.3 (#258, B+): antes esto era sin_asignacion_activa (rechazo). Ahora el login crea la asignación del rol ordinario respaldado.
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101);
        clsUsuarioRol? otorgado = null;
        _e.Roles.When(r => r.AgregarAsignacion(Arg.Any<clsUsuarioRol>())).Do(c => otorgado = c.Arg<clsUsuarioRol>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Valor.Sesion!.Usuario.Rol.Should().Be("comercial");
        otorgado!.RolId.Should().Be(_e.Comercial.Id);
        otorgado.UsuarioId.Should().Be(usuario.Id);
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("roles_otorgados").EnumerateArray()
            .Select(r => r.GetString()).Should().Equal("comercial");
    }

    [Fact]
    public async Task HU0403_ElLoginReactivaLaFilaExistenteEnVezDeInsertarUnaNueva()
    {
        // HU-040.3 (#258, B+): si ya hay una fila (retirada) del rol, se reactiva, sin insertar otra (uq_usuariosRoles_usuarioId_rolId).
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101);
        var filas = _e.ConAsignaciones(usuario, (RolInterno.Comercial, false));

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Valor.Sesion!.Usuario.Rol.Should().Be("comercial");
        filas[0].Activo.Should().BeTrue("se reactivó la fila existente");
        _e.Roles.DidNotReceiveWithAnyArgs().AgregarAsignacion(default!);
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

        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(32, null)]).Should().Be((RolInterno.Comercial, clsMotivosAcceso.ReglaMapeoUnico));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(31, null)]).Should().Be((RolInterno.GerenteComercial, clsMotivosAcceso.ReglaMapeoUnico));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(31, null), new clsRolExterno(32, null)])
            .Should().Be((RolInterno.GerenteComercial, clsMotivosAcceso.ReglaPrecedenciaGerente));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(40, null)]).Should().Be(((RolInterno?)null, (string?)null));
        clsVerificadorIdentidad.ResolverRol(catalogo, []).Rol.Should().BeNull();
    }

    [Fact]
    public void ElDirectorGanaALosDemasRolesConSuReglaAuditada()
    {
        var catalogo = _e.Catalogo;
        var director = clsEscenarioIdentidad.RolExternoDirector;

        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(director, null)]).Should().Be((RolInterno.Director, clsMotivosAcceso.ReglaMapeoUnico));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(32, null), new clsRolExterno(director, null)])
            .Should().Be((RolInterno.Director, clsMotivosAcceso.ReglaPrecedenciaDirector));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(director, null), new clsRolExterno(31, null)])
            .Should().Be((RolInterno.Director, clsMotivosAcceso.ReglaPrecedenciaDirector));
        clsVerificadorIdentidad.ResolverRol(catalogo, [new clsRolExterno(31, null), new clsRolExterno(32, null), new clsRolExterno(director, null)])
            .Should().Be((RolInterno.Director, clsMotivosAcceso.ReglaPrecedenciaDirector));
    }

    [Fact]
    public void UnDirectorInactivoNoGanaYElGerenteSigueConSuRegla()
    {
        _e.Director.Desactivar();

        clsVerificadorIdentidad.ResolverRol(_e.Catalogo, [new clsRolExterno(31, null), new clsRolExterno(clsEscenarioIdentidad.RolExternoDirector, null)])
            .Should().Be((RolInterno.GerenteComercial, clsMotivosAcceso.ReglaMapeoUnico));
    }

    [Fact]
    public void UnRolInternoInactivoNoDaAcceso()
    {
        _e.Gerente.Desactivar();

        clsVerificadorIdentidad.ResolverRol(_e.Catalogo, [new clsRolExterno(31, null)]).Rol.Should().BeNull();
    }
}
