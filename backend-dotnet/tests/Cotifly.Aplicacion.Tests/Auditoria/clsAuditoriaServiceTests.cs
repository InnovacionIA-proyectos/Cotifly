using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Auditoria;

/// <summary>
/// Auditoría transversal (port de <c>mcp_server/auditoria.py</c>): herramienta, entrada y resumen JSON (máximo 4000
/// caracteres, siempre JSON válido), éxito, duración e hilo; ámbito propio por evento; nunca interrumpe al llamador.
/// </summary>
public sealed class clsAuditoriaServiceTests
{
    private readonly IClsAuditoriaRepository _repositorio = Substitute.For<IClsAuditoriaRepository>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly clsLoggerFalso<clsAuditoriaService> _logger = new();
    private readonly List<clsEventoHerramienta> _eventos = [];
    private readonly IServiceScopeFactory _ambitos = Substitute.For<IServiceScopeFactory>();
    private int _ambitosCreados;

    public clsAuditoriaServiceTests()
    {
        _repositorio.When(r => r.Registrar(Arg.Any<clsEventoHerramienta>())).Do(ci => _eventos.Add(ci.Arg<clsEventoHerramienta>()));
        _ambitos.CreateScope().Returns(_ =>
        {
            Interlocked.Increment(ref _ambitosCreados);
            var proveedor = Substitute.For<IServiceProvider>();
            proveedor.GetService(typeof(IClsAuditoriaRepository)).Returns(_repositorio);
            proveedor.GetService(typeof(IClsUnitOfWork)).Returns(_unidadTrabajo);
            var ambito = Substitute.For<IServiceScope>();
            ambito.ServiceProvider.Returns(proveedor);
            return ambito;
        });
    }

    private clsAuditoriaService Servicio() => new(_ambitos, _logger);

