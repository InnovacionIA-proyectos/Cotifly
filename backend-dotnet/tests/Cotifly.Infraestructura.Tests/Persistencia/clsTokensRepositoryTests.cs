using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Refresh y jti revocados contra SQL Server 2019: rotación atómica (de dos simultáneas gana una), revocación de familia y
/// de usuario, y purga de vencidos (repositorios y <c>clsLimpiezaTokensHostedService</c>).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsTokensRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task LaRotacionMarcaElAnteriorComoReemplazadoYAgregaElNuevoEnLaMismaFamilia()
    {
        var usuario = await CrearUsuarioAsync();
        var actual = await GuardarTokenAsync(usuario);
        var nuevo = Emitir(usuario, actual.FamiliaId);

        var rotado = await ConsultarAsync(s => Tokens(s).RotarAsync(actual.Id, nuevo, Reloj.AhoraUtc, Ct));
        var familia = await FamiliaAsync(actual.FamiliaId);

        rotado.Should().BeTrue();
        familia.Should().HaveCount(2);
        familia.Single(t => t.Id == actual.Id).ReemplazadoPorId.Should().Be(nuevo.Id);
        familia.Single(t => t.Id == actual.Id).RevocadoAt.Should().Be(Reloj.AhoraUtc);
        familia.Single(t => t.Id == nuevo.Id).EstaVigente(Reloj.AhoraUtc).Should().BeTrue();
    }

    [Fact]
    public async Task UnTokenYaRotadoRevocadoOVencidoNoRotaYNoDejaElNuevo()
    {
        var usuario = await CrearUsuarioAsync();
        var actual = await GuardarTokenAsync(usuario);
        await ConsultarAsync(s => Tokens(s).RotarAsync(actual.Id, Emitir(usuario, actual.FamiliaId), Reloj.AhoraUtc, Ct));
        var otro = Emitir(usuario, actual.FamiliaId);
        var vencido = await GuardarTokenAsync(usuario, TimeSpan.FromMinutes(1));

        var segundaVez = await ConsultarAsync(s => Tokens(s).RotarAsync(actual.Id, otro, Reloj.AhoraUtc, Ct));
        var trasVencer = await ConsultarAsync(s => Tokens(s).RotarAsync(vencido.Id, Emitir(usuario), Reloj.AhoraUtc.AddMinutes(2), Ct));

        segundaVez.Should().BeFalse();
        trasVencer.Should().BeFalse();
        (await ConsultarAsync(s => Tokens(s).ObtenerPorHashAsync(otro.TokenHash, Ct))).Should().BeNull("el insert se deshace si no rota");
    }

    [Fact]
    public async Task DeDosRotacionesSimultaneasDelMismoTokenSoloUnaGana()
    {
        for (var intento = 0; intento < 5; intento++)
        {
            var usuario = await CrearUsuarioAsync();
            var actual = await GuardarTokenAsync(usuario);

            async Task<bool> RotarAsync()
            {
                await using var ambito = NuevoAmbito();
                return await Tokens(ambito.ServiceProvider).RotarAsync(actual.Id, Emitir(usuario, actual.FamiliaId), Reloj.AhoraUtc, Ct);
            }

            var resultados = await Task.WhenAll(RotarAsync(), RotarAsync());

            resultados.Count(r => r).Should().Be(1);
            (await FamiliaAsync(actual.FamiliaId)).Should().HaveCount(2, "el perdedor no deja su token");
        }
    }

    [Fact]
    public async Task RevocarLaFamiliaNoTocaOtrasSesionesDelUsuarioYRevocarAlUsuarioSi()
    {
        var usuario = await CrearUsuarioAsync();
        var sesionA = await GuardarTokenAsync(usuario);
        var sesionB = await GuardarTokenAsync(usuario);

        var porFamilia = await ConsultarAsync(s => Tokens(s).RevocarFamiliaAsync(sesionA.FamiliaId, Reloj.AhoraUtc, Ct));
        var bSigueVigente = (await ConsultarAsync(s => Tokens(s).ObtenerPorHashAsync(sesionB.TokenHash, Ct)))!.EstaVigente(Reloj.AhoraUtc);
        var porUsuario = await ConsultarAsync(s => Tokens(s).RevocarActivosDelUsuarioAsync(usuario, Reloj.AhoraUtc, Ct));

        porFamilia.Should().Be(1);
        bSigueVigente.Should().BeTrue();
        porUsuario.Should().Be(1);
        (await ConsultarAsync(s => Tokens(s).ObtenerPorHashAsync(sesionB.TokenHash, Ct)))!.RevocadoAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task LosJtiRevocadosVigentesSeListanYLosVencidosSePurgan()
    {
        var vigente = $"jti-{Guid.NewGuid():N}";
        var vencido = $"jti-{Guid.NewGuid():N}";
        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsTokenRevocadoRepository>().Agregar(clsTokenRevocado.Crear(vigente, Reloj.AhoraUtc.AddMinutes(30)).Valor);
            s.GetRequiredService<IClsTokenRevocadoRepository>().Agregar(clsTokenRevocado.Crear(vencido, Reloj.AhoraUtc.AddMinutes(-1)).Valor);
            return Task.CompletedTask;
        });

        var listados = await ConsultarAsync(s => s.GetRequiredService<IClsTokenRevocadoRepository>().ListarVigentesAsync(Reloj.AhoraUtc, Ct));
        var purgados = await ConsultarAsync(s => s.GetRequiredService<IClsTokenRevocadoRepository>().PurgarVencidosAsync(Reloj.AhoraUtc, Ct));

        listados.Select(j => j.Jti).Should().Contain(vigente).And.NotContain(vencido);
        purgados.Should().BeGreaterThanOrEqualTo(1);
        (await ExisteJtiAsync(vencido)).Should().BeFalse();
        (await ExisteJtiAsync(vigente)).Should().BeTrue();
    }

    [Fact]
    public async Task LaLimpiezaHorariaBorraRefreshVencidosHaceMasDeUnDiaAunqueOtrosLosReferencien()
    {
        var usuario = await CrearUsuarioAsync();
        var viejo = await GuardarTokenAsync(usuario, TimeSpan.FromHours(1));
        await ConsultarAsync(s => Tokens(s).RotarAsync(viejo.Id, Emitir(usuario, viejo.FamiliaId, TimeSpan.FromHours(1)), Reloj.AhoraUtc, Ct));
        var reciente = await GuardarTokenAsync(usuario);
        Reloj.Avanzar(TimeSpan.FromDays(2));
        await using var ambito = NuevoAmbito();
        var limpieza = ambito.ServiceProvider.GetServices<IHostedService>().OfType<clsLimpiezaTokensHostedService>().Single();

        var resultado = await limpieza.LimpiarAsync(Ct);

        resultado.Should().NotBeNull();
        (await FamiliaAsync(viejo.FamiliaId)).Should().BeEmpty();
        (await ConsultarAsync(s => Tokens(s).ObtenerPorHashAsync(reciente.TokenHash, Ct))).Should().NotBeNull();
    }

    [Fact]
    public async Task SinBaseLaLimpiezaNoLanzaYAvisa()
    {
        await using var proveedor = clsServiciosPruebas.Crear(clsBaseDatosPruebas.ConBase(
            "Server=tcp:127.0.0.1,1;User Id=x;Password=y;Connect Timeout=2;Encrypt=False", "x"), Reloj);
        var limpieza = proveedor.GetServices<IHostedService>().OfType<clsLimpiezaTokensHostedService>().Single();

        var resultado = await limpieza.LimpiarAsync(Ct);

        resultado.Should().BeNull();
    }

    private static IClsTokenRefrescoRepository Tokens(IServiceProvider servicios) => servicios.GetRequiredService<IClsTokenRefrescoRepository>();

    private clsTokenRefresco Emitir(Guid usuario, Guid? familia = null, TimeSpan? vigencia = null) =>
        clsTokenRefresco.Emitir(usuario, clsTokenOpaco.Hash(clsTokenOpaco.Generar()), Reloj.AhoraUtc, vigencia ?? TimeSpan.FromDays(30), familia).Valor;

    private async Task<clsTokenRefresco> GuardarTokenAsync(Guid usuario, TimeSpan? vigencia = null)
    {
        var token = Emitir(usuario, vigencia: vigencia);
        await EnAmbitoAsync(s =>
        {
            Tokens(s).Agregar(token);
            return Task.CompletedTask;
        });
        return token;
    }

    private Task<List<clsTokenRefresco>> FamiliaAsync(Guid familia) =>
        ConsultarAsync(s => Contexto(s).TokensRefresco.AsNoTracking().Where(t => t.FamiliaId == familia).ToListAsync(Ct));

    private Task<bool> ExisteJtiAsync(string jti) =>
        ConsultarAsync(s => Contexto(s).TokensRevocados.AsNoTracking().AnyAsync(t => t.Jti == jti, Ct));
}
