using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>
/// Crear, editar y borrar términos del diccionario del cliente: validaciones locales antes de NEXO, NIT obligatorio, los términos generales
/// no se tocan y un id que no es del cliente no llega a la escritura de NEXO. Cada cambio deja rastro con el comercial y el id.
/// </summary>
public sealed class clsDiccionarioClienteEscrituraServiceTests
{
    private const string Nit = "900.555.111-2";
    private static readonly Guid _usuario = Guid.NewGuid();
    private static readonly Guid _cliente = Guid.NewGuid();
    private readonly IClsImportacionExcelNexo _nexo = Substitute.For<IClsImportacionExcelNexo>();
    private readonly clsLoggerFalso<clsFormatosClienteService> _logger = new();

    private clsFormatosClienteService Servicio() => new(_nexo, _logger);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsEntradaTerminoCliente Entrada(
        string? nit = Nit, string? termino = "Tractomula", string? tipo = "valor", string? concepto = "3S3", string? contexto = "tipoVehiculo",
        string? ejemplo = null, bool? activo = null) =>
        new(nit, termino, tipo, concepto, contexto, ejemplo, activo);

    private static clsTerminoCliente Termino(Guid id, Guid? clienteId, string termino = "Tractomula", bool clienteIdLeido = true) =>
        new(id, clienteId, clienteId is null ? null : "Cliente ficticio", termino, "valor", "3S3", "tipoVehiculo", null, true,
            DateTimeOffset.UtcNow, null, clienteIdLeido);

    private void ListarDevuelve(params clsTerminoCliente[] terminos) =>
        _nexo.ObtenerDiccionarioClienteAsync(_usuario, Nit, Arg.Any<clsFiltroDiccionarioCliente?>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTerminoCliente>>.Exito(terminos));

    // --- Crear ---

    [Fact]
    public async Task CrearPasaLosCamposLimpiosYElNitANexoYDejaRastroSinDatosDelCliente()
    {
        var creado = Termino(Guid.NewGuid(), _cliente);
        _nexo.CrearTerminoClienteAsync(_usuario, Nit, Arg.Any<clsDatosTerminoCliente>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsTerminoCliente>.Exito(creado));

        var resultado = await Servicio().CrearTerminoAsync(_usuario, Entrada(termino: "  Tractomula ", contexto: " tipoVehiculo ", ejemplo: "   "), Ct);

        resultado.Valor.Should().Be(creado);
        await _nexo.Received(1).CrearTerminoClienteAsync(_usuario, Nit,
            new clsDatosTerminoCliente("Tractomula", "valor", "3S3", "tipoVehiculo", null, true), Arg.Any<CancellationToken>());
        var rastro = _logger.Registros.Single(r => r.Nivel == LogLevel.Information).Mensaje;
        rastro.Should().Contain(_usuario.ToString()).And.Contain(creado.Id.ToString()).And.NotContain("900555111").And.NotContain("Tractomula");
    }

