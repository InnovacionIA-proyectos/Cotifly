using System.Text.Json;
using Cotifly.Aceptacion.Tests.Precio;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Auditoria;
using Microsoft.Extensions.DependencyInjection;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Auditoria;

/// <summary>Pasos de <c>specs/features/auditoria.feature</c>.</summary>
[Binding]
public sealed class clsPasosAuditoria(clsContextoEscenario contexto)
{
    [When("{string} negocia el margen {string} sobre la política {string} en el hilo {string}")]
    public async Task CuandoNegocia(string nombreUsuario, string aplicado, string politica, string hilo)
    {
        var entrada = new clsEntradaAjusteMargen(clsPasosPrecio.Fraccion(politica), clsPasosPrecio.Fraccion(aplicado), contexto.Usuarios[nombreUsuario], ThreadId: hilo);
        var resultado = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAjusteMargenService>().RegistrarAsync(entrada));
        resultado.EsExitoso.Should().BeTrue();
    }

    [Then("el hilo {string} tiene un evento {string} {word}")]
    public async Task EntoncesElHiloTieneUnEvento(string hilo, string herramienta, string resultado)
    {
        var evento = await EventoAsync(hilo, herramienta);
        evento.Exito.Should().Be(resultado == "exitoso");
        evento.DuracionMs.Should().BeGreaterThanOrEqualTo(0);
        JsonDocument.Parse(evento.Input!).RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Then("el evento {string} del hilo {string} tiene el folio {string} en su resultado")]
    public async Task EntoncesTieneElFolio(string herramienta, string hilo, string folio)
    {
        using var salida = JsonDocument.Parse((await EventoAsync(hilo, herramienta)).OutputResumen!);
        salida.RootElement.GetProperty("folio").GetString().Should().Be(folio);
    }

    [Then("el evento {string} del hilo {string} tiene el código {string} en su resultado")]
    public async Task EntoncesTieneElCodigo(string herramienta, string hilo, string codigo)
    {
        using var salida = JsonDocument.Parse((await EventoAsync(hilo, herramienta)).OutputResumen!);
        salida.RootElement.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
    }

    private async Task<clsEventoHerramienta> EventoAsync(string hilo, string herramienta)
    {
        var eventos = await contexto.ConsultarAsync(s => s.GetRequiredService<IClsAuditoriaRepository>().ListarPorThreadAsync(hilo));
        return eventos.Should().ContainSingle(e => e.Herramienta == herramienta).Subject;
    }
}
