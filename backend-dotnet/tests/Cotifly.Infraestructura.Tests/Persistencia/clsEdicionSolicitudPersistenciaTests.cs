using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-028 (#186, ADR-021) contra SQL Server 2019: la edición de la solicitud se guarda en UNA transacción con el control de versión sin
/// violar los índices únicos (<c>uq_cotizacionLineasSolicitud_cotizacionId_orden</c>, <c>uq_cotizacionMercancias_lineaSolicitudId_orden</c>,
/// <c>uq_cotizacionServiciosConexos_lineaSolicitudId_idServicioConexo</c>) ni las FK sin cascada de la base (camiones, mercancías, conexos y
/// <c>MargenAjustes</c> de la ruta quitada).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsEdicionSolicitudPersistenciaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private const int Linea = 1;

    [Fact]
    public async Task QuitarLaPrimeraRutaCorreLasDemasYDesvinculaSusAjustesDeMargen()
    {
        var (cotizacion, rutas) = await TresRutasAsync();

        var guardado = await EditarAsync(cotizacion.Id, (c, _) =>
        [
            new clsRutaEditada(rutas[1], Capturada(Ruta() with { Posiciones = [Ruta().Posiciones[0]] }, [new clsCambioConexo("CX-2", "Escolta", 42_000m, 3m), new clsCambioConexo("CX-9", "Precinto", 1m, 1m)]), Replanificada: true),
            new clsRutaEditada(rutas[2], Capturada(Ruta()), Replanificada: false),
            new clsRutaEditada(null, Capturada(Ruta() with { Destino = "Cali", DestinoMunicipioId = 76001 }), Replanificada: true),
        ]);

        guardado.Should().BeTrue();
        (await FilasAsync(cotizacion.Id)).Should().Equal(
            $"1|{rutas[1]}|1|1|2", $"2|{rutas[2]}|2|1|0", "3|nueva|2|1|0");
        (await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE cotizacionId = '{cotizacion.Id}' AND lineaSolicitudId IS NULL"))
            .Should().Be(1, "el ajuste de la ruta quitada queda en el historial sin ruta");
        (await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
                $"SELECT (SELECT COUNT(*) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud WHERE lineaSolicitudId = '{rutas[0]}') "
                + $"+ (SELECT COUNT(*) FROM dbo_cotifly.CotizacionMercancias WHERE lineaSolicitudId = '{rutas[0]}') "
                + $"+ (SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos WHERE lineaSolicitudId = '{rutas[0]}')"))
            .Should().Be(0, "la ruta quitada se va con sus camiones, mercancías y conexos");
    }

    [Fact]
    public async Task UnaRutaNuevaAdelanteCorreHaciaAtrasLasQueSeConservan()
    {
        var (cotizacion, rutas) = await TresRutasAsync();

        var guardado = await EditarAsync(cotizacion.Id, (c, _) =>
        [
            new clsRutaEditada(null, Capturada(Ruta() with { Destino = "Cali", DestinoMunicipioId = 76001 }), Replanificada: true),
            new clsRutaEditada(rutas[0], Capturada(Ruta()), Replanificada: false),
            new clsRutaEditada(rutas[1], Capturada(Ruta()), Replanificada: false),
            new clsRutaEditada(rutas[2], Capturada(Ruta()), Replanificada: false),
        ]);

        guardado.Should().BeTrue();
        (await FilasAsync(cotizacion.Id)).Select(f => f.Split('|')[1]).Should().Equal("nueva", rutas[0].ToString(), rutas[1].ToString(), rutas[2].ToString());
        (await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE cotizacionId = '{cotizacion.Id}' AND lineaSolicitudId = '{rutas[0]}'"))
            .Should().Be(1, "la ruta que se conserva mantiene su ajuste");
    }

    [Fact]
    public async Task QuitarUnaRutaDelMedioYAgregarOtraEnSuLugar()
    {
        var (cotizacion, rutas) = await TresRutasAsync();

        var guardado = await EditarAsync(cotizacion.Id, (c, _) =>
        [
            new clsRutaEditada(rutas[0], Capturada(Ruta()), Replanificada: false),
            new clsRutaEditada(null, Capturada(Ruta() with { Destino = "Cali", DestinoMunicipioId = 76001 }), Replanificada: true),
            new clsRutaEditada(rutas[2], Capturada(Ruta()), Replanificada: false),
        ]);

        guardado.Should().BeTrue();
        (await FilasAsync(cotizacion.Id)).Select(f => f.Split('|')[1]).Should().Equal(rutas[0].ToString(), "nueva", rutas[2].ToString());
    }

    // ------------------------------------------------------------------ Soporte

    private static clsLineaSolicitudCosteo Ruta() => clsSolicitudCosteoMuestra.RutaCaptura();

    private static clsRutaCapturada Capturada(clsLineaSolicitudCosteo linea, IReadOnlyList<clsCambioConexo>? conexos = null) =>
        new(linea, Linea, 0, [new clsCamionPlaneado(null, null, linea.PesoKg)], conexos ?? []);

    /// <summary>Formulario de tres rutas en Nexo (pendiente_costeo), con conexos y un ajuste de margen en la ruta 1.</summary>
    private async Task<(clsCotizacionSolicitud Cotizacion, IReadOnlyList<Guid> Rutas)> TresRutasAsync()
    {
        var usuario = await CrearUsuarioAsync();
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = FolioUnico(), UsuarioId = usuario, NitCliente = "900123456", ClienteNombre = "LONGYIN S.A.S.", OrigenCliente = OrigenCliente.Cliente,
        }).Valor;
        var conexos = new List<clsCambioConexo> { new("CX-1", "Cargue", 85_000m, 1m), new("CX-2", "Escolta", 40_000m, 1m) };
        cotizacion.RegistrarCaptura(new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot, Deadline = clsSolicitudCosteoMuestra.Deadline },
            [Capturada(Ruta(), conexos), Capturada(Ruta() with { Destino = "Tuluá", DestinoMunicipioId = 76834 }, conexos), Capturada(Ruta())]);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, Reloj.AhoraUtc, Reloj.AhoraUtc);
        await GuardarAsync(cotizacion);
        var rutas = cotizacion.RutasOrdenadas.Select(r => r.Id).ToList();
        _originales.UnionWith(rutas);
        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsMargenAjusteRepository>().Agregar(
                clsMargenAjuste.Registrar(0.15m, 0.20m, "Negociado", usuario, cotizacion.Id, cotizacion.Folio, lineaSolicitudId: rutas[0]).Valor);
            return Task.CompletedTask;
        });
        return (cotizacion, rutas);
    }

    /// <summary>Lo que hace el caso de uso al aplicar (en sitio o reemplazo): edita, desvincula los ajustes, sube la versión y guarda con ella.</summary>
    private Task<bool> EditarAsync(Guid id, Func<clsCotizacionSolicitud, int, IReadOnlyList<clsRutaEditada>> rutas) =>
        ConsultarAsync(async s =>
        {
            var propia = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(id, Ct);
            var version = propia!.VersionNegocio;
            var resultado = propia.AplicarEdicion(new clsEdicionSolicitud(
                new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Spot, Deadline = clsSolicitudCosteoMuestra.Deadline, Notas = "Editada" },
                rutas(propia, version)), Reloj.AhoraUtc);
            if (resultado.RutasQuitadas.Count > 0)
            {
                await s.GetRequiredService<IClsMargenAjusteRepository>().DesvincularRutasAsync([.. resultado.RutasQuitadas.Select(r => r.Id)], Ct);
            }

            propia.IncrementarVersionNegocio();
            return await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(id, version, Ct);
        });

    /// <summary>Cada ruta guardada: <c>orden|id (o "nueva")|mercancías|camiones|conexos</c>, por orden.</summary>
    private async Task<IReadOnlyList<string>> FilasAsync(Guid id)
    {
        var existentes = await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
            "SELECT STRING_AGG(CONCAT(t.orden, '|', t.id, '|', t.m, '|', t.v, '|', t.x), ';') WITHIN GROUP (ORDER BY t.orden) FROM (SELECT l.orden, l.id, "
            + "(SELECT COUNT(*) FROM dbo_cotifly.CotizacionMercancias m WHERE m.lineaSolicitudId = l.id) AS m, "
            + "(SELECT COUNT(*) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id) AS v, "
            + "(SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos x WHERE x.lineaSolicitudId = l.id) AS x "
            + $"FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = '{id}') t");
        return [.. existentes!.Split(';').Select(f =>
        {
            var partes = f.Split('|');
            var rutaId = Guid.Parse(partes[1]);
            partes[1] = _originales.Contains(rutaId) ? rutaId.ToString() : "nueva";

            return string.Join('|', partes);
        })];
    }

    private readonly HashSet<Guid> _originales = [];
}
