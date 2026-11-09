using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-044.2 (#253): sugerir-mapeo y leer-estructura son un puente hacia la importación inteligente de NEXO. La sugerencia nunca deja al
/// comercial sin pantalla (más de 100 columnas, importación apagada o NEXO caído → la hoja sin propuesta e <c>ia_usada = false</c>), pero
/// el tope diario de IA y los rechazos sí llegan como error. La lectura solo valida el archivo (sin abrirlo) y pasa la receta exacta.
/// </summary>
public sealed class clsSugerenciaMapeoServiceTests
{
    private static readonly Guid _usuario = Guid.NewGuid();
    private readonly IClsImportacionExcelNexo _nexo = Substitute.For<IClsImportacionExcelNexo>();

    private clsSugerenciaMapeoService Servicio() => new(_nexo);

    private static clsPeticionSugerirMapeo Peticion(int columnas, int filas = 2) => new(
        "900.555.111-2",
        [.. Enumerable.Range(0, columnas).Select(i => $"COL{i}")],
        [.. Enumerable.Range(0, filas).Select(f => (IReadOnlyList<string>)[.. Enumerable.Range(0, columnas).Select(c => $"{f}-{c}")])]);

    [Fact]
    public async Task ConMasDeCienColumnasNoLlamaANexoYLaHojaQuedaSinPropuesta()
    {
        var resultado = await Servicio().SugerirAsync(_usuario, Peticion(101), TestContext.Current.CancellationToken);

        var sugerencia = resultado.Valor;
        (sugerencia.IaUsada, sugerencia.MotivoSinIa, sugerencia.PlantillaId).Should().Be((false, "DEMASIADAS_COLUMNAS", (Guid?)null));
        sugerencia.Columnas.Should().HaveCount(101).And.OnlyContain(c => c.Campo == "ignorar" && c.Origen == "ninguno" && c.Confianza == 0
            && c.Transformacion == "ninguna" && c.Nota == null);
        sugerencia.Columnas[100].Should().Be(new clsColumnaMapeada(100, "COL100", "ignorar", "ninguna", 0, "ninguno", null));
        sugerencia.Dudas.Should().BeEmpty();
        await _nexo.DidNotReceiveWithAnyArgs().SugerirMapeoAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConCienColumnasSiLlamaANexo()
    {
        _nexo.SugerirMapeoAsync(_usuario, Arg.Any<clsPeticionSugerirMapeo>(), Arg.Any<CancellationToken>())
            .Returns(new clsSugerenciaMapeo(true, null, [], ["¿TON es el peso total?"], null));

        var resultado = await Servicio().SugerirAsync(_usuario, Peticion(100), TestContext.Current.CancellationToken);

        resultado.Valor.IaUsada.Should().BeTrue();
        await _nexo.Received(1).SugerirMapeoAsync(_usuario, Arg.Is<clsPeticionSugerirMapeo>(p => p.Encabezados.Count == 100), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("---")]
    [InlineData(".-.")]
    [InlineData("1234567890123456789012345678901")]
    public async Task ConUnNitInvalidoYMasDeCienColumnasGanaElNitInvalido(string nit)
    {
        // O1 (val253): el NIT se valida antes que el límite de columnas, con la regla de NEXO (antes del guion, letras y dígitos, máx. 30).
        var peticion = Peticion(101) with { ClienteNit = nit };

        var resultado = await Servicio().SugerirAsync(_usuario, peticion, TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteNitInvalido);
        await _nexo.DidNotReceiveWithAnyArgs().SugerirMapeoAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("900.555.111-2")]
    [InlineData("123456789012345678901234567890-1")]
    public async Task UnNitValidoOVacioNoCambiaElLimiteDeColumnas(string? nit)
    {
        var resultado = await Servicio().SugerirAsync(_usuario, Peticion(101) with { ClienteNit = nit }, TestContext.Current.CancellationToken);

        resultado.Valor.MotivoSinIa.Should().Be(clsCodigosError.DemasiadasColumnas);
    }

    [Fact]
    public async Task SinEncabezadosEsEncabezadosRequeridosSinLlamarANexo()
    {
        var resultado = await Servicio().SugerirAsync(_usuario, new clsPeticionSugerirMapeo(null, [], null), TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(clsCodigosError.EncabezadosRequeridos);
        await _nexo.DidNotReceiveWithAnyArgs().SugerirMapeoAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LaPropuestaDeNexoPasaTalCualConElNitYLasFilas()
    {
        var propuesta = new clsSugerenciaMapeo(false, "PLANTILLA_RECONOCIDA",
            [new clsColumnaMapeada(0, "COL0", "origen", "ninguna", 1, "plantilla", null)], [], Guid.NewGuid());
        clsPeticionSugerirMapeo? enviada = null;
        _nexo.SugerirMapeoAsync(_usuario, Arg.Do<clsPeticionSugerirMapeo>(p => enviada = p), Arg.Any<CancellationToken>()).Returns(propuesta);

        var resultado = await Servicio().SugerirAsync(_usuario, Peticion(1, filas: 3), TestContext.Current.CancellationToken);

        resultado.Valor.Should().BeSameAs(propuesta);
        enviada!.ClienteNit.Should().Be("900.555.111-2", "el adaptador normaliza el NIT como NEXO");
        enviada.Filas.Should().HaveCount(3);
    }

    [Fact]
    public async Task SoloSeReenvianLasPrimeras200FilasDeMuestra()
    {
        clsPeticionSugerirMapeo? enviada = null;
        _nexo.SugerirMapeoAsync(_usuario, Arg.Do<clsPeticionSugerirMapeo>(p => enviada = p), Arg.Any<CancellationToken>())
            .Returns(new clsSugerenciaMapeo(true, null, [], [], null));

        await Servicio().SugerirAsync(_usuario, Peticion(2, filas: 250), TestContext.Current.CancellationToken);

        enviada!.Filas.Should().HaveCount(clsSugerenciaMapeoService.MaximoFilasMuestra);
        enviada.Filas![199][0].Should().Be("199-0");
    }

    [Fact]
    public async Task ConNexoNoDisponibleLaHojaQuedaSinPropuestaYNoEsUnError()
    {
        _nexo.SugerirMapeoAsync(_usuario, Arg.Any<clsPeticionSugerirMapeo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "caído", "HTTP 503"));

        var resultado = await Servicio().SugerirAsync(_usuario, Peticion(3), TestContext.Current.CancellationToken);

        resultado.EsExitoso.Should().BeTrue();
        (resultado.Valor.IaUsada, resultado.Valor.MotivoSinIa).Should().Be((false, "NEXO_NO_DISPONIBLE"));
        resultado.Valor.Columnas.Select(c => (c.Indice, c.Columna, c.Campo)).Should().Equal((0, "COL0", "ignorar"), (1, "COL1", "ignorar"), (2, "COL2", "ignorar"));
    }

    [Theory]
    [InlineData(clsCodigosError.IaTopeDiario)]
    [InlineData(clsCodigosError.NexoSinPermiso)]
    [InlineData(clsCodigosError.ClienteNitInvalido)]
    [InlineData(clsCodigosError.CosteoNexoRechazada)]
    public async Task ElTopeDeIaYLosRechazosNoSeEsconden(string codigo)
    {
        _nexo.SugerirMapeoAsync(_usuario, Arg.Any<clsPeticionSugerirMapeo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "rechazo"));

        var resultado = await Servicio().SugerirAsync(_usuario, Peticion(3), TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(codigo);
    }

    [Theory]
    [InlineData(null, 0, clsCodigosError.ArchivoRequerido)]
    [InlineData(null, 10, clsCodigosError.FormatoNoSoportado)]
    [InlineData("pliego.xlsx", 0, clsCodigosError.ArchivoRequerido)]
    [InlineData("pliego.xls", 10, clsCodigosError.FormatoNoSoportado)]
    [InlineData("pliego.csv", 10, clsCodigosError.FormatoNoSoportado)]
    [InlineData("pliego.xlsx", clsArchivoExcel.TamanoMaximoBytes + 1, clsCodigosError.ArchivoDemasiadoGrande)]
    public async Task LaLecturaRechazaElArchivoSinLlamarANexo(string? nombre, int bytes, string codigo)
    {
        var entrada = new clsEntradaLeerEstructura(new clsArchivoSubido(nombre, new byte[bytes]), null, null, null);

        var resultado = await Servicio().LeerEstructuraAsync(_usuario, entrada, TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(codigo);
        await _nexo.DidNotReceiveWithAnyArgs().LeerEstructuraAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LaLecturaNoAbreElLibroYPasaLaRecetaExacta()
    {
        // Bytes que no son un .xlsx: Cotifly no abre el libro (#277), lo decide NEXO.
        const string Receta = "  {\"version\":1,\"hojas\":[]}\n";
        clsPeticionLeerEstructura? enviada = null;
        var lectura = new clsLecturaEstructura("EPM.xlsm", "manual", null, null, false, "SIN_INSTRUCCION", default, null, [], [], [], [], null);
        _nexo.LeerEstructuraAsync(_usuario, Arg.Do<clsPeticionLeerEstructura>(p => enviada = p), Arg.Any<CancellationToken>()).Returns(lectura);
        var entrada = new clsEntradaLeerEstructura(new clsArchivoSubido(@"C:\Users\ana\EPM.xlsm", [1, 2, 3]), "900555111", "el destino es la casilla", Receta);

        var resultado = await Servicio().LeerEstructuraAsync(_usuario, entrada, TestContext.Current.CancellationToken);

        resultado.Valor.Should().BeSameAs(lectura);
        enviada!.Archivo.NombreArchivo.Should().Be("EPM.xlsm", "el nombre original, sin la ruta del equipo del comercial");
        enviada.Archivo.Contenido.Should().Equal(1, 2, 3);
        (enviada.ClienteNit, enviada.Instrucciones, enviada.RecetaJson).Should().Be(("900555111", "el destino es la casilla", Receta));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(100, 0, true)]
    [InlineData(0, 100, true)]
    [InlineData(50, 50, true)]
    public async Task ElArchivoConLaRecetaYLasInstruccionesNoPuedePasarElTopeDeNexo(int bytesReceta, int bytesInstrucciones, bool rechaza)
    {
        // Auditoría B7: el archivo solo cabe (justo en el tope); con la receta o las instrucciones (en bytes UTF-8) ya no.
        _nexo.LeerEstructuraAsync(_usuario, Arg.Any<clsPeticionLeerEstructura>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "x"));
        static string? Texto(int bytes) => bytes == 0 ? null : new string('ñ', bytes / 2);
        var entrada = new clsEntradaLeerEstructura(
            new clsArchivoSubido("a.xlsx", new byte[clsArchivoExcel.TamanoMaximoBytes - 49]), null, Texto(bytesInstrucciones), Texto(bytesReceta));

        var resultado = await Servicio().LeerEstructuraAsync(_usuario, entrada, TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(rechaza ? clsCodigosError.LecturaDemasiadoGrande : clsCodigosError.NexoNoDisponible);
        await _nexo.Received(rechaza ? 0 : 1).LeerEstructuraAsync(_usuario, Arg.Any<clsPeticionLeerEstructura>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(clsCodigosError.NexoNoDisponible)]
    [InlineData(clsCodigosError.IaTopeDiario)]
    [InlineData(clsCodigosError.RecetaInvalida)]
    public async Task LosErroresDeLaLecturaLleganTalCual(string codigo)
    {
        _nexo.LeerEstructuraAsync(_usuario, Arg.Any<clsPeticionLeerEstructura>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(codigo, "error"));

        var resultado = await Servicio().LeerEstructuraAsync(
            _usuario, new clsEntradaLeerEstructura(new clsArchivoSubido("a.xlsx", [1]), null, null, null), TestContext.Current.CancellationToken);

        resultado.Error.Codigo.Should().Be(codigo);
    }

    [Fact]
    public async Task SinComercialEsUnErrorInterno()
    {
        (await Servicio().SugerirAsync(Guid.Empty, Peticion(1), TestContext.Current.CancellationToken)).Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
        (await Servicio().LeerEstructuraAsync(Guid.Empty, new clsEntradaLeerEstructura(new clsArchivoSubido("a.xlsx", [1]), null, null, null),
            TestContext.Current.CancellationToken)).Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
    }
}
