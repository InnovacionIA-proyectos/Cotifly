using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Tests.Costeo;

/// <summary>
/// HU-028.1 (#183, Nexo #320): la Mesa de Costeo rechaza una línea (un camión de Cotifly) con un motivo. El polling lo lee del detalle de la
/// foto (<c>estadoLinea = "rechazada"</c> y <c>motivoRechazo</c>), lo guarda en el camión y la regla se refleja en la ruta, la cabecera, el
/// precio, el PDF, el Excel, la licitación, el margen, los conexos, la re-solicitud y la <c>-vN</c>.
/// </summary>
public sealed class clsRechazoLineaTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 20, 16, 0, 0, TimeSpan.Zero);
    private readonly clsPrecioCosteoService _servicio = new();

    // ------------------------------------------------------------------ Lectura y camión

    [Fact]
    public void UnCamionRechazadoGuardaElMotivoYNoCuentaEnElPrecioDeSuRuta()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "  Ruta sin operación  "), Linea("N-3", "C")],
            [("N-1", 1_000_000m), ("N-3", 500_000m)]));

        var segunda = cotizacion.RutasOrdenadas[1];
        var (rechazado, vigente) = (segunda.VehiculosOrdenados[0], segunda.VehiculosOrdenados[1]);
        (rechazado.EstadoCosteo, rechazado.MotivoRechazo, rechazado.RechazadaAt, rechazado.CostoFlete, rechazado.LineaSolicitudNexoId)
            .Should().Be(((EstadoCosteo?)EstadoCosteo.Rechazada, (string?)"Ruta sin operación", (DateTimeOffset?)_ahora, (decimal?)null, (string?)"N-2"),
                "la línea rechazada sigue existiendo en Nexo: no es una línea sin dueño");
        segunda.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo, "el rechazado se ignora en el resumen, como un descartado");
        segunda.CostoFletesTotal.Should().Be(500_000m, "solo el camión vigente");
        segunda.MotivoRechazo.Should().Be("Ruta sin operación");
        segunda.Rechazada.Should().BeFalse("le queda un camión vigente");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo, "el autocosteo no espera la línea rechazada");
        cotizacion.PrecioFinal.Should().Be(1_764_706m, "1.500.000 con margen del 15 %");
        clsServiciosConexosCosteoService.PrimerCamion(segunda).Should().BeSameAs(vigente, "el rechazado no recibe conexos");
    }

    /// <summary>
    /// HU-039.1 (#218, validador): con un camión rechazado y otro vigente, el costo bruto de la ruta es solo el del vigente (el mismo con
    /// el que se calcula su precio), y la otra ruta trae el suyo.
    /// </summary>
    [Fact]
    public void ConUnCamionRechazadoElCostoBrutoDeLaRutaEsSoloElDeLosQueCuentan()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Sin cupo"), Linea("N-3", "C")],
            [("N-1", 1_000_000m), ("N-3", 500_000m)]));

        var vista = clsCosteoCotizacionService.Vista(cotizacion, null);

        vista.Rutas.Select(r => (r.EstadoCosteo, r.CostoFinal, r.PrecioFinalLinea)).Should().Equal(
            ("autocosteo", (decimal?)1_000_000m, (decimal?)1_176_471m),
            ("autocosteo", (decimal?)500_000m, (decimal?)588_235m));
        vista.Rutas[1].MotivoRechazo.Should().Be("Sin cupo");
    }

    [Fact]
    public void UnaRutaConTodosSusCamionesRechazadosQuedaRechazadaSinPrecioYNoBloqueaLaCotizacion()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Sin tarifa"), Linea("N-3", "C", rechazo: "Sin cupo")],
            [("N-1", 1_000_000m)]));

        var segunda = cotizacion.RutasOrdenadas[1];
        segunda.EstadoCosteo.Should().Be(EstadoCosteo.Rechazada);
        segunda.Rechazada.Should().BeTrue();
        segunda.PrecioFinalLinea.Should().BeNull();
        segunda.CostoFinal.Should().BeNull();
        segunda.VehiculosQueCuentan.Should().BeEmpty("los rechazados nunca cuentan, ni siquiera si lo están todos");
        segunda.VehiculosVigentes.Should().HaveCount(2, "siguen en la ruta (se copian a la -vN como rechazados)");
        segunda.VehiculosQueViajan.Should().BeEmpty("decisión del 2026-10-20: no se reenvían por defecto");
        segunda.MotivoRechazo.Should().Be("Sin tarifa | Sin cupo");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        cotizacion.PrecioFinal.Should().Be(1_176_471m, "el total es solo la ruta 1");
        cotizacion.CostoFinal.Should().Be(1_000_000m);
        cotizacion.RutasCotizables.Should().ContainSingle().Which.Orden.Should().Be(1);
        clsTextosCosteoService.Contexto(cotizacion, null).Rutas.Should().ContainSingle("la IA no redacta sobre la ruta rechazada (validación #183)")
            .Which.Origen.Should().Be(cotizacion.RutasOrdenadas[0].OrigenNombre);
        cotizacion.TodasLasRutasRechazadas.Should().BeFalse();
        cotizacion.MotivoEstadoCosteo.Should().BeNull();

        // Margen y conexos en la ruta rechazada: 409 RUTA_RECHAZADA con el motivo; la otra ruta sí se ajusta.
        var margen = cotizacion.ValidarAjusteMargen(2, 0.2m, null);
        margen!.Codigo.Should().Be(clsCodigosError.RutaRechazada);
        margen.Datos!["motivo_rechazo"].Should().Be("Sin tarifa | Sin cupo");
        cotizacion.ValidarAjusteMargen(1, 0.2m, null).Should().BeNull();
        clsAjusteMargenCosteoService.Recalcular(cotizacion, segunda, 0.2m).Error.Codigo.Should().Be(clsCodigosError.RutaRechazada);
        clsAjusteMargenCosteoService.Recalcular(cotizacion, cotizacion.RutasOrdenadas[0], 0.2m).Valor.PrecioFinal
            .Should().Be(1_250_000m, "el total de la vista previa no suma la ruta rechazada");
        cotizacion.CambiarConexos(2, [new clsCambioConexo("CX-1", "Cargue", 100_000m, 1m)], _ahora);
        clsCosteoCotizacionService.ConexosConPrecio(cotizacion, segunda).Should().BeEmpty("una ruta rechazada no muestra ni cobra conexos");
        cotizacion.PrecioFinal.Should().Be(1_176_471m, "el conexo de la ruta rechazada no suma");
    }

    [Fact]
    public void ElPdfYElExcelMuestranLaRutaRechazadaSinCosteoSinPrecioNiMotivoYElTotalSinElla()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Motivo interno"), Linea("N-3", "C", rechazo: "Motivo interno")],
            [("N-1", 1_000_000m)]));

        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP").Should().BeNull("las rutas rechazadas no cuentan en \"todas con precio\"");
        var resumen = clsCosteoCotizacionService.ResumenPliego(cotizacion, "COP");
        var precioRuta1 = Cotifly.Aplicacion.Pdf.Services.clsFormatoMoneda.Formatear(1_176_471m, "COP");
        resumen.Filas.Select(f => f.Tarifa).Should().Equal(precioRuta1, clsCosteoCotizacionService.TextoSinCosteo);
        resumen.Total.Should().Be(precioRuta1, "el total del pliego no suma la ruta rechazada");
        var pliego = clsCosteoCotizacionService.PayloadPliego(cotizacion, null, "COP");
        var fila = pliego.Secciones![1].Filas!.Single();
        (fila.Tarifa, fila.Nota).Should().Be(((decimal?)null, clsCosteoCotizacionService.TextoSinCosteo));
        fila.VehiculoPeso.Should().NotBeNull("se muestran los camiones que se pidieron");
        System.Text.Json.JsonSerializer.Serialize(pliego).Should().NotContain("Motivo interno", "el PDF no lleva el motivo");

        var excel = clsLicitacionService.DatosExportacion(cotizacion, null, _ahora);
        excel.Lineas[1].Should().Match<Cotifly.Aplicacion.Licitaciones.DTOs.clsLineaExportacion>(
            l => l.Rechazada && l.PrecioFinal == null && l.Estado == clsLicitacionService.EstadoRechazadaEnNexo);
        excel.Lineas[1].Camiones!.Should().OnlyContain(c => c.Rechazado);
        excel.PrecioTotal.Should().Be(1_176_471m, "el total del Excel no suma la ruta rechazada");
        System.Text.Json.JsonSerializer.Serialize(excel).Should().NotContain("Motivo interno", "el Excel no lleva el motivo");

        var licitacion = clsLicitacionService.Vista(cotizacion, null);
        (licitacion.LineasCosteadas, licitacion.LineasPendientes, licitacion.LineasRechazadas, licitacion.PrecioFinal)
            .Should().Be((1, 0, 1, (decimal?)1_176_471m));
        licitacion.Lineas[1].Should().Match<Cotifly.Aplicacion.Licitaciones.DTOs.clsLineaLicitacion>(
            l => l.EstadoCosteo == "rechazada" && l.MotivoRechazo == "Motivo interno" && l.PrecioFinal == null);

        var vista = clsCosteoCotizacionService.Vista(cotizacion, null);
        vista.Rutas[1].Should().Match<clsRutaCosteo>(
            r => r.EstadoCosteo == "rechazada" && r.MotivoRechazo == "Motivo interno" && r.PrecioFinalLinea == null && r.CostoFinal == null);
        vista.Rutas[0].CostoFinal.Should().Be(1_000_000m, "HU-039.1 (#218): la ruta con precio trae su costo bruto");
        vista.Rutas[1].Camiones.Should().OnlyContain(c => c.EstadoCosteo == "rechazada" && c.MotivoRechazo == "Motivo interno" && c.RechazadaAt == _ahora);
        vista.Rutas[0].MotivoRechazo.Should().BeNull();
    }

    [Fact]
    public void SiLaMesaDeshaceElRechazoAntesDeEntregarElCamionVuelveAPendienteSinMotivo()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Revisar"), Linea("N-3", "C", rechazo: "Revisar")],
            [("N-1", 1_000_000m)]));
        cotizacion.RutasOrdenadas[1].Rechazada.Should().BeTrue();

        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C"), Linea("N-3", "C")], [("N-1", 1_000_000m)])).Should().BeTrue();

        var segunda = cotizacion.RutasOrdenadas[1];
        segunda.Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.PendienteCosteo && v.MotivoRechazo == null && v.RechazadaAt == null);
        segunda.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "la línea vuelve a esperar a la Mesa");
        cotizacion.PrecioFinal.Should().BeNull();
    }

    [Fact]
    public void ConLaCotizacionEntregadaElRechazoEsDefinitivoAunqueLaFotoNoTraigaElEstadoDeLaLinea()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Sin tarifa"), Linea("N-3", "C")],
            [("N-1", 1_000_000m), ("N-3", 500_000m)]));

        // La respuesta del resultado (o una foto sin detalle) dice "Entregada" sin el estado de cada línea: el rechazo se conserva.
        var sinDetalle = new clsCosteoConsultadoNexo("Entregada", ["N-1", "N-2", "N-3"], new clsCotizacionNexo("COT-1", "Entregada", "Ganada",
            [new clsLineaCosteoNexo("N-1", 1_000_000m, null, null), new clsLineaCosteoNexo("N-3", 500_000m, null, null)], []));
        Aplicar(cotizacion, sinDetalle);

        var rechazado = cotizacion.RutasOrdenadas[1].VehiculosOrdenados[0];
        (rechazado.EstadoCosteo, rechazado.MotivoRechazo).Should().Be(((EstadoCosteo?)EstadoCosteo.Rechazada, (string?)"Sin tarifa"));
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada, "marcar ganada con un camión rechazado funciona");
        cotizacion.PrecioFinal.Should().Be(1_764_706m);
        cotizacion.MotivoEstadoCosteo.Should().BeNull("un camión rechazado no es \"entregada sin flete\"");
    }

    [Fact]
    public void SiLaMesaDeshaceCosteaYEntregaEntreDosConsultasElCamionTomaSuFleteYLaRutaVuelveAlTotal()
    {
        // Validación #183: el polling puede tardar hasta 15 min entre consultas. Si en ese tiempo la Mesa deshace el rechazo, costea la
        // línea y entrega, la foto trae "entregada" CON el detalle de la línea (no rechazada): manda el detalle, no se conserva el rechazo.
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Revisar"), Linea("N-3", "C", rechazo: "Revisar")],
            [("N-1", 1_000_000m)]));
        cotizacion.RutasOrdenadas[1].Rechazada.Should().BeTrue();

        Aplicar(cotizacion, Foto("Entregada", [Linea("N-1", "A"), Linea("N-2", "C"), Linea("N-3", "C")],
            [("N-1", 1_000_000m), ("N-2", 300_000m), ("N-3", 200_000m)], estadoCotizacion: "Entregada"));

        var segunda = cotizacion.RutasOrdenadas[1];
        segunda.Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.Entregada && v.CostoFlete != null && v.MotivoRechazo == null && v.RechazadaAt == null);
        segunda.Rechazada.Should().BeFalse();
        segunda.CostoFletesTotal.Should().Be(500_000m);
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.PrecioFinal.Should().Be(1_764_706m, "1.500.000 con margen del 15 %: la ruta 2 vuelve a contar");
        cotizacion.MotivoEstadoCosteo.Should().BeNull();
    }

    [Fact]
    public void UnaFotoSinElEstadoDeLaLineaNoDeshaceElRechazoNiLoHaceOscilar()
    {
        // Validación #183: una lectura en costeo sin el detalle de la línea (Nexo no devolvió lineas[], o la línea no vino) no es la
        // confirmación de que la Mesa deshizo el rechazo: se conserva, con su fecha, y la siguiente lectura completa no lo "re-crea".
        var cotizacion = DosRutas();
        var rechazo = Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Sin tarifa"), Linea("N-3", "C")], [("N-1", 1_000_000m), ("N-3", 500_000m)]);
        Aplicar(cotizacion, rechazo);
        var camion = cotizacion.RutasOrdenadas[1].VehiculosOrdenados[0];

        var incompleta = Foto("EnCosteo", [], [("N-1", 1_000_000m), ("N-3", 500_000m)]);
        Aplicar(cotizacion, incompleta, _ahora.AddMinutes(5));
        var soloOtras = Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-3", "C")], [("N-1", 1_000_000m), ("N-3", 500_000m)]) with { LineasSolicitud = ["N-1", "N-2", "N-3"] };
        Aplicar(cotizacion, soloOtras, _ahora.AddMinutes(10));

        (camion.EstadoCosteo, camion.MotivoRechazo, camion.RechazadaAt).Should().Be(
            ((EstadoCosteo?)EstadoCosteo.Rechazada, (string?)"Sin tarifa", (DateTimeOffset?)_ahora));
        Aplicar(cotizacion, rechazo, _ahora.AddMinutes(15)).Should().BeFalse("la lectura completa no cambia nada: no oscila");
        camion.RechazadaAt.Should().Be(_ahora);
        cotizacion.PrecioFinal.Should().Be(1_764_706m);
    }

    [Fact]
    public void UnRechazoDespuesDelPdfConservaElPrecioDeLaVersionYLoAdvierte()
    {
        // Validación #183: con PDF el precio de la versión se conserva (ADR-015 §5), pero si la Mesa rechaza una línea que el PDF cotizó el
        // comercial debe saberlo (antes no había aviso: la línea rechazada no trae flete en Nexo y no "difería").
        var cotizacion = DosRutas();
        var completa = Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C"), Linea("N-3", "C")], [("N-1", 1_000_000m), ("N-2", 300_000m), ("N-3", 200_000m)]);
        Aplicar(cotizacion, completa);
        var precio = cotizacion.PrecioFinal;
        precio.Should().NotBeNull();
        cotizacion.AsignarPdf("pdf/PCN-REC.pdf");
        Aplicar(cotizacion, completa, _ahora.AddMinutes(5));
        cotizacion.MotivoEstadoCosteo.Should().BeNull("sin cambios no hay aviso");

        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Sin cupo"), Linea("N-3", "C")],
            [("N-1", 1_000_000m), ("N-3", 200_000m)]), _ahora.AddMinutes(10));

        cotizacion.RutasOrdenadas[1].VehiculosOrdenados[0].Rechazado.Should().BeTrue();
        cotizacion.PrecioFinal.Should().Be(precio, "el precio de la versión con PDF se conserva");
        cotizacion.MotivoEstadoCosteo.Should().Be(clsPrecioCosteoService.MotivoCambioDespuesDelPdf);
    }

    [Fact]
    public void ConTodasLasRutasRechazadasLaCabeceraSigueEnCosteoYAlEntregarQuedaEntregadaSinTotalYElPdfSeBloquea()
    {
        var cotizacion = DosRutas();
        var todas = new[] { Linea("N-1", "A", rechazo: "Fuera de cobertura"), Linea("N-2", "C", rechazo: "Fuera de cobertura"), Linea("N-3", "C", rechazo: "Fuera de cobertura") };

        Aplicar(cotizacion, Foto("EnCosteo", todas, []));

        cotizacion.TodasLasRutasRechazadas.Should().BeTrue();
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "la Mesa puede deshacer o entregar: el polling sigue");
        cotizacion.CosteoSiguienteConsultaAt.Should().NotBeNull();
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.MotivoEstadoCosteo.Should().Be(clsCotizacionSolicitud.MotivoTodasLasRutasRechazadas);

        Aplicar(cotizacion, Foto("Entregada", todas, [], estadoCotizacion: "Entregada"));

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.CosteoSiguienteConsultaAt.Should().BeNull("entregada: el polling se detiene");
        cotizacion.PrecioFinal.Should().BeNull();
        cotizacion.EntregadaSinPrecio.Should().BeFalse("sin rutas que cotizar no hay precio que completar");
        cotizacion.MotivoEstadoCosteo.Should().Be(clsCotizacionSolicitud.MotivoTodasLasRutasRechazadas);
        cotizacion.Rutas.Should().OnlyContain(r => r.EstadoCosteo == EstadoCosteo.Rechazada);
        clsCosteoCotizacionService.ValidarTotal(cotizacion, "COP")!.Codigo.Should().Be(clsCodigosError.SinRutasCotizables);
        clsLicitacionService.DatosExportacion(cotizacion, null, _ahora).PrecioTotal.Should().BeNull();
    }

    [Fact]
    public void ConUnNexoSinElEstadoDeLaLineaNoHayRechazo()
    {
        var cotizacion = DosRutas();
        // Nexo anterior a #309/#320: sin estadoLinea ni motivoRechazo.
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A") with { EstadoLinea = null }, Linea("N-2", "C") with { EstadoLinea = null },
            Linea("N-3", "C") with { EstadoLinea = null }], [("N-1", 1_000_000m)]));

        cotizacion.Vehiculos.Should().NotContain(v => v.EstadoCosteo == EstadoCosteo.Rechazada);
        clsDerivacionEstadoCosteo.LineaRechazada(Foto("EnCosteo", [Linea("N-1", "A")], []), "N-1").Should().BeFalse();
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo);
    }

    // ------------------------------------------------------------------ Reenvío, -vN y edición

    [Fact]
    public void LaReSolicitudNoReenviaElCamionRechazadoLoConservaRechazadoYElPollingDeLaSolicitudNuevaNoLoEmpareja()
    {
        // Decisión del 2026-10-20 ("no reenviar los camiones rechazados por defecto"): la ruta 2 tiene un camión rechazado y otro vigente.
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Sin tarifa"), Linea("N-3", "C")],
            [("N-1", 1_000_000m), ("N-3", 500_000m)]));
        var instantanea = System.Text.Json.JsonSerializer.Serialize(clsReSolicitudCosteoService.Instantanea(cotizacion));
        instantanea.Should().Contain("Sin tarifa", "el rechazo queda en la auditoría");
        clsReSolicitudCosteoService.AvisoRechazos(cotizacion, versionNueva: false).Should().BeNull("antes de re-solicitar no se omitió nada");

        cotizacion.PrepararReSolicitud();

        var (primero, segunda) = (cotizacion.RutasOrdenadas[0].VehiculosOrdenados[0], cotizacion.RutasOrdenadas[1]);
        var (rechazado, vigente) = (segunda.VehiculosOrdenados[0], segunda.VehiculosOrdenados[1]);
        (rechazado.EstadoCosteo, rechazado.MotivoRechazo, rechazado.RechazadaAt, rechazado.LineaSolicitudNexoId, rechazado.FueraDelEnvio).Should().Be(
            ((EstadoCosteo?)EstadoCosteo.Rechazada, (string?)"Sin tarifa", (DateTimeOffset?)_ahora, (string?)null, true),
            "sigue rechazado, con su motivo, y olvida la línea de la solicitud anterior");
        (vigente.EstadoCosteo, vigente.LineaSolicitudNexoId).Should().Be(((EstadoCosteo?)EstadoCosteo.PendienteEnvio, (string?)null));
        cotizacion.LineasNexo().Select(l => l.Camion).Should().Equal([primero, vigente], "el rechazado no viaja a Nexo");
        segunda.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        clsReSolicitudCosteoService.AvisoRechazos(cotizacion, versionNueva: false).Should()
            .Contain("ruta 2 (C → D): 1 de 2 camiones").And.Contain("Motivo: Sin tarifa").And.Contain("reincluir_rechazados");
        clsEnvioCosteoService.NotaCamionesPorRuta(cotizacion).Should().Contain("Ruta 2 (C → D): 1 camión.",
            "la nota dice cuántas líneas viajan por ruta (y que van sin posiciones: la ruta tiene dos camiones)");

        // Polling de la solicitud nueva: solo trae las dos líneas que viajaron. El rechazado no compite por la línea de su ruta (sin esta
        // regla, dos camiones de C → D para una línea serían un empate sin resolver y la ruta quedaría sin precio).
        cotizacion.RegistrarEnvioCosteo("SOL-2", false, _ahora, _ahora);
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("M-1", "A"), Linea("M-3", "C")], [("M-1", 1_000_000m), ("M-3", 500_000m)]), _ahora.AddHours(1));

        (primero.LineaSolicitudNexoId, vigente.LineaSolicitudNexoId, rechazado.LineaSolicitudNexoId).Should().Be(("M-1", "M-3", (string?)null));
        (rechazado.EstadoCosteo, rechazado.MotivoRechazo, rechazado.RechazadaAt).Should().Be(
            ((EstadoCosteo?)EstadoCosteo.Rechazada, (string?)"Sin tarifa", (DateTimeOffset?)_ahora), "no se lee ni se da por línea borrada");
        segunda.CostoFletesTotal.Should().Be(500_000m);
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
        cotizacion.MotivoEstadoCosteo.Should().BeNull("no hay camiones sin asociar");
        cotizacion.PrecioFinal.Should().Be(1_764_706m, "1.500.000 con margen del 15 %: el rechazado no cuenta");
        clsServiciosConexosCosteoService.PrimerCamion(segunda).Should().BeSameAs(vigente, "los conexos van al primer camión no rechazado");
    }

    [Fact]
    public void UnaRutaConTodosSusCamionesRechazadosSeOmiteDelEnvioYQuedaRechazadaVisible()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "Sin tarifa"), Linea("N-3", "C", rechazo: "Sin cupo")],
            [("N-1", 1_000_000m)]));

        cotizacion.PrepararReSolicitud();

        var segunda = cotizacion.RutasOrdenadas[1];
        cotizacion.LineasNexo().Should().ContainSingle("solo viaja la ruta 1: no se manda a Nexo una ruta sin camiones")
            .Which.Camion.Should().BeSameAs(cotizacion.RutasOrdenadas[0].VehiculosOrdenados[0]);
        (segunda.Rechazada, segunda.ViajaANexo, segunda.PrecioFinalLinea).Should().Be((true, false, (decimal?)null));
        segunda.Vehiculos.Should().OnlyContain(v => v.FueraDelEnvio);
        cotizacion.TieneLineasQueViajan.Should().BeTrue();
        cotizacion.RutasCotizables.Should().ContainSingle();
        clsReSolicitudCosteoService.AvisoRechazos(cotizacion, versionNueva: false).Should()
            .Contain("ruta 2 (C → D): la ruta completa (2 camiones): no viaja y queda rechazada, sin precio").And.Contain("Sin tarifa | Sin cupo");
        clsEnvioCosteoService.NotaCamionesPorRuta(cotizacion).Should().BeNull("la única ruta que viaja tiene un camión");
    }

    [Fact]
    public void ConTodosLosCamionesRechazadosNoHayNadaQueEnviarHastaQueElComercialLosReincluye()
    {
        var cotizacion = DosRutas();
        var todas = new[] { Linea("N-1", "A", rechazo: "x"), Linea("N-2", "C", rechazo: "y"), Linea("N-3", "C", rechazo: "y") };
        Aplicar(cotizacion, Foto("EnCosteo", todas, []));

        cotizacion.TieneLineasQueViajan.Should().BeFalse();
        clsReSolicitudCosteoService.TodosRechazados(cotizacion.Folio).Should().Match<clsErrorDominio>(
            e => e.Codigo == clsCodigosError.SinRutasCotizables && e.Detalle == clsReSolicitudCosteoService.MotivoTodosRechazados);

        // Reincluir (lo que hace la edición con reincluir_rechazados): el camión olvida el rechazo y vuelve a viajar.
        cotizacion.RutasOrdenadas[1].ReincluirRechazados().Should().Be(2);
        cotizacion.PrepararReSolicitud();
        cotizacion.LineasNexo().Should().HaveCount(2);
        cotizacion.RutasOrdenadas[1].Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.PendienteEnvio && v.MotivoRechazo == null && v.RechazadaAt == null);
        cotizacion.RutasOrdenadas[0].VehiculosOrdenados[0].FueraDelEnvio.Should().BeTrue("la ruta 1 no se reincluyó");
    }

    [Fact]
    public void LaCabeceraNuncaQuedaRechazadaYUnEstadoDeLaSolicitudNoPisaElRechazo()
    {
        var cotizacion = DosRutas();
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: "x"), Linea("N-3", "C", rechazo: "x")], [("N-1", 1_000_000m)]));

        cotizacion.RegistrarEntregaImplicita("COT-1");

        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.Entregada);
        cotizacion.RutasOrdenadas[1].Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.Rechazada && v.MotivoRechazo == "x");
        cotizacion.EstadoCabecera(EstadoCosteo.PendienteCosteo).Should().NotBe(EstadoCosteo.Rechazada);
        clsResumenEstadoCosteo.Resumir([EstadoCosteo.Rechazada, EstadoCosteo.Descartada]).Should().Be(EstadoCosteo.Rechazada);
        clsResumenEstadoCosteo.Resumir([EstadoCosteo.Rechazada, EstadoCosteo.Autocosteo]).Should().Be(EstadoCosteo.Autocosteo);
        clsResumenEstadoCosteo.Resumir([EstadoCosteo.Descartada, EstadoCosteo.Descartada]).Should().Be(EstadoCosteo.Descartada);
    }

    [Fact]
    public void ElMotivoDeNexoSeRecortaA500YUnMotivoVacioNoBorraElQueYaSeTenia()
    {
        var cotizacion = DosRutas();
        var largo = new string('m', 600);
        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: largo), Linea("N-3", "C")], [("N-1", 1_000_000m)]));
        var camion = cotizacion.RutasOrdenadas[1].VehiculosOrdenados[0];
        camion.MotivoRechazo!.Length.Should().Be(clsSituacionLineaNexo.LargoMaximoMotivoRechazo);
        camion.MotivoRechazo.Should().EndWith("…");

        Aplicar(cotizacion, Foto("EnCosteo", [Linea("N-1", "A"), Linea("N-2", "C", rechazo: " ", rechazada: true), Linea("N-3", "C")], [("N-1", 1_000_000m)]),
            _ahora.AddHours(1));

        camion.MotivoRechazo.Should().HaveLength(clsSituacionLineaNexo.LargoMaximoMotivoRechazo, "un motivo vacío no borra el anterior");
        camion.RechazadaAt.Should().Be(_ahora, "la fecha es la de la primera lectura que lo vio");
    }

    // ------------------------------------------------------------------ Apoyo

    private bool Aplicar(clsCotizacionSolicitud cotizacion, clsCosteoConsultadoNexo foto, DateTimeOffset? cuando = null) =>
        clsLecturaCosteo.AplicarFoto(cotizacion, foto, cuando ?? _ahora, new clsOpcionesCosteoNexo(), _servicio);

    /// <summary>
    /// Dos rutas enviadas a Nexo con margen del 15 %: la 1 (A → B) con un camión y la 2 (C → D) con dos camiones. Los camiones ya están
    /// asociados a sus líneas de Nexo: N-1 (ruta 1), N-2 y N-3 (ruta 2).
    /// </summary>
    private static clsCotizacionSolicitud DosRutas()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = "PCN-REC-20102026-001" }).Valor;
        cotizacion.RegistrarSolicitudLicitacion(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion },
            [
                new clsLineaSolicitudCosteo { Origen = "A", Destino = "B", TipoVehiculo = "3S3", PesoKg = 10_000m },
                new clsLineaSolicitudCosteo { Origen = "C", Destino = "D", TipoVehiculo = "3S3", PesoKg = 8_000m },
            ],
            [0, 1], 3);
        cotizacion.RutasOrdenadas[1].AgregarVehiculo("2S2", null, 4_000m);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        cotizacion.RutasOrdenadas[0].VehiculosOrdenados[0].AsignarLineaNexo("N-1");
        cotizacion.RutasOrdenadas[1].VehiculosOrdenados[0].AsignarLineaNexo("N-2");
        cotizacion.RutasOrdenadas[1].VehiculosOrdenados[1].AsignarLineaNexo("N-3");
        return cotizacion;
    }

    private static clsLineaSolicitudNexo Linea(string id, string origen, string? rechazo = null, bool rechazada = false) =>
        new(id, $"{origen} → {(origen == "A" ? "B" : "D")}", null, null, null, null)
        {
            EstadoLinea = rechazo is not null || rechazada ? "rechazada" : "porCostear",
            MotivoRechazo = rechazo,
        };

    private static clsCosteoConsultadoNexo Foto(
        string estado, IReadOnlyList<clsLineaSolicitudNexo> detalle, IReadOnlyList<(string Id, decimal Flete)> fletes, string estadoCotizacion = "Borrador") =>
        new(estado, [.. detalle.Select(d => d.Id)], new clsCotizacionNexo("COT-1", estadoCotizacion, "SinDefinir",
            [.. fletes.Select(f => new clsLineaCosteoNexo(f.Id, f.Flete, null, null))], []))
        {
            Detalle = detalle,
        };
}