    [Fact]
    public async Task ActivoAusenteEsTrueYFalseSeRespeta()
    {
        _nexo.CrearTerminoClienteAsync(_usuario, Nit, Arg.Any<clsDatosTerminoCliente>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsTerminoCliente>.Exito(Termino(Guid.NewGuid(), _cliente)));

        await Servicio().CrearTerminoAsync(_usuario, Entrada(activo: false), Ct);

        await _nexo.Received(1).CrearTerminoClienteAsync(_usuario, Nit, Arg.Is<clsDatosTerminoCliente>(d => !d.Activo), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, "valor", "3S3", "TERMINO_REQUERIDO")]
    [InlineData("  ", "valor", "3S3", "TERMINO_REQUERIDO")]
    [InlineData("Mula", null, "3S3", "TIPO_TERMINO_INVALIDO")]
    [InlineData("Mula", " ", "3S3", "TIPO_TERMINO_INVALIDO")]
    [InlineData("Mula", "valor", "", "CONCEPTO_REQUERIDO")]
    public async Task LosVaciosRespondenElCodigoDeNexoSinLlamarlo(string? termino, string? tipo, string? concepto, string codigo)
    {
        var resultado = await Servicio().CrearTerminoAsync(_usuario, Entrada(termino: termino, tipo: tipo, concepto: concepto), Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        await _nexo.DidNotReceiveWithAnyArgs().CrearTerminoClienteAsync(default, default!, default!, Ct);
    }

    [Theory]
    [InlineData("termino", 201)]
    [InlineData("tipo", 101)]
    [InlineData("concepto", 101)]
    [InlineData("contexto", 51)]
    [InlineData("ejemplo", 501)]
    public async Task UnCampoMasLargoDeLoPermitidoEsSolicitudInvalidaSinLlamarANexo(string campo, int largo)
    {
        var texto = new string('x', largo);
        var entrada = campo switch
        {
            "termino" => Entrada(termino: texto),
            "tipo" => Entrada(tipo: texto),
            "concepto" => Entrada(concepto: texto),
            "contexto" => Entrada(contexto: texto),
            _ => Entrada(ejemplo: texto),
        };

        var resultado = await Servicio().CrearTerminoAsync(_usuario, entrada, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        resultado.Error.Mensaje.Should().Contain(campo);
        await _nexo.DidNotReceiveWithAnyArgs().CrearTerminoClienteAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task LosLargosJustosPasan()
    {
        _nexo.CrearTerminoClienteAsync(_usuario, Nit, Arg.Any<clsDatosTerminoCliente>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsTerminoCliente>.Exito(Termino(Guid.NewGuid(), _cliente)));

        var resultado = await Servicio().CrearTerminoAsync(_usuario, Entrada(
            termino: new string('t', 200), concepto: new string('c', 100), contexto: new string('x', 50), ejemplo: new string('e', 500)), Ct);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task CrearSinNitEsClienteRequeridoSinLlamarANexo(string? nit)
    {
        var resultado = await Servicio().CrearTerminoAsync(_usuario, Entrada(nit: nit), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        await _nexo.DidNotReceiveWithAnyArgs().CrearTerminoClienteAsync(default, default!, default!, Ct);
    }

    [Fact]
    public async Task UnRechazoDeNexoAlCrearLlegaTalCualSinRastro()
    {
        _nexo.CrearTerminoClienteAsync(_usuario, Nit, Arg.Any<clsDatosTerminoCliente>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsTerminoCliente>.Fallo(new clsErrorDominio(clsCodigosError.TerminoDuplicado, "Ya existe.")));

        var resultado = await Servicio().CrearTerminoAsync(_usuario, Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoDuplicado);
        _logger.Registros.Should().NotContain(r => r.Nivel == LogLevel.Information);
    }

    [Fact]
    public async Task SinElComercialEsUnErrorInterno()
    {
        var resultado = await Servicio().CrearTerminoAsync(Guid.Empty, Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ErrorInterno);
    }

    // --- Editar ---

    [Fact]
    public async Task EditarUnTerminoDelClienteLoListaPrimeroYLuegoLoEdita()
    {
        var id = Guid.NewGuid();
        ListarDevuelve(Termino(Guid.NewGuid(), null), Termino(id, _cliente));
        _nexo.EditarTerminoClienteAsync(_usuario, id, Arg.Any<clsDatosTerminoCliente>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsTerminoCliente>.Exito(Termino(id, _cliente, "Mula")));

        var resultado = await Servicio().EditarTerminoAsync(_usuario, id, Entrada(termino: "Mula"), Ct);

        resultado.Valor.Termino.Should().Be("Mula");
        await _nexo.Received(1).ObtenerDiccionarioClienteAsync(_usuario, Nit,
            Arg.Is<clsFiltroDiccionarioCliente?>(f => f!.IncluirGenerales && !f.SoloActivos && f.Tipo == null && f.Q == null), Arg.Any<CancellationToken>());
        _logger.Registros.Single(r => r.Nivel == LogLevel.Information).Mensaje.Should().Contain(id.ToString()).And.Contain("editó");
    }

    [Fact]
    public async Task EditarUnTerminoGeneralEsTerminoGeneralNoEditableSinLlamarALaEscritura()
    {
        var id = Guid.NewGuid();
        ListarDevuelve(Termino(id, null), Termino(Guid.NewGuid(), _cliente));

        var resultado = await Servicio().EditarTerminoAsync(_usuario, id, Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoGeneralNoEditable);
        await _nexo.DidNotReceiveWithAnyArgs().EditarTerminoClienteAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task EditarUnIdQueNoEsDelClienteEsTerminoNoEncontradoSinLlamarALaEscritura()
    {
        ListarDevuelve(Termino(Guid.NewGuid(), null), Termino(Guid.NewGuid(), _cliente));

        var resultado = await Servicio().EditarTerminoAsync(_usuario, Guid.NewGuid(), Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoNoEncontrado);
        await _nexo.DidNotReceiveWithAnyArgs().EditarTerminoClienteAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task SiNexoNoFiltroPorElNitNingunTerminoDeClienteSeEdita()
    {
        var id = Guid.NewGuid();
        ListarDevuelve(Termino(id, _cliente), Termino(Guid.NewGuid(), Guid.NewGuid()));

        var resultado = await Servicio().EditarTerminoAsync(_usuario, id, Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoNoEncontrado);
        await _nexo.DidNotReceiveWithAnyArgs().EditarTerminoClienteAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task UnTerminoSinClienteIdLeidoNoSeEdita()
    {
        var id = Guid.NewGuid();
        ListarDevuelve(Termino(id, null, clienteIdLeido: false));

        var resultado = await Servicio().EditarTerminoAsync(_usuario, id, Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoNoEncontrado);
    }

    [Fact]
    public async Task EditarValidaAntesDeListar()
    {
        var resultado = await Servicio().EditarTerminoAsync(_usuario, Guid.NewGuid(), Entrada(concepto: null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ConceptoRequerido);
        await _nexo.DidNotReceiveWithAnyArgs().ObtenerDiccionarioClienteAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task UnFalloDeNexoAlListarLlegaTalCual()
    {
        _nexo.ObtenerDiccionarioClienteAsync(_usuario, Nit, Arg.Any<clsFiltroDiccionarioCliente?>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsTerminoCliente>>.Fallo(new clsErrorDominio(clsCodigosError.NexoNoDisponible, "caído")));

        var resultado = await Servicio().EditarTerminoAsync(_usuario, Guid.NewGuid(), Entrada(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        await _nexo.DidNotReceiveWithAnyArgs().EditarTerminoClienteAsync(default, default, default!, Ct);
    }

    // --- Borrar ---

    [Fact]
    public async Task BorrarUnTerminoDelClienteDejaRastro()
    {
        var id = Guid.NewGuid();
        ListarDevuelve(Termino(id, _cliente));
        _nexo.EliminarTerminoClienteAsync(_usuario, id, Arg.Any<CancellationToken>()).Returns(clsResultado<bool>.Exito(true));

        var resultado = await Servicio().EliminarTerminoAsync(_usuario, id, Nit, Ct);

        resultado.Valor.Should().BeTrue();
        _logger.Registros.Single(r => r.Nivel == LogLevel.Information).Mensaje.Should().Contain(id.ToString()).And.Contain("borró");
    }

    [Fact]
    public async Task BorrarUnTerminoGeneralEsTerminoGeneralNoEditable()
    {
        var id = Guid.NewGuid();
        ListarDevuelve(Termino(id, null));

        var resultado = await Servicio().EliminarTerminoAsync(_usuario, id, Nit, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoGeneralNoEditable);
        await _nexo.DidNotReceiveWithAnyArgs().EliminarTerminoClienteAsync(default, default, Ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task BorrarSinNitEsClienteRequeridoSinLlamarANexo(string? nit)
    {
        var resultado = await Servicio().EliminarTerminoAsync(_usuario, Guid.NewGuid(), nit, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.ClienteRequerido);
        await _nexo.DidNotReceiveWithAnyArgs().ObtenerDiccionarioClienteAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task UnBorradoQueNexoNoEncuentraNoDejaRastro()
    {
        var id = Guid.NewGuid();
        ListarDevuelve(Termino(id, _cliente));
        _nexo.EliminarTerminoClienteAsync(_usuario, id, Arg.Any<CancellationToken>())
            .Returns(clsResultado<bool>.Fallo(new clsErrorDominio(clsCodigosError.TerminoNoEncontrado, "No existe.")));

        var resultado = await Servicio().EliminarTerminoAsync(_usuario, id, Nit, Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.TerminoNoEncontrado);
        _logger.Registros.Should().NotContain(r => r.Nivel == LogLevel.Information);
    }
}
