using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Licitaciones.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Adaptador de la importación inteligente con NEXO (HU-044.1, #252, ADR-028): registro por <c>Nexo:UrlBase</c> e
/// <c>ImportacionInteligente:Habilitada</c>, identidad del comercial, cliente por NIT normalizado, receta opaca, sin reintentos en lo que
/// gasta IA, <c>429 IA_TOPE_DIARIO</c> sin bloqueo global y la traducción de los errores de NEXO. Todo contra Nexo falsos (sin red).
/// </summary>
public sealed class clsImportacionExcelNexoTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();
    private static readonly Guid _usuarioSinExterno = Guid.NewGuid();
    private const long IdExterno = 1007;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly clsPeticionSugerirMapeo _sugerir =
        new("900.123.456-7", ["ORIGEN", "DESTINO", "TON"], [["Buenaventura", "Bogotá", "32"]]);

    private const string SugerenciaJson =
        """{"iaUsada":true,"motivoSinIa":null,"columnas":[{"indice":0,"columna":"ORIGEN","campo":"origen","transformacion":"ninguna","confianza":0.9,"origen":"ia","nota":null}],"dudas":[],"plantillaId":null}""";

    // --- Registro y opciones ---

    [Theory]
    [InlineData(null, "true")]
    [InlineData("", "true")]
    [InlineData(clsNexoClientesFalso.UrlBase, "false")]
    public void SinNexoOApagadaEsLaNoDisponible(string? url, string habilitada)
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = url, ["ImportacionInteligente:Habilitada"] = habilitada });
        using var ambito = proveedor.CreateScope();

        var importacion = ambito.ServiceProvider.GetRequiredService<IClsImportacionExcelNexo>();

        importacion.Should().BeOfType<clsImportacionExcelNoDisponible>();
        importacion.EstaHabilitada.Should().BeFalse();
    }

    [Fact]
    public void ConNexoYHabilitadaEsElAdaptadorDeNexo()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase });
        using var ambito = proveedor.CreateScope();

        var importacion = ambito.ServiceProvider.GetRequiredService<IClsImportacionExcelNexo>();

        importacion.Should().BeOfType<clsImportacionExcelNexo>();
        importacion.EstaHabilitada.Should().BeTrue();
        var opciones = proveedor.GetRequiredService<IOptions<clsOpcionesImportacionInteligente>>().Value;
        opciones.TimeoutSegundos.Should().Be(90);
        opciones.TimeoutLecturaSegundos.Should().Be(240, "más que los 3 intentos de IA de 60 s de NEXO: deja margen para la lectura sin IA");
        opciones.LimiteRespuestaMegabytes.Should().Be(20);
    }

    [Theory]
    [InlineData("ImportacionInteligente:TimeoutLecturaSegundos", "179")]
    [InlineData("ImportacionInteligente:TimeoutSegundos", "0")]
    [InlineData("ImportacionInteligente:TimeoutSegundos", "601")]
    [InlineData("ImportacionInteligente:LimiteRespuestaMegabytes", "0")]
    public void UnTiempoFueraDeRangoImpideArrancar(string clave, string valor)
    {
        using var proveedor = Proveedor(new() { [clave] = valor });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesImportacionInteligente>>().Value;

        leer.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task LaNoDisponibleRespondeNexoNoDisponibleSinLlamar()
    {
        var apagada = new clsImportacionExcelNoDisponible();
        var archivo = new clsArchivoImportacion("a.xlsx", [1]);

        IReadOnlyList<clsErrorDominio> errores =
        [
            (await apagada.SugerirMapeoAsync(_usuarioId, _sugerir, Ct)).Error,
            (await apagada.AplicarMapeoAsync(_usuarioId, new clsPeticionAplicarMapeo("900123456", [new(0, "origen", null)], [["Cali"]], null), Ct)).Error,
            (await apagada.NormalizarValoresAsync(_usuarioId, null, [new("ciudad", "Cali")], Ct)).Error,
            (await apagada.GuardarPlantillaMapeoAsync(_usuarioId,
                new clsPeticionPlantillaMapeo("900123456", null, ["ORIGEN"], [new(0, "origen", null)]), Ct)).Error,
            (await apagada.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct)).Error,
            (await apagada.EliminarPlantillaMapeoAsync(_usuarioId, Guid.NewGuid(), Ct)).Error,
            (await apagada.ObtenerDiccionarioClienteAsync(_usuarioId, "900123456", null, Ct)).Error,
            (await apagada.ObtenerOpcionesDiccionarioAsync(_usuarioId, Ct)).Error,
            (await apagada.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(archivo, null, null, null), Ct)).Error,
            (await apagada.GuardarRecetaLecturaAsync(_usuarioId, new clsPeticionGuardarReceta(archivo, "{}", "900123456", null, null), Ct)).Error,
            (await apagada.ListarRecetasLecturaAsync(_usuarioId, "900123456", Ct)).Error,
            (await apagada.EliminarRecetaLecturaAsync(_usuarioId, Guid.NewGuid(), Ct)).Error,
        ];

        errores.Should().AllSatisfy(e => e.Codigo.Should().Be(clsCodigosError.NexoNoDisponible));
        apagada.EstaHabilitada.Should().BeFalse();
    }

    [Fact]
    public void LaTuberiaDaElTiempoLargoSoloALaLecturaYNoReintentaLoQueNoEstaMarcado()
    {
        var opciones = new HttpStandardResilienceOptions();

        clsImportacionExcelNexo.ConfigurarResiliencia(opciones, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180));

        opciones.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(90));
        opciones.Retry.MaxRetryAttempts.Should().Be(1, "NEXO admite un solo reintento de los GET");
        opciones.TotalRequestTimeout.Timeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(180));
        opciones.TotalRequestTimeout.Timeout.Should().BeGreaterThan(opciones.AttemptTimeout.Timeout);
        opciones.CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(360));
        clsImportacionExcelNexo.NombreCliente.Should().Be("NexoImportacion");
    }

    [Fact]
    public async Task CadaIntentoRecibeSuTiempoSegunLaMarcaDeLectura()
    {
        var opciones = new HttpStandardResilienceOptions();
        clsImportacionExcelNexo.ConfigurarResiliencia(opciones, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(240));
        using var lectura = new HttpRequestMessage(HttpMethod.Post, "http://nexo/api/captura-solicitudes/leer-estructura");
        lectura.Options.Set(clsImportacionExcelNexo.OpcionLectura, true);
        using var normal = new HttpRequestMessage(HttpMethod.Post, "http://nexo/api/captura-solicitudes/sugerir-mapeo");

        (await TiempoDelIntento(opciones, lectura)).Should().Be(TimeSpan.FromSeconds(240));
        (await TiempoDelIntento(opciones, normal)).Should().Be(TimeSpan.FromSeconds(90));
        opciones.TotalRequestTimeout.Timeout.Should().BeGreaterThan(TimeSpan.FromSeconds(240), "el total no corta antes que el intento de lectura");
    }

    [Fact]
    public async Task UnTopeDiarioNoCuentaComoFalloDelCircuitoYUnCincoCeroTresSi()
    {
        var opciones = new HttpStandardResilienceOptions();
        clsImportacionExcelNexo.ConfigurarResiliencia(opciones, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(240));

        (await CuentaParaElCircuito(opciones, HttpStatusCode.TooManyRequests)).Should().BeFalse("IA_TOPE_DIARIO es del comercial, no de la salud de NEXO");
        (await CuentaParaElCircuito(opciones, HttpStatusCode.ServiceUnavailable)).Should().BeTrue();
        (await CuentaParaElCircuito(opciones, HttpStatusCode.OK)).Should().BeFalse();
    }

    // --- Identidad, NIT y receta opaca ---

    [Fact]
    public async Task MandaLaIdentidadDelComercialYElNitNormalizadoComoNexo()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, SugerenciaJson);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        resultado.Valor.IaUsada.Should().BeTrue();
        resultado.Valor.Columnas.Should().ContainSingle().Which.Campo.Should().Be("origen");
        var peticion = nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.IdPortalUsuario.Should().Be("1007");
        peticion.Url.AbsolutePath.Should().Be(clsImportacionExcelNexo.RutaSugerirMapeo);
        var cuerpo = JsonNode.Parse(peticion.Cuerpo!)!;
        cuerpo["clienteNit"]!.GetValue<string>().Should().Be("900123456", "NEXO compara sin puntos ni dígito de verificación");
        cuerpo["clienteId"].Should().BeNull("Cotifly no busca ni manda el clienteId de NEXO");
        cuerpo["encabezados"]!.AsArray().Should().HaveCount(3);
    }

    [Theory]
    [InlineData("900.123.456-7", "900123456")]
    [InlineData(" 900 123 456 ", "900123456")]
    [InlineData("900123456", "900123456")]
    [InlineData("ab-1", "AB")]
    [InlineData("---", null)]
    [InlineData("  ", null)]
    public void NormalizaElNitComoNexo(string nit, string? esperado) =>
        clsImportacionExcelNexo.NormalizarNit(nit).Should().Be(esperado);

    [Fact]
    public async Task SinIdUsuarioExternoNoLlamaANexo()
    {
        var nexo = new clsNexoClientesFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.SugerirMapeoAsync(_usuarioSinExterno, _sugerir, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ListarRecetasSinNitNoLlamaANexo()
    {
        var nexo = new clsNexoClientesFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var sinNit = await importacion.ListarRecetasLecturaAsync(_usuarioId, "  ", Ct);
        var plantillas = await importacion.ListarPlantillasMapeoAsync(_usuarioId, string.Empty, Ct);
        var nitInvalido = await importacion.ListarRecetasLecturaAsync(_usuarioId, "---", Ct);

        sinNit.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        plantillas.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        nitInvalido.Error.Codigo.Should().Be(clsCodigosError.ClienteNitInvalido);
        nexo.Peticiones.Should().BeEmpty("sin NIT NEXO devolvería los formatos de todos los clientes");
    }

    [Fact]
    public async Task ListarRecetasFiltraPorElNitYDevuelveLaRecetaTalCual()
    {
        const string RecetaJson = """{"version":1,"hojas":[{"hoja":"*","usar":true,"extra":{"a":[1,2]}}],"unir":false}""";
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK,
            $$"""[{"id":"{{Guid.NewGuid()}}","clienteId":"{{Guid.NewGuid()}}","clienteNombre":"EPM","nombre":null,"receta":{{RecetaJson}},"instrucciones":null,"usos":2,"ultimoUsoAt":null,"createdAt":"2026-11-05T15:00:00Z","updatedAt":null}]""");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.ListarRecetasLecturaAsync(_usuarioId, "890.904.996-1", Ct);

        nexo.Peticiones.Should().ContainSingle().Which.Url.Query.Should().Be("?clienteNit=890904996");
        var guardada = resultado.Valor.Should().ContainSingle().Subject;
        guardada.Usos.Should().Be(2);
        guardada.Receta.GetRawText().Should().Be(JsonDocument.Parse(RecetaJson).RootElement.GetRawText(), "la receta es JSON opaco");
    }

    [Fact]
    public async Task LeerEstructuraReenviaElArchivoYLaRecetaSinTocarla()
    {
        const string RecetaJson = """{"version":1,"hojas":[{"hoja":"Hoja 1","usar":true,"campoDelFuturo":"x"}],"unir":false}""";
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("EPM formulario.xlsx", [1, 2, 3]), "890.904.996-1", null, RecetaJson), Ct);

        var formulario = nexo.UltimoFormularioImportacion!;
        formulario["archivo"].Should().Be("EPM formulario.xlsx", "con su nombre original");
        formulario["receta"].Should().Be(RecetaJson, "la receta viaja tal cual");
        formulario["clienteNit"].Should().Be("890904996");
        formulario.Should().NotContainKey("instrucciones");
        resultado.Valor.OrigenReceta.Should().Be("manual");
        resultado.Valor.IaUsada.Should().BeFalse();
        JsonNode.DeepEquals(JsonNode.Parse(resultado.Valor.Receta.GetRawText()), JsonNode.Parse(RecetaJson)).Should().BeTrue();
        resultado.Valor.Tablas.Should().ContainSingle().Which.Encabezados.Should().Equal("ORIGEN", "DESTINO", "TON");
        nexo.Llamadas(HttpMethod.Post, clsImportacionExcelNexo.RutaLeerEstructura).Should().Be(1);
    }

    [Fact]
    public async Task ConLaIaApagadaEnNexoLaLecturaNoEsUnError()
    {
        var nexo = new clsNexoFalso { IaImportacionDisponible = false };
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("a.xlsx", [1]), null, "el destino está en A2", null), Ct);
        var sugerencia = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        lectura.Valor.IaUsada.Should().BeFalse();
        lectura.Valor.MotivoSinIa.Should().Be("IA_NO_DISPONIBLE");
        lectura.Valor.OrigenReceta.Should().Be("automatica");
        sugerencia.Valor.IaUsada.Should().BeFalse();
        sugerencia.Valor.MotivoSinIa.Should().Be("IA_NO_DISPONIBLE");
    }

    [Fact]
    public async Task GuardarLaRecetaYVolverALeerUsaLaGuardadaSinIa()
    {
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;
        var archivo = new clsArchivoImportacion("epm.xlsx", [1, 2]);

        var primera = await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(archivo, "890904996", null, null), Ct);
        var guardada = await importacion.GuardarRecetaLecturaAsync(_usuarioId,
            new clsPeticionGuardarReceta(archivo, primera.Valor.Receta.GetRawText(), "890.904.996-1", "Formulario EPM", "el destino está en A2"), Ct);
        var otraVez = await importacion.GuardarRecetaLecturaAsync(_usuarioId,
            new clsPeticionGuardarReceta(archivo, primera.Valor.Receta.GetRawText(), "890904996", null, null), Ct);
        var segunda = await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(archivo, "890904996", null, null), Ct);

        primera.Valor.OrigenReceta.Should().Be("ia");
        guardada.Valor.Creada.Should().BeTrue("201 la primera vez");
        otraVez.Valor.Creada.Should().BeFalse("200 al reemplazar");
        segunda.Valor.OrigenReceta.Should().Be("guardada");
        segunda.Valor.IaUsada.Should().BeFalse();
        segunda.Valor.RecetaGuardadaId.Should().Be(guardada.Valor.Receta.Id);
        nexo.RecetasGuardadas.Should().ContainSingle().Which.Nit.Should().Be("890904996");
    }

    [Fact]
    public async Task GuardarSinNitNoLlamaANexo()
    {
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.GuardarRecetaLecturaAsync(_usuarioId,
            new clsPeticionGuardarReceta(new clsArchivoImportacion("a.xlsx", [1]), "{}", " ", null, null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        nexo.Peticiones.Should().BeEmpty();
    }

    // --- Reintentos ---

    [Fact]
    public async Task SugerirMapeoNoSeReintentaAunqueNexoFalleTransitoriamente()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.OK, SugerenciaJson);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().ContainSingle("cada llamada puede gastar IA y cuenta para el tope");
    }

    [Fact]
    public async Task LaLecturaYGuardarLaRecetaNoSeReintentan()
    {
        var nexo = new clsNexoClientesFalso()
            .Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;
        var archivo = new clsArchivoImportacion("a.xlsx", [1]);

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId, new clsPeticionLeerEstructura(archivo, null, null, null), Ct);
        var guardar = await importacion.GuardarRecetaLecturaAsync(_usuarioId, new clsPeticionGuardarReceta(archivo, "{}", "900123456", null, null), Ct);
        var normalizar = await importacion.NormalizarValoresAsync(_usuarioId, null, [new clsValorANormalizar("ciudad", "Medelin")], Ct);

        lectura.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        guardar.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        normalizar.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().HaveCount(3, "una sola petición por llamada");
    }

    [Fact]
    public async Task AplicarMapeoYLasLecturasSiSeReintentan()
    {
        var nexo = new clsNexoClientesFalso()
            .Responder(HttpStatusCode.ServiceUnavailable)
            .Responder(HttpStatusCode.OK, """{"totalFilas":1,"filasVacias":0,"filasConErrores":0,"lineas":[{"fila":2,"origen":"Cali","destino":"Bogotá","peso":32000}],"errores":[],"totalErrores":0}""")
            .Responder(HttpStatusCode.ServiceUnavailable)
            .Responder(HttpStatusCode.OK, """{"tipos":["columna"],"camposDestino":["origen"],"unidades":["kg"],"contextos":["tipoVehiculo"]}""");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var aplicar = await importacion.AplicarMapeoAsync(_usuarioId,
            new clsPeticionAplicarMapeo("900123456", [new(0, "origen", null), new(1, "destino", "")], [["Cali", "Bogotá"]], null), Ct);
        var opciones = await importacion.ObtenerOpcionesDiccionarioAsync(_usuarioId, Ct);

        aplicar.Valor.Lineas.Should().ContainSingle().Which.Peso.Should().Be(32000m);
        opciones.Valor.Tipos.Should().Equal("columna");
        nexo.Peticiones.Should().HaveCount(4, "sin IA ni efecto en NEXO: se reintentan como los GET");
        var cuerpo = JsonNode.Parse(nexo.Peticiones[0].Cuerpo!)!;
        cuerpo["columnas"]![1]!["transformacion"].Should().BeNull("una transformación vacía no viaja (NEXO la toma como ninguna)");
        cuerpo["primeraFila"].Should().BeNull();
    }

    // --- Errores ---

    [Fact]
    public async Task ElTopeDiarioDeIaEsUnErrorDeNegocioYNoBloqueaAlResto()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.TooManyRequests,
            """{"code":"IA_TOPE_DIARIO","message":"Superó su tope diario de IA (500.000 tokens)."}""");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.IaTopeDiario);
        resultado.Error.Mensaje.Should().Be("Superó su tope diario de IA (500.000 tokens).", "llega el mensaje de NEXO");
        nexo.Peticiones.Should().ContainSingle();
        proveedor.GetRequiredService<clsCupoNexo>().Restante().Should().BeNull("el tope es del comercial: no se activa el bloqueo global");
    }

    [Fact]
    public async Task ElTopeDiarioDelNexoFalsoTambienLlegaEnLaLectura()
    {
        var nexo = new clsNexoFalso { ImportacionFalla = HttpStatusCode.TooManyRequests, CodigoImportacionFalla = "IA_TOPE_DIARIO" };
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("a.xlsx", [1]), null, "corrige", null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.IaTopeDiario);
        proveedor.GetRequiredService<clsCupoNexo>().Restante().Should().BeNull();
    }

    [Fact]
    public async Task ElCupoPorMinutoSigueSiendoNoDisponibleConElRetryAfter()
    {
        var nexo = new clsNexoClientesFalso().ResponderLimite(30);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        resultado.Error.Detalle.Should().Contain("DEMASIADAS_SOLICITUDES").And.Contain("Retry-After 30 s");
        nexo.Peticiones.Should().ContainSingle("un 429 no se reintenta");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "DEMASIADAS_COLUMNAS", clsCodigosError.DemasiadasColumnas)]
    [InlineData(HttpStatusCode.BadRequest, "CAMPO_INVALIDO", clsCodigosError.CampoInvalido)]
    [InlineData(HttpStatusCode.BadRequest, "CAMPO_DUPLICADO", clsCodigosError.CampoDuplicado)]
    [InlineData(HttpStatusCode.BadRequest, "CAMPO_REQUERIDO", clsCodigosError.CampoRequerido)]
    [InlineData(HttpStatusCode.BadRequest, "RECETA_INVALIDA", clsCodigosError.RecetaInvalida)]
    [InlineData(HttpStatusCode.BadRequest, "CLIENTE_NIT_INVALIDO", clsCodigosError.ClienteNitInvalido)]
    [InlineData(HttpStatusCode.NotFound, "RECETA_NO_ENCONTRADA", clsCodigosError.RecetaNoEncontrada)]
    [InlineData(HttpStatusCode.BadRequest, "VALIDATION_ERROR", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(HttpStatusCode.BadRequest, "OTRO_CODIGO", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(HttpStatusCode.Conflict, "RECETA_INVALIDA", clsCodigosError.CosteoNexoRechazada)]
    [InlineData(HttpStatusCode.Unauthorized, "SESION_INVALIDA", clsCodigosError.NexoSinPermiso)]
    [InlineData(HttpStatusCode.Forbidden, "SIN_PERMISO_CABINA_FLETES", clsCodigosError.NexoSinPermiso)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, null, clsCodigosError.ArchivoDemasiadoGrande)]
    [InlineData(HttpStatusCode.InternalServerError, "INTERNAL_ERROR", clsCodigosError.NexoNoDisponible)]
    public async Task TraduceLosErroresDeNexo(HttpStatusCode estado, string? codigoNexo, string esperado)
    {
        var cuerpo = codigoNexo is null ? string.Empty : $$"""{"code":"{{codigoNexo}}","message":"Mensaje de NEXO para {{codigoNexo}}."}""";
        var nexo = new clsNexoClientesFalso().Responder(estado, cuerpo).Responder(estado, cuerpo).Responder(estado, cuerpo);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.GuardarRecetaLecturaAsync(_usuarioId,
            new clsPeticionGuardarReceta(new clsArchivoImportacion("a.xlsx", [1]), "{}", "900123456", null, null), Ct);

        resultado.Error.Codigo.Should().Be(esperado);
        if (esperado == codigoNexo)
        {
            resultado.Error.Mensaje.Should().Be($"Mensaje de NEXO para {codigoNexo}.", "el código pasa tal cual con el mensaje de NEXO");
        }

        resultado.Error.Detalle.Should().NotContain(IdExterno.ToString(System.Globalization.CultureInfo.InvariantCulture), "el detalle no lleva la identidad");
    }

    [Fact]
    public async Task BorrarUnaRecetaQueNoExisteLlegaConElCodigoDeNexo()
    {
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.EliminarRecetaLecturaAsync(_usuarioId, Guid.NewGuid(), Ct);
        var plantilla = await importacion.EliminarPlantillaMapeoAsync(_usuarioId, Guid.NewGuid(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.RecetaNoEncontrada);
        plantilla.Error.Codigo.Should().Be(clsCodigosError.PlantillaNoEncontrada);
    }

    [Fact]
    public async Task UnaRecetaQueNoEsJsonLaRechazaNexoConRecetaInvalida()
    {
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("a.xlsx", [1]), null, null, "{no es json"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.RecetaInvalida);
        nexo.Llamadas(HttpMethod.Post, clsImportacionExcelNexo.RutaLeerEstructura).Should().Be(1, "Cotifly no valida la receta: la valida NEXO");
    }

    [Fact]
    public async Task UnFalloDeRedOUnTiempoAgotadoEsNoDisponible()
    {
        var nexo = new clsNexoClientesFalso()
            .Fallar(new HttpRequestException("conexión rechazada"))
            .Fallar(new TaskCanceledException("tiempo agotado"));
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var red = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);
        var tiempo = await importacion.NormalizarValoresAsync(_usuarioId, "900123456", [new clsValorANormalizar("ciudad", "Cali")], Ct);

        red.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        tiempo.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        tiempo.Error.Detalle.Should().Contain("tiempo agotado");
    }

    [Fact]
    public async Task UnJsonInesperadoEsNoDisponible()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, "<html>proxy</html>");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    [Fact]
    public async Task ElDiccionarioMandaElNitYLosFiltros()
    {
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var terminos = await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, "900.123.456-7",
            new clsFiltroDiccionarioCliente(Tipo: "valor", Q: "tracto", SoloActivos: true), Ct);
        var tipoInvalido = await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, null, new clsFiltroDiccionarioCliente(Tipo: "otro"), Ct);

        terminos.Valor.Should().Contain(t => t.Termino == "Tractomula" && t.Concepto == "3S3");
        var consulta = nexo.Peticiones.First(p => p.Url.AbsolutePath == clsImportacionExcelNexo.RutaDiccionarioCliente).Url.Query;
        consulta.Should().Contain("clienteNit=900123456").And.Contain("tipo=valor").And.Contain("q=tracto").And.Contain("soloActivos=true");
        tipoInvalido.Error.Codigo.Should().Be(clsCodigosError.TipoTerminoInvalido);
    }

    [Fact]
    public async Task LaPlantillaSeGuardaPorNitYLaSiguienteSugerenciaLaReconoce()
    {
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var guardada = await importacion.GuardarPlantillaMapeoAsync(_usuarioId, new clsPeticionPlantillaMapeo(
            "900.123.456-7", "Pliego 2026", ["ORIGEN", "DESTINO", "TON"],
            [new(0, "origen", null), new(1, "destino", null), new(2, "peso", "peso_toneladas_a_kg")]), Ct);
        var lista = await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct);
        var sugerencia = await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct);

        guardada.Valor.Creada.Should().BeTrue();
        lista.Valor.Should().ContainSingle().Which.Columnas.Should().HaveCount(3);
        sugerencia.Valor.MotivoSinIa.Should().Be("PLANTILLA_RECONOCIDA");
        sugerencia.Valor.PlantillaId.Should().Be(guardada.Valor.Plantilla.Id);
    }

    [Fact]
    public async Task UnNitDesconocidoAlGuardarLlegaComoClienteNitInvalido()
    {
        var nexo = new clsNexoFalso();
        nexo.NitsDesconocidos.Add("123");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.GuardarPlantillaMapeoAsync(_usuarioId,
            new clsPeticionPlantillaMapeo("123", null, ["ORIGEN", "DESTINO"], [new(0, "origen", null), new(1, "destino", null)]), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteNitInvalido);
    }

    // --- Forma de las respuestas ---

    [Fact]
    public async Task NormalizarValoresTraeLosCandidatosDeUnaCiudadAmbigua()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, """
            {"iaUsada":true,"motivoSinIa":null,"totalDistintos":2,"valores":[
              {"campo":"ciudad","valor":"San José","estado":"ambiguo","codigo":null,"descripcion":null,"municipioId":null,"origen":"catalogo",
               "confianza":0.5,"nota":"Hay varias","candidatos":[{"municipioId":101,"codigoDane":"05656","municipio":"San José de la Montaña","departamento":"Antioquia"},
               {"municipioId":102,"codigoDane":null,"municipio":null,"departamento":null}]},
              {"campo":"tipoVehiculo","valor":"tracto","estado":"resuelto","codigo":"TRACTO","descripcion":"Tractocamión","municipioId":null,
               "origen":"ia","confianza":0.8,"nota":null,"candidatos":null}]}
            """);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.NormalizarValoresAsync(_usuarioId, "900.123.456-7",
            [new("ciudad", "San José"), new("tipoVehiculo", "tracto")], Ct);

        resultado.Valor.TotalDistintos.Should().Be(2);
        var ciudad = resultado.Valor.Valores[0];
        ciudad.Estado.Should().Be("ambiguo");
        ciudad.Candidatos.Should().HaveCount(2);
        ciudad.Candidatos![0].Should().Be(new clsCandidatoCiudad(101, "05656", "San José de la Montaña", "Antioquia"));
        ciudad.Candidatos[1].CodigoDane.Should().BeEmpty("un texto nulo de NEXO llega vacío");
        resultado.Valor.Valores[1].Codigo.Should().Be("TRACTO");
        resultado.Valor.Valores[1].Candidatos.Should().BeNull();
        var cuerpo = JsonNode.Parse(nexo.Peticiones.Should().ContainSingle().Subject.Cuerpo!)!;
        cuerpo["clienteNit"]!.GetValue<string>().Should().Be("900123456");
        cuerpo["valores"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task AplicarMapeoTraeLasPosicionesYLosErroresPorFila()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, """
            {"totalFilas":3,"filasVacias":0,"filasConErrores":1,"lineas":[{"fila":2,"origen":"Cali","destino":"Bogotá","peso":1200,
              "posiciones":[{"descripcion":"Estiba","cantidad":2,"largoCm":120,"anchoCm":100,"altoCm":150,"pesoKg":600,"codigoTipoMercancia":"GEN"}]}],
             "errores":[{"fila":3,"campo":"destino","codigo":"CIUDAD_NO_ENCONTRADA","mensaje":"No existe"},{"fila":4,"campo":null,"codigo":null,"mensaje":null}],
             "totalErrores":2}
            """);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.AplicarMapeoAsync(_usuarioId,
            new clsPeticionAplicarMapeo("900123456", [new(0, "origen", null), new(1, "destino", null)], [["Cali", "Bogotá"]], 2), Ct);

        var posicion = resultado.Valor.Lineas.Should().ContainSingle().Subject.Posiciones.Should().ContainSingle().Subject;
        posicion.Should().Be(new clsPosicionImportada("Estiba", 2, 120m, 100m, 150m, 600m, "GEN"));
        resultado.Valor.Errores.Should().Equal(
            new clsErrorDeFila(3, "destino", "CIUDAD_NO_ENCONTRADA", "No existe"),
            new clsErrorDeFila(4, null, string.Empty, string.Empty));
        resultado.Valor.TotalErrores.Should().Be(2);
    }

    [Fact]
    public async Task UnFormatoQueCambioTraeLosTitulosQueFaltanYLosNuevos()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, """
            {"archivo":null,"origenReceta":"guardada","similitud":0.9,"recetaGuardadaId":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","iaUsada":false,
             "motivoSinIa":null,"receta":{"version":1},"resumen":null,"dudas":null,"hojasDelArchivo":null,
             "tablas":[{"nombre":"H1","filaEncabezado":3,"encabezados":["A",null],"filas":[["x",null],null],"totalFilas":null}],
             "avisos":["columna vacía"],"cambios":{"faltan":["PESO"],"nuevos":null}}
            """);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var lectura = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("EPM.xlsx", [1]), "890904996", null, null), Ct);

        lectura.Valor.Archivo.Should().Be("EPM.xlsx", "sin nombre de NEXO queda el del archivo enviado");
        lectura.Valor.Cambios.Should().NotBeNull();
        lectura.Valor.Cambios!.Faltan.Should().Equal("PESO");
        lectura.Valor.Cambios.Nuevos.Should().BeEmpty();
        lectura.Valor.Avisos.Should().Equal("columna vacía");
        var tabla = lectura.Valor.Tablas.Should().ContainSingle().Subject;
        tabla.Encabezados.Should().Equal("A", string.Empty);
        tabla.Filas.Should().HaveCount(2);
        tabla.Filas[1].Should().BeEmpty();
        tabla.TotalFilas.Should().Be(2, "sin totalFilas cuenta las filas recibidas");
    }

    [Fact]
    public async Task AvisosYCambiosConOtraFormaNoTumbanLaLectura()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, """
            {"archivo":"a.xlsx","origenReceta":"ia","iaUsada":true,"receta":{"version":1},"tablas":[],
             "avisos":["texto",{"codigo":"FILA_VACIA","mensaje":"Fila 5 vacía"},{"codigo":"SIN_MENSAJE"},7],
             "cambios":{"faltan":["PESO",{"titulo":"VALOR"}],"nuevos":"no es lista","otro":1}}
            """)
            .Responder(HttpStatusCode.OK, """{"archivo":"a.xlsx","origenReceta":"ia","iaUsada":true,"receta":{},"avisos":"un aviso","cambios":["raro"]}""");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;
        var peticion = new clsPeticionLeerEstructura(new clsArchivoImportacion("a.xlsx", [1]), null, null, null);

        var primera = await importacion.LeerEstructuraAsync(_usuarioId, peticion, Ct);
        var segunda = await importacion.LeerEstructuraAsync(_usuarioId, peticion, Ct);

        primera.Valor.Avisos.Should().Equal("texto", "Fila 5 vacía", """{"codigo":"SIN_MENSAJE"}""", "7");
        primera.Valor.Cambios!.Faltan.Should().Equal("PESO", """{"titulo":"VALOR"}""");
        primera.Valor.Cambios.Nuevos.Should().BeEmpty();
        segunda.Valor.Avisos.Should().Equal("un aviso");
        segunda.Valor.Cambios.Should().BeNull("una forma que no es un objeto no se adivina");
    }

    [Theory]
    [InlineData("plantillas", "[null]")]
    [InlineData("recetas", "[null]")]
    [InlineData("diccionario", "[null]")]
    [InlineData("aplicar", """{"totalFilas":1,"lineas":[null],"errores":[]}""")]
    [InlineData("aplicar", """{"totalFilas":1,"lineas":[{"fila":2,"origen":"Cali","destino":"Bogotá","posiciones":[null]}],"errores":[null]}""")]
    [InlineData("normalizar", """{"iaUsada":true,"totalDistintos":1,"valores":[{"campo":"ciudad","valor":"X","estado":"ambiguo","candidatos":[null]}]}""")]
    [InlineData("sugerir", """{"iaUsada":true,"columnas":[null],"dudas":[]}""")]
    public async Task UnNuloDentroDeUnaListaEsUnaRespuestaInvalidaNoUnaExcepcion(string llamada, string json)
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, json);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        clsErrorDominio error = llamada switch
        {
            "plantillas" => (await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct)).Error,
            "recetas" => (await importacion.ListarRecetasLecturaAsync(_usuarioId, "900123456", Ct)).Error,
            "diccionario" => (await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, "900123456", null, Ct)).Error,
            "aplicar" => (await importacion.AplicarMapeoAsync(_usuarioId,
                new clsPeticionAplicarMapeo("900123456", [new(0, "origen", null)], [["Cali"]], null), Ct)).Error,
            "normalizar" => (await importacion.NormalizarValoresAsync(_usuarioId, null, [new("ciudad", "X")], Ct)).Error,
            _ => (await importacion.SugerirMapeoAsync(_usuarioId, _sugerir, Ct)).Error,
        };

        error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    // --- Tamaño y tiempo de la respuesta ---

    [Fact]
    public async Task UnaRespuestaMasGrandeQueElLimiteEsNoDisponible()
    {
        var grande = "[" + string.Join(',', Enumerable.Repeat("""{"id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301","nombre":"xxxxxxxxxxxxxxxxxxxx"}""", 20_000)) + "]";
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, grande);
        var importacion = Importacion(nexo, out var proveedor, new() { ["ImportacionInteligente:LimiteRespuestaMegabytes"] = "1" });
        using var limpieza = proveedor;

        var resultado = await importacion.ListarPlantillasMapeoAsync(_usuarioId, "900123456", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        resultado.Error.Detalle.Should().Contain("demasiado grande");
    }

    [Fact]
    public async Task UnCuerpoQueNoTerminaDeLlegarSeCortaConElTiempoDelIntento()
    {
        var importacion = Importacion(new clsNexoCuerpoColgado(), out var proveedor, new() { ["ImportacionInteligente:TimeoutSegundos"] = "10" });
        using var limpieza = proveedor;
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var resultado = await importacion.ObtenerOpcionesDiccionarioAsync(_usuarioId, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "el cuerpo se lee dentro del tiempo del intento");
    }

    // --- Reintentos exactos y cancelación ---

    [Fact]
    public async Task TresFallosSeguidosEnAplicarMapeoSonExactamenteDosPeticiones()
    {
        var nexo = new clsNexoClientesFalso()
            .Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.AplicarMapeoAsync(_usuarioId,
            new clsPeticionAplicarMapeo("900123456", [new(0, "origen", null)], [["Cali"]], null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().HaveCount(2, "un intento y un solo reintento");
    }

    [Fact]
    public async Task LosDeleteYGuardarLaPlantillaNoSeReintentan()
    {
        var nexo = new clsNexoClientesFalso()
            .Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable)
            .Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.ServiceUnavailable);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var receta = await importacion.EliminarRecetaLecturaAsync(_usuarioId, Guid.NewGuid(), Ct);
        var plantilla = await importacion.EliminarPlantillaMapeoAsync(_usuarioId, Guid.NewGuid(), Ct);
        var guardar = await importacion.GuardarPlantillaMapeoAsync(_usuarioId,
            new clsPeticionPlantillaMapeo("900123456", null, ["ORIGEN"], [new(0, "origen", null)]), Ct);

        new[] { receta.Error.Codigo, plantilla.Error.Codigo, guardar.Error.Codigo }.Should().AllBe(clsCodigosError.NexoNoDisponible);
        nexo.Peticiones.Should().HaveCount(3, "una sola petición por llamada");
    }

    [Fact]
    public async Task LaCancelacionDeQuienLlamaSePropaga()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, SugerenciaJson);
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var sugerir = () => importacion.SugerirMapeoAsync(_usuarioId, _sugerir, cancelacion.Token);

        await sugerir.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- Logs ---

    [Fact]
    public async Task LosLogsNoLlevanElNitNiElMensajeDeNexoNiLaUrl()
    {
        using var logs = new clsProveedorLogsFalso();
        var nexo = new clsNexoClientesFalso()
            .Responder(HttpStatusCode.OK, "[]")
            .Responder(HttpStatusCode.BadRequest, """{"code":"RECETA_INVALIDA","message":"La columna 900123456 de ACME S.A.S. no cuadra"}""");
        var importacion = Importacion(nexo, out var proveedor, logs: logs);
        using var limpieza = proveedor;

        await importacion.ObtenerDiccionarioClienteAsync(_usuarioId, "900123456", new clsFiltroDiccionarioCliente(Q: "contenedor"), Ct);
        var rechazo = await importacion.ListarRecetasLecturaAsync(_usuarioId, "900123456", Ct);

        rechazo.Error.Codigo.Should().Be(clsCodigosError.RecetaInvalida);
        logs.Registros.Should().NotBeEmpty();
        logs.Registros.Should().NotContain(r => r.Registro.Mensaje.Contains("900123456") || r.Registro.Mensaje.Contains("ACME")
            || r.Registro.Mensaje.Contains("contenedor"));
        logs.Registros.Should().NotContain(r => r.Categoria.StartsWith("System.Net.Http.HttpClient." + clsImportacionExcelNexo.NombreCliente,
            StringComparison.Ordinal));
    }

    // --- Soporte ---

    private static async Task<TimeSpan> TiempoDelIntento(HttpStandardResilienceOptions opciones, HttpRequestMessage peticion)
    {
        var contexto = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);
        try
        {
            contexto.SetRequestMessage(peticion);
            return await opciones.AttemptTimeout.TimeoutGenerator!(new TimeoutGeneratorArguments(contexto));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(contexto);
        }
    }

    private static async Task<bool> CuentaParaElCircuito(HttpStandardResilienceOptions opciones, HttpStatusCode estado)
    {
        var contexto = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);
        using var respuesta = new HttpResponseMessage(estado);
        try
        {
            return await opciones.CircuitBreaker.ShouldHandle(
                new CircuitBreakerPredicateArguments<HttpResponseMessage>(contexto, Outcome.FromResult(respuesta)));
        }
        finally
        {
            ResilienceContextPool.Shared.Return(contexto);
        }
    }

    /// <summary>NEXO que manda las cabeceras de un 200 y nunca termina el cuerpo.</summary>
    private sealed class clsNexoCuerpoColgado : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new clsFlujoColgado()) });
    }

    private sealed class clsFlujoColgado : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static IClsImportacionExcelNexo Importacion(
        HttpMessageHandler nexo, out ServiceProvider proveedor, Dictionary<string, string?>? configuracion = null, ILoggerProvider? logs = null)
    {
        var valores = new Dictionary<string, string?>(configuracion ?? []) { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase };
        proveedor = Proveedor(valores, s =>
        {
            if (logs is not null)
            {
                s.AddLogging(b => b.AddProvider(logs));
            }

            s.AddHttpClient(clsImportacionExcelNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);
            var usuarios = Substitute.For<IClsUsuarioWebRepository>();
            usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
                .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, IdExterno).Valor);
            usuarios.ObtenerPorIdAsync(_usuarioSinExterno, Arg.Any<CancellationToken>())
                .Returns(clsUsuarioWeb.Crear("sinexterno", "Sin Externo", "sin@boterosoto.com.co", null, DateTimeOffset.UtcNow).Valor);
            s.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
        });
        return proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsImportacionExcelNexo>();
    }

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores, Action<IServiceCollection>? configurar = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }
}
