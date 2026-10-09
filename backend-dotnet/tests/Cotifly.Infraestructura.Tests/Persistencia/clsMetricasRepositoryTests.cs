using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Vistas de métricas (<c>vActividadPorComercial</c> corregida por el 0003, <c>vAjustesMargenPorComercial</c>) leídas con el
/// JOIN a <c>UsuariosWeb</c> y filtradas por comercial o regional.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMetricasRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task LaActividadCuentaEnviadasYReenviadasYTraeElNombreDelComercial()
    {
        var ana = await CrearUsuarioAsync();
        var enviada = NuevaCotizacion(ana);
        var reenviada = NuevaCotizacion(ana);
        await GuardarAsync(enviada);
        await GuardarAsync(reenviada);
        await GuardarAsync(NuevaCotizacion(ana));
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoEnvio = 'enviada' WHERE id = '{enviada.Id}';
            UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoEnvio = 'reenviada' WHERE id = '{reenviada.Id}';
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), '{enviada.Id}', '{enviada.Folio}', 0.15, 0.12, -0.03, N'Ajuste', '{ana}', SYSDATETIMEOFFSET());
            """);

        var filas = await Repositorio(r => r.ListarActividadAsync(clsAlcanceMetricas.DelUsuario(ana), Ct));

        filas.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            UsuarioId = ana,
            NombreCompleto = "Comercial de prueba",
            NumCotizaciones = 3,
            NumCotizacionesEnviadas = 2,
            NumAjustes = 1,
            UltimaCotizacion = Reloj.AhoraUtc,
        });
    }

    [Fact]
    public async Task PorRegionalSoloSalenLosComercialesDeEsaRegional()
    {
        var regional = $"R-{Guid.NewGuid():N}"[..12];
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var otro = await CrearUsuarioAsync();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.UsuariosWeb SET regional = '{regional}' WHERE id IN ('{ana}', '{luis}');");
        foreach (var usuario in new[] { ana, luis, otro })
        {
            await GuardarAsync(NuevaCotizacion(usuario));
        }

        var actividad = await Repositorio(r => r.ListarActividadAsync(clsAlcanceMetricas.DeLaRegional(regional), Ct));

        actividad.Select(f => f.UsuarioId).Should().BeEquivalentTo([ana, luis]);
    }

    [Fact]
    public async Task LosAjustesSeAgrupanPorComercialYLinea()
    {
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        // HU-021: la línea de negocio del ajuste es la de la ruta ajustada (MargenAjustes.lineaSolicitudId, 0009).
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            DECLARE @ruta UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = '{cotizacion.Id}' AND orden = 1);
            UPDATE dbo_cotifly.CotizacionLineasSolicitud
               SET lineaNegocioId = (SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Nacional') WHERE id = @ruta;
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, lineaSolicitudId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), '{cotizacion.Id}', @ruta, '{cotizacion.Folio}', 0.15, 0.12, -0.03, N'A', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), '{cotizacion.Id}', @ruta, '{cotizacion.Folio}', 0.15, 0.10, -0.05, N'B', '{ana}', SYSDATETIMEOFFSET());
            """);

        var filas = await Repositorio(r => r.ListarAjustesMargenAsync(clsAlcanceMetricas.DelUsuario(ana), Ct));

        var fila = filas.Should().ContainSingle().Subject;
        fila.LineaNegocio.Should().Be("Nacional");
        fila.NumAjustes.Should().Be(2);
        fila.DiferenciaPromedio.Should().Be(-0.04m);
        fila.DiferenciaMin.Should().Be(-0.05m);
        fila.DiferenciaMax.Should().Be(-0.03m);
        fila.NombreCompleto.Should().Be("Comercial de prueba");
    }

    [Fact]
    public async Task LosAjustesDeUnaCotizacionConRutasDeDosLineasVanALaLineaDeSuRutaYLaActividadLaCuentaUnaVez()
    {
        // HU-021.10 (#137): verificación de las vistas que redefinió el 0009 (no hace falta un 0011). La línea del ajuste es la de SU ruta
        // (MargenAjustes.lineaSolicitudId → CotizacionLineasSolicitud.lineaNegocioId), y vActividadPorComercial cuenta cotizaciones, no rutas.
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            DECLARE @id UNIQUEIDENTIFIER = '{cotizacion.Id}';
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET lineaNegocioId = (SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Nacional')
            WHERE cotizacionSolicitudId = @id AND orden = 1;
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, lineaNegocioId) VALUES
                (@id, 2, (SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Importacion')),
                (@id, 3, (SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Importacion'));
            DECLARE @r1 UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = @id AND orden = 1);
            DECLARE @r2 UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = @id AND orden = 2);
            DECLARE @r3 UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = @id AND orden = 3);
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, lineaSolicitudId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), @id, @r1, N'{cotizacion.Folio}', 0.15, 0.12, -0.03, N'A', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), @id, @r2, N'{cotizacion.Folio}', 0.15, 0.20, 0.05, N'B', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), @id, @r3, N'{cotizacion.Folio}', 0.15, 0.10, -0.05, N'C', '{ana}', SYSDATETIMEOFFSET());
            """);

        var ajustes = await Repositorio(r => r.ListarAjustesMargenAsync(clsAlcanceMetricas.DelUsuario(ana), Ct));
        var actividad = await Repositorio(r => r.ListarActividadAsync(clsAlcanceMetricas.DelUsuario(ana), Ct));

        ajustes.Select(f => (f.LineaNegocio, f.NumAjustes, f.DiferenciaPromedio, f.NumAjustesBajoSugerido)).Should().BeEquivalentTo(
            [("Importacion", 2, (decimal?)0m, 1), ("Nacional", 1, (decimal?)-0.03m, 1)]);
        actividad.Should().ContainSingle().Which.Should().BeEquivalentTo(new { NumCotizaciones = 1, NumAjustes = 3 }, "tres rutas, una cotización");
    }

    private Task<IReadOnlyList<T>> Repositorio<T>(Func<IClsMetricasRepository, Task<IReadOnlyList<T>>> consulta) =>
        ConsultarAsync(s => consulta(s.GetRequiredService<IClsMetricasRepository>()));
}
