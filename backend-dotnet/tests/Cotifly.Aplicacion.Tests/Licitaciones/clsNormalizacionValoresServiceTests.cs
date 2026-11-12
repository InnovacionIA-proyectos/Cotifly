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
/// ADR-029: <c>normalizar-valores</c> valida en local con los límites de NEXO, reenvía por el puerto de la importación
/// de NEXO, devuelve un resultado por valor distinto y traduce la línea de negocio de NEXO a la de Cotifly (lista cerrada de la fase 0).
/// </summary>
public sealed class clsNormalizacionValoresServiceTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private readonly IClsImportacionExcelNexo _nexo = Substitute.For<IClsImportacionExcelNexo>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private IReadOnlyList<clsValorANormalizar>? _enviados;

    public clsNormalizacionValoresServiceTests()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns(
        [
            Linea("Nacional", "Nacional"), Linea("Distribucion", null), Linea("Importacion", "PuertoAInterior"),
            Linea("Exportacion", "InteriorAPuerto"), Linea("Internacional", "Andino"), Linea("DevolucionContenedor", "DevolucionContenedor"),
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
        new("900.123.456-7", [.. valores.Select(v => new clsValorEntradaNormalizacion(v.Campo, v.Valor))]);

    private static clsValorNormalizado DeNexo(string campo, string valor, string estado, string? codigo, string origen, double confianza = 1,
        int? municipio = null, IReadOnlyList<clsCandidatoCiudad>? candidatos = null, string? descripcion = null) =>
        new(campo, valor, estado, codigo, descripcion, municipio, origen, confianza, null, candidatos);

    private void NexoResponde(bool iaUsada, string? motivo, params clsValorNormalizado[] valores) =>
        _nexo.NormalizarValoresAsync(_usuario, Arg.Any<string?>(), Arg.Do<IReadOnlyList<clsValorANormalizar>>(v => _enviados = v), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoNormalizacion>.Exito(new clsResultadoNormalizacion(iaUsada, motivo, valores.Length, valores)));

    [Fact]
    public async Task ResuelvePorAliasYPorDiccionarioConElCampoEnElNombreDeCotifly()
    {
        NexoResponde(false, "SIN_PENDIENTES",
            DeNexo("tipoVehiculo", "TB", "resuelto", "2L2", "alias"),
            DeNexo("carroceria", "furgon", "resuelto", "FURGON", "diccionario"));

        var resultado = await Normalizar(Entrada(("tipo_vehiculo", "TB"), ("carroceria", "furgon")));

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.IaUsada.Should().BeFalse();
        resultado.Valor.MotivoSinIa.Should().Be("SIN_PENDIENTES");
        resultado.Valor.Valores.Select(v => (v.Campo, v.Valor, v.Estado, v.Codigo, v.Origen)).Should().Equal(
            ("tipo_vehiculo", "TB", "resuelto", "2L2", "alias"), ("carroceria", "furgon", "resuelto", "FURGON", "diccionario"));
        _enviados!.Select(v => (v.Campo, v.Valor)).Should().Equal(("tipoVehiculo", "TB"), ("carroceria", "furgon"));
        await _nexo.Received(1).NormalizarValoresAsync(_usuario, "900.123.456-7", Arg.Any<IReadOnlyList<clsValorANormalizar>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnaCiudadAmbiguaTraeLosCandidatosYUnaResueltaSuMunicipio()
    {
        clsCandidatoCiudad[] candidatos = [new(5400, "05400", "La Unión", "Antioquia"), new(52399, "52399", "La Unión", "Nariño")];
        NexoResponde(true, null,
            DeNexo("ciudad", "La Union", "ambiguo", null, "catalogo", 0.5, candidatos: candidatos),
            DeNexo("ciudad", "BUENAVENTURA", "resuelto", "76109", "catalogo", municipio: 76109, descripcion: "Buenaventura"));

        var resultado = await Normalizar(Entrada(("ciudad", "La Union"), ("ciudad", "BUENAVENTURA")));

        var ambigua = resultado.Valor.Valores[0];
        (ambigua.Estado, ambigua.MunicipioId).Should().Be(("ambiguo", (int?)null));
        ambigua.Candidatos!.Select(c => c.MunicipioId).Should().Equal(5400, 52399);
        var resuelta = resultado.Valor.Valores[1];
        (resuelta.Estado, resuelta.MunicipioId, resuelta.Descripcion, resuelta.Candidatos).Should().Be(("resuelto", (int?)76109, "Buenaventura", (IReadOnlyList<clsCandidatoCiudad>?)null));
    }

    [Fact]
    public async Task SinResolverYSinRespuestaDeNexoSalenSinResolver()
    {
        NexoResponde(true, null, DeNexo("tipoVehiculo", "Zeppelin", "sin_resolver", null, "ninguno", 0));

        var resultado = await Normalizar(Entrada(("tipo_vehiculo", "Zeppelin"), ("carroceria", "Plataforma rara")));

        resultado.Valor.Valores.Select(v => (v.Estado, v.Codigo, v.Origen)).Should().Equal(("sin_resolver", null, "ninguno"), ("sin_resolver", null, "ninguno"));
        resultado.Valor.Valores[1].Nota.Should().Be(clsNormalizacionValoresService.NotaSinRespuestaNexo);
    }

    [Fact]
    public async Task ConLaIaDeNexoNoDisponibleDevuelveLoQueResolvieronLasReglas()
    {
        NexoResponde(false, "IA_NO_DISPONIBLE",
            DeNexo("tipoVehiculo", "Tractomula", "resuelto", "3S3", "diccionario"), DeNexo("tipoVehiculo", "Cosa", "sin_resolver", null, "ninguno", 0));

        var resultado = await Normalizar(Entrada(("tipo_vehiculo", "Tractomula"), ("tipo_vehiculo", "Cosa")));

        resultado.EsExitoso.Should().BeTrue();
        (resultado.Valor.IaUsada, resultado.Valor.MotivoSinIa).Should().Be((false, "IA_NO_DISPONIBLE"));
        resultado.Valor.Valores.Select(v => v.Estado).Should().Equal("resuelto", "sin_resolver");
    }

    [Fact]
    public async Task UnErrorDeNexoSePropagaConSuCodigo()
    {
        _nexo.NormalizarValoresAsync(_usuario, Arg.Any<string?>(), Arg.Any<IReadOnlyList<clsValorANormalizar>>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoNormalizacion>.Fallo(new clsErrorDominio(clsCodigosError.IaTopeDiario, "Agotaste tu cupo diario de IA.")));

        var resultado = await Normalizar(Entrada(("ciudad", "Cali")));

        resultado.Error.Codigo.Should().Be(clsCodigosError.IaTopeDiario);
    }

    [Fact]
    public async Task UnaLineaDeCotifly_ComoDistribucion_SeResuelveSinNexo()
    {
        var resultado = await Normalizar(Entrada(("linea_negocio", "Distribución"), ("linea_negocio", "IMPORTACION")));

        resultado.Valor.Valores.Select(v => (v.Estado, v.Codigo, v.Origen, v.LineaNexo)).Should().Equal(
            ("resuelto", "Distribucion", "linea_cotifly", null), ("resuelto", "Importacion", "linea_cotifly", "Puerto a interior"));
        (resultado.Valor.IaUsada, resultado.Valor.MotivoSinIa).Should().Be((false, "SIN_PENDIENTES"));
        await _nexo.DidNotReceiveWithAnyArgs().NormalizarValoresAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnaLineaDeNexoSeTraduceALaDeCotifly()
    {
        NexoResponde(true, null,
            DeNexo("lineaNegocio", "impo", "resuelto", "puertoAInterior", "sinonimo"),
            DeNexo("lineaNegocio", "Outbound", "resuelto", "Interior a puerto", "ia", 0.7),
            DeNexo("lineaNegocio", "Pliego", "resuelto", "Licitaciones", "diccionario"),
            DeNexo("lineaNegocio", "Rara", "resuelto", "Cabotaje", "ia", 0.6),
            DeNexo("lineaNegocio", "Inbound", "sin_resolver", null, "ninguno", 0));

        var resultado = await Normalizar(
            Entrada(("linea_negocio", "impo"), ("linea_negocio", "Outbound"), ("linea_negocio", "Pliego"), ("linea_negocio", "Rara"), ("linea_negocio", "Inbound")));

        resultado.Valor.Valores.Select(v => (v.Estado, v.Codigo, v.Origen, v.LineaNexo)).Should().Equal(
            ("resuelto", "Importacion", "sinonimo", "Puerto a interior"),
            ("resuelto", "Exportacion", "ia", "Interior a puerto"),
            ("sin_resolver", null, "diccionario", "Licitaciones"),
            ("sin_resolver", null, "ia", null),
            ("sin_resolver", null, "ninguno", null));
        resultado.Valor.Valores[2].Nota.Should().Contain("catálogo de Nexo");
        resultado.Valor.Valores[3].Nota.Should().Contain("Cabotaje");
        _enviados!.Select(v => v.Campo).Should().AllBe("lineaNegocio");
    }

    [Fact]
    public async Task DosLineasDeCotifly_ConLaMismaLineaDeNexo_SonAmbiguas()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns([Linea("Nacional", "Nacional"), Linea("Nacional Especial", "Nacional"), Linea("Vieja", "Nacional", activa: false)]);
        NexoResponde(true, null, DeNexo("lineaNegocio", "Local", "resuelto", "nacional", "ia", 0.8));

        var valor = (await Normalizar(Entrada(("linea_negocio", "Local")))).Valor.Valores[0];

        (valor.Estado, valor.Codigo, valor.LineaNexo).Should().Be(("ambiguo", (string?)null, "Nacional"));
        valor.LineasCandidatas.Should().Equal("Nacional", "Nacional Especial");
    }

    [Fact]
    public async Task UnaLineaDeCotifly_Inactiva_QuedaSinResolverSinNexo()
    {
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns([Linea("Distribucion", null, activa: false)]);

        var valor = (await Normalizar(Entrada(("linea_negocio", "distribucion")))).Valor.Valores[0];

        (valor.Estado, valor.Codigo, valor.Origen).Should().Be(("sin_resolver", (string?)null, "linea_cotifly"));
        valor.Nota.Should().Contain("inactiva");
        await _nexo.DidNotReceiveWithAnyArgs().NormalizarValoresAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LosRepetidosSeEnvianUnaVezYNexoSeEmparejaSinTildesNiMayusculas()
    {
        NexoResponde(true, null, DeNexo("ciudad", "BOGOTA", "resuelto", "11001", "catalogo", municipio: 11001));

        var resultado = await Normalizar(Entrada(("ciudad", "Bogotá"), ("ciudad", " Bogotá "), ("ciudad", "Bogotá")));

        _enviados!.Should().ContainSingle().Which.Valor.Should().Be("Bogotá");
        resultado.Valor.Valores.Should().ContainSingle().Which.MunicipioId.Should().Be(11001);
    }

    [Theory]
    [InlineData(null, "x", "CAMPO_INVALIDO")]
    [InlineData("clase_contenedor", "40HC", "CAMPO_INVALIDO")]
    [InlineData("tipoVehiculo", "TB", "CAMPO_INVALIDO")]
    [InlineData("ciudad", "  ", "VALORES_REQUERIDOS")]
    public async Task UnValorInvalidoSeRechazaSinLlamarANexo(string? campo, string? valor, string codigo)
    {
        var resultado = await Normalizar(Entrada(("ciudad", "Cali"), (campo, valor)));

        resultado.Error.Codigo.Should().Be(codigo);
        resultado.Error.Mensaje.Should().Contain("valor 2");
        await _nexo.DidNotReceiveWithAnyArgs().NormalizarValoresAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LaClaseDeContenedorRemiteAlResolverDeAlias()
    {
        var resultado = await Normalizar(Entrada(("clase_contenedor", "40HC")));

        resultado.Error.Mensaje.Should().Contain("/web/alias/resolver");
    }

    [Fact]
    public async Task LosLimitesSonLosDeNexo()
    {
        var sinValores = await Normalizar(new clsEntradaNormalizacion(null, []));
        var demasiados = await Normalizar(
            Entrada([.. Enumerable.Range(0, clsValidadorNormalizacionValores.MaximoValores + 1).Select(i => ((string?)"ciudad", (string?)$"C{i}"))]));
        var largo = await Normalizar(Entrada(("ciudad", new string('a', clsValidadorNormalizacionValores.LargoMaximoValor + 1))));
        var justo = clsValidadorNormalizacionValores.Validar(Entrada([.. Enumerable.Range(0, 1000).Select(i => ((string?)"ciudad", (string?)new string('a', 150 - 4) + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)))]));

        (sinValores.Error.Codigo, demasiados.Error.Codigo, largo.Error.Codigo).Should().Be((clsCodigosError.ValoresRequeridos, clsCodigosError.DemasiadosValores, clsCodigosError.ValorDemasiadoLargo));
        justo.EsExitoso.Should().BeTrue();
        justo.Valor.Should().HaveCount(1000);
    }

    [Theory]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("---")]
    public async Task UnNitInvalidoSeRechazaSiempre_AunqueTodoSeResuelvaEnCotifly(string nit)
    {
        var resultado = await Normalizar(new clsEntradaNormalizacion(nit, [new clsValorEntradaNormalizacion("linea_negocio", "Nacional")]));

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteNitInvalido);
        await _nexo.DidNotReceiveWithAnyArgs().NormalizarValoresAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnNitConPuntosYDigitoDeVerificacionEsValido()
    {
        var resultado = await Normalizar(new clsEntradaNormalizacion("900.123.456-7", [new clsValorEntradaNormalizacion("linea_negocio", "Nacional")]));

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task NexoAmbiguoEnUnaLineaConUnaSolaLineaDeCotifly_EsResuelto()
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "Puerto", "ambiguo", "PuertoAInterior", "ia", 0.6));

        var valor = (await Normalizar(Entrada(("linea_negocio", "Puerto")))).Valor.Valores[0];

        (valor.Estado, valor.Codigo, valor.LineasCandidatas).Should().Be(("resuelto", (string?)"Importacion", (IReadOnlyList<string>?)null));
    }

    [Fact]
    public async Task UnEstadoDesconocidoDeNexoEsSinResolverYLaConfianzaSeAcota()
    {
        NexoResponde(true, null,
            DeNexo("tipoVehiculo", "TB", "pendiente", "2L2", "alias", 0.9),
            DeNexo("carroceria", "furgon", "resuelto", "FURGON", "diccionario", 1.7),
            DeNexo("ciudad", "Cali", "resuelto", "76001", "catalogo", -0.2, municipio: 76001),
            DeNexo("lineaNegocio", "impo", "otro", "PuertoAInterior", "sinonimo", 1));

        var valores = (await Normalizar(Entrada(("tipo_vehiculo", "TB"), ("carroceria", "furgon"), ("ciudad", "Cali"), ("linea_negocio", "impo")))).Valor.Valores;

        valores.Select(v => (v.Estado, v.Confianza)).Should().Equal(("sin_resolver", 0d), ("resuelto", 1d), ("resuelto", 0d), ("sin_resolver", 0d));
        valores[0].Codigo.Should().BeNull("un estado que Cotifly no conoce no se propone");
    }

    [Fact]
    public async Task LicitacionesDeNexoExplicaQueEsUnaLineaDeNexo()
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "Pliego", "resuelto", "Licitaciones", "diccionario"));

        var valor = (await Normalizar(Entrada(("linea_negocio", "Pliego")))).Valor.Valores[0];

        valor.Estado.Should().Be("sin_resolver");
        valor.Nota.Should().Contain("catálogo de Nexo").And.Contain("Usa una de estas");
    }

    [Fact]
    public async Task UnaLineaDeCotifly_ConUnMapeoFueraDeLasSeis_QuedaSinResolverConElMotivo()
    {
        var linea = Linea("Cabotaje", "Nacional");
        typeof(clsMargenPolitica).GetProperty(nameof(clsMargenPolitica.MapeoLineasNexo))!.SetValue(linea, "Cabotaje");
        _politicas.ListarAsync(Arg.Any<CancellationToken>()).Returns([linea]);

        var valor = (await Normalizar(Entrada(("linea_negocio", "cabotaje")))).Valor.Valores[0];

        (valor.Estado, valor.Codigo, valor.Origen).Should().Be(("sin_resolver", (string?)null, "linea_cotifly"));
        valor.Nota.Should().Contain("Cabotaje");
        await _nexo.DidNotReceiveWithAnyArgs().NormalizarValoresAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnaLineaResueltaSinOrigenDeNexoLlegaConOrigenNinguno()
    {
        NexoResponde(true, null, DeNexo("lineaNegocio", "impo", "resuelto", "PuertoAInterior", ""));

        var valor = (await Normalizar(Entrada(("linea_negocio", "impo")))).Valor.Valores[0];

        (valor.Estado, valor.Codigo, valor.Origen).Should().Be(("resuelto", (string?)"Importacion", "ninguno"));
    }
}
