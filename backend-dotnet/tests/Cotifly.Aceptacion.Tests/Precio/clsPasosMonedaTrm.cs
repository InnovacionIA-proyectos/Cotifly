using System.Globalization;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Precio;

/// <summary>
/// Pasos de <c>specs/features/moneda_trm.feature</c> (HU-012): TRM oficial con el adaptador real contra los datos abiertos falsos,
/// conversión con el caso de uso real y la cotización en USD generada y guardada con el registro real (el texto del PDF se verifica con los
/// pasos de <c>clsPasosPdf</c>).
/// </summary>
[Binding]
public sealed class clsPasosMonedaTrm(clsContextoEscenario contexto)
{
    private clsResultado<clsTrm>? _trm;
    private clsResultado<clsSalidaPrecio>? _precio;

    [Given("que la fecha de negocio es {string}")]
    public void DadoLaFechaDeNegocio(string fecha)
    {
        // 10:00 en Bogotá (UTC-5): la fecha de negocio y la UTC coinciden.
        var dia = Fecha(fecha);
        contexto.Reloj.AhoraUtc = new DateTimeOffset(dia.Year, dia.Month, dia.Day, 15, 0, 0, TimeSpan.Zero);
    }

    [Given("que la fuente de la TRM no responde")]
    public void DadoQueLaFuenteNoResponde() => contexto.TrmFalso.Caida(10);

    [Given("que la TRM se consulta con el proveedor {string}")]
    public void DadoElProveedorDeTrm(string proveedor) => contexto.Reconfigurar(new Dictionary<string, string?> { ["Trm:Proveedor"] = proveedor });

    [Given("que DolarApi publica la TRM {string} el {string}")]
    public void DadoQueDolarApiPublica(string valor, string fechaActualizacion)
    {
        contexto.DolarApiFalso.Valor = clsPasosPrecio.Fraccion(valor);
        contexto.DolarApiFalso.FechaActualizacion = DateTimeOffset.Parse(fechaActualizacion, CultureInfo.InvariantCulture);
    }

    [When("pasan {int} horas y DolarApi publica la TRM {string} el {string}")]
    public void CuandoPasanHorasYDolarApiPublica(int horas, string valor, string fechaActualizacion)
    {
        contexto.Reloj.AhoraUtc = contexto.Reloj.AhoraUtc.AddHours(horas);
        DadoQueDolarApiPublica(valor, fechaActualizacion);
    }

    [Given("que DolarApi no responde")]
    public void DadoQueDolarApiNoResponde() => contexto.DolarApiFalso.Caida(10);

    [Then("la TRM es {string} de la fuente {string}")]
    public void EntoncesLaTrmEsDeLaFuente(string valor, string fuente)
    {
        _trm.Should().NotBeNull();
        _trm!.EsExitoso.Should().BeTrue(_trm.EsFallo ? _trm.Error.Detalle ?? _trm.Error.Mensaje : string.Empty);
        _trm.Valor.Valor.Should().Be(clsPasosPrecio.Fraccion(valor));
        _trm.Valor.Fuente.Should().Be(fuente);
    }

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

    [When("calculo en {string} el precio de un costo de {int} con margen {string} por el método {string}")]
    public async Task CuandoCalculoEnMoneda(string moneda, int costo, string margen, string metodo) =>
        _precio = await contexto.ConsultarAsync(s =>
            s.GetRequiredService<IClsPrecioService>().CalcularAsync(costo, clsPasosPrecio.Fraccion(margen), metodo, moneda));

    [Then("el precio es {string} en {string} con la TRM {string}")]
    public void EntoncesElPrecioEnMoneda(string precio, string moneda, string trm)
    {
        var salida = Precio();
        salida.Precio.Should().Be(clsPasosPrecio.Fraccion(precio));
        salida.Moneda.Should().Be(moneda);
        salida.TrmAplicada.Should().Be(clsPasosPrecio.Fraccion(trm));
    }

