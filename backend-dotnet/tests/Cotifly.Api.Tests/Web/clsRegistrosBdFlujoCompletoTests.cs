using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Data.SqlClient;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// SQL Server 2019 (Testcontainers) instalado DESDE CERO con <c>db/scripts/01_crear_esquema.sql</c> y los seeders de <c>db/seeder/</c>
/// (regla de las pruebas de esquema de HU-021.12, #139: el esquema vigente de la base compartida, no las migraciones 0001 a 0010).
/// </summary>
public sealed class clsEntornoApiWebInstalada : IAsyncLifetime
{
    public clsBaseDatosPruebas BaseDatos { get; } = new() { InstalacionDesdeCero = true };

    public ValueTask InitializeAsync() => BaseDatos.InitializeAsync();

    public ValueTask DisposeAsync() => BaseDatos.DisposeAsync();
}

/// <summary>Colección de las pruebas sobre la base instalada desde cero: un contenedor propio.</summary>
[CollectionDefinition(Nombre)]
public sealed class clsColeccionApiWebInstalada : ICollectionFixture<clsEntornoApiWebInstalada>
{
    public const string Nombre = "ApiWebInstalada";
}

/// <summary>
/// HU-021.12 (#139): un flujo completo por HTTP (formulario de dos rutas → cubicador falso → Nexo falso por camión → polling → margen de
/// la ruta 2 → PDF de pliego → correo por el relay falso → entrega) sobre la base instalada desde cero, verificando FILA POR FILA lo que
/// queda en <c>CotizacionesSolicitud</c>, <c>CotizacionLineasSolicitud</c>, <c>CotizacionVehiculosLineaSolicitud</c>,
/// <c>CotizacionMercancias</c>, <c>CotizacionServiciosConexos</c>, <c>MargenAjustes</c>, <c>EventosHerramienta</c> y <c>Correos</c>:
/// valores, FK, CHECK, totales guardados (§9.1 de CAMBIOS_BACKEND) y <c>versionNegocio</c>.
/// </summary>
[Collection(clsColeccionApiWebInstalada.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsRegistrosBdFlujoCompletoTests : IAsyncDisposable
{
    private const string TipoCafe = clsCosteoEndpointsTests.TipoMercancia;

    /// <summary>Código del catálogo del cubicador de "Café en estibas" (HU-023.3: la mercancía guarda el código, no el id).</summary>
    private const string CodigoCafe = "CAFE_EN_ESTIBAS";
    private const string Cargue = "SC-CARGUE-BD";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private static readonly HttpMethod _put = HttpMethod.Put;
    private readonly clsBaseDatosPruebas _bd;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, Cargue, "Cargue en puerto", "logistico", "UND", 150_000m);
    private readonly clsCubicadorFalso _cubicador = new();
    private readonly clsApiWebPruebas _api;

    public clsRegistrosBdFlujoCompletoTests(clsEntornoApiWebInstalada entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _bd = entorno.BaseDatos;
        _cubicador.TiposMercancia.Add((Guid.Parse(TipoCafe), "Café en estibas"));

        // Ruta 1 (28000 kg): dos camiones del cubicador; ruta 2: el cubicador no puede escoger (SUPERA_K_MAX) → un camión sin vehículo
        // con su peso, que resuelve la Mesa de Costeo (HU-023.3).
        _cubicador.Recomendar = p => clsCubicadorFalso.PesoTotal(p) == 28_000m
            ? clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 16_000m), clsCubicadorFalso.Vehiculo("2S3", "ESTACAS", 12_000m))
            : clsCubicadorFalso.SinSeleccion("SUPERA_K_MAX");
        _api = clsApiWebPruebas.ConNexo(_bd, _nexo, cubicador: _cubicador, extra: new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Cadena => _bd.CadenaCotifly;

    [Fact]
    public async Task LaBaseInstaladaDesdeCeroTieneLasTablasYLaConfiguracionSembrada()
    {
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly'")).Should().Be(23, "las 20 de HU-021 más CapturasIdempotentes del script 06 (#191) y las dos del 17 (HU-042: SapMargenCliente, SapMargenSyncLog)");
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena,
            "SELECT COUNT(*) FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id WHERE s.name = 'dbo_cotifly'")).Should().Be(0, "las vistas del borrador de métricas se retiraron (HU-014.3, #152)");
        (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena,
            "SELECT STRING_AGG(CONCAT(lineaNegocio, '=', ISNULL(mapeoLineasNexo, '-')), ',') WITHIN GROUP (ORDER BY lineaNegocio) FROM dbo_cotifly.MargenesPolitica"))
            .Should().Be("DevolucionContenedor=DevolucionContenedor,Distribucion=-,Exportacion=InteriorAPuerto,Importacion=PuertoAInterior,Internacional=Andino,Nacional=Nacional");
        // HU-037.1 (#209): comercial y gerente_comercial del seeder, más director del script 15.
        (await clsBaseDatosPruebas.EscalarAsync<int>(Cadena, "SELECT COUNT(*) FROM dbo_cotifly.Roles")).Should().Be(3);
    }

    [Fact]
    public async Task UnFlujoCompletoDejaCadaRegistroConSusValoresFkCheckTotalesYVersionNegocio()
    {
        var ana = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

        // ---------------------------------------------------------------- 1. Captura
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", ana.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var id = await Escalar<Guid>($"SELECT id FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");
        var solicitud = _nexo.SolicitudesCreadas[^1];

        (await Fila($"""
            SELECT CONCAT(nitCliente, '|', clienteNombre, '|', origenCliente, '|', tipoSolicitud, '|', moneda, '|', trmAplicada, '|', usuarioId, '|',
                estadoEnvio, '|', estadoCosteo, '|', solicitudNexoId, '|', ISNULL(cotizacionNexoId, '-'), '|', version, '|', ISNULL(CAST(precioFinal AS nvarchar(30)), '-'),
                '|', pdfDesactualizado, '|', IIF(costeoEnviadoAt IS NULL, '-', 'E'), '|', IIF(costeoSiguienteConsultaAt IS NULL, '-', 'A'), '|', costeoConsultasFallidas)
            FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'
            """)).Should().Be(
            $"{clsCosteoEndpointsTests.Nit}|LONGYIN S.A.S.|cliente|spot|COP|0.0000|{ana.UsuarioId.ToString().ToUpperInvariant()}|sin_enviar|pendiente_costeo|{solicitud}|-|1|-|0|E|A|0",
            "cabecera: cliente del catálogo de Nexo, COP sin TRM, sin enviar, pendiente de costeo, con la solicitud de Nexo y en la agenda del polling");
        var versionCaptura = await VersionNegocioAsync(id);

        // Rutas: orden, rutaOrigenIndice, línea de negocio (FK a MargenesPolitica), municipios id + nombre (del catálogo de Nexo, no el digitado), condiciones, levante y horas.
        (await Filas($"""
            SELECT CONCAT(l.orden, '|', l.rutaOrigenIndice, '|', m.lineaNegocio, '|', l.origenMunicipioId, '=', l.origenNombre, '|', l.destinoMunicipioId, '=', l.destinoNombre,
                '|', l.tipologia, '|', l.claseViaje, '|', l.claseContenedor, '|', l.exclusivo, '|', l.consolidado, '|', l.retornaContenedor, '|',
                ISNULL(CAST(l.ciudadDevolucionMunicipioId AS nvarchar(10)), '-'), '=', ISNULL(l.ciudadDevolucionNombre, '-'), '|', l.levanteContenedorDiferente, '|',
                ISNULL(CAST(l.ciudadLevanteMunicipioId AS nvarchar(10)), '-'), '=', ISNULL(l.ciudadLevanteNombre, '-'), '|',
                ISNULL(CAST(l.valorLevanteContenedor AS nvarchar(30)), '-'), '|', l.horasCargue, '|', l.horasDescargue, '|', l.pesoKg, '|', l.valorMercancia, '|', l.estadoCosteo)
            FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.MargenesPolitica m ON m.id = l.lineaNegocioId
            WHERE l.cotizacionSolicitudId = '{id}' ORDER BY l.orden
            """)).Should().Equal(
            "1|0|Importacion|76109=Buenaventura|11001=Bogotá, D.C.|Contenedor|Contenedor|CNT40.HC|1|0|1|76109=Buenaventura|1|76892=Yumbo|150000.00|4.00|6.00|28000.00|250000000.00|pendiente_costeo",
            "2|1|Exportacion|5001=Medellín|13001=Cartagena|Contenedor|Contenedor|CNT20.ST|1|0|0|-=-|0|-=-|-|4.00|6.00|12500.00|90000000.00|pendiente_costeo");

        // Camiones: uno por vehículo del cubicador (ruta 1) y uno sin vehículo (ruta 2, sin selección automática); el levante de la ruta se copia a cada camión (decisión 4).
        (await Filas($"""
            SELECT CONCAT(l.orden, '|', ISNULL(v.tipoVehiculo, '-'), '|', ISNULL(v.carroceria, '-'), '|', v.pesoKg, '|', IIF(v.idRecomendacionVehiculo IS NULL, '-', 'R'), '|',
                v.costoLevanteContenedor, '|', ISNULL(CAST(v.costoFlete AS nvarchar(30)), '-'), '|', IIF(v.lineaSolicitudNexoId IS NULL, '-', 'N'), '|', v.estadoCosteo)
            {DeLosCamiones(id)} ORDER BY l.orden, IIF(v.ordenVehiculo IS NULL, 1, 0), v.ordenVehiculo, v.createdAt, v.id
            """)).Should().Equal(
            "1|3S3|FURGON|16000.00|R|150000.00|-|-|pendiente_costeo",
            "1|2S3|ESTACAS|12000.00|R|150000.00|-|-|pendiente_costeo",
            "2|-|-|12500.00|-|0.00|-|-|pendiente_costeo");

        // Mercancías: cuelgan de la ruta, con el código del catálogo del cubicador (HU-023.3) y su nombre copiado.
        (await Filas($"""
            SELECT CONCAT(l.orden, '|', m.orden, '|', m.codigoMercancia, '|', m.nombreMercancia, '|', m.cantidad, '|', m.largoCm, 'x', m.anchoCm, 'x', m.altoCm, '|', m.pesoKg)
            FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId
            WHERE l.cotizacionSolicitudId = '{id}' ORDER BY l.orden, m.orden
            """)).Should().Equal(
            $"1|1|{CodigoCafe}|Café en estibas|20|120.00x100.00x150.00|1400.00",
            $"2|1|{CodigoCafe}|Café en estibas|10|120.00x100.00x150.00|1250.00");

        // Conexo capturado: en la ruta 1, con el id del catálogo de Nexo, su nombre y el costo unitario de Nexo como precio.
        (await Filas($"""
            SELECT CONCAT(l.orden, '|', s.idServicioConexo, '|', s.nombreServicioConexo, '|', s.precio, '|', s.cantidad)
            FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId
            WHERE l.cotizacionSolicitudId = '{id}'
            """)).Should().Equal($"1|{Cargue}|Cargue en puerto|150000.00|1.00");

        // ---------------------------------------------------------------- 2. Polling: la Mesa costea cada camión (líneas barajadas)
        var lineas = _nexo.UltimaAlta!["lineas"]!.AsArray().Select(l => l!).ToList();
        lineas.Should().HaveCount(3, "una línea por camión");
        _nexo.SemillaOrdenLineas = 20261009;
        _nexo.CostearLinea(solicitud, Indice(lineas, 16_000m), 1_000_000m, retorno: 100_000m);
        _nexo.CostearLinea(solicitud, Indice(lineas, 12_000m), 800_000m, retorno: 100_000m);
        _nexo.CostearLinea(solicitud, Indice(lineas, 12_500m), 600_000m);
        await _api.ProcesarPollingAsync(folio);

        (await Filas($"""
            SELECT CONCAT(l.orden, '|', v.pesoKg, '|', v.costoFlete, '|', ISNULL(CAST(v.costoRetornoContenedor AS nvarchar(30)), '-'), '|', v.costoLevanteContenedor, '|',
                IIF(v.lineaSolicitudNexoId IS NULL, '-', 'N'), '|', v.estadoCosteo)
            {DeLosCamiones(id)} ORDER BY l.orden, IIF(v.ordenVehiculo IS NULL, 1, 0), v.ordenVehiculo, v.createdAt, v.id
            """)).Should().Equal(
            "1|16000.00|1000000.00|100000.00|150000.00|N|autocosteo",
            "1|12000.00|800000.00|100000.00|150000.00|N|autocosteo",
            "2|12500.00|600000.00|0.00|0.00|N|autocosteo");
        (await Escalar<int>($"SELECT COUNT(DISTINCT v.lineaSolicitudNexoId) {DeLosCamiones(id)}")).Should().Be(3, "cada camión con su propia línea de Nexo");

        // Totales GUARDADOS de la ruta (§9.1): fletes + retorno + un levante por camión + conexos; el margen de política de la línea.
        (await Filas($"""
            SELECT CONCAT(l.orden, '|', l.costoFletesTotal, '|', l.costoRetornoContenedorTotal, '|', l.costoLevanteContenedorTotal, '|', l.costoServiciosConexos, '|',
                l.costoFinal, '|', l.margenAplicado, '|', l.origenMargen, '|', l.metodoCalculo, '|', l.precioFinalLinea, '|', l.estadoCosteo)
            FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = '{id}' ORDER BY l.orden
            """)).Should().Equal(
            "1|1800000.00|200000.00|300000.00|150000.00|2450000.00|0.1500|politica_linea|margen|2882353.00|autocosteo",
            "2|600000.00|0.00|0.00|0.00|600000.00|0.1500|politica_linea|margen|705882.00|autocosteo");
        await VerificarTotalesCuadranAsync(id);
        (await Fila($"SELECT CONCAT(precioFinal, '|', estadoCosteo, '|', IIF(costeoListoAt IS NULL, '-', 'L')) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'"))
            .Should().Be("3588235.00|autocosteo|L", "el precio de la cabecera es la suma de las rutas y el estado resume el de las rutas");

        // El conexo capturado viajó diferido a la línea del primer camión (decisión 10).
        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, Indice(lineas, 16_000m)));

        // ---------------------------------------------------------------- 3. Margen de la ruta 2 (If-Match)
        var etag = await EtagAsync(ana, folio);
        var versionAntesMargen = await VersionNegocioAsync(id);
        versionAntesMargen.Should().BeGreaterThanOrEqualTo(versionCaptura);
        etag.Should().Contain(versionAntesMargen.ToString(CultureInfo.InvariantCulture), "el ETag es la versionNegocio");
        using (var sinIfMatch = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access, """{"margen":0.30,"linea_orden":2}"""))
        {
            sinIfMatch.StatusCode.Should().Be((HttpStatusCode)428, "las mutaciones de la etapa final exigen If-Match");
        }

        using (var margen = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access,
            """{"margen":0.30,"linea_orden":2,"justificacion":"Cliente estratégico de la regional"}""", etag))
        {
            await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", _put, HttpStatusCode.OK);
        }

        (await VersionNegocioAsync(id)).Should().Be(versionAntesMargen + 1, "el ajuste sube la versionNegocio una vez");
        using (var viejo = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access, """{"margen":0.25,"linea_orden":2,"justificacion":"x"}""", etag))
        {
            viejo.StatusCode.Should().Be(HttpStatusCode.Conflict, "un If-Match viejo es un conflicto de concurrencia");
        }

        var ruta2 = await Escalar<Guid>($"SELECT id FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = '{id}' AND orden = 2");
        (await Filas($"""
            SELECT CONCAT(a.folio, '|', a.margenPolitica, '|', a.margenAplicado, '|', a.diferencia, '|', a.motivo, '|', a.usuarioId, '|', a.lineaSolicitudId)
            FROM dbo_cotifly.MargenAjustes a WHERE a.cotizacionId = '{id}'
            """)).Should().Equal(
            $"{folio}|0.1500|0.3000|0.1500|Cliente estratégico de la regional|{ana.UsuarioId.ToString().ToUpperInvariant()}|{ruta2.ToString().ToUpperInvariant()}");
        (await Filas($"""
            SELECT CONCAT(l.orden, '|', l.margenAplicado, '|', l.origenMargen, '|', l.precioFinalLinea) FROM dbo_cotifly.CotizacionLineasSolicitud l
            WHERE l.cotizacionSolicitudId = '{id}' ORDER BY l.orden
            """)).Should().Equal("1|0.1500|politica_linea|2882353.00", "2|0.3000|negociado|857143.00");
        (await Escalar<decimal>($"SELECT precioFinal FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'")).Should().Be(3_739_496m);
        await VerificarTotalesCuadranAsync(id);

        // ---------------------------------------------------------------- 4. PDF de pliego y correo
        await _api.FinalizarSiCorrespondeAsync(ana.Access, folio); // HU-036.3: como el frontend
        using (var pdf = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/pdf", ana.Access))
        {
            await VerificarContratoAsync(pdf, "/web/cotizaciones/{folio}/costeo/pdf", _post, HttpStatusCode.Created);
        }

        (await Fila($"SELECT CONCAT(IIF(pdfPath IS NULL, '-', 'P'), '|', pdfDesactualizado, '|', ISNULL(CAST(ISJSON(textosCotizacion) AS nvarchar(1)), '-')) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'"))
            .Should().Be("P|0|-", "el PDF queda vigente; sin redactar ni editar textos, el PDF usa los de respaldo y no los guarda");

        using (var correo = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/enviar", ana.Access,
            """{"para":["compras@longyin.com.co"],"cc":["gerencia@longyin.com.co"]}"""))
        {
            correo.StatusCode.Should().Be(HttpStatusCode.OK, await correo.Content.ReadAsStringAsync(Ct));
        }

        // HU-033 (#199): sale por MailSender (falso). Fuera de Production va al buzón de pruebas con [QA], sin CC ni CCO reales y con los
        // destinatarios originales en el cuerpo; el cuerpo trae el precio por ruta y el total, sin desglose.
        var recibido = _api.MailSender.Recibidos.Should().ContainSingle().Subject;
        recibido.To.Should().Equal(clsMailSenderFalso.BuzonPruebas);
        recibido.Cc.Should().BeEmpty();
        recibido.Bcc.Should().BeEmpty();
        recibido.Subject.Should().StartWith("[QA] Cotización ");
        recibido.Body.Should().Contain("compras@longyin.com.co").And.Contain("gerencia@longyin.com.co")
            .And.Contain(System.Net.WebUtility.HtmlEncode(Cotifly.Aplicacion.Pdf.Services.clsTextosEstandar.EtiquetaTotalGeneral));
        recibido.Body.Should().NotContainAny("Costo", "Margen", "margen", "flete");
        recibido.Adjuntos.Should().ContainSingle().Which.Nombre.Should().EndWith(".pdf");
        recibido.CorrelationId.Should().StartWith(folio + ":");
        (await Filas($"""
            SELECT CONCAT(c.estado, '|', IIF(c.idMailSender IS NULL, '-', 'I'), '|', c.confirmadoPor, '|', ISNULL(c.error, '-'), '|', IIF(c.asunto IS NULL OR c.asunto = '', '-', 'A'), '|',
                ISNULL(c.redirigidoA, '-'), '|', IIF(c.siguienteConsultaAt IS NULL, '-', 'S'), '|',
                (SELECT STRING_AGG(CONCAT(d.tipo, '=', d.email), ',') WITHIN GROUP (ORDER BY d.tipo DESC) FROM dbo_cotifly.CorreoDestinatarios d WHERE d.correoId = c.id))
            FROM dbo_cotifly.Correos c WHERE c.cotizacionId = '{id}'
            """)).Should().Equal(
            $"encolado|I|{ana.UsuarioId.ToString().ToUpperInvariant()}|-|A|{clsMailSenderFalso.BuzonPruebas}|S|para=compras@longyin.com.co,cc=gerencia@longyin.com.co");

        // El worker de correos consulta MailSender: entregado → "enviado", sin más consultas.
        _api.MailSender.Entregar(recibido.Id);
        (await _api.ProcesarCorreosAsync(folio)).Should().Be(1);
        (await Fila($"SELECT CONCAT(c.estado, '|', IIF(c.enviadoAt IS NULL, '-', 'E'), '|', IIF(c.siguienteConsultaAt IS NULL, '-', 'S'), '|', c.intentosEntrega) FROM dbo_cotifly.Correos c WHERE c.cotizacionId = '{id}'"))
            .Should().Be("enviado|E|-|1");
        (await Fila($"SELECT CONCAT(estadoEnvio, '|', IIF(marcadoAt IS NULL, '-', 'M')) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'"))
            .Should().StartWith("enviada|");

        // Ya enviada: otro ajuste de margen responde PDF_YA_ENVIADO y no toca nada.
        var versionEnviada = await VersionNegocioAsync(id);
        using (var tarde = await _api.EnviarAsync(_put, $"/web/cotizaciones/{folio}/costeo/margen", ana.Access,
            """{"margen":0.20,"linea_orden":1,"justificacion":"Después del envío"}""", await EtagAsync(ana, folio)))
        {
            tarde.StatusCode.Should().Be(HttpStatusCode.Conflict);
            await VerificarErrorNegocioAsync(tarde, "PDF_YA_ENVIADO");
        }

        (await VersionNegocioAsync(id)).Should().Be(versionEnviada);
        (await Escalar<int>($"SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE cotizacionId = '{id}'")).Should().Be(1);

        // ---------------------------------------------------------------- 5. Entrega de la Mesa
        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);
        (await Fila($"""
            SELECT CONCAT(c.estadoCosteo, '|', ISNULL(c.cotizacionNexoId, '-'), '|',
                (SELECT STRING_AGG(l.estadoCosteo, ',') WITHIN GROUP (ORDER BY l.orden) FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = c.id), '|',
                (SELECT COUNT(*) FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId
                 WHERE l.cotizacionSolicitudId = c.id AND v.estadoCosteo <> 'entregada'))
            FROM dbo_cotifly.CotizacionesSolicitud c WHERE c.id = '{id}'
            """)).Should().Be($"entregada|{_nexo.CotizacionDe(solicitud)}|entregada,entregada|0", "camión → ruta → cabecera");

        // ---------------------------------------------------------------- 6. EventosHerramienta: la traza de la cadena, sin montos en la entrada
        var eventos = await Filas($"""
            SELECT CONCAT(herramienta, '|', ISNULL(CAST(exito AS nvarchar(1)), '-'), '|', ISJSON(input), '|', ISNULL(CAST(ISJSON(outputResumen) AS nvarchar(1)), '-'))
            FROM dbo_cotifly.EventosHerramienta WHERE input LIKE '%{folio}%' OR outputResumen LIKE '%{folio}%' ORDER BY createdAt
            """);
        var herramientas = eventos.Select(e => e.Split('|')[0]).ToList();
        herramientas.Should().Contain(["enviar_costeo_nexo", "costeo_nexo_estado", "conexos_envio_nexo", "ajustar_margen_costeo", "generar_pdf_costeo", "enviar_correo"]);
        eventos.Where(e => e.StartsWith("enviar_costeo_nexo|", StringComparison.Ordinal) || e.StartsWith("generar_pdf_costeo|", StringComparison.Ordinal)
                || e.StartsWith("enviar_correo|", StringComparison.Ordinal))
            .Should().OnlyContain(e => e.Split('|')[1] == "1" && e.Split('|')[2] == "1");
        eventos.Where(e => e.StartsWith("ajustar_margen_costeo|", StringComparison.Ordinal)).Select(e => e.Split('|')[1])
            .Should().BeEquivalentTo(["1", "0", "0"], "el ajuste exitoso, el conflicto por If-Match viejo y el PDF_YA_ENVIADO quedan auditados");

        // ---------------------------------------------------------------- 7. FK y CHECK del esquema sobre estas filas
        var ruta1 = await Escalar<Guid>($"SELECT id FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionSolicitudId = '{id}' AND orden = 1");
        await RechazaAsync($"UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoEnvio = 'enviado_x' WHERE id = '{id}'", 547, "CHECK de estadoEnvio");
        await RechazaAsync($"UPDATE dbo_cotifly.CotizacionesSolicitud SET versionNegocio = -1 WHERE id = '{id}'", 547, "CHECK de versionNegocio");
        await RechazaAsync($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET estadoCosteo = 'costeada' WHERE id = '{ruta1}'", 547, "CHECK del estado de la ruta");
        await RechazaAsync($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET origenMargen = 'manual' WHERE id = '{ruta1}'", 547, "CHECK de origenMargen");
        await RechazaAsync($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET lineaNegocioId = 9999 WHERE id = '{ruta1}'", 547, "FK ruta → MargenesPolitica");
        await RechazaAsync($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET orden = 2 WHERE id = '{ruta1}'", 2627, "orden único por solicitud");
        await RechazaAsync($"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId) VALUES ('{Guid.NewGuid()}')", 547, "FK camión → ruta");
        await RechazaAsync($"UPDATE v SET v.costoFlete = -1 {DeLosCamiones(id)}", 547, "CHECK de montos del camión");
        await RechazaAsync($"INSERT INTO dbo_cotifly.CotizacionServiciosConexos (lineaSolicitudId, idServicioConexo, precio, cantidad) VALUES ('{ruta1}', '{Cargue}', 1, 1)",
            2627, "un conexo una vez por ruta");
        await RechazaAsync($"INSERT INTO dbo_cotifly.CotizacionServiciosConexos (lineaSolicitudId, idServicioConexo, precio, cantidad) VALUES ('{ruta1}', 'OTRO', 1, 0)",
            547, "CHECK cantidad > 0 del conexo");
        await RechazaAsync($"INSERT INTO dbo_cotifly.CotizacionMercancias (lineaSolicitudId, orden, largoCm, anchoCm, altoCm) VALUES ('{ruta1}', 9, 0, 1, 1)", 547,
            "CHECK de medidas de la mercancía");
        await RechazaAsync($"INSERT INTO dbo_cotifly.MargenAjustes (cotizacionId, lineaSolicitudId) VALUES ('{id}', '{Guid.NewGuid()}')", 547, "FK ajuste → ruta");
        await RechazaAsync($"INSERT INTO dbo_cotifly.Correos (cotizacionId) VALUES ('{Guid.NewGuid()}')", 547, "FK correo → cotización");
        await RechazaAsync($"INSERT INTO dbo_cotifly.EventosHerramienta (herramienta, input) VALUES ('x', 'no es json')", 547, "CHECK ISJSON de la auditoría");
        await RechazaAsync($"DELETE FROM dbo_cotifly.CotizacionLineasSolicitud WHERE id = '{ruta1}'", 547, "la ruta con camiones, mercancías y conexos no se borra suelta");
    }

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        _nexo.Dispose();
        _cubicador.Dispose();
    }

    /// <summary>
    /// Para cada ruta de la solicitud: los totales guardados cuadran con sus camiones y conexos (§9.1), el precio sale del costo final con
    /// el margen, y el precio de la cabecera es la suma de los de sus rutas.
    /// </summary>
    private async Task VerificarTotalesCuadranAsync(Guid id)
    {
        (await Escalar<int>($"""
            SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud l
            CROSS APPLY (SELECT SUM(v.costoFlete) AS fletes, SUM(ISNULL(v.costoRetornoContenedor, 0)) AS retorno, SUM(ISNULL(v.costoLevanteContenedor, 0)) AS levante
                         FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id) cam
            CROSS APPLY (SELECT ISNULL(SUM(s.precio * s.cantidad), 0) AS conexos FROM dbo_cotifly.CotizacionServiciosConexos s WHERE s.lineaSolicitudId = l.id) cx
            WHERE l.cotizacionSolicitudId = '{id}'
              AND (l.costoFletesTotal <> cam.fletes OR l.costoRetornoContenedorTotal <> cam.retorno OR l.costoLevanteContenedorTotal <> cam.levante
                   OR l.costoServiciosConexos <> cx.conexos
                   OR l.costoFinal <> l.costoFletesTotal + l.costoRetornoContenedorTotal + l.costoLevanteContenedorTotal + l.costoServiciosConexos
                   OR l.precioFinalLinea <> ROUND(l.costoFinal / (1 - l.margenAplicado), 0))
            """)).Should().Be(0, "los totales guardados de cada ruta cuadran con sus camiones y conexos");
        (await Escalar<int>($"""
            SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud c
            WHERE c.id = '{id}' AND c.precioFinal <> (SELECT SUM(l.precioFinalLinea) FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionSolicitudId = c.id)
            """)).Should().Be(0, "precioFinal = suma de precioFinalLinea");
    }

    private async Task RechazaAsync(string sql, int numero, string motivo)
    {
        var accion = async () => await clsBaseDatosPruebas.EjecutarAsync(Cadena, sql);
        (await accion.Should().ThrowAsync<SqlException>(motivo)).Which.Number.Should().Be(numero, motivo);
    }

    private async Task<int> VersionNegocioAsync(Guid id) => await Escalar<int>($"SELECT versionNegocio FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{id}'");

    private async Task<string> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        costeo.StatusCode.Should().Be(HttpStatusCode.OK);
        return costeo.Headers.ETag!.Tag;
    }

    private async Task<T> Escalar<T>(string sql) => (await clsBaseDatosPruebas.EscalarAsync<T>(Cadena, sql))!;

    private async Task<string> Fila(string sql) => (await clsBaseDatosPruebas.EscalarAsync<string>(Cadena, sql))!;

    private async Task<IReadOnlyList<string>> Filas(string sql)
    {
        var filas = new List<string>();
        await using var conexion = new SqlConnection(Cadena);
        await conexion.OpenAsync(Ct);
        await using var comando = new SqlCommand(sql, conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        while (await lector.ReadAsync(Ct))
        {
            filas.Add(lector.GetString(0));
        }

        return filas;
    }

    private static int Indice(IReadOnlyList<JsonNode> lineas, decimal peso) => lineas.ToList().FindIndex(l => l["peso"]!.GetValue<decimal>() == peso);

    private static string DeLosCamiones(Guid id) =>
        "FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId "
        + $"WHERE l.cotizacionSolicitudId = '{id}'";

    /// <summary>Dos rutas: la 1 (Importacion, 28000 kg) con retorno a Buenaventura, levante en Yumbo de 150.000 y un conexo; la 2 (Exportacion, 12500 kg).</summary>
    private static string Formulario() => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = clsCosteoEndpointsTests.Nit,
        cliente_nombre = "Longyin",
        linea_negocio = "Importacion",
        deadline = "2026-10-15T17:00:00-05:00",
        horas_cargue = 4,
        horas_descargue = 6,
        rutas = new object[]
        {
            new
            {
                linea_negocio = "Importacion",
                origen_municipio_id = 76109,
                origen = "Buenaventura",
                destino_municipio_id = 11001,
                destino = "Bogotá D.C.",
                tipologia = "Contenedor",
                clase_viaje = "Contenedor",
                clase_contenedor = "CNT40.HC",
                exclusivo = true,
                consolidado = false,
                retorna_contenedor = true,
                ciudad_devolucion_municipio_id = 76109,
                ciudad_devolucion_contenedor = "Buenaventura",
                levante_contenedor_diferente = true,
                ciudad_levante_municipio_id = 76892,
                ciudad_levante_contenedor = "Yumbo",
                valor_levante_contenedor = 150000,
                peso_kg = 28000,
                valor_mercancia = 250000000,
                mercancias = new object[] { new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 } },
                servicios_conexos = new object[] { new { id_servicio_conexo = Cargue, cantidad = 1 } },
            },
            new
            {
                linea_negocio = "Exportacion",
                origen_municipio_id = 5001,
                origen = "Medellín",
                destino_municipio_id = 13001,
                destino = "Cartagena",
                tipologia = "Contenedor",
                clase_viaje = "Contenedor",
                clase_contenedor = "CNT20.ST",
                exclusivo = true,
                consolidado = false,
                retorna_contenedor = false,
                levante_contenedor_diferente = false,
                peso_kg = 12500,
                valor_mercancia = 90000000,
                mercancias = new object[] { new { codigo_mercancia = CodigoCafe, nombre_mercancia = "Café verde", cantidad = 10, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1250 } },
            },
        },
    });
}
