using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Envío a costeo en Nexo (HU-005.5, ADR-015 §3): la cotización se confirma antes de llamar a Nexo; cualquier fallo la deja en
/// <c>error_envio</c> con el motivo (nunca se pierde) y se puede reintentar sin volver a llenar el formulario.
/// </summary>
public sealed class clsEnvioCosteoServiceTests
{
    private const string Nit = "900123456";

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsMargenResolverService _margen = Substitute.For<IClsMargenResolverService>();
    private readonly IClsGeneradorFolioService _folios = Substitute.For<IClsGeneradorFolioService>();
    private readonly IClsBuscadorClientes _buscador = Substitute.For<IClsBuscadorClientes>();
    private readonly IClsCosteoNexo _nexo = Substitute.For<IClsCosteoNexo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsMargenPolitica _politica = clsMargenPolitica.Crear("Importacion", 0.15m, mapeoLineasNexo: "PuertoAInterior").Valor;
    private readonly List<string> _eventos = [];
    private clsCotizacion? _creada;
    private IReadOnlyList<clsCotizacionPosicion> _posiciones = [];

    public clsEnvioCosteoServiceTests()
    {
        _margen.ResolverLineaAsync("Importación", Arg.Any<CancellationToken>()).Returns(_politica);
        _politicas.ObtenerPorIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_politica);
        _folios.CrearConFolioNuevoAsync(_ana, Nit, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacion>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var folio = clsFolio.Crear(new DateOnly(2026, 9, 28), "AP", 1, Nit).Valor;
                return ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacion>>>>()(folio, CancellationToken.None);
            });
        _cotizaciones.When(r => r.Agregar(Arg.Any<clsCotizacion>())).Do(ci => _creada = ci.Arg<clsCotizacion>());
        _cotizaciones.When(r => r.AgregarPosiciones(Arg.Any<IEnumerable<clsCotizacionPosicion>>()))
            .Do(ci => _posiciones = [.. ci.Arg<IEnumerable<clsCotizacionPosicion>>()]);
        _unidad.GuardarCambiosAsync(Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _eventos.Add($"guardar:{(_creada?.EstadoCosteo is { } e ? clsCodigoEnum.ACodigo(e) : "-")}");
            return 1;
        });
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            _eventos.Add("nexo");
            return new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false);
        });
        ClienteEnCatalogo(new clsClienteEncontrado("900.123.456", "LONGYIN S.A.S.", "cliente", "15927"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LaCotizacionSeConfirmaEnPendienteEnvioAntesDeLlamarANexoYLuegoQuedaPendienteDeCosteo()
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        _eventos.Should().Equal("guardar:pendiente_envio", "nexo", "guardar:pendiente_costeo");
        var cotizacion = resultado.Valor;
        cotizacion.Folio.Should().Be("PCN-900123456-AP-28092026-001", "el folio lo pone el servidor con el NIT (ADR-011)");
        cotizacion.UsuarioId.Should().Be(_ana);
        cotizacion.NitCliente.Should().Be(Nit);
        cotizacion.CosteoId.Should().Be("sol-1");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        cotizacion.CosteoEnviadoAt.Should().Be(_reloj.AhoraUtc);
        cotizacion.CosteoSiguienteConsultaAt.Should().Be(_reloj.AhoraUtc.AddSeconds(5));
        cotizacion.PrecioFinal.Should().BeNull("el costo de Nexo todavía no pasó por el margen");
        _posiciones.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElClienteSaleDelCatalogoDeNexoConSuBpYLaLineaVaConSuMapeo()
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { ClienteNombre = "longyin" }, Ct);

        resultado.Valor.ClienteNombre.Should().Be("LONGYIN S.A.S.");
        resultado.Valor.OrigenCliente.Should().Be(OrigenCliente.Cliente);
        await _nexo.Received(1).EnviarSolicitudAsync(_ana,
            Arg.Is<clsSolicitudNexo>(s => s.ClienteBp == "15927" && s.LineaNegocioNexo == "PuertoAInterior" && s.Solicitud.Origen == "Buenaventura"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiNexoYaCosteoLaLineaQuedaEnAutocosteo()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsSolicitudCreadaNexo("sol-2", 1, 1, 0, false));

        (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
    }

    [Theory]
    [InlineData(clsCodigosError.CosteoNexoTiempoAgotado, "Nexo no respondió a tiempo", null)]
    [InlineData(clsCodigosError.CosteoNexoRechazada, "Nexo rechazó la solicitud.", "HTTP 400 VALIDATION_ERROR: El BP no existe")]
    [InlineData(clsCodigosError.CosteoNexoNoDisponible, "El costeo de Nexo no está disponible", "HTTP 503 (ServiceUnavailable)")]
    public async Task UnFalloDeNexoDejaLaCotizacionEnErrorEnvioConElMotivoYNoSePierde(string codigo, string mensaje, string? detalle)
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, mensaje, detalle));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        resultado.EsExitoso.Should().BeTrue("el comercial no cometió un error: la cotización queda guardada para reintentar");
        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        resultado.Valor.MotivoEstadoCosteo.Should().StartWith(mensaje);
        if (detalle is not null)
        {
            resultado.Valor.MotivoEstadoCosteo.Should().Contain(detalle);
        }

        resultado.Valor.CosteoSiguienteConsultaAt.Should().BeNull("sin solicitud en Nexo no hay nada que consultar");
        _eventos.Should().Equal("guardar:pendiente_envio", "guardar:error_envio");
        await _nexo.Received(1).EnviarSolicitudAsync(_ana, Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(clsEnvioCosteoService.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task SiElCatalogoDeNexoNoRespondeQuedaEnErrorEnvioSinLlamarAlAlta()
    {
        _buscador.BuscarAsync(_ana, Nit, Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.ClienteBusquedaNoDisponible, "caído"));

        var resultado = await Servicio().EnviarAsync(_ana, Entrada(), Ct);

        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        resultado.Valor.MotivoEstadoCosteo.Should().Contain("catálogo de Nexo");
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task UnNitQueNoEstaEnElCatalogoOUnProspectoSinBpQuedanEnErrorEnvio()
    {
        ClienteEnCatalogo(new clsClienteEncontrado("800999999", "OTRO", "cliente", "1"));
        (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor.MotivoEstadoCosteo.Should().Contain("no está en el catálogo");

        ClienteEnCatalogo(new clsClienteEncontrado(Nit, "PROSPECTO S.A.S.", "prospecto"));
        var prospecto = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;

        prospecto.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        prospecto.MotivoEstadoCosteo.Should().Contain("prospecto").And.Contain("Pendiente de confirmar con el equipo de Nexo");
        prospecto.OrigenCliente.Should().Be(OrigenCliente.Prospecto);
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Theory]
    [InlineData(null, "nit_cliente")]
    [InlineData("  ", "nit_cliente")]
    [InlineData("123456789012345678901", "nit_cliente")]
    public async Task SinNitValidoNoSeGuardaNada(string? nit, string campo)
    {
        var resultado = await Servicio().EnviarAsync(_ana, Entrada() with { NitCliente = nit }, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        resultado.Error.Mensaje.Should().Contain(campo);
        _eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task UnFormularioInvalidoOUnaLineaDesconocidaNoGuardanNada()
    {
        var invalido = await Servicio().EnviarAsync(_ana, Entrada() with { Solicitud = clsSolicitudCosteoMuestra.Crear() with { Destino = "" } }, Ct);
        var linea = await Servicio().EnviarAsync(_ana, Entrada() with { LineaNegocio = "Marítimo" }, Ct);

        invalido.Error.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        linea.Error.Codigo.Should().Be(clsCodigosError.MargenLineaDesconocida);
        _eventos.Should().BeEmpty();
        await _folios.DidNotReceiveWithAnyArgs().CrearConFolioNuevoAsync<clsCotizacion>(default, default, default!, Ct);
    }

    [Fact]
    public async Task UnaLineaInactivaNoSePuedeEnviar()
    {
        var inactiva = clsMargenPolitica.Crear("Importacion", 0.15m).Valor;
        inactiva.Desactivar();
        _margen.ResolverLineaAsync("Importación", Arg.Any<CancellationToken>()).Returns(inactiva);

        (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Error.Detalle.Should().Be(clsMargenResolverService.DetalleLineaInactiva);
    }

    [Fact]
    public async Task ElReintentoReenviaLaSolicitudGuardadaSinVolverALlenarElFormulario()
    {
        var cotizacion = await CotizacionEnErrorAsync();
        _cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, cotizacion.UpdatedAt, _reloj.AhoraUtc, Arg.Any<CancellationToken>()).Returns(true);
        _nexo.ClearReceivedCalls();

        var resultado = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        resultado.Valor.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        resultado.Valor.CosteoId.Should().Be("sol-1");
        resultado.Valor.MotivoEstadoCosteo.Should().BeNull();
        await _nexo.Received(1).EnviarSolicitudAsync(_ana,
            Arg.Is<clsSolicitudNexo>(s => s.Solicitud.Posiciones.Count == 2 && s.Solicitud.Referencia == "OC-7788"), Arg.Any<CancellationToken>());
        await _cotizaciones.Received(1).RecargarAsync(cotizacion, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiOtroReintentoGanoElReclamoNoSeVuelveAEnviar()
    {
        var cotizacion = await CotizacionEnErrorAsync();
        _cotizaciones.ReclamarReenvioCosteoAsync(cotizacion.Id, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);
        _nexo.ClearReceivedCalls();

        var resultado = await Servicio().ReintentarAsync(_ana, cotizacion.Folio, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNoReintentable);
        await _nexo.DidNotReceiveWithAnyArgs().EnviarSolicitudAsync(default, default!, Ct);
    }

    [Fact]
    public async Task NoSeReintentaUnaCotizacionYaEnviadaNiUnaAjena()
    {
        var enviada = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;
        Registrada(enviada);

        (await Servicio().ReintentarAsync(_ana, enviada.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNoReintentable);
        (await Servicio().ReintentarAsync(Guid.NewGuid(), enviada.Folio, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
    }

    [Fact]
    public async Task UnPendienteDeEnvioRecienteNoSeReintentaPorqueElEnvioOriginalPuedeSeguirEnCurso()
    {
        var cotizacion = clsCotizacion.Crear(new clsDatosCotizacion { Folio = "PCN-P1", UsuarioId = _ana }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        Registrada(cotizacion);

        cotizacion.PuedeReenviarCosteo(cotizacion.UpdatedAt.AddMinutes(1)).Should().BeFalse();
        cotizacion.PuedeReenviarCosteo(cotizacion.UpdatedAt.AddMinutes(3)).Should().BeTrue();
        await Task.CompletedTask;
    }

    [Fact]
    public void ElMotivoSeRecortaAlLargoDeLaColumna()
    {
        var motivo = clsEnvioCosteoService.Motivo(new clsErrorDominio("X", new string('m', 900), new string('d', 900)));

        motivo.Length.Should().Be(clsCotizacion.LargoMaximoMotivoCosteo);
    }

    private async Task<clsCotizacion> CotizacionEnErrorAsync()
    {
        _nexo.EnviarSolicitudAsync(Arg.Any<Guid>(), Arg.Any<clsSolicitudNexo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.CosteoNexoNoDisponible, "caído"), new clsSolicitudCreadaNexo("sol-1", 1, 0, 1, false));
        var cotizacion = (await Servicio().EnviarAsync(_ana, Entrada(), Ct)).Valor;
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.ErrorEnvio);
        Registrada(cotizacion);
        return cotizacion;
    }

    private void Registrada(clsCotizacion cotizacion)
    {
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _cotizaciones.ListarPosicionesAsync(cotizacion.Id, Arg.Any<CancellationToken>()).Returns(_posiciones);
    }

    private void ClienteEnCatalogo(clsClienteEncontrado candidato) =>
        _buscador.BuscarAsync(_ana, Nit, Arg.Any<CancellationToken>()).Returns(new clsResultadoBusquedaClientes(true, [candidato]));

    private static clsEntradaSolicitudCosteo Entrada() => new(Nit, "Longyin", "Importación", clsSolicitudCosteoMuestra.Crear());

    private clsEnvioCosteoService Servicio() => new(
        _cotizaciones, _politicas, _margen, _folios, _buscador, _nexo, _unidad, _auditoria, _reloj,
        Options.Create(new clsOpcionesCosteoNexo()), NullLogger<clsEnvioCosteoService>.Instance);
}
