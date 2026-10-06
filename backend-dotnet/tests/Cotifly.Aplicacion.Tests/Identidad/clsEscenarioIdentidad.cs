using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>
/// Puertos simulados (NSubstitute) de la verificación doble, el login, el refresh y el logout, con un catálogo de roles
/// como el de las pruebas de integración: <c>comercial</c> ↔ IDROLES 34 y <c>gerente_comercial</c> ↔ IDROLES 35.
/// </summary>
internal sealed class clsEscenarioIdentidad
{
    public const int RolExternoComercial = 34;
    public const int RolExternoGerente = 35;
    public const int RolExternoSinMapeo = 40;

    public clsEscenarioIdentidad()
    {
        Comercial = ConId(clsRol.Crear(RolInterno.Comercial, "Comercial", RolExternoComercial).Valor, 1);
        Gerente = ConId(clsRol.Crear(RolInterno.GerenteComercial, "Gerente comercial", RolExternoGerente).Valor, 2);
        Roles.ListarActivosAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<clsRol>)Catalogo);
        Roles.ListarRolesAsignadosAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<RolInterno>());
        Almacen.ObtenerRolesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<clsRolExterno>());
        Configuracion.VigenciaAcceso.Returns(TimeSpan.FromMinutes(60));
        Configuracion.VigenciaRefresh.Returns(TimeSpan.FromDays(30));
        Emisor.EmitirAcceso(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<RolInterno>())
            .Returns(c => new clsTokenAccesoEmitido($"jwt-{c.ArgAt<RolInterno>(2)}", $"jti-{Guid.NewGuid():N}", Reloj.AhoraUtc.AddMinutes(60)));
    }

    public clsRelojFalso Reloj { get; } = new();
    public IClsAlmacenIdentidadExterno Almacen { get; } = Substitute.For<IClsAlmacenIdentidadExterno>();
    public IClsRolRepository Roles { get; } = Substitute.For<IClsRolRepository>();
    public IClsUsuarioWebRepository Usuarios { get; } = Substitute.For<IClsUsuarioWebRepository>();

    /// <summary>Directorio de ejecutivos (<c>DimEjecutivo</c>, #81): sin configurar salvo que la prueba lo active con <see cref="ConRegional"/>.</summary>
    public IClsDirectorioEjecutivos Directorio { get; } = Substitute.For<IClsDirectorioEjecutivos>();
    public IClsTokenRefrescoRepository Tokens { get; } = Substitute.For<IClsTokenRefrescoRepository>();
    public IClsTokenRevocadoRepository Revocados { get; } = Substitute.For<IClsTokenRevocadoRepository>();
    public IClsRevocacionAccesos Revocacion { get; } = Substitute.For<IClsRevocacionAccesos>();
    public IClsEmisorTokens Emisor { get; } = Substitute.For<IClsEmisorTokens>();
    public IClsConfiguracionJwt Configuracion { get; } = Substitute.For<IClsConfiguracionJwt>();
    public IClsUnitOfWork UnidadTrabajo { get; } = Substitute.For<IClsUnitOfWork>();
    public IClsAuditoriaService Auditoria { get; } = Substitute.For<IClsAuditoriaService>();
    public clsLoggerFalso<clsVerificadorIdentidad> LoggerVerificador { get; } = new();
    public clsLoggerFalso<clsLoginClienteProService> LoggerLogin { get; } = new();
    public clsLoggerFalso<clsRefreshTokenService> LoggerRefresh { get; } = new();
    public clsLoggerFalso<clsLogoutService> LoggerLogout { get; } = new();

    public clsRol Comercial { get; }
    public clsRol Gerente { get; }
    public List<clsRol> Catalogo => [Comercial, Gerente];

    public clsVerificadorIdentidad Verificador() => new(Almacen, Roles, Usuarios, Directorio, LoggerVerificador);

    public clsLoginClienteProService Login() =>
        new(Verificador(), Tokens, Emisor, Configuracion, UnidadTrabajo, Reloj, Auditoria, LoggerLogin);

    public clsRefreshTokenService Refresh() =>
        new(Verificador(), Tokens, Usuarios, Emisor, Reloj, Auditoria, LoggerRefresh);

    public clsLogoutService Logout() =>
        new(Tokens, Revocados, Revocacion, UnidadTrabajo, Reloj, Auditoria, LoggerLogout);

    /// <summary>Persona del almacén con sus roles externos.</summary>
    public clsUsuarioExterno PersonaExterna(string nombreUsuario, long id, params int[] rolesExternos)
    {
        var persona = new clsUsuarioExterno(id, nombreUsuario, $"Nombre de {nombreUsuario}", $"{nombreUsuario.ToLowerInvariant()}@boterosoto.com.co");
        Almacen.BuscarUsuarioAsync(nombreUsuario, Arg.Any<CancellationToken>()).Returns(persona);
        Almacen.ObtenerRolesAsync(id, Arg.Any<CancellationToken>())
            .Returns(rolesExternos.Select(r => new clsRolExterno(r, $"ROL {r}")).ToList());
        return persona;
    }

    /// <summary>
    /// Activa el directorio de ejecutivos: <paramref name="nombreUsuario"/> tiene <paramref name="regional"/> en <c>DimEjecutivo</c>
    /// (<c>null</c>: no aparece). Límite del login de 3 s.
    /// </summary>
    public void ConRegional(string nombreUsuario, string? regional)
    {
        Directorio.Configurado.Returns(true);
        Directorio.LimiteLogin.Returns(TimeSpan.FromSeconds(3));
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (regional is not null)
        {
            mapa[nombreUsuario] = regional;
        }

        Directorio.ObtenerRegionalesAsync(Arg.Is<IReadOnlyCollection<string>>(n => n.Contains(nombreUsuario)), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)mapa);
    }

    /// <summary>Usuario ya creado en Cotifly (vinculado o no) con su rol asignado.</summary>
    public clsUsuarioWeb UsuarioLocal(string nombreUsuario, long? idExterno, params RolInterno[] asignados)
    {
        var usuario = clsUsuarioWeb.Crear(nombreUsuario, "Nombre local", "local@boterosoto.com.co", "Antioquia", Reloj.AhoraUtc.AddDays(-10), idExterno).Valor;
        Usuarios.ObtenerPorNombreUsuarioAsync(nombreUsuario, Arg.Any<CancellationToken>()).Returns(usuario);
        Usuarios.ObtenerPorIdAsync(usuario.Id, Arg.Any<CancellationToken>()).Returns(usuario);
        if (idExterno is { } id)
        {
            Usuarios.ObtenerPorIdUsuarioExternoAsync(id, Arg.Any<CancellationToken>()).Returns(usuario);
        }

        Roles.ListarRolesAsignadosAsync(usuario.Id, Arg.Any<CancellationToken>()).Returns(asignados.ToList());
        return usuario;
    }

    /// <summary>Salida (3er argumento) del último evento auditado de <paramref name="herramienta"/>, como JSON snake_case.</summary>
    public JsonElement SalidaAuditada(string herramienta) => ArgumentoAuditado(herramienta, 2);

    /// <summary>Entrada (2º argumento) del último evento auditado de <paramref name="herramienta"/>, como JSON snake_case.</summary>
    public JsonElement EntradaAuditada(string herramienta) => ArgumentoAuditado(herramienta, 1);

    /// <summary>Éxito (4º argumento) y hilo (6º) del último evento auditado de <paramref name="herramienta"/>.</summary>
    public (bool Exito, string? Hilo) EventoAuditado(string herramienta)
    {
        var argumentos = LlamadaAuditada(herramienta);
        return ((bool)argumentos[3]!, (string?)argumentos[5]);
    }

    private JsonElement ArgumentoAuditado(string herramienta, int indice) =>
        JsonDocument.Parse(clsAuditoriaService.AJson(LlamadaAuditada(herramienta)[indice]!)).RootElement.Clone();

    private object?[] LlamadaAuditada(string herramienta) =>
        Auditoria.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IClsAuditoriaService.RegistrarAsync) && (string)c.GetArguments()[0]! == herramienta)
            .Select(c => c.GetArguments())
            .Should().NotBeEmpty($"se esperaba un evento de auditoría '{herramienta}'").And.Subject.Last();

    /// <summary>El <c>Id</c> de los roles lo genera la base (IDENTITY): en memoria se fija por reflexión.</summary>
    private static clsRol ConId(clsRol rol, int id)
    {
        typeof(clsRol).GetProperty(nameof(clsRol.Id))!.SetValue(rol, id);
        return rol;
    }
}
