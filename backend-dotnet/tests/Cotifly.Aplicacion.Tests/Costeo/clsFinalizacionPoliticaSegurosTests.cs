using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Seguros;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-046.3 (#269): "Finalizar" con la política de seguros. Sin el acompañante desde 350 M responde <c>COTIZACION_NO_FINALIZABLE</c> con
/// <c>medidas_obligatorias_faltantes</c>; con justificación finaliza y la audita (sin montos); la justificación no salta los conexos
/// pendientes en Nexo (decisión del 2026-11-06); la política se lee en cada finalización (sin caché); los códigos en NULL se completan
/// con el catálogo antes de evaluar.
/// </summary>
public sealed class clsFinalizacionPoliticaSegurosTests
{
    private const int Version = 4;
    private const decimal M = clsPoliticaSegurosMuestra.Millon;
    private static readonly DateTimeOffset _ahora = new(2026, 11, 8, 15, 0, 0, TimeSpan.Zero);
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly IClsEnvioConexosNexoService _envioConexos = Substitute.For<IClsEnvioConexosNexoService>();
    private readonly IClsServiciosConexosNexo _nexo = Substitute.For<IClsServiciosConexosNexo>();
    private readonly IClsPoliticaSegurosRepository _politicas = Substitute.For<IClsPoliticaSegurosRepository>();
    private readonly IClsCategoriasConexos _catalogo = Substitute.For<IClsCategoriasConexos>();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly List<(object? Entrada, object? Salida)> _auditados = [];
    private clsCotizacionSolicitud _cotizacion = null!;

    public clsFinalizacionPoliticaSegurosTests()
    {
        Usar(400 * M);
        _reloj.AhoraUtc.Returns(_ahora);
        _envioConexos.EstadosAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), string>());
        _politicas.ObtenerAsync(Arg.Any<CancellationToken>()).Returns(_ => clsPoliticaSegurosMuestra.Aprobada());
        _catalogo.CodigosAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        _auditoria.RegistrarAsync(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask)
            .AndDoes(c => _auditados.Add((c.ArgAt<object?>(1), c.ArgAt<object?>(2))));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Usar(decimal? valor, params string[] conexos)
    {
        _cotizacion = clsCotizacionMuestra.ConVersionNegocio(
            clsCotizacionMuestra.Costeada(usuarioId: _ana, solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = valor }), Version);
        foreach (var codigo in conexos)
        {
            _cotizacion.PrimeraRuta!.AgregarOActualizarConexo(
                new clsCambioConexo($"FILA-{codigo}", codigo, 100_000m, 1m, $"CAT-{codigo}", "adicional", CodigoServicio: codigo));
        }

        _cotizacion.RecalcularTotales(_ahora);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
    }

    private clsFinalizacionCotizacionService Servicio() => new(_cotizaciones, _unidad, _reloj, _auditoria, _envioConexos, _nexo,
        NullLogger<clsFinalizacionCotizacionService>.Instance, _politicas, _catalogo);

    private static JsonElement Json(object? valor) => JsonSerializer.SerializeToElement(valor);

    [Fact]
    public async Task SinElAcompananteDesde350MRespondeNoFinalizableConLasMedidasYNoGuarda()
    {
        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Datos!["motivo"].Should().Be("medidas_obligatorias_faltantes");
        Json(resultado.Error.Datos["rutas"]).ToString().Should().Contain("\"linea_orden\":1").And.Contain("acompanante_vehicular").And.Contain("AVT_AUTO");
        _cotizacion.Finalizada.Should().BeFalse();
        await _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, Ct);
        _auditados.Should().ContainSingle();
    }

    [Fact]
    public async Task ConElAcompananteFinalizaSinJustificacion()
    {
        Usar(400 * M, "AVT_AUTO");

        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        Json(_auditados.Single().Salida).GetProperty("justificacion_aplicada").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ConJustificacionFinalizaYLaAuditaConLasMedidasOmitidasSinMontos()
    {
        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, "  Riesgo aprobó el viaje sin acompañante.  ", Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.Finalizada.Should().BeTrue();
        var (entrada, salida) = _auditados.Single();
        Json(entrada).GetProperty("justificacion").GetString().Should().Be("Riesgo aprobó el viaje sin acompañante.");
        var auditada = Json(salida);
        auditada.GetProperty("justificacion_aplicada").GetBoolean().Should().BeTrue();
        auditada.GetProperty("medidas_omitidas")[0].GetProperty("linea_orden").GetInt32().Should().Be(1);
        auditada.GetProperty("medidas_omitidas")[0].GetProperty("medidas")[0].GetString().Should().Be("acompanante_vehicular");
        auditada.ToString().Should().NotContain("precio").And.NotContain("costo").And.NotContain("valor");
    }

    [Fact]
    public async Task LaJustificacionNoSaltaLosConexosPendientesEnNexo()
    {
        _envioConexos.EstadosAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), string> { [(_cotizacion.PrimeraRuta!.Id, "S-1")] = clsEnvioConexosNexoService.VisiblePendiente });
        _nexo.LeerCotizacionAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "Nexo no responde."));

        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, "Riesgo lo aprobó.", Ct);

        resultado.Error.Datos!["motivo"].Should().Be(clsCotizacionSolicitud.CodigoConexosPendientesEnNexo);
        _cotizacion.Finalizada.Should().BeFalse();
    }

    [Fact]
    public async Task UnaJustificacionQueNoCabeEsSolicitudInvalidaSinLeerLaCotizacion()
    {
        var resultado = await Servicio().FinalizarAsync(
            _ana, _cotizacion.Folio, Version, new string('x', clsCotizacionSolicitud.LargoMaximoJustificacion + 1), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        await _cotizaciones.DidNotReceiveWithAnyArgs().ObtenerPorFolioDelUsuarioAsync(default!, default, Ct);
    }

    [Fact]
    public async Task UnaRutaSinValorDeMercanciaNoBloquea()
    {
        Usar(null);

        (await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task LaPoliticaSeLeeEnCadaFinalizacionUnCambioSeVeSinDesplegar()
    {
        _politicas.ObtenerAsync(Arg.Any<CancellationToken>()).Returns(clsPoliticaSeguros.Vacia, clsPoliticaSegurosMuestra.Aprobada());
        Usar(400 * M);
        var otra = clsCotizacionMuestra.ConVersionNegocio(clsCotizacionMuestra.Costeada(usuarioId: _ana, folio: "PCN-AP-28092026-099",
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = 400 * M }), Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(otra.Folio, _ana, Arg.Any<CancellationToken>()).Returns(otra);

        (await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue("la política vacía no exige nada");
        var despues = await Servicio().FinalizarAsync(_ana, otra.Folio, Version, Ct);

        despues.Error.Datos!["motivo"].Should().Be("medidas_obligatorias_faltantes", "la siguiente lectura ya trae la política cambiada");
        await _politicas.Received(2).ObtenerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnConexoSinCodigoSeCompletaConElCatalogoAntesDeEvaluarYSeGuardaConLaFinalizacion()
    {
        Usar(400 * M);
        _cotizacion.PrimeraRuta!.AgregarOActualizarConexo(new clsCambioConexo("FILA-VIEJA", "Acompañante total en moto", 100_000m, 1m));
        _catalogo.CodigosAsync(_cotizacion, Arg.Any<CancellationToken>()).Returns(new Dictionary<string, string> { ["FILA-VIEJA"] = "AVT_MOTO" });

        var resultado = await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _cotizacion.PrimeraRuta.Conexos.Single(c => c.IdServicioConexo == "FILA-VIEJA").CodigoServicio.Should().Be("AVT_MOTO");
    }

    [Fact]
    public async Task SiLaPoliticaNoSePuedeLeerNoSeFinaliza()
    {
        _politicas.ObtenerAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("tabla caída"));

        var finalizar = () => Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, "Riesgo lo aprobó.", Ct);

        await finalizar.Should().ThrowAsync<InvalidOperationException>();
        _cotizacion.Finalizada.Should().BeFalse();
    }

    [Fact]
    public async Task ReFinalizarNoLeeLaPolitica()
    {
        _cotizacion.Finalizar(_ana, _ahora.AddHours(-1), medidasFaltantes: [], justificacion: null);

        (await Servicio().FinalizarAsync(_ana, _cotizacion.Folio, Version, Ct)).Valor.Should().BeSameAs(_cotizacion);
        await _politicas.DidNotReceiveWithAnyArgs().ObtenerAsync(default);
    }
}
