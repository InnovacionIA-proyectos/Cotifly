using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary>
/// Prueba contra el SAP TM real para HU-009.2 (equivale a <c>@pytest.mark.sap</c> del Python). Se salta si no están las
/// variables, así que en CI y en <c>verificar.sh</c> queda como omitida; para excluirla del todo:
/// <c>--filter-not-trait "Categoria=sap"</c>. Para correrla (desde una máquina con ruta de red a SAP, HU-009.1):
/// <c>SapTm__Url</c>, <c>SapTm__User</c>, <c>SapTm__Pass</c>, opcional <c>SapTm__SapClient</c> y <c>SapTm__SalesOrgDefault</c>,
/// y un caso conocido en <c>SAPTM_PRUEBA_TRQTYPE</c> y <c>SAPTM_PRUEBA_BP</c> (y <c>SAPTM_PRUEBA_VALUE</c> si se conoce el
/// valor esperado). Filtro: <c>--filter-trait "Categoria=sap"</c>.
/// </summary>
[Trait("Categoria", "sap")]
public sealed class clsSapTmRealTests
{
    [Fact]
    public async Task ConsultaUnCasoConocidoContraSapReal()
    {
        static string? Variable(string nombre) => Environment.GetEnvironmentVariable(nombre);
        var url = Variable("SapTm__Url");
        var trqType = Variable("SAPTM_PRUEBA_TRQTYPE");
        var bp = Variable("SAPTM_PRUEBA_BP");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(trqType) || string.IsNullOrWhiteSpace(bp),
            "Sin SapTm__Url, SAPTM_PRUEBA_TRQTYPE o SAPTM_PRUEBA_BP: prueba contra SAP real omitida (HU-009.2).");

        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SapTm:Modo"] = "odata",
                ["SapTm:Url"] = url,
                ["SapTm:User"] = Variable("SapTm__User"),
                ["SapTm:Pass"] = Variable("SapTm__Pass"),
                ["SapTm:SapClient"] = Variable("SapTm__SapClient"),
            })
            .Build();
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoFalso(Environments.Development));
        servicios.AddInfraestructura(configuracion);
        await using var proveedor = servicios.BuildServiceProvider();

        var fuente = proveedor.GetRequiredService<IClsFuenteMargen>();
        fuente.Should().BeOfType<clsFuenteMargenOData>();
        var valor = await fuente.ObtenerValorAsync(
            Variable("SapTm__SalesOrgDefault") ?? "50000005", trqType!, bp!, TestContext.Current.CancellationToken);

        TestContext.Current.SendDiagnosticMessage($"SAP TM real: {trqType}/{bp} → {valor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "sin registro"}");
        if (Variable("SAPTM_PRUEBA_VALUE") is { Length: > 0 } esperado)
        {
            valor.Should().Be(decimal.Parse(esperado, System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
