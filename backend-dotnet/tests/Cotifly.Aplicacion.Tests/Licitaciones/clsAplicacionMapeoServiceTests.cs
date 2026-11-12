using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-044.3 (#254): <c>aplicar-mapeo</c> valida la forma en local con los códigos de NEXO, reenvía por el puerto de la importación (#252) y
/// traduce cada línea de NEXO a la línea de licitación de Cotifly: fila (base 0 en las filas enviadas) y fila de Excel, línea de negocio de
/// Cotifly y línea de NEXO (ADR-029, la regla de #255), peso sin tara, posiciones como mercancías con el código sugerido, los ids de municipio
/// en null y los errores por fila (los de NEXO con su campo; <c>LINEA_NEGOCIO_SIN_EQUIVALENCIA</c> va en lineas y en errores).
/// </summary>
public sealed class clsAplicacionMapeoServiceTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private readonly IClsImportacionExcelNexo _nexo = Substitute.For<IClsImportacionExcelNexo>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsCubicador _cubicador = Substitute.For<IClsCubicador>();
    private readonly IClsColumnasDerivadasService _columnas = Substitute.For<IClsColumnasDerivadasService>();
    private clsPeticionAplicarMapeo? _enviada;

    public clsAplicacionMapeoServiceTests()
    {
        // HU-044.9 (#292): la detección de columnas derivadas se prueba aparte; aquí es un paso que devuelve las filas tal cual.
        _columnas.EnriquecerFilasAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<IReadOnlyList<string?>?>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyList<IReadOnlyList<string?>?>>(1));
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Linea("Nacional", "Nacional"), Linea("Distribucion", null), Linea("Importacion", "PuertoAInterior"),
            Linea("Exportacion", "InteriorAPuerto"), Linea("Internacional", "Andino"), Linea("DevolucionContenedor", "DevolucionContenedor"),
        ]);
        _cubicador.EstaConfigurado.Returns(true);
        _cubicador.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Exito(
        [
            new clsTipoMercanciaCubicador("CAFE_EN_ESTIBAS", "Café en estibas", null, true, false, null, true),
            new clsTipoMercanciaCubicador("BOBINAS_ACERO", "Bobinas de acero", null, true, false, null, true),
            new clsTipoMercanciaCubicador("GRANEL", "Granel líquido", null, false, false, null, true),
        ]));
    }

    private clsAplicacionMapeoService Servicio(IClsCubicador? cubicador = null) =>
        new(_nexo, _politicas, _columnas, NullLogger<clsAplicacionMapeoService>.Instance, cubicador ?? _cubicador);

    private Task<clsResultado<clsMapeoAplicado>> Aplicar(clsEntradaAplicacionMapeo entrada, IClsCubicador? cubicador = null) =>
        Servicio(cubicador).AplicarAsync(_usuario, entrada, TestContext.Current.CancellationToken);

    private static clsMargenPolitica Linea(string nombre, string? mapeo, bool activa = true)
    {
        var linea = clsMargenPolitica.Crear(nombre, 0.15m, mapeoLineasNexo: mapeo).Valor;
        if (!activa)
        {
            linea.Desactivar();
        }

        return linea;
    }

    private static List<IReadOnlyList<string?>?> Filas(int cuantas) =>
        [.. Enumerable.Range(0, cuantas).Select(i => (IReadOnlyList<string?>?)new List<string?> { $"Origen {i}", $"Destino {i}", "1,5" })];

    private static clsEntradaAplicacionMapeo Entrada(int filas = 3, int? primeraFila = 2, string? nit = "900.123.456-7") =>
        new(nit,
            [new clsColumnaEntradaMapeo(0, "origen", null), new clsColumnaEntradaMapeo(1, "destino", "ninguna"), new clsColumnaEntradaMapeo(2, "peso", "peso_toneladas_a_kg")],
            Filas(filas), primeraFila);

    private static clsLineaImportada Importada(
        int fila, string? lineaNegocio = null, decimal? peso = 1500m, string? claseViaje = null, IReadOnlyList<clsPosicionImportada>? posiciones = null,
        string origen = "Buenaventura", string destino = "Bogotá") =>
        new(fila, origen, destino, "3S3", "FURGON", null, lineaNegocio, claseViaje, claseViaje == "Contenedor" ? "CNT40.HC" : null, null, null,
            null, null, null, null, 4m, 4m, peso, 150_000_000m, posiciones, "REF-1", DateTimeOffset.UnixEpoch);

    private void NexoResponde(int totalFilas, IReadOnlyList<clsLineaImportada> lineas, IReadOnlyList<clsErrorDeFila>? errores = null, int filasVacias = 0) =>
        _nexo.AplicarMapeoAsync(_usuario, Arg.Do<clsPeticionAplicarMapeo>(p => _enviada = p), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoAplicarMapeo>.Exito(new clsResultadoAplicarMapeo(
                totalFilas, filasVacias, errores?.Select(e => e.Fila).Distinct().Count() ?? 0, lineas, errores ?? [], errores?.Count ?? 0)));

    [Fact]
    public async Task ReenviaElMapeoYLasFilasANexoYTraduceCadaCampoALaLineaDeCotifly()
    {
        NexoResponde(1, [Importada(2)]);

        var resultado = await Aplicar(Entrada(filas: 1));

        resultado.EsExitoso.Should().BeTrue();
        _enviada!.ClienteNit.Should().Be("900.123.456-7");
        _enviada.Columnas.Select(c => (c.Indice, c.Campo, c.Transformacion)).Should().Equal(
            (0, "origen", null), (1, "destino", "ninguna"), (2, "peso", "peso_toneladas_a_kg"));
        _enviada.Filas.Should().HaveCount(1);
        _enviada.PrimeraFila.Should().Be(2);
        var linea = resultado.Valor.Lineas.Single();
        linea.Should().BeEquivalentTo(new clsLineaMapeada(0, 2, null, null, "Buenaventura", "Bogotá", null, null, 4m, 4m, "3S3", "FURGON",
            null, null, null, null, null, null, null, null, null, null, null, 1500m, 150_000_000m, null));
        (resultado.Valor.TotalFilas, resultado.Valor.TotalErrores, resultado.Valor.FilasConErrores).Should().Be((1, 0, 0));
    }

    [Theory]
    [InlineData(2, 5, 3, 6, 4)]
    [InlineData(4, 4, 0, 8, 4)]
    [InlineData(10, 12, 2, 10, 0)]
    public async Task LaFilaEsLaPosicionEnLasFilasEnviadasYElErrorTraeLaFilaDeExcel(int primeraFila, int filaNexo, int esperada, int filaError, int esperadaError)
    {
        NexoResponde(5, [Importada(filaNexo)], [new clsErrorDeFila(filaError, "peso", "PESO_INVALIDO", "El peso no es un número.")]);

        var resultado = await Aplicar(Entrada(filas: 5, primeraFila: primeraFila));

        resultado.Valor.Lineas.Single().Fila.Should().Be(esperada);
        resultado.Valor.Lineas.Single().FilaExcel.Should().Be(filaNexo);
        resultado.Valor.Errores.Single().Should().Be(new clsErrorFilaImportada(esperadaError, filaError, "peso", "PESO_INVALIDO", "El peso no es un número."));
    }

    [Fact]
    public async Task SinPrimeraFilaSeNumeraDesdeLaDosComoNexo()
    {
        NexoResponde(2, [Importada(3)]);

        var resultado = await Aplicar(Entrada(filas: 2, primeraFila: null));

        _enviada!.PrimeraFila.Should().BeNull("NEXO usa su valor por defecto");
        resultado.Valor.Lineas.Single().Fila.Should().Be(1);
        resultado.Valor.Lineas.Single().FilaExcel.Should().Be(3);
    }

    [Fact]
    public async Task UnaFilaQueNexoNumeraFueraDeLasEnviadasQuedaSinFilaPeroConSuNumeroDeExcel()
    {
        NexoResponde(1, [Importada(9)], [new clsErrorDeFila(1, null, "FILA_RARA", "Error de prueba.")]);

        var resultado = await Aplicar(Entrada(filas: 1));

        resultado.Valor.Lineas.Single().Fila.Should().BeNull();
        resultado.Valor.Lineas.Single().FilaExcel.Should().Be(9);
        resultado.Valor.Errores.Single().Should().Be(new clsErrorFilaImportada(null, 1, "fila", "FILA_RARA", "Error de prueba."));
    }

    [Fact]
    public async Task ElPesoVaAPesoKgSinTaraTambienEnContenedorYLosIdsDeMunicipioSonNull()
    {
        NexoResponde(2, [Importada(2, peso: 24_500m, claseViaje: "Contenedor"), Importada(3, peso: 8_000m, claseViaje: "Carga suelta")]);

        var resultado = await Aplicar(Entrada(filas: 2));

        resultado.Valor.Lineas.Select(l => (l.ClaseViaje, l.ClaseContenedor, l.PesoKg)).Should().Equal(
            ("Contenedor", "CNT40.HC", 24_500m), ("Carga suelta", null, 8_000m));
        resultado.Valor.Lineas.Should().OnlyContain(l =>
            l.OrigenMunicipioId == null && l.DestinoMunicipioId == null && l.CiudadDevolucionMunicipioId == null && l.CiudadLevanteMunicipioId == null);
    }

    [Fact]
    public async Task LasPosicionesSonMercanciasConElPesoPorUnidadYElCodigoDelExcelOElSugerido()
    {
        NexoResponde(1,
        [
            Importada(2, posiciones:
            [
                new clsPosicionImportada("Bobinas de acero laminado", 4, 120m, 100m, 90m, 500m, null),
                new clsPosicionImportada("Lo que sea", 2, 50m, 40m, 30m, 20m, "CODIGO_DEL_EXCEL"),
                new clsPosicionImportada("Cosa desconocida", 1, 0m, 0m, 0m, 10m, null),
                new clsPosicionImportada("   ", 1, 10m, 10m, 10m, null, null),
            ]),
        ]);

        var resultado = await Aplicar(Entrada(filas: 1));

        resultado.Valor.Lineas.Single().Mercancias.Should().Equal(
            new clsMercanciaMapeada("BOBINAS_ACERO", "Bobinas de acero laminado", "Bobinas de acero laminado", 4, 120m, 100m, 90m, 500m),
            new clsMercanciaMapeada("CODIGO_DEL_EXCEL", "Lo que sea", "Lo que sea", 2, 50m, 40m, 30m, 20m),
            new clsMercanciaMapeada(null, "Cosa desconocida", "Cosa desconocida", 1, null, null, null, 10m),
            new clsMercanciaMapeada(null, null, null, 1, 10m, 10m, 10m, null));
        await _cubicador.Received(1).ListarTiposMercanciaAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SoloSugiereTiposCubicablesYSinCatalogoNoSugiereNiFalla()
    {
        NexoResponde(1, [Importada(2, posiciones: [new clsPosicionImportada("Granel líquido", 1, 10m, 10m, 10m, 5m, null)])]);
        var caido = Substitute.For<IClsCubicador>();
        caido.EstaConfigurado.Returns(true);
        caido.ListarTiposMercanciaAsync(Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTipoMercanciaCubicador>>.Fallo(new clsErrorDominio(clsCodigosError.CubicadorNoDisponible, "caído")));

        var conCatalogo = await Aplicar(Entrada(filas: 1));
        var sinCatalogo = await Aplicar(Entrada(filas: 1), caido);

        conCatalogo.Valor.Lineas.Single().Mercancias!.Single().CodigoMercancia.Should().BeNull("GRANEL no es cubicable");
        sinCatalogo.EsExitoso.Should().BeTrue();
        sinCatalogo.Valor.Lineas.Single().Mercancias!.Single().CodigoMercancia.Should().BeNull("sin catálogo no se sugiere");
    }

    [Fact]
    public async Task ConTodasLasPosicionesConCodigoNoLeeElCatalogo()
    {
        NexoResponde(1, [Importada(2, posiciones: [new clsPosicionImportada("Café", 1, 10m, 10m, 10m, 5m, "CAFE_EN_ESTIBAS")])]);

        var resultado = await Aplicar(Entrada(filas: 1));

        resultado.Valor.Lineas.Single().Mercancias!.Single().CodigoMercancia.Should().Be("CAFE_EN_ESTIBAS");
        await _cubicador.DidNotReceive().ListarTiposMercanciaAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Puerto a interior", "Importacion", "Puerto a interior")]
    [InlineData("puertoAInterior", "Importacion", "Puerto a interior")]
    [InlineData("Andino", "Internacional", "Andino")]
    [InlineData("Nacional", "Nacional", "Nacional")]
    [InlineData("Devolución de contenedor", "DevolucionContenedor", "Devolución de contenedor")]
    [InlineData("Distribucion", "Distribucion", null)]
    [InlineData("IMPORTACION", "Importacion", "Puerto a interior")]
    public async Task LaLineaDeNegocioSeTraduceALaDeCotiflyYTraeLaLineaDeNexo(string deNexo, string deCotifly, string? lineaNexo)
    {
        NexoResponde(1, [Importada(2, lineaNegocio: deNexo)]);

        var resultado = await Aplicar(Entrada(filas: 1));

        var linea = resultado.Valor.Lineas.Single();
        (linea.LineaNegocio, linea.LineaNexo).Should().Be((deCotifly, lineaNexo));
        resultado.Valor.Errores.Should().BeEmpty();
    }

    [Fact]
    public async Task SinLineaDeNegocioNoLeeLasLineasYQuedaNull()
    {
        NexoResponde(1, [Importada(2, lineaNegocio: "  ")]);

        var resultado = await Aplicar(Entrada(filas: 1));

        var linea = resultado.Valor.Lineas.Single();
        (linea.LineaNegocio, linea.LineaNexo).Should().Be((null, null));
        await _politicas.DidNotReceive().ListarAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnaLineaSinEquivalenciaVaEnLineasYEnErroresConSuLineaDeNexo()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Linea("Nacional", "Nacional"), Linea("Importacion", "PuertoAInterior"), Linea("ImpoEspecial", "Puerto a interior"),
            Linea("Exportacion", "InteriorAPuerto", activa: false),
        ]);
        NexoResponde(6,
        [
            Importada(2, lineaNegocio: "Licitaciones"), Importada(3, lineaNegocio: "Puerto a interior"), Importada(4, lineaNegocio: "Otra cosa"),
            Importada(5, lineaNegocio: "Exportacion"), Importada(6, lineaNegocio: "Nacional"),
        ],
        [new clsErrorDeFila(7, "origen", "LINEA_INCOMPLETA", "Falta el origen.")]);

        var resultado = await Aplicar(Entrada(filas: 6));

        // Todas las filas vuelven en lineas[], aun las que no resuelven su línea de negocio (solo la fila con error de NEXO se queda fuera).
        resultado.Valor.Lineas.Select(l => (l.Fila, l.LineaNegocio, l.LineaNexo)).Should().Equal(
            (0, null, "Licitaciones"), (1, null, "Puerto a interior"), (2, null, null), (3, null, null), (4, "Nacional", "Nacional"));
        resultado.Valor.Errores.Select(e => (e.Fila, e.FilaExcel, e.Campo, e.Codigo)).Should().Equal(
            (0, 2, "linea_negocio", "LINEA_NEGOCIO_SIN_EQUIVALENCIA"),
            (1, 3, "linea_negocio", "LINEA_NEGOCIO_SIN_EQUIVALENCIA"),
            (2, 4, "linea_negocio", "LINEA_NEGOCIO_SIN_EQUIVALENCIA"),
            (3, 5, "linea_negocio", "LINEA_NEGOCIO_SIN_EQUIVALENCIA"),
            (5, 7, "origen", "LINEA_INCOMPLETA"));
        resultado.Valor.Errores[1].Mensaje.Should().Contain("ImpoEspecial").And.Contain("Importacion");
        resultado.Valor.Errores[3].Mensaje.Should().Contain("inactiva");
        (resultado.Valor.FilasConErrores, resultado.Valor.TotalErrores).Should().Be((5, 5));
    }

    [Fact]
    public async Task LosCamposDeLosErroresDeNexoVanTalComoLosMandaNexo()
    {
        NexoResponde(3, [], [new clsErrorDeFila(3, "valorMercancia", "VALOR_INVALIDO", "x"), new clsErrorDeFila(2, "horasCargue", "VALOR_INVALIDO", "y")]);

        var resultado = await Aplicar(Entrada(filas: 3));

        resultado.Valor.Errores.Select(e => (e.FilaExcel, e.Campo)).Should().Equal((2, "horasCargue"), (3, "valorMercancia"));
    }

    [Fact]
    public async Task LosConteosSonLosDeNexoMasLasFilasQueRechazaCotifly()
    {
        _nexo.AplicarMapeoAsync(_usuario, Arg.Any<clsPeticionAplicarMapeo>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoAplicarMapeo>.Exito(new clsResultadoAplicarMapeo(
                250, 10, 140, [Importada(2, lineaNegocio: "Licitaciones")], [new clsErrorDeFila(3, null, "X", "y")], 180)));

        var resultado = await Aplicar(Entrada(filas: 250));

        (resultado.Valor.TotalFilas, resultado.Valor.FilasVacias, resultado.Valor.FilasConErrores, resultado.Valor.TotalErrores)
            .Should().Be((250, 10, 141, 181));
        resultado.Valor.Lineas.Should().ContainSingle("la fila sin equivalencia también vuelve en lineas[]");
        resultado.Valor.Errores.Should().HaveCount(2, "NEXO devuelve hasta 100 errores en la lista; total_errores cuenta todos");
    }

    [Fact]
    public async Task CincoMilFilasLleganANexoYCincoMilUnaEsDemasiadasFilasSinLlamar()
    {
        NexoResponde(clsValidadorAplicacionMapeo.MaximoFilas, []);

        var tope = await Aplicar(Entrada(filas: clsValidadorAplicacionMapeo.MaximoFilas));
        var masUna = await Aplicar(Entrada(filas: clsValidadorAplicacionMapeo.MaximoFilas + 1));

        tope.EsExitoso.Should().BeTrue();
        _enviada!.Filas.Should().HaveCount(clsValidadorAplicacionMapeo.MaximoFilas);
        clsValidadorAplicacionMapeo.MaximoFilas.Should().Be(5000);
        masUna.Error.Codigo.Should().Be(clsCodigosError.DemasiadasFilas);
        await _nexo.Received(1).AplicarMapeoAsync(_usuario, Arg.Any<clsPeticionAplicarMapeo>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<string, clsEntradaAplicacionMapeo> EntradasInvalidas() => new()
    {
        { "CLIENTE_NIT_INVALIDO", Entrada(nit: "---") },
        { "MAPEO_REQUERIDO", Entrada() with { Columnas = [] } },
        { "MAPEO_REQUERIDO", Entrada() with { Columnas = null } },
        { "DEMASIADAS_COLUMNAS", Entrada() with { Columnas = [.. Enumerable.Range(0, 101).Select(i => new clsColumnaEntradaMapeo(i % 100, "ignorar", null))] } },
        { "COLUMNA_INVALIDA", Entrada() with { Columnas = [null] } },
        { "COLUMNA_INVALIDA", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(null, "origen", null)] } },
        { "COLUMNA_INVALIDA", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(0, "origen", null), new clsColumnaEntradaMapeo(0, "destino", null)] } },
        { "COLUMNA_FUERA_DE_RANGO", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(-1, "origen", null)] } },
        { "COLUMNA_FUERA_DE_RANGO", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(100, "origen", null)] } },
        { "CAMPO_INVALIDO", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(0, " ", null)] } },
        { "CAMPO_INVALIDO", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(0, "origen; drop", null)] } },
        { "CAMPO_INVALIDO", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(0, new string('a', 51), null)] } },
        { "CAMPO_DUPLICADO", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(0, "origen", null), new clsColumnaEntradaMapeo(1, "Origen", null)] } },
        { "TRANSFORMACION_INVALIDA", Entrada() with { Columnas = [new clsColumnaEntradaMapeo(0, "peso", "toneladas a kg")] } },
        { "EXCEL_SIN_DATOS", Entrada() with { Filas = [] } },
        { "EXCEL_SIN_DATOS", Entrada() with { Filas = null } },
        { "FILA_INVALIDA", Entrada() with { Filas = [["a", "b"], null] } },
        { "FILA_INVALIDA", Entrada(primeraFila: 1) },
        { "FILA_INVALIDA", Entrada(primeraFila: 1_048_576) },
        { "DEMASIADAS_COLUMNAS", Entrada() with { Filas = [[.. Enumerable.Repeat("x", 101)]] } },
    };

    [Theory]
    [MemberData(nameof(EntradasInvalidas))]
    public async Task UnaPeticionMalFormadaEs400SinLlamarANexo(string codigo, clsEntradaAplicacionMapeo entrada)
    {
        var resultado = await Aplicar(entrada);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigo);
        await _nexo.DidNotReceive().AplicarMapeoAsync(Arg.Any<Guid>(), Arg.Any<clsPeticionAplicarMapeo>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IgnorarSePuedeRepetirYLasCeldasNulasSonVacias()
    {
        NexoResponde(1, []);

        var resultado = await Aplicar(Entrada() with
        {
            Columnas = [new clsColumnaEntradaMapeo(0, "origen", " "), new clsColumnaEntradaMapeo(1, "ignorar", null), new clsColumnaEntradaMapeo(2, "IGNORAR", null)],
            Filas = [["Cali", null, "x"]],
        });

        resultado.EsExitoso.Should().BeTrue();
        _enviada!.Filas.Single().Should().Equal("Cali", string.Empty, "x");
        _enviada.Columnas[0].Transformacion.Should().BeNull();
    }

    [Theory]
    [InlineData(clsCodigosError.CampoDuplicado)]
    [InlineData(clsCodigosError.CampoRequerido)]
    [InlineData(clsCodigosError.IaTopeDiario)]
    [InlineData(clsCodigosError.NexoSinPermiso)]
    [InlineData(clsCodigosError.NexoNoDisponible)]
    public async Task UnErrorDeNexoSePropagaConSuCodigo(string codigo)
    {
        _nexo.AplicarMapeoAsync(_usuario, Arg.Any<clsPeticionAplicarMapeo>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoAplicarMapeo>.Fallo(new clsErrorDominio(codigo, "Mensaje de NEXO.")));

        var resultado = await Aplicar(Entrada());

        (resultado.Error.Codigo, resultado.Error.Mensaje).Should().Be((codigo, "Mensaje de NEXO."));
    }

    [Fact]
    public async Task LasFilasEnriquecidasConLasColumnasDerivadasViajanANexoYSeMapeanComoCualquierOtra()
    {
        // HU-044.9 (#292): aplicar-mapeo agrega la columna derivada al final de cada fila y el mapeo confirmado la referencia por índice.
        _columnas.EnriquecerFilasAsync(_usuario, Arg.Any<IReadOnlyList<IReadOnlyList<string?>?>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<IReadOnlyList<string?>?>)[.. ci.ArgAt<IReadOnlyList<IReadOnlyList<string?>?>>(1)
                .Select(f => (IReadOnlyList<string?>?)[.. f!, "Mula"])]);
        NexoResponde(1, [Importada(2)]);

        await Aplicar(Entrada(filas: 1) with
        {
            Columnas =
            [
                new clsColumnaEntradaMapeo(0, "origen", null), new clsColumnaEntradaMapeo(1, "destino", null),
                new clsColumnaEntradaMapeo(2, "peso", "peso_toneladas_a_kg"), new clsColumnaEntradaMapeo(3, "tipoVehiculo", null),
            ],
        });

        _enviada!.Filas.Single().Should().Equal("Origen 0", "Destino 0", "1,5", "Mula");
        _enviada.Columnas.Should().Contain(c => c.Indice == 3 && c.Campo == "tipoVehiculo");
    }

    [Fact]
    public async Task SinUsuarioEsErrorInterno()
    {
        var resultado = await Servicio().AplicarAsync(Guid.Empty, Entrada(), TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
    }

    [Fact]
    public async Task LaReferenciaYElDeadlineDeLaFilaSeDescartan()
    {
        NexoResponde(1, [Importada(2)]);

        var resultado = await Aplicar(Entrada(filas: 1));

        typeof(clsLineaMapeada).GetProperties().Select(p => p.Name).Should().NotContain(["Referencia", "Deadline"]);
        resultado.Valor.Lineas.Should().ContainSingle();
    }
}
