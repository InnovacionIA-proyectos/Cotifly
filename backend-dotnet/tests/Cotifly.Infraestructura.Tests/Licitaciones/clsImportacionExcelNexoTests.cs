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
using Microsoft.Extensions.Options;
using NSubstitute;

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
        opciones.TimeoutLecturaSegundos.Should().Be(180);
    }

    [Theory]
    [InlineData("ImportacionInteligente:TimeoutLecturaSegundos", "179")]
    [InlineData("ImportacionInteligente:TimeoutSegundos", "0")]
    [InlineData("ImportacionInteligente:TimeoutSegundos", "601")]
    public void UnTiempoFueraDeRangoImpideArrancar(string clave, string valor)
    {
        using var proveedor = Proveedor(new() { [clave] = valor });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesImportacionInteligente>>().Value;

        leer.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task LaNoDisponibleRespondeNexoNoDisponibleSinLlamar()
    {
        var resultado = await new clsImportacionExcelNoDisponible().LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("a.xlsx", [1]), null, null, null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    [Fact]
    public void LaTuberiaDaElTiempoLargoSoloALaLecturaYNoReintentaLoQueNoEstaMarcado()
    {
        var opciones = new HttpStandardResilienceOptions();

        clsImportacionExcelNexo.ConfigurarResiliencia(opciones, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180));

        opciones.AttemptTimeout.Timeout.Should().Be(TimeSpan.FromSeconds(90));
        opciones.TotalRequestTimeout.Timeout.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(180));
        opciones.TotalRequestTimeout.Timeout.Should().BeGreaterThan(opciones.AttemptTimeout.Timeout);
        opciones.CircuitBreaker.SamplingDuration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(360));
        clsImportacionExcelNexo.NombreCliente.Should().Be("NexoImportacion");
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
        const string receta = """{"version":1,"hojas":[{"hoja":"*","usar":true,"extra":{"a":[1,2]}}],"unir":false}""";
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK,
            $$"""[{"id":"{{Guid.NewGuid()}}","clienteId":"{{Guid.NewGuid()}}","clienteNombre":"EPM","nombre":null,"receta":{{receta}},"instrucciones":null,"usos":2,"ultimoUsoAt":null,"createdAt":"2026-11-05T15:00:00Z","updatedAt":null}]""");
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.ListarRecetasLecturaAsync(_usuarioId, "890.904.996-1", Ct);

        nexo.Peticiones.Should().ContainSingle().Which.Url.Query.Should().Be("?clienteNit=890904996");
        var guardada = resultado.Valor.Should().ContainSingle().Subject;
        guardada.Usos.Should().Be(2);
        guardada.Receta.GetRawText().Should().Be(JsonDocument.Parse(receta).RootElement.GetRawText(), "la receta es JSON opaco");
    }

    [Fact]
    public async Task LeerEstructuraReenviaElArchivoYLaRecetaSinTocarla()
    {
        const string receta = """{"version":1,"hojas":[{"hoja":"Hoja 1","usar":true,"campoDelFuturo":"x"}],"unir":false}""";
        var nexo = new clsNexoFalso();
        var importacion = Importacion(nexo, out var proveedor);
        using var limpieza = proveedor;

        var resultado = await importacion.LeerEstructuraAsync(_usuarioId,
            new clsPeticionLeerEstructura(new clsArchivoImportacion("EPM formulario.xlsx", [1, 2, 3]), "890.904.996-1", null, receta), Ct);

        var formulario = nexo.UltimoFormularioImportacion!;
        formulario["archivo"].Should().Be("EPM formulario.xlsx", "con su nombre original");
        formulario["receta"].Should().Be(receta, "la receta viaja tal cual");
        formulario["clienteNit"].Should().Be("890904996");
        formulario.Should().NotContainKey("instrucciones");
        resultado.Valor.OrigenReceta.Should().Be("manual");
        resultado.Valor.IaUsada.Should().BeFalse();
        JsonNode.DeepEquals(JsonNode.Parse(resultado.Valor.Receta.GetRawText()), JsonNode.Parse(receta)).Should().BeTrue();
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

    // --- Soporte ---

    private static IClsImportacionExcelNexo Importacion(HttpMessageHandler nexo, out ServiceProvider proveedor)
    {
        proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, s =>
        {
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
