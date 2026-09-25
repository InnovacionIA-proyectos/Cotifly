using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Infraestructura.Tests.Agente;

/// <summary>
/// Google Chat en Infraestructura, sin red (<see cref="clsGoogleFalso"/>): verificación del JWT con certificados falsos (port de
/// <c>test_jwt_*</c> y <c>test_google_verifier_*</c>), forma de la petición del publicador (port de
/// <c>test_construir_publicador_postea_a_chat_api</c>), worker resiliente (port de <c>test_worker_*</c>) y URL firmada del PDF.
/// </summary>
public sealed class clsGoogleChatInfraestructuraTests : IDisposable
{
    private readonly clsGoogleFalso _google = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _google.Dispose();

    private IHttpClientFactory Http()
    {
        var fabrica = Substitute.For<IHttpClientFactory>();
        fabrica.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(_google, disposeHandler: false));
        return fabrica;
    }

    private static IOptions<clsOpcionesGoogleChat> Opciones(clsOpcionesGoogleChat opciones) => Options.Create(opciones);

    private clsOpcionesGoogleChat OpcionesGoogle(bool verificar = true, bool conCuenta = true) => new()
    {
        ProjectNumber = clsGoogleFalso.ProjectNumber,
        VerificarJwt = verificar,
        UrlCertificados = clsGoogleFalso.UrlCertificados,
        ApiBase = clsGoogleFalso.ApiBase,
        SaJson = conCuenta ? _google.CuentaServicioJson() : null,
    };

    private clsVerificadorJwtGoogleChat Verificador(clsOpcionesGoogleChat? opciones = null) =>
        new(Http(), Opciones(opciones ?? OpcionesGoogle()), TimeProvider.System, NullLogger<clsVerificadorJwtGoogleChat>.Instance);

    // test_jwt_valido_ack_200 (fixture jwt_valido.txt firmada con el certificado falso)
    [Fact]
    public async Task ElJwtValidoDeLaFixturePasa() =>
        (await Verificador().VerificarAsync($"Bearer {clsGoogleFalso.Fixture("jwt_valido.txt")}", Ct)).Should().BeTrue();

    // test_jwt_invalido_401 (fixture jwt_invalido.txt: firma de otra clave)
    [Fact]
    public async Task ElJwtConLaFirmaDeOtraClaveNoPasa() =>
        (await Verificador().VerificarAsync($"Bearer {clsGoogleFalso.Fixture("jwt_invalido.txt")}", Ct)).Should().BeFalse();

    // test_jwt_ausente_401 + test_bearer_malformado_401
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("eyJhbGciOi.sin.bearer")]
    [InlineData("Basic abc")]
    [InlineData("Bearer ")]
    public async Task SinBearerNoPasa(string? autorizacion) =>
        (await Verificador().VerificarAsync(autorizacion, Ct)).Should().BeFalse();

    // test_google_verifier_pasa_audience_y_certs / test_google_verifier_emisor_inesperado
    [Fact]
    public async Task ExigeLaAudienciaDelProyectoElEmisorDeChatYLaVigencia()
    {
        var verificador = Verificador();

        (await verificador.VerificarAsync($"Bearer {_google.FirmarToken()}", Ct)).Should().BeTrue();
        (await verificador.VerificarAsync($"Bearer {_google.FirmarToken(audiencia: "999888")}", Ct)).Should().BeFalse();
        (await verificador.VerificarAsync($"Bearer {_google.FirmarToken(emisor: "atacante@evil.com")}", Ct)).Should().BeFalse();
        (await verificador.VerificarAsync($"Bearer {_google.FirmarToken(venceEn: TimeSpan.FromHours(-2))}", Ct)).Should().BeFalse();
    }

    // test_google_verifier_sin_project_number
    [Fact]
    public async Task SinNumeroDeProyectoNadaPasa() =>
        (await Verificador(OpcionesGoogle().ConProyecto(null)).VerificarAsync($"Bearer {_google.FirmarToken()}", Ct)).Should().BeFalse();

    // test_webhook_sin_jwt_cuando_verificacion_desactivada
    [Fact]
    public async Task ConLaVerificacionDesactivadaTodoPasa() =>
        (await Verificador(OpcionesGoogle(verificar: false)).VerificarAsync(null, Ct)).Should().BeTrue();

    [Fact]
    public async Task LosCertificadosSeLeenUnaVezYSeGuardanEnCache()
    {
        var verificador = Verificador();

        await verificador.VerificarAsync($"Bearer {_google.FirmarToken()}", Ct);
        await verificador.VerificarAsync($"Bearer {clsGoogleFalso.Fixture("jwt_valido.txt")}", Ct);

        verificador.Descargas.Should().Be(1);
        _google.Peticiones.Count(p => p.Url.ToString() == clsGoogleFalso.UrlCertificados).Should().Be(1);
    }

    // test_construir_publicador_postea_a_chat_api: URL, token de la cuenta de servicio, opción de respuesta e hilo.
    [Fact]
    public async Task ElPublicadorPosteaALaApiDeChatConElTokenDeLaCuentaDeServicioYElHilo()
    {
        var opciones = Opciones(OpcionesGoogle());
        var publicador = new clsPublicadorGoogleChat(Http(), new clsTokenCuentaServicioGoogle(Http(), opciones, TimeProvider.System), opciones, NullLogger<clsPublicadorGoogleChat>.Instance);

        await publicador.PublicarAsync("spaces/X", "spaces/X/threads/1", new JsonObject { ["text"] = "hola" }, Ct);
        await publicador.PublicarAsync("spaces/X", "spaces/X/threads/1", new JsonObject { ["text"] = "otra" }, Ct);

        var token = _google.Peticiones.Where(p => p.Url.ToString() == clsGoogleFalso.UrlToken).ToList();
        token.Should().ContainSingle("el token se reutiliza hasta que vence");
        token[0].Cuerpo.Should().Contain("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer").And.Contain("assertion=");
        var chat = _google.Peticiones.Where(p => p.Url.AbsolutePath == "/v1/spaces/X/messages").ToList();
        chat.Should().HaveCount(2);
        chat[0].Metodo.Should().Be(HttpMethod.Post);
        chat[0].Url.Query.Should().Be("?messageReplyOption=REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD");
        chat[0].Autorizacion.Should().Be($"Bearer {clsGoogleFalso.TokenAcceso}");
        var cuerpo = JsonNode.Parse(chat[0].Cuerpo!)!;
        cuerpo["text"]!.GetValue<string>().Should().Be("hola");
        cuerpo["thread"]!["name"]!.GetValue<string>().Should().Be("spaces/X/threads/1");
    }

    [Fact]
    public async Task LaAsercionDeLaCuentaDeServicioVaFirmadaConSuClave()
    {
        var opciones = Opciones(OpcionesGoogle());

        await new clsTokenCuentaServicioGoogle(Http(), opciones, TimeProvider.System).ObtenerAsync(Ct);

        var cuerpo = _google.Peticiones.Single(p => p.Url.ToString() == clsGoogleFalso.UrlToken).Cuerpo!;
        var asercion = Uri.UnescapeDataString(cuerpo.Split('&').Single(p => p.StartsWith("assertion=", StringComparison.Ordinal))["assertion=".Length..]);
        var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(asercion);
        jwt.Issuer.Should().Be("cotifly-bot@proyecto-falso.iam.gserviceaccount.com");
        jwt.Audiences.Should().Equal(clsGoogleFalso.UrlToken);
        jwt.GetClaim("scope").Value.Should().Be(clsTokenCuentaServicioGoogle.AlcanceChat);
        jwt.Kid.Should().Be("clave-sa-1");
        var validacion = await new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().ValidateTokenAsync(asercion, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateAudience = false,
            ValidateIssuer = false,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(_google.ClaveCuentaServicio.ExportParameters(false)),
        });
        validacion.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task SinCuentaDeServicioNoSePublicaNada()
    {
        var opciones = Opciones(OpcionesGoogle(conCuenta: false));
        var publicador = new clsPublicadorGoogleChat(Http(), new clsTokenCuentaServicioGoogle(Http(), opciones, TimeProvider.System), opciones, NullLogger<clsPublicadorGoogleChat>.Instance);

        await publicador.PublicarAsync("spaces/X", null, new JsonObject { ["text"] = "hola" }, Ct);

        _google.Peticiones.Should().BeEmpty();
    }

    // test_publicador_sin_space_falla + un space que intenta cambiar la URL
    [Theory]
    [InlineData("")]
    [InlineData("spaces/X/../../otra")]
    [InlineData("spaces/X?alt=otra")]
    public async Task UnSpaceInvalidoSeRechaza(string space)
    {
        var opciones = Opciones(OpcionesGoogle());
        var publicador = new clsPublicadorGoogleChat(Http(), new clsTokenCuentaServicioGoogle(Http(), opciones, TimeProvider.System), opciones, NullLogger<clsPublicadorGoogleChat>.Instance);

        var publicar = async () => await publicador.PublicarAsync(space, null, new JsonObject { ["text"] = "x" }, Ct);

        await publicar.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UnErrorDeLaApiDeChatSePropagaConSuEstado()
    {
        _google.EstadoChat = HttpStatusCode.Forbidden;
        var opciones = Opciones(OpcionesGoogle());
        var publicador = new clsPublicadorGoogleChat(Http(), new clsTokenCuentaServicioGoogle(Http(), opciones, TimeProvider.System), opciones, NullLogger<clsPublicadorGoogleChat>.Instance);

        var publicar = async () => await publicador.PublicarAsync("spaces/X", null, new JsonObject { ["text"] = "x" }, Ct);

        (await publicar.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static (clsWorkerEventosChat Worker, List<clsEventoChat> Procesados, List<JsonObject> Alertas, clsColaEventosChat Cola) Worker(Func<clsEventoChat, Task> procesar, string? soporte = "spaces/SOPORTE")
    {
        var procesados = new List<clsEventoChat>();
        var alertas = new List<JsonObject>();
        var procesador = Substitute.For<IClsProcesadorEventoChat>();
        procesador.ProcesarAsync(Arg.Any<clsEventoChat>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            await procesar(ci.Arg<clsEventoChat>());
            procesados.Add(ci.Arg<clsEventoChat>());
        });
        var publicador = Substitute.For<IClsPublicadorChat>();
        publicador.PublicarAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Do<JsonObject>(alertas.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var servicios = new ServiceCollection().AddScoped(_ => procesador).AddScoped(_ => publicador).BuildServiceProvider();
        var opciones = Options.Create(new clsOpcionesGoogleChat { SupportSpace = soporte });
        var cola = new clsColaEventosChat(opciones);
        var worker = new clsWorkerEventosChat(cola, servicios.GetRequiredService<IServiceScopeFactory>(), opciones, NullLogger<clsWorkerEventosChat>.Instance);
        return (worker, procesados, alertas, cola);
    }

    // test_worker_procesa_encolados + test_worker_sobrevive_a_evento_que_falla (+ alerta al space de soporte)
    [Fact]
    public async Task ElWorkerSobreviveAUnEventoQueFallaYAlertaAlSoporte()
    {
        var (worker, procesados, alertas, _) = Worker(e => e.Texto == "boom" ? throw new InvalidOperationException("fallo simulado") : Task.CompletedTask);

        await worker.ProcesarAsync(new clsEventoChat("MESSAGE", "spaces/S", "t", "boom"), Ct);
        await worker.ProcesarAsync(new clsEventoChat("MESSAGE", "spaces/S", "t", "ok"), Ct);

        procesados.Select(e => e.Texto).Should().Equal("ok");
        alertas.Should().ContainSingle().Which["text"]!.GetValue<string>().Should().Contain("Error del bot cotizador");
    }

    [Fact]
    public async Task ElWorkerConsumeLaColaEnSegundoPlano()
    {
        var (worker, procesados, _, cola) = Worker(_ => Task.CompletedTask);

        cola.Encolar(new clsEventoChat("MESSAGE", "spaces/W", "t", "hola")).Should().BeTrue();
        await worker.StartAsync(Ct);
        for (var i = 0; i < 100 && procesados.Count == 0; i++)
        {
            await Task.Delay(20, Ct);
        }

        await worker.StopAsync(Ct);
        procesados.Should().ContainSingle().Which.Texto.Should().Be("hola");
    }

    [Fact]
    public void LaColaLlenaRechazaSinBloquear()
    {
        var cola = new clsColaEventosChat(Options.Create(new clsOpcionesGoogleChat { CapacidadCola = 1 }));

        cola.Encolar(new clsEventoChat("MESSAGE", "spaces/A", "t", "1")).Should().BeTrue();
        cola.Encolar(new clsEventoChat("MESSAGE", "spaces/A", "t", "2")).Should().BeFalse();
    }

    private static clsFirmaUrlPdfHmac Firma(clsRelojFalso reloj, string? clave = "clave-hmac-de-pruebas-de-32-caracteres!") =>
        new(Options.Create(new clsOpcionesFirmaUrlPdf { ClaveFirmaUrl = clave, MinutosUrlFirmada = 15 }), reloj, NullLogger<clsFirmaUrlPdfHmac>.Instance);

    // ADR-011 §3: URL firmada válida, vencida, alterada o de otro archivo.
    [Fact]
    public void LaUrlFirmadaValeQuinceMinutosSoloParaSuArchivoYSinAlteraciones()
    {
        var reloj = new clsRelojFalso();
        var firma = Firma(reloj);
        var usuario = Guid.NewGuid();
        var token = firma.Firmar("PCN-NV-24092026-001.pdf", usuario);

        firma.Verificar("PCN-NV-24092026-001.pdf", token).Should().Be(usuario);
        firma.Verificar("PCN-NV-24092026-002.pdf", token).Should().BeNull("el token es de otro archivo");
        firma.Verificar("PCN-NV-24092026-001.pdf", token[..^2] + (token[^2] == 'A' ? "BB" : "AA")).Should().BeNull("firma alterada");
        firma.Verificar("PCN-NV-24092026-001.pdf", $"{Guid.NewGuid():N}{token[32..]}").Should().BeNull("otro comercial con la misma firma");
        firma.Verificar("PCN-NV-24092026-001.pdf", "basura").Should().BeNull();
        firma.Verificar("PCN-NV-24092026-001.pdf", null).Should().BeNull();
        Firma(reloj, "otra-clave-hmac-distinta-de-32-caracteres").Verificar("PCN-NV-24092026-001.pdf", token).Should().BeNull("otra clave");

        reloj.Avanzar(TimeSpan.FromMinutes(14));
        firma.Verificar("PCN-NV-24092026-001.pdf", token).Should().Be(usuario);
        reloj.Avanzar(TimeSpan.FromMinutes(2));
        firma.Verificar("PCN-NV-24092026-001.pdf", token).Should().BeNull("venció a los 15 minutos");
    }

    [Fact]
    public void SinClaveConfiguradaUsaUnaClaveEfimeraPorProceso()
    {
        var reloj = new clsRelojFalso();
        var usuario = Guid.NewGuid();
        var token = Firma(reloj, null).Firmar("a.pdf", usuario);

        Firma(reloj, null).Verificar("a.pdf", token).Should().BeNull("cada instancia sin clave tiene la suya");
    }

    [Fact]
    public void LaValidacionDeGChatRevisaProyectoUrlsCuentaYSpaceDeSoporte()
    {
        var validador = Validador("Development");

        validador.Validate(null, OpcionesGoogle()).Succeeded.Should().BeTrue();
        validador.Validate(null, new clsOpcionesGoogleChat { ProjectNumber = "abc" }).FailureMessage.Should().Contain("GChat:ProjectNumber");
        validador.Validate(null, new clsOpcionesGoogleChat { SaJson = "{\"client_email\":\"x\"}" }).FailureMessage.Should().Contain("GChat:SaJson");
        validador.Validate(null, new clsOpcionesGoogleChat { SaJson = "/no/existe/sa.json" }).FailureMessage.Should().Contain("GChat:SaJson");
        validador.Validate(null, new clsOpcionesGoogleChat { SupportSpace = "soporte" }).FailureMessage.Should().Contain("GChat:SupportSpace");
        validador.Validate(null, new clsOpcionesGoogleChat { ApiBase = "no-es-url" }).FailureMessage.Should().Contain("GChat:ApiBase");
    }

    private static clsValidadorOpcionesGoogleChat Validador(string entorno, string? claveFirma = null) =>
        new(new Configuracion.clsEntornoFalso(entorno), Options.Create(new clsOpcionesFirmaUrlPdf { ClaveFirmaUrl = claveFirma }));

    // Auditoría de #12 (F2): en Production la verificación del JWT no se apaga y con Google Chat la clave de firma es obligatoria.
    [Fact]
    public void EnProductionNoSeApagaElJwtYGoogleChatExigeLaClaveDeFirma()
    {
        Validador("Production").Validate(null, new clsOpcionesGoogleChat { VerificarJwt = false }).FailureMessage.Should().Contain("GChat:VerificarJwt");
        Validador("Production").Validate(null, new clsOpcionesGoogleChat { ProjectNumber = "123" }).FailureMessage.Should().Contain("Pdf:ClaveFirmaUrl");
        Validador("Production", "clave-hmac-de-produccion-de-32-caracteres").Validate(null, new clsOpcionesGoogleChat { ProjectNumber = "123" }).Succeeded.Should().BeTrue();
        Validador("Production").Validate(null, new clsOpcionesGoogleChat()).Succeeded.Should().BeTrue("sin Google Chat nadie emite URLs firmadas");
        Validador("Development").Validate(null, new clsOpcionesGoogleChat { VerificarJwt = false, ProjectNumber = "123" }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void LaCuentaDeServicioPuedeVenirComoRutaAUnArchivo()
    {
        var ruta = Path.Combine(Path.GetTempPath(), $"sa-{Guid.NewGuid():N}.json");
        File.WriteAllText(ruta, _google.CuentaServicioJson());
        try
        {
            var opciones = new clsOpcionesGoogleChat { SaJson = ruta };

            Validador("Development").Validate(null, opciones).Succeeded.Should().BeTrue();
            opciones.LeerCuentaServicio().Should().Contain("client_email");
        }
        finally
        {
            File.Delete(ruta);
        }
    }

    [Fact]
    public async Task ElRegistroRealDejaElAgenteSinConfigurarYElStubDeClientes()
    {
        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Pdf:StorageDir"] = clsServiciosPruebas.CarpetaPdfsTemporal() }).Build();
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new Configuracion.clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(configuracion);
        await using var proveedor = servicios.BuildServiceProvider();
        await using var ambito = proveedor.CreateAsyncScope();

        proveedor.GetRequiredService<IChatClient>().Should().BeOfType<clsChatClientNoConfigurado>();
        proveedor.GetRequiredService<IClsVerificadorJwtGoogleChat>().Should().BeOfType<clsVerificadorJwtGoogleChat>();
        proveedor.GetRequiredService<IClsPublicadorChat>().Should().BeOfType<clsPublicadorGoogleChat>();
        proveedor.GetRequiredService<IClsColaEventosChat>().Should().BeOfType<clsColaEventosChat>();
        proveedor.GetRequiredService<IClsFirmaUrlPdf>().Should().BeOfType<clsFirmaUrlPdfHmac>();
        proveedor.GetServices<IHostedService>().Select(h => h.GetType()).Should().Contain([typeof(clsWorkerEventosChat), typeof(clsBarridoSesionesHostedService)]);
        var buscador = ambito.ServiceProvider.GetRequiredService<IClsBuscadorClientes>();
        (await buscador.BuscarAsync("Falabella", Ct)).Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
        proveedor.GetRequiredService<IOptions<clsOpcionesAgente>>().Value.MaxIteraciones.Should().Be(12);
        proveedor.GetRequiredService<IOptions<clsOpcionesSesiones>>().Value.LimiteTokens.Should().Be(6000);
    }
}

/// <summary>Ayuda de las pruebas: copia de las opciones con otro número de proyecto.</summary>
internal static class clsExtensionesOpcionesGoogleChat
{
    public static clsOpcionesGoogleChat ConProyecto(this clsOpcionesGoogleChat opciones, string? proyecto) => new()
    {
        ProjectNumber = proyecto,
        VerificarJwt = opciones.VerificarJwt,
        UrlCertificados = opciones.UrlCertificados,
        ApiBase = opciones.ApiBase,
        SaJson = opciones.SaJson,
    };
}
