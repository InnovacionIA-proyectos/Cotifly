using Cotifly.Infraestructura.Comun.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-045.2 (#264), validación adversarial de la sonda: cualquiera de las columnas del script 20, sola, pide el 20; el 20 junto con un
/// script anterior pide el anterior; y todas juntas también son "falta 20" (base sin el script aplicado).
/// </summary>
public sealed class clsSondaScript20AdversarialTests
{
    public static TheoryData<string> ColumnasDel20() => [.. clsBaseDatosHealthCheck.Columnas20];

    [Theory]
    [MemberData(nameof(ColumnasDel20))]
    public void CadaColumnaDel20SolaPideEl20(string columna)
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar([columna], cotizacionesViejas: false);

        resultado.Status.Should().Be(HealthStatus.Unhealthy);
        resultado.Description.Should().Be(clsBaseDatosHealthCheck.DescripcionEsquemaSin20);
    }

    [Fact]
    public void TodoElScript20FaltantePideEl20YNoNombraColumnasNiConexion()
    {
        var resultado = clsBaseDatosHealthCheck.Clasificar([.. clsBaseDatosHealthCheck.Columnas20], cotizacionesViejas: false);

        resultado.Description.Should().Be("esquema incompleto: falta 20");
    }

    [Fact]
    public void ElScript20MapeaLaTablaYLasTresColumnasDeLaRuta()
    {
        clsBaseDatosHealthCheck.Columnas20.Should().BeEquivalentTo(
        [
            "TarifasSeguro.id", "TarifasSeguro.tasa", "TarifasSeguro.vigenteDesde", "TarifasSeguro.isActive", "TarifasSeguro.updatedAt",
            "CotizacionLineasSolicitud.incluyeSeguro", "CotizacionLineasSolicitud.tasaSeguroAplicada", "CotizacionLineasSolicitud.costoSeguro",
        ]);
        clsBaseDatosHealthCheck.Columnas19.Overlaps(clsBaseDatosHealthCheck.Columnas20).Should().BeFalse("cada script tiene sus columnas");
    }
}
