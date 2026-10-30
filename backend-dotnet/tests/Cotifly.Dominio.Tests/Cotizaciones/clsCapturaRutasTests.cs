using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// Captura por rutas (HU-021.6, #132): reglas de una captura nueva (municipios con id, mercancías con su tipo) y
/// <see cref="clsCotizacionSolicitud.RegistrarCaptura"/> (rutas con su línea de negocio, camiones del planificador, conexos con el costo de
/// Nexo y ciudades del contenedor por id + nombre).
/// </summary>
public sealed class clsCapturaRutasTests
{
    private static clsLineaSolicitudCosteo Ruta() => clsSolicitudCosteoMuestra.RutaCaptura();

    [Fact]
    public void UnaCapturaCompletaNoTieneErroresDeCaptura()
    {
        Ruta().Errores().Should().BeEmpty();
        Ruta().ErroresCaptura().Should().BeEmpty();
    }

    [Fact]
    public void SinIdsDeMunicipioNiTipoDeMercanciaLaCapturaLosExige()
    {
        var linea = Ruta() with
        {
            OrigenMunicipioId = null,
            DestinoMunicipioId = null,
            CiudadDevolucionMunicipioId = null,
            CiudadLevanteContenedor = "Yumbo",
            Posiciones = [new clsPosicionCarga("Cajas", 1, 10m, 10m, 10m, 28_500.5m)],
        };

        linea.ErroresCaptura().Select(e => (e.Campo, e.Codigo)).Should().Equal(
            ("origen_municipio_id", clsCodigosLineaSolicitud.CampoRequerido),
            ("destino_municipio_id", clsCodigosLineaSolicitud.CampoRequerido),
            ("ciudad_devolucion_municipio_id", clsCodigosLineaSolicitud.CampoRequerido),
            ("ciudad_levante_municipio_id", clsCodigosLineaSolicitud.CampoRequerido));
        // HU-023.3: el codigo_mercancia solo se exige con el cubicador configurado (lo valida la captura, no la línea).
    }

    [Fact]
    public void ConElIdDeMunicipioElNombreNoEsObligatorioYUnIdInvalidoSeReporta()
    {
        (Ruta() with { Origen = string.Empty, Destino = " " }).Errores().Should().BeEmpty("el nombre se copia del catálogo al resolver el id");
        (Ruta() with { Origen = string.Empty, OrigenMunicipioId = null }).Errores().Should().ContainSingle(e => e.Campo == "origen");
        (Ruta() with { CiudadLevanteMunicipioId = 0 }).Errores()
            .Should().ContainSingle(e => e.Campo == "ciudad_levante_municipio_id" && e.Codigo == clsCodigosLineaSolicitud.ValorFueraDeRango);
    }

    [Theory]
    [InlineData(76001, 76109, null, "Buenaventura", true)]
    [InlineData(76109, 76109, "Otro nombre", "Buenaventura", false)]
    [InlineData(null, null, "Yumbo", "Buenaventura", true)]
    [InlineData(null, null, " buenaventura ", "Buenaventura", false)]
    [InlineData(76892, null, null, "Buenaventura", true)]
    [InlineData(null, null, null, "Buenaventura", false)]
    public void ElLevanteEsDistintoDelOrigenPorIdOPorNombre(int? levanteId, int? origenId, string? levante, string origen, bool distinto)
    {
        var linea = Ruta() with { CiudadLevanteMunicipioId = levanteId, OrigenMunicipioId = origenId, CiudadLevanteContenedor = levante, Origen = origen };

        linea.LevanteDistintoDelOrigen.Should().Be(distinto);
        (linea with { ValorAprovisionamiento = 150_000m }).ErroresCondicionales().Any(e => e.Campo == "valor_levante_contenedor").Should().Be(!distinto);
    }

    [Fact]
    public void RegistrarCapturaCreaCadaRutaConSusCamionesConexosYCiudadesPorIdYNombre()
    {
        var cotizacion = clsCotizacionMuestra.Cabecera();
        var segunda = Ruta() with { CiudadLevanteMunicipioId = 76001, CiudadLevanteContenedor = "Cali", LevanteContenedorDiferente = true, ValorAprovisionamiento = 300_000m };

        var rutas = cotizacion.RegistrarCaptura(clsSolicitudCosteoMuestra.CrearCaptura().Encabezado,
        [
            new clsRutaCapturada(Ruta(), 3, 0, [new clsCamionPlaneado(null, null, 28_500.5m)], [new clsCambioConexo("SC-1", "Cargue", 150_000m, 2m)]),
            new clsRutaCapturada(segunda, 7, 1,
                [new clsCamionPlaneado(null, null, 14_000m), new clsCamionPlaneado("3S3", "FURGON", 14_500.5m, Guid.NewGuid())], []),
        ]);

        rutas.Select(r => (r.Orden, r.LineaNegocioId, r.RutaOrigenIndice)).Should().Equal((1, (int?)3, (int?)0), (2, (int?)7, (int?)1));
        cotizacion.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        rutas[0].CiudadDevolucionMunicipioId.Should().Be(76109);
        rutas[0].CiudadDevolucionNombre.Should().Be("Buenaventura");
        rutas[0].Vehiculos.Should().ContainSingle().Which.TipoVehiculo.Should().BeNull("sin cubicador la Mesa de Costeo elige el vehículo");
        rutas[0].CostoServiciosConexos.Should().Be(300_000m, "el precio del conexo es el costo de Nexo y suma a la ruta (decisión 3)");
        rutas[0].PrecioFinalLinea.Should().BeNull("sin flete no hay precio");
        rutas[1].CiudadLevanteMunicipioId.Should().Be(76001);
        rutas[1].CiudadLevanteNombre.Should().Be("Cali");
        rutas[1].Vehiculos.Should().HaveCount(2).And.OnlyContain(v => v.CostoLevanteContenedor == 300_000m, "el levante se copia a cada camión (decisión 4)");
        rutas[1].ALinea().CiudadLevanteMunicipioId.Should().Be(76001, "la ruta vuelve a la captura con sus ids (reintento y re-solicitud)");
    }

