using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Lectura del tablero de métricas (<see cref="IClsMetricasRepository.ListarCotizacionesAsync"/>) contra la base real.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsMetricasRepositoryTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    /// <summary>
    /// Se crea la <c>-v2</c> (vigente, todavía sin precio) y DESPUÉS se marca ganada la original. El tablero muestra la cadena con
    /// los datos de la vigente, pero el valor ganado y la ganancia salen de la versión ganada.
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

    /// <summary>
    /// Un ajuste de margen hecho en una ruta que la Mesa de Costeo rechazó después no cuenta en ninguna ruta (la
    /// ruta rechazada no sale): tampoco en la cabecera. Con todas las rutas rechazadas, un ajuste sin ruta tampoco (no hay ruta que lo lleve).
    /// </summary>
    [Fact]
    public async Task LosAjustesDeUnaRutaRechazadaNoCuentanNiEnLaCabeceraNiEnLasRutas()
    {
        var ana = await CrearUsuarioAsync();
        var cotizacion = NuevaCotizacion(ana);
        await GuardarAsync(cotizacion);
        var ruta = cotizacion.PrimeraRuta!.Id;
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, $"""
            INSERT INTO dbo_cotifly.MargenAjustes (id, cotizacionId, lineaSolicitudId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId, createdAt)
            VALUES (NEWID(), '{cotizacion.Id}', '{ruta}', '{cotizacion.Folio}', 0.15, 0.20, 0.05, N'En la ruta', '{ana}', SYSDATETIMEOFFSET()),
                   (NEWID(), '{cotizacion.Id}', NULL, '{cotizacion.Folio}', 0.15, 0.12, -0.03, N'Sin ruta', '{ana}', SYSDATETIMEOFFSET());
            """);

        var antes = await FilaAsync(ana);
        (antes.NumAjustes, antes.Rutas.Sum(r => r.NumAjustes)).Should().Be((2, 2), "sin rechazo, la cabecera y las rutas cuadran");

        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET estadoCosteo = N'rechazada', costoFinal = NULL, precioFinalLinea = NULL WHERE id = '{ruta}';");

        var despues = await FilaAsync(ana);
        despues.Rutas.Should().BeEmpty("la ruta rechazada no sale en las métricas");
        despues.NumAjustes.Should().Be(0, "la cabecera suma lo mismo que sus rutas");
    }

    private async Task<Cotifly.Dominio.Metricas.clsCotizacionMetrica> FilaAsync(Guid usuario) =>
        (await ConsultarAsync(s => s.GetRequiredService<IClsMetricasRepository>()
            .ListarCotizacionesAsync([usuario], Reloj.AhoraUtc.AddDays(-1), Reloj.AhoraUtc.AddDays(1), Ct))).Filas.Should().ContainSingle().Subject;
}
