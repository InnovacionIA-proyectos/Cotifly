using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Iniciales del comercial para el folio (ADR-011, decisión del 2026-09-25): dos letras A–Z en mayúscula, la primera del primer
/// nombre y la primera del primer apellido (la penúltima palabra cuando hay tres o más), sin partículas ni tildes.
/// </summary>
public sealed class clsInicialesComercialTests
{
    [Theory]
    [InlineData("Duban Guerra Castro", "DG")]
    [InlineData("Duban Guerra", "DG")]
    [InlineData("María José Pérez Gómez", "MP")]
    [InlineData("Nathaly Vargas Garcia", "NV")]
    [InlineData("Juan Pablo de la Torre Restrepo", "JT")]
    [InlineData("Ana María del Castillo Ruiz", "AC")]
    [InlineData("Luis de los Ríos", "LR")]
    [InlineData("Carlos Pérez-Gómez Díaz", "CP")]
    [InlineData("José O'Neill", "JO")]
    [InlineData("  duban   guerra   castro  ", "DG")]
    public void SonLaInicialDelPrimerNombreYDelPrimerApellido(string nombreCompleto, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, "otro.usuario").Should().Be(esperadas);

    [Theory]
    [InlineData("Ángela Úsuga", "AU")]
    [InlineData("Óscar Ñañez Íñiguez", "ON")]
    [InlineData("Ñusta Ébano", "NE")]
    [InlineData("Çelik Ümit", "CU")]
    public void LasTildesYLaEnieSePasanAAscii(string nombreCompleto, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, null).Should().Be(esperadas);

    [Theory]
    [InlineData("María José Pérez", "MJ")]
    [InlineData("Ana María Castillo", "AM")]
    public void ConTresPalabrasSeAsumeUnNombreYDosApellidos(string nombreCompleto, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, null).Should().Be(esperadas, "con tres palabras no se sabe si son dos nombres; la regla asume dos apellidos");

    [Theory]
    [InlineData("Carlos", "CA")]
    [InlineData("Ñoño", "NO")]
    [InlineData("A", "AX")]
    public void UnaSolaPalabraDaSusDosPrimerasLetras(string nombreCompleto, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, null).Should().Be(esperadas);

    [Theory]
    [InlineData(null, "dguerra", "DG")]
    [InlineData("", "duban.guerra", "DG")]
    [InlineData("   ", "nvargas", "NV")]
    [InlineData("12345", "jperez2", "JP")]
    public void SinNombreCompletoSeUsaElNombreDeUsuario(string? nombreCompleto, string? nombreUsuario, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, nombreUsuario).Should().Be(esperadas);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "123")]
    [InlineData("—", "_.-")]
    public void SinNingunaLetraSonXX(string? nombreCompleto, string? nombreUsuario) =>
        clsInicialesComercial.Calcular(nombreCompleto, nombreUsuario).Should().Be(clsInicialesComercial.SinNombre);

    [Fact]
    public void UnNombreSoloDeParticulasNoLasDescarta() => clsInicialesComercial.Calcular("De La", null).Should().Be("DL");

    [Theory]
    [InlineData("dguerra@boterosoto.com.co", "DG")]
    [InlineData("duban.guerra@boterosoto.com.co", "DG")]
    [InlineData("NVARGAS@BOTEROSOTO.COM.CO", "NV")]
    [InlineData("@boterosoto.com.co", "XX")]
    public void UnNombreDeUsuarioConCorreoPierdeElDominio(string nombreUsuario, string esperadas) =>
        clsInicialesComercial.Calcular(null, nombreUsuario).Should().Be(esperadas);

    [Theory]
    [InlineData("ana_maria_perez_gomez", "AP")]
    [InlineData("ana_maria.perez", "AM")]
    [InlineData("duban_guerra", "DG")]
    public void ElGuionBajoSeparaPalabrasYSeAplicaLaMismaRegla(string nombreUsuario, string esperadas) =>
        clsInicialesComercial.Calcular(null, nombreUsuario).Should().Be(esperadas, "con tres o más palabras son la primera y la penúltima");

    [Theory]
    [InlineData("Duban Guerra - Comercial Medellín", "DG")]
    [InlineData("Duban Guerra – Comercial Medellín", "DG")]
    [InlineData("Duban Guerra (Medellín)", "DG")]
    [InlineData("Duban Guerra Castro (Comercial) - Regional Antioquia", "DG")]
    [InlineData("Duban (Comercial Senior) Guerra", "DG")]
    [InlineData("Duban Guerra (sin cerrar", "DG")]
    [InlineData("Pérez-Gómez Ana", "PA")]
    public void ElTextoLibreEntreParentesisOTrasUnGuionConEspaciosSeDescarta(string nombreCompleto, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, null).Should().Be(esperadas);

    [Fact]
    public void UnNombreSoloDeTextoLibreUsaElNombreDeUsuario() =>
        clsInicialesComercial.Calcular("(Comercial Medellín)", "dguerra").Should().Be("DG");

    [Theory]
    [InlineData("Lina M. Restrepo", "LR")]
    [InlineData("Lina M. Restrepo Gómez", "LR")]
    [InlineData("Juan C. Pérez", "JP")]
    [InlineData("J. Carlos Pérez", "JC")]
    [InlineData("Lina Ma. Restrepo", "LM")]
    public void UnaInicialAbreviadaNoCuentaComoApellido(string nombreCompleto, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, null).Should().Be(esperadas);

    [Theory]
    [InlineData("María José Pérez", "MJ")]
    [InlineData("Guerra Castro Duban", "GC")]
    [InlineData("Ana San Martín", "AS")]
    public void LimitacionesConocidasDocumentadasEnElAdr(string nombreCompleto, string esperadas) =>
        clsInicialesComercial.Calcular(nombreCompleto, null).Should().Be(esperadas);

    [Theory]
    [InlineData("DG", true)]
    [InlineData("XX", true)]
    [InlineData("dg", false)]
    [InlineData("D", false)]
    [InlineData("DGC", false)]
    [InlineData("Ñ1", false)]
    [InlineData(null, false)]
    public void SoloSonValidasDosLetrasMayusculasAscii(string? iniciales, bool valida) =>
        clsInicialesComercial.EsValida(iniciales).Should().Be(valida);
}
