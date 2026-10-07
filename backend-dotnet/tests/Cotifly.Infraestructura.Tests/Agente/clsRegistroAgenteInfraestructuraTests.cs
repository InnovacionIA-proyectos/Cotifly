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
/// publicador, ni opciones <c>GChat</c>), con el candado por sesión, el barrido de sesiones y la URL firmada del PDF (ADR-011 §3).
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
        proveedor.GetRequiredService<IClsFirmaUrlPdf>().Should().BeOfType<clsFirmaUrlPdfHmac>();
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

    // ADR-011 §3: URL firmada válida, vencida, alterada o de otro archivo.
    [Fact]
    public void LaUrlFirmadaValeQuinceMinutosSoloParaSuArchivoYSinAlteraciones()
    {
        var reloj = new clsRelojFalso();
        var firma = Firma(reloj);
        var usuario = Guid.NewGuid();
        var token = firma.Firmar("PCN-NV-24092026-001.pdf", usuario);

        firma.Verificar("PCN-NV-24092026-001.pdf", token).Should().Be(usuario);
        firma.Verificar("PCN-NV-24092026-002.pdf", token).Should().BeNull("el token es de otro archivo");
        firma.Verificar("PCN-NV-24092026-001.pdf", token[..^2] + (token[^2] == 'A' ? "BB" : "AA")).Should().BeNull("firma alterada");
        firma.Verificar("PCN-NV-24092026-001.pdf", $"{Guid.NewGuid():N}{token[32..]}").Should().BeNull("otro comercial con la misma firma");
        firma.Verificar("PCN-NV-24092026-001.pdf", "basura").Should().BeNull();
        firma.Verificar("PCN-NV-24092026-001.pdf", null).Should().BeNull();
        Firma(reloj, "otra-clave-hmac-distinta-de-32-caracteres").Verificar("PCN-NV-24092026-001.pdf", token).Should().BeNull("otra clave");

        reloj.Avanzar(TimeSpan.FromMinutes(14));
        firma.Verificar("PCN-NV-24092026-001.pdf", token).Should().Be(usuario);
        reloj.Avanzar(TimeSpan.FromMinutes(2));
        firma.Verificar("PCN-NV-24092026-001.pdf", token).Should().BeNull("venció a los 15 minutos");
    }

    [Fact]
    public void SinClaveConfiguradaUsaUnaClaveEfimeraPorProceso()
    {
        var reloj = new clsRelojFalso();
        var usuario = Guid.NewGuid();
        var token = Firma(reloj, null).Firmar("a.pdf", usuario);

        Firma(reloj, null).Verificar("a.pdf", token).Should().BeNull("cada instancia sin clave tiene la suya");
    }

    private static clsFirmaUrlPdfHmac Firma(clsRelojFalso reloj, string? clave = "clave-hmac-de-pruebas-de-32-caracteres!") =>
        new(Options.Create(new clsOpcionesFirmaUrlPdf { ClaveFirmaUrl = clave, MinutosUrlFirmada = 15 }), reloj, NullLogger<clsFirmaUrlPdfHmac>.Instance);
}
