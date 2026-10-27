using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-041.1 (#231, ADR-025): margen y servicios conexos para varias rutas en una operación. Licitación de 5 rutas al 15 % (flete 850.000
/// cada una: precio 1.000.000) para el margen y un formulario de 3 rutas para los conexos (las licitaciones no los admiten). Todo o nada:
/// una ruta que no admite el cambio deja todas como estaban y el error dice cuál; una invalidación del PDF y una auditoría por operación.
/// </summary>
public sealed class clsCosteoLineasServiceTests
{
    private const int Version = 4;
    private const string Folio = "PCN-L5";
    private const string FolioSpot = "PCN-S3";
    private const string Cargue = "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d";
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenAjusteRepository _ajustes = Substitute.For<IClsMargenAjusteRepository>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsCostosConexosNexo _costos = Substitute.For<IClsCostosConexosNexo>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;
    private readonly clsCotizacionSolicitud _spot;
    private string? _herramientaAuditada;
    private object? _salidaAuditada;

    public clsCosteoLineasServiceTests()
    {
        _cotizacion = clsCotizacionMuestra.LicitacionCosteada([850_000m, 850_000m, 850_000m, 850_000m, 850_000m], folio: Folio, usuarioId: _ana);
        clsCotizacionMuestra.ConVersionNegocio(_cotizacion, Version);
        _spot = clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m], folio: FolioSpot, usuarioId: _ana, tipo: TipoSolicitud.Spot);
        clsCotizacionMuestra.ConVersionNegocio(_spot, Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(FolioSpot, _ana, Arg.Any<CancellationToken>()).Returns(_spot);
        _cotizaciones.ListarEnviosConexosNexoAsync(FolioSpot, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Version, Arg.Any<CancellationToken>()).Returns(true);
        _reloj.AhoraUtc.Returns(clsCotizacionMuestra.Ahora);
        _costos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(
                new Dictionary<string, clsCostoConexoNexo> { [Cargue] = new(Cargue, "Cargue en puerto", 85_000m, "logistico") }));
        _auditoria.RegistrarAsync(Arg.Do<string>(h => _herramientaAuditada = h), Arg.Any<object?>(), Arg.Do<object?>(s => _salidaAuditada = s),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>()).Returns(Task.CompletedTask);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------ Margen

    [Fact]
    public async Task ElMismoMargenACincoRutasEnUnaLlamadaInvalidaElPdfUnaVezYAuditaUnaVez()
    {
        _cotizacion.AsignarPdf("PCN-L5.pdf");

        var resultado = await Servicio().AjustarMargenAsync(_ana, Folio, Margen([1, 2, 3, 4, 5], 0.20m), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _cotizacion.Rutas.Should().OnlyContain(r => r.MargenAplicado == 0.20m && r.OrigenMargen == OrigenMargen.Negociado);
        resultado.Valor.Rutas.Select(r => r.PrecioFinalLinea).Should().OnlyContain(p => p == 1_062_500m, "850.000 / (1 − 0,20)");
        resultado.Valor.PrecioFinal.Should().Be(5_312_500m);
        resultado.Valor.PdfDesactualizado.Should().BeTrue();
        resultado.Valor.RowVersion.Should().Be((Version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "una sola versión nueva");
        _ajustes.Received(5).Agregar(Arg.Is<clsMargenAjuste>(a => a.MargenAplicado == 0.20m && a.MargenPolitica == 0.15m && a.LineaSolicitudId != null));
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h => h.Motivo == MotivoInvalidacionPdf.AjusteMargen));
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>());
        _herramientaAuditada.Should().Be(clsCosteoLineasService.HerramientaMargen);
        JsonSerializer.Serialize(_salidaAuditada).Should().Contain("\"rutas_cambiadas\":5").And.Contain("\"pdf_invalidado\":true")
            .And.NotContain("1062500", "la auditoría no lleva montos");
    }

    [Fact]
    public async Task RevertirAlSugeridoEnTodasDejaCadaRutaEnSuSugeridoSinRegistrarAjustes()
    {
        foreach (var orden in new[] { 1, 2, 3 })
        {
            _cotizacion.AplicarMargenNegociado(orden, 0.25m, clsCotizacionMuestra.Ahora).EsExitoso.Should().BeTrue();
        }

        var resultado = await Servicio().AjustarMargenAsync(_ana, Folio, Revertir(), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _cotizacion.Rutas.Should().OnlyContain(r => r.MargenAplicado == 0.15m && r.OrigenMargen == OrigenMargen.PoliticaLinea);
        resultado.Valor.Rutas.Should().OnlyContain(r => r.OrigenMargen == "politica_linea" && r.PrecioFinalLinea == 1_000_000m);
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        JsonSerializer.Serialize(_salidaAuditada).Should().Contain("\"rutas_cambiadas\":3");
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnaRutaSinPrecioBloqueaLaOperacionYNingunaRutaCambia(bool todas)
    {
        var cotizacion = clsCotizacionMuestra.Licitacion(5, "PCN-B5", _ana);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, clsCotizacionMuestra.Ahora, clsCotizacionMuestra.Ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        clsCotizacionMuestra.AplicarCosteo(cotizacion, [850_000m, 850_000m, 850_000m], EstadoCosteo.Autocosteo, null);
        clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-B5", _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        var entrada = todas ? new clsMargenLineasEntrada(new clsSeleccionLineas(null, true), 0.20m, null, null) : Margen([1, 2, 3, 4, 5], 0.20m);

        var resultado = await Servicio().AjustarMargenAsync(_ana, "PCN-B5", entrada, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
        resultado.Error.Datos!["linea_orden"].Should().Be(4, "la primera ruta que bloquea");
        resultado.Error.Datos["linea_solicitud_id"].Should().Be(cotizacion.RutasOrdenadas[3].Id);
        cotizacion.Rutas.Should().OnlyContain(r => r.OrigenMargen == OrigenMargen.PoliticaLinea && r.MargenAplicado == 0.15m, "nada cambió");
        _ajustes.DidNotReceiveWithAnyArgs().Agregar(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        await _auditoria.Received(1).RegistrarAsync(clsCosteoLineasService.HerramientaMargen, Arg.Any<object?>(), Arg.Any<object?>(), false,
            Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task PorDebajoDelSugeridoSinJustificacionDiceLaRutaYSuSugerido()
    {
        var resultado = await Servicio().AjustarMargenAsync(_ana, Folio, Margen([2, 3], 0.10m), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.JustificacionRequerida);
        resultado.Error.Datos!["linea_orden"].Should().Be(2);
        resultado.Error.Datos["margen_sugerido"].Should().Be(0.15m);
        _cotizacion.Rutas.Should().OnlyContain(r => r.MargenAplicado == 0.15m);

        var justificada = await Servicio().AjustarMargenAsync(_ana, Folio,
            new clsMargenLineasEntrada(new clsSeleccionLineas([2, 3], null), 0.10m, null, "Cliente estratégico"), Version, null, Ct);

        justificada.EsExitoso.Should().BeTrue();
        _ajustes.Received(2).Agregar(Arg.Is<clsMargenAjuste>(a => a.Motivo == "Cliente estratégico"));
    }

    [Fact]
    public async Task UnaRutaQueNoExisteSeRechazaConSuLineaOrden()
    {
        var resultado = await Servicio().AjustarMargenAsync(_ana, Folio, Margen([1, 9], 0.20m), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.RutaInexistente);
        resultado.Error.Datos!["linea_orden"].Should().Be(9);
        _cotizacion.Rutas.Should().OnlyContain(r => r.MargenAplicado == 0.15m);
    }

    [Fact]
    public async Task ConOtraVersionOFinalizadaNoCambiaNada()
    {
        var conflicto = await Servicio().AjustarMargenAsync(_ana, Folio, Margen([1], 0.20m), Version - 1, null, Ct);
        _cotizacion.Finalizar(_ana, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        var finalizada = await Servicio().AjustarMargenAsync(_ana, Folio, Margen([1], 0.20m), Version, null, Ct);

        conflicto.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        finalizada.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        finalizada.Error.Datos!["accion"].Should().Be("nueva_version");
        _cotizacion.Rutas.Should().OnlyContain(r => r.MargenAplicado == 0.15m);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task LaMismaOperacionRepetidaNoInvalidaElPdfNiSubeLaVersion()
    {
        _cotizacion.AsignarPdf("PCN-L5.pdf");

        var resultado = await Servicio().AjustarMargenAsync(_ana, Folio, Margen([1, 2], 0.15m), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.PdfDesactualizado.Should().BeFalse();
        _cotizacion.PdfPath.Should().Be("PCN-L5.pdf");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        JsonSerializer.Serialize(_salidaAuditada).Should().Contain("\"rutas_cambiadas\":0");
    }

    [Fact]
    public async Task UnaFormaInvalidaEsSolicitudInvalidaSinLeerLaCotizacion()
    {
        var formas = new[]
        {
            new clsMargenLineasEntrada(new clsSeleccionLineas(null, null), 0.2m, null, null),
            new clsMargenLineasEntrada(new clsSeleccionLineas([1], true), 0.2m, null, null),
            new clsMargenLineasEntrada(new clsSeleccionLineas([1], null), 0.2m, true, null),
            new clsMargenLineasEntrada(new clsSeleccionLineas([1], null), null, null, null),
            new clsMargenLineasEntrada(new clsSeleccionLineas([1, 1], null), 0.2m, null, null),
            new clsMargenLineasEntrada(new clsSeleccionLineas([0], null), 0.2m, null, null),
            new clsMargenLineasEntrada(new clsSeleccionLineas([], null), 0.2m, null, null),
            new clsMargenLineasEntrada(new clsSeleccionLineas([.. Enumerable.Range(1, clsCosteoLineasService.MaximoLineas + 1).Select(i => (int?)i)], null), 0.2m, null, null),
        };

        foreach (var forma in formas)
        {
            (await Servicio().AjustarMargenAsync(_ana, Folio, forma, Version, null, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        }

        (await Servicio().AjustarMargenAsync(_ana, Folio, Margen([1], 1.5m), Version, null, Ct)).Error.Codigo.Should().Be(clsCodigosError.AjusteMargenInvalido);
        await _cotizaciones.DidNotReceiveWithAnyArgs().ObtenerPorFolioDelUsuarioAsync(default!, default, Ct);
    }

    [Fact]
    public async Task UnaCotizacionAjenaEsNoEncontrada()
    {
        var resultado = await Servicio().AjustarMargenAsync(Guid.NewGuid(), Folio, Margen([1], 0.2m), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public void LaSeleccionDevuelveLasLineasOrdenadasOTodas()
    {
        clsCosteoLineasService.ValidarSeleccion(new clsSeleccionLineas([3, 1], null)).Valor.Should().Equal(1, 3);
        clsCosteoLineasService.ValidarSeleccion(new clsSeleccionLineas(null, true)).Valor.Should().BeEmpty("todas");
    }

    // ------------------------------------------------------------------ Conexos

    [Fact]
    public async Task UnConexoAgregadoATodasQuedaEnCadaRutaConSuServicioIdYEnEsperaDelEnvioDiferido()
    {
        _spot.AsignarPdf("PCN-S3.pdf");
        _spot.DetenerPollingCosteo();

        var resultado = await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo(null, Cargue, 2m), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        resultado.Valor.Rutas.Should().OnlyContain(r => r.ServiciosConexos.Count == 1 && r.ServiciosConexos[0].ServicioId == Cargue
            && r.ServiciosConexos[0].Cantidad == 2m && r.ServiciosConexos[0].Precio == 200_000m, "85.000 × 2 / 0,85, con el costo del catálogo");
        _spot.RutasOrdenadas[1].PrecioFinalLinea.Should().Be(2_200_000m, "(1.700.000 + 170.000) / 0,85");
        _cotizaciones.Received(3).RegistrarEnvioConexosNexo(Arg.Is<clsEnvioConexosNexo>(e =>
            e.Estado == clsEnvioConexosNexo.EnEspera && e.Conexos.Single() == Cargue && e.EsperaDesde == clsCotizacionMuestra.Ahora));
        _spot.CosteoSiguienteConsultaAt.Should().Be(clsCotizacionMuestra.Ahora, "con el polling detenido vuelve a la agenda para el envío");
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h => h.Motivo == MotivoInvalidacionPdf.ServiciosConexos));
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_spot.Id, Version, Arg.Any<CancellationToken>());
        _herramientaAuditada.Should().Be(clsCosteoLineasService.HerramientaAgregarConexo);
        JsonSerializer.Serialize(_salidaAuditada).Should().Contain("\"rutas_cambiadas\":3").And.NotContain("85000");
    }

    [Fact]
    public async Task UnaRutaQueYaTieneElConexoNoCambiaYSiTodasLoTienenNoSeGuardaNada()
    {
        clsCotizacionMuestra.ConConexo(_spot, Cargue, "Cargue en puerto", 85_000m, 1m, orden: 2);

        var resultado = await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo([1, 2], Cargue, 2m), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Codigo + ": " + resultado.Error.Mensaje : string.Empty);
        _spot.RutasOrdenadas[1].Conexos.Single().Cantidad.Should().Be(1m, "la ruta 2 ya lo tenía: no cambia");
        _spot.RutasOrdenadas[0].Conexos.Single().Cantidad.Should().Be(2m);
        _cotizaciones.Received(1).RegistrarEnvioConexosNexo(Arg.Any<clsEnvioConexosNexo>());

        _unidad.ClearReceivedCalls();
        var repetida = await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo([1, 2], Cargue, 2m), Version + 1, null, Ct);

        repetida.EsExitoso.Should().BeTrue();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task ConElCatalogoCaidoOElServicioInexistenteNoCambiaNada()
    {
        _costos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Fallo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "Nexo no responde.")));
        var caido = await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo(null, Cargue, 1m), Version, null, Ct);
        _costos.CostosAsync(_ana, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyDictionary<string, clsCostoConexoNexo>>.Exito(new Dictionary<string, clsCostoConexoNexo>()));
        var inexistente = await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo(null, Cargue, 1m), Version, null, Ct);

        caido.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        inexistente.Error.Codigo.Should().Be(clsCodigosError.ConexoInvalido);
        inexistente.Error.Detalle.Should().Be("CONEXO_NO_ENCONTRADO");
        _spot.Rutas.Should().OnlyContain(r => r.Conexos.Count == 0);
        _cotizaciones.DidNotReceiveWithAnyArgs().RegistrarEnvioConexosNexo(default!);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task UnaLicitacionNoAdmiteConexosNiSeLeeElCatalogo()
    {
        var resultado = await Servicio().AgregarConexoAsync(_ana, Folio, Conexo(null, Cargue, 1m), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        await _costos.DidNotReceiveWithAnyArgs().CostosAsync(default, default!, Ct);
    }

    [Fact]
    public async Task UnaRutaSinLineaEnNexoBloqueaYDiceCual()
    {
        _spot.DescartarCamionSinLineaEnNexo(_spot.RutasOrdenadas[2].VehiculosOrdenados[0].Id).Should().BeTrue();

        var resultado = await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo(null, Cargue, 1m), Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.LineaSinCotizacionNexo);
        resultado.Error.Datos!["linea_orden"].Should().Be(3);
        _spot.Rutas.Should().OnlyContain(r => r.Conexos.Count == 0);
    }

    [Fact]
    public async Task QuitarDeTodasBorraElConexoDondeEstaYDejaLasDemasRutas()
    {
        clsCotizacionMuestra.ConConexo(_spot, Cargue, "Cargue en puerto", 85_000m, 2m, orden: 1);
        clsCotizacionMuestra.ConConexo(_spot, Cargue, "Cargue en puerto", 85_000m, 1m, orden: 3);
        clsCotizacionMuestra.ConConexo(_spot, "CX-9", "Escolta", 42_500m, 1m, orden: 3);
        _spot.AsignarPdf("PCN-S3.pdf");

        var resultado = await Servicio().QuitarConexoAsync(_ana, FolioSpot, new clsConexoLineasEntrada(new clsSeleccionLineas(null, true), Cargue), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _spot.RutasOrdenadas[0].Conexos.Should().BeEmpty();
        _spot.RutasOrdenadas[2].Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-9");
        _spot.RutasOrdenadas.Select(r => r.PrecioFinalLinea).Should().Equal(1_000_000m, 2_000_000m, 550_000m);
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Any<clsCotizacionPdfHistorial>());
        _herramientaAuditada.Should().Be(clsCosteoLineasService.HerramientaQuitarConexo);
        JsonSerializer.Serialize(_salidaAuditada).Should().Contain("\"rutas_cambiadas\":2");
    }

    [Fact]
    public async Task QuitarUnConexoQueNingunaRutaTieneRespondeSinCambios()
    {
        var resultado = await Servicio().QuitarConexoAsync(_ana, FolioSpot, new clsConexoLineasEntrada(new clsSeleccionLineas([1, 2], null), Cargue), Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task UnServicioQueNoEsUuidOUnaCantidadFueraDeRangoSonSolicitudInvalida()
    {
        (await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo(null, "CX-1", 1m), Version, null, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        (await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo(null, Cargue, 0m), Version, null, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        (await Servicio().AgregarConexoAsync(_ana, FolioSpot, Conexo(null, Cargue, 1.005m), Version, null, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        (await Servicio().QuitarConexoAsync(_ana, FolioSpot, Conexo(null, Cargue, 1m), Version, null, Ct)).Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
    }

    private static clsMargenLineasEntrada Margen(IReadOnlyList<int?> lineas, decimal margen) => new(new clsSeleccionLineas(lineas, null), margen, null, null);

    private static clsMargenLineasEntrada Revertir() => new(new clsSeleccionLineas(null, true), null, true, null);

    private static clsConexoLineasEntrada Conexo(IReadOnlyList<int?>? lineas, string servicio, decimal cantidad) =>
        new(new clsSeleccionLineas(lineas, lineas is null ? true : null), servicio, cantidad);

    private clsCosteoLineasService Servicio() => new(
        _cotizaciones, _ajustes, _unidad, _auditoria, Options.Create(new clsOpcionesPdf()), Substitute.For<IClsAlmacenIdempotencia>(),
        Substitute.For<IClsEnvioConexosNexoService>(), _costos, Substitute.For<IClsCategoriasConexos>(), Substitute.For<IClsSituacionLineasNexo>(),
        _reloj, NullLogger<clsCosteoLineasService>.Instance);
}
