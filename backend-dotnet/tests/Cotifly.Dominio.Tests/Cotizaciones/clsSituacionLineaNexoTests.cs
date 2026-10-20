using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// #181 (Nexo #309): el estado de la línea de Nexo pasa a código de Cotifly (<c>snake_case</c>) y el motivo pendiente se muestra tal cual;
/// sin los campos (Nexo anterior a #309) la situación queda vacía. No toca el emparejamiento.
/// </summary>
public sealed class clsSituacionLineaNexoTests
{
    [Theory]
    [InlineData("porCostear", "por_costear")]
    [InlineData("PorCostear", "por_costear")]
    [InlineData("autoResuelta", "auto_resuelta")]
    [InlineData("AutoResuelta", "auto_resuelta")]
    [InlineData("revisada", "revisada")]
    [InlineData(" rechazada ", "rechazada")]
    [InlineData("por_costear", "por_costear")]
    [InlineData("PORCOSTEAR", "porcostear")]
    [InlineData("Por Costear", "por_costear")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void ElEstadoDeNexoPasaACodigoDeCotifly(string? nexo, string? codigo) =>
        clsSituacionLineaNexo.CodigoEstado(nexo).Should().Be(codigo);

    [Fact]
    public void LaLineaDeNexoTraeSuSituacionYSinLosCamposQuedaVacia()
    {
        var conCampos = new clsLineaSolicitudNexo("L1", "A → B", null, 1m, null, null)
        {
            EstadoLinea = "porCostear",
            MotivoPendiente = "  Consulta completa: el Administrador de Tarifas debe elegir la tarifa.  ",
        };
        var sinCampos = new clsLineaSolicitudNexo("L1", "A → B", null, 1m, null, null);

        conCampos.Situacion.Should().Be(new clsSituacionLineaNexo("por_costear", "Consulta completa: el Administrador de Tarifas debe elegir la tarifa."));
        conCampos.Situacion.Vacia.Should().BeFalse();
        sinCampos.Situacion.Should().Be(new clsSituacionLineaNexo(null, null));
        sinCampos.Situacion.Vacia.Should().BeTrue();
        clsSituacionLineaNexo.Desde("revisada", " ").MotivoPendiente.Should().BeNull("un motivo en blanco no es un motivo");
    }

    [Fact]
    public void ElEstadoYElMotivoNoCambianElEmparejamiento()
    {
        var camion = new clsCamionParaEmparejar(Guid.NewGuid(), "A", "B", null, "3S3", null, 10_000m);
        var linea = new clsLineaSolicitudNexo("L1", "A → B", null, 10_000m, "3S3", null);

        clsEmparejadorLineasNexo.Coincide(camion, linea with { EstadoLinea = "porCostear", MotivoPendiente = "x" })
            .Should().Be(clsEmparejadorLineasNexo.Coincide(camion, linea));
        clsEmparejadorLineasNexo.Emparejar([camion], [linea with { EstadoLinea = "revisada" }])
            .Should().BeEquivalentTo(clsEmparejadorLineasNexo.Emparejar([camion], [linea]));
    }
}
