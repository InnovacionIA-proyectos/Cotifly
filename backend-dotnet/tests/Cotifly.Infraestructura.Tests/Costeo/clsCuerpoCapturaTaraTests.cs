using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// HU-030.1 (#184): el cuerpo de <c>POST /api/captura-solicitudes</c> (<see cref="clsCosteoNexo.CuerpoCaptura"/>) de una ruta de contenedor
/// con peso con tara: <c>peso</c> = el peso con tara (repartido por camión) y SIN <c>posiciones</c>; la regla de <c>PESO_INCONSISTENTE</c>
/// del Nexo falso (la de Nexo, NEXO#308) lo acepta, y rechazaría la misma línea si llevara las posiciones. Sin tara, el cuerpo no cambia.
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
    public void ConTaraYUnCamionElPesoEsElPesoConTaraSinPosicionesYNexoNoLoRechaza()
    {
        var cuerpo = Cuerpo(Ruta("Contenedor", 31_800m, 28_000m));

        var linea = cuerpo["lineas"]!.AsArray().Should().ContainSingle().Subject!;
        linea["peso"]!.GetValue<decimal>().Should().Be(31_800m);
        linea.AsObject().ContainsKey("posiciones").Should().BeFalse();
        clsNexoFalso.LineaConPesoInconsistente(cuerpo).Should().BeNull();
    }

    [Fact]
    public void LaMismaLineaConTaraYConPosicionesLaRechazariaNexoPorPesoInconsistente()
    {
        // Control: si la línea con tara viajara con sus posiciones (28000 kg), Nexo (y su falso) la rechazarían: por eso no viajan.
        var cuerpo = Cuerpo(Ruta("Contenedor", null, 28_000m));
        cuerpo["lineas"]![0]!["peso"] = 31_800m;

        clsNexoFalso.LineaConPesoInconsistente(cuerpo).Should().Be(0);
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
