using Cotifly.Aplicacion.Identidad.DTOs;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Infraestructura.Identidad.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Almacén corporativo de solo lectura (<c>dbsolucionesti</c> falsa con los tipos reales: ID NUMERIC, IDUSUARIO e IDROLES
/// INT): búsqueda por NOMBREUSUARIO y por ID, roles con su descripción, y la conversión segura NUMERIC → INT.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsAlmacenIdentidadIntegracionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task EncuentraLaPersonaPorNombreUsuarioSinDistinguirMayusculas()
    {
        var persona = await ConsultarAsync(s => Almacen(s).BuscarUsuarioAsync(" aperez ", Ct));

        persona.Should().Be(new clsUsuarioExterno(101, "APEREZ", "Ana Pérez", "aperez@boterosoto.com.co"));
    }

    [Fact]
    public async Task SiNoApareceComoNombreBuscaElIdNumerico()
    {
        var persona = await ConsultarAsync(s => Almacen(s).BuscarUsuarioAsync("102", Ct));

        persona!.NombreUsuario.Should().Be("LGOMEZ");
        persona.IdUsuarioExterno.Should().Be(102);
    }

    [Theory]
    [InlineData("NADIE")]
    [InlineData("0")]
    [InlineData("-101")]
    [InlineData("101.0")]
    [InlineData("99999999999999999999999")]
    public async Task UnaPersonaInexistenteOUnIdNoEnteroPositivoNoSeEncuentra(string idUsuario)
    {
        var persona = await ConsultarAsync(s => Almacen(s).BuscarUsuarioAsync(idUsuario, Ct));

        persona.Should().BeNull();
    }

    [Fact]
    public async Task UnNombreDeMasDeCincuentaCaracteresNoSeBuscaComoNombre()
    {
        var persona = await ConsultarAsync(s => Almacen(s).BuscarUsuarioAsync(new string('A', 51), Ct));

        persona.Should().BeNull();
    }

    [Fact]
    public async Task DevuelveLosRolesExternosConSuDescripcionSinRepetir()
    {
        var roles = await ConsultarAsync(s => Almacen(s).ObtenerRolesAsync(102, Ct));

        roles.Should().Equal(
            new clsRolExterno(clsBaseDatosPruebas.RolExternoGerente, "GERENTE COMERCIAL"),
            new clsRolExterno(clsBaseDatosPruebas.RolExternoSinMapeo, "CONSULTA"));
    }

    [Fact]
    public async Task UnIdFueraDelRangoDeIntNoTieneRolesPeroLaPersonaSeEncuentra()
    {
        var persona = await ConsultarAsync(s => Almacen(s).BuscarUsuarioAsync("FUERARANGO", Ct));
        var roles = await ConsultarAsync(s => Almacen(s).ObtenerRolesAsync(persona!.IdUsuarioExterno, Ct));

        persona!.IdUsuarioExterno.Should().Be(3_000_000_000);
        roles.Should().BeEmpty();
    }

    [Fact]
    public async Task UnRolSinFilaEnElCatalogoSeDevuelveSinDescripcion()
    {
        var id = await BaseDatos.CrearPersonaExternaAsync($"r{Guid.NewGuid():N}"[..20], null, null, 777);

        var roles = await ConsultarAsync(s => Almacen(s).ObtenerRolesAsync(id, Ct));

        roles.Should().Equal(new clsRolExterno(777, null));
    }

    [Fact]
    public async Task NingunaConsultaLeeLaColumnaClave()
    {
        var sql = await ConsultarAsync(s =>
        {
            var contexto = s.GetRequiredService<clsIdentidadDbContext>();
            var nombre = "X";
            return Task.FromResult(contexto.Usuarios.Where(u => u.NombreUsuario == nombre).ToQueryString()
                                   + contexto.UsuarioRoles.Where(r => r.IdUsuario == 1).ToQueryString()
                                   + contexto.Roles.ToQueryString());
        });

        sql.Should().NotContainEquivalentOf("CLAVE");
        sql.Should().Contain("varchar(50)", "los parámetros de NOMBREUSUARIO son VARCHAR, sin conversión implícita");
    }

    [Fact]
    public async Task LosRolesSeLeenSinLaColumnaIdDeTblSapUsuarioRoles()
    {
        // #172: Nexo mapea tblSAPUsuarioRoles de dbSolucionesTI_Prueba solo con IDUSUARIO e IDROLES (INT). La tabla falsa no tiene ID
        // y Cotifly no debe pedirla: la consulta de roles solo nombra esas dos columnas.
        var sql = await ConsultarAsync(s =>
        {
            var contexto = s.GetRequiredService<clsIdentidadDbContext>();
            return Task.FromResult(contexto.UsuarioRoles.Where(r => r.IdUsuario == 1).ToQueryString());
        });

        sql.Should().Contain("[IDUSUARIO]").And.Contain("[IDROLES]").And.NotContain("[ID]");
    }

    [Fact]
    public async Task UnIdNumericDelPortalSeConvierteAlMismoNumeroEnLosRoles()
    {
        // #172: el ID numeric(18,0) del portal (p. ej. 10123) es el idUsuarioExterno y el IDUSUARIO (INT) de sus roles.
        var nombre = $"p{Guid.NewGuid():N}"[..20];
        var id = await BaseDatos.CrearPersonaExternaAsync(nombre, null, null, clsBaseDatosPruebas.RolExternoComercial);

        var persona = await ConsultarAsync(s => Almacen(s).BuscarUsuarioAsync(nombre.ToLowerInvariant(), Ct));
        var roles = await ConsultarAsync(s => Almacen(s).ObtenerRolesAsync(persona!.IdUsuarioExterno, Ct));

        persona!.IdUsuarioExterno.Should().Be(id);
        roles.Should().Equal(new clsRolExterno(clsBaseDatosPruebas.RolExternoComercial, "COMERCIAL"));
    }

    private static IClsAlmacenIdentidadExterno Almacen(IServiceProvider servicios) =>
        servicios.GetRequiredService<IClsAlmacenIdentidadExterno>();
}
