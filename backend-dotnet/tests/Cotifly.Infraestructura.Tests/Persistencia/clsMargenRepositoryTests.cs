using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMargenRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    // Organización ficticia: la base es compartida y una fila específica de la organización por defecto
    // (50000005) le ganaría al comodín en las pruebas del resolver de margen (#10).
    private const string OrganizacionDePrueba = "99990001";

    [Theory]
    [InlineData("Nacional")]
    [InlineData("nacional")]
    [InlineData(" Exportacion ")]
    public async Task LaPoliticaDeUnaLineaSembradaSeObtienePorNombreSinDistinguirMayusculas(string linea)
    {
        var politica = await ConsultarAsync(s => s.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorLineaAsync(linea, Ct));

        politica.Should().NotBeNull();
        politica!.Margen.Should().Be(0.15m);
        politica.MetodoCalculo.Should().Be("margen");
        politica.Activa.Should().BeTrue();
    }

    [Fact]
    public async Task UnaLineaDesconocidaDevuelveNull()
    {
        var politica = await ConsultarAsync(s => s.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorLineaAsync("Aérea", Ct));

        politica.Should().BeNull();
    }

    [Fact]
    public async Task LaPoliticaSeObtienePorIdYSeListanLasLineasOrdenadas()
    {
        var (porId, todas) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsMargenPoliticaRepository>();
            var nacional = await repo.ObtenerPorLineaAsync("Nacional", Ct);
            return (await repo.ObtenerPorIdAsync(nacional!.Id, Ct), await repo.ListarAsync(Ct));
        });

        porId!.LineaNegocio.Should().Be("Nacional");
        porId.MapeoLineasNexo.Should().Be("Nacional");
        // HU-021 (§10): el 0009 retira Almacenamiento, Almacenamiento3PL e ITR y agrega DevolucionContenedor.
        todas.Select(l => l.LineaNegocio).Should().Contain(["DevolucionContenedor", "Importacion", "Nacional"]).And.NotContain("ITR")
            .And.BeInAscendingOrder(StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CambiarElMargenDeUnaLineaActualizaUpdatedAt()
    {
        var linea = await CrearLineaAsync();
        Reloj.Avanzar(TimeSpan.FromDays(1));

        await EnAmbitoAsync(async s =>
        {
            var politica = await s.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorLineaAsync(linea, Ct);
            politica!.CambiarMargen(0.2m).EsExitoso.Should().BeTrue();
        });
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsMargenPoliticaRepository>().ObtenerPorLineaAsync(linea, Ct));

        leida!.Margen.Should().Be(0.2m);
        leida.UpdatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task ElMapeoSembradoTraduceTrqTypeALineaYViceversa()
    {
        var (linea, trqType) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsSapMapeoRepository>();
            return (await repo.LineaDeAsync("OPTN", cancellationToken: Ct), await repo.TrqTypeDeAsync("Nacional", cancellationToken: Ct));
        });

        linea.Should().Be("Nacional");
        trqType.Should().Be("OPTN");
    }

    [Fact]
    public async Task ElMapeoDeUnaOrganizacionEspecificaGanaSobreElComodin()
    {
        var trqType = $"Z{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await AgregarMapeosAsync(
            clsSapMapeoTrqType.Crear(trqType, "Nacional").Valor,
            clsSapMapeoTrqType.Crear(trqType, "Exportacion", OrganizacionDePrueba).Valor);

        var (especifica, otraOrganizacion, comodin) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsSapMapeoRepository>();
            return (await repo.LineaDeAsync(trqType, OrganizacionDePrueba, Ct),
                await repo.LineaDeAsync(trqType, "99999999", Ct),
                await repo.LineaDeAsync(trqType, cancellationToken: Ct));
        });

        especifica.Should().Be("Exportacion");
        otraOrganizacion.Should().Be("Nacional");
        comodin.Should().Be("Nacional");
    }

    [Fact]
    public async Task UnMapeoInactivoSeIgnora()
    {
        var trqType = $"Y{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var inactivo = clsSapMapeoTrqType.Crear(trqType, "Nacional").Valor;
        inactivo.Desactivar();
        await AgregarMapeosAsync(inactivo);

        var linea = await ConsultarAsync(s => s.GetRequiredService<IClsSapMapeoRepository>().LineaDeAsync(trqType, cancellationToken: Ct));

        linea.Should().BeNull();
    }

    [Fact]
    public async Task UnAjusteDeMargenSeGuardaConSuDiferenciaYSeListaPorCotizacion()
    {
        var usuario = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(usuario);
        await GuardarAsync(cotizacion);
        var ajuste = clsMargenAjuste.Registrar(0.15m, 0.12m, "Cliente estratégico", usuario, cotizacion.Id, cotizacion.Folio).Valor;

        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsMargenAjusteRepository>().Agregar(ajuste);
            return Task.CompletedTask;
        });
        var ajustes = await ConsultarAsync(s => s.GetRequiredService<IClsMargenAjusteRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct));

        ajustes.Should().ContainSingle().Which.Diferencia.Should().Be(-0.03m);
        ajustes[0].UsuarioId.Should().Be(usuario);
        ajustes[0].CreatedAt.Should().Be(Reloj.AhoraUtc);
    }

    private async Task<string> CrearLineaAsync()
    {
        var linea = $"L{Guid.NewGuid():N}"[..30];
        await EnAmbitoAsync(s =>
        {
            Contexto(s).MargenesPolitica.Add(clsMargenPolitica.Crear(linea, 0.1m).Valor);
            return Task.CompletedTask;
        });
        return linea;
    }

    private Task AgregarMapeosAsync(params clsSapMapeoTrqType[] mapeos) =>
        EnAmbitoAsync(s =>
        {
            Contexto(s).SapMapeoTrqType.AddRange(mapeos);
            return Task.CompletedTask;
        });
}
