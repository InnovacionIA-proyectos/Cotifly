using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Excel de licitaciones (HU-011.2, ADR-017): lectura local del original con la regla de Nexo (ClosedXML), análisis reenviado a
/// Nexo (falso, nunca el real) y el alta de Nexo con N líneas.
/// </summary>
public sealed class clsExcelLicitacionTests : IDisposable
{
    private static readonly Guid _usuarioId = Guid.NewGuid();
    private readonly clsNexoFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly clsLectorExcelLicitacionClosedXml _lector = new();

    public clsExcelLicitacionTests()
    {
        _http = new HttpClient(_nexo);
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("ana", "Ana", "ana@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void LasFilasDeDatosSaltanLaPortadaElEncabezadoYLasFilasVacias()
    {
        var filas = _lector.FilasDeDatos(clsPliegoMuestra.Excel(4), clsPliegoMuestra.Hoja, TestContext.Current.CancellationToken);

        filas.EsExitoso.Should().BeTrue();
        filas.Valor.Should().Equal(Enumerable.Range(0, 4).Select(clsPliegoMuestra.FilaExcel), "la fila 6 está vacía y no cuenta, como en Nexo");
    }

    [Fact]
    public void UnSubencabezadoConRotulosRepetidosNoCuentaComoDato()
    {
        using var libro = new XLWorkbook();
        var ws = libro.Worksheets.Add("H");
        string[] encabezado = ["RUTA", "RUTA", "VEHÍCULO", "PESO"];
        string[] sub = ["Origen", "Destino", "Tipo", "Kg"];
        for (var c = 0; c < 4; c++)
        {
            ws.Cell(1, c + 1).Value = encabezado[c];
            ws.Cell(2, c + 1).Value = sub[c];
            ws.Cell(3, c + 1).Value = $"d{c}";
        }

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);

        _lector.FilasDeDatos(memoria.ToArray(), "H", TestContext.Current.CancellationToken).Valor.Should().Equal(3);
    }

    [Fact]
    public void UnArchivoIlegibleOUnaHojaInexistenteSonErroresDeNegocio()
    {
        _lector.FilasDeDatos([1, 2, 3], "Pliego", TestContext.Current.CancellationToken).Error.Codigo.Should().Be(clsCodigosError.ExcelInvalido);
        _lector.FilasDeDatos(clsPliegoMuestra.Excel(), "Otra", TestContext.Current.CancellationToken).Error.Codigo.Should().Be(clsCodigosError.ExcelSinDatos);
    }

    [Fact]
    public async Task ElAnalisisVaANexoComoMultipartConLaIdentidadDelComercial()
    {
        var archivo = clsPliegoMuestra.Excel();

        var analisis = await Analizador().AnalizarAsync(_usuarioId, "pliego.xlsx", archivo, Ct);

        analisis.EsExitoso.Should().BeTrue();
        analisis.Valor.Hojas.Should().ContainSingle().Which.Filas.Should().HaveCount(3);
        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Url.AbsolutePath.Should().Be(clsAnalizadorExcelNexo.Ruta);
        peticion.IdPortalUsuario.Should().Be("1007");
        _nexo.UltimoArchivoAnalizado.Should().BeGreaterThan(archivo.Length, "el multipart lleva el archivo completo");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "EXCEL_SIN_DATOS", "EXCEL_SIN_DATOS")]
    [InlineData(HttpStatusCode.BadRequest, "OTRO_CODIGO", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(HttpStatusCode.Forbidden, "SIN_PERMISO_CABINA_FLETES", clsCodigosError.NexoSinPermiso)]
    [InlineData(HttpStatusCode.Unauthorized, "SESION_INVALIDA", clsCodigosError.CosteoNexoNoDisponible)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE", clsCodigosError.ArchivoDemasiadoGrande)]
    [InlineData(HttpStatusCode.InternalServerError, "X", clsCodigosError.CosteoNexoNoDisponible)]
    public async Task LosErroresDeNexoSeTraducen(HttpStatusCode estado, string codigoNexo, string esperado)
    {
        _nexo.AnalisisFalla = estado;
        _nexo.CodigoAnalisisFalla = codigoNexo;

        var analisis = await Analizador().AnalizarAsync(_usuarioId, "pliego.xlsx", [1], Ct);

        analisis.Error.Codigo.Should().Be(esperado);
    }

    // HU-013.15 (#124): el 503 (y cualquier error de Nexo) lleva en el detalle solo el código de Nexo, nunca su texto crudo.
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "SESION_INVALIDA")]
    [InlineData(HttpStatusCode.Forbidden, "SIN_PERMISO_CABINA_FLETES")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "PAYLOAD_TOO_LARGE")]
    [InlineData(HttpStatusCode.Conflict, "OTRO_CODIGO")]
    public async Task ElDetalleDeUnErrorDeNexoLlevaSoloSuCodigo(HttpStatusCode estado, string codigoNexo)
    {
        _nexo.AnalisisFalla = estado;
        _nexo.CodigoAnalisisFalla = codigoNexo;

        var analisis = await Analizador().AnalizarAsync(_usuarioId, "pliego.xlsx", [1], Ct);

        analisis.Error.Detalle.Should().Be(codigoNexo);
    }

    // HU-013.15 (#124): las filas con datos de todas las hojas en una sola lectura, con los números de fila de Excel.
    [Fact]
    public void LasFilasDeDatosPorHojaTraenLasFilasDelExcelDeCadaHojaConEncabezado()
    {
        var porHoja = _lector.FilasDeDatosPorHoja(clsPliegoMuestra.Excel(3), Ct);

        porHoja.Valor.Keys.Should().Equal(clsPliegoMuestra.Hoja);
        porHoja.Valor[clsPliegoMuestra.Hoja].Should().Equal(4, 5, 7);
        _lector.FilasDeDatosPorHoja([1, 2, 3], Ct).Error.Codigo.Should().Be(clsCodigosError.ExcelInvalido);
    }

    [Fact]
    public void UnaHojaQueNoSePuedeUbicarNoQuitaLasFilasDelExcelDeLasDemas()
    {
        using var libro = new XLWorkbook(new MemoryStream(clsPliegoMuestra.Excel(3)));
        var rota = libro.Worksheets.Add("Combinadas");
        for (var r = 1; r <= clsLectorExcelLicitacionClosedXml.MaximoCombinadas + 1; r++)
        {
            rota.Cell(r, 1).Value = "x";
            rota.Range(r, 1, r, 2).Merge();
        }

        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);

        var porHoja = _lector.FilasDeDatosPorHoja(memoria.ToArray(), Ct);

        _lector.FilasDeDatos(memoria.ToArray(), "Combinadas", Ct).Error.Codigo.Should().Be(clsCodigosError.ExcelInvalido);
        porHoja.EsExitoso.Should().BeTrue("una hoja con demasiadas combinadas solo queda fuera");
        porHoja.Valor.Keys.Should().Equal(clsPliegoMuestra.Hoja);
        porHoja.Valor[clsPliegoMuestra.Hoja].Should().Equal(4, 5, 7);
    }

    [Fact]
    public async Task SinNexoConfiguradoNoSeLlama()
    {
        var analisis = await Analizador(url: null).AnalizarAsync(_usuarioId, "pliego.xlsx", [1], Ct);

        analisis.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public void ElAltaDeUnaLicitacionLlevaTodasSusLineasEnOrdenConLaLineaDeNegocioEnCadaUna()
    {
        var encabezado = new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion, Referencia = "Pliego 7" };
        clsLineaSolicitudCosteo[] lineas =
        [
            new() { Origen = "A", Destino = "B", PesoKg = 1m },
            new() { Origen = "C", Destino = "D", PesoKg = 2m, Posiciones = [new clsPosicionCarga(null, 1, 1, 1, 1, 2m)] },
        ];

        var json = JsonSerializer.Serialize(clsCosteoNexo.CuerpoCaptura(new clsSolicitudNexo("15927", encabezado, lineas, "Licitaciones")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        using var cuerpo = JsonDocument.Parse(json);

        cuerpo.RootElement.GetProperty("tipo").GetString().Should().Be("licitacion");
        var enviadas = cuerpo.RootElement.GetProperty("lineas").EnumerateArray().ToList();
        enviadas.Select(l => l.GetProperty("origen").GetString()).Should().Equal("A", "C");
        enviadas.Should().OnlyContain(l => l.GetProperty("lineaNegocio").GetString() == "Licitaciones");
        enviadas[1].GetProperty("posiciones").GetArrayLength().Should().Be(1);
    }

    private clsAnalizadorExcelNexo Analizador(string? url = clsNexoFalso.UrlBase) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url }), _usuarios, NullLogger<clsAnalizadorExcelNexo>.Instance);

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }
}
