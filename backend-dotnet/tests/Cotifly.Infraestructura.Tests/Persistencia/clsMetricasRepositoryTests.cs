using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Lectura del tablero de métricas (<see cref="IClsMetricasRepository.ListarCotizacionesAsync"/>) contra la base real. Las pruebas de
/// las vistas del borrador (<c>vActividadPorComercial</c>, <c>vAjustesMargenPorComercial</c>) se retiraron con ellas (HU-014.3, #152).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMetricasRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    /// <summary>
    /// B3 (#173): se crea la <c>-v2</c> (vigente, todavía sin precio) y DESPUÉS se marca ganada la original. El tablero muestra la cadena con
    /// los datos de la vigente, pero el valor ganado y la ganancia salen de la versión ganada (antes daban 0).
    /// </summary>
    [Fact]
    public async Task ElValorGanadoSaleDeLaVersionGanadaAunqueLaVigenteSeaLaV2SinPrecio()
    {
        var ana = await CrearUsuarioAsync();
        var folio = $"PCN-B3-{Guid.NewGuid():N}"[..30];
        var original = NuevaCotizacion(ana, folio, 2_000_000m);
        await GuardarAsync(original);
        var v2 = NuevaCotizacion(ana, folio + "-v2", 999m);
        await GuardarAsync(v2);
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            UPDATE dbo_cotifly.CotizacionesSolicitud SET version = 2, precioFinal = NULL WHERE id = '{v2.Id}';
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET costoFinal = NULL, precioFinalLinea = NULL WHERE cotizacionSolicitudId = '{v2.Id}';
            UPDATE dbo_cotifly.CotizacionesSolicitud SET ganada = 1, marcadoAt = SYSDATETIMEOFFSET() WHERE id = '{original.Id}';
            """);

        var datos = await ConsultarAsync(s => s.GetRequiredService<IClsMetricasRepository>()
            .ListarCotizacionesAsync([ana], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1), Ct));

        var fila = datos.Filas.Should().ContainSingle("una fila por cadena de versiones").Subject;
        fila.Versiones.Should().Be(2);
        fila.PrecioFinal.Should().BeNull("los datos de la fila son los de la vigente (-v2)");
        fila.Ganada.Should().BeTrue();
        var indicadores = Cotifly.Dominio.Metricas.clsCalculadoraMetricas.Indicadores(datos.Filas, Reloj.AhoraUtc, deadlineDisponible: false);
        indicadores.ValorGanadoCop.Should().Be(2_000_000m, "el valor ganado es el de la versión ganada, no el de la vigente");
        indicadores.CosteoGanadas.Should().Be(1_275_000m);
        indicadores.Ganancias.Should().Be(725_000m);
        fila.RutasResultado.Should().ContainSingle("las rutas de la versión ganada (la v1) vienen aparte");
        Cotifly.Dominio.Metricas.clsCalculadoraMetricas.PorLineaNegocio(datos.Filas).Sum(l => l.ValorGanadoCop)
            .Should().Be(2_000_000m, "la apertura por línea cuadra con el total");
    }
}
