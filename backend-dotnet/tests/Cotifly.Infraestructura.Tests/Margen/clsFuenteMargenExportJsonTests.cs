using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary>
/// <see cref="clsFuenteMargenExportJson"/>: port de <c>ExportJsonFuente</c> (<c>mcp_server/margen/export_fuente.py</c>) con la
/// muestra del Python (<c>tests/fixtures/sap/rentab_cliente_muestra.json</c>, idéntica a <c>datos/sap_export.json</c>).
/// </summary>
public sealed class clsFuenteMargenExportJsonTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), $"cotifly-sap-{Guid.NewGuid():N}");

    public clsFuenteMargenExportJsonTests() => Directory.CreateDirectory(_carpeta);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // test_export_encuentra_y_no_encuentra
    [Fact]
    public async Task EncuentraElValueDelClienteYDevuelveNullSiNoExiste()
    {
        var fuente = Fuente(clsSapTmFalso.RutaMuestra);

        (await fuente.ObtenerValorAsync("50000005", "OPTN", "15927", Ct)).Should().Be(13.5m);
        (await fuente.ObtenerValorAsync("50000005", "OPTN", "99999", Ct)).Should().BeNull("cliente inexistente");
    }

    [Theory]
    [InlineData("50000005", "OPTN", "15927", "13.5")]
    [InlineData("50000005", "OPIM", "580", "5")]
    [InlineData("50000005", "OPEX", "704", "8.2")]
    [InlineData("50000005", "OPTI", "1123", "12")]
    [InlineData("50000005", "OPDS", "2044", "9.75")]
    [InlineData("50000010", "OHUB", "3301", "15")]
    [InlineData("50000010", "OH3P", "3302", "18.4")]
    [InlineData("50000005", "OPSC", "4110", "7")]
    [InlineData("50000005", "OPTN", "5000", "-19.2")]
    [InlineData("50000005", "OPIM", "5001", "99.9")]
    public async Task DevuelveElValueCrudoDeCadaFilaSinDividirPorCien(string salesOrg, string trqType, string bp, string esperado)
    {
        var valor = await Fuente(clsSapTmFalso.RutaMuestra).ObtenerValorAsync(salesOrg, trqType, bp, Ct);

        // El resolver es el único que divide por 100 (clsReglasMargen.NormalizarValorSap).
        valor.Should().Be(decimal.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task LaMuestraEnMemoriaDeLasPruebasCoincideConElArchivo()
    {
        var fuente = Fuente(clsSapTmFalso.RutaMuestra);
        using var documento = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(clsSapTmFalso.RutaMuestra, Ct));

        documento.RootElement.GetProperty("d").GetProperty("results").GetArrayLength().Should().Be(clsFuenteMargenMuestra.Filas.Count);
        foreach (var fila in clsFuenteMargenMuestra.Filas)
        {
            (await fuente.ObtenerValorAsync(fila.SalesOrg, fila.TrqType, fila.Bp, Ct))
                .Should().Be(decimal.Parse(fila.Valor, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    [Fact]
    public async Task LaLlaveIncluyeLaOrganizacionDeVentas()
    {
        // OHUB/3301 vive en 50000010: con la organización por defecto no está (test_resolver_respeta_sales_org).
        (await Fuente(clsSapTmFalso.RutaMuestra).ObtenerValorAsync("50000005", "OHUB", "3301", Ct)).Should().BeNull();
    }

    [Fact]
    public void ConRutaEstaConfiguradaYSuModoEsExportJson()
    {
        var fuente = Fuente(clsSapTmFalso.RutaMuestra);

        fuente.Modo.Should().Be("export_json");
        fuente.EstaConfigurada.Should().BeTrue();
        Fuente(null).EstaConfigurada.Should().BeFalse();
        Fuente("  ").EstaConfigurada.Should().BeFalse();
    }

    // test_export_archivo_ausente
    [Fact]
    public async Task UnArchivoAusenteEsFuenteNoDisponibleSinExponerLaRuta()
    {
        var ruta = Path.Combine(_carpeta, "no", "existe", "export.json");

        var consultar = () => Fuente(ruta).ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        (await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>())
            .Which.Message.Should().Contain("export").And.NotContain(_carpeta);
    }

    // test_export_json_malformado
    [Theory]
    [InlineData("{ esto no es json")]
    [InlineData("[]")]
    [InlineData("{\"d\":{}}")]
    [InlineData("{\"d\":{\"results\":{}}}")]
    [InlineData("{\"d\":{\"results\":[{\"SalesOrgId\":\"50000005\",\"TrqType\":\"OPTN\",\"OrderPartyId\":\"1\"}]}}")]
    [InlineData("{\"d\":{\"results\":[{\"SalesOrgId\":\"50000005\",\"TrqType\":\"OPTN\",\"OrderPartyId\":\"1\",\"Value\":\"trece\"}]}}")]
    [InlineData("{\"d\":{\"results\":[{\"SalesOrgId\":\"50000005\",\"TrqType\":\"OPTN\",\"OrderPartyId\":\"1\",\"Value\":null}]}}")]
    [InlineData("{\"d\":{\"results\":[{\"SalesOrgId\":50000005,\"TrqType\":\"OPTN\",\"OrderPartyId\":\"1\",\"Value\":\"1\"}]}}")]
    public async Task UnExportMalformadoEsFuenteNoDisponible(string contenido)
    {
        var ruta = Escribir("malo.json", contenido);

        var consultar = () => Fuente(ruta).ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        (await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>())
            .Which.Message.Should().StartWith("Export SAP ilegible");
    }

    [Fact]
    public async Task AceptaElValueComoNumeroJson()
    {
        var ruta = Escribir("numero.json", Export(("50000005", "OPTN", "15927", "13.5")));

        (await Fuente(ruta).ObtenerValorAsync("50000005", "OPTN", "15927", Ct)).Should().Be(13.5m);
    }

    [Fact]
    public async Task ConFilasRepetidasGanaLaUltimaComoEnElPython()
    {
        var ruta = Escribir("repetido.json", Export(("50000005", "OPTN", "1", "\"10\""), ("50000005", "OPTN", "1", "\"11\"")));

        (await Fuente(ruta).ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().Be(11m);
    }

    [Fact]
    public async Task NoReleeElArchivoMientrasNoCambieSuFechaNiSuTamano()
    {
        var ruta = Escribir("cache.json", Export(("50000005", "OPTN", "1", "\"10\"")));
        var fuente = Fuente(ruta);
        (await fuente.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().Be(10m);
        var fecha = File.GetLastWriteTimeUtc(ruta);

        // Mismo tamaño y misma fecha: la caché sigue sirviendo el valor anterior.
        await File.WriteAllTextAsync(ruta, Export(("50000005", "OPTN", "1", "\"20\"")), Ct);
        File.SetLastWriteTimeUtc(ruta, fecha);

        (await fuente.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().Be(10m);
    }

    [Fact]
    public async Task RecargaElArchivoCuandoCambia()
    {
        var ruta = Escribir("recarga.json", Export(("50000005", "OPTN", "1", "\"10\"")));
        var fuente = Fuente(ruta);
        (await fuente.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().Be(10m);

        await File.WriteAllTextAsync(ruta, Export(("50000005", "OPTN", "1", "\"12.5\""), ("50000005", "OPTN", "2", "\"3\"")), Ct);
        File.SetLastWriteTimeUtc(ruta, DateTime.UtcNow.AddMinutes(1));

        (await fuente.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().Be(12.5m);
        (await fuente.ObtenerValorAsync("50000005", "OPTN", "2", Ct)).Should().Be(3m);
    }

    [Fact]
    public async Task SiElArchivoDesapareceODejaDeSerLegibleDespuesDeCargarLoReporta()
    {
        var ruta = Escribir("vuelve.json", Export(("50000005", "OPTN", "1", "\"10\"")));
        var fuente = Fuente(ruta);
        (await fuente.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().Be(10m);

        await File.WriteAllTextAsync(ruta, "{ roto", Ct);
        File.SetLastWriteTimeUtc(ruta, DateTime.UtcNow.AddMinutes(1));
        await fuente.Invoking(f => f.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();

        File.Delete(ruta);
        await fuente.Invoking(f => f.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();

        // Cuando el archivo vuelve a estar bien, se recupera sin reiniciar.
        Escribir("vuelve.json", Export(("50000005", "OPTN", "1", "\"14\"")));
        (await fuente.ObtenerValorAsync("50000005", "OPTN", "1", Ct)).Should().Be(14m);
    }

    [Fact]
    public async Task ConsultasConcurrentesCarganUnIndiceConsistente()
    {
        var fuente = Fuente(clsSapTmFalso.RutaMuestra);

        var valores = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => fuente.ObtenerValorAsync("50000005", "OPTN", "15927", Ct)));

        valores.Should().AllBeEquivalentTo(13.5m);
    }

    [Fact]
    public async Task SinRutaConfiguradaNoSeConsulta()
    {
        var consultar = () => Fuente(null).ObtenerValorAsync("50000005", "OPTN", "15927", Ct);

        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_carpeta, recursive: true);
        }
        catch (IOException)
        {
            // Carpeta temporal: si queda algo abierto, el sistema la limpia.
        }
    }

    private static clsFuenteMargenExportJson Fuente(string? ruta) =>
        new(
            Options.Create(new clsOpcionesSapTm { Modo = clsOpcionesSapTm.ModoExportJson, ExportPath = ruta }),
            NullLogger<clsFuenteMargenExportJson>.Instance);

    private string Escribir(string nombre, string contenido)
    {
        var ruta = Path.Combine(_carpeta, nombre);
        File.WriteAllText(ruta, contenido);
        return ruta;
    }

    /// <summary>Export con la forma de SAP; <c>valor</c> va tal cual (con comillas es texto, sin comillas es número JSON).</summary>
    private static string Export(params (string SalesOrg, string TrqType, string Bp, string Valor)[] filas) =>
        "{\"d\":{\"results\":[" + string.Join(',', filas.Select(f =>
            $"{{\"SalesOrgId\":\"{f.SalesOrg}\",\"TrqType\":\"{f.TrqType}\",\"OrderPartyId\":\"{f.Bp}\",\"Value\":{f.Valor}}}")) + "]}}";
}
