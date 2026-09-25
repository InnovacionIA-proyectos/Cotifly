using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// <c>GET /pdfs/{archivo}</c> (dueño o gerente de la regional), administración de líneas (solo <c>gerente_comercial</c>,
/// auditada) y métricas en borrador (alcance por rol y regional) por HTTP contra la base real.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPdfAdminMetricasEndpointsTests(clsEntornoApiWeb entorno)
{
    private const string NoEsTuya = """{"error":"Cotización no encontrada o no es tuya."}""";
    private const string SinPermiso = """{"error":"No tienes permiso para esta operación."}""";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _put = HttpMethod.Put;

    private clsApiWebPruebas Api => entorno.Api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Regional() => $"Reg-{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task ElDuenoDescargaSuPdfInlineYSinSesionEs401()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var cotizacion = await Api.CrearCotizacionAsync(ana.UsuarioId, "900111222");

        using var respuesta = await Api.EnviarAsync(_get, $"/pdfs/{cotizacion.ArchivoPdf}", ana.Access);
        using var sinSesion = await Api.EnviarAsync(_get, $"/pdfs/{cotizacion.ArchivoPdf}", null);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        respuesta.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        respuesta.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        (await respuesta.Content.ReadAsByteArrayAsync(Ct)).Should().Equal(clsApiWebPruebas.PdfMinimo);
        respuesta.Headers.CacheControl!.NoStore.Should().BeTrue();
        sinSesion.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ElGerenteDeLaRegionalDescargaElPdfYElDeOtraRegionalOUnComercialAjenoNo()
    {
        var occidente = Regional();
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente);
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var hugo = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, Regional());
        var sinRegional = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);
        var deLuis = await Api.CrearCotizacionAsync(luis.UsuarioId, "800333444");
        var ruta = $"/pdfs/{deLuis.ArchivoPdf}";

        using var gerenteMismaRegional = await Api.EnviarAsync(_get, ruta, gloria.Access);
        using var gerenteOtraRegional = await Api.EnviarAsync(_get, ruta, hugo.Access);
        using var comercialAjeno = await Api.EnviarAsync(_get, ruta, ana.Access);
        using var gerenteSinRegional = await Api.EnviarAsync(_get, ruta, sinRegional.Access);
        var auditados = await clsBaseDatosPruebas.EscalarAsync<int>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'descargar_pdf' AND input LIKE '%{deLuis.ArchivoPdf}%'");

        gerenteMismaRegional.StatusCode.Should().Be(HttpStatusCode.OK);
        gerenteOtraRegional.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await gerenteOtraRegional.Content.ReadAsStringAsync(Ct)).Should().Be(NoEsTuya);
        comercialAjeno.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await comercialAjeno.Content.ReadAsStringAsync(Ct)).Should().Be(NoEsTuya);
        gerenteSinRegional.StatusCode.Should().Be(HttpStatusCode.OK, "sin regional conocida no se puede restringir (contrato: dueño o gerente_comercial)");
        auditados.Should().Be(4, "cada acceso de alguien que no es el dueño queda auditado, permitido o no");
    }

    [Theory]
    [InlineData("otro-sin-extension")]
    [InlineData("PCN-01011999-001.pdf")]
    [InlineData("..%2F..%2Fetc%2Fpasswd.pdf")]
    public async Task UnArchivoInvalidoOInexistenteResponde404(string archivo)
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using var respuesta = await Api.EnviarAsync(_get, $"/pdfs/{archivo}", ana.Access);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ElGerenteListaTodasLasLineasConElContrato()
    {
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var respuesta = await Api.EnviarAsync(_get, "/web/admin/lineas-negocio", gloria.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/admin/lineas-negocio", _get, HttpStatusCode.OK);

        json.EnumerateArray().Select(l => l.GetProperty("linea_negocio").GetString()).Should().Contain(
            ["Nacional", "Distribucion", "Importacion", "Exportacion", "Internacional", "Almacenamiento", "Almacenamiento3PL", "ITR"]);
    }

    [Fact]
    public async Task ElGerenteEditaUnaLineaYElCambioQuedaAuditadoConAntesYDespues()
    {
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);
        var (id, nombre) = await CrearLineaDePruebaAsync();

        using var respuesta = await Api.EnviarAsync(_put, $"/web/admin/lineas-negocio/{id}", gloria.Access,
            """{"margen": 0.2, "metodo_calculo": "markup", "is_active": false}""");
        var json = await VerificarContratoAsync(respuesta, "/web/admin/lineas-negocio/{id}", _put, HttpStatusCode.OK);
        var auditoria = await clsBaseDatosPruebas.EscalarAsync<string>(entorno.BaseDatos.CadenaCotifly,
            $"SELECT TOP 1 CONCAT(CAST(exito AS nvarchar(1)), '|', input, '|', outputResumen) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = 'admin_actualizar_linea_negocio' AND input LIKE '%\"linea_negocio_id\":{id},%' ORDER BY createdAt DESC");

        json.GetProperty("linea_negocio").GetString().Should().Be(nombre);
        json.GetProperty("margen").GetDecimal().Should().Be(0.2m);
        json.GetProperty("metodo_calculo").GetString().Should().Be("markup");
        json.GetProperty("is_active").GetBoolean().Should().BeFalse();
        json.GetProperty("updated_at").ValueKind.Should().Be(JsonValueKind.String);
        auditoria.Should().StartWith("1|").And.Contain(gloria.UsuarioId.ToString()).And.Contain(gloria.NombreUsuario);
        auditoria.Should().Contain("\"antes\":{").And.Contain("\"despues\":{").And.Contain("\"metodo_calculo\":\"markup\"");
    }

    [Fact]
    public async Task UnComercialRecibe403EnAdministracionYLaLineaNoCambia()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var (id, _) = await CrearLineaDePruebaAsync();

        using var listar = await Api.EnviarAsync(_get, "/web/admin/lineas-negocio", ana.Access);
        using var editar = await Api.EnviarAsync(_put, $"/web/admin/lineas-negocio/{id}", ana.Access,
            """{"margen": 0.3, "metodo_calculo": "margen", "is_active": true}""");

        await VerificarContratoAsync(listar, "/web/admin/lineas-negocio", _get, HttpStatusCode.Forbidden);
        (await listar.Content.ReadAsStringAsync(Ct)).Should().Be(SinPermiso);
        await VerificarContratoAsync(editar, "/web/admin/lineas-negocio/{id}", _put, HttpStatusCode.Forbidden);
        (await MargenDeAsync(id)).Should().Be(0.15m);
    }

    [Theory]
    [InlineData("""{"margen": 1.5, "metodo_calculo": "margen", "is_active": true}""")]
    [InlineData("""{"margen": 0.995, "metodo_calculo": "markup", "is_active": true}""")]
    [InlineData("""{"margen": -0.1, "metodo_calculo": "margen", "is_active": true}""")]
    [InlineData("""{"margen": 0.2, "metodo_calculo": "otro", "is_active": true}""")]
    [InlineData("""{"margen": 0.2, "is_active": true}""")]
    [InlineData("""{"margen": "0.2", "metodo_calculo": "margen", "is_active": true}""")]
    [InlineData("""{"margen": 0.2, "metodo_calculo": "margen", "is_active": "si"}""")]
    [InlineData("{no es json")]
    public async Task UnCambioInvalidoResponde400YLaLineaNoCambia(string cuerpo)
    {
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);
        var (id, _) = await CrearLineaDePruebaAsync();

        using var respuesta = await Api.EnviarAsync(_put, $"/web/admin/lineas-negocio/{id}", gloria.Access, cuerpo);

        await VerificarContratoAsync(respuesta, "/web/admin/lineas-negocio/{id}", _put, HttpStatusCode.BadRequest);
        (await MargenDeAsync(id)).Should().Be(0.15m);
    }

    [Fact]
    public async Task EditarUnaLineaInexistenteResponde404()
    {
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);

        using var respuesta = await Api.EnviarAsync(_put, "/web/admin/lineas-negocio/999999", gloria.Access,
            """{"margen": 0.2, "metodo_calculo": "margen", "is_active": true}""");

        await VerificarContratoAsync(respuesta, "/web/admin/lineas-negocio/{id}", _put, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LasMetricasDeActividadRespetanElAlcancePorRolYCuentanLasReenviadas()
    {
        var occidente = Regional();
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Ana Métricas");
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial, occidente, "Luis Métricas");
        var gloria = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, occidente);
        var hugo = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, Regional());
        var sinRegional = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente);
        var enviada = await Api.CrearCotizacionAsync(ana.UsuarioId);
        var reenviada = await Api.CrearCotizacionAsync(ana.UsuarioId);
        await Api.CrearCotizacionAsync(ana.UsuarioId);
        await Api.CrearCotizacionAsync(luis.UsuarioId);
        await Api.CrearCotizacionAsync(sinRegional.UsuarioId);
        await clsBaseDatosPruebas.EjecutarAsync(entorno.BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.Cotizaciones SET estadoEnvio = 'enviada' WHERE id = '{enviada.Id}';
            UPDATE dbo_cotifly.Cotizaciones SET estadoEnvio = 'reenviada' WHERE id = '{reenviada.Id}';
            """);

        var deAna = await ActividadAsync(ana.Access);
        var deGloria = await ActividadAsync(gloria.Access);
        var deHugo = await ActividadAsync(hugo.Access);
        var deSinRegional = await ActividadAsync(sinRegional.Access);

        deAna.Should().ContainSingle();
        deAna[0].GetProperty("usuario_id").GetString().Should().Be(ana.UsuarioId.ToString());
        deAna[0].GetProperty("nombre_completo").GetString().Should().Be("Ana Métricas");
        deAna[0].GetProperty("num_cotizaciones").GetInt32().Should().Be(3);
        deAna[0].GetProperty("num_cotizaciones_enviadas").GetInt32().Should().Be(2, "enviada y reenviada cuentan (script 0003)");
        deGloria.Select(f => f.GetProperty("usuario_id").GetString()).Should().BeEquivalentTo([ana.UsuarioId.ToString(), luis.UsuarioId.ToString()]);
        deHugo.Should().BeEmpty("otra regional sin cotizaciones");
        deSinRegional.Select(f => f.GetProperty("usuario_id").GetString()).Should().Equal(sinRegional.UsuarioId.ToString());
    }

    [Fact]
    public async Task LasMetricasDeAjustesCumplenElContratoYSoloMuestranLasDelComercial()
    {
        var ana = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var luis = await Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var deAna = await Api.CrearCotizacionAsync(ana.UsuarioId);
        var deLuis = await Api.CrearCotizacionAsync(luis.UsuarioId);
        await clsBaseDatosPruebas.EjecutarAsync(entorno.BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), '{deAna.Id}', '{deAna.Folio}', 0.15, 0.12, -0.03, N'Cliente estratégico', '{ana.UsuarioId}', SYSDATETIMEOFFSET()),
                   (NEWID(), '{deLuis.Id}', '{deLuis.Folio}', 0.15, 0.10, -0.05, N'Competencia', '{luis.UsuarioId}', SYSDATETIMEOFFSET());
            """);

        using var respuesta = await Api.EnviarAsync(_get, "/web/metricas/ajustes-margen", ana.Access);
        var json = await VerificarContratoAsync(respuesta, "/web/metricas/ajustes-margen", _get, HttpStatusCode.OK);

        var filas = json.EnumerateArray().ToList();
        filas.Should().ContainSingle();
        filas[0].GetProperty("usuario_id").GetString().Should().Be(ana.UsuarioId.ToString());
        filas[0].GetProperty("linea_negocio").GetString().Should().Be("Nacional");
        filas[0].GetProperty("num_ajustes").GetInt32().Should().Be(1);
        filas[0].GetProperty("diferencia_promedio").GetDecimal().Should().Be(-0.03m);
    }

    private async Task<List<JsonElement>> ActividadAsync(string token)
    {
        using var respuesta = await Api.EnviarAsync(_get, "/web/metricas/actividad", token);
        var json = await VerificarContratoAsync(respuesta, "/web/metricas/actividad", _get, HttpStatusCode.OK);
        return [.. json.EnumerateArray()];
    }

    /// <summary>Línea propia de la prueba (no se tocan las 8 sembradas, que usan otras pruebas).</summary>
    private async Task<(int Id, string Nombre)> CrearLineaDePruebaAsync()
    {
        var nombre = $"Prueba{Guid.NewGuid():N}"[..20];
        var id = await clsBaseDatosPruebas.EscalarAsync<int>(entorno.BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.MargenesPolitica (lineaNegocio, margen, metodoCalculo, isActive) VALUES (N'{nombre}', 0.15, 'margen', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);
        return (id, nombre);
    }

    private async Task<decimal> MargenDeAsync(int id) =>
        await clsBaseDatosPruebas.EscalarAsync<decimal>(entorno.BaseDatos.CadenaCotifly, $"SELECT margen FROM dbo_cotifly.MargenesPolitica WHERE id = {id}");
}
