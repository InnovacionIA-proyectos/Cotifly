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
        leida.LineaNegocioId.Should().Be(1);
        leida.NitCliente.Should().Be("900123456");
        leida.OrigenCliente.Should().Be(OrigenCliente.Cliente);
        leida.Costo.Should().Be(1_275_000m);
        leida.MargenAplicado.Should().Be(0.15m);
        leida.OrigenMargen.Should().Be(OrigenMargen.PoliticaLinea);
        leida.PrecioFinal.Should().Be(1_500_000m);
        leida.Moneda.Should().Be("COP");
        leida.TrmAplicada.Should().Be(0m);
        leida.Version.Should().Be(1);
        leida.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        leida.CreatedAt.Should().Be(Reloj.AhoraUtc);
        leida.UpdatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task LosEnumsSeGuardanConElCodigoQueExigenLosCheckDelEsquema()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.RegistrarEnvio();
        await GuardarAsync(cotizacion);

        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(origenCliente, '|', origenMargen, '|', estadoEnvio) FROM dbo_cotifly.Cotizaciones WHERE id = '{cotizacion.Id}'");

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
        (await unidad.GuardarCambiosAsync(Ct)).Should().Be(1);
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

        // La regla vive en IClsSeguimientoService desde #11 (deuda de ADR-008); aquí se prueba con el repositorio real.
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
            .MarcarAsync(ana, cotizacion.Folio, false, "Precio alto", Ct));
        marcada.EsExitoso.Should().BeTrue();
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(cotizacion.Folio, Ct));

        leida!.Ganada.Should().BeFalse();
        leida.MotivoRechazo.Should().Be("Precio alto");
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
    public async Task LaSolicitudDeCosteoYSusPosicionesSeGuardanYSeReconstruyenSinPerderDatos()
    {
        var solicitud = clsSolicitudCosteoMuestra.Crear();
        var cotizacion = NuevaCotizacion(await CrearUsuarioAsync());
        cotizacion.RegistrarSolicitudCosteo(solicitud);

        await EnAmbitoAsync(s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            repo.Agregar(cotizacion);
            repo.AgregarPosiciones(clsMercancia.Desde(cotizacion.Id, solicitud.Posiciones));
            return Task.CompletedTask;
        });

        var (leida, posiciones) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            return (await repo.ObtenerPorFolioAsync(cotizacion.Folio, Ct), await repo.ListarPosicionesAsync(cotizacion.Id, Ct));
        });

        leida!.EstadoCosteo.Should().Be(EstadoCosteo.PendienteEnvio);
        posiciones.Select(p => p.Orden).Should().Equal(1, 2);
        leida.SolicitudCosteo(posiciones).Should().BeEquivalentTo(solicitud, "reenviar a Nexo no debe exigir volver a llenar el formulario");
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT CONCAT(tipoSolicitud, '|', estadoCosteo, '|', claseContenedor, '|', pesoKg) FROM dbo_cotifly.Cotizaciones WHERE id = '{cotizacion.Id}'");
        fila.Should().Be("spot|pendiente_envio|CNT40.HC|28500.50");
    }

    [Fact]
    public async Task UnaCotizacionSinFormularioAmpliadoNoTienePosicionesNiSolicitudDeCosteo()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);

        var (leida, posiciones) = await ConsultarAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsCotizacionRepository>();
            return (await repo.ObtenerPorFolioAsync(cotizacion.Folio, Ct), await repo.ListarPosicionesAsync(cotizacion.Id, Ct));
        });

        posiciones.Should().BeEmpty();
        leida!.EstadoCosteo.Should().BeNull("las cotizaciones del chat con costo manual no pasan por Nexo");
        leida.SolicitudCosteo(posiciones).Should().BeNull();
        leida.CosteoConsultasFallidas.Should().Be(0);
    }

    [Fact]
    public async Task LaAgendaDelPollingTraeSoloLasVencidasYElReclamoLaTomaUnaSolaVez()
    {
        var vencida = NuevaCotizacion(null);
        vencida.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
        var agenda = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        vencida.RegistrarEnvioCosteo($"SOL-{Guid.NewGuid():N}", false, agenda, agenda);
        var futura = NuevaCotizacion(null);
        futura.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
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

    [Fact]
    public async Task ElReclamoDeReenvioSoloGanaConElUpdatedAtLeidoYLaDejaPendienteDeEnvio()
    {
        var cotizacion = NuevaCotizacion(null);
        cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear());
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
}
