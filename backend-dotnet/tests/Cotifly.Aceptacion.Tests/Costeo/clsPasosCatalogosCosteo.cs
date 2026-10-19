using System.Net;
using System.Text.Json.Nodes;
using Cotifly.Aceptacion.Tests.Web;
using Reqnroll;

namespace Cotifly.Aceptacion.Tests.Costeo;

/// <summary>
/// Pasos de <c>specs/features/catalogos_formulario_costeo.feature</c> (HU-013.10, #106): los catálogos del formulario salen de los
/// catálogos maestros del Nexo falso (<c>GET /api/catalogos/*</c>) y a Nexo solo llegan códigos de catálogo. Reutiliza la API y el
/// Nexo falso de <see cref="clsPasosCosteo"/>.
/// </summary>
[Binding]
public sealed class clsPasosCatalogosCosteo(clsContextoWeb web, clsPasosCosteo costeo)
{
    [Given("el catálogo maestro de tipos de vehículo de Nexo tiene:")]
    public void DadoLosTiposDeVehiculo(DataTable tabla)
    {
        ArgumentNullException.ThrowIfNull(tabla);
        costeo.Nexo.TiposVehiculo.Clear();
        foreach (var fila in tabla.Rows)
        {
            costeo.Nexo.TiposVehiculo.Add((fila["codigo"], fila["descripcion"], bool.Parse(fila["esCatalogoOficial"])));
        }
    }

    [Given("el catálogo maestro de clases de viaje de Nexo tiene {string}, {string}, {string} y {string}")]
    public void DadoLasClasesDeViaje(string a, string b, string c, string d)
    {
        costeo.Nexo.ClasesViaje.Clear();
        costeo.Nexo.ClasesViaje.AddRange([a, b, c, d]);
    }

    [Given("que el catálogo maestro de clases de contenedor de Nexo está vacío")]
    public void DadoClasesContenedorVacias() => costeo.Nexo.ClasesContenedor.Clear();

    [Given("que el catálogo maestro de clases de contenedor de Nexo falla")]
    public void DadoClasesContenedorFalla() => costeo.Nexo.CatalogosFallidos.Add("clases-contenedor");

    [Then("las clases de contenedor son {string}, {string}, {string}, {string} y {string} con el mismo texto como etiqueta")]
    public void EntoncesLasClasesDeContenedor(string a, string b, string c, string d, string e)
    {
        var clases = ClasesContenedor();
        clases.Select(t => t.Valor).Should().Equal(a, b, c, d, e);
        clases.Should().OnlyContain(t => t.Valor == t.Etiqueta);
    }

    [Then("las clases de contenedor no incluyen las provisionales {string}, {string}, {string} ni {string}")]
    public void EntoncesSinProvisionales(string a, string b, string c, string d) =>
        ClasesContenedor().Select(t => t.Valor).Should().NotContain([a, b, c, d]);

    [Then("las clases de contenedor vienen vacías")]
    public void EntoncesClasesContenedorVacias() => ClasesContenedor().Should().BeEmpty();

    private List<(string? Valor, string? Etiqueta)> ClasesContenedor() =>
        [.. web.UltimoJson().GetProperty("clases_contenedor").EnumerateArray()
            .Select(t => (t.GetProperty("valor").GetString(), t.GetProperty("etiqueta").GetString()))];

    [When("{string} pide los catálogos de flete")]
    public Task CuandoPideLosCatalogos(string alias) =>
        web.EnviarAsync(HttpMethod.Get, "/web/catalogos-flete", web.Sesion(alias).Access);

    [Then("los tipos de vehículo son {string} y {string} con su descripción como etiqueta")]
    public void EntoncesLosTiposDeVehiculo(string primero, string segundo)
    {
        var tipos = web.UltimoJson().GetProperty("tipos_vehiculo").EnumerateArray()
            .Select(t => (Valor: t.GetProperty("valor").GetString(), Etiqueta: t.GetProperty("etiqueta").GetString())).ToList();
        tipos.Select(t => t.Valor).Should().Equal(primero, segundo);
        tipos.Should().OnlyContain(t => costeo.Nexo.TiposVehiculo.Any(n => n.Codigo == t.Valor && n.Descripcion == t.Etiqueta));
    }

    [Then("las clases de viaje son {string}, {string}, {string} y {string}")]
    public void EntoncesLasClasesDeViaje(string a, string b, string c, string d) =>
        web.UltimoJson().GetProperty("clases_viaje").EnumerateArray().Select(t => t.GetProperty("valor").GetString())
            .Should().Equal(a, b, c, d);