    [Fact]
    public void RegistrarCapturaUsaElIdDeRutaQueYaConoceElCubicadorYGuardaLaRecomendacionDeCadaCamion()
    {
        // HU-021.5 (#133): el id de la ruta viaja al cubicador (IdLineaSolicitud) antes de guardar, así que la ruta nace con él.
        var rutaId = Guid.NewGuid();
        var recomendacion = Guid.NewGuid();

        var rutas = clsCotizacionMuestra.Cabecera().RegistrarCaptura(clsSolicitudCosteoMuestra.CrearCaptura().Encabezado,
        [
            new clsRutaCapturada(Ruta(), 3, 0, [new clsCamionPlaneado("3S3", "FURGON", 16_000m, recomendacion), new clsCamionPlaneado(null, null, 12_500m)], [], rutaId),
            new clsRutaCapturada(Ruta(), 3, 1, [new clsCamionPlaneado(null, null, 1m)], []),
        ]);

        rutas[0].Id.Should().Be(rutaId);
        rutas[0].Vehiculos.Select(v => v.IdRecomendacionVehiculo).Should().Equal(recomendacion, null);
        rutas[0].VehiculosOrdenados.Select(v => (v.OrdenVehiculo, v.PesoKg)).Should().Equal([((int?)1, (decimal?)16_000m), (2, 12_500m)],
            "sin persistir (createdAt igual) los camiones quedan en el orden de la recomendación, no en el de su id");
        rutas[1].Id.Should().NotBe(Guid.Empty).And.NotBe(rutaId, "sin id fijado la ruta recibe uno nuevo");
    }

    [Fact]
    public void HU014_ReemplazarLosCamionesLosVuelveANumerarDesdeUnoYUnoAgregadoVaDespuesDelUltimo()
    {
        var ruta = clsCotizacionMuestra.Cabecera().RegistrarCaptura(clsSolicitudCosteoMuestra.CrearCaptura().Encabezado,
        [
            new clsRutaCapturada(Ruta(), 3, 0, [new clsCamionPlaneado("3S3", "FURGON", 16_000m), new clsCamionPlaneado("2", "FURGON", 1_000m)], []),
        ])[0];

        ruta.ReemplazarCamiones([new clsCamionPlaneado("2S3", "ESTACAS", 9_000m)], null);
        ruta.AgregarVehiculo("4X2", null, 500m);

        ruta.VehiculosOrdenados.Select(v => (v.OrdenVehiculo, v.TipoVehiculo)).Should().Equal([((int?)1, "2S3"), (2, "4X2")]);
    }

    [Fact]
    public void HU014_EnOrdenPoneLosNumeradosPorSuNumeroYDespuesLosQueNoTienenOrdenVehiculo()
    {
        // Los numerados primero, por su número aunque el id diga otra cosa; las filas sin ordenVehiculo después, por createdAt.
        var linea = Guid.NewGuid();
        var tercero = clsVehiculoLineaSolicitud.Crear(linea, "C", null, 1m, null, null, ordenVehiculo: 3);
        var segundo = clsVehiculoLineaSolicitud.Crear(linea, "B", null, 1m, null, null, ordenVehiculo: 2);
        var viejo = clsVehiculoLineaSolicitud.Crear(linea, "A", null, 1m, null, null);

        clsVehiculoLineaSolicitud.EnOrden([viejo, tercero, segundo]).Select(v => v.TipoVehiculo).Should().Equal("B", "C", "A");
        FluentActions.Invoking(() => clsVehiculoLineaSolicitud.Crear(linea, "X", null, 1m, null, null, ordenVehiculo: 0))
            .Should().Throw<ArgumentOutOfRangeException>("el orden empieza en 1, como el CHECK del script 03");
    }

    [Fact]
    public void RegistrarCapturaRechazaUnaRutaSinCamionesOSinLasReglasDeCaptura()
    {
        var encabezado = clsSolicitudCosteoMuestra.CrearCaptura().Encabezado;

        var sinCamiones = () => clsCotizacionMuestra.Cabecera().RegistrarCaptura(encabezado, [new clsRutaCapturada(Ruta(), 3, 0, [], [])]);
        var sinTipo = () => clsCotizacionMuestra.Cabecera().RegistrarCaptura(encabezado,
            [new clsRutaCapturada(Ruta() with { OrigenMunicipioId = null }, 3, 0, [new clsCamionPlaneado(null, null, 1m)], [])]);
        var sinRutas = () => clsCotizacionMuestra.Cabecera().RegistrarCaptura(encabezado, []);

        sinCamiones.Should().Throw<ArgumentException>().WithMessage("*camión*");
        sinTipo.Should().Throw<ArgumentException>().WithMessage("Ruta 1:*municipio*");
        sinRutas.Should().Throw<ArgumentException>();
    }
}
