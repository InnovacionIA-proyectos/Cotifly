using Cotifly.Api.Comun;
using Cotifly.Aplicacion.Comun;
using Cotifly.Dominio.Comun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Api.Tests.Comun;

/// <summary>Traducción de errores de los casos de uso a HTTP en <c>/web/*</c>: tabla "Para #12" de docs/07-api.md (§7.4) y el contrato.</summary>
public sealed class clsRespuestasWebTests
{
    [Theory]
    [InlineData(clsCodigosError.CotizacionNoEncontrada, 404, "ErrorWeb")]
    [InlineData(clsCodigosAplicacion.NoEncontrado, 404, "ErrorWeb")]
    [InlineData(clsCodigosError.CorreoDeshabilitado, 503, "ErrorNegocio")]
    [InlineData(clsCodigosAplicacion.RegionalNoDisponible, 503, "ErrorNegocio")]
    [InlineData(clsCodigosError.PdfRenderNoDisponible, 503, "ErrorNegocio")]
    [InlineData(clsCodigosError.ServicioOcupado, 503, "ErrorNegocio")]
    [InlineData(clsCodigosAplicacion.RegionalNoAsignada, 409, "ErrorNegocio")]
    [InlineData("USUARIO_NO_ENCONTRADO", 401, "ErrorWeb")]
    [InlineData(clsCodigosAplicacion.NoAutenticado, 401, "ErrorWeb")]
    [InlineData(clsCodigosAplicacion.SinPermiso, 403, "ErrorWeb")]
    [InlineData("TELEFONO_INVALIDO", 400, "ErrorWeb")]
    [InlineData("CORREO_INVALIDO", 400, "ErrorWeb")]
    [InlineData("SEGUIMIENTO_INVALIDO", 400, "ErrorWeb")]
    [InlineData(clsCodigosAplicacion.SolicitudInvalida, 400, "ErrorWeb")]
    [InlineData(clsCodigosError.CorreoDuplicado, 400, "ErrorNegocio")]
    [InlineData(clsCodigosError.CorreoPdfNoEncontrado, 400, "ErrorNegocio")]
    [InlineData(clsCodigosError.CorreoEnvioFallido, 400, "ErrorNegocio")]
    [InlineData(clsCodigosError.ConfirmacionRequerida, 400, "ErrorNegocio")]
    public void CadaCodigoRespondeElEstadoYLaFormaDeLaTabla(string codigo, int estado, string forma)
    {
        var resultado = clsRespuestasWeb.Error(new clsErrorDominio(codigo, "mensaje del caso de uso", "motivo"));

        var objeto = resultado.Should().BeAssignableTo<ObjectResult>().Subject;
        objeto.StatusCode.Should().Be(estado);
        objeto.Value!.GetType().Should().Be(forma == "ErrorWeb" ? typeof(clsErrorWeb) : typeof(clsErrorNegocioWeb));
    }

    [Theory]
    [InlineData(clsCodigosError.CotizacionNoEncontrada)]
    [InlineData(clsCodigosAplicacion.NoEncontrado)]
    public void Los404DeCotizacionNoDistinguenSiExisteONoEsDelComercial(string codigo)
    {
        var resultado = (ObjectResult)clsRespuestasWeb.Error(new clsErrorDominio(codigo, "Cotización no encontrada o no es tuya."));

        resultado.Value.Should().Be(new clsErrorWeb("Cotización no encontrada o no es tuya."));
    }

    [Fact]
    public async Task ServicioOcupadoResponde503ConRetryAfter()
    {
        var resultado = clsRespuestasWeb.Error(new clsErrorDominio(clsCodigosError.ServicioOcupado, "ocupado"));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider(),
        };
        http.Response.Body = new MemoryStream();

        await resultado.ExecuteResultAsync(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));

        http.Response.StatusCode.Should().Be(503);
        http.Response.Headers.RetryAfter.ToString().Should().Be("1");
    }

    [Fact]
    public void ElErrorNegocioLlevaElDetalleComoObjeto()
    {
        var cuerpo = clsErrorNegocioWeb.Desde(new clsErrorDominio("CORREO_ENVIO_FALLIDO", "Falló el relay", "timeout"));

        cuerpo.Error.Codigo.Should().Be("CORREO_ENVIO_FALLIDO");
        cuerpo.Error.Detalle.Should().Equal(new Dictionary<string, object?> { ["motivo"] = "timeout" });
        clsErrorNegocioWeb.Desde(new clsErrorDominio("CORREO_DUPLICADO", "x")).Error.Detalle.Should().BeNull();
    }

    [Fact]
    public void LosDatosDelErrorVanEnElDetalleJuntoAlMotivo()
    {
        var error = new clsErrorDominio("JUSTIFICACION_REQUERIDA", "Falta la justificación", "texto")
        {
            Datos = new Dictionary<string, object?> { ["margen_sugerido"] = 0.15m },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(clsErrorNegocioWeb.Desde(error), clsJsonWeb.Opciones);

        json.Should().Contain("\"detalle\":{\"margen_sugerido\":0.15,\"motivo\":\"texto\"}");
    }

    [Theory]
    [InlineData("CONFLICTO_CONCURRENCIA", 409)]
    [InlineData("PDF_YA_ENVIADO", 409)]
    [InlineData("PRECONDICION_REQUERIDA", 428)]
    [InlineData("JUSTIFICACION_REQUERIDA", 400)]
    public void LosCodigosDelPipelineDeterministaTienenSuEstadoHttp(string codigo, int estado)
    {
        var resultado = (ObjectResult)clsRespuestasWeb.Error(new clsErrorDominio(codigo, "x"));

        (resultado.StatusCode ?? 200).Should().Be(estado);
        resultado.Value.Should().BeOfType<clsErrorNegocioWeb>();
    }
}
