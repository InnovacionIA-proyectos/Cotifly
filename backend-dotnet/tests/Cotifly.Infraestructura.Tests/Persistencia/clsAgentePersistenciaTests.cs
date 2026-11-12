using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Sesiones;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Persistencia del asistente contra SQL Server 2019: barrido de sesiones inactivas (port de
/// <c>test_archivar_inactivas_por_timeout</c>), sesiones legadas de Google Chat legibles como auditoría y candado por sesión.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsAgentePersistenciaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    // test_archivar_inactivas_por_timeout
    [Fact]
    public async Task ElBarridoArchivaLasSesionesInactivasDesdeHaceMasDelTtl()
    {
        var vieja = $"spaces/barrido-{Guid.NewGuid():N}";
        var fresca = $"spaces/barrido-{Guid.NewGuid():N}";
        await EnAmbitoAsync(s => s.GetRequiredService<IClsSesionRepository>().TocarAsync(vieja, vieja, Reloj.AhoraUtc - TimeSpan.FromHours(25), Ct));
        await EnAmbitoAsync(s => s.GetRequiredService<IClsSesionRepository>().TocarAsync(fresca, fresca, Reloj.AhoraUtc - TimeSpan.FromHours(1), Ct));
        await using var ambito = NuevoAmbito();
        var barrido = new clsBarridoSesionesHostedService(
            ambito.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new clsOpcionesSesiones { TtlHoras = 24 }),
            TimeProvider.System,
            NullLogger<clsBarridoSesionesHostedService>.Instance);

        var archivadas = await barrido.BarrerAsync(Ct);

        archivadas.Should().BeGreaterThanOrEqualTo(1);
        (await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(vieja, vieja, Ct)))!.Estado.Should().Be(EstadoSesion.Archivada);
        (await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(fresca, fresca, Ct)))!.Estado.Should().Be(EstadoSesion.Activa);
    }

    // Las sesiones de Google Chat y del chat sin folio quedan en la base como auditoría con el JSON del slot-filling
    // legado. Se siguen leyendo (los campos retirados se ignoran, el folio se conserva) y la lectura no reescribe la fila.
    [Fact]
    public async Task UnaSesionLegadaDeGoogleChatSeLeeSinPerderSuJsonDeAuditoria()
    {
        var space = $"spaces/legado-{Guid.NewGuid():N}"[..40];
        var hilo = $"{space}/threads/t1";
        const string JsonLegado =
            """{"nitCliente":"900123456","origenCliente":"prospecto","clienteConfirmado":true,"candidatosCliente":[{"nitCliente":"800","nombre":"Otro","origenCliente":"cliente"}],"costo":2850000,"fuenteCosto":"manual","origenMargen":"politica_linea","precioFinal":3352941,"candidatosOrigen":[{"id":2,"nombre":"La Unión","departamento":"Nariño"}],"folioEnProgreso":"PCN-NV-24092026-001","version":1,"moneda":"COP","confirmacionPendiente":true}""";
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.Sesiones (spaceId, threadId, estado, ultimaActividad, estadoAgente) VALUES ('{space}', '{hilo}', 'activa', SYSDATETIMEOFFSET(), N'{JsonLegado}')");

        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(space, hilo, Ct));
        await EnAmbitoAsync(async s => _ = await s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(space, hilo, Ct));

        leida!.EstadoAgente.Should().Be(clsEstadoConversacion.Vacio with { FolioEnProgreso = "PCN-NV-24092026-001", Version = 1, Moneda = "COP" });
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            $"SELECT estadoAgente FROM dbo_cotifly.Sesiones WHERE spaceId = '{space}' AND threadId = '{hilo}'")).Should().Be(JsonLegado);
    }

    // Un turno a la vez por sesión, entre conexiones (y réplicas), con sp_getapplock de sesión.
    [Fact]
    public async Task ElCandadoDeLaSesionNoSeTomaDosVecesYSeLiberaAlDesecharlo()
    {
        var candado = await ConsultarAsync(s => Task.FromResult(s.GetRequiredService<IClsCandadoSesion>()));
        var hilo = $"web/candado-{Guid.NewGuid():N}";

        var primero = await candado.IntentarTomarAsync(hilo, hilo, TimeSpan.Zero, Ct);
        var segundo = await candado.IntentarTomarAsync(hilo, hilo, TimeSpan.FromMilliseconds(300), Ct);
        var otraSesion = await candado.IntentarTomarAsync(hilo + "-2", hilo + "-2", TimeSpan.Zero, Ct);
        await primero!.DisposeAsync();
        var tercero = await candado.IntentarTomarAsync(hilo, hilo, TimeSpan.Zero, Ct);

        primero.Should().NotBeNull();
        segundo.Should().BeNull("otro turno de la misma sesión sigue en curso");
        otraSesion.Should().NotBeNull("otra sesión no se bloquea");
        tercero.Should().NotBeNull("al liberar, la sesión queda libre");
        await otraSesion!.DisposeAsync();
        await tercero!.DisposeAsync();
    }

    [Fact]
    public void UnRecursoLargoSeAcortaConSuHash() =>
        clsCandadoSesionSql.Recurso(new string('s', 128), new string('t', 128)).Length.Should().BeLessThanOrEqualTo(255);
}
