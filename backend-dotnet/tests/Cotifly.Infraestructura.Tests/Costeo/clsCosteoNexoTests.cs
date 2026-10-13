using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// <see cref="clsCosteoNexo"/> contra un <see cref="HttpMessageHandler"/> falso (<see cref="clsNexoClientesFalso"/>): alta de la
/// solicitud (HU-005.5). Sin la tubería de resiliencia (esa se prueba en <c>clsRegistroCosteoNexoTests</c>); nunca sale a la red.
/// </summary>
public sealed class clsCosteoNexoTests : IDisposable
{
    private const long IdUsuarioExterno = 1007;

    private static readonly Guid _usuarioId = Guid.NewGuid();

    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsCosteoNexoTests()
    {
        _http = new HttpClient(_nexo);
        UsuarioResuelto(UsuarioConIdExterno(IdUsuarioExterno));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsSolicitudNexo Solicitud() => new("15927", clsSolicitudCosteoMuestra.Crear(), "PuertoAInterior");

    private const string Creada =
        """{"id":"3f2b8c1e-0000-4000-8000-000000000001","lineas":1,"rutasCreadas":0,"vehiculosCreados":1,"scorePrioridad":0.73,"fletesCalculados":0,"lineasPorCostear":1,"costeoEnProceso":false}""";

    [Fact]
    public async Task ElAltaVaAApiCapturaSolicitudesConElIdExternoComoIdPortal()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);

