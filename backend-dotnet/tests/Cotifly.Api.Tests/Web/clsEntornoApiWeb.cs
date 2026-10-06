using System.Net;
using System.Text.Json;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Contrato;
using Cotifly.Pruebas.Web;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Un SQL Server 2019 (Testcontainers) y dos APIs en memoria para las pruebas de <c>/web/*</c>: <see cref="Api"/> con el
/// correo habilitado (relay inalcanzable) y <see cref="ApiSinCorreo"/> con el valor por defecto (<c>Correo:Habilitado=false</c>).
/// </summary>
public sealed class clsEntornoApiWeb : IAsyncLifetime
{
    public clsBaseDatosPruebas BaseDatos { get; } = new();

    public clsApiWebPruebas Api { get; private set; } = null!;

    public clsApiWebPruebas ApiSinCorreo { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await BaseDatos.InitializeAsync();
        Api = new clsApiWebPruebas(BaseDatos, correoHabilitado: true);
        ApiSinCorreo = new clsApiWebPruebas(BaseDatos);
    }

    public async ValueTask DisposeAsync()
    {
        Api?.Dispose();
        ApiSinCorreo?.Dispose();
        await BaseDatos.DisposeAsync();
    }

    /// <summary>
    /// Comprueba el estado y valida el cuerpo contra el esquema de esa respuesta en <c>specs/openapi.yaml</c>
    /// (<paramref name="rutaContrato"/> tal como está en el contrato). Devuelve el JSON.
    /// </summary>
    public static async Task<JsonElement> VerificarContratoAsync(
        HttpResponseMessage respuesta, string rutaContrato, HttpMethod metodo, HttpStatusCode esperado)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        respuesta.StatusCode.Should().Be(esperado, cuerpo);
        var esquema = await clsValidadorEsquemaOpenApi.EsquemaRespuestaAsync(rutaContrato, metodo, esperado);
        clsValidadorEsquemaOpenApi.Validar(cuerpo, esquema).Should().BeEmpty($"{metodo} {rutaContrato} {(int)esperado} debe cumplir el contrato: {cuerpo}");
        using var documento = JsonDocument.Parse(cuerpo);
        return documento.RootElement.Clone();
    }

    /// <summary>El cuerpo cumple <c>ErrorNegocio</c> del contrato y trae el código esperado.</summary>
    public static async Task VerificarErrorNegocioAsync(HttpResponseMessage respuesta, string codigo)
    {
        ArgumentNullException.ThrowIfNull(respuesta);
        var cuerpo = await respuesta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        clsValidadorEsquemaOpenApi.Validar(cuerpo, await clsValidadorEsquemaOpenApi.EsquemaAsync("ErrorNegocio")).Should().BeEmpty(cuerpo);
        using var documento = JsonDocument.Parse(cuerpo);
        documento.RootElement.GetProperty("error").GetProperty("codigo").GetString().Should().Be(codigo);
    }
}

/// <summary>Colección de las pruebas de <c>/web/*</c> y <c>/pdfs</c> contra la base real: un contenedor para todas.</summary>
[CollectionDefinition(Nombre)]
public sealed class clsColeccionApiWeb : ICollectionFixture<clsEntornoApiWeb>
{
    public const string Nombre = "ApiWeb";
}
