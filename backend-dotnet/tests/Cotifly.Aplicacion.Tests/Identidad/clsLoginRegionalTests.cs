using System.Diagnostics;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>
/// Regional y datos corporativos en el login (ADR-016 §6): el alta JIT toma la regional de <c>DimEjecutivo</c>, cada login
/// siguiente la refresca junto con nombre y correo, un usuario ausente conserva la que tenga, y la fuente caída, lenta o sin
/// configurar nunca bloquea el login. El refresh no la consulta.
/// </summary>
public sealed class clsLoginRegionalTests
{
    private readonly clsEscenarioIdentidad _e = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElPrimerIngresoTomaLaRegionalDeDimEjecutivo()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.ConRegional("APEREZ", " Occidente ");
        clsUsuarioWeb? creado = null;
        _e.Usuarios.When(u => u.Agregar(Arg.Any<clsUsuarioWeb>())).Do(c => creado = c.Arg<clsUsuarioWeb>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        creado!.Regional.Should().Be("Occidente");
        resultado.Valor.Sesion!.Usuario.Ubicacion.Should().Be("Occidente", "LoginResponse.user.ubicacion lee la columna recién llenada");
    }

    [Fact]
    public async Task UnPrimerIngresoQueNoApareceEnDimEjecutivoQuedaConRegionalNula()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.ConRegional("APEREZ", null);
        clsUsuarioWeb? creado = null;
        _e.Usuarios.When(u => u.Agregar(Arg.Any<clsUsuarioWeb>())).Do(c => creado = c.Arg<clsUsuarioWeb>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.EsExitoso.Should().BeTrue();
        creado!.Regional.Should().BeNull("no todo el personal de ClientePro está en DimEjecutivo: NULL es lo correcto");
        _e.LoggerVerificador.Registros.Should().NotContain(r => r.Nivel >= LogLevel.Warning);
    }

    [Fact]
    public async Task CadaLoginSiguienteRefrescaLaRegionalElNombreYElCorreo()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.ConRegional("APEREZ", "Occidente");

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        usuario.Regional.Should().Be("Occidente", "antes era Antioquia: DimEjecutivo manda");
        usuario.NombreCompleto.Should().Be("Nombre de APEREZ");
        usuario.Email.Should().Be("aperez@boterosoto.com.co");
        resultado.Valor.Sesion!.Usuario.Should().Be(new clsUsuarioSesion(
            usuario.Id, "APEREZ", "aperez@boterosoto.com.co", "Nombre de APEREZ", "comercial", "Occidente"));
        await _e.UnidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiElCorreoCambiaEnElLoginQuedaUnAvisoSinLosValores()
    {
        // El cambio de correo se registra solo con el nombre de usuario, sin los valores.
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);

