using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Validación adversarial del envío de conexos huérfanos: la variante 4a nunca manda los conexos de una ruta a la línea de OTRA ruta, elige bien con varias líneas en
/// varios camiones, no duplica el POST y el invariante de agenda solo cuenta las rutas cotizables.
/// </summary>
public sealed class clsValidacion259Tests
{
    private static readonly Guid _ana = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsServiciosConexosNexo _nexo = Substitute.For<IClsServiciosConexosNexo>();
    private readonly IClsResolutorConexosCatalogo _resolutor = Substitute.For<IClsResolutorConexosCatalogo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly List<clsEnvioConexosNexo> _registrados = [];
    private readonly List<IReadOnlyList<clsConexoParaNexo>> _posts = [];

    public clsValidacion259Tests()
    {
        _reloj.AhoraUtc.Returns(clsCotizacionMuestra.Ahora);
        _cotizaciones.ListarEnviosConexosNexoAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyList<clsEnvioConexosNexo>)[.. _registrados]);
        _cotizaciones.When(c => c.RegistrarEnvioConexosNexo(Arg.Any<clsEnvioConexosNexo>())).Do(c => _registrados.Add(c.Arg<clsEnvioConexosNexo>()));
        _resolutor.ResolverAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<(string, string?, string?)>>(), Arg.Any<CancellationToken>())
            .Returns(c => new clsResolucionConexosCatalogo(
                c.Arg<IReadOnlyCollection<(string Id, string? IdCatalogo, string? Nombre)>>()
                    .ToDictionary(x => x.Id, x => new clsCostoConexoNexo("CAT-" + x.Id, x.Nombre, 85_000m), StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(), null));
        _nexo.AgregarAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<clsConexoParaNexo>>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                _posts.Add(c.Arg<IReadOnlyList<clsConexoParaNexo>>());
                return clsResultado<clsAgregadoNexo>.Exito(new clsAgregadoNexo(true, null));
            });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsEnvioConexosNexoService Envio() => new(
        _cotizaciones, _nexo, _resolutor, _unidad, Options.Create(new clsOpcionesCosteoNexo()), _reloj, NullLogger<clsEnvioConexosNexoService>.Instance);

    // ------------------------------------------------------------------ Ruta sin camión vigente: nunca la línea de otra ruta

    [Fact]
    public void RutaRechazadaEnUnaEntregada_NoTomaLaUnicaLineaDeLaCotizacionQueEsDeOtraRuta()
    {
        var cotizacion = DosRutasConConexoEnLaPrimera();
        Rechazar(cotizacion, cotizacion.RutasOrdenadas[0]);
        var nexo = CotizacionNexo(("L2", "CL2"));   // la Mesa rechazó L1: la cotización de Nexo solo tiene la línea de la ruta 2

        clsEnvioConexosNexoService.LineaDestino(cotizacion, cotizacion.RutasOrdenadas[0], nexo)
            .Should().BeNull("4a: sin ningún camión vigente con línea en SU ruta quedan fallido_definitivo; CL2 es de la ruta 2");
    }

    [Fact]
    public void RutaConTodosLosCamionesDescartadosEnUnaEntregada_NoTomaLaLineaDeOtraRuta()
    {
        var cotizacion = DosRutasConConexoEnLaPrimera();
        var camion = cotizacion.RutasOrdenadas[0].VehiculosOrdenados[0];
        camion.RegistrarCosteo(EstadoCosteo.Descartada, null, null);
        cotizacion.ResumirRutas();
        cotizacion.RutasCotizables.Should().Contain(cotizacion.RutasOrdenadas[0], "una ruta descartada sigue contando para finalizar y el invariante");

        clsEnvioConexosNexoService.LineaDestino(cotizacion, cotizacion.RutasOrdenadas[0], CotizacionNexo(("L2", "CL2"))).Should().BeNull();
    }

    [Fact]
    public async Task ReintentoDeUnaEntregadaConLaRutaRechazada_NoHacePostALaLineaDeOtraRuta()
    {
        var cotizacion = DosRutasConConexoEnLaPrimera();
        Rechazar(cotizacion, cotizacion.RutasOrdenadas[0]);
        _nexo.LeerCotizacionAsync(_ana, "COT-1", Arg.Any<CancellationToken>()).Returns(CotizacionNexo(("L2", "CL2")));
        // El reintento no recorre las rutas rechazadas (mismo alcance que el bloqueo de finalizar), así que ni siquiera entra; si
        // entrara por otra ruta, igual no hace POST a la línea de otra ruta.
        (await Envio().TieneReintentosAsync(cotizacion, Ct)).Should().BeFalse("el conexo de la ruta rechazada no cuenta para el reintento");

        await Envio().ReintentarAsync(cotizacion, _ana, Ct);

        _posts.SelectMany(p => p).Should().NotContain(i => i.LineaId == "CL2", "el conexo de la ruta 1 terminaría cobrado en la ruta 2 de Nexo");
    }

    // ------------------------------------------------------------------ Varias líneas en varios camiones

    [Fact]
    public void ConVariasLineasEnVariosCamiones_GanaElPrimerCamionVigenteYDentroDeEl_LaPrimeraPorId()
    {
        var (cotizacion, ruta) = TresCamiones(EstadoCosteo.Entregada);
        var nexo = CotizacionNexo(("L3", "CL-0"), ("L1", "CL-z"), ("L2", "CL-a"), ("L1", "CL-y"));

        clsEnvioConexosNexoService.LineaDestino(cotizacion, ruta, nexo).Should().Be("CL-y", "no el menor id global (CL-0) ni el primero de la lista");
    }

    [Fact]
    public void Variante4a_SaltaAlPrimerCamionRechazadoYAlVigenteSinLinea()
    {
        var (cotizacion, ruta) = TresCamiones(EstadoCosteo.Entregada);
        ruta.VehiculosOrdenados[0].RegistrarCosteo(EstadoCosteo.Rechazada, null, null);
        var nexo = CotizacionNexo(("L1", "CL-1"), ("L3", "CL-3b"), ("L3", "CL-3a"));   // L1 rechazada (aún en la lista), L2 sin línea

        clsEnvioConexosNexoService.LineaDestino(cotizacion, ruta, nexo).Should().Be("CL-3a");
    }

    [Fact]
    public void Variante4a_UnaLineaSinIdNoCuentaComoLinea()
    {
        var (cotizacion, ruta) = TresCamiones(EstadoCosteo.Entregada);
        var nexo = CotizacionNexo(("L1", "  "), ("L2", "CL-2"));

        clsEnvioConexosNexoService.LineaDestino(cotizacion, ruta, nexo).Should().Be("CL-2");
    }

    // ------------------------------------------------------------------ El POST no se duplica

    [Fact]
    public async Task Variante4a_DosPasadasSobreLaMismaEntregadaHacenUnSoloPost()
    {
        var (cotizacion, _) = TresCamiones(EstadoCosteo.Entregada);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        var nexo = CotizacionNexo(("L2", "CL-2"));

        await Envio().EnviarPendientesAsync(cotizacion, nexo, _ana, Ct);
        await Envio().EnviarPendientesAsync(cotizacion, nexo, _ana, Ct);
        (await Envio().TieneReintentosAsync(cotizacion, Ct)).Should().BeFalse();

        _posts.Should().ContainSingle().Which.Should().ContainSingle().Which.LineaId.Should().Be("CL-2");
        clsEnvioConexosNexoService.Estados(_registrados)["CX-1"].Should().Be(clsEnvioConexosNexo.Enviado);
    }

    [Fact]
    public async Task EnCosteoConVariasLineasDelPrimerCamion_NoEsperaEnviaALaPrimeraPorId()
    {
        var (cotizacion, _) = TresCamiones(EstadoCosteo.Autocosteo);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);

        await Envio().EnviarPendientesAsync(cotizacion, CotizacionNexo(("L1", "CL-9"), ("L1", "CL-10")), _ana, Ct);

        _posts.Should().ContainSingle().Which.Single().LineaId.Should().Be("CL-10", "orden ordinal: \"CL-10\" < \"CL-9\"");
    }

    // ------------------------------------------------------------------ Invariante de agenda: solo rutas cotizables

    [Fact]
    public async Task ElInvarianteNoRetieneEnLaAgendaPorUnPendienteDeUnaRutaRechazada()
    {
        var cotizacion = DosRutasConConexoEnLaPrimera();
        Rechazar(cotizacion, cotizacion.RutasOrdenadas[0]);
        cotizacion.ProgramarRelecturaCosteo(clsCotizacionMuestra.Ahora);
        var envio = Substitute.For<IClsEnvioConexosNexoService>();
        envio.TieneReintentosAsync(cotizacion, Arg.Any<CancellationToken>()).Returns(true);
        envio.ReintentarAsync(cotizacion, _ana, Arg.Any<CancellationToken>()).Returns(clsResultadoEnvioConexos.Nada);
        envio.EstadosAsync(cotizacion, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<(Guid, string), string> { [(cotizacion.RutasOrdenadas[0].Id, "CX-1")] = clsEnvioConexosNexoService.VisiblePendiente });
        _cotizaciones.ReclamarConsultaCosteoAsync(cotizacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cotizaciones.ObtenerPorIdAsync(cotizacion.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        var polling = new clsPollingCosteoService(_cotizaciones, Substitute.For<IClsCosteoNexo>(), _unidad, Substitute.For<IClsAuditoriaService>(), _reloj,
            Options.Create(new clsOpcionesCosteoNexo()), NullLogger<clsPollingCosteoService>.Instance, new clsPrecioCosteoService(), envio);

        var resultado = await polling.ProcesarAsync(new clsConsultaCosteoPendiente(cotizacion.Id, clsCotizacionMuestra.Ahora), Ct);

        resultado.Should().Be(ResultadoConsultaCosteo.Detenida);
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull();
    }

    // ------------------------------------------------------------------ Apoyo

    /// <summary>Formulario Spot de dos rutas (un camión cada una: L1 y L2) entregado, con el conexo <c>CX-1</c> en la ruta 1.</summary>
    private static clsCotizacionSolicitud DosRutasConConexoEnLaPrimera()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada(
            [2_000_000m, 3_000_000m], estado: EstadoCosteo.Entregada, usuarioId: _ana, tipo: TipoSolicitud.Spot);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m, orden: 1);
        cotizacion.CotizacionNexoId.Should().Be("COT-1");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        return cotizacion;
    }

    private static void Rechazar(clsCotizacionSolicitud cotizacion, clsLineaSolicitud ruta)
    {
        foreach (var camion in ruta.VehiculosOrdenados)
        {
            camion.RegistrarCosteo(EstadoCosteo.Rechazada, null, null);
        }

        cotizacion.ResumirRutas();
        ruta.Rechazada.Should().BeTrue();
        cotizacion.RutasCotizables.Should().NotContain(ruta);
    }

    /// <summary>Una ruta con tres camiones vigentes: L1, L2 y L3.</summary>
    private static (clsCotizacionSolicitud Cotizacion, clsLineaSolicitud Ruta) TresCamiones(EstadoCosteo estado)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(estado: estado, usuarioId: _ana);
        var ruta = cotizacion.PrimeraRuta!;
        cotizacion.AsignarLineaNexo(ruta.AgregarVehiculo("2S2", null, 8_000m).Id, "L2");
        cotizacion.AsignarLineaNexo(ruta.AgregarVehiculo("2S2", null, 8_000m).Id, "L3");
        ruta.VehiculosOrdenados.Select(v => v.LineaSolicitudNexoId).Should().Equal("L1", "L2", "L3");
        return (cotizacion, ruta);
    }

    private static clsCotizacionNexo CotizacionNexo(params (string LineaSolicitud, string Id)[] lineas) =>
        new("COT-1", clsCotizacionNexo.EstadoEntregada, "SinDefinir", [.. lineas.Select(l => new clsLineaCosteoNexo(l.LineaSolicitud, 1_000_000m, null, null, l.Id))], []);
}
