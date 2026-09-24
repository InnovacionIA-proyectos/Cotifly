using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Dominio.Correo;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCorreoRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task UnCorreoSeGuardaConSusDestinatariosYSeListaPorCotizacion()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);
        var correo = clsCorreo.Crear(cotizacion.Id, ["cliente@empresa.com"], EstadoCorreo.Enviado, "Cotización", ["jefe@empresa.com"]).Valor;

        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsCorreoRepository>().Agregar(correo);
            return Task.CompletedTask;
        });
        var correos = await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct));

        correos.Should().ContainSingle();
        correos[0].Para.Should().Equal("cliente@empresa.com");
        correos[0].Cc.Should().Equal("jefe@empresa.com");
        correos[0].Estado.Should().Be(EstadoCorreo.Enviado);
        correos[0].CreatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task UnEnvioALosMismosDestinatariosDentroDeCincoMinutosEsDuplicadoSinImportarMayusculasNiOrden()
    {
        var cotizacionId = await CrearCotizacionConCorreoAsync(EstadoCorreo.Enviado, "a@x.com", "b@x.com");
        Reloj.Avanzar(TimeSpan.FromMinutes(4));

        var duplicado = await ExisteDuplicadoAsync(cotizacionId, "B@X.com", " a@x.com ");

        duplicado.Should().BeTrue();
    }

    [Fact]
    public async Task PasadaLaVentanaDeCincoMinutosYaNoEsDuplicado()
    {
        var cotizacionId = await CrearCotizacionConCorreoAsync(EstadoCorreo.Enviado, "a@x.com");
        Reloj.Avanzar(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        (await ExisteDuplicadoAsync(cotizacionId, "a@x.com")).Should().BeFalse();
    }

    [Fact]
    public async Task OtroConjuntoDeDestinatariosNoEsDuplicado()
    {
        var cotizacionId = await CrearCotizacionConCorreoAsync(EstadoCorreo.Enviado, "a@x.com", "b@x.com");

        (await ExisteDuplicadoAsync(cotizacionId, "a@x.com")).Should().BeFalse();
        (await ExisteDuplicadoAsync(cotizacionId, "a@x.com", "b@x.com", "c@x.com")).Should().BeFalse();
    }

    [Fact]
    public async Task UnEnvioFallidoNoCuentaComoDuplicado()
    {
        var cotizacionId = await CrearCotizacionConCorreoAsync(EstadoCorreo.Fallido, "a@x.com");

        (await ExisteDuplicadoAsync(cotizacionId, "a@x.com")).Should().BeFalse();
    }

    [Fact]
    public async Task ElEnvioDeOtraCotizacionNoCuentaComoDuplicado()
    {
        await CrearCotizacionConCorreoAsync(EstadoCorreo.Enviado, "a@x.com");
        var otra = NuevaCotizacion(null);
        await GuardarAsync(otra);

        (await ExisteDuplicadoAsync(otra.Id, "a@x.com")).Should().BeFalse();
    }

    private async Task<Guid> CrearCotizacionConCorreoAsync(EstadoCorreo estado, params string[] para)
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);
        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsCorreoRepository>().Agregar(clsCorreo.Crear(cotizacion.Id, para, estado).Valor);
            return Task.CompletedTask;
        });
        return cotizacion.Id;
    }

    private Task<bool> ExisteDuplicadoAsync(Guid cotizacionId, params string[] para) =>
        ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ExisteDuplicadoRecienteAsync(cotizacionId, para, cancellationToken: Ct));
}
