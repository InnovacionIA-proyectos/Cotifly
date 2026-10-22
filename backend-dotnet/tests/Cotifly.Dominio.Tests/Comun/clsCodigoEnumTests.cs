using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Sesiones;

namespace Cotifly.Dominio.Tests.Comun;

/// <summary>Los códigos de los enums son los que exigen los CHECK del esquema y el contrato #17.</summary>
public sealed class clsCodigoEnumTests
{
    [Fact]
    public void EstadoEnvioUsaSinEnviarEnviadaReenviada() =>
        clsCodigoEnum.Codigos<EstadoEnvio>().Should().Equal("sin_enviar", "enviada", "reenviada");

    [Fact]
    public void OrigenMargenUsaSapTmPoliticaLineaNegociado() =>
        clsCodigoEnum.Codigos<OrigenMargen>().Should().Equal("sap_tm", "politica_linea", "negociado");

    [Fact]
    public void OrigenClienteUsaClienteProspecto() =>
        clsCodigoEnum.Codigos<OrigenCliente>().Should().Equal("cliente", "prospecto");

    [Fact]
    public void EstadoSesionUsaActivaArchivada() =>
        clsCodigoEnum.Codigos<EstadoSesion>().Should().Equal("activa", "archivada");

    [Fact]
    public void RolInternoUsaComercialGerenteComercial() =>
        clsCodigoEnum.Codigos<RolInterno>().Should().Equal("comercial", "gerente_comercial");

    [Fact]
    public void TipoDestinatarioUsaParaCc() =>
        clsCodigoEnum.Codigos<TipoDestinatario>().Should().Equal("para", "cc");

    [Fact]
    public void EstadoCorreoUsaEnviadoFallidoEncoladoYEnviando() =>
        clsCodigoEnum.Codigos<EstadoCorreo>().Should().Equal("enviado", "fallido", "encolado", "enviando");

    [Fact]
    public void LaSincronizacionDelResultadoUsaPendienteSincronizadoYNoSincronizable() =>
        clsCodigoEnum.Codigos<SincronizacionResultadoNexo>().Should().Equal("pendiente", "sincronizado", "no_sincronizable");

    [Theory]
    [InlineData("politica_linea", OrigenMargen.PoliticaLinea)]
    [InlineData("SAP_TM", OrigenMargen.SapTm)]
    [InlineData(" negociado ", OrigenMargen.Negociado)]
    public void DesdeCodigoAceptaMayusculasYEspacios(string codigo, OrigenMargen esperado) =>
        clsCodigoEnum.DesdeCodigo<OrigenMargen>(codigo).Should().Be(esperado);

    [Theory]
    [InlineData("admin")]
    [InlineData("")]
    [InlineData("GerenteComercial")]
    public void DesdeCodigoRechazaCodigosDesconocidos(string codigo)
    {
        var convertir = () => clsCodigoEnum.DesdeCodigo<RolInterno>(codigo);

        convertir.Should().Throw<ArgumentException>();
        clsCodigoEnum.TryDesdeCodigo<RolInterno>(codigo, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDesdeCodigoConNuloDevuelveFalse() =>
        clsCodigoEnum.TryDesdeCodigo<EstadoSesion>(null, out _).Should().BeFalse();

    [Fact]
    public void ACodigoDeUnValorNoDefinidoLanza()
    {
        var convertir = () => clsCodigoEnum.ACodigo((EstadoEnvio)99);

        convertir.Should().Throw<ArgumentOutOfRangeException>();
    }
}
