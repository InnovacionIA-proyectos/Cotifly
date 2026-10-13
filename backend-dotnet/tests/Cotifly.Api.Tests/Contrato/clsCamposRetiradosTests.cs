using System.Text.Json;
using Cotifly.Api.Comun;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Contrato;
using Microsoft.Extensions.Logging;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// HU-021.11 (#138), decisión 1 de HU-021: <c>valor_estimado</c> y <c>referencia</c> salieron del contrato. Un frontend anterior que los
/// siga mandando no recibe error: se ignoran y queda un aviso en el log con el nombre del campo (nunca su valor).
/// </summary>
public sealed class clsCamposRetiradosTests
{
    [Theory]
    [InlineData("SolicitudCosteoWeb")]
    [InlineData("LicitacionWeb")]
    public async Task ElContratoYaNoDeclaraValorEstimadoNiReferencia(string esquema)
    {
        var propiedades = (await clsValidadorEsquemaOpenApi.EsquemaAsync(esquema)).Properties!.Keys;

        propiedades.Should().NotContain(["valor_estimado", "referencia"]);
    }

    [Fact]
    public async Task LaVistaDeLaLicitacionYaNoTraeReferencia()
    {
        var esquema = await clsValidadorEsquemaOpenApi.EsquemaAsync("Licitacion");

        esquema.Properties!.Keys.Should().NotContain("referencia");
        esquema.Required.Should().NotContain("referencia");
    }

    [Fact]
    public void ElFormularioLeeLosCamposRetiradosSoloParaAvisarlos()
    {
        var con = JsonSerializer.Deserialize<clsSolicitudCosteoWebRequest>(
            """{"tipo":"spot","valor_estimado":12000000,"referencia":"OC-7788"}""", clsJsonWeb.Opciones)!;
        var sin = JsonSerializer.Deserialize<clsSolicitudCosteoWebRequest>("""{"tipo":"spot"}""", clsJsonWeb.Opciones)!;

        con.CamposRetirados().Should().Equal("valor_estimado", "referencia");
        sin.CamposRetirados().Should().BeEmpty();
    }

    [Fact]
    public void LaLicitacionLeeLosCamposRetiradosSoloParaAvisarlos()
    {
        var con = JsonSerializer.Deserialize<clsLicitacionWebRequest>("""{"referencia":"Pliego 2026-04"}""", clsJsonWeb.Opciones)!;

        con.CamposRetirados().Should().Equal("referencia");
    }

    [Fact]
    public void ElAvisoNombraLosCamposSinSusValores()
    {
        var logger = new clsLoggerFalso<clsCamposRetiradosTests>();

        clsCamposRetirados.Registrar(logger, "POST /web/solicitudes-costeo", ["valor_estimado", "referencia"]);
        clsCamposRetirados.Registrar(logger, "POST /web/licitaciones", []);

        var registro = logger.Registros.Should().ContainSingle().Subject;
        registro.Nivel.Should().Be(LogLevel.Warning);
        registro.Mensaje.Should().Contain("CAMPOS_RETIRADOS").And.Contain("POST /web/solicitudes-costeo").And.Contain("valor_estimado, referencia");
    }
}
