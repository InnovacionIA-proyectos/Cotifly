using System.Globalization;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Dominio.Metricas;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Ganancias y costeo por mes de la serie del tablero sobre SQL Server real. Las filas son las mismas del tablero
/// (<see cref="IClsMetricasRepository.ListarCotizacionesAsync"/>, una consulta para todo el periodo, sin consultas por mes) y la serie
/// las agrupa por el mes de Bogotá de la primera creación de la cadena con la regla de los indicadores: COP o USD × TRM, sin las rutas
/// rechazadas, lo ganado de la versión ganada. La suma de los meses cuadra con los indicadores del periodo.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMetricasSerieMensualRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static readonly TimeSpan _bogota = TimeSpan.FromHours(-5);
    private static readonly DateOnly _desde = new(2026, 1, 1);
    private static readonly DateOnly _hasta = new(2026, 5, 31);

    [Fact]
    public async Task CadaMesTraeSusGananciasYSuCosteoYLaSumaCuadraConLosIndicadores()
    {
        var ana = await CrearUsuarioAsync();

        // Enero: una ganada, una perdida y una ganada a las 23:30 del 31 de enero en Bogotá (ya es 1 de febrero en UTC).
        await CrearAsync(ana, "2026-01-10T12:00:00-05:00", precio: 1_000_000m, costo: 800_000m, ganada: true);
        await CrearAsync(ana, "2026-01-20T12:00:00-05:00", precio: 2_000_000m, costo: 1_700_000m, ganada: false);
        await CrearAsync(ana, "2026-01-31T23:30:00-05:00", precio: 500_000m, costo: 400_000m, ganada: true);

        // Febrero (00:30 del 1 de febrero en Bogotá): ganada en USD con TRM y una segunda ruta rechazada por la Mesa, que no cuenta.
        var usd = await CrearAsync(ana, "2026-02-01T00:30:00-05:00", precio: 1_000m, costo: 3_000_000m, ganada: true, moneda: "USD", trm: 4_000.5m);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, costoFinal, precioFinalLinea, estadoCosteo)
            VALUES ('{usd}', 2, 999999, NULL, N'rechazada');
            """);

        // Marzo: una sin resultado y una cadena cuya v1 se ganó después de crear la -v2 (vigente, sin precio ni costo).
        await CrearAsync(ana, "2026-03-05T12:00:00-05:00", precio: 300_000m, costo: 250_000m, ganada: null);
        var folio = $"PCN-S-{Guid.NewGuid():N}"[..30];
        var v1 = await CrearAsync(ana, "2026-03-11T12:00:00-05:00", precio: 600_000m, costo: 500_000m, ganada: null, folio: folio);
        var v2 = await CrearAsync(ana, "2026-03-12T12:00:00-05:00", precio: null, costo: null, ganada: null, folio: folio + "-v2");
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.CotizacionesSolicitud SET version = 2 WHERE id = '{v2}';
            UPDATE dbo_cotifly.CotizacionesSolicitud SET ganada = 1, marcadoAt = SYSDATETIMEOFFSET() WHERE id = '{v1}';
            """);

        // Abril: solo una perdida (mes sin ganadas). Mayo: sin cotizaciones.
        await CrearAsync(ana, "2026-04-15T12:00:00-05:00", precio: 700_000m, costo: 650_000m, ganada: false);

        var datos = await ListarAsync(ana);
        var serie = clsCalculadoraMetricas.SerieMensual(datos.Filas, _desde, _hasta, FechaBogota);
        var indicadores = clsCalculadoraMetricas.Indicadores(datos.Filas, Reloj.AhoraUtc, deadlineDisponible: false);

        serie.Should().Equal(
            new clsPuntoSerieMensual("2026-01", 3, 2, 1, 3_500_000m, 1_500_000m, 2_900_000m, 1_200_000m, 300_000m),
            new clsPuntoSerieMensual("2026-02", 1, 1, 0, 4_000_500m, 4_000_500m, 3_000_000m, 3_000_000m, 1_000_500m),
            new clsPuntoSerieMensual("2026-03", 2, 1, 0, 300_000m, 600_000m, 250_000m, 500_000m, 100_000m),
            new clsPuntoSerieMensual("2026-04", 1, 0, 1, 700_000m, 0m, 650_000m, 0m, 0m),
            new clsPuntoSerieMensual("2026-05", 0, 0, 0, 0m, 0m, 0m, 0m, 0m));

        (serie.Sum(p => p.Cantidad), serie.Sum(p => p.Ganadas), serie.Sum(p => p.Perdidas))
            .Should().Be((indicadores.Cantidad, indicadores.Ganadas, indicadores.Perdidas));
        serie.Sum(p => p.ValorCotizadoCop).Should().Be(indicadores.ValorCotizadoCop);
        serie.Sum(p => p.ValorGanadoCop).Should().Be(indicadores.ValorGanadoCop);
        serie.Sum(p => p.CosteoTotal).Should().Be(indicadores.CosteoTotal).And.Be(6_800_000m);
        serie.Sum(p => p.CosteoGanadas).Should().Be(indicadores.CosteoGanadas).And.Be(4_700_000m);
        serie.Sum(p => p.Ganancias).Should().Be(indicadores.Ganancias).And.Be(1_400_500m);
    }

    /// <summary>Crea una cotización con una ruta y fija sus valores por SQL; devuelve su id.</summary>
    private async Task<Guid> CrearAsync(
        Guid usuario, string creada, decimal? precio, decimal? costo, bool? ganada, string moneda = "COP", decimal trm = 0m, string? folio = null)
    {
        var cotizacion = NuevaCotizacion(usuario, folio);
        await GuardarAsync(cotizacion);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.CotizacionesSolicitud
            SET moneda = N'{moneda}', trmAplicada = {Sql(trm)}, precioFinal = {Sql(precio)}, ganada = {Sql(ganada)},
                marcadoAt = {(ganada is null ? "NULL" : "SYSDATETIMEOFFSET()")}, createdAt = '{creada}'
            WHERE id = '{cotizacion.Id}';
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET costoFinal = {Sql(costo)}, precioFinalLinea = {Sql(precio)}
            WHERE cotizacionSolicitudId = '{cotizacion.Id}';
            """);
        return cotizacion.Id;
    }

    private Task<clsDatosMetricas> ListarAsync(Guid usuario) =>
        ConsultarAsync(s => s.GetRequiredService<IClsMetricasRepository>().ListarCotizacionesAsync(
            [usuario],
            new DateTimeOffset(_desde.ToDateTime(TimeOnly.MinValue), _bogota),
            new DateTimeOffset(_hasta.AddDays(1).ToDateTime(TimeOnly.MinValue), _bogota),
            Ct));

    private static DateOnly FechaBogota(DateTimeOffset instante) => DateOnly.FromDateTime(instante.ToOffset(_bogota).DateTime);

    private static string Sql(decimal? valor) => valor is { } v ? v.ToString(CultureInfo.InvariantCulture) : "NULL";

    private static string Sql(bool? valor) => valor switch
    {
        true => "1",
        false => "0",
        null => "NULL",
    };
}
