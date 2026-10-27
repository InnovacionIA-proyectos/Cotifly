using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Validación de #228 (DEF-1027B-2, ADR-023): vectores de inyección de cabeceras en <c>POST .../enviar</c>, en la vista previa y en el
/// reintento, por HTTP con SQL Server de pruebas y MailSender falso (registra el multipart; nunca sale un correo). Ningún vector llega a
/// MailSender ni cambia el estado de envío; el tope de 20 no se salta con duplicados, mayúsculas ni repartiendo entre <c>para</c> y
/// <c>cc</c>; un correo guardado antes de #228 con el asunto inyectado no se reintenta.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCabecerasCorreoEndpointsTests : IDisposable
{
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _get = HttpMethod.Get;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsApiWebPruebas _api;

    public clsCabecerasCorreoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = new clsApiWebPruebas(entorno.BaseDatos, correoHabilitado: true);
        _api.MailSender.Caido = false;
    }

    /// <summary>Cuerpos JSON CRUDOS (los escapes <c>\u000d</c> los decodifica el servidor): asunto, para y cc con cada vector.</summary>
    public static TheoryData<string> VectoresDeInyeccion =>
    [
        // Asunto: CR/LF en todas sus formas que el JSON puede traer, separadores Unicode, C1 y demás controles.
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\r\nBcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\u000d\u000aBcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\nBcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\rBcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\u0085Bcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\u2028Bcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\u2029Bcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\u000bBcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\u000cBcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\u0000Bcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\tBcc: intruso@ejemplo.invalid"}""",
        """{"para":["x@ejemplo.invalid"],"asunto":"Hola\r\n"}""",
        // Destinatarios: controles, listas, nombre visible, comillas, comentarios, ángulos, varias arrobas, ancho cero, BOM, literal IP,
        // dominio de una etiqueta.
        """{"para":["x@ejemplo.invalid\r\nBcc: intruso@ejemplo.invalid"]}""",
        """{"para":["x@ejemplo.invalid\u0085"]}""",
        """{"para":["x@ejemplo.invalid\u2028"]}""",
        """{"para":["x@ejemplo.invalid"],"cc":["y@ejemplo.invalid\r\n"]}""",
        """{"para":["x@ejemplo.invalid, intruso@ejemplo.invalid"]}""",
        """{"para":["x@ejemplo.invalid;intruso@ejemplo.invalid"]}""",
        """{"para":["Cliente <x@ejemplo.invalid>"]}""",
        """{"para":["<x@ejemplo.invalid>"]}""",
        """{"para":["\"x y\"@ejemplo.invalid"]}""",
        """{"para":["x(comentario)@ejemplo.invalid"]}""",
        """{"para":["x@ejemplo.invalid (Cliente)"]}""",
        """{"para":["x@intruso.invalid@ejemplo.invalid"]}""",
        """{"para":["x\u200B@ejemplo.invalid"]}""",
        """{"para":["x\uFEFF@ejemplo.invalid"]}""",
        """{"para":["x@[10.0.0.1]"]}""",
        """{"para":["x@localhost"]}""",
        """{"para":["x@ejemplo.invalid"],"cc":["Jefe <y@ejemplo.invalid>"]}""",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(VectoresDeInyeccion))]
    public async Task Validacion228_NingunVectorDeInyeccionLlegaAMailSender(string cuerpo)
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900228001");

        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access, cuerpo);
        (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.BadRequest))
            .GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "CORREO_INVALIDO es ErrorWeb");

        using var vista = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa", ana.Access, cuerpo);
        await VerificarContratoAsync(vista, "/web/cotizaciones/{folio}/correo/vista-previa", _post, HttpStatusCode.BadRequest);

        _api.MailSender.Recibidos.Should().BeEmpty();
        (await EscalarAsync($"SELECT estadoEnvio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{cotizacion.Folio}'")).Should().Be("sin_enviar");
        (await EscalarAsync($"SELECT CONCAT(COUNT(*), '') FROM dbo_cotifly.Correos c JOIN dbo_cotifly.CotizacionesSolicitud s ON s.id = c.cotizacionId WHERE s.folio = '{cotizacion.Folio}'"))
            .Should().Be("0");
    }

    /// <summary>UTF-8 inválido (CR "overlong" <c>C0 8D</c>, LF <c>C0 8A</c>): el JSON no se puede leer, 400 sin llegar a MailSender.</summary>
    [Fact]
    public async Task Validacion228_UnCrLfOverlongEnUtf8NoSeLeeComoSaltoYNoLlegaAMailSender()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900228002");
        byte[] cuerpo = [.. "{\"para\":[\"x@ejemplo.invalid\"],\"asunto\":\"Hola"u8, 0xC0, 0x8D, 0xC0, 0x8A, .. "Bcc: intruso@ejemplo.invalid\"}"u8];
        using var peticion = new HttpRequestMessage(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar")
        {
            Content = new ByteArrayContent(cuerpo) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ana.Access);

        using var respuesta = await _api.Cliente.SendAsync(peticion, Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _api.MailSender.Recibidos.Should().BeEmpty();
    }

    /// <summary>
    /// Lo que NO es un salto de línea (texto literal "%0d%0a", "\r\n" escrito, una encoded-word RFC 2047, un espacio de ancho cero) es
    /// asunto válido: llega a MailSender en UNA sola parte <c>Subject</c>, sin CR/LF, y no agrega destinatarios.
    /// </summary>
    [Theory]
    [InlineData("Hola%0d%0aBcc: intruso@ejemplo.invalid")]
    [InlineData("Hola\\r\\nBcc: intruso@ejemplo.invalid")]
    [InlineData("=?utf-8?q?Hola=0D=0ABcc:_intruso@ejemplo.invalid?=")]
    [InlineData("Hola\u200BBcc: intruso@ejemplo.invalid")]
    public async Task Validacion228_UnAsuntoLiteralSinSaltosSaleEnUnaSolaCabecera(string asunto)
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900228003");

        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" }, asunto }));

        await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK);
        var recibido = _api.MailSender.Recibidos.Should().ContainSingle().Subject;
        recibido.Subject.Should().Be("[QA] " + asunto).And.NotContainAny("\r", "\n");
        recibido.To.Should().Equal(clsMailSenderFalso.BuzonPruebas);
        recibido.Cc.Should().BeEmpty();
        recibido.Bcc.Should().BeEmpty();
        recibido.From.Should().NotContainAny("\r", "\n");
        recibido.CorrelationId.Should().NotContainAny("\r", "\n");
        recibido.Adjuntos.Should().ContainSingle().Which.Nombre.Should().NotContainAny("\r", "\n");
    }

    /// <summary>El tope (20 entre para y cc) cuenta cada entrada: no se salta con duplicados, mayúsculas ni repartiendo en cc.</summary>
    [Fact]
    public async Task Validacion228_ElTopeNoSeSaltaConDuplicadosMayusculasNiCc()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900228004");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/enviar";
        object[] excedidos =
        [
            new { para = Enumerable.Repeat("compras@cliente.com", 21).ToArray() },
            new { para = Enumerable.Range(0, 21).Select(i => i % 2 == 0 ? "COMPRAS@CLIENTE.COM" : "compras@cliente.com").ToArray() },
            new { para = Enumerable.Range(1, 10).Select(i => $"p{i}@cliente.com").ToArray(), cc = Enumerable.Range(1, 11).Select(i => $"c{i}@cliente.com").ToArray() },
            new { para = new[] { "p@cliente.com" }, cc = Enumerable.Range(1, 20).Select(i => $"c{i}@cliente.com").ToArray() },
        ];

        foreach (var cuerpo in excedidos)
        {
            using var envio = await _api.EnviarAsync(_post, ruta, ana.Access, clsApiWebPruebas.Json(cuerpo));
            var error = (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.BadRequest)).GetProperty("error");
            error.GetProperty("codigo").GetString().Should().Be("CORREO_DEMASIADOS_DESTINATARIOS");
            error.GetProperty("detalle").GetProperty("recibidos").GetInt32().Should().Be(21);

            using var vista = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa", ana.Access, clsApiWebPruebas.Json(cuerpo));
            await VerificarErrorNegocioAsync(vista, "CORREO_DEMASIADOS_DESTINATARIOS");
        }

        _api.MailSender.Recibidos.Should().BeEmpty();

        // Las entradas vacías no cuentan y 20 justos sí salen.
        using var justo = await _api.EnviarAsync(_post, ruta, ana.Access, clsApiWebPruebas.Json(new
        {
            para = Enumerable.Range(1, 10).Select(i => $"p{i}@cliente.com").Append("  ").ToArray(),
            cc = Enumerable.Range(1, 10).Select(i => $"c{i}@cliente.com").Append(string.Empty).ToArray(),
        }));
        await VerificarContratoAsync(justo, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK);
        _api.MailSender.Recibidos.Should().ContainSingle();
    }

    /// <summary>
    /// Un correo aceptado ANTES de #228 con el asunto inyectado (MailSender lo guardó así) no se reintenta: <c>/retry</c> lo reenviaría con
    /// las mismas cabeceras. 409 <c>CORREO_NO_REINTENTABLE</c> sin llamar a MailSender, y el historial lo muestra no reintentable.
    /// </summary>
    [Fact]
    public async Task Validacion228_UnCorreoViejoConElAsuntoInyectadoNoSeReintenta()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900228005");
        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } }));
        var correoId = (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK)).GetProperty("correo_id").GetString();
        _api.MailSender.Fallar(_api.MailSender.Recibidos[^1].Id);
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Correos SET asunto = CONCAT(N'Hola', CHAR(13), CHAR(10), N'Bcc: intruso@ejemplo.invalid') WHERE id = '{correoId}'");

        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correos/{correoId}/reintentar", ana.Access);

        reintento.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await VerificarErrorNegocioAsync(reintento, "CORREO_NO_REINTENTABLE");
        _api.MailSender.Reintentos.Should().Be(0);
        (await EscalarAsync($"SELECT estado FROM dbo_cotifly.Correos WHERE id = '{correoId}'")).Should().Be("fallido");
        using var historial = await _api.EnviarAsync(_get, $"/web/cotizaciones/{cotizacion.Folio}/correos", ana.Access);
        (await VerificarContratoAsync(historial, "/web/cotizaciones/{folio}/correos", _get, HttpStatusCode.OK))
            .GetProperty("correos").EnumerateArray().Should().ContainSingle().Which.GetProperty("reintentable").GetBoolean().Should().BeFalse();
    }

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose() => _api.Dispose();
}
