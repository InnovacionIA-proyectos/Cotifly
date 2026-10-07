using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Sesiones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSesionRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task ObtenerOCrearCreaUnaSesionActivaConPkCompuestaYLaReutiliza()
    {
        var (space, hilo) = IdsUnicos();

        await EnAmbitoAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerOCrearAsync(space, hilo, cancellationToken: Ct));
        Reloj.Avanzar(TimeSpan.FromMinutes(10));
        await EnAmbitoAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerOCrearAsync(space, hilo, cancellationToken: Ct));
        var sesion = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(space, hilo, Ct));

        sesion.Should().NotBeNull();
        sesion!.Estado.Should().Be(EstadoSesion.Activa);
        sesion.UltimaActividad.Should().Be(Reloj.AhoraUtc - TimeSpan.FromMinutes(10), "la segunda llamada reutiliza la sesión existente");
        (await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            $"SELECT COUNT(*) FROM dbo_cotifly.Sesiones WHERE spaceId = '{space}' AND threadId = '{hilo}'")).Should().Be(1);
    }

    [Fact]
    public async Task LaMismaSesionEnOtroHiloEsOtraFila()
    {
        var (space, hilo) = IdsUnicos();

        await EnAmbitoAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsSesionRepository>();
            await repo.ObtenerOCrearAsync(space, hilo, cancellationToken: Ct);
            await repo.ObtenerOCrearAsync(space, hilo + "-2", cancellationToken: Ct);
        });
        var sesiones = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ListarPorSpaceAsync(space, Ct));

        sesiones.Should().HaveCount(2);
    }

    [Fact]
    public async Task TocarActualizaLaActividadYArchivarLaSacaDeLasInactivas()
    {
        var (space, hilo) = IdsUnicos();
        var inicio = Reloj.AhoraUtc;
        await EnAmbitoAsync(s => s.GetRequiredService<IClsSesionRepository>().TocarAsync(space, hilo, cancellationToken: Ct));

        var inactivas = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ListarInactivasAsync(inicio.AddMinutes(1), Ct));
        inactivas.Should().Contain(x => x.SpaceId == space && x.ThreadId == hilo);

        await EnAmbitoAsync(s => s.GetRequiredService<IClsSesionRepository>().ArchivarAsync(space, hilo, Ct));
        var trasArchivar = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ListarInactivasAsync(inicio.AddMinutes(1), Ct));
        var archivada = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(space, hilo, Ct));

        trasArchivar.Should().NotContain(x => x.SpaceId == space && x.ThreadId == hilo);
        archivada!.Estado.Should().Be(EstadoSesion.Archivada);
    }

    [Fact]
    public async Task ArchivarUnaSesionInexistenteDevuelveFalse()
    {
        var (space, hilo) = IdsUnicos();

        var archivada = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ArchivarAsync(space, hilo, Ct));

        archivada.Should().BeFalse();
    }

    [Fact]
    public async Task ElEstadoDelAgenteSeGuardaComoJsonValidoYSeRecuperaIgual()
    {
        var (space, hilo) = IdsUnicos();
        var usuario = await CrearUsuarioAsync();
        var estado = clsEstadoConversacion.Vacio with
        {
            FolioEnProgreso = "PCN-NV-24092026-001",
            CotizacionId = Guid.NewGuid(),
            Version = 2,
            Moneda = "USD",
        };

        await EnAmbitoAsync(async s =>
        {
            var sesion = await s.GetRequiredService<IClsSesionRepository>().ObtenerOCrearAsync(space, hilo, usuario, Ct);
            sesion.ActualizarEstadoAgente(estado);
        });
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(space, hilo, Ct));
        var json = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT estadoAgente FROM dbo_cotifly.Sesiones WHERE spaceId = '{space}' AND threadId = '{hilo}' AND ISJSON(estadoAgente) = 1");

        leida!.EstadoAgente.Should().Be(estado);
        leida.UsuarioId.Should().Be(usuario);
        json.Should().Contain("\"folioEnProgreso\":\"PCN-NV-24092026-001\"").And.Contain("\"moneda\":\"USD\"");
    }

    [Fact]
    public async Task CambiarElEstadoDelAgenteDeUnaSesionExistenteSePersiste()
    {
        var (space, hilo) = IdsUnicos();
        await EnAmbitoAsync(async s =>
            (await s.GetRequiredService<IClsSesionRepository>().ObtenerOCrearAsync(space, hilo, cancellationToken: Ct))
                .ActualizarEstadoAgente(clsEstadoConversacion.Vacio));

        await EnAmbitoAsync(async s =>
        {
            var sesion = await s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(space, hilo, Ct);
            sesion!.ActualizarEstadoAgente(sesion.EstadoAgente! with { FolioEnProgreso = "PCN-DG-24092026-001" });
        });
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(space, hilo, Ct));

        leida!.EstadoAgente!.FolioEnProgreso.Should().Be("PCN-DG-24092026-001");
    }

    [Fact]
    public async Task ElHistorialDevuelveLosTurnosEnOrdenCronologicoPorFkCompuesta()
    {
        var (space, hilo) = IdsUnicos();
        var t0 = Reloj.AhoraUtc;
        await EnAmbitoAsync(async s =>
        {
            var repo = s.GetRequiredService<IClsSesionRepository>();
            await repo.ObtenerOCrearAsync(space, hilo, cancellationToken: Ct);
            repo.AgregarEvento(clsEventoConversacion.Crear(space, hilo, "assistant", "segundo", cuando: t0.AddSeconds(2),
                herramientasJson: """{"llamadas":[{"nombre":"obtener_margen"}]}""").Valor);
            repo.AgregarEvento(clsEventoConversacion.Crear(space, hilo, "user", "primero", cuando: t0.AddSeconds(1)).Valor);
        });

        var historial = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().HistorialAsync(space, hilo, Ct));

        historial.Select(e => e.Contenido).Should().Equal("primero", "segundo");
        historial[1].Herramientas.Should().Contain("obtener_margen");
    }

    [Fact]
    public async Task UnEventoSinSesionExistenteViolaLaFkCompuesta()
    {
        var (space, hilo) = IdsUnicos();

        var guardar = () => EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsSesionRepository>().AgregarEvento(clsEventoConversacion.Crear(space, hilo, "user", "hola").Valor);
            return Task.CompletedTask;
        });

        (await guardar.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateException>())
            .Which.InnerException!.Message.Should().Contain("fk_eventosConversacion_sesion");
    }

    private static (string Space, string Hilo) IdsUnicos()
    {
        var id = clsSesion.IdWeb(Guid.NewGuid());
        return (id, id);
    }
}
