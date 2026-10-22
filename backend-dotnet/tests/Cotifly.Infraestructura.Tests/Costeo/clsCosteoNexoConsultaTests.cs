using System.Net;
using System.Text.Json;
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
/// <see cref="clsCosteoNexo.ConsultarCosteoAsync"/> (HU-005.6) contra un <see cref="HttpMessageHandler"/> falso: <c>GET
/// /api/solicitudes/{id}</c> y luego <c>GET /api/cotizaciones/por-solicitud/{id}</c> (sin escribir en Nexo, E2E 2026-10-16), con la
/// forma de <c>CONTRATO.md</c>.
/// </summary>
public sealed class clsCosteoNexoConsultaTests : IDisposable
{
    private static readonly Guid _usuarioId = Guid.NewGuid();
    private readonly clsNexoClientesFalso _nexo = new();
    private readonly HttpClient _http;
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();

    public clsCosteoNexoConsultaTests()
    {
        _http = new HttpClient(_nexo);
        _usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Solicitud =
        """{"solicitud":{"id":"SOL-1","clienteId":"00000000-0000-0000-0000-000000000000","tipo":"Spot","estado":"EnCosteo","lineas":[{"id":"L1","estadoLinea":"AutoResuelta"}]},"tiempos":{"horasCargue":4,"horasDescargue":4},"lineas":[]}""";

    private const string Cotizacion =
        """
        {"id":"COT-1","solicitudId":"SOL-1","estado":"Borrador","resultado":"SinDefinir","total":2750000,"entregadaAt":null,
         "lineas":[{"id":"CL-1","lineaSolicitudId":"L1","valorFlete":2500000,"fuente":"Sicetac","valorAprovisionamiento":null,"valorRetornoContenedor":150000},
                   {"id":"CL-2","lineaSolicitudId":null,"valorFlete":null}],
         "conexos":[{"id":"CX-1","servicioConexoId":"S1","nombre":"Cargue","categoria":"logistico","cantidad":1,"valorAplicado":100000},
                    {"id":"CX-2","nombre":"Sin valor"}]}
        """;

    [Fact]
    public async Task ConsultaLaSolicitudYLeeSuCotizacionSinEscribirEnNexo()
    {
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.OK, Cotizacion);

        var resultado = await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Cotizacion!.Id.Should().Be("COT-1");
        _nexo.Peticiones.Should().HaveCount(2);
        _nexo.Peticiones.Should().OnlyContain(p => p.Metodo == HttpMethod.Get, "E2E 2026-10-16: el polling no escribe en Nexo");
        _nexo.Peticiones[0].Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/solicitudes/SOL-1");
        _nexo.Peticiones[1].Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/cotizaciones/por-solicitud/SOL-1");
        _nexo.Peticiones.Should().OnlyContain(p => p.IdPortalUsuario == "1007");
    }

    [Fact]
    public async Task SinCotizacionEnNexoLaFotoNoTraeCotizacionYNoSeCreaUnBorrador()
    {
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.NoContent);

        var foto = (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.Cotizacion.Should().BeNull("Nexo responde 204: todavía no hay cotización (ni fletes)");
        Cotifly.Dominio.Cotizaciones.clsDerivacionEstadoCosteo.Derivar(foto).Should().Be(Cotifly.Dominio.Cotizaciones.EstadoCosteo.PendienteCosteo);
        _nexo.Peticiones.Should().HaveCount(2).And.OnlyContain(p => p.Metodo == HttpMethod.Get);
    }

    [Fact]
    public async Task UnNexoSinLaRutaDeLecturaUsaUnaVezElCrearORecuperar()
    {
        _nexo.Responder(HttpStatusCode.OK, Solicitud)
            .Responder(HttpStatusCode.NotFound)
            .Responder(HttpStatusCode.Created, Cotizacion);

        var foto = (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.Cotizacion!.Id.Should().Be("COT-1");
        _nexo.Peticiones.Should().HaveCount(3);
        _nexo.Peticiones[2].Metodo.Should().Be(HttpMethod.Post);
        _nexo.Peticiones[2].Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/cotizaciones");
        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones[2].Cuerpo!);
        cuerpo.RootElement.GetProperty("solicitudId").GetString().Should().Be("SOL-1");
    }

    [Fact]
    public async Task ConElIdGuardadoLeeLaCotizacionPorSuIdSinBuscarlaNiCrearla()
    {
        var cotizaciones = Substitute.For<Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository>();
        cotizaciones.ObtenerCotizacionNexoIdAsync("SOL-1", Arg.Any<CancellationToken>()).Returns("COT-1");
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.OK, Cotizacion);

