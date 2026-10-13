using System.Globalization;
using System.Net;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/margen_sap_anomalo.feature</c> (HU-009, #139, B1): la API real conectada al Nexo falso de
/// <see cref="clsPasosCosteo"/> y a una fuente SAP TM que propone siempre el mismo <c>Value</c>.
/// </summary>
[Binding]
public sealed class clsPasosMargenSapAnomalo(clsPasosCosteo costeo, clsContextoWeb web)
{
    [Given("la API web de Cotifly conectada a Nexo con el cliente {string} de BP {string} y SAP TM que propone {string}")]
    public void DadoLaApiConSapFijo(string nit, string bp, string valor)
    {
        var porcentaje = decimal.Parse(valor, CultureInfo.InvariantCulture);
        costeo.Nexo.ConCliente(nit, $"CLIENTE {nit} S.A.S.", "cliente", bp);
        web.Iniciar(clsApiWebPruebas.ConNexo(clsEntornoAceptacion.BaseDatos, costeo.Nexo, dolarApi: costeo.DolarApi,
            superfinanciera: costeo.Superfinanciera, modelo: costeo.Modelo,
            extra: new Dictionary<string, string?>
            {
                ["IA:TiempoRedaccionSegundos"] = clsPasosCosteo.TiempoRedaccionSegundos.ToString(CultureInfo.InvariantCulture),
            },
            servicios: s => s.AddTransient<IClsFuenteMargen>(_ => new clsFuenteMargenFija(porcentaje))));
    }

    [Then("{string} ve en la ruta {int} de {string} el aviso de margen SAP anómalo de {string}")]
    public async Task EntoncesAvisa(string alias, int orden, string cotizacion, string margen)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var ruta = web.UltimoJson().GetProperty("rutas").EnumerateArray().Single(r => r.GetProperty("linea_orden").GetInt32() == orden);
        ruta.GetProperty("margen_sap_anomalo").GetBoolean().Should().BeTrue();
        ruta.GetProperty("aviso_margen").GetString().Should().Contain(margen).And.Contain("política");
    }

    /// <summary>SAP TM configurado que responde siempre el mismo <c>Value</c> (porcentaje de SAP: 75 = 75 %).</summary>
    private sealed class clsFuenteMargenFija(decimal valor) : IClsFuenteMargen
    {
        public string Modo => "fija";

        public bool EstaConfigurada => true;

        public Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default) =>
            Task.FromResult<decimal?>(valor);
    }
}
