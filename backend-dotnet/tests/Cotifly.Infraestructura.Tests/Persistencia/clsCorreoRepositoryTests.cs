using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Consultas de <c>Correos</c>/<c>CorreoDestinatarios</c>. La regla de deduplicación vive en <c>clsCorreoService</c>
/// (ADR-008); aquí se prueban los datos que la alimentan (envíos exitosos desde un instante) y la consulta de
/// destinatarios previos por cliente.
/// </summary>
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

        await AgregarAsync(correo);
        var correos = await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct));

        correos.Should().ContainSingle();
        correos[0].Para.Should().Equal("cliente@empresa.com");
        correos[0].Cc.Should().Equal("jefe@empresa.com");
        correos[0].Estado.Should().Be(EstadoCorreo.Enviado);
        correos[0].CreatedAt.Should().Be(Reloj.AhoraUtc);
    }

    [Fact]
    public async Task LosEnviadosDesdeUnInstanteSoloIncluyenEnviosExitososDeEsaCotizacion()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);
        var otra = NuevaCotizacion(null);
        await GuardarAsync(otra);
        var desde = Reloj.AhoraUtc;

        await AgregarAsync(clsCorreo.Crear(cotizacion.Id, ["a@x.com", "b@x.com"], EstadoCorreo.Enviado).Valor);
        await AgregarAsync(clsCorreo.Crear(cotizacion.Id, ["c@x.com"], EstadoCorreo.Fallido, error: "relay caído").Valor);
        await AgregarAsync(clsCorreo.Crear(otra.Id, ["a@x.com"], EstadoCorreo.Enviado).Valor);

        var enviados = await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ListarEnviadosDesdeAsync(cotizacion.Id, desde, Ct));

        enviados.Should().ContainSingle();
        enviados[0].Para.Should().BeEquivalentTo(["a@x.com", "b@x.com"], "los destinatarios vienen cargados para comparar el conjunto");
    }

    [Fact]
    public async Task UnEnvioAnteriorAlInstanteNoSeLista()
    {
        var cotizacion = NuevaCotizacion(null);
        await GuardarAsync(cotizacion);
        await AgregarAsync(clsCorreo.Crear(cotizacion.Id, ["a@x.com"], EstadoCorreo.Enviado).Valor);
        Reloj.Avanzar(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var enviados = await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>()
            .ListarEnviadosDesdeAsync(cotizacion.Id, Reloj.AhoraUtc - TimeSpan.FromMinutes(5), Ct));

        enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task LosDestinatariosPreviosSonLosDelUltimoEnvioExitosoDelComercialAEseCliente()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var nit = $"NIT{Guid.NewGuid():N}"[..15];
        var primera = ConNit(ana, nit);
        var segunda = ConNit(ana, nit);
        var deLuis = ConNit(luis, nit);
        await GuardarAsync(primera);
        await GuardarAsync(segunda);
        await GuardarAsync(deLuis);

        await AgregarAsync(clsCorreo.Crear(primera.Id, ["viejo@cliente.com"], EstadoCorreo.Enviado).Valor);
        Reloj.Avanzar(TimeSpan.FromMinutes(1));
        await AgregarAsync(clsCorreo.Crear(segunda.Id, ["compras@cliente.com"], EstadoCorreo.Enviado, cc: ["gerente@cliente.com"]).Valor);
        Reloj.Avanzar(TimeSpan.FromMinutes(1));
        await AgregarAsync(clsCorreo.Crear(segunda.Id, ["fallido@cliente.com"], EstadoCorreo.Fallido, error: "x").Valor);
        await AgregarAsync(clsCorreo.Crear(deLuis.Id, ["deluis@cliente.com"], EstadoCorreo.Enviado).Valor);

        var deAna = await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ObtenerUltimoEnviadoAClienteAsync(ana, nit, Ct));
        var otroCliente = await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ObtenerUltimoEnviadoAClienteAsync(ana, "000", Ct));

        deAna!.Para.Should().Equal("compras@cliente.com");
        deAna.Cc.Should().Equal("gerente@cliente.com");
        otroCliente.Should().BeNull();
    }

    private static clsCotizacionSolicitud ConNit(Guid usuarioId, string nit) =>
        clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = FolioUnico(), UsuarioId = usuarioId, NitCliente = nit }).Valor;

    private Task AgregarAsync(clsCorreo correo) =>
        EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsCorreoRepository>().Agregar(correo);
            return Task.CompletedTask;
        });
}
