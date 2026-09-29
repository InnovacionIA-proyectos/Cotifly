using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Dominio.Identidad;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Filas del tablero de HU-010.1 (ADR-016 §2) sobre SQL Server real: versión vigente por <c>(usuarioId, folio base)</c> decidida
/// sobre toda la historia, periodo por <c>createdAt</c>, campos de moneda, resumen de <c>MargenAjustes</c> y columnas de ciudad
/// solo si el esquema las tiene (script 0004).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMetricasTableroRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static readonly TimeSpan _bogota = TimeSpan.FromHours(-5);
    private static readonly DateTimeOffset _enero = new(2026, 1, 1, 0, 0, 0, _bogota);
    private static readonly DateTimeOffset _febrero = new(2026, 2, 1, 0, 0, 0, _bogota);
    private static readonly DateTimeOffset _marzo = new(2026, 3, 1, 0, 0, 0, _bogota);

    [Fact]
    public async Task LaVersionVigenteSeDecideSobreTodaLaHistoriaYElPeriodoFiltraPorFechaDeCreacion()
    {
        var ana = await CrearUsuarioAsync();
        var folio = FolioUnico();
        var v1 = NuevaCotizacion(ana, folio);
        var suelta = NuevaCotizacion(ana);
        await GuardarAsync(v1);
        await GuardarAsync(suelta);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.Cotizaciones SET createdAt = '2026-01-10T12:00:00-05:00' WHERE id IN ('{v1.Id}', '{suelta.Id}');
            INSERT INTO dbo_cotifly.Cotizaciones (id, folio, usuarioId, precioFinal, costo, version, createdAt, updatedAt)
            VALUES (NEWID(), N'{folio}-v2', '{ana}', 2000000, 1500000, 2, '2026-02-10T12:00:00-05:00', SYSDATETIMEOFFSET());
            """);

        var enero = await ListarAsync([ana], _enero, _febrero);
        var febrero = await ListarAsync([ana], _febrero, _marzo);

        enero.Filas.Should().HaveCount(2);
        enero.Filas.Count(f => f.EsVigente).Should().Be(1, "la v1 ya no es la vigente aunque su v2 esté fuera del periodo");
        febrero.Filas.Should().ContainSingle().Which.Should().BeEquivalentTo(new { EsVigente = true, PrecioFinal = 2_000_000m, Costo = 1_500_000m });
    }

    [Fact]
    public async Task LasCadenasSonPorComercialYLaVigenteEsLaDeMayorVersion()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var folio = FolioUnico();
        await GuardarAsync(NuevaCotizacion(ana, folio));
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.Cotizaciones (id, folio, usuarioId, precioFinal, version, createdAt, updatedAt) VALUES
                (NEWID(), N'{folio}-v3', '{ana}', 3, 3, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
                (NEWID(), N'{folio}-v2', '{ana}', 2, 2, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
                (NEWID(), N'{FolioUnico()}-v2', '{luis}', 9, 2, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """);

        var datos = await ListarAsync([ana, luis], Reloj.AhoraUtc.AddYears(-1), Reloj.AhoraUtc.AddYears(1));

        datos.Filas.Where(f => f.EsVigente).Select(f => (f.UsuarioId, f.PrecioFinal)).Should().BeEquivalentTo(
            [(ana, (decimal?)3m), (luis, (decimal?)9m)], "la v2 suelta de luis es la única de su cadena");
        datos.Filas.Should().HaveCount(4);
    }

    [Fact]
    public async Task TraeMonedaTrmResultadoYElResumenDeLosAjustes()
    {
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.Cotizaciones SET moneda = N'USD', trmAplicada = 4000.5, precioFinal = 1000, ganada = 1,
                costeoListoAt = DATEADD(HOUR, 2, createdAt) WHERE id = '{cotizacion.Id}';
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), '{cotizacion.Id}', '{cotizacion.Folio}', 0.15, 0.12, -0.03, N'A', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), '{cotizacion.Id}', '{cotizacion.Folio}', 0.15, 0.10, -0.05, N'B', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), '{cotizacion.Id}', '{cotizacion.Folio}', 0.15, NULL, NULL, N'C', '{ana}', SYSDATETIMEOFFSET());
            """);

        var fila = (await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1))).Filas.Should().ContainSingle().Subject;

        fila.Should().BeEquivalentTo(new
        {
            UsuarioId = ana,
            Moneda = "USD",
            TrmAplicada = 4000.5m,
            PrecioFinal = 1000m,
            Costo = 1_275_000m,
            MargenAplicado = 0.15m,
            Ganada = (bool?)true,
            NumAjustes = 3,
            NumDiferencias = 2,
            SumaDiferencias = (decimal?)-0.08m,
        });
        fila.PrecioCop.Should().Be(4_000_500m);
        (fila.CosteoListoAt - fila.CreatedAt).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task SinLasColumnasDelScript0004LasCiudadesNoEstanDisponibles()
    {
        var ana = await CrearUsuarioAsync();
        await GuardarAsync(NuevaCotizacion(ana));

        var datos = await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1));
        var sinUsuarios = await ListarAsync([], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1));

        datos.CiudadesDisponibles.Should().BeFalse();
        datos.Filas.Should().ContainSingle().Which.Destino.Should().BeNull();
        sinUsuarios.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task ConLasColumnasDelScript0004TraeOrigenYDestino()
    {
        var cadena = await BaseDatos.CadenaConCiudadesAsync();
        await using var proveedor = clsServiciosPruebas.Crear(cadena, Reloj);
        var usuario = clsUsuarioWeb.Crear($"u{Guid.NewGuid():N}", "Ana", null, "Antioquia", Reloj.AhoraUtc).Valor;
        var cotizacion = NuevaCotizacion(usuario.Id);
        await using (var ambito = proveedor.CreateAsyncScope())
        {
            ambito.ServiceProvider.GetRequiredService<IClsUsuarioWebRepository>().Agregar(usuario);
            ambito.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().Agregar(cotizacion);
            await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
        }

        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"UPDATE dbo_cotifly.Cotizaciones SET origen = N'MEDELLIN', destino = N'BOGOTA D.C.' WHERE id = '{cotizacion.Id}';");

        await using var consulta = proveedor.CreateAsyncScope();
        var datos = await consulta.ServiceProvider.GetRequiredService<IClsMetricasRepository>()
            .ListarCotizacionesAsync([usuario.Id], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1), Ct);

        datos.CiudadesDisponibles.Should().BeTrue();
        datos.Filas.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Origen = "MEDELLIN", Destino = "BOGOTA D.C." });
    }

    [Fact]
    public async Task CuentaLasLineasDeNegocioActivas()
    {
        var esperadas = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.MargenesPolitica WHERE isActive = 1");

        var activas = await ConsultarAsync(s => s.GetRequiredService<IClsMetricasRepository>().ContarLineasNegocioActivasAsync(Ct));

        activas.Should().Be(esperadas).And.BeGreaterThan(0);
    }

    private Task<clsDatosMetricas> ListarAsync(IReadOnlyCollection<Guid> usuarios, DateTimeOffset desde, DateTimeOffset hasta) =>
        ConsultarAsync(s => s.GetRequiredService<IClsMetricasRepository>().ListarCotizacionesAsync(usuarios, desde, hasta, Ct));
}
