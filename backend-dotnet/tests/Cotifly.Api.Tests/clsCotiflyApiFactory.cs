using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Cotifly.Api.Tests;

/// <summary>
/// Levanta la API en memoria para las pruebas. Usa un entorno propio ("Pruebas")
/// para comprobar que el entorno se lee de la configuración y no está fijo en el código.
/// </summary>
public sealed class clsCotiflyApiFactory : WebApplicationFactory<Program>
{
    public const string EntornoPruebas = "Pruebas";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(EntornoPruebas);
    }
}
