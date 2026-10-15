using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/recalculo_vehiculos_ruta.feature</c> (HU-023.3, #158; E3 y E1 de #174): <c>POST .../rutas/{rutaId}/recalcular-vehiculos</c>
/// con y sin <c>If-Match</c>, la versión vieja, la solicitud ya en Nexo y una ruta ajena, con los camiones guardados y la auditoría.
/// </summary>
[Binding]
public sealed class clsPasosRecalculoVehiculos(clsPasosCosteo costeo, clsContextoWeb web)
{
    private static string Cadena => clsEntornoAceptacion.BaseDatos.CadenaCotifly;

    [When("{string} recalcula los camiones de la ruta {int} de {string} con el ETag vigente")]
    public async Task CuandoRecalculaConEtag(string alias, int orden, string cotizacion) =>
        await RecalcularAsync(alias, cotizacion, await RutaIdAsync(cotizacion, orden), await EtagAsync(alias, cotizacion));

    [When("{string} recalcula los camiones de la ruta {int} de {string} sin If-Match")]
    public async Task CuandoRecalculaSinIfMatch(string alias, int orden, string cotizacion) =>
        await RecalcularAsync(alias, cotizacion, await RutaIdAsync(cotizacion, orden), ifMatch: null);

    [When("{string} recalcula los camiones de la ruta {int} de {string} con una versión que ya no es la vigente")]
    public async Task CuandoRecalculaConVersionVieja(string alias, int orden, string cotizacion)
    {
        var version = await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            $"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{costeo.Folio(cotizacion)}'");
        await RecalcularAsync(alias, cotizacion, await RutaIdAsync(cotizacion, orden), $"\"{version + 7}\"");
    }

    [When("{string} recalcula los camiones de una ruta que no existe en {string} con el ETag vigente")]
    public async Task CuandoRecalculaRutaAjena(string alias, string cotizacion) =>
        await RecalcularAsync(alias, cotizacion, Guid.NewGuid().ToString(), await EtagAsync(alias, cotizacion));

    [Then("la ruta {int} de {string} tiene los camiones {string}")]
    public async Task EntoncesLosCamiones(int orden, string cotizacion, string camiones) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(ISNULL(v.tipoVehiculo, '-'), '|', CAST(v.pesoKg AS nvarchar(20))), ', ') WITHIN GROUP (ORDER BY v.pesoKg DESC) "
            + "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
            + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}"))
            .Should().Be(camiones);

    [Then("el recálculo de la ruta {int} de {string} queda en la auditoría como exitoso")]
    public async Task EntoncesAuditadoExitoso(int orden, string cotizacion)
    {
        var (exito, entrada, salida) = await EventoAsync(cotizacion);
        exito.Should().BeTrue();
        entrada.Should().Contain($"\"ruta_orden\":{orden}");
        salida.Should().Contain("\"camiones\":2");
        SinMontos(entrada, salida);
    }

    [Then("el recálculo de la ruta {int} de {string} queda en la auditoría como rechazado por {string}")]
    public async Task EntoncesAuditadoRechazado(int orden, string cotizacion, string codigo)
    {
        var (exito, entrada, salida) = await EventoAsync(cotizacion);
        exito.Should().BeFalse();
        entrada.Should().Contain($"\"ruta_orden\":{orden}");
        salida.Should().Contain(codigo);
        SinMontos(entrada, salida);
    }

    [Then("la cotización {string} sigue en el estado de costeo {string} sin llegar a Nexo")]
    public async Task EntoncesSinLlegarANexo(string cotizacion, string estado)
    {
        (await clsPasosCosteo.ColumnaDelFolio(costeo.Folio(cotizacion), "estadoCosteo")).Should().Be(estado);
        (await clsPasosCosteo.ColumnaDelFolio(costeo.Folio(cotizacion), "solicitudNexoId")).Should().BeNull();
    }

    private static void SinMontos(string entrada, string salida) =>
        (entrada + salida).Should().NotContain("peso").And.NotContain("costo").And.NotContain("precio");

    private async Task<(bool Exito, string Entrada, string Salida)> EventoAsync(string cotizacion)
    {
        var fila = await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT TOP (1) CONCAT(CAST(exito AS nvarchar(1)), CHAR(31), input, CHAR(31), outputResumen) FROM dbo_cotifly.EventosHerramienta "
            + $"WHERE herramienta = 'recalcular_vehiculos_ruta' AND input LIKE '%{costeo.Folio(cotizacion)}%' ORDER BY createdAt DESC");
        fila.Should().NotBeNull("el recálculo deja un evento en EventosHerramienta (E1)");
        var partes = fila!.Split('\u001f');
        return (partes[0] == "1", partes[1], partes[2]);
    }

    private async Task<string> RutaIdAsync(string cotizacion, int orden) =>
        await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT LOWER(CAST(l.id AS nvarchar(36))) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}")
        ?? throw new InvalidOperationException($"No existe la ruta {orden} de {cotizacion}.");

    private async Task<string> EtagAsync(string alias, string cotizacion)
    {
        await web.EnviarAsync(HttpMethod.Get, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/costeo", web.Sesion(alias).Access);
        return web.UltimoEtag ?? throw new InvalidOperationException("GET .../costeo no trajo ETag.");
    }

    private Task RecalcularAsync(string alias, string cotizacion, string rutaId, string? ifMatch) =>
        web.EnviarAsync(HttpMethod.Post, $"/web/cotizaciones/{costeo.Folio(cotizacion)}/rutas/{rutaId}/recalcular-vehiculos",
            web.Sesion(alias).Access, "{}", ifMatch);
}
