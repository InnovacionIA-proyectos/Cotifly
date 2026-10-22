using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// HU-030.1 (#184, validación): una fila de contenedor con <c>peso_con_tara_kg</c> se guarda sin cubicar (camión provisional) y la tara
    /// se aplica DESPUÉS de la cubicación en segundo plano: cada línea lleva el peso de su camión más su parte de la tara, sin posiciones,
    /// y al cubicador va el peso por mercancía (sin tara).
    /// </summary>
    [Fact]
    public async Task HU0301_UnaFilaDeContenedorConTaraSeCubicaEnSegundoPlanoYSaleANexoConLaTaraRepartidaSinPosiciones()
    {
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 6_000m), clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 4_000m));
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = clsCosteoEndpointsTests.Nit,
            cliente_nombre = "Longyin",
            linea_negocio = "Importacion",
            lineas = new[]
            {
                new
                {
                    fila = 0,
                    origen = "BUENAVENTURA",
                    destino = "BOGOTA, D.C.",
                    tipologia = "Contenedor",
                    clase_viaje = "Contenedor",
                    clase_contenedor = "CNT40.HC",
                    peso_kg = 10_000m,
                    peso_con_tara_kg = 17_600m,
                    mercancias = new[]
                    {
                        new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Café", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1_000 },
                    },
                },
            },
        });

        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, cuerpo);
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        _nexo.UltimaAlta.Should().BeNull("se envía después de cubicar");

        (await api.ProcesarCubicacionAsync(folio)).Should().Be(1);

        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => l["peso"]!.GetValue<decimal>()).Should().Equal(9_800m, 7_800m);
        lineas.Should().OnlyContain(l => l["posiciones"] == null);
        _nexo.AltasPesoInconsistente.Should().Be(0);
        _cubicador.Recomendaciones.Should().ContainSingle().Which.ToJsonString().Should().NotContain("17600");
        (await clsBaseDatosPruebas.EscalarAsync<decimal?>(Cadena, "SELECT l.pesoConTaraKg FROM dbo_cotifly.CotizacionLineasSolicitud l "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = N'{folio}'")).Should().Be(17_600m);
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

        // Un rechazo de la petición NO se acepta con el camión sin vehículo (escondería un dato malo).
        using var respaldo = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, """{"usar_respaldo":true}""");
        respaldo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(respaldo, "ESTADO_NO_PERMITE");
        _nexo.UltimaAlta.Should().BeNull();

        // Corregido el motivo del rechazo, el reintento normal la cubica y la licitación sale completa a Nexo.
        _cubicador.ErrorPara = null;
        using var corregida = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, "{}");
        corregida.StatusCode.Should().Be(HttpStatusCode.OK);
        await api.ProcesarCubicacionAsync(folio);
        Progreso(await LicitacionAsync(api, sesion, folio)).Should().Be(("completa", 3, 3, 0, 0));
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!["tipoVehiculo"]?.GetValue<string>()).Should().Equal("3S3", "3S3", "3S3");

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

        // Una caída sí se puede aceptar con el camión sin vehículo: la ruta queda cubicada, sin error, y la licitación sale a Nexo.
        using var respaldo = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, """{"usar_respaldo":true}""");
        var aceptada = await VerificarContratoAsync(respaldo, "/web/licitaciones/{folio}/cubicacion/reintentar", _post, HttpStatusCode.OK);
        Progreso(aceptada).Should().Be(("completa", 1, 1, 0, 0));
        aceptada.GetProperty("lineas")[0].GetProperty("error_cubicacion").ValueKind.Should().Be(JsonValueKind.Null);
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        _nexo.UltimaAlta!["lineas"]!.AsArray().Should().ContainSingle().Which!["tipoVehiculo"].Should().BeNull();
    }

    /// <summary>
    /// Endurecimiento B (#199, HU-032, ADR-022): <c>usar_respaldo = true</c> sobre una ruta caída aplica el vehículo ELEGIDO de la ruta, como
    /// el camino en segundo plano: válido en Nexo → el camión lo lleva con <c>VEHICULO_ELEGIDO_USADO</c>; inválido → sin vehículo con
    /// <c>VEHICULO_ELEGIDO_INVALIDO</c>. Antes aceptaba el camión provisional sin vehículo. Sin volver a llamar al cubicador.
    /// </summary>
    [Theory]
    [InlineData("3S3", "3S3", "VEHICULO_ELEGIDO_USADO")]
    [InlineData("XYZ", null, "VEHICULO_ELEGIDO_INVALIDO")]
    public async Task ConRespaldoLaCaidaUsaElVehiculoElegidoDeLaRuta(string elegido, string? final, string aviso)
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        var api = Api(extra: new() { ["Cubicador:RespaldoSiFalla"] = "false", ["Cubicador:MaximoIntentos"] = "1" });
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = JsonNode.Parse(Licitacion(1))!;
        cuerpo["lineas"]![0]!["tipo_vehiculo"] = elegido;
        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, cuerpo.ToJsonString());
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await api.ProcesarCubicacionAsync(folio);
        (await LicitacionAsync(api, sesion, folio)).GetProperty("lineas")[0].GetProperty("estado_cubicacion").GetString().Should().Be("fallida");
        var llamadas = _cubicador.Recomendaciones.Count;

        using var respaldo = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, """{"usar_respaldo":true}""");

        Progreso(await VerificarContratoAsync(respaldo, "/web/licitaciones/{folio}/cubicacion/reintentar", _post, HttpStatusCode.OK))
            .Should().Be(("completa", 1, 1, 0, 0));
        _cubicador.Recomendaciones.Should().HaveCount(llamadas, "el respaldo no vuelve a llamar al cubicador");
        _nexo.UltimaAlta!["lineas"]!.AsArray().Should().ContainSingle().Which!["tipoVehiculo"]?.GetValue<string>().Should().Be(final);
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK)).GetProperty("rutas")[0]
            .GetProperty("avisos_cubicacion").GetProperty("advertencias").EnumerateArray().Select(a => a.GetProperty("codigo").GetString())
            .Should().Equal(aviso);
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

        await Task.WhenAll(replica1.ProcesarCubicacionAsync(folio), replica2.ProcesarCubicacionAsync(folio));

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

    [Fact]
    public async Task LosConexosQuedanEnLaRutaAlReemplazarElCamionProvisionalYElEnvioDiferidoVaAlPrimerCamionReal()
    {
        const string Cargue = "SC-CARGUE-LIC";
        _nexo.ConServicioConexo(Cargue, Cargue, "Cargue en puerto", "logistico", "UND", 150_000m);
        _cubicador.RecomendarSiempre(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 6_000m), clsCubicadorFalso.Vehiculo("C2", "ESTACAS", 4_000m));
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cuerpo = JsonNode.Parse(Licitacion(1))!;
        cuerpo["lineas"]![0]!["servicios_conexos"] = new JsonArray(new JsonObject { ["id_servicio_conexo"] = Cargue, ["cantidad"] = 1 });
        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, cuerpo.ToJsonString());
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var provisional = await clsBaseDatosPruebas.EscalarAsync<Guid>(Cadena,
            "SELECT v.id FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = N'{folio}'");

        await api.ProcesarCubicacionAsync(folio);

        // El camión provisional ya no existe; los dos reales quedan en la ruta y el conexo sigue colgado de la ruta (no de un camión).
        var deLaRuta = "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = N'{folio}'";
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena, $"SELECT COUNT(*) {deLaRuta} AND v.id = '{provisional}'")).Should().Be(0);
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena, $"SELECT COUNT(*) {deLaRuta} AND v.idRecomendacionVehiculo IS NOT NULL")).Should().Be(2);
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = N'{folio}'")).Should().Be(1);

        // Nexo costea los dos camiones; el envío diferido (decisión 10) manda el conexo a la línea del primer camión real (fila 1).
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => l["peso"]!.GetValue<decimal>()).Should().Equal(6_000m, 4_000m);
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 1, 800_000m);
        await api.ProcesarPollingAsync(folio);
        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["servicioConexoId"]!.GetValue<string>().Should().Be(Cargue);
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, 0));
    }

    [Fact]
    public async Task ExportarCotizacionSirveParaCualquierCotizacionPropiaYExportarLicitacionNo()
    {
        // HU-018.1, parte C ("Ver detalle") reusa la exportación de la licitación con cualquier cotización con rutas.
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(sesion.UsuarioId, clsCosteoEndpointsTests.Nit, conPdf: false);
        await using var ambito = api.Factory.Services.CreateAsyncScope();
        var servicio = ambito.ServiceProvider.GetRequiredService<Cotifly.Aplicacion.Licitaciones.Services.IClsLicitacionService>();

        var detalle = await servicio.ExportarCotizacionAsync(sesion.UsuarioId, cotizacion.Folio, Ct);
        var licitacion = await servicio.ExportarAsync(sesion.UsuarioId, cotizacion.Folio, Ct);
        var ajena = await servicio.ExportarCotizacionAsync(Guid.NewGuid(), cotizacion.Folio, Ct);

        detalle.EsExitoso.Should().BeTrue();
        detalle.Valor.Nombre.Should().Be(cotizacion.Folio + ".xlsx");
        using var libro = new ClosedXML.Excel.XLWorkbook(new MemoryStream(detalle.Valor.Contenido));
        libro.Worksheets.Select(h => h.Name).Should().Contain(["Licitación", "Resumen Cotifly", "Camiones Cotifly"]);
        libro.Worksheet("Licitación").Row(1).CellsUsed().Select(c => c.GetString()).Should().Contain("Camiones Cotifly");
        licitacion.EsFallo.Should().BeTrue("GET /web/licitaciones/{folio}/excel sigue siendo solo de licitaciones");
        licitacion.Error.Codigo.Should().Be("COTIZACION_NO_ENCONTRADA");
        ajena.Error.Codigo.Should().Be("COTIZACION_NO_ENCONTRADA");
    }

    [Fact]
    public async Task UnaCaidaConElRespaldoPorDefectoDejaLaRutaCubicadaConCamionSinVehiculoYSaleANexo()
    {
        // Del validador (V05): con Cubicador:RespaldoSiFalla (por defecto) la caída no llega a la cola de reintentos.
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(2));
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        await api.ProcesarCubicacionAsync(folio);

        Progreso(await LicitacionAsync(api, sesion, folio)).Estado.Should().Be("completa");
        _nexo.SolicitudesCreadas.Should().ContainSingle();
    }

    [Fact]
    public async Task LaRecuperacionDeDosReplicasALaVezEnviaANexoUnaSolaVez()
    {
        // Del validador (V06): licitación ya cubicada que no salió (proceso cortado) y seis vueltas concurrentes en dos réplicas.
        var r1 = Api();
        var r2 = Api();
        var sesion = await r1.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var envio = await r1.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(3));
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var id = await r1.IdCotizacionAsync(folio);
        await clsBaseDatosPruebas.EjecutarAsync(Cadena,
            $"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET estadoCubicacion = N'cubicada' WHERE cotizacionSolicitudId = '{id}';"
            + $"UPDATE dbo_cotifly.CotizacionesSolicitud SET updatedAt = DATEADD(MINUTE, -5, updatedAt) WHERE id = '{id}';");

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => Lote(i % 2 == 0 ? r1 : r2, id), Ct)));

        _nexo.SolicitudesCreadas.Should().ContainSingle();
    }

    [Fact]
    public async Task SinCubicadorOConElSegundoPlanoApagadoLaLicitacionSaleDeUnaVezSinCola()
    {
        // Del validador (V07 y V08).
        var sinCubicador = Api(conCubicador: false);
        var sesion = await sinCubicador.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var primera = await sinCubicador.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(3));
        var creada = await VerificarContratoAsync(primera, Ruta, _post, HttpStatusCode.Created);
        creada.GetProperty("lineas").EnumerateArray().Should().OnlyContain(l => l.GetProperty("estado_cubicacion").ValueKind == JsonValueKind.Null);
        creada.GetProperty("progreso_cubicacion").ValueKind.Should().Be(JsonValueKind.Null);
        _nexo.SolicitudesCreadas.Should().ContainSingle("sale de una vez");

        var sincrona = Api(extra: new() { ["Cubicador:LicitacionEnSegundoPlano"] = "false" });
        using var segunda = await sincrona.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(2));
        var otra = await VerificarContratoAsync(segunda, Ruta, _post, HttpStatusCode.Created);
        otra.GetProperty("lineas").EnumerateArray().Should().OnlyContain(l => l.GetProperty("estado_cubicacion").ValueKind == JsonValueKind.Null);
        _cubicador.Recomendaciones.Should().HaveCount(2, "HU-023.3: las rutas se cubican mientras el comercial espera");
        _nexo.SolicitudesCreadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task ReintentarUnFolioAjenoEs404YUnaRutaPendienteNoSeAceptaConRespaldo()
    {
        // Del validador (V09).
        var api = Api();
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var ajeno = await api.EnviarAsync(_post, $"{Ruta}/LIC-NO-EXISTE/cubicacion/reintentar", sesion.Access, "{}");
        ajeno.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var envio = await api.EnviarAsync(_post, Ruta, sesion.Access, Licitacion(1));
        var folio = (await VerificarContratoAsync(envio, Ruta, _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;

        using var pendiente = await api.EnviarAsync(_post, $"{Ruta}/{folio}/cubicacion/reintentar", sesion.Access, """{"usar_respaldo":true}""");

        pendiente.StatusCode.Should().Be(HttpStatusCode.Conflict, "una ruta pendiente (no fallida) no se acepta con respaldo");
        _nexo.SolicitudesCreadas.Should().BeEmpty();
    }

    private static async Task<int> Lote(clsApiWebPruebas api, Guid id)
    {
        var ambitos = api.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var logger = api.Factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("pruebas");
        return await Cotifly.Infraestructura.Cubicacion.Services.clsWorkerCubicacionLicitacion.ProcesarLoteAsync(ambitos, logger, id, CancellationToken.None);
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
        // Integración final HU-023: desde #159 los códigos del cubicador (tipoVehiculo/carroceria) viajan a Nexo solo con
        // Nexo:EnviarCodigosCubicador (contrato de Nexo #297). Estas pruebas verifican que el alta lleva los camiones del cubicador.
        var valores = new Dictionary<string, string?>
        {
            ["Cubicador:MaximoParalelo"] = maximoParalelo.ToString(System.Globalization.CultureInfo.InvariantCulture),
            // Opción C: el paralelo de la licitación no puede superar el cupo global del proceso.
            ["Cubicador:MaximoConcurrente"] = maximoParalelo.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Nexo:EnviarCodigosCubicador"] = "true",
        };
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
