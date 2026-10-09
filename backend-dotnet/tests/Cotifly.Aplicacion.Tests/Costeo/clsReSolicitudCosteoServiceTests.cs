using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly IClsAuditoriaRepository _registro = Substitute.For<IClsAuditoriaRepository>();
    private readonly IClsValidadorCatalogosCaptura _catalogos = Substitute.For<IClsValidadorCatalogosCaptura>();
    private readonly IClsValidadorFilasLicitacion _filas = FilasQuePasan();
    private readonly List<clsEventoHerramienta> _instantaneas = [];
    private readonly IClsFijacionMonedaService _monedas = Substitute.For<IClsFijacionMonedaService>();
    private readonly IClsPlanificadorCamiones _planificador = Substitute.For<IClsPlanificadorCamiones>();
    private readonly IClsCostosConexosNexo _costos = Substitute.For<IClsCostosConexosNexo>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsMargenPolitica _politica = clsMargenPolitica.Crear("Importacion", 0.15m).Valor;
    private readonly List<string> _eventos = [];
    private readonly clsCotizacionSolicitud _cotizacion;
    private clsCotizacionSolicitud? _agregada;

    public clsReSolicitudCosteoServiceTests()
    {
        _cotizacion = Vencida(_ana, "PCN-900555111-AP-05102026-001", 2_550_000m);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.When(r => r.Agregar(Arg.Any<clsCotizacionSolicitud>())).Do(ci => _agregada = ci.Arg<clsCotizacionSolicitud>());
        _politicas.ObtenerPorIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_politica);
        _monedas.FijarAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new clsMonedaFijada(ci.Arg<string?>() ?? "COP", ci.Arg<string?>() == "USD" ? 4_012.35m : 0m));
        _registro.When(r => r.Registrar(Arg.Any<clsEventoHerramienta>())).Do(ci =>
        {
            _eventos.Add("instantanea");
            _instantaneas.Add(ci.Arg<clsEventoHerramienta>());
        });
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _eventos.Add($"reservar:{clsCodigoEnum.ACodigo(_cotizacion.EstadoCosteo!.Value)}:v{_cotizacion.VersionNegocio}");
            return true;
        });
        _envio.EnviarGuardadaAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<clsMargenPolitica?>(), Arg.Any<Guid>(), Arg.Any<string?>())
            .Returns(ci =>
            {
                _eventos.Add("nexo");
                ci.Arg<clsCotizacionSolicitud>().RegistrarEnvioCosteo("sol-nueva", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5));
                return Task.CompletedTask;
            });
        _mis.ResumirAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns(ci => clsCotizacionResumen.Desde(ci.Arg<clsCotizacionSolicitud>(), "Importacion", null));
        _folios.CrearNuevaVersionAsync(_ana, _cotizacion.Id, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>()(
                clsFolio.Desde(_cotizacion.Folio).Valor.ConVersion(2).Valor, CancellationToken.None));

        // El planificador por defecto (hasta el cubicador, #133): un camión sin vehículo con el peso total de la ruta.
        _planificador.PlanificarAsync(Arg.Any<Guid>(), Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(ci => new clsPlanificadorCamionSinVehiculo().PlanificarAsync(ci.ArgAt<Guid>(0), ci.ArgAt<clsLineaSolicitudCosteo>(1)));
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
        _eventos.Should().Equal("instantanea", "reservar:pendiente_envio:v5", "nexo");
        _cotizacion.SolicitudNexoId.Should().Be("sol-nueva");
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        _cotizacion.CostoFinal.Should().BeNull();
        _cotizacion.PrecioFinal.Should().BeNull();
        _cotizacion.PdfPath.Should().BeNull();
        _cotizacion.PdfDesactualizado.Should().BeTrue();
        _cotizacion.Vehiculos[0].CostoFlete.Should().BeNull();
        _cotizacion.Vehiculos[0].LineaSolicitudNexoId.Should().BeNull("los camiones de la solicitud nueva son otras líneas en Nexo");
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h => h.Motivo == MotivoInvalidacionPdf.ReSolicitud && h.UsuarioId == _ana));
        await _envio.Received(1).EnviarGuardadaAsync(_cotizacion, _politica, _ana, $"Re-solicitud de {_cotizacion.Folio}; reemplaza la solicitud sol-vieja.");
        var instantanea = _instantaneas.Should().ContainSingle().Subject;
        instantanea.Herramienta.Should().Be(clsReSolicitudCosteoService.HerramientaAuditoria);
        instantanea.ThreadId.Should().BeNull("los costos no quedan al alcance del asistente (#102)");
        instantanea.Input.Should().Contain("El costeo venció").And.Contain(_ana.ToString());
        // HU-021: la instantánea es por ruta y camión; el costo y el precio de la ruta incluyen el conexo Cargue (85.000).
        instantanea.OutputResumen.Should().Contain("\"costo\":2635000").And.Contain("sol-vieja").And.Contain("linea-vieja")
            .And.Contain("\"costo_flete\":2550000").And.Contain("\"margen_aplicado\":0.15").And.Contain("\"margen_politica_sugerido\":0.15")
            .And.Contain("Cargue").And.Contain("\"precio_final\":3100000").And.Contain("PCN-1.pdf");
        await _auditoria.Received(1).RegistrarAsync(clsReSolicitudCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Is<object?>(s => Json(s).Contains("sol-nueva")), true, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task SiOtroCambioGanaElReclamoRespondeConflictoSinLlamarANexo()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
    }

    [Fact]
    public async Task ConUnaVersionViejaRespondeConflictoSinTocarNada()
    {
        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version - 1, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _cotizacion.Vehiculos[0].CostoFlete.Should().Be(2_550_000m);
        _cotizacion.SolicitudNexoId.Should().Be("sol-vieja");
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
        nueva.SolicitudNexoId.Should().Be("sol-nueva");

        // HU-021: la versión nueva es un agregado nuevo: la ruta con los cambios de carga, sus mercancías y su camión.
        var ruta = nueva.Rutas.Should().ContainSingle().Subject;
        ruta.PesoKg.Should().Be(28_400m);
        ruta.LineaNegocioId.Should().Be(3);
        ruta.Mercancias.Should().HaveCount(2).And.OnlyContain(m => m.LineaSolicitudId == ruta.Id);

        // HU-021.9: el peso cambió, así que el camión anterior (3S3) se borra y se vuelve a planificar; el tipo_vehiculo de datos_carga
        // se ignora (decisión 7: lo define el cubicador o la Mesa de Costeo de Nexo).
        var camion = ruta.Vehiculos.Should().ContainSingle().Subject;
        (camion.TipoVehiculo, camion.Carroceria, camion.PesoKg).Should().Be(((string?)null, (string?)null, 28_400m));
        // Las mercancías de la muestra no tienen id_tipo_mercancia (filas anteriores a #132): el cubicador no las puede cubicar y va el
        // respaldo de un camión sin vehículo con el peso de la ruta (corrección del auditor de #136).
        await _planificador.DidNotReceiveWithAnyArgs().PlanificarAsync(default, default!, Ct);
        resultado.Valor.RutasReplanificadas.Should().Equal(1);
        await _unidad.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        await _envio.Received(1).EnviarGuardadaAsync(nueva, _politica, _ana, Arg.Any<string?>());
        _cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido, "la original no cambia su costeo");
        _cotizacion.SolicitudNexoId.Should().Be("sol-vieja");
        _eventos.Should().StartWith(new[] { "instantanea", "reservar:costeo_vencido:v5" }, "la versión de la original se reserva antes de crear la nueva");
        await _catalogos.Received(1).ValidarAsync(_ana, Arg.Is<clsLineaSolicitudCosteo>(l => l.TipoVehiculo != "2S3"), Arg.Any<CancellationToken>());
    }

    // --- HU-021.9 (#136): la versión nueva copia por ruta y vuelve a planificar los camiones solo si cambian el peso o las mercancías ---

    [Fact]
    public async Task SinCambiosDeCargaLaVersionNuevaCopiaCadaRutaConSusCamionesSusConexosYSuLineaDeNegocio()
    {
        var licitacion = VencidaDeDosRutas();

        var resultado = await Servicio().ReSolicitarAsync(_ana, licitacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var nueva = _agregada!;
        nueva.Rutas.Should().HaveCount(2);
        var r1 = nueva.RutasOrdenadas[0];
        var r2 = nueva.RutasOrdenadas[1];
        (r1.LineaNegocioId, r2.LineaNegocioId).Should().Be((3, 7), "cada ruta conserva su línea de negocio");
        (r1.RutaOrigenIndice, r2.RutaOrigenIndice).Should().Be((0, 1));
        r1.Vehiculos.Select(v => (v.TipoVehiculo, v.Carroceria, v.PesoKg)).Should().BeEquivalentTo(
            [("3S3", "FURGON", (decimal?)10_001m), ("3S3", "FURGON", (decimal?)5_000m)]);
        r1.Vehiculos.Should().OnlyContain(v => v.LineaSolicitudNexoId == null && v.CostoFlete == null && v.EstadoCosteo == EstadoCosteo.PendienteCosteo,
            "camiones nuevos, sin el costeo de la original, enviados a Nexo como líneas nuevas");
        r2.Vehiculos.Should().ContainSingle();
        r2.Conexos.Should().ContainSingle(c => c.IdServicioConexo == "CX-9" && c.Nombre == "Escolta" && c.Precio == 42_500m && c.Cantidad == 2m);
        r2.CostoServiciosConexos.Should().Be(85_000m, "el conexo copiado suma al costo de su ruta");
        r1.Conexos.Should().BeEmpty();
        await _planificador.DidNotReceiveWithAnyArgs().PlanificarAsync(default, default!, Ct);
        resultado.Valor.RutasReplanificadas.Should().BeEmpty();
        resultado.Valor.ConexosConservados.Should().Equal("Escolta");
    }

    [Fact]
    public async Task UnCamionDescartadoEnNexoNoSeCopiaALaVersionNueva()
    {
        var licitacion = VencidaDeDosRutas();
        var descartado = licitacion.RutasOrdenadas[0].Vehiculos.Single(v => v.PesoKg == 5_000m);
        licitacion.DescartarCamionSinLineaEnNexo(descartado.Id).Should().BeTrue();

        var resultado = await Servicio().ReSolicitarAsync(_ana, licitacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _agregada!.RutasOrdenadas[0].Vehiculos.Should().ContainSingle().Which.PesoKg.Should().Be(10_001m);
    }

    /// <summary>
    /// Corrección del validador de #136: en el mismo folio un camión descartado en Nexo (la Mesa borró su línea) no se reenvía. La ruta
    /// tenía 450.000 + 450.000 + 1.000.000 (el descartado): reenviarlo daba 3 líneas y el precio subía de 1.058.824 a 2.235.294.
    /// </summary>
    [Fact]
    public async Task EnElMismoFolioUnCamionDescartadoNoSeReenviaYElPrecioNoSube()
    {
        var cotizacion = clsCotizacionMuestra.Formulario("PCN-900555111-AP-05102026-040", _ana);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.AgregarVehiculo("3S3", "FURGON", 10_000m);
        ruta.AgregarVehiculo("2S2", "FURGON", 5_000m);
        cotizacion.RegistrarEnvioCosteo("sol-vieja", false, DateTimeOffset.UtcNow.AddHours(-2), DateTimeOffset.UtcNow);
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        var camiones = ruta.Vehiculos.ToList();
        decimal[] fletes = [450_000m, 450_000m, 1_000_000m];
        for (var i = 0; i < camiones.Count; i++)
        {
            camiones[i].AsignarLineaNexo($"L{i + 1}");
            camiones[i].RegistrarCosteo(EstadoCosteo.Autocosteo, fletes[i], null);
        }

        cotizacion.DescartarCamionSinLineaEnNexo(camiones[2].Id).Should().BeTrue();
        cotizacion.RegistrarEntregaImplicita("COT-1");
        cotizacion.RecalcularTotales(DateTimeOffset.UtcNow);
        cotizacion.PrecioFinal.Should().Be(1_058_824m, "900.000 / 0,85: el descartado no cuenta");
        clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _unidad.GuardarCambiosSiVersionAsync(cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);

        var resultado = await Servicio().ReSolicitarAsync(_ana, cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var lineas = cotizacion.LineasNexo();
        lineas.Should().HaveCount(2, "el descartado no viaja otra vez a Nexo");
        lineas.Select(l => l.Camion.Id).Should().NotContain(camiones[2].Id);
        camiones[2].EstadoCosteo.Should().Be(EstadoCosteo.Descartada);

        // Nexo costea las 2 líneas nuevas con los mismos fletes.
        for (var n = 0; n < lineas.Count; n++)
        {
            var camion = lineas[n].Camion;
            camion.AsignarLineaNexo($"N{n + 1}");
            camion.RegistrarCosteo(EstadoCosteo.Autocosteo, 450_000m, null);
        }

        cotizacion.ResumirRutas();
        cotizacion.RecalcularTotales(DateTimeOffset.UtcNow);
        cotizacion.PrecioFinal.Should().Be(1_058_824m, "con el descartado reenviado serían 1.900.000 / 0,85 = 2.235.294");
    }

    [Fact]
    public async Task LaVersionNuevaRefrescaElCostoDeLosConexosConElCatalogoDeNexo()
    {
        var licitacion = VencidaDeDosRutas();
        var id = Guid.NewGuid().ToString("D");
        licitacion.RutasOrdenadas[0].AgregarOActualizarConexo(id, "Precinto", 10_000m, 3m);
        _costos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(
                new Dictionary<string, clsCostoConexoNexo> { [id] = new(id, "Precinto de seguridad", 12_500m) }));

        var resultado = await Servicio().ReSolicitarAsync(_ana, licitacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var copiado = _agregada!.RutasOrdenadas[0].Conexos.Should().ContainSingle().Subject;
        (copiado.Precio, copiado.Cantidad, copiado.Nombre).Should().Be((12_500m, 3m, "Precinto de seguridad"), "costo del catálogo de hoy, misma cantidad");
        _agregada.RutasOrdenadas[1].Conexos.Single().Precio.Should().Be(42_500m, "CX-9 no es un id del catálogo: conserva su costo");
        licitacion.RutasOrdenadas[0].Conexos.Single().Precio.Should().Be(10_000m, "la original no cambia");
        await _costos.Received(1).CostosAsync(_ana, Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1 && ids.Contains(id)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinCatalogoDeConexosLaVersionNuevaNoSeCrea()
    {
        var licitacion = VencidaDeDosRutas();
        licitacion.RutasOrdenadas[0].AgregarOActualizarConexo(Guid.NewGuid().ToString("D"), "Precinto", 10_000m, 1m);
        _costos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "Nexo no responde."));

        var resultado = await Servicio().ReSolicitarAsync(_ana, licitacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        _agregada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task ConLaMercanciaCambiadaSeBorranLosCamionesYSeVuelvenAPlanificar()
    {
        // La ruta tenía dos camiones (uno con vehículo): con otras mercancías ya no sirven.
        _cotizacion.PrimeraRuta!.AgregarVehiculo("2S2", "FURGON", 500m);
        var mercancias = new List<clsPosicionCarga> { new("Estiba de café", 10, 120m, 100m, 150m, 1425m, clsSolicitudCosteoMuestra.TipoMercancia) };
        var cambios = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoPosiciones, clsCambiosCarga.CampoPesoKg },
            Posiciones = mercancias,
            PesoKg = 14_250m,
        };
        _planificador.PlanificarAsync(_ana, Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsCamionPlaneado>>.Exito([new clsCamionPlaneado("2S3", "FURGON", 8_000m), new clsCamionPlaneado(null, null, 6_250m)]));

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var ruta = _agregada!.Rutas.Should().ContainSingle().Subject;
        ruta.Mercancias.Should().ContainSingle().Which.Cantidad.Should().Be(10);
        // Sin persistencia createdAt es el mismo: el orden estable cae en el id (aleatorio), así que se compara sin orden.
        ruta.Vehiculos.Select(v => (v.TipoVehiculo, v.PesoKg)).Should().BeEquivalentTo([("2S3", (decimal?)8_000m), ((string?)null, (decimal?)6_250m)]);
        await _planificador.Received(1).PlanificarAsync(_ana,
            Arg.Is<clsLineaSolicitudCosteo>(l => l.Posiciones.Count == 1 && l.PesoKg == 14_250m), Arg.Any<CancellationToken>());
        ruta.Conexos.Should().ContainSingle(c => c.Nombre == "Cargue", "los conexos de la ruta se copian aunque cambie la carga");
        _cotizacion.PrimeraRuta.Vehiculos.Should().HaveCount(2, "la original no cambia");
    }

    [Fact]
    public async Task UnPesoIgualAlGuardadoNoVuelveAPlanificarLosCamiones()
    {
        var cambios = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoPesoKg, clsCambiosCarga.CampoHorasCargue },
            PesoKg = _cotizacion.PrimeraRuta!.PesoKg,
            HorasCargue = 8m,
        };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var ruta = _agregada!.Rutas.Should().ContainSingle().Subject;
        ruta.HorasCargue.Should().Be(8m);
        ruta.Vehiculos.Should().ContainSingle().Which.TipoVehiculo.Should().Be("3S3", "sin cambios de peso ni de mercancías el camión se copia");
        await _planificador.DidNotReceiveWithAnyArgs().PlanificarAsync(default, default!, Ct);
    }

    [Fact]
    public async Task SiElPlanificadorFallaNoSeReservaNiSeCreaLaVersion()
    {
        var cambios = new clsCambiosCarga
        {
            Presentes = new HashSet<string> { clsCambiosCarga.CampoPesoKg, clsCambiosCarga.CampoPosiciones },
            PesoKg = 28_400m,
            Posiciones = clsSolicitudCosteoMuestra.CrearCaptura().Posiciones,
        };
        _planificador.PlanificarAsync(_ana, Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio("CUBICADOR_NO_DISPONIBLE", "No se pudo calcular los vehículos."));

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be("CUBICADOR_NO_DISPONIBLE");
        _agregada.Should().BeNull();
        _cotizacion.VersionNegocio.Should().Be(Version, "no se reserva la original");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
    }

    // --- Margen ajustado (decisión del usuario del 2026-10-06): se conserva al re-solicitar, por ruta (HU-021.9) ---

    [Fact]
    public async Task EnElMismoFolioElMargenAjustadoDeLaRutaSeConserva()
    {
        _cotizacion.AplicarMargenNegociado(1, 0.2m, DateTimeOffset.UtcNow).EsExitoso.Should().BeTrue();

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var ruta = _cotizacion.PrimeraRuta!;
        (ruta.MargenAplicado, ruta.OrigenMargen, ruta.MargenPoliticaSugerido).Should().Be((0.2m, OrigenMargen.Negociado, 0.15m));
        ruta.PrecioFinalLinea.Should().BeNull("el precio vuelve con el costeo nuevo");
    }

    [Fact]
    public async Task LaVersionNuevaCopiaElMargenAjustadoDeCadaRuta()
    {
        var licitacion = VencidaDeDosRutas();
        licitacion.AplicarMargenNegociado(2, 0.22m, DateTimeOffset.UtcNow).EsExitoso.Should().BeTrue();

        var resultado = await Servicio().ReSolicitarAsync(_ana, licitacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        var rutas = _agregada!.RutasOrdenadas;
        (rutas[1].MargenAplicado, rutas[1].OrigenMargen).Should().Be((0.22m, OrigenMargen.Negociado));
        rutas[0].TieneMargenNegociado.Should().BeFalse("la ruta 1 no se ajustó: su margen se resuelve al enviar");
    }

    /// <summary>
    /// Licitación vencida de dos rutas (líneas de negocio 3 y 7): la ruta 1 con dos camiones 3S3 (10.001 y 5.000 kg), la ruta 2 con uno
    /// y el conexo Escolta (42.500 × 2). Ambas con flete de Nexo y precio.
    /// </summary>
    private clsCotizacionSolicitud VencidaDeDosRutas()
    {
        var licitacion = clsCotizacionMuestra.Licitacion(2, "PCN-900555111-AP-05102026-020", _ana);
        licitacion.RutasOrdenadas[0].AgregarVehiculo("3S3", "FURGON", 5_000m);
        typeof(clsLineaSolicitud).GetProperty(nameof(clsLineaSolicitud.LineaNegocioId))!.SetValue(licitacion.RutasOrdenadas[1], 7);
        licitacion.RegistrarEnvioCosteo("sol-vieja", false, DateTimeOffset.UtcNow.AddHours(-73), DateTimeOffset.UtcNow);
        foreach (var ruta in licitacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        licitacion.RutasOrdenadas[1].AgregarOActualizarConexo("CX-9", "Escolta", 42_500m, 2m);
        clsCotizacionMuestra.AplicarCosteo(licitacion, [1_000_000m, 900_000m, 2_000_000m], EstadoCosteo.Entregada, null);
        clsCotizacionMuestra.ConVersionNegocio(licitacion, Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(licitacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(licitacion);
        _unidad.GuardarCambiosSiVersionAsync(licitacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        _folios.CrearNuevaVersionAsync(_ana, licitacion.Id, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>()(
                clsFolio.Desde(licitacion.Folio).Valor.ConVersion(2).Valor, CancellationToken.None));
        return licitacion;
    }

    [Fact]
    public async Task LaVersionNuevaEnUsdFijaLaTrmDelDiaYLaOriginalConservaLaSuya()
    {
        EnDolares(_cotizacion, 3_900m);

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _agregada!.Moneda.Should().Be("USD");
        _agregada.TrmAplicada.Should().Be(4_012.35m, "la versión nueva es una cotización nueva: TRM del día");
        _cotizacion.TrmAplicada.Should().Be(3_900m, "la original queda intacta");
        await _monedas.Received(1).FijarAsync("USD", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SinTrmLaVersionNuevaEnUsdNoReservaNiCreaNadaNiLlamaANexo()
    {
        EnDolares(_cotizacion, 3_900m);
        _monedas.FijarAsync("USD", Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.TrmNoDisponible, "Ninguna fuente de la TRM respondió."));

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TrmNoDisponible);
        _cotizacion.VersionNegocio.Should().Be(Version, "sin TRM no se reserva la original");
        _agregada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _folios.DidNotReceiveWithAnyArgs().CrearNuevaVersionAsync<clsCotizacionSolicitud>(default, default, default!, Ct);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
    }

    [Fact]
    public async Task EnElMismoFolioLaMonedaYLaTrmSeConservanSinConsultarLaTrm()
    {
        EnDolares(_cotizacion, 3_900m);

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.TrmAplicada.Should().Be(3_900m);
        await _monedas.DidNotReceiveWithAnyArgs().FijarAsync(default, Ct);
    }

    private static void EnDolares(clsCotizacionSolicitud cotizacion, decimal trm)
    {
        typeof(clsCotizacionSolicitud).GetProperty(nameof(clsCotizacionSolicitud.Moneda))!.SetValue(cotizacion, "USD");
        typeof(clsCotizacionSolicitud).GetProperty(nameof(clsCotizacionSolicitud.TrmAplicada))!.SetValue(cotizacion, trm);
    }

    [Fact]
    public async Task SiOtraPeticionGanaLaReservaDeLaOriginalNoSeCreaVersionNiSeLlamaANexo()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _agregada.Should().BeNull();
        await _folios.DidNotReceiveWithAnyArgs().CrearNuevaVersionAsync<clsCotizacionSolicitud>(default, default, default!, Ct);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
    }

    [Fact]
    public async Task UnCodigoFueraDelCatalogoEnLosCambiosDeCargaEs400SinReservar()
    {
        _catalogos.ValidarAsync(_ana, Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "tipo_vehiculo no es un código del catálogo"));
        var cambios = new clsCambiosCarga { Presentes = new HashSet<string> { clsCambiosCarga.CampoTipoVehiculo }, TipoVehiculo = "camion grande" };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task UnaLineaInactivaSeRechazaTambienEnElMismoFolio()
    {
        var inactiva = clsMargenPolitica.Crear("Importacion", 0.15m).Valor;
        inactiva.Desactivar();
        _politicas.ObtenerPorIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(inactiva);

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.EsFallo.Should().BeTrue();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public void UnFalloDeNexoNoConsumeLaIdempotencyKey()
    {
        var enNexo = clsCotizacionResumen.Desde(_cotizacion, null, null) with { EstadoCosteo = "pendiente_costeo" };
        var conError = enNexo with { EstadoCosteo = "error_envio" };

        clsReSolicitudCosteoService.SeGuarda(new clsReSolicitudCosteo(false, conError, "5")).Should().BeFalse();
        clsReSolicitudCosteoService.SeGuarda(new clsReSolicitudCosteo(false, enNexo, "5")).Should().BeTrue();
        clsReSolicitudCosteoService.SeGuarda(new clsErrorDominio("X", "y")).Should().BeFalse();
    }

    [Fact]
    public async Task CambiosDeCargaQueRompenLaReglaDePesoNoCreanVersion()
    {
        var cambios = new clsCambiosCarga { Presentes = new HashSet<string> { clsCambiosCarga.CampoPesoKg }, PesoKg = 0m };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Detalle.Should().Be(clsSolicitudCosteo.PesoRequerido);
        _agregada.Should().BeNull();
        await _folios.DidNotReceiveWithAnyArgs().CrearNuevaVersionAsync<clsCotizacionSolicitud>(default, default, default!, Ct);
    }

    [Fact]
    public async Task DatosDeCargaVaciosSonInvalidos()
    {
        var cambios = new clsCambiosCarga { Presentes = new HashSet<string>() };

        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { DatosCarga = cambios }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain("datos_carga");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaLicitacionCuyasFilasGuardadasYaNoPasanPideUnaCargaNuevaYNoLlegaANexo(bool versionNueva)
    {
        var licitacion = Vencida(_ana, "PCN-900555111-AP-05102026-009", 2_550_000m, TipoSolicitud.Licitacion);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(licitacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(licitacion);
        var requiere = clsValidadorFilasLicitacion.RequiereNuevaCarga(licitacion.Folio,
            [new(0, 3, "tipo_vehiculo", clsCodigosLineaSolicitud.CodigoNoCatalogo, "Tractomula no es un código.")]);
        _filas.RevalidarGuardadasAsync(_ana, licitacion.Folio, Arg.Any<clsEncabezadoSolicitudCosteo>(),
                Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<IReadOnlyList<int?>>(), Arg.Any<CancellationToken>())
            .Returns(requiere);

        var resultado = await Servicio().ReSolicitarAsync(_ana, licitacion.Folio, new clsEntradaReSolicitudCosteo("El costeo venció", null, versionNueva), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.LicitacionInvalida);
        resultado.Error.Detalle.Should().Be(clsValidadorFilasLicitacion.MotivoRequiereNuevaCarga);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
        _agregada.Should().BeNull("la -vN copia las mismas filas: tampoco se crea");

        // La misma regla corre al validar la propuesta del asistente (HU-013.6): nunca propone re-solicitar una licitación inválida.
        var propuesta = await Servicio().ValidarAsync(_ana, licitacion, Entrada() with { NuevaVersion = versionNueva }, Ct);
        propuesta!.Detalle.Should().Be(clsValidadorFilasLicitacion.MotivoRequiereNuevaCarga);
    }

    [Fact]
    public async Task UnaLicitacionNoAdmiteCambiosDeCarga()
    {
        // HU-021: una segunda ruta en la misma solicitud la vuelve un pliego.
        _cotizacion.AgregarRuta(_cotizacion.PrimeraRuta!.ALinea(), 1, 3);
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

    // --- HU-021.9 (#136; rehace #124 con la decisión 3): los conexos son de Cotifly y se conservan; a Nexo los manda el envío diferido ---

    [Fact]
    public async Task EnElMismoFolioLosConexosSeConservanEnLaRutaYSeAvisanSinMontos()
    {
        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada(), Version, null, Ct);

        resultado.Valor.ConexosConservados.Should().Equal("Cargue");
        resultado.Valor.ConexosDescartados.Should().BeEmpty("la re-solicitud ya no descarta conexos");
        var conexo = _cotizacion.PrimeraRuta!.Conexos.Should().ContainSingle().Subject;
        (conexo.IdServicioConexo, conexo.Precio, conexo.Cantidad).Should().Be(("CX-1", 85_000m, 1m), "mismo costo unitario de Nexo y cantidad");
        _cotizacion.PrimeraRuta.CostoServiciosConexos.Should().Be(85_000m);
        _cotizacion.PrimeraRuta.PrecioFinalLinea.Should().BeNull("sin flete no hay precio: vuelve con el costeo nuevo");
        Json(resultado.Valor.ConexosConservados).Should().NotContain("85000").And.NotContain("100000");
        await _auditoria.Received(1).RegistrarAsync(clsReSolicitudCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Is<object?>(s => Json(s).Contains("\"conexos_conservados\":[\"Cargue\"]")), true, Arg.Any<int>(), null);
    }

    [Fact]
    public async Task LaVersionNuevaCopiaLosConexosDeLaOriginal()
    {
        var resultado = await Servicio().ReSolicitarAsync(_ana, _cotizacion.Folio, Entrada() with { NuevaVersion = true }, Version, null, Ct);

        resultado.Valor.VersionNueva.Should().BeTrue();
        resultado.Valor.ConexosConservados.Should().Equal("Cargue");
        var copiado = _agregada!.Rutas.SelectMany(r => r.Conexos).Should().ContainSingle().Subject;
        (copiado.IdServicioConexo, copiado.Nombre, copiado.Precio, copiado.Cantidad).Should().Be(("CX-1", "Cargue", 85_000m, 1m));
        copiado.LineaSolicitudId.Should().Be(_agregada.PrimeraRuta!.Id);
        _cotizacion.PrimeraRuta!.Conexos.Should().NotBeEmpty("la original conserva los suyos");
    }

    [Fact]
    public void LosConexosConservadosSonLosDeLasRutasSinRepetirConElIdSiNoTienenNombre()
    {
        // HU-021: los conexos son filas de la ruta (ya no hay "excluidos": todos van en el precio) y una fila sin nombre (migrada) se
        // muestra con su id de Nexo (la tabla no guarda la categoría).
        var cotizacion = Vencida(_ana, "PCN-900555111-AP-05102026-002", 1_000_000m);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.AgregarOActualizarConexo("CX-2", " cargue ", 85_000m, 2m);
        ruta.AgregarOActualizarConexo("CX-3", null, 10m, 1m);
        ruta.AgregarOActualizarConexo("CX-4", "Stand-by", 10m, 1m);
        ruta.AgregarOActualizarConexo("CX-5", "Precinto", 10m, 1m);

        clsReSolicitudCosteoService.ConexosConservados(cotizacion).Should().Equal("Cargue", "CX-3", "Stand-by", "Precinto");
    }

    [Fact]
    public void SinConexosNoSeConservaNada()
    {
        var cotizacion = Vencida(_ana, "PCN-900555111-AP-05102026-003", 1_000_000m);
        cotizacion.PrimeraRuta!.ConservarConexos(new HashSet<string>());

        clsReSolicitudCosteoService.ConexosConservados(cotizacion).Should().BeEmpty();
    }

    private clsReSolicitudCosteoService Servicio() =>
        new(_cotizaciones, _politicas, _folios, _envio, _catalogos, _filas, _monedas, _mis, _idempotencia, _unidad, _registro, _auditoria,
            _planificador, _costos, NullLogger<clsReSolicitudCosteoService>.Instance);

    /// <summary>Validador de filas de licitación que deja pasar las líneas tal cual (cada prueba lo cambia si lo necesita).</summary>
    private static IClsValidadorFilasLicitacion FilasQuePasan()
    {
        var filas = Substitute.For<IClsValidadorFilasLicitacion>();
        filas.RevalidarGuardadasAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<clsEncabezadoSolicitudCosteo>(),
                Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<IReadOnlyList<int?>>(), Arg.Any<CancellationToken>())
            .Returns(ci => clsResultado<IReadOnlyList<clsLineaSolicitudCosteo>>.Exito(ci.ArgAt<IReadOnlyList<clsLineaSolicitudCosteo>>(3)));
        return filas;
    }

    private static clsEntradaReSolicitudCosteo Entrada() => new("El costeo venció", null, false);

    private static readonly System.Text.Json.JsonSerializerOptions _json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Json(object? valor) => System.Text.Json.JsonSerializer.Serialize(valor, _json);

    /// <summary>
    /// Cotización del formulario enviada a Nexo (solicitud <c>sol-vieja</c>), con el flete de su camión (<c>linea-vieja</c>), el conexo
    /// Cargue (85.000) en la ruta, precio (HU-021: (costo + conexo) con el 15 %) y en <c>costeo_vencido</c>.
    /// </summary>
    public static clsCotizacionSolicitud Vencida(Guid usuarioId, string folio, decimal costo, TipoSolicitud tipo = TipoSolicitud.Spot)
    {
        var cotizacion = clsCotizacionMuestra.Formulario(folio, usuarioId, solicitud: clsSolicitudCosteoMuestra.Crear() with { Tipo = tipo });
        cotizacion.RegistrarEnvioCosteo("sol-vieja", false, DateTimeOffset.UtcNow.AddHours(-73), DateTimeOffset.UtcNow);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        var camion = ruta.Vehiculos[0];
        camion.AsignarLineaNexo("linea-vieja");
        camion.RegistrarCosteo(EstadoCosteo.PendienteCosteo, costo, null);
        ruta.AgregarOActualizarConexo("CX-1", "Cargue", 85_000m, 1m);
        cotizacion.RecalcularTotales(DateTimeOffset.UtcNow);
        cotizacion.VencerPollingCosteo("Sin costeo de Nexo en 72 h.");
        return clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version);
    }
}
