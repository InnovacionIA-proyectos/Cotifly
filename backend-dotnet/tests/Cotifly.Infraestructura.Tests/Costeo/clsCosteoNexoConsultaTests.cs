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
/// /api/solicitudes/{id}</c> y luego <c>POST /api/cotizaciones</c> ("crear o recuperar"), con la forma de <c>CONTRATO.md</c>.
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
    public async Task ConsultaLaSolicitudYLuegoCreaORecuperaLaCotizacionConElIdExterno()
    {
        _nexo.Responder(HttpStatusCode.OK, Solicitud).Responder(HttpStatusCode.Created, Cotizacion);

        var resultado = await Adaptador().ConsultarCosteoAsync(_usuarioId, "SOL-1", Ct);

        resultado.EsExitoso.Should().BeTrue();
        _nexo.Peticiones.Should().HaveCount(2);
        _nexo.Peticiones[0].Metodo.Should().Be(HttpMethod.Get);
        _nexo.Peticiones[0].Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/solicitudes/SOL-1");
        _nexo.Peticiones[1].Metodo.Should().Be(HttpMethod.Post);
        _nexo.Peticiones[1].Url.AbsoluteUri.Should().Be("http://nexo-test.local/api/cotizaciones");
        using var cuerpo = JsonDocument.Parse(_nexo.Peticiones[1].Cuerpo!);
        cuerpo.RootElement.GetProperty("solicitudId").GetString().Should().Be("SOL-1");
        _nexo.Peticiones.Should().OnlyContain(p => p.IdPortalUsuario == "1007");
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
    public async Task Un409DeNexoConservaSuCodigo(string codigo)
    {
        _nexo.Responder(HttpStatusCode.Conflict, $$"""{"code":"{{codigo}}","message":"No se puede."}""");

        var resultado = await Adaptador().MarcarResultadoAsync(_usuarioId, "COT-1", new Cotifly.Aplicacion.Costeo.DTOs.clsResultadoNexo(false, null, null), Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        resultado.Error.Mensaje.Should().Be("No se puede.");
    }

    [Fact]
    public async Task Otro409EsRechazada()
    {
        _nexo.Responder(HttpStatusCode.Conflict, """{"code":"SOLICITUD_NO_TRANSICIONABLE","message":"Descartada."}""");

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

    private clsCosteoNexo Adaptador() =>
        new(_http, Options.Create(new clsOpcionesNexo { UrlBase = clsNexoClientesFalso.UrlBase }), _usuarios, NullLogger<clsCosteoNexo>.Instance);

    public void Dispose()
    {
        _http.Dispose();
        _nexo.Dispose();
    }
}
