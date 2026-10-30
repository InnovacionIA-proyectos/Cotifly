using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// Integración 1021: el peso con tara de una ruta de contenedor (HU-030.1, #184) junto con el rechazo de la línea por la Mesa de Costeo
/// (HU-028.1, #183, decisión del 2026-10-20: los camiones rechazados no se reenvían). Regla: la tara total es el peso con tara menos la
/// suma de los pesos de carga de las líneas de TODOS los camiones; la tara de un contenedor es esa tara dividida entre el número TOTAL de
/// camiones (uno por contenedor); solo la llevan los camiones que viajan (los que no están <c>FueraDelEnvio</c>) y el último que viaja
/// suma el resto del redondeo. Un rechazado fuera del envío no viaja ni lleva tara.
/// </summary>
public sealed class clsTaraConRechazoTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);
    private readonly clsPrecioCosteoService _servicio = new();

    [Fact]
    public void EnUnaRutaContenedorConTaraElRechazadoNoViajaNiLlevaTaraYLosDemasSiConSuParte()
    {
        // Tres contenedores de 10000 kg de carga; peso con tara 36000: 6000 kg de tara, 2000 por contenedor.
        var cotizacion = RutaContenedor(36_000m);
        var ruta = cotizacion.RutasOrdenadas[0];
        var (primero, segundo, tercero) = (ruta.VehiculosOrdenados[0], ruta.VehiculosOrdenados[1], ruta.VehiculosOrdenados[2]);
        ruta.VehiculosOrdenados.Select(ruta.PesoLineaNexo).Should().Equal(12_000m, 12_000m, 12_000m);

        Aplicar(cotizacion, Foto([Linea("N-1"), Linea("N-2", rechazo: "Sin cupo de contenedor"), Linea("N-3")], [("N-1", 2_000_000m), ("N-3", 2_000_000m)]));

        segundo.Rechazado.Should().BeTrue();
        segundo.FueraDelEnvio.Should().BeFalse("su línea es de la solicitud actual: viajó con su tara");
        ruta.PesoConTaraCamion(segundo).Should().Be(12_000m, "lo que ya viajó no cambia (el emparejador vuelve a calcular el mismo peso)");

        cotizacion.PrepararReSolicitud();

        segundo.FueraDelEnvio.Should().BeTrue();
        ruta.VehiculosQueViajan.Should().Equal([primero, tercero]);
        var lineas = cotizacion.LineasNexo();
        lineas.Select(l => l.Camion).Should().Equal([primero, tercero], "el rechazado no viaja");
        lineas.Select(l => l.Linea.PesoKg).Should().Equal([12_000m, 12_000m], "cada camión que viaja lleva su carga y la tara de SU contenedor");
        lineas.Should().OnlyContain(l => l.Linea.Posiciones.Count == 0 && l.Linea.PesoConTaraKg == null);
        ruta.PesoConTaraCamion(segundo).Should().BeNull("un rechazado fuera del envío no lleva tara");
        ruta.PesoConTaraCamion(primero).Should().Be(12_000m);
        clsEnvioCosteoService.NotaCamionesPorRuta(cotizacion).Should().Contain("Ruta 1 (Buenaventura → Bogotá, D.C.): 2 camiones.");
    }

    [Fact]
    public void LaReSolicitudConUnRechazadoEmparejaLasLineasNuevasPorElPesoConTaraYCotizaSoloLosQueViajan()
    {
        var cotizacion = RutaContenedor(36_000m);
        var ruta = cotizacion.RutasOrdenadas[0];
        var (primero, segundo, tercero) = (ruta.VehiculosOrdenados[0], ruta.VehiculosOrdenados[1], ruta.VehiculosOrdenados[2]);
        Aplicar(cotizacion, Foto([Linea("N-1"), Linea("N-2", rechazo: "Sin cupo de contenedor"), Linea("N-3")], [("N-1", 2_000_000m), ("N-3", 2_000_000m)]));

        cotizacion.PrepararReSolicitud();
        cotizacion.RegistrarEnvioCosteo("SOL-2", false, _ahora, _ahora);
        // La solicitud nueva solo trae las dos líneas que viajaron, con el peso con tara que se mandó (12000 kg cada una).
        Aplicar(cotizacion, Foto([Linea("M-1"), Linea("M-3")], [("M-1", 2_100_000m), ("M-3", 2_100_000m)]), _ahora.AddHours(1));

        (primero.LineaSolicitudNexoId, tercero.LineaSolicitudNexoId, segundo.LineaSolicitudNexoId).Should().Be(("M-1", "M-3", (string?)null));
        (segundo.EstadoCosteo, segundo.MotivoRechazo).Should().Be(((EstadoCosteo?)EstadoCosteo.Rechazada, (string?)"Sin cupo de contenedor"));
        ruta.CostoFletesTotal.Should().Be(4_200_000m, "solo los dos camiones que viajaron");
        cotizacion.MotivoEstadoCosteo.Should().BeNull("no quedan camiones sin asociar");
        cotizacion.PrecioFinal.Should().Be(4_941_176m, "4.200.000 con margen del 15 %: el rechazado no cuenta");
        clsReSolicitudCosteoService.AvisoRechazos(cotizacion, versionNueva: false).Should().Contain("1 de 3 camiones");
    }

    [Fact]
    public void ElRedondeoDeLaTaraLoAbsorbeElUltimoCamionQueViaja()
    {
        // 6666.67 kg de tara entre 3 contenedores: 2222.22 cada uno y 0.01 de resto.
        var cotizacion = RutaContenedor(36_666.67m);
        var ruta = cotizacion.RutasOrdenadas[0];
        var (primero, segundo, tercero) = (ruta.VehiculosOrdenados[0], ruta.VehiculosOrdenados[1], ruta.VehiculosOrdenados[2]);
        ruta.VehiculosOrdenados.Select(ruta.PesoLineaNexo).Should().Equal([12_222.22m, 12_222.22m, 12_222.23m], "sin rechazos, la regla de #184");

        Aplicar(cotizacion, Foto([Linea("N-1"), Linea("N-2"), Linea("N-3", rechazo: "Sin tarifa")], [("N-1", 2_000_000m), ("N-2", 2_000_000m)]));
        cotizacion.PrepararReSolicitud();

        cotizacion.LineasNexo().Select(l => (l.Camion, l.Linea.PesoKg)).Should().Equal(
            [(primero, (decimal?)12_222.22m), (segundo, (decimal?)12_222.23m)], "el último que viaja suma el resto; el rechazado no carga a nadie");
        ruta.PesoConTaraCamion(tercero).Should().BeNull();
    }

    [Fact]
    public void EnLaVersionNuevaElCamionCopiadoComoRechazadoNoLlevaTaraYAlReincluirloVuelveATenerla()
    {
        var linea = LineaContenedor(36_000m);
        var ruta = clsLineaSolicitud.Desde(Guid.NewGuid(), 1, 0, 1, linea);
        var primero = ruta.AgregarVehiculo(new clsCamionPlaneado("3S3", null, 10_000m));
        var copiado = ruta.AgregarVehiculo(new clsCamionPlaneado("3S3", null, 10_000m, Rechazo: new clsRechazoHeredado("Sin tarifa", _ahora)));
        var tercero = ruta.AgregarVehiculo(new clsCamionPlaneado("3S3", null, 10_000m));

        copiado.FueraDelEnvio.Should().BeTrue("la -vN lo copia rechazado y sin línea");
        ruta.VehiculosQueViajan.Should().Equal([primero, tercero]);
        ruta.PesoLineaNexo(primero).Should().Be(12_000m);
        ruta.PesoLineaNexo(tercero).Should().Be(12_000m);
        ruta.PesoConTaraCamion(copiado).Should().BeNull();

        // reincluir_rechazados (edición): el rechazo se olvida, deja de estar fuera del envío y vuelve a llevar su tara.
        ruta.ReincluirRechazados().Should().Be(1);
        ruta.ReiniciarCosteo();

        copiado.FueraDelEnvio.Should().BeFalse();
        ruta.VehiculosQueViajan.Should().HaveCount(3);
        ruta.VehiculosOrdenados.Select(ruta.PesoLineaNexo).Should().Equal(12_000m, 12_000m, 12_000m);
    }

    [Fact]
    public void ConUnSoloContenedorRechazadoLaRutaNoViajaYSinTaraNoHayNadaQueRepartir()
    {
        var linea = LineaContenedor(31_800m) with { PesoKg = 28_000m };
        var ruta = clsLineaSolicitud.Desde(Guid.NewGuid(), 1, 0, 1, linea);
        var unico = ruta.AgregarVehiculo(new clsCamionPlaneado("3S3", null, 28_000m, Rechazo: new clsRechazoHeredado("Sin tarifa", _ahora)));

        ruta.ViajaANexo.Should().BeFalse();
        ruta.PesoConTaraCamion(unico).Should().BeNull();
    }

    // ------------------------------------------------------------------ Apoyo

    private bool Aplicar(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset? cuando = null) =>
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, cuando ?? _ahora, new clsOpcionesCosteoNexo(), _servicio);

    private static clsLineaSolicitudCosteo LineaContenedor(decimal pesoConTaraKg) => new()
    {
        Origen = "Buenaventura",
        Destino = "Bogotá, D.C.",
        Tipologia = "Contenedor",
        ClaseViaje = "Contenedor",
        ClaseContenedor = "CNT40.HC",
        TipoVehiculo = "3S3",
        PesoKg = 10_000m,
        PesoConTaraKg = pesoConTaraKg,
    };

    /// <summary>
    /// Una ruta Contenedor (Buenaventura → Bogotá) con tres camiones de 10000 kg de carga y <paramref name="pesoConTaraKg"/>, enviada a
    /// Nexo con margen del 15 %; los camiones ya tienen sus líneas N-1, N-2 y N-3.
    /// </summary>
    private static clsCotizacionSolicitud RutaContenedor(decimal pesoConTaraKg)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-TAR-21102026-001" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [LineaContenedor(pesoConTaraKg)], [0], 3);
        var ruta = cotizacion.RutasOrdenadas[0];
        ruta.AgregarVehiculo("3S3", null, 10_000m);
        ruta.AgregarVehiculo("3S3", null, 10_000m);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        ruta.VehiculosOrdenados[0].AsignarLineaNexo("N-1");
        ruta.VehiculosOrdenados[1].AsignarLineaNexo("N-2");
        ruta.VehiculosOrdenados[2].AsignarLineaNexo("N-3");
        return cotizacion;
    }

    private static clsLineaSolicitudNexo Linea(string id, string? rechazo = null) =>
        new(id, "Buenaventura → Bogotá, D.C.", "Contenedor", 12_000m, "3S3", null)
        {
            EstadoLinea = rechazo is not null ? "rechazada" : "porCostear",
            MotivoRechazo = rechazo,
        };

    private static clsCosteoConsultadoNexo Foto(IReadOnlyList<clsLineaSolicitudNexo> detalle, IReadOnlyList<(string Id, decimal Flete)> fletes) =>
        new("EnCosteo", [.. detalle.Select(d => d.Id)], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir",
            [.. fletes.Select(f => new clsLineaCosteoNexo(f.Id, f.Flete, null, null))], []))
        {
            Detalle = detalle,
        };
}
