using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Exceptions;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Sesiones;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Loop del agente con el modelo guionizado (port de <c>tests/test_agente.py</c> y <c>tests/test_agente_contexto.py</c>): sin red ni
/// tokens. Herramientas reales sobre casos de uso simulados; cada llamada pasa por <c>kernel.InvokeAsync</c> y sus filtros.
/// </summary>
public sealed class clsAgenteRunnerTests
{
    private static readonly string[] _herramientasSinCorreo =
        ["buscar_cliente", "resolver_municipio", "buscar_tarifa", "obtener_margen", "calcular_precio", "registrar_ajuste_margen", "generar_pdf", "consultar_cotizacion"];

    private readonly clsArnesAgente _arnes = new();

    // Turno sin herramientas: el loop termina y la respuesta sale de la llamada final sin herramientas (ADR-003).
    [Fact]
    public async Task SinHerramientasElTurnoCierraConLaRespuestaDeLaLlamadaFinal()
    {
        _arnes.Modelo.Responder("borrador del loop").ResponderFinal("¿De qué origen sale la carga?");

        var resultado = (await _arnes.TurnoAsync("hola")).Valor;

        resultado.Texto.Should().Be("¿De qué origen sale la carga?");
        resultado.HerramientasInvocadas.Should().BeEmpty();
        resultado.Iteraciones.Should().Be(2);
        resultado.DetenerPor.Should().Be("stop");
        _arnes.Modelo.Llamadas[0].Herramientas.Should().BeEquivalentTo(_herramientasSinCorreo);
        _arnes.Modelo.Llamadas[1].Herramientas.Should().BeEmpty("la respuesta final se pide sin herramientas");
        _arnes.Modelo.Llamadas[1].Opciones!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>("la respuesta final se pide con JSON Schema (#16)")
            .Which.SchemaName.Should().Be(clsReglasRespuestaAgente.NombreEsquema);
        resultado.Respuesta.Tipo.Should().Be(TipoRespuestaAgente.Conversacional);
    }

    // test_loop_tool_use_invoca_mcp_y_cierra: dos herramientas encadenadas; la salida REAL vuelve al modelo.
    [Fact]
    public async Task DosHerramientasEncadenadasDevuelvenSuSalidaRealAlModelo()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" }, "Consulto el margen.")
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("El precio final es 3.294.798 COP.");

        var resultado = (await _arnes.TurnoAsync("Cotiza YUMBO→BUN al margen de política, costo 2.850.000.")).Valor;

