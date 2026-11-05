using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-042.2 (#243, ADR-027): la sincronización del margen de SAP TM que hace el backend (<see cref="clsSincronizacionMargenSapSql"/>, el port
/// del script de Datos) contra SQL Server 2019 con el script 17 real (<c>SapMargenCliente</c>, <c>SapMargenSyncLog</c> y
/// <c>sp_sincronizar_margen_sap</c>), un SAP falso que ignora <c>$filter</c> (<see cref="clsSapRentabFalso"/>) y un DW falso
/// (<c>DWCubosOperacionesEBS.dbo.DimBPSolicitante</c>) en el mismo contenedor. Cada prueba usa una base AISLADA creada con la collation de la
/// base real (<see cref="clsBaseDatosPruebas.CollationCotifly"/>), distinta de la del servidor: así se prueba el <c>COLLATE DATABASE_DEFAULT</c>
/// de <c>#SapMargenStage</c>. La base compartida de la colección nunca tiene una corrida <c>ok</c> (ver
/// <c>clsFuenteMargenTablaLocalIntegracionTests</c>).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsSincronizacionMargenSapIntegracionTests(clsBaseDatosPruebas baseDatos)
{
    private static readonly ConditionalWeakTable<clsBaseDatosPruebas, Lazy<Task<clsBaseAuxiliarPruebas>>> _basesFallos = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AltaCambioBajaYReactivacionConElNitDelDwYSinFilterEnLaUrl()
    {
        await using var aislada = await BaseAsync("dbSapAlta");
        var p = Prefijo();
        await baseDatos.AgregarBpDwAsync(($"{p}1", "900111222"), ($"{p}2", " 800333444 "), ($"0000{p}3", "700555666"));
        var sap = new clsSapRentabFalso().ConFilas(
        [
            ("50000005", "OPTN", $"{p}1", "13.592356781"),
            (" 50000005 ", "optn", $" {p}2 ", "21.25"),
            ("50000005", "OPIM", $"0000{p}3", "6.5"),
        ]);
        await using var proveedor = await ProveedorAsync(aislada.Cadena, sap, conDw: true, extra: new() { ["SapTm:SapClient"] = "100" });
        var sincronizacion = proveedor.GetRequiredService<IClsSincronizacionMargenSap>();

        // Alta.
        var alta = await sincronizacion.SincronizarAsync(forzada: false, Ct);

        alta.Estado.Should().Be("ok", alta.Corrida?.MensajeError);
        (alta.FilasRecibidas, alta.Corrida!.FilasInsertadas, alta.Corrida.FilasActualizadas, alta.Corrida.FilasDesactivadas).Should().Be((3, 3, 0, 0));
        var filas = await FilasAsync(aislada.Cadena);
        filas.Should().BeEquivalentTo(
        [
            new clsFilaPrueba("50000005", "OPTN", $"{p}1", "900111222", 13.5924m, true),
            new clsFilaPrueba("50000005", "OPTN", $"{p}2", "800333444", 21.25m, true),
            new clsFilaPrueba("50000005", "OPIM", $"0000{p}3", "700555666", 6.5m, true),
        ], "el BP se guarda como lo manda SAP (con sus ceros), recortado; organización y TrqType en mayúsculas");
        var peticion = sap.Peticiones.Should().ContainSingle().Subject;
        peticion.Url.AbsolutePath.Should().EndWith("/ZGW_RENTAB_CLI_SRV/RentabClienteSet");
        peticion.Url.Query.Should().Be("?$format=json&sap-client=100").And.NotContain("filter");
        peticion.Autorizacion.Should().StartWith("Basic ");
        peticion.AcceptEncoding.Should().Contain("gzip");

        // Cambio de valor (y de NIT en el DW), baja de una combinación que SAP dejó de traer.
        await baseDatos.AgregarBpDwAsync(($"{p}9", "100200300"));
        sap.ConFilas(
        [
            ("50000005", "OPTN", $"{p}1", "14.0000000001"),
            ("50000005", "OPTN", $"{p}2", "21.25"),
            ("50000005", "OPTN", $"{p}9", "9"),
        ]);
        var cambio = await sincronizacion.SincronizarAsync(forzada: true, Ct);

        (cambio.Estado, cambio.Corrida!.FilasInsertadas, cambio.Corrida.FilasActualizadas, cambio.Corrida.FilasDesactivadas).Should().Be(("ok", 1, 1, 1));
        (await FilasAsync(aislada.Cadena)).Should().Contain(
        [
            new clsFilaPrueba("50000005", "OPTN", $"{p}1", "900111222", 14m, true),
            new clsFilaPrueba("50000005", "OPIM", $"0000{p}3", "700555666", 6.5m, false),
            new clsFilaPrueba("50000005", "OPTN", $"{p}9", "100200300", 9m, true),
        ]);

        // Reactivación: vuelve la combinación desactivada.
        sap.ConFilas(
        [
            ("50000005", "OPTN", $"{p}1", "14"),
            ("50000005", "OPTN", $"{p}2", "21.25"),
            ("50000005", "OPTN", $"{p}9", "9"),
            ("50000005", "OPIM", $"0000{p}3", "7.25"),
        ]);
        var reactivacion = await sincronizacion.SincronizarAsync(forzada: true, Ct);

        (reactivacion.Estado, reactivacion.Corrida!.FilasInsertadas, reactivacion.Corrida.FilasActualizadas, reactivacion.Corrida.FilasDesactivadas)
            .Should().Be(("ok", 0, 1, 0));
        (await FilasAsync(aislada.Cadena)).Should().ContainSingle(f => f.Bp == $"0000{p}3")
            .Which.Should().Be(new clsFilaPrueba("50000005", "OPIM", $"0000{p}3", "700555666", 7.25m, true));
        (await clsBaseDatosPruebas.EscalarAsync<int>(aislada.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.SapMargenSyncLog WHERE status = N'ok'"))
            .Should().Be(3);
    }

    [Fact]
    public async Task LaBaseTieneOtraCollationQueElServidorYLaTemporalNoChoca()
    {
        await using var aislada = await BaseAsync("dbSapColl");
        var servidor = await clsBaseDatosPruebas.EscalarAsync<string>(aislada.Cadena, "SELECT CAST(SERVERPROPERTY('Collation') AS nvarchar(128))");
        var baseCotifly = await clsBaseDatosPruebas.EscalarAsync<string>(aislada.Cadena, "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'Collation') AS nvarchar(128))");
        await using var proveedor = await ProveedorAsync(aislada.Cadena, new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 5)));

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        baseCotifly.Should().Be(clsBaseDatosPruebas.CollationCotifly).And.NotBe(servidor);
        r.Estado.Should().Be("ok", r.Corrida?.MensajeError);
        (await FilasAsync(aislada.Cadena)).Should().HaveCount(5).And.OnlyContain(f => f.Nit == null);
    }

    [Fact]
    public async Task SinElDwConfiguradoElNitQuedaNullYSeSincronizaConUnWarning()
    {
        await using var aislada = await BaseAsync("dbSapSinDw");
        var logs = new clsProveedorLogsFalso();
        await using var proveedor = await ProveedorAsync(aislada.Cadena, new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 3)), logs: logs);

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("ok");
        (await FilasAsync(aislada.Cadena)).Should().HaveCount(3).And.OnlyContain(f => f.Nit == null && f.Activo);
        var registros = logs.Registros.Select(r => r.Registro).ToList();
        registros.Where(l => l.Nivel == LogLevel.Warning && l.Mensaje.Contains("DwOperaciones")).Should().ContainSingle();
        registros.Should().Contain(l => l.Nivel == LogLevel.Information && l.Mensaje.Contains("3 filas recibidas"));
        // La URL (sin credenciales) sí aparece en los logs de IHttpClientFactory; el usuario, la clave y la cabecera Basic, nunca.
        registros.Should().NotContain(l => l.Mensaje.Contains(clsSapRentabFalso.Clave) || l.Mensaje.Contains(clsSapRentabFalso.Usuario)
            || l.Mensaje.Contains("Basic ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConMenosFilasQueElMinimoAbsolutoNoSeTocaLaTablaYQuedaErrorConElMotivo()
    {
        await using var aislada = await BaseAsync("dbSapMinAbs");
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 50));
        await using var proveedor = await ProveedorAsync(aislada.Cadena, sap, extra: new() { ["SapTm:MinimoFilas"] = "100" });

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("error");
        r.Corrida!.MensajeError.Should().Contain("devolvió 50 filas").And.Contain("mínimo exigido es 100");
        (await FilasAsync(aislada.Cadena)).Should().BeEmpty();
        (await UltimaCorridaAsync(aislada.Cadena)).Should().Be(("error", r.Corrida.MensajeError));
    }

    [Fact]
    public async Task ConMenosDelPorcentajeDeLasActivasNoSeDesactivaNada()
    {
        await using var aislada = await BaseAsync("dbSapMinPct");
        var p = Prefijo();
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(p, 200));
        await using var proveedor = await ProveedorAsync(aislada.Cadena, sap, extra: new() { ["SapTm:MinimoFilas"] = "10" });
        var sincronizacion = proveedor.GetRequiredService<IClsSincronizacionMargenSap>();
        (await sincronizacion.SincronizarAsync(forzada: true, Ct)).Estado.Should().Be("ok");

        sap.ConFilas(clsSapRentabFalso.Set(p, 150)); // 150 < ceil(80 % × 200) = 160
        var r = await sincronizacion.SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("error");
        r.Corrida!.MensajeError.Should().Contain("devolvió 150 filas").And.Contain("mínimo exigido es 160");
        (await FilasAsync(aislada.Cadena)).Should().HaveCount(200).And.OnlyContain(f => f.Activo);
    }

    public static TheoryData<string, string> FallosDeSap() => new()
    {
        { "500", "HTTP 500" },
        { "401", "rechazó las credenciales" },
        { "json", "JSON inválido" },
        { "forma", "no tiene la forma" },
        { "red", "No se pudo conectar con SAP TM" },
        { "sin_campo", "no trae 'OrderPartyId' de texto" },
        { "valor", "trae 'Value' no numérico" },
        { "duplicada", "repitió la combinación" },
        { "vacio", "0 registros" },
    };

    [Theory]
    [MemberData(nameof(FallosDeSap))]
    public async Task UnFalloDeSapDejaUnaFilaErrorSinSecretosYLaTablaIntacta(string caso, string motivo)
    {
        var cadena = (await BaseFallosAsync()).Cadena;
        var antes = await FilasAsync(cadena);
        var sap = new clsSapRentabFalso();
        _ = caso switch
        {
            "500" => sap.Responder(HttpStatusCode.InternalServerError, "error"),
            "401" => sap.Responder(HttpStatusCode.Unauthorized),
            "json" => sap.Responder(HttpStatusCode.OK, "{\"d\": {\"results\": ["),
            "forma" => sap.Responder(HttpStatusCode.OK, "{\"value\": []}"),
            "red" => sap.Fallar(new HttpRequestException("Name or service not known (sap-tm-falso.pruebas.local:8000)")),
            "sin_campo" => sap.Responder(HttpStatusCode.OK, """{"d":{"results":[{"SalesOrgId":"50000005","TrqType":"OPTN","Value":"1"}]}}"""),
            "valor" => sap.Responder(HttpStatusCode.OK, """{"d":{"results":[{"SalesOrgId":"50000005","TrqType":"OPTN","OrderPartyId":"1","Value":"13,5"}]}}"""),
            "duplicada" => sap.ConFilas([("50000005", "OPTN", "77", "1"), ("50000005", "optn", "77", "2")]),
            _ => sap.ConFilas([]),
        };
        await using var proveedor = await ProveedorAsync(cadena, sap);

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("error");
        r.Corrida!.MensajeError.Should().Contain(motivo).And.NotContain(clsSapRentabFalso.Clave).And.NotContain(clsSapRentabFalso.Usuario)
            .And.NotContain("sap-tm-falso");
        r.Corrida.FinalizadoAt.Should().NotBeNull();
        (await UltimaCorridaAsync(cadena)).Should().Be(("error", r.Corrida.MensajeError));
        (await FilasAsync(cadena)).Should().BeEquivalentTo(antes);
    }

    [Fact]
    public async Task ConElDwConfiguradoYCaidoQuedaErrorYNoSeTocaLaTabla()
    {
        var cadena = (await BaseFallosAsync()).Cadena;
        var antes = await FilasAsync(cadena);
        var dwInexistente = clsBaseDatosPruebas.ConBase(baseDatos.CadenaMaster, "DWNoExiste_Pruebas");
        await using var proveedor = await ProveedorAsync(cadena, new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 3)),
            extra: new() { ["ConnectionStrings:DwOperaciones"] = dwInexistente });

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("error");
        r.Corrida!.MensajeError.Should().Contain("DWCubosOperacionesEBS").And.NotContain("Password").And.NotContain("DWNoExiste");
        (await FilasAsync(cadena)).Should().BeEquivalentTo(antes);
    }

    [Fact]
    public async Task SiTodaviaNoTocaNoLlamaASapYElWorkerSincronizaCuandoNuncaHuboUnaOk()
    {
        await using var aislada = await BaseAsync("dbSapNoToca");
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 4));
        await using var proveedor = await ProveedorAsync(aislada.Cadena, sap);
        var worker = proveedor.GetServices<IHostedService>().OfType<clsWorkerSincronizacionMargenSap>().Single();

        var primera = await worker.RondaAsync(Ct); // nunca hubo una corrida ok: toca
        var segunda = await worker.RondaAsync(Ct); // la ok es de hace segundos: no toca

        primera!.Estado.Should().Be("ok");
        segunda!.Estado.Should().Be(clsResultadoSincronizacionMargenSap.EstadoNoToca);
        sap.Peticiones.Should().ContainSingle("la segunda ronda no llama a SAP");

        // Una corrida ok más vieja que SapTm:SincronizacionMinutos (1440) vuelve a tocar.
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena,
            "UPDATE dbo_cotifly.SapMargenSyncLog SET iniciadoAt = DATEADD(MINUTE, -1500, iniciadoAt), finalizadoAt = DATEADD(MINUTE, -1500, finalizadoAt);");
        (await worker.RondaAsync(Ct))!.Estado.Should().Be("ok");
        sap.Peticiones.Should().HaveCount(2);
    }

    [Fact]
    public async Task ElEstadoTraeLaUltimaCorridaLaUltimaOkYLasFilasActivas()
    {
        await using var aislada = await BaseAsync("dbSapEstado");
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 6));
        await using var proveedor = await ProveedorAsync(aislada.Cadena, sap);
        var sincronizacion = proveedor.GetRequiredService<IClsSincronizacionMargenSap>();
        await sincronizacion.SincronizarAsync(forzada: true, Ct);
        sap.Responder(HttpStatusCode.ServiceUnavailable);
        await sincronizacion.SincronizarAsync(forzada: true, Ct);

        var estado = await sincronizacion.EstadoAsync(Ct);

        estado.UltimaCorrida!.Estado.Should().Be("error");
        estado.UltimaCorridaOk!.Estado.Should().Be("ok");
        estado.UltimaCorridaOk.FilasInsertadas.Should().Be(6);
        estado.FilasActivas.Should().Be(6);
        (estado.IntervaloMinutos, estado.ChequeoMinutos, estado.EnCurso, estado.NitConfigurado).Should().Be((1440, 60, false, false));
        estado.ProximaCorridaEstimada.Should().BeCloseTo(estado.UltimaCorridaOk.FinalizadoAt!.Value.AddMinutes(1440), TimeSpan.FromSeconds(1));
    }

    // H1 (#243): la PK de #SapMargenStage tenía nombre; en tempdb el nombre de una restricción es global y la temporal de una segunda
    // réplica fallaba con el 2714 antes del sp_getapplock (en vez de quedar 'omitido').
    [Fact]
    public async Task DosConexionesPuedenTenerSuStageALaVez()
    {
        await using var aislada = await BaseAsync("dbSapStage");
        await using var primera = new SqlConnection(aislada.Cadena);
        await using var segunda = new SqlConnection(aislada.Cadena);
        await primera.OpenAsync(Ct);
        await segunda.OpenAsync(Ct);

        await using (var crear = new SqlCommand(clsSincronizacionMargenSapSql.DdlStage, primera))
        {
            await crear.ExecuteNonQueryAsync(Ct);
        }

        var crearOtra = async () =>
        {
            await using var comando = new SqlCommand(clsSincronizacionMargenSapSql.DdlStage, segunda);
            await comando.ExecuteNonQueryAsync(Ct);
        };

        await crearOtra.Should().NotThrowAsync("cada sesión tiene su #SapMargenStage; la PK sin nombre no choca en tempdb");
    }

    // H2 (#243): un corte a mitad del cuerpo o un gzip corrupto escapaban de la sincronización (sin fila y 500 en el POST).
    [Theory]
    [InlineData("corte")]
    [InlineData("gzip")]
    public async Task UnCorteOUnGzipCorruptoAMitadDelCuerpoDejaFilaError(string caso)
    {
        var cadena = (await BaseFallosAsync()).Cadena;
        var antes = await FilasAsync(cadena);
        Exception error = caso == "corte"
            ? new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
            : new InvalidDataException("The archive entry was compressed using an unsupported compression method.");
        var inicio = Encoding.UTF8.GetBytes("{\"d\":{\"results\":[{\"SalesOrgId\":\"50000005\",");
        var sap = new clsSapRentabFalso().ResponderFlujo(() => new clsCuerpoQueSeCorta(inicio, error));
        await using var proveedor = await ProveedorAsync(cadena, sap);

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("error");
        r.Corrida!.MensajeError.Should().Contain("SAP TM cortó la respuesta");
        (await UltimaCorridaAsync(cadena)).Should().Be(("error", r.Corrida.MensajeError));
        (await FilasAsync(cadena)).Should().BeEquivalentTo(antes);
    }

    // H3 (#243): un error que el TRY del stored procedure no atrapa (207 de recompilación del MERGE) no dejaba ninguna fila.
    [Fact]
    public async Task UnFalloDelSpSinFilaPropiaQuedaRegistradoComoError()
    {
        await using var aislada = await BaseAsync("dbSap207");
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, """
            CREATE OR ALTER PROCEDURE dbo_cotifly.sp_sincronizar_margen_sap AS
            BEGIN
                SET NOCOUNT ON; SET XACT_ABORT ON;
                DECLARE @iniciadoAt DATETIMEOFFSET = SYSDATETIMEOFFSET();
                BEGIN TRY
                    BEGIN TRANSACTION;
                    UPDATE d SET nit = o.nombreCliente FROM dbo_cotifly.SapMargenCliente d JOIN #SapMargenStage o ON o.bpSap = d.bpSap;
                    INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status) VALUES (@iniciadoAt, SYSDATETIMEOFFSET(), N'ok');
                    COMMIT;
                END TRY
                BEGIN CATCH
                    IF XACT_STATE() <> 0 ROLLBACK;
                    INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError)
                    VALUES (@iniciadoAt, SYSDATETIMEOFFSET(), N'error', LEFT(ERROR_MESSAGE(), 2000));
                    THROW;
                END CATCH
            END
            """);
        await using var proveedor = await ProveedorAsync(aislada.Cadena, new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 3)));

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("error");
        r.Corrida!.MensajeError.Should().Contain("SQL 207");
        (await UltimaCorridaAsync(aislada.Cadena)).Should().Be(("error", r.Corrida.MensajeError));
    }

    [Fact]
    public async Task SinElSpOSinPermisoExecuteQuedaRegistradoComoError()
    {
        await using var aislada = await BaseAsync("dbSapSinSp");
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, "DROP PROCEDURE dbo_cotifly.sp_sincronizar_margen_sap;");
        await using var proveedor = await ProveedorAsync(aislada.Cadena, new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 3)));

        var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

        r.Estado.Should().Be("error");
        r.Corrida!.MensajeError.Should().Contain("SQL 2812");
        (await UltimaCorridaAsync(aislada.Cadena)).Estado.Should().Be("error");
    }

    // H4 (#243): con dos réplicas a la vez, la que hizo el MERGE leía la fila 'omitido' de la otra (la más reciente desde su marca).
    [Fact]
    public async Task ConOtraReplicaOmitidaMientrasSincronizaCadaUnaReportaSuPropiaCorrida()
    {
        await using var aislada = await BaseAsync("dbSapRep");
        await clsBaseDatosPruebas.EjecutarAsync(baseDatos.CadenaMaster, $"ALTER DATABASE [{aislada.Nombre}] SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;");
        await using var replicaA = await ProveedorAsync(aislada.Cadena, new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 3)));

        // Un bloqueo exclusivo sobre la copia detiene el MERGE de A cuando ya tiene el sp_getapplock.
        await using var bloqueo = new SqlConnection(aislada.Cadena);
        await bloqueo.OpenAsync(Ct);
        var transaccion = (SqlTransaction)await bloqueo.BeginTransactionAsync(Ct);
        await using (var tomar = new SqlCommand("SELECT COUNT(*) FROM dbo_cotifly.SapMargenCliente WITH (TABLOCKX, HOLDLOCK);", bloqueo, transaccion))
        {
            await tomar.ExecuteScalarAsync(Ct);
        }

        var a = Task.Run(() => replicaA.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct), Ct);
        var limite = DateTime.UtcNow.AddSeconds(60);
        while (await clsBaseDatosPruebas.EscalarAsync<int>(baseDatos.CadenaMaster,
                   $"SELECT COUNT(*) FROM sys.dm_tran_locks WHERE resource_type = N'APPLICATION' AND request_status = N'GRANT' AND resource_database_id = DB_ID(N'{aislada.Nombre}')") == 0)
        {
            DateTime.UtcNow.Should().BeBefore(limite, "A debía tomar el sp_getapplock");
            await Task.Delay(200, Ct);
        }

        // Réplica B: su stored procedure no toma el candado y deja 'omitido' (más reciente que el inicio de A).
        await using (var b = new SqlConnection(aislada.Cadena))
        {
            await b.OpenAsync(Ct);
            await using var comando = new SqlCommand(clsSincronizacionMargenSapSql.DdlStage + """
                EXEC dbo_cotifly.sp_sincronizar_margen_sap;
                SELECT TOP (1) status FROM dbo_cotifly.SapMargenSyncLog ORDER BY iniciadoAt DESC;
                """, b);
            ((string)(await comando.ExecuteScalarAsync(Ct))!).Should().Be("omitido");
        }

        await transaccion.CommitAsync(Ct);
        await transaccion.DisposeAsync();
        var resultadoA = await a;

        resultadoA.Estado.Should().Be("ok", "A hizo el MERGE; no es suya la fila 'omitido' de B (mensaje: " + resultadoA.Corrida?.MensajeError + ")");
        resultadoA.Corrida!.FilasInsertadas.Should().Be(3);
        (await FilasAsync(aislada.Cadena)).Should().HaveCount(3);
    }

    // Punto 5 (#243): sin el DW, o sin el BP en el DW, la fila conserva el NIT que ya tenía (el MERGE lo pisaba con NULL).
    [Fact]
    public async Task SinElDwOSinElBpEnElDwSeConservaElNitAnterior()
    {
        await using var aislada = await BaseAsync("dbSapNit");
        var p = Prefijo();
        await baseDatos.AgregarBpDwAsync(($"{p}1", "900111222"), ($"{p}2", "800333444"));
        var sap = new clsSapRentabFalso().ConFilas([("50000005", "OPTN", $"{p}1", "10"), ("50000005", "OPTN", $"{p}2", "11"), ("50000005", "OPTN", $"{p}3", "12")]);
        await using (var conDw = await ProveedorAsync(aislada.Cadena, sap, conDw: true))
        {
            (await conDw.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct)).Estado.Should().Be("ok");
        }

        sap.ConFilas([("50000005", "OPTN", $"{p}1", "10.5"), ("50000005", "OPTN", $"{p}2", "11"), ("50000005", "OPTN", $"{p}3", "12")]);
        await using (var sinDw = await ProveedorAsync(aislada.Cadena, sap))
        {
            (await sinDw.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct)).Estado.Should().Be("ok");
        }

        (await FilasAsync(aislada.Cadena)).Select(f => (f.Bp, f.Nit, f.Margen)).Should().BeEquivalentTo(new (string, string?, decimal)[]
        {
            ($"{p}1", "900111222", 10.5m), ($"{p}2", "800333444", 11m), ($"{p}3", null, 12m),
        });

        // Con el DW, pero sin uno de los BP (cliente que salió del cubo): ese conserva su NIT.
        await clsBaseDatosPruebas.EjecutarAsync(await baseDatos.CadenaDwOperacionesAsync(), $"DELETE FROM dbo.DimBPSolicitante WHERE BP = '{p}2';");
        await using (var conDw = await ProveedorAsync(aislada.Cadena, sap, conDw: true))
        {
            (await conDw.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct)).Estado.Should().Be("ok");
        }

        (await FilasAsync(aislada.Cadena)).Single(f => f.Bp == $"{p}2").Nit.Should().Be("800333444");
    }

    // Punto 8 (#243): un BP con varios NIT en el DW toma siempre el menor y avisa.
    [Fact]
    public async Task UnBpConVariosNitEnElDwTomaElMenorYAvisa()
    {
        await using var aislada = await BaseAsync("dbSapNits");
        var p = Prefijo();
        await baseDatos.AgregarBpDwAsync(($"{p}1", "900222333"), ($"{p}1", "800111222"), ($"{p}1", "900222333"));
        var logs = new clsProveedorLogsFalso();
        await using var proveedor = await ProveedorAsync(aislada.Cadena, new clsSapRentabFalso().ConFilas([("50000005", "OPTN", $"{p}1", "10")]),
            conDw: true, logs: logs);

        (await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct)).Estado.Should().Be("ok");

        (await FilasAsync(aislada.Cadena)).Should().ContainSingle().Which.Nit.Should().Be("800111222");
        logs.Registros.Should().Contain(r => r.Registro.Nivel == LogLevel.Warning && r.Registro.Mensaje.Contains("varios NIT para 1 BP"));
    }

    // Punto 7 (#243): con la base de Cotifly caída, el estado y la sincronización lanzan NoDisponible (503 en la administración).
    [Fact]
    public async Task ConLaBaseDeCotiflyCaidaElEstadoYLaSincronizacionNoEstanDisponibles()
    {
        var inexistente = clsBaseDatosPruebas.ConBase(baseDatos.CadenaMaster, "dbCotiflyNoExiste_Pruebas");
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 3));
        await using var proveedor = await ProveedorAsync(inexistente, sap);
        var sincronizacion = proveedor.GetRequiredService<IClsSincronizacionMargenSap>();

        var estado = () => sincronizacion.EstadoAsync(Ct);
        var ronda = () => sincronizacion.SincronizarAsync(forzada: false, Ct);
        var forzada = () => sincronizacion.SincronizarAsync(forzada: true, Ct);

        (await estado.Should().ThrowAsync<clsSincronizacionMargenSapNoDisponibleException>()).Which.Message.Should().NotContain("dbCotiflyNoExiste");
        await ronda.Should().ThrowAsync<clsSincronizacionMargenSapNoDisponibleException>();
        await forzada.Should().ThrowAsync<clsSincronizacionMargenSapNoDisponibleException>();
        sap.Peticiones.Should().BeEmpty("la corrida forzada comprueba la base antes de descargar de SAP");
    }

    // Ajuste del 2026-11-04: sin sincronización (deshabilitada o sin configuración válida) no hay próxima corrida que estimar.
    [Theory]
    [InlineData("false", clsSapRentabFalso.Url)]
    [InlineData("true", "sap-tm-prd:8000/sin-esquema")]
    public async Task SinSincronizacionLaProximaCorridaEstimadaEsNull(string habilitada, string url)
    {
        await using var aislada = await BaseAsync("dbSapProx");
        await using var proveedor = await ProveedorAsync(aislada.Cadena, new clsSapRentabFalso(),
            extra: new() { ["SapTm:SincronizacionHabilitada"] = habilitada, ["SapTm:Url"] = url });

        var estado = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().EstadoAsync(Ct);

        estado.ProximaCorridaEstimada.Should().BeNull();
    }

    // Ajuste del 2026-11-04: un error de una orden con la base respondiendo (229 sin permiso, 208 sin el objeto) en las lecturas previas
    // es un fallo de la corrida (fila 'error'), no "base no disponible".
    [Theory]
    [InlineData("229")]
    [InlineData("208")]
    public async Task UnErrorDeLaBaseQueRespondeEnLasLecturasPreviasEsUnErrorDeLaCorrida(string caso)
    {
        await using var aislada = await BaseAsync("dbSapPrevia");
        var cadena = aislada.Cadena;
        var login = $"v243p_{Guid.NewGuid():N}"[..20];
        const string Clave = "V243-Prueba-Clave-x9!";
        if (caso == "229")
        {
            await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, $"""
                CREATE LOGIN [{login}] WITH PASSWORD = N'{Clave}', CHECK_POLICY = OFF;
                CREATE USER [{login}] FOR LOGIN [{login}];
                GRANT SELECT, INSERT, EXECUTE ON SCHEMA::dbo_cotifly TO [{login}];
                DENY SELECT ON dbo_cotifly.SapMargenCliente TO [{login}];
                """);
            cadena = new SqlConnectionStringBuilder(aislada.Cadena) { UserID = login, Password = Clave, IntegratedSecurity = false }.ConnectionString;
        }
        else
        {
            await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, "EXEC sp_rename N'dbo_cotifly.SapMargenCliente', N'SapMargenClienteRenombrada';");
        }

        try
        {
            var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set(Prefijo(), 3));
            await using var proveedor = await ProveedorAsync(cadena, sap);

            var r = await proveedor.GetRequiredService<IClsSincronizacionMargenSap>().SincronizarAsync(forzada: true, Ct);

            r.Estado.Should().Be("error");
            r.Corrida!.MensajeError.Should().Be($"Falló la base de Cotifly al sincronizar el margen de SAP TM (SQL {caso}): no se tocó la tabla.");
            (await UltimaCorridaAsync(aislada.Cadena)).Should().Be(("error", r.Corrida.MensajeError));
            sap.Peticiones.Should().BeEmpty("falla antes de descargar de SAP");
        }
        finally
        {
            if (caso == "229")
            {
                SqlConnection.ClearAllPools();
                await clsBaseDatosPruebas.EjecutarAsync(baseDatos.CadenaMaster,
                    $"IF EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{login}') BEGIN TRY DROP LOGIN [{login}]; END TRY BEGIN CATCH END CATCH");
            }
        }
    }

    private static string Prefijo() => $"9{Random.Shared.Next(10_000, 99_999)}";

    private Task<clsBaseAuxiliarPruebas> BaseAsync(string prefijo) =>
        baseDatos.CrearBaseConScriptsAsync(prefijo, clsBaseDatosPruebas.Scripts.Length, clsBaseDatosPruebas.CollationCotifly);

    /// <summary>Base aislada para los fallos (no cambian la tabla), una por contenedor, con dos filas sembradas.</summary>
    private async Task<clsBaseAuxiliarPruebas> BaseFallosAsync() =>
        await _basesFallos.GetValue(baseDatos, b => new Lazy<Task<clsBaseAuxiliarPruebas>>(async () =>
        {
            var aislada = await b.CrearBaseConScriptsAsync("dbSapFallos", clsBaseDatosPruebas.Scripts.Length, clsBaseDatosPruebas.CollationCotifly);
            await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, """
                INSERT INTO dbo_cotifly.SapMargenCliente (salesOrgId, trqType, bpSap, nit, margenRecomendado) VALUES
                    (N'50000005', N'OPTN', N'15927', N'900123456', 13.5),
                    (N'50000005', N'OPIM', N'777', NULL, 6.5);
                """);
            return aislada;
        })).Value;

    private async Task<ServiceProvider> ProveedorAsync(
        string cadena, clsSapRentabFalso sap, bool conDw = false, Dictionary<string, string?>? extra = null, clsProveedorLogsFalso? logs = null)
    {
        var valores = new Dictionary<string, string?>
        {
            ["SapTm:SincronizacionHabilitada"] = "true",
            ["SapTm:Url"] = clsSapRentabFalso.Url,
            ["SapTm:User"] = clsSapRentabFalso.Usuario,
            ["SapTm:Pass"] = clsSapRentabFalso.Clave,
            ["SapTm:MinimoFilas"] = "1",
            ["ConnectionStrings:DwOperaciones"] = conDw ? await baseDatos.CadenaDwOperacionesAsync() : null,
        };
        foreach (var (clave, valor) in extra ?? [])
        {
            valores[clave] = valor;
        }

        return clsServiciosPruebas.Crear(cadena, new clsRelojFalso(), baseDatos.CadenaIdentidad, valores, configurar: s =>
        {
            s.AddHttpClient(clsClienteRentabSap.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => sap);
            if (logs is not null)
            {
                s.AddLogging(l => l.AddProvider(logs));
            }
        });
    }

    private static async Task<IReadOnlyList<clsFilaPrueba>> FilasAsync(string cadena)
    {
        await using var conexion = new Microsoft.Data.SqlClient.SqlConnection(cadena);
        await conexion.OpenAsync(Ct);
        await using var comando = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT salesOrgId, trqType, bpSap, nit, margenRecomendado, activo FROM dbo_cotifly.SapMargenCliente ORDER BY salesOrgId, trqType, bpSap;", conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        var filas = new List<clsFilaPrueba>();
        while (await lector.ReadAsync(Ct))
        {
            filas.Add(new clsFilaPrueba(lector.GetString(0), lector.GetString(1), lector.GetString(2), lector.IsDBNull(3) ? null : lector.GetString(3),
                lector.GetDecimal(4), lector.GetBoolean(5)));
        }

        return filas;
    }

    private static async Task<(string? Estado, string? Mensaje)> UltimaCorridaAsync(string cadena)
    {
        await using var conexion = new Microsoft.Data.SqlClient.SqlConnection(cadena);
        await conexion.OpenAsync(Ct);
        await using var comando = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT TOP (1) status, mensajeError FROM dbo_cotifly.SapMargenSyncLog ORDER BY iniciadoAt DESC;", conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        return await lector.ReadAsync(Ct) ? (lector.GetString(0), lector.IsDBNull(1) ? null : lector.GetString(1)) : (null, null);
    }

    /// <summary>Cuerpo que entrega unos bytes y luego lanza (conexión cortada o gzip corrupto).</summary>
    private sealed class clsCuerpoQueSeCorta(byte[] inicio, Exception error) : Stream
    {
        private int _posicion;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _posicion;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_posicion >= inicio.Length)
            {
                throw error;
            }

            var n = Math.Min(buffer.Length, inicio.Length - _posicion);
            inicio.AsSpan(_posicion, n).CopyTo(buffer);
            _posicion += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed record clsFilaPrueba(string SalesOrg, string TrqType, string Bp, string? Nit, decimal Margen, bool Activo);
}
