using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Idempotencia de crear la licitación contra SQL Server real, buscando romperla: el folio y la <c>Idempotency-Key</c> se confirman (o se
/// deshacen) juntos, dos réplicas con la misma clave a la vez, la API que se cae mientras el alta va a Nexo (con la toma vigente y
/// vencida), la recuperación con reintentar, el 503 que no consume la clave, la licitación con cubicador y la misma clave en la captura.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsLicitacionIdempotenciaAdversarialTests : IDisposable
{
    private const string Ruta = "/web/licitaciones";
    private const string AltaNexo = "/api/captura-solicitudes";
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsNexoFalso _nexoReplica = new clsNexoFalso().ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.");
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly List<IDisposable> _desechables = [];
    private readonly clsApiWebPruebas _api;

    public clsLicitacionIdempotenciaAdversarialTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = Registrar(clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string JsonPliego => clsPliegoMuestra.Json(clsCosteoEndpointsTests.Nit, clsPliegoMuestra.Lineas());

    // Atomicidad: el registro de la clave escribe su fila DENTRO de la transacción del folio y luego falla. Si la escritura fuera por otra
    // conexión o en otra transacción, el folio quedaría en CapturasIdempotentes apuntando a una licitación que no existe.
    [Fact]
    public async Task SiRegistrarLaClaveFallaNoQuedaNiLaLicitacionNiElFolioEnLaClave()
    {
        var fallo = new clsInterruptor(activo: true);
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, servicios: s =>
            s.AddScoped<IClsRegistroCapturasIdempotentes>(sp => new clsRegistroQueFallaTrasEscribir(
                ActivatorUtilities.CreateInstance<clsRegistroCapturasIdempotentesSql>(sp), fallo)));
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();

        using (var primera = await EnviarConClaveAsync(api.Cliente, sesion, JsonPliego, clsPliegoMuestra.Excel(), clave))
        {
            ((int)primera.StatusCode).Should().BeGreaterThanOrEqualTo(500, await primera.Content.ReadAsStringAsync(Ct));
        }

        fallo.Escrituras.Should().Be(1, "el registro sí escribió la fila antes de fallar");
        (await LicitacionesAsync(sesion)).Should().Be("0", "la licitación se deshizo con la clave");
        (await EscalarAsync($"SELECT CONCAT(COUNT(*), '|', MAX(folio)) FROM dbo_cotifly.CapturasIdempotentes WHERE {Fila(sesion, clave)}"))
            .Should().Be("0|", "la toma se soltó y el folio deshecho no quedó con la clave");
        _nexo.Llamadas(HttpMethod.Post, AltaNexo).Should().Be(0, "nada salió a Nexo");

        fallo.Activo = false;
        var reintento = await CrearConClaveAsync(api.Cliente, sesion, JsonPliego, clsPliegoMuestra.Excel(), clave, HttpStatusCode.Created);

        var folio = reintento.GetProperty("folio").GetString();
        (await LicitacionesAsync(sesion)).Should().Be("1");
        (await EscalarAsync($"SELECT CONCAT(estado, '|', folio) FROM dbo_cotifly.CapturasIdempotentes WHERE {Fila(sesion, clave)}"))
            .Should().Be($"terminada|{folio}");
    }

    // Dos réplicas con la misma clave y el mismo cuerpo a la vez (dos por réplica): una sola licitación y un solo alta en Nexo. Cada
    // respuesta es el 201 con ese folio o el 409 CAPTURA_EN_CURSO de la réplica que encontró la toma ajena.
    [Fact]
    public async Task DosReplicasConLaMismaClaveYElMismoCuerpoCreanUnaSolaLicitacion()
    {
        var replica = Registrar(clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexoReplica));
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();
        var excel = clsPliegoMuestra.Excel();

        var respuestas = await Task.WhenAll(
            LeerAsync(EnviarConClaveAsync(_api.Cliente, sesion, JsonPliego, excel, clave)),
            LeerAsync(EnviarConClaveAsync(replica.Cliente, sesion, JsonPliego, excel, clave)),
            LeerAsync(EnviarConClaveAsync(_api.Cliente, sesion, JsonPliego, excel, clave)),
            LeerAsync(EnviarConClaveAsync(replica.Cliente, sesion, JsonPliego, excel, clave)));

        var folios = respuestas.Where(r => r.Estado == HttpStatusCode.Created).Select(r => r.Json.GetProperty("folio").GetString()).ToList();
        folios.Should().NotBeEmpty().And.OnlyContain(f => f == folios[0], "todas las 201 son el mismo folio");
        respuestas.Where(r => r.Estado != HttpStatusCode.Created).Should().OnlyContain(
            r => r.Estado == HttpStatusCode.Conflict && Motivo(r.Json) == "CAPTURA_EN_CURSO",
            string.Join(" / ", respuestas.Select(r => $"{(int)r.Estado} {r.Json}")));
        (await LicitacionesAsync(sesion)).Should().Be("1", "una sola licitación");
        (_nexo.Llamadas(HttpMethod.Post, AltaNexo) + _nexoReplica.Llamadas(HttpMethod.Post, AltaNexo)).Should().Be(1, "un solo alta en Nexo");
    }

    [Fact]
    public async Task DosReplicasConLaMismaClaveYOtroArchivoALaVezCreanUnaSolaYLaOtraEsReutilizada()
    {
        var replica = Registrar(clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexoReplica));
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();

        var respuestas = await Task.WhenAll(
            LeerAsync(EnviarConClaveAsync(_api.Cliente, sesion, JsonPliego, clsPliegoMuestra.Excel(), clave)),
            LeerAsync(EnviarConClaveAsync(replica.Cliente, sesion, JsonPliego, clsPliegoMuestra.Excel(4), clave)));

        var detalle = string.Join(" / ", respuestas.Select(r => $"{(int)r.Estado} {r.Json}"));
        respuestas.Count(r => r.Estado == HttpStatusCode.Created).Should().Be(1, detalle);
        respuestas.Should().ContainSingle(r => r.Estado == HttpStatusCode.Conflict && Codigo(r.Json) == "IDEMPOTENCY_KEY_REUTILIZADA", detalle);
        (await LicitacionesAsync(sesion)).Should().Be("1");
    }

    // La API "se cae" con el alta en camino a Nexo (el folio y la clave ya están confirmados). Otra réplica repite la petición: mientras la
    // toma está vigente responde 409 CAPTURA_EN_CURSO con el folio; pasada la toma (10 min), 201 con el mismo folio en pendiente_envio y
    // sin volver a enviar. La licitación se recupera con reintentar.
    [Fact]
    public async Task UnaCaidaConElAltaEnCaminoSeRecuperaSinOtraLicitacionNiOtroAlta()
    {
        var replica = Registrar(clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexoReplica));
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();
        var excel = clsPliegoMuestra.Excel();
        var colgada = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enNexo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nexo.AntesDelAlta = async () =>
        {
            enNexo.TrySetResult();
            await colgada.Task;
        };
        var original = EnviarConClaveAsync(_api.Cliente, sesion, JsonPliego, excel, clave);
        await enNexo.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var folio = await EscalarAsync($"SELECT folio FROM dbo_cotifly.CapturasIdempotentes WHERE {Fila(sesion, clave)}");
        folio.Should().NotBeNull("el folio quedó con la clave antes de ir a Nexo");

        try
        {
            var enCurso = await LeerAsync(EnviarConClaveAsync(replica.Cliente, sesion, JsonPliego, excel, clave));
            enCurso.Estado.Should().Be(HttpStatusCode.Conflict, enCurso.Json.ToString());
            Codigo(enCurso.Json).Should().Be("ESTADO_NO_PERMITE");
            Motivo(enCurso.Json).Should().Be("CAPTURA_EN_CURSO");
            enCurso.Json.ToString().Should().Contain(folio!, "el 409 dice qué folio revisar");

            var otroCuerpo = await LeerAsync(EnviarConClaveAsync(replica.Cliente, sesion, JsonPliego, clsPliegoMuestra.Excel(4), clave));
            Codigo(otroCuerpo.Json).Should().Be("IDEMPOTENCY_KEY_REUTILIZADA");

            // Pasan los 10 min de la toma (la hora de la API no viaja por HTTP: se vence la toma y se envejece la licitación en la base).
            await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
                $"UPDATE dbo_cotifly.CapturasIdempotentes SET enCursoHasta = DATEADD(MINUTE, -1, SYSDATETIMEOFFSET()) WHERE {Fila(sesion, clave)}");
            var tardia = await CrearConClaveAsync(replica.Cliente, sesion, JsonPliego, excel, clave, HttpStatusCode.Created);
            tardia.GetProperty("folio").GetString().Should().Be(folio, "pasada la toma, el reintento relee la licitación que ya existe");
            tardia.GetProperty("estado_costeo").GetString().Should().Be("pendiente_envio", "el estado de ahora: el alta no terminó");

            await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
                $"UPDATE dbo_cotifly.CotizacionesSolicitud SET updatedAt = DATEADD(MINUTE, -11, updatedAt) WHERE folio = '{folio}'");
            using (var reintentar = await replica.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access))
            {
                reintentar.StatusCode.Should().Be(HttpStatusCode.OK, await reintentar.Content.ReadAsStringAsync(Ct));
            }

            (await LicitacionesAsync(sesion)).Should().Be("1", "una sola licitación");
            _nexoReplica.Llamadas(HttpMethod.Post, AltaNexo).Should().Be(1, "solo el reintentar del comercial la envía");
            (await EscalarAsync($"SELECT CONCAT(estadoCosteo, '|', IIF(solicitudNexoId IS NULL, 'sin', 'con')) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'"))
                .Should().Be("pendiente_costeo|con");
            (await EscalarAsync($"SELECT CONCAT(estado, '|', folio) FROM dbo_cotifly.CapturasIdempotentes WHERE {Fila(sesion, clave)}"))
                .Should().Be($"terminada|{folio}");

            // En la réplica "caída" la petición colgada aún tiene el semáforo de la clave: la repetición va a la otra réplica.
            var otraVez = await CrearConClaveAsync(replica.Cliente, sesion, JsonPliego, excel, clave, HttpStatusCode.Created);
            otraVez.GetProperty("folio").GetString().Should().Be(folio);
            _nexoReplica.Llamadas(HttpMethod.Post, AltaNexo).Should().Be(1);
        }
        finally
        {
            // La petición "caída" nunca debió seguir; se libera solo para cerrar la API de la prueba.
            _nexo.AntesDelAlta = null;
            colgada.TrySetException(new HttpRequestException("caída simulada"));
            try
            {
                using var _ = await original.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException)
            {
                // La petición colgada pudo vencer su tiempo de espera en el cliente: da igual, se simulaba una caída.
            }
        }
    }

    // Nexo rechaza el alta (503): la licitación se crea en error_envio (201) y consume la clave. La repetición devuelve el mismo folio sin
    // volver a enviar; el comercial la recupera con reintentar.
    [Fact]
    public async Task ConElAltaFallidaLaRepeticionDevuelveElMismoFolioEnErrorEnvioYReintentarLaRecupera()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();
        var excel = clsPliegoMuestra.Excel();
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;

        var primera = await CrearConClaveAsync(_api.Cliente, sesion, JsonPliego, excel, clave, HttpStatusCode.Created);
        var segunda = await CrearConClaveAsync(_api.Cliente, sesion, JsonPliego, excel, clave, HttpStatusCode.Created);

        var folio = primera.GetProperty("folio").GetString();
        primera.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        segunda.GetProperty("folio").GetString().Should().Be(folio);
        segunda.GetProperty("estado_costeo").GetString().Should().Be("error_envio");
        _nexo.Llamadas(HttpMethod.Post, AltaNexo).Should().Be(1, "la repetición no reenvía");

        _nexo.AltaFalla = null;
        using var reintentar = await _api.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);
        reintentar.StatusCode.Should().Be(HttpStatusCode.OK, await reintentar.Content.ReadAsStringAsync(Ct));
        _nexo.SolicitudesCreadas.Should().ContainSingle();
        (await LicitacionesAsync(sesion)).Should().Be("1");
    }

    [Fact]
    public async Task UnServicioNoDisponibleNoConsumeLaClave()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();
        var excel = clsPliegoMuestra.Excel();
        _nexo.MunicipiosFalla = HttpStatusCode.ServiceUnavailable;

        var caido = await LeerAsync(EnviarConClaveAsync(_api.Cliente, sesion, JsonPliego, excel, clave));
        caido.Estado.Should().Be(HttpStatusCode.ServiceUnavailable, caido.Json.ToString());
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.CapturasIdempotentes WHERE {Fila(sesion, clave)}"))
            .Should().Be("0", "el 503 no deja la clave");

        _nexo.MunicipiosFalla = null;
        var creada = await CrearConClaveAsync(_api.Cliente, sesion, JsonPliego, excel, clave, HttpStatusCode.Created);

        (await EscalarAsync($"SELECT folio FROM dbo_cotifly.CapturasIdempotentes WHERE {Fila(sesion, clave)}"))
            .Should().Be(creada.GetProperty("folio").GetString());
        (await LicitacionesAsync(sesion)).Should().Be("1");
    }

    // Con el cubicador la licitación se guarda y sale a Nexo en segundo plano: la clave también queda con el folio y la repetición tras una
    // caída (toma vencida) no crea otra.
    [Fact]
    public async Task ConCubicadorYSinArchivoLaClaveQuedaConElFolioYLaRepeticionTardiaNoCreaOtra()
    {
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("CAFE_EN_ESTIBAS", "Café en estibas"));
        var api = Registrar(clsApiWebPruebas.ConNexo(_entorno.BaseDatos, _nexo, new Dictionary<string, string?>
        {
            ["Nexo:EnviarCodigosCubicador"] = "true",
        }, cubicador: _cubicador));
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = clsCosteoEndpointsTests.Nit,
            cliente_nombre = "Longyin",
            linea_negocio = "Nacional",
            lineas = new[]
            {
                new
                {
                    fila = 0, origen = "BUENAVENTURA", destino = "BOGOTA, D.C.", tipologia = "CargaSeca", peso_kg = 10000,
                    mercancias = new[] { new { codigo_mercancia = "CAFE_EN_ESTIBAS", nombre_mercancia = "Café", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1000 } },
                },
            },
        });

        var primera = await CrearJsonConClaveAsync(api.Cliente, sesion, cuerpo, clave, HttpStatusCode.Created);
        var folio = primera.GetProperty("folio").GetString();
        (await EscalarAsync($"SELECT folio FROM dbo_cotifly.CapturasIdempotentes WHERE {Fila(sesion, clave)}")).Should().Be(folio);
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CapturasIdempotentes SET estado = N'en_curso', resultado = NULL, tomadaPor = NEWID(), enCursoHasta = '2000-01-01T00:00:00+00:00' WHERE {Fila(sesion, clave)}");

        var tardia = await CrearJsonConClaveAsync(api.Cliente, sesion, cuerpo, clave, HttpStatusCode.Created);

        tardia.GetProperty("folio").GetString().Should().Be(folio);
        (await LicitacionesAsync(sesion)).Should().Be("1");
    }

    // La misma Idempotency-Key en la captura del formulario y en la licitación: ámbitos distintos, dos filas, dos folios. El registro de la
    // licitación no lo ve el reintento del costeo (BuscarPorFolioAsync solo mira capturar_costeo).
    [Fact]
    public async Task LaMismaClaveEnLaCapturaYEnLaLicitacionSonFilasDistintas()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        var clave = Guid.NewGuid().ToString();

        string? folioCaptura;
        using (var peticion = new HttpRequestMessage(HttpMethod.Post, "/web/solicitudes-costeo"))
        {
            peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
            peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
            peticion.Content = new StringContent(clsCosteoEndpointsTests.Formulario(), System.Text.Encoding.UTF8, "application/json");
            using var captura = await _api.Cliente.SendAsync(peticion, Ct);
            folioCaptura = (await VerificarContratoAsync(captura, "/web/solicitudes-costeo", HttpMethod.Post, HttpStatusCode.Created))
                .GetProperty("folio").GetString();
        }

        var licitacion = await CrearConClaveAsync(_api.Cliente, sesion, JsonPliego, clsPliegoMuestra.Excel(), clave, HttpStatusCode.Created);

        var folioLicitacion = licitacion.GetProperty("folio").GetString();
        folioLicitacion.Should().NotBe(folioCaptura);
        (await EscalarAsync($"SELECT STRING_AGG(CONCAT(operacion, '=', folio), ',') WITHIN GROUP (ORDER BY operacion) FROM dbo_cotifly.CapturasIdempotentes WHERE usuarioId = '{sesion.UsuarioId}' AND clave = N'{clave}'"))
            .Should().Be($"capturar_costeo={folioCaptura},crear-licitacion={folioLicitacion}");
        await using var ambito = _api.Factory.Services.CreateAsyncScope();
        var registro = ambito.ServiceProvider.GetRequiredService<IClsRegistroCapturasIdempotentes>();
        (await registro.BuscarPorFolioAsync(folioLicitacion!, Ct)).Should().BeNull("el reintento del costeo no ve la fila de la licitación");
        (await registro.BuscarPorFolioAsync(folioCaptura!, Ct))!.Folio.Should().Be(folioCaptura);
        (await registro.BuscarAsync($"{clsLicitacionService.OperacionIdempotencia}:{sesion.UsuarioId}", clave, Ct))!.Folio.Should().Be(folioLicitacion);
        (await registro.BuscarAsync($"{clsEnvioCosteoService.OperacionIdempotencia}:{sesion.UsuarioId}", clave, Ct))!.Folio.Should().Be(folioCaptura);
    }

    [Fact]
    public async Task SinIdempotencyKeyCadaPeticionCreaSuLicitacionYNoDejaFila()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        using (var primera = await _api.EnviarContenidoAsync(HttpMethod.Post, Ruta, sesion.Access, clsApiWebPruebas.Licitacion(JsonPliego, clsPliegoMuestra.Excel())))
        {
            await VerificarContratoAsync(primera, Ruta, HttpMethod.Post, HttpStatusCode.Created);
        }

        using (var segunda = await _api.EnviarContenidoAsync(HttpMethod.Post, Ruta, sesion.Access, clsApiWebPruebas.Licitacion(JsonPliego, clsPliegoMuestra.Excel())))
        {
            await VerificarContratoAsync(segunda, Ruta, HttpMethod.Post, HttpStatusCode.Created);
        }

        (await LicitacionesAsync(sesion)).Should().Be("2");
        (await EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.CapturasIdempotentes WHERE usuarioId = '{sesion.UsuarioId}'")).Should().Be("0");
    }

    private static string Fila(clsSesionPrueba sesion, string clave) =>
        $"usuarioId = '{sesion.UsuarioId}' AND operacion = N'{clsLicitacionService.OperacionIdempotencia}' AND clave = N'{clave}'";

    private static string? Codigo(JsonElement json) =>
        json.TryGetProperty("error", out var error) && error.TryGetProperty("codigo", out var codigo) ? codigo.GetString() : null;

    private static string? Motivo(JsonElement json) =>
        json.TryGetProperty("error", out var error) && error.TryGetProperty("detalle", out var detalle) && detalle.ValueKind == JsonValueKind.Object
            && detalle.TryGetProperty("motivo", out var motivo) ? motivo.GetString() : null;

    private static async Task<(HttpStatusCode Estado, JsonElement Json)> LeerAsync(Task<HttpResponseMessage> envio)
    {
        using var respuesta = await envio;
        var texto = await respuesta.Content.ReadAsStringAsync(Ct);
        return (respuesta.StatusCode, string.IsNullOrWhiteSpace(texto) ? default : JsonDocument.Parse(texto).RootElement.Clone());
    }

    private static async Task<HttpResponseMessage> EnviarConClaveAsync(HttpClient cliente, clsSesionPrueba sesion, string json, byte[] excel, string clave)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, Ruta) { Content = clsApiWebPruebas.Licitacion(json, excel) };
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
        peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
        return await cliente.SendAsync(peticion, Ct);
    }

    private static async Task<JsonElement> CrearConClaveAsync(HttpClient cliente, clsSesionPrueba sesion, string json, byte[] excel, string clave, HttpStatusCode esperado)
    {
        using var respuesta = await EnviarConClaveAsync(cliente, sesion, json, excel, clave);
        return await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, esperado);
    }

    private static async Task<JsonElement> CrearJsonConClaveAsync(HttpClient cliente, clsSesionPrueba sesion, string json, string clave, HttpStatusCode esperado)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Post, Ruta) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sesion.Access);
        peticion.Headers.TryAddWithoutValidation("Idempotency-Key", clave);
        using var respuesta = await cliente.SendAsync(peticion, Ct);
        return await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, esperado);
    }

    private Task<string?> LicitacionesAsync(clsSesionPrueba sesion) =>
        EscalarAsync($"SELECT CAST(COUNT(*) AS nvarchar(10)) FROM dbo_cotifly.CotizacionesSolicitud WHERE usuarioId = '{sesion.UsuarioId}' AND tipoSolicitud = N'licitacion'");

    private Task<string?> EscalarAsync(string sql) => clsBaseDatosPruebas.EscalarAsync<string>(_entorno.BaseDatos.CadenaCotifly, sql);

    private clsApiWebPruebas Registrar(clsApiWebPruebas api)
    {
        _desechables.Add(api);
        return api;
    }

    public void Dispose()
    {
        foreach (var desechable in _desechables)
        {
            desechable.Dispose();
        }

        _nexo.Dispose();
        _nexoReplica.Dispose();
        _cubicador.Dispose();
    }

    private sealed class clsInterruptor(bool activo)
    {
        public bool Activo { get; set; } = activo;

        public int Escrituras { get; set; }
    }

    /// <summary>Registro real que, para la licitación, escribe la fila (en la transacción abierta) y luego falla.</summary>
    private sealed class clsRegistroQueFallaTrasEscribir(IClsRegistroCapturasIdempotentes real, clsInterruptor fallo) : IClsRegistroCapturasIdempotentes
    {
        public Task<clsCapturaIdempotente?> BuscarAsync(string ambito, string clave, CancellationToken cancellationToken = default) =>
            real.BuscarAsync(ambito, clave, cancellationToken);

        public Task<clsCapturaIdempotente?> BuscarPorFolioAsync(string folio, CancellationToken cancellationToken = default) =>
            real.BuscarPorFolioAsync(folio, cancellationToken);

        public async Task RegistrarAsync(string ambito, string clave, clsCapturaIdempotente captura, CancellationToken cancellationToken = default)
        {
            await real.RegistrarAsync(ambito, clave, captura, cancellationToken);
            if (fallo.Activo && ambito.StartsWith(clsLicitacionService.OperacionIdempotencia + ":", StringComparison.Ordinal))
            {
                fallo.Escrituras++;
                throw new InvalidOperationException("Fallo simulado después de registrar la clave.");
            }
        }
    }
}
