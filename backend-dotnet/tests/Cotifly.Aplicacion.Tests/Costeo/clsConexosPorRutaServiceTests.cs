using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Conexos por ruta (HU-021.8, #135, decisión 3): <c>PUT .../costeo/servicios-conexos</c> quita conexos (se BORRA la fila de la ruta) o
/// cambia su cantidad con el costo unitario de Nexo; recalcula esa ruta y la cabecera con el margen de la ruta, invalida el PDF, sube la
/// versión y AUDITA cada movimiento (sin montos). Pliego de 3 rutas al 15 % (precios 1.000.000, 2.000.000 y 500.000) con dos conexos
/// en la ruta 2: Cargue (85.000 × 2) y Escolta (42.500 × 1).
/// </summary>
public sealed class clsConexosPorRutaServiceTests
{
    private const int Version = 4;
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;
    private object? _salidaAuditada;

    public clsConexosPorRutaServiceTests()
    {
        _cotizacion = clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m], folio: "PCN-L1", usuarioId: _ana);
        clsCotizacionMuestra.ConConexo(_cotizacion, "CX-1", "Cargue", 85_000m, 2m, orden: 2);
        clsCotizacionMuestra.ConConexo(_cotizacion, "CX-2", "Escolta", 42_500m, 1m, orden: 2);
        clsCotizacionMuestra.ConVersionNegocio(_cotizacion, Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-L1", _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        _auditoria.RegistrarAsync(clsCosteoCotizacionService.HerramientaConexos, Arg.Any<object?>(), Arg.Do<object?>(s => _salidaAuditada = s),
            Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>()).Returns(Task.CompletedTask);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QuitarUnConexoDeLaRutaBorraLaFilaRecalculaYQuedaAuditado()
    {
        _cotizacion.AsignarPdf("PCN-L1.pdf");

        var resultado = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null, [new clsCantidadConexoEntrada("CX-2", 1m)]), Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _cotizacion.RutasOrdenadas[1].Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-2");
        _cotizacion.RutasOrdenadas[1].PrecioFinalLinea.Should().Be(2_050_000m, "(1.700.000 + 42.500) / 0,85");
        resultado.Valor.PrecioFinal.Should().Be(3_550_000m);
        resultado.Valor.Rutas[1].ServiciosConexos.Select(s => s.Id).Should().Equal("CX-2");
        resultado.Valor.ServiciosConexos.Single().LineaOrden.Should().Be(2);
        resultado.Valor.PdfDesactualizado.Should().BeTrue("ADR-018 §11: el PDF vigente se invalida");
        resultado.Valor.RowVersion.Should().Be((Version + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h => h.Motivo == MotivoInvalidacionPdf.ServiciosConexos));

        await _auditoria.Received(1).RegistrarAsync(clsCosteoCotizacionService.HerramientaConexos,
            Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
        var salida = JsonSerializer.Serialize(_salidaAuditada);
        salida.Should().Contain("\"quitado\":true").And.Contain("\"id\":\"CX-1\"").And.Contain("\"linea_orden\":2");
        salida.Should().NotContain("85000").And.NotContain("2050000", "la auditoría no lleva montos");
    }

    [Fact]
    public async Task CambiarLaCantidadUsaElCostoUnitarioDeNexoYElMargenDeLaRuta()
    {
        var resultado = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null,
                [new clsCantidadConexoEntrada("CX-1", 1m), new clsCantidadConexoEntrada("CX-2", 1m)]), Version, Ct);

        resultado.Valor.Rutas[1].ServiciosConexos.Single(s => s.Id == "CX-1").Cantidad.Should().Be(1m);
        resultado.Valor.Rutas[1].ServiciosConexos.Single(s => s.Id == "CX-1").Precio.Should().Be(100_000m, "85.000 / 0,85");
        _cotizacion.RutasOrdenadas[1].PrecioFinalLinea.Should().Be(2_150_000m, "(1.700.000 + 85.000 + 42.500) / 0,85");
        _cotizacion.RutasOrdenadas.Select(r => r.PrecioFinalLinea).Should().Equal(1_000_000m, 2_150_000m, 500_000m);
        JsonSerializer.Serialize(_salidaAuditada).Should().Contain("\"cantidad_anterior\":2").And.Contain("\"cantidad_nueva\":1");
    }

    [Fact]
    public async Task ConVariasRutasYSinIndicarLaRutaRespondeRutaRequerida()
    {
        var resultado = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.Ninguna, null, [new clsCantidadConexoEntrada("CX-2", 1m)]), Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.RutaRequerida);
        _cotizacion.RutasOrdenadas[1].Conexos.Should().HaveCount(2);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task UnConexoDeOtraRutaUnaCantidadFueraDeRangoOLasDosFormasJuntasSeRechazan()
    {
        var deOtra = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(1), null, [new clsCantidadConexoEntrada("CX-1", 1m)]), Version, Ct);
        var cantidad = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null, [new clsCantidadConexoEntrada("CX-1", 0m)]), Version, Ct);
        var juntas = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), ["CX-1"], [new clsCantidadConexoEntrada("CX-1", 1m)]), Version, Ct);
        var repetido = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null,
                [new clsCantidadConexoEntrada("CX-1", 1m), new clsCantidadConexoEntrada("cx-1", 2m)]), Version, Ct);

        deOtra.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        cantidad.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        juntas.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        repetido.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _cotizacion.RutasOrdenadas[1].Conexos.Should().HaveCount(2, "nada cambió");
    }

    [Fact]
    public async Task IncluidosConRutaSoloQuitaEnEsaRutaYSinRutaEnTodas()
    {
        clsCotizacionMuestra.ConConexo(_cotizacion, "CX-3", "Precinto", 8_500m, 1m, orden: 1);

        var conRuta = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), ["CX-1"], null), Version, Ct);

        conRuta.EsExitoso.Should().BeTrue();
        _cotizacion.RutasOrdenadas[0].Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-3");
        _cotizacion.RutasOrdenadas[1].Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-1");

        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>()).Returns(true);
        var sinRuta = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.Ninguna, [], null), Version + 1, Ct);

        sinRuta.EsExitoso.Should().BeTrue();
        _cotizacion.Rutas.Should().OnlyContain(r => r.Conexos.Count == 0, "HU-005.7: 'incluidos' vacío sin ruta quita todos");
    }

    [Fact]
    public async Task IncluidosComparaLosIdsConElMismoCriterioQueLaValidacion()
    {
        // H1 (#135): "cx-2" pasa la validación (sin distinguir mayúsculas) y por eso CONSERVA "CX-2"; antes lo validaba y lo borraba.
        var resultado = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), ["cx-2"], null), Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.RutasOrdenadas[1].Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-2");
    }

    [Fact]
    public async Task SinIncluidosNiConexosRespondeSolicitudInvalidaYNoBorraNada()
    {
        // H2 (#135): {"linea_orden": 2} sin 'incluidos' ni 'conexos' no vacía la ruta.
        var resultado = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null, null), Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _cotizacion.RutasOrdenadas[1].Conexos.Should().HaveCount(2);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task LasMismasCantidadesNoInvalidanElPdfNiSubenLaVersion()
    {
        _cotizacion.AsignarPdf("PCN-L1.pdf");

        var resultado = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null,
                [new clsCantidadConexoEntrada("CX-1", 2m), new clsCantidadConexoEntrada("CX-2", 1m)]), Version, Ct);

        resultado.Valor.PdfDesactualizado.Should().BeFalse();
        _cotizacion.PdfPath.Should().Be("PCN-L1.pdf");
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    [Fact]
    public async Task ConOtraVersionOYaEnviadaNoCambiaNada()
    {
        var conflicto = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null, []), Version - 1, Ct);
        _cotizacion.AsignarPdf("PCN-L1.pdf");
        _cotizacion.RegistrarEnvio();
        var enviada = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null, []), Version, Ct);

        conflicto.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        enviada.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        _cotizacion.RutasOrdenadas[1].Conexos.Should().HaveCount(2);
    }

    [Fact]
    public async Task HU0363_FinalizadaNoCambiaLosConexosYPideLaVersionNueva()
    {
        _cotizacion.Finalizar(_ana, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();

        var finalizada = await Servicio().SeleccionarServiciosConexosAsync(_ana, "PCN-L1",
            new clsCambioServiciosConexos(clsRutaIndicada.DeOrden(2), null, []), Version, Ct);

        finalizada.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        finalizada.Error.Datos!["accion"].Should().Be("nueva_version");
        _cotizacion.RutasOrdenadas[1].Conexos.Should().HaveCount(2);
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
    }

    private clsCosteoCotizacionService Servicio() => new(
        _cotizaciones, Substitute.For<IClsMargenPoliticaRepository>(), Substitute.For<IClsUsuarioWebRepository>(), Substitute.For<IClsGeneradorPdf>(),
        Substitute.For<IClsAlmacenPdf>(), _unidad, Substitute.For<IClsCalendarioNegocio>(), _auditoria, Options.Create(new clsOpcionesPdf()),
        NullLogger<clsCosteoCotizacionService>.Instance);
}
