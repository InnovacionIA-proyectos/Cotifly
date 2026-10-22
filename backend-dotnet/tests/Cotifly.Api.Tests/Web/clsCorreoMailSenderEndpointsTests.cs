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
        json.GetProperty("copia_oculta").GetString().Should().Be(ana.NombreUsuario + "@boterosoto.com.co");
        json.GetProperty("cuerpo_html").GetString().Should().Contain("Botero Soto</b>");
        _api.MailSender.Recibidos.Should().BeEmpty("la vista previa no envía nada");
    }

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    public void Dispose() => _api.Dispose();
}
