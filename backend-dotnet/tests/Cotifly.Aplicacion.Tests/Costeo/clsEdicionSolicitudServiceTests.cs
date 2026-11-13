using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
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
/// Edición de la solicitud (ADR-021): <see cref="clsEdicionSolicitudService"/> con la captura real (<see cref="clsCapturaRutasService"/>) y el resto
/// sustituido: los tres modos (en sitio, reemplazo y versión nueva), el rechazo en Nexo (con éxito, ya descartada y fallido sin volver a
/// capturar), el margen que sobrevive, las rutas agregadas y quitadas, los campos bloqueados, la recubicación solo de lo que cambia, el
/// GET y la idempotencia.
/// </summary>
public sealed class clsEdicionSolicitudServiceTests
{
    private const int Version = 4;
    private const int Linea = 3;
    private const string Folio = "PCN-900123456-AP-19102026-001";

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsMargenAjusteRepository _ajustes = Substitute.For<IClsMargenAjusteRepository>();
    private readonly IClsGeneradorFolioService _folios = Substitute.For<IClsGeneradorFolioService>();
    private readonly IClsEnvioCosteoService _envio = Substitute.For<IClsEnvioCosteoService>();
    private readonly IClsCosteoNexo _nexo = Substitute.For<IClsCosteoNexo>();
    private readonly IClsFijacionMonedaService _monedas = Substitute.For<IClsFijacionMonedaService>();
    private readonly IClsMisCotizacionesService _mis = Substitute.For<IClsMisCotizacionesService>();
    private readonly IClsAlmacenIdempotencia _idempotencia = Substitute.For<IClsAlmacenIdempotencia>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaRepository _registro = Substitute.For<IClsAuditoriaRepository>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsValidadorFilasLicitacion _filas = Substitute.For<IClsValidadorFilasLicitacion>();
    private readonly IClsCostosConexosNexo _costos = Substitute.For<IClsCostosConexosNexo>();
    private readonly IClsServiciosConexosNexo _catalogoNexo = Substitute.For<IClsServiciosConexosNexo>();
    private readonly IClsPlanificadorCamiones _planificador = Substitute.For<IClsPlanificadorCamiones>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsMargenPolitica _importacion = ConId(clsMargenPolitica.Crear("Importacion", 0.15m).Valor, Linea);
    private readonly clsMargenPolitica _nacional = ConId(clsMargenPolitica.Crear("Nacional", 0.2m).Valor, 7);
    private readonly List<string> _eventos = [];
    private readonly Dictionary<string, clsCostoConexoNexo> _catalogo = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CX-1"] = new("CX-1", "Cargue", 85_000m),
        ["CX-9"] = new("CX-9", "Escolta", 42_500m),
    };

    private clsCotizacionSolicitud? _agregada;

    public clsEdicionSolicitudServiceTests()
    {
        _reloj.AhoraUtc.Returns(clsCotizacionMuestra.Ahora);
        _politicas.ObtenerPorIdAsync(Linea, Arg.Any<CancellationToken>()).Returns(_importacion);
        _politicas.ObtenerPorIdAsync(7, Arg.Any<CancellationToken>()).Returns(_nacional);
        _monedas.FijarAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => new clsMonedaFijada(ci.Arg<string?>() ?? "COP", ci.Arg<string?>() == "USD" ? 4_012.35m : 0m));
        _filas.ValidarAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaLicitacionEntrada?>>(), Arg.Any<IReadOnlyList<clsErrorFilaLicitacion>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new clsFilasLicitacionValidadas(
                [.. ci.ArgAt<IReadOnlyList<clsLineaLicitacionEntrada?>>(1).OfType<clsLineaLicitacionEntrada>()], ci.ArgAt<IReadOnlyList<clsErrorFilaLicitacion>>(2), null));
        _costos.CostosAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(ci.ArgAt<IReadOnlyCollection<string>>(1)
                .Where(_catalogo.ContainsKey).ToDictionary(id => id, id => _catalogo[id], StringComparer.OrdinalIgnoreCase)));
        _catalogoNexo.BuscarAsync(Arg.Any<Guid>(), Arg.Any<clsFiltroCatalogoConexos>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaCatalogoConexosNexo>.Exito(new clsPaginaCatalogoConexosNexo(0, [])));
        _planificador.PlanificarAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _eventos.Add("cubicador");
                return clsResultado<clsPlanRuta>.Exito(new clsPlanRuta([new clsCamionPlaneado("2S2", "FURGON", 16_000m)]));
            });
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _eventos.Add($"guardar:v{ci.ArgAt<int>(1)}");
            return true;
        });
        // La reserva de la versión nueva se compara dentro de la transacción del candado del folio -vN.
        _unidad.VersionVigenteEnTransaccionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _eventos.Add($"reservar-en-candado:v{ci.ArgAt<int>(1)}");
            return true;
        });
        _nexo.RechazarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _eventos.Add("rechazar");
                return clsResultado<ResultadoRechazoNexo>.Exito(ResultadoRechazoNexo.Descartada);
            });
        _envio.EnviarGuardadaAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<clsMargenPolitica?>(), Arg.Any<Guid>(), Arg.Any<string?>())
            .Returns(ci =>
            {
                _eventos.Add("capturar");
                ci.Arg<clsCotizacionSolicitud>().RegistrarEnvioCosteo("sol-nueva", false, clsCotizacionMuestra.Ahora, clsCotizacionMuestra.Ahora);
                return Task.CompletedTask;
            });
        _mis.ResumirAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns(ci => clsCotizacionResumen.Desde(ci.Arg<clsCotizacionSolicitud>(), "Importacion", null));
        _cotizaciones.When(r => r.Agregar(Arg.Any<clsCotizacionSolicitud>())).Do(ci => _agregada = ci.Arg<clsCotizacionSolicitud>());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // --- En sitio ---

    [Fact]
    public async Task SinLlegarANexoSeEditaEnSitioYSeEnviaSinRechazarNada()
    {
        var cotizacion = Registrar(SinEnviar());
        var entrada = EntradaDe(cotizacion, (i, l) => l with { Destino = "Medellín", DestinoMunicipioId = 5001 });

        var resultado = await Servicio().EditarAsync(_ana, Folio, entrada, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        var respuesta = resultado.Valor.Respuesta;
        (respuesta.Modo, respuesta.Folio, respuesta.SinCambios, resultado.Valor.Creada).Should().Be(("en_sitio", Folio, false, false));
        respuesta.RutasReplanificadas.Should().BeEmpty("cambiar el destino no exige recubicar");
        cotizacion.PrimeraRuta!.DestinoMunicipioId.Should().Be(5001);
        resultado.Valor.RowVersion.Should().Be("5");
        _eventos.Should().Equal("guardar:v4", "capturar");
        await _nexo.DidNotReceiveWithAnyArgs().RechazarSolicitudAsync(default, default!, default!, default, Ct);
        await _envio.Received(1).EnviarGuardadaAsync(cotizacion, _importacion, _ana, $"Edición de {Folio}.");
        _registro.Received(1).Registrar(Arg.Is<clsEventoHerramienta>(e => e.Herramienta == clsEdicionSolicitudService.HerramientaAuditoria && e.ThreadId == null));
    }

    [Fact]
    public async Task SinCambiosNoSeTocaNada()
    {
        var cotizacion = Registrar(EnNexo());

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion), Version, null, Ct);

        resultado.Valor.Respuesta.SinCambios.Should().BeTrue();
        resultado.Valor.Respuesta.Avisos.Should().ContainSingle().Which.Codigo.Should().Be(clsAvisoEdicion.SinCambios);
        resultado.Valor.RowVersion.Should().Be(Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _eventos.Should().BeEmpty();
        cotizacion.SolicitudNexoId.Should().Be("SOL-1");
    }

    // --- Reemplazo ---

    [Fact]
    public async Task EnNexoRechazaLaAnteriorYCapturaEnElMismoFolioConElPdfInvalidado()
    {
        var cotizacion = Registrar(EnNexo());
        cotizacion.AsignarPdf("/data/pdfs/PCN-1.pdf");
        var entrada = EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }) with { Motivo = "El cliente cambió el horario" };

        var resultado = await Servicio().EditarAsync(_ana, Folio, entrada, Version, null, Ct);

        var respuesta = resultado.Valor.Respuesta;
        (respuesta.Modo, respuesta.Folio, respuesta.SolicitudNexoReemplazada).Should().Be(("reemplazo", Folio, "descartada"));
        respuesta.Avisos.Select(a => a.Codigo).Should().Contain([clsAvisoEdicion.ReemplazaSolicitudNexo, clsAvisoEdicion.PierdeCosteoActual, clsAvisoEdicion.InvalidaPdf]);
        _eventos.Should().Equal("guardar:v4", "rechazar", "guardar:v5", "capturar");
        await _nexo.Received(1).RechazarSolicitudAsync(_ana, "SOL-1", clsEdicionSolicitudService.MotivoRechazoNexo,
            Arg.Is<string?>(c => c!.Contains(Folio) && c.Contains("El cliente cambió el horario")), CancellationToken.None);
        await _envio.Received(1).EnviarGuardadaAsync(cotizacion, _importacion, _ana, $"Edición de {Folio}; reemplaza la solicitud SOL-1.");
        cotizacion.PdfPath.Should().BeNull();
        cotizacion.PdfDesactualizado.Should().BeTrue();
        cotizacion.PrimeraRuta!.HorasCargue.Should().Be(9m);
        cotizacion.SolicitudNexoId.Should().Be("sol-nueva");
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h => h.Motivo == MotivoInvalidacionPdf.ReSolicitud));
        resultado.Valor.RowVersion.Should().Be("6");
    }

    [Fact]
    public async Task SiLaAnteriorYaEstabaDescartadaSeContinua()
    {
        var cotizacion = Registrar(EnNexo());
        _nexo.RechazarSolicitudAsync(default, default!, default!, default, Ct).ReturnsForAnyArgs(ResultadoRechazoNexo.YaDescartada);

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { ValorMercancia = 1m }), Version, null, Ct);

        resultado.Valor.Respuesta.SolicitudNexoReemplazada.Should().Be("ya_descartada");
        await _envio.Received(1).EnviarGuardadaAsync(cotizacion, Arg.Any<clsMargenPolitica?>(), _ana, Arg.Any<string?>());
    }

    [Theory]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible, clsCodigosError.NexoNoDisponible)]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado, clsCodigosError.NexoNoDisponible)]
    [InlineData(clsCodigosError.NexoSinPermiso, clsCodigosError.NexoSinPermiso)]
    public async Task SiElRechazoFallaNoSeCapturaOtraYLaEdicionNoSeAplica(string fallo, string esperado)
    {
        var cotizacion = Registrar(EnNexo());
        _nexo.RechazarSolicitudAsync(default, default!, default!, default, Ct)
            .ReturnsForAnyArgs(new clsErrorDominio(fallo, "Nexo no responde.", "HTTP 403 FORBIDDEN texto crudo de Nexo"));
        _eventos.Clear();

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(esperado);
        resultado.Error.Datos!["motivo"].Should().Be(clsEdicionSolicitudService.MotivoRechazoFallido);
        resultado.Error.Detalle.Should().BeNull("endurecimiento B: el texto técnico de Nexo no pisa detalle.motivo (solo va al log)");
        resultado.Error.Datos["solicitud_nexo_id"].Should().Be("SOL-1");
        _eventos.Should().Equal(["guardar:v4"], "solo se reservó la versión");
        await _nexo.ReceivedWithAnyArgs(1).RechazarSolicitudAsync(default, default!, default!, default, Ct);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
        cotizacion.SolicitudNexoId.Should().Be("SOL-1", "la edición no se aplicó: la solicitud sigue apuntando a la de Nexo");
        cotizacion.PrimeraRuta!.HorasCargue.Should().NotBe(9m);
        await _auditoria.Received(1).RegistrarAsync(clsEdicionSolicitudService.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), false,
            Arg.Any<int>(), null);
    }

    [Fact]
    public async Task EnPendienteDeCosteoAvisaQueSePierdeElTrabajoDeLaMesa()
    {
        var cotizacion = Registrar(EnNexo(costeada: false));

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.Valor.Respuesta.Avisos.Select(a => a.Codigo).Should().Contain(clsAvisoEdicion.PierdeTrabajoMesa);
    }

    [Fact]
    public async Task SiOtraEscrituraGanaDespuesDelRechazoYYaNoEsLaMismaSolicitudEsConflictoSinCapturar()
    {
        var cotizacion = Registrar(EnNexo());
        _unidad.GuardarCambiosSiVersionAsync(cotizacion.Id, 5, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        await _cotizaciones.Received(1).RecargarAsync(cotizacion, Arg.Any<CancellationToken>());
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
    }

    [Fact]
    public async Task SiElPollingGuardaEntreElRechazoYLaEdicionSeRecargaYSeAplicaUnaVezMas()
    {
        // El polling ve la solicitud anterior descartada y guarda (v5 → v6) entre el rechazo y la edición. Sigue siendo
        // la misma solicitud y se puede reemplazar: se recarga, se aplica sobre v6 y se captura UNA sola vez.
        var cotizacion = Registrar(EnNexo());
        _unidad.GuardarCambiosSiVersionAsync(cotizacion.Id, 5, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _eventos.Add("guardar:v5 (gana el polling)");
            return false;
        });
        _cotizaciones.When(r => r.RecargarAsync(cotizacion, Arg.Any<CancellationToken>())).Do(_ =>
        {
            // La recarga real devuelve lo guardado: la solicitud anterior (SOL-1, que el intento fallido había olvidado en memoria) y la
            // versión que guardó el polling (6, la misma que ya tiene el agregado en memoria tras el intento fallido).
            _eventos.Add("recargar");
            cotizacion.RegistrarEnvioCosteo("SOL-1", false, clsCotizacionMuestra.Ahora, clsCotizacionMuestra.Ahora);
        });

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        _eventos.Should().Equal("guardar:v4", "rechazar", "guardar:v5 (gana el polling)", "recargar", "guardar:v6", "capturar");
        await _nexo.ReceivedWithAnyArgs(1).RechazarSolicitudAsync(default, default!, default!, default, Ct);
        resultado.Valor.Respuesta.SolicitudNexoReemplazada.Should().Be("descartada");
        cotizacion.SolicitudNexoId.Should().Be("sol-nueva");
    }

    // --- Margen y rutas (decisiones 3 y 4) ---

    [Fact]
    public async Task ElMargenNegociadoSobreviveSiLaRutaConservaSuId()
    {
        var cotizacion = Registrar(EnNexo());
        cotizacion.PrimeraRuta!.AplicarMargenNegociado(0.22m);

        await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        (cotizacion.PrimeraRuta.MargenAplicado, cotizacion.PrimeraRuta.OrigenMargen).Should().Be((0.22m, OrigenMargen.Negociado));
    }

    [Fact]
    public async Task SeAgreganYQuitanRutasYLosAjustesDeLaQuitadaQuedanSinRuta()
    {
        var cotizacion = Registrar(EnNexo(rutas: 2));
        var (primera, segunda) = (cotizacion.RutasOrdenadas[0], cotizacion.RutasOrdenadas[1]);
        primera.AplicarMargenNegociado(0.3m);
        _ajustes.DesvincularRutasAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(1);
        var nueva = new clsRutaEdicionEntrada(null, new clsLineaLicitacionEntrada(null,
            clsSolicitudCosteoMuestra.RutaCaptura() with { Destino = "Cali", DestinoMunicipioId = 76001 }, null, [new clsConexoCapturaEntrada("CX-9", 2m)]));
        var entrada = EntradaDe(cotizacion) with { Rutas = [EntradaDe(cotizacion).Rutas[1], nueva] };

        var resultado = await Servicio().EditarAsync(_ana, Folio, entrada, Version, null, Ct);

        var respuesta = resultado.Valor.Respuesta;
        respuesta.RutasQuitadas.Should().Equal(primera.Id);
        respuesta.RutasAgregadas.Should().Equal(2);
        respuesta.RutasReplanificadas.Should().Equal(2);
        respuesta.Avisos.Select(a => a.Codigo).Should().Contain(clsAvisoEdicion.MargenDesvinculado);
        await _ajustes.Received(1).DesvincularRutasAsync(Arg.Is<IReadOnlyCollection<Guid>>(r => r.Single() == primera.Id), Arg.Any<CancellationToken>());
        cotizacion.RutasOrdenadas.Select(r => (r.Orden, r.Id == segunda.Id, r.LineaNegocioId)).Should().Equal((1, true, Linea), (2, false, Linea));
        var conexo = cotizacion.RutasOrdenadas[1].Conexos.Should().ContainSingle().Subject;
        (conexo.IdServicioConexo, conexo.Precio, conexo.Cantidad).Should().Be(("CX-9", 42_500m, 2m), "el costo unitario es el del catálogo de hoy");
        await _planificador.Received(1).PlanificarAsync(_ana, cotizacion.RutasOrdenadas[1].Id, Arg.Any<clsLineaSolicitudCosteo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CambiarLaClaseDeViajeVuelveACubicarLaRutaConSuMismoId()
    {
        var cotizacion = Registrar(EnNexo());
        var ruta = cotizacion.PrimeraRuta!;

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { ClaseViaje = "Carga Suelta", ClaseContenedor = null }), Version, null, Ct);

        resultado.Valor.Respuesta.RutasReplanificadas.Should().Equal(1);
        await _planificador.Received(1).PlanificarAsync(_ana, ruta.Id, Arg.Is<clsLineaSolicitudCosteo>(l => l.ClaseViaje == "Carga Suelta"), Arg.Any<CancellationToken>());
        ruta.Vehiculos.Should().ContainSingle().Which.TipoVehiculo.Should().Be("2S2");
    }

    // --- Versión nueva ---

    [Fact]
    public async Task YaEntregadaAlClienteCreaLaVersionNuevaSinTocarLaOriginalNiRechazarEnNexo()
    {
        var cotizacion = Registrar(EnNexo());
        cotizacion.PrimeraRuta!.AplicarMargenNegociado(0.25m);
        cotizacion.RegistrarEnvio();
        _folios.CrearNuevaVersionAsync(_ana, cotizacion.Id, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>()(
                clsFolio.Desde(Folio).Valor.ConVersion(2).Valor, CancellationToken.None));

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        var respuesta = resultado.Valor.Respuesta;
        (respuesta.Modo, respuesta.Folio, respuesta.FolioOrigen, resultado.Valor.Creada).Should().Be(("nueva_version", $"{Folio}-v2", Folio, true));
        respuesta.Avisos.Select(a => a.Codigo).Should().Equal(clsAvisoEdicion.CreaVersionNueva);
        await _nexo.DidNotReceiveWithAnyArgs().RechazarSolicitudAsync(default, default!, default!, default, Ct);
        var nueva = _agregada!;
        nueva.PrimeraRuta!.HorasCargue.Should().Be(9m);
        (nueva.PrimeraRuta.MargenAplicado, nueva.PrimeraRuta.OrigenMargen).Should().Be((0.25m, OrigenMargen.Negociado));
        nueva.PrimeraRuta.Id.Should().NotBe(cotizacion.PrimeraRuta.Id);
        cotizacion.SolicitudNexoId.Should().Be("SOL-1");
        cotizacion.PrimeraRuta.HorasCargue.Should().NotBe(9m, "la original no cambia");
        cotizacion.VersionNegocio.Should().Be(Version + 1, "la original solo reserva su versión");
        await _envio.Received(1).EnviarGuardadaAsync(nueva, _importacion, _ana, $"Versión editada de {Folio}.");
        _eventos.Should().Contain($"reservar-en-candado:v{Version}", "la reserva va dentro del candado del folio -vN");
        await _unidad.DidNotReceive().GuardarCambiosSiVersionAsync(cotizacion.Id, Version, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// En <c>nueva_version</c>, si el candado del folio -vN está ocupado
    /// (<c>SERVICIO_OCUPADO</c>), la original no cambia: ni su <c>versionNegocio</c> (el <c>If-Match</c> sigue valiendo) ni nada más.
    /// </summary>
    [Fact]
    public async Task EnNuevaVersionConElCandadoOcupadoLaOriginalConservaSuVersion()
    {
        var cotizacion = Registrar(EnNexo());
        cotizacion.RegistrarEnvio();
        _folios.CrearNuevaVersionAsync(_ana, cotizacion.Id, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ServicioOcupado, "ocupado"));

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ServicioOcupado);
        cotizacion.VersionNegocio.Should().Be(Version, "nada cambia");
        _agregada.Should().BeNull();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _unidad.DidNotReceiveWithAnyArgs().VersionVigenteEnTransaccionAsync(default, default, Ct);
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
    }

    [Fact]
    public async Task EnNuevaVersionSiOtraPeticionGanaLaReservaNoSeCreaLaVersion()
    {
        var cotizacion = Registrar(EnNexo());
        cotizacion.RegistrarEnvio();
        _unidad.VersionVigenteEnTransaccionAsync(cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);
        _folios.CrearNuevaVersionAsync(_ana, cotizacion.Id, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>()(
                clsFolio.Desde(Folio).Valor.ConVersion(2).Valor, CancellationToken.None));

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        cotizacion.VersionNegocio.Should().Be(Version);
        _agregada.Should().BeNull();
        await _envio.DidNotReceiveWithAnyArgs().EnviarGuardadaAsync(default!, default, default, default);
    }

    // --- Campos bloqueados y estados (decisiones 1, 5 y 6) ---

    [Fact]
    public async Task CambiarElClienteEsCampoNoEditableYNadaCambia()
    {
        var cotizacion = Registrar(EnNexo());

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion) with { NitCliente = "800111222" }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CampoNoEditable);
        resultado.Error.Datos!["campo"].Should().Be("nit_cliente");
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task CambiarLaLineaDeNegocioDeUnaRutaEsCampoNoEditable()
    {
        var cotizacion = Registrar(EnNexo());
        var entrada = EntradaDe(cotizacion);
        entrada = entrada with { Rutas = [entrada.Rutas[0]! with { Ruta = entrada.Rutas[0]!.Ruta with { LineaNegocio = "Nacional" } }] };

        var resultado = await Servicio().EditarAsync(_ana, Folio, entrada, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CampoNoEditable);
        resultado.Error.Datos!["campo"].Should().Be("rutas[0].linea_negocio");
    }

    [Fact]
    public async Task UnaRutaNuevaConUnaLineaAjenaEsCampoNoEditable()
    {
        var cotizacion = Registrar(EnNexo());
        var entrada = EntradaDe(cotizacion);
        var nueva = new clsRutaEdicionEntrada(null, new clsLineaLicitacionEntrada(null, clsSolicitudCosteoMuestra.RutaCaptura(), "Nacional"));

        var resultado = await Servicio().EditarAsync(_ana, Folio, entrada with { Rutas = [entrada.Rutas[0], nueva] }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CampoNoEditable);
        resultado.Error.Datos!["campo"].Should().Be("rutas[1].linea_negocio");
    }

    [Fact]
    public async Task UnaRutaQueNoEsDeLaCotizacionEsInvalida()
    {
        var cotizacion = Registrar(EnNexo());
        var entrada = EntradaDe(cotizacion);

        var resultado = await Servicio().EditarAsync(_ana, Folio,
            entrada with { Rutas = [entrada.Rutas[0]! with { LineaSolicitudId = Guid.NewGuid() }] }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain("no es de la cotización");
    }

    [Fact]
    public async Task UnaLicitacionNoSeEditaEnEstaFase()
    {
        var licitacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m], folio: Folio, usuarioId: _ana);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(Folio, _ana, Arg.Any<CancellationToken>()).Returns(clsCotizacionMuestra.ConVersionNegocio(licitacion, Version));

        var resultado = await Servicio().EditarAsync(_ana, Folio, new clsEntradaEdicionSolicitud([]), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        resultado.Error.Detalle.Should().Be(clsCotizacionSolicitud.MotivoEdicionLicitacion);
    }

    [Fact]
    public async Task ConUnaVersionViejaEsConflictoSinTocarNada()
    {
        var cotizacion = Registrar(EnNexo());

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version - 1, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task SiElModoCambioDesdeQueSeLeyoEsEstadoNoPermite()
    {
        var cotizacion = Registrar(EnNexo());

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion) with { ModoEsperado = "en_sitio" }, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        resultado.Error.Detalle.Should().Be(clsEdicionSolicitudService.MotivoModoCambio);
    }

    [Fact]
    public async Task UnaAjenaEsNoEncontrada()
    {
        Registrar(EnNexo());

        var resultado = await Servicio().EditarAsync(Guid.NewGuid(), Folio, new clsEntradaEdicionSolicitud([]), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }

    // --- Idempotencia ---

    [Fact]
    public async Task ConIdempotencyKeySeEjecutaUnaVezPorComercialYHuella()
    {
        var cotizacion = Registrar(EnNexo());
        var entrada = EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m });
        _idempotencia.EjecutarUnaVezAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Func<CancellationToken, Task<clsResultado<clsEdicionAplicada>>>>(), Arg.Any<Func<clsResultado<clsEdicionAplicada>, bool>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<CancellationToken, Task<clsResultado<clsEdicionAplicada>>>>()(CancellationToken.None));

        await Servicio().EditarAsync(_ana, Folio, entrada, Version, "clave-12345", Ct);

        await _idempotencia.Received(1).EjecutarUnaVezAsync($"{clsEdicionSolicitudService.OperacionIdempotencia}:{_ana}", "clave-12345",
            clsEdicionSolicitudService.Huella(Folio, entrada), Arg.Any<Func<CancellationToken, Task<clsResultado<clsEdicionAplicada>>>>(),
            Arg.Any<Func<clsResultado<clsEdicionAplicada>, bool>?>(), Arg.Any<CancellationToken>());
        clsEdicionSolicitudService.Huella(Folio, entrada).Should().NotBe(clsEdicionSolicitudService.Huella(Folio, entrada with { Notas = "Otra" }));
    }

    // --- GET ---

    [Fact]
    public async Task ElModeloEditableTraeLasRutasCompletasLosBloqueadosYElModo()
    {
        var cotizacion = Registrar(EnNexo());
        cotizacion.PrimeraRuta!.AplicarMargenNegociado(0.22m);

        var editable = (await Servicio().ObtenerAsync(_ana, Folio, Ct)).Valor;

        (editable.Folio, editable.VersionNegocio, editable.Moneda).Should().Be((Folio, Version, "COP"));
        editable.Bloqueados.Should().Be(new clsBloqueadosSolicitud("spot", "900123456", "LONGYIN S.A.S.", null));
        editable.CamposBloqueados.Should().Contain(["nit_cliente", "tipo", "rutas[].linea_negocio"]);
        (editable.Edicion.Modo, editable.Edicion.Permitida).Should().Be(("reemplazo", true));
        editable.Edicion.Avisos.Select(a => a.Codigo).Should().Contain(clsAvisoEdicion.ReemplazaSolicitudNexo);
        editable.LineasNegocioDisponibles.Should().Equal("Importacion");
        var ruta = editable.Rutas.Should().ContainSingle().Subject;
        (ruta.LineaSolicitudId, ruta.LineaNegocio, ruta.LineaNegocioBloqueada).Should().Be((cotizacion.PrimeraRuta.Id, "Importacion", true));
        (ruta.OrigenMunicipioId, ruta.DestinoMunicipioId, ruta.CiudadDevolucionMunicipioId).Should().Be((76109, 11001, 76109));
        ruta.Mercancias.Should().HaveCount(2).And.OnlyContain(m => m.CodigoMercancia == clsSolicitudCosteoMuestra.CodigoMercancia);
        ruta.ServiciosConexos.Should().ContainSingle().Which.Should().Be(new clsConexoEditable("CX-1", "Cargue", 1m));
        (ruta.MargenAplicado, ruta.OrigenMargen, ruta.MargenNegociado).Should().Be((0.22m, "negociado", true));
    }

    [Fact]
    public async Task UnConexoAgregadoEnNexoSaleConElIdDelCatalogoSumadoYReenviarElGetNoCambiaNada()
    {
        // Un servicio capturado (id del catálogo) y el mismo agregado después en Nexo (id de la fila de Nexo).
        // El GET los debe mostrar como UNA fila con el id del catálogo (si no, el PUT con ese cuerpo es CONEXO_DUPLICADO), y reenviar el
        // GET tal cual es sin_cambios: no se rechaza en Nexo ni se pierde el trabajo de la Mesa.
        var cotizacion = Registrar(EnNexo());
        var catalogo = Guid.NewGuid().ToString("D");
        var filaNexo = Guid.NewGuid().ToString("D");
        clsCotizacionMuestra.ConConexo(cotizacion, catalogo, "Cargue en puerto", 150_000m, 1m);
        clsCotizacionMuestra.ConConexo(cotizacion, filaNexo, "Cargue en puerto", 150_000m, 2m);
        _catalogo[catalogo] = new clsCostoConexoNexo(catalogo, "Cargue en puerto", 150_000m);
        _catalogoNexo.BuscarAsync(_ana, Arg.Is<clsFiltroCatalogoConexos>(f => f.Q == "Cargue en puerto"), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaCatalogoConexosNexo>.Exito(new clsPaginaCatalogoConexosNexo(1,
                [new clsServicioConexoNexo(catalogo, "CAR-01", "Cargue en puerto", "logistico", "UND", null, null, null, null, null, null, true)])));

        var editable = (await Servicio().ObtenerAsync(_ana, Folio, Ct)).Valor;
        var conexos = editable.Rutas.Single().ServiciosConexos;
        conexos.Should().BeEquivalentTo([new clsConexoEditable("CX-1", "Cargue", 1m), new clsConexoEditable(catalogo, "Cargue en puerto", 3m)]);

        var entrada = EntradaDe(cotizacion) with
        {
            Rutas = [.. EntradaDe(cotizacion).Rutas.Select(r => r! with
            {
                Ruta = r.Ruta with { Conexos = [.. conexos.Select(c => (clsConexoCapturaEntrada?)new clsConexoCapturaEntrada(c.IdServicioConexo, c.Cantidad))] },
            })],
        };
        var resultado = await Servicio().EditarAsync(_ana, Folio, entrada, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        resultado.Valor.Respuesta.SinCambios.Should().BeTrue();
        _eventos.Should().BeEmpty("sin cambios no se rechaza en Nexo ni se captura");
    }

    [Fact]
    public async Task ElModeloDeUnaLicitacionDiceQueNoSePermiteYPorQue()
    {
        var licitacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m], folio: Folio, usuarioId: _ana);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(Folio, _ana, Arg.Any<CancellationToken>()).Returns(licitacion);

        var editable = (await Servicio().ObtenerAsync(_ana, Folio, Ct)).Valor;

        (editable.Edicion.Permitida, editable.Edicion.MotivoNoPermitida, editable.Edicion.Modo).Should().Be((false, "LICITACION", (string?)null));
    }

    // --- Los camiones rechazados por la Mesa no se reenvían salvo reincluir_rechazados ---

    /// <summary>
    /// Dos rutas en Nexo (en costeo: modo reemplazo). La ruta 1 tiene dos camiones y la Mesa rechazó el segundo ("Sin cupo"); la ruta 2 tiene
    /// uno y la Mesa lo rechazó ("Sin operación"): la ruta 2 está toda rechazada. Con <paramref name="todos"/>, una sola ruta con su único
    /// camión rechazado.
    /// </summary>
    private clsCotizacionSolicitud ConRechazos(bool todos = false)
    {
        var cotizacion = EnNexo(costeada: false, rutas: todos ? 1 : 2);
        var ruta1 = cotizacion.RutasOrdenadas[0];
        List<clsCosteoCamionNexo> lecturas = todos
            ? [new(ruta1.VehiculosOrdenados[0].Id, EstadoCosteo.Rechazada, true, MotivoRechazo: "Sin operación")]
            :
            [
                new(ruta1.AgregarVehiculo("2S2", "FURGON", 10_000m).Id, EstadoCosteo.Rechazada, true, MotivoRechazo: "Sin cupo"),
                new(cotizacion.RutasOrdenadas[1].VehiculosOrdenados[0].Id, EstadoCosteo.Rechazada, true, MotivoRechazo: "Sin operación"),
            ];
        var n = 0;
        foreach (var camion in cotizacion.RutasOrdenadas.SelectMany(r => r.VehiculosOrdenados))
        {
            // Como el polling: la línea de Nexo de cada camión (un rechazo leído siempre viene de su línea).
            cotizacion.AsignarLineaNexo(camion.Id, $"L-{++n}");
        }

        cotizacion.AplicarFotoNexo(lecturas, clsCotizacionMuestra.Ahora).EsExitoso.Should().BeTrue();
        cotizacion.Vehiculos.Should().NotContain(v => v.FueraDelEnvio, "antes de re-solicitar o editar, el rechazo es de la solicitud actual");
        return Registrar(cotizacion);
    }

    [Fact]
    public async Task ElReemplazoNoReenviaLosRechazadosYAvisaQueSeOmitieron()
    {
        var cotizacion = ConRechazos();
        IReadOnlyList<clsVehiculoLineaSolicitud>? enviados = null;
        _envio.When(e => e.EnviarGuardadaAsync(cotizacion, Arg.Any<clsMargenPolitica?>(), Arg.Any<Guid>(), Arg.Any<string?>()))
            .Do(_ => enviados = [.. cotizacion.LineasNexo().Select(l => l.Camion)]);

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        var respuesta = resultado.Valor.Respuesta;
        respuesta.Modo.Should().Be("reemplazo");
        var (ruta1, ruta2) = (cotizacion.RutasOrdenadas[0], cotizacion.RutasOrdenadas[1]);
        enviados.Should().Equal([ruta1.VehiculosOrdenados[0]], "el rechazado de la ruta 1 y la ruta 2 completa no viajan");
        ruta1.VehiculosOrdenados[1].Should().Match<clsVehiculoLineaSolicitud>(v => v.FueraDelEnvio && v.MotivoRechazo == "Sin cupo");
        (ruta2.Rechazada, ruta2.HorasCargue).Should().Be((true, (decimal?)9m), "la ruta rechazada se edita igual y queda visible, sin viajar");
        respuesta.AvisoRechazos.Should().StartWith("No se volvieron a enviar").And.Contain("1 de 2 camiones. Motivo: Sin cupo")
            .And.Contain("la ruta completa (1 camión)").And.Contain("Sin operación");
        respuesta.Cotizacion.AvisoRechazos.Should().Be(respuesta.AvisoRechazos);
        respuesta.Avisos.Should().ContainSingle(a => a.Codigo == clsAvisoEdicion.RechazadosOmitidos).Which.Mensaje.Should().Be(respuesta.AvisoRechazos);
    }

    [Fact]
    public async Task ConReincluirRechazadosLosDeEsaRutaVuelvenAViajarSinElRechazoAunqueNadaMasCambie()
    {
        var cotizacion = ConRechazos();
        IReadOnlyList<clsVehiculoLineaSolicitud>? enviados = null;
        _envio.When(e => e.EnviarGuardadaAsync(cotizacion, Arg.Any<clsMargenPolitica?>(), Arg.Any<Guid>(), Arg.Any<string?>()))
            .Do(_ => enviados = [.. cotizacion.LineasNexo().Select(l => l.Camion)]);
        var entrada = EntradaDe(cotizacion);
        entrada = entrada with { Rutas = [entrada.Rutas[0], entrada.Rutas[1]! with { ReincluirRechazados = true }] };

        var resultado = await Servicio().EditarAsync(_ana, Folio, entrada, Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        resultado.Valor.Respuesta.SinCambios.Should().BeFalse("pedir que la Mesa reconsidere es un cambio");
        resultado.Valor.Respuesta.RutasReplanificadas.Should().BeEmpty("no se vuelve a cubicar: viajan los mismos camiones (D2)");
        var (ruta1, ruta2) = (cotizacion.RutasOrdenadas[0], cotizacion.RutasOrdenadas[1]);
        var reincluido = ruta2.VehiculosOrdenados[0];
        enviados.Should().Equal([ruta1.VehiculosOrdenados[0], reincluido]);
        (reincluido.Rechazado, reincluido.MotivoRechazo, reincluido.RechazadaAt).Should().Be((false, (string?)null, (DateTimeOffset?)null));
        ruta2.Rechazada.Should().BeFalse();
        resultado.Valor.Respuesta.AvisoRechazos.Should().Contain("ruta 1").And.NotContain("ruta 2", "la ruta 1 no se reincluyó; la 2 sí");
        _eventos.Should().Equal("guardar:v4", "rechazar", "guardar:v5", "capturar");
    }

    [Fact]
    public async Task ElMismoCuerpoSinReincluirSigueSiendoSinCambiosYNoAvisaRechazos()
    {
        var cotizacion = ConRechazos();

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion), Version, null, Ct);

        resultado.Valor.Respuesta.SinCambios.Should().BeTrue("tener camiones rechazados no dispara la edición");
        resultado.Valor.Respuesta.AvisoRechazos.Should().BeNull();
        resultado.Valor.Respuesta.Avisos.Select(a => a.Codigo).Should().Equal(clsAvisoEdicion.SinCambios);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task SiNingunaRutaViajaResponde409SinTocarNadaYConReincluirSeEnvia()
    {
        var cotizacion = ConRechazos(todos: true);

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SinRutasCotizables);
        resultado.Error.Detalle.Should().Be(clsReSolicitudCosteoService.MotivoTodosRechazados);
        _eventos.Should().BeEmpty("se valida antes de reservar, rechazar en Nexo o capturar");
        cotizacion.VersionNegocio.Should().Be(Version);

        var entrada = EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m });
        entrada = entrada with { Rutas = [entrada.Rutas[0]! with { ReincluirRechazados = true }] };
        var reincluida = await Servicio().EditarAsync(_ana, Folio, entrada, Version, null, Ct);

        reincluida.EsExitoso.Should().BeTrue(reincluida.EsFallo ? reincluida.Error.Mensaje : null);
        cotizacion.LineasNexo().Should().ContainSingle();
        reincluida.Valor.Respuesta.AvisoRechazos.Should().BeNull();
    }

    [Fact]
    public async Task LaVersionNuevaEditadaCopiaLosRechazadosComoRechazadosYElGetLosMuestra()
    {
        var cotizacion = ConRechazos();
        cotizacion.RegistrarEnvio();
        _folios.CrearNuevaVersionAsync(_ana, cotizacion.Id, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>()(
                clsFolio.Desde(Folio).Valor.ConVersion(2).Valor, CancellationToken.None));

        var editable = (await Servicio().ObtenerAsync(_ana, Folio, Ct)).Valor;
        editable.Rutas.Select(r => (r.CamionesRechazados, r.MotivoRechazo)).Should().Equal([(1, "Sin cupo"), (1, "Sin operación")]);

        var resultado = await Servicio().EditarAsync(_ana, Folio, EntradaDe(cotizacion, (i, l) => l with { HorasCargue = 9m, HorasDescargue = 9m }), Version, null, Ct);

        resultado.Valor.Respuesta.Modo.Should().Be("nueva_version");
        var nueva = _agregada!;
        nueva.RutasOrdenadas[0].VehiculosOrdenados[1].Should().Match<clsVehiculoLineaSolicitud>(v => v.FueraDelEnvio && v.MotivoRechazo == "Sin cupo");
        nueva.RutasOrdenadas[1].Rechazada.Should().BeTrue();
        nueva.LineasNexo().Should().ContainSingle();
        resultado.Valor.Respuesta.AvisoRechazos.Should().StartWith("La versión nueva copia como rechazados");
        cotizacion.RutasOrdenadas[1].VehiculosOrdenados[0].LineaSolicitudNexoId.Should().Be("L-3", "la original no cambia");
    }

    private clsEdicionSolicitudService Servicio() =>
        new(_cotizaciones, _politicas, _ajustes,
            new clsCapturaRutasService(_filas, _costos, _planificador, NullLogger<clsCapturaRutasService>.Instance),
            _folios, _envio, _nexo, _monedas, _mis, new clsResolutorConexosCatalogo(_costos, _catalogoNexo), _idempotencia, _unidad, _registro,
            _auditoria, _reloj, NullLogger<clsEdicionSolicitudService>.Instance);

    private clsCotizacionSolicitud Registrar(clsCotizacionSolicitud cotizacion)
    {
        var conVersion = clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(Folio, _ana, Arg.Any<CancellationToken>()).Returns(conVersion);
        return conVersion;
    }

    /// <summary>Captura por rutas de <paramref name="rutas"/> rutas de la línea 3 (con ids de municipio y códigos), sin enviar a Nexo.</summary>
    private clsCotizacionSolicitud SinEnviar(int rutas = 1)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = Folio, UsuarioId = _ana, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S.",
        }).Valor;
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot, Deadline = clsSolicitudCosteoMuestra.Deadline },
            [.. Enumerable.Range(0, rutas).Select(i => new clsRutaCapturada(
                clsSolicitudCosteoMuestra.RutaCaptura() with { DestinoMunicipioId = 11001 + i }, Linea, i,
                [new clsCamionPlaneado("3S3", "FURGON", 28_500m)], i == 0 ? [new clsCambioConexo("CX-1", "Cargue", 85_000m, 1m)] : []))]);
        cotizacion.RegistrarErrorEnvioCosteo("Nexo no respondió (HTTP 503)");
        return cotizacion;
    }

    /// <summary>La misma captura enviada a Nexo (<c>SOL-1</c>), con margen y, si <paramref name="costeada"/>, con el flete (autocosteo).</summary>
    private clsCotizacionSolicitud EnNexo(bool costeada = true, int rutas = 1)
    {
        var cotizacion = SinEnviar(rutas);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, clsCotizacionMuestra.Ahora, clsCotizacionMuestra.Ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        if (costeada)
        {
            clsCotizacionMuestra.AplicarCosteo(cotizacion, [.. Enumerable.Repeat(2_550_000m, rutas)], EstadoCosteo.Autocosteo, null);
        }

        return cotizacion;
    }

    /// <summary>
    /// El cuerpo de la edición con el contenido guardado (como lo arma el frontend con el GET, con el vehículo elegido de la ruta),
    /// con <paramref name="cambio"/> por ruta.
    /// </summary>
    private static clsEntradaEdicionSolicitud EntradaDe(
        clsCotizacionSolicitud cotizacion, Func<int, clsLineaSolicitudCosteo, clsLineaSolicitudCosteo>? cambio = null) =>
        new(
            [.. cotizacion.RutasOrdenadas.Select((r, i) => (clsRutaEdicionEntrada?)new clsRutaEdicionEntrada(r.Id, new clsLineaLicitacionEntrada(
                null,
                (cambio ?? ((_, l) => l))(i, r.ALineaConElegido()),
                "Importacion",
                [.. r.Conexos.Select(c => (clsConexoCapturaEntrada?)new clsConexoCapturaEntrada(c.IdServicioConexo, c.Cantidad))])))],
            Tipo: "spot",
            NitCliente: cotizacion.NitCliente,
            ClienteNombre: cotizacion.ClienteNombre,
            Deadline: cotizacion.Deadline,
            Notas: cotizacion.Notas,
            Moneda: cotizacion.Moneda);

    private static clsMargenPolitica ConId(clsMargenPolitica politica, int id)
    {
        typeof(clsMargenPolitica).GetProperty(nameof(clsMargenPolitica.Id))!.SetValue(politica, id);
        return politica;
    }
}
