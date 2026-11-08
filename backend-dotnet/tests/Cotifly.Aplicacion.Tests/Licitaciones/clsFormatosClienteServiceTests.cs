using System.Text.Json;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// HU-044.5 (#256): plantillas de mapeo, recetas de lectura y diccionario del cliente son un puente hacia NEXO. Los listados exigen el NIT
/// (sin él NEXO devolvería los de todos los clientes) y no llaman a NEXO sin él; el PUT de la receta solo valida el archivo (sin abrirlo) y
/// que venga la receta; el diccionario solo devuelve los términos generales mientras NEXO no filtre por cliente.
/// </summary>
public sealed class clsFormatosClienteServiceTests
{
    private const string Nit = "900.555.111-2";
    private static readonly Guid _usuario = Guid.NewGuid();
    private readonly IClsImportacionExcelNexo _nexo = Substitute.For<IClsImportacionExcelNexo>();

    private readonly clsLoggerFalso<clsFormatosClienteService> _logger = new();

    private clsFormatosClienteService Servicio() => new(_nexo, _logger);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsTerminoCliente Termino(string termino, Guid? clienteId, bool clienteIdLeido = true, string? clienteNombre = null) =>
        new(Guid.NewGuid(), clienteId, clienteNombre ?? (clienteId is null ? null : "Cliente ficticio"), termino, "valor", "3S3", "tipoVehiculo",
            null, true, DateTimeOffset.UtcNow, null, clienteIdLeido);

