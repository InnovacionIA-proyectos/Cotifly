using Cotifly.Aplicacion.Comun.Exceptions;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Services;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCotizacionRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task UnaCotizacionSeGuardaYSeRecuperaPorFolioConTodosSusDatos()
    {
        var usuarioId = await CrearUsuarioAsync();
        var original = NuevaCotizacion(usuarioId);

        await GuardarAsync(original);
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(original.Folio, Ct));

        leida.Should().NotBeNull();
        leida!.Id.Should().Be(original.Id);
        leida.UsuarioId.Should().Be(usuarioId);
        leida.NitCliente.Should().Be("900123456");
        leida.OrigenCliente.Should().Be(OrigenCliente.Cliente);
        leida.PrecioFinal.Should().Be(1_500_000m);

        // La línea de negocio, el margen y el costo viven en la ruta; el flete, en su camión.
        var ruta = leida.Rutas.Should().ContainSingle().Subject;
        (ruta.LineaNegocioId, ruta.MargenAplicado, ruta.OrigenMargen, ruta.CostoFinal, ruta.PrecioFinalLinea)
            .Should().Be(((int?)1, (decimal?)0.15m, (OrigenMargen?)OrigenMargen.PoliticaLinea, (decimal?)1_275_000m, (decimal?)1_500_000m));
        ruta.Vehiculos.Should().ContainSingle().Which.CostoFlete.Should().Be(1_275_000m);
        leida.Moneda.Should().Be("COP");
        leida.TrmAplicada.Should().Be(0m);
        leida.Version.Should().Be(1);
        leida.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        leida.CreatedAt.Should().Be(Reloj.AhoraUtc);
        leida.UpdatedAt.Should().Be(Reloj.AhoraUtc);
    }

    /// <summary>
    /// Recargar reconstruye las colecciones desde la base. Una fila que otro proceso borró desaparece (y
    /// el guardado siguiente no la reinserta) y una que otro proceso agregó aparece.
    /// </summary>
    [Fact]
    public async Task RecargarReconstruyeElAgregadoYNoReinsertaLasFilasQueOtroProcesoBorro()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-1", "Cargue", 10_000m, 1m), new clsCambioConexo("CX-2", "Escolta", 5_000m, 1m)], Reloj.AhoraUtc);
        await GuardarAsync(cotizacion);

        await using var ambito = NuevoAmbito();
        var repositorio = ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>();
        var leida = (await repositorio.ObtenerPorIdAsync(cotizacion.Id, Ct))!;
        leida.PrimeraRuta!.Conexos.Should().HaveCount(2);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            DELETE s FROM dbo_cotifly.CotizacionServiciosConexos s
              JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId
             WHERE l.cotizacionSolicitudId = '{cotizacion.Id}' AND s.idServicioConexo = N'CX-1';
            INSERT INTO dbo_cotifly.CotizacionServiciosConexos (lineaSolicitudId, idServicioConexo, nombreServicioConexo, precio, cantidad, precioVenta)
            SELECT id, N'CX-3', N'Precinto', 1000, 1, 1000 FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = '{cotizacion.Id}';
            """);

        await repositorio.RecargarAsync(leida, Ct);

        leida.PrimeraRuta!.Conexos.Select(c => c.IdServicioConexo).Should().BeEquivalentTo(["CX-2", "CX-3"]);
        leida.RegistrarEnvio();
        await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                "SELECT STRING_AGG(s.idServicioConexo, ',') WITHIN GROUP (ORDER BY s.idServicioConexo) FROM dbo_cotifly.CotizacionServiciosConexos s "
                + $"JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId WHERE l.cotizacionSolicitudId = '{cotizacion.Id}'"))
            .Should().Be("CX-2,CX-3", "la fila borrada no se reinserta");
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                $"SELECT estadoEnvio FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{cotizacion.Id}'"))
            .Should().Be("enviada", "el cambio después de recargar sí se guarda");
    }

    /// <summary>
    /// Script 19: <c>precioVenta</c> es NOT NULL sin DEFAULT. Agregar un conexo no falla (EF manda la columna; sin ella, SQL
    /// Server respondería 515) y guarda <c>precioVenta = precio</c>; actualizarlo lo mantiene igual al
    /// precio.
    /// </summary>
    [Fact]
    public async Task AgregarYActualizarUnConexoGuardaPrecioVentaIgualAlPrecio()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.CambiarConexos(1, [new clsCambioConexo("CX-PV", "Cargue", 12_345.67m, 2m)], Reloj.AhoraUtc);

        await GuardarAsync(cotizacion);

        (await PrecioYPrecioVentaAsync(cotizacion.Id, "CX-PV")).Should().Be("12345.67|12345.67", "al agregar, precioVenta = precio");

        await using (var ambito = NuevoAmbito())
        {
            var leida = (await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
            leida.CambiarConexos(1, [new clsCambioConexo("CX-PV", "Cargue", 20_000m, 3m)], Reloj.AhoraUtc);
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        (await PrecioYPrecioVentaAsync(cotizacion.Id, "CX-PV")).Should().Be("20000.00|20000.00", "al actualizar, precioVenta sigue al precio");
    }

    private Task<string?> PrecioYPrecioVentaAsync(Guid cotizacionId, string idServicioConexo) => clsBaseDatosPruebas.EscalarAsync<string>(
        BaseDatos.CadenaCotifly,
        "SELECT CONCAT(s.precio, '|', s.precioVenta) FROM dbo_cotifly.CotizacionServiciosConexos s "
        + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
        + $"WHERE l.cotizacionSolicitudId = '{cotizacionId}' AND s.idServicioConexo = N'{idServicioConexo}'");

    [Fact]
    public async Task LosEnumsSeGuardanConElCodigoQueExigenLosCheckDelEsquema()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.RegistrarEnvio();
        await GuardarAsync(cotizacion);

        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(c.origenCliente, '|', l.origenMargen, '|', c.estadoEnvio) FROM dbo_cotifly.CotizacionesSolicitud c "
            + $"JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id WHERE c.id = '{cotizacion.Id}'");

        fila.Should().Be("cliente|politica_linea|enviada");
    }

    [Fact]
    public async Task ExisteFolioDistingueFoliosUsadosDeLibres()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);

        var (usado, libre) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            return (await repo.ExisteFolioAsync(cotizacion.Folio, Ct), await repo.ExisteFolioAsync(FolioUnico(), Ct));
        });

        usado.Should().BeTrue();
        libre.Should().BeFalse();
    }

    [Fact]
    public async Task UnFolioRepetidoSeRechazaConElErrorDeNegocioFolioDuplicado()
    {
        var folio = FolioUnico();
        await GuardarAsync(NuevaCotizacion(null, folio));

        var guardarOtra = () => GuardarAsync(NuevaCotizacion(null, folio));

        var excepcion = (await guardarOtra.Should().ThrowAsync<clsViolacionUnicidadException>()).Which;
        excepcion.Restriccion.Should().Be("uq_cotizaciones_folio");
        excepcion.Error.Codigo.Should().Be(clsViolacionUnicidadException.CodigoFolioDuplicado);
    }

    [Fact]
    public async Task TrasUnFolioDuplicadoLaUnidadDeTrabajoDescartaLoPendienteYSigueUsable()
    {
        var folio = FolioUnico();
        await GuardarAsync(NuevaCotizacion(null, folio));
        var otra = NuevaCotizacion(null);

        await using var ambito = NuevoAmbito();
        var repo = ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>();
        var unidad = ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>();
        repo.Agregar(NuevaCotizacion(null, folio));
        await FluentActions.Awaiting(() => unidad.GuardarCambiosAsync(Ct)).Should().ThrowAsync<clsViolacionUnicidadException>();

        repo.Agregar(otra);
        (await unidad.GuardarCambiosAsync(Ct)).Should().Be(3, "la cabecera, su ruta y su camión (HU-021)");
    }

    [Fact]
    public async Task ListarPorUsuarioDevuelveSoloLasSuyasDeLaMasRecienteALaMasAntigua()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var primera = NuevaCotizacion(ana);
        await GuardarAsync(primera);
        Reloj.Avanzar(TimeSpan.FromMinutes(1));
        var segunda = NuevaCotizacion(ana);
        await GuardarAsync(segunda);
        await GuardarAsync(NuevaCotizacion(luis));

        var deAna = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarPorUsuarioAsync(ana, cancellationToken: Ct));

        deAna.Select(c => c.Folio).Should().Equal(segunda.Folio, primera.Folio);
    }

    [Fact]
    public async Task ListarPorUsuarioRespetaElLimite()
    {
        var usuario = await CrearUsuarioAsync();
        for (var i = 0; i < 3; i++)
        {
            Reloj.Avanzar(TimeSpan.FromSeconds(1));
            await GuardarAsync(NuevaCotizacion(usuario));
        }

        var dos = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarPorUsuarioAsync(usuario, 2, Ct));

        dos.Should().HaveCount(2);
    }

    [Fact]
    public async Task UnComercialNoObtienePorFolioNiPorIdLaCotizacionDeOtro()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var deAna = NuevaCotizacion(ana);
        await GuardarAsync(deAna);

        var (porFolioAna, porFolioLuis, porIdAna, porIdLuis) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            return (await repo.ObtenerPorFolioDelUsuarioAsync(deAna.Folio, ana, Ct),
                await repo.ObtenerPorFolioDelUsuarioAsync(deAna.Folio, luis, Ct),
                await repo.ObtenerPorIdDelUsuarioAsync(deAna.Id, ana, Ct),
                await repo.ObtenerPorIdDelUsuarioAsync(deAna.Id, luis, Ct));
        });

        porFolioAna.Should().NotBeNull();
        porIdAna.Should().NotBeNull();
        porFolioLuis.Should().BeNull();
        porIdLuis.Should().BeNull();
    }

    [Fact]
    public async Task MarcarSeguimientoDeUnaCotizacionAjenaDevuelveNullYNoLaModifica()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var deAna = NuevaCotizacion(ana);
        await GuardarAsync(deAna);

        // La regla vive en IClsSeguimientoService (ADR-008); aquí se prueba con el repositorio real.
        var marcada = await ConsultarAsync(s => s.GetRequiredService<IClsSeguimientoService>()
            .MarcarAsync(luis, deAna.Folio, true, null, Ct));
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(deAna.Folio, Ct));

        marcada.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        leida!.Ganada.Should().BeNull();
        leida.MarcadoAt.Should().BeNull();
    }

    [Fact]
    public async Task MarcarSeguimientoPropioPersisteElResultadoYActualizaUpdatedAt()
    {
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        var creada = Reloj.AhoraUtc;
        Reloj.Avanzar(TimeSpan.FromHours(2));

        var marcada = await ConsultarAsync(s => s.GetRequiredService<IClsSeguimientoService>()
            .MarcarAsync(ana, cotizacion.Folio, false, "Valor muy alto", Ct));
        marcada.EsExitoso.Should().BeTrue();
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));

        leida!.Ganada.Should().BeFalse();
        leida.MotivoRechazo.Should().Be("Valor muy alto");
        leida.MarcadoAt.Should().Be(Reloj.AhoraUtc);
        leida.UpdatedAt.Should().Be(Reloj.AhoraUtc, "el interceptor fija updatedAt con el reloj de la aplicación");
        leida.CreatedAt.Should().Be(creada, "createdAt no cambia al modificar");
    }

    [Fact]
    public async Task SinCambiosNoSeTocaUpdatedAt()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);
        var creada = Reloj.AhoraUtc;
        Reloj.Avanzar(TimeSpan.FromHours(1));

        await EnAmbitoAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));

        leida!.UpdatedAt.Should().Be(creada);
    }

    [Fact]
    public async Task LaSolicitudDeCosteoConSuRutaCamionYMercanciasSeGuardaYSeReconstruyeSinPerderDatos()
    {
        var solicitud = clsSolicitudCosteoMuestra.Crear();
        var cotizacion = Formulario(await CrearUsuarioAsync());

        await GuardarAsync(cotizacion);
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));

        leida!.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        var ruta = leida.Rutas.Should().ContainSingle().Subject;
        ruta.MercanciasOrdenadas.Select(p => p.Orden).Should().Equal(1, 2);
        ruta.Vehiculos.Should().ContainSingle().Which.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        clsSolicitudCosteo.Desde(leida.EncabezadoSolicitud!, ruta.ALinea()).Should().BeEquivalentTo(
            solicitud with { RetornaContenedor = solicitud.RetornaContenedor ?? false, LevanteContenedorDiferente = solicitud.LevanteContenedorDiferente ?? false },
            "reenviar a Nexo no debe exigir volver a llenar el formulario");
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            "SELECT CONCAT(c.tipoSolicitud, '|', c.estadoCosteo, '|', l.claseContenedor, '|', l.pesoKg, '|', l.origenNombre, '|', l.ciudadDevolucionNombre, '|', v.tipoVehiculo, '|', v.estadoCosteo) "
            + "FROM dbo_cotifly.CotizacionesSolicitud c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id "
            + $"JOIN dbo_cotifly.CotizacionVehiculosLineaSolicitud v ON v.lineaSolicitudId = l.id WHERE c.id = '{cotizacion.Id}'");
        fila.Should().Be("spot|pendiente_envio|CNT40.HC|28500.50|Buenaventura|Buenaventura|3S3|pendiente_envio");
    }

    /// <summary>
    /// Script 12: el vehículo elegido de la ruta (<c>tipoVehiculoElegido</c> y <c>carroceriaElegida</c>) y una mercancía no
    /// cubicable (sin medidas: <c>largoCm</c>, <c>anchoCm</c> y <c>altoCm</c> en NULL) se guardan y se reconstruyen igual.
    /// </summary>
    [Fact]
    public async Task ElVehiculoElegidoYUnaMercanciaSinMedidasSeGuardanYSeReconstruyenIgual()
    {
        var solicitud = clsSolicitudCosteoMuestra.Crear() with
        {
            TipoVehiculo = "2S3",
            Carroceria = "FURGON",
            PesoKg = 29_500m,
            Posiciones = [new clsPosicionCarga("Estiba de café", 20, 120m, 100m, 150m, 1425m), new clsPosicionCarga("Bultos", 2, 0m, 0m, 0m, 500m)],
        };
        var cotizacion = clsCotizacionMuestra.Formulario(folio: FolioUnico(), usuarioId: await CrearUsuarioAsync(), lineaNegocioId: 1, solicitud: solicitud);
        var original = cotizacion.PrimeraRuta!;
        (original.TipoVehiculoElegido, original.CarroceriaElegida).Should().Be(("2S3", "FURGON"));

        await GuardarAsync(cotizacion);
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));

        var ruta = leida!.Rutas.Should().ContainSingle().Subject;
        (ruta.TipoVehiculoElegido, ruta.CarroceriaElegida).Should().Be(("2S3", "FURGON"));
        ruta.VehiculoElegido.Should().Be(("2S3", "FURGON"));
        ruta.MercanciasOrdenadas.Select(m => (m.Orden, m.LargoCm, m.AnchoCm, m.AltoCm, m.PesoKg)).Should().Equal(
            original.MercanciasOrdenadas.Select(m => (m.Orden, m.LargoCm, m.AnchoCm, m.AltoCm, m.PesoKg)));
        var sinMedidas = ruta.MercanciasOrdenadas[1];
        (sinMedidas.LargoCm, sinMedidas.AnchoCm, sinMedidas.AltoCm).Should().Be(((decimal?)null, (decimal?)null, (decimal?)null));
        sinMedidas.APosicion().SinMedidas.Should().BeTrue("vuelve a la captura como una mercancía sin medidas");
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                "SELECT CONCAT(l.tipoVehiculoElegido, '|', l.carroceriaElegida, '|', "
                + "(SELECT COUNT(*) FROM dbo_cotifly.CotizacionMercancias m WHERE m.lineaSolicitudId = l.id AND m.largoCm IS NULL AND m.anchoCm IS NULL AND m.altoCm IS NULL)) "
                + $"FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = '{cotizacion.Id}'"))
            .Should().Be("2S3|FURGON|1");
    }

    [Fact]
    public async Task UnaCotizacionDelChatTieneSuRutaSinMercanciasNiSolicitudDeCosteo()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);

        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));

        leida!.Rutas.Should().ContainSingle().Which.Mercancias.Should().BeEmpty();
        leida.EstadoCosteo.Should().BeNull("las cotizaciones del chat con costo manual no pasan por Nexo");
        leida.EncabezadoSolicitud.Should().BeNull();
        leida.CosteoConsultasFallidas.Should().Be(0);
    }

    /// <summary>
    /// El <c>scorePrioridad</c> de Nexo se guarda en <c>CotizacionesSolicitud.scorePrioridad</c> (<c>decimal(9,4)</c>, script
    /// 09) y se lee de vuelta; sin score, la columna queda en NULL.
    /// </summary>
    [Fact]
    public async Task ElScorePrioridadSeGuardaEnLaCabeceraConCuatroDecimalesYSeLeeDeVuelta()
    {
        var conScore = Formulario(null);
        conScore.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, Reloj.AhoraUtc, Reloj.AhoraUtc.AddYears(50), 0.123456m);
        var sinScore = Formulario(null);
        sinScore.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, Reloj.AhoraUtc, Reloj.AhoraUtc.AddYears(50));
        await GuardarAsync(conScore);
        await GuardarAsync(sinScore);

        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(conScore.Id, Ct));
        var leidaSin = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(sinScore.Id, Ct));

        leida!.ScorePrioridad.Should().Be(0.1235m);
        leidaSin!.ScorePrioridad.Should().BeNull();
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                $"SELECT CONCAT(CAST(scorePrioridad AS nvarchar(20)), '|', (SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud "
                + $"WHERE id = '{sinScore.Id}' AND scorePrioridad IS NULL)) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{conScore.Id}'"))
            .Should().Be("0.1235|1");
    }

    /// <summary>
    /// Script 10: el rechazo de la Mesa de Costeo que lee el polling se guarda en el camión (estado <c>rechazada</c>,
    /// <c>motivoRechazo</c> y <c>rechazadaAt</c>) y en la ruta (<c>rechazada</c>, sin precio); si la Mesa lo deshace, el camión vuelve a
    /// <c>pendiente_costeo</c> y la base queda sin motivo ni fecha (el CHECK del 10 lo exige).
    /// </summary>
    [Fact]
    public async Task ElRechazoDeLaLineaSeGuardaEnElCamionYSeBorraAlDeshacerlo()
    {
        var cotizacion = Formulario(null);
        cotizacion.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, Reloj.AhoraUtc, Reloj.AhoraUtc.AddYears(50));
        cotizacion.Vehiculos.Single().AsignarLineaNexo("N-1");
        await GuardarAsync(cotizacion);

        await using (var ambito = NuevoAmbito())
        {
            var leida = (await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
            Cotifly.Aplicacion.Costeo.Services.clsLecturaCosteo.AplicarFoto(leida, Foto("N-1", "Ruta sin operación"), Reloj.AhoraUtc,
                new Cotifly.Aplicacion.Costeo.Services.clsOpcionesCosteoNexo(), new Cotifly.Aplicacion.Costeo.Services.clsPrecioCosteoService());
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        var rechazada = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));
        var camion = rechazada!.Vehiculos.Single();
        (camion.EstadoCosteo, camion.MotivoRechazo, camion.RechazadaAt).Should().Be(
            ((EstadoCosteo?)EstadoCosteo.Rechazada, (string?)"Ruta sin operación", (DateTimeOffset?)Reloj.AhoraUtc));
        rechazada.PrimeraRuta!.EstadoCosteo.Should().Be(EstadoCosteo.Rechazada);
        rechazada.EstadoCosteo.Should().Be(EstadoCosteo.PendienteCosteo, "la cabecera no queda rechazada: la Mesa puede deshacer o entregar");
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                "SELECT CONCAT(v.estadoCosteo, '|', v.motivoRechazo, '|', CASE WHEN v.rechazadaAt IS NULL THEN 0 ELSE 1 END, '|', l.estadoCosteo, '|', "
                + "COALESCE(CAST(l.precioFinalLinea AS nvarchar(30)), 'sin precio')) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
                + $"JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId WHERE l.cotizacionSolicitudId = '{cotizacion.Id}'"))
            .Should().Be("rechazada|Ruta sin operación|1|rechazada|sin precio");

        await using (var ambito = NuevoAmbito())
        {
            var leida = (await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
            Cotifly.Aplicacion.Costeo.Services.clsLecturaCosteo.AplicarFoto(leida, Foto("N-1", null), Reloj.AhoraUtc,
                new Cotifly.Aplicacion.Costeo.Services.clsOpcionesCosteoNexo(), new Cotifly.Aplicacion.Costeo.Services.clsPrecioCosteoService());
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                "SELECT CONCAT(v.estadoCosteo, '|', COALESCE(v.motivoRechazo, 'sin motivo'), '|', CASE WHEN v.rechazadaAt IS NULL THEN 0 ELSE 1 END) "
                + "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
                + $"WHERE l.cotizacionSolicitudId = '{cotizacion.Id}'"))
            .Should().Be("pendiente_costeo|sin motivo|0", "deshacer el rechazo borra el motivo y la fecha");

        static clsCosteoConsultadoNexo Foto(string linea, string? motivo) =>
            new("EnCosteo", [linea], new clsCotizacionNexo("COT-1", "Borrador", "SinDefinir", [], []))
            {
                Detalle = [new clsLineaSolicitudNexo(linea, "x", null, null, null, null)
                {
                    EstadoLinea = motivo is null ? "porCostear" : "rechazada",
                    MotivoRechazo = motivo,
                }],
            };
    }

    [Fact]
    public async Task LaAgendaDelPollingTraeSoloLasVencidasYElReclamoLaTomaUnaSolaVez()
    {
        var vencida = Formulario(null);
        var agenda = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        vencida.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, agenda, agenda);
        var futura = Formulario(null);
        futura.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, Reloj.AhoraUtc, Reloj.AhoraUtc.AddYears(50));
        await GuardarAsync(vencida);
        await GuardarAsync(futura);

        var pendientes = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarConsultasCosteoPendientesAsync(Reloj.AhoraUtc, 500, Ct));
        pendientes.Should().Contain(new Cotifly.Aplicacion.Cotizaciones.DTOs.clsConsultaCosteoPendiente(vencida.Id, agenda));
        pendientes.Should().NotContain(p => p.Id == futura.Id);

        var reclamo = Reloj.AhoraUtc.AddMinutes(2);
        var (primera, segunda) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            return (await repo.ReclamarConsultaCosteoAsync(vencida.Id, agenda, reclamo, Ct), await repo.ReclamarConsultaCosteoAsync(vencida.Id, agenda, reclamo, Ct));
        });

        primera.Should().BeTrue();
        segunda.Should().BeFalse("otra réplica ya la reclamó");
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(vencida.Id, Ct));
        leida!.CosteoSiguienteConsultaAt.Should().Be(reclamo);
    }

    /// <summary>
    /// Reencolar desde finalizar solo agenda una cotización que está fuera de la agenda; nunca pisa un reclamo
    /// vigente del polling ni adelanta una agenda programada, aunque la cotización en memoria se haya leído antes.
    /// </summary>
    [Fact]
    public async Task ReencolarSoloAgendaUnaCotizacionFueraDeLaAgendaYNoPisaUnReclamoVigente()
    {
        var fuera = Formulario(null);
        fuera.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, Reloj.AhoraUtc, Reloj.AhoraUtc);
        fuera.DetenerPollingCosteo();
        var reclamada = Formulario(null);
        var reclamo = Reloj.AhoraUtc.AddMinutes(2);
        reclamada.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, Reloj.AhoraUtc, reclamo);
        await GuardarAsync(fuera);
        await GuardarAsync(reclamada);
        var ahora = Reloj.AhoraUtc;

        var (primera, segunda, ajena) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            return (await repo.ReencolarConsultaCosteoAsync(fuera.Id, ahora, Ct), await repo.ReencolarConsultaCosteoAsync(fuera.Id, ahora.AddSeconds(5), Ct),
                await repo.ReencolarConsultaCosteoAsync(reclamada.Id, ahora, Ct));
        });

        (primera, segunda, ajena).Should().Be((true, false, false), "la segunda ya la encuentra agendada y la tercera tiene un reclamo vigente");
        (await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(fuera.Id, Ct)))!.CosteoSiguienteConsultaAt.Should().Be(ahora);
        (await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(reclamada.Id, Ct)))!.CosteoSiguienteConsultaAt.Should().Be(reclamo);
    }

    [Fact]
    public async Task ElReclamoDeReenvioSoloGanaConElUpdatedAtLeidoYLaDejaPendienteDeEnvio()
    {
        var cotizacion = Formulario(null);
        cotizacion.RegistrarErrorEnvioCosteo("Nexo caído");
        await GuardarAsync(cotizacion);
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));
        var ahora = Reloj.AhoraUtc.AddMinutes(5);

        var (gana, pierde) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            return (await repo.ReclamarReenvioCosteoAsync(cotizacion.Id, leida!.UpdatedAt, ahora, Ct), await repo.ReclamarReenvioCosteoAsync(cotizacion.Id, leida!.UpdatedAt, ahora, Ct));
        });

        gana.Should().BeTrue();
        pierde.Should().BeFalse();
        var despues = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));
        despues!.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        despues.UpdatedAt.Should().Be(ahora);
    }

    [Fact]
    public async Task UnaCotizacionEnDolaresGuardaLaTrmConCuatroDecimales()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = FolioUnico(), Moneda = "USD", TrmAplicada = 4123.4567m }).Valor;
        await GuardarAsync(cotizacion);

        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));

        leida!.Moneda.Should().Be("USD");
        leida.TrmAplicada.Should().Be(4123.4567m);
    }

    /// <summary>
    /// Script 03: los camiones se guardan numerados 1..n en <c>ordenVehiculo</c> en el orden de la recomendación, y un
    /// recálculo (<see cref="clsLineaSolicitud.ReemplazarCamiones"/>: borra los anteriores e inserta los nuevos en el mismo guardado) los
    /// vuelve a numerar 1..n sin chocar con el índice único filtrado <c>(lineaSolicitudId, ordenVehiculo)</c>. El conexo guarda el id del
    /// catálogo (03) y la categoría (05).
    /// </summary>
    [Fact]
    public async Task LosCamionesSeGuardanNumeradosYUnRecalculoLosRenumeraSinChocarConElIndiceUnico()
    {
        var cotizacion = CapturaDeTresCamiones();
        await GuardarAsync(cotizacion);

        (await OrdenesAsync(cotizacion.Id)).Should().Be("1=3S3,2=2S3,3=2");
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                "SELECT CONCAT(s.idServicioConexo, '|', s.idServicioConexoCatalogo, '|', s.categoriaServicioConexo) FROM dbo_cotifly.CotizacionServiciosConexos s "
                + $"JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId WHERE l.cotizacionSolicitudId = '{cotizacion.Id}'"))
            .Should().Be("CX-CAT|CX-CAT|adicional");

        await using (var ambito = NuevoAmbito())
        {
            var leida = (await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
            leida.PrimeraRuta!.VehiculosOrdenados.Select(v => v.TipoVehiculo).Should().Equal("3S3", "2S3", "2");
            var conexo = leida.PrimeraRuta.Conexos.Should().ContainSingle().Subject;
            (conexo.IdServicioConexoCatalogo, conexo.Categoria).Should().Be(("CX-CAT", "adicional"));

            leida.PrimeraRuta.ReemplazarCamiones([new clsCamionPlaneado("2", "FURGON", 9_000m), new clsCamionPlaneado("3S3", "FURGON", 20_000m)], null);
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        (await OrdenesAsync(cotizacion.Id)).Should().Be("1=2,2=3S3", "el recálculo borra los tres camiones y numera los dos nuevos desde 1");
        var releida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));
        releida!.PrimeraRuta!.VehiculosOrdenados.Select(v => v.TipoVehiculo).Should().Equal("2", "3S3");
    }

    /// <summary>
    /// Una ruta con <c>ordenVehiculo</c> en NULL (escrita sin el 03) se lee en el orden de <c>createdAt</c>, y un camión que se le agregue después queda también sin
    /// número y va al final por su <c>createdAt</c>.
    /// </summary>
    [Fact]
    public async Task UnaRutaSinOrdenVehiculoSeOrdenaPorCreatedAtYUnCamionNuevoVaAlFinal()
    {
        var cotizacion = CapturaDeTresCamiones();
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            "UPDATE v SET ordenVehiculo = NULL, createdAt = DATEADD(MILLISECOND, CASE v.tipoVehiculo WHEN N'2' THEN 0 WHEN N'3S3' THEN 1 ELSE 2 END, DATEADD(SECOND, -1, v.createdAt)) "
            + "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"WHERE l.cotizacionSolicitudId = '{cotizacion.Id}'");

        await using (var ambito = NuevoAmbito())
        {
            var leida = (await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
            leida.PrimeraRuta!.VehiculosOrdenados.Select(v => v.TipoVehiculo).Should().Equal(["2", "3S3", "2S3"], "sin ordenVehiculo manda createdAt");
            leida.PrimeraRuta.AgregarVehiculo("4X2", null, 500m).OrdenVehiculo.Should().BeNull("la ruta no tiene números: va por createdAt");
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        var releida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));
        releida!.PrimeraRuta!.VehiculosOrdenados.Select(v => v.TipoVehiculo).Should().Equal("2", "3S3", "2S3", "4X2");
    }

    /// <summary>
    /// Recálculos que reusan TODOS los números anteriores (3 → 3 en otro orden) y que los superan (3 → 5, y luego
    /// 5 → 1) en el mismo guardado: EF borra antes de insertar y el índice único filtrado no salta.
    /// </summary>
    [Fact]
    public async Task RecalculosQueReusanOSuperanLosNumerosNoChocanConElIndiceUnico()
    {
        var cotizacion = CapturaDeTresCamiones();
        await GuardarAsync(cotizacion);

        async Task RecalcularAsync(params clsCamionPlaneado[] camiones)
        {
            await using var ambito = NuevoAmbito();
            var leida = (await ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
            leida.PrimeraRuta!.ReemplazarCamiones(camiones, null);
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        await RecalcularAsync(new("2", "FURGON", 1_000m), new("2S3", "ESTACAS", 12_000m), new("3S3", "FURGON", 16_000m));
        (await OrdenesAsync(cotizacion.Id)).Should().Be("1=2,2=2S3,3=3S3");

        await RecalcularAsync(new("A1", null, 1m), new("A2", null, 1m), new("A3", null, 1m), new("A4", null, 1m), new("A5", null, 1m));
        (await OrdenesAsync(cotizacion.Id)).Should().Be("1=A1,2=A2,3=A3,4=A4,5=A5");

        await RecalcularAsync(new clsCamionPlaneado("B1", null, 1m));
        (await OrdenesAsync(cotizacion.Id)).Should().Be("1=B1");
    }

    /// <summary>
    /// Concurrencia: dos recálculos de la misma ruta leídos a la vez y guardados sin control de versión. El segundo
    /// falla (sus DELETE ya no encuentran filas o su INSERT choca con el índice único) y la base queda con UNA numeración 1..n, sin
    /// números repetidos ni camiones de las dos versiones mezclados.
    /// </summary>
    [Fact]
    public async Task DosRecalculosConcurrentesDeLaMismaRutaNoDejanNumerosRepetidos()
    {
        var cotizacion = CapturaDeTresCamiones();
        await GuardarAsync(cotizacion);

        await using var ambitoA = NuevoAmbito();
        await using var ambitoB = NuevoAmbito();
        var a = (await ambitoA.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
        var b = (await ambitoB.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct))!;
        a.PrimeraRuta!.ReemplazarCamiones([new clsCamionPlaneado("A1", null, 1m), new clsCamionPlaneado("A2", null, 1m)], null);
        b.PrimeraRuta!.ReemplazarCamiones([new clsCamionPlaneado("B1", null, 1m), new clsCamionPlaneado("B2", null, 1m), new clsCamionPlaneado("B3", null, 1m)], null);

        await ambitoA.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        await FluentActions.Awaiting(() => ambitoB.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct))
            .Should().ThrowAsync<Exception>("el segundo recálculo trabaja sobre camiones que ya no existen");

        (await OrdenesAsync(cotizacion.Id)).Should().Be("1=A1,2=A2", "queda solo el primer recálculo, numerado 1..n");
    }

    private static clsCotizacionSolicitud CapturaDeTresCamiones()
    {
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = FolioUnico() }).Valor;
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot },
        [
            new clsRutaCapturada(clsSolicitudCosteoMuestra.RutaCaptura(), 1, 0,
                [new clsCamionPlaneado("3S3", "FURGON", 16_000m), new clsCamionPlaneado("2S3", "ESTACAS", 12_000m), new clsCamionPlaneado("2", "FURGON", 1_000m)],
                [new clsCambioConexo("CX-CAT", "Cargue", 10_000m, 1m, "CX-CAT", "adicional")]),
        ]);
        return cotizacion;
    }

    /// <summary><c>ordenVehiculo=tipoVehiculo</c> de los camiones de la cotización, por <c>ordenVehiculo</c>.</summary>
    private Task<string?> OrdenesAsync(Guid cotizacionId) => clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
        "SELECT STRING_AGG(CONCAT(v.ordenVehiculo, '=', v.tipoVehiculo), ',') WITHIN GROUP (ORDER BY v.ordenVehiculo) "
        + "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
        + $"WHERE l.cotizacionSolicitudId = '{cotizacionId}'");

    /// <summary>Formulario de una ruta y un camión con folio único, sin guardar.</summary>
    private static clsCotizacionSolicitud Formulario(Guid? usuarioId) =>
        clsCotizacionMuestra.Formulario(folio: FolioUnico(), usuarioId: usuarioId, lineaNegocioId: 1);
}
