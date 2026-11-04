using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-042.2 (#243): un set con la forma completa de <c>RentabClienteSet</c> real (1.276 filas, dos organizaciones, ocho TrqType, 10 márgenes
/// negativos y 6 mayores que 60, <c>Value</c> como texto con hasta 17 decimales, BP sin ceros) pasa la guarda y queda tal cual en la copia
/// (redondeado a 4 decimales; los anómalos se guardan: el que decide no aplicarlos es el envío a Nexo, #139). El set es SINTÉTICO (el real
/// tiene BP y márgenes de clientes y no se versiona); con <see cref="VariableSetReal"/> apuntando a una copia local del real, la misma
/// prueba lo carga (se omite si no está).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSincronizacionMargenSapSetCompletoTests(clsBaseDatosPruebas baseDatos)
{
    /// <summary>Ruta de una copia local de la respuesta real de SAP (no se versiona: datos de clientes).</summary>
    public const string VariableSetReal = "COTIFLY_SAPTM_JSON";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task ElSetSinteticoConLaFormaRealSeSincronizaCompleto() => SincronizarYVerificarAsync(SetSintetico());

    [Fact]
    public async Task ElSetRealSeSincronizaCompletoSiHayUnaCopiaLocal()
    {
        var ruta = Environment.GetEnvironmentVariable(VariableSetReal);
        Assert.SkipUnless(!string.IsNullOrWhiteSpace(ruta) && File.Exists(ruta), $"Solo con {VariableSetReal} apuntando a la respuesta real de SAP.");

        await SincronizarYVerificarAsync(await File.ReadAllTextAsync(ruta!, Ct));
    }

    /// <summary>1.276 filas como las reales: dos organizaciones, ocho TrqType, 10 negativos, 6 mayores que 60 y decimales largos.</summary>
    public static string SetSintetico()
    {
        string[] organizaciones = ["50000005", "50000010"];
        string[] tipos = ["OPTN", "OPDS", "OPIM", "OPEX", "OPTI", "OPSC", "OHUB", "OH3P"];
        var azar = new Random(243);
        var filas = new List<(string, string, string, string)>(1276);
        for (var i = 0; i < 1276; i++)
        {
            var entero = i < 10 ? -azar.Next(1, 40) : i < 16 ? azar.Next(61, 99) : azar.Next(0, 60);
            var decimales = string.Concat(Enumerable.Range(0, azar.Next(1, 18)).Select(_ => (char)('0' + azar.Next(0, 10))));
            filas.Add((organizaciones[i % 2], tipos[i % 8], (100_000 + i).ToString(CultureInfo.InvariantCulture), $"{entero}.{decimales}"));
        }

        return clsSapRentabFalso.Cuerpo(filas);
    }

    private async Task SincronizarYVerificarAsync(string cuerpo)
    {
        var esperadas = Esperadas(cuerpo);
        await using var aislada = await baseDatos.CrearBaseConScriptsAsync("dbSapSetCompleto", clsBaseDatosPruebas.Scripts.Length, clsBaseDatosPruebas.CollationCotifly);
        var sap = new clsSapRentabFalso().Responder(HttpStatusCode.OK, cuerpo);
        await using var proveedor = clsServiciosPruebas.Crear(aislada.Cadena, new clsRelojFalso(), baseDatos.CadenaIdentidad, new Dictionary<string, string?>
        {
            ["SapTm:SincronizacionHabilitada"] = "true",
            ["SapTm:Url"] = clsSapRentabFalso.Url,
            ["SapTm:User"] = clsSapRentabFalso.Usuario,
            ["SapTm:Pass"] = clsSapRentabFalso.Clave,
        }, configurar: s => s.AddHttpClient(clsClienteRentabSap.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => sap));

        // Con los valores por defecto de la guarda (100 filas como mínimo, la tabla vacía).
        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("ok", r.Corrida?.MensajeError);
        (r.FilasRecibidas, r.Corrida!.FilasInsertadas).Should().Be((esperadas.Count, esperadas.Count));
        var guardadas = await GuardadasAsync(aislada.Cadena);
        guardadas.Should().BeEquivalentTo(esperadas, "cada valor queda redondeado a 4 decimales, también los negativos y los mayores que 60");
        guardadas.Values.Count(v => v < 0).Should().Be(esperadas.Values.Count(v => v < 0));
        guardadas.Values.Count(v => v > 60).Should().Be(esperadas.Values.Count(v => v > 60));
    }

    /// <summary>Lo que debe quedar: clave normalizada y valor redondeado a 4 decimales (alejándose de cero, como SQL Server).</summary>
    private static Dictionary<string, decimal> Esperadas(string cuerpo)
    {
        using var documento = JsonDocument.Parse(cuerpo);
        return documento.RootElement.GetProperty("d").GetProperty("results").EnumerateArray().ToDictionary(
            f => $"{f.GetProperty("SalesOrgId").GetString()!.Trim().ToUpperInvariant()}|{f.GetProperty("TrqType").GetString()!.Trim().ToUpperInvariant()}|{f.GetProperty("OrderPartyId").GetString()!.Trim()}",
            f => Math.Round(decimal.Parse(f.GetProperty("Value").GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture), 4, MidpointRounding.AwayFromZero),
            StringComparer.Ordinal);
    }

    private static async Task<Dictionary<string, decimal>> GuardadasAsync(string cadena)
    {
        await using var conexion = new SqlConnection(cadena);
        await conexion.OpenAsync(Ct);
        await using var comando = new SqlCommand("SELECT salesOrgId, trqType, bpSap, margenRecomendado FROM dbo_cotifly.SapMargenCliente WHERE activo = 1;", conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        var guardadas = new Dictionary<string, decimal>(StringComparer.Ordinal);
        while (await lector.ReadAsync(Ct))
        {
            guardadas[$"{lector.GetString(0)}|{lector.GetString(1)}|{lector.GetString(2)}"] = lector.GetDecimal(3);
        }

        return guardadas;
    }
}
