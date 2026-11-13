// Composition root de Cotifly (ADR-002).
// El compilador genera la clase "Program" a partir de estas instrucciones de nivel superior.
// Es la única clase exenta del prefijo cls (ADR-002): IDE1006 la reporta en la primera
// instrucción, por eso la supresión cubre solo esa línea. En .NET 10 "Program" ya es
// pública (generador de ASP.NET Core), así que WebApplicationFactory<Program> no necesita
// declarar "public partial class Program".
using System.Reflection;
using Cotifly.Api.Comun;
using Cotifly.Api.Identidad;
using Cotifly.Api.Sistema;
using Cotifly.Aplicacion;
using Cotifly.Infraestructura;
using Microsoft.OpenApi;

// HEALTHCHECK del Dockerfile: "dotnet Cotifly.Api.dll --healthcheck" solo consulta
// GET /health del proceso que ya corre en el contenedor y termina (0 sano, 1 no sano).
// No construye el host web ni valida la configuración.
#pragma warning disable IDE1006
if (clsSondaSalud.EsSolicitada(args))
#pragma warning restore IDE1006
{
    return await clsSondaSalud.EjecutarAsync();
}

// Fallos de arranque. Si este ensamblado es el proceso (contenedor, "dotnet Cotifly.Api.dll"),
// cualquier excepción al construir o arrancar el host se registra UNA vez y el proceso termina con 1:
// dejarla escapar en el PID 1 del contenedor aborta el proceso (código 139) y repite la traza.
// Bajo WebApplicationFactory (pruebas) el ensamblado de entrada es el de pruebas: la excepción se
// propaga, porque es la que la fábrica entrega a la prueba.
var esProcesoPropio = Assembly.GetEntryAssembly() == typeof(Program).Assembly;

WebApplication app;
try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Services
        .AddAplicacion()
        .AddInfraestructura(builder.Configuration);

    // Errores de validación automática como ErrorWeb ({"error": "..."}), no ProblemDetails.
    // JSON por grupo (ADR-002): /web/* en snake_case (clsJsonSnakeCaseAttribute); /api/auth/* y /health sin cambios.
    // ETag de la versión de la cotización en las respuestas 2xx que la traen (ADR-018 §8).
    builder.Services.AddControllers(o => o.Filters.Add<clsEtagVersionFilter>())
        .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = clsErrorWeb.RespuestaValidacion)
        .AddConvencionJsonWeb();
    clsFabricaErroresCliente.Registrar(builder.Services);

    // Swagger UI para explorar la API. Solo se expone en Development (ver más abajo); el generador
    // se registra siempre porque no cuesta nada y así la prueba de arranque no depende del entorno.
    // Botón "Authorize" con JWT Bearer: se pega el access token del login (/api/auth/login-clientepro)
    // y la UI lo envía como "Authorization: Bearer <token>". Patrón de Swashbuckle 10.x / Microsoft.OpenApi 2.x
    // (AddSecurityRequirement recibe un Func<OpenApiDocument, OpenApiSecurityRequirement> y usa OpenApiSecuritySchemeReference).
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(opciones =>
    {
        opciones.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Pega el access token del login (sin la palabra 'Bearer').",
        });
        opciones.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("bearer", document)] = [],
        });
    });

    // CORS del frontend de ClientePro, encabezados reenviados de Traefik y límites HTTP.
    builder.Services.AddSeguridadHttp();
    builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

    // JwtBearer + revocación por jti + políticas por rol + límite de /api/auth/* (ADR-009).
    builder.Services.AddAutenticacionCotifly();

    app = builder.Build();
}
catch (Exception ex) when (esProcesoPropio)
{
    // Aún no hay logger: una línea a stderr. En esta fase no se leen valores de configuración
    // (las opciones se enlazan y validan al arrancar), así que el mensaje no lleva secretos.
    await Console.Error.WriteLineAsync($"crit: Cotifly.Api no pudo construir el host: {ex.GetType().FullName}: {ex.Message}");
    return 1;
}

// Primero, para atrapar lo que escape de todo lo demás. 500 ErrorNegocio ERROR_INTERNO (ErrorWeb en /api/*), sin
// detalles técnicos, con la excepción en el log junto al id de correlación de la petición.
app.UseManejadorErroresNoControlados();

// Swagger UI solo en Development (#diagnóstico servidor): GET /swagger. En Production no se expone.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSeguridadHttp();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();

try
{
    await app.RunAsync();
}
catch (Exception) when (esProcesoPropio)
{
    // El host ya registró el error: "Hosting failed to start" (StartAsync, p. ej. ValidateOnStart, que
    // nombra la clave y nunca el valor, o un puerto inválido) o el fallo al detenerse. No se repite.
    return 1;
}

return 0;
