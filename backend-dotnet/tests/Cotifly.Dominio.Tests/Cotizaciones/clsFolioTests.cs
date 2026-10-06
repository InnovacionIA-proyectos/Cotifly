using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>Folio del servidor (ADR-011, aprobado): <c>PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]</c>.</summary>
public sealed class clsFolioTests
{
    private static readonly DateOnly _fecha = new(2026, 9, 25);

    [Theory]
    [InlineData(1, null, "PCN-DG-25092026-001")]
    [InlineData(42, "", "PCN-DG-25092026-042")]
    [InlineData(999, "   ", "PCN-DG-25092026-999")]
    [InlineData(7, "900123456", "PCN-900123456-DG-25092026-007")]
    [InlineData(7, "900.123.456-7", "PCN-9001234567-DG-25092026-007")]
    [InlineData(7, "NIT sin dígitos", "PCN-DG-25092026-007")]
    public void ElFolioNuevoLlevaInicialesFechaConsecutivoYSoloLosDigitosDelNit(int consecutivo, string? nit, string esperado)
    {
        var folio = clsFolio.Crear(_fecha, "DG", consecutivo, nit).Valor;

        folio.Valor.Should().Be(esperado);
        folio.Base.Should().Be(esperado);
        folio.Version.Should().Be(1);
        folio.Iniciales.Should().Be("DG");
        folio.Consecutivo.Should().Be(consecutivo);
        folio.Fecha.Should().Be(_fecha);
    }

    [Fact]
    public void ElEjemploDeLaDecisionConNitYVersion() =>
        clsFolio.Crear(_fecha, "DG", 1, "9000472281").Valor.ConVersion(2).Valor.Valor.Should().Be("PCN-9000472281-DG-25092026-001-v2");

    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(-1)]
    public void FueraDe001A999ElConsecutivoEstaAgotado(int consecutivo) =>
        clsFolio.Crear(_fecha, "DG", consecutivo, null).Error.Codigo.Should().Be(clsCodigosError.FolioConsecutivoAgotado);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dg")]
    [InlineData("D")]
    [InlineData("DGC")]
    [InlineData("D1")]
    public void LasInicialesDebenSerDosLetrasMayusculas(string? iniciales) =>
        clsFolio.Crear(_fecha, iniciales!, 1, null).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void ElNitMaximoSeCalculaParaQueQuepaUnaVersionDeDosDigitos() =>
        clsFolio.LargoMaximoNit.Should().Be(clsCotizacion.LargoMaximoFolio - "PCN--XX-DDMMAAAA-NNN-v99".Length).And.Be(26);

    [Fact]
    public void UnNitMasLargoQueElMaximoNoCabeEnElFolio() =>
        clsFolio.Crear(_fecha, "DG", 1, new string('9', clsFolio.LargoMaximoNit + 1)).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void ConElNitMasLargoYLaVersion99ElFolioOcupaExactamenteCincuentaCaracteres()
    {
        var nit = new string('9', clsFolio.LargoMaximoNit);
        var folio = clsFolio.Crear(_fecha, "DG", 999, nit).Valor.ConVersion(99).Valor;

        folio.Valor.Should().Be($"PCN-{nit}-DG-25092026-999-v99");
        folio.Valor.Length.Should().Be(clsCotizacion.LargoMaximoFolio);
        clsFolio.Desde(folio.Valor).Valor.NitCliente.Should().Be(nit);
    }

    [Fact]
    public void ConElNitMasLargoLaVersion100YaNoCabe() =>
        clsFolio.Crear(_fecha, "DG", 1, new string('9', clsFolio.LargoMaximoNit)).Valor.ConVersion(100).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void LaExpresionDelFormatoAdmiteExactamenteHastaLargoMaximoNitDigitos()
    {
        var alLimite = clsFolio.Desde($"PCN-{new string('8', clsFolio.LargoMaximoNit)}-DG-25092026-001").Valor;
        var pasado = clsFolio.Desde($"PCN-{new string('8', clsFolio.LargoMaximoNit + 1)}-DG-25092026-001").Valor;

        alLimite.NitCliente.Should().HaveLength(clsFolio.LargoMaximoNit, "la expresión debe aceptar el NIT más largo que genera Crear");
        alLimite.Iniciales.Should().Be("DG");
        pasado.NitCliente.Should().BeNull("un NIT más largo que LargoMaximoNit no es del formato: la expresión y la constante deben coincidir");
        pasado.Fecha.Should().BeNull();
    }

    [Theory]
    [InlineData("PCN-DG-25092026-003", "PCN-DG-25092026-003", 1, 3, null)]
    [InlineData("PCN-DG-25092026-003-v2", "PCN-DG-25092026-003", 2, 3, null)]
    [InlineData("PCN-900123456-DG-25092026-010-v12", "PCN-900123456-DG-25092026-010", 12, 10, "900123456")]
    public void UnFolioDelFormatoSeInterpreta(string texto, string folioBase, int version, int consecutivo, string? nit)
    {
        var folio = clsFolio.Desde(texto).Valor;

        folio.Valor.Should().Be(texto);
        folio.Base.Should().Be(folioBase);
        folio.Version.Should().Be(version);
        folio.Iniciales.Should().Be("DG");
        folio.Consecutivo.Should().Be(consecutivo);
        folio.NitCliente.Should().Be(nit);
        folio.Fecha.Should().Be(_fecha);
    }

    [Theory]
    [InlineData("PCN-23072026", "PCN-23072026", 1)]
    [InlineData("PCN-23072026-v3", "PCN-23072026", 3)]
    [InlineData("PCN-25092026-001", "PCN-25092026-001", 1)]
    [InlineData("PCN-900123456-25092026-001-v2", "PCN-900123456-25092026-001", 2)]
    [InlineData("LEGADO_1", "LEGADO_1", 1)]
    public void UnFolioAnteriorAlFormatoConservaSuBaseYSuVersion(string texto, string folioBase, int version)
    {
        var folio = clsFolio.Desde(texto).Valor;

        folio.Base.Should().Be(folioBase);
        folio.Version.Should().Be(version);
        folio.Fecha.Should().BeNull();
        folio.Iniciales.Should().BeNull();
        folio.Consecutivo.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PCN DG 25092026")]
    [InlineData("../PCN-DG-25092026-001")]
    public void UnFolioVacioOConCaracteresInvalidosNoSeInterpreta(string? texto) =>
        clsFolio.Desde(texto).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void LaVersionNuevaReutilizaLaBaseDeLaV1()
    {
        var v2 = clsFolio.Desde("PCN-DG-25092026-004").Valor.ConVersion(2).Valor;
        var v3 = clsFolio.Desde(v2.Valor).Valor.ConVersion(3).Valor;

        v2.Valor.Should().Be("PCN-DG-25092026-004-v2");
        v3.Valor.Should().Be("PCN-DG-25092026-004-v3");
        v3.Version.Should().Be(3);
        v3.Iniciales.Should().Be("DG");
    }

    [Fact]
    public void UnaVersionNuevaEsLaDosOMayor() =>
        clsFolio.Crear(_fecha, "DG", 1, null).Valor.ConVersion(1).Error.Codigo.Should().Be(clsFolio.CodigoInvalido);

    [Fact]
    public void ElSegmentoDeFechaEsDiaMesAnio() => clsFolio.SegmentoFecha(new DateOnly(2031, 1, 5)).Should().Be("05012031");
}
