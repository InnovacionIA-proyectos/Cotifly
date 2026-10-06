using System.Net;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Costeo;

/// <summary>
/// Adaptador de servicios conexos de Nexo (HU-013.3, ADR-018 §4): rutas y cuerpos del swagger de Nexo, el catálogo sin
/// <c>costoUnitario</c>, el POST sin <c>valorAplicado</c> y sin reintentos, el límite de tamaño de respuesta y la traducción de los
/// errores (403 → <c>NEXO_SIN_PERMISO</c>, 400/404 al agregar → <c>CONEXO_INVALIDO</c>, el resto → <c>NEXO_NO_DISPONIBLE</c>).
/// </summary>
public sealed class clsServiciosConexosNexoTests
{
    private static readonly Guid _usuarioId = Guid.NewGuid();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void SinUrlBaseElAdaptadorEsElNoDisponible()
    {
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = null }, new clsNexoClientesFalso());
        using var ambito = proveedor.CreateScope();

        ambito.ServiceProvider.GetRequiredService<IClsServiciosConexosNexo>().Should().BeOfType<clsServiciosConexosNoDisponible>();
    }

    [Fact]
    public async Task ElNoDisponibleRespondeNexoNoDisponible()
    {
        var resultado = await new clsServiciosConexosNoDisponible().BuscarAsync(_usuarioId, new clsFiltroCatalogoConexos(null, null, 1, 20), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
    }

    [Fact]
    public async Task ElCatalogoPideSoloActivosConElFiltroYNuncaLeeElCosto()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, """
            {"items":[{"id":"6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d","codigoServicio":"ALM-01","tipoServicio":"Almacenamiento","categoria":"logistico",
              "unidad":"Día","costoUnitario":120000,"vigencia":"2026-12-31","activo":true,"tipoMaquina":"Montacargas"}],"total":7}
            """);
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, nexo);
        using var ambito = proveedor.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsServiciosConexosNexo>()
            .BuscarAsync(_usuarioId, new clsFiltroCatalogoConexos("alm acén", "logistico", 2, 10), Ct);

        var peticion = nexo.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Get);
        peticion.Url.AbsolutePath.Should().Be("/api/servicios-conexos");
        peticion.Url.Query.Should().Contain("soloActivos=true").And.Contain("page=2").And.Contain("pageSize=10")
            .And.Contain("q=alm%20ac%C3%A9n").And.Contain("categoria=logistico");
        peticion.IdPortalUsuario.Should().Be("1007");
        resultado.Valor.Total.Should().Be(7);
        var servicio = resultado.Valor.Items.Should().ContainSingle().Subject;
        servicio.Should().Be(new clsServicioConexoNexo("6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d", "ALM-01", "Almacenamiento", "logistico", "Día",
            null, null, null, null, "Montacargas", new DateOnly(2026, 12, 31), true));
        typeof(clsServicioConexoNexo).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Costo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AgregarEnviaLineaIdServicioYCantidadSinValorAplicadoYNoSeReintenta()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.ServiceUnavailable).Responder(HttpStatusCode.OK, """{"id":"COT-1"}""");
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, nexo);
        using var ambito = proveedor.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsServiciosConexosNexo>()
            .AgregarAsync(_usuarioId, "COT-1", [new clsConexoParaNexo("LC-1", "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d", 2.5m)], Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        var peticion = nexo.Peticiones.Should().ContainSingle("el POST de conexos no es idempotente").Subject;
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsolutePath.Should().Be("/api/cotizaciones/COT-1/conexos");
        var item = JsonNode.Parse(peticion.Cuerpo!)!["items"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        item.Select(p => p.Key).Should().BeEquivalentTo("lineaId", "servicioConexoId", "cantidad");
        item["lineaId"]!.GetValue<string>().Should().Be("LC-1");
        item["cantidad"]!.GetValue<decimal>().Should().Be(2.5m);
    }

    [Fact]
    public async Task LeerLaCotizacionTraeElIdDeCadaLineaDeLaCotizacionDeNexo()
    {
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, """
            {"id":"COT-1","estado":"Borrador","resultado":"SinDefinir",
             "lineas":[{"id":"LC-1","lineaSolicitudId":"LS-1","valorFlete":2550000}],
             "conexos":[{"id":"CX-1","nombre":"Almacenamiento","categoria":"logistico","cantidad":2,"valorAplicado":240000}]}
            """);
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, nexo);
        using var ambito = proveedor.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsServiciosConexosNexo>().LeerCotizacionAsync(_usuarioId, "COT-1", Ct);

        nexo.Peticiones.Single().Url.AbsolutePath.Should().Be("/api/cotizaciones/COT-1");
        nexo.Peticiones.Single().Metodo.Should().Be(HttpMethod.Get);
        resultado.Valor.Lineas.Single().Should().Be(new Cotifly.Dominio.Cotizaciones.clsLineaCosteoNexo("LS-1", 2_550_000m, null, null, "LC-1"));
        resultado.Valor.Conexos.Single().ValorAplicado.Should().Be(240_000m);
    }

    [Fact]
    public async Task UnaRespuestaMasGrandeQueElLimiteNoSeLee()
    {
        var enorme = """{"items":[],"total":0,"relleno":""" + "\"" + new string('x', (int)clsServiciosConexosNexo.LimiteRespuestaBytes) + "\"}";
        var nexo = new clsNexoClientesFalso().Responder(HttpStatusCode.OK, enorme);
        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, nexo);
        using var ambito = proveedor.CreateScope();

        var resultado = await ambito.ServiceProvider.GetRequiredService<IClsServiciosConexosNexo>()
            .BuscarAsync(_usuarioId, new clsFiltroCatalogoConexos(null, null, 1, 20), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.NexoNoDisponible);
        resultado.Error.Detalle.Should().Contain("demasiado grande");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true, "NEXO_SIN_PERMISO")]
    [InlineData(HttpStatusCode.Forbidden, false, "NEXO_SIN_PERMISO")]
    [InlineData(HttpStatusCode.BadRequest, true, "CONEXO_INVALIDO")]
    [InlineData(HttpStatusCode.NotFound, true, "CONEXO_INVALIDO")]
    [InlineData(HttpStatusCode.Unauthorized, true, "NEXO_NO_DISPONIBLE")]
    [InlineData(HttpStatusCode.BadRequest, false, "NEXO_NO_DISPONIBLE")]
    [InlineData(HttpStatusCode.InternalServerError, true, "NEXO_NO_DISPONIBLE")]
    public async Task LosErroresDeNexoSeTraducenAlContrato(HttpStatusCode estado, bool agregando, string codigo)
    {
        var nexo = new clsNexoClientesFalso();
        for (var i = 0; i < 3; i++)
        {
            nexo.Responder(estado, """{"code":"SERVICIO_CONEXO_NO_ENCONTRADO","message":"No existe."}""");
        }

        using var proveedor = Proveedor(new() { ["Nexo:UrlBase"] = clsNexoClientesFalso.UrlBase }, nexo);
        using var ambito = proveedor.CreateScope();
        var adaptador = ambito.ServiceProvider.GetRequiredService<IClsServiciosConexosNexo>();

        var error = agregando
            ? (await adaptador.AgregarAsync(_usuarioId, "COT-1", [new clsConexoParaNexo("LC-1", Guid.NewGuid().ToString(), 1m)], Ct)).Error
            : (await adaptador.BuscarAsync(_usuarioId, new clsFiltroCatalogoConexos(null, null, 1, 20), Ct)).Error;

        error.Codigo.Should().Be(codigo);
        if (codigo == "CONEXO_INVALIDO")
        {
            error.Detalle.Should().Be("SERVICIO_CONEXO_NO_ENCONTRADO", "el código de Nexo viaja en detalle.motivo");
        }
    }

    [Fact]
    public void ElCodigoDeNexoSeExtraeDelDetalle()
    {
        clsServiciosConexosNexo.CodigoNexo("HTTP 404 SERVICIO_CONEXO_NO_ENCONTRADO: No existe.").Should().Be("SERVICIO_CONEXO_NO_ENCONTRADO");
        clsServiciosConexosNexo.CodigoNexo("HTTP 400: texto").Should().BeNull();
    }

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores, clsNexoClientesFalso nexo)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        servicios.AddHttpClient(clsCosteoNexo.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => nexo);
        var usuarios = Substitute.For<IClsUsuarioWebRepository>();
        usuarios.ObtenerPorIdAsync(_usuarioId, Arg.Any<CancellationToken>())
            .Returns(clsUsuarioWeb.Crear("mospina", "Mariana Ospina", "mospina@boterosoto.com.co", null, DateTimeOffset.UtcNow, 1007).Valor);
        servicios.AddScoped<IClsUsuarioWebRepository>(_ => usuarios);
        return servicios.BuildServiceProvider();
    }
}