    [Then("Nexo recibió la consulta de {string} y de {string}")]
    public void EntoncesNexoRecibioLasConsultas(string primera, string segunda)
    {
        var rutas = costeo.Nexo.Peticiones.Where(p => p.Metodo == HttpMethod.Get).Select(p => p.Url.AbsolutePath).ToList();
        rutas.Should().Contain([primera, segunda]);
    }

    [Then("Nexo no recibió la consulta de {string}")]
    public void EntoncesNexoNoRecibioLaConsulta(string ruta) =>
        costeo.Nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath == ruta);

    [When("{string} envía a costeo el formulario del cliente {string} como {string} con el tipo de vehículo {string}")]
    public Task CuandoEnviaConTipoDeVehiculo(string alias, string nit, string cotizacion, string tipo) =>
        EnviarAsync(alias, nit, cotizacion, f => f["tipo_vehiculo"] = tipo);

    [When("{string} envía a costeo el formulario del cliente {string} como {string} con la clase de viaje {string}")]
    public Task CuandoEnviaConClaseDeViaje(string alias, string nit, string cotizacion, string claseViaje) =>
        EnviarAsync(alias, nit, cotizacion, f =>
        {
            f["clase_viaje"] = claseViaje;
            if (claseViaje != "Contenedor")
            {
                f.Remove("clase_contenedor");
            }
        });

    [Then("Nexo recibió la solicitud de {string} sin {word}")]
    public void EntoncesNexoRecibioSinElCampo(string cotizacion, string campo)
    {
        costeo.Folio(cotizacion).Should().NotBeNullOrEmpty();
        var alta = costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        alta["lineas"]![0]![campo].Should().BeNull();
    }

    [When("{string} envía a costeo el formulario del cliente {string} como {string} con la tipología {string}")]
    public Task CuandoEnviaConTipologia(string alias, string nit, string cotizacion, string tipologia) =>
        EnviarAsync(alias, nit, cotizacion, f => f["tipologia"] = tipologia);

    [When("{string} envía a costeo el formulario del cliente {string} como {string} con la clase de viaje {string} y la clase de contenedor {string}")]
    public Task CuandoEnviaConClases(string alias, string nit, string cotizacion, string claseViaje, string claseContenedor) =>
        EnviarAsync(alias, nit, cotizacion, f =>
        {
            f["clase_viaje"] = claseViaje;
            f["clase_contenedor"] = claseContenedor;
        });

    [Then("Nexo recibió la solicitud de {string} con el {word} {string}")]
    public void EntoncesNexoRecibioElCampo(string cotizacion, string campo, string valor)
    {
        costeo.Folio(cotizacion).Should().NotBeNullOrEmpty();
        var alta = costeo.Nexo.UltimaAlta ?? throw new InvalidOperationException("Nexo no recibió ningún alta.");
        alta["lineas"]![0]![campo]!.GetValue<string>().Should().Be(valor);
    }

    [Then("el mensaje del error menciona {string}")]
    public void EntoncesElMensajeMenciona(string texto) =>
        web.UltimoJson().GetProperty("error").GetProperty("mensaje").GetString().Should().Contain(texto);

    [Then("Nexo no recibió solicitudes")]
    public void EntoncesNexoNoRecibioSolicitudes() =>
        costeo.Nexo.Peticiones.Should().NotContain(p => p.Metodo == HttpMethod.Post && p.Url.AbsolutePath == "/api/captura-solicitudes");

    [Then("{string} no tiene cotizaciones en \"Mis cotizaciones\"")]
    public async Task EntoncesSinCotizaciones(string alias)
    {
        await web.EnviarAsync(HttpMethod.Get, "/web/mis-cotizaciones", web.Sesion(alias).Access);
        web.UltimoEstado.Should().Be(HttpStatusCode.OK);
        web.UltimoJson().GetProperty("cotizaciones").GetArrayLength().Should().Be(0);
    }

    private async Task EnviarAsync(string alias, string nit, string cotizacion, Action<JsonObject> cambio)
    {
        var formulario = new JsonObject
        {
            ["tipo"] = "spot",
            ["nit_cliente"] = nit,
            ["linea_negocio"] = "Importacion",
            ["origen"] = "Buenaventura",
            ["destino"] = "Bogotá D.C.",
            ["tipologia"] = "Contenedor",
            ["clase_viaje"] = "Contenedor",
            ["clase_contenedor"] = "CNT40.HC",
            ["peso_kg"] = 28500,
        };
        cambio(formulario);
        await web.EnviarAsync(HttpMethod.Post, "/web/solicitudes-costeo", web.Sesion(alias).Access, formulario.ToJsonString());
        if (web.UltimoEstado == HttpStatusCode.Created)
        {
            costeo.Registrar(cotizacion, web.UltimoJson().GetProperty("folio").GetString()!);
        }
    }
}
