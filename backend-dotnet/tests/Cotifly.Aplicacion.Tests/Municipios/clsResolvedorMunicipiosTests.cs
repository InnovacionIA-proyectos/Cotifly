using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Aplicacion.Municipios.Services;
using Cotifly.Dominio.Agente;

namespace Cotifly.Aplicacion.Tests.Municipios;

/// <summary>
/// <see cref="clsResolvedorMunicipios"/> (HU-005.13): el texto libre del comercial contra el catálogo de municipios de Nexo. Una
/// coincidencia se usa; varias (mismo nombre en varios departamentos) o una aproximada se preguntan; ninguna no se inventa. El
/// catálogo de prueba trae los casi homónimos reales (Cartagena de Indias y Cartagena del Chairá; Santiago de Cali, Calima y
/// California) para que las ciudades principales no terminen en "preguntar" sin necesidad.
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
        new(13, "18150", "Cartagena del Chairá", "Caquetá", "Colombia"),
        new(14, "76001", "Santiago de Cali", "Valle del Cauca", "Colombia"),
        new(15, "76126", "Calima", "Valle del Cauca", "Colombia"),
        new(16, "68132", "California", "Santander", "Colombia"),
        new(17, "08001", "Barranquilla", "Atlántico", "Colombia"),
        new(18, "76109", "Buenaventura", "Valle del Cauca", "Colombia"),
        new(19, "54001", "San José de Cúcuta", "Norte de Santander", "Colombia"),
        new(20, "05055", "Argelia", "Antioquia", "Colombia"),
        new(21, "19050", "Argelia", "Cauca", "Colombia"),
        new(22, "76054", "Argelia", "Valle del Cauca", "Colombia"),
        new(23, "05001", "Medellín", "Antioquia", "Colombia"),
    ];

    [Theory]
    [InlineData("Yumbo", 9)]
    [InlineData("  YUMBO ", 9)]
    [InlineData("Bogotá", 7)]
    [InlineData("bogota d.c.", 7)]
    [InlineData("Bogota DC", 7)]
    [InlineData("Bogotá D C", 7)]
    [InlineData("La Unión, Nariño", 2)]
    [InlineData("la union narino", 2)]
    [InlineData("LA UNION, NARINO", 2)]
    [InlineData("La Unión (Valle)", 3)]
    [InlineData("La Unión, Nariño, Colombia", 2)]
    [InlineData("San Pedro, Sucre", 5)]
    [InlineData("Medellín Antioquia Colombia", 23)]
    [InlineData("Barranquilla", 17)]
    [InlineData("Buenaventura", 18)]
    [InlineData("Barranq", 17)]
    [InlineData("san jose de cucuta", 19)]
    [InlineData("San José de Cúcuta", 19)]
    [InlineData("Argelia, Valle del Cauca", 22)]
    [InlineData("Argelia, Cauca", 21)]
    [InlineData("Argelia Valle del Cauca", 22)]
    public void UnaSolaCoincidenciaSeUsaDirecto(string texto, int id)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Unica, texto);
        resolucion.Resuelto!.Id.Should().Be(id);
        resolucion.Total.Should().Be(1, "las filas repetidas del catálogo cuentan una vez");
    }

    // Palabra completa antes que parecido; entre varias por palabra, la única capital de departamento (Dane XX001).
    [Theory]
    [InlineData("Cali", 14)]
    [InlineData("Cartagena", 8)]
    [InlineData("Cartagena, Bolívar", 8)]
    [InlineData("Cúcuta", 19)]
    [InlineData("Cúcuta, Norte de Santander", 19)]
    [InlineData("Cucuta Norte de Santander", 19)]
    public void LasCiudadesPrincipalesSeResuelvenSinPreguntar(string texto, int id)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Unica, texto);
        resolucion.Resuelto!.Id.Should().Be(id);
    }

    [Fact]
    public void CartagenaConElDepartamentoDelOtroMunicipioEligeEse()
    {
        var resolucion = clsResolvedorMunicipios.Resolver("Cartagena, Caquetá", _catalogo);

        resolucion.Resuelto!.Id.Should().Be(13, "la pista del departamento manda sobre el desempate por capital");
    }

    [Theory]
    [InlineData("La Unión", new[] { "Antioquia", "Nariño", "Valle del Cauca" })]
    [InlineData("la union", new[] { "Antioquia", "Nariño", "Valle del Cauca" })]
    [InlineData("San Pedro", new[] { "Sucre", "Valle del Cauca" })]
    [InlineData("Argelia", new[] { "Antioquia", "Cauca", "Valle del Cauca" })]
    public void ElMismoNombreEnVariosDepartamentosQuedaComoCandidatos(string texto, string[] departamentos)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Varias);
        resolucion.Resuelto.Should().BeNull();
        resolucion.Candidatos.Select(c => c.Departamento).Should().Equal(departamentos);
        resolucion.Total.Should().Be(departamentos.Length);
    }

    [Fact]
    public void DosMunicipiosConElMismoNombreExactoSePreguntanAunqueUnoSeaCapital()
    {
        var catalogo = new clsMunicipio[] { new(1, "63001", "Armenia", "Quindío", "Colombia"), new(2, "05059", "Armenia", "Antioquia", "Colombia") };

        clsResolvedorMunicipios.Resolver("Armenia", catalogo).Coincidencia.Should().Be(CoincidenciaMunicipio.Varias);
    }

    [Fact]
    public void UnaPistaDeDepartamentoQueNoCoincideDejaTodosLosDelNombre()
    {
        var resolucion = clsResolvedorMunicipios.Resolver("La Unión, Cundinamarca", _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Varias);
        resolucion.Candidatos.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("ilagros", 4)]
    [InlineData("ranquil", 17)]
    public void UnaCoincidenciaAproximadaSiempreSePregunta(string texto, int id)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Varias, "nunca se asume un municipio por parecido");
        resolucion.Candidatos.Should().ContainSingle().Which.Id.Should().Be(id);
    }

    [Theory]
    [InlineData("Macondo")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Un")]
    [InlineData(" , ( ) ")]
    [InlineData("--")]
    public void SinCoincidenciasNoSeInventaNada(string? texto)
    {
        var resolucion = clsResolvedorMunicipios.Resolver(texto, _catalogo);

        resolucion.Coincidencia.Should().Be(CoincidenciaMunicipio.Ninguna);
        resolucion.Candidatos.Should().BeEmpty();
        resolucion.Total.Should().Be(0);
    }

    [Fact]
    public void UnaEntradaMuyLargaNoRompeNiTarda()
    {
        var largo = string.Concat(Enumerable.Repeat("La Unión, Nariño (", 20_000));
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var resolucion = clsResolvedorMunicipios.Resolver(largo, _catalogo);

        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
        resolucion.Should().NotBeNull();
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

        municipio.Should().Be(new clsMunicipioResuelto(2, "La Unión", "Nariño", "Colombia", "52399"));
        municipio.Canonico().Should().Be("La Unión, Nariño");
        new clsMunicipioResuelto(12, "Medellín").Canonico().Should().Be("Medellín");
    }

    [Fact]
    public void ElCanonicoDeBogotaNoRepiteElDepartamento() =>
        clsResolvedorMunicipios.Resolver("Bogotá", _catalogo).Resuelto!.Canonico().Should().Be("Bogotá, D.C.");

    [Theory]
    [InlineData("  Bogotá, D.C. ", "bogota dc")]
    [InlineData("Bogota D C", "bogota dc")]
    [InlineData("LA UNIÓN (Nariño)", "la union narino")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void LaClaveIgnoraTildesMayusculasYSignosYUneLasSiglas(string? texto, string clave) =>
        clsResolvedorMunicipios.Clave(texto).Should().Be(clave);

    [Theory]
    [InlineData("valle del cauca", "valle del cauca")]
    [InlineData("argelia valle del cauca", "valle del cauca")]
    [InlineData("el de cauca", "cauca")]
    [InlineData("norte de santander", "norte de santander")]
    public void DeDepartamentosAnidadosGanaElMasLargo(string pista, string esperado) =>
        clsResolvedorMunicipios.DepartamentosNombrados(pista, ["cauca", "valle del cauca", "santander", "norte de santander", "antioquia"])
            .Should().Equal(esperado);
}
