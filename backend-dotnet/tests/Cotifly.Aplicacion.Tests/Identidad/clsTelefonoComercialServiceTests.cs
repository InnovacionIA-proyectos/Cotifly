using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Identidad.Services;
using Cotifly.Dominio.Identidad;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>Confirmación del teléfono antes del PDF (<c>GET/PUT /web/usuario/telefono</c>, patrón de <c>TelefonoComercial</c>).</summary>
public sealed class clsTelefonoComercialServiceTests
{
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsUnitOfWork _unidadTrabajo = Substitute.For<IClsUnitOfWork>();
    private readonly clsUsuarioWeb _usuario = clsUsuarioWeb.Crear("nvargas", "Nathaly", "n@x.com", null, DateTimeOffset.UtcNow).Valor;

    public clsTelefonoComercialServiceTests() =>
        _usuarios.ObtenerPorIdAsync(_usuario.Id, Arg.Any<CancellationToken>()).Returns(_usuario);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsTelefonoComercialService Servicio() => new(_usuarios, _unidadTrabajo);

    [Fact]
    public async Task SinTelefonoCapturadoDevuelveNull() =>
        (await Servicio().ObtenerAsync(_usuario.Id, Ct)).Valor.Telefono.Should().BeNull();

    [Theory]
    [InlineData("3134156187")]
    [InlineData("313 415 6187")]
    [InlineData("+57 313 415 6187")]
    [InlineData("  6041234  ")]
    public async Task UnTelefonoValidoQuedaGuardadoEnUsuariosWeb(string telefono)
    {
        var resultado = await Servicio().GuardarAsync(_usuario.Id, telefono, Ct);

        resultado.Valor.Telefono.Should().Be(telefono.Trim());
        _usuario.Telefono.Should().Be(telefono.Trim());
        await _unidadTrabajo.Received(1).GuardarCambiosAsync(Arg.Any<CancellationToken>());
        (await Servicio().ObtenerAsync(_usuario.Id, Ct)).Valor.Telefono.Should().Be(telefono.Trim());
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("313-415-6187")]
    [InlineData("++573134156187")]
    [InlineData("+57 300 000 0000 000 0000")]
    [InlineData("abc1234567")]
    [InlineData("3134156187\n")]
    public async Task UnTelefonoFueraDelPatronSeRechazaSinGuardar(string telefono)
    {
        var resultado = await Servicio().GuardarAsync(_usuario.Id, telefono.Replace("\n", "\nx", StringComparison.Ordinal), Ct);

        resultado.Error.Codigo.Should().Be(clsTelefonoComercialService.CodigoTelefonoInvalido);
        await _unidadTrabajo.DidNotReceiveWithAnyArgs().GuardarCambiosAsync(Ct);
    }

    [Fact]
    public async Task UnTelefonoVacioLoBorra()
    {
        _usuario.CambiarTelefono("3134156187");

        var resultado = await Servicio().GuardarAsync(_usuario.Id, " ", Ct);

        resultado.Valor.Telefono.Should().BeNull();
        _usuario.Telefono.Should().BeNull();
    }

    [Fact]
    public async Task UnComercialInexistenteSeInforma()
    {
        (await Servicio().ObtenerAsync(Guid.NewGuid(), Ct)).Error.Codigo.Should().Be(clsTelefonoComercialService.CodigoUsuarioNoEncontrado);
        (await Servicio().GuardarAsync(Guid.NewGuid(), "3134156187", Ct)).Error.Codigo.Should().Be(clsTelefonoComercialService.CodigoUsuarioNoEncontrado);
    }
}
