using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Cotizaciones;

/// <summary>
/// Reglas del folio del servidor (ADR-011) con puertos simulados: iniciales del comercial, fecha de negocio, consecutivo por
/// iniciales y día, candado por iniciales y día, NIT, agotamiento y versiones. La concurrencia real (sp_getapplock) se prueba
/// en integración.
/// </summary>
public sealed class clsGeneradorFolioServiceTests
{
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsCalendarioNegocio _calendario = Substitute.For<IClsCalendarioNegocio>();
    private readonly clsCandadoDirecto _candado = new();
    private readonly Guid _duban;

    public clsGeneradorFolioServiceTests()
    {
        _calendario.Hoy.Returns(new DateOnly(2026, 9, 25));
        _duban = Comercial("dguerra", "Duban Guerra Castro");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsGeneradorFolioService Servicio() => new(_candado, _cotizaciones, _usuarios, _calendario);

    private static Task<clsResultado<clsFolio>> Devolver(clsFolio folio, CancellationToken _) => Task.FromResult(clsResultado<clsFolio>.Exito(folio));

    private Guid Comercial(string nombreUsuario, string? nombreCompleto)
    {
        var usuario = clsUsuarioWeb.Crear(nombreUsuario, nombreCompleto, null, null, DateTimeOffset.UtcNow).Valor;
        _usuarios.ObtenerPorIdAsync(usuario.Id, Arg.Any<CancellationToken>()).Returns(usuario);
        return usuario.Id;
    }

    [Fact]
    public async Task LaPrimeraCotizacionDelDiaEsLa001ConLasInicialesYElCandadoDeLasInicialesDelDia()
    {
        var folio = await Servicio().CrearConFolioNuevoAsync(_duban, null, Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-DG-25092026-001");
        folio.Valor.Version.Should().Be(1);
        _candado.Recursos.Should().Equal("cotifly:folio:25092026:DG");
        await _cotizaciones.Received(1).ObtenerUltimoConsecutivoDelDiaAsync("DG", "25092026", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElConsecutivoSigueAlUltimoDeLasInicialesEseDiaYLlevaElNit()
    {
        _cotizaciones.ObtenerUltimoConsecutivoDelDiaAsync("DG", "25092026", Arg.Any<CancellationToken>()).Returns(4);

        var folio = await Servicio().CrearConFolioNuevoAsync(_duban, "900.123.456", Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-900123456-DG-25092026-005");
    }

    [Fact]
    public async Task SinNombreCompletoLasInicialesSalenDelNombreDeUsuario()
    {
        var nathaly = Comercial("nvargas", null);

        var folio = await Servicio().CrearConFolioNuevoAsync(nathaly, null, Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-NV-25092026-001");
        _candado.Recursos.Should().Equal("cotifly:folio:25092026:NV");
    }

    [Fact]
    public async Task NoSeBuscanFoliosLibresFueraDelCandado()
    {
        await Servicio().CrearConFolioNuevoAsync(_duban, null, Devolver, Ct);

        await _cotizaciones.DidNotReceiveWithAnyArgs().ExisteFolioAsync(default!, Ct);
    }

    [Fact]
    public async Task DespuesDel999NoHayMasFoliosYNoSeCreaNada()
    {
        _cotizaciones.ObtenerUltimoConsecutivoDelDiaAsync("DG", "25092026", Arg.Any<CancellationToken>()).Returns(999);
        var creadas = 0;

        var folio = await Servicio().CrearConFolioNuevoAsync(_duban, null, (f, ct) =>
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

        var resultado = await Servicio().CrearConFolioNuevoAsync<clsFolio>(_duban, null, (_, _) => Task.FromResult<clsResultado<clsFolio>>(error), Ct);

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
    public async Task UnComercialQueNoExisteEnUsuariosWebNoRecibeFolio()
    {
        var resultado = await Servicio().CrearConFolioNuevoAsync(Guid.NewGuid(), null, Devolver, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
        _candado.Recursos.Should().BeEmpty();
    }

    [Fact]
    public async Task LaVersionNuevaEsLaMaximaDeLaFamiliaMasUnoSobreElFolioBase()
    {
        var v2 = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-DG-25092026-004-v2", Version = 2, UsuarioId = _duban }).Valor;
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(v2.Id, _duban, Arg.Any<CancellationToken>()).Returns(v2);
        _cotizaciones.ObtenerVersionMaximaAsync("PCN-DG-25092026-004", Arg.Any<CancellationToken>()).Returns(3);

        var folio = await Servicio().CrearNuevaVersionAsync(_duban, v2.Id, Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-DG-25092026-004-v4");
        folio.Valor.Version.Should().Be(4);
        _candado.Recursos.Should().Equal("cotifly:folio-version:PCN-DG-25092026-004");
    }

    [Fact]
    public async Task LaVersionNuevaDeUnFolioHeredadoSinInicialesLlevaV2SobreSuBase()
    {
        var heredada = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-900123456-25092026-001", UsuarioId = _duban }).Valor;
        _cotizaciones.ObtenerPorIdDelUsuarioAsync(heredada.Id, _duban, Arg.Any<CancellationToken>()).Returns(heredada);
        _cotizaciones.ObtenerVersionMaximaAsync("PCN-900123456-25092026-001", Arg.Any<CancellationToken>()).Returns(1);

        var folio = await Servicio().CrearNuevaVersionAsync(_duban, heredada.Id, Devolver, Ct);

        folio.Valor.Valor.Should().Be("PCN-900123456-25092026-001-v2");
        folio.Valor.Version.Should().Be(2);
        folio.Valor.Iniciales.Should().BeNull();
        _candado.Recursos.Should().Equal("cotifly:folio-version:PCN-900123456-25092026-001");
        await _usuarios.DidNotReceiveWithAnyArgs().ObtenerPorIdAsync(default, Ct);
    }

    [Fact]
    public async Task NoSeVersionaUnaCotizacionAjenaOInexistente()
    {
        var resultado = await Servicio().CrearNuevaVersionAsync(_duban, Guid.NewGuid(), Devolver, Ct);

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
