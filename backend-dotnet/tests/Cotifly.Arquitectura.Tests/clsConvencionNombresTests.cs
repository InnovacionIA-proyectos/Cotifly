using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cotifly.Arquitectura.Tests;

/// <summary>
/// Convención de nombres de ADR-002 verificada sobre los ensamblados compilados
/// (complementa a IDE1006 del .editorconfig, que la verifica sobre el código fuente).
/// Exentos: <c>Program</c> (instrucciones de nivel superior) y los tipos que genera el compilador.
/// </summary>
public sealed class clsConvencionNombresTests
{
    public static TheoryData<string> Capas => new(clsCapas.Todas.Select(a => a.GetName().Name!));

    [Theory]
    [MemberData(nameof(Capas))]
    public void LasClasesEmpiezanPorCls(string capa)
    {
        var infractores = TiposPropios(capa)
            .Where(t => t.IsClass && t.Name != "Program")
            .Where(t => !NombreSinAridad(t).StartsWith("cls", StringComparison.Ordinal) || !EsPascalTrasPrefijo(t, "cls"))
            .Select(t => t.FullName);

        infractores.Should().BeEmpty("las clases de {0} deben llamarse cls + PascalCase (ADR-002)", capa);
    }

    [Theory]
    [MemberData(nameof(Capas))]
    public void LasInterfacesEmpiezanPorICls(string capa)
    {
        var infractores = TiposPropios(capa)
            .Where(t => t.IsInterface)
            .Where(t => !NombreSinAridad(t).StartsWith("ICls", StringComparison.Ordinal) || !EsPascalTrasPrefijo(t, "ICls"))
            .Select(t => t.FullName);

        infractores.Should().BeEmpty("las interfaces de {0} deben llamarse ICls + PascalCase (ADR-002)", capa);
    }

    private static IEnumerable<Type> TiposPropios(string capa) =>
        clsCapas.Todas.Single(a => a.GetName().Name == capa)
            .GetTypes()
            .Where(t => t.Namespace?.StartsWith("Cotifly", StringComparison.Ordinal) == true)
            .Where(t => !t.Name.StartsWith('<'))
            .Where(t => t.GetCustomAttribute<CompilerGeneratedAttribute>() is null);

    private static string NombreSinAridad(Type tipo)
    {
        var indice = tipo.Name.IndexOf('`', StringComparison.Ordinal);
        return indice < 0 ? tipo.Name : tipo.Name[..indice];
    }

    private static bool EsPascalTrasPrefijo(Type tipo, string prefijo)
    {
        var resto = NombreSinAridad(tipo)[prefijo.Length..];
        return resto.Length > 0 && char.IsUpper(resto[0]);
    }
}
