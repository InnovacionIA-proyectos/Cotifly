using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Api.Comun;
using Cotifly.Api.Licitaciones.DTOs;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Contrato;
using Cotifly.Pruebas.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-044.3 (#254): <c>POST /web/licitaciones/aplicar-mapeo</c> por HTTP contra SQL Server real, el Nexo falso y el cubicador falso, con el
/// cuerpo validado contra <c>specs/openapi.yaml</c>: transformaciones de NEXO (toneladas, «ORIGEN - DESTINO», coma decimal), errores por
/// fila con su número, posiciones como mercancías, línea de negocio de Cotifly y de NEXO, peso sin tara, 5000/5001 filas y los errores de NEXO.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsAplicacionMapeoEndpointsTests : IDisposable
{
    private const string Ruta = "/web/licitaciones/aplicar-mapeo";
    private const string RutaNexo = "/api/captura-solicitudes/aplicar-mapeo";
    private readonly clsNexoFalso _nexo = new();
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly clsApiWebPruebas _api;

    public clsAplicacionMapeoEndpointsTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("CAFE_EN_ESTIBAS", "Café en estibas"));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("BOBINAS_ACERO", "Bobinas de acero"));
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo, cubicador: _cubicador);
    }

    private static object[] Columnas(params (int Indice, string Campo, string? Transformacion)[] columnas) =>
        [.. columnas.Select(c => new { indice = c.Indice, campo = c.Campo, transformacion = c.Transformacion })];

    private static string Cuerpo(object[] columnas, IEnumerable<string?[]> filas, int? primeraFila = null, string? nit = "900.123.456-7") =>
        JsonSerializer.Serialize(new { nit_cliente = nit, columnas, filas, primera_fila = primeraFila });

    private static readonly object[] _origenDestinoTon = Columnas((0, "origen", null), (1, "destino", null), (2, "peso", "peso_toneladas_a_kg"));

    private async Task<JsonElement> PostAsync(string cuerpo, HttpStatusCode esperado)
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, cuerpo);
        return await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, esperado);
    }

    private IReadOnlyList<JsonNode> CuerposANexo() =>
        [.. _nexo.Peticiones.Where(p => p.Url.AbsolutePath == RutaNexo).Select(p => JsonNode.Parse(p.Cuerpo!)!)];

    [Fact]
    public async Task AplicaLasTransformacionesDeNexoYDevuelveLasLineasComoLineaLicitacionWeb()
    {
        var columnas = Columnas((0, "origen", "dividir_origen_destino"), (1, "peso", "peso_toneladas_a_kg"), (2, "valorMercancia", null),
            (3, "tipoVehiculo", null), (4, "ignorar", null));
        var json = await PostAsync(Cuerpo(columnas, [["BUENAVENTURA - BOGOTÁ", "24,5", "1.500.000,50", "Tractomula", "nota"]], primeraFila: 4), HttpStatusCode.OK);

        var linea = json.GetProperty("lineas").EnumerateArray().Single();
        (linea.GetProperty("origen").GetString(), linea.GetProperty("destino").GetString()).Should().Be(("BUENAVENTURA", "BOGOTÁ"));
        linea.GetProperty("peso_kg").GetDecimal().Should().Be(24_500m, "24,5 toneladas con coma decimal");
        linea.GetProperty("valor_mercancia").GetDecimal().Should().Be(1_500_000.50m);
        linea.GetProperty("tipo_vehiculo").GetString().Should().Be("Tractomula", "aplicar-mapeo no reemplaza la validación de códigos del alta");
        linea.GetProperty("fila").GetInt32().Should().Be(0);
        linea.GetProperty("fila_excel").GetInt32().Should().Be(4);
        linea.GetProperty("linea_negocio").ValueKind.Should().Be(JsonValueKind.Null);
        linea.GetProperty("linea_nexo").ValueKind.Should().Be(JsonValueKind.Null);
        linea.GetProperty("origen_municipio_id").ValueKind.Should().Be(JsonValueKind.Null, "los ids de municipio van siempre en null");
        foreach (var ausente in new[] { "peso_con_tara_kg", "posiciones", "servicios_conexos", "valor_levante_contenedor" })
        {
            linea.TryGetProperty(ausente, out _).Should().BeFalse($"{ausente} no sale del Excel");
        }

        json.TryGetProperty("primera_fila", out _).Should().BeFalse("la respuesta no lleva primera_fila (cada línea trae fila y fila_excel)");
        (json.GetProperty("total_filas").GetInt32(), json.GetProperty("total_errores").GetInt32()).Should().Be((1, 0));
        var enviado = CuerposANexo().Single();
        enviado["clienteNit"]!.GetValue<string>().Should().Be("900123456");
        enviado["primeraFila"]!.GetValue<int>().Should().Be(4);
        enviado["columnas"]!.AsArray().Select(c => (c!["indice"]!.GetValue<int>(), c["campo"]!.GetValue<string>(), c["transformacion"]?.GetValue<string>()))
            .Should().Equal((0, "origen", "dividir_origen_destino"), (1, "peso", "peso_toneladas_a_kg"), (2, "valorMercancia", null),
                (3, "tipoVehiculo", null), (4, "ignorar", null));
        _nexo.Peticiones.Last(p => p.Url.AbsolutePath == RutaNexo).IdPortalUsuario.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task LosErroresPorFilaTraenElNumeroDeFilaDeExcelYLaPosicionEnLasFilasYSinIndice()
    {
        var json = await PostAsync(Cuerpo(_origenDestinoTon,
        [
            ["Cali", "Bogotá", "1,5"],
            ["Medellín", "", "2"],
            ["Pasto", "Cali", "dos"],
            ["Ipiales", "Pasto", null],
        ], primeraFila: 6), HttpStatusCode.OK);

        json.GetProperty("lineas").EnumerateArray().Select(l => (l.GetProperty("fila").GetInt32(), l.GetProperty("fila_excel").GetInt32(), l.GetProperty("origen").GetString()))
            .Should().Equal((0, 6, "Cali"), (3, 9, "Ipiales"));
        json.GetProperty("errores").EnumerateArray()
            .Select(e => (e.GetProperty("fila").GetInt32(), e.GetProperty("fila_excel").GetInt32(), e.GetProperty("campo").GetString(),
                e.GetProperty("codigo").GetString(), e.TryGetProperty("indice", out _)))
            .Should().Equal((1, 7, "destino", "LINEA_INCOMPLETA", false), (2, 8, "peso", "VALOR_INVALIDO", false));
        (json.GetProperty("total_filas").GetInt32(), json.GetProperty("filas_vacias").GetInt32(), json.GetProperty("filas_con_errores").GetInt32(),
            json.GetProperty("total_errores").GetInt32()).Should().Be((4, 0, 2, 2));
    }

    [Fact]
    public async Task LasPosicionesSonMercanciasLaLineaDeNegocioLaDeCotiflyYLaSinEquivalenciaVaEnLineasYEnErrores()
    {
        _nexo.AplicarMapeoFijo = """
            {"totalFilas":3,"filasVacias":0,"filasConErrores":0,"totalErrores":0,"errores":[],"lineas":[
              {"fila":2,"origen":"Buenaventura","destino":"Bogotá","lineaNegocio":"Puerto a interior","claseViaje":"Contenedor",
               "claseContenedor":"CNT40.HC","peso":24500,"referencia":"REF-9","deadline":"2026-12-01T00:00:00Z",
               "posiciones":[{"descripcion":"Café en estibas de exportación","cantidad":20,"largoCm":120,"anchoCm":100,"altoCm":150,"pesoKg":1000},
                             {"descripcion":"Repuestos","cantidad":2,"largoCm":0,"anchoCm":0,"altoCm":0,"pesoKg":50,"codigoTipoMercancia":"REPUESTOS"}]},
              {"fila":3,"origen":"Cali","destino":"Bogotá","lineaNegocio":"Licitaciones","peso":1000},
              {"fila":4,"origen":"Cali","destino":"Medellín","lineaNegocio":"Nacional","claseViaje":"Carga suelta","peso":8000}]}
            """;

        var json = await PostAsync(Cuerpo(_origenDestinoTon, [["a", "b", "1"], ["c", "d", "1"], ["e", "f", "1"]]), HttpStatusCode.OK);

        var lineas = json.GetProperty("lineas").EnumerateArray().ToList();
        lineas.Select(l => (l.GetProperty("fila").GetInt32(), l.GetProperty("linea_negocio").GetString(), l.GetProperty("linea_nexo").GetString(),
                l.TryGetProperty("peso_con_tara_kg", out _)))
            .Should().Equal((0, "Importacion", "Puerto a interior", false), (1, null, "Licitaciones", false), (2, "Nacional", "Nacional", false));
        lineas[0].GetProperty("peso_kg").GetDecimal().Should().Be(24_500m, "la carga sin tara, aun en Contenedor");
        var mercancias = lineas[0].GetProperty("mercancias").EnumerateArray().ToList();
        mercancias.Select(m => (m.GetProperty("codigo_mercancia").GetString(), m.GetProperty("descripcion").GetString(), m.GetProperty("nombre_mercancia").GetString(),
                m.GetProperty("cantidad").GetInt32(), m.GetProperty("peso_kg").GetDecimal()))
            .Should().Equal(
                ("CAFE_EN_ESTIBAS", "Café en estibas de exportación", "Café en estibas de exportación", 20, 1000m),
                ("REPUESTOS", "Repuestos", "Repuestos", 2, 50m));
        mercancias[0].GetProperty("largo_cm").GetDecimal().Should().Be(120m);
        mercancias[1].GetProperty("largo_cm").ValueKind.Should().Be(JsonValueKind.Null, "sin medidas en NEXO");
        var error = json.GetProperty("errores").EnumerateArray().Single();
        (error.GetProperty("fila_excel").GetInt32(), error.GetProperty("campo").GetString(), error.GetProperty("codigo").GetString())
            .Should().Be((3, "linea_negocio", "LINEA_NEGOCIO_SIN_EQUIVALENCIA"));
        (json.GetProperty("filas_con_errores").GetInt32(), json.GetProperty("total_errores").GetInt32()).Should().Be((1, 1));

        // La línea va tal cual en lineas[] de POST /web/licitaciones: se lee como LineaLicitacionWeb con los mismos valores (fila_excel y
        // linea_nexo son campos de más que el alta ignora).
        var comoAlta = JsonSerializer.Deserialize<clsLineaLicitacionWebRequest>(lineas[0].GetRawText(), clsJsonWeb.Opciones)!.AEntrada();
        (comoAlta.Fila, comoAlta.LineaNegocio, comoAlta.Linea.Origen, comoAlta.Linea.PesoKg, comoAlta.Linea.PesoConTaraKg, comoAlta.Linea.ClaseContenedor)
            .Should().Be(((int?)0, "Importacion", "Buenaventura", (decimal?)24_500m, (decimal?)null, "CNT40.HC"));
        comoAlta.Linea.Posiciones.Select(p => (p.CodigoMercancia, p.Descripcion, p.Cantidad, p.PesoKg))
            .Should().Equal(("CAFE_EN_ESTIBAS", "Café en estibas de exportación", 20, (decimal?)1000m), ("REPUESTOS", "Repuestos", 2, (decimal?)50m));
    }

    [Theory]
    [InlineData("LineaLicitacionImportada", "LineaLicitacionWeb")]
    [InlineData("MercanciaImportada", "PosicionCargaWeb")]
    public async Task LaLineaImportadaEsUnSubconjuntoDeLaLineaDelAltaConLosMismosTipos(string importada, string delAlta)
    {
        // fila_excel y linea_nexo son los dos campos de más que el contrato declara sobre la línea del alta (el resto es un subconjunto).
        var extras = new HashSet<string>(StringComparer.Ordinal) { "fila_excel", "linea_nexo" };
        var propia = await clsValidadorEsquemaOpenApi.EsquemaAsync(importada);
        var alta = await clsValidadorEsquemaOpenApi.EsquemaAsync(delAlta);

        foreach (var (nombre, esquema) in propia.Properties!)
        {
            if (extras.Contains(nombre))
            {
                continue;
            }

            alta.Properties!.Should().ContainKey(nombre, $"{importada}.{nombre} debe existir en {delAlta}");
            var deAlta = alta.Properties![nombre];
            (deAlta.Type & ~Microsoft.OpenApi.JsonSchemaType.Null).Should().Be(esquema.Type & ~Microsoft.OpenApi.JsonSchemaType.Null, $"{importada}.{nombre}");
        }
    }

    [Fact]
    public async Task CincoMilFilasSeAplicanYCincoMilUnaEsDemasiadasFilasSinLlamarANexo()
    {
        string?[][] Filas(int n) => [.. Enumerable.Range(0, n).Select(i => new string?[] { $"Origen {i}", $"Destino {i}", "1" })];

        var tope = await PostAsync(Cuerpo(_origenDestinoTon, Filas(5000)), HttpStatusCode.OK);
        var masUna = await PostAsync(Cuerpo(_origenDestinoTon, Filas(5001)), HttpStatusCode.BadRequest);

        tope.GetProperty("lineas").GetArrayLength().Should().Be(5000);
        tope.GetProperty("lineas")[4999].GetProperty("fila").GetInt32().Should().Be(4999);
        masUna.GetProperty("error").GetProperty("codigo").GetString().Should().Be("DEMASIADAS_FILAS");
        CuerposANexo().Should().ContainSingle();
    }

    [Fact]
    public async Task UnErrorDelMapeoEnNexoEs400ConElCodigoDeNexo()
    {
        var json = await PostAsync(Cuerpo(Columnas((0, "origen", null)), [["Cali"]]), HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("CAMPO_REQUERIDO", "NEXO exige origen y destino");
        CuerposANexo().Should().ContainSingle();
    }

    [Theory]
    [InlineData("""{"columnas":[],"filas":[["a"]]}""", "MAPEO_REQUERIDO")]
    [InlineData("""{"columnas":[{"indice":0,"campo":"origen"},{"indice":0,"campo":"destino"}],"filas":[["a"]]}""", "COLUMNA_INVALIDA")]
    [InlineData("""{"columnas":[{"indice":0,"campo":"origen"},{"indice":1,"campo":"origen"}],"filas":[["a","b"]]}""", "CAMPO_DUPLICADO")]
    [InlineData("""{"columnas":[{"indice":0,"campo":"origen"}],"filas":[]}""", "EXCEL_SIN_DATOS")]
    [InlineData("""{"columnas":[{"indice":0,"campo":"origen"}],"filas":[["a"],null]}""", "FILA_INVALIDA")]
    [InlineData("""{"columnas":[{"indice":0,"campo":"origen"}],"filas":[["a"]],"primera_fila":1}""", "FILA_INVALIDA")]
    [InlineData("""{"nit_cliente":"9999999999999999999999999999999","columnas":[{"indice":0,"campo":"origen"}],"filas":[["a"]]}""", "CLIENTE_NIT_INVALIDO")]
    public async Task UnaPeticionMalFormadaEs400SinLlamarANexo(string cuerpo, string codigo)
    {
        var json = await PostAsync(cuerpo, HttpStatusCode.BadRequest);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
        CuerposANexo().Should().BeEmpty();
    }

    [Fact]
    public async Task UnaCeldaQueNoEsTextoEsCuerpoInvalido()
    {
        var json = await PostAsync("""{"columnas":[{"indice":0,"campo":"peso"}],"filas":[[24.5]]}""", HttpStatusCode.BadRequest);

        json.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
        CuerposANexo().Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "IA_TOPE_DIARIO", 429, "IA_TOPE_DIARIO")]
    [InlineData(HttpStatusCode.Forbidden, "SIN_PERMISO", 403, "NEXO_SIN_PERMISO")]
    [InlineData(HttpStatusCode.InternalServerError, "ERROR", 503, "NEXO_NO_DISPONIBLE")]
    [InlineData(HttpStatusCode.TooManyRequests, "DEMASIADAS_SOLICITUDES", 503, "NEXO_NO_DISPONIBLE")]
    public async Task LosErroresDeNexoSeTraducenComoEnLaImportacion(HttpStatusCode deNexo, string codigoNexo, int esperado, string codigo)
    {
        _nexo.ImportacionFalla = deNexo;
        _nexo.CodigoImportacionFalla = codigoNexo;

        var json = await PostAsync(Cuerpo(_origenDestinoTon, [["Cali", "Bogotá", "1"]]), (HttpStatusCode)esperado);

        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
    }

    [Fact]
    public async Task UnRolQueNoEsComercialEs403SinLlamarANexo()
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "cotifly-api",
            Audience = "clientepro",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["jti"] = Guid.NewGuid().ToString(), ["rol"] = "consulta" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clsApiWebPruebas.ClaveJwt)), SecurityAlgorithms.HmacSha256),
        });

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, token, Cuerpo(_origenDestinoTon, [["Cali", "Bogotá", "1"]]));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        CuerposANexo().Should().BeEmpty();
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
        _cubicador.Dispose();
    }
}
