namespace Cotifly.Arquitectura.Tests;

/// <summary>
/// El Dominio solo usa la biblioteca base de .NET: ningún paquete externo (EF Core, HTTP, etc.)
/// puede entrar en esa capa (ADR-002).
/// </summary>
public sealed class clsDependenciasExternasTests
{
    [Fact]
    public void DominioSoloReferenciaLaBibliotecaBaseDeNet()
    {
        var externas = clsCapas.Dominio.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => !EsBibliotecaBase(n));

        externas.Should().BeEmpty("Cotifly.Dominio no puede depender de paquetes externos");
    }

    private static bool EsBibliotecaBase(string nombre) =>
        nombre == "System"
        || nombre.StartsWith("System.", StringComparison.Ordinal)
        || nombre == "netstandard"
        || nombre == "mscorlib"
        || nombre == "Microsoft.CSharp";
}
