using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Cotifly.Infraestructura.Tests.Configuracion;

/// <summary>Entorno de host mínimo para probar validaciones que dependen del entorno.</summary>
internal sealed class clsEntornoFalso(string nombreEntorno) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = nombreEntorno;
    public string ApplicationName { get; set; } = "Cotifly.Pruebas";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
