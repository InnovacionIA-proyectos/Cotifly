using System.Globalization;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Precio;

/// <summary>Pasos de <c>specs/features/moneda_trm.feature</c> (HU-012): TRM oficial con el adaptador real contra los datos abiertos falsos.</summary>
[Binding]
public sealed class clsPasosMonedaTrm(clsContextoEscenario contexto)
{
    private clsResultado<clsTrm>? _trm;

    [Given("que la fecha de negocio es {string}")]
    public void DadoLaFechaDeNegocio(string fecha)
    {
        // 10:00 en Bogotá (UTC-5): la fecha de negocio y la UTC coinciden.
        var dia = Fecha(fecha);
        contexto.Reloj.AhoraUtc = new DateTimeOffset(dia.Year, dia.Month, dia.Day, 15, 0, 0, TimeSpan.Zero);
    }

    [Given("que la fuente de la TRM no responde")]
    public void DadoQueLaFuenteNoResponde() => contexto.TrmFalso.Caida(10);

    [When("consulto la TRM del día")]
    public Task CuandoConsultoLaTrm() => ConsultarAsync(1);

    [When("consulto la TRM del día {int} veces")]
    public Task CuandoConsultoLaTrmVariasVeces(int veces) => ConsultarAsync(veces);

    [Then("la TRM es {string} vigente del {string} al {string}")]
    public void EntoncesLaTrmEs(string valor, string desde, string hasta)
    {
        _trm.Should().NotBeNull();
        _trm!.EsExitoso.Should().BeTrue(_trm.EsFallo ? _trm.Error.Mensaje : string.Empty);
        _trm.Valor.Valor.Should().Be(clsPasosPrecio.Fraccion(valor));
        _trm.Valor.VigenciaDesde.Should().Be(Fecha(desde));
        _trm.Valor.VigenciaHasta.Should().Be(Fecha(hasta));
    }

    [Then("la fuente de la TRM recibió {int} consulta(s)")]
    public void EntoncesLaFuenteRecibio(int consultas) => contexto.TrmFalso.Peticiones.Should().HaveCount(consultas);

    [Then("la consulta falla con el código {string}")]
    public void EntoncesLaConsultaFalla(string codigo)
    {
        _trm.Should().NotBeNull();
        _trm!.EsFallo.Should().BeTrue();
        _trm.Error.Codigo.Should().Be(codigo);
    }

    internal static DateOnly Fecha(string texto) => DateOnly.ParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task ConsultarAsync(int veces)
    {
        for (var i = 0; i < veces; i++)
        {
            _trm = await contexto.ConsultarAsync(s =>
                s.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(s.GetRequiredService<IClsCalendarioNegocio>().Hoy));
        }
    }
}
