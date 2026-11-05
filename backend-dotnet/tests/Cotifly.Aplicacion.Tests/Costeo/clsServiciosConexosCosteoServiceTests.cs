using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Servicios conexos desde el catálogo de Nexo (HU-013.3, ADR-018 §4, §8 y §11): el catálogo sin costos, la traducción de
/// <c>linea_orden</c> al <c>lineaId</c> de la cotización de Nexo, el POST sin <c>valorAplicado</c>, la relectura con el margen de la
/// cotización (función común <see cref="clsPrecioMonedaCosteo"/>), la regla del PDF, el control de versión y los errores.
/// </summary>
public sealed class clsServiciosConexosCosteoServiceTests
{
    private const int Version = 7;
    private const string CotizacionNexo = "COT-NX-1";
    private const string Almacenamiento = "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d";
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsServiciosConexosNexo _nexo = Substitute.For<IClsServiciosConexosNexo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsEnvioConexosNexoService _envioConexos = Substitute.For<IClsEnvioConexosNexoService>();
    private readonly IClsCategoriasConexos _categorias = Substitute.For<IClsCategoriasConexos>();
    private readonly IClsSituacionLineasNexo _situaciones = Substitute.For<IClsSituacionLineasNexo>();
    private readonly clsPrecioService _precios = new(Substitute.For<IClsProveedorTrm>(), Substitute.For<IClsCalendarioNegocio>());
    private readonly Guid _ana = Guid.NewGuid();
    private readonly clsCotizacionSolicitud _cotizacion;

    public clsServiciosConexosCosteoServiceTests()
    {
        // HU-021: la línea de Nexo es la del primer camión de la ruta (lineaSolicitudNexoId), guardada por el polling.
        _cotizacion = clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-900555111-AP-05102026-001");
        _cotizacion.RegistrarCotizacionNexo(CotizacionNexo);
        _cotizacion.Vehiculos[0].AsignarLineaNexo("LS-1");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(_cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(_cotizacion);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(true);
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>()).Returns(true);
        _nexo.LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>()).Returns(Ok(Nexo()));
        PostResponde(new clsAgregadoNexo(true, Nexo(_cx2)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// DEF-1027-3 (#226): la respuesta del POST trae el <c>estado_nexo</c> de cada conexo con el estado del envío diferido (el que no
    /// aparece en los estados, <c>pendiente</c>), igual que <c>GET .../costeo</c>. Las dependencias son obligatorias: sin ellas no hay
    /// servicio, en vez de una respuesta con <c>estado_nexo = null</c>.
    /// </summary>
    [Fact]
    public async Task AgregarRespondeConElEstadoNexoDeCadaConexoComoElGet()
    {
        _envioConexos.EstadosAsync(_cotizacion, Arg.Any<CancellationToken>()).Returns(
            new Dictionary<(Guid Ruta, string Conexo), string> { [(_cotizacion.PrimeraRuta!.Id, "CX-2")] = clsEnvioConexosNexoService.VisibleEnviado });

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 2m)], Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        resultado.Valor.ServiciosConexos.Single(s => s.Id == "CX-2").EstadoNexo.Should().Be(clsEnvioConexosNexoService.VisibleEnviado);
        resultado.Valor.ServiciosConexos.Single(s => s.Id == "CX-1").EstadoNexo.Should().Be(clsEnvioConexosNexoService.VisiblePendiente);
        resultado.Valor.Rutas[0].ServiciosConexos.Select(s => s.EstadoNexo).Should().OnlyContain(e => e != null);
        _situaciones.Received().Obtener(_cotizacion.Id);
        typeof(clsServiciosConexosCosteoService).GetConstructors().Single().GetParameters().Should()
            .OnlyContain(p => !p.HasDefaultValue, "ninguna dependencia es opcional: un registro faltante falla al resolver, no en silencio");
    }

