using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-044.9 (#292): datos metidos en otra columna. Antes de <c>sugerir-mapeo</c> y <c>aplicar-mapeo</c>, Cotifly agrega columnas derivadas con
/// nombre fijo: el vehículo reconocido dentro de un texto («Venezuela Mula» → Mula, con el diccionario de alias de NEXO) y el contenedor con su
/// peso o rango («C20 de 0 a 8 Ton» → 20' y 8.000 kg, del rango el valor más alto). Solo se agregan si al menos una fila reconoce algo, al
/// final, sin tocar la celda original; un archivo sin esos textos mantiene su firma de plantilla.
/// </summary>
public sealed class clsColumnasDerivadasServiceTests
{
    private static readonly Guid _usuario = Guid.NewGuid();

    // El diccionario de alias de NEXO: aliases de vehículo (palabras), NO los códigos oficiales del catálogo (un «2» o «3» suelto no es vehículo).
    private static readonly clsDiccionarioAliasNexo _diccionario = new(
    [
        new(TipoAliasNexo.Vehiculo, "Mula", "MULA", "3S3"),
        new(TipoAliasNexo.Vehiculo, "Turbo", "TURBO", "2L2"),
        new(TipoAliasNexo.Vehiculo, "Doble troque", "DOBLE TROQUE", "2L2"),
    ]);

    private readonly IClsAliasNexoProvider _alias = Substitute.For<IClsAliasNexoProvider>();

    public clsColumnasDerivadasServiceTests() =>
        _alias.ObtenerAsync(_usuario, Arg.Any<CancellationToken>()).Returns(clsResultado<clsDiccionarioAliasNexo>.Exito(_diccionario));

    private clsColumnasDerivadasService Servicio() => new(_alias);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<IReadOnlyList<string>> Filas(params string[] celdas) =>
        [.. celdas.Select(c => (IReadOnlyList<string>)[c])];

    [Fact]
    public async Task UnVehiculoDentroDelTextoSeReconoceConElDiccionarioDeAlias()
    {
        var hoja = await Servicio().EnriquecerAsync(_usuario, ["Zona"], Filas("Venezuela Mula", "Bogotá"), Ct);

        hoja.Encabezados.Should().Equal("Zona", clsColumnasDerivadasService.EncabezadoVehiculo);
        hoja.Filas[0].Should().Equal("Venezuela Mula", "Mula");
        // Sin vehículo reconocido la celda derivada queda vacía.
        hoja.Filas[1].Should().Equal("Bogotá", string.Empty);
    }

    [Theory]
    [InlineData("C20 de 0 a 8 Ton", "20'", "8000")]
    [InlineData("Conten. x 40' Hasta 25 ton", "40'", "25000")]
    [InlineData("C40 de 5.000 a 8.000 kg", "40'", "8000")]
    [InlineData("20' hasta 8 ton", "20'", "8000")]
    // #292 (hallazgo del validador): el «20»/«40» del código del contenedor no debe leerse como extremo bajo del rango de peso.
    [InlineData("C20 hasta 8 ton", "20'", "8000")]
    [InlineData("C40 a 25 ton", "40'", "25000")]
    public async Task UnContenedorConPesoORangoDaLaClaseYElPesoMasAltoEnKg(string texto, string contenedor, string peso)
    {
        var hoja = await Servicio().EnriquecerAsync(_usuario, ["Servicio"], Filas(texto), Ct);

        hoja.Encabezados.Should().Equal(
            "Servicio", clsColumnasDerivadasService.EncabezadoContenedor, clsColumnasDerivadasService.EncabezadoPeso);
        hoja.Filas[0].Should().Equal(texto, contenedor, peso);
    }

    [Fact]
    public async Task DelRangoSeTomaElValorMasAltoYUnSoloPesoSinContenedorSoloAgregaElPeso()
    {
        // «de 5.000 a 8.000 kg» → 8.000 kg y sin contenedor (no hay C20/C40/20'/40').
        var hoja = await Servicio().EnriquecerAsync(_usuario, ["Carga"], Filas("de 5.000 a 8.000 kg"), Ct);

        hoja.Encabezados.Should().Equal("Carga", clsColumnasDerivadasService.EncabezadoPeso);
        hoja.Filas[0].Should().Equal("de 5.000 a 8.000 kg", "8000");
    }

    [Fact]
    public async Task UnTextoConVehiculoYPesoAgregaLasDosColumnasEnOrdenFijo()
    {
        // «Turbo hasta 4.5 ton»: el punto es decimal (4,5 t = 4.500 kg) y Turbo es un alias de vehículo.
        var hoja = await Servicio().EnriquecerAsync(_usuario, ["Tipo de servicio"], Filas("Turbo hasta 4.5 ton"), Ct);

        hoja.Encabezados.Should().Equal(
            "Tipo de servicio", clsColumnasDerivadasService.EncabezadoVehiculo, clsColumnasDerivadasService.EncabezadoPeso);
        hoja.Filas[0].Should().Equal("Turbo hasta 4.5 ton", "Turbo", "4500");
    }

    [Fact]
    public async Task UnTextoSinVehiculoNiContenedorNoAgregaColumnasYMantieneLaFirma()
    {
        IReadOnlyList<string> encabezados = ["Origen", "Destino"];
        IReadOnlyList<IReadOnlyList<string>> filas = [["Buenaventura", "Bogotá"], ["Cartagena", "Medellín"]];

        var hoja = await Servicio().EnriquecerAsync(_usuario, encabezados, filas, Ct);

        hoja.Encabezados.Should().BeSameAs(encabezados);
        hoja.Filas.Should().BeSameAs(filas, "sin nada reconocido la hoja vuelve igual, con su misma firma de plantilla");
    }

    [Fact]
    public async Task LasColumnasDerivadasSeAgreganSoloSiAlMenosUnaFilaReconoceAlgo()
    {
        // Ninguna fila trae contenedor, solo vehículo: no se agrega la columna de contenedor ni la de peso.
        var hoja = await Servicio().EnriquecerAsync(_usuario, ["Zona"], Filas("Mula", "Turbo"), Ct);

        hoja.Encabezados.Should().Equal("Zona", clsColumnasDerivadasService.EncabezadoVehiculo);
        hoja.Filas.Select(f => f[^1]).Should().Equal("Mula", "Turbo");
    }

    [Fact]
    public async Task LasTresColumnasVanEnOrdenFijoVehiculoContenedorPeso()
    {
        var hoja = await Servicio().EnriquecerAsync(_usuario, ["A"], Filas("Mula C20 de 0 a 8 Ton"), Ct);

        hoja.Encabezados.Should().Equal(
            "A", clsColumnasDerivadasService.EncabezadoVehiculo, clsColumnasDerivadasService.EncabezadoContenedor,
            clsColumnasDerivadasService.EncabezadoPeso);
        hoja.Filas[0].Should().Equal("Mula C20 de 0 a 8 Ton", "Mula", "20'", "8000");
    }

    [Fact]
    public async Task EnriquecerFilasAgregaLasMismasColumnasAlFinalDeCadaFila()
    {
        IReadOnlyList<IReadOnlyList<string?>?> filas = [["Venezuela Mula", "x"], ["Bogotá", "y"]];

        var enriquecidas = await Servicio().EnriquecerFilasAsync(_usuario, filas, Ct);

        enriquecidas[0].Should().Equal("Venezuela Mula", "x", "Mula");
        enriquecidas[1].Should().Equal("Bogotá", "y", string.Empty);
    }

    [Fact]
    public async Task EnriquecerFilasConUnaFilaNulaDevuelveLasFilasSinCambios()
    {
        // Una fila null es una petición mal formada: no se enriquece, para que aplicar-mapeo la rechace con FILA_INVALIDA.
        IReadOnlyList<IReadOnlyList<string?>?> filas = [["Venezuela Mula"], null];

        var enriquecidas = await Servicio().EnriquecerFilasAsync(_usuario, filas, Ct);

        enriquecidas.Should().BeSameAs(filas);
    }

    [Fact]
    public async Task SinDiccionarioNoDetectaVehiculoPeroSiContenedorYPesoSinError()
    {
        _alias.ObtenerAsync(_usuario, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsDiccionarioAliasNexo>.Fallo(new clsErrorDominio(clsCodigosError.CatalogosFleteNoDisponible, "caído")));

        var hoja = await Servicio().EnriquecerAsync(_usuario, ["Zona"], Filas("Mula C20 de 0 a 8 Ton"), Ct);

        // El vehículo necesita el diccionario de alias de NEXO; el contenedor y el peso son locales y siguen funcionando, sin error.
        hoja.Encabezados.Should().Equal(
            "Zona", clsColumnasDerivadasService.EncabezadoContenedor, clsColumnasDerivadasService.EncabezadoPeso);
        hoja.Filas[0].Should().Equal("Mula C20 de 0 a 8 Ton", "20'", "8000");
    }

    [Fact]
    public async Task NoSePasaDelTopeDeCienColumnas()
    {
        // 100 columnas de origen: aunque se reconozca vehículo, contenedor y peso, no se agrega ninguna (no caben).
        IReadOnlyList<string> encabezados = [.. Enumerable.Range(0, 100).Select(i => $"C{i}")];
        var fila = new List<string>(Enumerable.Repeat("x", 99)) { "Mula C20 de 0 a 8 Ton" };

        var hoja = await Servicio().EnriquecerAsync(_usuario, encabezados, [fila], Ct);

        hoja.Encabezados.Should().HaveCount(100).And.BeSameAs(encabezados);
    }
}
