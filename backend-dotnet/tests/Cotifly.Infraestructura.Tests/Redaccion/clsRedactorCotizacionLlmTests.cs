using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Redaccion.DTOs;
using Cotifly.Aplicacion.Redaccion.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Cotifly.Infraestructura.Redaccion.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Redaccion;

/// <summary>
/// Redactor del asunto y la introducción del PDF (ADR-018 §2a) de punta a punta sin red: <see cref="clsRedaccionCotizacionService"/>
/// sobre <see cref="clsRedactorCotizacionLlm"/> y un <see cref="IChatClient"/> guionizado. La IA nunca recibe montos y, si tarda, falla
/// o su salida no pasa la validación, salen los textos estándar de la variante (exactamente los del PDF).
/// </summary>
public sealed class clsRedactorCotizacionLlmTests
{
    private const string Folio = "PCN-900555111-AP-05102026-001";
    private const string AsuntoIa = "Cotización transporte Buenaventura - Bogotá D.C.";
    private const string IntroIa = "Apreciado cliente: agradecemos la oportunidad de presentarle nuestra propuesta de transporte nacional.\n\n"
                                   + "Con gusto atenderemos su ruta en tractocamión 3S3, con el servicio de cargue incluido.";

    private static readonly clsTextosBase _estandar = new("presentación oferta de servicios de Transporte Nacional", clsTextosEstandar.IntroduccionEstandar(false));

