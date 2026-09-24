using Cotifly.Aplicacion;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Infraestructura;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Contenedor de dependencias real (<c>AddAplicacion</c> + <c>AddInfraestructura</c>, como la API) apuntando a la base de pruebas y con un
/// reloj falso. Así las pruebas de integración y de aceptación ejercitan el mismo registro que la API.
/// </summary>
public static class clsServiciosPruebas
{
    public static ServiceProvider Crear(
        string? cadenaCotifly,
        IClsReloj reloj,
        string? cadenaIdentidad = null,
        IDictionary<string, string?>? configuracionExtra = null,
        string entorno = "Development",
        Action<IServiceCollection>? configurar = null)
    {
        ArgumentNullException.ThrowIfNull(reloj);

        var valores = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cotifly"] = cadenaCotifly,
            ["ConnectionStrings:Identidad"] = cadenaIdentidad,
        };
        foreach (var (clave, valor) in configuracionExtra ?? new Dictionary<string, string?>())
        {
            valores[clave] = valor;
        }

        var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoPruebas(entorno));
        servicios.AddSingleton(reloj); // antes de AddInfraestructura: su TryAdd respeta este reloj
        servicios.AddLogging();
        servicios.AddAplicacion();
        servicios.AddInfraestructura(configuracion);
        configurar?.Invoke(servicios); // reemplazos de prueba (ej. la fuente de margen SAP TM de muestra)
        return servicios.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class clsEntornoPruebas(string nombre) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "Cotifly.Pruebas";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
