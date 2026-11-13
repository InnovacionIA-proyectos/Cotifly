using System.Text.RegularExpressions;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Los <c>mapeoLineasNexo</c> que siembran <c>dbo_cotifly.MargenesPolitica</c>, leídos de los scripts
/// reales (copiados a la salida por <c>tests/Directory.Build.props</c>), son todos líneas de NEXO válidas. Si uno no lo fuera, toda captura
/// con esa línea respondería 400 <c>LINEA_NEGOCIO_INVALIDO</c>. No necesita base de datos.
/// </summary>
public sealed class clsSemillaLineasNegocioNexoTests
{
    /// <summary>
    /// Todos los <c>mapeoLineasNexo</c> que siembran la base, leídos de los archivos reales (copiados a la salida por
    /// <c>tests/Directory.Build.props</c>): <c>db/migrations_mssql/0001</c> y <c>0009</c> y <c>db/seeder/01_lineas_negocio.sql</c>. Ninguno
    /// puede quedar fuera de las seis líneas de NEXO: si quedara, toda captura con esa línea respondería 400 <c>LINEA_NEGOCIO_INVALIDO</c>.
    /// </summary>
    [Theory]
    [InlineData("migrations_mssql", "0001_script_bd_cotifly_sqlserver.sql")]
    [InlineData("migrations_mssql", "0009_reestructuracion_cotizaciones.sql")]
    [InlineData("scripts_bd/seeder", "01_lineas_negocio.sql")]
    public void LaSemillaVigenteDeMapeoLineasNexoEsValida(string carpeta, string archivo)
    {
        var mapeos = MapeosSembrados(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, carpeta, archivo)));

        mapeos.Should().NotBeEmpty($"{archivo} siembra MargenesPolitica");
        mapeos.Where(m => m.Mapeo is not null && !clsLineasNegocioNexo.EsValida(m.Mapeo)).Should().BeEmpty();
        clsMargenPolitica.Crear("Prueba", 0.15m, mapeoLineasNexo: mapeos[^1].Mapeo).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public void LaSemillaVigenteIncluyeDevolucionContenedorYSeEnviaCanonica()
    {
        var seeder = MapeosSembrados(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scripts_bd", "seeder", "01_lineas_negocio.sql")));

        seeder.Should().Contain(("DevolucionContenedor", "DevolucionContenedor"));
        clsLineasNegocioNexo.Canonica("DevolucionContenedor").Should().Be("Devolución de contenedor");
    }

    /// <summary>Las filas <c>(lineaNegocio, ..., mapeoLineasNexo)</c> de los <c>INSERT INTO dbo_cotifly.MargenesPolitica</c> de un script.</summary>
    private static List<(string Linea, string? Mapeo)> MapeosSembrados(string sql)
    {
        var filas = new List<(string, string?)>();
        foreach (Match insert in Regex.Matches(sql,
                     @"INSERT INTO dbo_cotifly\.MargenesPolitica.*?(?:;|WHERE NOT EXISTS|\nGO)", RegexOptions.Singleline))
        {
            foreach (Match fila in Regex.Matches(insert.Value,
                         @"\(\s*N?'(?<linea>[^']+)'\s*,[^\n]*?,\s*(?<mapeo>N?'[^']*'|NULL|CAST\(NULL AS NVARCHAR\(MAX\)\))\s*\)"))
            {
                var mapeo = fila.Groups["mapeo"].Value;
                filas.Add((fila.Groups["linea"].Value, mapeo.Contains('\'', StringComparison.Ordinal) ? mapeo[(mapeo.IndexOf('\'', StringComparison.Ordinal) + 1)..^1] : null));
            }
        }

        return filas;
    }
}
