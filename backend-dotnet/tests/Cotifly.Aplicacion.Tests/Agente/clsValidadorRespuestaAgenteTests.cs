using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Agente;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Validador de la respuesta final del agente (HU-003.10, issue #16): esquema, reglas de validación manual, reintento con
/// corrección, respaldo controlado, proveedores sin JSON Schema, métricas y logs sin datos del texto. El modelo es el guionizado.
/// </summary>
public sealed class clsValidadorRespuestaAgenteTests : IDisposable
{
    private const string Folio = "PCN-900123456-24092026-001";

    private static readonly string[] _declaradas =
        ["buscar_cliente", "buscar_tarifa", "obtener_margen", "calcular_precio", "registrar_ajuste_margen", "generar_pdf", "consultar_cotizacion"];

    private readonly clsChatClientGuionizado _modelo = new();
    private readonly clsLoggerFalso<clsValidadorRespuestaAgente> _logger = new();
    private readonly clsOpcionesAgente _opciones = new();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsFabricaMedidoresPrueba _medidores = new();
    private readonly clsValidadorRespuestaAgente _validador;
    private readonly MeterListener _escucha = new();
    private readonly List<(string Instrumento, long Valor, string? Motivo)> _medidas = [];

    public clsValidadorRespuestaAgenteTests()
    {
        _validador = new clsValidadorRespuestaAgente(Options.Create(_opciones), _logger, _medidores, _reloj);
        _escucha.InstrumentPublished = (instrumento, escucha) =>
        {
            if (ReferenceEquals(instrumento.Meter, _validador.Medidor))
            {
                escucha.EnableMeasurementEvents(instrumento);
            }
        };
        _escucha.SetMeasurementEventCallback<long>((instrumento, valor, etiquetas, _) =>
        {
            string? motivo = null;
            foreach (var etiqueta in etiquetas)
            {
                if (etiqueta.Key == "motivo")
                {
                    motivo = etiqueta.Value as string;
                }
            }

            lock (_medidas)
            {
                _medidas.Add((instrumento.Name, valor, motivo));
            }
        });
        _escucha.Start();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _escucha.Dispose();
        _medidores.Dispose();
    }

    // ---------- Contextos ----------

    private static clsContextoValidacionRespuesta SinDatos(params string[] mensajes) =>
        new(clsEstadoConversacion.Vacio, [], mensajes.Length == 0 ? ["hola"] : mensajes, _declaradas);

    /// <summary>calcular_precio del turno: costo 2.850.000, margen 15 %, precio 3.352.941, utilidad 502.941; previsualización pendiente.</summary>
    private static clsContextoValidacionRespuesta ConPrecio(params string[] mensajes) =>
        new(
            clsEstadoConversacion.Vacio with { Costo = 2_850_000m, MargenAplicado = 0.15m, PrecioFinal = 3_352_941m, Moneda = "COP", ConfirmacionPendiente = true },
            [
                clsResultadoHerramienta.Exito("obtener_margen", new clsSalidaMargen(0.15m, "politica_linea", "OPTN", false, "Política")),
                clsResultadoHerramienta.Exito("calcular_precio", new clsSalidaPrecio(3_352_941m, 2_850_000m, 0.15m, "margen", 502_941m)),
            ],
            mensajes.Length == 0 ? ["Cotiza Nacional YUMBO a BUN"] : mensajes,
            _declaradas);

    private static clsContextoValidacionRespuesta ConFolio() =>
        new(
            clsEstadoConversacion.Vacio with { PrecioFinal = 3_352_941m, FolioEnProgreso = Folio + "-v2" },
            [clsResultadoHerramienta.Exito("generar_pdf", new clsSalidaPdf(Guid.NewGuid(), Folio + "-v2", $"/pdfs/{Folio}-v2.pdf", true))],
            ["Confirmo"],
            _declaradas);

    /// <summary>Contexto de la batería del validador de #16: precio 3.294.798, costo 2.850.000, NIT 9000472281, tarifa USD 1.200.</summary>
    private static clsContextoValidacionRespuesta Bateria(params string[] mensajes) =>
        new(
            clsEstadoConversacion.Vacio with
            {
                Costo = 2_850_000m, MargenAplicado = 0.135m, PrecioFinal = 3_294_798m, Moneda = "COP", ConfirmacionPendiente = true,
                NitCliente = "9000472281", FolioEnProgreso = "PCN-24092026-001-v2",
            },
            [
                clsResultadoHerramienta.Exito("obtener_margen", new clsSalidaMargen(0.135m, "politica_linea", "OPTN", false, "Política")),
                clsResultadoHerramienta.Exito("calcular_precio", new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "margen", 444_798m)),
                clsResultadoHerramienta.Exito("buscar_tarifa", new Dictionary<string, object> { ["costo"] = 1200m, ["moneda"] = "USD" }),
            ],
            mensajes.Length == 0 ? ["Cotiza Nacional YUMBO a BUN, costo 2.850.000"] : mensajes,
            _declaradas);

    private static string Json(string texto, string tipo = "conversacional", bool requiere = false, params string[] acciones) =>
        clsChatClientGuionizado.JsonFinal(texto, tipo, requiere, acciones);

    private static IEnumerable<string> Codigos(clsValidacionRespuesta validacion) => validacion.Fallos.Select(f => f.Codigo);

    /// <summary>Excepción con la propiedad <c>Status</c> de los SDK (<c>ClientResultException</c> de OpenAI).</summary>
    private sealed class clsErrorConStatus(int status, string mensaje) : Exception(mensaje)
    {
        public int Status { get; } = status;
    }

    // ---------- Esquema ----------

    [Fact]
    public void ElEsquemaSeGeneraDelTipoConLosEnumsLosCamposRequeridosYSinPropiedadesAdicionales()
    {
        var esquema = clsReglasRespuestaAgente.Esquema;
        var texto = esquema.GetRawText();

        esquema.GetProperty("type").GetString().Should().Be("object");
        esquema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
        esquema.GetProperty("required").EnumerateArray().Select(r => r.GetString())
            .Should().BeEquivalentTo("tipo", "texto", "requiere_confirmacion", "acciones_sugeridas");
        esquema.GetProperty("properties").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("tipo", "texto", "requiere_confirmacion", "acciones_sugeridas");
        texto.Should().Contain("\"conversacional\"").And.Contain("\"previsualizacion\"").And.Contain("\"confirmacion_requerida\"").And.Contain("\"error\"");
        texto.Should().Contain("\"confirmar_pdf\"").And.Contain("\"ajustar_margen\"");
        texto.Should().NotContain("precio\":").And.NotContain("\"margen\"").And.NotContain("\"pdf\"", "precio, margen y PDF nunca salen del LLM (ADR-003)");
        clsReglasRespuestaAgente.Formato.Should().BeOfType<ChatResponseFormatJson>().Which.SchemaName.Should().Be("respuesta_agente");
        clsValidadorRespuestaAgente.InstruccionConEsquema.Should().NotContain("\"properties\"", "en modo esquema el esquema ya va en response_format");
        clsValidadorRespuestaAgente.InstruccionSinEsquema.Should().Contain(clsReglasRespuestaAgente.EsquemaTexto);
        clsReglasRespuestaAgente.EsquemaTexto.Should().Contain("español").And.NotContain("\\u00", "el esquema de la instrucción no escapa tildes");
        var propiedades = esquema.GetProperty("properties");
        propiedades.GetProperty("texto").GetProperty("minLength").GetInt32().Should().Be(1);
        propiedades.GetProperty("texto").GetProperty("maxLength").GetInt32().Should().Be(clsRespuestaAgenteEstructurada.LargoMaximoTexto);
        propiedades.GetProperty("acciones_sugeridas").GetProperty("maxItems").GetInt32().Should().Be(clsRespuestaAgenteEstructurada.MaximoAcciones);
        propiedades.GetProperty("acciones_sugeridas").GetProperty("uniqueItems").GetBoolean().Should().BeTrue();
        var estricto = clsReglasRespuestaAgente.EsquemaEstricto.GetProperty("properties");
        estricto.GetProperty("texto").TryGetProperty("maxLength", out _).Should().BeFalse("el modo estricto de OpenAI no admite maxLength");
        estricto.GetProperty("acciones_sugeridas").TryGetProperty("uniqueItems", out _).Should().BeFalse();
        estricto.GetProperty("acciones_sugeridas").GetProperty("maxItems").GetInt32().Should().Be(clsRespuestaAgenteEstructurada.MaximoAcciones);
    }

    // ---------- Reglas: forma ----------

    [Fact]
    public void UnaRespuestaConversacionalValidaPasa()
    {
        var validacion = clsReglasRespuestaAgente.Validar(Json("  ¿De qué ciudad sale la carga?  "), SinDatos());

        validacion.EsValida.Should().BeTrue();
        validacion.Respuesta!.Tipo.Should().Be(TipoRespuestaAgente.Conversacional);
        validacion.Respuesta.Texto.Should().Be("¿De qué ciudad sale la carga?");
        validacion.Respuesta.RequiereConfirmacion.Should().BeFalse();
        validacion.Respuesta.AccionesSugeridas.Should().BeNull();
    }

    [Fact]
    public void SeTolerianLasCercasDeCodigoYLaAusenciaDeAccionesSugeridas()
    {
        const string Crudo = "```json\n{\"tipo\": \"error\", \"texto\": \"El costo debe ser mayor que cero.\", \"requiere_confirmacion\": false}\n```";

        var validacion = clsReglasRespuestaAgente.Validar(Crudo, SinDatos());

        validacion.EsValida.Should().BeTrue();
        validacion.Respuesta!.Tipo.Should().Be(TipoRespuestaAgente.Error);
    }

    // Los 3 casos malformados que pide el issue (JSON incompleto, campo con tipo incorrecto, herramienta/campo alucinado) y los demás.
    [Theory]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"¿De qué ciudad sale", clsReglasRespuestaAgente.JsonInvalido)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"Hola\", \"requiere_confirmacion\": false", clsReglasRespuestaAgente.JsonInvalido)]
    [InlineData("Claro, aquí va: {\"tipo\": \"conversacional\"}", clsReglasRespuestaAgente.JsonInvalido)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"Hola\", \"requiere_confirmacion\": \"false\", \"acciones_sugeridas\": null}", clsReglasRespuestaAgente.TipoIncorrecto)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": 3352941, \"requiere_confirmacion\": false, \"acciones_sugeridas\": null}", clsReglasRespuestaAgente.TipoIncorrecto)]
    [InlineData("{\"tipo\": 1, \"texto\": \"Hola\", \"requiere_confirmacion\": false, \"acciones_sugeridas\": null}", clsReglasRespuestaAgente.TipoIncorrecto)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"Hola\", \"requiere_confirmacion\": false, \"acciones_sugeridas\": \"confirmar_pdf\"}", clsReglasRespuestaAgente.TipoIncorrecto)]
    [InlineData("{\"tipo\": \"confirmacion_requerida\", \"texto\": \"¿Reservo?\", \"requiere_confirmacion\": true, \"acciones_sugeridas\": [\"reservar_vehiculo\"]}", clsReglasRespuestaAgente.AccionInvalida)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"Consulto el clima.\", \"requiere_confirmacion\": false, \"acciones_sugeridas\": null, \"herramienta\": \"consultar_clima\"}", clsReglasRespuestaAgente.CampoDesconocido)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"Hola\", \"requiere_confirmacion\": false, \"precio\": 3500000}", clsReglasRespuestaAgente.CampoDesconocido)]
    [InlineData("{\"tipo\": \"invocacion_herramienta\", \"texto\": \"Hola\", \"requiere_confirmacion\": false, \"acciones_sugeridas\": null}", clsReglasRespuestaAgente.EnumInvalido)]
    [InlineData("{\"tipo\": \"Conversacional\", \"texto\": \"Hola\", \"requiere_confirmacion\": false, \"acciones_sugeridas\": null}", clsReglasRespuestaAgente.EnumInvalido)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"   \", \"requiere_confirmacion\": false, \"acciones_sugeridas\": null}", clsReglasRespuestaAgente.TextoVacio)]
    [InlineData("{\"tipo\": \"conversacional\", \"requiere_confirmacion\": false, \"acciones_sugeridas\": null}", clsReglasRespuestaAgente.CampoFaltante)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": null, \"requiere_confirmacion\": false}", clsReglasRespuestaAgente.CampoFaltante)]
    [InlineData("{\"texto\": \"Hola\", \"requiere_confirmacion\": false}", clsReglasRespuestaAgente.CampoFaltante)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"Hola\"}", clsReglasRespuestaAgente.CampoFaltante)]
    [InlineData("{\"tipo\": \"conversacional\", \"texto\": \"Hola\", \"texto\": \"$9.999.999\", \"requiere_confirmacion\": false}", clsReglasRespuestaAgente.CampoRepetido)]
    [InlineData("[{\"tipo\": \"conversacional\"}]", clsReglasRespuestaAgente.NoEsObjeto)]
    [InlineData("\"Hola, ¿qué cotizamos?\"", clsReglasRespuestaAgente.NoEsObjeto)]
    [InlineData("Hola, ¿qué cotizamos?", clsReglasRespuestaAgente.JsonInvalido)]
    [InlineData("", clsReglasRespuestaAgente.RespuestaVacia)]
    [InlineData("   ", clsReglasRespuestaAgente.RespuestaVacia)]
    public void UnaRespuestaMalformadaSeRechazaConSuCodigo(string crudo, string codigo)
    {
        var validacion = clsReglasRespuestaAgente.Validar(crudo, ConPrecio());

        validacion.EsValida.Should().BeFalse();
        Codigos(validacion).Should().Contain(codigo);
        validacion.Fallos.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.Detalle));
    }

    [Fact]
    public void UnaRespuestaNulaSeRechaza() =>
        Codigos(clsReglasRespuestaAgente.Validar(null, SinDatos())).Should().Equal(clsReglasRespuestaAgente.RespuestaVacia);

    [Fact]
    public void ElTextoAdmiteHastaCuatroMilCaracteres()
    {
        clsReglasRespuestaAgente.Validar(Json(new string('a', 4000)), SinDatos()).EsValida.Should().BeTrue();

        var largo = clsReglasRespuestaAgente.Validar(Json(new string('a', 4001)), SinDatos());

        Codigos(largo).Should().Equal(clsReglasRespuestaAgente.TextoLargo);
    }

    [Fact]
    public void SeInformanTodosLosFallosDeFormaALaVez()
    {
        var validacion = clsReglasRespuestaAgente.Validar(
            "{\"tipo\": \"accion\", \"texto\": \"\", \"requiere_confirmacion\": \"si\", \"acciones_sugeridas\": [\"llamar_cliente\"], \"extra\": 1}", SinDatos());

        Codigos(validacion).Should().BeEquivalentTo(
            clsReglasRespuestaAgente.CampoDesconocido, clsReglasRespuestaAgente.EnumInvalido, clsReglasRespuestaAgente.TextoVacio,
            clsReglasRespuestaAgente.TipoIncorrecto, clsReglasRespuestaAgente.AccionInvalida);
    }

    // ---------- Reglas: coherencia ----------

    [Theory]
    [InlineData("conversacional", true)]
    [InlineData("error", true)]
    [InlineData("confirmacion_requerida", false)]
    [InlineData("previsualizacion", false)]
    public void RequiereConfirmacionDebeCoincidirConElTipo(string tipo, bool requiere) =>
        Codigos(clsReglasRespuestaAgente.Validar(Json("Texto.", tipo, requiere), ConPrecio())).Should().Contain(clsReglasRespuestaAgente.IncoherenciaConfirmacion);

    [Fact]
    public void UnaPrevisualizacionSinPrecioCalculadoSeRechaza()
    {
        Codigos(clsReglasRespuestaAgente.Validar(Json("Aquí va la previsualización.", "previsualizacion", true), SinDatos()))
            .Should().Equal(clsReglasRespuestaAgente.PrevisualizacionSinPrecio);

        clsReglasRespuestaAgente.Validar(Json("El precio final es $3.352.941. ¿Confirmas?", "previsualizacion", true, "confirmar_pdf", "ajustar_margen"), ConPrecio())
            .Respuesta!.AccionesSugeridas.Should().Equal(AccionSugeridaAgente.ConfirmarPdf, AccionSugeridaAgente.AjustarMargen);
    }

    [Fact]
    public void LasAccionesExigenUnaPrevisualizacionPendienteYSuHerramientaDeclarada()
    {
        Codigos(clsReglasRespuestaAgente.Validar(Json("¿Genero el PDF?", "confirmacion_requerida", true, "confirmar_pdf"), SinDatos()))
            .Should().Equal(clsReglasRespuestaAgente.AccionNoDisponible);

        var sinHerramienta = ConPrecio() with { HerramientasDeclaradas = ["obtener_margen", "calcular_precio", "generar_pdf"] };
        Codigos(clsReglasRespuestaAgente.Validar(Json("¿Ajusto el margen?", "confirmacion_requerida", true, "ajustar_margen"), sinHerramienta))
            .Should().Equal(clsReglasRespuestaAgente.AccionNoDisponible);

        clsReglasRespuestaAgente.Validar(Json("¿Genero el PDF?", "confirmacion_requerida", true, "confirmar_pdf", "confirmar_pdf"), ConPrecio())
            .Respuesta!.AccionesSugeridas.Should().Equal(AccionSugeridaAgente.ConfirmarPdf);
    }

    // ---------- Reglas: montos y folios ----------

    [Theory]
    [InlineData("El precio final es $3.352.941. ¿Confirmas?")]
    [InlineData("El precio final es $ 3.352.941 con utilidad de $502.941.")]
    [InlineData("Precio: 3.352.941 COP; costo 2.850.000.")]
    [InlineData("El precio queda en 3352941 pesos.")]
    [InlineData("Con el costo que me diste ($2.850.000) y margen de 15 % el precio es COP 3.352.941,00.")]
    [InlineData("El margen de la política es 13,5 % y el costo 2,85 millones de pesos.")]
    [InlineData("La tarifa se cotiza a 30 días y 34 toneladas.")]
    [InlineData("¿Confirmas para generar el PDF, o quieres ajustar el margen?")]
    public void LosMontosRespaldadosPorLasHerramientasElEstadoOElComercialPasan(string texto) =>
        clsReglasRespuestaAgente.Validar(Json(texto), ConPrecio("Cotiza Nacional YUMBO a BUN, costo 2.850.000")).Fallos.Should().BeEmpty();

    [Theory]
    [InlineData("El precio final es $3.500.000.")]
    [InlineData("El precio final es 3.500.000 COP.")]
    [InlineData("El precio final es de 3.500.000.")]
    [InlineData("La utilidad queda en USD 1.200.")]
    [InlineData("Con descuento te queda en $3.300.000.")]
    [InlineData("El total aproximado es 3,4 millones de pesos.")]
    public void UnMontoInventadoSeRechaza(string texto)
    {
        var validacion = clsReglasRespuestaAgente.Validar(Json(texto), ConPrecio());

        Codigos(validacion).Should().Equal(clsReglasRespuestaAgente.MontoNoRespaldado);
        validacion.Fallos[0].Detalle.Should().Contain("no aparece en las salidas de las herramientas");
    }

    [Fact]
    public void UnPrecioDeUnTurnoAnteriorQuedaRespaldadoPorElEstado()
    {
        var contexto = ConPrecio() with { Salidas = [] };

        clsReglasRespuestaAgente.Validar(Json("Seguimos con el precio de $3.352.941 y la utilidad de $502.941."), contexto).Fallos.Should().BeEmpty();
    }

    [Fact]
    public void UnFolioInventadoSeRechazaYElDelServidorPasa()
    {
        Codigos(clsReglasRespuestaAgente.Validar(Json("Tu cotización es la PCN-24092026-007."), ConPrecio()))
            .Should().Equal(clsReglasRespuestaAgente.FolioNoRespaldado);

        clsReglasRespuestaAgente.Validar(Json($"Listo: la cotización {Folio}-v2 (versión 2 de {Folio}) quedó con un total de $3.352.941."), ConFolio())
            .Fallos.Should().BeEmpty("el folio no se lee como un monto y la versión reutiliza el folio base");
    }

    // Falsos positivos de la batería del validador de #16: NIT, teléfono y NIT con puntos no son montos.
    [Theory]
    [InlineData("El valor para el cliente con NIT 9000472281 es $3.294.798.")]
    [InlineData("Si necesitas ajustar el valor, llama al 3001234567.")]
    [InlineData("El costo depende del NIT 900.047.228-1 registrado.")]
    [InlineData("El total para el cliente 9000472281 es $3.294.798.")]
    [InlineData("Precio: $ 3,294,798; tu celular 300 123 4567 queda registrado.")]
    [InlineData("Valor total para 2 vehículos de 32 toneladas: $3.294.798.")]
    [InlineData("El costo por kilo es 1.200 aprox.")]
    [InlineData("Precio COP3.294.798")]
    [InlineData("El precio de la cotización del 24/09/2026 es $3.294.798.")]
    [InlineData("La tarifa vigente es de 2026 y aplica desde el 25 de septiembre.")]
    [InlineData("Costo 2,85 millones de pesos.")]
    public void LosIdentificadoresFechasYUnidadesNoSonMontos(string texto) =>
        clsReglasRespuestaAgente.Validar(Json(texto), Bateria()).Fallos.Should().BeEmpty();

    // Falsos negativos de la batería del validador de #16: montos sin palabra clave, pegados a la moneda, con sufijo de millones o
    // después del primero de la ventana.
    [Theory]
    [InlineData("Te lo dejo en 3.400.000.")]
    [InlineData("Precio: 13,5% sobre 4.000.000")]
    [InlineData("Te sale en COP3.500.000")]
    [InlineData("Precio: 3.5M")]
    [InlineData("Te sale en 3,5 MM.")]
    [InlineData("Quedaría en 3,5 millones más IVA.")]
    [InlineData("Cobraríamos 3.450.000 por el viaje.")]
    [InlineData("Precio sugerido: 3.294.798, o 3.100.000 con descuento.")]
    [InlineData("Precio 3.294.798 y descuento de 150.000.")]
    [InlineData("El precio es $3.294.798 COP y el costo $2.850.001.")]
    public void LosMontosSinPalabraClaveOPegadosSeRevisan(string texto) =>
        Codigos(clsReglasRespuestaAgente.Validar(Json(texto), Bateria())).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);

    [Fact]
    public void UnNumeroDelComercialSoloRespaldaSiLoEscribioComoDinero()
    {
        Codigos(clsReglasRespuestaAgente.Validar(Json("El precio es $12.500."), Bateria("Son 12.500 kg, costo 2.850.000")))
            .Should().Equal(clsReglasRespuestaAgente.MontoNoRespaldado);

        clsReglasRespuestaAgente.Validar(Json("Con el costo de $1.950.000 que me diste, sigo."), Bateria("El costo es de 1.950.000 pesos"))
            .Fallos.Should().BeEmpty();
        clsReglasRespuestaAgente.Validar(Json("Tomo el costo de 2,1 millones de pesos."), Bateria("costo 2,1 millones"))
            .Fallos.Should().BeEmpty();
    }

    [Fact]
    public void ElNitDelEstadoYLasCadenasDeDigitosDeLasSalidasRespaldan()
    {
        var contexto = Bateria() with
        {
            Salidas = [clsResultadoHerramienta.Exito("consultar_cotizacion", new Dictionary<string, object> { ["valor_registrado"] = "3100000" })],
        };

        clsReglasRespuestaAgente.Validar(Json("El valor registrado era $3.100.000."), contexto).Fallos.Should().BeEmpty();
    }

    [Fact]
    public void LosMontosDeUnTextoSeExtraenConSusValoresPosibles()
    {
        var montos = clsReglasRespuestaAgente.Montos("Precio $3.294.798, NIT 900123456, 12.500 kg y 3,5 millones; el 25/09/2026.");

        montos.Select(m => m.Fragmento).Should().Equal("$3.294.798", "3,5 millones");
        montos[0].Candidatos.Should().Contain(3_294_798m);
        montos[1].Candidatos.Should().Contain(3_500_000m);
    }

    // ---------- Orquestación: llamada final, reintento, respaldo ----------

    [Fact]
    public async Task UnaRespuestaValidaSaleEnUnaSolaLlamadaSinHerramientasYConElEsquema()
    {
        _modelo.ResponderFinal("¿De qué ciudad sale la carga?");
        var mensajes = new List<ChatMessage> { new(ChatRole.System, "sistema"), new(ChatRole.User, "hola") };

        var final = await _validador.ObtenerRespuestaFinalAsync(_modelo, mensajes, SinDatos(), Ct);

        final.EsRespaldo.Should().BeFalse();
        final.Llamadas.Should().Be(1);
        final.Reintentos.Should().Be(0);
        final.Respuesta.Texto.Should().Be("¿De qué ciudad sale la carga?");
        var llamada = _modelo.Llamadas.Should().ContainSingle().Subject;
        llamada.Herramientas.Should().BeEmpty();
        llamada.Opciones!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>().Which.Schema!.Value.GetRawText()
            .Should().Be(clsReglasRespuestaAgente.Esquema.GetRawText());
        llamada.Opciones.MaxOutputTokens.Should().Be(_opciones.MaxTokens);
        llamada.Mensajes[^1].Role.Should().Be(ChatRole.User);
        llamada.Mensajes[^1].Text.Should().Be(clsValidadorRespuestaAgente.InstruccionConEsquema);
        llamada.Opciones.AdditionalProperties.Should().BeNull("el modo estricto es opcional");
        mensajes.Should().HaveCount(2, "la conversación del runner no se modifica");
        _medidas.Should().BeEmpty();
    }

    [Fact]
    public async Task UnaRespuestaInvalidaSeReintentaUnaVezConLaCorreccion()
    {
        const string Invalida = "{\"tipo\": \"conversacional\", \"texto\": \"El precio es $3.500.000\", \"requiere_confirmacion\": \"no\"}";
        _modelo.Responder(Invalida).ResponderFinal("El precio final es $3.352.941.");

        var final = await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "Cotiza")], ConPrecio(), Ct);

        final.EsRespaldo.Should().BeFalse();
        final.Llamadas.Should().Be(2);
        final.Reintentos.Should().Be(1);
        final.Motivos.Should().Equal(clsReglasRespuestaAgente.TipoIncorrecto);
        final.Respuesta.Texto.Should().Be("El precio final es $3.352.941.");
        var reintento = _modelo.Llamadas[1].Mensajes;
        reintento[^2].Role.Should().Be(ChatRole.Assistant);
        reintento[^2].Text.Should().Be(Invalida);
        reintento[^1].Role.Should().Be(ChatRole.User);
        reintento[^1].Text.Should().Contain("requiere_confirmacion").And.Contain("booleano").And.Contain("SOLO con el objeto JSON");
        _modelo.Llamadas[1].Opciones!.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>();
        _medidas.Should().Contain((clsValidadorRespuestaAgente.MetricaReintentos, 1, null));
        _medidas.Should().Contain((clsValidadorRespuestaAgente.MetricaInvalidas, 1, clsReglasRespuestaAgente.TipoIncorrecto));
    }

    [Fact]
    public async Task SiElReintentoTambienFallaSeRespondeElRespaldoControlado()
    {
        _modelo.Responder("{\"tipo\": \"conversacional\", \"texto\": \"Tu NIT 900123456 de Juan Pérez").Responder(Json("Precio $9.999.999 para Juan Pérez."));

        var final = await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "Cotiza")], ConPrecio(), Ct);

        final.EsRespaldo.Should().BeTrue();
        final.Llamadas.Should().Be(2);
        final.Reintentos.Should().Be(1);
        final.Motivos.Should().Equal(clsReglasRespuestaAgente.JsonInvalido, clsReglasRespuestaAgente.MontoNoRespaldado);
        final.Respuesta.Should().Be(clsRespuestaAgenteEstructurada.Respaldo(clsTextosAgente.RespuestaNoEstructurada));
        final.Respuesta.Tipo.Should().Be(TipoRespuestaAgente.Error);
        final.Respuesta.Texto.Should().StartWith("No pude estructurar la respuesta");
        _medidas.Should().Contain((clsValidadorRespuestaAgente.MetricaRespaldos, 1, null));
        _medidas.Count(m => m.Instrumento == clsValidadorRespuestaAgente.MetricaReintentos).Should().Be(1, "un solo reintento");
        _logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains(clsReglasRespuestaAgente.JsonInvalido));
        _logger.Registros.Should().OnlyContain(r => !r.Mensaje.Contains("Juan") && !r.Mensaje.Contains("900123456") && !r.Mensaje.Contains("9.999.999"),
            "los logs llevan solo los códigos de los fallos, nunca el texto");
    }

    [Fact]
    public async Task SinSoporteDeJsonSchemaPorConfiguracionSePideSinFormatoYSeValidaIgual()
    {
        _opciones.SoportaJsonSchema = false;
        _modelo.Responder("```json\n" + Json("Hola, ¿qué cotizamos?") + "\n```");

        var final = await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "hola")], SinDatos(), Ct);

        final.Respuesta.Texto.Should().Be("Hola, ¿qué cotizamos?");
        _modelo.Llamadas.Should().ContainSingle().Which.Opciones!.ResponseFormat.Should().BeNull();
        _modelo.Llamadas[0].Mensajes[^1].Text.Should().Contain("\"requiere_confirmacion\"", "el esquema va en la instrucción");
    }

    [Fact]
    public async Task SiElProveedorRechazaElEsquemaSePasaAlModoSinFormatoYTrasElTtlSeVuelveAProbar()
    {
        _modelo.RechazarJsonSchema(new HttpRequestException("Invalid parameter: 'response_format' of type 'json_schema' is not supported with this model.", null, HttpStatusCode.BadRequest))
            .ResponderFinal("Hola, ¿qué cotizamos?")
            .ResponderFinal("¿Algo más?")
            .ResponderFinal("¿Seguimos?");

        var primera = await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "hola")], SinDatos(), Ct);
        _reloj.Avanzar(TimeSpan.FromMinutes(_opciones.MinutosSinJsonSchema - 1));
        var segunda = await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "gracias")], SinDatos(), Ct);
        _reloj.Avanzar(TimeSpan.FromMinutes(2));
        var tercera = await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "otra")], SinDatos(), Ct);

        primera.Respuesta.Texto.Should().Be("Hola, ¿qué cotizamos?");
        primera.Llamadas.Should().Be(2, "la llamada rechazada y la repetida sin formato");
        segunda.Llamadas.Should().Be(1, "dentro del TTL sigue sin formato");
        tercera.Llamadas.Should().Be(2, "tras el TTL vuelve a probar con esquema (re-sonda) y el proveedor lo rechaza otra vez");
        _modelo.Llamadas.Select(l => l.Opciones!.ResponseFormat is null).Should().Equal(false, true, true, false, true);
        _modelo.Llamadas[1].Mensajes[^1].Text.Should().Be(clsValidadorRespuestaAgente.InstruccionSinEsquema, "sin formato, el esquema va en la instrucción");
        _medidas.Count(m => m.Instrumento == clsValidadorRespuestaAgente.MetricaEsquemaRechazado).Should().Be(2);
        _logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("JSON Schema") && r.Mensaje.Contains("400"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(429)]
    public async Task UnErrorDelServidorQueMencionaElFormatoNoApagaElEsquema(int estado)
    {
        _modelo.RechazarJsonSchema(new clsErrorConStatus(estado, "upstream error while handling response_format json_schema"));

        var llamada = async () => await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "hola")], SinDatos(), Ct);

        await llamada.Should().ThrowAsync<clsErrorConStatus>();
        _modelo.Llamadas.Should().ContainSingle("un 5xx o un 429 se propagan: no son un esquema no soportado");
        _medidas.Should().NotContain(m => m.Instrumento == clsValidadorRespuestaAgente.MetricaEsquemaRechazado);
    }

    [Fact]
    public async Task ConElModoEstrictoSePideStrictYElEsquemaSinPalabrasNoAdmitidas()
    {
        _opciones.JsonSchemaEstricto = true;
        _modelo.ResponderFinal("Hola.");

        await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "hola")], SinDatos(), Ct);

        var opciones = _modelo.Llamadas[0].Opciones!;
        opciones.AdditionalProperties![clsValidadorRespuestaAgente.ClaveEstricto].Should().Be(true);
        opciones.ResponseFormat.Should().BeOfType<ChatResponseFormatJson>().Which.Schema!.Value.GetRawText()
            .Should().Be(clsReglasRespuestaAgente.EsquemaEstricto.GetRawText());
    }

    [Fact]
    public async Task UnFalloDelProveedorQueNoEsDelEsquemaSePropaga()
    {
        _modelo.Fallar(new HttpRequestException("Too Many Requests", null, HttpStatusCode.TooManyRequests));

        var llamada = async () => await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "hola")], SinDatos(), Ct);

        await llamada.Should().ThrowAsync<HttpRequestException>();
        _modelo.Llamadas.Should().ContainSingle("un 429 no se confunde con un esquema no soportado");
    }

    [Fact]
    public async Task LaCancelacionSePropagaAunqueFalteElReintento()
    {
        _modelo.Responder("{\"tipo\":").Fallar(new OperationCanceledException());

        var llamada = async () => await _validador.ObtenerRespuestaFinalAsync(_modelo, [new(ChatRole.User, "hola")], SinDatos(), Ct);

        await llamada.Should().ThrowAsync<OperationCanceledException>();
        _medidas.Should().NotContain(m => m.Instrumento == clsValidadorRespuestaAgente.MetricaRespaldos, "cancelar no es un respaldo");
    }

    [Theory]
    [InlineData(400, "'response_format' of type 'json_schema' is not supported", true)]
    [InlineData(422, "This model does not support structured output.", true)]
    [InlineData(400, "Invalid value for responseFormat", true)]
    [InlineData(400, "Invalid API key", false)]
    [InlineData(401, "response_format not allowed for this key", false)]
    [InlineData(500, "response_format json_schema backend error", false)]
    [InlineData(503, "Service Unavailable", false)]
    [InlineData(429, "response_format rate limit", false)]
    public void SoloUn400O422QueNombraElFormatoEsEsquemaNoSoportado(int estado, string mensaje, bool esEsquema)
    {
        clsValidadorRespuestaAgente.EsEsquemaNoSoportado(new InvalidOperationException("Fallo del proveedor", new HttpRequestException(mensaje, null, (HttpStatusCode)estado)))
            .Should().Be(esEsquema);
        clsValidadorRespuestaAgente.EsEsquemaNoSoportado(new clsErrorConStatus(estado, mensaje)).Should().Be(esEsquema, "también con la propiedad Status de los SDK");
    }

    [Fact]
    public void UnNotSupportedDelAdaptadorSinEstadoHttpCuentaComoEsquemaNoSoportado()
    {
        clsValidadorRespuestaAgente.EsEsquemaNoSoportado(new NotSupportedException("ResponseFormat")).Should().BeTrue();
        clsValidadorRespuestaAgente.EsEsquemaNoSoportado(new HttpRequestException("response_format")).Should().BeFalse("sin estado HTTP solo cuenta NotSupported");
    }

    [Fact]
    public void LaRespuestaEstructuradaSeSerializaConLosCodigosDelEsquema() =>
        JsonSerializer.Serialize(
                new clsRespuestaAgenteEstructurada { Tipo = TipoRespuestaAgente.ConfirmacionRequerida, Texto = "¿Genero el PDF?", RequiereConfirmacion = true, AccionesSugeridas = [AccionSugeridaAgente.ConfirmarPdf] },
                clsReglasRespuestaAgente.OpcionesJson)
            .Should().Be("{\"tipo\":\"confirmacion_requerida\",\"texto\":\"¿Genero el PDF?\",\"requiere_confirmacion\":true,\"acciones_sugeridas\":[\"confirmar_pdf\"]}");
}
