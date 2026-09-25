using Cotifly.Aplicacion.Identidad.Services;

namespace Cotifly.Aplicacion.Tests.Identidad;

/// <summary>Refresh opaco de 256 bits en base64url y su SHA-256 hexadecimal (lo único que se guarda).</summary>
public sealed class clsTokenOpacoTests
{
    [Fact]
    public void GeneraTreintaYDosBytesAleatoriosEnBase64UrlSinRelleno()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => clsTokenOpaco.Generar()).ToList();

        tokens.Should().OnlyContain(t => t.Length == 43 && t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'));
        tokens.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ElHashEsSha256EnHexadecimalMinuscula() =>
        clsTokenOpaco.Hash("abc").Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
}