        var foto = (await AdaptadorCon(cotizaciones).ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.Cotizacion!.Id.Should().Be("COT-1");
        _nexo.Peticiones.Select(p => (p.Metodo.Method, p.Url.AbsolutePath))
            .Should().Equal(("GET", "/api/solicitudes/SOL-1"), ("GET", "/api/cotizaciones/COT-1"));
    }

    [Fact]
    public async Task SiNexoYaNoReconoceElIdGuardadoLaBuscaPorLaSolicitud()
    {
        var cotizaciones = Substitute.For<Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository>();
        cotizaciones.ObtenerCotizacionNexoIdAsync("SOL-1", Arg.Any<CancellationToken>()).Returns("COT-VIEJA");
        _nexo.Responder(HttpStatusCode.OK, Solicitud)
            .Responder(HttpStatusCode.NotFound, """{"code":"COTIZACION_NO_ENCONTRADA","message":"x"}""")
            .Responder(HttpStatusCode.OK, Cotizacion);

        var foto = (await AdaptadorCon(cotizaciones).ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.Cotizacion!.Id.Should().Be("COT-1");
        _nexo.Peticiones[2].Url.AbsolutePath.Should().Be("/api/cotizaciones/por-solicitud/SOL-1");
        _nexo.Peticiones.Should().OnlyContain(p => p.Metodo == HttpMethod.Get);
    }

    /// <summary>E2E 2026-10-16 (observación 2): el 409 de dos lecturas a la vez se reintenta una sola vez.</summary>
    [Fact]
    public async Task Un409PasajeroDeLaSolicitudSeReintentaUnaVez()
    {
        _nexo.Responder(HttpStatusCode.Conflict, """{"code":"CONFLICTO_CONCURRENCIA","message":"x"}""")
            .Responder(HttpStatusCode.OK, Solicitud)
            .Responder(HttpStatusCode.OK, Cotizacion);

        var resultado = await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct);

        resultado.EsExitoso.Should().BeTrue();
        _nexo.Peticiones.Count(p => p.Url.AbsolutePath == "/api/solicitudes/SOL-1").Should().Be(2);
    }

    [Fact]
    public async Task LeeEstadoLineasFletesYConexosDescartandoLosIncompletos()
    {
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.Created, Cotizacion);

        var foto = (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.EstadoSolicitud.Should().Be("EnCosteo");
        foto.LineasSolicitud.Should().Equal("L1");
        foto.Cotizacion!.Id.Should().Be("COT-1");
        foto.Cotizacion.Estado.Should().Be("Borrador");
        var linea = foto.Cotizacion.Lineas.Should().ContainSingle().Subject;
        linea.LineaSolicitudId.Should().Be("L1");
        linea.Costo.Should().Be(2_650_000m);
        var conexo = foto.Cotizacion.Conexos.Should().ContainSingle().Subject;
        conexo.Nombre.Should().Be("Cargue");
        conexo.ValorAplicado.Should().Be(100_000m);
    }