        resultado.HerramientasInvocadas.Should().Equal("obtener_margen", "calcular_precio");
        resultado.Iteraciones.Should().Be(4);
        resultado.Texto.Should().Contain("3.294.798");
        resultado.UltimaSalida<clsSalidaPrecio>("calcular_precio")!.Precio.Should().Be(3_294_798m);
        var tercera = _arnes.Modelo.Llamadas[2];
        tercera.Mensajes[^1].Role.Should().Be(ChatRole.Tool);
        tercera.ResultadosHerramientas[^1].Should().Contain("3294798");
        _arnes.Modelo.Llamadas[1].ResultadosHerramientas[^1].Should().Contain("\"origen\":\"politica_linea\"");
    }

    // test_max_iteraciones_excedidas
    [Fact]
    public async Task SiElModeloNuncaCierraElLoopCortaEnElTope()
    {
        _arnes.OpcionesAgente.MaxIteraciones = 3;
        _arnes.Modelo.RepetirSiempre("buscar_tarifa", new { lineaNegocio = "Nacional", origen = "YUMBO", destino = "BUN" });

        var turno = async () => await _arnes.TurnoAsync("bucle");

        await turno.Should().ThrowAsync<clsMaxIteracionesExcedidasException>();
        _arnes.Modelo.Llamadas.Should().HaveCount(3);
        _arnes.Sesiones.Eventos.Should().Contain(e => e.Rol == clsEventoConversacion.RolUsuario && e.Contenido == "bucle");
    }

    // Herramienta alucinada: HERRAMIENTA_DESCONOCIDA como resultado y el loop sigue.
    [Fact]
    public async Task UnaHerramientaInexistenteRespondeHerramientaDesconocidaYElLoopSigue()
    {
        _arnes.Modelo.Llamar("consultar_clima", new { ciudad = "Cali" }).Cerrar("No tengo esa herramienta.");

        var resultado = (await _arnes.TurnoAsync("¿Llueve en Cali?")).Valor;

        resultado.HerramientasInvocadas.Should().BeEmpty();
        resultado.Salidas.Should().ContainSingle(s => s.Error!.Codigo == clsCodigosError.HerramientaDesconocida);
        _arnes.Modelo.Llamadas[1].ResultadosHerramientas[0].Should().Contain("HERRAMIENTA_DESCONOCIDA").And.Contain("buscar_tarifa");
        resultado.Texto.Should().Be("No tengo esa herramienta.");
    }

    // Error estructurado de una herramienta: llega al modelo con el contrato {"error": {codigo, mensaje}}.
    [Fact]
    public async Task ElErrorEstructuradoDeUnaHerramientaLlegaAlModelo()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 0, margen = 0.135, metodo = "margen" })
            .Cerrar("El costo debe ser mayor que cero.");

        var resultado = (await _arnes.TurnoAsync("Cotiza con costo 0")).Valor;

        var error = JsonDocument.Parse(_arnes.Modelo.Llamadas[2].ResultadosHerramientas[^1]).RootElement.GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be(clsCodigosError.PrecioInvalido);
        error.GetProperty("mensaje").GetString().Should().NotBeNullOrWhiteSpace();
        resultado.HerramientasInvocadas.Should().Contain("calcular_precio");
        resultado.UltimaSalida<clsSalidaPrecio>("calcular_precio").Should().BeNull("una salida con error no cuenta");
    }

    // test_ejecutar_turno_envia_system_con_contexto + bloque "Estado resuelto" (ADR-004).
    [Fact]
    public async Task ElSistemaLlevaElContextoDelComercialYElEstadoResuelto()
    {
        _arnes.Modelo.Llamar("obtener_margen", new { lineaNegocio = "Nacional" }).Cerrar("Listo.").Cerrar("Segundo turno.");

        await _arnes.TurnoAsync("hola");
        await _arnes.TurnoAsync("sigue");

        var primero = _arnes.Modelo.Llamadas[0].Sistema;
        primero.Should().StartWith("# Contexto del comercial");
        primero.Should().Contain("Nathaly Vargas Garcia").And.Contain("nvargas@boterosoto.com.co").And.Contain("2026-09-24");
        primero.Should().Contain("# Estado resuelto").And.Contain("Todavía no hay datos resueltos");
        primero.Should().Contain("# Cotizador Botero Soto").And.Contain("El folio lo genera el servidor");
        primero.Should().NotContain("NUNCA** el formato `PCN`", "se corrigió la contradicción del folio del prompt del Python");
        _arnes.Modelo.Llamadas[3].Sistema.Should().Contain("- Línea de negocio: Nacional").And.Contain("- Margen aplicado: 13,5 %");
    }

    // test_registra_cada_turno_en_conversacion
    [Fact]
    public async Task CadaTurnoQuedaEnLaConversacionConLasHerramientasYSusSalidas()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Cerrar("El margen de la política es 13,5 %.");

        await _arnes.TurnoAsync("¿Qué margen tiene Nacional?");

        var eventos = _arnes.Sesiones.Eventos;
        eventos.Select(e => e.Rol).Should().Equal("user", "assistant", "herramienta", "assistant");
        eventos[0].Contenido.Should().Be("¿Qué margen tiene Nacional?");
        eventos[0].UsuarioId.Should().Be(_arnes.Usuario.Id);
        JsonDocument.Parse(eventos[1].Herramientas!).RootElement.GetProperty("invocaciones")[0].GetProperty("nombre").GetString().Should().Be("obtener_margen");
        var resultado = JsonDocument.Parse(eventos[2].Herramientas!).RootElement.GetProperty("resultados")[0];
        resultado.GetProperty("nombre").GetString().Should().Be("obtener_margen");
        resultado.GetProperty("salida").GetProperty("margen").GetDecimal().Should().Be(0.135m);
        eventos[3].Contenido.Should().Be("El margen de la política es 13,5 %.");
        eventos.Select(e => e.CreatedAt).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        await _arnes.Unidad.Received().GuardarCambiosAsync(Arg.Any<CancellationToken>());
    }

    // test_memoria_entre_turnos_alimenta_contexto
    [Fact]
    public async Task ElSegundoTurnoLlevaLaSolicitudOriginalYLasRespuestasPrevias()
    {
        _arnes.Modelo.Cerrar("¿De qué línea de negocio es la carga?").Cerrar("Listo, lo ajusto al margen de 11%.");

        await _arnes.TurnoAsync("Cotiza YUMBO→BUN.");
        var segundo = (await _arnes.TurnoAsync("Ahora ajústalo a margen 11%.")).Valor;

        segundo.Texto.Should().Contain("11%");
        var mensajes = _arnes.Modelo.Llamadas[2].Mensajes;
        mensajes[0].Role.Should().Be(ChatRole.System);
        mensajes.Should().Contain(m => m.Role == ChatRole.User && m.Text == "Cotiza YUMBO→BUN.");
        mensajes.Should().Contain(m => m.Role == ChatRole.Assistant && m.Text == "¿De qué línea de negocio es la carga?");
        mensajes[^1].Role.Should().Be(ChatRole.User);
        mensajes[^1].Text.Should().Be("Ahora ajústalo a margen 11%.");
    }

    // La sesión queda ligada a su usuario: otro usuario recibe NO_ENCONTRADO (404 del contrato).
    [Fact]
    public async Task LaSesionDeOtroUsuarioNoSeContinua()
    {
        _arnes.Sesiones.Agregar(clsSesion.Crear(clsArnesAgente.Hilo, clsArnesAgente.Hilo, DateTimeOffset.UtcNow, Guid.NewGuid()).Valor);

        var resultado = await _arnes.TurnoAsync("hola");

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        _arnes.Modelo.Llamadas.Should().BeEmpty();
        _arnes.Sesiones.Eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task UnComercialQueNoExisteNoEsAutenticado()
    {
        var resultado = await _arnes.TurnoAsync("hola", usuarioId: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoAutenticado);
    }

    // enviar_correo solo se declara con Correo:Habilitado=true (como el Python).
    [Fact]
    public async Task EnviarCorreoSoloSeDeclaraConElCorreoHabilitado()
    {
        _arnes.OpcionesCorreo.Habilitado = true;
        _arnes.Modelo.Cerrar("ok");

        await _arnes.TurnoAsync("hola");

        _arnes.Modelo.Llamadas[0].Herramientas.Should().Contain("enviar_correo").And.HaveCount(9);
    }

    // IClsValidadorRespuestaAgente (#16): el runner delega la llamada final con el contexto del turno y usa la respuesta validada.
    [Fact]
    public async Task LaRespuestaFinalLaPideElValidadorConElContextoDelTurno()
    {
        var validador = Substitute.For<IClsValidadorRespuestaAgente>();
        var estructurada = new clsRespuestaAgenteEstructurada { Tipo = TipoRespuestaAgente.Previsualizacion, Texto = "validado", RequiereConfirmacion = true };
        validador.ObtenerRespuestaFinalAsync(Arg.Any<IChatClient>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<clsContextoValidacionRespuesta>(), Arg.Any<CancellationToken>())
            .Returns(new clsRespuestaFinalAgente(estructurada, 2, 1, false, ["json_invalido"]));
        _arnes.Validador = validador;
        _arnes.Modelo.Llamar("obtener_margen", new { lineaNegocio = "Nacional" }).Responder("loop");

        var resultado = (await _arnes.TurnoAsync("Cotiza Nacional, costo 2.850.000")).Valor;

        resultado.Texto.Should().Be("validado");
        resultado.Respuesta.Should().Be(estructurada);
        resultado.ReintentosRespuesta.Should().Be(1);
        resultado.EsRespaldo.Should().BeFalse();
        resultado.Iteraciones.Should().Be(4, "dos llamadas del loop y dos de la respuesta final (con el reintento)");
        _arnes.Sesiones.Eventos[^1].Contenido.Should().Be("validado");
        await validador.Received(1).ObtenerRespuestaFinalAsync(
            _arnes.Modelo,
            Arg.Is<IReadOnlyList<ChatMessage>>(m => m.Last().Role == ChatRole.Tool),
            Arg.Is<clsContextoValidacionRespuesta>(c =>
                c.Salidas.Count == 1 && c.Estado.MargenAplicado == 0.135m && c.MensajesDelComercial.Last() == "Cotiza Nacional, costo 2.850.000"
                && c.HerramientasDeclaradas.Contains("generar_pdf") && !c.HerramientasDeclaradas.Contains("enviar_correo")),
            Arg.Any<CancellationToken>());
    }

    // El respaldo del validador no lanza: el turno queda guardado con el texto controlado y marcado para que el canal lo muestre como error.
    [Fact]
    public async Task ElRespaldoDelValidadorNoLanzaYQuedaMarcado()
    {
        var validador = Substitute.For<IClsValidadorRespuestaAgente>();
        validador.ObtenerRespuestaFinalAsync(Arg.Any<IChatClient>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<clsContextoValidacionRespuesta>(), Arg.Any<CancellationToken>())
            .Returns(new clsRespuestaFinalAgente(clsRespuestaAgenteEstructurada.Respaldo(clsTextosAgente.RespuestaNoEstructurada), 2, 1, true, ["json_invalido", "json_invalido"]));
        _arnes.Validador = validador;
        _arnes.Modelo.Responder("loop");

        var resultado = (await _arnes.TurnoAsync("hola")).Valor;

        resultado.EsRespaldo.Should().BeTrue();
        resultado.Texto.Should().Be(clsTextosAgente.RespuestaNoEstructurada);
        resultado.Respuesta.Tipo.Should().Be(TipoRespuestaAgente.Error);
        _arnes.Sesiones.Eventos.Select(e => e.Rol).Should().Equal("user", "assistant");
    }

    // Con el validador real: una respuesta final malformada se corrige con un reintento dentro del mismo turno.
    [Fact]
    public async Task ConElValidadorRealUnaRespuestaMalformadaSeReintentaEnElMismoTurno()
    {
        _arnes.Modelo.Responder("loop").Responder("{\"tipo\": \"conversacional\", \"texto\": \"hol").ResponderFinal("Hola, ¿qué cotizamos?");

        var resultado = (await _arnes.TurnoAsync("hola")).Valor;

        resultado.Texto.Should().Be("Hola, ¿qué cotizamos?");
        resultado.ReintentosRespuesta.Should().Be(1);
        resultado.Iteraciones.Should().Be(3);
        _arnes.Modelo.Llamadas[2].Mensajes[^1].Text.Should().Contain("JSON");
    }

    // Un fallo del proveedor (429) se propaga; el mensaje del comercial ya quedó guardado (el canal responde error: true).
    [Fact]
    public async Task UnFalloDelProveedorSePropagaYElMensajeQuedaGuardado()
    {
        _arnes.Modelo.Fallar(new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests));

        var turno = async () => await _arnes.TurnoAsync("Cotiza YUMBO→BUN.");

        (await turno.Should().ThrowAsync<HttpRequestException>()).Which.Should().Match<Exception>(e => clsTextosAgente.EsLimiteDePeticiones(e));
        _arnes.Sesiones.Eventos.Should().ContainSingle(e => e.Rol == "user");
    }
}
