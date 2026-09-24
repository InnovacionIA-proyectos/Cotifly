// Composition root de Cotifly (ADR-002).
// El compilador genera la clase "Program" a partir de estas instrucciones de nivel superior.
// Es la única clase exenta del prefijo cls (ADR-002): IDE1006 la reporta en la primera
// instrucción, por eso la supresión cubre solo esa línea. En .NET 10 "Program" ya es
// pública (generador de ASP.NET Core), así que WebApplicationFactory<Program> no necesita
// declarar "public partial class Program".
using Cotifly.Aplicacion;
using Cotifly.Infraestructura;

#pragma warning disable IDE1006
var builder = WebApplication.CreateBuilder(args);
#pragma warning restore IDE1006

builder.Services
    .AddAplicacion()
    .AddInfraestructura(builder.Configuration);

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

await app.RunAsync();
