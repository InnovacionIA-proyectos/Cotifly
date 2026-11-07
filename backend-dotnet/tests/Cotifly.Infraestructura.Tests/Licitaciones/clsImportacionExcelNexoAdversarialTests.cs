using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Pruebas adversariales del adaptador de importación inteligente (validación de #252, HU-044.1): cuántas veces se llama a NEXO ante cada
/// fallo, tiempos por endpoint con la tubería real (acortada), respuestas raras de NEXO, NIT y receta como texto opaco, nada del Excel ni
/// de la identidad en los logs, archivos grandes y nombres raros. Todo contra un NEXO grabador en memoria (sin red). Las marcadas con
/// <c>Skip</c> reproducen un defecto abierto (ver el informe del validador) y afirman el comportamiento esperado.
/// </summary>
public sealed class clsImportacionExcelNexoAdversarialTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();
    private static readonly Guid _otroUsuarioId = Guid.NewGuid();
    private static readonly Guid _usuarioInexistente = Guid.NewGuid();
    private const long IdExterno = 4242424242;
    private const long OtroIdExterno = 5151;

    /// <summary>Nombre de la tubería estándar del cliente (<c>{nombre}-standard</c>), para acortar sus tiempos en las pruebas.</summary>
    private const string Tuberia = clsImportacionExcelNexo.NombreCliente + "-standard";

    private static readonly TimeSpan _tiempoCorto = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan _tiempoLecturaCorto = TimeSpan.FromMilliseconds(1500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly clsPeticionSugerirMapeo _sugerir = new("900.123.456-7", ["ORIGEN", "DESTINO"], [["Cali", "Bogotá"]]);

    private static readonly clsPeticionAplicarMapeo _aplicar =
        new("900.123.456-7", [new(0, "origen", null), new(1, "destino", "mayusculas")], [["Cali", "Bogotá"], ["Buga", "Tunja"]], 3);

    private static clsArchivoImportacion Archivo(string nombre = "a.xlsx") => new(nombre, [0x50, 0x4B, 3, 4]);

    private const string SugerenciaJson =
        """{"iaUsada":true,"motivoSinIa":null,"columnas":[{"indice":0,"columna":"ORIGEN","campo":"origen","transformacion":"ninguna","confianza":0.9,"origen":"ia","nota":null}],"dudas":[],"plantillaId":null}""";

    private const string LecturaJson =
        """{"archivo":"a.xlsx","origenReceta":"ia","similitud":null,"recetaGuardadaId":null,"iaUsada":true,"motivoSinIa":null,"receta":{"version":1},"resumen":null,"dudas":[],"hojasDelArchivo":[],"tablas":[],"avisos":[]}""";

    private static string RecetaGuardadaJson() =>
        $$"""{"id":"{{Guid.NewGuid()}}","clienteId":"{{Guid.NewGuid()}}","clienteNombre":"EPM","nombre":null,"receta":{"version":1},"instrucciones":null,"usos":0,"ultimoUsoAt":null,"createdAt":"2026-11-05T15:00:00Z","updatedAt":null}""";

    private static string PlantillaJson() =>
        $$"""{"id":"{{Guid.NewGuid()}}","clienteId":"{{Guid.NewGuid()}}","clienteNombre":"EPM","nombre":"x","encabezados":["ORIGEN"],"columnas":[{"indice":0,"campo":"origen","transformacion":null}],"createdAt":"2026-11-05T15:00:00Z","updatedAt":null}""";

    // --- Reintentos: exactamente uno en los GET y en aplicar-mapeo, ninguno en lo demás ---

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task UnGetSeReintentaUnaSolaVezAnteUnFalloTransitorio(int estado)
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta((HttpStatusCode)estado, """{"code":"INTERNAL_ERROR","message":"x"}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var plantillas = await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct);

        nexo.Llamadas.Should().HaveCount(2, "NEXO admite un solo reintento de los GET");
        plantillas.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    [Fact]
    public async Task UnCuatroCeroOchoEnUnGetSeReintentaUnaVez()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.RequestTimeout, string.Empty));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var plantillas = await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct);

        plantillas.EsFallo.Should().BeTrue();
        nexo.Llamadas.Should().HaveCount(2, "la tubería trata el 408 como transitorio");
    }

    [Fact(Skip = "Defecto D5 del validador de #252 (bajo): un 408 de NEXO (o de un proxy) se reintenta como transitorio pero termina como COSTEO_NEXO_RECHAZADA en vez de NEXO_NO_DISPONIBLE.")]
    public async Task UnCuatroCeroOchoEsNoDisponible()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.RequestTimeout, string.Empty));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var sugerencia = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        sugerencia.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible, "un 408 es un tiempo agotado, no un rechazo de los datos");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(413)]
    [InlineData(429)]
    public async Task UnGetNoSeReintentaAnteUnRechazo(int estado)
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta((HttpStatusCode)estado, """{"code":"DEMASIADAS_SOLICITUDES","message":"x"}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var recetas = await importacion.ListarRecetasLecturaAsync(_usuarioId, "900123456", Ct);

        recetas.EsFallo.Should().BeTrue();
        nexo.Llamadas.Should().ContainSingle("un 4xx (ni un 429) no se reintenta");
    }

    [Fact]
    public async Task UnFalloDeRedEnUnGetSeReintentaUnaVezYElSegundoIntentoGana()
    {
        var nexo = new clsNexoGrabador((llamada, _) => llamada.Numero == 1
            ? throw new HttpRequestException("conexión reiniciada")
            : Respuesta(HttpStatusCode.OK, """{"tipos":["columna"],"camposDestino":[],"unidades":[],"contextos":[]}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var opciones = await importacion.ObtenerOpcionesDiccionarioAsync(_usuarioId, Ct);

        opciones.Valor.Tipos.Should().Equal("columna");
        nexo.Llamadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task UnGetQueFallaSiempreSeIntentaSoloDosVeces()
    {
        var nexo = new clsNexoGrabador((_, _) => throw new HttpRequestException("caído"));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var diccionario = await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, "900123456", null, Ct);

        diccionario.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        diccionario.Error.Detalle.Should().Contain("error de red");
        nexo.Llamadas.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoQueGastaIaLasEscriturasYLosDeleteNoSeReintentan(bool fallaDeRed)
    {
        var nexo = new clsNexoGrabador((_, _) => fallaDeRed
            ? throw new HttpRequestException("caído")
            : Respuesta(HttpStatusCode.ServiceUnavailable, """{"code":"X","message":"x"}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        IReadOnlyList<clsErrorDominio> errores =
        [
            (await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct)).Error,
            (await importacion.NormalizarValoresAsync(_usuarioId, "900123456", [new("ciudad", "Medelin")], Ct)).Error,
            (await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), "900123456", "corrige", null), Ct)).Error,
            (await importacion.GuardarRecetaLecturaAsync(_usuarioId, new clsPeticionGuardarReceta(Archivo(), "{}", "900123456", null, null), Ct)).Error,
            (await importacion.GuardarPlantillaMapeoAsync(_usuarioId,
                new clsPeticionPlantillaMapeo("900123456", null, ["ORIGEN"], [new(0, "origen", null)]), Ct)).Error,
            (await importacion.EliminarPlantillaMapeoAsync(_usuarioId, Guid.NewGuid(), Ct)).Error,
            (await importacion.EliminarRecetaLecturaAsync(_usuarioId, Guid.NewGuid(), Ct)).Error,
        ];

        errores.Should().AllSatisfy(e => e.Codigo.Should().Be(clsCodigosError.NexoNoDisponible));
        nexo.Llamadas.Should().HaveCount(7, "una sola petición por operación");
        nexo.Llamadas.Select(l => l.Metodo).Should().NotContain(HttpMethod.Get);
    }

    [Fact]
    public async Task AplicarMapeoReenviaElMismoCuerpoEnElReintento()
    {
        var nexo = new clsNexoGrabador((llamada, _) => llamada.Numero == 1
            ? Respuesta(HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>")
            : Respuesta(HttpStatusCode.OK, """{"totalFilas":2,"filasVacias":0,"filasConErrores":0,"lineas":[],"errores":[],"totalErrores":0}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var aplicar = await importacion.AplicarMapeoAsync(_usuarioId, _aplicar, Ct);

        aplicar.Valor.TotalFilas.Should().Be(2);
        nexo.Llamadas.Should().HaveCount(2);
        nexo.Llamadas[1].Cuerpo.Should().Be(nexo.Llamadas[0].Cuerpo).And.NotBeNullOrEmpty();
        var cuerpo = JsonNode.Parse(nexo.Llamadas[1].Cuerpo!)!;
        cuerpo["primeraFila"]!.GetValue<int>().Should().Be(3);
        cuerpo["filas"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task AplicarMapeoNoSeReintentaAnteUnRechazoDeNegocio()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.BadRequest, """{"code":"CAMPO_INVALIDO","message":"campo raro"}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var aplicar = await importacion.AplicarMapeoAsync(_usuarioId, _aplicar, Ct);

        aplicar.Error.Codigo.Should().Be(clsCodigosError.CampoInvalido);
        aplicar.Error.Mensaje.Should().Be("campo raro");
        nexo.Llamadas.Should().ContainSingle();
    }

    // --- Tiempos (tubería real con tiempos acortados: 400 ms por intento y 1,5 s en la lectura) ---

    [Fact]
    public async Task UnGetLentoAgotaElTiempoEnCadaIntentoYSeReintentaUnaVez()
    {
        var nexo = new clsNexoGrabador(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return await Respuesta(HttpStatusCode.OK, "[]");
        });
        var (importacion, proveedor) = Crear(nexo, tiemposCortos: true);
        using var limpieza = proveedor;

        var plantillas = await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct);

        plantillas.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        plantillas.Error.Detalle.Should().Contain("tiempo agotado");
        nexo.Llamadas.Should().HaveCount(2);
    }

    [Fact]
    public async Task SugerirLentoAgotaElTiempoSinReintento()
    {
        var nexo = new clsNexoGrabador(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return await Respuesta(HttpStatusCode.OK, SugerenciaJson);
        });
        var (importacion, proveedor) = Crear(nexo, tiemposCortos: true);
        using var limpieza = proveedor;

        var sugerencia = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        sugerencia.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        sugerencia.Error.Detalle.Should().Contain("tiempo agotado");
        nexo.Llamadas.Should().ContainSingle();
    }

    [Fact]
    public async Task LaLecturaYGuardarLaRecetaTienenElTiempoLargoYLaPlantillaNo()
    {
        var nexo = new clsNexoGrabador(async (llamada, ct) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(900), ct); // más que el intento normal, menos que el de lectura
            return await (llamada.Url.AbsolutePath switch
            {
                clsImportacionExcelNexo.RutaLeerEstructura => Respuesta(HttpStatusCode.OK, LecturaJson),
                clsImportacionExcelNexo.RutaRecetasLectura => Respuesta(HttpStatusCode.Created, RecetaGuardadaJson()),
                _ => Respuesta(HttpStatusCode.Created, PlantillaJson()),
            });
        });
        var (importacion, proveedor) = Crear(nexo, tiemposCortos: true);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), null, null, null), Ct);
        var receta = await importacion.GuardarRecetaLecturaAsync(_usuarioId, new clsPeticionGuardarReceta(Archivo(), "{}", "900123456", null, null), Ct);
        var plantilla = await importacion.GuardarPlantillaMapeoAsync(_usuarioId,
            new clsPeticionPlantillaMapeo("900123456", null, ["ORIGEN"], [new(0, "origen", null)]), Ct);

        lectura.EsExitoso.Should().BeTrue("leer-estructura usa TimeoutLecturaSegundos");
        receta.EsExitoso.Should().BeTrue("PUT recetas-lectura usa TimeoutLecturaSegundos");
        receta.Valor.Creada.Should().BeTrue();
        plantilla.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible, "PUT plantillas-mapeo usa el tiempo normal");
        plantilla.Error.Detalle.Should().Contain("tiempo agotado");
        nexo.Llamadas.Should().HaveCount(3, "ninguna se reintenta");
    }

    [Fact]
    public async Task LaLecturaTambienTieneTopeYNoSeReintenta()
    {
        var nexo = new clsNexoGrabador(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return await Respuesta(HttpStatusCode.OK, LecturaJson);
        });
        var (importacion, proveedor) = Crear(nexo, tiemposCortos: true);
        using var limpieza = proveedor;
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), null, "corrige", null), Ct);

        reloj.Stop();
        lectura.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        lectura.Error.Detalle.Should().Contain("tiempo agotado");
        nexo.Llamadas.Should().ContainSingle();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "se corta en el tiempo de lectura, no espera a NEXO");
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorSePropagaYNoEsNoDisponible()
    {
        var nexo = new clsNexoGrabador(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return await Respuesta(HttpStatusCode.OK, "[]");
        });
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelacion.CancelAfter(TimeSpan.FromMilliseconds(200));

        var listar = () => importacion.ListarRecetasLecturaAsync(_usuarioId, "900123456", cancelacion.Token);

        await listar.Should().ThrowAsync<OperationCanceledException>();
        nexo.Llamadas.Should().ContainSingle("tras la cancelación del llamador no hay reintento");
    }

    [Fact]
    public async Task UnTokenYaCanceladoNoSeConvierteEnNoDisponible()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK, SugerenciaJson));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var sugerir = () => importacion.SugerirMapeoAsync(_usuarioId, _sugerir, new CancellationToken(canceled: true));

        await sugerir.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact(Skip = "Defecto D2 del validador de #252: el cuerpo de la respuesta se lee fuera de la tubería (ResponseHeadersRead) y el HttpClient no tiene timeout: un cuerpo que se queda colgado espera sin límite.")]
    public async Task UnCuerpoQueSeQuedaColgadoTambienAgotaElTiempo()
    {
        var nexo = new clsNexoGrabador((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new clsFlujoColgado()),
        }));
        var (importacion, proveedor) = Crear(nexo, tiemposCortos: true);
        using var limpieza = proveedor;
        using var cancelacion = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancelacion.CancelAfter(TimeSpan.FromSeconds(6));

        var resultado = await importacion.ObtenerOpcionesDiccionarioAsync(_usuarioId, cancelacion.Token);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible, "el tiempo de NEXO debe cubrir también el cuerpo");
    }

    // --- Respuestas raras de NEXO ---

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("   ")]
    [InlineData("<!DOCTYPE html><html><body>Proxy error</body></html>")]
    [InlineData("""{"iaUsada":true,"columnas":[""")]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("""{"columnas":{"a":1}}""")]
    [InlineData("""{"columnas":[{"indice":"cero"}]}""")]
    [InlineData("""{"iaUsada":"si"}""")]
    public async Task UnCuerpoDeExitoIlegibleEsNoDisponible(string cuerpo)
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK, cuerpo, cuerpo.StartsWith('<') ? "text/html" : "application/json"));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var sugerencia = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        sugerencia.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        sugerencia.Error.Mensaje.Should().Be(clsImportacionExcelNexo.MensajeNoDisponible);
    }

    [Theory]
    [InlineData("""{"items":[]}""")]
    [InlineData("""[{"id":"no-es-guid"}]""")]
    [InlineData("""[{"id":"7d0c9a4e-1b7f-4bb1-9a59-2f8a3e1c5d10","clienteId":"7d0c9a4e-1b7f-4bb1-9a59-2f8a3e1c5d11","usos":0,"createdAt":"2026-11-05T15:00:00Z"}]""")]
    public async Task UnaListaDeRecetasConOtraFormaEsNoDisponible(string cuerpo)
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK, cuerpo));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var recetas = await importacion.ListarRecetasLecturaAsync(_usuarioId, "900123456", Ct);

        recetas.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible, "una receta sin el campo receta no se puede devolver");
    }

    [Fact]
    public async Task LosCamposExtraDeNexoSeIgnoranYLosNulosNoRompen()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK,
            """{"iaUsada":true,"motivoSinIa":null,"version":"2.0","columnas":[{"indice":0,"columna":null,"campo":null,"transformacion":null,"confianza":0.5,"origen":null,"nota":null,"campoDelFuturo":{"x":[1]}}],"dudas":null,"plantillaId":null,"tokens":1234}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var sugerencia = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        var columna = sugerencia.Valor.Columnas.Should().ContainSingle().Subject;
        columna.Campo.Should().BeEmpty();
        columna.Columna.Should().BeEmpty();
        sugerencia.Valor.Dudas.Should().BeEmpty();
    }

    [Fact]
    public async Task UnDoscientosConOkFalseNoInventaDatos()
    {
        // El contrato de NEXO no tiene un sobre {ok:false}: los errores llegan con su estado HTTP. Si llegara, no se inventa nada.
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK, """{"ok":false,"code":"INTERNAL_ERROR","message":"falló"}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var sugerencia = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        sugerencia.EsExitoso.Should().BeTrue("se lee como una sugerencia vacía");
        sugerencia.Valor.IaUsada.Should().BeFalse();
        sugerencia.Valor.Columnas.Should().BeEmpty();
        sugerencia.Valor.Dudas.Should().BeEmpty();
    }

    [Fact]
    public async Task IaUsadaFalseEnLaNormalizacionEsUnaRespuestaValida()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK,
            """{"iaUsada":false,"motivoSinIa":"IA_RESPUESTA_INVALIDA","totalDistintos":1,"valores":[{"campo":"ciudad","valor":"Medelin","estado":"sin_resolver","origen":"regla","confianza":0}]}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var normalizacion = await importacion.NormalizarValoresAsync(_usuarioId, null, [new("ciudad", "Medelin")], Ct);

        normalizacion.Valor.IaUsada.Should().BeFalse();
        normalizacion.Valor.MotivoSinIa.Should().Be("IA_RESPUESTA_INVALIDA");
        normalizacion.Valor.Valores.Should().ContainSingle().Which.Estado.Should().Be("sin_resolver");
        nexo.Llamadas.Should().ContainSingle();
        JsonNode.Parse(nexo.Llamadas[0].Cuerpo!)!.AsObject().ContainsKey("clienteNit").Should().BeFalse("sin NIT no viaja clienteNit");
    }

    [Theory(Skip = "Defecto D3 del validador de #252: un elemento null dentro de una lista de la respuesta de NEXO lanza NullReferenceException (no se convierte en NEXO_NO_DISPONIBLE).")]
    [InlineData("sugerir", """{"iaUsada":true,"columnas":[null]}""")]
    [InlineData("aplicar", """{"totalFilas":1,"lineas":[null],"errores":[]}""")]
    [InlineData("aplicar-errores", """{"totalFilas":1,"lineas":[],"errores":[null]}""")]
    [InlineData("normalizar", """{"iaUsada":true,"valores":[null]}""")]
    [InlineData("leer", """{"receta":{},"tablas":[null]}""")]
    [InlineData("plantillas", "[null]")]
    [InlineData("recetas", "[null]")]
    [InlineData("diccionario", "[null]")]
    public async Task UnElementoNuloEnUnaListaDeNexoEsNoDisponible(string operacion, string cuerpo)
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK, cuerpo));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var error = operacion switch
        {
            "sugerir" => (await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct)).Error,
            "aplicar" or "aplicar-errores" => (await importacion.AplicarMapeoAsync(_usuarioId, _aplicar, Ct)).Error,
            "normalizar" => (await importacion.NormalizarValoresAsync(_usuarioId, null, [new("ciudad", "Cali")], Ct)).Error,
            "leer" => (await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), null, null, null), Ct)).Error,
            "plantillas" => (await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct)).Error,
            "recetas" => (await importacion.ListarRecetasLecturaAsync(_usuarioId, "900123456", Ct)).Error,
            _ => (await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, "900123456", null, Ct)).Error,
        };

        error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    [Theory]
    [InlineData(400, "<html><body>400 Bad Request (proxy)</body></html>", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(400, """{"code":123,"message":["no","es","texto"]}""", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(400, "[1,2,3]", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(400, """{"code":"campo_invalido","message":"minúsculas"}""", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(400, """{"code":"IA_TOPE_DIARIO","message":"con otro estado"}""", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(404, "", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(418, """{"code":"TETERA","message":"x"}""", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(422, """{"code":"CAMPO_INVALIDO","message":"x"}""", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(429, "<html>Too Many Requests</html>", clsCodigosError.NexoNoDisponible)]
    [InlineData(302, "", clsCodigosError.NexoNoDisponible)]
    [InlineData(304, "", clsCodigosError.NexoNoDisponible)]
    [InlineData(500, "<html>500</html>", clsCodigosError.NexoNoDisponible)]
    public async Task UnErrorRaroDeNexoSeTraduceSinLanzar(int estado, string cuerpo, string esperado)
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta((HttpStatusCode)estado, cuerpo));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var resultado = await importacion.NormalizarValoresAsync(_usuarioId, "900123456", [new("ciudad", "Cali")], Ct);

        resultado.Error.Codigo.Should().Be(esperado);
        nexo.Llamadas.Should().ContainSingle();
    }

    [Fact]
    public async Task UnCodigoConocidoSinMensajeUsaElMensajeGenerico()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.BadRequest, """{"code":"DEMASIADAS_FILAS","message":null}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var resultado = await importacion.AplicarMapeoAsync(_usuarioId, _aplicar, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.DemasiadasFilas);
        resultado.Error.Mensaje.Should().Be(clsImportacionExcelNexo.MensajeRechazada);
        resultado.Error.Detalle.Should().Be("HTTP 400 DEMASIADAS_FILAS");
    }

    [Fact]
    public async Task ElTopeDiarioSinMensajeUsaElDeCotifly()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.TooManyRequests, """{"code":"IA_TOPE_DIARIO"}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var resultado = await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), null, "corrige", null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.IaTopeDiario);
        resultado.Error.Mensaje.Should().Be(clsCosteoNexo.MensajeIaTopeDiario);
        nexo.Llamadas.Should().ContainSingle();
    }

    [Fact(Skip = "Defecto D1 del validador de #252: el cortacircuitos de la tubería cuenta los 429 IA_TOPE_DIARIO de un comercial como fallos y, al abrirse, deja sin importación a los demás comerciales.")]
    public async Task ElTopeDiarioDeUnComercialNoAbreElCircuitoParaLosDemas()
    {
        var nexo = new clsNexoGrabador((llamada, _) => llamada.Identidad == IdExterno.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ? Respuesta(HttpStatusCode.TooManyRequests, """{"code":"IA_TOPE_DIARIO","message":"Superó su tope diario de IA."}""")
            : Respuesta(HttpStatusCode.OK, SugerenciaJson));
        // Solo se baja el mínimo de peticiones del cortacircuitos (100 por defecto) para reproducirlo con pocas llamadas.
        var (importacion, proveedor) = Crear(nexo, ajustar: r => r.CircuitBreaker.MinimumThroughput = 2);
        using var limpieza = proveedor;

        var propios = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            propios.Add((await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct)).Error.Codigo);
        }

        var otro = await importacion.SugerirMapeoAsync(_otroUsuarioId, _sugerir, Ct);

        propios.Should().AllBe(clsCodigosError.IaTopeDiario, "el comercial sin cupo sigue viendo su tope, no «NEXO no disponible»");
        otro.EsExitoso.Should().BeTrue("el tope diario es de un comercial: no debe afectar a los demás (documento de lectura con IA de NEXO)");
    }

    // --- Seguridad: NIT, clienteId, parámetros y receta opaca ---

    [Fact]
    public async Task NuncaSeMandaElClienteIdEnNingunaOperacion()
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;
        const string Nit = "900.123.456-7";

        await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);
        await importacion.AplicarMapeoAsync(_usuarioId, _aplicar, Ct);
        await importacion.NormalizarValoresAsync(_usuarioId, Nit, [new("ciudad", "Cali")], Ct);
        await importacion.GuardarPlantillaMapeoAsync(_usuarioId, new clsPeticionPlantillaMapeo(Nit, "n", ["ORIGEN"], [new(0, "origen", null)]), Ct);
        await importacion.ListarPlantillasMapeoAsync(_usuarioId, Nit, Ct);
        await importacion.EliminarPlantillaMapeoAsync(_usuarioId, Guid.NewGuid(), Ct);
        await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, Nit, new clsFiltroDiccionarioCliente("columna", "outbound", true, false), Ct);
        await importacion.ObtenerOpcionesDiccionarioAsync(_usuarioId, Ct);
        await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), Nit, "i", "{}"), Ct);
        await importacion.GuardarRecetaLecturaAsync(_usuarioId, new clsPeticionGuardarReceta(Archivo(), "{}", Nit, "n", "i"), Ct);
        await importacion.ListarRecetasLecturaAsync(_usuarioId, Nit, Ct);
        await importacion.EliminarRecetaLecturaAsync(_usuarioId, Guid.NewGuid(), Ct);

        nexo.Llamadas.Should().HaveCount(12);
        foreach (var llamada in nexo.Llamadas)
        {
            var consulta = System.Web.HttpUtility.ParseQueryString(llamada.Url.Query);
            consulta.AllKeys.Should().NotContain(k => string.Equals(k, "clienteId", StringComparison.OrdinalIgnoreCase), llamada.Url.ToString());
            llamada.Partes.Select(p => p.Nombre).Should().NotContain(n => string.Equals(n, "clienteId", StringComparison.OrdinalIgnoreCase));
            if (llamada.Partes.Count == 0 && llamada.Cuerpo is { Length: > 0 } cuerpo)
            {
                JsonNode.Parse(cuerpo)!.AsObject().Select(p => p.Key).Should().NotContain(k => string.Equals(k, "clienteId", StringComparison.OrdinalIgnoreCase));
            }

            llamada.CuerpoOConsulta.Should().NotContain("900.123.456", "solo viaja el NIT normalizado");
            llamada.Identidad.Should().Be(IdExterno.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        nexo.Llamadas.Where(l => l.Metodo != HttpMethod.Delete && l.Url.AbsolutePath != clsImportacionExcelNexo.RutaOpcionesDiccionario)
            .Should().AllSatisfy(l => l.CuerpoOConsulta.Should().Contain("900123456"));
    }

    [Theory]
    [InlineData("900.123.456-7")]
    [InlineData("900 123 456 - 7")]
    [InlineData("\t900123456\n")]
    [InlineData("900123456-7")]
    [InlineData("900,123,456")]
    [InlineData("900_123_456")]
    public async Task ElNitSiempreViajaNormalizado(string nit)
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        await importacion.SugerirMapeoAsync(_usuarioId, _sugerir with { ClienteNit = nit }, Ct);
        await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), nit, null, null), Ct);
        await importacion.ListarRecetasLecturaAsync(_usuarioId, nit, Ct);

        JsonNode.Parse(nexo.Llamadas[0].Cuerpo!)!["clienteNit"]!.GetValue<string>().Should().Be("900123456");
        Encoding.UTF8.GetString(nexo.Llamadas[1].Partes.Single(p => p.Nombre == "clienteNit").Bytes).Should().Be("900123456");
        nexo.Llamadas[2].Url.Query.Should().Be("?clienteNit=900123456");
    }

    [Fact]
    public async Task UnNitDeMasDeTreintaCaracteresNoLlegaANexoYUnoDeTreintaSi()
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;
        var treintaYUno = new string('9', 31);
        var treintaConPuntos = string.Join('.', Enumerable.Repeat("123456", 5)) + "-7";

        var largo = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir with { ClienteNit = treintaYUno }, Ct);
        var largoListar = await importacion.ListarPlantillasMapeoAsync(_usuarioId, treintaYUno, Ct);
        var justo = await importacion.ListarPlantillasMapeoAsync(_usuarioId, treintaConPuntos, Ct);

        largo.Error.Codigo.Should().Be(clsCodigosError.ClienteNitInvalido);
        largoListar.Error.Codigo.Should().Be(clsCodigosError.ClienteNitInvalido);
        justo.EsExitoso.Should().BeTrue("30 caracteres tras normalizar es el máximo de NEXO");
        nexo.Llamadas.Should().ContainSingle().Which.Url.Query.Should().Be("?clienteNit=" + string.Concat(Enumerable.Repeat("123456", 5)));
    }

    [Fact]
    public async Task UnNitConCaracteresDeConsultaNoInyectaParametros()
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        await importacion.ListarRecetasLecturaAsync(_usuarioId, "900&clienteId=abc#frag", Ct);
        await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900%26clienteId%3Dabc", Ct);

        foreach (var llamada in nexo.Llamadas)
        {
            var consulta = System.Web.HttpUtility.ParseQueryString(llamada.Url.Query);
            consulta.AllKeys.Should().Equal("clienteNit");
            consulta["clienteNit"].Should().MatchRegex("^[0-9A-Z]+$", "la normalización deja solo letras y dígitos");
            llamada.Url.Fragment.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("-7")]
    [InlineData(".-.")]
    [InlineData("#&?=")]
    public async Task ListarSinUnNitUtilNoLlamaANexo(string? nit)
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var recetas = await importacion.ListarRecetasLecturaAsync(_usuarioId, nit!, Ct);
        var plantillas = await importacion.ListarPlantillasMapeoAsync(_usuarioId, nit!, Ct);
        var guardar = await importacion.GuardarRecetaLecturaAsync(_usuarioId, new clsPeticionGuardarReceta(Archivo(), "{}", nit!, null, null), Ct);

        new[] { recetas.Error.Codigo, plantillas.Error.Codigo, guardar.Error.Codigo }
            .Should().AllSatisfy(c => c.Should().BeOneOf(clsCodigosError.ClienteRequerido, clsCodigosError.ClienteNitInvalido));
        nexo.Llamadas.Should().BeEmpty("sin NIT NEXO devolvería los formatos de todos los clientes");
    }

    [Fact]
    public async Task LosFiltrosDelDiccionarioSeEscapanSinInyectarParametros()
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;
        const string Q = "a&clienteId=x#y?z=1 ñ/../%2F";

        await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, "900123456", new clsFiltroDiccionarioCliente("columna&soloActivos=false", "  " + Q + "  "), Ct);

        var llamada = nexo.Llamadas.Should().ContainSingle().Subject;
        llamada.Url.AbsolutePath.Should().Be(clsImportacionExcelNexo.RutaDiccionarioCliente);
        var consulta = System.Web.HttpUtility.ParseQueryString(llamada.Url.Query);
        consulta.AllKeys.Should().BeEquivalentTo("clienteNit", "incluirGenerales", "tipo", "q");
        consulta["q"].Should().Be(Q);
        consulta["tipo"].Should().Be("columna&soloActivos=false");
        consulta["incluirGenerales"].Should().Be("true");
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("""{"a":"é","b":1.0,"c":1e2,"d":"<script>alert(1)</script>","e":"../../api/clientes?clienteId=1"}""")]
    [InlineData("{\r\n  \"comillas\": \"\\\"x\\\"\",\r\n  \"nulo\": null\r\n}")]
    [InlineData("  {\"espacios\":true}  ")]
    public async Task LaRecetaViajaByteAByteSinInterpretarseNiTocarLaUrl(string receta)
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), null, null, receta), Ct);
        await importacion.GuardarRecetaLecturaAsync(_usuarioId, new clsPeticionGuardarReceta(Archivo(), receta, "900123456", null, null), Ct);

        nexo.Llamadas.Should().HaveCount(2);
        foreach (var llamada in nexo.Llamadas)
        {
            llamada.Url.Query.Should().BeEmpty();
            llamada.Partes.Single(p => p.Nombre == "receta").Bytes.Should().Equal(Encoding.UTF8.GetBytes(receta));
        }
    }

    [Fact]
    public async Task UnaRecetaGrandeViajaCompleta()
    {
        var receta = "{\"notas\":\"" + new string('x', 60_000) + "\"}";
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), null, null, receta), Ct);

        nexo.Llamadas.Single().Partes.Single(p => p.Nombre == "receta").Bytes.Should().HaveCount(receta.Length, "Cotifly no recorta: el límite lo valida NEXO");
    }

    [Fact]
    public async Task LaRecetaDeLaRespuestaConservaSuTextoCrudo()
    {
        const string Receta = """{"b":1.0,"c":1e2,"s":"é","n":null,"anidada":{"lista":[{"x":"y"}]}}""";
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.OK, LecturaJson.Replace("""{"version":1}""", Receta, StringComparison.Ordinal)));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(Archivo(), null, null, null), Ct);

        lectura.Valor.Receta.GetRawText().Should().Be(Receta, "la receta es opaca: ni 1.0 ni \\u00e9 se reescriben");
    }

    [Fact]
    public async Task NoSeLogueaElContenidoDelExcelNiLaIdentidad()
    {
        var logs = new clsProveedorLogsFalso();
        var nexo = new clsNexoGrabador((llamada, _) => llamada.Numero switch
        {
            1 => Respuesta(HttpStatusCode.ServiceUnavailable, "<html>caído</html>"),
            2 => Respuesta(HttpStatusCode.BadRequest, """{"code":"DEMASIADAS_COLUMNAS","message":"El archivo tiene demasiadas columnas."}"""),
            3 => throw new HttpRequestException("conexión rechazada"),
            4 => Respuesta(HttpStatusCode.Forbidden, """{"code":"SIN_PERMISO_CABINA_FLETES","message":"sin rol"}"""),
            _ => RespuestaPorRuta(llamada),
        });
        var (importacion, proveedor) = Crear(nexo, logs: logs);
        using var limpieza = proveedor;
        var excel = Encoding.UTF8.GetBytes("PK..EXCEL-SECRETO-1..");

        await importacion.SugerirMapeoAsync(_usuarioId, new("900123456", ["ENCABEZADO-SECRETO"], [["FILA-SECRETA"]]), Ct);
        await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("libro.xlsx", excel), "900123456", "INSTRUCCION-SECRETA", "{\"r\":\"RECETA-SECRETA\"}"), Ct);
        await importacion.NormalizarValoresAsync(_usuarioId, null, [new("ciudad", "VALOR-SECRETO")], Ct);
        await importacion.AplicarMapeoAsync(_usuarioId, new("900123456", [new(0, "origen", null)], [["FILA-SECRETA"]], null), Ct);
        await importacion.GuardarRecetaLecturaAsync(_usuarioId,
            new clsPeticionGuardarReceta(new clsArchivoImportacion("libro.xlsx", excel), "{\"r\":\"RECETA-SECRETA\"}", "900123456", null, "INSTRUCCION-SECRETA"), Ct);

        nexo.Llamadas.Should().HaveCount(5);
        logs.Registros.Should().NotBeEmpty();
        var todo = string.Join('\n', logs.Registros.Select(r => r.Registro.Mensaje + " " + r.Registro.Excepcion));
        foreach (var secreto in new[] { "EXCEL-SECRETO", "ENCABEZADO-SECRETO", "FILA-SECRETA", "INSTRUCCION-SECRETA", "RECETA-SECRETA", "VALOR-SECRETO" })
        {
            todo.Should().NotContain(secreto);
        }

        todo.Should().NotContain(IdExterno.ToString(System.Globalization.CultureInfo.InvariantCulture), "el header de identidad no se registra");
    }

    // --- Archivos: grandes, binarios, vacíos y nombres raros ---

    [Fact]
    public async Task UnArchivoBinarioGrandeLlegaIntacto()
    {
        var contenido = new byte[11 * 1024 * 1024];
        for (var i = 0; i < contenido.Length; i++)
        {
            contenido[i] = (byte)(i * 31 % 256);
        }

        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("grande.xlsx", contenido), null, null, null), Ct);

        lectura.EsExitoso.Should().BeTrue("el límite de 10 MB lo validan el endpoint (#253) y NEXO, no el adaptador");
        var parte = nexo.Llamadas.Should().ContainSingle().Subject.Partes.Single(p => p.Nombre == "archivo");
        parte.Bytes.Should().Equal(contenido);
        parte.TipoContenido.Should().Be(clsArchivoExcel.TipoXlsx);
    }

    [Fact]
    public async Task UnArchivoVacioSeReenviaYElCodigoDeNexoLlegaTalCual()
    {
        var nexo = new clsNexoGrabador((_, _) => Respuesta(HttpStatusCode.BadRequest, """{"code":"ARCHIVO_REQUERIDO","message":"Falta el archivo."}"""));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("vacio.xlsx", []), null, null, null), Ct);

        lectura.Error.Codigo.Should().Be(clsCodigosError.ArchivoRequerido);
        nexo.Llamadas.Single().Partes.Single(p => p.Nombre == "archivo").Bytes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Cotización Ñandú (v2).xlsx", "Cotización Ñandú (v2).xlsx", false)]
    [InlineData("a;b=c.xlsm", "a;b=c.xlsm", true)]
    [InlineData("LIBRO CON ESPACIOS.XLSM", "LIBRO CON ESPACIOS.XLSM", true)]
    [InlineData("..\\..\\etc\\passwd.xlsx", "..\\..\\etc\\passwd.xlsx", false)]
    [InlineData("日本語.xlsx", "日本語.xlsx", false)]
    [InlineData("", "licitacion.xlsx", false)]
    [InlineData("   ", "licitacion.xlsx", false)]
    public async Task UnNombreDeArchivoRaroViajaSinRomperElFormulario(string nombre, string esperado, bool xlsm)
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion(nombre, [1, 2, 3]), null, null, null), Ct);

        lectura.EsExitoso.Should().BeTrue();
        var llamada = nexo.Llamadas.Should().ContainSingle().Subject;
        var parte = llamada.Partes.Single(p => p.Nombre == "archivo");
        parte.NombreArchivo.Should().Be(esperado);
        parte.TipoContenido.Should().Be(xlsm ? clsArchivoExcel.TipoXlsm : clsArchivoExcel.TipoXlsx);
        llamada.Partes.Select(p => p.Nombre).Should().OnlyHaveUniqueItems().And.NotContain(n => n == null);
    }

    [Theory(Skip = "Defecto D4 del validador de #252: un nombre de archivo con comillas o saltos de línea lanza una excepción al armar el formulario (fuera del try del adaptador).")]
    [InlineData("Cotización \"final\".xlsx")]
    [InlineData("a\r\nX-Inyectado: 1.xlsx")]
    public async Task UnNombreDeArchivoConComillasOSaltosNoRompeElAdaptador(string nombre)
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion(nombre, [1, 2, 3]), null, null, null), Ct);

        lectura.EsExitoso.Should().BeTrue();
        var crudo = nexo.Llamadas.Single().Cuerpo!;
        crudo.Should().NotContain("\r\nX-Inyectado", "el nombre no puede inyectar cabeceras en la parte");
    }

    // --- Apagada, sin usuario y la NoDisponible ---

    [Fact]
    public void ConUrlBaseEnBlancoEsLaNoDisponible()
    {
        using var proveedor = ProveedorBase(new() { ["Nexo:UrlBase"] = "   " });
        using var ambito = proveedor.CreateScope();

        ambito.ServiceProvider.GetRequiredService<IClsImportacionExcelNexo>().Should().BeOfType<clsImportacionExcelNoDisponible>();
    }

    [Fact]
    public async Task ElAdaptadorDeNexoApagadoNoLlamaAunqueSeResuelvaDirecto()
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (_, proveedor) = Crear(nexo, extra: new() { ["ImportacionInteligente:Habilitada"] = "false" });
        using var limpieza = proveedor;
        using var ambito = proveedor.CreateScope();
        var adaptador = ambito.ServiceProvider.GetRequiredService<clsImportacionExcelNexo>();

        var sugerencia = await adaptador.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);
        var recetas = await adaptador.ListarRecetasLecturaAsync(_usuarioId, "900123456", Ct);

        adaptador.EstaHabilitada.Should().BeFalse();
        sugerencia.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        recetas.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task UnComercialQueNoExisteNoLlamaANexo()
    {
        var nexo = new clsNexoGrabador((llamada, _) => RespuestaPorRuta(llamada));
        var (importacion, proveedor) = Crear(nexo);
        using var limpieza = proveedor;

        var resultado = await importacion.LeerEstructuraAsync(_usuarioInexistente, new clsPeticionLeerEstructura(Archivo(), null, null, null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        resultado.Error.Detalle.Should().Contain("no existe");
        nexo.Llamadas.Should().BeEmpty();
    }

    [Fact]
    public async Task LaNoDisponibleNoMiraLosArgumentosNiLanza()
    {
        var apagada = new clsImportacionExcelNoDisponible();

        var sugerir = await apagada.SugerirMapeoAsync(Guid.Empty, null!, Ct);
        var leer = await apagada.LeerEstructuraAsync(Guid.Empty, null!, new CancellationToken(canceled: true));
        var listar = await apagada.ListarRecetasLecturaAsync(Guid.Empty, null!, Ct);

        new[] { sugerir.Error, leer.Error, listar.Error }.Should().AllSatisfy(e =>
        {
            e.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
            e.Mensaje.Should().Be(clsImportacionExcelNoDisponible.Mensaje);
        });
    }

    // --- Soporte ---

    private static Task<HttpResponseMessage> Respuesta(HttpStatusCode estado, string cuerpo, string tipo = "application/json") =>
        Task.FromResult(new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, tipo) });

    /// <summary>Una respuesta válida de NEXO según la ruta (para las pruebas que miran lo que se envía).</summary>
    private static Task<HttpResponseMessage> RespuestaPorRuta(clsLlamadaNexo llamada)
    {
        var ruta = llamada.Url.AbsolutePath;
        if (llamada.Metodo == HttpMethod.Delete)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }

        return ruta switch
        {
            clsImportacionExcelNexo.RutaSugerirMapeo => Respuesta(HttpStatusCode.OK, SugerenciaJson),
            clsImportacionExcelNexo.RutaAplicarMapeo => Respuesta(HttpStatusCode.OK, "{}"),
            clsImportacionExcelNexo.RutaNormalizarValores => Respuesta(HttpStatusCode.OK, "{}"),
            clsImportacionExcelNexo.RutaPlantillasMapeo when llamada.Metodo == HttpMethod.Put => Respuesta(HttpStatusCode.Created, PlantillaJson()),
            clsImportacionExcelNexo.RutaLeerEstructura => Respuesta(HttpStatusCode.OK, LecturaJson),
            clsImportacionExcelNexo.RutaRecetasLectura when llamada.Metodo == HttpMethod.Put => Respuesta(HttpStatusCode.Created, RecetaGuardadaJson()),
            clsImportacionExcelNexo.RutaOpcionesDiccionario => Respuesta(HttpStatusCode.OK, "{}"),
            _ => Respuesta(HttpStatusCode.OK, "[]"),
        };
    }

    private static (IClsImportacionExcelNexo Importacion, ServiceProvider Proveedor) Crear(
        HttpMessageHandler nexo, bool tiemposCortos = false, Action<HttpStandardResilienceOptions>? ajustar = null,
        clsProveedorLogsFalso? logs = null, Dictionary<string, string?>? extra = null)
    {
        var valores = new Dictionary<string, string?> { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase };
        foreach (var (clave, valor) in extra ?? [])
        {
            valores[clave] = valor;
        }

        var proveedor = ProveedorBase(valores, s =>
        {
            if (logs is not null)
            {
                s.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            }

            s.AddHttpClient(clsImportacionExcelNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);
            s.PostConfigure<HttpStandardResilienceOptions>(Tuberia, r =>
            {
                if (tiemposCortos)
                {
                    clsImportacionExcelNexo.ConfigurarResiliencia(r, _tiempoCorto, _tiempoLecturaCorto);
                }

                ajustar?.Invoke(r);
            });
            var usuarios = Substitute.For<IClsUsuarioWebRepository>();
            usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
                .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, IdExterno).Valor);
            usuarios.ObtenerPorIdAsync(_otroUsuarioId, Arg.Any<CancellationToken>())
                .Returns(clsUsuarioWeb.Crear("jperez", "Juan Pérez", "jperez@boterosoto.com.co", null, DateTimeOffset.UtcNow, OtroIdExterno).Valor);
            usuarios.ObtenerPorIdAsync(_usuarioInexistente, Arg.Any<CancellationToken>()).Returns((clsUsuarioWeb?)null);
            s.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
        });
        return (proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsImportacionExcelNexo>(), proveedor);
    }

    private static ServiceProvider ProveedorBase(Dictionary<string, string?> valores, Action<IServiceCollection>? configurar = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }

    /// <summary>Una parte del formulario multipart tal como la armó el adaptador.</summary>
    private sealed record clsParteFormulario(string? Nombre, string? NombreArchivo, string? TipoContenido, byte[] Bytes);

    /// <summary>Una petición recibida por <see cref="clsNexoGrabador"/> (<paramref name="Numero"/> empieza en 1).</summary>
    private sealed record clsLlamadaNexo(
        int Numero, HttpMethod Metodo, Uri Url, string? Identidad, string? Cuerpo, IReadOnlyList<clsParteFormulario> Partes)
    {
        public string CuerpoOConsulta =>
            Url.Query + "\n" + (Partes.Count == 0 ? Cuerpo : string.Join('\n', Partes.Where(p => p.Nombre != "archivo").Select(p => Encoding.UTF8.GetString(p.Bytes))));
    }

    /// <summary>NEXO falso que graba cada petición (cuerpo, partes del multipart e identidad) y responde con la función dada.</summary>
    private sealed class clsNexoGrabador(Func<clsLlamadaNexo, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<clsLlamadaNexo> _llamadas = new();
        private int _numero;

        public IReadOnlyList<clsLlamadaNexo> Llamadas => [.. _llamadas.OrderBy(l => l.Numero)];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var partes = new List<clsParteFormulario>();
            if (request.Content is MultipartFormDataContent formulario)
            {
                foreach (var parte in formulario)
                {
                    var disposicion = parte.Headers.ContentDisposition;
                    partes.Add(new clsParteFormulario(
                        disposicion?.Name?.Trim('"'),
                        disposicion?.FileNameStar ?? disposicion?.FileName?.Trim('"'),
                        parte.Headers.ContentType?.MediaType,
                        await parte.ReadAsByteArrayAsync(cancellationToken)));
                }
            }

            var cuerpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var identidad = request.Headers.TryGetValues(clsPeticionNexo.EncabezadoIdentidad, out var valores) ? valores.Single() : null;
            var llamada = new clsLlamadaNexo(Interlocked.Increment(ref _numero), request.Method, request.RequestUri!, identidad, cuerpo, partes);
            _llamadas.Enqueue(llamada);
            return await responder(llamada, cancellationToken);
        }
    }

    /// <summary>Flujo de respuesta que nunca entrega datos: solo termina si se cancela la lectura.</summary>
    private sealed class clsFlujoColgado : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("solo asíncrono");

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
