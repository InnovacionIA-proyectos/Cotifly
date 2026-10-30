using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-016.1 (#154): <see cref="IClsCotizacionRepository.ListarPorUsuariosAsync"/> contra SQL Server 2019 real: solo las cotizaciones de
/// los usuarios pedidos (<c>WHERE usuarioId IN (...)</c>), con la misma proyección, el mismo orden (<c>createdAt DESC, folio DESC</c>) y el
/// mismo tope que "Mis cotizaciones", aplicado al conjunto.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsListadoRegionalIntegracionTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task TraeSoloLasDeLosUsuariosPedidosDeLaMasRecienteALaMasAntiguaConSuLinea()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var marta = await CrearUsuarioAsync();
        var deAna = NuevaCotizacion(ana);
        var deMarta = NuevaCotizacion(marta);
        var deLuis = NuevaCotizacion(luis);
        var sinLinea = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = FolioUnico(), UsuarioId = ana }).Valor;
        foreach (var cotizacion in new[] { deAna, deMarta, deLuis, sinLinea })
        {
            await GuardarAsync(cotizacion);
            Reloj.Avanzar(TimeSpan.FromSeconds(1));
        }

        var filas = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarPorUsuariosAsync([ana, luis], 100, Ct));

        filas.Select(f => (f.Cotizacion.Folio, f.Cotizacion.UsuarioId, f.LineaNegocio)).Should().Equal(
            (sinLinea.Folio, ana, (string?)null),
            (deLuis.Folio, luis, "Nacional"),
            (deAna.Folio, ana, "Nacional"));
    }

    [Fact]
    public async Task ElTopeEsDelConjuntoYAIgualFechaDesempataPorFolioDescendente()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var raiz = $"PCN-T-{Guid.NewGuid():N}";
        await GuardarAsync(NuevaCotizacion(ana, $"{raiz}-A"));
        Reloj.Avanzar(TimeSpan.FromSeconds(1));
        // Misma createdAt: el folio decide.
        await GuardarAsync(NuevaCotizacion(luis, $"{raiz}-B"));
        await GuardarAsync(NuevaCotizacion(ana, $"{raiz}-C"));

        var dos = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarPorUsuariosAsync([ana, luis, ana], 2, Ct));

        dos.Select(f => f.Cotizacion.Folio).Should().Equal($"{raiz}-C", $"{raiz}-B");
    }

    [Fact]
    public async Task ConMasDeDosMilUsuariosNoChocaConElTopeDeParametrosDeSqlServer()
    {
        // HU-037.3 (#211): el director sin regional pide las de todos los usuarios; la lista viaja como un solo parámetro.
        var ana = await CrearUsuarioAsync();
        var deAna = NuevaCotizacion(ana);
        await GuardarAsync(deAna);
        List<Guid> usuarios = [.. Enumerable.Range(0, 2500).Select(_ => Guid.NewGuid()), ana];

        var filas = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarPorUsuariosAsync(usuarios, 100, Ct));

        filas.Should().ContainSingle().Which.Cotizacion.Folio.Should().Be(deAna.Folio);
    }

    [Fact]
    public async Task SinUsuariosDevuelveUnaListaVacia()
    {
        await GuardarAsync(NuevaCotizacion(await CrearUsuarioAsync()));

        var filas = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ListarPorUsuariosAsync([], 100, Ct));

        filas.Should().BeEmpty();
    }
}