    [Fact]
    public async Task RegistraElEventoConSuEntradaResumenExitoDuracionEHilo()
    {
        await Servicio().RegistrarAsync("generar_pdf", new { LineaNegocio = "Nacional", Folio = "PCN-1" }, new { CotizacionId = "x", RegistradaEnBd = true }, true, 42, "web/s-1");

        var evento = _eventos.Should().ContainSingle().Subject;
        evento.Herramienta.Should().Be("generar_pdf");
        evento.Input.Should().Be("""{"linea_negocio":"Nacional","folio":"PCN-1"}""");
        evento.OutputResumen.Should().Be("""{"cotizacion_id":"x","registrada_en_bd":true}""");
        evento.Exito.Should().BeTrue();
        evento.DuracionMs.Should().Be(42);
        evento.ThreadId.Should().Be("web/s-1");
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CadaEventoUsaSuPropioAmbito()
    {
        await Servicio().RegistrarAsync("a", null, null, true, 1, null);
        await Servicio().RegistrarAsync("b", null, null, true, 1, null);

        _ambitosCreados.Should().Be(2);
    }

    [Fact]
    public async Task UnaSalidaQueNoEsObjetoSeEnvuelveEnValor()
    {
        await Servicio().RegistrarAsync("ping", null, "ok", true, 1, null);

        _eventos[0].OutputResumen.Should().Be("""{"valor":"ok"}""");
        _eventos[0].Input.Should().BeNull();
    }

    [Fact]
    public async Task UnaEntradaGiganteSeTruncaSinRomperElJson()
    {
        await Servicio().RegistrarAsync("generar_pdf", new { Texto = new string('x', 10_000) }, null, true, 1, null);

        var input = _eventos[0].Input!;
        using var json = JsonDocument.Parse(input);
        json.RootElement.GetProperty("truncado").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("preview").GetString()!.Length.Should().Be(clsAuditoriaService.MaxCaracteres);
    }

    [Fact]
    public async Task LasTildesSeGuardanSinEscapar()
    {
        await Servicio().RegistrarAsync("enviar_correo", new { Asunto = "Cotización — Botero Soto" }, null, true, 1, null);

        _eventos[0].Input.Should().Be("""{"asunto":"Cotización — Botero Soto"}""");
    }

    [Fact]
    public async Task SiLaBaseFallaNoInterrumpeAlLlamadorYDejaUnAviso()
    {
        _unidadTrabajo.GuardarCambiosAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("base caída"));

        var registrar = () => Servicio().RegistrarAsync("generar_pdf", null, null, true, 1, null);

        await registrar.Should().NotThrowAsync();
        _logger.Registros.Should().ContainSingle(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("generar_pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SiNoSePuedeCrearElAmbitoTampocoInterrumpe()
    {
        var fabrica = Substitute.For<IServiceScopeFactory>();
        fabrica.CreateScope().Throws(new ObjectDisposedException("proveedor"));

        var registrar = () => new clsAuditoriaService(fabrica, _logger).RegistrarAsync("x", null, null, true, 1, null);

        await registrar.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AuditarUnResultadoExitosoGuardaElValor()
    {
        var resultado = await Servicio().AuditarAsync("calcular", new { Costo = 100 }, "h", () => Task.FromResult(clsResultado<string>.Exito("listo")));

        resultado.Valor.Should().Be("listo");
        _eventos[0].Exito.Should().BeTrue();
        _eventos[0].OutputResumen.Should().Be("""{"valor":"listo"}""");
        _eventos[0].DuracionMs.Should().BeGreaterThanOrEqualTo(0);
        _eventos[0].ThreadId.Should().Be("h");
    }

    [Fact]
    public async Task AuditarUnRechazoGuardaElCodigoComoFallo()
    {
        var resultado = await Servicio().AuditarAsync<string>("generar_pdf", null, null,
            () => Task.FromResult<clsResultado<string>>(new clsErrorDominio(clsCodigosError.FolioDuplicado, "Ya existe.")));

        resultado.EsFallo.Should().BeTrue();
        _eventos[0].Exito.Should().BeFalse();
        _eventos[0].OutputResumen.Should().Be("""{"error":{"codigo":"FOLIO_DUPLICADO","mensaje":"Ya existe.","detalle":null}}""");
    }

    [Fact]
    public async Task AuditarUnaExcepcionLaRegistraComoFalloYLaPropaga()
    {
        var auditar = () => Servicio().AuditarAsync<string>("generar_pdf", null, null, () => throw new InvalidOperationException("inesperado"));

        await auditar.Should().ThrowAsync<InvalidOperationException>();
        _eventos[0].Exito.Should().BeFalse();
        _eventos[0].OutputResumen.Should().Be("""{"error":{"codigo":"EXCEPCION"}}""");
    }

    [Fact]
    public async Task ElAjusteDeMargenNegociadoSeAudita()
    {
        var interno = Substitute.For<IClsAjusteMargenService>();
        var salida = new clsSalidaAjusteMargen(new clsSalidaMargen(0.12m, "negociado", null, false, "detalle"), true, -0.03m, Guid.NewGuid(), (clsSalidaPrecio?)null);
        interno.RegistrarAsync(Arg.Any<clsEntradaAjusteMargen>(), Arg.Any<CancellationToken>()).Returns(salida);

        var entrada = new clsEntradaAjusteMargen(0.15m, 0.12m, Guid.NewGuid(), ThreadId: "web/aud");
        await new clsAjusteMargenServiceAuditado(interno, Servicio()).RegistrarAsync(entrada, TestContext.Current.CancellationToken);

        var evento = _eventos.Should().ContainSingle().Subject;
        evento.Herramienta.Should().Be(clsAuditoriaService.HerramientaAjusteMargen);
        evento.ThreadId.Should().Be("web/aud");
        evento.Exito.Should().BeTrue();
        evento.Input.Should().Contain("\"margen_aplicado\":0.12");
        clsAuditoriaService.HerramientasAuditadasPorCasoDeUso.Should().BeEquivalentTo(["generar_pdf", "enviar_correo", "registrar_ajuste_margen"]);
    }
}
