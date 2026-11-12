using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// La sonda y el script 21: cualquiera de sus columnas, sola, pide el 21; el 21 junto con un
/// script anterior pide el anterior; y todas juntas también son "falta 21" (base sin el script aplicado).
/// </summary>
public sealed class clsSondaScript21Tests
{
    public static TheoryData<string> ColumnasDel21() => [.. clsBaseDatosHealthCheck.Columnas21];

    [Theory]
    [MemberData(nameof(ColumnasDel21))]
    public void CadaColumnaDel21SolaPideEl21(string columna)
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar([columna], cotizacionesViejas: false);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionEsquemaSin21);
    }

    [Fact]
    public void TodoElScript21FaltantePideEl21YNoNombraColumnasNiConexion()
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar([.. clsBaseDatosHealthCheck.Columnas21], cotizacionesViejas: false);

        resultado.Description.Should().Be("esquema incompleto: falta 21");
    }

    [Fact]
    public void ElScript21MapeaLasCuatroTablasYElCodigoDelConexo()
    {
        clsBaseDatosHealthCheck.Columnas21.Should().HaveCount(22);
        clsBaseDatosHealthCheck.Columnas21.Select(c => c.Split('.')[0]).Distinct().Should().BeEquivalentTo(
            ["PoliticaSeguroTramos", "PoliticaSeguroMedidas", "PoliticaSeguroTramoMedidas", "PoliticaSeguroMedidaConexos", "CotizacionServiciosConexos"]);
        clsBaseDatosHealthCheck.Columnas21.Should().Contain("CotizacionServiciosConexos.codigoServicio");
        clsBaseDatosHealthCheck.Columnas20.Overlaps(clsBaseDatosHealthCheck.Columnas21).Should().BeFalse("cada script tiene sus columnas");
    }

    [Fact]
    public void LasColumnasDel21SonLasQueMapeaElModelo()
    {
        using var proveedor = clsServiciosPruebas.Crear(null, new clsRelojFalso());
        using var ambito = proveedor.CreateScope();
        var modelo = ambito.ServiceProvider.GetRequiredService<clsCotiflyDbContext>().Model;
        var mapeadas = modelo.GetEntityTypes()
            .Where(e => e.GetTableName() is { } t && t.StartsWith("PoliticaSeguro", StringComparison.Ordinal))
            .SelectMany(e => e.GetProperties().Select(p => $"{e.GetTableName()}.{p.GetColumnName()}"))
            .Append("CotizacionServiciosConexos.codigoServicio");

        mapeadas.Should().BeEquivalentTo(clsBaseDatosHealthCheck.Columnas21);
    }
}