    private readonly clsChatClientGuionizado _modelo = new();
    private readonly clsOpcionesAgente _agente = new();
    private readonly clsOpcionesIA _ia = new() { Proveedor = "openai_compatible", Modelo = "gemini/gemini-3.5-flash-lite" };
    private readonly clsOpcionesRedaccion _redaccion = new();
    private readonly clsInterruptorJsonSchema _interruptor = new();
    private readonly clsRelojFalso _reloj = new();
    private TimeProvider _tiempo = TimeProvider.System;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnaRespuestaValidaQuedaConFuenteIaSinRespaldoConProveedorModeloYVersionDelPrompt()
    {
        _modelo.Responder(Json(AsuntoIa, IntroIa));

        var resultado = await Servicio().RedactarAsync(Contexto(), _estandar, Ct);

        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteIa);
        resultado.Asunto.Should().Be(AsuntoIa);
        resultado.Introduccion.Should().Be(IntroIa);
        resultado.Respaldo.Should().BeFalse();
        resultado.Proveedor.Should().Be("openai_compatible");
        resultado.Modelo.Should().Be("gemini/gemini-3.5-flash-lite");
        resultado.VersionPrompt.Should().Be(clsRedactorCotizacionLlm.VersionPrompt).And.HaveLength(16);
        var llamada = _modelo.Llamadas.Single();
        llamada.Opciones!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>()
            .Which.SchemaName.Should().Be(clsReglasTextosCotizacion.NombreEsquema, "con IA:SoportaJsonSchema la salida va con el esquema");
        llamada.Herramientas.Should().BeEmpty("el redactor no usa herramientas");
        llamada.Sistema.Should().Contain("Nunca escribas montos").And.Contain("No inventes condiciones");
    }

    [Fact]
    public async Task LaIaNuncaRecibeCostosMargenPreciosTrmNiElComercial()
    {
        _modelo.Responder(Json(AsuntoIa, IntroIa));
        var cotizacion = CotizacionCosteada();

        await Servicio().RedactarAsync(clsTextosCosteoService.Contexto(cotizacion, "Nacional"), _estandar, Ct);

        var enviado = string.Join("\n", _modelo.Llamadas.Single().Mensajes.Select(m => m.Text));
        var usuario = _modelo.Llamadas.Single().Mensajes.Single(m => m.Role == ChatRole.User).Text;
        usuario.Should().Contain("CLIENTE SAS").And.Contain("Nacional").And.Contain("Buenaventura").And.Contain("Bogotá D.C.")
            .And.Contain("3S3").And.Contain("Cargue").And.Contain("USD");
        foreach (var prohibido in new[] { "2550000", "2.550.000", "2550", "3000000", "3.000.000", "746", "0.15", "0,15", "4000", "4.000", "1500", "85000", "100000" })
        {
            enviado.Should().NotContain(prohibido, "la IA nunca recibe costos, margen, precios ni la TRM (ADR-018 §2a)");
        }

        foreach (var campo in new[] { "costo", "precio", "margen", "trm", "folio", "nit", "comercial" })
        {
            usuario.Should().NotContainEquivalentOf($"\"{campo}", "el contexto no tiene campos de dinero, identificadores ni el comercial");
        }

        using var json = JsonDocument.Parse(usuario[usuario.IndexOf('{', StringComparison.Ordinal)..]);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(["cliente", "linea_negocio", "rutas", "tipo_vehiculo", "conexos", "moneda"]);
    }

    [Fact]
    public void CadaTextoDelContextoLlegaRecortadoA150Caracteres()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = Folio, ClienteNombre = new string('C', 300) }).Valor;
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 1);
        cotizacion.PrimeraRuta!.AgregarOActualizarConexo("CX-1", new string('X', 400), 1m, 1m);

        var contexto = clsTextosCosteoService.Contexto(cotizacion, new string('L', 200));

        contexto.Cliente.Should().HaveLength(clsContextoRedaccion.LargoMaximoCampo);
        contexto.LineaNegocio.Should().HaveLength(clsContextoRedaccion.LargoMaximoCampo);
        contexto.Conexos.Single().Should().HaveLength(clsContextoRedaccion.LargoMaximoCampo);
    }

    [Fact]
    public async Task SiLaIaTardaMasDelTiempoMaximoSeUsanLosEstandarSinEsperarLos15SegundosReales()
    {
        var tiempo = new clsTiempoManual();
        _tiempo = tiempo;
        _modelo.Demorar(TimeSpan.FromMinutes(5)).Responder(Json(AsuntoIa, IntroIa));
        var cronometro = Stopwatch.StartNew();

        var tarea = Servicio().RedactarAsync(Contexto(), _estandar, Ct);
        while (tiempo.Pendientes < 2 && cronometro.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10, Ct);
        }

        tiempo.Avanzar(TimeSpan.FromSeconds(14));
        tarea.IsCompleted.Should().BeFalse("a los 14 s todavía espera al modelo");
        tiempo.Avanzar(TimeSpan.FromSeconds(1));
        var resultado = await tarea.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        cronometro.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "el tiempo máximo corre sobre el TimeProvider inyectado");
        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteEstandar);
        resultado.Respaldo.Should().BeTrue();
        resultado.MotivoRespaldo.Should().Be(clsRedaccionCotizacionService.MotivoTiempoAgotado);
        resultado.Asunto.Should().Be(_estandar.Asunto);
        resultado.Introduccion.Should().Be(_estandar.Introduccion);
    }

    [Fact]
    public async Task ConLaIaCaidaSeUsanLosTextosEstandar()
    {
        _modelo.Fallar(new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable));

        var resultado = await Servicio().RedactarAsync(Contexto(), _estandar, Ct);

        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteEstandar);
        resultado.MotivoRespaldo.Should().Be(clsRedaccionCotizacionService.MotivoIaNoDisponible);
    }

    [Fact]
    public async Task SinIaConfiguradaSeUsanLosTextosEstandarYSeInformaNoConfigurado()
    {
        using var sinIa = clsChatClientFactory.Crear(new clsOpcionesIA(), NullLoggerFactory.Instance, TimeProvider.System);
        var redactor = new clsRedactorCotizacionLlm(sinIa, Options.Create(_agente), Options.Create(new clsOpcionesIA()), _interruptor, _reloj,
            NullLogger<clsRedactorCotizacionLlm>.Instance);

        var resultado = await Servicio(redactor).RedactarAsync(Contexto(), _estandar, Ct);

        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteEstandar);
        resultado.Proveedor.Should().Be(clsRedactorCotizacionLlm.NoConfigurado);
    }

    public static TheoryData<string, string> SalidasInvalidas() => new()
    {
        { Json(AsuntoIa, "Nuestra tarifa es de $ 3.000.000 por viaje."), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "El valor del flete es 3.000.000 COP."), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "El flete queda en 2550000 para su ruta."), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "Cotizamos con una TRM de 3.987,65 para su operación."), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "Aplicamos un margen del 15 % en esta ruta."), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "Tiene un 10% de beneficio en esta ruta."), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "La propuesta asciende a tres millones de pesos."), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "El servicio vale 500 euros por viaje."), clsReglasTextosCotizacion.Monto },
        { Json("Cotización en dólares", IntroIa), clsReglasTextosCotizacion.Monto },
        { Json(AsuntoIa, "Comuníquese al 300 123 4567 para coordinar."), clsReglasTextosCotizacion.Telefono },
        { Json(AsuntoIa, "Llámenos a nuestro celular para coordinar."), clsReglasTextosCotizacion.Telefono },
        { Json(AsuntoIa, "Conozca más en https://ejemplo.com sobre nosotros."), clsReglasTextosCotizacion.Url },
        { Json(AsuntoIa, "Visite www.ejemplo.com.co para más información."), clsReglasTextosCotizacion.Url },
        { Json(AsuntoIa, "Visite botero[.]com para más información."), clsReglasTextosCotizacion.Url },
        { Json(AsuntoIa, "Visite boterosoto punto com para más información."), clsReglasTextosCotizacion.Url },
        { Json(AsuntoIa, "Escríbanos a alguien@ejemplo.com con sus dudas."), clsReglasTextosCotizacion.Correo },
        { Json(AsuntoIa, "Escríbanos a ventas (arroba) boterosoto con sus dudas."), clsReglasTextosCotizacion.Correo },
        { Json(AsuntoIa, "Su carga viaja asegurada durante todo el trayecto."), clsReglasTextosCotizacion.CondicionNoPermitida },
        { Json(AsuntoIa, "Incluimos póliza de mercancías para su operación."), clsReglasTextosCotizacion.CondicionNoPermitida },
        { Json(AsuntoIa, "Ofrecemos pago a sesenta días sin recargo."), clsReglasTextosCotizacion.CondicionNoPermitida },
        { Json(AsuntoIa, "Le damos un descuento especial por ser cliente."), clsReglasTextosCotizacion.CondicionNoPermitida },
        { Json(AsuntoIa, "Garantizamos la entrega en 3 días hábiles."), clsReglasTextosCotizacion.CondicionNoPermitida },
        { Json(AsuntoIa, "La oferta tiene vigencia hasta fin de mes."), clsReglasTextosCotizacion.CondicionNoPermitida },
        { Json(new string('a', 121), IntroIa), clsReglasTextosCotizacion.AsuntoLargo },
        { Json(AsuntoIa, new string('b', 901)), clsReglasTextosCotizacion.IntroduccionLarga },
        { Json(" ", IntroIa), clsReglasTextosCotizacion.AsuntoVacio },
        { """{"asunto":"Cotización","introduccion":"Hola","precio":3000000}""", clsReglasTextosCotizacion.JsonInvalido },
        { """{"asunto":"Cotización"}""", clsReglasTextosCotizacion.JsonInvalido },
        { """{"asunto":"Cotización","introduccion":42}""", clsReglasTextosCotizacion.JsonInvalido },
        { """{"asunto":"A","asunto":"B","introduccion":"Hola"}""", clsReglasTextosCotizacion.JsonInvalido },
        { "Claro, aquí tiene la introducción: Apreciado cliente...", clsReglasTextosCotizacion.JsonInvalido },
        { "[]", clsReglasTextosCotizacion.JsonInvalido },
    };

    [Theory]
    [MemberData(nameof(SalidasInvalidas))]
    public async Task UnaSalidaInvalidaDeLaIaSeDescartaYSeUsanLosTextosEstandar(string salida, string motivo)
    {
        _modelo.Responder(salida);

        var resultado = await Servicio().RedactarAsync(Contexto(), _estandar, Ct);

        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteEstandar);
        resultado.MotivoRespaldo.Should().Be(motivo);
        resultado.Introduccion.Should().Be(_estandar.Introduccion);
    }

    [Fact]
    public async Task LosNombresDelContextoNoCuentanComoCifras()
    {
        var contexto = Contexto() with { Cliente = "3M Colombia S.A.", Conexos = ["Escolta 2000"] };
        _modelo.Responder(Json("Cotización para 3M Colombia S.A.", "Apreciado equipo de 3M Colombia S.A.: incluimos el servicio Escolta 2000 en su ruta."));

        var resultado = await Servicio().RedactarAsync(contexto, _estandar, Ct);

        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteIa, "los textos que vienen literalmente del contexto se permiten");
    }

    [Fact]
    public async Task SeAceptaElJsonEntreCercasYSeNormalizanLosParrafos()
    {
        _modelo.Responder("```json\n" + Json("  Cotización   transporte\nBuenaventura  ", "Párrafo uno\ncontinúa.\n\n\n\nPárrafo dos.") + "\n```");

        var resultado = await Servicio().RedactarAsync(Contexto(), _estandar, Ct);

        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteIa);
        resultado.Asunto.Should().Be("Cotización transporte Buenaventura");
        resultado.Introduccion.Should().Be("Párrafo uno continúa.\n\nPárrafo dos.");
    }

    [Fact]
    public async Task SinJsonSchemaElEsquemaVaEnLaInstruccionYNoEnResponseFormat()
    {
        _agente.SoportaJsonSchema = false;
        _modelo.Responder(Json(AsuntoIa, IntroIa));

        var resultado = await Servicio().RedactarAsync(Contexto(), _estandar, Ct);

        resultado.Fuente.Should().Be(clsTextosCotizacion.FuenteIa);
        var llamada = _modelo.Llamadas.Single();
        llamada.Opciones!.ResponseFormat.Should().BeNull();
        llamada.Sistema.Should().Contain("\"introduccion\"").And.Contain("additionalProperties");
    }

    [Fact]
    public async Task SiElProveedorRechazaElEsquemaSeRepiteSinFormatoYSeApagaParaLasSiguientes()
    {
        _modelo.RechazarJsonSchema(new HttpRequestException("Invalid parameter: 'response_format' of type 'json_schema' is not supported.", null, HttpStatusCode.BadRequest))
            .Responder(Json(AsuntoIa, IntroIa))
            .Responder(Json(AsuntoIa, IntroIa));

        var primera = await Servicio().RedactarAsync(Contexto(), _estandar, Ct);
        var segunda = await Servicio().RedactarAsync(Contexto(), _estandar, Ct);

        primera.Fuente.Should().Be(clsTextosCotizacion.FuenteIa);
        segunda.Fuente.Should().Be(clsTextosCotizacion.FuenteIa);
        _modelo.Llamadas.Should().HaveCount(3, "la segunda redacción ya no envía el esquema rechazado");
        _modelo.Llamadas[2].Opciones!.ResponseFormat.Should().BeNull();
        _interruptor.Habilitado(_reloj.AhoraUtc).Should().BeFalse("el apagado lo comparte con el agente");

        _reloj.Avanzar(TimeSpan.FromMinutes(_agente.MinutosSinJsonSchema));
        _interruptor.Habilitado(_reloj.AhoraUtc).Should().BeTrue("pasado IA:MinutosSinJsonSchema se vuelve a probar con esquema");
    }

    [Fact]
    public void ConJsonSchemaEstrictoElEsquemaNoTraeLongitudes()
    {
        clsReglasTextosCotizacion.EsquemaEstricto.GetRawText().Should().NotContain("maxLength");
        clsReglasTextosCotizacion.Esquema.GetRawText().Should().Contain("\"maxLength\":120").And.Contain("\"maxLength\":900");
    }

    [Fact]
    public void ElPromptEmbebidoPideTonoProfesionalEnEspanolSinMontosNiCondicionesInventadas()
    {
        clsRedactorCotizacionLlm.Prompt.Should().Contain("transporte de carga").And.Contain("español de Colombia")
            .And.Contain("Nunca escribas montos").And.Contain("No inventes condiciones").And.Contain("\"introduccion\"");
    }

    // Indicaciones del comercial para el redactor con IA.

    [Fact]
    public async Task LasIndicacionesVanAlModeloComoIndicacionesComercial()
    {
        _modelo.Responder(Json(AsuntoIa, IntroIa));
        var contexto = Contexto() with { IndicacionesComercial = "Tono cercano, resaltar la carga refrigerada." };

        await Servicio().RedactarAsync(contexto, _estandar, Ct);

        var usuario = _modelo.Llamadas.Single().Mensajes.Single(m => m.Role == ChatRole.User).Text;
        usuario.Should().Contain("indicaciones_comercial").And.Contain("Tono cercano, resaltar la carga refrigerada.");
    }

    [Fact]
    public async Task SinIndicacionesElContextoNoLlevaIndicacionesComercial()
    {
        _modelo.Responder(Json(AsuntoIa, IntroIa));

        await Servicio().RedactarAsync(Contexto(), _estandar, Ct);

        var usuario = _modelo.Llamadas.Single().Mensajes.Single(m => m.Role == ChatRole.User).Text;
        usuario.Should().NotContain("indicaciones_comercial", "sin indicaciones el contexto queda idéntico al de siempre");
    }

    [Fact]
    public void ElPromptTrataLasIndicacionesComoPreferenciaQueNoPasaSobreLasReglas()
    {
        clsRedactorCotizacionLlm.Prompt.Should().Contain("indicaciones_comercial").And.Contain("preferencia")
            .And.Contain("Nunca").And.Contain("ignora esa parte");
    }

    private clsRedaccionCotizacionService Servicio(clsRedactorCotizacionLlm? redactor = null) => new(
        redactor ?? new clsRedactorCotizacionLlm(_modelo, Options.Create(_agente), Options.Create(_ia), _interruptor, _reloj,
            NullLogger<clsRedactorCotizacionLlm>.Instance),
        Options.Create(_redaccion),
        _tiempo,
        NullLogger<clsRedaccionCotizacionService>.Instance);

    private static clsContextoRedaccion Contexto() =>
        new("CLIENTE SAS", "Nacional", [new clsRutaRedaccion("Buenaventura", "Bogotá D.C.")], "3S3", ["Cargue"], "COP");

    /// <summary>
    /// Cotización en USD costeada (flete 2.550.000, margen 15 %, TRM 4.000) y un conexo (Cargue, 85.000). Los conexos son filas de
    /// la ruta (todos van en el precio).
    /// </summary>
    private static clsCotizacionSolicitud CotizacionCosteada()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: Folio, usuarioId: Guid.NewGuid(), moneda: "USD", trm: 4_000m);
        cotizacion.ActualizarClienteDelCatalogo("CLIENTE SAS", null);
        cotizacion.PrimeraRuta!.AgregarOActualizarConexo("CX-1", "Cargue", 85_000m, 1m);
        cotizacion.RecalcularTotales(clsCotizacionMuestra.Ahora);
        return cotizacion;
    }

    private static string Json(string asunto, string introduccion) =>
        JsonSerializer.Serialize(new { asunto, introduccion }, clsReglasRespuestaAgente.OpcionesJson);
}