    [Then("el precio en pesos es {int} y la utilidad en pesos {int}")]
    public void EntoncesElPrecioEnPesos(int precio, int utilidad)
    {
        var salida = Precio();
        salida.PrecioCop.Should().Be(precio);
        salida.Utilidad.Should().Be(utilidad);
    }

    [Then("el cálculo en la moneda pedida se rechaza con el código {string}")]
    public void EntoncesElCalculoSeRechaza(string codigo)
    {
        _precio.Should().NotBeNull();
        _precio!.EsFallo.Should().BeTrue();
        _precio.Error.Codigo.Should().Be(codigo);
    }

    [When("{string} cotiza en {string} la solicitud de ejemplo con un costo de {int} y margen {string} con el folio {string}")]
    public async Task CuandoCotizaEnMoneda(string nombreUsuario, string moneda, int costo, string margen, string folio)
    {
        // Como el agente (ADR-004): el precio sale de calcular_precio y la fila impresa y la trazabilidad, del mismo resultado.
        await CuandoCalculoEnMoneda(moneda, costo, margen, "margen");
        var salida = Precio();
        var payload = clsCotizacionPdfPayload.DesdeJson(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "cotizacion", "ejemplo-solicitud.json"))).Valor;
        payload.Tarifa!.Filas!.RemoveAt(1);
        payload.Tarifa.Filas[0].Tarifa = salida.Precio;
        payload.Tarifa.Filas[0].Moneda = salida.Moneda;
        var traza = new clsTrazabilidadCotizacion(
            Costo: salida.Costo,
            MargenPolitica: salida.Margen,
            MargenAplicado: salida.Margen,
            OrigenMargen: "politica_linea",
            MetodoCalculo: salida.Metodo,
            PrecioFinal: salida.Precio,
            Moneda: salida.Moneda,
            TrmAplicada: salida.TrmAplicada,
            PrecioCop: salida.PrecioCop);
        var entrada = new clsEntradaGenerarPdf(contexto.Usuarios[nombreUsuario], payload, folio, Trazabilidad: traza);

        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsGenerarPdfService>().GenerarAsync(entrada));
        contexto.UltimoErrorNegocio = resultado.EsFallo ? resultado.Error : null;
        contexto.UltimoPdf = resultado.EsExitoso
            ? await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().LeerAsync(folio + ".pdf"))
            : null;
    }

    [Then("la cotización {string} queda guardada en {string} con precio final {string} y TRM {string}")]
    public async Task EntoncesQuedaGuardadaEnMoneda(string folio, string moneda, string precio, string trm)
    {
        contexto.UltimoErrorNegocio.Should().BeNull(contexto.UltimoErrorNegocio?.Mensaje ?? string.Empty);
        var cotizacion = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio));

        cotizacion.Should().NotBeNull();
        cotizacion!.Moneda.Should().Be(moneda);
        cotizacion.PrecioFinal.Should().Be(clsPasosPrecio.Fraccion(precio));
        cotizacion.TrmAplicada.Should().Be(clsPasosPrecio.Fraccion(trm));
        cotizacion.CostoFinal.Should().Be(2_850_000m, "el costo se guarda en pesos (en la ruta, HU-021)");
    }

    internal static DateOnly Fecha(string texto) => DateOnly.ParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private clsSalidaPrecio Precio()
    {
        _precio.Should().NotBeNull();
        _precio!.EsExitoso.Should().BeTrue(_precio.EsFallo ? _precio.Error.Mensaje : string.Empty);
        return _precio.Valor;
    }

    private async Task ConsultarAsync(int veces)
    {
        for (var i = 0; i < veces; i++)
        {
            _trm = await contexto.ConsultarAsync(s =>
                s.GetRequiredService<IClsProveedorTrm>().ObtenerAsync(s.GetRequiredService<IClsCalendarioNegocio>().Hoy));
        }
    }
}
