using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// #278 (DU §5): el asistente propone a veces una sola hora ("cambia el cargue a 8 h"). Nexo recibe las horas en pareja, así que la otra
/// se completa con la guardada en la ruta o, si no hay, con la de Nexo por defecto (4, o 1 en una devolución de contenedor).
/// </summary>
public sealed class clsPropuestasAsistenteHorasTests
{
    [Fact]
    public void ConUnaSolaHoraLaOtraSeCompletaConLaGuardadaEnLaRuta()
    {
        var cotizacion = Cotizacion(clsSolicitudCosteoMuestra.Crear());

        var cambios = clsPropuestasAsistente.CompletarHoras(new clsCambiosCargaAsistente(HorasCargue: 8m), cotizacion, "PuertoAInterior");

        (cambios.HorasCargue, cambios.HorasDescargue).Should().Be((8m, 6.5m));
    }

    [Theory]
    [InlineData("PuertoAInterior", 4.0)]
    [InlineData("DevolucionContenedor", 1.0)]
    [InlineData("Devolución de contenedor", 1.0)]
    [InlineData(null, 4.0)]
    public void SinHoraGuardadaSeUsaLaDeNexoPorDefecto(string? lineaNegocioNexo, double esperada)
    {
        var cotizacion = Cotizacion(clsSolicitudCosteoMuestra.Crear() with { HorasCargue = null, HorasDescargue = null });

        var cambios = clsPropuestasAsistente.CompletarHoras(new clsCambiosCargaAsistente(HorasDescargue: 3m), cotizacion, lineaNegocioNexo);

        (cambios.HorasCargue, cambios.HorasDescargue).Should().Be(((decimal?)esperada, (decimal?)3m));
    }

    [Fact]
    public void ConLasDosONingunaLosCambiosNoCambian()
    {
        var cotizacion = Cotizacion(clsSolicitudCosteoMuestra.Crear());
        var ambas = new clsCambiosCargaAsistente(HorasCargue: 2m, HorasDescargue: 3m);
        var ninguna = new clsCambiosCargaAsistente(PesoKg: 28_000m);

        clsPropuestasAsistente.CompletarHoras(ambas, cotizacion, "PuertoAInterior").Should().Be(ambas);
        clsPropuestasAsistente.CompletarHoras(ninguna, cotizacion, "PuertoAInterior").Should().Be(ninguna);
    }

    [Fact]
    public void LaPropuestaCompletadaPasaLaValidacionDeLaLineaQueAntesFallaba()
    {
        // Antes de completar: una ruta sin horas guardadas y una sola hora propuesta rompe la pareja (CAMPO_REQUERIDO).
        var cotizacion = Cotizacion(clsSolicitudCosteoMuestra.Crear() with { HorasCargue = null, HorasDescargue = null });
        var propuesta = new clsCambiosCargaAsistente(HorasCargue: 8m);
        var linea = cotizacion.PrimeraRuta!.ALinea();

        Aplicar(propuesta, linea).ErroresHorasEnPareja().Should().Contain(e => e.Codigo == clsCodigosLineaSolicitud.CampoRequerido);
        Aplicar(clsPropuestasAsistente.CompletarHoras(propuesta, cotizacion, "PuertoAInterior"), linea).ErroresHorasEnPareja().Should().BeEmpty();
    }

    /// <summary>
    /// #278: el valor por defecto sale del mapeo a NEXO de la línea (<c>mapeoLineasNexo</c>), no del nombre de la línea de Cotifly: una
    /// línea "Contenedores Vacíos" mapeada a "Devolución de contenedor" completa con 1, y "DevolucionContenedor" mapeada a Nacional, con 4.
    /// </summary>
    [Theory]
    [InlineData("Contenedores Vacíos", "Devolución de contenedor", 1.0)]
    [InlineData("DevolucionContenedor", "Nacional", 4.0)]
    public async Task LaHerramientaUsaElMapeoANexoDeLaLineaParaElValorPorDefecto(string lineaCotifly, string mapeoNexo, double esperada)
    {
        var cotizacion = Cotizacion(clsSolicitudCosteoMuestra.Crear() with { HorasCargue = null, HorasDescargue = null });
        var contexto = new clsContextoAsistenteFolio();
        contexto.Iniciar(Guid.NewGuid(), cotizacion, lineaCotifly, "web/s1/" + cotizacion.Folio, mapeoNexo);
        var reSolicitud = Substitute.For<IClsReSolicitudCosteoService>();
        var herramientas = new clsHerramientasAsistenteFolio(contexto, Substitute.For<IClsServiciosConexosCosteoService>(), reSolicitud, Substitute.For<IClsCategoriasConexos>());

        await herramientas.ProponerReSolicitudAsync("El cliente cambió el horario", horasCargue: 8m, cancellationToken: TestContext.Current.CancellationToken);

        await reSolicitud.Received(1).ValidarAsync(Arg.Any<Guid>(), cotizacion,
            Arg.Is<clsEntradaReSolicitudCosteo>(e => e.DatosCarga!.HorasCargue == 8m && e.DatosCarga.HorasDescargue == (decimal)esperada),
            Arg.Any<CancellationToken>());
    }

    private static clsLineaSolicitudCosteo Aplicar(clsCambiosCargaAsistente cambios, clsLineaSolicitudCosteo linea) =>
        clsPropuestasAsistente.EntradaReSolicitud("Cambio de horario", cambios, nuevaVersion: true).DatosCarga!.Aplicar(linea);

    private static clsCotizacionSolicitud Cotizacion(clsSolicitudCosteo solicitud)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-AP-05112026-001" }).Valor;
        cotizacion.RegistrarSolicitudCosteo(solicitud, 3);
        return cotizacion;
    }
}
