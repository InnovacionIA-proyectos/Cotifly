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
/// Filas del tablero (ADR-016 §2) sobre SQL Server real: una por cadena <c>(usuarioId, folio base)</c> con los valores
/// de la versión vigente, el periodo por la primera creación de la cadena, el resultado más reciente de la cadena, los campos de
/// moneda, el resumen de <c>MargenAjustes</c> y las ciudades de la ruta 1 solo si el esquema las tiene (0010). Los
/// conteos son por cotización; el costo es la suma de sus rutas y el margen, el promedio de sus rutas ponderado por costo.
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
            UPDATE dbo_cotifly.CotizacionesSolicitud SET createdAt = '2026-01-10T12:00:00-05:00' WHERE id IN ('{v1.Id}', '{suelta.Id}');
            DECLARE @v2 UNIQUEIDENTIFIER = NEWID();
            INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio, usuarioId, precioFinal, version, createdAt, updatedAt)
            VALUES (@v2, N'{folio}-v2', '{ana}', 2000000, 2, '2026-02-10T12:00:00-05:00', SYSDATETIMEOFFSET());
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, costoFinal, precioFinalLinea) VALUES (@v2, 1, 1500000, 2000000);
            """);

        var enero = await ListarAsync([ana], _enero, _febrero);
        var febrero = await ListarAsync([ana], _febrero, _marzo);

        enero.Filas.Should().HaveCount(2, "rehacer una cotizaciÃ³n no la mueve del mes en que se creÃ³");
        var cadena = enero.Filas.Single(f => f.Versiones == 2);
        cadena.Should().BeEquivalentTo(new { PrecioFinal = 2_000_000m, Costo = 1_500_000m, CreatedAt = new DateTimeOffset(2026, 1, 10, 12, 0, 0, _bogota) });
        cadena.CreatedAtVigente.Should().Be(new DateTimeOffset(2026, 2, 10, 12, 0, 0, _bogota));
        febrero.Filas.Should().BeEmpty("la v2 no abre una cotizaciÃ³n nueva en febrero");
    }

    [Fact]
    public async Task LasCadenasSonPorComercialYLaVigenteEsLaDeMayorVersion()
    {
        var ana = await CrearUsuarioAsync();
        var luis = await CrearUsuarioAsync();
        var folio = FolioUnico();
        await GuardarAsync(NuevaCotizacion(ana, folio));
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio, usuarioId, precioFinal, version, createdAt, updatedAt) VALUES
                (NEWID(), N'{folio}-v3', '{ana}', 3, 3, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
                (NEWID(), N'{folio}-v2', '{ana}', 2, 2, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()),
                (NEWID(), N'{FolioUnico()}-v2', '{luis}', 9, 2, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """);

        var datos = await ListarAsync([ana, luis], Reloj.AhoraUtc.AddYears(-1), Reloj.AhoraUtc.AddYears(1));

        datos.Filas.Select(f => (f.UsuarioId, f.PrecioFinal, f.Versiones)).Should().BeEquivalentTo(
            [(ana, (decimal?)3m, 3), (luis, (decimal?)9m, 1)], "la v2 suelta de luis es la Ãºnica de su cadena");
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
            UPDATE dbo_cotifly.CotizacionesSolicitud SET ganada = {ganadaV1}, marcadoAt = {Fecha(marcadoV1)} WHERE id = '{v1.Id}';
            INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio, usuarioId, precioFinal, version, ganada, marcadoAt, createdAt, updatedAt)
            VALUES (NEWID(), N'{folio}-v2', '{ana}', 2000000, 2, {ganadaV2}, {Fecha(marcadoV2)}, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            """);

        var fila = (await ListarAsync([ana], Reloj.AhoraUtc.AddYears(-1), Reloj.AhoraUtc.AddYears(1))).Filas.Should().ContainSingle().Subject;

        fila.Ganada.Should().Be(esperado);
        fila.PrecioFinal.Should().Be(2_000_000m, "los valores son de la versiÃ³n vigente");
    }

    [Fact]
    public async Task TraeMonedaTrmYElResumenDeLosAjustesDeLaVersionVigente()
    {
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.CotizacionesSolicitud SET moneda = N'USD', trmAplicada = 4000.5, precioFinal = 1000, ganada = 1,
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
    public async Task SinLosNombresDeMunicipioDel0010NoHayCiudades()
    {
        // Las ciudades son los nombres de la ruta 1 (0010). La base compartida ya tiene el 0010 (y el modelo EF lo mapea): la fila
        // se inserta con SQL en una base auxiliar que llega hasta el 0009.
        await using var sin0010 = await BaseDatos.CrearBaseConScriptsAsync("dbMetricasSin0010", clsBaseDatosPruebas.ScriptsHasta0009);
        var cadena = sin0010.Cadena;
        var ana = Guid.NewGuid();
        var creada = Reloj.AhoraUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        await clsBaseDatosPruebas.EjecutarAsync(cadena, $"""
            INSERT INTO dbo_cotifly.UsuariosWeb (id, nombreUsuario, regional) VALUES ('{ana}', N'u{ana:N}', N'Antioquia');
            INSERT INTO dbo_cotifly.CotizacionesSolicitud (folio, usuarioId, createdAt) VALUES (N'{FolioUnico()}', '{ana}', '{creada}');
            """);
        await using var proveedor = clsServiciosPruebas.Crear(cadena, Reloj);

        async Task<clsDatosMetricas> ListarSin0010Async(IReadOnlyCollection<Guid> usuarios)
        {
            await using var ambito = proveedor.CreateAsyncScope();
            return await ambito.ServiceProvider.GetRequiredService<IClsMetricasRepository>()
                .ListarCotizacionesAsync(usuarios, Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1), Ct);
        }

        var datos = await ListarSin0010Async([ana]);
        var sinUsuarios = await ListarSin0010Async([]);

        datos.CiudadesDisponibles.Should().BeFalse();
        datos.DeadlineDisponible.Should().BeTrue("deadline sigue en la cabecera");
        datos.Filas.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Destino = (string?)null, Deadline = (DateTimeOffset?)null });
        sinUsuarios.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task ConLosNombresDeMunicipioTraeElOrigenYElDestinoDeLaRutaUnoYElDeadline()
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
            $"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET origenNombre = N'MEDELLIN', destinoNombre = N'BOGOTA D.C.' WHERE cotizacionSolicitudId = '{cotizacion.Id}'; "
            + $"UPDATE dbo_cotifly.CotizacionesSolicitud SET deadline = '2026-09-20T18:00:00-05:00' WHERE id = '{cotizacion.Id}';");

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
    public async Task ElCostoSumaLasRutasYElMargenEsElPromedioPonderadoPorCosto()
    {
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET costoFinal = 1000000, margenAplicado = 0.10 WHERE cotizacionSolicitudId = '{cotizacion.Id}';
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, costoFinal, margenAplicado) VALUES ('{cotizacion.Id}', 2, 3000000, 0.20);
            """);

        var fila = (await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1))).Filas.Should().ContainSingle("se cuenta la cotizaciÃ³n, no sus rutas").Subject;

        fila.Costo.Should().Be(4_000_000m);
        fila.MargenAplicado.Should().Be(0.175m, "(0,10 Ã— 1.000.000 + 0,20 Ã— 3.000.000) / 4.000.000");
    }

    [Fact]
    public async Task UnaCotizacionGanadaConTresRutasDeDistintasLineasEsUnaFilaConSusTresRutasYLosAjustesDeCadaUna()
    {
        // Una fila por cotización (conteos) con sus rutas (valores y apertura por línea). Los ajustes
        // cuentan en su ruta (lineaSolicitudId); el que no tiene ruta va a la ruta 1, como en la migración del 0009.
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            DECLARE @id UNIQUEIDENTIFIER = '{cotizacion.Id}';
            DECLARE @nacional INT = (SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Nacional');
            DECLARE @importacion INT = (SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Importacion');
            DECLARE @exportacion INT = (SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Exportacion');
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET lineaNegocioId = @nacional, costoFinal = 1000000, precioFinalLinea = 1200000, margenAplicado = 0.1667
            WHERE cotizacionSolicitudId = @id AND orden = 1;
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, lineaNegocioId, costoFinal, precioFinalLinea, margenAplicado) VALUES
                (@id, 2, @importacion, 2000000, 2500000, 0.20), (@id, 3, @exportacion, 3000000, 3300000, 0.0909);
            UPDATE dbo_cotifly.CotizacionesSolicitud SET precioFinal = 7000000, ganada = 1, moneda = N'USD', trmAplicada = 2 WHERE id = @id;
            DECLARE @r3 UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = @id AND orden = 3);
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, lineaSolicitudId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), @id, @r3, N'{cotizacion.Folio}', 0.10, 0.12, 0.02, N'A', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), @id, @r3, N'{cotizacion.Folio}', 0.10, 0.11, 0.01, N'B', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), @id, NULL, N'{cotizacion.Folio}', 0.15, 0.13, -0.02, N'C', '{ana}', SYSDATETIMEOFFSET());
            """);

        var fila = (await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1))).Filas
            .Should().ContainSingle("una cotización con tres rutas cuenta una vez").Subject;

        fila.Costo.Should().Be(6_000_000m);
        fila.NumAjustes.Should().Be(3);
        fila.Rutas.Select(r => (r.Orden, r.LineaNegocio, r.Costo, r.PrecioFinal, r.MargenAplicado, r.NumAjustes, r.NumDiferencias, r.SumaDiferencias))
            .Should().Equal(
                (1, "Nacional", 1_000_000m, 1_200_000m, 0.1667m, 1, 1, -0.02m),
                (2, "Importacion", 2_000_000m, 2_500_000m, 0.20m, 0, 0, null),
                (3, "Exportacion", 3_000_000m, 3_300_000m, 0.0909m, 2, 2, 0.03m));
        fila.Rutas.Sum(r => r.NumAjustes).Should().Be(fila.NumAjustes, "cada ajuste cuenta en una sola ruta");

        var apertura = Cotifly.Dominio.Metricas.clsCalculadoraMetricas.PorLineaNegocio([fila]);
        apertura.Select(l => (l.LineaNegocio, l.Cotizaciones, l.Ganadas, l.ValorCotizadoCop)).Should().Equal(
            ("Exportacion", 1, 1, 6_600_000m), ("Importacion", 1, 1, 5_000_000m), ("Nacional", 1, 1, 2_400_000m));
    }

    [Fact]
    public async Task CadaRutaTraeSuMargenDePoliticaYLaRechazadaNoSale()
    {
        // La referencia del margen aplicado es el margenPoliticaSugerido de cada ruta de la versión vigente.
        // La comparación solo mira las finalizadas, así que la cotización se finaliza para que sus rutas entren.
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            DECLARE @id UNIQUEIDENTIFIER = '{cotizacion.Id}';
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET costoFinal = 1000000, margenAplicado = 0.10, margenPoliticaSugerido = 0.15
            WHERE cotizacionSolicitudId = @id AND orden = 1;
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, costoFinal, margenAplicado, margenPoliticaSugerido, estadoCosteo) VALUES
                (@id, 2, 3000000, 0.20, NULL, NULL), (@id, 3, 500000, 0.30, 0.25, N'rechazada');
            UPDATE dbo_cotifly.CotizacionesSolicitud SET finalizadaAt = SYSDATETIMEOFFSET(), finalizadaPor = '{ana}' WHERE id = @id;
            """);

        var fila = (await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1))).Filas.Should().ContainSingle().Subject;

        fila.Rutas.Select(r => (r.Orden, r.MargenAplicado, r.MargenPolitica)).Should().Equal((1, 0.10m, 0.15m), (2, 0.20m, (decimal?)null));
        var indicadores = Cotifly.Dominio.Metricas.clsCalculadoraMetricas.Indicadores([fila], Reloj.AhoraUtc, deadlineDisponible: false);
        (indicadores.MargenPoliticaPromedio, indicadores.MargenPoliticaPonderado, indicadores.DesviacionMargenPolitica, indicadores.RutasSinMargenPolitica)
            .Should().Be((0.15m, 0.15m, -0.05m, 1));
        indicadores.MargenAplicadoPromedio.Should().Be(0.10m, "solo la ruta 1: la 2 no tiene política y la 3 está rechazada");
    }

    /// <summary>
    /// Solo las cotizaciones finalizadas (<c>finalizadaAt</c>) entran en la comparación del margen contra la política. Una
    /// abierta cuenta en la cantidad, pero su ruta sin política no infla <c>rutas_sin_margen_politica</c> ni mueve la desviación.
    /// </summary>
    [Fact]
    public async Task SoloLasFinalizadasEntranEnLaComparacionConLaPoliticaPeroTodasCuentanEnLaCantidad()
    {
        var ana = await CrearUsuarioAsync();
        var finalizada = NuevaCotizacion(ana);
        await GuardarAsync(finalizada);
        var abierta = NuevaCotizacion(ana);
        await GuardarAsync(abierta);
        // La abierta queda con margen aplicado pero SIN política: si entrara, sumaría en rutas_sin_margen_politica. Se finaliza solo la otra.
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET margenPoliticaSugerido = NULL WHERE cotizacionSolicitudId = '{abierta.Id}';
            UPDATE dbo_cotifly.CotizacionesSolicitud SET finalizadaAt = SYSDATETIMEOFFSET(), finalizadaPor = '{ana}' WHERE id = '{finalizada.Id}';
            """);

        var datos = await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1));

        datos.Filas.Should().HaveCount(2);
        datos.Filas.Single(f => f.Finalizada).Rutas.Should().ContainSingle("la finalizada tiene su ruta con política");
        datos.Filas.Count(f => !f.Finalizada).Should().Be(1, "la abierta no está finalizada");
        var indicadores = Cotifly.Dominio.Metricas.clsCalculadoraMetricas.Indicadores(datos.Filas, Reloj.AhoraUtc, deadlineDisponible: false);
        indicadores.Cantidad.Should().Be(2, "la abierta sí cuenta en la cantidad");
        indicadores.MargenPoliticaPromedio.Should().Be(0.15m, "solo la finalizada entra en la comparación");
        indicadores.RutasSinMargenPolitica.Should().Be(0, "la ruta sin política es de la cotización abierta, que no cuenta");
    }

    /// <summary>
    /// Se mira la versión vigente. Una cadena con la v1 finalizada y una v2 sin finalizar (la vigente) NO entra en la
    /// comparación con la política.
    /// </summary>
    [Fact]
    public async Task UnaV1FinalizadaConV2SinFinalizarNoEntraEnLaComparacion()
    {
        var ana = await CrearUsuarioAsync();
        var folio = FolioUnico();
        var v1 = NuevaCotizacion(ana, folio);
        await GuardarAsync(v1);
        // La vigente es una v2 sin finalizar (con su ruta y política); se finaliza solo la v1.
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            DECLARE @v2 UNIQUEIDENTIFIER = NEWID();
            INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio, usuarioId, precioFinal, version, createdAt, updatedAt)
            VALUES (@v2, N'{folio}-v2', '{ana}', 2000000, 2, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, costoFinal, margenAplicado, margenPoliticaSugerido)
            VALUES (@v2, 1, 1000000, 0.10, 0.15);
            UPDATE dbo_cotifly.CotizacionesSolicitud SET finalizadaAt = SYSDATETIMEOFFSET(), finalizadaPor = '{ana}' WHERE id = '{v1.Id}';
            """);

        var datos = await ListarAsync([ana], Reloj.AhoraUtc.AddYears(-1), Reloj.AhoraUtc.AddYears(1));

        var fila = datos.Filas.Should().ContainSingle("una fila por cadena de versiones").Subject;
        fila.Versiones.Should().Be(2);
        fila.Finalizada.Should().BeFalse("la versión vigente (la v2) no está finalizada");
        var indicadores = Cotifly.Dominio.Metricas.clsCalculadoraMetricas.Indicadores(datos.Filas, Reloj.AhoraUtc, deadlineDisponible: false);
        indicadores.Cantidad.Should().Be(1);
        indicadores.MargenPoliticaPromedio.Should().BeNull("la cadena vigente no está finalizada");
        indicadores.RutasSinMargenPolitica.Should().Be(0);
    }

    [Fact]
    public async Task ElSeguroDeLaRutaSeLeeYNoCuentaComoGanancia()
    {
        // costoSeguro (script 20) está en el precio de la ruta pero no es ganancia.
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            DECLARE @id UNIQUEIDENTIFIER = '{cotizacion.Id}';
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET costoFinal = 1000000, precioFinalLinea = 1940000, margenAplicado = 0.20,
                   incluyeSeguro = 1, tasaSeguroAplicada = 0.003450, costoSeguro = 690000
            WHERE cotizacionSolicitudId = @id AND orden = 1;
            UPDATE dbo_cotifly.CotizacionesSolicitud SET precioFinal = 1940000, ganada = 1 WHERE id = @id;
            """);

        var fila = (await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1))).Filas.Should().ContainSingle().Subject;

        (fila.Seguro, fila.Rutas.Single().Seguro).Should().Be((690_000m, 690_000m));
        var indicadores = Cotifly.Dominio.Metricas.clsCalculadoraMetricas.Indicadores([fila], Reloj.AhoraUtc, deadlineDisponible: false);
        (indicadores.Ganancias, indicadores.MargenPonderado, indicadores.ValorCotizadoCop).Should().Be((250_000m, 0.20m, 1_940_000m));
    }

    [Fact]
    public async Task SinCotizacionesNoConsultaRutas()
    {
        var ana = await CrearUsuarioAsync();

        var datos = await ListarAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1));

        datos.Filas.Should().BeEmpty();
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
