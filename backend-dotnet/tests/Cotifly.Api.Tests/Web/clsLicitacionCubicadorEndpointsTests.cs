using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-023.5 (#160): licitación con el cubicador, por HTTP con SQL Server real, el Nexo falso y el cubicador falso. El código de mercancía
/// sugerido al analizar el Excel (parecido sin tildes ni mayúsculas, sin coincidencia y la columna de código del Excel), la cubicación de
/// las rutas en segundo plano con el límite de llamadas en paralelo (P6) y el progreso visible, una ruta que falla, dos réplicas y el
/// Excel exportado con los camiones.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsLicitacionCubicadorEndpointsTests : IDisposable
{
    private const string Ruta = "/web/licitaciones";
    private const string CodigoCafe = "CAFE_EN_ESTIBAS";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<clsApiWebPruebas> _apis = [];

    public clsLicitacionCubicadorEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso(CodigoCafe, "Café en estibas"));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("BOBINAS_DE_ACERO", "Bobinas de acero"));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("BEBIDAS", "Bebidas"));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("EXPLOSIVOS", "Explosivos", Cubicable: false));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cadena => _entorno.BaseDatos.CadenaCotifly;

    [Fact]
    public async Task ElAnalisisSugiereElCodigoMasParecidoSinTildesNiMayusculasYUsaLaColumnaDeCodigoTalCual()
    {
        _nexo.Analisis = new
        {
            archivo = "pliego.xlsx",
            hojas = new object[]
            {
                new
                {
                    nombre = "Pliego",
                    filaEncabezado = 1,
                    encabezados = new[] { "ITEM", "ORIGEN", "DESTINO", "Tipo de MERCANCÍA" },
                    filas = new[]
                    {
                        new[] { "1", "Buenaventura", "Bogotá", "CAFÉ EN ESTIBAS" },
                        new[] { "2", "Cali", "Cartagena", "cafe en estiba" },
                        new[] { "3", "Cúcuta", "Bogotá", "Tornillos galvanizados" },
                        new[] { "4", "Cali", "Bogotá", string.Empty },
                        new[] { "5", "Cali", "Bogotá", "explosivos" },
                    },
                    totalFilas = 5,
                },
                new
                {
                    nombre = "Con código",
                    filaEncabezado = 1,
                    encabezados = new[] { "ORIGEN", "DESTINO", "Descripción", "Cód. mercancía" },
                    filas = new[]
                    {
                        new[] { "Cali", "Bogotá", "Gaseosas", "BEBIDAS" },
                        new[] { "Cali", "Bogotá", "Café", "CODIGO_QUE_NO_EXISTE" },
                    },
                    totalFilas = 2,
                },
            },
        };
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, "/web/licitaciones/analizar-excel", sesion.Access,
            clsApiWebPruebas.Archivo(clsPliegoMuestra.Excel()));

        var json = await VerificarContratoAsync(respuesta, "/web/licitaciones/analizar-excel", _post, HttpStatusCode.OK);
        json.GetProperty("catalogo_mercancias_disponible").GetBoolean().Should().BeTrue();
        var pliego = json.GetProperty("hojas")[0];
        pliego.GetProperty("columna_mercancia").GetString().Should().Be("Tipo de MERCANCÍA");
        var sugeridas = pliego.GetProperty("mercancias").EnumerateArray().ToList();
        sugeridas.Select(m => m.GetProperty("fila").GetInt32()).Should().Equal([0, 1, 2, 4], "la fila sin mercancía no aparece");
        sugeridas[0].GetProperty("codigo_mercancia").GetString().Should().Be(CodigoCafe, "con tildes y mayúsculas");
        sugeridas[0].GetProperty("sugerido").GetBoolean().Should().BeTrue();
        sugeridas[0].GetProperty("origen").GetString().Should().Be("parecido");
        sugeridas[0].GetProperty("similitud").GetDecimal().Should().Be(1m);
        sugeridas[0].GetProperty("nombre_mercancia").GetString().Should().Be("Café en estibas");
        sugeridas[1].GetProperty("codigo_mercancia").GetString().Should().Be(CodigoCafe, "sin tildes, en minúsculas y en singular");
        sugeridas[1].GetProperty("similitud").GetDecimal().Should().BeGreaterThanOrEqualTo(0.5m);
        sugeridas[2].GetProperty("codigo_mercancia").ValueKind.Should().Be(JsonValueKind.Null, "nada se parece: queda vacío");
        sugeridas[2].GetProperty("sugerido").GetBoolean().Should().BeFalse();
        sugeridas[3].GetProperty("codigo_mercancia").ValueKind.Should().Be(JsonValueKind.Null, "un tipo no cubicable no se sugiere");

        var conCodigo = json.GetProperty("hojas")[1];
        conCodigo.GetProperty("columna_mercancia").GetString().Should().Be("Descripción");
        conCodigo.GetProperty("columna_codigo_mercancia").GetString().Should().Be("Cód. mercancía");
        var deExcel = conCodigo.GetProperty("mercancias").EnumerateArray().ToList();
        deExcel.Select(m => (m.GetProperty("codigo_mercancia").GetString(), m.GetProperty("sugerido").GetBoolean(), m.GetProperty("origen").GetString()))
            .Should().Equal(("BEBIDAS", false, "excel"), ("CODIGO_QUE_NO_EXISTE", false, "excel"));
        deExcel[0].GetProperty("nombre_mercancia").GetString().Should().Be("Bebidas");
    }

    [Fact]
    public async Task SinCubicadorElAnalisisNoFallaYSoloUsaLaColumnaDeCodigo()
    {
        _nexo.Analisis = new
        {
            archivo = "pliego.xlsx",
            hojas = new[]
            {
                new
                {
                    nombre = "Pliego",
                    filaEncabezado = 1,
                    encabezados = new[] { "Mercancía", "Código mercancía" },
                    filas = new[] { new[] { "Café en estibas", string.Empty }, new[] { "Bebidas", "BEBIDAS" } },
                    totalFilas = 2,
                },
            },
        };
        var api = Api(conCubicador: false);
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await api.EnviarContenidoAsync(_post, "/web/licitaciones/analizar-excel", sesion.Access,
            clsApiWebPruebas.Archivo(clsPliegoMuestra.Excel()));

        var json = await VerificarContratoAsync(respuesta, "/web/licitaciones/analizar-excel", _post, HttpStatusCode.OK);
        json.GetProperty("catalogo_mercancias_disponible").GetBoolean().Should().BeFalse();
        var mercancias = json.GetProperty("hojas")[0].GetProperty("mercancias").EnumerateArray().ToList();
        mercancias[0].GetProperty("codigo_mercancia").ValueKind.Should().Be(JsonValueKind.Null);
        mercancias[1].GetProperty("codigo_mercancia").GetString().Should().Be("BEBIDAS");
    }

    [Fact]
    public async Task DiezRutasSeCubicanEnSegundoPlanoRespetandoElParalelismoConElProgresoVisibleYDespuesSaleANexo()
    {
        _cubicador.DemoraRecomendacion = TimeSpan.FromMilliseconds(400);
        var api = Api(maximoParalelo: 3);
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(10));

        // Se guarda sin esperar al cubicador: ninguna llamada, nada a Nexo y el progreso "en curso".
        var creada = await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created);
        var folio = creada.GetProperty("folio").GetString()!;
        _cubicador.Recomendaciones.Should().BeEmpty();
        _nexo.UltimaAlta.Should().BeNull();
        creada.GetProperty("estado_costeo").GetString().Should().Be("pendiente_envio");
        Progreso(creada).Should().Be(("en_curso", 10, 0, 10, 0));
        creada.GetProperty("lineas").EnumerateArray().Should().OnlyContain(l => l.GetProperty("estado_cubicacion").GetString() == "pendiente");
        creada.GetProperty("lineas")[0].GetProperty("camiones").GetArrayLength().Should().Be(1, "un camión provisional sin vehículo");

        // El reintento manual no puede mandar a Nexo una licitación a medio cubicar.
        await clsBaseDatosPruebas.EjecutarAsync(Cadena,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET updatedAt = DATEADD(MINUTE, -5, updatedAt) WHERE folio = N'{folio}';");
        using var reintento = await api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _nexo.UltimaAlta.Should().BeNull();

        // El procesador cubica con 3 llamadas a la vez como máximo; mientras tanto el GET muestra el avance.
        var proceso = api.ProcesarCubicacionAsync(folio);
        var intermedios = new List<int>();
        while (!proceso.IsCompleted)
        {
            var vista = await LicitacionAsync(api, sesion, folio);
            intermedios.Add(Progreso(vista).Cubicadas);
            await Task.Delay(100, Ct);
        }

        (await proceso).Should().Be(10);
        _cubicador.MaximoEnParalelo.Should().BeInRange(2, 3, "Cubicador:MaximoParalelo = 3 (P6)");
        intermedios.Should().Contain(c => c > 0 && c < 10, "el progreso por ruta se ve mientras cubica");
        _cubicador.Recomendaciones.Should().HaveCount(10, "una llamada por ruta");

        var final = await LicitacionAsync(api, sesion, folio);
        Progreso(final).Should().Be(("completa", 10, 10, 0, 0));
        var linea = final.GetProperty("lineas")[0];
        linea.GetProperty("estado_cubicacion").GetString().Should().Be("cubicada");
        linea.GetProperty("resultado_cubicacion").GetString().Should().Be("ASIGNADO");
        linea.GetProperty("camiones")[0].GetProperty("tipo_vehiculo").GetString().Should().Be("3S3");
        linea.GetProperty("camiones")[0].GetProperty("porcentaje_llenado_volumen").GetDecimal().Should().Be(80m);
        final.GetProperty("cubicacion").GetArrayLength().Should().Be(10);

        // Todo o nada hacia Nexo: una sola alta, con los camiones del cubicador.
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray();
        lineas.Should().HaveCount(10);
        lineas.Should().OnlyContain(l => l!["tipoVehiculo"]!.GetValue<string>() == "3S3");
        final.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
    }

    [Fact]
    public async Task UnaRutaQueFallaDejaLaLicitacionSinEnviarConElErrorYSeAceptaConElCamionSinVehiculo()
    {
        // La ruta 2 (10 estibas de 1001 kg) la rechaza el cubicador; las demás se cubican.
        _cubicador.ErrorPara = p => clsCubicadorFalso.PesoTotal(p) == 10 * 1001m
            ? (HttpStatusCode.BadRequest, "DIMENSIONES_INVALIDAS")
            : null;
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(3));
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        await api.ProcesarCubicacionAsync(folio);

        var vista = await LicitacionAsync(api, sesion, folio);
        Progreso(vista).Should().Be(("con_fallas", 3, 2, 0, 1));
        var fallida = vista.GetProperty("lineas")[1];
        fallida.GetProperty("estado_cubicacion").GetString().Should().Be("fallida");
        fallida.GetProperty("error_cubicacion").GetString().Should().StartWith("DIMENSIONES_INVALIDAS");
        _nexo.UltimaAlta.Should().BeNull("una ruta fallida detiene el envío de toda la licitación");
        _cubicador.Recomendaciones.Should().HaveCount(3, "un rechazo no se reintenta solo");

        // Reintentar sin respaldo: vuelve a la cola y vuelve a fallar.
        using var otraVez = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, "{}");
        Progreso(await VerificarContratoAsync(otraVez, "/web/licitaciones/{folio}/cubicacion/reintentar", _post, HttpStatusCode.OK))
            .Should().Be(("en_curso", 3, 2, 1, 0));
        await api.ProcesarCubicacionAsync(folio);
        Progreso(await LicitacionAsync(api, sesion, folio)).Fallidas.Should().Be(1);

        // Aceptar la ruta con el camión sin vehículo: la licitación sale completa a Nexo.
        using var respaldo = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, """{"usar_respaldo":true}""");
        var aceptada = await VerificarContratoAsync(respaldo, "/web/licitaciones/{folio}/cubicacion/reintentar", _post, HttpStatusCode.OK);
        Progreso(aceptada).Should().Be(("completa", 3, 3, 0, 0));
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!["tipoVehiculo"]?.GetValue<string>()).Should().Equal("3S3", null, "3S3");

        // Sin fallidas, otro reintento es 409.
        using var sinFallidas = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, "{}");
        sinFallidas.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(sinFallidas, "ESTADO_NO_PERMITE");
    }

    [Fact]
    public async Task UnaCaidaSinRespaldoSeReintentaConEsperaYAlAgotarLosIntentosQuedaFallida()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        var api = Api(extra: new() { ["Cubicador:RespaldoSiFalla"] = "false", ["Cubicador:MaximoIntentos"] = "2" });
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(1));
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        await api.ProcesarCubicacionAsync(folio);

        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT CONCAT(l.estadoCubicacion, '|', l.cubicacionIntentos, '|', IIF(l.cubicacionDisponibleAt > SYSDATETIMEOFFSET(), 'espera', 'ya')) "
            + $"FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = N'{folio}'"))
            .Should().Be("pendiente|1|espera", "una caída se reintenta más tarde");

        await clsBaseDatosPruebas.EjecutarAsync(Cadena,
            "UPDATE l SET cubicacionDisponibleAt = DATEADD(MINUTE, -1, SYSDATETIMEOFFSET()) FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = N'{folio}';");
        await api.ProcesarCubicacionAsync(folio);

        var vista = await LicitacionAsync(api, sesion, folio);
        vista.GetProperty("lineas")[0].GetProperty("estado_cubicacion").GetString().Should().Be("fallida");
        vista.GetProperty("lineas")[0].GetProperty("error_cubicacion").GetString().Should().StartWith("CUBICADOR_NO_DISPONIBLE");
        _nexo.UltimaAlta.Should().BeNull();
    }

    [Fact]
    public async Task DosReplicasNoRepitenNingunaRutaNiPasanElLimiteGlobalYEnvianANexoUnaSolaVez()
    {
        _cubicador.DemoraRecomendacion = TimeSpan.FromMilliseconds(300);
        var replica1 = Api(maximoParalelo: 2);
        var replica2 = Api(maximoParalelo: 2);
        var sesion = await replica1.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await replica1.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(8));
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        await Task.WhenAll(replica1.ProcesarCubicacionAsync(folio, vueltas: 40), replica2.ProcesarCubicacionAsync(folio, vueltas: 40));

        _cubicador.Recomendaciones.Select(r => r["idLineaSolicitud"]!.GetValue<string>()).Should().OnlyHaveUniqueItems().And.HaveCount(8);
        _cubicador.MaximoEnParalelo.Should().BeLessThanOrEqualTo(2, "el límite de llamadas es entre todas las réplicas (cupos en la base)");
        Progreso(await LicitacionAsync(replica2, sesion, folio)).Should().Be(("completa", 8, 8, 0, 0));
        _nexo.SolicitudesCreadas.Should().ContainSingle("de las dos réplicas solo una envía la licitación");
    }

    [Fact]
    public async Task ElExcelExportadoMuestraLosCamionesDeCadaRuta()
    {
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 10 * 1000m
            ? clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 6_000m, 30m, 81.25m, 60m), clsCubicadorFalso.Vehiculo("C2", "ESTACAS", 4_010m))
            : clsCubicadorFalso.SinSeleccion("SUPERA_K_MAX");
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(2));
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await api.ProcesarCubicacionAsync(folio);

        using var respuesta = await api.EnviarAsync(_get, $"{Ruta}/{folio}/excel", sesion.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(await respuesta.Content.ReadAsByteArrayAsync(Ct)));
        var hoja = libro.Worksheet("Licitación");
        var columna = hoja.Row(1).CellsUsed().Single(c => c.GetString() == "Camiones Cotifly").Address.ColumnNumber;
        hoja.Cell(2, columna).GetString().Should().Contain("1. Tractocamión 3S3 / Furgón · 6.000 kg · 81,25 % vol.")
            .And.Contain("2. Sencillo C2 / Estacas");
        hoja.Cell(3, columna).GetString().Should().Contain("Sin vehículo (Mesa de Costeo)");
        var camiones = libro.Worksheet("Camiones Cotifly");
        camiones.RowsUsed().Count().Should().Be(4, "encabezado + dos camiones de la ruta 1 + uno de la ruta 2");
        camiones.Cell(2, 4).GetString().Should().Be("Tractocamión 3S3");
        camiones.Cell(2, 8).GetValue<decimal>().Should().Be(81.25m);
    }

    private static (string Estado, int Total, int Cubicadas, int Pendientes, int Fallidas) Progreso(JsonElement licitacion)
    {
        var p = licitacion.GetProperty("progreso_cubicacion");
        return (p.GetProperty("estado").GetString()!, p.GetProperty("total").GetInt32(), p.GetProperty("cubicadas").GetInt32(),
            p.GetProperty("pendientes").GetInt32(), p.GetProperty("fallidas").GetInt32());
    }

    private static async Task<JsonElement> LicitacionAsync(clsApiWebPruebas api, clsSesionPrueba sesion, string folio)
    {
        using var respuesta = await api.EnviarAsync(_get, $"{Ruta}/{folio}", sesion.Access);
        return await VerificarContratoAsync(respuesta, "/web/licitaciones/{folio}", _get, HttpStatusCode.OK);
    }

    /// <summary>Licitación de <paramref name="rutas"/> filas; la fila <c>i</c> lleva 10 estibas de café de <c>1000 + i</c> kg.</summary>
    private static string Licitacion(int rutas) =>
        JsonSerializer.Serialize(new
        {
            nit_cliente = clsCosteoEndpointsTests.Nit,
            cliente_nombre = "Longyin",
            linea_negocio = "Nacional",
            lineas = Enumerable.Range(0, rutas).Select(i => new
            {
                fila = i,
                origen = "BUENAVENTURA",
                destino = "BOGOTA, D.C.",
                tipologia = "CargaSeca",
                peso_kg = 10 * (1000 + i),
                mercancias = new[]
                {
                    new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Café", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1000 + i },
                },
            }).ToArray(),
        });

    private clsApiWebPruebas Api(bool conCubicador = true, int maximoParalelo = 4, Dictionary<string, string?>? extra = null)
    {
        var valores = new Dictionary<string, string?> { ["Cubicador:MaximoParalelo"] = maximoParalelo.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
        {
            valores[clave] = valor;
        }

        var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, valores, cubicador: conCubicador ? _cubicador : null);
        _apis.Add(api);
        return api;
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
