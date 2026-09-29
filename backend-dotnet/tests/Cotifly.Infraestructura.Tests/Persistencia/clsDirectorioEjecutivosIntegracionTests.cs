using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Regional desde la tabla de ejecutivos (<c>DWCubosEBSSAP.DimEjecutivo</c> falsa, ADR-016 §6): cruce por <c>nombreUsuario</c>
/// sin el dominio de <c>USUARIO</c> (<c>boterosoto\</c>, #81), sin distinguir mayúsculas ni espacios, usuario con dos regionales
/// ignorado, elección del adaptador por <c>ConnectionStrings:Regional</c>, regla completa con <c>UsuariosWeb</c> como respaldo y
/// login que llena y refresca <c>UsuariosWeb.regional</c> sin bloquearse si la fuente cae.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsDirectorioEjecutivosIntegracionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static string Unico(string prefijo) => $"{prefijo}{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    [Fact]
    public async Task CruzaPorNombreDeUsuarioSinDistinguirMayusculasNiEspacios()
    {
        var cadena = await BaseDatos.CadenaDirectorioEjecutivosAsync();
        var (ana, luis, doble, regional) = (Unico("ana"), Unico("luis"), Unico("doble"), Unico("OCC"));
        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"""
            INSERT INTO dbo.DimEjecutivo (USUARIO, NOMBRE, REGIONAL) VALUES
                ('{ana}  ', 'Ana', ' {regional} '), ('{luis}', 'Luis', '{regional}'), ('{luis}', 'Luis', '{regional}'),
                ('{doble}', 'Doble', '{regional}'), ('{doble}', 'Doble', 'OTRA');
            """);
        var directorio = Directorio(cadena);

        var mapa = await directorio.ObtenerRegionalesAsync([ana.ToLowerInvariant(), luis, doble, "NOEXISTE"], Ct);
        var nombres = await directorio.ListarNombresUsuarioAsync(regional.ToLowerInvariant(), Ct);

        directorio.Configurado.Should().BeTrue();
        mapa.Should().HaveCount(2);
        mapa[ana].Should().Be(regional);
        mapa[luis.ToLowerInvariant()].Should().Be(regional, "las claves no distinguen mayúsculas");
        mapa.Should().NotContainKey(doble, "dos regionales distintas: no se adivina");
        nombres.Should().BeEquivalentTo([ana, luis, doble]);
    }

    [Fact]
    public async Task LasRespuestasSeReutilizanSesentaSegundos()
    {
        var cadena = await BaseDatos.CadenaDirectorioEjecutivosAsync();
        var (ana, regional) = (Unico("cache"), Unico("REG"));
        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"INSERT INTO dbo.DimEjecutivo (USUARIO, NOMBRE, REGIONAL) VALUES ('{ana}', 'Ana', '{regional}');");
        var reloj = new clsRelojFalso();
        var cache = new clsCacheDirectorioEjecutivos(reloj);
        var directorio = new clsDirectorioEjecutivosDimEjecutivo(
            Options.Create(new clsOpcionesConexiones { Regional = cadena }), Options.Create(new clsOpcionesRegional()), cache,
            NullLogger<clsDirectorioEjecutivosDimEjecutivo>.Instance);

        (await directorio.ObtenerRegionalesAsync([ana], Ct))[ana].Should().Be(regional);
        (await directorio.ListarNombresUsuarioAsync(regional, Ct)).Should().Equal(ana);
        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"DELETE FROM dbo.DimEjecutivo WHERE USUARIO = '{ana}';");

        (await directorio.ObtenerRegionalesAsync([ana], Ct)).Should().ContainKey(ana, "sigue en caché");
        (await directorio.ListarNombresUsuarioAsync(regional, Ct)).Should().Equal(ana);
        reloj.Avanzar(clsCacheDirectorioEjecutivos.Vigencia + TimeSpan.FromSeconds(1));
        (await directorio.ObtenerRegionalesAsync([ana], Ct)).Should().BeEmpty("la entrada venció y se volvió a consultar");
        (await directorio.ListarNombresUsuarioAsync(regional, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task SinCadenaSeUsaElDirectorioNoDisponible()
    {
        var servicio = await ConsultarAsync(s => Task.FromResult(s.GetRequiredService<IClsDirectorioEjecutivos>()));

        servicio.Should().BeOfType<clsDirectorioEjecutivosNoDisponible>();
        servicio.Configurado.Should().BeFalse();
    }

    [Fact]
    public async Task ConCadenaLaRegionalEfectivaUsaElDirectorioYCaeAUsuariosWeb()
    {
        var cadena = await BaseDatos.CadenaDirectorioEjecutivosAsync();
        var regional = Unico("NORTE");
        var enDirectorio = await CrearUsuarioAsync(Unico("dir"));
        var soloRespaldo = await CrearUsuarioAsync(Unico("resp"));
        var movido = await CrearUsuarioAsync(Unico("mov"));
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.UsuariosWeb SET regional = N'{regional}' WHERE id IN ('{soloRespaldo}', '{movido}');
            """);
        var nombreDirectorio = await NombreAsync(enDirectorio);
        var nombreMovido = await NombreAsync(movido);
        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"""
            INSERT INTO dbo.DimEjecutivo (USUARIO, NOMBRE, REGIONAL) VALUES ('boterosoto\{nombreDirectorio}', 'Dir', '{regional}'), ('boterosoto\{nombreMovido}', 'Mov', 'OTRA-{regional}');
            """);

        await using var proveedor = clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad,
            new Dictionary<string, string?> { ["ConnectionStrings:Regional"] = cadena });
        await using var ambito = proveedor.CreateAsyncScope();
        var regionales = ambito.ServiceProvider.GetRequiredService<IClsRegionalUsuario>();
        var usuarios = ambito.ServiceProvider.GetRequiredService<IClsUsuarioWebRepository>();

        var miembros = await regionales.ListarMiembrosAsync(regional, Ct);
        var deDirectorio = await regionales.ResolverAsync((await usuarios.ObtenerPorIdAsync(enDirectorio, Ct))!, Ct);
        var deRespaldo = await regionales.ResolverAsync((await usuarios.ObtenerPorIdAsync(soloRespaldo, Ct))!, Ct);

        ambito.ServiceProvider.GetRequiredService<IClsDirectorioEjecutivos>().Should().BeOfType<clsDirectorioEjecutivosDimEjecutivo>();
        miembros.Valor.Select(u => u.Id).Should().BeEquivalentTo([enDirectorio, soloRespaldo], "el movido tiene otra regional en el directorio");
        deDirectorio.Valor.Should().Be(new clsRegionalResuelta(regional, clsRegionalResuelta.FuenteDimEjecutivo));
        deRespaldo.Valor.Should().Be(new clsRegionalResuelta(regional, clsRegionalResuelta.FuenteUsuariosWeb));
    }

    [Fact]
    public async Task ConElDirectorioInalcanzableLaRegionalNoEstaDisponible()
    {
        var gerente = await CrearUsuarioAsync(Unico("ger"));
        var inalcanzable = "Server=127.0.0.1,1;Database=DWCubosEBSSAP;User Id=sa;Password=NoImporta123!;Connect Timeout=2;TrustServerCertificate=True";
        await using var proveedor = clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad,
            new Dictionary<string, string?> { ["ConnectionStrings:Regional"] = inalcanzable });
        await using var ambito = proveedor.CreateAsyncScope();
        var usuario = (await ambito.ServiceProvider.GetRequiredService<IClsUsuarioWebRepository>().ObtenerPorIdAsync(gerente, Ct))!;

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsRegionalUsuario>().ResolverAsync(usuario, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoDisponible);
    }

    [Fact]
    public async Task QuitaElDominioDeUsuarioYComparaSinDistinguirMayusculas()
    {
        // #81: DimEjecutivo.USUARIO viene como 'boterosoto\usuario'; NOMBREUSUARIO de ClientePro viene sin dominio.
        var cadena = await BaseDatos.CadenaDirectorioEjecutivosAsync();
        var sufijo = Guid.NewGuid().ToString("N")[..8];
        var (perez, gomez, doble, sinDominio, ausente, regional) =
            ($"apérez{sufijo}", $"lgómez{sufijo}", $"doble{sufijo}", $"plano{sufijo}", $"nadie{sufijo}", Unico("SUR"));
        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"""
            INSERT INTO dbo.DimEjecutivo (NOMBRE, REGIONAL, CORREO, USUARIO) VALUES
                ('Ana Pérez',  '{regional}', 'ap@boterosoto.com.co', 'boterosoto\{perez}'),
                ('Luis Gómez', '{regional}', 'lg@boterosoto.com.co', 'BOTEROSOTO\{gomez.ToUpperInvariant()}'),
                ('Luis Gómez', '{regional}', 'lg@boterosoto.com.co', '  boterosoto\{gomez}  '),
                ('Doble A',    '{regional}', NULL, 'boterosoto\{doble}'),
                ('Doble B',    'OTRA',       NULL, '{doble}'),
                ('Plano',      '{regional}', NULL, '{sinDominio}'),
                ('Vacío',      '{regional}', NULL, 'boterosoto\'),
                ('Nulo',       '{regional}', NULL, NULL);
            """);
        var directorio = Directorio(cadena);

        var mapa = await directorio.ObtenerRegionalesAsync([perez.ToUpperInvariant(), gomez, doble, sinDominio, ausente, "boterosoto"], Ct);
        var nombres = await directorio.ListarNombresUsuarioAsync(regional, Ct);

        mapa.Should().HaveCount(3);
        mapa[perez].Should().Be(regional, "se quita 'boterosoto\\' y se compara sin mayúsculas, con tilde incluida");
        mapa[gomez].Should().Be(regional, "dos filas del mismo usuario con la misma regional no son ambiguas");
        mapa[sinDominio].Should().Be(regional, "un valor sin dominio se compara tal cual");
        mapa.Should().NotContainKey(doble, "'boterosoto\\doble' y 'doble' son el mismo usuario con dos regionales: se ignora");
        mapa.Should().NotContainKey(ausente);
        mapa.Should().NotContainKey("boterosoto", "el dominio solo no es un usuario");
        nombres.Select(n => n.ToUpperInvariant()).Distinct().Should().BeEquivalentTo(
            new[] { perez, gomez, doble, sinDominio }.Select(n => n.ToUpperInvariant()), "los nombres salen sin dominio y sin vacíos");
        nombres.Should().NotContain(n => n.Contains('\\', StringComparison.Ordinal) || n.Length == 0);
    }

    [Fact]
    public async Task ElLoginLlenaYRefrescaLaRegionalDesdeDimEjecutivoYConservaLaDelAusente()
    {
        var cadena = await BaseDatos.CadenaDirectorioEjecutivosAsync();
        var nombre = $"lg{Guid.NewGuid():N}"[..14];
        var (occidente, norte) = (Unico("OCC"), Unico("NOR"));
        await BaseDatos.CrearPersonaExternaAsync(nombre, "Luis Gómez", $"{nombre}@boterosoto.com.co", clsBaseDatosPruebas.RolExternoComercial);
        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"INSERT INTO dbo.DimEjecutivo (NOMBRE, REGIONAL, USUARIO) VALUES ('Luis', '{occidente}', 'boterosoto\\{nombre.ToUpperInvariant()}');");
        await using var proveedor = clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad,
            new Dictionary<string, string?> { ["ConnectionStrings:Regional"] = cadena });

        var primero = await LoginAsync(proveedor, nombre);
        var trasPrimero = await RegionalGuardadaAsync(nombre);

        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"UPDATE dbo.DimEjecutivo SET REGIONAL = '{norte}' WHERE REGIONAL = '{occidente}';");
        Reloj.Avanzar(clsCacheDirectorioEjecutivos.Vigencia + TimeSpan.FromSeconds(1));
        var segundo = await LoginAsync(proveedor, nombre);
        var trasSegundo = await RegionalGuardadaAsync(nombre);

        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"DELETE FROM dbo.DimEjecutivo WHERE REGIONAL = '{norte}';");
        Reloj.Avanzar(clsCacheDirectorioEjecutivos.Vigencia + TimeSpan.FromSeconds(1));
        var tercero = await LoginAsync(proveedor, nombre);
        var trasTercero = await RegionalGuardadaAsync(nombre);

        primero.Valor.Usuario.Ubicacion.Should().Be(occidente);
        trasPrimero.Should().Be(occidente, "el alta JIT toma la regional de DimEjecutivo");
        segundo.Valor.Usuario.Ubicacion.Should().Be(norte);
        trasSegundo.Should().Be(norte, "cada login la refresca");
        tercero.EsExitoso.Should().BeTrue();
        trasTercero.Should().Be(norte, "si deja de aparecer, se conserva la que tenía");
    }

    [Fact]
    public async Task ConDimEjecutivoInalcanzableElLoginEntraYLaRegionalNoCambia()
    {
        var nombre = $"iv{Guid.NewGuid():N}"[..14];
        await BaseDatos.CrearPersonaExternaAsync(nombre, "Iván", $"{nombre}@boterosoto.com.co", clsBaseDatosPruebas.RolExternoComercial);
        var inalcanzable = "Server=127.0.0.1,1;Database=DWCubosEBSSAP;User Id=sa;Password=NoImporta123!;Connect Timeout=2;TrustServerCertificate=True";
        await using var proveedor = clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad,
            new Dictionary<string, string?> { ["ConnectionStrings:Regional"] = inalcanzable, ["Regional:TiempoEsperaLoginSegundos"] = "1" });

        var primero = await LoginAsync(proveedor, nombre);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"UPDATE dbo_cotifly.UsuariosWeb SET regional = N'Puesta por el DBA' WHERE nombreUsuario = '{nombre}';");
        var segundo = await LoginAsync(proveedor, nombre);

        primero.EsExitoso.Should().BeTrue("DimEjecutivo caída nunca da 401");
        primero.Valor.Usuario.Ubicacion.Should().BeNull();
        segundo.EsExitoso.Should().BeTrue();
        segundo.Valor.Usuario.Ubicacion.Should().Be("Puesta por el DBA");
        (await RegionalGuardadaAsync(nombre)).Should().Be("Puesta por el DBA");
    }

    private static async Task<clsResultado<clsSesionEmitida>> LoginAsync(ServiceProvider proveedor, string nombreUsuario)
    {
        await using var ambito = proveedor.CreateAsyncScope();
        return await ambito.ServiceProvider.GetRequiredService<IClsLoginClienteProService>()
            .IniciarSesionAsync(new clsSolicitudLoginClientePro(nombreUsuario), Ct);
    }

    private Task<string?> RegionalGuardadaAsync(string nombreUsuario) =>
        clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly, $"SELECT regional FROM dbo_cotifly.UsuariosWeb WHERE nombreUsuario = '{nombreUsuario}'");

    private Task<string?> NombreAsync(Guid id) =>
        clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly, $"SELECT nombreUsuario FROM dbo_cotifly.UsuariosWeb WHERE id = '{id}'");

    private static clsDirectorioEjecutivosDimEjecutivo Directorio(string cadena) => new(
        Options.Create(new clsOpcionesConexiones { Regional = cadena }),
        Options.Create(new clsOpcionesRegional()),
        new clsCacheDirectorioEjecutivos(new clsRelojFalso()),
        NullLogger<clsDirectorioEjecutivosDimEjecutivo>.Instance);
}