        await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        var peticion = _nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/captura-solicitudes", "Nexo__UrlBase es la raíz, sin /api (ADR-015 §8)");
        peticion.IdPortalUsuario.Should().Be("1007");
        peticion.Accept.Should().Be("application/json");
    }

    [Fact]
    public async Task ElCuerpoEsCapturaSolicitudRequestEnCamelCaseConUnaLineaYSinNulos()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);

        await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        var raiz = cuerpo.RootElement;
        raiz.GetProperty("clienteBp").GetString().Should().Be("15927");
        raiz.GetProperty("tipo").GetString().Should().Be("spot", "Nexo valida el tipo en minúscula");
        raiz.GetProperty("deadline").GetDateTimeOffset().Should().Be(clsSolicitudCosteoMuestra.Deadline);
        raiz.TryGetProperty("valorEstimado", out _).Should().BeFalse("decisión 1 de HU-021: valor estimado y referencia dejan de enviarse a Nexo");
        raiz.TryGetProperty("referencia", out _).Should().BeFalse();
        raiz.GetProperty("horasDescargue").GetDecimal().Should().Be(6.5m);
        raiz.TryGetProperty("clienteId", out _).Should().BeFalse("los nulos no viajan");
        raiz.TryGetProperty("modalidad", out _).Should().BeFalse("la modalidad no aplica en Cotifly (mapeo aprobado)");

        var linea = raiz.GetProperty("lineas").EnumerateArray().Should().ContainSingle().Subject;
        linea.GetProperty("origen").GetString().Should().Be("Buenaventura");
        linea.GetProperty("destino").GetString().Should().Be("Bogotá D.C.");
        linea.GetProperty("tipoVehiculo").GetString().Should().Be("3S3");
        linea.GetProperty("carroceria").GetString().Should().Be("PORTACONTENEDORES");
        linea.GetProperty("lineaNegocio").GetString().Should().Be("PuertoAInterior");
        linea.GetProperty("claseContenedor").GetString().Should().Be("CNT40.HC");
        linea.GetProperty("exclusivo").GetBoolean().Should().BeTrue();
        linea.GetProperty("retornaContenedor").GetBoolean().Should().BeTrue();
        linea.GetProperty("peso").GetDecimal().Should().Be(28_500.5m, "Nexo llama 'peso' al peso de la línea en kg");
        linea.GetProperty("valorMercancia").GetDecimal().Should().Be(250_000_000m);
        linea.TryGetProperty("valorAprovisionamiento", out _).Should().BeFalse();

        var posiciones = linea.GetProperty("posiciones").EnumerateArray().ToList();
        posiciones.Should().HaveCount(2);
        posiciones[0].GetProperty("descripcion").GetString().Should().Be("Estiba de café");
        posiciones[0].GetProperty("cantidad").GetInt32().Should().Be(20);
        posiciones[0].GetProperty("largoCm").GetDecimal().Should().Be(120m);
        posiciones[0].GetProperty("pesoKg").GetDecimal().Should().Be(1425m);
        posiciones[1].TryGetProperty("descripcion", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SinPosicionesNoMandaElArreglo()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var solicitud = new clsSolicitudNexo("15927", clsSolicitudCosteoMuestra.Crear() with { Posiciones = [] }, "PuertoAInterior");

        await Adaptador().EnviarSolicitudAsync(_usuarioId, solicitud, Ct);

        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        cuerpo.RootElement.GetProperty("lineas")[0].TryGetProperty("posiciones", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Un201DevuelveElIdYSiNexoYaCosteoLaLinea()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada.Replace("\"fletesCalculados\":0,\"lineasPorCostear\":1", "\"fletesCalculados\":1,\"lineasPorCostear\":0", StringComparison.Ordinal));

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Valor.Id.Should().Be("3f2b8c1e-0000-4000-8000-000000000001");
        resultado.Valor.FletesCalculados.Should().Be(1);
        resultado.Valor.CosteoCompleto.Should().BeTrue();
    }

    [Fact]
    public async Task ConLineasPorCostearOCosteoEnProcesoNoEstaCompleto()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        _nexo.Responder(HttpStatusCode.Created, Creada.Replace("\"lineasPorCostear\":1,\"costeoEnProceso\":false", "\"lineasPorCostear\":0,\"costeoEnProceso\":true", StringComparison.Ordinal));

        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Valor.CosteoCompleto.Should().BeFalse();
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Valor.CosteoCompleto.Should().BeFalse();
    }

    [Theory]
    [InlineData("ENUM_INVALIDO", clsCodigosError.SolicitudCosteoInvalida)]
    [InlineData("LINEA_INCOMPLETA", clsCodigosError.SolicitudCosteoInvalida)]
    [InlineData("PESO_REQUERIDO", clsCodigosError.SolicitudCosteoInvalida)]
    [InlineData("PESO_INCONSISTENTE", clsCodigosError.SolicitudCosteoInvalida)]
    [InlineData("CLIENTE_REQUERIDO", clsCodigosError.CosteoNexoRechazada)]
    [InlineData("CLIENTE_INVALIDO", clsCodigosError.CosteoNexoRechazada)]
    [InlineData("CLIENTE_BP_INVALIDO", clsCodigosError.CosteoNexoRechazada)]
    [InlineData("DWCUBOS_NO_CONFIGURADO", clsCodigosError.CosteoNexoNoDisponible)]
    [InlineData("VALIDATION_ERROR", clsCodigosError.CosteoNexoSolicitudInvalida)]
    public async Task Un400DelAltaSeTraduceAlCodigoDeCotiflyConservandoElDeNexo(string codigoNexo, string esperado)
    {
        _nexo.Responder(HttpStatusCode.BadRequest, $$"""{"code":"{{codigoNexo}}","message":"No coincide."}""");

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(esperado);
        resultado.Error.Detalle.Should().Be($"HTTP 400 {codigoNexo}: No coincide.");
    }

    [Theory]
    [InlineData("HTTP 400 ENUM_INVALIDO: x", "ENUM_INVALIDO")]
    [InlineData("HTTP 400 CLIENTE_BP_INVALIDO", "CLIENTE_BP_INVALIDO")]
    [InlineData("HTTP 400: texto libre", null)]
    [InlineData("HTTP 400", null)]
    public void ElCodigoDeNexoSaleDelDetalle(string detalle, string? codigo) =>
        clsCosteoNexo.CodigoNexo(detalle).Should().Be(codigo);

    [Fact]
    public async Task ElCuerpoLlevaNotasMunicipiosYHorasPorLinea()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var formulario = clsSolicitudCosteoMuestra.Crear() with { Notas = "  Cliente pide cita previa  ", OrigenMunicipioId = 76109, DestinoMunicipioId = 11001 };

        await Adaptador().EnviarSolicitudAsync(_usuarioId, new clsSolicitudNexo("15927", formulario, "PuertoAInterior"), Ct);

        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        var raiz = cuerpo.RootElement;
        raiz.GetProperty("notas").GetString().Should().Be("Cliente pide cita previa");
        raiz.GetProperty("horasCargue").GetDecimal().Should().Be(4m);
        var linea = raiz.GetProperty("lineas")[0];
        linea.GetProperty("origenMunicipioId").GetInt32().Should().Be(76109);
        linea.GetProperty("destinoMunicipioId").GetInt32().Should().Be(11001);
        linea.GetProperty("horasCargue").GetDecimal().Should().Be(4m, "sin ellas Nexo aplica a la línea su valor por defecto");
        linea.GetProperty("horasDescargue").GetDecimal().Should().Be(6.5m);
        raiz.GetProperty("deadline").GetString().Should().Be("2026-10-15T17:00:00-05:00", "ISO 8601 con el desplazamiento de Bogotá");
    }

    [Fact]
    public async Task UnProspectoViajaPorClienteIdSinClienteBp()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var id = Guid.NewGuid();
        var formulario = clsSolicitudCosteoMuestra.Crear();

        await Adaptador().EnviarSolicitudAsync(_usuarioId, new clsSolicitudNexo(clsClienteNexo.PorId(id), formulario.Encabezado, [formulario.ALinea()], null), Ct);

        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        cuerpo.RootElement.GetProperty("clienteId").GetGuid().Should().Be(id);
        cuerpo.RootElement.TryGetProperty("clienteBp", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "SESION_INVALIDA", "Identidad no válida.", clsCodigosError.CosteoNexoNoDisponible)]
    [InlineData(HttpStatusCode.Forbidden, "SIN_PERMISO_CABINA_FLETES", "Sin permiso.", clsCodigosError.NexoSinPermiso)]
    public async Task Un401EsConfiguracionNoDisponibleYUn403EsSinPermisoConUnaLineaDeError(HttpStatusCode estado, string codigo, string mensaje, string esperado)
    {
        var logger = new clsLoggerFalso<clsCosteoNexo>();
        _nexo.Responder(estado, JsonSerializer.Serialize(new { code = codigo, message = mensaje, timestamp = "2026-09-28T15:00:00Z" }));

        var resultado = await Adaptador(logger: logger).EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(esperado, "un 403 del alta es el rol del comercial en Nexo, no una caída (ADR-017 §4)");
        resultado.Error.Detalle.Should().Be($"HTTP {(int)estado} {codigo}: {mensaje}");
        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Error && r.Mensaje.Contains("X-Portal-clsUsuario-Id"));
    }

    [Fact]
    public async Task Otro4xxEsRechazadaConElCodigoDeNexo()
    {
        _nexo.Responder(HttpStatusCode.UnprocessableEntity, """{"code":"X","message":"y"}""");

        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoRechazada);
    }

    [Fact]
    public async Task Un4xxSinCuerpoJsonUsaElTexto()
    {
        _nexo.Responder(HttpStatusCode.NotFound, "no encontrado");

        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("HTTP 404: no encontrado");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Un5xxEsNoDisponible(HttpStatusCode estado)
    {
        _nexo.Responder(estado);

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        resultado.Error.Detalle.Should().StartWith($"HTTP {(int)estado}");
    }

    [Fact]
    public async Task UnTiempoAgotadoAvisaQueLaSolicitudPudoQuedarCreada()
    {
        _nexo.Fallar(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var resultado = await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoTiempoAgotado);
        resultado.Error.Mensaje.Should().Contain("pudo quedar creada");
    }

    [Fact]
    public async Task UnErrorDeRedOUnJsonInvalidoEsNoDisponible()
    {
        _nexo.Fallar(new HttpRequestException("Connection refused"));
        _nexo.Responder(HttpStatusCode.Created, "no es json");
        _nexo.Responder(HttpStatusCode.Created, """{"lineas":1}""");

        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("error de red");
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("JSON inválido");
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Be("respuesta sin id");
    }

    [Fact]
    public async Task SinUrlBaseOSinIdExternoFallaSinLlamarANexo()
    {
        (await Adaptador(url: null).EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        UsuarioResuelto(UsuarioConIdExterno(null));
        (await Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct)).Error.Detalle.Should().Contain("idUsuarioExterno");

        _nexo.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task UnFalloDejaUnaSolaLineaDeWarningSinTraza()
    {
        var logger = new clsLoggerFalso<clsCosteoNexo>();
        _nexo.Fallar(new HttpRequestException("Connection refused"));

        await Adaptador(logger: logger).EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        logger.Registros.Should().ContainSingle(r => r.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && r.Excepcion == null);
        logger.Registros.Should().NotContain(r => r.Mensaje.Contains("1007", StringComparison.Ordinal), "el id del portal no va al log");
    }

    [Fact]
    public async Task LaCancelacionDelLlamadorSePropaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();

        var enviar = () => Adaptador().EnviarSolicitudAsync(_usuarioId, Solicitud(), cancelacion.Token);

        await enviar.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void EstaConfiguradoSoloConUrlBase()
    {
        Adaptador(url: null).EstaConfigurado.Should().BeFalse();
        Adaptador(url: "  ").EstaConfigurado.Should().BeFalse();
        Adaptador().EstaConfigurado.Should().BeTrue();
    }

    [Fact]
    public async Task UnaUrlBaseConBarraFinalNoDuplicaLaBarra()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);

        await Adaptador(url: clsNexoClientesFalso.UrlBase + "/").EnviarSolicitudAsync(_usuarioId, Solicitud(), Ct);

        _nexo.Peticiones.Single().Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/captura-solicitudes");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaRutaConTresCamionesViajaComoTresLineasYElGrupoRutaIdSoloConLaOpcion(bool enviarReferencia)
    {
        // HU-021.7 (#134): una línea por camión con los datos de su ruta y de su camión, sin posiciones (a Nexo le basta el peso). El id de
        // la ruta solo viaja como grupoRutaId con Nexo:EnviarGrupoRutaId (HU-023.4: Nexo QA lo tiene, pero sin desplegar).
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var cotizacion = Dominio.Cotizaciones.clsCotizacionSolicitud.Crear(new Dominio.Cotizaciones.clsDatosCotizacion { Folio = "PCN-CAM-1" }).Valor;
        var ruta = cotizacion.RegistrarSolicitudCosteo(clsSolicitudCosteoMuestra.Crear(), 1);
        ruta.AgregarVehiculo("2S2", null, 8_000m);
        ruta.AgregarVehiculo(null, null, 4_000m);
        var solicitud = new clsSolicitudNexo(clsClienteNexo.PorBp("15927"), cotizacion.EncabezadoSolicitud!, [.. cotizacion.LineasNexo().Select(l => l.Linea)], "PuertoAInterior");

        await Adaptador(enviarGrupoRutaId: enviarReferencia).EnviarSolicitudAsync(_usuarioId, solicitud, Ct);

        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        var lineas = cuerpo.RootElement.GetProperty("lineas").EnumerateArray().ToList();
        lineas.Should().HaveCount(3);
        lineas.Select(l => l.GetProperty("peso").GetDecimal()).Should().BeEquivalentTo(cotizacion.Vehiculos.Select(v => v.PesoKg!.Value));
        lineas.Should().OnlyContain(l => l.GetProperty("origen").GetString() == "Buenaventura" && l.GetProperty("destino").GetString() == "Bogotá D.C."
                                         && l.GetProperty("horasCargue").GetDecimal() == 4m);
        lineas.Count(l => l.TryGetProperty("posiciones", out _)).Should().Be(0, "a Nexo le basta el peso de cada camión");
        lineas.Count(l => !l.TryGetProperty("tipoVehiculo", out _)).Should().Be(1, "el camión sin vehículo viaja vacío a la Mesa");
        lineas.Select(l => l.TryGetProperty("grupoRutaId", out var r) ? r.GetString() : null)
            .Should().OnlyContain(r => enviarReferencia ? r == ruta.Id.ToString("D") : r == null);
        lineas.Count(l => l.TryGetProperty("referenciaRuta", out _)).Should().Be(0, "el nombre viejo ya no viaja");
    }

    [Fact]
    public void LaLineaDeLaSolicitudAceptaElNombreDelSwaggerYLosDatosDelCubicador()
    {
        // El swagger de Nexo QA (2026-10-08) publica la ruta como "clsRuta"; Cotifly lee los dos nombres. HU-023.4: clsLineaCosteoDto trae
        // grupoRutaId e idRecomendacionVehiculo.
        using var documento = JsonDocument.Parse(
            """{"lineaId":"L1","rutaId":"b2b8c1e0-0000-4000-8000-000000000009","clsRuta":"BUENAVENTURA → BOGOTA","tipologia":"Contenedor","peso":10000,"tipoVehiculo":"3S3","grupoRutaId":"c3c8c1e0-0000-4000-8000-000000000001","idRecomendacionVehiculo":"d4d8c1e0-0000-4000-8000-000000000002"}""");

        var linea = clsCosteoNexo.LeerLineaSolicitud(documento.RootElement);

        linea.Should().Be(new Dominio.Cotizaciones.clsLineaSolicitudNexo("L1", "BUENAVENTURA → BOGOTA", "Contenedor", 10_000m, "3S3", null)
        {
            GrupoRutaId = "c3c8c1e0-0000-4000-8000-000000000001",
            IdRecomendacionVehiculo = "d4d8c1e0-0000-4000-8000-000000000002",
        }, "rutaId es del catálogo de rutas de Nexo, no el grupo de Cotifly");
    }

    [Fact]
    public async Task ConLasBanderasApagadasElPayloadNoLlevaLosCamposNuevos()
    {
        // HU-023.4 (#159): con Nexo:EnviarGrupoRutaId y Nexo:EnviarDatosCubicador apagadas (por defecto) el alta es la de antes.
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var (solicitud, _) = SolicitudCubicada();

        await Adaptador().EnviarSolicitudAsync(_usuarioId, solicitud, Ct);

        var cuerpo = _nexo.Peticiones.Single().Cuerpo!;
        cuerpo.Should().NotContain("grupoRutaId").And.NotContain("idRecomendacionVehiculo").And.NotContain("codigoTipoMercancia")
            .And.NotContain("referenciaRuta");
        using var json = JsonDocument.Parse(cuerpo);
        var linea = json.RootElement.GetProperty("lineas").EnumerateArray().Single();
        linea.GetProperty("tipoVehiculo").GetString().Should().Be("3S3", "los códigos del cubicador viajan sin bandera (decisión 7)");
        linea.GetProperty("carroceria").GetString().Should().Be("FURGON");
        linea.GetProperty("peso").GetDecimal().Should().Be(28_500.5m, "un solo camión: el peso declarado de la ruta, no el del cubicador");
    }

    [Fact]
    public async Task ConLasBanderasEncendidasViajanGrupoRutaIdIdRecomendacionYCodigoTipoMercancia()
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var (solicitud, ruta) = SolicitudCubicada();

        await Adaptador(enviarGrupoRutaId: true, enviarDatosCubicador: true).EnviarSolicitudAsync(_usuarioId, solicitud, Ct);

        using var json = JsonDocument.Parse(_nexo.Peticiones.Single().Cuerpo!);
        var linea = json.RootElement.GetProperty("lineas").EnumerateArray().Single();
        linea.GetProperty("grupoRutaId").GetString().Should().Be(ruta.Id.ToString("D"));
        linea.GetProperty("idRecomendacionVehiculo").GetString().Should().Be(_recomendacion.ToString("D"));
        linea.GetProperty("idRecomendacionVehiculo").GetString()!.Length.Should().BeLessThanOrEqualTo(64);
        linea.GetProperty("posiciones").EnumerateArray().Select(p => p.TryGetProperty("codigoTipoMercancia", out var c) ? c.GetString() : null)
            .Should().Equal("CAFE_EN_ESTIBAS", null);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CadaBanderaControlaSusCampos(bool grupo, bool cubicador)
    {
        _nexo.Responder(HttpStatusCode.Created, Creada);
        var (solicitud, _) = SolicitudCubicada();

        await Adaptador(enviarGrupoRutaId: grupo, enviarDatosCubicador: cubicador).EnviarSolicitudAsync(_usuarioId, solicitud, Ct);

        var cuerpo = _nexo.Peticiones.Single().Cuerpo!;
        cuerpo.Contains("\"grupoRutaId\"", StringComparison.Ordinal).Should().Be(grupo);
        cuerpo.Contains("\"idRecomendacionVehiculo\"", StringComparison.Ordinal).Should().Be(cubicador);
        cuerpo.Contains("\"codigoTipoMercancia\"", StringComparison.Ordinal).Should().Be(cubicador);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" CAFE ", "CAFE")]
    [InlineData("1234567890123456789012345678901234567890", "1234567890123456789012345678901234567890")]
    [InlineData("12345678901234567890123456789012345678901", null)]
    public void ElCodigoTipoMercanciaRespetaLos40DeNexo(string? codigo, string? esperado) =>
        clsCosteoNexo.CodigoTipoMercancia(codigo).Should().Be(esperado, "recortarlo mandaría otro código");

    [Fact]
    public async Task Un429EnElGetEsNoDisponibleConElRetryAfterYNuncaRechazada()
    {
        // HU-023.4 (#159): el cupo de Nexo (por rol) agotado no es un rechazo de los datos.
        using var respuesta = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"code":"DEMASIADAS_SOLICITUDES","message":"Ha alcanzado el límite."}""", System.Text.Encoding.UTF8, "application/json"),
        };
        respuesta.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        using var nexo = new clsManejadorFijo(respuesta);
        using var http = new HttpClient(nexo);
        var adaptador = new clsCosteoNexo(http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase }), _usuarios,
            NullLogger<clsCosteoNexo>.Instance);

        var resultado = await adaptador.ConsultarCosteoAsync(_usuarioId, "S-1", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible).And.NotBe(clsCodigosError.CosteoNexoRechazada);
        resultado.Error.Detalle.Should().Contain("HTTP 429 DEMASIADAS_SOLICITUDES").And.Contain("Retry-After 30 s");
    }

    [Fact]
    public void ElConexoDeLaCotizacionTraeSuLineaDeCotizacion()
    {
        using var documento = JsonDocument.Parse(
            """{"id":"COT","lineas":[],"conexos":[{"id":"CX-1","cotizacionLineaId":"LC-7","nombre":"Escolta","categoria":"logistico","cantidad":1,"valorAplicado":50000}]}""");

        clsCosteoNexo.LeerCotizacion(documento.RootElement)!.Conexos.Should().ContainSingle().Which.CotizacionLineaId.Should().Be("LC-7");
    }

    private void UsuarioResuelto(clsUsuarioWeb? usuario) =>
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>()).Returns(usuario);

    private static clsUsuarioWeb UsuarioConIdExterno(long? idUsuarioExterno) =>
        clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, idUsuarioExterno).Valor;

    private clsCosteoNexo Adaptador(
        string? url = clsNexoClientesFalso.UrlBase, Microsoft.Extensions.Logging.ILogger<clsCosteoNexo>? logger = null, bool enviarGrupoRutaId = false,
        bool enviarDatosCubicador = false) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = url, EnviarGrupoRutaId = enviarGrupoRutaId, EnviarDatosCubicador = enviarDatosCubicador }),
            _usuarios, logger ?? NullLogger<clsCosteoNexo>.Instance);

    private static readonly Guid _recomendacion = Guid.Parse("e5e8c1e0-0000-4000-8000-000000000005");

    /// <summary>
    /// Ruta de un camión del cubicador (3S3 FURGON, 26 t, más de 1 % lejos de las posiciones) capturada con códigos de mercancía (el
    /// segundo, de 41 caracteres, no cabe en Nexo), y la solicitud de Nexo que se arma con ella.
    /// </summary>
    private static (clsSolicitudNexo Solicitud, Dominio.Cotizaciones.clsLineaSolicitud Ruta) SolicitudCubicada()
    {
        var cotizacion = Dominio.Cotizaciones.clsCotizacionSolicitud.Crear(new Dominio.Cotizaciones.clsDatosCotizacion { Folio = "PCN-CUB-9" }).Valor;
        var linea = clsSolicitudCosteoMuestra.RutaCaptura();
        linea = linea with { Posiciones = [linea.Posiciones[0], linea.Posiciones[1] with { CodigoMercancia = new string('X', 41) }] };
        cotizacion.RegistrarCaptura(new Dominio.Cotizaciones.clsEncabezadoSolicitudCosteo { Tipo = Dominio.Cotizaciones.TipoSolicitud.Spot },
            [new Dominio.Cotizaciones.clsRutaCapturada(linea, 1, 0, [new Dominio.Cotizaciones.clsCamionPlaneado("3S3", "FURGON", 26_000m, _recomendacion)], [])]);
        var solicitud = new clsSolicitudNexo(clsClienteNexo.PorBp("15927"), cotizacion.EncabezadoSolicitud!,
            [.. cotizacion.LineasNexo().Select(l => l.Linea)], "PuertoAInterior");
        return (solicitud, cotizacion.RutasOrdenadas[0]);
    }

    /// <summary>Manejador que responde siempre la misma respuesta (para cabeceras como <c>Retry-After</c>).</summary>
    private sealed class clsManejadorFijo(HttpResponseMessage respuesta) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respuesta);
    }

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }
}
