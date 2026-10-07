using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.IA.Services;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Agente;

/// <summary>
/// Registro del asistente en Infraestructura (HU-013.9, #105): sin Google Chat (ni verificador del JWT, ni cola, ni worker, ni
/// publicador, ni opciones <c>GChat</c>, ni la URL firmada del PDF), con el candado por sesión y el barrido de sesiones.
/// </summary>
public sealed class clsRegistroAgenteInfraestructuraTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Proveedor(string entorno, IDictionary<string, string?>? extra = null)
    {
        var valores = new Dictionary<string, string?> { ["Pdf:StorageDir"] = clsServiciosPruebas.CarpetaPdfsTemporal() };
        foreach (var (clave, valor) in extra ?? new Dictionary<string, string?>())
        {
            valores[clave] = valor;
        }

        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new Configuracion.clsEntornoFalso(entorno));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        return servicios.BuildServiceProvider();
    }

    [Fact]
    public async Task ElRegistroRealDejaElAsistenteSinConfigurarSinGoogleChatYConElStubDeClientes()
    {
        await using var proveedor = Proveedor("Development");
        await using var ambito = proveedor.CreateAsyncScope();

        proveedor.GetRequiredService<IChatClient>().Should().BeOfType<clsChatClientNoConfigurado>();
        proveedor.GetRequiredService<IClsCandadoSesion>().Should().BeOfType<clsCandadoSesionSql>();
        var hospedados = proveedor.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
        hospedados.Should().Contain(typeof(clsBarridoSesionesHostedService));
        hospedados.Select(t => t.Name).Should().NotContain(n => n.Contains("Chat", StringComparison.OrdinalIgnoreCase), "Google Chat se retiró (#105)");
        var buscador = ambito.ServiceProvider.GetRequiredService<IClsBuscadorClientes>();
        (await buscador.BuscarAsync(Guid.NewGuid(), "Falabella", Ct)).Error.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
        proveedor.GetRequiredService<IOptions<clsOpcionesAgente>>().Value.MaxIteraciones.Should().Be(12);
        proveedor.GetRequiredService<IOptions<clsOpcionesSesiones>>().Value.LimiteTokens.Should().Be(6000);
    }

    // HU-013.9 (#105): ninguna clase de opciones se enlaza a las secciones GChat (Google Chat) o Chat (flag de creación del agente
    // legado): las variables GChat__* y Chat__* heredadas de un despliegue viejo no se leen ni se validan.
    [Fact]
    public void NingunaOpcionSeEnlazaALasSeccionesGChatOChat()
    {
        var secciones = new[] { typeof(clsOpcionesIA).Assembly, typeof(clsOpcionesAgente).Assembly }
            .SelectMany(a => a.GetTypes())
            .Select(t => t.GetField("Seccion")?.GetValue(null) as string)
            .Where(s => s is not null)
            .ToList();

        secciones.Should().Contain(["Sesiones", "IA"]);
        secciones.Should().NotContain(["GChat", "Chat"], "las opciones de Google Chat y el flag del chat legado se retiraron");
    }
}