        await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        var aviso = _e.LoggerVerificador.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning).Subject;
        aviso.Mensaje.Should().Contain("APEREZ").And.Contain("correo nuevo");
        aviso.Mensaje.Should().NotContain("local@boterosoto.com.co").And.NotContain("aperez@boterosoto.com.co");
    }

    [Fact]
    public async Task SiElCorreoNoCambiaNoHayAviso()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        usuario.ActualizarDatos(null, "APEREZ@boterosoto.com.co", null);

        await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        _e.LoggerVerificador.Registros.Should().NotContain(r => r.Nivel >= LogLevel.Warning, "solo cambia la caja del correo");
    }

    [Fact]
    public async Task UnUsuarioQueNoApareceEnDimEjecutivoConservaLaRegionalQueTenia()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.ConRegional("APEREZ", null);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        usuario.Regional.Should().Be("Antioquia", "la regional que fijó el DBA a mano no se borra");
        resultado.Valor.Sesion!.Usuario.Ubicacion.Should().Be("Antioquia");
    }

    [Fact]
    public async Task ConDimEjecutivoCaidaElLoginSigueConLaRegionalGuardada()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.ConRegional("APEREZ", "Occidente");
        _e.Directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Login failed for user 'usuario_lectura' en sql-regional.test"));

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.EsExitoso.Should().BeTrue("la regional nunca bloquea el login");
        usuario.Regional.Should().Be("Antioquia");
        usuario.NombreCompleto.Should().Be("Nombre de APEREZ", "nombre y correo vienen del almacén, que sí respondió");
        var aviso = _e.LoggerVerificador.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("DimEjecutivo")).Subject;
        aviso.Mensaje.Should().Contain("APEREZ").And.Contain(nameof(InvalidOperationException));
        aviso.Mensaje.Should().NotContain("usuario_lectura", "el warning no copia el mensaje de la base");
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.UsuarioExistente);
    }

    [Fact]
    public async Task ConDimEjecutivoCaidaElPrimerIngresoCreaElUsuarioSinRegional()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.ConRegional("APEREZ", "Occidente");
        _e.Directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Throws(new TimeoutException("sin respuesta"));
        clsUsuarioWeb? creado = null;
        _e.Usuarios.When(u => u.Agregar(Arg.Any<clsUsuarioWeb>())).Do(c => creado = c.Arg<clsUsuarioWeb>());

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.EsExitoso.Should().BeTrue();
        creado!.Regional.Should().BeNull();
        _e.SalidaAuditada(clsCodigosAcceso.HerramientaLogin).GetProperty("motivo").GetString().Should().Be(clsMotivosAcceso.AltaJit);
    }

    [Fact]
    public async Task SiDimEjecutivoTardaMasDelLimiteElLoginNoLaEspera()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.ConRegional("APEREZ", "Occidente");
        _e.Directorio.LimiteLogin.Returns(TimeSpan.FromMilliseconds(200));
        CancellationToken recibido = default;
        _e.Directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                // Una fuente que ni siquiera atiende la cancelación: el login igual sigue al vencer el límite.
                recibido = c.Arg<CancellationToken>();
                return new TaskCompletionSource<IReadOnlyDictionary<string, string>>().Task;
            });
        var cronometro = Stopwatch.StartNew();

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        cronometro.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        resultado.EsExitoso.Should().BeTrue();
        usuario.Regional.Should().Be("Antioquia");
        recibido.IsCancellationRequested.Should().BeTrue("la consulta abandonada se cancela");
        _e.LoggerVerificador.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains(nameof(TimeoutException)));
    }

    [Fact]
    public async Task SinConexionConfiguradaNoSeConsultaDimEjecutivo()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.EsExitoso.Should().BeTrue();
        usuario.Regional.Should().Be("Antioquia");
        await _e.Directorio.DidNotReceiveWithAnyArgs().ObtenerRegionalesAsync(default!, Ct);
    }

    [Fact]
    public async Task UnLoginRechazadoNoConsultaDimEjecutivoNiCambiaDatos()
    {
        // Un desajuste de rol ordinario no rechaza (reconcilia). Para un rechazo real se usa un rol externo sin
        // mapeo (sin_rol_autorizado): ahí no se reconcilia ni se consulta DimEjecutivo ni se cambian datos.
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoSinMapeo);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.ConRegional("APEREZ", "Occidente");

        var resultado = await _e.Login().IniciarSesionAsync(new clsSolicitudLoginClientePro("APEREZ"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAcceso.CredencialesInvalidas);
        usuario.Regional.Should().Be("Antioquia");
        usuario.NombreCompleto.Should().Be("Nombre local");
        await _e.Directorio.DidNotReceiveWithAnyArgs().ObtenerRegionalesAsync(default!, Ct);
    }

    [Fact]
    public async Task ElRefreshSinAltaNoConsultaDimEjecutivoNiCambiaDatos()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        var usuario = _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.ConRegional("APEREZ", "Occidente");

        var verificacion = await _e.Verificador().VerificarAsync("APEREZ", permitirAlta: false, _e.Reloj.AhoraUtc, Ct);

        verificacion.EsValida.Should().BeTrue();
        usuario.Regional.Should().Be("Antioquia");
        usuario.NombreCompleto.Should().Be("Nombre local");
        await _e.Directorio.DidNotReceiveWithAnyArgs().ObtenerRegionalesAsync(default!, Ct);
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorSePropaga()
    {
        _e.PersonaExterna("APEREZ", 101, clsEscenarioIdentidad.RolExternoComercial);
        _e.UsuarioLocal("APEREZ", 101, RolInterno.Comercial);
        _e.ConRegional("APEREZ", "Occidente");
        using var cancelacion = new CancellationTokenSource();
        _e.Directorio.ObtenerRegionalesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(async c =>
            {
                await cancelacion.CancelAsync();
                c.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
            });

        var verificar = () => _e.Verificador().VerificarAsync("APEREZ", permitirAlta: true, _e.Reloj.AhoraUtc, cancelacion.Token);

        await verificar.Should().ThrowAsync<OperationCanceledException>();
    }
}
