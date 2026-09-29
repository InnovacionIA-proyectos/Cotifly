using System.Globalization;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Identidad;

/// <summary>
/// Pasos de <c>specs/features/login_clientepro.feature</c> y <c>refresh_logout.feature</c>. La base se comparte entre
/// escenarios: cada nombre y cada ID externo del escenario se traduce a uno único (sufijo por escenario, ID nuevo).
/// </summary>
[Binding]
public sealed class clsPasosIdentidad(clsContextoEscenario contexto)
{
    private readonly string _sufijo = Guid.NewGuid().ToString("N")[..6];
    private readonly Dictionary<string, string> _nombres = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, long> _ids = [];
    private readonly Dictionary<string, clsSesionEmitida> _sesiones = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _refreshAnterior = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _usuariosDimEjecutivo = new(StringComparer.OrdinalIgnoreCase);
    private clsResultado<clsSesionEmitida>? _login;
    private clsResultado<clsSesionEmitida>? _renovacion;
    private IReadOnlyList<clsResultado<clsSesionEmitida>> _renovacionesSimultaneas = [];
    private Exception? _errorLogout;

    private static clsBaseDatosPruebas BaseDatos => clsEntornoAceptacion.BaseDatos;

    [Given("la persona {string} con ID externo {int} y nombre {string} en el almacén corporativo con los roles externos {string}")]
    public async Task DadoLaPersona(string nombreUsuario, int idLogico, string nombreCompleto, string roles)
    {
        var real = Real(nombreUsuario);
        var id = await BaseDatos.CrearPersonaExternaAsync(real, nombreCompleto, $"{real}@boterosoto.com.co", Roles(roles));
        _ids[idLogico] = id;
    }

    [Given("{string} ya inició sesión una vez")]
    [Given("{string} inició sesión desde ClientePro")]
    public async Task DadoYaInicioSesion(string nombreUsuario)
    {
        var resultado = await LoginAsync(Real(nombreUsuario));
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        _sesiones[nombreUsuario] = resultado.Valor;
    }

    [Given("en el almacén corporativo {string} pasa a tener los roles externos {string}")]
    public async Task DadoCambianLosRolesExternos(string nombreUsuario, string roles)
    {
        var persona = await ContextoConsultaAsync(s => s.GetRequiredService<IClsAlmacenIdentidadExterno>().BuscarUsuarioAsync(Real(nombreUsuario)));
        await BaseDatos.CambiarRolesExternosAsync(persona!.IdUsuarioExterno, Roles(roles));
    }

    [Given("en el almacén corporativo el ID externo {int} pasa a llamarse {string}")]
    public Task DadoCambiaElNombreUsuario(int idLogico, string nuevoNombre) =>
        BaseDatos.RenombrarPersonaExternaAsync(_ids[idLogico], Real(nuevoNombre));

