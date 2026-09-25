using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// Reglas del folio del servidor (ADR-011) con puertos simulados: fecha de negocio, consecutivo por comercial, salto de folios
/// ya tomados, NIT, agotamiento, versiones y candado. La concurrencia real (sp_getapplock) se prueba en integración.
/// </summary>
public sealed class clsGeneradorFolioServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCalendarioNegocio _calendario = Substitute.For<IClsCalendarioNegocio>();
    private readonly clsCandadoDirecto _candado = new();
    private readonly Guid _ana = Guid.NewGuid();

    public clsGeneradorFolioServiceTests() => _calendario.Hoy.Returns(new DateOnly(2026, 9, 25));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsGeneradorFolioService Servicio() => new(_candado, _cotizaciones, _calendario);

    private static Task<clsResultado<clsFolio>> Devolver(clsFolio folio, CancellationToken _) => Task.FromResult(clsResultado<clsFolio>.Exito(folio));

    [Fact]
    public async Task LaPrimeraCotizacionDelDiaEsLa001ConLaFechaDeNegocioYElCandadoDelDia()
    {
        var folio = await Servicio().CrearConFolioNuevoAsync(_ana, null, Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-25092026-001");
        folio.Valor.Version.Should().Be(1);
        _candado.Recursos.Should().Equal("cotifly:folio:25092026");
        await _cotizaciones.Received(1).ObtenerUltimoConsecutivoDelDiaAsync(_ana, "25092026", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElConsecutivoSigueAlUltimoDelComercialEseDiaYLlevaElNit()
    {
        _cotizaciones.ObtenerUltimoConsecutivoDelDiaAsync(_ana, "25092026", Arg.Any<CancellationToken>()).Returns(4);

        var folio = await Servicio().CrearConFolioNuevoAsync(_ana, "900.123.456", Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-900123456-25092026-005");
    }

    [Fact]
    public async Task UnFolioQueYaTieneOtraCotizacionSeSalta()
    {
        _cotizaciones.ExisteFolioAsync("PCN-25092026-001", Arg.Any<CancellationToken>()).Returns(true);
        _cotizaciones.ExisteFolioAsync("PCN-25092026-002", Arg.Any<CancellationToken>()).Returns(true);

        var folio = await Servicio().CrearConFolioNuevoAsync(_ana, null, Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-25092026-003");
    }

    [Fact]
    public async Task DespuesDel999NoHayMasFoliosYNoSeCreaNada()
    {
        _cotizaciones.ObtenerUltimoConsecutivoDelDiaAsync(_ana, "25092026", Arg.Any<CancellationToken>()).Returns(999);
        var creadas = 0;

        var folio = await Servicio().CrearConFolioNuevoAsync(_ana, null, (f, ct) =>
        {
            creadas++;
            return Devolver(f, ct);
        }, Ct);

        folio.Error.Codigo.Should().Be(clsCodigosError.FolioConsecutivoAgotado);
        creadas.Should().Be(0);
    }

    [Fact]
    public async Task ElFalloDeLaCreacionSeDevuelveTalCual()
    {
        var error = new clsErrorDominio(clsCodigosError.PdfPayloadInvalido, "payload");

        var resultado = await Servicio().CrearConFolioNuevoAsync<clsFolio>(_ana, null, (_, _) => Task.FromResult<clsResultado<clsFolio>>(error), Ct);

        resultado.Error.Should().Be(error);
    }

    [Fact]
    public async Task SinComercialNoSeGeneraFolio()
    {
        var resultado = await Servicio().CrearConFolioNuevoAsync(Guid.Empty, null, Devolver, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
        _candado.Recursos.Should().BeEmpty();
    }

    [Fact]
    public async Task LaVersionNuevaEsLaMaximaDeLaFamiliaMasUnoSobreElFolioBase()
    {
        var v2 = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-25092026-004-v2", Version = 2, UsuarioId = _ana }).Valor;
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(v2.Id, _ana, Arg.Any<CancellationToken>()).Returns(v2);
        _cotizaciones.ObtenerVersionMaximaAsync("PCN-25092026-004", Arg.Any<CancellationToken>()).Returns(3);

        var folio = await Servicio().CrearNuevaVersionAsync(_ana, v2.Id, Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-25092026-004-v4");
        folio.Valor.Version.Should().Be(4);
        _candado.Recursos.Should().Equal("cotifly:folio-version:PCN-25092026-004");
    }

    [Fact]
    public async Task NoSeVersionaUnaCotizacionAjenaOInexistente()
    {
        var resultado = await Servicio().CrearNuevaVersionAsync(_ana, Guid.NewGuid(), Devolver, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        _candado.Recursos.Should().BeEmpty();
    }

    /// <summary>Candado que ejecuta la operación en el acto y registra el recurso pedido.</summary>
    private sealed class clsCandadoDirecto : IClsCandadoTransaccional
    {
        public List<string> Recursos { get; } = [];

        public Task<clsResultado<TValor>> EjecutarAsync<TValor>(string recurso, Func<CancellationToken, Task<clsResultado<TValor>>> operacion, CancellationToken cancellationToken = default)
        {
            Recursos.Add(recurso);
            return operacion(cancellationToken);
        }
    }
}
