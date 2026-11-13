using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// ADR-021: el comercial edita la solicitud. Reglas del dominio: el modo según el estado (en sitio, reemplazo o versión
/// nueva; la licitación y el chat no se editan), los campos bloqueados (cliente, tipo y línea de negocio de cada ruta) y la aplicación de
/// la edición (rutas que se conservan con su id y su margen, rutas nuevas y quitadas, mercancías, conexos y levante).
/// </summary>
public sealed class clsEdicionSolicitudTests
{
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;

    // --- Modo según el estado (decisión 2) ---

    [Fact]
    public void SinLlegarANexoConErrorDeEnvioSeEditaEnSitio()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.RegistrarErrorEnvioCosteo("Nexo no respondió (HTTP 503)");

        cotizacion.ModoEdicion(_ahora).Valor.Should().Be(ModoEdicionSolicitud.EnSitio);
    }

    [Fact]
    public void ElRechazoDefinitivoDeNexoTambienSeCorrigeEnSitio()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();
        cotizacion.RegistrarErrorEnvioCosteo("PESO_INCONSISTENTE", definitivo: true);

        cotizacion.ModoEdicion(_ahora).Valor.Should().Be(ModoEdicionSolicitud.EnSitio);
    }

    [Fact]
    public void UnPendienteDeEnvioAbandonadoSeEditaEnSitioYUnoRecienteNo()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();

        cotizacion.ModoEdicion(cotizacion.UpdatedAt + TimeSpan.FromMinutes(3)).Valor.Should().Be(ModoEdicionSolicitud.EnSitio);
        var reciente = cotizacion.ModoEdicion(cotizacion.UpdatedAt + TimeSpan.FromSeconds(30));
        reciente.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        reciente.Error.Detalle.Should().Be(clsCotizacionSolicitud.MotivoEdicionEnvioEnCurso);
    }

    [Theory]
    [InlineData(EstadoCosteo.PendienteCosteo)]
    [InlineData(EstadoCosteo.Autocosteo)]
    [InlineData(EstadoCosteo.Entregada)]
    [InlineData(EstadoCosteo.CosteoVencido)]
    public void YaEnNexoSinEntregarAlClienteEsUnReemplazo(EstadoCosteo estado)
    {
        var cotizacion = EnNexo(estado);

        cotizacion.ModoEdicion(_ahora).Valor.Should().Be(ModoEdicionSolicitud.Reemplazo);
    }

    [Fact]
    public void YaEntregadaAlClienteEsUnaVersionNueva()
    {
        var cotizacion = EnNexo(EstadoCosteo.Entregada);
        cotizacion.RegistrarEnvio();

        cotizacion.ModoEdicion(_ahora).Valor.Should().Be(ModoEdicionSolicitud.NuevaVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConResultadoEsUnaVersionNueva(bool ganada)
    {
        var cotizacion = EnNexo(EstadoCosteo.Entregada);
        cotizacion.MarcarSeguimiento(ganada, ganada ? null : "Precio", _ahora);

        cotizacion.ModoEdicion(_ahora).Valor.Should().Be(ModoEdicionSolicitud.NuevaVersion);
    }

    [Fact]
    public void UnaLicitacionNoSeEditaEnEstaFase()
    {
        var licitacion = clsCotizacionMuestra.LicitacionCosteada([1_000_000m, 2_000_000m]);

        var modo = licitacion.ModoEdicion(_ahora);

        modo.Error.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
        modo.Error.Detalle.Should().Be(clsCotizacionSolicitud.MotivoEdicionLicitacion);
        modo.Error.Mensaje.Should().Contain("licitación");
    }

    [Fact]
    public void UnaCotizacionDelChatNoTieneSolicitudQueEditar()
    {
        var chat = clsCotizacionMuestra.Chat();

        chat.ModoEdicion(_ahora).Error.Detalle.Should().Be(clsCotizacionSolicitud.MotivoEdicionSinFormulario);
    }

    // --- Campos bloqueados (decisión 1) ---

    [Fact]
    public void LosCamposBloqueadosConElMismoValorSeAceptan()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();

        cotizacion.CamposBloqueadosCambiados(TipoSolicitud.Spot, "900.123.456", " longyin s.a.s. ", null).Should().BeEmpty();
        cotizacion.CamposBloqueadosCambiados(null, null, null, null).Should().BeEmpty();
    }

    [Fact]
    public void CambiarElClienteOElTipoEsCampoNoEditable()
    {
        var cotizacion = clsCotizacionMuestra.Formulario();

        var errores = cotizacion.CamposBloqueadosCambiados(TipoSolicitud.Licitacion, "800111222", "OTRO S.A.S.", OrigenCliente.Prospecto);

        errores.Select(e => e.Campo).Should().Equal("tipo", "nit_cliente", "cliente_nombre", "origen_cliente");
        errores.Should().OnlyContain(e => e.Codigo == clsCodigosError.CampoNoEditable);
    }

    // --- Aplicar la edición (decisiones 3 y 4) ---

    [Fact]
    public void LaRutaQueSeConservaGuardaSuIdSuLineaYSuMargenNegociadoYTomaElContenidoNuevo()
    {
        var cotizacion = EnNexo(EstadoCosteo.Autocosteo);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.AplicarMargenNegociado(0.22m);
        var id = ruta.Id;
        var mercancias = ruta.Mercancias.Select(m => m.Id).ToList();
        var linea = Linea() with
        {
            Destino = "Medellín",
            DestinoMunicipioId = 5001,
            LevanteContenedorDiferente = true,
            CiudadLevanteContenedor = "Cali",
            CiudadLevanteMunicipioId = 76001,
            ValorAprovisionamiento = 350_000m,
        };

        var resultado = cotizacion.AplicarEdicion(Edicion(new clsRutaEditada(id, Capturada(linea, 3), Replanificada: false)), _ahora);

        resultado.RutasQuitadas.Should().BeEmpty();
        resultado.RutasAgregadas.Should().BeEmpty();
        var editada = cotizacion.Rutas.Should().ContainSingle().Subject;
        editada.Id.Should().Be(id);
        (editada.DestinoNombre, editada.DestinoMunicipioId).Should().Be(("Medellín", 5001));
        editada.LineaNegocioId.Should().Be(3);
        (editada.MargenAplicado, editada.OrigenMargen).Should().Be((0.22m, OrigenMargen.Negociado), "el margen negociado sobrevive (decisión 3)");
        editada.Mercancias.Select(m => m.Id).Should().Equal(mercancias, "las mercancías se actualizan en su lugar");
        editada.MercanciasOrdenadas.Should().OnlyContain(m => m.CodigoMercancia == clsSolicitudCosteoMuestra.CodigoMercancia);
        editada.Vehiculos.Should().ContainSingle().Which.CostoLevanteContenedor.Should().Be(350_000m, "el levante se copia al camión (decisión 4)");
        editada.Vehiculos[0].CostoFlete.Should().BeNull("el costeo anterior se olvida");
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        cotizacion.SolicitudNexoId.Should().BeNull();
        cotizacion.PrecioFinal.Should().BeNull();
    }

    [Fact]
    public void SePuedenAgregarYQuitarRutasYElOrdenSeCompacta()
    {
        var cotizacion = DeDosRutas();
        var segunda = cotizacion.RutasOrdenadas[1];
        segunda.AplicarMargenNegociado(0.3m);

        var resultado = cotizacion.AplicarEdicion(Edicion(
            new clsRutaEditada(segunda.Id, Capturada(Linea(), 3), false),
            new clsRutaEditada(null, Capturada(Linea() with { Destino = "Cali", DestinoMunicipioId = 76001 }, 3), true)), _ahora);

        resultado.RutasQuitadas.Should().ContainSingle().Which.Orden.Should().Be(1);
        resultado.RutasAgregadas.Should().Equal(2);
        cotizacion.RutasOrdenadas.Select(r => (r.Orden, r.DestinoNombre)).Should().Equal((1, "Bogotá D.C."), (2, "Cali"));
        cotizacion.RutasOrdenadas[0].Id.Should().Be(segunda.Id);
        cotizacion.RutasOrdenadas[0].TieneMargenNegociado.Should().BeTrue();
        cotizacion.RutasOrdenadas[1].TieneMargenNegociado.Should().BeFalse("una ruta nueva toma el margen sugerido al enviarse");
        cotizacion.Vehiculos.Should().OnlyContain(v => v.EstadoCosteo == EstadoCosteo.PendienteEnvio);
    }

    [Fact]
    public void LosConexosQuedanExactamenteLosDeLaEdicion()
    {
        var cotizacion = EnNexo(EstadoCosteo.Autocosteo);
        var ruta = cotizacion.PrimeraRuta!;
        ruta.AgregarOActualizarConexo("CX-1", "Cargue", 85_000m, 1m);
        ruta.AgregarOActualizarConexo("CX-2", "Escolta", 40_000m, 1m);

        cotizacion.AplicarEdicion(Edicion(new clsRutaEditada(ruta.Id,
            Capturada(Linea(), 3, conexos: [new clsCambioConexo("CX-2", "Escolta", 42_000m, 3m), new clsCambioConexo("CX-9", "Precinto", 10_000m, 1m)]),
            false)), _ahora);

        ruta.Conexos.Select(c => (c.IdServicioConexo, c.Precio, c.Cantidad)).Should().BeEquivalentTo(
            [("CX-2", 42_000m, 3m), ("CX-9", 10_000m, 1m)]);
        ruta.CostoServiciosConexos.Should().Be(136_000m);
    }

    [Fact]
    public void ReplanificadaReemplazaLosCamionesConLosDelPlan()
    {
        var cotizacion = EnNexo(EstadoCosteo.Autocosteo);
        var ruta = cotizacion.PrimeraRuta!;

        cotizacion.AplicarEdicion(Edicion(new clsRutaEditada(ruta.Id, Capturada(Linea(), 3,
            camiones: [new clsCamionPlaneado("2S2", "FURGON", 16_000m), new clsCamionPlaneado("C2", "ESTACAS", 12_500m)]), true)), _ahora);

        ruta.Vehiculos.Select(v => v.TipoVehiculo).Should().Equal("2S2", "C2");
        ruta.Vehiculos.Should().OnlyContain(v => v.LineaSolicitudNexoId == null && v.EstadoCosteo == EstadoCosteo.PendienteEnvio);
    }

    [Fact]
    public void LaLineaDeNegocioDeUnaRutaNoSeCambia()
    {
        var cotizacion = EnNexo(EstadoCosteo.Autocosteo);

        var aplicar = () => cotizacion.AplicarEdicion(Edicion(new clsRutaEditada(cotizacion.PrimeraRuta!.Id, Capturada(Linea(), 7), false)), _ahora);

        aplicar.Should().Throw<ArgumentException>().WithMessage("*línea de negocio*");
    }

    [Fact]
    public void UnaRutaNuevaSoloUsaUnaLineaDeLaSolicitud()
    {
        var cotizacion = EnNexo(EstadoCosteo.Autocosteo);

        var aplicar = () => cotizacion.AplicarEdicion(Edicion(
            new clsRutaEditada(cotizacion.PrimeraRuta!.Id, Capturada(Linea(), 3), false),
            new clsRutaEditada(null, Capturada(Linea(), 7), true)), _ahora);

        aplicar.Should().Throw<ArgumentException>().WithMessage("*línea de negocio de la solicitud*");
    }

    [Fact]
    public void LasRutasQueSeConservanNoSeReordenan()
    {
        var cotizacion = DeDosRutas();
        var (r1, r2) = (cotizacion.RutasOrdenadas[0], cotizacion.RutasOrdenadas[1]);

        var aplicar = () => cotizacion.AplicarEdicion(Edicion(
            new clsRutaEditada(r2.Id, Capturada(Linea(), 3), false), new clsRutaEditada(r1.Id, Capturada(Linea(), 3), false)), _ahora);

        aplicar.Should().Throw<ArgumentException>().WithMessage("*no se reordenan*");
    }

    [Fact]
    public void YaEntregadaAlClienteNoSeEditaEnSuLugar()
    {
        var cotizacion = EnNexo(EstadoCosteo.Entregada);
        cotizacion.RegistrarEnvio();

        var aplicar = () => cotizacion.AplicarEdicion(Edicion(new clsRutaEditada(cotizacion.PrimeraRuta!.Id, Capturada(Linea(), 3), false)), _ahora);

        aplicar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void CambiarLaMonedaFijaLaTrmNueva()
    {
        var cotizacion = EnNexo(EstadoCosteo.Autocosteo);

        cotizacion.AplicarEdicion(Edicion(new clsRutaEditada(cotizacion.PrimeraRuta!.Id, Capturada(Linea(), 3), false)) with
        {
            MonedaNueva = "usd",
            TrmNueva = 4_012.35m,
        }, _ahora);

        (cotizacion.Moneda, cotizacion.TrmAplicada).Should().Be(("USD", 4_012.35m));
    }

    [Fact]
    public void LaVersionEditadaCopiaElMargenNegociadoPorLaRutaQueEdita()
    {
        var original = DeDosRutas();
        original.RutasOrdenadas[1].AplicarMargenNegociado(0.25m);
        var nueva = original.CrearVersionEditada(clsFolio.Desde(original.Folio).Valor.ConVersion(2).Valor, "COP", 0m).Valor;
        var rutaNueva = Guid.NewGuid();
        nueva.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot }, [Capturada(Linea(), 3) with { RutaId = rutaNueva }]);

        nueva.CopiarMargenesNegociadosDe(original, new Dictionary<Guid, Guid> { [rutaNueva] = original.RutasOrdenadas[1].Id });

        nueva.PrimeraRuta!.MargenAplicado.Should().Be(0.25m);
        nueva.PrimeraRuta.OrigenMargen.Should().Be(OrigenMargen.Negociado);
        nueva.Folio.Should().EndWith("-v2");
        nueva.NitCliente.Should().Be(original.NitCliente);
    }

    [Fact]
    public void LaEdicionNumeraLosCamionesYGuardaElIdDelCatalogoYLaCategoriaDelConexo()
    {
        // ordenVehiculo 1..n en la ruta recubicada y en la nueva; el conexo con su id del catálogo y su categoría.
        var cotizacion = DeDosRutas();
        var r1 = cotizacion.RutasOrdenadas[0];

        cotizacion.AplicarEdicion(Edicion(
            new clsRutaEditada(r1.Id, Capturada(Linea(), 3,
                camiones: [new clsCamionPlaneado("2S2", "FURGON", 16_000m), new clsCamionPlaneado("C2", "ESTACAS", 12_500m)],
                conexos: [new clsCambioConexo("CX-9", "Precinto", 10_000m, 1m, "CX-9", "adicional")]), true),
            new clsRutaEditada(null, Capturada(Linea(), 3,
                camiones: [new clsCamionPlaneado("3S3", "FURGON", 20_000m), new clsCamionPlaneado("C2", "ESTACAS", 8_500m)],
                conexos: [new clsCambioConexo("CX-1", "Cargue", 85_000m, 1m, "CX-1", "logistico")]), true)), _ahora);

        var nueva = cotizacion.RutasOrdenadas[1];
        r1.VehiculosOrdenados.Select(v => v.OrdenVehiculo).Should().Equal(1, 2);
        nueva.VehiculosOrdenados.Select(v => (v.OrdenVehiculo, v.TipoVehiculo)).Should().Equal((1, "3S3"), (2, "C2"));
        r1.Conexos.Should().ContainSingle().Which.Should().Match<clsServicioConexoRuta>(c => c.IdServicioConexoCatalogo == "CX-9" && c.Categoria == "adicional");
        nueva.Conexos.Should().ContainSingle().Which.Should().Match<clsServicioConexoRuta>(c => c.IdServicioConexoCatalogo == "CX-1" && c.Categoria == "logistico");
    }

    // --- Recubicar (defecto C) ---

    [Fact]
    public void CambiarLaClaseDeViajeElPesoOLasMercanciasExigeRecubicar()
    {
        var linea = Linea();

        clsReglaRecubicacion.Exige(linea, linea with { ClaseViaje = "Carga Suelta" }).Should().BeTrue("defecto C: la clase de viaje viaja al cubicador");
        clsReglaRecubicacion.Exige(linea, linea with { PesoKg = 1m }).Should().BeTrue();
        clsReglaRecubicacion.Exige(linea, linea with { Posiciones = [linea.Posiciones[0] with { CodigoMercancia = "BEBIDAS" }, linea.Posiciones[1]] }).Should().BeTrue();
        clsReglaRecubicacion.Exige(linea, linea with { Posiciones = [linea.Posiciones[0]] }).Should().BeTrue();
    }

    [Fact]
    public void LoQueNoRecibeElCubicadorNoExigeRecubicar()
    {
        var linea = Linea();

        clsReglaRecubicacion.Exige(linea, linea with
        {
            Destino = "Cali",
            DestinoMunicipioId = 76001,
            HorasCargue = 9m,
            Tipologia = "CargaSuelta",
            ClaseViaje = " contenedor ",
            Posiciones = [.. linea.Posiciones.Select(p => p with { Descripcion = "Otro nombre", IdTipoMercancia = null })],
        }).Should().BeFalse();
    }

    private static clsLineaSolicitudCosteo Linea() => clsSolicitudCosteoMuestra.RutaCaptura();

    private static clsRutaCapturada Capturada(
        clsLineaSolicitudCosteo linea, int lineaNegocio, IReadOnlyList<clsCamionPlaneado>? camiones = null, IReadOnlyList<clsCambioConexo>? conexos = null) =>
        new(linea, lineaNegocio, 0, camiones ?? [new clsCamionPlaneado(null, null, linea.PesoKg)], conexos ?? []);

    private static clsEdicionSolicitud Edicion(params clsRutaEditada[] rutas) =>
        new(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot, Deadline = clsSolicitudCosteoMuestra.Deadline, Notas = "Editada" }, rutas);

    /// <summary>Formulario enviado a Nexo (<c>SOL-1</c>) con el flete aplicado, en <paramref name="estado"/>.</summary>
    private static clsCotizacionSolicitud EnNexo(EstadoCosteo estado) =>
        estado == EstadoCosteo.PendienteCosteo
            ? clsCotizacionMuestra.Costeada(flete: null)
            : estado == EstadoCosteo.CosteoVencido
                ? Vencida()
                : clsCotizacionMuestra.Costeada(estado: estado);

    private static clsCotizacionSolicitud Vencida()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: null);
        cotizacion.VencerPollingCosteo("72 h");
        return cotizacion;
    }

    /// <summary>Formulario de dos rutas de la línea 3 (captura por rutas), en Nexo y costeado.</summary>
    private static clsCotizacionSolicitud DeDosRutas()
    {
        var cotizacion = clsCotizacionMuestra.Cabecera();
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
            [Capturada(Linea() with { Destino = "Tuluá", DestinoMunicipioId = 76834 }, 3), Capturada(Linea(), 3)]);
        cotizacion.RegistrarEnvioCosteo("SOL-2", false, _ahora, _ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        clsCotizacionMuestra.AplicarCosteo(cotizacion, [1_000_000m, 2_000_000m], EstadoCosteo.Autocosteo, null);
        return cotizacion;
    }
}
