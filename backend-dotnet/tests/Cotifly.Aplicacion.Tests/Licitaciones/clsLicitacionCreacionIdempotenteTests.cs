using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// Crear una licitación con <c>Idempotency-Key</c> cuando la API se cae después de confirmar el folio y antes de cerrar la clave: el folio
/// queda registrado con la clave en la transacción que lo confirma, así el reintento tardío (pasada la toma en curso, cuando el almacén
/// vuelve a ejecutar la acción) relee esa licitación y responde con el mismo folio, sin crear otra ni enviarla otra vez a Nexo.
/// </summary>
public sealed class clsLicitacionCreacionIdempotenteTests
{
    private const string Nit = "900123456";
    private const string Clave = "clave-licitacion-01";

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenResolverService _margen = Substitute.For<IClsMargenResolverService>();
    private readonly IClsGeneradorFolioService _folios = Substitute.For<IClsGeneradorFolioService>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsEnvioCosteoService _envio = Substitute.For<IClsEnvioCosteoService>();
    private readonly IClsValidadorCatalogosCaptura _catalogos = Substitute.For<IClsValidadorCatalogosCaptura>();
    private readonly IClsMunicipiosProvider _municipios = Substitute.For<IClsMunicipiosProvider>();
    private readonly IClsFijacionMonedaService _monedas = Substitute.For<IClsFijacionMonedaService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsMargenPolitica _politica = clsMargenPolitica.Crear("Importacion", 0.15m, mapeoLineasNexo: "PuertoAInterior").Valor;
    private readonly List<clsCotizacionSolicitud> _creadas = [];
    private readonly clsRegistroCapturasIdempotentesEnMemoria _registro;
    private int _consecutivo;