    [Fact]
    public async Task AgregarLoCreaEnNexoConElLineaIdDeSuCotizacionYLoCotizaSinMargen()
    {
        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 2m)], Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        await _nexo.Received(1).AgregarAsync(_ana, CotizacionNexo, Arg.Is<IReadOnlyList<clsConexoParaNexo>>(i =>
            i.Count == 1 && i[0].LineaId == "LC-1" && i[0].ServicioConexoId == Almacenamiento && i[0].Cantidad == 2m), Arg.Any<CancellationToken>());
        var nuevo = resultado.Valor.ServiciosConexos.Single(s => s.Id == "CX-2");
        // HU-043.2 (#251): esta respuesta de Nexo no trae valorVenta (Nexo anterior a S6-HU-38.2): el conexo va al costo, sin margen.
        nuevo.Precio.Should().Be(240_000m);
        nuevo.Incluido.Should().BeTrue("un conexo agregado queda incluido");

        // HU-021: el conexo queda como fila de la ruta (costo unitario de Nexo = 240.000 / 2) y el precio de la ruta lo incluye; ya no hay
        // conexos excluidos (el Cargue de la ruta también va en el precio).
        var fila = _cotizacion.PrimeraRuta!.Conexos.Single(c => c.IdServicioConexo == "CX-2");
        (fila.Precio, fila.Cantidad, fila.Nombre).Should().Be((120_000m, 2m, "Almacenamiento"));
        resultado.Valor.ServiciosConexos.Single(s => s.Id == "CX-1").Incluido.Should().BeTrue();
        resultado.Valor.PrecioFinal.Should().Be(_precios.Calcular(2_550_000m, 0.15m).Valor.Precio + 85_000m + 240_000m).And.Be(3_325_000m);
        resultado.Valor.RowVersion.Should().Be("8", "la versión reservada antes del POST");
        Received.InOrder(() =>
        {
            _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>());
            _nexo.AgregarAsync(_ana, CotizacionNexo, Arg.Any<IReadOnlyList<clsConexoParaNexo>>(), CancellationToken.None);
            _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>());
        });
        await _nexo.Received(1).LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>());
        await _auditoria.Received(1).RegistrarAsync(clsServiciosConexosCosteoService.HerramientaAuditoria,
            Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ConVariosCamionesElConexoViajaPegadoALaLineaDelPrimerCamionDeLaRuta()
    {
        // HU-021.7 (#134, decisión 3): la ruta 1 viaja a Nexo como tres líneas (una por camión); el conexo va con el lineaId de la línea de
        // la cotización de Nexo del PRIMER camión (orden estable), después del costeo, con la versión reservada.
        var ruta = _cotizacion.PrimeraRuta!;
        ruta.AgregarVehiculo("3S3", null, 10_000m);
        ruta.AgregarVehiculo("2S2", null, 8_000m);
        var ordenados = ruta.VehiculosOrdenados;
        var lineasNexo = new List<clsLineaCosteoNexo>();
        for (var i = 0; i < ordenados.Count; i++)
        {
            if (ordenados[i].LineaSolicitudNexoId is null)
            {
                ordenados[i].AsignarLineaNexo($"LS-N{i}"); // el camión original ya tiene LS-1
            }

            ordenados[i].RegistrarCosteo(EstadoCosteo.Autocosteo, 1_000_000m, null);
            lineasNexo.Add(new clsLineaCosteoNexo(ordenados[i].LineaSolicitudNexoId, 1_000_000m, null, null, $"LC-{ordenados[i].LineaSolicitudNexoId}"));
        }

        _cotizacion.RecalcularTotales(clsCotizacionMuestra.Ahora).EsExitoso.Should().BeTrue();
        lineasNexo.Reverse(); // Nexo devuelve las líneas en cualquier orden
        var nexo = new clsCotizacionNexo(CotizacionNexo, "Borrador", "SinDefinir", lineasNexo, [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 85_000m)]);
        _nexo.LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>()).Returns(Ok(nexo));
        var primera = $"LC-{ordenados[0].LineaSolicitudNexoId}";
        PostResponde(new clsAgregadoNexo(true, nexo with { Conexos = [.. nexo.Conexos, _cx2 with { CotizacionLineaId = primera }] }));

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 2m)], Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        await _nexo.Received(1).AgregarAsync(_ana, CotizacionNexo, Arg.Is<IReadOnlyList<clsConexoParaNexo>>(i => i.Count == 1 && i[0].LineaId == primera),
            Arg.Any<CancellationToken>());
        ruta.Conexos.Select(c => c.IdServicioConexo).Should().Contain("CX-2", "el conexo vuelve a la ruta del camión que lo lleva");
        resultado.Valor.RowVersion.Should().Be("8", "la versión reservada antes del POST");
    }

    [Fact]
    public async Task LoQueGuardaNoLoCambiaLaSiguienteConsultaDelPollingParaNoSubirLaVersion()
    {
        // HU-021: el JSON de conexos ya no existe; lo equivalente es que la siguiente lectura del costeo con los mismos conexos en Nexo no
        // cambie nada (ni duplique el conexo ni mueva el precio).
        await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 2m)], Version, null, Ct);
        var precio = _cotizacion.PrecioFinal;

        var foto = new clsCosteoConsultadoNexo("EnCosteo", ["LS-1"], Nexo(_cx2));
        clsLecturaCosteo.AplicarFoto(_cotizacion, foto, DateTimeOffset.UtcNow, new clsOpcionesCosteoNexo(), new clsPrecioCosteoService())
            .Should().BeFalse("el polling ve los mismos conexos y no cambia nada");
        _cotizacion.PrimeraRuta!.Conexos.Should().HaveCount(2);
        _cotizacion.PrecioFinal.Should().Be(precio);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task SinItemsOConMasDeVeinteEsUnaSolicitudInvalidaSinLlamarANexo(int cantidadItems)
    {
        var items = Enumerable.Range(0, cantidadItems).Select(_ => Item(1, Almacenamiento, 1m)).ToList();

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, items, Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, Almacenamiento, "0")]
    [InlineData(1, Almacenamiento, "-1")]
    [InlineData(1, Almacenamiento, "1000000")]
    [InlineData(1, Almacenamiento, "1.005")]
    [InlineData(0, Almacenamiento, "1")]
    [InlineData(1, "no-es-uuid", "1")]
    public async Task UnItemFueraDelEsquemaEsUnaSolicitudInvalidaSinLlamarANexo(int linea, string servicio, string cantidad)
    {
        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio,
            [Item(linea, servicio, decimal.Parse(cantidad, System.Globalization.CultureInfo.InvariantCulture))], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UnaLineaQueNoExisteEsUnConexoInvalidoSinLlamarANexo()
    {
        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(2, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConexoInvalido);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task UnaLineaSinIdDeNexoGuardadoRespondeLineaSinCotizacionNexoSinLlamarANexo()
    {
        // HU-021: el camión todavía no tiene su línea de Nexo (el polling no la ha asociado).
        var sinLinea = clsAjusteMargenCosteoServiceTests.Costeada(_ana, "PCN-SIN-LINEA");
        sinLinea.RegistrarCotizacionNexo(CotizacionNexo);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(sinLinea.Folio, _ana, Arg.Any<CancellationToken>()).Returns(sinLinea);

        var resultado = await Servicio().AgregarAsync(_ana, sinLinea.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.LineaSinCotizacionNexo);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SiLaCotizacionDeNexoNoTieneLineaParaEsaLineaNoSeAgregaNada()
    {
        _nexo.LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>())
            .Returns(Ok(new clsCotizacionNexo(CotizacionNexo, "Borrador", "SinDefinir", [new clsLineaCosteoNexo("OTRA", 1m, null, null, "LC-9")], [])));

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.LineaSinCotizacionNexo);
        await _nexo.DidNotReceiveWithAnyArgs().AgregarAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task UnaLicitacionNoAdmiteConexosConElMotivoLicitacion()
    {
        var licitacion = clsCotizacionMuestra.ConVersionNegocio(
            clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 1_550_000m], folio: "PCN-LIC", usuarioId: _ana), Version);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(licitacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(licitacion);

        var resultado = await Servicio().AgregarAsync(_ana, licitacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        resultado.Error.Detalle.Should().Be("LICITACION");
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SinCosteoRespondeCosteoNoListo()
    {
        var sinCosteo = clsCotizacionMuestra.Formulario("PCN-SIN", _ana);
        sinCosteo.RegistrarEnvioCosteo("SOL-1", false, clsCotizacionMuestra.Ahora, clsCotizacionMuestra.Ahora);
        sinCosteo.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-SIN", _ana, Arg.Any<CancellationToken>()).Returns(sinCosteo);

        var resultado = await Servicio().AgregarAsync(_ana, "PCN-SIN", [Item(1, Almacenamiento, 1m)], 0, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNoListo);
    }

    [Fact]
    public async Task YaEnviadaRespondePdfYaEnviadoSinLlamarANexo()
    {
        _cotizacion.AsignarPdf("PCN-1.pdf");
        _cotizacion.RegistrarEnvio();

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        resultado.Error.Datos!["accion"].Should().Be("nueva_version");
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task HU0363_FinalizadaRespondePdfYaEnviadoSinLlamarANexo()
    {
        _cotizacion.Finalizar(_ana, clsCotizacionMuestra.Ahora);

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.PdfYaEnviado);
        resultado.Error.Datos!["accion"].Should().Be("nueva_version");
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ConElPdfGeneradoSinEnviarLoInvalidaYLoDejaEnElHistorial()
    {
        _cotizacion.AsignarPdf("/data/pdfs/PCN-1.pdf");

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 2m)], Version, null, Ct);

        resultado.Valor.PdfUrl.Should().BeNull();
        resultado.Valor.PdfDesactualizado.Should().BeTrue();
        _cotizaciones.Received(1).AgregarHistorialPdf(Arg.Is<clsCotizacionPdfHistorial>(h =>
            h.PdfPath == "/data/pdfs/PCN-1.pdf" && h.Motivo == MotivoInvalidacionPdf.ServiciosConexos && h.UsuarioId == _ana));
    }

    [Fact]
    public async Task UnIfMatchViejoNoLlegaANexo()
    {
        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], 6, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SiNexoRechazaElConexoNadaCambiaEnCotifly()
    {
        _nexo.AgregarAsync(_ana, CotizacionNexo, Arg.Any<IReadOnlyList<clsConexoParaNexo>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsAgregadoNexo>.Fallo(new clsErrorDominio(clsCodigosError.ConexoInvalido, "No.", "SERVICIO_CONEXO_NO_ENCONTRADO")));
        var precio = _cotizacion.PrecioFinal;

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConexoInvalido);
        _cotizacion.PrimeraRuta!.Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-1");
        _cotizacion.PrecioFinal.Should().Be(precio);
        _cotizacion.VersionNegocio.Should().Be(Version + 1, "solo queda la versión reservada");
        await _unidad.DidNotReceive().GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiLaRelecturaFallaRespondeNoDisponibleConAgregadoEnNexoYNoRepiteElPost()
    {
        PostResponde(new clsAgregadoNexo(true, null, "respuesta vacía"));
        _nexo.LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>())
            .Returns(Ok(Nexo()), clsResultado<clsCotizacionNexo>.Fallo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "Caído.", "HTTP 503")));

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        resultado.Error.Datos.Should().ContainKey("agregado_en_nexo").WhoseValue.Should().Be(true, "un 2xx de Nexo siempre es efecto");
        await _nexo.Received(1).AgregarAsync(_ana, CotizacionNexo, Arg.Any<IReadOnlyList<clsConexoParaNexo>>(), Arg.Any<CancellationToken>());

        // HU-021 (decisión 3): el polling ya no importa los conexos de Nexo por su cuenta; para que la siguiente lectura traiga el conexo,
        // se guarda la marca ConexosPendientesDeLeer con la versión reservada (antes no se guardaba nada sin PDF).
        _cotizacion.ConexosPendientesDeLeer.Should().BeTrue();
        _cotizacion.PrimeraRuta!.Conexos.Select(c => c.IdServicioConexo).Should().Equal("CX-1");

        // HU-021.8 (#135): con la marca se guardan la ruta pedida y los conexos que Nexo YA tenía antes del POST (CX-1).
        _cotizaciones.Received(1).RegistrarConexosPendientesNexo(Arg.Is<clsConexosPendientesNexo>(p =>
            p.Folio == _cotizacion.Folio && p.LineaSolicitudId == _cotizacion.PrimeraRuta!.Id && p.Previos.SequenceEqual(new[] { "CX-1" })));
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiLaRelecturaFallaConPdfLoInvalidaParaQueElPollingRecojaElConexo()
    {
        _cotizacion.AsignarPdf("/data/pdfs/PCN-1.pdf");
        PostResponde(new clsAgregadoNexo(true, null, "respuesta vacía"));
        _nexo.LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>())
            .Returns(Ok(Nexo()), clsResultado<clsCotizacionNexo>.Fallo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "Caído.")));

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Datos!["agregado_en_nexo"].Should().Be(true);
        _cotizacion.PdfPath.Should().BeNull();
        await _unidad.Received(1).GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SiLaVersionCambiaAlGuardarRespondeConflictoConAgregadoEnNexo()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version + 1, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        resultado.Error.Datos!["agregado_en_nexo"].Should().Be(true);
    }

    [Fact]
    public async Task SiOtraPeticionReservoLaVersionNoSeLlamaAlPostDeNexo()
    {
        _unidad.GuardarCambiosSiVersionAsync(_cotizacion.Id, Version, Arg.Any<CancellationToken>()).Returns(false);

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConflictoConcurrencia);
        resultado.Error.Datos.Should().BeNull("Nexo no se tocó");
        await _nexo.DidNotReceiveWithAnyArgs().AgregarAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task SinRespuestaDeNexoYConLaRelecturaQueMuestraElConexoSeTerminaNormal()
    {
        PostResponde(new clsAgregadoNexo(false, null, "Nexo no respondió a tiempo"));
        _nexo.LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>()).Returns(Ok(Nexo()), Ok(Nexo(_cx2)));

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 2m)], Version, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : null);
        resultado.Valor.ServiciosConexos.Should().Contain(s => s.Id == "CX-2" && s.Incluido);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SinRespuestaDeNexoYSinConfirmacionElAgregadoEsDesconocido(bool relecturaFalla)
    {
        PostResponde(new clsAgregadoNexo(false, null, "error de red"));
        _nexo.LeerCotizacionAsync(_ana, CotizacionNexo, Arg.Any<CancellationToken>()).Returns(Ok(Nexo()), relecturaFalla
            ? clsResultado<clsCotizacionNexo>.Fallo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "Caído."))
            : Ok(Nexo()));

        var resultado = await Servicio().AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        resultado.Error.Datos!["agregado_en_nexo"].Should().Be("desconocido");
        await _nexo.Received(1).AgregarAsync(_ana, CotizacionNexo, Arg.Any<IReadOnlyList<clsConexoParaNexo>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LaIdempotenciaGuardaLosExitosYTodoLoQueYaPudoLlegarANexo()
    {
        var conMarca = new clsErrorDominio(clsCodigosError.NexoNoDisponible, "x") { Datos = new Dictionary<string, object?> { ["agregado_en_nexo"] = "desconocido" } };

        clsServiciosConexosCosteoService.SeGuarda(clsResultado<clsCosteoCotizacion>.Fallo(conMarca)).Should().BeTrue();
        clsServiciosConexosCosteoService.SeGuarda(clsResultado<clsCosteoCotizacion>.Fallo(
            new clsErrorDominio(clsCodigosError.NexoNoDisponible, "x"))).Should().BeFalse("sin efecto en Nexo se vuelve a evaluar");
        clsServiciosConexosCosteoService.Huella("F", [Item(1, Almacenamiento, 1m)])
            .Should().NotBe(clsServiciosConexosCosteoService.Huella("F", [Item(1, Almacenamiento, 2m)]));
    }

    [Fact]
    public async Task ConClaveSeUsaElAlmacenUnicoConElAmbitoDeConexos()
    {
        var almacen = Substitute.For<IClsAlmacenIdempotencia>();
        var servicio = new clsServiciosConexosCosteoService(
            _cotizaciones, _nexo, _unidad, _auditoria, Options.Create(new clsOpcionesPdf()), almacen, _envioConexos,
            NullLogger<clsServiciosConexosCosteoService>.Instance, _categorias, _situaciones,
            Substitute.For<Cotifly.Aplicacion.Margen.Services.IClsAvisoSincronizacionMargenSap>());

        await servicio.AgregarAsync(_ana, _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, "clave-12345", Ct);

        await almacen.Received(1).EjecutarUnaVezAsync(
            $"conexos:{_ana}", "clave-12345", clsServiciosConexosCosteoService.Huella(_cotizacion.Folio, [Item(1, Almacenamiento, 1m)]),
            Arg.Any<Func<CancellationToken, Task<clsResultado<clsCosteoCotizacion>>>>(),
            Arg.Any<Func<clsResultado<clsCosteoCotizacion>, bool>?>(), Ct);
    }

    [Fact]
    public async Task UnaAjenaEsNoEncontradaYNoSeAudita()
    {
        var resultado = await Servicio().AgregarAsync(Guid.NewGuid(), _cotizacion.Folio, [Item(1, Almacenamiento, 1m)], Version, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        _nexo.ReceivedCalls().Should().BeEmpty();
        await _auditoria.DidNotReceiveWithAnyArgs().RegistrarAsync(default!, default, default, default, default, default);
    }

    [Fact]
    public void EnDolaresElConexoSeConvierteConLaTrmFijadaDeLaCotizacion()
    {
        // HU-021: el conexo de Nexo queda en la ruta y su precio sale en la moneda de la solicitud. HU-043.2 (#251): a su venta (aquí, sin
        // valorVenta, el costo), sin margen.
        var usd = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: "PCN-U", moneda: "USD", trm: 4000m);
        var ruta = usd.PrimeraRuta!;
        var precioAntes = usd.PrecioFinal;
        clsServiciosConexosCosteoService.ImportarConexos(usd, ruta, [_cx2], clsCotizacionMuestra.Ahora).Valor.Should().Be(1);
        usd.PrecioFinal.Should().BeGreaterThan(precioAntes!.Value, "la raíz recalcula siempre al cambiar los conexos (ADR-019)");

        var conexos = clsCosteoCotizacionService.ConexosConPrecio(usd, ruta);

        conexos.Single(c => c.Id == "CX-2").Precio.Should().Be(60m, "240.000 / 4.000, con la TRM de la cotización y no la del día");
    }

    [Fact]
    public void Hu0432_ElConexoDeNexoGuardaElCostoEnPrecioYLaVentaEnPrecioVentaYSeCotizaASuVentaSinMargen()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: "PCN-V");
        var ruta = cotizacion.PrimeraRuta!;
        var conVenta = _cx2 with { PrecioVentaUnitario = 150_000m, ValorVenta = 300_000m };

        clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta, [conVenta], clsCotizacionMuestra.Ahora).Valor.Should().Be(1);

        var fila = ruta.Conexos.Single();
        (fila.Precio, fila.PrecioVenta, fila.Cantidad).Should().Be((120_000m, 150_000m, 2m), "costo = valorAplicado ÷ cantidad; venta = precioVentaUnitario");
        ruta.CostoFinal.Should().Be(2_790_000m, "costo_final sigue siendo el costo: 2.550.000 + 240.000");
        ruta.PrecioFinalLinea.Should().Be(3_300_000m, "2.550.000 / 0,85 = 3.000.000 + 300.000 de venta, sin margen");
        clsCosteoCotizacionService.ConexosConPrecio(cotizacion, ruta).Single().Precio.Should().Be(300_000m, "el comercial ve la venta, nunca el costo");
        clsCosteoCotizacionService.PrecioTransporte(cotizacion, ruta).Should().Be(3_000_000m, "transporte + conexos = precio de la ruta");
    }

    [Theory]
    [InlineData(null, 300_000.0, 150_000.0)]   // sin precioVentaUnitario: valorVenta ÷ cantidad
    [InlineData(null, null, 120_000.0)]        // Nexo anterior a S6-HU-38.2: al costo, y el conexo no desaparece
    [InlineData(-1.0, -1.0, 120_000.0)]        // una venta negativa no se usa
    public void Hu0432_SinPrecioVentaUnitarioSeUsaElValorVentaYSinNingunoElCosto(double? unitaria, double? valorVenta, double esperada)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: "PCN-V");
        var ruta = cotizacion.PrimeraRuta!;
        var conexo = _cx2 with { PrecioVentaUnitario = (decimal?)unitaria, ValorVenta = (decimal?)valorVenta };

        clsServiciosConexosCosteoService.ImportarConexos(cotizacion, ruta, [conexo], clsCotizacionMuestra.Ahora).Valor.Should().Be(1);

        ruta.Conexos.Should().ContainSingle().Which.PrecioVenta.Should().Be((decimal)esperada);
        ruta.PrecioFinalLinea.Should().Be(3_000_000m + (decimal)esperada * 2m);
    }

    [Fact]
    public async Task ElCatalogoNoExponeCostosYDescartaInactivos()
    {
        _nexo.BuscarAsync(_ana, new clsFiltroCatalogoConexos("alm", "logistico", 1, 20), Arg.Any<CancellationToken>()).Returns(
            clsResultado<clsPaginaCatalogoConexosNexo>.Exito(new clsPaginaCatalogoConexosNexo(2,
            [
                new clsServicioConexoNexo(Almacenamiento, "ALM-01", "Almacenamiento", "logistico", "Día", null, null, null, null, null, null, true),
                new clsServicioConexoNexo("8b3e4c1a-5d9f-4e77-9c2a-3f4b5c6d7e8f", "ESC-01", "Escolta", "logistico", "Viaje", null, null, null, null, null, null, false),
            ])));

        var resultado = await Servicio().BuscarAsync(_ana, " alm ", "logistico", null, null, Ct);

        resultado.Valor.Page.Should().Be(1);
        resultado.Valor.PageSize.Should().Be(20);
        resultado.Valor.Total.Should().Be(2);
        resultado.Valor.Items.Should().ContainSingle().Which.Nombre.Should().Be("Almacenamiento");
        typeof(clsServicioConexoCatalogo).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Costo", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 51)]
    public async Task UnaPaginaFueraDeRangoEsUnaSolicitudInvalidaSinLlamarANexo(int pagina, int tamano)
    {
        var resultado = await Servicio().BuscarAsync(_ana, null, null, pagina, tamano, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _nexo.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ElCatalogoPropagaLosErroresDeNexo()
    {
        _nexo.BuscarAsync(_ana, Arg.Any<clsFiltroCatalogoConexos>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaCatalogoConexosNexo>.Fallo(new clsErrorDominio(clsCodigosError.NexoSinPermiso, "Sin rol.")));

        var resultado = await Servicio().BuscarAsync(_ana, "a", null, null, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoSinPermiso);
    }

    private clsServiciosConexosCosteoService Servicio() =>
        new(_cotizaciones, _nexo, _unidad, _auditoria, Options.Create(new clsOpcionesPdf()), Substitute.For<IClsAlmacenIdempotencia>(),
            _envioConexos, NullLogger<clsServiciosConexosCosteoService>.Instance, _categorias, _situaciones,
            Substitute.For<Cotifly.Aplicacion.Margen.Services.IClsAvisoSincronizacionMargenSap>());

    private static readonly clsConexoNexo _cx2 = new("CX-2", "Almacenamiento", "logistico", 2m, 240_000m);

    private static clsResultado<clsCotizacionNexo> Ok(clsCotizacionNexo cotizacion) => clsResultado<clsCotizacionNexo>.Exito(cotizacion);

    private void PostResponde(clsAgregadoNexo agregado) =>
        _nexo.AgregarAsync(_ana, CotizacionNexo, Arg.Any<IReadOnlyList<clsConexoParaNexo>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsAgregadoNexo>.Exito(agregado));

    private static clsConexoAgregarEntrada Item(int linea, string servicio, decimal cantidad) => new(linea, servicio, cantidad);

    /// <summary>Cotización de Nexo con la línea LC-1 (de la línea de solicitud LS-1), el conexo CX-1 (85.000) y los conexos extra.</summary>
    private static clsCotizacionNexo Nexo(params clsConexoNexo[] extra) => new(
        CotizacionNexo, "Borrador", "SinDefinir",
        [new clsLineaCosteoNexo("LS-1", 2_550_000m, null, null, "LC-1")],
        [new clsConexoNexo("CX-1", "Cargue", "logistico", 1m, 85_000m), .. extra]);
}
