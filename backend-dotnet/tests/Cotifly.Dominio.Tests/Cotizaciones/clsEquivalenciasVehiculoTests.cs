using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Cotifly guarda el código original (<c>C2</c>) y compara contra el que viajó traducido por el alias de Nexo (<c>2</c>).
/// </summary>
public sealed class clsEquivalenciasVehiculoTests
{
    private static readonly Dictionary<string, string> _equivalencias = new() { ["C2"] = "2", ["turbo"] = "2L2" };

    [Theory]
    [InlineData("C2", "2")]
    [InlineData("c2", "2")]
    [InlineData("Turbo", "2L2")]
    [InlineData("3S3", "3S3")]
    [InlineData(null, null)]
    public void ElCodigoEnviadoEsLaTraduccionRegistrada(string? codigo, string? enviado) =>
        clsEquivalenciasVehiculo.Enviado(codigo, _equivalencias).Should().Be(enviado);

    [Fact]
    public void SinEquivalenciasElCodigoEsElMismo() => clsEquivalenciasVehiculo.Enviado("C2", null).Should().Be("C2");

    [Theory]
    [InlineData("C2", "2", "C2")]          // Nexo devolvió lo que se le envió: no es un cambio de la Mesa
    [InlineData("C2", " 2 ", "C2")]
    [InlineData("C2", "3S3", "3S3")]       // la Mesa cambió el vehículo: manda
    [InlineData("C2", "c2", "c2")]         // la misma sigla de Cotifly: D2 ya la reconoce (MismoCodigo)
    [InlineData("3S3", "2", "2")]          // sin traducción registrada para 3S3
    [InlineData(null, "2", "2")]           // camión sin vehículo: la Mesa lo llena
    [InlineData("C2", null, null)]
    public void LoQueDevuelveNexoSeVeDesdeCotifly(string? actual, string? deNexo, string? esperado) =>
        clsEquivalenciasVehiculo.DeNexo(actual, deNexo, _equivalencias).Should().Be(esperado);

    [Fact]
    public void ElEmparejadorComparaConElCodigoQueViajo()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-ALIAS-1" }).Valor;
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
        [
            new clsRutaCapturada(new clsLineaSolicitudCosteo { Origen = "Buenaventura", Destino = "Bogotá, D.C.", OrigenMunicipioId = 76109, DestinoMunicipioId = 11001, PesoKg = 4_000m }, 3, 0,
                [new clsCamionPlaneado("C2", "FURGON", 4_000m, Guid.NewGuid())], []),
        ]);
        var ruta = cotizacion.RutasOrdenadas[0];
        var linea = new clsLineaSolicitudNexo("N1", "BUENAVENTURA → BOGOTA, D.C.", null, 4_000m, "2", "FURGON");

        var conTraduccion = clsCamionParaEmparejar.Desde(ruta, cotizacion.Vehiculos[0], codigosCubicador: true, _equivalencias);

        conTraduccion.TipoVehiculo.Should().Be("2");
        clsEmparejadorLineasNexo.Coincide(conTraduccion, linea).Should().BeTrue();
        clsEmparejadorLineasNexo.Coincide(clsCamionParaEmparejar.Desde(ruta, cotizacion.Vehiculos[0]), linea).Should().BeFalse();
        cotizacion.Vehiculos[0].TipoVehiculo.Should().Be("C2", "Cotifly conserva el código del cubicador");
    }
}
