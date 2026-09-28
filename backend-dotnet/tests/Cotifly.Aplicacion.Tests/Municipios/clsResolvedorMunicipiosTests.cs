using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Services;

namespace Cotifly.Aplicacion.Tests.Municipios;

/// <summary>
/// <see cref="clsResolvedorMunicipios"/> (HU-005.13): el texto libre del comercial contra el catálogo de municipios de Nexo. Una
/// coincidencia se usa; varias (mismo nombre en varios departamentos) o una aproximada se preguntan; ninguna no se inventa.
/// </summary>
public sealed class clsResolvedorMunicipiosTests
{
    private static readonly clsMunicipio[] _catalogo =
    [
        new(1, "05400", "La Unión", "Antioquia", "Colombia"),
        new(2, "52399", "La Unión", "Nariño", "Colombia"),
        new(3, "76400", "La Unión", "Valle del Cauca", "Colombia"),
        new(4, "05664", "San Pedro de los Milagros", "Antioquia", "Colombia"),
        new(5, "70717", "San Pedro", "Sucre", "Colombia"),
        new(6, "76670", "San Pedro", "Valle del Cauca", "Colombia"),
        new(7, "11001", "Bogotá, D.C.", "Bogotá, D.C.", "Colombia"),
        new(8, "13001", "Cartagena de Indias", "Bolívar", "Colombia"),
        new(9, "76892", "Yumbo", "Valle del Cauca", "Colombia"),
        new(10, "76892", "Yumbo", "Valle del Cauca", "Colombia"),
        new(11, null, null, "Sin nombre", "Colombia"),
        new(12, null, "Medellín", null, null),
    ];

    [Theory]
    [InlineData("Yumbo", 9)]
    [InlineData("  YUMBO ", 9)]
    [InlineData("Medellin", 12)]
    [InlineData("Bogotá", 7)]
    [InlineData("bogota d.c.", 7)]
    [InlineData("Cartagena", 8)]
    [InlineData("La Unión, Nariño", 2)]
    [InlineData("la union narino", 2)]
    [InlineData("La Unión (Valle)", 3)]
    [InlineData("San Pedro, Sucre", 5)]
    public void UnaSolaCoincidenciaSeUsaDirecto(string texto, int id)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Unica);
        resolucion.Resuelto!.Id.Should().Be(id);
        resolucion.Total.Should().Be(1, "las filas repetidas del catálogo cuentan una vez");
    }

    [Theory]
    [InlineData("La Unión", new[] { "Antioquia", "Nariño", "Valle del Cauca" })]
    [InlineData("la union", new[] { "Antioquia", "Nariño", "Valle del Cauca" })]
    [InlineData("San Pedro", new[] { "Sucre", "Valle del Cauca" })]
    public void ElMismoNombreEnVariosDepartamentosQuedaComoCandidatos(string texto, string[] departamentos)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Varias);
        resolucion.Resuelto.Should().BeNull();
        resolucion.Candidatos.Select(c => c.Departamento).Should().Equal(departamentos);
        resolucion.Total.Should().Be(departamentos.Length);
    }

    [Fact]
    public void UnaPistaDeDepartamentoQueNoCoincideDejaTodosLosDelNombre()
    {
        var resolucion = clsResolvedorMunicipios.Resolver("La Unión, Cundinamarca", _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Varias);
        resolucion.Candidatos.Should().HaveCount(3);
    }

    [Fact]
    public void UnaCoincidenciaAproximadaSiempreSePregunta()
    {
        var resolucion = clsResolvedorMunicipios.Resolver("Milagros", _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Varias, "nunca se asume un municipio por parecido");
        resolucion.Candidatos.Should().ContainSingle().Which.Id.Should().Be(4);
    }

    [Theory]
    [InlineData("Macondo")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Un")]
    public void SinCoincidenciasNoSeInventaNada(string? texto)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Ninguna);
        resolucion.Candidatos.Should().BeEmpty();
        resolucion.Total.Should().Be(0);
    }

    [Fact]
    public void RecortaLosCandidatosPeroInformaElTotal()
    {
        var catalogo = Enumerable.Range(1, 12).Select(i => new clsMunicipio(i, null, "Santa Rosa", $"Departamento {i:00}", "Colombia")).ToList();

        var resolucion = clsResolvedorMunicipios.Resolver("Santa Rosa", catalogo);

        resolucion.Candidatos.Should().HaveCount(clsResolvedorMunicipios.MaximoCandidatos);
        resolucion.Total.Should().Be(12);
    }

    [Fact]
    public void ElCandidatoLlevaLosDatosDelCatalogoYSuFormaCanonica()
    {
        var municipio = clsResolvedorMunicipios.Resolver("La Unión, Nariño", _catalogo).Resuelto!;

        municipio.Should().Be(new Dominio.Agente.clsMunicipioResuelto(2, "La Unión", "Nariño", "Colombia", "52399"));
        municipio.Canonico().Should().Be("La Unión, Nariño");
        new Dominio.Agente.clsMunicipioResuelto(12, "Medellín").Canonico().Should().Be("Medellín");
    }

    [Theory]
    [InlineData("  Bogotá, D.C. ", "bogota d c")]
    [InlineData("LA UNIÓN (Nariño)", "la union narino")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LaClaveIgnoraTildesMayusculasYSignos(string? texto, string clave) =>
        clsResolvedorMunicipios.Clave(texto).Should().Be(clave);
}
