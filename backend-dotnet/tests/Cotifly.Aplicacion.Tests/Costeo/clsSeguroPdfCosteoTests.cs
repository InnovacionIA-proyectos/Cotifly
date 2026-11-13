using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// El PDF del costeo muestra el seguro de la mercancía en una línea propia por ruta, aparte del flete (ADR-032 §9, política NOGE0076):
/// la fila del flete imprime el precio de la ruta SIN el seguro y la fila "Seguro de la mercancía" su valor; el total general sigue siendo
/// la suma de los precios finales. Nunca la tasa. Una ruta con el seguro marcado sin valor de la mercancía sale sin su fila y con un aviso.
/// </summary>
public sealed class clsSeguroPdfCosteoTests
{
    private const int Version = 7;
    private const decimal Tasa = 0.003450m;

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsGeneradorPdf _generador = Substitute.For<IClsGeneradorPdf>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly Guid _ana = Guid.NewGuid();
    private clsDocumentoCotizacion? _documento;

    public clsSeguroPdfCosteoTests()
    {
        _usuarios.ObtenerPorIdAsync(_ana, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("ana", "Ana Pérez", "ana@boterosoto.com.co", "Occidente", DateTimeOffset.UnixEpoch).Valor);
        _generador.GenerarAsync(Arg.Do<clsDocumentoCotizacion>(d => _documento = d), Arg.Any<CancellationToken>()).Returns(new clsPdfGenerado([1, 2, 3], 1));
        _almacen.GuardarNuevoAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(ci => "/data/pdfs/" + ci.Arg<string>());
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Version, Arg.Any<CancellationToken>()).Returns(true);
        _politicas.ObtenerPorIdAsync(3, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Importacion", 0.15m).Valor);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElPdfDeUnaRutaLlevaElFleteYElSeguroEnFilasSeparadasYElTotalConElSeguro()
    {
        var cotizacion = Registrada(Preparada(UnaRuta()));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        var filas = _documento!.Secciones.Single().Filas;
        filas.Should().HaveCount(2);
        filas[0].Tarifa.Should().Be("$1.250.000", "el flete sin el seguro: 1.000.000 / 0,8");
        filas[1].Destino.Replace("​", string.Empty, StringComparison.Ordinal).Should().Be(clsCosteoCotizacionService.EtiquetaSeguroPdf);
        filas[1].Tarifa.Should().Be("$690.000", "200.000.000 × 0,345 %, sin margen");
        filas[1].Nota.Should().Be(clsCosteoCotizacionService.NotaSeguroPdf);
        _documento.TotalGeneral!.Valor.Should().Be("$1.940.000 COP", "el total es la suma de los precios finales, con el seguro");
    }

    [Fact]
    public async Task SinSeguroElPdfQuedaComoAntesConUnaSolaFila()
    {
        var cotizacion = Registrada(Preparada(UnaRuta(conSeguro: false)));

        (await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue();

        _documento!.Secciones.Single().Filas.Should().ContainSingle().Which.Tarifa.Should().Be("$1.250.000");
        _documento.TotalGeneral!.Valor.Should().Be("$1.250.000 COP");
    }

    [Fact]
    public async Task EnDolaresElSeguroSaleConvertidoConLaTrmDeLaCotizacion()
    {
        var cotizacion = Registrada(Preparada(UnaRuta(moneda: "USD", trm: 4_000m)));

        (await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue();

        _documento!.Secciones.Single().Filas.Select(f => f.Tarifa).Should().Equal("US$312,50", "US$172,50");
        _documento.TotalGeneral!.Valor.Should().Be("US$485,00 USD");
    }

    [Fact]
    public async Task ElPliegoLlevaElSeguroEnLaSeccionYEnElResumenDeCadaRutaQueLoIncluye()
    {
        var cotizacion = Registrada(Preparada(DosRutas()));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.Variante.Should().Be(VarianteDocumento.Pliego);
        _documento.Secciones[0].Filas.Select(f => f.Tarifa).Should().Equal("$1.250.000");
        _documento.Secciones[1].Filas.Select(f => f.Tarifa).Should().Equal("$2.500.000", "$345.000");
        var resumen = _documento.Pliego!;
        resumen.Filas.Select(f => (f.Numero, f.Ruta, f.Tarifa)).Should().Equal(
            ("1", "Buenaventura → Destino 1", "$1.250.000"),
            ("2", "Buenaventura → Destino 2", "$2.500.000"),
            ("2", clsCosteoCotizacionService.EtiquetaSeguroPdf, "$345.000"));
        resumen.Total.Should().Be("$4.095.000", "1.250.000 + 2.500.000 + 345.000");
        resumen.Nota.Should().Contain("seguro de la mercancía");
        _documento.TotalGeneral!.Valor.Should().Be("$4.095.000 COP");
    }

    /// <summary>
    /// El seguro no se redondea a pesos (se guarda en centavos). En el PDF, el seguro y el
    /// total con centavos los muestran (2 decimales), así cuadran exacto con lo guardado; el flete, sin centavos, sigue en pesos enteros.
    /// </summary>
    [Fact]
    public async Task ElSeguroConCentavosSeMuestraConSusCentavosYCuadraConElTotal()
    {
        var cotizacion = Registrada(Preparada(UnaRuta(valor: 123_456_789.99m)));

        (await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct)).EsExitoso.Should().BeTrue();

        cotizacion.PrimeraRuta!.CostoSeguro.Should().Be(425_925.93m, "123.456.789,99 × 0,345 % = 425.925,925…, en centavos");
        _documento!.Secciones.Single().Filas.Select(f => f.Tarifa).Should().Equal("$1.250.000", "$425.925,93");
        _documento.TotalGeneral!.Valor.Should().Be("$1.675.925,93 COP");
    }

    /// <summary>
    /// Si las secciones no siguen a las rutas, la fila del seguro no se omite en silencio (la ruta ya imprime su precio
    /// sin el seguro y el total no cuadraría a la vista): es un error de programación.
    /// </summary>
    [Fact]
    public void SiLasSeccionesNoSonUnaPorRutaLaFilaDelSeguroNoSeOmiteEnSilencio()
    {
        var cotizacion = UnaRuta();
        var documento = clsConstructorDocumentoCotizacion.Construir(
            clsCosteoCotizacionService.Payload(cotizacion, "Importacion", "COP"), cotizacion.Folio,
            new clsFirmaDocumento("Ana", null, null, null), DateOnly.FromDateTime(clsCotizacionMuestra.Ahora.UtcDateTime), "COP", 0m).Valor;

        FluentActions.Invoking(() => clsCosteoCotizacionService.ConFilasSeguro(documento with { Secciones = [] }, cotizacion, "COP"))
            .Should().Throw<InvalidOperationException>();
        clsCosteoCotizacionService.ConFilasSeguro(documento with { Secciones = [] }, UnaRuta(conSeguro: false), "COP").Secciones
            .Should().BeEmpty("sin seguro no hay fila que agregar");
    }

    /// <summary>
    /// En COP, el seguro y los totales con centavos se muestran con sus 2 decimales, así lo impreso
    /// cuadra exacto con lo guardado: dos rutas con seguro de $100,40 (valor 100.400 × 0,1 %).
    /// </summary>
    [Fact]
    public async Task ElPliegoConSegurosConCentavosLosMuestraYLasFilasCuadranConElTotal()
    {
        var cotizacion = Registrada(Preparada(DosRutas([100_400m, 100_400m], 0.001m)));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        cotizacion.RutasOrdenadas.Select(r => r.CostoSeguro).Should().Equal(100.40m, 100.40m);
        _documento!.Secciones.Select(s => s.Filas.Select(f => f.Tarifa).ToList()).Should().BeEquivalentTo(
            new[] { new[] { "$1.250.000", "$100,40" }, new[] { "$2.500.000", "$100,40" } }, o => o.WithStrictOrdering());
        _documento.Pliego!.Filas.Select(f => f.Tarifa).Should().Equal("$1.250.000", "$100,40", "$2.500.000", "$100,40");
        _documento.Pliego.Total.Should().Be("$3.750.200,80", "1.250.000 + 100,40 + 2.500.000 + 100,40: cuadra exacto");
        _documento.TotalGeneral!.Valor.Should().Be("$3.750.200,80 COP");
        cotizacion.PrecioFinal.Should().Be(3_750_200.80m, "lo impreso es lo guardado");
    }

    [Theory]
    [InlineData("100.40", "COP", "$100,40")]
    [InlineData("100.4", "COP", "$100,40")]
    [InlineData("1250000", "COP", "$1.250.000")]
    [InlineData("1250000.00", "COP", "$1.250.000")]
    [InlineData("3750200.8", "COP", "$3.750.200,80")]
    [InlineData("312.5", "USD", "US$312,50")]
    public void ElFormatoExactoMuestraLosCentavosEnPesosSoloSiLosHay(string valor, string moneda, string esperado) =>
        clsFormatoMoneda.FormatearExacto(decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture), moneda).Should().Be(esperado);

    [Fact]
    public void ElPdfNoImprimeLaTasaDelSeguro()
    {
        var cotizacion = UnaRuta();
        var documento = clsCosteoCotizacionService.ConFilasSeguro(
            clsConstructorDocumentoCotizacion.Construir(
                clsCosteoCotizacionService.Payload(cotizacion, "Importacion", "COP"), cotizacion.Folio,
                new clsFirmaDocumento("Ana", null, null, null), DateOnly.FromDateTime(clsCotizacionMuestra.Ahora.UtcDateTime), "COP", 0m).Valor,
            cotizacion, "COP");

        var json = JsonSerializer.Serialize(documento);

        json.Should().NotContain("0,345").And.NotContain("0.00345").And.NotContain("0,00345", "el cliente ve el valor del seguro, nunca la tasa");
    }

    /// <summary>
    /// El seguro marcado sin valor de la mercancía no bloquea. El PDF sale sin la fila del seguro en esa
    /// ruta (no suma) y la respuesta y la vista del costeo traen el aviso con las rutas.
    /// </summary>
    [Fact]
    public async Task ConElSeguroMarcadoSinValorDeLaMercanciaElPdfSaleSinSuFilaYConElAviso()
    {
        var cotizacion = UnaRuta();
        QuitarValor(cotizacion);
        Registrada(Preparada(cotizacion));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        _documento!.Secciones.Single().Filas.Should().ContainSingle().Which.Tarifa.Should().Be("$1.250.000", "sin valor, el seguro no suma");
        _documento.TotalGeneral!.Valor.Should().Be("$1.250.000 COP");
        var aviso = resultado.Valor.Pdf.AvisoSeguro!;
        aviso.Codigo.Should().Be(clsCodigosError.SeguroSinValorMercancia);
        aviso.Rutas.Should().Equal(new Cotifly.Aplicacion.Costeo.DTOs.clsRutaAvisoSeguro(1, cotizacion.PrimeraRuta!.Id));
        aviso.Mensaje.Should().Contain("la ruta 1");
        clsCosteoCotizacionService.Vista(cotizacion, null).AvisoSeguro.Should().BeEquivalentTo(aviso, "la vista del costeo (y el detalle) trae el mismo aviso");
    }

    [Fact]
    public async Task ConElSeguroCalculadoNoHayAviso()
    {
        var cotizacion = Registrada(Preparada(UnaRuta()));

        var resultado = await Servicio().GenerarPdfAsync(_ana, cotizacion.Folio, Version, Ct);

        resultado.Valor.Pdf.AvisoSeguro.Should().BeNull();
        clsCosteoCotizacionService.Vista(cotizacion, null).AvisoSeguro.Should().BeNull();
    }

    /// <summary>Formulario de una ruta: flete 1.000.000 al 20 % (1.250.000) y valor de la mercancía 200.000.000 (seguro 690.000).</summary>
    private clsCotizacionSolicitud UnaRuta(
        bool conSeguro = true, string moneda = clsCotizacionSolicitud.MonedaPesos, decimal trm = 0m, decimal valor = 200_000_000m)
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m, folio: "PCN-900555111-AP-05112026-001", usuarioId: _ana,
            moneda: moneda, trm: trm, solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = valor });
        if (conSeguro)
        {
            cotizacion.CambiarSeguro(1, true, Tasa, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        }

        return cotizacion;
    }

    /// <summary>
    /// Spot de dos rutas al 20 % (1.000.000 → 1.250.000 y 2.000.000 → 2.500.000). Por defecto, la 1 sin seguro y la 2 (valor 100.000.000)
    /// con seguro de 345.000; con <paramref name="valores"/> y <paramref name="tasa"/>, cada ruta con valor lleva el seguro.
    /// </summary>
    internal clsCotizacionSolicitud DosRutas(decimal?[]? valores = null, decimal tasa = Tasa)
    {
        valores ??= [null, 100_000_000m];
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900555111-AP-05112026-002", UsuarioId = _ana, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S.",
        }).Valor;
        var lineas = Enumerable.Range(1, 2).Select(i => new clsLineaSolicitudCosteo
        {
            Origen = "Buenaventura",
            Destino = $"Destino {i}",
            OrigenMunicipioId = 76109,
            DestinoMunicipioId = 11000 + i,
            TipoVehiculo = "3S3",
            Carroceria = "FURGON",
            PesoKg = 10_000m + i,
            ValorMercancia = valores[i - 1],
        }).ToList();
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot }, lineas, [0, 1], 3);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, clsCotizacionMuestra.Ahora, clsCotizacionMuestra.Ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.20m, 0.20m, OrigenMargen.PoliticaLinea, "margen");
        }

        clsCotizacionMuestra.AplicarCosteo(cotizacion, [1_000_000m, 2_000_000m], EstadoCosteo.Autocosteo, null);
        foreach (var ruta in cotizacion.RutasOrdenadas.Where(r => r.ValorMercancia is not null))
        {
            cotizacion.CambiarSeguro(ruta.Orden, true, tasa, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        }

        return cotizacion;
    }

    /// <summary>
    /// Como la edición que quita el valor de la mercancía de la ruta 1 (<c>clsLineaSolicitud.AplicarEdicion</c>): el seguro queda marcado sin
    /// tasa ni costo y el precio de la ruta ya no lo suma.
    /// </summary>
    internal static void QuitarValor(clsCotizacionSolicitud cotizacion)
    {
        var ruta = cotizacion.PrimeraRuta!;
        var anterior = ruta.ValorMercancia;
        typeof(clsLineaSolicitud).GetProperty(nameof(clsLineaSolicitud.ValorMercancia))!.SetValue(ruta, null);
        ruta.ActualizarSeguroPorValor(anterior, Tasa).Should().BeTrue();
        cotizacion.RecalcularTotales(clsCotizacionMuestra.Ahora);
        ruta.Should().Match<clsLineaSolicitud>(r => r.IncluyeSeguro && r.CostoSeguro == null);
    }

    private static clsCotizacionSolicitud Preparada(clsCotizacionSolicitud cotizacion)
    {
        cotizacion.ActualizarClienteDelCatalogo("CLIENTE S.A.S.", null);
        cotizacion.Finalizar(cotizacion.UsuarioId ?? Guid.NewGuid(), clsCotizacionMuestra.Ahora);
        return clsCotizacionMuestra.ConVersionNegocio(cotizacion, Version);
    }

    private clsCotizacionSolicitud Registrada(clsCotizacionSolicitud cotizacion)
    {
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, _ana, Arg.Any<CancellationToken>()).Returns(cotizacion);
        return cotizacion;
    }

    private clsCosteoCotizacionService Servicio() => new(
        _cotizaciones, _politicas, _usuarios, _generador, _almacen, _unidad,
        Substitute.For<IClsCalendarioNegocio>(), Substitute.For<IClsAuditoriaService>(), Options.Create(new clsOpcionesPdf()),
        NullLogger<clsCosteoCotizacionService>.Instance);
}
