using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// HU-033 (#199, ADR-023): el envío por MailSender (falso: nunca sale un correo real) por HTTP con SQL Server real. El 200 deja el envío
/// <c>encolado</c> con el id de MailSender; el worker lo lleva a <c>enviado</c> o <c>fallido</c>; el historial lo muestra; el reintento solo
/// aplica a un fallido propio; fuera de producción todo va al buzón de pruebas y la vista previa lo avisa. Contrato verificado.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCorreoMailSenderEndpointsTests : IDisposable
{
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _get = HttpMethod.Get;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsApiWebPruebas _api;

    public clsCorreoMailSenderEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = new clsApiWebPruebas(entorno.BaseDatos, correoHabilitado: true);
        _api.MailSender.Caido = false;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElEnvioQuedaEncoladoElWorkerLoConfirmaYElHistorialLoMuestra()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111222");

        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" }, cc = new[] { "jefe@cliente.com" } }));
        var json = await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK);

        json.GetProperty("enviado").GetBoolean().Should().BeTrue();
        json.GetProperty("estado").GetString().Should().Be("encolado");
        json.GetProperty("redirigido_a").GetString().Should().Be(clsMailSenderFalso.BuzonPruebas, "el ambiente de pruebas redirige");
        var recibido = _api.MailSender.Recibidos.Should().ContainSingle().Subject;
        json.GetProperty("message_id").GetString().Should().Be(recibido.Id.ToString());
        recibido.To.Should().Equal(clsMailSenderFalso.BuzonPruebas);
        recibido.Cc.Should().BeEmpty();
        recibido.Bcc.Should().BeEmpty("la copia oculta al comercial tampoco sale de pruebas");
        recibido.Subject.Should().StartWith("[QA] ");
        recibido.Body.Should().Contain("compras@cliente.com").And.Contain("jefe@cliente.com").And.Contain(ana.NombreUsuario + "@boterosoto.com.co");
        recibido.CorrelationId.Should().Be($"{cotizacion.Folio}:{json.GetProperty("correo_id").GetString()}");
        (await EscalarAsync($"SELECT CONCAT(estado, '|', CAST(idMailSender AS nvarchar(36)), '|', redirigidoA) FROM dbo_cotifly.Correos WHERE id = '{json.GetProperty("correo_id").GetString()}'"))
            .Should().Be($"encolado|{recibido.Id.ToString().ToUpperInvariant()}|{clsMailSenderFalso.BuzonPruebas}");

        _api.MailSender.Entregar(recibido.Id);
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);

        using var historial = await _api.EnviarAsync(_get, $"/web/cotizaciones/{cotizacion.Folio}/correos", ana.Access);
        var correo = (await VerificarContratoAsync(historial, "/web/cotizaciones/{folio}/correos", _get, HttpStatusCode.OK))
            .GetProperty("correos").EnumerateArray().Should().ContainSingle().Subject;
        correo.GetProperty("estado").GetString().Should().Be("enviado");
        correo.GetProperty("para").EnumerateArray().Select(p => p.GetString()).Should().Equal("compras@cliente.com");
        correo.GetProperty("cc").EnumerateArray().Select(p => p.GetString()).Should().Equal("jefe@cliente.com");
        correo.GetProperty("enviado_at").ValueKind.Should().Be(JsonValueKind.String);
        correo.GetProperty("reintentable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task UnFallidoDeMailSenderSeReintentaSoloPorElDuenoYSoloSiFallo()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111333");
        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } }));
        var correoId = (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK)).GetProperty("correo_id").GetString();
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/correos/{correoId}/reintentar";

        using (var enCurso = await _api.EnviarAsync(_post, ruta, ana.Access))
        {
            enCurso.StatusCode.Should().Be(HttpStatusCode.Conflict);
            await VerificarErrorNegocioAsync(enCurso, "CORREO_NO_REINTENTABLE");
        }

        _api.MailSender.Fallar(_api.MailSender.Recibidos[^1].Id, "550 5.7.1 Unable to relay");
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);
        (await EscalarAsync($"SELECT CONCAT(estado, '|', error) FROM dbo_cotifly.Correos WHERE id = '{correoId}'")).Should().Be("fallido|550 5.7.1 Unable to relay");
        (await EscalarAsync($"SELECT estadoEnvio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{cotizacion.Folio}'"))
            .Should().Be("enviada", "un fallo posterior en MailSender no devuelve el estado de envío: se reintenta el mismo correo");

        using (var ajena = await _api.EnviarAsync(_post, ruta, luis.Access))
        {
            ajena.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        using var reintento = await _api.EnviarAsync(_post, ruta, ana.Access);
        (await VerificarContratoAsync(reintento, "/web/cotizaciones/{folio}/correos/{correoId}/reintentar", _post, HttpStatusCode.OK))
            .GetProperty("estado").GetString().Should().Be("encolado");
        _api.MailSender.Reintentos.Should().Be(1);

        using var otro = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correos/{Guid.NewGuid()}/reintentar", ana.Access);
        otro.StatusCode.Should().Be(HttpStatusCode.NotFound, "un envío que no es de la cotización");
    }

    [Fact]
    public async Task UnRechazoDeMailSenderEsEnvioFallidoSinCambiarElEstadoDeEnvio()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111444");
        _api.MailSender.AltaFalla = HttpStatusCode.UnprocessableEntity;

        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } }));

        (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.BadRequest))
            .GetProperty("error").GetProperty("codigo").GetString().Should().Be("CORREO_ENVIO_FALLIDO");
        (await EscalarAsync($"SELECT estadoEnvio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{cotizacion.Folio}'")).Should().Be("sin_enviar");
        (await EscalarAsync($"SELECT CONCAT(c.estado, '|', IIF(c.idMailSender IS NULL, '-', 'I')) FROM dbo_cotifly.Correos c JOIN dbo_cotifly.CotizacionesSolicitud s ON s.id = c.cotizacionId WHERE s.folio = '{cotizacion.Folio}'"))
            .Should().Be("fallido|-");
    }

    /// <summary>
    /// DEF-1027B-2 (#228, E2E real 2026-10-27B): el asunto "Hola\r\nBcc: intruso@ejemplo.invalid" respondió 200 y llegó al relay. Ahora es 400
    /// <c>ErrorWeb</c> sin llamar a MailSender ni tocar el estado de envío; lo mismo una dirección con CR/LF o con una coma.
    /// </summary>
    [Fact]
    public async Task DEF1027B2_UnAsuntoOUnaDireccionConSaltosDeLineaEs400SinLlegarAMailSender()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111556");
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/enviar";
        object[] cuerpos =
        [
            new { para = new[] { "x@ejemplo.invalid" }, asunto = "Hola\r\nBcc: intruso@ejemplo.invalid", cuerpo = "x" },
            new { para = new[] { "x@ejemplo.invalid\r\nBcc: intruso@ejemplo.invalid" } },
            new { para = new[] { "x@ejemplo.invalid" }, cc = new[] { "y@ejemplo.invalid, intruso@ejemplo.invalid" } },
        ];

        foreach (var cuerpo in cuerpos)
        {
            using var envio = await _api.EnviarAsync(_post, ruta, ana.Access, clsApiWebPruebas.Json(cuerpo));
            (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.BadRequest))
                .GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "CORREO_INVALIDO es ErrorWeb");
        }

        using var vista = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa", ana.Access,
            clsApiWebPruebas.Json(new { asunto = "Hola\r\nBcc: intruso@ejemplo.invalid" }));
        await VerificarContratoAsync(vista, "/web/cotizaciones/{folio}/correo/vista-previa", _post, HttpStatusCode.BadRequest);

        _api.MailSender.Recibidos.Should().BeEmpty();
        (await EscalarAsync($"SELECT estadoEnvio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{cotizacion.Folio}'")).Should().Be("sin_enviar");
        (await EscalarAsync($"SELECT CONCAT(COUNT(*), '') FROM dbo_cotifly.Correos c JOIN dbo_cotifly.CotizacionesSolicitud s ON s.id = c.cotizacionId WHERE s.folio = '{cotizacion.Folio}'"))
            .Should().Be("0");
    }

    /// <summary>
    /// #228: el E2E aceptó 60 destinatarios en <c>para</c>. Tope entre <c>para</c> y <c>cc</c>, 10 por defecto (decisión del 2026-10-27):
    /// 400 <c>ErrorNegocio</c> con detalle.
    /// </summary>
    [Fact]
    public async Task Tope228_MasDe10DestinatariosEs400ConDetalleYNoLlegaAMailSender()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111557");

        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = Enumerable.Range(1, 60).Select(i => $"c{i}@ejemplo.invalid").ToArray() }));

        var error = (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.BadRequest)).GetProperty("error");
        error.GetProperty("codigo").GetString().Should().Be("CORREO_DEMASIADOS_DESTINATARIOS");
        error.GetProperty("detalle").GetProperty("maximo").GetInt32().Should().Be(10);
        error.GetProperty("detalle").GetProperty("recibidos").GetInt32().Should().Be(60);
        _api.MailSender.Recibidos.Should().BeEmpty();
    }

    /// <summary>#228: con <c>Correo:MaximoDestinatarios</c> configurado, el tope y <c>detalle.maximo</c> son ese valor (envío y vista previa).</summary>
    [Fact]
    public async Task Tope228_ElTopeConfiguradoMandaEnElEnvioYEnLaVistaPrevia()
    {
        using var api = new clsApiWebPruebas(_entorno.BaseDatos, correoHabilitado: true,
            extra: new Dictionary<string, string?> { ["Correo:MaximoDestinatarios"] = "3" });
        api.MailSender.Caido = false;
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, "900111558");
        var cuatro = clsApiWebPruebas.Json(new { para = new[] { "a@cliente.com", "b@cliente.com" }, cc = new[] { "c@cliente.com", "d@cliente.com" } });

        using var vista = await api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa", ana.Access, cuatro);
        using var envio = await api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access, cuatro);

        foreach (var respuesta in new[] { vista, envio })
        {
            respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            using var documento = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));
            var error = documento.RootElement.GetProperty("error");
            error.GetProperty("codigo").GetString().Should().Be("CORREO_DEMASIADOS_DESTINATARIOS");
            error.GetProperty("detalle").GetProperty("maximo").GetInt32().Should().Be(3);
            error.GetProperty("detalle").GetProperty("recibidos").GetInt32().Should().Be(4);
        }

        api.MailSender.Recibidos.Should().BeEmpty();

        using var justo = await api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "a@cliente.com", "b@cliente.com" }, cc = new[] { "c@cliente.com" } }));
        justo.StatusCode.Should().Be(HttpStatusCode.OK, await justo.Content.ReadAsStringAsync(Ct));
        api.MailSender.Recibidos.Should().ContainSingle();
    }

    [Fact]
    public async Task LaVistaPreviaAvisaQueEsUnAmbienteDePruebas()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111555");

        using var vista = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correo/vista-previa", ana.Access);
        var json = await VerificarContratoAsync(vista, "/web/cotizaciones/{folio}/correo/vista-previa", _post, HttpStatusCode.OK);

        json.GetProperty("entorno_pruebas").GetBoolean().Should().BeTrue();
        json.GetProperty("redirigido_a").GetString().Should().Be(clsMailSenderFalso.BuzonPruebas);
        json.GetProperty("aviso").GetString().Should().Contain("Ambiente de pruebas");
        // OBS-1027-5 (#226): al redirigir el correo sale sin CC ni CCO, así que la vista previa no anuncia la copia oculta al comercial.
        json.GetProperty("copia_oculta").ValueKind.Should().Be(JsonValueKind.Null);
        json.GetProperty("aviso").GetString().Should().Contain("sin CC ni CCO");
        // #262: el HTML es el del correo (encabezado "Cotización" con el logo).
        json.GetProperty("cuerpo_html").GetString().Should().Contain(">Cotizaci&#243;n<").And.Contain("<img src=\"https://");
        _api.MailSender.Recibidos.Should().BeEmpty("la vista previa no envía nada");
    }

    [Fact]
    public async Task AuditoriaUnReintentoDeUnCorreoDeMasDe24HorasNoVenceEnLaPrimeraConsulta()
    {
        // Auditoría #199: la edad máxima se medía desde createdAt, así que reintentar un fallido viejo (por ejemplo, el que venció a las 24 h)
        // lo devolvía a "fallido" en la primera consulta sin preguntarle a MailSender. Ahora se mide desde la última aceptación (encoladoAt).
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111666");
        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } }));
        var correoId = (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK)).GetProperty("correo_id").GetString();
        _api.MailSender.Fallar(_api.MailSender.Recibidos[^1].Id);
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Correos SET createdAt = DATEADD(HOUR, -30, SYSDATETIMEOFFSET()), encoladoAt = DATEADD(HOUR, -30, SYSDATETIMEOFFSET()) WHERE id = '{correoId}'");

        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/correos/{correoId}/reintentar", ana.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);

        (await EscalarAsync($"SELECT estado FROM dbo_cotifly.Correos WHERE id = '{correoId}'"))
            .Should().Be("encolado", "MailSender todavía lo tiene en cola: el reintento se sigue, no vence por la fecha del primer envío");
        _api.MailSender.Entregar(_api.MailSender.Recibidos[^1].Id);
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);
        (await EscalarAsync($"SELECT estado FROM dbo_cotifly.Correos WHERE id = '{correoId}'")).Should().Be("enviado");
    }

    [Fact]
    public async Task AuditoriaDosReintentosSimultaneosPidenUnSoloRetryAMailSender()
    {
        // Auditoría #199: un doble clic pedía dos /retry y MailSender corría dos entregas del mismo correo a la vez (correo duplicado).
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111777");
        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } }));
        var correoId = (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK)).GetProperty("correo_id").GetString();
        _api.MailSender.Fallar(_api.MailSender.Recibidos[^1].Id);
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/correos/{correoId}/reintentar";
        _api.MailSender.Demora = TimeSpan.FromMilliseconds(500);

        var respuestas = await Task.WhenAll(_api.EnviarAsync(_post, ruta, ana.Access), _api.EnviarAsync(_post, ruta, ana.Access));
        _api.MailSender.Demora = null;
        try
        {
            respuestas.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
            await VerificarErrorNegocioAsync(respuestas.Single(r => r.StatusCode == HttpStatusCode.Conflict), "CORREO_NO_REINTENTABLE");
        }
        finally
        {
            foreach (var r in respuestas)
            {
                r.Dispose();
            }
        }

        _api.MailSender.Reintentos.Should().Be(1, "un solo /retry por fallo");
        (await EscalarAsync($"SELECT estado FROM dbo_cotifly.Correos WHERE id = '{correoId}'")).Should().Be("encolado");
    }

    [Fact]
    public async Task AuditoriaUnReintentoQueMailSenderNoAceptaDejaElCorreoFallidoYReintentable()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await _api.CrearCotizacionAsync(ana.UsuarioId, "900111888");
        using var envio = await _api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } }));
        var correoId = (await VerificarContratoAsync(envio, "/web/cotizaciones/{folio}/enviar", _post, HttpStatusCode.OK)).GetProperty("correo_id").GetString();
        _api.MailSender.Fallar(_api.MailSender.Recibidos[^1].Id);
        (await _api.ProcesarCorreosAsync(cotizacion.Folio)).Should().Be(1);
        var ruta = $"/web/cotizaciones/{cotizacion.Folio}/correos/{correoId}/reintentar";

        _api.MailSender.Caido = true;
        using (var caido = await _api.EnviarAsync(_post, ruta, ana.Access))
        {
            caido.StatusCode.Should().NotBe(HttpStatusCode.OK);
        }

        (await EscalarAsync($"SELECT CONCAT(estado, '|', IIF(siguienteConsultaAt IS NULL, '-', 'agendado')) FROM dbo_cotifly.Correos WHERE id = '{correoId}'"))
            .Should().Be("fallido|-", "la reserva del reintento se deshace si MailSender no lo aceptó");
        _api.MailSender.Caido = false;
        using var otraVez = await _api.EnviarAsync(_post, ruta, ana.Access);
        otraVez.StatusCode.Should().Be(HttpStatusCode.OK, "sigue siendo reintentable");
    }

    [Fact]
    public async Task AuditoriaSinRespuestaAlEncolarElEnvioQuedaInciertoYBloqueaElReenvioInmediato()
    {
        // Auditoría #199: un tiempo agotado en el POST pudo dejar el correo encolado en MailSender. Antes se deshacía la reserva y la fila
        // "fallido" no contaba en la deduplicación: un reenvío inmediato duplicaba el correo al cliente.
        using var api = new clsApiWebPruebas(_entorno.BaseDatos, correoHabilitado: true,
            extra: new Dictionary<string, string?> { ["Correo:MailSender:TimeoutSegundos"] = "1" });
        api.MailSender.Caido = false;
        var ana = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await api.CrearCotizacionAsync(ana.UsuarioId, "900111999");
        api.MailSender.Demora = TimeSpan.FromSeconds(3);

        using (var lento = await api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
                   clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } })))
        {
            await VerificarErrorNegocioAsync(lento, "CORREO_ENVIO_FALLIDO");
        }

        api.MailSender.Demora = null;
        (await EscalarAsync($"SELECT estadoEnvio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{cotizacion.Folio}'"))
            .Should().Be("enviada", "el PDF pudo salir: la reserva se conserva");
        (await EscalarAsync($"SELECT CONCAT(c.estado, '|', IIF(c.idMailSender IS NULL, '-', 'I'), '|', LEFT(c.error, 20)) FROM dbo_cotifly.Correos c JOIN dbo_cotifly.CotizacionesSolicitud s ON s.id = c.cotizacionId WHERE s.folio = '{cotizacion.Folio}'"))
            .Should().Be("fallido|-|Resultado incierto: ");

        using var otra = await api.EnviarAsync(_post, $"/web/cotizaciones/{cotizacion.Folio}/enviar", ana.Access,
            clsApiWebPruebas.Json(new { para = new[] { "compras@cliente.com" } }));
        await VerificarErrorNegocioAsync(otra, "CORREO_DUPLICADO");
        api.MailSender.Recibidos.Should().BeEmpty("el reenvío inmediato no llegó a MailSender");
    }

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose() => _api.Dispose();
}
