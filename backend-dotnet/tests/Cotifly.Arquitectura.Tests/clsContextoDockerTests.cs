using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Cotifly.Arquitectura.Tests;

/// <summary>
/// El contexto de <c>docker build</c> (<c>.dockerignore</c>, lista blanca) debe incluir todo lo que los <c>.csproj</c> de <c>src/</c>
/// embeben o copian (<c>EmbeddedResource</c>, <c>None</c>/<c>Content</c> con archivo): si un recurso queda fuera, la imagen no compila
/// (CS1566; por ejemplo, un prompt del agente en Markdown excluido por <c>**/*.md</c>).
/// </summary>
/// <remarks>
/// Se eligió evaluar el <c>.dockerignore</c> real con las reglas de Docker (en vez de prohibir recursos <c>.md</c>) porque protege
/// cualquier extensión y cualquier regla futura: cada patrón se aplica en orden, el último que coincide gana, <c>!</c> vuelve a
/// incluir, <c>**</c> cruza carpetas, <c>*</c> y <c>?</c> no cruzan <c>/</c>, y un patrón que coincide con una carpeta cubre su
/// contenido (como <c>moby/patternmatcher</c>).
/// </remarks>
public sealed class clsContextoDockerTests
{
    private static readonly string _raiz = BuscarRaiz();

    [Fact]
    public void LosArchivosQueEmbebenOCopianLosProyectosDeSrcEntranEnElContexto()
    {
        var reglas = LeerReglas();
        var archivos = ArchivosDeLosProyectos().ToList();

        archivos.Should().Contain(a => a.EndsWith("Agente/Recursos/prompt_asistente_folio.md", StringComparison.Ordinal), "el prompt del agente es un recurso embebido");
        archivos.Where(a => EstaExcluido(reglas, a)).Should().BeEmpty("todo recurso de un .csproj de src/ debe viajar en el contexto de docker build");
    }

    [Theory]
    [InlineData("src/Cotifly.Dominio/Pdf/LEEME.md", true)]
    [InlineData("src/Cotifly.Aplicacion/Agente/Recursos/prompt_asistente_folio.md", false)]
    [InlineData("src/Cotifly.Api/bin/Release/x.dll", true)]
    [InlineData("tests/Cotifly.Api.Tests/clsCotiflyApiFactory.cs", true)]
    [InlineData("specs/openapi.yaml", true)]
    [InlineData("src/Cotifly.Api/Program.cs", false)]
    [InlineData("Directory.Packages.props", false)]
    [InlineData("src/Cotifly.Api/.env", true)]
    public void ElEvaluadorAplicaLasReglasDeDocker(string ruta, bool excluido) => EstaExcluido(LeerReglas(), ruta).Should().Be(excluido);

    private static List<(bool Negada, Regex Patron)> LeerReglas() =>
        File.ReadAllLines(Path.Combine(_raiz, ".dockerignore"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.StartsWith('!') ? (true, l[1..].Trim()) : (false, l))
            .Select(r => (r.Item1, Convertir(r.Item2.Trim('/'))))
            .ToList();

    /// <summary>Último patrón que coincide con la ruta o con alguna de sus carpetas; <c>!</c> la vuelve a incluir.</summary>
    private static bool EstaExcluido(List<(bool Negada, Regex Patron)> reglas, string ruta)
    {
        var partes = ruta.Split('/');
        var candidatos = Enumerable.Range(1, partes.Length).Select(n => string.Join('/', partes.Take(n))).ToList();
        var excluido = false;
        foreach (var (negada, patron) in reglas)
        {
            if (candidatos.Any(patron.IsMatch))
            {
                excluido = !negada;
            }
        }

        return excluido;
    }

    private static Regex Convertir(string patron)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < patron.Length; i++)
        {
            var c = patron[i];
            if (c == '*' && i + 1 < patron.Length && patron[i + 1] == '*')
            {
                var conBarra = i + 2 < patron.Length && patron[i + 2] == '/';
                regex.Append(conBarra ? "(.*/)?" : ".*");
                i += conBarra ? 2 : 1;
            }
            else
            {
                regex.Append(c switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(c.ToString()),
                });
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>Archivos de <c>EmbeddedResource</c>, <c>None</c> y <c>Content</c> de los .csproj de src/, relativos a backend-dotnet.</summary>
    private static IEnumerable<string> ArchivosDeLosProyectos()
    {
        foreach (var proyecto in Directory.GetFiles(Path.Combine(_raiz, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var carpeta = Path.GetDirectoryName(proyecto)!;
            var elementos = XDocument.Load(proyecto).Descendants()
                .Where(e => e.Name.LocalName is "EmbeddedResource" or "None" or "Content")
                .Select(e => e.Attribute("Include")?.Value)
                .OfType<string>()
                .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            foreach (var include in elementos)
            {
                var relativo = include.Replace('\\', '/');
                var directorio = Path.Combine(carpeta, Path.GetDirectoryName(relativo) ?? string.Empty);
                if (!Directory.Exists(directorio))
                {
                    continue;
                }

                foreach (var archivo in Directory.GetFiles(directorio, Path.GetFileName(relativo)))
                {
                    yield return Path.GetRelativePath(_raiz, archivo).Replace('\\', '/');
                }
            }
        }
    }

    private static string BuscarRaiz()
    {
        for (var directorio = new DirectoryInfo(AppContext.BaseDirectory); directorio is not null; directorio = directorio.Parent)
        {
            if (File.Exists(Path.Combine(directorio.FullName, "Cotifly.sln")))
            {
                return directorio.FullName;
            }
        }

        throw new InvalidOperationException("No se encontró backend-dotnet (Cotifly.sln) desde la carpeta de las pruebas.");
    }
}
