using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Seguros;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Seguros;

/// <summary>
/// La política de seguros sobre la cotización. Las medidas pendientes de las rutas cotizables (las rechazadas no
/// cuentan), el bloqueo de "Finalizar" con <c>medidas_obligatorias_faltantes</c>, la justificación que salta SOLO ese bloqueo
/// y el <c>codigoServicio</c> de los conexos.
/// </summary>
public sealed class clsPoliticaSegurosCotizacionTests
{
    private const decimal M = clsPoliticaSegurosMuestra.Millon;
    private static readonly DateTimeOffset _ahora = clsCotizacionMuestra.Ahora;
    private static readonly Guid _ana = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly clsPoliticaSeguros _politica = clsPoliticaSegurosMuestra.Aprobada();

    private static clsCotizacionSolicitud Costeada(decimal? valor) =>
        clsCotizacionMuestra.Costeada(usuarioId: _ana, solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = valor });

    /// <summary>Formulario de varias rutas costeado, cada una con su valor de la mercancía.</summary>
    private static clsCotizacionSolicitud Rutas(params decimal?[] valores)
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-AP-28092026-012", UsuarioId = _ana, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S.",
        }).Valor;
        var lineas = valores.Select((v, i) => new clsLineaSolicitudCosteo
        {
            Origen = "Buenaventura", Destino = $"Destino {i + 1}", OrigenMunicipioId = 76109, DestinoMunicipioId = 11001 + i,
            TipoVehiculo = "3S3", Carroceria = "FURGON", PesoKg = 10_000m, ValorMercancia = v,
        }).ToList();
        cotizacion.RegistrarSolicitudLicitacion(
            new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot }, lineas, [.. Enumerable.Range(0, valores.Length).Select(i => (int?)i)], 3);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, _ahora, _ahora);
        foreach (var ruta in cotizacion.Rutas)
        {
            ruta.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        }

        clsCotizacionMuestra.AplicarCosteo(cotizacion, [.. valores.Select(_ => 1_000_000m)], EstadoCosteo.Autocosteo, null);
        return cotizacion;
    }

    private static clsCotizacionSolicitud ConConexo(clsCotizacionSolicitud cotizacion, string codigo, int orden = 1)
    {
        cotizacion.Rutas.Single(r => r.Orden == orden).AgregarOActualizarConexo(
            new clsCambioConexo($"FILA-{codigo}-{orden}", codigo, 100_000m, 1m, $"CAT-{codigo}", "adicional", CodigoServicio: codigo));
        cotizacion.RecalcularTotales(_ahora);
        return cotizacion;
    }

    // ------------------------------------------------------------------ Medidas pendientes por ruta

    [Fact]
    public void UnaRutaDe400MSinAcompananteTieneLaObligacionPendiente()
    {
        var cotizacion = Costeada(400 * M);

        var faltantes = cotizacion.MedidasObligatoriasPendientes(_politica);

        var ruta = faltantes.Should().ContainSingle().Subject;
        ruta.LineaOrden.Should().Be(1);
        ruta.Origen.Should().Be(cotizacion.PrimeraRuta!.OrigenNombre);
        ruta.Destino.Should().Be(cotizacion.PrimeraRuta!.DestinoNombre);
        ruta.Medidas.Single().Codigo.Should().Be("acompanante_vehicular");
    }

    [Fact]
    public void ConElAcompananteEnLaRutaNoQuedaNadaPendiente() =>
        ConConexo(Costeada(400 * M), "AVT_AUTO").MedidasObligatoriasPendientes(_politica).Should().BeEmpty();

    [Fact]
    public void ElConexoDeOtraRutaNoCumpleLaObligacion()
    {
        var cotizacion = ConConexo(Rutas(400 * M, 100 * M), "AVP_MOTO", orden: 2);

        cotizacion.MedidasObligatoriasPendientes(_politica).Should().ContainSingle().Which.LineaOrden.Should().Be(1);
    }

    [Fact]
    public void LasRutasRechazadasPorLaMesaNoCuentan()
    {
        var cotizacion = Rutas(400 * M, 600 * M);
        var rechazada = cotizacion.Rutas.Single(r => r.Orden == 1);
        rechazada.Vehiculos[0].RegistrarRechazo("Ruta no operada", _ahora);
        rechazada.ResumirEstado(true);
        rechazada.Rechazada.Should().BeTrue();

        var faltantes = cotizacion.MedidasObligatoriasPendientes(_politica);

        faltantes.Should().ContainSingle().Which.LineaOrden.Should().Be(2);
    }

    [Fact]
    public void UnaRutaSinValorDeMercanciaOConCeroNoBloquea()
    {
        Costeada(null).MedidasObligatoriasPendientes(_politica).Should().BeEmpty();
        Costeada(0m).MedidasObligatoriasPendientes(_politica).Should().BeEmpty();
        Costeada(null).PrimeraRuta!.EvaluarPoliticaSeguros(_politica).Aviso.Should().Be(clsPoliticaSeguros.AvisoSinValorMercancia);
        Costeada(0m).PrimeraRuta!.EvaluarPoliticaSeguros(_politica).Aviso.Should().Be(clsPoliticaSeguros.AvisoSinValorMercancia);
    }

    [Fact]
    public void UnaRutaDeVariosCamionesUsaElValorTotalDeLaRutaSinDividirPorCamion()
    {
        var ruta = clsCotizacionMuestra.Cabecera().RegistrarCaptura(clsSolicitudCosteoMuestra.CrearCaptura().Encabezado,
        [
            new clsRutaCapturada(clsSolicitudCosteoMuestra.RutaCaptura() with { ValorMercancia = 400 * M }, 3, 0,
                [new clsCamionPlaneado("3S3", "FURGON", 16_000m), new clsCamionPlaneado("3S3", "FURGON", 12_500m)], []),
        ])[0];

        ruta.Vehiculos.Should().HaveCount(2);
        var evaluacion = ruta.EvaluarPoliticaSeguros(_politica);

        evaluacion.Tramo!.Nombre.Should().Be("> 350 – 500 M", "400 M es de la ruta (no 200 M por camión, que caería en > 150 – 250 M)");
        evaluacion.Pendientes.Should().ContainSingle();
    }

    [Fact]
    public void UnConexoSinCodigoServicioNoCumpleNada()
    {
        var cotizacion = Costeada(400 * M);
        cotizacion.PrimeraRuta!.AgregarOActualizarConexo(new clsCambioConexo("FILA-1", "Acompañante", 100_000m, 1m));

        cotizacion.MedidasObligatoriasPendientes(_politica).Should().ContainSingle();
    }

    // ------------------------------------------------------------------ Finalizar

    [Fact]
    public void FinalizarConUnaMedidaPendienteYSinJustificacionRespondeNoFinalizableConElDetallePorRuta()
    {
        var cotizacion = Rutas(400 * M, 800 * M);
        var faltantes = cotizacion.MedidasObligatoriasPendientes(_politica);

        var resultado = cotizacion.Finalizar(_ana, _ahora, medidasFaltantes: faltantes);

        resultado.EsFallo.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Datos!["motivo"].Should().Be("medidas_obligatorias_faltantes");
        var rutas = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)resultado.Error.Datos["rutas"]!;
        rutas.Select(r => r["linea_orden"]).Should().Equal(1, 2);
        rutas[1]["origen"].Should().Be(cotizacion.Rutas.Single(r => r.Orden == 2).OrigenNombre);
        rutas[1]["destino"].Should().Be(cotizacion.Rutas.Single(r => r.Orden == 2).DestinoNombre);
        var medidas = (IEnumerable<IReadOnlyDictionary<string, object?>>)rutas[1]["medidas"]!;
        medidas.Single()["codigo"].Should().Be("acompanante_total");
        medidas.Single()["nombre"].Should().Be("Acompañante vehicular total");
        medidas.Single().Should().NotContainKey("conexos", "el detalle del contrato del 2026-11-10 solo lleva codigo y nombre");
        resultado.Error.Mensaje.Should().Contain("ruta 1: Acompañante vehicular (parcial o total)").And.Contain("ruta 2: Acompañante vehicular total");
        cotizacion.Finalizada.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnaJustificacionVaciaNoSaltaElBloqueo(string? justificacion)
    {
        var cotizacion = Costeada(400 * M);

        var resultado = cotizacion.Finalizar(_ana, _ahora, medidasFaltantes: cotizacion.MedidasObligatoriasPendientes(_politica), justificacion: justificacion);

        resultado.Error.Datos!["motivo"].Should().Be(clsCotizacionSolicitud.CodigoMedidasObligatoriasFaltantes);
    }

    [Fact]
    public void ConJustificacionSeFinalizaAunqueFalteLaMedida()
    {
        var cotizacion = Costeada(400 * M);

        var resultado = cotizacion.Finalizar(_ana, _ahora, medidasFaltantes: cotizacion.MedidasObligatoriasPendientes(_politica),
            justificacion: "Riesgo aprobó el viaje sin acompañante (correo del 2026-11-06).");

        resultado.Valor.Should().BeTrue();
        cotizacion.Finalizada.Should().BeTrue();
    }

    [Fact]
    public void LaJustificacionNoSaltaLosConexosPendientesEnNexo()
    {
        var cotizacion = Costeada(400 * M);

        var resultado = cotizacion.Finalizar(_ana, _ahora, conexosPendientesEnNexo: 1,
            medidasFaltantes: cotizacion.MedidasObligatoriasPendientes(_politica), justificacion: "Riesgo lo aprobó.");

        resultado.Error.Datos!["motivo"].Should().Be(clsCotizacionSolicitud.CodigoConexosPendientesEnNexo, "la justificación salta SOLO las medidas");
        cotizacion.Finalizada.Should().BeFalse();
    }

    [Fact]
    public void LaJustificacionNoSaltaElEstadoQueNoAdmiteFinalizar()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: null, usuarioId: _ana,
            solicitud: clsSolicitudCosteoMuestra.Crear() with { ValorMercancia = 400 * M });

        var resultado = cotizacion.Finalizar(_ana, _ahora, medidasFaltantes: cotizacion.MedidasObligatoriasPendientes(_politica),
            justificacion: "Riesgo lo aprobó.");

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoFinalizable);
        resultado.Error.Datos!.Should().NotContainKey("motivo");
    }

    [Fact]
    public void SinMedidasPendientesSeFinalizaSinJustificacion() =>
        ConConexo(Costeada(400 * M), "AVP_MOTO_ARMADA").Finalizar(_ana, _ahora, medidasFaltantes: []).Valor.Should().BeTrue();

    [Fact]
    public void UnaFinalizadaNoSeRecalculaFinalizarOtraVezNoMiraLasMedidas()
    {
        var cotizacion = Costeada(400 * M);
        cotizacion.Finalizar(_ana, _ahora, medidasFaltantes: cotizacion.MedidasObligatoriasPendientes(_politica), justificacion: "Aprobado.")
            .Valor.Should().BeTrue();
        var finalizadaAt = cotizacion.FinalizadaAt;

        var otra = cotizacion.Finalizar(_ana, _ahora.AddHours(1), medidasFaltantes: cotizacion.MedidasObligatoriasPendientes(_politica));

        otra.Valor.Should().BeFalse("ya estaba finalizada: idempotente, sin bloqueo nuevo (decisión 7)");
        cotizacion.FinalizadaAt.Should().Be(finalizadaAt);
    }

    [Fact]
    public void UnConexoQueNoCuentaNoCumpleLaMedida()
    {
        // El caso de uso excluye los conexos fallido_definitivo o incierto en Nexo.
        var cotizacion = ConConexo(Costeada(400 * M), "AVT_AUTO");

        cotizacion.MedidasObligatoriasPendientes(_politica, (_, _) => false).Should().ContainSingle();
        cotizacion.MedidasObligatoriasPendientes(_politica, (_, _) => true).Should().BeEmpty();
        cotizacion.PrimeraRuta!.EvaluarPoliticaSeguros(_politica, _ => false).Pendientes.Should().ContainSingle();
    }

    // ------------------------------------------------------------------ codigoServicio del conexo

    [Fact]
    public void ElConexoGuardaElCodigoDelServicioAlAgregarlo()
    {
        var cotizacion = ConConexo(Costeada(400 * M), "AVT_MOTO");

        cotizacion.PrimeraRuta!.Conexos.Single().CodigoServicio.Should().Be("AVT_MOTO");
    }

    [Fact]
    public void CompletarLosCodigosSoloLlenaLosNullYNoCambiaElPrecioNiLaVersion()
    {
        var cotizacion = ConConexo(Costeada(400 * M), "AVT_MOTO");
        cotizacion.PrimeraRuta!.AgregarOActualizarConexo(new clsCambioConexo("FILA-VIEJA", "Acompañante total en auto", 100_000m, 1m));
        cotizacion.RecalcularTotales(_ahora);
        var (precio, version) = (cotizacion.PrecioFinal, cotizacion.VersionNegocio);

        var completados = cotizacion.CompletarCodigosConexos(c => c.IdServicioConexo == "FILA-VIEJA" ? " AVT_AUTO " : "OTRO");

        completados.Should().ContainSingle().Which.IdServicioConexo.Should().Be("FILA-VIEJA");
        cotizacion.PrimeraRuta.Conexos.Select(c => c.CodigoServicio).Should().BeEquivalentTo(["AVT_MOTO", "AVT_AUTO"]);
        (cotizacion.PrecioFinal, cotizacion.VersionNegocio).Should().Be((precio, version));
        cotizacion.MedidasObligatoriasPendientes(_politica).Should().BeEmpty();
    }

    [Fact]
    public void UnaLecturaSinCodigoNoBorraElGuardadoYUnoQueNoCabeNoSeGuarda()
    {
        var cotizacion = ConConexo(Costeada(400 * M), "AVT_MOTO");
        var ruta = cotizacion.PrimeraRuta!;

        ruta.AgregarOActualizarConexo(new clsCambioConexo("FILA-AVT_MOTO-1", "AVT_MOTO", 120_000m, 2m));
        cotizacion.CompletarCodigosConexos(_ => new string('X', 41)).Should().BeEmpty();

        ruta.Conexos.Single().CodigoServicio.Should().Be("AVT_MOTO");
        ruta.AgregarOActualizarConexo(new clsCambioConexo("FILA-NUEVA", "Otro", 1m, 1m, CodigoServicio: new string('X', 41)));
        ruta.Conexos.Single(c => c.IdServicioConexo == "FILA-NUEVA").CodigoServicio.Should().BeNull("no se guarda un código truncado");
    }
}
