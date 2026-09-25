using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>Folio del servidor (ADR-011, aprobado): <c>PCN-[{nitCliente}-]DDMMAAAA-{NNN}[-vN]</c>.</summary>
public sealed class clsFolioTests
{
    private static readonly DateOnly _fecha = new(2026, 9, 25);

    [Theory]
    [InlineData(1, null, "PCN-25092026-001")]
    [InlineData(42, "", "PCN-25092026-042")]
    [InlineData(999, "   ", "PCN-25092026-999")]
    [InlineData(7, "900123456", "PCN-900123456-25092026-007")]
    [InlineData(7, "900.123.456-7", "PCN-9001234567-25092026-007")]
    [InlineData(7, "NIT sin dígitos", "PCN-25092026-007")]
    public void ElFolioNuevoLlevaFechaConsecutivoYSoloLosDigitosDelNit(int consecutivo, string? nit, string esperado)
    {
        var folio = clsFolio.Crear(_fecha, consecutivo, nit).Valor;

        folio.Valor.Should().Be(esperado);
        folio.Base.Should().Be(esperado);
        folio.Version.Should().Be(1);
        folio.Consecutivo.Should().Be(consecutivo);
        folio.Fecha.Should().Be(_fecha);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void FueraDe001A999ElConsecutivoEstaAgotado(int consecutivo) =>
        clsFolio.Crear(_fecha, consecutivo, null).Error.Codigo.Should().Be(clsCodigosError.FolioConsecutivoAgotado);

    [Fact]
    public void UnNitDeMasDeVeinteDigitosNoCabeEnElFolio() =>
        clsFolio.Crear(_fecha, 1, new string('9', 21)).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void ConElNitMasLargoYUnaVersionAltaElFolioCabeEnCincuentaCaracteres()
    {
        var folio = clsFolio.Crear(_fecha, 999, new string('9', 20)).Valor.ConVersion(999).Valor;

        folio.Valor.Should().Be($"PCN-{new string('9', 20)}-25092026-999-v999");
        folio.Valor.Length.Should().BeLessThanOrEqualTo(clsCotizacion.LargoMaximoFolio);
    }

    [Theory]
    [InlineData("PCN-25092026-003", "PCN-25092026-003", 1, 3, null)]
    [InlineData("PCN-25092026-003-v2", "PCN-25092026-003", 2, 3, null)]
    [InlineData("PCN-900123456-25092026-010-v12", "PCN-900123456-25092026-010", 12, 10, "900123456")]
    public void UnFolioDelFormatoSeInterpreta(string texto, string folioBase, int version, int consecutivo, string? nit)
    {
        var folio = clsFolio.Desde(texto).Valor;

        folio.Valor.Should().Be(texto);
        folio.Base.Should().Be(folioBase);
        folio.Version.Should().Be(version);
        folio.Consecutivo.Should().Be(consecutivo);
        folio.NitCliente.Should().Be(nit);
        folio.Fecha.Should().Be(_fecha);
    }

    [Theory]
    [InlineData("PCN-23072026", "PCN-23072026", 1)]
    [InlineData("PCN-23072026-v3", "PCN-23072026", 3)]
    [InlineData("LEGADO_1", "LEGADO_1", 1)]
    public void UnFolioAnteriorAlFormatoConservaSuBaseYSuVersion(string texto, string folioBase, int version)
    {
        var folio = clsFolio.Desde(texto).Valor;

        folio.Base.Should().Be(folioBase);
        folio.Version.Should().Be(version);
        folio.Fecha.Should().BeNull();
        folio.Consecutivo.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PCN 25092026")]
    [InlineData("../PCN-25092026-001")]
    public void UnFolioVacioOConCaracteresInvalidosNoSeInterpreta(string? texto) =>
        clsFolio.Desde(texto).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void LaVersionNuevaReutilizaLaBaseDeLaV1()
    {
        var v2 = clsFolio.Desde("PCN-25092026-004").Valor.ConVersion(2).Valor;
        var v3 = clsFolio.Desde(v2.Valor).Valor.ConVersion(3).Valor;

        v2.Valor.Should().Be("PCN-25092026-004-v2");
        v3.Valor.Should().Be("PCN-25092026-004-v3");
        v3.Version.Should().Be(3);
    }

    [Fact]
    public void UnaVersionNuevaEsLaDosOMayor() =>
        clsFolio.Crear(_fecha, 1, null).Valor.ConVersion(1).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void ElSegmentoDeFechaEsDiaMesAnio() => clsFolio.SegmentoFecha(new DateOnly(2031, 1, 5)).Should().Be("05012031");
}
