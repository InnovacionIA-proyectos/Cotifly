using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// El cuerpo de <c>POST /api/captura-solicitudes</c> (<see cref="clsCosteoNexo.CuerpoCaptura"/>) de una ruta de contenedor
/// con peso con tara: <c>peso</c> = el peso con tara (repartido por camión). Según DU §8, con un solo camión la línea lleva además sus
/// <c>posiciones</c> (Nexo solo rechaza con <c>PESO_INCONSISTENTE</c> una suma que SUPERA el peso en más de 1 %; un peso mayor, la tara, es
/// válido) y el Nexo falso aplica esa misma regla asimétrica; con varios camiones, sin posiciones. Sin tara, el cuerpo no cambia.
/// </summary>
public sealed class clsCuerpoCapturaTaraTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static clsLineaSolicitud Ruta(string claseViaje, decimal? pesoConTaraKg, params decimal[] camiones)
    {
        var ruta = clsLineaSolicitud.Desde(Guid.NewGuid(), 1, 0, 1, new clsLineaSolicitudCosteo
        {
            Origen = "Buenaventura",
            Destino = "Bogotá, D.C.",
            OrigenMunicipioId = 76109,
            DestinoMunicipioId = 11001,
            Tipologia = "Contenedor",
            ClaseViaje = claseViaje,
            ClaseContenedor = claseViaje == "Contenedor" ? "CNT40.HC" : null,
            PesoKg = 28_000m,
            PesoConTaraKg = pesoConTaraKg,
            Posiciones = [new clsPosicionCarga("Estiba de café", 20, 120, 100, 150, 1_400m, CodigoMercancia: "CAFE_EN_ESTIBAS")],
        });
        foreach (var peso in camiones)
        {
            ruta.AgregarVehiculo("3S3", "FURGON", peso, Guid.NewGuid());
        }

        return ruta;
    }

    private static JsonNode Cuerpo(clsLineaSolicitud ruta) =>
        JsonNode.Parse(JsonSerializer.Serialize(
            clsCosteoNexo.CuerpoCaptura(new clsSolicitudNexo("15927", new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
                [.. ruta.VehiculosOrdenados.Select(ruta.ALineaNexo)], "Importacion")),
            _json))!;

    [Fact]
    public void ConTaraYUnCamionElPesoEsElPesoConTaraConSusPosicionesYNexoNoLoRechaza()
    {
        var cuerpo = Cuerpo(Ruta("Contenedor", 31_800m, 28_000m));

        var linea = cuerpo["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["peso"]!.GetValue<decimal>().Should().Be(31_800m);
        var posicion = linea["posiciones"]!.AsArray().Should().ContainSingle().Subject!;
        (posicion["cantidad"]!.GetValue<int>(), posicion["pesoKg"]!.GetValue<decimal>()).Should().Be((20, 1_400m), "la carga sin tara: 28000 kg");
        clsNexoFalso.LineaConPesoInconsistente(cuerpo).Should().BeNull("DU §8: un peso mayor que la suma de las posiciones es la tara");
    }

    [Theory]
    [InlineData(28_000, false)]
    [InlineData(40_000, false)]
    [InlineData(27_723, false)]
    [InlineData(27_722, true)]
    [InlineData(20_000, true)]
    public void ElNexoFalsoSoloRechazaUnaSumaDePosicionesQueSuperaElPesoEnMasDeUnoPorCiento(double peso, bool rechaza)
    {
        // DU §8: la regla de Nexo es asimétrica. Las posiciones suman 28000 kg: un peso mayor (con tara) es válido; uno menor solo
        // hasta 1 % (27723 kg: 28000 ≤ 27723 × 1,01); por debajo, PESO_INCONSISTENTE.
        var cuerpo = Cuerpo(Ruta("Contenedor", null, 28_000m));
        cuerpo["lineas"]![0]!["peso"] = (decimal)peso;

        clsNexoFalso.LineaConPesoInconsistente(cuerpo).Should().Be(rechaza ? 0 : null);
    }

    [Fact]
    public void ConTaraYVariosCamionesCadaLineaLlevaSuPesoMasSuParteDeLaTaraSinPosiciones()
    {
        var cuerpo = Cuerpo(Ruta("Contenedor", 36_000m, 16_000m, 12_000m));

        var lineas = cuerpo["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Select(l => l["peso"]!.GetValue<decimal>()).Should().Equal(20_000m, 16_000m);
        lineas.Should().OnlyContain(l => !l.AsObject().ContainsKey("posiciones"));
        clsNexoFalso.LineaConPesoInconsistente(cuerpo).Should().BeNull();
    }

    [Theory]
    [InlineData("Contenedor")]
    [InlineData("Cupo")]
    public void SinTaraElCuerpoNoCambia(string claseViaje)
    {
        var uno = Cuerpo(Ruta(claseViaje, null, 27_500m));
        var varios = Cuerpo(Ruta(claseViaje, null, 16_000m, 12_000m));

        uno["lineas"]![0]!["peso"]!.GetValue<decimal>().Should().Be(28_000m, "con un camión, el peso declarado de la ruta");
        uno["lineas"]![0]!["posiciones"]!.AsArray().Should().ContainSingle().Which!["pesoKg"]!.GetValue<decimal>().Should().Be(1_400m);
        varios["lineas"]!.AsArray().Select(l => l!["peso"]!.GetValue<decimal>()).Should().Equal(16_000m, 12_000m);
        varios["lineas"]!.AsArray().Should().OnlyContain(l => !l!.AsObject().ContainsKey("posiciones"));
        clsNexoFalso.LineaConPesoInconsistente(uno).Should().BeNull();
    }
}
