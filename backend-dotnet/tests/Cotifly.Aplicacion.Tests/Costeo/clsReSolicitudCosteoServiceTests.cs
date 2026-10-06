using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// "Cotizar costo" (HU-013.4, ADR-018 §5): re-solicitar es una captura nueva en Nexo con lo guardado (Nexo no tiene "reabrir"), en el
/// mismo folio (reclamado con el control de versión, sin el costeo anterior y con el PDF invalidado) o en la versión <c>-vN</c> (la
/// original no se toca), con idempotencia y auditoría.
/// </summary>
public sealed class clsReSolicitudCosteoServiceTests
{
    private const int Version = 4;
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsGeneradorFolioService _folios = Substitute.For<IClsGeneradorFolioService>();
    private readonly IClsEnvioCosteoService _envio = Substitute.For<IClsEnvioCosteoService>();
    private readonly IClsMisCotizacionesService _mis = Substitute.For<IClsMisCotizacionesService>();
    private readonly IClsAlmacenIdempotencia _idempotencia = Substitute.For<IClsAlmacenIdempotencia>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsMargenPolitica _politica = clsMargenPolitica.Crear("Importacion", 0.15m).Valor;
    private readonly List<string> _eventos = [];
    private readonly clsCotizacion _cotizacion;
    private readonly IReadOnlyList<clsCotizacionLineaSolicitud> _lineas;
    private readonly IReadOnlyList<clsCotizacionPosicion> _posiciones;
    private clsCotizacion? _agregada;
    private IReadOnlyList<clsCotizacionLineaSolicitud> _lineasAgregadas = [];
    private IReadOnlyList<clsCotizacionPosicion> _posicionesAgregadas = [];

