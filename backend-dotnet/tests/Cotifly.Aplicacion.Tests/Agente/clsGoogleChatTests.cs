using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Google Chat en Aplicación: parseo de eventos y acuses (port de <c>tests/test_chat_webhook.py</c>), tarjetas (port de
/// <c>tests/test_chat_publicador.py</c>) y procesamiento de un evento (identidad por email, ADR-011).
/// </summary>
public sealed class clsGoogleChatTests
{
    private static readonly JsonElement _eventoReal =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "chat", "evento_mensaje.json"))).RootElement.Clone();

    private static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement.Clone();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // test_parsear_evento_real + test_ack_no_espera_procesamiento (campos del hilo)
    [Fact]
    public void ParseaElEventoRealDelFormatoClasico()
    {
        var evento = clsParserEventoChat.Parsear(_eventoReal);

        evento.EsMensaje.Should().BeTrue();
        evento.Tipo.Should().Be("MESSAGE");
        evento.SpaceId.Should().Be("spaces/AAAAbot_cotizador");
        evento.ThreadId.Should().Be("spaces/AAAAbot_cotizador/threads/tLongyin19032026");
        evento.UsuarioEmail.Should().Be("nvargas@boterosoto.com.co");
        evento.Texto.Should().Contain("YUMBO");
        evento.EsAddOn.Should().BeFalse();
    }

    // test_parsear_captura_nombre_del_comercial
    [Fact]
    public void CapturaElNombreDelComercial() =>
        clsParserEventoChat.Parsear(_eventoReal).UsuarioNombre.Should().Be("Nathaly Vargas Garcia");

    // test_parsear_formato_nuevo_chat_messagepayload
    [Fact]
    public void ParseaElFormatoAddOnConElTipoInferido()
    {
        var evento = clsParserEventoChat.Parsear(Json("""
            {"chat":{"messagePayload":{"message":{"text":"Cotiza YUMBO a BUN","thread":{"name":"spaces/S/threads/T"},
             "sender":{"email":"nvargas@boterosoto.com.co","displayName":"Nathaly Vargas"}},"space":{"name":"spaces/S"}}}}
            """));

        evento.EsMensaje.Should().BeTrue();
        evento.Tipo.Should().Be("MESSAGE");
        evento.SpaceId.Should().Be("spaces/S");
        evento.ThreadId.Should().Be("spaces/S/threads/T");
        evento.Texto.Should().Be("Cotiza YUMBO a BUN");
        evento.UsuarioEmail.Should().Be("nvargas@boterosoto.com.co");
        evento.UsuarioNombre.Should().Be("Nathaly Vargas");
        evento.EsAddOn.Should().BeTrue();
    }

    // test_evento_no_mensaje_no_encola
    [Fact]
    public void UnEventoQueNoEsMensajeNoDisparaTurno() =>
        clsParserEventoChat.Parsear(Json("""{"type":"ADDED_TO_SPACE","space":{"name":"spaces/X"}}""")).EsMensaje.Should().BeFalse();

    [Fact]
    public void ElClicEnConfirmarDeLaTarjetaSeVuelveConfirmo()
    {
        var clasico = clsParserEventoChat.Parsear(Json("""
            {"type":"CARD_CLICKED","action":{"actionMethodName":"confirmar_cotizacion"},
             "message":{"thread":{"name":"spaces/S/threads/T"}},"space":{"name":"spaces/S"},"user":{"email":"a@b.co"}}
            """));
        var addOn = clsParserEventoChat.Parsear(Json("""
            {"commonEventObject":{"invokedFunction":"ajustar_margen"},
             "chat":{"buttonClickedPayload":{"message":{"thread":{"name":"spaces/S/threads/T"}},"space":{"name":"spaces/S"},"user":{"email":"a@b.co"}}}}
            """));

        clasico.TextoParaAgente.Should().Be(clsEventoChat.TextoConfirmar);
        clasico.ThreadId.Should().Be("spaces/S/threads/T");
        addOn.Tipo.Should().Be("CARD_CLICKED");
        addOn.TextoParaAgente.Should().Be(clsEventoChat.TextoAjustarMargen);
        addOn.UsuarioEmail.Should().Be("a@b.co");
    }

    // test_jwt_valido_ack_200 / test_webhook_formato_addon_ack_hostappdataaction / test_evento_no_mensaje_no_encola
    [Fact]
    public void ElAcuseTieneLaFormaDeCadaModeloDeApp()
    {
        var clasico = clsParserEventoChat.Acuse(false, "Recibí");
        clasico.Should().ContainSingle();
        clasico["text"]!.GetValue<string>().Should().Be("Recibí");
        clsParserEventoChat.Acuse(true, "Recibí")["hostAppDataAction"]!["chatDataAction"]!["createMessageAction"]!["message"]!["text"]!.GetValue<string>().Should().Be("Recibí");
        clsParserEventoChat.Acuse(false, null).ToJsonString().Should().Be("{}");
    }

    // test_formato_cop_y_pct
    [Fact]
    public void FormateaPesosYPorcentajes()
    {
        clsTarjetasChat.FormatoCop(3_294_798m).Should().Be("$3.294.798");
        clsTarjetasChat.FormatoCop(2_850_000m).Should().Be("$2.850.000");
        clsTarjetasChat.FormatoPct(0.135m).Should().Be("13,5%");
        clsTarjetasChat.FormatoPct(0.11m).Should().Be("11,0%");
    }

    private static clsResultadoTurno Resultado(string texto, params clsResultadoHerramienta[] salidas) =>
        new(texto, 1, salidas.Select(s => s.Herramienta).ToList(), "stop", salidas);

    private static Dictionary<string, string> Valores(JsonObject mensaje) =>
        mensaje["cardsV2"]![0]!["card"]!["sections"]![0]!["widgets"]!.AsArray()
            .Where(w => w!["decoratedText"] is not null)
            .ToDictionary(w => w!["decoratedText"]!["topLabel"]!.GetValue<string>(), w => w!["decoratedText"]!["text"]!.GetValue<string>());

    private static List<JsonNode> Botones(JsonObject mensaje) =>
        [.. mensaje["cardsV2"]![0]!["card"]!["sections"]![0]!["widgets"]!.AsArray()
            .Where(w => w!["buttonList"] is not null)
            .SelectMany(w => w!["buttonList"]!["buttons"]!.AsArray())
            .Select(b => b!)];

    // test_mensaje_previsualizacion_desde_calcular_precio
    [Fact]
    public void ConPrecioSePublicaLaPrevisualizacionConLosBotonesDeControlHumano()
    {
        var mensaje = clsTarjetasChat.ParaResultado(Resultado("Listo.",
            clsResultadoHerramienta.Exito("obtener_margen", new clsSalidaMargen(0.15m, "politica_linea", "OPTN", false, "Política")),
            clsResultadoHerramienta.Exito("calcular_precio", new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "margen", 444_798m))), _ => null);

        var valores = Valores(mensaje);
        valores["Costo"].Should().Be("$2.850.000");
        valores["Margen de política"].Should().Be("15,0%");
        valores["Margen aplicado"].Should().Be("13,5%");
        valores["Precio"].Should().Be("$3.294.798");
        mensaje["text"]!.GetValue<string>().Should().Be(clsTarjetasChat.PromptConfirmacion);
        Botones(mensaje).Select(b => b["text"]!.GetValue<string>()).Should().Contain(["Confirmar y generar PDF", "Ajustar margen"]);
    }

    // test_mensaje_pdf_desde_generar_pdf (con la URL firmada de ADR-011 §3)
    [Fact]
    public void ConPdfSePublicaLaTarjetaConElEnlaceFirmado()
    {
        var mensaje = clsTarjetasChat.ParaResultado(Resultado("Cotización generada.",
            clsResultadoHerramienta.Exito("calcular_precio", new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "margen", 444_798m)),
            clsResultadoHerramienta.Exito("generar_pdf", new clsSalidaPdf(Guid.NewGuid(), "PCN-24092026-001", "/pdfs/PCN-24092026-001.pdf", true))),
            pdf => $"https://cotifly/pdfs/{pdf.Folio}.pdf?t=firma");

        mensaje["cardsV2"]![0]!["cardId"]!.GetValue<string>().Should().Be("pdf");
        Botones(mensaje)[0]["onClick"]!["openLink"]!["url"]!.GetValue<string>().Should().Be("https://cotifly/pdfs/PCN-24092026-001.pdf?t=firma");
    }

    // test_mensaje_texto_cuando_no_hay_precio_ni_pdf + test_mensaje_ignora_salida_con_error
    [Fact]
    public void SinPrecioNiPdfOConErrorSePublicaTexto()
    {
        var pregunta = clsTarjetasChat.ParaResultado(Resultado("¿De qué origen sale la carga?"), _ => null);
        var conError = clsTarjetasChat.ParaResultado(Resultado("No pude calcular.", clsResultadoHerramienta.Fallo("calcular_precio", clsCodigosError.PrecioInvalido, "x")), _ => null);

        pregunta.Should().ContainSingle();
        pregunta["text"]!.GetValue<string>().Should().Be("¿De qué origen sale la carga?");
        conError.Should().ContainSingle();
        conError["text"]!.GetValue<string>().Should().Be("No pude calcular.");
    }

    // test_card_pdf_sin_url
    [Fact]
    public void LaTarjetaDelPdfSinUrlNoTieneBoton() => Botones(clsTarjetasChat.Pdf("PCN-1", null, "ok")).Should().BeEmpty();

    // HU-003.10: una respuesta estructurada que pide confirmación con acciones sugeridas se publica con sus botones.
    [Fact]
    public void UnaConfirmacionConAccionesSugeridasSePublicaConSusBotones()
    {
        var resultado = Resultado("¿Genero el PDF con el precio que ya revisaste?") with
        {
            Respuesta = new clsRespuestaAgenteEstructurada
            {
                Tipo = TipoRespuestaAgente.ConfirmacionRequerida,
                Texto = "¿Genero el PDF con el precio que ya revisaste?",
                RequiereConfirmacion = true,
                AccionesSugeridas = [AccionSugeridaAgente.ConfirmarPdf, AccionSugeridaAgente.AjustarMargen],
            },
        };

        var mensaje = clsTarjetasChat.ParaResultado(resultado, _ => null);

        mensaje["text"]!.GetValue<string>().Should().Be("¿Genero el PDF con el precio que ya revisaste?");
        Botones(mensaje).Select(b => b["onClick"]!["action"]!["function"]!.GetValue<string>())
            .Should().Equal(clsEventoChat.AccionConfirmar, clsEventoChat.AccionAjustarMargen);
    }

    // HU-003.10: con el respaldo del validador no se muestran los botones de control humano, aunque haya un precio del turno.
    [Fact]
    public void ConElRespaldoDelValidadorSePublicaSoloElTexto()
    {
        var resultado = Resultado(clsTextosAgente.RespuestaNoEstructurada,
            clsResultadoHerramienta.Exito("calcular_precio", new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "margen", 444_798m))) with
        {
            EsRespaldo = true,
            Respuesta = clsRespuestaAgenteEstructurada.Respaldo(clsTextosAgente.RespuestaNoEstructurada),
        };

        var mensaje = clsTarjetasChat.ParaResultado(resultado, _ => null);

        mensaje.Should().ContainSingle();
        mensaje["text"]!.GetValue<string>().Should().Be(clsTextosAgente.RespuestaNoEstructurada);
    }

    private sealed class clsProcesador
    {
        public clsProcesador()
        {
            Usuario = clsUsuarioWeb.Crear("nvargas", "Nathaly", "nvargas@boterosoto.com.co", null, DateTimeOffset.UtcNow).Valor;
            Usuarios.ObtenerPorEmailAsync("nvargas@boterosoto.com.co", Arg.Any<CancellationToken>()).Returns(Usuario);
            Firma.Firmar(Arg.Any<string>(), Arg.Any<Guid>()).Returns("token.firmado");
            Publicador.PublicarAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Do<JsonObject>(m => Publicados.Add(m)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            Verificador.VerificarAsync("nvargas", false, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(new clsVerificacionIdentidad(new clsIdentidadVerificada(Usuario, RolInterno.Comercial, false), clsMotivosAcceso.UsuarioExistente, null));
        }

        public IClsVerificadorIdentidad Verificador { get; } = Substitute.For<IClsVerificadorIdentidad>();

        public IClsAuditoriaService Auditoria { get; } = Substitute.For<IClsAuditoriaService>();

        public IClsUsuarioWebRepository Usuarios { get; } = Substitute.For<IClsUsuarioWebRepository>();

        public IClsAgenteRunner Runner { get; } = Substitute.For<IClsAgenteRunner>();

        public IClsPublicadorChat Publicador { get; } = Substitute.For<IClsPublicadorChat>();

        public IClsFirmaUrlPdf Firma { get; } = Substitute.For<IClsFirmaUrlPdf>();

        public List<JsonObject> Publicados { get; } = [];

        public clsUsuarioWeb Usuario { get; }

        public clsProcesadorEventoChat Crear() =>
            new(Usuarios, Verificador, Auditoria, new clsRelojFalso(), Runner, Publicador, Firma, Options.Create(new clsOpcionesPdf { BaseUrl = "https://cotifly.boterosoto.com.co" }), new clsLoggerFalso<clsProcesadorEventoChat>());
    }

    private static clsEventoChat Mensaje(string email = "nvargas@boterosoto.com.co", string texto = "Cotiza YUMBO→BUN.") =>
        new("MESSAGE", "spaces/PROC", "spaces/PROC/threads/1", texto, email, "Nathaly");

    // ADR-011 §2: sin cuenta en UsuariosWeb no se cotiza.
    [Fact]
    public async Task UnRemitenteSinCuentaRecibeElAvisoYNoCotiza()
    {
        var proc = new clsProcesador();

        await proc.Crear().ProcesarAsync(Mensaje("desconocido@gmail.com"), Ct);

        proc.Publicados.Should().ContainSingle().Which["text"]!.GetValue<string>().Should().Be("Ingresa primero a Cotifly desde ClientePro.");
        await proc.Runner.DidNotReceive().EjecutarTurnoAsync(Arg.Any<clsSolicitudTurno>(), Arg.Any<CancellationToken>());
    }

    // test_procesador_corre_turno_y_publica: el turno corre con el comercial resuelto y se publica en el hilo.
    [Fact]
    public async Task UnComercialConocidoCotizaYLaRespuestaSePublicaEnElHilo()
    {
        var proc = new clsProcesador();
        proc.Runner.EjecutarTurnoAsync(Arg.Any<clsSolicitudTurno>(), Arg.Any<CancellationToken>()).Returns(clsResultado<clsResultadoTurno>.Exito(Resultado("Listo.",
            clsResultadoHerramienta.Exito("generar_pdf", new clsSalidaPdf(Guid.NewGuid(), "PCN-24092026-001", "/pdfs/PCN-24092026-001.pdf", true)))));

        await proc.Crear().ProcesarAsync(Mensaje(), Ct);

        await proc.Runner.Received(1).EjecutarTurnoAsync(
            Arg.Is<clsSolicitudTurno>(s => s.UsuarioId == proc.Usuario.Id && s.SpaceId == "spaces/PROC" && s.ThreadId == "spaces/PROC/threads/1" && s.Mensaje == "Cotiza YUMBO→BUN."),
            Arg.Any<CancellationToken>());
        await proc.Publicador.Received(1).PublicarAsync("spaces/PROC", "spaces/PROC/threads/1", Arg.Any<JsonObject>(), Arg.Any<CancellationToken>());
        Botones(proc.Publicados.Single())[0]["onClick"]!["openLink"]!["url"]!.GetValue<string>()
            .Should().Be("https://cotifly.boterosoto.com.co/pdfs/PCN-24092026-001.pdf?t=token.firmado");
        proc.Firma.Received(1).Firmar("PCN-24092026-001.pdf", proc.Usuario.Id);
    }

    // I5: la verificación doble también en Google Chat; si el rol externo cambió, no se cotiza y queda auditado.
    [Fact]
    public async Task SiLaVerificacionDobleFallaNoSeCotizaYSeAudita()
    {
        var proc = new clsProcesador();
        proc.Verificador.VerificarAsync("nvargas", false, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new clsVerificacionIdentidad(null, clsMotivosAcceso.RolExternoCambio, null, proc.Usuario.Id));

        await proc.Crear().ProcesarAsync(Mensaje(), Ct);

        proc.Publicados.Single()["text"]!.GetValue<string>().Should().Be(clsTextosAgente.SinIdentidad);
        await proc.Runner.DidNotReceive().EjecutarTurnoAsync(Arg.Any<clsSolicitudTurno>(), Arg.Any<CancellationToken>());
        await proc.Auditoria.Received(1).RegistrarAsync(clsProcesadorEventoChat.HerramientaAuditoria, Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), "spaces/PROC/threads/1");
    }

    // test_procesador_no_mensaje_no_publica
    [Fact]
    public async Task UnEventoQueNoEsMensajeNoPublica()
    {
        var proc = new clsProcesador();

        await proc.Crear().ProcesarAsync(new clsEventoChat("ADDED_TO_SPACE", "spaces/X"), Ct);

        proc.Publicados.Should().BeEmpty();
    }

    [Fact]
    public async Task UnHiloDeOtroComercialNoSeContinua()
    {
        var proc = new clsProcesador();
        proc.Runner.EjecutarTurnoAsync(Arg.Any<clsSolicitudTurno>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoTurno>.Fallo(new clsErrorDominio(clsCodigosAplicacion.NoEncontrado, "Sesión no encontrada.")));

        await proc.Crear().ProcesarAsync(Mensaje(), Ct);

        proc.Publicados.Single()["text"]!.GetValue<string>().Should().Be(clsTextosAgente.HiloAjeno);
    }

    // Un fallo del agente se avisa en el hilo (texto de límite o genérico) y se propaga para la alerta del worker.
    [Fact]
    public async Task UnFalloDelAgenteSeAvisaEnElHiloYSePropaga()
    {
        var proc = new clsProcesador();
        proc.Runner.EjecutarTurnoAsync(Arg.Any<clsSolicitudTurno>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("RateLimitError 429"));

        var procesar = async () => await proc.Crear().ProcesarAsync(Mensaje(), Ct);

        await procesar.Should().ThrowAsync<InvalidOperationException>();
        proc.Publicados.Single()["text"]!.GetValue<string>().Should().Be(clsTextosAgente.LimiteModelo);
    }
}
