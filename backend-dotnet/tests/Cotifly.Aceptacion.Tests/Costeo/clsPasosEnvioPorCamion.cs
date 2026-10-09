using System.Globalization;
using System.Text.Json.Nodes;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Comun;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/envio_nexo_por_camion.feature</c> (HU-021.7, #134, ADR-019): una línea por camión hacia Nexo, el
/// emparejamiento de las líneas que devuelve Nexo con los camiones, la Mesa que pone vehículo, el camión descartado y los conexos en el
/// primer camión. Usa la API, la base y el Nexo falso de <see cref="clsPasosCosteo"/>. Mientras el cubicador (#133) no crea camiones, los
/// camiones extra de una ruta se agregan en la base.
/// </summary>
[Binding]
public sealed class clsPasosEnvioPorCamion(clsPasosCosteo costeo)
{
    private static string Cadena => clsEntornoAceptacion.BaseDatos.CadenaCotifly;

    [When("la ruta {int} de {string} lleva además estos camiones:")]
    public async Task CuandoLaRutaLlevaCamiones(int orden, string cotizacion, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var folio = costeo.Folio(cotizacion);
        var segundo = 0;
        foreach (var fila in tabla.Rows)
        {
            // Creados después del camión del formulario (createdAt posterior): el orden estable de envío los deja detrás de él.
            segundo++;
            await clsBaseDatosPruebas.EjecutarAsync(Cadena,
                "INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, tipoVehiculo, carroceria, pesoKg, costoLevanteContenedor, "
                + "estadoCosteo, createdAt, updatedAt) "
                + $"SELECT NEWID(), l.id, {Texto(fila["vehiculo"])}, {Texto(fila["carroceria"])}, {Decimal(fila["peso_kg"])}, ISNULL(l.valorLevanteContenedor, 0), "
                + $"(SELECT TOP 1 v.estadoCosteo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id), "
                + $"DATEADD(SECOND, {segundo}, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET() "
                + $"FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
                + $"WHERE c.folio = '{folio}' AND l.orden = {orden}");
        }
    }

    [When("Nexo devuelve las líneas de la solicitud en orden aleatorio")]
    public void CuandoNexoBaraja() => costeo.Nexo.SemillaOrdenLineas = 20261008;

    [When("Nexo costea la línea del camión de {int} kg de {string} con un flete de {int}")]
    public async Task CuandoNexoCosteaElCamion(int pesoKg, string cotizacion, int flete) =>
        costeo.Nexo.CostearLinea((await costeo.Columna(cotizacion, "costeoId"))!, IndiceDelCamion(pesoKg), flete);

    [When("Nexo costea la línea {int} de la solicitud de {string} con un flete de {int}")]
    public async Task CuandoNexoCosteaLaLinea(int linea, string cotizacion, int flete) =>
        costeo.Nexo.CostearLinea((await costeo.Columna(cotizacion, "costeoId"))!, linea - 1, flete);

    [When("la Mesa de Nexo le pone el vehículo {string} con la carrocería {string} a la línea del camión de {int} kg de {string}")]
    public async Task CuandoLaMesaPoneVehiculo(string vehiculo, string carroceria, int pesoKg, string cotizacion) =>
        costeo.Nexo.AsignarVehiculo((await costeo.Columna(cotizacion, "costeoId"))!, IndiceDelCamion(pesoKg), vehiculo, carroceria);

    [When("el camión de {int} kg de {string} queda descartado")]
    public Task CuandoElCamionQuedaDescartado(int pesoKg, string cotizacion) =>
        clsBaseDatosPruebas.EjecutarAsync(Cadena, $"UPDATE v SET estadoCosteo = 'descartada' {DelCamion(costeo.Folio(cotizacion), pesoKg)}");

    // --- Verificaciones ---

    [Then("Nexo recibió {int} líneas de {string} a {string}, una por camión y sin posiciones")]
    public void EntoncesNexoRecibioLineas(int cantidad, string origen, string destino)
    {
        var lineas = Lineas();
        lineas.Should().HaveCount(cantidad);
        lineas.Should().OnlyContain(l => l["origen"]!.GetValue<string>() == origen && l["destino"]!.GetValue<string>() == destino
                                         && l["posiciones"] == null && l["lineaNegocio"] != null);
    }

    [Then("las líneas que recibió Nexo llevan los pesos {string}")]
    public void EntoncesLosPesos(string pesos) =>
        Lineas().Select(l => l["peso"]!.GetValue<decimal>())
            .Should().BeEquivalentTo(pesos.Split(',').Select(p => decimal.Parse(p.Trim(), CultureInfo.InvariantCulture)));

    [Then("la nota que recibió Nexo menciona {string}")]
    public void EntoncesLaNota(string texto) => Alta()["notas"]!.GetValue<string>().Should().Contain(texto);

    [Then("ninguna de las líneas que recibió Nexo lleva vehículo ni carrocería")]
    public void EntoncesLineasSinVehiculo() =>
        Lineas().Should().OnlyContain(l => l["tipoVehiculo"] == null && l["carroceria"] == null,
            "decisión 7 (#132): el camión de la captura nace sin vehículo y la línea viaja igual para que la resuelva la Mesa");

    [Then("cada camión de {string} quedó asociado a una línea distinta de Nexo")]
    public async Task EntoncesCadaCamionAsociado(string cotizacion)
    {
        var folio = costeo.Folio(cotizacion);
        var sql = "SELECT CONCAT(COUNT(*), '|', COUNT(v.lineaSolicitudNexoId), '|', COUNT(DISTINCT v.lineaSolicitudNexoId)) "
                  + $"{DeLosCamiones(folio)} AND (v.estadoCosteo IS NULL OR v.estadoCosteo <> 'descartada')";
        var conteos = (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena, sql))!.Split('|');
        conteos[1].Should().Be(conteos[0], "todo camión vigente tiene línea de Nexo");
        conteos[2].Should().Be(conteos[0], "y ninguna línea está en dos camiones");
        var nexo = costeo.Nexo.LineasDe((await costeo.Columna(cotizacion, "costeoId"))!);
        nexo.Should().HaveCount(int.Parse(conteos[0], CultureInfo.InvariantCulture));
    }

    [Then("el camión de {int} kg de {string} tiene el flete {int} y el estado {string}")]
    public async Task EntoncesElCamion(int pesoKg, string cotizacion, int flete, string estado)
    {
        var valor = await CamionAsync(cotizacion, pesoKg, "CONCAT(CAST(v.costoFlete AS nvarchar(30)), '|', v.estadoCosteo)");
        var partes = valor!.Split('|');
        decimal.Parse(partes[0], CultureInfo.InvariantCulture).Should().Be(flete);
        partes[1].Should().Be(estado);
    }

    [Then("el camión de {int} kg de {string} tiene el vehículo {string} y la carrocería {string}")]
    public async Task EntoncesElVehiculo(int pesoKg, string cotizacion, string vehiculo, string carroceria) =>
        (await CamionAsync(cotizacion, pesoKg, "CONCAT(v.tipoVehiculo, '|', v.carroceria)")).Should().Be($"{vehiculo}|{carroceria}");

    [Then("el camión de {int} kg de {string} sigue descartado y sin línea de Nexo")]
    public async Task EntoncesSigueDescartado(int pesoKg, string cotizacion) =>
        (await CamionAsync(cotizacion, pesoKg, "CONCAT(v.estadoCosteo, '|', ISNULL(v.lineaSolicitudNexoId, '-'), '|', ISNULL(CAST(v.costoFlete AS nvarchar(30)), '-'))"))
            .Should().Be("descartada|-|-");

    [Then("Nexo recibió el conexo de {string} en la línea de la cotización de Nexo del primer camión de su ruta {int}")]
    public async Task EntoncesElConexoEnElPrimerCamion(string cotizacion, int orden)
    {
        var folio = costeo.Folio(cotizacion);
        var solicitud = (await costeo.Columna(cotizacion, "costeoId"))!;
        var primero = await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT TOP 1 v.lineaSolicitudNexoId FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND l.orden = {orden} ORDER BY v.createdAt, v.id");
        var indice = costeo.Nexo.LineasDe(solicitud).ToList().IndexOf(primero!);
        indice.Should().Be(0, "el primer camión de la ruta es la primera línea que viajó a Nexo");
        var peticion = costeo.Nexo.PeticionesConexos(costeo.Nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject;
        peticion["items"]!.AsArray().Should().ContainSingle().Subject!["lineaId"]!.GetValue<string>()
            .Should().Be(costeo.Nexo.LineaCotizacionDe(solicitud, indice), "decisión 3: los conexos de la ruta van pegados a su primer camión");
    }

    // --- Apoyo ---

    private JsonNode Alta() => costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");

    private List<JsonNode> Lineas() => [.. Alta()["lineas"]!.AsArray().Select(l => l!)];

    /// <summary>Índice (base 0, orden de captura) de la línea que viajó a Nexo con el camión de <paramref name="pesoKg"/>.</summary>
    private int IndiceDelCamion(int pesoKg)
    {
        var indice = Lineas().FindIndex(l => l["peso"]!.GetValue<decimal>() == pesoKg);
        return indice >= 0 ? indice : throw new InvalidOperationException($"Nexo no recibió un camión de {pesoKg} kg.");
    }

    private static string DeLosCamiones(string folio) =>
        "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
        + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'";

    private static string DelCamion(string folio, int pesoKg) =>
        $"{DeLosCamiones(folio)} AND v.pesoKg = {pesoKg.ToString(CultureInfo.InvariantCulture)}";

    private Task<string?> CamionAsync(string cotizacion, int pesoKg, string expresion) =>
        clsBaseDatosPruebas.EscalarAsync<string>(Cadena, $"SELECT {expresion} {DelCamion(costeo.Folio(cotizacion), pesoKg)}");

    private static string Texto(string valor) => string.IsNullOrWhiteSpace(valor) ? "NULL" : $"N'{valor.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Decimal(string valor) => decimal.Parse(valor, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
}
