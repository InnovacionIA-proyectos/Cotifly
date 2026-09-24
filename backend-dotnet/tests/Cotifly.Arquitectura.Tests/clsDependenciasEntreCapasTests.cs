using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Cotifly.Arquitectura.Tests;

/// <summary>
/// Reglas de dependencia de ADR-002:
/// Dominio → nada; Aplicacion → Dominio; Infraestructura → Aplicacion (y Dominio);
/// Api → Aplicacion, e Infraestructura solo para registrar DI.
/// </summary>
public sealed class clsDependenciasEntreCapasTests
{
    private static readonly IObjectProvider<IType> _dominio =
        Types().That().ResideInAssembly(clsCapas.Dominio).As("Dominio");
    private static readonly IObjectProvider<IType> _aplicacion =
        Types().That().ResideInAssembly(clsCapas.Aplicacion).As("Aplicacion");
    private static readonly IObjectProvider<IType> _infraestructura =
        Types().That().ResideInAssembly(clsCapas.Infraestructura).As("Infraestructura");
    private static readonly IObjectProvider<IType> _api =
        Types().That().ResideInAssembly(clsCapas.Api).As("Api");

    [Fact]
    public void DominioNoDependeDeNingunaOtraCapa()
    {
        var regla = Types().That().Are(_dominio).Should()
            .NotDependOnAny(_aplicacion).AndShould().NotDependOnAny(_infraestructura).AndShould().NotDependOnAny(_api);

        Verificar(regla);
    }

    [Fact]
    public void AplicacionSoloDependeDeDominio()
    {
        var regla = Types().That().Are(_aplicacion).Should()
            .NotDependOnAny(_infraestructura).AndShould().NotDependOnAny(_api);

        Verificar(regla);
    }

    [Fact]
    public void InfraestructuraNoDependeDeApi()
    {
        var regla = Types().That().Are(_infraestructura).Should().NotDependOnAny(_api);

        Verificar(regla);
    }

    [Fact]
    public void ApiSoloUsaInfraestructuraParaRegistrarDependencias()
    {
        var infraestructuraSalvoComposicion = Types().That().Are(_infraestructura)
            .And().DoNotHaveFullName("Cotifly.Infraestructura.clsDependenciasInfraestructura")
            .As("Infraestructura (salvo clsDependenciasInfraestructura)");

        var regla = Types().That().Are(_api).Should().NotDependOnAny(infraestructuraSalvoComposicion);

        Verificar(regla);
    }

    [Fact]
    public void LasReferenciasDeEnsambladoRespetanLasCapas()
    {
        static IEnumerable<string> ReferenciasCotifly(System.Reflection.Assembly ensamblado) =>
            ensamblado.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("Cotifly.", StringComparison.Ordinal));

        ReferenciasCotifly(clsCapas.Dominio).Should().BeEmpty();
        ReferenciasCotifly(clsCapas.Aplicacion).Should().BeSubsetOf(["Cotifly.Dominio"]);
        ReferenciasCotifly(clsCapas.Infraestructura).Should().BeSubsetOf(["Cotifly.Dominio", "Cotifly.Aplicacion"]);
        ReferenciasCotifly(clsCapas.Api).Should().BeSubsetOf(["Cotifly.Dominio", "Cotifly.Aplicacion", "Cotifly.Infraestructura"]);
    }

    private static void Verificar(IArchRule regla)
    {
        var violaciones = regla.Evaluate(clsCapas.Modelo)
            .Where(r => !r.Passed)
            .Select(r => r.Description)
            .ToList();

        violaciones.Should().BeEmpty(regla.Description);
    }
}