    [Fact]
    public async Task UnaSolicitudDescartadaNoCreaCotizacion()
    {
        _nexo.Responder(HttpStatusCode.OK, Solicitud.Replace("EnCosteo", "Descartada", StringComparison.Ordinal));

        var foto = (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.EstadoSolicitud.Should().Be("Descartada");
        foto.Cotizacion.Should().BeNull();
        _nexo.Peticiones.Should().ContainSingle();
    }

    [Fact]
    public async Task UnIdQueNexoYaNoReconoceEsSolicitudNoEncontradaTerminal()
    {
        _nexo.Responder(HttpStatusCode.NotFound, """{"code":"SOLICITUD_NO_ENCONTRADA","message":"No existe."}""");

        var resultado = await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-X", Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoSolicitudNoEncontrada);
        resultado.Error.Detalle.Should().Be("HTTP 404 SOLICITUD_NO_ENCONTRADA: No existe.");
    }

    [Fact]
    public async Task Un409SolicitudDescartadaEsLaFotoDescartadaComoLaDaElNexoReal()
    {
        _nexo.Responder(HttpStatusCode.Conflict, """{"code":"SOLICITUD_DESCARTADA","message":"La solicitud está descartada y no puede costearse."}""");

        var foto = (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.EstadoSolicitud.Should().Be("Descartada");
        foto.Cotizacion.Should().BeNull();
        Cotifly.Dominio.Cotizaciones.clsDerivacionEstadoCosteo.Derivar(foto).Should().Be(Cotifly.Dominio.Cotizaciones.EstadoCosteo.Descartada);
        _nexo.Peticiones.Should().ContainSingle("no se crea cotización de una descartada");
    }

    [Fact]
    public async Task Otro404U409SigueSiendoUnErrorReintentable()
    {
        _nexo.Responder(HttpStatusCode.NotFound, "no");
        // El 409 se reintenta una vez; si persiste, es el error de siempre.
        _nexo.Responder(HttpStatusCode.Conflict, """{"code":"CONFLICTO_CONCURRENCIA","message":"x"}""");
        _nexo.Responder(HttpStatusCode.Conflict, """{"code":"CONFLICTO_CONCURRENCIA","message":"x"}""");

        (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoRechazada);
        (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoRechazada);
    }

    [Fact]
    public async Task UnFalloEnLaCotizacionOUnaRespuestaIncompletaEsNoDisponible()
    {
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.InternalServerError);
        _nexo.Responder(HttpStatusCode.OK, """{"solicitud":{}}""");
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.Created, """{"estado":"Borrador"}""");

        (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Error.Codigo.Should().Be(clsCodigosError.CosteoNexoNoDisponible);
        (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Error.Detalle.Should().Contain("sin estado");
        (await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Error.Detalle.Should().Contain("sin id");
    }

    [Fact]
    public async Task ElResultadoVaAApiCotizacionesIdResultadoEnMinusculaConMotivoYComentario()
    {
        _nexo.Responder(HttpStatusCode.OK, Cotizacion.Replace("\"SinDefinir\"", "\"Ganada\"", StringComparison.Ordinal));

        var resultado = await Adaptador().MarcarResultadoAsync(_usuarioId, "COT-1", new Cotifly.Aplicacion.Costeo.DTOs.clsResultadoNexo(true, "Mejor precio", "Nota"), Ct);

        resultado.Valor.Resultado.Should().Be("Ganada");
        var peticion = _nexo.Peticiones.Single();
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/cotizaciones/COT-1/resultado");
        using var cuerpo = JsonDocument.Parse(peticion.Cuerpo!);
        cuerpo.RootElement.GetProperty("resultado").GetString().Should().Be("ganada", "Nexo valida el resultado en minúscula");
        cuerpo.RootElement.GetProperty("motivo").GetString().Should().Be("Mejor precio");
        cuerpo.RootElement.GetProperty("comentario").GetString().Should().Be("Nota");
    }

    [Theory]
    [InlineData("RESULTADO_YA_DEFINIDO")]
    [InlineData("COTIZACION_NO_ENTREGADA")]
    [InlineData("SOLICITUD_NO_TRANSICIONABLE")]
    public async Task Un409DeNexoConservaSuCodigo(string codigo)
    {
        _nexo.Responder(HttpStatusCode.Conflict, $$"""{"code":"{{codigo}}","message":"No se puede."}""");

        var resultado = await Adaptador().MarcarResultadoAsync(_usuarioId, "COT-1", new Cotifly.Aplicacion.Costeo.DTOs.clsResultadoNexo(false, null, null), Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        resultado.Error.Mensaje.Should().Be("No se puede.");
    }

    // DEF-C-1 (E2E 2026-10-22): leer la cotización para el resultado nunca escribe en Nexo si hay una ruta de lectura.
    [Fact]
    public async Task LeerLaCotizacionConElIdConocidoHaceSoloUnGetPorId()
    {
        _nexo.Responder(HttpStatusCode.OK, Cotizacion);

        var leida = await Adaptador().LeerCotizacionAsync(_usuarioId, "SOL-1", "COT-1", Ct);

        leida.Valor.Id.Should().Be("COT-1");
        _nexo.Peticiones.Should().ContainSingle().Which.Url.AbsolutePath.Should().Be("/api/cotizaciones/COT-1");
        _nexo.Peticiones.Should().OnlyContain(p => p.Metodo == HttpMethod.Get);
    }

    [Fact]
    public async Task LeerLaCotizacionSinIdLaBuscaPorLaSolicitudYSinCotizacionEsNoEntregada()
    {
        _nexo.Responder(HttpStatusCode.OK, Cotizacion).Responder(HttpStatusCode.NoContent);

        (await Adaptador().LeerCotizacionAsync(_usuarioId, "SOL-1", null, Ct)).Valor.Id.Should().Be("COT-1");
        (await Adaptador().LeerCotizacionAsync(_usuarioId, "SOL-1", null, Ct)).Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEntregada);

        _nexo.Peticiones.Should().HaveCount(2).And.OnlyContain(p => p.Metodo == HttpMethod.Get
            && p.Url.AbsolutePath == "/api/cotizaciones/por-solicitud/SOL-1");
    }

    [Fact]
    public async Task LeerLaCotizacionConUnIdQueNexoNoReconoceLaBuscaPorLaSolicitudSinEscribir()
    {
        _nexo.Responder(HttpStatusCode.NotFound, """{"code":"COTIZACION_NO_ENCONTRADA","message":"x"}""").Responder(HttpStatusCode.OK, Cotizacion);

        (await Adaptador().LeerCotizacionAsync(_usuarioId, "SOL-1", "COT-VIEJA", Ct)).Valor.Id.Should().Be("COT-1");

        _nexo.Peticiones.Select(p => (p.Metodo.Method, p.Url.AbsolutePath))
            .Should().Equal(("GET", "/api/cotizaciones/COT-VIEJA"), ("GET", "/api/cotizaciones/por-solicitud/SOL-1"));
    }

    [Fact]
    public async Task Un404CotizacionNoEncontradaDeNexoEsCotizacionNexoNoEncontradaYNoElNoEsTuya()
    {
        // Endurecimiento B (#199): antes viajaba como COTIZACION_NO_ENCONTRADA y el comercial veía el 404 "no es tuya".
        _nexo.Responder(HttpStatusCode.NotFound, """{"code":"COTIZACION_NO_ENCONTRADA","message":"Cotización no encontrada."}""");

        var resultado = await Adaptador().MarcarResultadoAsync(_usuarioId, "COT-X", new Cotifly.Aplicacion.Costeo.DTOs.clsResultadoNexo(true, null, null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNexoNoEncontrada);
        resultado.Error.Detalle.Should().Be("COTIZACION_NO_ENCONTRADA", "solo el código de Nexo, sin el texto HTTP");
        resultado.Error.Mensaje.Should().Be(clsCosteoNexo.MensajeCotizacionNexoNoEncontrada);
    }

    [Fact]
    public async Task Otro409EsRechazada()
    {
        _nexo.Responder(HttpStatusCode.Conflict, """{"code":"CONFLICTO_CONCURRENCIA","message":"Otra edición."}""");

        var resultado = await Adaptador().MarcarResultadoAsync(_usuarioId, "COT-1", new Cotifly.Aplicacion.Costeo.DTOs.clsResultadoNexo(true, null, null), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CosteoNexoRechazada);
    }

    [Fact]
    public async Task ElIdSeCodificaEnLaRuta()
    {
        _nexo.Responder(HttpStatusCode.NotFound);

        await Adaptador().ConsultarCosteoAsync(_usuarioId, "a/b?c", Ct);

        _nexo.Peticiones.Single().Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/solicitudes/a%2Fb%3Fc");
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(null, true, false)]
    [InlineData(true, true, true)]
    public async Task LosCodigosDelCubicadorSonLosQueViajaronNoLosDeLaBanderaActual(bool? registrado, bool banderaActual, bool esperado)
    {
        // Validación de #159: enviado con Nexo:EnviarCodigosCubicador apagada y leído con ella encendida (o al revés), el emparejamiento debe
        // comparar contra lo que viajó (registro del envío). Sin registro (solicitudes anteriores), "no viajaron".
        var cotizaciones = Substitute.For<Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository>();
        cotizaciones.ObtenerCodigosCubicadorEnviadosAsync("SOL-1", Arg.Any<CancellationToken>()).Returns(registrado);
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.Created, Cotizacion);
        var adaptador = new clsCosteoNexo(_http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase, EnviarCodigosCubicador = banderaActual ? "siempre" : "nunca" }),
            _usuarios, NullLogger<clsCosteoNexo>.Instance, cotizaciones);

        var foto = (await adaptador.ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.CodigosCubicadorEnviados.Should().Be(esperado);
    }

    [Fact]
    public async Task HU0311_LaFotoTraeLosVehiculosTraducidosPorAliasQueRegistroElEnvio()
    {
        var cotizaciones = Substitute.For<Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository>();
        cotizaciones.ObtenerEquivalenciasVehiculoAsync("SOL-1", Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { ["C2"] = "2" });
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.Created, Cotizacion);

        var foto = (await AdaptadorCon(cotizaciones).ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct)).Valor;

        foto.EquivalenciasVehiculo.Should().Equal(new Dictionary<string, string> { ["C2"] = "2" });
    }

    [Fact]
    public async Task HU0311_SiNoSePuedenLeerLasEquivalenciasLaFotoSigueSinEllas()
    {
        var cotizaciones = Substitute.For<Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository>();
        cotizaciones.ObtenerEquivalenciasVehiculoAsync("SOL-1", Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<string, string>>>(_ => throw new InvalidOperationException("base caída"));
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.Created, Cotizacion);

        var resultado = await AdaptadorCon(cotizaciones).ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.EquivalenciasVehiculo.Should().BeEmpty();
    }

    private clsCosteoNexo AdaptadorCon(Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository cotizaciones) =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase }), _usuarios, NullLogger<clsCosteoNexo>.Instance, cotizaciones);

    private clsCosteoNexo Adaptador() =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase }), _usuarios, NullLogger<clsCosteoNexo>.Instance);

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }
}