    [Given("el almacén corporativo no está disponible")]
    public void DadoElAlmacenNoDisponible() =>
        contexto.Reconfigurar(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identidad"] = "Server=tcp:127.0.0.1,1;Database=dbsolucionesti;User Id=x;Password=NoImporta.123;Encrypt=False;Connect Timeout=2",
        });

    [Given("en DimEjecutivo {string} aparece como {string} en la regional {string}")]
    public async Task DadoEnDimEjecutivo(string nombreUsuario, string usuarioDimEjecutivo, string regional)
    {
        // "BOTEROSOTO\JULI.SSO" → "BOTEROSOTO\JULI.SSO-<sufijo>": se conserva el dominio y la caja del nombre lógico.
        var cadena = await ConectarDimEjecutivoAsync();
        var separador = usuarioDimEjecutivo.IndexOf('\\', StringComparison.Ordinal) + 1;
        var logico = usuarioDimEjecutivo[separador..];
        var real = Real(nombreUsuario);
        var valor = usuarioDimEjecutivo[..separador] + (logico == logico.ToUpperInvariant() ? real.ToUpperInvariant() : real);
        _usuariosDimEjecutivo[nombreUsuario] = valor;
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo.DimEjecutivo (NOMBRE, REGIONAL, USUARIO) VALUES ('{nombreUsuario}', '{regional}', '{valor}');");
    }

    [Given("DimEjecutivo está conectada pero {string} no aparece en ella")]
    public Task DadoDimEjecutivoSinElUsuario(string nombreUsuario) => ConectarDimEjecutivoAsync();

    [Given("DimEjecutivo no está disponible")]
    public void DadoDimEjecutivoNoDisponible() =>
        contexto.Reconfigurar(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Regional"] = "Server=tcp:127.0.0.1,1;Database=DWCubosEBSSAP;User Id=x;Password=NoImporta.123;Encrypt=False;Connect Timeout=2",
            ["Regional:TiempoEsperaLoginSegundos"] = "1",
        });

    [Given("el DBA asignó a {string} la regional {string}")]
    public Task DadoElDbaAsignoLaRegional(string nombreUsuario, string regional) =>
        clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.UsuariosWeb SET regional = N'{regional}' WHERE nombreUsuario = '{Real(nombreUsuario)}';");

    [When("en DimEjecutivo {string} pasa a la regional {string}")]
    public async Task CuandoCambiaLaRegionalEnDimEjecutivo(string nombreUsuario, string regional)
    {
        var cadena = await BaseDatos.CadenaDirectorioEjecutivosAsync();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"UPDATE dbo.DimEjecutivo SET REGIONAL = '{regional}' WHERE USUARIO = '{_usuariosDimEjecutivo[nombreUsuario]}';");

        // La caché de DimEjecutivo dura 60 s por proceso: el siguiente login ya ve el cambio.
        contexto.Reloj.Avanzar(clsCacheDirectorioEjecutivos.Vigencia + TimeSpan.FromSeconds(1));
    }

    [Given("{string} renovó su sesión hace {int} minuto")]
    public async Task DadoRenovoSuSesion(string nombreUsuario, int minutos)
    {
        await CuandoRenuevaSuSesion(nombreUsuario);
        _renovacion!.EsExitoso.Should().BeTrue();
        contexto.Reloj.Avanzar(TimeSpan.FromMinutes(minutos));
    }

    [Given("pasaron {int} días")]
    public void DadoPasaronDias(int dias) => contexto.Reloj.Avanzar(TimeSpan.FromDays(dias));

    [When("{string} inicia sesión desde ClientePro")]
    public async Task CuandoIniciaSesion(string nombreUsuario) => _login = await LoginAsync(Real(nombreUsuario));

    [When("la persona con idUsuario {string} inicia sesión desde ClientePro")]
    public async Task CuandoIniciaSesionConId(string idLogico) =>
        _login = await LoginAsync(_ids[long.Parse(idLogico, CultureInfo.InvariantCulture)].ToString(CultureInfo.InvariantCulture));

    [When("{string} inicia sesión desde ClientePro declarando los roles {string}")]
    public async Task CuandoIniciaSesionDeclarandoRoles(string nombreUsuario, string roles) =>
        _login = await LoginAsync(Real(nombreUsuario), roles.Split(',', StringSplitOptions.TrimEntries));

    [When("{string} renueva su sesión")]
    public async Task CuandoRenuevaSuSesion(string nombreUsuario)
    {
        var refresh = _sesiones[nombreUsuario].Refresh;
        _renovacion = await ContextoConsultaAsync(s => s.GetRequiredService<IClsRefreshTokenService>().RenovarAsync(refresh));
        if (_renovacion.EsExitoso)
        {
            _refreshAnterior[nombreUsuario] = refresh;
            _sesiones[nombreUsuario] = _renovacion.Valor;
        }
    }

    [When("{string} vuelve a usar el refresh ya rotado")]
    public async Task CuandoReutilizaElRefresh(string nombreUsuario)
    {
        var anterior = _refreshAnterior[nombreUsuario];
        _renovacion = await ContextoConsultaAsync(s => s.GetRequiredService<IClsRefreshTokenService>().RenovarAsync(anterior));
    }

    [When("{string} renueva su sesión dos veces al mismo tiempo con el mismo refresh")]
    public async Task CuandoRenuevaDosVecesAlMismoTiempo(string nombreUsuario)
    {
        var refresh = _sesiones[nombreUsuario].Refresh;
        _renovacionesSimultaneas = await Task.WhenAll(
            ContextoConsultaAsync(s => s.GetRequiredService<IClsRefreshTokenService>().RenovarAsync(refresh)),
            ContextoConsultaAsync(s => s.GetRequiredService<IClsRefreshTokenService>().RenovarAsync(refresh)));
    }

    [When("{string} cierra su sesión")]
    public async Task CuandoCierraSuSesion(string nombreUsuario)
    {
        var sesion = _sesiones[nombreUsuario];
        var acceso = new JsonWebToken(sesion.Access);
        await CerrarAsync(new clsSolicitudLogout(sesion.Refresh, acceso.Id, new DateTimeOffset(acceso.ValidTo, TimeSpan.Zero), sesion.Usuario.Id));
    }

    [When("alguien cierra sesión sin tokens")]
    public Task CuandoCierraSinTokens() => CerrarAsync(new clsSolicitudLogout(null));

    [Then("la sesión se emite con el rol {string}")]
    public void EntoncesLaSesionSeEmiteConElRol(string rol)
    {
        _login!.EsExitoso.Should().BeTrue(_login.EsFallo ? _login.Error.Mensaje : null);
        _login.Valor.Usuario.Rol.Should().Be(rol);
        _login.Valor.Access.Should().NotBeNullOrEmpty();
        _login.Valor.Refresh.Should().HaveLength(43);
    }

    [Then("la sesión trae la ubicación {string}")]
    public void EntoncesLaSesionTraeLaUbicacion(string ubicacion)
    {
        _login!.EsExitoso.Should().BeTrue(_login.EsFallo ? _login.Error.Mensaje : null);
        _login.Valor.Usuario.Ubicacion.Should().Be(ubicacion);
    }

    [Then("la sesión trae la ubicación vacía")]
    public void EntoncesLaSesionTraeLaUbicacionVacia()
    {
        _login!.EsExitoso.Should().BeTrue(_login.EsFallo ? _login.Error.Mensaje : null);
        _login.Valor.Usuario.Ubicacion.Should().BeNull();
    }

    [Then("el usuario {string} tiene la regional {string} en Cotifly")]
    public async Task EntoncesTieneLaRegional(string nombreUsuario, string regional) =>
        (await RegionalGuardadaAsync(nombreUsuario)).Should().Be(regional);

    [Then("el usuario {string} tiene la regional vacía en Cotifly")]
    public async Task EntoncesTieneLaRegionalVacia(string nombreUsuario)
    {
        (await ContarUsuariosAsync(nombreUsuario)).Should().Be(1);
        (await RegionalGuardadaAsync(nombreUsuario)).Should().BeNull();
    }

    [Then("el login responde credenciales inválidas")]
    public void EntoncesCredencialesInvalidas() =>
        _login!.Error.Should().Be(new clsErrorDominio(clsCodigosAcceso.CredencialesInvalidas, "Credenciales inválidas"));

    [Then("el usuario {string} existe en Cotifly con nombre {string} y vinculado al ID externo {int}")]
    public async Task EntoncesElUsuarioExiste(string nombreUsuario, string nombreCompleto, int idLogico)
    {
        var usuario = await UsuarioAsync(nombreUsuario);
        usuario.Should().NotBeNull();
        usuario!.NombreCompleto.Should().Be(nombreCompleto);
        usuario.Email.Should().Be($"{Real(nombreUsuario)}@boterosoto.com.co");
        usuario.IdUsuarioExterno.Should().Be(_ids[idLogico]);
    }

    [Then("el usuario {string} tiene asignado el rol {string}")]
    [Then("el usuario {string} sigue con el rol {string}")]
    public async Task EntoncesTieneAsignadoElRol(string nombreUsuario, string rol)
    {
        var usuario = await UsuarioAsync(nombreUsuario);
        var asignados = await ContextoConsultaAsync(s => s.GetRequiredService<IClsRolRepository>().ListarRolesAsignadosAsync(usuario!.Id));
        asignados.Should().Equal(clsCodigoEnum.DesdeCodigo<RolInterno>(rol));
    }

    [Then("existe un solo usuario {string} en Cotifly")]
    public async Task EntoncesExisteUnSoloUsuario(string nombreUsuario) =>
        (await ContarUsuariosAsync(nombreUsuario)).Should().Be(1);

    [Then("no existe el usuario {string} en Cotifly")]
    public async Task EntoncesNoExisteElUsuario(string nombreUsuario) =>
        (await ContarUsuariosAsync(nombreUsuario)).Should().Be(0);

    [Then("el acceso de {string} queda auditado como exitoso")]
    public async Task EntoncesAuditadoComoExitoso(string nombreUsuario) =>
        (await EventosAccesoAsync(nombreUsuario)).Should().Contain(e => e.Exito && e.Resultado == "aceptado");

    [Then("el acceso de {string} queda auditado como rechazado por {string}")]
    public async Task EntoncesAuditadoComoRechazado(string nombreUsuario, string motivo) =>
        (await EventosAccesoAsync(nombreUsuario)).Should().Contain(e => !e.Exito && e.Resultado == "rechazado" && e.Motivo == motivo);

    [Then("el acceso de {string} queda auditado con la regla {string}")]
    public async Task EntoncesAuditadoConLaRegla(string nombreUsuario, string regla) =>
        (await EventosAccesoAsync(nombreUsuario)).Should().Contain(e => e.Regla == regla);

    [Then("la renovación emite un par nuevo con el rol {string}")]
    public void EntoncesLaRenovacionEmiteUnParNuevo(string rol)
    {
        _renovacion!.EsExitoso.Should().BeTrue(_renovacion.EsFallo ? _renovacion.Error.Mensaje : null);
        _renovacion.Valor.Usuario.Rol.Should().Be(rol);
    }

    [Then("la renovación responde sesión expirada")]
    public void EntoncesSesionExpirada() =>
        _renovacion!.Error.Should().Be(new clsErrorDominio(clsCodigosAcceso.SesionExpirada, "Inicia sesión de nuevo."));

    [Then("exactamente una renovación emite un par nuevo")]
    public void EntoncesExactamenteUna()
    {
        _renovacionesSimultaneas.Should().HaveCount(2);
        _renovacionesSimultaneas.Count(r => r.EsExitoso).Should().Be(1);
    }

    [Then("el refresh anterior de {string} ya no está vigente")]
    public async Task EntoncesElRefreshAnteriorNoEstaVigente(string nombreUsuario) =>
        (await RefreshVigenteAsync(_refreshAnterior[nombreUsuario])).Should().BeFalse();

    [Then("el refresh de {string} ya no está vigente")]
    public async Task EntoncesElRefreshNoEstaVigente(string nombreUsuario) =>
        (await RefreshVigenteAsync(_sesiones[nombreUsuario].Refresh)).Should().BeFalse();

    [Then("{string} no tiene refresh vigentes")]
    public async Task EntoncesNoTieneRefreshVigentes(string nombreUsuario)
    {
        var usuario = await UsuarioAsync(nombreUsuario);
        var vigentes = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.TokensRefresco WHERE usuarioId = '{usuario!.Id}' AND revocadoAt IS NULL");
        vigentes.Should().Be(0);
    }

    [Then("el access de {string} figura como revocado")]
    public async Task EntoncesElAccessFiguraComoRevocado(string nombreUsuario)
    {
        var jti = new JsonWebToken(_sesiones[nombreUsuario].Access).Id;
        var enBase = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.TokensRevocados WHERE jti = '{jti}'");
        var revocado = await ContextoConsultaAsync(s => s.GetRequiredService<IClsRevocacionAccesos>().EstaRevocadoAsync(jti));
        enBase.Should().Be(1);
        revocado.Should().BeTrue();
    }

    [Then("el logout responde sin error")]
    public void EntoncesElLogoutRespondeSinError() => _errorLogout.Should().BeNull();

    /// <summary>Nombre único en la base compartida para el nombre lógico del escenario.</summary>
    private string Real(string nombreUsuario)
    {
        if (!_nombres.TryGetValue(nombreUsuario, out var real))
        {
            real = $"{nombreUsuario}-{_sufijo}";
            _nombres[nombreUsuario] = real;
        }

        return real;
    }

    private static int[] Roles(string roles) =>
        [.. roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(r => int.Parse(r, CultureInfo.InvariantCulture))];

    private Task<clsResultado<clsSesionEmitida>> LoginAsync(string idUsuario, IReadOnlyList<string>? roles = null) =>
        ContextoConsultaAsync(s => s.GetRequiredService<IClsLoginClienteProService>()
            .IniciarSesionAsync(new clsSolicitudLoginClientePro(idUsuario, "Nombre informativo", roles)));

    private async Task CerrarAsync(clsSolicitudLogout solicitud)
    {
        try
        {
            await ContextoConsultaAsync(async s =>
            {
                await s.GetRequiredService<IClsLogoutService>().CerrarSesionAsync(solicitud);
                return true;
            });
        }
        catch (Exception ex)
        {
            _errorLogout = ex;
        }
    }

    private Task<clsUsuarioWeb?> UsuarioAsync(string nombreUsuario) =>
        ContextoConsultaAsync(s => s.GetRequiredService<IClsUsuarioWebRepository>().ObtenerPorNombreUsuarioAsync(Real(nombreUsuario)));

    /// <summary>Conecta la <c>DimEjecutivo</c> falsa (<c>ConnectionStrings:Regional</c>) al proveedor del escenario.</summary>
    private async Task<string> ConectarDimEjecutivoAsync()
    {
        var cadena = await BaseDatos.CadenaDirectorioEjecutivosAsync();
        contexto.Reconfigurar(new Dictionary<string, string?> { ["ConnectionStrings:Regional"] = cadena });
        return cadena;
    }

    private Task<string?> RegionalGuardadaAsync(string nombreUsuario) =>
        clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT regional FROM dbo_cotifly.UsuariosWeb WHERE nombreUsuario = '{Real(nombreUsuario)}'");

    private Task<int> ContarUsuariosAsync(string nombreUsuario) =>
        clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.UsuariosWeb WHERE nombreUsuario = '{Real(nombreUsuario)}'");

    private async Task<bool> RefreshVigenteAsync(string refresh)
    {
        var token = await ContextoConsultaAsync(s => s.GetRequiredService<IClsTokenRefrescoRepository>().ObtenerPorHashAsync(clsTokenOpaco.Hash(refresh)));
        return token!.EstaVigente(contexto.Reloj.AhoraUtc);
    }

    /// <summary>Eventos de login auditados en el hilo <c>auth/{usuario}</c> (el reloj falso no permite ordenarlos por fecha).</summary>
    private async Task<IReadOnlyList<(bool Exito, string? Resultado, string? Motivo, string? Regla)>> EventosAccesoAsync(string nombreUsuario)
    {
        var eventos = await ContextoConsultaAsync(s => s.GetRequiredService<IClsAuditoriaRepository>()
            .ListarPorThreadAsync(clsCodigosAcceso.PrefijoHilo + Real(nombreUsuario)));
        return [.. eventos
            .Where(e => e.Herramienta == clsCodigosAcceso.HerramientaLogin)
            .Select(e =>
            {
                using var salida = JsonDocument.Parse(e.OutputResumen!);
                var raiz = salida.RootElement;
                return (e.Exito == true, Texto(raiz, "resultado"), Texto(raiz, "motivo"), Texto(raiz, "regla"));
            })];
    }

    private static string? Texto(JsonElement raiz, string propiedad) =>
        raiz.TryGetProperty(propiedad, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private Task<T> ContextoConsultaAsync<T>(Func<IServiceProvider, Task<T>> consulta) => contexto.ConsultarAsync(consulta);
}