    public clsReSolicitudCosteoServiceTests()
    {
        (_cotizacion, _lineas, _posiciones) = Vencida(_ana, "PCN-900555111-AP-05102026-001", 2_550_000m);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ListarLineasAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns(_lineas);
        _cotizaciones.ListarPosicionesAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns(_posiciones);
        _cotizaciones.When(r => r.Agregar(Arg.Any<clsCotizacion>())).Do(ci => _agregada = ci.Arg<clsCotizacion>());
        _cotizaciones.When(r => r.AgregarLineas(Arg.Any<IEnumerable<clsCotizacionLineaSolicitud>>()))
            .Do(ci => _lineasAgregadas = [.. ci.Arg<IEnumerable<clsCotizacionLineaSolicitud>>()]);
        _cotizaciones.When(r => r.AgregarPosiciones(Arg.Any<IEnumerable<clsCotizacionPosicion>>()))
            .Do(ci => _posicionesAgregadas = [.. ci.Arg<IEnumerable<clsCotizacionPosicion>>()]);
        _politicas.ObtenerPorIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_politica);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _eventos.Add($"reclamar:{clsCodigoEnum.ACodigo(_cotizacion.EstadoCosteo!.Value)}");
            return true;
        });
        _envio.EnviarGuardadaAsync(Arg.Any<clsCotizacion>(), Arg.Any<IReadOnlyList<clsCotizacionLineaSolicitud>>(),
                Arg.Any<IReadOnlyList<clsCotizacionPosicion>>(), Arg.Any<clsMargenPolitica?>(), Arg.Any<Guid>())
            .Returns(ci =>
            {
                _eventos.Add("nexo");
                ci.Arg<clsCotizacion>().RegistrarEnvioCosteo("sol-nueva", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5));
                return Task.CompletedTask;
            });
        _mis.ResumirAsync(Arg.Any<clsCotizacion>(), Arg.Any<CancellationToken>())
            .Returns(ci => clsCotizacionResumen.Desde(ci.Arg<clsCotizacion>(), "Importacion", null));
        _folios.CrearNuevaVersionAsync(_ana, _cotizacion.Id, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacion>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacion>>>>()(
                clsFolio.Desde(_cotizacion.Folio).Valor.ConVersion(2).Valor, CancellationToken.None));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnElMismoFolioReclamaConLaVersionOlvidaElCosteoAnteriorYLuegoEnviaANexo()
    {
        _cotizacion.AsignarPdf("/data/pdfs/PCN-1.pdf");

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.VersionNueva.Should().BeFalse();
        resultado.Valor.Cotizacion.Folio.Should().Be(_cotizacion.Folio);
        resultado.Valor.RowVersion.Should().Be("5", "la re-solicitud sube la versión de negocio");
        _eventos.Should().Equal("reclamar:pendiente_envio", "nexo");
        _cotizacion.CosteoId.Should().Be("sol-nueva");
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        _cotizacion.Costo.Should().BeNull();
        _cotizacion.PrecioFinal.Should().BeNull();
        _cotizacion.PdfPath.Should().BeNull();
        _cotizacion.PdfDesactualizado.Should().BeTrue();
        _lineas[0].Costo.Should().BeNull();
        _lineas[0].LineaSolicitudNexoId.Should().BeNull("las líneas de la solicitud nueva son otras en Nexo");
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h => h.Motivo == MotivoInvalidacionPdf.ReSolicitud && h.UsuarioId == _ana));
        await _envio.Received(1).EnviarGuardadaAsync(_cotizacion, _lineas, _posiciones, _politica, _ana);
        await _auditoria.Received(1).RegistrarAsync(clsReSolicitudCosteoService.HerramientaAuditoria,
            Arg.Is<object?>(e => Json(e).Contains("\"motivo\":\"El costeo venció\"") && Json(e).Contains(_ana.ToString())),
            Arg.Is<object?>(s => Json(s).Contains("\"costo\":2550000") && Json(s).Contains("sol-vieja")),
            true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SiOtroCambioGanaElReclamoRespondeConflictoSinLlamarANexo()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task ConUnaVersionViejaRespondeConflictoSinTocarNada()
    {
        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version - 1, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _cotizacion.Costo.Should().Be(2_550_000m);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task SinMotivoEs400(string? motivo)
    {
        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { Motivo = motivo }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain("motivo");
    }

    [Fact]
    public async Task UnMotivoDeMasDe300CaracteresEs400()
    {
        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { Motivo = new string('x', 301) }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
    }

    [Fact]
    public async Task UnaAjenaEsNoEncontradaYNoSeAudita()
    {
        var resultado = await Servicio().ReSolicitarAsync(Guid.NewGuid(), _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public async Task YaEnviadaElMismoFolioEsPdfYaEnviadoConLaLlamadaDeLaVersionNueva()
    {
        _cotizacion.RegistrarEnvio();

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        resultado.Error.Datos!["endpoint"].Should().Be($"/web/cotizaciones/{_cotizacion.Folio}/costeo/re-solicitar");
        ((IReadOnlyDictionary<string, object?>)resultado.Error.Datos["cuerpo"]!)["nueva_version"].Should().Be(true);
        await _auditoria.Received(1).RegistrarAsync(clsReSolicitudCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ConCambiosDeCargaCreaLaVersionNuevaConLosDatosCopiadosYLaEnviaSinTocarLaOriginal()
    {
        var cambios = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoPesoKg, clsCambiosCarga.CampoTipoVehiculo },
            PesoKg = 28_400m,
            TipoVehiculo = "2S3",
        };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.VersionNueva.Should().BeTrue();
        var nueva = _agregada!;
        nueva.Folio.Should().Be($"{_cotizacion.Folio}-v2");
        nueva.Version.Should().Be(2);
        nueva.NitCliente.Should().Be(_cotizacion.NitCliente);
        nueva.UsuarioId.Should().Be(_ana);
        nueva.PesoKg.Should().Be(28_400m);
        nueva.TipoVehiculo.Should().Be("2S3");
        nueva.Carroceria.Should().Be("PORTACONTENEDORES");
        nueva.CosteoId.Should().Be("sol-nueva");
        _lineasAgregadas.Should().ContainSingle().Which.PesoKg.Should().Be(28_400m);
        _posicionesAgregadas.Should().HaveCount(2).And.OnlyContain(p => p.CotizacionId == nueva.Id && p.LineaSolicitudId == _lineasAgregadas[0].Id);
        await _unidad.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        await _envio.Received(1).EnviarGuardadaAsync(nueva,
            Arg.Is<IReadOnlyList<clsCotizacionLineaSolicitud>>(l => l.Count == 1 && l[0].Id == _lineasAgregadas[0].Id),
            Arg.Is<IReadOnlyList<clsCotizacionPosicion>>(p => p.Count == 2), _politica, _ana);
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido, "la original no se toca");
        _cotizacion.CosteoId.Should().Be("sol-vieja");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task CambiosDeCargaQueRompenLaReglaDePesoNoCreanVersion()
    {
        var cambios = new clsCambiosCarga { Presentes = new HashSet<string> { clsCambiosCarga.CampoPesoKg }, PesoKg = 0m };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Detalle.Should().Be(clsSolicitudCosteo.PesoRequerido);
        _agregada.Should().BeNull();
        await _folios.DidNotReceiveWithAnyArgs().CrearNuevaVersionAsync<clsCotizacion>(default, default, default!, Ct);
    }

    [Fact]
    public async Task DatosDeCargaVaciosSonInvalidos()
    {
        var cambios = new clsCambiosCarga { Presentes = new HashSet<string>() };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain("datos_carga");
    }

    [Fact]
    public async Task UnaLicitacionNoAdmiteCambiosDeCarga()
    {
        var segunda = clsCotizacionLineaSolicitud.Desde(_cotizacion.Id, [_lineas[0].ALinea([]), _lineas[0].ALinea([])]);
        _cotizaciones.ListarLineasAsync(_cotizacion.Id, Arg.Any<CancellationToken>()).Returns(segunda);
        var cambios = new clsCambiosCarga { Presentes = new HashSet<string> { clsCambiosCarga.CampoPesoKg }, PesoKg = 1000m };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        resultado.Error.Detalle.Should().Be("LICITACION");
    }

    [Fact]
    public async Task ConIdempotencyKeyEjecutaUnaSolaVezPorComercialYHuella()
    {
        _idempotencia.EjecutarUnaVezAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<clsResultado<clsReSolicitudCosteo>>>>(), Arg.Any<Func<clsResultado<clsReSolicitudCosteo>, bool>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task<clsResultado<clsReSolicitudCosteo>>>>()(CancellationToken.None));

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, "clave-12345", Ct);

        resultado.EsExitoso.Should().BeTrue();
        await _idempotencia.Received(1).EjecutarUnaVezAsync(
            $"{clsReSolicitudCosteoService.OperacionIdempotencia}:{_ana}", "clave-12345",
            clsReSolicitudCosteoService.Huella(_cotizacion.Folio, Entrada()),
            Arg.Any<Func<CancellationToken, Task<clsResultado<clsReSolicitudCosteo>>>>(), Arg.Any<Func<clsResultado<clsReSolicitudCosteo>, bool>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LaHuellaCambiaConElCuerpoYNoConElOrdenDeLosCampos()
    {
        var a = new clsCambiosCarga { Presentes = new HashSet<string> { "peso_kg", "tipo_vehiculo" }, PesoKg = 1m, TipoVehiculo = "2S3" };
        var b = new clsCambiosCarga { Presentes = new HashSet<string> { "tipo_vehiculo", "peso_kg" }, PesoKg = 1m, TipoVehiculo = "2S3" };

        clsReSolicitudCosteoService.Huella("F", Entrada() with { DatosCarga = a })
            .Should().Be(clsReSolicitudCosteoService.Huella("F", Entrada() with { DatosCarga = b }));
        clsReSolicitudCosteoService.Huella("F", Entrada()).Should().NotBe(clsReSolicitudCosteoService.Huella("F", Entrada() with { Motivo = "Otro" }));
        clsReSolicitudCosteoService.Huella("F", Entrada()).Should().NotBe(clsReSolicitudCosteoService.Huella("G", Entrada()));
    }

    private clsReSolicitudCosteoService Servicio() =>
        new(_cotizaciones, _politicas, _folios, _envio, _mis, _idempotencia, _unidad, _auditoria);

    private static clsEntradaReSolicitudCosteo Entrada() => new("El costeo venció", null, false);

    private static readonly System.Text.Json.JsonSerializerOptions _json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Json(object? valor) => System.Text.Json.JsonSerializer.Serialize(valor, _json);

    /// <summary>Cotización del formulario enviada a Nexo (solicitud <c>sol-vieja</c>), con costo y en <c>costeo_vencido</c>.</summary>
    public static (clsCotizacion, IReadOnlyList<clsCotizacionLineaSolicitud>, IReadOnlyList<clsCotizacionPosicion>) Vencida(
        Guid usuarioId, string folio, decimal costo)
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = folio,
            UsuarioId = usuarioId,
            NitCliente = "900555111",
            LineaNegocioId = 3,
            MargenPolitica = 0.15m,
            MargenAplicado = 0.15m,
            OrigenMargen = OrigenMargen.PoliticaLinea,
            MetodoCalculo = "margen",
        }).Valor;
        var solicitud = clsSolicitudCosteoMuestra.Crear();
        cotizacion.RegistrarSolicitudCosteo(solicitud);
        var lineas = clsCotizacionLineaSolicitud.Desde(cotizacion.Id, [solicitud.ALinea()]);
        var posiciones = clsCotizacionPosicion.Desde(cotizacion.Id, solicitud.Posiciones, lineas[0].Id, 1);
        cotizacion.RegistrarEnvioCosteo("sol-vieja", false, DateTimeOffset.UtcNow.AddHours(-73), DateTimeOffset.UtcNow);
        lineas[0].AsignarLineaNexo("linea-vieja");
        lineas[0].RegistrarCosteo(EstadoCosteo.PendienteCosteo, costo);
        cotizacion.RegistrarPrecioCosteo(costo, 3_000_000m, null, DateTimeOffset.UtcNow);
        cotizacion.VencerPollingCosteo("Sin costeo de Nexo en 72 h.");
        typeof(clsCotizacion).GetProperty(nameof(clsCotizacion.VersionNegocio))!.SetValue(cotizacion, Version);
        return (cotizacion, lineas, posiciones);
    }
}
