using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// El estado de la línea de Nexo y el motivo por el que sigue pendiente, por camión: lo que el polling deja a la vista
/// (en memoria, sin base) y cómo sale en <c>CamionCosteo</c> de <c>GET .../costeo</c>.
/// </summary>
public sealed class clsSituacionLineasNexoTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);
    private const string SinVehiculo = "Línea sin vehículo: requiere consulta completa";

    [Fact]
    public void CadaCamionTomaLaSituacionDeSuLineaDeNexo()
    {
        var cotizacion = Ruta(("3S3", 10_000m), ("2S2", 8_000m));
        var foto = Foto(
            [
                Detalle("N-b", "2S2", 8_000m) with { EstadoLinea = "autoResuelta" },
                Detalle("N-a", "3S3", 10_000m) with { EstadoLinea = "porCostear", MotivoPendiente = SinVehiculo },
            ],
            [("N-b", 600_000m)]);
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, _ahora, new clsOpcionesCosteoNexo(), new clsPrecioCosteoService());

        var situaciones = clsLecturaCosteo.SituacionesPorCamion(cotizacion, foto);

        var grande = cotizacion.Vehiculos.Single(v => v.PesoKg == 10_000m);
        var mediano = cotizacion.Vehiculos.Single(v => v.PesoKg == 8_000m);
        situaciones.Should().BeEquivalentTo(new Dictionary<Guid, clsSituacionLineaNexo>
        {
            [grande.Id] = new("por_costear", SinVehiculo) { LineaNexoId = "N-a" },
            [mediano.Id] = new("auto_resuelta", null) { LineaNexoId = "N-b" },
        });

        var vista = clsCosteoCotizacionService.Vista(cotizacion, null, situaciones: situaciones);
        var camiones = vista.Rutas!.Single().Camiones!;
        camiones.Select(c => (c.PesoKg, c.EstadoCosteo, c.EstadoLineaNexo, c.MotivoPendiente)).Should().Equal(
            (10_000m, "pendiente_costeo", "por_costear", SinVehiculo),
            (8_000m, "autocosteo", "auto_resuelta", (string?)null));
    }

    [Fact]
    public void SinLosCamposDeNexoOSinCamionAsociadoNoHayNadaQueMostrar()
    {
        var cotizacion = Ruta(("3S3", 10_000m), ("2S2", 8_000m));
        // Un Nexo sin estadoLinea ni motivoPendiente y una sola línea: el 2S2 queda sin asociar.
        var foto = Foto([Detalle("N-a", "3S3", 10_000m)], []);
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, _ahora, new clsOpcionesCosteoNexo(), new clsPrecioCosteoService());

        clsLecturaCosteo.SituacionesPorCamion(cotizacion, foto).Should().BeEmpty();
        clsCosteoCotizacionService.Vista(cotizacion, null).Rutas!.Single().Camiones!
            .Should().OnlyContain(c => c.EstadoLineaNexo == null && c.MotivoPendiente == null);
    }

    [Fact]
    public void ElMotivoPendienteSoloSeMuestraConElCamionPendiente()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var camion = cotizacion.Vehiculos.Single();
        camion.AsignarLineaNexo("N-a");
        camion.RegistrarEstado(EstadoCosteo.Entregada);
        var situaciones = new Dictionary<Guid, clsSituacionLineaNexo> { [camion.Id] = new("por_costear", SinVehiculo) { LineaNexoId = "N-a" } };

        var vista = clsCosteoCotizacionService.CamionCosteo(camion, situaciones);

        vista.EstadoLineaNexo.Should().Be("por_costear");
        vista.MotivoPendiente.Should().BeNull("el camión ya no está pendiente en Cotifly");
        clsCosteoCotizacionService.CamionCosteo(camion).Should().Be(clsCosteoCotizacionService.CamionCosteo(camion, new Dictionary<Guid, clsSituacionLineaNexo>()));
    }

    [Fact]
    public void LaFotoSoloSeMuestraEnElCamionQueSigueAsociadoASuLinea()
    {
        var cotizacion = Ruta(("3S3", 10_000m));
        var foto = Foto([Detalle("N-a", "3S3", 10_000m) with { EstadoLinea = "porCostear", MotivoPendiente = SinVehiculo }], []);
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, _ahora, new clsOpcionesCosteoNexo(), new clsPrecioCosteoService());
        var situaciones = clsLecturaCosteo.SituacionesPorCamion(cotizacion, foto);
        var camion = cotizacion.Vehiculos.Single();
        clsCosteoCotizacionService.CamionCosteo(camion, situaciones).MotivoPendiente.Should().Be(SinVehiculo);

        // La línea en otra forma de escribir el id sigue siendo la misma.
        clsCosteoCotizacionService.CamionCosteo(camion, new Dictionary<Guid, clsSituacionLineaNexo>
        {
            [camion.Id] = new("por_costear", SinVehiculo) { LineaNexoId = "n-A" },
        }).EstadoLineaNexo.Should().Be("por_costear");

        // Una foto de otra línea (o sin línea conocida) no se muestra en este camión.
        clsCosteoCotizacionService.CamionCosteo(camion, new Dictionary<Guid, clsSituacionLineaNexo>
        {
            [camion.Id] = new("por_costear", "de otra línea") { LineaNexoId = "N-z" },
        }).Should().Match<clsCamionCosteo>(c => c.EstadoLineaNexo == null && c.MotivoPendiente == null);
        clsCosteoCotizacionService.CamionCosteo(camion, new Dictionary<Guid, clsSituacionLineaNexo>
        {
            [camion.Id] = new("por_costear", "sin línea"),
        }).Should().Match<clsCamionCosteo>(c => c.EstadoLineaNexo == null && c.MotivoPendiente == null);

        // Re-solicitud: el camión conserva su id y vuelve a pendiente_costeo, pero olvida su línea; la foto en memoria de la
        // solicitud anterior no se muestra hasta que el polling lea la nueva.
        cotizacion.PrepararReSolicitud();
        cotizacion.RegistrarEnvioCosteo("SOL-2", false, _ahora, _ahora);
        camion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        clsCosteoCotizacionService.CamionCosteo(camion, situaciones)
            .Should().Match<clsCamionCosteo>(c => c.EstadoLineaNexo == null && c.MotivoPendiente == null);

        // La solicitud nueva asocia otra línea: la foto vieja (N-a) sigue sin mostrarse.
        var nueva = Foto([Detalle("N-x", "3S3", 10_000m)], []);
        clsLecturaCosteo.AplicarFoto(cotizacion, nueva, _ahora, new clsOpcionesCosteoNexo(), new clsPrecioCosteoService());
        camion.LineaSolicitudNexoId.Should().Be("N-x");
        clsCosteoCotizacionService.CamionCosteo(camion, situaciones)
            .Should().Match<clsCamionCosteo>(c => c.EstadoLineaNexo == null && c.MotivoPendiente == null);
    }

    [Fact]
    public void LaMemoriaGuardaLaUltimaFotoPorCotizacionYLaOlvidaAlVencer()
    {
        var reloj = new clsRelojFalso(_ahora);
        var memoria = new clsSituacionLineasNexoEnMemoria(reloj);
        var (c1, c2, camion) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        memoria.Obtener(c1).Should().BeNull("nada leído todavía (o el proceso se reinició)");
        memoria.Registrar(c1, new Dictionary<Guid, clsSituacionLineaNexo> { [camion] = new("por_costear", "uno") });
        memoria.Registrar(c1, new Dictionary<Guid, clsSituacionLineaNexo> { [camion] = new("por_costear", "dos") });
        memoria.Registrar(c2, new Dictionary<Guid, clsSituacionLineaNexo> { [camion] = new("revisada", null) });

        memoria.Obtener(c1)![camion].MotivoPendiente.Should().Be("dos", "cada lectura reemplaza la anterior");
        memoria.Registrar(c2, new Dictionary<Guid, clsSituacionLineaNexo>());
        memoria.Obtener(c2).Should().BeNull("una foto sin nada que mostrar borra la anterior");

        reloj.Avanzar(clsSituacionLineasNexoEnMemoria.Retencion + TimeSpan.FromMinutes(1));
        memoria.Obtener(c1).Should().BeNull("pasada la retención sin lecturas nuevas, se olvida");
    }

    /// <summary>Formulario de una ruta (Buenaventura → Bogotá) con un camión por (vehículo, peso), enviado y con margen del 15 %.</summary>
    private static clsCotizacionSolicitud Ruta(params (string? Vehiculo, decimal Peso)[] camiones)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-MOT-20102026-001" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [new clsLineaSolicitudCosteo { Origen = "Buenaventura", Destino = "Bogotá, D.C.", TipoVehiculo = camiones[0].Vehiculo, PesoKg = camiones[0].Peso }],
            [0], 3);
        var ruta = cotizacion.PrimeraRuta!;
        foreach (var camion in camiones.Skip(1))
        {
            ruta.AgregarVehiculo(camion.Vehiculo, null, camion.Peso);
        }

        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        return cotizacion;
    }

    private static clsLineaSolicitudNexo Detalle(string id, string? vehiculo, decimal peso) =>
        new(id, "BUENAVENTURA → BOGOTA, D.C.", null, peso, vehiculo, null);

    private static clsCosteoConsultadoNexo Foto(IReadOnlyList<clsLineaSolicitudNexo> detalle, IReadOnlyList<(string Id, decimal Flete)> fletes) =>
        new("EnCosteo", [.. detalle.Select(d => d.Id)], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir",
            [.. fletes.Select(f => new clsLineaCosteoNexo(f.Id, f.Flete, null, null))], []))
        {
            Detalle = detalle,
        };
}
