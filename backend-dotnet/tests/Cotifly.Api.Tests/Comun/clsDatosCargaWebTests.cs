using System.Text.Json;
using Cotifly.Api.Costeo.DTOs;

namespace Cotifly.Api.Tests.Comun;

/// <summary>
/// HU-028 (#186): defectos A y B de <c>datos_carga</c> (<c>DatosCargaWeb</c>, obsoleto pero aceptado): las mercancías conservan
/// <c>codigo_mercancia</c> y <c>nombre_mercancia</c>, y las ciudades de devolución y de levante llevan su id de municipio.
/// </summary>
public sealed class clsDatosCargaWebTests
{
    [Fact]
    public void LasPosicionesConservanElCodigoYElNombreDeLaMercancia()
    {
        using var datos = JsonDocument.Parse(
            """{"posiciones":[{"codigo_mercancia":" BEBIDAS ","nombre_mercancia":"Bebidas","cantidad":3,"largo_cm":120,"ancho_cm":100,"alto_cm":150,"peso_kg":200}]}""");

        var cambios = clsDatosCargaWebRequest.ACambios(datos.RootElement).Valor;

        var posicion = cambios.Posiciones.Should().ContainSingle().Subject;
        (posicion.CodigoMercancia, posicion.Descripcion, posicion.Cantidad).Should().Be(("BEBIDAS", "Bebidas", 3));
    }

    [Fact]
    public void LasCiudadesDelContenedorLlevanSuIdDeMunicipio()
    {
        using var datos = JsonDocument.Parse(
            """{"ciudad_devolucion_contenedor":"Cali","ciudad_devolucion_municipio_id":76001,"ciudad_levante_contenedor":"Yumbo","ciudad_levante_municipio_id":76892}""");

        var cambios = clsDatosCargaWebRequest.ACambios(datos.RootElement).Valor;

        (cambios.CiudadDevolucionMunicipioId, cambios.CiudadLevanteMunicipioId).Should().Be((76001, 76892));
        cambios.Presentes.Should().Contain(["ciudad_devolucion_municipio_id", "ciudad_levante_municipio_id"]);
    }
}