    public clsLicitacionCreacionIdempotenteTests()
    {
        _registro = new clsRegistroCapturasIdempotentesEnMemoria(_reloj);
        _catalogos.ValidarLineasAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<clsLineaSolicitudCosteo>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new clsValidacionCatalogoLineas([.. ci.ArgAt<IReadOnlyList<clsLineaSolicitudCosteo>>(1).Select(_ => (IReadOnlyList<clsErrorCampo>)[])], null));
        _municipios.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsMunicipio>>.Exito(
            [
                new clsMunicipio(76109, "76109", "Buenaventura", "Valle del Cauca", "Colombia"),
                new clsMunicipio(11001, "11001", "Bogotá, D.C.", "Bogotá, D.C.", "Colombia"),
            ]));
        _margen.ResolverLineaAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_politica);
        _folios.CrearConFolioNuevoAsync(_ana, Nit, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var folio = clsFolio.Crear(new DateOnly(2026, 9, 28), "AP", ++_consecutivo, Nit).Valor;
                return ci.Arg<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>()(folio, CancellationToken.None);
            });
        _cotizaciones.When(r => r.Agregar(Arg.Any<clsCotizacionSolicitud>())).Do(ci => _creadas.Add(ci.Arg<clsCotizacionSolicitud>()));
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(Arg.Any<string>(), _ana, Arg.Any<CancellationToken>())
            .Returns(ci => _creadas.FirstOrDefault(c => c.Folio == ci.ArgAt<string>(0)));
        _unidad.GuardarCambiosAsync(Arg.Any<CancellationToken>()).Returns(1);
        _monedas.FijarAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new clsMonedaFijada(clsCotizacionSolicitud.MonedaPesos, 0m));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SiLaApiSeCaeTrasConfirmarElFolioElReintentoTardioDevuelveElMismoFolioSinCrearOtra()
    {
        CaidaAlEnviarANexo();
        var servicio = Servicio(_registro);
        var entrada = Entrada();

        var primera = () => servicio.CrearUnaVezAsync(_ana, entrada, Clave, Ct);
        await primera.Should().ThrowAsync<TimeoutException>("la petición se corta después de confirmar el folio");
        var folio = _creadas.Should().ContainSingle().Subject.Folio;
        (await _registro.BuscarAsync($"{clsLicitacionService.OperacionIdempotencia}:{_ana}", Clave, Ct))!.Folio
            .Should().Be(folio, "el folio quedó registrado con la clave al confirmarlo");

        var reintento = await servicio.CrearUnaVezAsync(_ana, entrada, Clave, Ct);

        reintento.Cotizacion!.Folio.Should().Be(folio, "el reintento relee la licitación que ya existe");
        _creadas.Should().ContainSingle("no se crea una segunda licitación");
        await _folios.Received(1).CrearConFolioNuevoAsync(
            _ana, Nit, Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsCotizacionSolicitud>>>>(), Arg.Any<CancellationToken>());
        await _envio.Received(1).EnviarGuardadaAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<clsMargenPolitica?>(), _ana, Arg.Any<string?>());
    }

    [Fact]
    public async Task ElReintentoTardioConOtroCuerpoEsIdempotencyKeyReutilizada()
    {
        CaidaAlEnviarANexo();
        var servicio = Servicio(_registro);
        var primera = () => servicio.CrearUnaVezAsync(_ana, Entrada(), Clave, Ct);
        await primera.Should().ThrowAsync<TimeoutException>();

        var otroCuerpo = await servicio.CrearUnaVezAsync(_ana, Entrada() with { ClienteNombre = "Otro cliente" }, Clave, Ct);

        otroCuerpo.Error!.Codigo.Should().Be(clsCodigosAplicacion.IdempotencyKeyReutilizada);
        _creadas.Should().ContainSingle();
    }

    [Fact]
    public async Task SiElGuardadoDelFolioFallaLaClaveNoQuedaRegistradaYElReintentoCreaLaLicitacion()
    {
        var guardados = 0;
        _unidad.GuardarCambiosAsync(Arg.Any<CancellationToken>()).Returns<Task<int>>(_ =>
            ++guardados == 1 ? throw new TimeoutException("Execution Timeout Expired") : Task.FromResult(1));
        var servicio = Servicio(_registro);
        var entrada = Entrada();

        var primera = () => servicio.CrearUnaVezAsync(_ana, entrada, Clave, Ct);
        await primera.Should().ThrowAsync<TimeoutException>();
        (await _registro.BuscarAsync($"{clsLicitacionService.OperacionIdempotencia}:{_ana}", Clave, Ct))
            .Should().BeNull("la clave solo se registra después de guardar la licitación, en su misma transacción");

        var reintento = await servicio.CrearUnaVezAsync(_ana, entrada, Clave, Ct);

        reintento.Cotizacion.Should().NotBeNull();
        await _envio.Received(1).EnviarGuardadaAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<clsMargenPolitica?>(), _ana, Arg.Any<string?>());
    }

    [Fact]
    public async Task ElReportePorFilaNoRegistraLaClave()
    {
        var servicio = Servicio(_registro);
        var invalida = Entrada() with { Lineas = [new clsLineaLicitacionEntrada(0, clsSolicitudCosteoMuestra.RutaCaptura() with { Destino = string.Empty, DestinoMunicipioId = null })] };

        var resultado = await servicio.CrearUnaVezAsync(_ana, invalida, Clave, Ct);

        resultado.Errores.Should().NotBeEmpty();
        (await _registro.BuscarAsync($"{clsLicitacionService.OperacionIdempotencia}:{_ana}", Clave, Ct)).Should().BeNull();
        _creadas.Should().BeEmpty();
    }

    [Fact]
    public async Task SinRegistroElServicioSigueCreandoUnaVezPorClave()
    {
        var servicio = Servicio(registro: null);

        var resultado = await servicio.CrearUnaVezAsync(_ana, Entrada(), Clave, Ct);

        resultado.Cotizacion.Should().NotBeNull();
        _creadas.Should().ContainSingle();
    }

    /// <summary>La API se cae justo después de confirmar el folio (el envío a Nexo nunca responde).</summary>
    private void CaidaAlEnviarANexo() =>
        _envio.EnviarGuardadaAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<clsMargenPolitica?>(), Arg.Any<Guid>(), Arg.Any<string?>())
            .Returns<Task>(_ => throw new TimeoutException("La API se cayó"));

    private clsEntradaLicitacion Entrada() =>
        new(Nit, "Longyin", "Importación", clsSolicitudCosteoMuestra.CrearCaptura().Encabezado with { Tipo = TipoSolicitud.Licitacion }, null,
            [new clsLineaLicitacionEntrada(0, clsSolicitudCosteoMuestra.RutaCaptura())], null);

    private clsLicitacionService Servicio(IClsRegistroCapturasIdempotentes? registro)
    {
        var filas = new clsValidadorFilasLicitacion(_catalogos, _municipios);
        var captura = new clsCapturaRutasService(
            filas, Substitute.For<IClsCostosConexosNexo>(), new clsPlanificadorCamionSinVehiculo(), NullLogger<clsCapturaRutasService>.Instance);
        return new clsLicitacionService(
            _cotizaciones, _margen, _folios, _unidad, _envio, Substitute.For<IClsAnalizadorExcelNexo>(), Substitute.For<IClsLectorExcelLicitacion>(),
            Substitute.For<IClsExportadorExcelLicitacion>(), _reloj, _monedas, captura, NullLogger<clsLicitacionService>.Instance,
            Substitute.For<IClsAccesoCotizacionAjena>(), idempotencia: new clsAlmacenQueRetoma(), registroIdempotente: registro);
    }

    /// <summary>
    /// Almacén tras la caída de la primera petición y pasada su toma en curso: nada quedó guardado como resultado, así que cada petición con
    /// la clave vuelve a ejecutar la acción (como la retoma de una fila huérfana en la tabla).
    /// </summary>
    private sealed class clsAlmacenQueRetoma : IClsAlmacenIdempotencia
    {
        public Task<clsResultado<T>> EjecutarUnaVezAsync<T>(
            string ambito, string clave, string huella, Func<CancellationToken, Task<clsResultado<T>>> accion,
            Func<clsResultado<T>, bool>? seGuarda, CancellationToken ct) => accion(ct);
    }
}
