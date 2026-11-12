using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Tests.Costeo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Correo;

/// <summary>
/// HU-045.3 (#265, ADR-032 §9): el correo y su vista previa. Con la regla de HU-033 (sin desglose) cada ruta sigue con un solo valor, su
/// precio final con el seguro, y el total lo incluye; la ruta que lo incluye lo dice, sin el monto ni la tasa. Una ruta con el seguro marcado
/// sin valor de la mercancía no bloquea el envío (decisión del usuario del 2026-11-09).
/// </summary>
public sealed class clsSeguroCorreoTests : IDisposable
{
    private const decimal Tasa = 0.003450m;

    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsCorreoRepository _correos = Substitute.For<IClsCorreoRepository>();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsAlmacenPdf _almacen = Substitute.For<IClsAlmacenPdf>();
    private readonly IClsTransporteCorreo _transporte = Substitute.For<IClsTransporteCorreo>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly clsCandadoEnvios _candado = new();
    private readonly clsUsuarioWeb _comercial = clsUsuarioWeb.Crear("nvargas", "Nathaly Vargas", "nvargas@boterosoto.com.co", null, DateTimeOffset.UnixEpoch).Valor;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _candado.Dispose();

    private clsCotizacionSolicitud ConSeguro()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m, folio: "PCN-C9", usuarioId: _comercial.Id,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = 200_000_000m });
        cotizacion.CambiarSeguro(1, true, Tasa, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();
        return cotizacion;
    }

    [Fact]
    public void ElResumenLlevaElPrecioFinalConElSeguroYMarcaLaRutaQueLoIncluye()
    {
        var resumen = clsPlantillaCorreo.Resumen(ConSeguro())!;

        resumen.Total.Should().Be(1_940_000m, "1.250.000 del flete + 690.000 del seguro");
        var ruta = resumen.Rutas.Single();
        ruta.Precio.Should().Be(1_940_000m, "HU-033: un solo valor por ruta, sin desglose");
        ruta.IncluyeSeguro.Should().BeTrue();
        var html = clsPlantillaCorreo.ResumenHtml(resumen);
        html.Should().Contain(System.Net.WebUtility.HtmlEncode(clsPlantillaCorreo.TextoIncluyeSeguro))
            .And.Contain("$1.940.000 COP")
            .And.NotContain("690.000", "sin desglose: el correo no separa el seguro")
            .And.NotContain("0,345");
    }

    [Fact]
    public void SinSeguroLaRutaNoLoMenciona()
    {
        var resumen = clsPlantillaCorreo.Resumen(clsCotizacionMuestra.Costeada())!;

        resumen.Rutas.Single().IncluyeSeguro.Should().BeFalse();
        clsPlantillaCorreo.ResumenHtml(resumen).Should().NotContain(System.Net.WebUtility.HtmlEncode(clsPlantillaCorreo.TextoIncluyeSeguro));
    }

    /// <summary>Decisión del usuario del 2026-11-09: el seguro marcado sin valor de la mercancía no bloquea el envío; la ruta no dice que lo incluye.</summary>
    [Fact]
    public async Task ConElSeguroMarcadoSinValorDeLaMercanciaSeEnviaSinDecirQueLoIncluye()
    {
        var cotizacion = ConSeguro();
        clsSeguroPdfCosteoTests.QuitarValor(cotizacion);
        cotizacion.ActualizarClienteDelCatalogo("CLIENTE S.A.S.", null);
        cotizacion.Finalizar(_comercial.Id, clsCotizacionMuestra.Ahora);
        cotizacion.AsignarPdf("/data/pdfs/PCN-C9.pdf");
        _usuarios.ObtenerPorIdAsync(_comercial.Id, Arg.Any<CancellationToken>()).Returns(_comercial);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C9", _comercial.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _correos.ListarEnviadosDesdeAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns([]);
        _almacen.LeerPorReferenciaAsync("/data/pdfs/PCN-C9.pdf", Arg.Any<CancellationToken>()).Returns("%PDF-1.4 prueba"u8.ToArray());
        _unidad.GuardarCambiosSiVersionAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        clsMensajeCorreo? enviado = null;
        _transporte.EnviarAsync(Arg.Do<clsMensajeCorreo>(m => enviado = m), Arg.Any<CancellationToken>())
            .Returns(new clsEnvioAceptado(Guid.NewGuid(), Cotifly.Dominio.Correo.EstadoCorreo.Encolado, null));
        var servicio = new clsCorreoService(_cotizaciones, _correos, _usuarios, _almacen, _transporte, _unidad, new clsRelojFalso(),
            Substitute.For<IClsCalendarioNegocio>(), _candado,
            Options.Create(new clsOpcionesCorreo { Habilitado = true, Proteccion = new() { Modo = "ninguno", PermitirEnvioReal = true } }),
            Options.Create(new clsOpcionesPdf { TextoSeguros = null }),
            new clsLoggerFalso<clsCorreoService>());

        var resultado = await servicio.EnviarAsync(new clsEntradaEnviarCorreo(_comercial.Id, ["cliente@empresa.com"], Folio: "PCN-C9"), Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        enviado!.CuerpoHtml.Should().Contain("$1.250.000 COP", "sin valor, el seguro no suma")
            .And.NotContain(System.Net.WebUtility.HtmlEncode(clsPlantillaCorreo.TextoIncluyeSeguro));
    }

    /// <summary>B1: en COP, el precio de la ruta y el total con centavos (el seguro) se muestran con sus 2 decimales.</summary>
    [Fact]
    public void ElResumenMuestraLosCentavosDelSeguro()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 1_000_000m, margen: 0.20m,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = 100_400m });
        cotizacion.CambiarSeguro(1, true, 0.001m, clsCotizacionMuestra.Ahora).Valor.Should().BeTrue();

        var resumen = clsPlantillaCorreo.Resumen(cotizacion)!;

        resumen.Rutas.Single().PrecioTexto.Should().Be("$1.250.100,40");
        resumen.TotalTexto.Should().Be("$1.250.100,40");
    }

    /// <summary>B5 (decisión del usuario): la vista previa del correo trae el mismo <c>aviso_seguro</c> que el costeo.</summary>
    [Fact]
    public async Task LaVistaPreviaTraeElAvisoDelSeguroSinValor()
    {
        var cotizacion = ConSeguro();
        clsSeguroPdfCosteoTests.QuitarValor(cotizacion);
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C9", _comercial.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        var previos = Substitute.For<IClsDestinatariosPreviosService>();
        previos.ObtenerAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new clsDestinatarios([], []));
        var servicio = new clsVistaPreviaCorreoService(_cotizaciones, previos, _usuarios, Substitute.For<IClsCalendarioNegocio>(),
            Options.Create(new clsOpcionesCorreo { Habilitado = true }), Options.Create(new clsOpcionesPdf { TextoSeguros = null }));

        var vista = await servicio.PrevisualizarAsync(_comercial.Id, "PCN-C9", cancellationToken: Ct);

        vista.Valor.AvisoSeguro!.Rutas.Should().ContainSingle().Which.LineaOrden.Should().Be(1);
        (await servicio.PrevisualizarAsync(_comercial.Id, "PCN-C9", cancellationToken: Ct)).Valor.AvisoSeguro.Should().NotBeNull();
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync("PCN-C9", _comercial.Id, Arg.Any<CancellationToken>()).Returns(ConSeguro());
        (await servicio.PrevisualizarAsync(_comercial.Id, "PCN-C9", cancellationToken: Ct)).Valor.AvisoSeguro.Should().BeNull();
    }
}
