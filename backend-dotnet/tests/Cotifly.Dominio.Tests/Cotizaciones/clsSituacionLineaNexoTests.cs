using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// El estado de la línea de Nexo pasa a código de Cotifly (<c>snake_case</c>) y el motivo pendiente se muestra tal cual;
/// si Nexo no manda los campos, la situación queda vacía. No toca el emparejamiento.
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
    [InlineData("PORCOSTEAR", "por_costear")]
    [InlineData("POR_COSTEAR", "por_costear")]
    [InlineData("Por Costear", "por_costear")]
    [InlineData("AUTORESUELTA", "auto_resuelta")]
    [InlineData("auto-resuelta", "auto_resuelta")]
    [InlineData("REVISADA", "revisada")]
    [InlineData("EnRevision", "en_revision")]
    [InlineData("ENREVISION", "enrevision")]
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
    public void LaLineaRechazadaTraeSuMotivoYCuentaComoRechazo()
    {
        var rechazada = new clsLineaSolicitudNexo("L1", "A → B", null, 1m, null, null) { EstadoLinea = "Rechazada", MotivoRechazo = "  Sin tarifa  " };

        rechazada.Situacion.Rechazada.Should().BeTrue("el enum de Nexo llega con cualquier mayúscula");
        rechazada.Situacion.EstadoLinea.Should().Be(clsSituacionLineaNexo.EstadoRechazada);
        rechazada.Situacion.MotivoRechazo.Should().Be("Sin tarifa");
        rechazada.Situacion.Vacia.Should().BeFalse();
        new clsLineaSolicitudNexo("L1", "A → B", null, 1m, null, null) { EstadoLinea = "porCostear", MotivoRechazo = "x" }.Situacion.Rechazada
            .Should().BeFalse("lo que decide el rechazo es el estado, no el motivo");
        clsSituacionLineaNexo.NormalizarMotivoRechazo(new string('x', 501))!.Length.Should().Be(clsSituacionLineaNexo.LargoMaximoMotivoRechazo);
        clsSituacionLineaNexo.NormalizarMotivoRechazo("   ").Should().BeNull();
    }

    [Fact]
    public void LaDerivacionNoEsperaLasLineasRechazadas()
    {
        var detalle = new[]
        {
            new clsLineaSolicitudNexo("L1", "A → B", null, null, null, null) { EstadoLinea = "autoResuelta" },
            new clsLineaSolicitudNexo("L2", "A → B", null, null, null, null) { EstadoLinea = "rechazada", MotivoRechazo = "x" },
        };
        var borrador = new clsCosteoConsultadoNexo("EnCosteo", ["L1", "L2"], new clsCotizacionNexo("C", "Borrador", "SinDefinir",
            [new clsLineaCosteoNexo("L1", 1m, null, null)], [])) { Detalle = detalle };

        clsDerivacionEstadoCosteo.Derivar(borrador).Should().Be(EstadoCosteo.Autocosteo, "todas las NO rechazadas tienen flete");
        clsDerivacionEstadoCosteo.DerivarLinea(borrador, "L2").Should().Be(EstadoCosteo.Rechazada);
        clsDerivacionEstadoCosteo.DerivarLinea(borrador with { Cotizacion = borrador.Cotizacion! with { Estado = "Entregada" } }, "L2")
            .Should().Be(EstadoCosteo.Rechazada, "entregada, la línea sigue rechazada");
        clsDerivacionEstadoCosteo.DerivarLinea(borrador with { EstadoSolicitud = "Descartada" }, "L2").Should().Be(EstadoCosteo.Descartada);
        clsDerivacionEstadoCosteo.Derivar(borrador with { Cotizacion = borrador.Cotizacion! with { Lineas = [] }, Detalle = [detalle[1]], LineasSolicitud = ["L2"] })
            .Should().Be(EstadoCosteo.PendienteCosteo, "con todas rechazadas y sin entregar no hay nada que cotizar todavía");
        clsDerivacionEstadoCosteo.EsTerminal(EstadoCosteo.Rechazada).Should().BeFalse("la Mesa puede deshacer el rechazo");
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