    private static clsRecetaGuardada Receta() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Cliente ficticio", "Formato", JsonDocument.Parse("""{"version":1}""").RootElement.Clone(), null, 0,
            null, DateTimeOffset.UtcNow, null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListarPlantillasSinNitEsClienteRequeridoSinLlamarANexo(string? nit)
    {
        var resultado = await Servicio().ListarPlantillasAsync(_usuario, nit, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        await _nexo.DidNotReceiveWithAnyArgs().ListarPlantillasMapeoAsync(default, default!, Ct);
    }

    [Fact]
    public async Task ListarPlantillasConNitLasPideANexo()
    {
        _nexo.ListarPlantillasMapeoAsync(_usuario, Nit, Arg.Any<CancellationToken>()).Returns(clsResultado<IReadOnlyList<clsPlantillaMapeo>>.Exito([]));

        var resultado = await Servicio().ListarPlantillasAsync(_usuario, Nit, Ct);

        resultado.Valor.Should().BeEmpty();
        await _nexo.Received(1).ListarPlantillasMapeoAsync(_usuario, Nit, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task ListarRecetasSinNitEsClienteRequeridoSinLlamarANexo(string? nit)
    {
        var resultado = await Servicio().ListarRecetasAsync(_usuario, nit, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        await _nexo.DidNotReceiveWithAnyArgs().ListarRecetasLecturaAsync(default, default!, Ct);
    }

    [Fact]
    public async Task GuardarRecetaPasaLaRecetaExactaYElNombreLimpioDelArchivo()
    {
        const string RecetaTexto = """{ "version" : 1, "hojas":[] }""";
        _nexo.GuardarRecetaLecturaAsync(_usuario, Arg.Any<clsPeticionGuardarReceta>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsRecetaGuardadaResultado>.Exito(new clsRecetaGuardadaResultado(Receta(), true)));
        var entrada = new clsEntradaGuardarReceta(new clsArchivoSubido(@"C:\carpeta\EPM.xlsx", [1, 2, 3]), RecetaTexto, Nit, "Oferta", "Las celdas vacías no");

        var resultado = await Servicio().GuardarRecetaAsync(_usuario, entrada, Ct);

        resultado.Valor.Creada.Should().BeTrue();
        await _nexo.Received(1).GuardarRecetaLecturaAsync(_usuario, Arg.Is<clsPeticionGuardarReceta>(p =>
            p.RecetaJson == RecetaTexto && p.Archivo.NombreArchivo == "EPM.xlsx" && p.ClienteNit == Nit && p.Nombre == "Oferta"
            && p.Instrucciones == "Las celdas vacías no"), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("pliego.csv", 3, "FORMATO_NO_SOPORTADO")]
    [InlineData("pliego.xlsx", 0, "ARCHIVO_REQUERIDO")]
    [InlineData("pliego.xlsx", clsArchivoExcel.TamanoMaximoBytes + 1, "ARCHIVO_DEMASIADO_GRANDE")]
    public async Task GuardarRecetaConUnArchivoInvalidoNoLlamaANexo(string nombre, int bytes, string codigo)
    {
        var entrada = new clsEntradaGuardarReceta(new clsArchivoSubido(nombre, new byte[bytes]), """{"version":1}""", Nit, null, null);

        var resultado = await Servicio().GuardarRecetaAsync(_usuario, entrada, Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        await _nexo.DidNotReceiveWithAnyArgs().GuardarRecetaLecturaAsync(default, default!, Ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task GuardarRecetaSinRecetaEsRecetaRequeridaSinLlamarANexo(string? receta)
    {
        var entrada = new clsEntradaGuardarReceta(new clsArchivoSubido("EPM.xlsx", [1]), receta, Nit, null, null);

        var resultado = await Servicio().GuardarRecetaAsync(_usuario, entrada, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.RecetaRequerida);
        await _nexo.DidNotReceiveWithAnyArgs().GuardarRecetaLecturaAsync(default, default!, Ct);
    }

    [Fact]
    public async Task GuardarRecetaSinNitEsClienteRequeridoSinLlamarANexo()
    {
        var entrada = new clsEntradaGuardarReceta(new clsArchivoSubido("EPM.xlsx", [1]), """{"version":1}""", null, null, null);

        var resultado = await Servicio().GuardarRecetaAsync(_usuario, entrada, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        await _nexo.DidNotReceiveWithAnyArgs().GuardarRecetaLecturaAsync(default, default!, Ct);
    }

    [Fact]
    public async Task ElDiccionarioSinNitSoloDevuelveLosTerminosGeneralesYSinAviso()
    {
        _nexo.ObtenerDiccionarioClienteAsync(_usuario, null, Arg.Any<clsFiltroDiccionarioCliente?>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito([Termino("Tractomula", null), Termino("Mula de otro cliente", Guid.NewGuid())]));

        var resultado = await Servicio().ConsultarDiccionarioAsync(_usuario, null, null, null, Ct);

        resultado.Valor.Terminos.Select(t => t.Termino).Should().Equal("Tractomula");
        resultado.Valor.Aviso.Should().BeNull();
    }

    [Fact]
    public async Task ElDiccionarioConNitDevuelveSoloLosGeneralesYUnAvisoPorqueNexoNoFiltraPorCliente()
    {
        _nexo.ObtenerDiccionarioClienteAsync(_usuario, Nit, Arg.Any<clsFiltroDiccionarioCliente?>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito([Termino("Tractomula", null), Termino("Mula de otro cliente", Guid.NewGuid())]));

        var resultado = await Servicio().ConsultarDiccionarioAsync(_usuario, Nit, "valor", "mula", Ct);

        resultado.Valor.Terminos.Select(t => t.Termino).Should().Equal("Tractomula");
        resultado.Valor.Aviso!.Codigo.Should().Be(clsFormatosClienteService.AvisoTerminosClienteNoDisponibles);
        await _nexo.Received(1).ObtenerDiccionarioClienteAsync(_usuario, Nit,
            Arg.Is<clsFiltroDiccionarioCliente?>(f => f!.Tipo == "valor" && f.Q == "mula" && f.IncluirGenerales), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElFiltroDeGeneralesFallaCerradoSiNexoNoMandaElClienteIdOTraeNombreDeCliente()
    {
        // #256 B2: un término es general solo si NEXO mandó clienteId = null explícito y sin nombre de cliente. Un cambio de forma en NEXO
        // (clienteId ausente, renombrado o anidado) no puede hacer pasar por generales los términos de otros clientes.
        _nexo.ObtenerDiccionarioClienteAsync(_usuario, null, Arg.Any<clsFiltroDiccionarioCliente?>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito([
                Termino("Tractomula", null),
                Termino("Sin clienteId", null, clienteIdLeido: false),
                Termino("Con nombre de cliente", null, clienteNombre: "Cliente ficticio")]));

        var resultado = await Servicio().ConsultarDiccionarioAsync(_usuario, null, null, null, Ct);

        resultado.Valor.Terminos.Select(t => t.Termino).Should().Equal("Tractomula");
    }

    [Fact]
    public async Task BorrarUnaPlantillaOUnaRecetaDejaRastroConElComercialYElIdSinDatosDelCliente()
    {
        var plantilla = Guid.NewGuid();
        var receta = Guid.NewGuid();
        _nexo.EliminarPlantillaMapeoAsync(_usuario, plantilla, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(true));
        _nexo.EliminarRecetaLecturaAsync(_usuario, receta, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(true));

        await Servicio().EliminarPlantillaAsync(_usuario, plantilla, Ct);
        await Servicio().EliminarRecetaAsync(_usuario, receta, Ct);

        var registros = _logger.Registros.Where(r => r.Nivel == LogLevel.Information).Select(r => r.Mensaje).ToList();
        registros.Should().HaveCount(2);
        registros[0].Should().Contain(_usuario.ToString()).And.Contain(plantilla.ToString()).And.Contain("plantilla");
        registros[1].Should().Contain(_usuario.ToString()).And.Contain(receta.ToString()).And.Contain("formato");
    }

    [Fact]
    public async Task UnBorradoQueNexoRechazaNoDejaRastroDeBorrado()
    {
        var id = Guid.NewGuid();
        _nexo.EliminarRecetaLecturaAsync(_usuario, id, Arg.Any<CancellationToken>())
            .Returns(clsResultado<bool>.Fallo(new clsErrorDominio(clsCodigosError.RecetaNoEncontrada, "No existe.")));

        var resultado = await Servicio().EliminarRecetaAsync(_usuario, id, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.RecetaNoEncontrada);
        _logger.Registros.Should().NotContain(r => r.Nivel == LogLevel.Information);
    }

    [Fact]
    public async Task UnErrorDeNexoEnElDiccionarioLlegaConSuCodigo()
    {
        _nexo.ObtenerDiccionarioClienteAsync(_usuario, null, Arg.Any<clsFiltroDiccionarioCliente?>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTerminoCliente>>.Fallo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "caído")));

        var resultado = await Servicio().ConsultarDiccionarioAsync(_usuario, null, null, null, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    [Fact]
    public async Task SinElComercialEsUnErrorInternoSinLlamarANexo()
    {
        var resultado = await Servicio().ListarRecetasAsync(Guid.Empty, Nit, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
        await _nexo.DidNotReceiveWithAnyArgs().ListarRecetasLecturaAsync(default, default!, Ct);
    }
}
