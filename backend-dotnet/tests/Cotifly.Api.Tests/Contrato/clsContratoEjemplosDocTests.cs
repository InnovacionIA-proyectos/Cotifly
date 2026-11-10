using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Cotifly.Pruebas.Contrato;

namespace Cotifly.Api.Tests.Contrato;

/// <summary>
/// HU-021.11 (#138): los ejemplos JSON de <c>docs/docs_front/CONTRATO_API_WEB.md</c> (sección 8, la guía del frontend) cumplen
/// <c>specs/openapi.yaml</c>. Cada bloque <c>```json</c> precedido de <c>&lt;!-- esquema: Nombre --&gt;</c> se valida contra ese esquema
/// de <c>components/schemas</c>, y uno precedido de <c>&lt;!-- respuesta: METODO /ruta ESTADO --&gt;</c>, contra la respuesta de esa
/// operación. Así un ejemplo que deja de cumplir el contrato rompe las pruebas en vez de confundir al frontend.
/// </summary>
public sealed partial class clsContratoEjemplosDocTests
{
    private static readonly string _rutaGuia = Path.Combine(AppContext.BaseDirectory, "docs", "CONTRATO_API_WEB.md");

    public static TheoryData<string, string> Ejemplos()
    {
        var datos = new TheoryData<string, string>();
        foreach (var (marca, json) in Bloques())
        {
            datos.Add(marca, json);
        }

        return datos;
    }

    private static List<(string Marca, string Json)> Bloques() =>
        [.. BloqueMarcado().Matches(File.ReadAllText(_rutaGuia))
            .Select(m => ($"{m.Groups["tipo"].Value}: {m.Groups["destino"].Value.Trim()}", m.Groups["json"].Value))];

    [Fact]
    public void LaGuiaTraeLosEjemplosDeLosEndpointsCambiados()
    {
        var marcas = Bloques().Select(b => b.Marca).ToList();

        marcas.Should().Contain([
            "esquema: SolicitudCosteoWeb",
            "respuesta: POST /web/solicitudes-costeo 201",
            "respuesta: GET /web/cotizaciones/{folio}/costeo 200",
            "respuesta: PUT /web/cotizaciones/{folio}/costeo/margen 400",
            "respuesta: POST /web/cotizaciones/{folio}/seguimiento 409",
            "respuesta: GET /web/tipos-mercancia 200",
            "respuesta: GET /web/mis-cotizaciones 200",
            "respuesta: GET /web/cotizaciones/{id}/nueva-version 200",
            // HU-016.1 (#154): listado regional del gerente comercial.
            "respuesta: GET /web/cotizaciones/regional 200",
            // HU-037.3 (#211): regionales del selector del director y del gerente.
            "respuesta: GET /web/regionales 200",
            "esquema: LicitacionWeb",
            // HU-023.2 (#157): "Calcular cubicaje" contra el cubicador.
            "esquema: CubicacionRequest",
            "respuesta: POST /web/cubicacion 200",
            "respuesta: POST /web/cubicacion 400",
            "respuesta: POST /web/cubicacion 503",
            // Opción D (E2E 2026-10-16, D3): "Calcular cubicaje" asíncrono.
            "respuesta: POST /web/cubicacion 202",
            "respuesta: GET /web/cubicacion/{id} 200",
            "respuesta: GET /web/cubicacion/{id} 404",
            // HU-028 (#186): editar la solicitud.
            "respuesta: GET /web/cotizaciones/{folio}/solicitud 200",
            "esquema: EdicionSolicitudWeb",
            "respuesta: PUT /web/cotizaciones/{folio}/solicitud 200",
            "respuesta: PUT /web/cotizaciones/{folio}/solicitud 400",
            // HU-027.1 (#179): detalle por folio.
            "respuesta: GET /web/cotizaciones/{folio} 200",
            "respuesta: PUT /web/cotizaciones/{folio}/solicitud 503",
            // HU-030.1 (#184): peso con tara de las cargas contenerizadas.
            "esquema: RutaCapturaWeb",
            "esquema: CamionCosteo",
            // HU-035.1 (#202): Excel de mercancías de una ruta, leído en Cotifly sin Nexo.
            "respuesta: POST /web/mercancias/analizar-excel 200",
            "respuesta: POST /web/mercancias/analizar-excel 400",
            // HU-016.1 (#154, parte B): desplegable de líneas de negocio activas, sin margen.
            "respuesta: GET /web/lineas-negocio 200",
            // HU-041.1 (#231): margen y conexos de varias rutas en una operación.
            "esquema: MargenLineasWeb",
            "esquema: ConexoLineasWeb",
            "esquema: QuitarConexoLineasWeb",
            "respuesta: PUT /web/cotizaciones/{folio}/costeo/margen/lineas 409",
            // HU-040.2 (#230): selector de rol del login multi-rol.
            "esquema: EleccionRolRequerida",
            "esquema: ElegirRolRequest",
            // HU-044.2 (#253): sugerir-mapeo y leer-estructura con la importación inteligente de NEXO.
            "esquema: SugerirMapeoWeb",
            "respuesta: POST /web/licitaciones/sugerir-mapeo 200",
            "respuesta: POST /web/licitaciones/leer-estructura 200",
            "respuesta: POST /web/licitaciones/leer-estructura 400",
        ]);
    }

    [Theory]
    [MemberData(nameof(Ejemplos))]
    public async Task CadaEjemploDeLaGuiaCumpleElContrato(string marca, string json)
    {
        var (tipo, destino) = (marca[..marca.IndexOf(':', StringComparison.Ordinal)], marca[(marca.IndexOf(':', StringComparison.Ordinal) + 2)..]);
        Microsoft.OpenApi.IOpenApiSchema esquema;
        if (tipo == "esquema")
        {
            esquema = await clsValidadorEsquemaOpenApi.EsquemaAsync(destino);
        }
        else
        {
            var partes = destino.Split(' ');
            esquema = await clsValidadorEsquemaOpenApi.EsquemaRespuestaAsync(
                partes[1], new HttpMethod(partes[0]), (HttpStatusCode)int.Parse(partes[2], CultureInfo.InvariantCulture));
        }

        clsValidadorEsquemaOpenApi.Validar(json, esquema).Should().BeEmpty($"el ejemplo «{marca}» de CONTRATO_API_WEB.md debe cumplir openapi.yaml");
    }

    [GeneratedRegex(@"<!-- (?<tipo>esquema|respuesta): (?<destino>[^>]+?) -->\s*```json\s*(?<json>.*?)```", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex BloqueMarcado();
}
