using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// Validación adversarial de HU-044.4 (#255): límites (0/1/1000/1001 valores, 150/151 caracteres con tildes, tildes descompuestas y
/// emojis), repetidos, campos raros, respuestas extrañas de NEXO (desordenadas, de más, de menos, de otro campo, sin estado) y la
/// traducción de la línea de negocio a 0, 1 o varias líneas de Cotifly.
/// </summary>
public sealed class clsNormalizacionValoresAdversarialTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private static readonly string[] _estados = ["resuelto", "ambiguo", "sin_resolver"];
    private readonly IClsImportacionExcelNexo _nexo = Substitute.For<IClsImportacionExcelNexo>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private IReadOnlyList<clsValorANormalizar>? _enviados;

    public clsNormalizacionValoresAdversarialTests()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Linea("Nacional", "Nacional"), Linea("Distribucion", null), Linea("Importacion", "PuertoAInterior"),
            Linea("Exportacion", "InteriorAPuerto"), Linea("Internacional", "Andino"),
        ]);
    }

    private Task<clsResultado<clsNormalizacionValores>> Normalizar(clsEntradaNormalizacion entrada) =>
        new clsNormalizacionValoresService(_nexo, _politicas).NormalizarAsync(_usuario, entrada, TestContext.Current.CancellationToken);

    private static clsMargenPolitica Linea(string nombre, string? mapeo, bool activa = true)
    {
        var linea = clsMargenPolitica.Crear(nombre, 0.15m, mapeoLineasNexo: mapeo).Valor;
        if (!activa)
        {
            linea.Desactivar();
        }

        return linea;
    }

    private static clsEntradaNormalizacion Entrada(params (string? Campo, string? Valor)[] valores) =>
        new(null, [.. valores.Select(v => new clsValorEntradaNormalizacion(v.Campo, v.Valor))]);

    private static clsValorNormalizado DeNexo(string campo, string valor, string estado, string? codigo, string origen, double confianza = 1,
        int? municipio = null, string? descripcion = null) =>
        new(campo, valor, estado, codigo, descripcion, municipio, origen, confianza, null, null);

    private void NexoResponde(bool iaUsada, string? motivo, params clsValorNormalizado[] valores) =>
        _nexo.NormalizarValoresAsync(_usuario, Arg.Any<string?>(), Arg.Do<IReadOnlyList<clsValorANormalizar>>(v => _enviados = v), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoNormalizacion>.Exito(new clsResultadoNormalizacion(iaUsada, motivo, valores.Length, valores)));

    private Task NoLlamoANexo() =>
        _nexo.DidNotReceiveWithAnyArgs().NormalizarValoresAsync(default, default, default!, TestContext.Current.CancellationToken);

    // ---------- Límites ----------

    [Fact]
    public async Task UnSoloValorEsValido_YCeroValoresONullSonValoresRequeridos()
    {
        NexoResponde(true, null, DeNexo("ciudad", "Cali", "resuelto", "76001", "catalogo", municipio: 76001));

        (await Normalizar(Entrada(("ciudad", "Cali")))).Valor.Valores.Should().ContainSingle();
        (await Normalizar(new clsEntradaNormalizacion(null, null))).Error.Codigo.Should().Be(clsCodigosError.ValoresRequeridos);
        (await Normalizar(new clsEntradaNormalizacion(null, []))).Error.Codigo.Should().Be(clsCodigosError.ValoresRequeridos);
    }

    [Fact]
    public async Task MilValoresDistintosSeEnvianTodosANexo_YMilUnoNoLlama()
    {
        NexoResponde(true, null);
        var mil = Enumerable.Range(0, 1000).Select(i => ((string?)"ciudad", (string?)$"Ciudad {i}")).ToArray();

        var resultado = await Normalizar(Entrada(mil));
        resultado.EsExitoso.Should().BeTrue();
        _enviados.Should().HaveCount(1000);
        resultado.Valor.Valores.Should().HaveCount(1000).And.OnlyContain(v => v.Estado == "sin_resolver");

        _nexo.ClearReceivedCalls();
        var milUno = await Normalizar(Entrada([.. mil, ("ciudad", "Otra")]));
        milUno.Error.Codigo.Should().Be(clsCodigosError.DemasiadosValores);
        await NoLlamoANexo();
    }

    [Fact]
    public async Task MilUnRepetidosTambienSonDemasiados_ElLimiteEsSobreLoQueLlega()
    {
        // Conservador: el límite cuenta lo que manda el front (que debe mandar solo los distintos), no lo que quedaría tras quitar repetidos.
        var resultado = await Normalizar(Entrada([.. Enumerable.Repeat(((string?)"ciudad", (string?)"Cali"), 1001)]));

        resultado.Error.Codigo.Should().Be(clsCodigosError.DemasiadosValores);
        await NoLlamoANexo();
    }

    [Theory]
    [InlineData('a', 150, true)]
    [InlineData('a', 151, false)]
    [InlineData('á', 150, true)] // tilde precompuesta (NFC): 1 unidad UTF-16, 2 bytes UTF-8
    [InlineData('á', 151, false)]
    [InlineData('ñ', 150, true)]
    public void ElLargoCuentaCaracteresNoBytes(char c, int largo, bool valido)
    {
        var resultado = clsValidadorNormalizacionValores.Validar(Entrada(("ciudad", new string(c, largo))));

        resultado.EsExitoso.Should().Be(valido);
        if (!valido)
        {
            resultado.Error.Codigo.Should().Be(clsCodigosError.ValorDemasiadoLargo);
        }
    }

    [Fact]
    public void ConEmojisOTildesDescompuestasCuentaUnidadesUtf16()
    {
        // Comportamiento documentado (observación para el informe): string.Length cuenta unidades UTF-16. Un emoji vale 2 y una tilde en NFD
        // (e + U+0301) también. 75 emojis (150 unidades) pasan; 76 (152) no, aunque se vean 76 «caracteres».
        var emoji = "\U0001F69A";
        clsValidadorNormalizacionValores.Validar(Entrada(("ciudad", string.Concat(Enumerable.Repeat(emoji, 75))))).EsExitoso.Should().BeTrue();
        clsValidadorNormalizacionValores.Validar(Entrada(("ciudad", string.Concat(Enumerable.Repeat(emoji, 76))))).Error.Codigo
            .Should().Be(clsCodigosError.ValorDemasiadoLargo);
        clsValidadorNormalizacionValores.Validar(Entrada(("ciudad", string.Concat(Enumerable.Repeat("é", 76))))).Error.Codigo
            .Should().Be(clsCodigosError.ValorDemasiadoLargo);
    }

    [Fact]
    public void LosEspaciosDeLosExtremosNoCuentanParaElLargo_YSeEnviaRecortado()
    {
        var resultado = clsValidadorNormalizacionValores.Validar(Entrada(("ciudad", "   " + new string('x', 150) + "\t ")));

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Single().Valor.Should().HaveLength(150);
    }

    [Fact]
    public void ElErrorDeLargoIndicaLaPosicion1000()
    {
        var valores = Enumerable.Range(0, 999).Select(i => ((string?)"ciudad", (string?)$"C{i}"))
            .Append(("ciudad", new string('z', 151))).ToArray();

        clsValidadorNormalizacionValores.Validar(Entrada(valores)).Error.Mensaje.Should().Contain("valor 1000");
    }

    // ---------- Campos ----------

    [Theory]
    [InlineData("Ciudad")]
    [InlineData("CIUDAD")]
    [InlineData("tipo-vehiculo")]
    [InlineData("lineaNegocio")]
    [InlineData("")]
    public async Task UnCampoQueNoEsExactamenteElDeCotiflyEsCampoInvalido(string campo)
    {
        var resultado = await Normalizar(Entrada((campo, "x")));

        resultado.Error.Codigo.Should().Be(clsCodigosError.CampoInvalido);
        await NoLlamoANexo();
    }

    [Theory]
    [InlineData("CLASE_CONTENEDOR")]
    [InlineData("claseContenedor")]
    [InlineData(" contenedor ")]
    public async Task CualquierFormaDeLaClaseDeContenedorRemiteAlResolverDeAlias(string campo)
    {
        var resultado = await Normalizar(Entrada((campo, "40HC")));

        resultado.Error.Codigo.Should().Be(clsCodigosError.CampoInvalido);
        resultado.Error.Mensaje.Should().Contain("/web/alias/resolver");
    }

    [Fact]
    public async Task UnCampoConEspaciosEnLosExtremosSeAcepta()
    {
        NexoResponde(true, null, DeNexo("ciudad", "Cali", "resuelto", "76001", "catalogo", municipio: 76001));

        var resultado = await Normalizar(Entrada((" ciudad ", "Cali")));

        resultado.Valor.Valores.Single().Campo.Should().Be("ciudad");
        _enviados!.Single().Campo.Should().Be("ciudad");
    }

    [Fact]
    public async Task UnElementoNuloEnLaListaEsCampoInvalidoConSuPosicion()
    {
        var resultado = await Normalizar(new clsEntradaNormalizacion(null, [new("ciudad", "Cali"), null]));

        resultado.Error.Codigo.Should().Be(clsCodigosError.CampoInvalido);
        resultado.Error.Mensaje.Should().Contain("valor 2");
    }

    // ---------- Repetidos ----------

    [Fact]
    public async Task RepetidosConDistintaMayusculaSeEnvianPorSeparadoYCadaUnoTraeSuResultado()
    {
        // NEXO agrupa los textos que para él son iguales y devuelve uno solo: los tres deben salir con ese resultado.
        NexoResponde(true, null, DeNexo("ciudad", "Cali", "resuelto", "76001", "catalogo", municipio: 76001));

        var resultado = await Normalizar(Entrada(("ciudad", "Cali"), ("ciudad", "CALI"), ("ciudad", " cali "), ("ciudad", "Cali  ")));

        _enviados!.Select(v => v.Valor).Should().Equal("Cali", "CALI", "cali");
        resultado.Valor.Valores.Select(v => (v.Valor, v.MunicipioId)).Should().Equal(("Cali", 76001), ("CALI", 76001), ("cali", 76001));
    }

    [Fact]
    public async Task ElMismoTextoEnDosCamposNoEsRepetido()
    {
        NexoResponde(true, null,
            DeNexo("carroceria", "Sencillo", "resuelto", "ESTACAS", "ia", 0.6),
            DeNexo("tipoVehiculo", "Sencillo", "resuelto", "C2", "alias"));

        var resultado = await Normalizar(Entrada(("tipo_vehiculo", "Sencillo"), ("carroceria", "Sencillo")));

        resultado.Valor.Valores.Select(v => (v.Campo, v.Codigo)).Should().Equal(("tipo_vehiculo", "C2"), ("carroceria", "ESTACAS"));
    }

    // ---------- Respuestas raras de NEXO ----------

    [Fact]
    public async Task NexoDesordenadoSeDevuelveEnElOrdenDeLaPeticion()
    {
        NexoResponde(true, null,
            DeNexo("ciudad", "C", "resuelto", "3", "catalogo", municipio: 3),
            DeNexo("ciudad", "B", "resuelto", "2", "catalogo", municipio: 2),
            DeNexo("ciudad", "A", "resuelto", "1", "catalogo", municipio: 1));

        var resultado = await Normalizar(Entrada(("ciudad", "A"), ("ciudad", "B"), ("ciudad", "C")));

        resultado.Valor.Valores.Select(v => (v.Valor, v.MunicipioId)).Should().Equal(("A", 1), ("B", 2), ("C", 3));
    }

    [Fact]
    public async Task ValoresQueNoSePidieronSeIgnoran()
    {
        NexoResponde(true, null,
            DeNexo("ciudad", "Medellin", "resuelto", "05001", "catalogo", municipio: 5001),
            DeNexo("ciudad", "Pereira", "resuelto", "66001", "catalogo", municipio: 66001),
            DeNexo("tipoVehiculo", "TB", "resuelto", "2L2", "alias"));

        var resultado = await Normalizar(Entrada(("ciudad", "Medellin")));

        resultado.Valor.Valores.Should().ContainSingle().Which.MunicipioId.Should().Be(5001);
    }

    [Fact]
    public async Task UnValorQueNexoNoDevolvioSaleSinResolverYLosDemasConservanSuResultado()
    {
        NexoResponde(true, null, DeNexo("ciudad", "B", "resuelto", "2", "catalogo", municipio: 2));

        var resultado = await Normalizar(Entrada(("ciudad", "A"), ("ciudad", "B"), ("ciudad", "C")));

        resultado.Valor.Valores.Select(v => (v.Estado, v.Origen, v.MunicipioId)).Should().Equal(
            ("sin_resolver", "ninguno", null), ("resuelto", "catalogo", 2), ("sin_resolver", "ninguno", null));
        resultado.Valor.Valores[0].Nota.Should().Be(clsNormalizacionValoresService.NotaSinRespuestaNexo);
    }

    [Fact]
    public async Task UnaRespuestaVaciaDeNexoDejaTodoSinResolver()
    {
        NexoResponde(true, null);

        var resultado = await Normalizar(Entrada(("ciudad", "A"), ("tipo_vehiculo", "B")));

        resultado.Valor.Valores.Should().HaveCount(2).And.OnlyContain(v => v.Estado == "sin_resolver" && v.Codigo == null);
    }

    [Fact]
    public async Task ElMismoTextoDevueltoParaOtroCampoNoSeEmpareja()
    {
        NexoResponde(true, null, DeNexo("carroceria", "Cali", "resuelto", "FURGON", "ia", 0.5));

        var valor = (await Normalizar(Entrada(("ciudad", "Cali")))).Valor.Valores.Single();

        (valor.Estado, valor.Codigo).Should().Be(("sin_resolver", (string?)null));
    }

    [Fact]
    public async Task UnCampoDeNexoEnOtraMayusculaSeEmpareja()
    {
        NexoResponde(true, null, DeNexo("TIPOVEHICULO", "TB", "resuelto", "2L2", "alias"));

        var valor = (await Normalizar(Entrada(("tipo_vehiculo", "TB")))).Valor.Valores.Single();

        (valor.Campo, valor.Codigo).Should().Be(("tipo_vehiculo", "2L2"));
    }

    [Fact]
    public async Task ConDosResultadosParaElMismoTextoGanaElExacto()
    {
        NexoResponde(true, null,
            DeNexo("ciudad", "bogota", "resuelto", "X", "ia", 0.3, municipio: 999),
            DeNexo("ciudad", "Bogotá", "resuelto", "11001", "catalogo", municipio: 11001));

        var valor = (await Normalizar(Entrada(("ciudad", "Bogotá")))).Valor.Valores.Single();

        valor.MunicipioId.Should().Be(11001);
    }

    [Fact]
    public async Task UnValorSinLetrasNiDigitosSoloSeEmparejaExacto()
    {
        // Clave vacía: no se usa el emparejamiento flexible (si no, «???» tomaría el resultado de cualquier otro texto sin letras).
        NexoResponde(true, null, DeNexo("ciudad", "!!!", "resuelto", "1", "ia", 0.1, municipio: 1));

        var resultado = await Normalizar(Entrada(("ciudad", "???"), ("ciudad", "\U0001F69A")));

        resultado.Valor.Valores.Should().OnlyContain(v => v.Estado == "sin_resolver");
    }

    [Fact]
    public async Task UnEstadoYUnOrigenVaciosDeNexoEnVehiculoSalenComoSinResolverYNinguno()
    {
        NexoResponde(true, null, DeNexo("tipoVehiculo", "TB", "", "2L2", ""));

        var valor = (await Normalizar(Entrada(("tipo_vehiculo", "TB")))).Valor.Valores.Single();

        (valor.Estado, valor.Origen).Should().Be(("sin_resolver", "ninguno"));
    }

    [Fact]
    public async Task UnEstadoYUnOrigenVaciosDeNexoEnLineaDeNegocioRespetanElEnum()
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "impo", "", "PuertoAInterior", ""));

        var valor = (await Normalizar(Entrada(("linea_negocio", "impo")))).Valor.Valores.Single();

        _estados.Should().Contain(valor.Estado);
        valor.Origen.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UnaCiudadAmbiguaSinCandidatosLlegaComoAmbiguaConCandidatosNulos()
    {
        NexoResponde(true, null, new clsValorNormalizado("ciudad", "La Union", "ambiguo", null, null, null, "catalogo", 0.5, null, []));

        var valor = (await Normalizar(Entrada(("ciudad", "La Union")))).Valor.Valores.Single();

        (valor.Estado, valor.Candidatos).Should().Be(("ambiguo", (IReadOnlyList<clsCandidatoCiudad>?)null));
    }

    // ---------- Línea de negocio ----------

    [Theory]
    [InlineData("importación")]
    [InlineData("IMPORTACIÓN")]
    [InlineData("  Importacion  ")]
    public async Task UnTextoQueYaEsLineaDeCotiflySeResuelveSinNexo(string texto)
    {
        var valor = (await Normalizar(Entrada(("linea_negocio", texto)))).Valor.Valores.Single();

        (valor.Estado, valor.Codigo, valor.Origen, valor.LineaNexo).Should().Be(("resuelto", "Importacion", "linea_cotifly", "Puerto a interior"));
        await NoLlamoANexo();
    }

    [Fact]
    public async Task ElNombreDeUnaLineaDeNexoNoEsLineaDeCotifly_VaANexoYSeTraduce()
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "Puerto a interior", "resuelto", "Puerto a interior", "catalogo"));

        var valor = (await Normalizar(Entrada(("linea_negocio", "Puerto a interior")))).Valor.Valores.Single();

        (valor.Estado, valor.Codigo, valor.LineaNexo).Should().Be(("resuelto", "Importacion", "Puerto a interior"));
        _enviados!.Single().Campo.Should().Be("lineaNegocio");
    }

    [Fact]
    public async Task UnaLineaDeNexoQueSoloLlegaEnLaDescripcionTambienSeTraduce()
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "expo", "resuelto", null, "regla", descripcion: "Interior a puerto"));

        var valor = (await Normalizar(Entrada(("linea_negocio", "expo")))).Valor.Valores.Single();

        (valor.Estado, valor.Codigo).Should().Be(("resuelto", "Exportacion"));
    }

    [Fact]
    public async Task UnaLineaDeNexoSinLineaActivaEnCotifly_PorqueLaUnicaEstaInactiva_QuedaSinResolver()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns([Linea("Exportacion", "InteriorAPuerto", activa: false)]);
        NexoResponde(true, null, DeNexo("lineaNegocio", "Outbound", "resuelto", "Interior a puerto", "ia", 0.7));

        var valor = (await Normalizar(Entrada(("linea_negocio", "Outbound")))).Valor.Valores.Single();

        (valor.Estado, valor.Codigo, valor.LineaNexo).Should().Be(("sin_resolver", (string?)null, "Interior a puerto"));
    }

    [Fact]
    public async Task VariasLineasDeCotifly_SonCandidatasOrdenadasYSinLasInactivas()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns(
            [Linea("Zeta Andina", "Andino"), Linea("Alfa Andina", "andino"), Linea("Beta Andina", "ANDINO", activa: false)]);
        NexoResponde(true, null, DeNexo("lineaNegocio", "Ecuador", "resuelto", "Andino", "ia", 0.9));

        var valor = (await Normalizar(Entrada(("linea_negocio", "Ecuador")))).Valor.Valores.Single();

        (valor.Estado, valor.Codigo).Should().Be(("ambiguo", (string?)null));
        valor.LineasCandidatas.Should().Equal("Alfa Andina", "Zeta Andina");
    }

    [Theory]
    [InlineData("Cabotaje")]
    [InlineData("Puerto a interior; DROP TABLE")]
    [InlineData("")]
    public async Task UnaLineaDesconocidaDeNexoQuedaSinResolverSinInventarCodigo(string propuesta)
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "Rara", "resuelto", propuesta, "ia", 0.6));

        var valor = (await Normalizar(Entrada(("linea_negocio", "Rara")))).Valor.Valores.Single();

        (valor.Estado, valor.Codigo, valor.LineaNexo).Should().Be(("sin_resolver", (string?)null, (string?)null));
    }

    [Fact]
    public async Task UnAmbiguoDeNexoEnLineaDeNegocioNoQuedaAmbiguoSinCandidatos()
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "impo/expo", "ambiguo", "PuertoAInterior", "ia", 0.4));

        var valor = (await Normalizar(Entrada(("linea_negocio", "impo/expo")))).Valor.Valores.Single();

        if (valor.Estado == "ambiguo")
        {
            valor.LineasCandidatas.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task LaRespuestaDeLineaNoExponeElMargen()
    {
        var resultado = await Normalizar(Entrada(("linea_negocio", "Nacional")));

        System.Text.Json.JsonSerializer.Serialize(resultado.Valor).Should().NotContain("0.15").And.NotContain("Margen");
    }

    // ---------- ia_usada ----------

    [Fact]
    public async Task SoloLineasDeCotifly_IaUsadaFalseSinPendientes_YMixtoTomaElDeNexo()
    {
        var local = await Normalizar(Entrada(("linea_negocio", "Nacional"), ("linea_negocio", "distribución")));
        (local.Valor.IaUsada, local.Valor.MotivoSinIa).Should().Be((false, "SIN_PENDIENTES"));
        await NoLlamoANexo();

        NexoResponde(true, null, DeNexo("ciudad", "Cali", "resuelto", "76001", "ia", 0.8, municipio: 76001));
        var mixto = await Normalizar(Entrada(("linea_negocio", "Nacional"), ("ciudad", "Cali")));
        (mixto.Valor.IaUsada, mixto.Valor.MotivoSinIa).Should().Be((true, (string?)null));
        mixto.Valor.Valores.Select(v => v.Campo).Should().Equal("linea_negocio", "ciudad");
        _enviados!.Should().ContainSingle();
    }

    [Theory]
    [InlineData(clsCodigosError.NexoSinPermiso)]
    [InlineData(clsCodigosError.NexoNoDisponible)]
    [InlineData(clsCodigosError.ClienteNitInvalido)]
    public async Task LosErroresDeNexoSePropaganSinResultadosParciales(string codigo)
    {
        _nexo.NormalizarValoresAsync(_usuario, Arg.Any<string?>(), Arg.Any<IReadOnlyList<clsValorANormalizar>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoNormalizacion>.Fallo(new clsErrorDominio(codigo, "x")));

        var resultado = await Normalizar(Entrada(("linea_negocio", "Nacional"), ("ciudad", "Cali")));

        resultado.Error.Codigo.Should().Be(codigo);
    }

    [Fact]
    public async Task UnUsuarioVacioEsErrorInternoSinLlamarANexo()
    {
        var resultado = await new clsNormalizacionValoresService(_nexo, _politicas)
            .NormalizarAsync(Guid.Empty, Entrada(("ciudad", "Cali")), TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
        await NoLlamoANexo();
    }
}
