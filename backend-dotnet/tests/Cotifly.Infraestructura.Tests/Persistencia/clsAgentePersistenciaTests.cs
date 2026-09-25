using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Sesiones;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Persistencia del agente (#12 parte C) contra SQL Server 2019: identidad de Google Chat por <c>UsuariosWeb.email</c> (ADR-011),
/// barrido de sesiones inactivas (port de <c>test_archivar_inactivas_por_timeout</c>) y el estado nuevo en <c>estadoAgente</c>.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsAgentePersistenciaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task ElComercialDeGoogleChatSeEncuentraPorSuCorreoSinDistinguirMayusculas()
    {
        var correo = $"c{Guid.NewGuid():N}@boterosoto.com.co"[..40];
        var usuario = clsUsuarioWeb.Crear($"u{Guid.NewGuid():N}", "Comercial Chat", correo, null, Reloj.AhoraUtc).Valor;
        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsUsuarioWebRepository>().Agregar(usuario);
            return Task.CompletedTask;
        });

        var encontrado = await ConsultarAsync(s => s.GetRequiredService<IClsUsuarioWebRepository>().ObtenerPorEmailAsync(correo.ToUpperInvariant(), Ct));
        var inexistente = await ConsultarAsync(s => s.GetRequiredService<IClsUsuarioWebRepository>().ObtenerPorEmailAsync("nadie@gmail.com", Ct));

        encontrado!.Id.Should().Be(usuario.Id);
        inexistente.Should().BeNull();
    }

    [Fact]
    public async Task UnCorreoRepetidoNoIdentificaANadie()
    {
        var correo = $"r{Guid.NewGuid():N}@boterosoto.com.co"[..40];
        await EnAmbitoAsync(s =>
        {
            var repositorio = s.GetRequiredService<IClsUsuarioWebRepository>();
            repositorio.Agregar(clsUsuarioWeb.Crear($"u{Guid.NewGuid():N}", "Uno", correo, null, Reloj.AhoraUtc).Valor);
            repositorio.Agregar(clsUsuarioWeb.Crear($"u{Guid.NewGuid():N}", "Dos", correo, null, Reloj.AhoraUtc).Valor);
            return Task.CompletedTask;
        });

        (await ConsultarAsync(s => s.GetRequiredService<IClsUsuarioWebRepository>().ObtenerPorEmailAsync(correo, Ct))).Should().BeNull();
    }

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

    [Fact]
    public async Task ElEstadoConLosCamposNuevosSeGuardaYSeLeeIgual()
    {
        var hilo = $"web/estado-{Guid.NewGuid():N}";
        var estado = clsEstadoConversacion.Vacio with
        {
            MargenSugerido = 0.12m,
            FuenteCosto = clsEstadoConversacion.FuenteCostoManual,
            CandidatosCliente = [new clsCandidatoCliente("900111222", "ACME", Dominio.Clientes.OrigenCliente.Cliente, "700")],
        };
        await EnAmbitoAsync(async s => (await s.GetRequiredService<IClsSesionRepository>().ObtenerOCrearAsync(hilo, hilo, cancellationToken: Ct)).ActualizarEstadoAgente(estado));

        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsSesionRepository>().ObtenerAsync(hilo, hilo, Ct));

        leida!.EstadoAgente.Should().Be(estado);
        leida.EstadoAgente!.CandidatosCliente[0].Bp.Should().Be("700");
    }
}
