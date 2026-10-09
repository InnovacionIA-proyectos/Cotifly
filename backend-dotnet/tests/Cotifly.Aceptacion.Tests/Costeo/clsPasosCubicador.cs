using System.Globalization;
using System.Net;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Aceptacion.Tests.Web;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/integracion_cubicador.feature</c> (HU-021.5, #133): la API real conectada al Nexo falso de
/// <see cref="clsPasosCosteo"/> y al cubicador falso (<see cref="clsCubicadorFalso"/>; el real aún no tiene <c>POST /recomendaciones</c>).
/// Las verificaciones del envío por camión y del emparejamiento son las de <see cref="clsPasosEnvioPorCamion"/>.
/// </summary>
[Binding]
public sealed class clsPasosCubicador(clsPasosCosteo costeo, clsContextoWeb web) : IDisposable
{
    private readonly clsCubicadorFalso _cubicador = new();

    private static string Cadena => clsEntornoAceptacion.BaseDatos.CadenaCotifly;

    [Given("la API web de Cotifly conectada a Nexo con el cliente {string} de BP {string} y al cubicador")]
    public void DadoLaApiConNexoYCubicador(string nit, string bp)
    {
        costeo.Nexo.ConCliente(nit, $"CLIENTE {nit} S.A.S.", "cliente", bp);
        web.Iniciar(clsApiWebPruebas.ConNexo(clsEntornoAceptacion.BaseDatos, costeo.Nexo, dolarApi: costeo.DolarApi,
            superfinanciera: costeo.Superfinanciera, modelo: costeo.Modelo, cubicador: _cubicador,
            extra: new Dictionary<string, string?>
            {
                ["IA:TiempoRedaccionSegundos"] = clsPasosCosteo.TiempoRedaccionSegundos.ToString(CultureInfo.InvariantCulture),
            }));
    }

    [Given("que el cubicador tiene el tipo de mercancía {string} de las mercancías del formulario")]
    public void DadoElTipoDeMercancia(string nombre) => _cubicador.TiposMercancia.Add((Guid.Parse(clsPasosCosteo.TipoMercancia), nombre));

    [Given("que el cubicador recomienda para cada ruta estos vehículos:")]
    public void DadoLaRecomendacion(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        _cubicador.Recomendar = _ => [.. tabla.Rows.Select(f => clsCubicadorFalso.Fila(
            Vacio(f["vehiculo"]), Vacio(f["carroceria"]), decimal.Parse(f["peso_kg"], CultureInfo.InvariantCulture), f["resultado"]))];
    }

    [Given("que el cubicador no responde")]
    public void DadoQueElCubicadorNoResponde() => _cubicador.Caido = HttpStatusCode.ServiceUnavailable;

    [Then("el cubicador recibió la ruta {int} de {string} con el origen {string} y su llave como Bearer")]
    public async Task EntoncesElCubicadorRecibio(int orden, string cotizacion, string origen)
    {
        var rutaId = await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT LOWER(CAST(l.id AS nvarchar(36))) FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}");
        var peticion = _cubicador.Recomendaciones.Should().ContainSingle(r => r["idLineaSolicitud"]!.GetValue<string>() == rutaId).Subject;
        peticion["origenSistema"]!.GetValue<string>().Should().Be(origen);
        peticion["mercancias"]!.AsArray().Should().NotBeEmpty();
        _cubicador.Peticiones.Should().OnlyContain(p => p.Autorizacion == "Bearer " + clsCubicadorFalso.ApiKey);
    }

    [Then("cada camión de la ruta {int} de {string} guarda el id de su recomendación del cubicador")]
    public async Task EntoncesCadaCamionGuardaLaRecomendacion(int orden, string cotizacion)
    {
        var conteos = (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            $"SELECT CONCAT(COUNT(*), '|', COUNT(v.idRecomendacionVehiculo), '|', COUNT(DISTINCT v.idRecomendacionVehiculo)) {DeLaRuta(cotizacion, orden)}"))!.Split('|');
        conteos[0].Should().NotBe("0");
        conteos[1].Should().Be(conteos[0], "C3: cada camión guarda el id exacto de la fila de la recomendación");
        conteos[2].Should().Be(conteos[0]);
    }

    [Then("las mercancías de {string} se guardaron con el nombre {string}")]
    public async Task EntoncesLasMercancias(string cotizacion, string nombre) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(DISTINCT_NOMBRE, ',') FROM (SELECT DISTINCT ISNULL(m.nombreMercancia, '-') AS DISTINCT_NOMBRE FROM dbo_cotifly.CotizacionMercancias m "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{costeo.Folio(cotizacion)}') x"))
            .Should().Be(nombre, "el nombre de cada mercancía se copia del catálogo del cubicador por su id_tipo_mercancia");

    [Then("la ruta {int} de {string} tiene un solo camión sin vehículo ni recomendación de {int} kg")]
    public async Task EntoncesUnSoloCamionSinVehiculo(int orden, string cotizacion, int pesoKg) =>
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(ISNULL(v.tipoVehiculo, '-'), '|', ISNULL(v.carroceria, '-'), '|', CAST(v.pesoKg AS nvarchar(20)), '|', "
            + $"IIF(v.idRecomendacionVehiculo IS NULL, '-', 'R')), ',') {DeLaRuta(cotizacion, orden)}"))
            .Should().Be($"-|-|{pesoKg.ToString(CultureInfo.InvariantCulture)}.00|-");

    public void Dispose() => _cubicador.Dispose();

    private string DeLaRuta(string cotizacion, int orden) =>
        "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
        + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{costeo.Folio(cotizacion)}' AND l.orden = {orden}";

    private static string? Vacio(string valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}
