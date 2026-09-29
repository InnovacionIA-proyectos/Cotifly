using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Regional desde la tabla de ejecutivos (<c>DWCubosEBSSAP.DimEjecutivo</c> falsa, ADR-016 §6): cruce por <c>nombreUsuario</c>
/// sin distinguir mayúsculas ni espacios, usuario con dos regionales ignorado, elección del adaptador por
/// <c>ConnectionStrings:Regional</c> y regla completa con <c>UsuariosWeb</c> como respaldo.
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
            INSERT INTO dbo.DimEjecutivo (ID, NOMBRE, REGIONAL) VALUES
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
            INSERT INTO dbo.DimEjecutivo (ID, NOMBRE, REGIONAL) VALUES ('{nombreDirectorio}', 'Dir', '{regional}'), ('{nombreMovido}', 'Mov', 'OTRA-{regional}');
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

    private Task<string?> NombreAsync(Guid id) =>
        clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly, $"SELECT nombreUsuario FROM dbo_cotifly.UsuariosWeb WHERE id = '{id}'");

    private static clsDirectorioEjecutivosDimEjecutivo Directorio(string cadena) => new(
        Options.Create(new clsOpcionesConexiones { Regional = cadena }),
        Options.Create(new clsOpcionesRegional()),
        NullLogger<clsDirectorioEjecutivosDimEjecutivo>.Instance);
}
