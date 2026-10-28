using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;

namespace Cotifly.Api.Tests.Identidad;

/// <summary>
/// <c>/api/auth/*</c> de punta a punta (HTTP en memoria + SQL Server 2019 y la <c>dbsolucionesti</c> falsa de
/// Testcontainers): contrato de las respuestas, verificación doble, JwtBearer con políticas por rol, rotación del refresh
/// (incluida la concurrencia), logout con revocación del jti y límite de peticiones.
/// </summary>
[Trait("Categoria", "Integracion")]
public sealed class clsAuthEndpointTests(clsBaseDatosPruebas baseDatos) : IClassFixture<clsBaseDatosPruebas>
{
    private const string Clave = "clave-jwt-de-pruebas-api-de-al-menos-32-bytes";
    private static readonly string _rutaContrato = Path.Combine(AppContext.BaseDirectory, "specs", "openapi.yaml");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElLoginCumpleElContratoYElAccessAbreUnEndpointProtegido()
    {
        var nombre = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = nombre, roles = new[] { "31" } }, Ct);
        using var json = await JsonAsync(respuesta);

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        var esquemas = await EsquemasAsync();
        Verificar(json.RootElement, esquemas["LoginResponse"]);
        var usuario = json.RootElement.GetProperty("user");
        Verificar(usuario, esquemas["UsuarioSesion"]);
        Guid.TryParse(usuario.GetProperty("id").GetString(), out _).Should().BeTrue();
        usuario.GetProperty("username").GetString().Should().Be(nombre);
        usuario.GetProperty("nombre_completo").GetString().Should().Be("Persona de prueba");
        usuario.GetProperty("rol").GetString().Should().Be("comercial");
        usuario.GetProperty("ubicacion").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("debeCambiarPassword").GetBoolean().Should().BeFalse();

        using var protegido = await GetConTokenAsync(cliente, "/pruebas/comercial", json.RootElement.GetProperty("access").GetString());
        using var contexto = await JsonAsync(protegido);
        protegido.StatusCode.Should().Be(HttpStatusCode.OK);
        contexto.RootElement.GetProperty("username").GetString().Should().Be(nombre);
        contexto.RootElement.GetProperty("rol").GetString().Should().Be("comercial");
        contexto.RootElement.GetProperty("usuarioId").GetString().Should().Be(usuario.GetProperty("id").GetString());
    }

    [Fact]
    public async Task SinTokenOConUnTokenDeOtraClaveElEndpointProtegidoResponde401ConErrorWeb()
    {
        using var factory = Crear();
        using var cliente = factory.CreateClient();
        var ajeno = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "cotifly-api",
            Audience = "clientepro",
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["jti"] = "x", ["rol"] = "gerente_comercial" },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes("otra-clave-de-32-bytes-o-mas-0123456789")), SecurityAlgorithms.HmacSha256),
        });

        using var sinToken = await cliente.GetAsync(new Uri("/pruebas/comercial", UriKind.Relative), Ct);
        using var conAjeno = await GetConTokenAsync(cliente, "/pruebas/comercial", ajeno);

        foreach (var respuesta in new[] { sinToken, conAjeno })
        {
            respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Inicia sesión de nuevo."}""");
        }
    }

    [Fact]
    public async Task UnComercialNoEntraAUnEndpointDeGerenteYUnGerenteSi()
    {
        var comercial = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        var gerente = await PersonaAsync(clsBaseDatosPruebas.RolExternoGerente);
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        var accesoComercial = (await LoginAsync(cliente, comercial)).Access;
        var accesoGerente = (await LoginAsync(cliente, gerente)).Access;
        using var prohibido = await GetConTokenAsync(cliente, "/pruebas/gerente", accesoComercial);
        using var permitido = await GetConTokenAsync(cliente, "/pruebas/gerente", accesoGerente);
        using var gerenteComoComercial = await GetConTokenAsync(cliente, "/pruebas/comercial", accesoGerente);

        prohibido.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await prohibido.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"No tienes permiso para esta operación."}""");
        permitido.StatusCode.Should().Be(HttpStatusCode.OK);
        gerenteComoComercial.StatusCode.Should().Be(HttpStatusCode.OK, "el gerente también usa la consola del comercial");
    }

    [Fact]
    public async Task UnComercialQueTambienEsDirectorEntraComoDirectorYUsaLoDelGerenteYLaConsola()
    {
        var persona = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial, clsBaseDatosPruebas.RolExternoDirector);
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        var login = await LoginAsync(cliente, persona);
        using var gerencial = await GetConTokenAsync(cliente, "/pruebas/gerente", login.Access);
        using var consola = await GetConTokenAsync(cliente, "/pruebas/comercial", login.Access);

        gerencial.StatusCode.Should().Be(HttpStatusCode.OK, "el director usa todo lo del gerente (HU-037, #210)");
        consola.StatusCode.Should().Be(HttpStatusCode.OK, "el director también usa la consola del comercial");
        (await consola.Content.ReadAsStringAsync(Ct)).Should().Contain("\"rol\":\"director\"");
    }

    [Fact]
    public async Task UnDirectorNuevoSeDaDeAltaConSuRolYVuelveAEntrarComoDirector()
    {
        // Validación HU-037 (JIT): el alta crea la asignación `director` en UsuariosRoles y el segundo login la encuentra igual.
        var persona = await PersonaAsync(clsBaseDatosPruebas.RolExternoDirector);
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        var primero = await LoginAsync(cliente, persona);
        var segundo = await LoginAsync(cliente, persona);
        using var gerencial = await GetConTokenAsync(cliente, "/pruebas/gerente", segundo.Access);
        var asignado = await clsBaseDatosPruebas.EscalarAsync<string>(baseDatos.CadenaCotifly, $"""
            SELECT STRING_AGG(r.codigo, ',') FROM dbo_cotifly.UsuariosRoles ur
            JOIN dbo_cotifly.Roles r ON r.id = ur.rolId JOIN dbo_cotifly.UsuariosWeb u ON u.id = ur.usuarioId
            WHERE u.nombreUsuario = N'{persona}' AND ur.activo = 1
            """);

        primero.Access.Should().NotBeNullOrEmpty();
        gerencial.StatusCode.Should().Be(HttpStatusCode.OK);
        asignado.Should().Be("director");
    }

    [Fact]
    public async Task UnGerenteQuePasaADirectorEnClienteProQuedaEnRevisionHastaQueSeLeReasigneElRol()
    {
        // Validación HU-037: ascender (o degradar) en ClientePro no cambia solo el rol de Cotifly. El login y el refresh se rechazan
        // (rol_externo_cambio) hasta que el DBA reasigna UsuariosRoles. El access ya emitido conserva su rol hasta vencer (60 min,
        // JWT sin estado, ADR-009): es el riesgo residual documentado.
        var nombre = $"api{Guid.NewGuid():N}"[..20];
        var idExterno = await baseDatos.CrearPersonaExternaAsync(nombre, "Persona de prueba", $"{nombre}@boterosoto.com.co", clsBaseDatosPruebas.RolExternoGerente);
        using var factory = Crear();
        using var cliente = factory.CreateClient();
        var comoGerente = await LoginAsync(cliente, nombre);

        await baseDatos.CambiarRolesExternosAsync(idExterno, clsBaseDatosPruebas.RolExternoDirector);
        using var relogin = await cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = nombre }, Ct);
        using var refresh = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = comoGerente.Refresh }, Ct);
        using var accesoViejo = await GetConTokenAsync(cliente, "/pruebas/comercial", comoGerente.Access);

        relogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await accesoViejo.Content.ReadAsStringAsync(Ct)).Should().Contain("\"rol\":\"gerente_comercial\"", "el access viejo no gana el rol nuevo");

        // La reasignación del DBA (desactivar la asignación anterior y crear la de director) le devuelve el acceso, ya como director.
        await clsBaseDatosPruebas.EjecutarAsync(baseDatos.CadenaCotifly, $"""
            DECLARE @u UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.UsuariosWeb WHERE nombreUsuario = N'{nombre}');
            UPDATE dbo_cotifly.UsuariosRoles SET activo = 0 WHERE usuarioId = @u AND activo = 1;
            INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId) SELECT @u, id FROM dbo_cotifly.Roles WHERE codigo = N'director';
            """);
        var comoDirector = await LoginAsync(cliente, nombre);
        using var consola = await GetConTokenAsync(cliente, "/pruebas/comercial", comoDirector.Access);
        (await consola.Content.ReadAsStringAsync(Ct)).Should().Contain("\"rol\":\"director\"");
    }

    [Fact]
    public async Task HU0401_ConElScript16LoginYRefreshSiguenIgualConUnaYConDosAsignacionesActivas()
    {
        // HU-040.1 (#229): la base de pruebas tiene el 16 (sin uq_usuariosRoles_usuarioId_activo). Una persona con un rol y otra a la que
        // Datos le agregó una segunda asignación activa (comercial + gerente, los dos respaldados en ClientePro) entran y renuevan como
        // hoy: la segunda con la jerarquía (gerente_comercial). Cada refresh guarda el rol de la sesión (TokensRefresco.rolSesion).
        var uno = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        var dos = await PersonaAsync(clsBaseDatosPruebas.RolExternoGerente);
        using var factory = Crear();
        using var cliente = factory.CreateClient();
        await LoginAsync(cliente, dos);
        await clsBaseDatosPruebas.EjecutarAsync(baseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId)
                SELECT u.id, r.id FROM dbo_cotifly.UsuariosWeb u CROSS JOIN dbo_cotifly.Roles r
                WHERE u.nombreUsuario = N'{dos}' AND r.codigo = N'comercial';
            """);
        var idExternoDos = await clsBaseDatosPruebas.EscalarAsync<long>(baseDatos.CadenaCotifly,
            $"SELECT idUsuarioExterno FROM dbo_cotifly.UsuariosWeb WHERE nombreUsuario = N'{dos}'");
        await baseDatos.CambiarRolesExternosAsync(idExternoDos, clsBaseDatosPruebas.RolExternoComercial, clsBaseDatosPruebas.RolExternoGerente);

        foreach (var (persona, rol) in new[] { (uno, "comercial"), (dos, "gerente_comercial") })
        {
            var login = await LoginAsync(cliente, persona);
            using var renovacion = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = login.Refresh }, Ct);
            using var par = await JsonAsync(renovacion);
            using var consola = await GetConTokenAsync(cliente, "/pruebas/comercial", par.RootElement.GetProperty("access").GetString());

            renovacion.StatusCode.Should().Be(HttpStatusCode.OK, $"{persona} renueva con el 16 aplicado");
            par.RootElement.GetProperty("user").GetProperty("rol").GetString().Should().Be(rol);
            (await consola.Content.ReadAsStringAsync(Ct)).Should().Contain($"\"rol\":\"{rol}\"");
            (await clsBaseDatosPruebas.EscalarAsync<string>(baseDatos.CadenaCotifly, $"""
                SELECT STRING_AGG(r.rol, ',') FROM (SELECT DISTINCT COALESCE(t.rolSesion, 'NULL') AS rol FROM dbo_cotifly.TokensRefresco t
                JOIN dbo_cotifly.UsuariosWeb u ON u.id = t.usuarioId WHERE u.nombreUsuario = N'{persona}' AND t.revocadoAt IS NULL) r
                """)).Should().Be(rol, "los refresh vigentes guardan el rol de la sesión");
        }

        (await clsBaseDatosPruebas.EscalarAsync<int>(baseDatos.CadenaCotifly, $"""
            SELECT COUNT(*) FROM dbo_cotifly.UsuariosRoles ur JOIN dbo_cotifly.UsuariosWeb u ON u.id = ur.usuarioId
            WHERE u.nombreUsuario = N'{dos}' AND ur.activo = 1
            """)).Should().Be(2, "el login no toca las asignaciones");
    }

    [Fact]
    public async Task UnaPersonaDesconocidaYUnaSinRolAutorizadoRecibenElMismo401Generico()
    {
        var sinRol = await PersonaAsync(clsBaseDatosPruebas.RolExternoSinMapeo);
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        foreach (var idUsuario in new[] { "NADIE-EXISTE", sinRol })
        {
            using var respuesta = await cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario }, Ct);

            respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Credenciales inválidas"}""");
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"idUsuario":""}""")]
    [InlineData("{no es json")]
    [InlineData("")]
    public async Task UnCuerpoDeLoginInvalidoResponde400ConErrorWeb(string cuerpo)
    {
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.PostAsync(new Uri("/api/auth/login-clientepro", UriKind.Relative),
            new StringContent(cuerpo, Encoding.UTF8, "application/json"), Ct);
        using var json = await JsonAsync(respuesta);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("error");
        json.RootElement.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task ConElAlmacenCorporativoCaidoElLoginFallaCerradoCon401()
    {
        var nombre = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var factory = Crear(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Identidad"] = "Server=tcp:127.0.0.1,1;Database=dbsolucionesti;User Id=x;Password=NoImporta.123;Encrypt=False;Connect Timeout=2",
        });
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = nombre }, Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Credenciales inválidas"}""");
    }

    [Fact]
    public async Task ElRefreshRotaYReutilizarElAnteriorResponde401YRevocaTambienElNuevo()
    {
        var nombre = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var factory = Crear();
        using var cliente = factory.CreateClient();
        var login = await LoginAsync(cliente, nombre);

        using var rotacion = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = login.Refresh }, Ct);
        using var par = await JsonAsync(rotacion);
        var nuevoRefresh = par.RootElement.GetProperty("refresh").GetString();
        await EnvejecerLasRotacionesAsync();
        using var reuso = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = login.Refresh }, Ct);
        using var conElNuevo = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = nuevoRefresh }, Ct);

        rotacion.StatusCode.Should().Be(HttpStatusCode.OK);
        Verificar(par.RootElement, (await EsquemasAsync())["LoginResponse"]);
        nuevoRefresh.Should().NotBe(login.Refresh);
        reuso.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await reuso.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Inicia sesión de nuevo."}""");
        conElNuevo.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "el reuso revoca todos los refresh vigentes del usuario");
    }

    [Fact]
    public async Task DeDosRefreshSimultaneosConElMismoTokenSoloUnoGana()
    {
        var nombre = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        for (var intento = 0; intento < 3; intento++)
        {
            var login = await LoginAsync(cliente, nombre);

            var respuestas = await Task.WhenAll(
                cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = login.Refresh }, Ct),
                cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = login.Refresh }, Ct));

            respuestas.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
            respuestas.Count(r => r.StatusCode == HttpStatusCode.Unauthorized).Should().Be(1);
            foreach (var respuesta in respuestas)
            {
                respuesta.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{no es json")]
    [InlineData("""{"refresh":"no-existe"}""")]
    public async Task UnRefreshAusenteMalFormadoODesconocidoResponde401(string cuerpo)
    {
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.PostAsync(new Uri("/api/auth/refresh", UriKind.Relative),
            new StringContent(cuerpo, Encoding.UTF8, "application/json"), Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await respuesta.Content.ReadAsStringAsync(Ct)).Should().Be("""{"error":"Inicia sesión de nuevo."}""");
    }

    [Fact]
    public async Task ElLogoutRevocaElAccessYElRefresh()
    {
        var nombre = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var factory = Crear();
        using var cliente = factory.CreateClient();
        var login = await LoginAsync(cliente, nombre);

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Content = JsonContent.Create(new { refresh = login.Refresh }) };
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Access);
        using var logout = await cliente.SendAsync(peticion, Ct);
        using var protegido = await GetConTokenAsync(cliente, "/pruebas/comercial", login.Access);
        using var refresh = await cliente.PostAsJsonAsync("/api/auth/refresh", new { refresh = login.Refresh }, Ct);
        var jtiEnBase = await clsBaseDatosPruebas.EscalarAsync<int>(baseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.TokensRevocados WHERE jti = '{new JsonWebToken(login.Access).Id}'");

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        protegido.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "el jti queda revocado al instante");
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        jtiEnBase.Should().Be(1);
    }

    [Fact]
    public async Task UnJtiRevocadoEnOtraReplicaSeRechazaCuandoSeRecargaLaFoto()
    {
        var nombre = await PersonaAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var replicaA = Crear();
        using var replicaB = Crear();
        using var clienteA = replicaA.CreateClient();
        using var clienteB = replicaB.CreateClient();
        var login = await LoginAsync(clienteA, nombre);
        using var antes = await GetConTokenAsync(clienteB, "/pruebas/comercial", login.Access);

        using var peticion = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", login.Access);
        using var logout = await clienteA.SendAsync(peticion, Ct);
        using var replicaC = Crear();
        using var clienteC = replicaC.CreateClient();
        using var despues = await GetConTokenAsync(clienteC, "/pruebas/comercial", login.Access);

        antes.StatusCode.Should().Be(HttpStatusCode.OK);
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
        despues.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "una réplica que carga la foto de TokensRevocados ve el logout");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{no es json")]
    [InlineData("""{"refresh":"no-existe"}""")]
    public async Task ElLogoutSiempreResponde204(string cuerpo)
    {
        using var factory = Crear();
        using var cliente = factory.CreateClient();

        using var respuesta = await cliente.PostAsync(new Uri("/api/auth/logout", UriKind.Relative),
            new StringContent(cuerpo, Encoding.UTF8, "application/json"), Ct);

        respuesta.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ElLimiteDePeticionesDeAuthResponde429ConErrorWeb()
    {
        using var factory = Crear(new Dictionary<string, string?> { ["LimiteAuth:PeticionesPorVentana"] = "2", ["LimiteAuth:VentanaSegundos"] = "60" });
        using var cliente = factory.CreateClient();

        var estados = new List<HttpStatusCode>();
        string? cuerpo = null;
        for (var i = 0; i < 3; i++)
        {
            using var respuesta = await cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = "NADIE" }, Ct);
            estados.Add(respuesta.StatusCode);
            cuerpo = await respuesta.Content.ReadAsStringAsync(Ct);
        }

        estados.Should().Equal(HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests);
        cuerpo.Should().Be("""{"error":"Demasiadas peticiones. Intenta de nuevo en unos segundos."}""");
    }

    private WebApplicationFactory<Program> Crear(IDictionary<string, string?>? extra = null)
    {
        var valores = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cotifly"] = baseDatos.CadenaCotifly,
            ["ConnectionStrings:Identidad"] = baseDatos.CadenaIdentidad,
            ["Jwt:SigningKey"] = Clave,
            ["LimiteAuth:PeticionesPorVentana"] = "100000",
            ["Pdf:StorageDir"] = clsServiciosPruebas.CarpetaPdfsTemporal(),
        };
        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
        {
            valores[clave] = valor;
        }

        return new clsCotiflyApiFactory().WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, configuracion) => configuracion.AddInMemoryCollection(valores));
            b.ConfigureServices(s => s.AddControllers().AddApplicationPart(typeof(clsProtegidoPruebaController).Assembly));
        });
    }

    /// <summary>Persona nueva en el almacén con sus roles externos; devuelve su NOMBREUSUARIO.</summary>
    private async Task<string> PersonaAsync(params int[] rolesExternos)
    {
        var nombre = $"api{Guid.NewGuid():N}"[..20];
        await baseDatos.CrearPersonaExternaAsync(nombre, "Persona de prueba", $"{nombre}@boterosoto.com.co", rolesExternos);
        return nombre;
    }

    private static async Task<(string Access, string Refresh)> LoginAsync(HttpClient cliente, string idUsuario)
    {
        using var respuesta = await cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario }, Ct);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = await JsonAsync(respuesta);
        return (json.RootElement.GetProperty("access").GetString()!, json.RootElement.GetProperty("refresh").GetString()!);
    }

    /// <summary>
    /// El reuso inmediato de un refresh recién rotado se trata como renovación concurrente (gracia de 10 s, sin revocar).
    /// Para probar el reuso "real" se envejece la rotación en la base.
    /// </summary>
    private Task EnvejecerLasRotacionesAsync() =>
        clsBaseDatosPruebas.EjecutarAsync(baseDatos.CadenaCotifly,
            "UPDATE dbo_cotifly.TokensRefresco SET revocadoAt = DATEADD(MINUTE, -1, revocadoAt) WHERE revocadoAt IS NOT NULL AND reemplazadoPorId IS NOT NULL;");

    private static async Task<HttpResponseMessage> GetConTokenAsync(HttpClient cliente, string ruta, string? token)
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
        peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await cliente.SendAsync(peticion, Ct);
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage respuesta) =>
        JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync(Ct));

    /// <summary>Campos obligatorios presentes y ningún campo fuera del esquema.</summary>
    private static void Verificar(JsonElement raiz, IOpenApiSchema esquema)
    {
        var campos = raiz.EnumerateObject().Select(p => p.Name).ToList();
        campos.Should().Contain(esquema.Required!, "la respuesta debe traer todos los campos obligatorios del contrato");
        campos.Should().OnlyContain(c => esquema.Properties!.ContainsKey(c), "la respuesta no debe traer campos fuera del contrato");
    }

    private static async Task<IDictionary<string, IOpenApiSchema>> EsquemasAsync()
    {
        var opciones = new OpenApiReaderSettings();
        opciones.AddYamlReader();
        await using var flujo = File.OpenRead(_rutaContrato);
        var resultado = await OpenApiDocument.LoadAsync(flujo, "yaml", opciones, Ct);
        return resultado.Document!.Components!.Schemas!;
    }
}
