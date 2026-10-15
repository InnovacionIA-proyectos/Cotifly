using System.Net;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Configuracion;

/// <summary>
/// Identidad del comercial ante Nexo en un solo lugar (HU-023.4, #159, decisión 1 del 2026-10-13): todos los adaptadores mandan
/// <c>X-Portal-clsUsuario-Id</c>, el encabezado que valida <c>clsIdentidadPortalAuthenticationHandler</c> de Nexo. Cada adaptador lo
/// prueba con su falso (<c>IdPortalUsuario</c> lee el encabezado nuevo y el falso responde 401 sin él).
/// </summary>
public sealed class clsIdentidadNexoTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ElEncabezadoEsElQueValidaNexo()
    {
        clsIdentidadNexo.Encabezado.Should().Be("X-Portal-clsUsuario-Id").And.Be(clsPeticionNexo.EncabezadoIdentidad);
    }

    [Fact]
    public void IdentificarPoneSoloElEncabezadoNuevoYReemplazaUnoAnterior()
    {
        using var peticion = new HttpRequestMessage(HttpMethod.Get, "http://nexo-test.local/api/catalogos/tipos-vehiculo");

        clsIdentidadNexo.Identificar(peticion, 1L);
        clsIdentidadNexo.Identificar(peticion, 1007L);

        peticion.Headers.GetValues("X-Portal-clsUsuario-Id").Should().Equal("1007");
        peticion.Headers.Contains("X-Portal-Usuario-Id").Should().BeFalse("Nexo responde 401 al nombre viejo");
    }

    [Fact]
    public async Task ElNexoFalsoExigeElEncabezadoNuevoComoNexo()
    {
        using var nexo = new clsNexoFalso();
        using var http = new HttpClient(nexo);
        using var viejo = new HttpRequestMessage(HttpMethod.Get, $"{clsNexoFalso.UrlBase}/api/catalogos/tipos-vehiculo");
        viejo.Headers.Add("X-Portal-Usuario-Id", "1007");
        using var nuevo = new HttpRequestMessage(HttpMethod.Get, $"{clsNexoFalso.UrlBase}/api/catalogos/tipos-vehiculo");
        clsIdentidadNexo.Identificar(nuevo, 1007L);

        using var conViejo = await http.SendAsync(viejo, Ct);
        using var conNuevo = await http.SendAsync(nuevo, Ct);

        conViejo.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        conNuevo.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void NingunAdaptadorEscribeElEncabezadoAMano()
    {
        // Un solo lugar: los adaptadores llaman a clsIdentidadNexo.Identificar; nadie vuelve a escribir el nombre del encabezado.
        var raiz = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        Directory.Exists(raiz).Should().BeTrue($"la prueba revisa las fuentes de {raiz}: sin ellas no puede pasar en verde");
        var archivos = Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories).ToList();
        archivos.Should().Contain(f => f.EndsWith("clsIdentidadNexo.cs", StringComparison.Ordinal), "debe estar revisando el src de Cotifly");

        var conLiteral = Directory.EnumerateFiles(raiz, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f).Any(l => !l.TrimStart().StartsWith("///", StringComparison.Ordinal)
                                                    && (l.Contains("\"X-Portal-Usuario-Id\"", StringComparison.Ordinal)
                                                        || (l.Contains("\"X-Portal-clsUsuario-Id\"", StringComparison.Ordinal)
                                                            && !f.EndsWith("clsIdentidadNexo.cs", StringComparison.Ordinal)))))
            .ToList();

        conLiteral.Should().BeEmpty();
    }
}
