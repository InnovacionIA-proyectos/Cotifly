using System.Globalization;
using System.Net;
using System.Text.Json;
using Cotifly.Aceptacion.Tests.Soporte;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Web;

/// <summary>
/// Pasos de <c>specs/features/metricas_tablero.feature</c> (HU-010.1, ADR-016): cotizaciones con valores del tablero fijados por
/// SQL, versiones nuevas, consultas a <c>/web/metricas/comercial</c> y <c>/regional</c> y comprobación de los indicadores.
/// Reutiliza la sesión, las regionales y la última respuesta de <see cref="clsContextoWeb"/>.
/// </summary>
[Binding]
public sealed class clsPasosMetricas(clsContextoWeb web)
{
    private static readonly HttpMethod _get = HttpMethod.Get;

    private static string CadenaCotifly => clsEntornoAceptacion.BaseDatos.CadenaCotifly;

    [Given("el gerente comercial {string} con sesión y sin regional")]
    public async Task DadoElGerenteSinRegional(string alias) =>
        web.Sesiones[alias] = await web.Api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoGerente, regional: null, $"Persona {alias}");

    [Given("que {string} tiene estas cotizaciones:")]
    public async Task DadoLasCotizaciones(string alias, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var usuarioId = web.Sesion(alias).UsuarioId;
        foreach (var fila in tabla.Rows)
        {
            var cotizacion = await web.Api.CrearCotizacionAsync(usuarioId, conPdf: false);
            web.Cotizaciones[fila["cotizacion"]] = cotizacion;
            await clsBaseDatosPruebas.EjecutarAsync(CadenaCotifly, $"""
                UPDATE dbo_cotifly.CotizacionesSolicitud
                SET moneda = N'{fila["moneda"]}', trmAplicada = {Numero(fila["trm"]) ?? "0"}, precioFinal = {Numero(fila["precio"]) ?? "NULL"},
                    ganada = {Resultado(fila["resultado"])}, createdAt = '{Creada(fila["creada"])}'
                WHERE id = '{cotizacion.Id}';
                UPDATE dbo_cotifly.CotizacionLineasSolicitud
                SET costoFinal = {Numero(fila["costo"]) ?? "NULL"}, margenAplicado = {Numero(fila["margen"]) ?? "NULL"}
                WHERE cotizacionSolicitudId = '{cotizacion.Id}';
                """);
        }
    }

    [Given(@"^que ""([^""]+)"" rehízo la cotización ""([^""]+)"" como versión (\d+) con precio (\d+) y costo (\d+)$")]
    public Task DadoLaNuevaVersion(string alias, string cotizacion, int version, string precio, string costo)
    {
        web.Sesion(alias).Should().NotBeNull();
        var original = web.Cotizacion(cotizacion);
        return clsBaseDatosPruebas.EjecutarAsync(CadenaCotifly, $"""
            DECLARE @nueva UNIQUEIDENTIFIER = NEWID();
            INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio, usuarioId, precioFinal, moneda, trmAplicada, estadoEnvio, ganada, version, createdAt, updatedAt)
            SELECT @nueva, CONCAT(folio, N'-v{version}'), usuarioId, {precio}, moneda, trmAplicada, N'sin_enviar', NULL, {version},
                DATEADD(DAY, 1, createdAt), SYSDATETIMEOFFSET()
            FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{original.Id}';
            INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, lineaNegocioId, costoFinal, margenPoliticaSugerido, margenAplicado,
                metodoCalculo, precioFinalLinea)
            SELECT @nueva, orden, lineaNegocioId, {costo}, margenPoliticaSugerido, margenAplicado, metodoCalculo, {precio}
            FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = '{original.Id}';
            """);
    }

    /// <summary>
    /// HU-021.10: reemplaza los valores de la ruta 1 y agrega las demás rutas con su línea de negocio (por nombre); la cabecera queda con la
    /// suma de los precios, como la deja el dominio. HU-016.1 (#154): con la columna <c>politica</c>, el <c>margenPoliticaSugerido</c> de
    /// cada ruta (vacía = <c>NULL</c>); sin ella, la ruta 1 conserva el suyo y las demás quedan sin política.
    /// </summary>
    [Given("que la cotización {string} tiene estas rutas:")]
    public async Task DadoLasRutas(string alias, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var cotizacion = web.Cotizacion(alias);
        var sentencias = new System.Text.StringBuilder($"DECLARE @id UNIQUEIDENTIFIER = '{cotizacion.Id}';\n");
        foreach (var fila in tabla.Rows)
        {
            var linea = $"(SELECT id FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = N'{fila["linea"]}')";
            var (costo, precio, margen) = (Numero(fila["costo"]) ?? "NULL", Numero(fila["precio"]) ?? "NULL", Numero(fila["margen"]) ?? "NULL");
            var conPolitica = tabla.Header.Contains("politica");
            var politica = conPolitica ? Numero(fila["politica"]) ?? "NULL" : "NULL";
            sentencias.AppendLine(fila["orden"] == "1"
                ? $"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET lineaNegocioId = {linea}, costoFinal = {costo}, precioFinalLinea = {precio}, margenAplicado = {margen}{(conPolitica ? $", margenPoliticaSugerido = {politica}" : string.Empty)} WHERE cotizacionSolicitudId = @id AND orden = 1;"
                : $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionSolicitudId, orden, lineaNegocioId, costoFinal, precioFinalLinea, margenAplicado, margenPoliticaSugerido) VALUES (@id, {int.Parse(fila["orden"], CultureInfo.InvariantCulture)}, {linea}, {costo}, {precio}, {margen}, {politica});");
        }

        sentencias.AppendLine("UPDATE dbo_cotifly.CotizacionesSolicitud SET precioFinal = (SELECT SUM(precioFinalLinea) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = @id) WHERE id = @id;");
        await clsBaseDatosPruebas.EjecutarAsync(CadenaCotifly, sentencias.ToString());
    }

    /// <summary>
    /// HU-034.1 (#201): la Mesa de Costeo rechaza una ruta (HU-028.1, #183): queda sin precio y la cabecera suma solo las rutas no
    /// rechazadas, como la deja el dominio. El costo de la ruta se conserva para comprobar que el tablero no lo cuenta.
    /// </summary>
    [Given("que la Mesa de Costeo rechazó la ruta {int} de la cotización {string}")]
    public Task DadoLaRutaRechazada(int orden, string alias) =>
        clsBaseDatosPruebas.EjecutarAsync(CadenaCotifly, $"""
            DECLARE @id UNIQUEIDENTIFIER = '{web.Cotizacion(alias).Id}';
            UPDATE dbo_cotifly.CotizacionLineasSolicitud SET estadoCosteo = N'rechazada', precioFinalLinea = NULL
            WHERE cotizacionSolicitudId = @id AND orden = {orden};
            UPDATE dbo_cotifly.CotizacionesSolicitud
            SET precioFinal = (SELECT SUM(precioFinalLinea) FROM dbo_cotifly.CotizacionLineasSolicitud
                               WHERE cotizacionSolicitudId = @id AND (estadoCosteo IS NULL OR estadoCosteo <> N'rechazada'))
            WHERE id = @id;
            """);

    [When("{string} consulta sus métricas del {string} al {string}")]
    public Task CuandoConsultaSusMetricas(string alias, string desde, string hasta) =>
        web.EnviarAsync(_get, $"/web/metricas/comercial?desde={desde}&hasta={hasta}", web.Sesion(alias).Access);

    [When("{string} consulta las métricas de su regional del {string} al {string}")]
    public Task CuandoConsultaLaRegional(string alias, string desde, string hasta) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?desde={desde}&hasta={hasta}", web.Sesion(alias).Access);

    [When("{string} consulta las métricas de su regional del {string} al {string} filtradas por {string}")]
    public Task CuandoConsultaLaRegionalFiltrada(string alias, string desde, string hasta, string comercial) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?desde={desde}&hasta={hasta}&usuario_id={web.Sesion(comercial).UsuarioId}",
            web.Sesion(alias).Access);

    [When("{string} consulta las métricas de su regional del año {string}")]
    public Task CuandoConsultaLaRegionalDelAnio(string alias, string anio) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?anio={anio}", web.Sesion(alias).Access);

    [When("{string} consulta las métricas de su regional del mes {string} de {string}")]
    public Task CuandoConsultaLaRegionalDelMes(string alias, string mes, string anio) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?anio={anio}&mes={mes}", web.Sesion(alias).Access);

    [When("{string} consulta las métricas de su regional con {string}")]
    public Task CuandoConsultaLaRegionalCon(string alias, string consulta) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?{consulta}", web.Sesion(alias).Access);

    [When("{string} consulta sus métricas con {string}")]
    public Task CuandoConsultaSusMetricasCon(string alias, string consulta) =>
        web.EnviarAsync(_get, $"/web/metricas/comercial?{consulta}", web.Sesion(alias).Access);

    [Then("el periodo de las métricas va del {string} al {string}")]
    public void EntoncesElPeriodo(string desde, string hasta)
    {
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var periodo = web.UltimoJson().GetProperty("periodo");
        (periodo.GetProperty("desde").GetString(), periodo.GetProperty("hasta").GetString()).Should().Be((desde, hasta));
        periodo.GetProperty("zona_horaria").GetString().Should().Be("America/Bogota");
    }

    [Then("los indicadores son:")]
    public void EntoncesLosIndicadores(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var indicadores = web.UltimoJson().GetProperty("indicadores");
        ComprobarIndicadores(indicadores, tabla);
    }

    /// <summary>
    /// HU-016.1 (#154, validación): los <c>indicadores</c> de un comercial en <c>comerciales[]</c> de <c>/web/metricas/regional</c>, con la
    /// misma tabla <c>campo | valor</c> de "los indicadores son".
    /// </summary>
    [Then("los indicadores de {string} en la lista de comerciales son:")]
    public void EntoncesLosIndicadoresDelComercial(string alias, DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var id = web.Sesion(alias).UsuarioId.ToString();
        var indicadores = web.UltimoJson().GetProperty("comerciales").EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("usuario_id").GetString(), id, StringComparison.OrdinalIgnoreCase))
            .GetProperty("indicadores");
        ComprobarIndicadores(indicadores, tabla);
    }

    /// <summary>Cada fila <c>campo | valor</c> contra <paramref name="indicadores"/> (<c>null</c> = JSON null).</summary>
    private static void ComprobarIndicadores(JsonElement indicadores, DataTable tabla)
    {
        foreach (var fila in tabla.Rows)
        {
            var valor = indicadores.GetProperty(fila["campo"]);
            if (fila["valor"] == "null")
            {
                valor.ValueKind.Should().Be(JsonValueKind.Null, fila["campo"]);
            }
            else
            {
                valor.GetDecimal().Should().Be(decimal.Parse(fila["valor"], CultureInfo.InvariantCulture), fila["campo"]);
            }
        }
    }

    /// <summary>Cada fila de la tabla es una línea de <c>por_linea_negocio</c>; una celda vacía no se comprueba.</summary>
    [Then("la apertura por línea de negocio es:")]
    public void EntoncesLaApertura(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        var apertura = web.UltimoJson().GetProperty("por_linea_negocio").EnumerateArray()
            .ToDictionary(l => l.GetProperty("linea_negocio").GetString() ?? string.Empty, StringComparer.Ordinal);
        foreach (var fila in tabla.Rows)
        {
            apertura.Should().ContainKey(fila["linea"]);
            var linea = apertura[fila["linea"]];
            foreach (var campo in tabla.Header.Where(c => c != "linea" && !string.IsNullOrWhiteSpace(fila[c])))
            {
                linea.GetProperty(campo).GetDecimal().Should().Be(decimal.Parse(fila[campo], CultureInfo.InvariantCulture), $"{fila["linea"]}.{campo}");
            }
        }
    }

    [Then("los indicadores cumplen cantidad = ganadas + perdidas + sin_resultado + pendientes_por_cotizar")]
    public void EntoncesLaParticion()
    {
        var i = web.UltimoJson().GetProperty("indicadores");
        (i.GetProperty("ganadas").GetInt32() + i.GetProperty("perdidas").GetInt32() + i.GetProperty("sin_resultado").GetInt32()
            + i.GetProperty("pendientes_por_cotizar").GetInt32()).Should().Be(i.GetProperty("cantidad").GetInt32());
    }

    [Then("en la lista de comerciales {string} tiene el rol {string} y {string} el rol {string}")]
    public void EntoncesLosRoles(string uno, string rolUno, string otro, string rolOtro)
    {
        var comerciales = web.UltimoJson().GetProperty("comerciales").EnumerateArray()
            .ToDictionary(c => c.GetProperty("usuario_id").GetString()!, c => c.GetProperty("rol").GetString());
        comerciales[web.Sesion(uno).UsuarioId.ToString()].Should().Be(rolUno);
        comerciales[web.Sesion(otro).UsuarioId.ToString()].Should().Be(rolOtro);
    }

    [Then("la serie mensual tiene el mes {string} con {int} cotizaciones y {int} ganadas")]
    public void EntoncesLaSerie(string mes, int cantidad, int ganadas)
    {
        var punto = web.UltimoJson().GetProperty("serie_mensual").EnumerateArray().Single(p => p.GetProperty("mes").GetString() == mes);
        punto.GetProperty("cantidad").GetInt32().Should().Be(cantidad);
        punto.GetProperty("ganadas").GetInt32().Should().Be(ganadas);
    }

    /// <summary>HU-034.1 (#201): cada fila es un mes de <c>serie_mensual</c>; una celda vacía no se comprueba.</summary>
    [Then("la serie mensual tiene estos montos:")]
    public void EntoncesLosMontosDeLaSerie(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK, web.UltimoCuerpo);
        var serie = web.UltimoJson().GetProperty("serie_mensual").EnumerateArray()
            .ToDictionary(p => p.GetProperty("mes").GetString() ?? string.Empty, StringComparer.Ordinal);
        foreach (var fila in tabla.Rows)
        {
            serie.Should().ContainKey(fila["mes"]);
            var punto = serie[fila["mes"]];
            foreach (var campo in tabla.Header.Where(c => c != "mes" && !string.IsNullOrWhiteSpace(fila[c])))
            {
                punto.GetProperty(campo).GetDecimal().Should().Be(decimal.Parse(fila[campo], CultureInfo.InvariantCulture), $"{fila["mes"]}.{campo}");
            }
        }
    }

    /// <summary>HU-034.1 (#201): la suma de los meses de la serie es exactamente el valor de los indicadores del periodo.</summary>
    [Then("la suma de la serie mensual cuadra con los indicadores")]
    public void EntoncesLaSerieCuadra()
    {
        var json = web.UltimoJson();
        var serie = json.GetProperty("serie_mensual").EnumerateArray().ToList();
        var indicadores = json.GetProperty("indicadores");
        foreach (var campo in new[] { "cantidad", "ganadas", "perdidas", "valor_cotizado_cop", "valor_ganado_cop", "costeo_total", "costeo_ganadas", "ganancias" })
        {
            serie.Sum(p => p.GetProperty(campo).GetDecimal()).Should().Be(indicadores.GetProperty(campo).GetDecimal(), campo);
        }
    }

    /// <summary>HU-037.3 (#211): <c>?regional=</c> con el nombre real de la regional (alias del escenario).</summary>
    [When("{string} consulta las métricas de la regional {string} del {string} al {string}")]
    public Task CuandoConsultaUnaRegional(string alias, string regional, string desde, string hasta) =>
        web.EnviarAsync(_get, $"/web/metricas/regional?desde={desde}&hasta={hasta}&regional={Uri.EscapeDataString(web.Regional(regional))}",
            web.Sesion(alias).Access);

    [Then("las métricas son de todas las regionales")]
    public void EntoncesTodas()
    {
        web.UltimoJson().GetProperty("regional").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        web.UltimoJson().GetProperty("fuente_regional").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Then("la lista de comerciales de las métricas incluye a {string}, {string} y {string}")]
    public void EntoncesIncluye(string uno, string dos, string tres) =>
        web.UltimoJson().GetProperty("comerciales").EnumerateArray().Select(c => c.GetProperty("usuario_id").GetString())
            .Should().Contain([.. new[] { uno, dos, tres }.Select(a => web.Sesion(a).UsuarioId.ToString())]);

    [Then("la regional de las métricas es {string}")]
    public void EntoncesLaRegional(string alias) => web.UltimoJson().GetProperty("regional").GetString().Should().Be(web.Regional(alias));

    [Then("los comerciales de las métricas son {string}, {string} y {string}")]
    public void EntoncesLosComerciales(string uno, string dos, string tres) =>
        web.UltimoJson().GetProperty("comerciales").EnumerateArray().Select(c => c.GetProperty("usuario_id").GetString())
            .Should().BeEquivalentTo([.. new[] { uno, dos, tres }.Select(a => web.Sesion(a).UsuarioId.ToString())]);

    /// <summary>Una fecha sola es el mediodía en Bogotá; con hora (<c>2026-04-30T23:30:00-05:00</c>) va tal cual (límites de mes, HU-010.5).</summary>
    private static string Creada(string texto) =>
        texto.Contains('T', StringComparison.Ordinal) ? texto : $"{texto}T12:00:00-05:00";

    private static string? Numero(string texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : clsApiWebPruebas.Numero(decimal.Parse(texto, CultureInfo.InvariantCulture));

    private static string Resultado(string texto) => texto switch
    {
        "ganada" => "1",
        "perdida" => "0",
        _ => "NULL",
    };
}
