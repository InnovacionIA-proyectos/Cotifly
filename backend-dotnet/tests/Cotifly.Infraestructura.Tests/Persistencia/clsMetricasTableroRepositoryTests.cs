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
/// Filas del tablero de HU-010.1 (ADR-016 §2) sobre SQL Server real: una por cadena <c>(usuarioId, folio base)</c> con los valores
/// de la versión vigente, el periodo por la primera creación de la cadena, el resultado más reciente de la cadena, los campos de
/// moneda, el resumen de <c>MargenAjustes</c> y las columnas opcionales del script 0004 solo si el esquema las tiene.
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
    public async Task LaCadenaCuentaEnElMesDeSuPrimeraCreacionConLosValoresDeLaVersionVigente()
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

        enero.Filas.Should().HaveCount(2, "rehacer una cotización no la mueve del mes en que se creó");
        var cadena = enero.Filas.Single(f => f.Versiones == 2);
        cadena.Should().BeEquivalentTo(new { PrecioFinal = 2_000_000m, Costo = 1_500_000m, CreatedAt = new DateTimeOffset(2026, 1, 10, 12, 0, 0, _bogota) });
        cadena.CreatedAtVigente.Should().Be(new DateTimeOffset(2026, 2, 10, 12, 0, 0, _bogota));
        febrero.Filas.Should().BeEmpty("la v2 no abre una cotización nueva en febrero");
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

        datos.Filas.Select(f => (f.UsuarioId, f.PrecioFinal, f.Versiones)).Should().BeEquivalentTo(
            [(ana, (decimal?)3m, 3), (luis, (decimal?)9m, 1)], "la v2 suelta de luis es la única de su cadena");
    }

    [Theory]
    [InlineData("1", "NULL", null, null, true)]
    [InlineData("1", "0", "2026-03-01T10:00:00-05:00", "2026-03-05T10:00:00-05:00", false)]
    [InlineData("0", "1", "2026-03-09T10:00:00-05:00", "2026-03-05T10:00:00-05:00", false)]
    [InlineData("0", "1", null, null, true)]
    [InlineData("NULL", "NULL", null, null, null)]
    public async Task ElResultadoEsElMasRecienteDeLaCadena(string ganadaV1, string ganadaV2, string? marcadoV1, string? marcadoV2, bool? esperado)
    {
        var ana = await CrearUsuarioAsync();
        var folio = FolioUnico();
        var v1 = NuevaCotizacion(ana, folio);
        await GuardarAsync(v1);
        static string Fecha(string? valor) => valor is null ? "NULL" : $"'{valor}'";
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.Cotizaciones SET ganada = {ganadaV1}, marcadoAt = {Fecha(marcadoV1)} WHERE id = '{v1.Id}';
            INSERT INTO dbo_cotifly.Cotizaciones (id, folio, usuarioId, precioFinal, version, ganada, marcadoAt, createdAt, updatedAt)
            VALUES (NEWID(), N'{folio}-v2', '{ana}', 2000000, 2, {ganadaV2}, {Fecha(marcadoV2)}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """);

        var fila = (await ListarAsync([ana], Reloj.AhoraUtc.AddYears(-1), Reloj.AhoraUtc.AddYears(1))).Filas.Should().ContainSingle().Subject;

        fila.Ganada.Should().Be(esperado);
        fila.PrecioFinal.Should().Be(2_000_000m, "los valores son de la versión vigente");
    }

    [Fact]
    public async Task TraeMonedaTrmYElResumenDeLosAjustesDeLaVersionVigente()
    {
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.Cotizaciones SET moneda = N'USD', trmAplicada = 4000.5, precioFinal = 1000, ganada = 1,
                costeoListoAt = DATEADD(HOUR, 2, createdAt) WHERE id = '{cotizacion.Id}';
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), '{cotizacion.Id}', '{cotizacion.Folio}', 0.15, 0.12, -0.03, N'A', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), '{cotizacion.Id}', '{cotizacion.Folio}', 0.15, 0.20, 0.05, N'B', '{ana}', SYSDATETIMEOFFSET()),
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
            Versiones = 1,
            NumAjustes = 3,
            NumDiferencias = 2,
            SumaDiferencias = (decimal?)0.02m,
            SumaDiferenciasAbsolutas = (decimal?)0.08m,
        });
        fila.ValorCop.Should().Be(4_000_500m);
        (fila.CosteoListoAt - fila.CreatedAtVigente).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public async Task SinLasColumnasDelScript0004NoHayCiudadesNiDeadline()
    {
        var ana = await CrearUsuarioAsync();
        await GuardarAsync(NuevaCotizacion(ana));

        var datos = await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1));
        var sinUsuarios = await ListarAsync([], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1));

        datos.CiudadesDisponibles.Should().BeFalse();
        datos.DeadlineDisponible.Should().BeFalse();
        datos.Filas.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Destino = (string?)null, Deadline = (DateTimeOffset?)null });
        sinUsuarios.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task ConLasColumnasDelScript0004TraeOrigenDestinoYDeadline()
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
            $"UPDATE dbo_cotifly.Cotizaciones SET origen = N'MEDELLIN', destino = N'BOGOTA D.C.', deadline = '2026-09-20T18:00:00-05:00' WHERE id = '{cotizacion.Id}';");

        await using var consulta = proveedor.CreateAsyncScope();
        var datos = await consulta.ServiceProvider.GetRequiredService<IClsMetricasRepository>()
            .ListarCotizacionesAsync([usuario.Id], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1), Ct);

        datos.CiudadesDisponibles.Should().BeTrue();
        datos.DeadlineDisponible.Should().BeTrue();
        datos.Filas.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Origen = "MEDELLIN",
            Destino = "BOGOTA D.C.",
            Deadline = (DateTimeOffset?)new DateTimeOffset(2026, 9, 20, 18, 0, 0, _bogota),
        });
    }

    [Fact]
    public async Task CuentaLasLineasDeNegocioActivas()
    {
        var esperadas = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.MargenesPolitica WHERE isActive = 1");

        var activas = await ConsultarAsync(s => s.GetRequiredService<IClsMetricasRepository>().ContarLineasNegocioActivasAsync(Ct));

        activas.Should().Be(esperadas).And.BeGreaterThan(0);
    }

    [Fact]
    public async Task ElRolDeCadaUsuarioSaleDeUsuariosRoles()
    {
        var ana = await CrearUsuarioAsync();
        var sinRol = await CrearUsuarioAsync();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId, activo)
            SELECT '{ana}', id, 1 FROM dbo_cotifly.Roles WHERE codigo = 'gerente_comercial';
            """);

        var roles = await ConsultarAsync(s => s.GetRequiredService<IClsRolRepository>().ListarRolesDeUsuariosAsync([ana, sinRol], Ct));

        roles.Should().Equal(new Dictionary<Guid, RolInterno> { [ana] = RolInterno.GerenteComercial });
    }

    private Task<clsDatosMetricas> ListarAsync(IReadOnlyCollection<Guid> usuarios, DateTimeOffset desde, DateTimeOffset hasta) =>
        ConsultarAsync(s => s.GetRequiredService<IClsMetricasRepository>().ListarCotizacionesAsync(usuarios, desde, hasta, Ct));
}
