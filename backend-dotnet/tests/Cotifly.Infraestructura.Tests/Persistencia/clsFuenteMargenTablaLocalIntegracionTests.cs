using System.Runtime.CompilerServices;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Margen.Exceptions;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// HU-042.2 (#243, ADR-027) y HU-042.4 (#247): <see cref="IClsMargenResolverService"/> con el registro real (la fuente es
/// <see cref="clsFuenteMargenTablaLocal"/>) sobre SQL Server 2019 con <c>SapMargenCliente</c> sembrada con la muestra del Python y
/// algunas filas propias. Las pruebas usan bases AISLADAS: la compartida de la colección nunca tiene una corrida <c>ok</c> en
/// <c>SapMargenSyncLog</c> (así <c>clsMargenSapNoConfiguradoIntegracionTests</c> y el resto ven "SAP TM no configurado").
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsFuenteMargenTablaLocalIntegracionTests(clsBaseDatosPruebas baseDatos)
{
    /// <summary>Fin de la corrida <c>ok</c> sembrada: 2 h antes del reloj falso de las pruebas (2026-09-24 13:00 UTC).</summary>
    private static readonly DateTimeOffset _sincronizacionOk = new(2026, 9, 24, 6, 0, 0, TimeSpan.FromHours(-5));

    /// <summary>Organización específica con un mapeo propio de Nacional (gana sobre el comodín en esa organización).</summary>
    private const string OrganizacionConMapeoPropio = "50000099";

    private static readonly ConditionalWeakTable<clsBaseDatosPruebas, Lazy<Task<clsBaseAuxiliarPruebas>>> _basesConCopia = new();

    private readonly clsRelojFalso _reloj = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CadaClienteRecibeSuPropioMargenNoElDeLaPrimeraFila()
    {
        var primero = await ResolverOkAsync("Nacional", "15927");
        var otro = await ResolverOkAsync("Nacional", "880");

        (primero.Origen, primero.Margen, primero.TrqType).Should().Be(("sap_tm", 0.135m, "OPTN"));
        (otro.Origen, otro.Margen, otro.TrqType).Should().Be(("sap_tm", 0.2125m, "OPTN"), "880 tiene su propia fila (21,25 %)");
        otro.Detalle.Should().Be("Margen propuesto SAP TM (tabla local, actualizado 2026-09-23) para cliente 880 en OPTN: 21.25%");
    }

    [Theory]
    [InlineData("Nacional", "0000015927", 0.135)]
    [InlineData("Nacional", " 15927 ", 0.135)]
    [InlineData("Importacion", "777", 0.065)]
    [InlineData("Importacion", "0000000777", 0.065)]
    public async Task ElBpSeEncuentraConOSinCerosALaIzquierdaEnCualquierLado(string linea, string bp, double margen)
    {
        var r = await ResolverOkAsync(linea, bp);

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be((decimal)margen);
    }

    [Fact]
    public async Task UnaFilaInactivaNoSeUsaYSeAplicaLaPolitica()
    {
        var r = await ResolverOkAsync("Exportacion", "4242");

        r.Origen.Should().Be("politica_linea");
        r.Margen.Should().Be(0.15m);
        r.Detalle.Should().Contain(clsMargenResolverService.MotivoSinRegistro);
    }

    [Fact]
    public async Task UnClienteSinFilaUsaLaPoliticaConElMotivoSinRegistro()
    {
        var r = await ResolverOkAsync("Nacional", "99999");

        r.Origen.Should().Be("politica_linea");
        r.Detalle.Should().Contain(clsMargenResolverService.MotivoSinRegistro);
    }

    [Fact]
    public async Task UnMargenFueraDeRangoSeMarcaAnomaloParaQueElEnvioUseLaPolitica()
    {
        // #139: el envío a Nexo aplica la política y guarda el de SAP como marca (clsMargenSapAnomaloEndpointsTests, también con esta fuente).
        var negativo = await ResolverOkAsync("Nacional", "5000");
        var atipico = await ResolverOkAsync("Importacion", "5001");

        (negativo.Origen, negativo.Margen, negativo.Anomalo).Should().Be(("sap_tm", -0.192m, true));
        negativo.Detalle.Should().Contain("ANÓMALO");
        (atipico.Margen, atipico.Anomalo).Should().Be((0.999m, true));
    }

    [Fact]
    public async Task ConLaSincronizacionViejaElMargenSeUsaYElDetalleLoAvisa()
    {
        _reloj.AhoraUtc = _sincronizacionOk.AddHours(49).ToUniversalTime();

        var r = await ResolverOkAsync("Nacional", "15927");

        r.Origen.Should().Be("sap_tm");
        r.Margen.Should().Be(0.135m, "la copia no caduca");
        r.Detalle.Should().Contain("sincronización con más de 48 h").And.Contain("2026-09-24 06:00 -05:00");
    }

    [Fact]
    public async Task ConLaSincronizacionRecienteNoHayAvisoAunqueHayaCorridasFallidasDespues()
    {
        // La base tiene una corrida 'error' posterior a la 'ok': solo cuenta la última 'ok' (2 h antes del reloj).
        var r = await ResolverOkAsync("Nacional", "15927");

        r.Detalle.Should().NotContain("sincronización con más de");
    }

    [Fact]
    public async Task UnRegistroDeOtraOrganizacionNoAplica()
    {
        var r = await ResolverOkAsync("Nacional", "15927", "50000010");

        r.Origen.Should().Be("politica_linea", "en la organización 50000010 el cliente no tiene fila (ni hay mapeo propio: usa el comodín OPTN)");
        r.TrqType.Should().Be("OPTN");
    }

    // HU-042.4 (#247): el TrqType sale de SapMapeoTrqType por el id de la política; la fila de la organización gana sobre el comodín.
    [Fact]
    public async Task ElMapeoPorIdDeUnaOrganizacionGanaSobreElComodinYLleganAlMargenDeEsaOrganizacion()
    {
        var propia = await ResolverOkAsync("Nacional", "15927", OrganizacionConMapeoPropio);

        (propia.Origen, propia.Margen, propia.TrqType).Should().Be(("sap_tm", 0.09m, "OPXX"));
    }

    [Fact]
    public async Task SinNingunaCorridaOkLaCopiaSeTrataComoNoConfigurada()
    {
        // Base propia con corridas 'error' y 'omitido' y filas en la tabla: sin una 'ok', la tabla no se lee.
        await using var sinOk = await baseDatos.CrearBaseConScriptsAsync("dbSapSinOk", clsBaseDatosPruebas.Scripts.Length);
        await clsBaseDatosPruebas.EjecutarAsync(sinOk.Cadena, """
            INSERT INTO dbo_cotifly.SapMargenCliente (salesOrgId, trqType, bpSap, margenRecomendado) VALUES (N'50000005', N'OPTN', N'15927', 13.5);
            INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError) VALUES
                ('2026-09-24T05:00:00-05:00', '2026-09-24T05:01:00-05:00', N'error', N'SAP no respondió'),
                ('2026-09-24T06:00:00-05:00', '2026-09-24T06:00:01-05:00', N'omitido', N'Ya hay otra corrida en curso');
            """);

        var r = (await ResolverAsync(sinOk.Cadena, new clsEntradaMargen("Nacional", "15927"))).Valor.Salida;

        r.Origen.Should().Be("politica_linea");
        r.Detalle.Should().Contain(clsMargenResolverService.MotivoNoConfigurado);
    }

    [Fact]
    public async Task UnaBaseSinLasTablasDelScript17HaceQueLaFuenteNoEsteDisponible()
    {
        // Error de base al leer la copia (aquí, la tabla no existe): la fuente lo traduce y el resolver usa la política ("no disponible").
        await using var proveedor = clsServiciosPruebas.Crear(await baseDatos.CadenaSolo0001Async(), _reloj, baseDatos.CadenaIdentidad);
        await using var ambito = proveedor.CreateAsyncScope();
        var fuente = ambito.ServiceProvider.GetRequiredService<IClsFuenteMargen>();

        var consultar = () => fuente.ConsultarAsync("50000005", "OPTN", "15927", Ct);

        await consultar.Should().ThrowAsync<clsFuenteMargenNoDisponibleException>();
    }

    [Fact]
    public async Task LaFuenteDevuelveLaFechaDeLaFilaYLaDeLaUltimaSincronizacionOk()
    {
        await using var proveedor = clsServiciosPruebas.Crear(await CadenaConCopiaAsync(), _reloj, baseDatos.CadenaIdentidad);
        await using var ambito = proveedor.CreateAsyncScope();

        var consulta = await ambito.ServiceProvider.GetRequiredService<IClsFuenteMargen>().ConsultarAsync(" 50000005 ", "optn", "880", Ct);

        consulta.Estado.Should().Be(EstadoConsultaMargenSap.ConValor);
        consulta.Porcentaje.Should().Be(21.25m);
        consulta.ActualizadoAt.Should().Be(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.FromHours(-5)));
        consulta.UltimaSincronizacion.Should().Be(_sincronizacionOk);
    }

    private async Task<clsSalidaMargen> ResolverOkAsync(string linea, string? bp, string? salesOrg = null)
    {
        var resultado = await ResolverAsync(await CadenaConCopiaAsync(), new clsEntradaMargen(linea, bp, salesOrg));
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        return resultado.Valor.Salida;
    }

    private async Task<clsResultado<clsMargenResuelto>> ResolverAsync(string cadena, clsEntradaMargen entrada)
    {
        await using var proveedor = clsServiciosPruebas.Crear(cadena, _reloj, baseDatos.CadenaIdentidad);
        await using var ambito = proveedor.CreateAsyncScope();
        return await ambito.ServiceProvider.GetRequiredService<IClsMargenResolverService>().ResolverAsync(entrada, Ct);
    }

    /// <summary>
    /// Base aislada con todos los scripts y la copia sembrada, una por contenedor (se crea la primera vez que una prueba la pide y vive lo
    /// que vive el contenedor): las filas de la muestra del Python más 880 (21,25 %, segunda fila de OPTN), 0000000777 (BP con ceros en la
    /// tabla), 4242 (inactiva), el mapeo propio de Nacional en <see cref="OrganizacionConMapeoPropio"/> (OPXX) con su fila, una corrida
    /// <c>ok</c> (<see cref="_sincronizacionOk"/>) y una <c>error</c> posterior.
    /// </summary>
    private async Task<string> CadenaConCopiaAsync() =>
        (await _basesConCopia.GetValue(baseDatos, b => new Lazy<Task<clsBaseAuxiliarPruebas>>(() => CrearBaseConCopiaAsync(b))).Value).Cadena;

    private static async Task<clsBaseAuxiliarPruebas> CrearBaseConCopiaAsync(clsBaseDatosPruebas baseDatos)
    {
        var aislada = await baseDatos.CrearBaseConScriptsAsync("dbSapCopia", clsBaseDatosPruebas.Scripts.Length);
        List<(string SalesOrg, string TrqType, string Bp, string Valor, int Activo)> filas =
        [
            .. clsFuenteMargenMuestra.Filas.Select(f => (f.SalesOrg, f.TrqType, f.Bp, f.Valor, 1)),
            ("50000005", "OPTN", "880", "21.25", 1),
            ("50000005", "OPIM", "0000000777", "6.5", 1),
            ("50000005", "OPEX", "4242", "11", 0),
            (OrganizacionConMapeoPropio, "OPXX", "15927", "9", 1),
        ];
        var valores = string.Join(",\n", filas.Select(f =>
            $"(N'{f.SalesOrg}', N'{f.TrqType}', N'{f.Bp}', NULL, {f.Valor}, {f.Activo}, '2026-09-01T08:00:00-05:00', '2026-09-23T08:00:00-05:00')"));
        await clsBaseDatosPruebas.EjecutarAsync(aislada.Cadena, $"""
            INSERT INTO dbo_cotifly.SapMargenCliente (salesOrgId, trqType, bpSap, nit, margenRecomendado, activo, creadoAt, actualizadoAt) VALUES
            {valores};
            INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, filasInsertadas, filasActualizadas, filasDesactivadas, status) VALUES
                ('2026-09-24T05:58:00-05:00', '2026-09-24T06:00:00-05:00', 14, 0, 1, N'ok');
            INSERT INTO dbo_cotifly.SapMargenSyncLog (iniciadoAt, finalizadoAt, status, mensajeError) VALUES
                ('2026-09-24T07:30:00-05:00', '2026-09-24T07:31:00-05:00', N'error', N'SAP no respondió');
            INSERT INTO dbo_cotifly.SapMapeoTrqType (trqType, salesOrg, lineaNegocio, lineaNegocioId, activo)
            SELECT N'OPXX', N'{OrganizacionConMapeoPropio}', lineaNegocio, id, 1 FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'Nacional';
            """);
        return aislada;
    }
}
