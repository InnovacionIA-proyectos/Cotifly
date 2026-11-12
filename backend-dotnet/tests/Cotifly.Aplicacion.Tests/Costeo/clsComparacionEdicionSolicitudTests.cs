using Cotifly.Aplicacion.CatalogosFlete.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// "¿La edición cambió algo?" se decide por VALOR. Un <c>decimal</c> de SQL Server conserva la escala
/// de la columna (<c>36666.60</c>) y el del JSON reenviado no (<c>36666.6</c>); el <c>deadline</c> puede venir en otra zona; los códigos con
/// otras mayúsculas o espacios y los conexos en otro orden son la misma solicitud. Un cambio real (+1 kg, otra cantidad) sí cuenta.
/// El vehículo digitado queda escrito como en el catálogo de Nexo.
/// </summary>
public sealed class clsComparacionEdicionSolicitudTests
{
    [Fact]
    public void LaMismaRutaConDecimalesDeOtraEscalaEsIgual()
    {
        var guardada = Ruta(36666.60m, 120.00m, 50000000.00m);
        var reenviada = Ruta(36666.6m, 120.0m, 50000000m);

        clsEdicionSolicitudService.MismaRuta(guardada, reenviada).Should().BeTrue("36666.60 y 36666.6 son el mismo valor");
        clsReglaRecubicacion.Exige(guardada, reenviada).Should().BeFalse("la misma carga no se vuelve a cubicar");
    }

    [Fact]
    public void LosCodigosConOtrasMayusculasOEspaciosYLosBooleanosSinValorComoFalseSonLaMismaRuta()
    {
        var guardada = Ruta(36666.60m, 120.00m, 50000000.00m) with { ClaseViaje = "Carga Suelta", RetornaContenedor = false, LevanteContenedorDiferente = null };
        var reenviada = guardada with
        {
            ClaseViaje = " carga suelta ",
            Tipologia = guardada.Tipologia?.ToLowerInvariant(),
            RetornaContenedor = null,
            LevanteContenedorDiferente = false,
            Posiciones = [.. guardada.Posiciones.Select(p => p with { CodigoMercancia = $" {p.CodigoMercancia?.ToLowerInvariant()} ", Descripcion = "otro nombre" })],
        };

        clsEdicionSolicitudService.MismaRuta(guardada, reenviada).Should().BeTrue();
    }

    [Fact]
    public void UnCambioRealSiSeDetecta()
    {
        var guardada = Ruta(36666.60m, 120.00m, 50000000.00m);

        clsEdicionSolicitudService.MismaRuta(guardada, guardada with { PesoKg = 36667.6m }).Should().BeFalse("+1 kg es un cambio");
        clsEdicionSolicitudService.MismaRuta(guardada, guardada with { ValorMercancia = 50000000.01m }).Should().BeFalse();
        clsEdicionSolicitudService.MismaRuta(guardada, guardada with { HorasCargue = 5m }).Should().BeFalse();
        clsEdicionSolicitudService.MismaRuta(guardada, guardada with { DestinoMunicipioId = 76001 }).Should().BeFalse("el municipio se compara por id");
        clsEdicionSolicitudService.MismaRuta(guardada, guardada with { Posiciones = [guardada.Posiciones[0] with { Cantidad = guardada.Posiciones[0].Cantidad + 1 }, .. guardada.Posiciones.Skip(1)] })
            .Should().BeFalse();
        clsEdicionSolicitudService.MismaRuta(guardada, guardada with { Posiciones = [.. guardada.Posiciones.Reverse()] })
            .Should().BeFalse("las mercancías viajan al cubicador en su orden");
    }

    [Fact]
    public void LosConexosSeComparanPorIdYCantidadSinImportarElOrdenNiLaEscala()
    {
        (string?, decimal?)[] guardados = [("CX-1", 2.00m), ("cx-2", 1.0m)];

        clsEdicionSolicitudService.MismosConexos(guardados, [("CX-2", 1m), (" cx-1 ", 2m)]).Should().BeTrue();
        clsEdicionSolicitudService.MismosConexos(guardados, [("CX-2", 1m), ("CX-1", 3m)]).Should().BeFalse("otra cantidad");
        clsEdicionSolicitudService.MismosConexos(guardados, [("CX-2", 1m)]).Should().BeFalse("se quitó uno");
        clsEdicionSolicitudService.MismosConexos(guardados, [("CX-2", 1m), ("CX-1", 2m), ("CX-3", 1m)]).Should().BeFalse("se agregó uno");
        clsEdicionSolicitudService.MismosConexos([], []).Should().BeTrue();
    }

    [Fact]
    public void ElDeadlineEsElMismoInstanteEnOtraZona()
    {
        var utc = new DateTimeOffset(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);

        clsEdicionSolicitudService.MismoInstante(utc, new DateTimeOffset(2026, 10, 15, 17, 0, 0, TimeSpan.FromHours(-5))).Should().BeTrue();
        clsEdicionSolicitudService.MismoInstante(utc, utc.AddMinutes(1)).Should().BeFalse();
        clsEdicionSolicitudService.MismoInstante(null, null).Should().BeTrue();
        clsEdicionSolicitudService.MismoInstante(utc, null).Should().BeFalse();
    }

    [Theory]
    [InlineData("2s2", "furgon", "2S2", "FURGON")]
    [InlineData(" 3s3 ", "furgón", "3S3", "FURGON")]
    [InlineData("3S3", "furgon_refrigerado", "3S3", "FURGON REFRIGERADO")]
    [InlineData("C2", "CAMA_BAJA", "C2", "CAMA_BAJA")]
    public void D1019B2_ElVehiculoDigitadoQuedaEscritoComoEnNexo(string tipo, string carroceria, string tipoEsperado, string carroceriaEsperada)
    {
        var catalogos = new clsCatalogosMaestros([new("2S2", "2S2"), new("3S3", "3S3")], [], [], []);
        var linea = clsSolicitudCosteoMuestra.RutaCaptura() with { TipoVehiculo = tipo, Carroceria = carroceria };

        var canonica = clsValidadorCatalogosCaptura.Canonica(linea, catalogos);

        (canonica.TipoVehiculo, canonica.Carroceria).Should().Be((tipoEsperado, carroceriaEsperada), "un código que Nexo no conoce queda tal cual (ya trae su error)");
    }

    [Fact]
    public void D1019B2_SinCambiosDevuelveLaMismaLinea()
    {
        var linea = clsSolicitudCosteoMuestra.RutaCaptura() with { TipoVehiculo = null, Carroceria = null };

        clsValidadorCatalogosCaptura.Canonica(linea, null).Should().BeSameAs(linea);
    }

    private static clsLineaSolicitudCosteo Ruta(decimal peso, decimal medida, decimal valor)
    {
        var linea = clsSolicitudCosteoMuestra.RutaCaptura();
        return linea with
        {
            PesoKg = peso,
            ValorMercancia = valor,
            HorasCargue = 4.00m,
            Posiciones = [.. linea.Posiciones.Select(p => p with { LargoCm = medida, PesoKg = p.PesoKg is null ? null : p.PesoKg.Value * 1.00m })],
        };
    }
}
