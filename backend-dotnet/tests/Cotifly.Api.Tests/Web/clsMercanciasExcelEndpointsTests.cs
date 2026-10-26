using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-035.1 (#202, ADR-024): <c>POST /web/mercancias/analizar-excel</c> por HTTP contra SQL Server real (sesiones), el Nexo falso (que
/// nunca debe recibir el archivo) y el cubicador falso: la grilla cruda cumple el contrato, el catálogo es opcional y los archivos
/// corruptos o maliciosos son 400 con código; sin multipart 415, sin sesión 401 y un cuerpo de más de 12 MB 413.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMercanciasExcelEndpointsTests : IDisposable
{
    private const string Ruta = "/web/mercancias/analizar-excel";
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new();
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    public clsMercanciasExcelEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("CAFE_EN_ESTIBAS", "Café en estibas"));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("BOBINAS_DE_ACERO", "Bobinas de acero"));
    }

    private clsApiWebPruebas Api(bool conCubicador = true)
    {
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, cubicador: conCubicador ? _cubicador : null);
        _apis.Add(api);
        return api;
    }

    private void SinLlamarANexo() =>
        _nexo.Peticiones.Should().NotContain(p => p.Metodo == HttpMethod.Post, "el Excel de mercancías se lee en Cotifly, nunca en Nexo");

    [Fact]
    public async Task LaGrillaCrudaCumpleElContratoSugiereElCodigoYNoLlamaANexo()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access,
            clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.Excel(3), "mercancias.xlsx"));

        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);
        json.GetProperty("archivo").GetString().Should().Be("mercancias.xlsx");
        json.GetProperty("catalogo_mercancias_disponible").GetBoolean().Should().BeTrue();
        var hoja = json.GetProperty("hojas")[0];
        hoja.GetProperty("nombre").GetString().Should().Be(clsExcelMercanciasMuestra.Hoja);
        hoja.GetProperty("fila_encabezado").GetInt32().Should().Be(1);
        hoja.GetProperty("oculta").GetBoolean().Should().BeFalse();
        hoja.GetProperty("encabezados").EnumerateArray().Select(e => e.GetString()).Should().Equal(clsExcelMercanciasMuestra.Encabezados);
        hoja.GetProperty("filas")[0].EnumerateArray().Select(e => e.GetString()).Should().Equal("Cafe en estiba", "10", "1.2", "1", "1.5", "850.5");
        hoja.GetProperty("filas_excel").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(2, 3, 4);
        hoja.GetProperty("columna_mercancia").GetString().Should().Be("Tipo de mercancía");
        var mercancias = hoja.GetProperty("mercancias").EnumerateArray().ToList();
        mercancias[0].GetProperty("codigo_mercancia").GetString().Should().Be("CAFE_EN_ESTIBAS");
        mercancias[0].GetProperty("sugerido").GetBoolean().Should().BeTrue();
        mercancias[1].GetProperty("codigo_mercancia").GetString().Should().Be("BOBINAS_DE_ACERO");
        SinLlamarANexo();
    }

    [Fact]
    public async Task UnaHojaOcultaSeLeeYVieneMarcada()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access,
            clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.ConHojaOculta(), "mercancias.xlsx"));

        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);
        json.GetProperty("hojas").EnumerateArray().Select(h => (h.GetProperty("nombre").GetString(), h.GetProperty("oculta").GetBoolean()))
            .Should().Equal(("Listas", true), (clsExcelMercanciasMuestra.Hoja, false));
        SinLlamarANexo();
    }

    [Fact]
    public async Task ElGerenteTambienAnalizaComoEnLaCaptura()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.Excel(1), "m.xlsx"));

        await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConElCubicadorCaidoElAnalisisSigueSinSugerencias()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.Excel(3), "m.xlsx"));

        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK);
        json.GetProperty("catalogo_mercancias_disponible").GetBoolean().Should().BeFalse();
        json.GetProperty("hojas")[0].GetProperty("filas").GetArrayLength().Should().Be(3);
        json.GetProperty("hojas")[0].GetProperty("mercancias")[0].GetProperty("codigo_mercancia").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SinCubicadorConfiguradoTambienResponde()
    {
        var api = Api(conCubicador: false);
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.Excel(2), "m.xlsx"));

        (await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK))
            .GetProperty("catalogo_mercancias_disponible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task UnaHojaDeMasDeMilFilasLlegaTruncadaYCumpleElContrato()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.Excel(1200), "m.xlsx"));

        var hoja = (await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.OK)).GetProperty("hojas")[0];
        hoja.GetProperty("filas").GetArrayLength().Should().Be(1000);
        hoja.GetProperty("total_filas").GetInt32().Should().Be(1200);
        hoja.GetProperty("filas_truncadas").GetBoolean().Should().BeTrue();
    }

    public static TheoryData<string, string, string, string?> Invalidos() => new()
    {
        { "texto", "mercancias.xlsx", "EXCEL_INVALIDO", "NO_ES_XLSX" },
        { "zip sin libro", "mercancias.xlsx", "EXCEL_INVALIDO", "SIN_LIBRO" },
        { "truncado", "mercancias.xlsx", "EXCEL_INVALIDO", "ZIP_INVALIDO" },
        { "libro ilegible", "mercancias.xlsm", "EXCEL_INVALIDO", "LIBRO_ILEGIBLE" },
        { "bomba zip", "mercancias.xlsx", "ARCHIVO_DEMASIADO_GRANDE", "DESCOMPRIMIDO_EXCEDE_LIMITE" },
        { "densa", "mercancias.xlsx", "ARCHIVO_DEMASIADO_GRANDE", "DESCOMPRIMIDO_EXCEDE_LIMITE" },
        { "xxe", "mercancias.xlsx", "EXCEL_INVALIDO", "LIBRO_ILEGIBLE" },
        { "xls renombrado", "mercancias.xlsx", "EXCEL_INVALIDO", "CIFRADO_O_XLS" },
        { "sin datos", "mercancias.xlsx", "EXCEL_SIN_DATOS", null },
        { "csv", "mercancias.csv", "FORMATO_NO_SOPORTADO", null },
        { "vacío", "mercancias.xlsx", "ARCHIVO_REQUERIDO", null },
    };

    [Theory]
    [MemberData(nameof(Invalidos))]
    public async Task UnArchivoCorruptoOMaliciosoEsUn400ConCodigoNuncaUn500(string caso, string nombre, string codigo, string? motivo)
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var archivo = caso switch
        {
            "texto" => clsExcelMercanciasMuestra.TextoRenombrado(),
            "zip sin libro" => clsExcelMercanciasMuestra.ZipSinLibro(),
            "truncado" => clsExcelMercanciasMuestra.Truncado(),
            "libro ilegible" => clsExcelMercanciasMuestra.LibroIlegible(),
            "bomba zip" => clsExcelMercanciasMuestra.BombaZip(),
            "densa" => clsExcelMercanciasMuestra.Densa(4_000_000),
            "xxe" => clsExcelMercanciasMuestra.ConEntidadExterna(),
            "xls renombrado" => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0],
            "sin datos" => clsExcelMercanciasMuestra.SinDatos(),
            "csv" => clsExcelMercanciasMuestra.TextoRenombrado(),
            _ => [],
        };

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, clsApiWebPruebas.Archivo(archivo, nombre));

        var error = (await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.BadRequest)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be(codigo, caso);
        if (motivo is not null)
        {
            error.GetProperty("detalle").GetProperty("motivo").GetString().Should().Be(motivo, caso);
        }

        SinLlamarANexo();
    }

    [Fact]
    public async Task SinLaParteArchivoEsArchivoRequerido()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, new MultipartFormDataContent { { new StringContent("x"), "otro" } });

        (await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("ARCHIVO_REQUERIDO");
    }

    [Fact]
    public async Task SinMultipartEs415ConErrorWeb()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarAsync(_post, Ruta, sesion.Access, """{"archivo":"mercancias.xlsx"}""");

        var json = await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.UnsupportedMediaType);
        json.GetProperty("error").GetString().Should().Contain("multipart/form-data");
    }

    [Fact]
    public async Task SinSesionEs401AntesQueElTipoDeContenido()
    {
        var api = Api();

        using var respuesta = await api.EnviarAsync(_post, Ruta, token: null, """{"archivo":"x"}""");

        await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnCuerpoDeMasDeDoceMegasEs413SinLeerlo()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var grande = new ByteArrayContent(new byte[(12 * 1024 * 1024) + 1]);
        grande.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, grande);

        await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task UnArchivoDeMasDelTopeEsArchivoDemasiadoGrandeAunqueElCuerpoQuepa()
    {
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var archivo = new byte[11 * 1024 * 1024];
        Encoding.ASCII.GetBytes("PK\u0003\u0004").CopyTo(archivo, 0);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, clsApiWebPruebas.Archivo(archivo, "m.xlsx"));

        (await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("ARCHIVO_DEMASIADO_GRANDE");
    }

    [Fact]
    public async Task ConElCupoDeLecturasLlenoEs503ServicioOcupadoConRetryAfter()
    {
        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, cubicador: _cubicador,
            servicios: s => s.AddSingleton<Cotifly.Aplicacion.Mercancias.Interfaces.IClsLectorGrillaExcel, clsLectorOcupado>());
        _apis.Add(api);
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, Ruta, sesion.Access, clsApiWebPruebas.Archivo(clsExcelMercanciasMuestra.Excel(1), "m.xlsx"));

        (await VerificarContratoAsync(respuesta, Ruta, _post, HttpStatusCode.ServiceUnavailable))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("SERVICIO_OCUPADO");
        respuesta.Headers.RetryAfter.Should().NotBeNull("el frontend reintenta tras Retry-After");
        SinLlamarANexo();
    }

    /// <summary>Lector con el cupo siempre lleno (como el real cuando 4 lecturas no se liberan en 15 s).</summary>
    private sealed class clsLectorOcupado : Cotifly.Aplicacion.Mercancias.Interfaces.IClsLectorGrillaExcel
    {
        public Task<Cotifly.Dominio.Comun.clsResultado<Cotifly.Aplicacion.Mercancias.DTOs.clsAnalisisExcelMercancias>> LeerAsync(
            string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default) =>
            Task.FromResult<Cotifly.Dominio.Comun.clsResultado<Cotifly.Aplicacion.Mercancias.DTOs.clsAnalisisExcelMercancias>>(
                new Cotifly.Dominio.Comun.clsErrorDominio(Cotifly.Dominio.Comun.clsCodigosError.ServicioOcupado, "Ocupado."));
    }

    public void Dispose()
    {
        foreach (var api in _apis)
        {
            api.Dispose();
        }

        _nexo.Dispose();
        _cubicador.Dispose();
    }
}
