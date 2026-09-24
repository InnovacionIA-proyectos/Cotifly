using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Identidad.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>Usuarios, tokens y roles (script 0002), auditoría y el contexto de solo lectura de dbsolucionesti.</summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsIdentidadPersistenciaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task UnUsuarioSeObtienePorNombreUsuarioSinDistinguirMayusculasYPorId()
    {
        var nombre = $"u{Guid.NewGuid():N}";
        var id = await CrearUsuarioAsync(nombre);

        var (porNombre, porId) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsUsuarioWebRepository>();
            return (await repo.ObtenerPorNombreUsuarioAsync(nombre.ToUpperInvariant(), Ct), await repo.ObtenerPorIdAsync(id, Ct));
        });

        porNombre!.Id.Should().Be(id);
        porId!.NombreUsuario.Should().Be(nombre);
        porId.Regional.Should().Be("Antioquia");
        porId.CreatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task ElNombreUsuarioEsUnicoPeroVariosUsuariosSinNombrePuedenConvivir()
    {
        var nombre = $"u{Guid.NewGuid():N}";
        await CrearUsuarioAsync(nombre);

        var repetido = () => CrearUsuarioAsync(nombre.ToUpperInvariant());
        var sinNombre = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.UsuariosWeb (nombreUsuario) VALUES (NULL), (NULL);");

        (await repetido.Should().ThrowAsync<clsViolacionUnicidadException>()).Which.Restriccion.Should().Be("uq_usuariosWeb_nombreUsuario");
        await sinNombre.Should().NotThrowAsync("el índice único está filtrado a nombreUsuario IS NOT NULL");
    }

    [Fact]
    public async Task UnTokenDeRefrescoSeGuardaSoloConSuHashYSuFamilia()
    {
        var usuario = await CrearUsuarioAsync();
        var hash = new string('a', 32) + new string('F', 32);
        var token = clsTokenRefresco.Emitir(usuario, hash, Reloj.AhoraUtc, TimeSpan.FromDays(30)).Valor;

        await EnAmbitoAsync(s =>
        {
            Contexto(s).TokensRefresco.Add(token);
            return Task.CompletedTask;
        });
        var leido = await ConsultarAsync(s => Contexto(s).TokensRefresco.AsNoTracking().SingleAsync(t => t.Id == token.Id, Ct));

        leido.TokenHash.Should().Be(hash.ToLowerInvariant());
        leido.FamiliaId.Should().Be(token.FamiliaId);
        leido.ExpiraAt.Should().Be(Reloj.AhoraUtc.AddDays(30));
        leido.EstaVigente(Reloj.AhoraUtc).Should().BeTrue();
    }

    [Fact]
    public async Task ElHashDelTokenDeRefrescoEsUnico()
    {
        var usuario = await CrearUsuarioAsync();
        var hash = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
        await EnAmbitoAsync(s =>
        {
            Contexto(s).TokensRefresco.Add(clsTokenRefresco.Emitir(usuario, hash, Reloj.AhoraUtc, TimeSpan.FromDays(30)).Valor);
            return Task.CompletedTask;
        });

        var repetir = () => EnAmbitoAsync(s =>
        {
            Contexto(s).TokensRefresco.Add(clsTokenRefresco.Emitir(usuario, hash, Reloj.AhoraUtc, TimeSpan.FromDays(30)).Valor);
            return Task.CompletedTask;
        });

        (await repetir.Should().ThrowAsync<clsViolacionUnicidadException>()).Which.Restriccion.Should().Be("uq_tokensRefresco_tokenHash");
    }

    [Fact]
    public async Task LaRotacionEncadenaElTokenNuevoConElAnterior()
    {
        var usuario = await CrearUsuarioAsync();
        var anterior = clsTokenRefresco.Emitir(usuario, HashAleatorio(), Reloj.AhoraUtc, TimeSpan.FromDays(30)).Valor;
        await EnAmbitoAsync(s =>
        {
            Contexto(s).TokensRefresco.Add(anterior);
            return Task.CompletedTask;
        });
        var nuevo = clsTokenRefresco.Emitir(usuario, HashAleatorio(), Reloj.AhoraUtc, TimeSpan.FromDays(30), anterior.FamiliaId).Valor;

        await EnAmbitoAsync(async s =>
        {
            var contexto = Contexto(s);
            contexto.TokensRefresco.Add(nuevo);
            var viejo = await contexto.TokensRefresco.SingleAsync(t => t.Id == anterior.Id, Ct);
            viejo.MarcarReemplazado(nuevo.Id, Reloj.AhoraUtc);
        });
        var familia = await ConsultarAsync(s => Contexto(s).TokensRefresco.AsNoTracking()
            .Where(t => t.FamiliaId == anterior.FamiliaId).ToListAsync(Ct));

        familia.Should().HaveCount(2);
        familia.Single(t => t.Id == anterior.Id).ReemplazadoPorId.Should().Be(nuevo.Id);
        familia.Single(t => t.Id == anterior.Id).EstaVigente(Reloj.AhoraUtc).Should().BeFalse();
    }

    [Fact]
    public async Task UnJtiRevocadoYUnRolMapeadoSeGuardanYLeen()
    {
        var usuario = await CrearUsuarioAsync();
        var jti = Guid.NewGuid().ToString();
        var rolExterno = $"COTIZADOR_{Guid.NewGuid():N}";

        await EnAmbitoAsync(s =>
        {
            Contexto(s).TokensRevocados.Add(clsTokenRevocado.Crear(jti, Reloj.AhoraUtc.AddMinutes(60), usuario).Valor);
            Contexto(s).RolesMapeo.Add(clsRolMapeo.Crear(rolExterno, RolInterno.GerenteComercial).Valor);
            return Task.CompletedTask;
        });
        var (revocado, rol) = await ConsultarAsync(async s => (
            await Contexto(s).TokensRevocados.AsNoTracking().SingleAsync(t => t.Jti == jti, Ct),
            await Contexto(s).RolesMapeo.AsNoTracking().SingleAsync(r => r.RolExterno == rolExterno, Ct)));
        var rolEnBase = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT rolInterno FROM dbo_cotifly.RolesMapeo WHERE rolExterno = '{rolExterno}'");

        revocado.UsuarioId.Should().Be(usuario);
        revocado.CreatedAt.Should().Be(Reloj.AhoraUtc);
        rol.RolInterno.Should().Be(RolInterno.GerenteComercial);
        rol.UpdatedAt.Should().Be(Reloj.AhoraUtc);
        rolEnBase.Should().Be("gerente_comercial");
    }

    [Fact]
    public async Task LaAuditoriaDeHerramientasSeGuardaYSeListaPorHilo()
    {
        var hilo = $"t-{Guid.NewGuid():N}";
        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsAuditoriaRepository>().Registrar(
                clsEventoHerramienta.Registrar("obtener_margen", """{"lineaNegocio":"Nacional"}""", """{"margen":0.15}""", true, 42, hilo).Valor);
            return Task.CompletedTask;
        });

        var eventos = await ConsultarAsync(s => s.GetRequiredService<IClsAuditoriaRepository>().ListarPorThreadAsync(hilo, Ct));

        eventos.Should().ContainSingle().Which.DuracionMs.Should().Be(42);
    }

    [Fact]
    public async Task LaAuditoriaRechazaEntradasQueNoSonJson()
    {
        var guardar = () => EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsAuditoriaRepository>().Registrar(
                clsEventoHerramienta.Registrar("obtener_margen", "no-json", null, false, 1).Valor);
            return Task.CompletedTask;
        });

        (await guardar.Should().ThrowAsync<DbUpdateException>()).Which.InnerException!.Message.Should().Contain("ck_eventosHerramienta_input_json");
    }

    [Fact]
    public async Task ElContextoDeIdentidadLeeLosUsuariosCorporativosSinLaColumnaClave()
    {
        var (porNombre, porId, sql) = await ConsultarAsync(async s =>
        {
            var identidad = s.GetRequiredService<clsIdentidadDbContext>();
            var consulta = identidad.UsuariosSapApp.Where(u => u.NombreUsuario == "apErez");
            return (await consulta.SingleOrDefaultAsync(Ct),
                await identidad.UsuariosSapApp.SingleOrDefaultAsync(u => u.Id == 102, Ct),
                consulta.ToQueryString());
        });

        porNombre!.Id.Should().Be(101);
        porNombre.DescripcionUsuario.Should().Be("Ana Pérez");
        porNombre.Email.Should().Be("aperez@boterosoto.com.co");
        porId!.NombreUsuario.Should().Be("LGOMEZ");
        sql.Should().NotContainEquivalentOf("CLAVE");
    }

    [Fact]
    public async Task ElContextoDeIdentidadEsDeSoloLectura()
    {
        await using var ambito = NuevoAmbito();
        var identidad = ambito.ServiceProvider.GetRequiredService<clsIdentidadDbContext>();

        identidad.ChangeTracker.QueryTrackingBehavior.Should().Be(QueryTrackingBehavior.NoTracking);
        await FluentActions.Awaiting(() => identidad.SaveChangesAsync(Ct)).Should().ThrowAsync<InvalidOperationException>();
        FluentActions.Invoking(() => identidad.SaveChanges()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ElEsquemaYLaTablaDeIdentidadSonConfigurables()
    {
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaIdentidad, """
            IF SCHEMA_ID('seg') IS NULL EXEC('CREATE SCHEMA seg');
            IF OBJECT_ID('seg.UsuariosApp') IS NULL
            BEGIN
                CREATE TABLE seg.UsuariosApp (ID INT PRIMARY KEY, NOMBREUSUARIO NVARCHAR(50), DESCRIPCIONUSUARIO NVARCHAR(200), EMAIL NVARCHAR(200), CLAVE NVARCHAR(200));
                INSERT INTO seg.UsuariosApp VALUES (7, 'OTRO', 'Otro Usuario', 'otro@x.com', 'x');
            END
            """);
        await using var proveedor = clsServiciosPruebas.Crear(BaseDatos.CadenaCotifly, Reloj, BaseDatos.CadenaIdentidad,
            new Dictionary<string, string?> { ["Identidad:Esquema"] = "seg", ["Identidad:Tabla"] = "UsuariosApp" });
        await using var ambito = proveedor.CreateAsyncScope();

        var usuario = await ambito.ServiceProvider.GetRequiredService<clsIdentidadDbContext>().UsuariosSapApp.SingleAsync(Ct);

        usuario.NombreUsuario.Should().Be("OTRO");
    }

    private static string HashAleatorio() =>
        Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
}
