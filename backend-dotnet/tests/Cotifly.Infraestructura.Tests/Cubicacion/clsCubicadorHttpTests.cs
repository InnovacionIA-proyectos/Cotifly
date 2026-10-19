using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Cubicacion.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Cubicacion;

/// <summary>
/// <see cref="clsCubicadorHttp"/> (HU-021.5, #133; contrato de HU-023.1, #156) contra el cubicador falso (<see cref="clsCubicadorFalso"/>, un
/// <see cref="HttpMessageHandler"/> que nunca sale a la red) y los ejemplos del contrato (<see cref="clsEjemplosContratoCubicador"/>, P2):
/// forma de <c>POST /recomendaciones</c>, lectura de cada resultado, rechazo de la respuesta incompleta (fallo silencioso), traducción de
/// los errores, catálogos con caché y sonda de salud. La tubería (sin reintento del POST, tiempo agotado) se prueba con el registro real
/// de Infraestructura.
/// </summary>
public sealed class clsCubicadorHttpTests : IDisposable
{
    private static readonly Guid _tipoCafe = Guid.Parse("6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50");
    private const string RutaId = "0b9e1d7a-3c55-4b0e-8a11-5f2c6d7e8f90";

    private readonly clsCubicadorFalso _cubicador = new();
    private readonly HttpClient _http;

    public clsCubicadorHttpTests() => _http = new HttpClient(_cubicador);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static clsPeticionRecomendacion Peticion(string? claseViaje = "Carga Suelta") => new(
        RutaId, claseViaje, "1234567",
        [new clsMercanciaCubicador("CAFE_EN_ESTIBAS", 20, 120m, 100m, 150m, 700m), new clsMercanciaCubicador("BOBINAS", 1, 1200m, 235m, 269m, null)]);

    /// <summary>HU-023 (#176, A3): la petición con el peso de todas las posiciones (20 × 700 kg y 1 × 1.700 kg), para comprobar la huella.</summary>
    private static clsPeticionRecomendacion PeticionConPesos() => new(
        RutaId, "Carga Suelta", "1234567",
        [new clsMercanciaCubicador("CAFE_EN_ESTIBAS", 20, 120m, 100m, 150m, 700m), new clsMercanciaCubicador("BOBINAS", 1, 1200m, 235m, 269m, 1_700m)]);

    /// <summary>Un <c>ASIGNADO</c> con el detalle de la petición con pesos: 20 cafés de <paramref name="pesoCafe"/> kg en total y una bobina.</summary>
    private static Dictionary<string, object?> AsignadoDeLaCarga(decimal pesoCafe = 14_000m, int cantidadCafe = 20) =>
        clsCubicadorFalso.Asignado(clsCubicadorFalso.VehiculoCompleto(
            1, "3S3", "Tractomula 3S3", "FURGON", pesoCafe + 1_700m, false, true,
            clsCubicadorFalso.MercanciaVehiculo(0, "CAFE_EN_ESTIBAS", cantidadCafe, pesoCafe, 36m),
            clsCubicadorFalso.MercanciaVehiculo(1, "BOBINAS", 1, 1_700m, 7.6m)));

    private static string? Diferencia(object respuesta, clsPeticionRecomendacion peticion)
    {
        using var documento = JsonDocument.Parse(JsonSerializer.Serialize(respuesta));
        return clsCubicadorHttp.DiferenciaConLaCarga(documento.RootElement, peticion);
    }

    /// <summary>Reconsulta tras un tiempo agotado sin corrida nueva: devuelve <paramref name="corridaAnterior"/> (la última de la ruta).</summary>
    private async Task<clsResultado<clsRecomendacionCubicador>> ReconsultarCorridaAnteriorAsync(object corridaAnterior)
    {
        _cubicador.Corridas[RutaId] = JsonSerializer.Serialize(corridaAnterior);
        _cubicador.TiempoAgotadoSinGuardar = true;
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);
        return await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(PeticionConPesos(), Ct);
    }

    [Fact]
    public async Task MandaLaRutaConOrigenCotiflyClaseDeViajeUsuarioMercanciasPorCodigoYLaLlaveComoBearer()
    {
        await Cubicador().RecomendarAsync(Peticion(), Ct);

        var peticion = _cubicador.Peticiones.Should().ContainSingle().Subject;
        peticion.Metodo.Should().Be(HttpMethod.Post);
        peticion.Url.AbsoluteUri.Should().Be(clsCubicadorFalso.UrlBase + "/recomendaciones");
        peticion.Autorizacion.Should().Be("Bearer " + clsCubicadorFalso.ApiKey);
        var cuerpo = JsonNode.Parse(peticion.Cuerpo!)!;
        cuerpo["idLineaSolicitud"]!.GetValue<string>().Should().Be(RutaId);
        cuerpo["origenSistema"]!.GetValue<string>().Should().Be("COTIFLY");
        cuerpo["claseViajeSeleccionada"]!.GetValue<string>().Should().Be("Carga Suelta");
        cuerpo["usuarioId"]!.GetValue<string>().Should().Be("1234567");
        var mercancias = cuerpo["mercancias"]!.AsArray();
        mercancias.Should().HaveCount(2);
        mercancias[0]!["codigoMercancia"]!.GetValue<string>().Should().Be("CAFE_EN_ESTIBAS");
        mercancias[0]!.AsObject().ContainsKey("idTipoMercancia").Should().BeFalse("HU-023: el uuid ya no viaja");
        mercancias[0]!["cantidad"]!.GetValue<int>().Should().Be(20);
        mercancias[0]!["largoCm"]!.GetValue<decimal>().Should().Be(120m);
        mercancias[0]!["pesoKg"]!.GetValue<decimal>().Should().Be(700m);
        mercancias[1]!["pesoKg"].Should().BeNull("los nulos no viajan");
    }

    [Fact]
    public async Task SinClaseDeViajeNoLaManda()
    {
        await Cubicador().RecomendarAsync(Peticion(claseViaje: " "), Ct);

        JsonNode.Parse(_cubicador.Peticiones[0].Cuerpo!)!.AsObject().ContainsKey("claseViajeSeleccionada").Should().BeFalse("es una preferencia opcional");
    }

    [Fact]
    public async Task LeeLaFlotaMixtaDelContratoConVolumenPorcentajesYAdvertencias()
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.FlotaMixta;

        var r = (await Cubicador().RecomendarAsync(Peticion(), Ct)).Valor;

        r.Asignado.Should().BeTrue();
        r.IdRecomendacion.Should().Be(Guid.Parse("0b9c3f0e-6d2a-4c1b-9e7f-1a2b3c4d5e6f"));
        r.Vehiculos.Should().Equal(
            new clsVehiculoRecomendado(Guid.Parse("5f0e1d2c-3b4a-4968-8776-655443322110"), "3S3", "FURGON", 16_000m, 62.5m, 81.25m, 47.06m, "Tractocamión 3S3"),
            new clsVehiculoRecomendado(Guid.Parse("6a1f2e3d-4c5b-4a69-8877-665544332211"), "C2", "ESTACAS", 4_500.5m, 18.25m, 60m, 75.01m));
        r.Advertencias.Should().Equal(new clsAdvertenciaCubicador("REFRIGERADO", "La mercancía refrigerada va en FURGON: confirmar el equipo de frío."));
        r.Condicionada.Should().BeFalse();
        r.Extradimension.Should().BeNull();
    }

    [Theory]
    [InlineData("EXTRADIMENSIONADO")]
    [InlineData("SIN_CANDIDATOS_DIMENSIONALES")]
    [InlineData("SUPERA_K_MAX")]
    [InlineData("SIN_CARROCERIA_COMPATIBLE")]
    public async Task SinSeleccionAutomaticaEsUnResultadoConSuMotivoNoUnError(string motivo)
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.SinSeleccion(motivo);

        var r = (await Cubicador().RecomendarAsync(Peticion(), Ct)).Valor;

        r.SinSeleccion.Should().BeTrue();
        r.MotivoSinSeleccion.Should().Be(motivo);
        r.Vehiculos.Should().BeEmpty();
        if (motivo == clsMotivosSinSeleccion.Extradimensionado)
        {
            r.Extradimension.Should().BeEquivalentTo(new clsExtradimensionCubicador(
                true, new clsMedidasEstandarCubicador(1220m, 260m, 300m, 34000m),
                [new clsPiezaExtradimension(0, null, "VIGAS_ESTRUCTURAS", ["alto 320.0 cm > 300.0 cm"])]));
        }
    }

    [Fact]
    public async Task LaCondicionadaTraeSusMercanciasYNoEsUnError()
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.Condicionada;

        var r = (await Cubicador().RecomendarAsync(Peticion(), Ct)).Valor;

        r.Condicionada.Should().BeTrue();
        r.MercanciasCondicionadas.Should().Equal(new clsMercanciaCondicionada("QUIMICOS", "Requiere aprobación de AdminTarifas"));
        r.Advertencias.Should().ContainSingle().Which.Mensaje.Should().Contain("condicionada");
        r.Extradimension!.HayExtradimension.Should().BeFalse();
        r.Vehiculos.Should().ContainSingle();
    }

    [Theory]
    [InlineData("idRecomendacionVehiculo")]
    [InlineData("codigoVehiculo")]
    [InlineData("codigoCarroceria")]
    [InlineData("pesoTotalKg")]
    [InlineData("volumenTotalM3")]
    [InlineData("porcentajeLlenadoVolumen")]
    [InlineData("porcentajeLlenadoPeso")]
    public async Task AsignadoConUnVehiculoIncompletoSeRechazaContraElFalloSilencioso(string campo)
    {
        var respuesta = JsonNode.Parse(clsEjemplosContratoCubicador.FlotaMixta)!;
        respuesta["vehiculos"]![1]!.AsObject().Remove(campo);
        _cubicador.CuerpoRecomendacion = respuesta.ToJsonString();

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorRespuestaInvalida);
        resultado.Error.Detalle.Should().Contain(campo).And.Contain("vehiculos[1]");
        clsErroresCubicador.EsCaida(resultado.Error).Should().BeFalse("una respuesta inválida nunca va al respaldo");
    }

    [Theory]
    [InlineData("""{"resultado":"SELECCIONADO","vehiculos":[]}""", "resultado desconocido")]
    [InlineData("""{"resultado":"ASIGNADO","vehiculos":[]}""", "sin vehiculos")]
    [InlineData("""{"resultado":"SIN_SELECCION_AUTOMATICA"}""", "motivo desconocido")]
    [InlineData("""{"resultado":"SIN_SELECCION_AUTOMATICA","motivoSinSeleccion":"OTRO"}""", "motivo desconocido")]
    [InlineData("""{"resultado":"ASIGNADO","idRecomendacion":"no-uuid","vehiculos":[]}""", "idRecomendacion")]
    [InlineData("""[{"codigoVehiculo":"3S3"}]""", "no es un objeto")]
    [InlineData("no es json", "JSON inválido")]
    public async Task UnaRespuestaFueraDelContratoSeRechaza(string cuerpo, string motivo)
    {
        _cubicador.CuerpoRecomendacion = cuerpo;

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorRespuestaInvalida);
        resultado.Error.Detalle.Should().Contain(motivo);
    }

    [Theory]
    [InlineData("porcentajeLlenadoVolumen", 100.5)]
    [InlineData("porcentajeLlenadoPeso", -1)]
    [InlineData("pesoTotalKg", 0)]
    [InlineData("volumenTotalM3", -0.5)]
    public async Task UnValorFueraDeRangoSeRechaza(string campo, double valor)
    {
        var respuesta = JsonNode.Parse(clsEjemplosContratoCubicador.FlotaMixta)!;
        respuesta["vehiculos"]![0]![campo] = valor;
        _cubicador.CuerpoRecomendacion = respuesta.ToJsonString();

        (await Cubicador().RecomendarAsync(Peticion(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CubicadorRespuestaInvalida);
    }

    [Theory]
    [InlineData("MERCANCIAS_REQUERIDAS")]
    [InlineData("MERCANCIA_DESCONOCIDA")]
    [InlineData("MERCANCIA_NO_CUBICABLE")]
    [InlineData("DIMENSIONES_INVALIDAS")]
    public async Task UnRechazo400DelContratoSeTraduceAlMismoCodigoConElMensajeDelCubicador(string codigo)
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, codigo);

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        resultado.Error.Mensaje.Should().Be(clsCubicadorHttp.Rechazos[codigo] + $" Detalle del cubicador: Error {codigo} del cubicador falso.");
        clsErroresCubicador.EsCaida(resultado.Error).Should().BeFalse();
    }

    /// <summary>
    /// D-1019-1 (E2E 2026-10-19): el 400 MERCANCIA_DESCONOCIDA/MERCANCIA_NO_CUBICABLE del cubicador no trae el índice, pero cita el código
    /// entre comillas: Cotifly señala esa mercancía (base 0) con <c>campo = codigo_mercancia</c>. Si el cuerpo trae <c>item</c>, manda ese.
    /// </summary>
    [Theory]
    [InlineData("MERCANCIA_DESCONOCIDA", """{"codigo":"MERCANCIA_DESCONOCIDA","mensaje":"'bobinas' no esta en el catalogo de tipos de mercancia."}""", 1)]
    [InlineData("MERCANCIA_NO_CUBICABLE", """{"codigo":"MERCANCIA_NO_CUBICABLE","mensaje":"'CAFE_EN_ESTIBAS' no es cubicable."}""", 0)]
    [InlineData("MERCANCIA_DESCONOCIDA", """{"codigo":"MERCANCIA_DESCONOCIDA","mensaje":"Mercancía desconocida.","item":1}""", 1)]
    [InlineData("MERCANCIA_DESCONOCIDA", """{"error":{"codigo":"MERCANCIA_DESCONOCIDA","mensaje":"'CAFE_EN_ESTIBAS'","detalle":{"item":1}}}""", 1)]
    public async Task UnRechazoDeMercanciaSenalaElItem(string codigo, string cuerpo, int item)
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, codigo);
        _cubicador.CuerpoErrorRecomendacion = cuerpo;

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        resultado.Error.Datos.Should().BeEquivalentTo(new Dictionary<string, object?> { ["item"] = item, ["campo"] = "codigo_mercancia" });
    }

    [Theory]
    [InlineData("""{"codigo":"MERCANCIA_DESCONOCIDA","mensaje":"Una mercancía no está en el catálogo."}""")]
    [InlineData("""{"codigo":"MERCANCIA_DESCONOCIDA","mensaje":"'CAFE_EN_ESTIBAS' y 'BOBINAS' no estan en el catalogo."}""")]
    [InlineData("""{"codigo":"DIMENSIONES_INVALIDAS","mensaje":"'BOBINAS' mide demasiado."}""")]
    [InlineData("""{"codigo":"MERCANCIA_DESCONOCIDA","mensaje":"Mercancía desconocida.","item":7}""")]
    public async Task SiNoSePuedeSaberLaMercanciaElRechazoQuedaSinItem(string cuerpo)
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, "MERCANCIA_DESCONOCIDA");
        _cubicador.CuerpoErrorRecomendacion = cuerpo;

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Datos.Should().BeNull();
    }

    [Fact]
    public async Task Un400SinCodigoDelContratoEsUnRechazoNoDisponible()
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, "OTRO_CODIGO");

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        clsErroresCubicador.EsCaida(resultado.Error).Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LaLlaveRechazadaEsCubicadorNoAutorizadoYCuentaComoCaida(HttpStatusCode estado)
    {
        _cubicador.ErrorRecomendacion = (estado, "NO_AUTORIZADO");

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoAutorizado);
        resultado.Error.Detalle.Should().NotContain(clsCubicadorFalso.ApiKey);
        clsErroresCubicador.EsCaida(resultado.Error).Should().BeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Un503Un5xxOUn404SonCaidasNoDisponibles(HttpStatusCode estado)
    {
        _cubicador.Caido = estado;

        var resultado = await Cubicador().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Detalle.Should().Contain(((int)estado).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .And.NotContain(clsCubicadorFalso.ApiKey);
        clsErroresCubicador.EsCaida(resultado.Error).Should().BeTrue();
    }

    [Fact]
    public async Task SinConfiguracionNoLlamaYNoEstaDisponible()
    {
        var cubicador = Cubicador(new clsOpcionesCubicador());

        cubicador.EstaConfigurado.Should().BeFalse();
        (await cubicador.RecomendarAsync(Peticion(), Ct)).Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        _cubicador.Peticiones.Should().BeEmpty();
    }

    [Fact]
    public async Task ElCatalogoDeTiposDeMercanciaTraeCodigoCategoriaYMarcasYQuedaEnCache()
    {
        _cubicador.TiposMercancia.Add((_tipoCafe, "Café en estibas"));
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("QUIMICOS", "Químicos", null, "PELIGROSA", Cubicable: true, Condicionada: true, Activo: false));
        var cubicador = Cubicador();

        var primera = await cubicador.ListarTiposMercanciaAsync(Ct);
        var segunda = await Cubicador().ListarTiposMercanciaAsync(Ct);

        primera.Valor.Should().Equal(
            new clsTipoMercanciaCubicador("CAFE_EN_ESTIBAS", "Café en estibas", "GENERAL", true, false, null, true, _tipoCafe),
            new clsTipoMercanciaCubicador("QUIMICOS", "Químicos", "PELIGROSA", true, true, "Requiere aprobación de AdminTarifas", false));
        segunda.Valor.Should().BeSameAs(primera.Valor);
        _cubicador.Peticiones.Should().ContainSingle().Which.Url.AbsoluteUri.Should().Be(clsCubicadorFalso.UrlBase + "/tipos-mercancia");
    }

    [Fact]
    public async Task LosCatalogosDeCarroceriasYVehiculosSeLeenConCache()
    {
        var cubicador = Cubicador();

        var carrocerias = await cubicador.ListarTiposCarroceriaAsync(Ct);
        var vehiculos = await cubicador.ListarTiposVehiculoAsync(Ct);
        await Cubicador().ListarTiposVehiculoAsync(Ct);

        carrocerias.Valor.Select(c => c.Codigo).Should().BeEquivalentTo("FURGON", "ESTACAS", "CAMA_BAJA");
        vehiculos.Valor.Should().ContainEquivalentOf(new clsTipoVehiculoCubicador("2S2", "Tractocamión 2S2", ["FURGON", "ESTACAS", "CAMA_BAJA"], true));
        _cubicador.Peticiones.Select(p => p.Url.AbsolutePath).Should().Equal("/api/tipos-carroceria", "/api/vehiculos-catalogo");
    }

    [Fact]
    public async Task SinElCatalogoDeVehiculosPublicadoNoEstaDisponible()
    {
        _cubicador.SinTiposVehiculo = true;

        (await Cubicador().ListarTiposVehiculoAsync(Ct)).Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
    }

    [Fact]
    public async Task UnCatalogoVacioOCaidoNoSeGuardaEnCache()
    {
        var vacio = await Cubicador().ListarTiposMercanciaAsync(Ct);
        _cubicador.TiposMercancia.Add((_tipoCafe, "Café en estibas"));
        var despues = await Cubicador().ListarTiposMercanciaAsync(Ct);

        vacio.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        despues.Valor.Should().ContainSingle();
    }

    [Fact]
    public void ValidarNoAceptaUnObjetoQueNoEsDelContrato()
    {
        using var documento = JsonDocument.Parse("""{"vehiculos":[]}""");

        clsCubicadorHttp.Validar(documento.RootElement).Error.Codigo.Should().Be(clsCodigosError.CubicadorRespuestaInvalida);
    }

    // --- Sonda de salud (P7) ---

    [Fact]
    public async Task SinRutaDeSaludLaSondaNoConsultaElCubicador()
    {
        var sonda = Sonda(new clsOpcionesCubicador { UrlBase = clsCubicadorFalso.UrlBase, ApiKey = clsCubicadorFalso.ApiKey });

        (await sonda.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.Should().Be(HealthStatus.Healthy);
        _cubicador.Peticiones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, true, HealthStatus.Healthy)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, HealthStatus.Healthy)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, HealthStatus.Unhealthy)]
    public async Task ConRutaDeSaludLaSondaSoloTumbaElReadinessSinRespaldo(HttpStatusCode? caido, bool respaldo, HealthStatus esperado)
    {
        _cubicador.Caido = caido;
        var opciones = new clsOpcionesCubicador
        {
            UrlBase = clsCubicadorFalso.UrlBase, ApiKey = clsCubicadorFalso.ApiKey, RutaSalud = "/health", RespaldoSiFalla = respaldo,
        };

        var resultado = await Sonda(opciones).CheckHealthAsync(new HealthCheckContext(), Ct);

        resultado.Status.Should().Be(esperado);
        _cubicador.Peticiones.Should().ContainSingle().Which.Url.AbsolutePath.Should().Be("/api/health");
    }

    // --- Tubería real (registro de Infraestructura) ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SinUrlBaseElAdaptadorEsElNoDisponible(string? url)
    {
        using var proveedor = Proveedor(new() { ["Cubicador:UrlBase"] = url });

        proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().Should().BeOfType<clsCubicadorNoDisponible>();
    }

    [Fact]
    public void ConUrlBaseSinLlaveLaConfiguracionEsInvalida()
    {
        using var proveedor = Proveedor(new() { ["Cubicador:UrlBase"] = clsCubicadorFalso.UrlBase });

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Cubicador:ApiKey");
    }

    [Theory]
    [InlineData("health")]
    [InlineData("http://otro/health")]
    [InlineData("/health?x=1")]
    public void UnaRutaDeSaludQueNoEsRelativaEsInvalida(string ruta)
    {
        var valores = Configurado();
        valores["Cubicador:RutaSalud"] = ruta;
        using var proveedor = Proveedor(valores);

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Cubicador:RutaSalud");
    }

    /// <summary>
    /// D3b/D7 y opción C (E2E 2026-10-16): el motor tarda 26-31 s con cargas grandes y bloquea su API. El POST /recomendaciones espera
    /// 90 s; la reconsulta espera 5 s y tiene 60 s; las consultas (catálogos, salud) siguen cortas (15 s); un solo POST a la vez (cupo
    /// global y licitación); el presupuesto y el reclamo de la licitación alcanzan; "Calcular cubicaje" espera 8 s antes del 202.
    /// </summary>
    [Fact]
    public void LosTiemposPorDefectoSonCoherentes()
    {
        var opciones = new clsOpcionesCubicador();

        opciones.TiempoEsperaRecomendacionSegundos.Should().Be(90);
        opciones.TiempoEsperaSegundos.Should().Be(15);
        opciones.EsperaAntesReconsultaSegundos.Should().Be(5);
        opciones.TiempoEsperaReconsultaSegundos.Should().Be(60);
        opciones.MaximoConcurrente.Should().Be(1);
        opciones.EsperaMaximaCupoSegundos.Should().Be(60);
        opciones.PresupuestoCapturaSegundos.Should().Be(180);
        opciones.EsperaSincronaCubicacionSegundos.Should().Be(8);
        opciones.RetencionCubicacionMinutos.Should().Be(15);
        opciones.MaximoCubicacionesEnMemoria.Should().Be(500);
        new clsValidadorOpcionesCubicador().Validate(null, Valido(opciones)).Succeeded.Should().BeTrue();
        var licitacion = new Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion();
        licitacion.MaximoParalelo.Should().Be(1);
        licitacion.ReclamoSegundos.Should().Be(300);
        clsValidadorOpcionesCubicacionLicitacion.Minimo(opciones).Should().Be(60 + 90 + 2 + 5 + 60 + 30);
        licitacion.ReclamoSegundos.Should().BeGreaterThan(clsValidadorOpcionesCubicacionLicitacion.Minimo(opciones));
        new clsValidadorOpcionesCubicacionLicitacion(Options.Create(opciones)).Validate(null, licitacion).Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Opción D (validación): una cubicación rápida en proceso vence a los <c>RetencionCubicacionMinutos</c> de creada. Si vence antes de lo
    /// que puede durar la llamada más larga (cupo, POST, espera y reconsulta), el resultado se perdería: no arranca.
    /// </summary>
    [Fact]
    public void LaRetencionDeLasCubicacionesRapidasDebeCubrirLaLlamadaMasLarga()
    {
        var corta = Valido(new clsOpcionesCubicador { RetencionCubicacionMinutos = 3 });
        var resultado = new clsValidadorOpcionesCubicador().Validate(null, corta);

        clsCubicadorHttp.DuracionMaximaLlamada(corta).Should().Be(TimeSpan.FromSeconds(217), "60 + 90 + 2 + 5 + 60");
        resultado.Failed.Should().BeTrue("3 min = 180 s < 217 s");
        resultado.FailureMessage.Should().Contain("RetencionCubicacionMinutos");
        new clsValidadorOpcionesCubicador().Validate(null, Valido(new clsOpcionesCubicador { RetencionCubicacionMinutos = 4 })).Succeeded.Should().BeTrue();
    }

    /// <summary>D7 (E2E 2026-10-16): <c>.env.example</c> decía 15 s y el código usaba 30. Los valores documentados son los del código.</summary>
    [Fact]
    public void ElEnvExampleDocumentaLosTiemposDelCodigo()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "Cotifly.sln")))
        {
            directorio = directorio.Parent;
        }

        var ejemplo = File.ReadAllText(Path.Combine(directorio!.FullName, ".env.example")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var opciones = new clsOpcionesCubicador();
        var licitacion = new Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion();

        ejemplo.Should().Contain($"# Cubicador__TiempoEsperaSegundos={opciones.TiempoEsperaSegundos}\n")
            .And.Contain($"# Cubicador__TiempoEsperaRecomendacionSegundos={opciones.TiempoEsperaRecomendacionSegundos}\n")
            .And.Contain($"# Cubicador__PresupuestoCapturaSegundos={opciones.PresupuestoCapturaSegundos}\n")
            .And.Contain($"# Cubicador__ReclamoSegundos={licitacion.ReclamoSegundos}\n")
            .And.Contain($"# Cubicador__MaximoParalelo={licitacion.MaximoParalelo}\n")
            .And.Contain($"# Cubicador__MaximoConcurrente={opciones.MaximoConcurrente}\n")
            .And.Contain($"# Cubicador__EsperaMaximaCupoSegundos={opciones.EsperaMaximaCupoSegundos}\n")
            .And.Contain($"# Cubicador__EsperaAntesReconsultaSegundos={opciones.EsperaAntesReconsultaSegundos}\n")
            .And.Contain($"# Cubicador__TiempoEsperaReconsultaSegundos={opciones.TiempoEsperaReconsultaSegundos}\n")
            .And.Contain($"# Cubicador__EsperaSincronaCubicacionSegundos={opciones.EsperaSincronaCubicacionSegundos}\n")
            .And.Contain($"# Cubicador__RetencionCubicacionMinutos={opciones.RetencionCubicacionMinutos}\n")
            .And.Contain($"# Cubicador__MaximoCubicacionesEnMemoria={opciones.MaximoCubicacionesEnMemoria}\n");
    }

    [Theory]
    [InlineData(30, 20, 120, "TiempoEsperaRecomendacionSegundos")]
    [InlineData(15, 60, 45, "PresupuestoCapturaSegundos")]
    public void TiemposIncoherentesNoArrancan(int espera, int recomendacion, int presupuesto, string clave)
    {
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = espera.ToString(CultureInfo.InvariantCulture);
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = recomendacion.ToString(CultureInfo.InvariantCulture);
        valores["Cubicador:PresupuestoCapturaSegundos"] = presupuesto.ToString(CultureInfo.InvariantCulture);
        using var proveedor = Proveedor(valores);

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Cubicador:" + clave);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void ElTiempoDeLaRecomendacionVaDe1A300(int segundos)
    {
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = segundos.ToString(CultureInfo.InvariantCulture);
        using var proveedor = Proveedor(valores);

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("TiempoEsperaRecomendacionSegundos");
    }

    [Fact]
    public void UnReclamoMenorQueLaLlamadaMasLentaNoArranca()
    {
        // Opción C: 60 s de espera por el cupo + 90 s del POST + 2 + 5 s antes de reconsultar + 60 s de la reconsulta + 30 s de margen = 247.
        var validador = new clsValidadorOpcionesCubicacionLicitacion(Options.Create(new clsOpcionesCubicador()));

        validador.Validate(null, new Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion { ReclamoSegundos = 247 }).Failed.Should().BeTrue();
        validador.Validate(null, new Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion { ReclamoSegundos = 248 }).Succeeded.Should().BeTrue();
    }

    /// <summary>Opción C: la licitación no puede pedir más llamadas en paralelo que el cupo global del proceso.</summary>
    [Fact]
    public void UnaLicitacionConMasParaleloQueElCupoGlobalNoArranca()
    {
        var validador = new clsValidadorOpcionesCubicacionLicitacion(Options.Create(new clsOpcionesCubicador { MaximoConcurrente = 2 }));

        var resultado = validador.Validate(null, new Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion { MaximoParalelo = 3 });

        resultado.Failed.Should().BeTrue();
        resultado.FailureMessage.Should().Contain("Cubicador:MaximoParalelo").And.Contain("Cubicador:MaximoConcurrente");
        validador.Validate(null, new Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion { MaximoParalelo = 2 }).Succeeded.Should().BeTrue();
    }

    /// <summary>Opción C: la reconsulta espera al motor; nunca puede ser más corta que una consulta normal.</summary>
    [Fact]
    public void UnaReconsultaMasCortaQueLasConsultasNoArranca()
    {
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "20";
        valores["Cubicador:TiempoEsperaReconsultaSegundos"] = "10";
        using var proveedor = Proveedor(valores);

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain("Cubicador:TiempoEsperaReconsultaSegundos");
    }

    [Theory]
    [InlineData("MaximoConcurrente", "0")]
    [InlineData("EsperaMaximaCupoSegundos", "601")]
    [InlineData("EsperaAntesReconsultaSegundos", "61")]
    [InlineData("TiempoEsperaReconsultaSegundos", "0")]
    [InlineData("EsperaSincronaCubicacionSegundos", "31")]
    [InlineData("RetencionCubicacionMinutos", "0")]
    [InlineData("MaximoCubicacionesEnMemoria", "0")]
    public void LasClavesNuevasFueraDeRangoNoArrancan(string clave, string valor)
    {
        var valores = Configurado();
        valores["Cubicador:" + clave] = valor;
        using var proveedor = Proveedor(valores);

        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesCubicador>>().Value;

        leer.Should().Throw<OptionsValidationException>().Which.Message.Should().Contain(clave);
    }

    // ---------------------------------------------------------------- Opción C: cupo global y reconsulta con espera

    /// <summary>Opción C: con un cupo, varias recomendaciones a la vez llegan al cubicador una detrás de otra (nunca dos POST en paralelo).</summary>
    [Fact]
    public async Task ConUnCupoLasRecomendacionesNoSeSolapan()
    {
        _cubicador.DemoraRecomendacion = TimeSpan.FromMilliseconds(400);
        using var proveedor = Proveedor(Configurado(), _cubicador);

        var tareas = Enumerable.Range(0, 3)
            .Select(_ => proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct))
            .ToList();
        var resultados = await Task.WhenAll(tareas);

        resultados.Should().OnlyContain(r => r.EsExitoso);
        _cubicador.Recomendaciones.Should().HaveCount(3);
        _cubicador.MaximoEnParalelo.Should().Be(1, "Cubicador:MaximoConcurrente = 1 por defecto");
    }

    /// <summary>Opción C: sin cupo libre a tiempo no se llama al cubicador: CUBICADOR_OCUPADO marcado como caída.</summary>
    [Fact]
    public async Task SinCupoATiempoEsCubicadorOcupadoYNoLlama()
    {
        var compuerta = _cubicador.CerrarCompuerta();
        var valores = Configurado();
        valores["Cubicador:EsperaMaximaCupoSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);
        var primera = proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);
        await EsperarAsync(() => _cubicador.RecomendacionesIniciadas == 1);

        var segunda = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);

        segunda.Error.Codigo.Should().Be(clsCodigosError.CubicadorOcupado);
        segunda.Error.Mensaje.Should().Be(clsCubicadorHttp.MensajeOcupado);
        clsErroresCubicador.EsCaida(segunda.Error).Should().BeTrue("la captura y la licitación lo tratan como una caída");
        _cubicador.RecomendacionesIniciadas.Should().Be(1, "no se llamó al cubicador");
        compuerta.SetResult();
        (await primera).EsExitoso.Should().BeTrue();
        proveedor.GetRequiredService<clsCupoLlamadasCubicador>().Libres.Should().Be(1, "el cupo se soltó");
    }

    /// <summary>Opción C: la espera por el cupo se cancela con quien llama (no se queda colgada).</summary>
    [Fact]
    public async Task LaEsperaPorElCupoSeCancelaConQuienLlama()
    {
        var compuerta = _cubicador.CerrarCompuerta();
        using var proveedor = Proveedor(Configurado(), _cubicador);
        var primera = proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);
        await EsperarAsync(() => _cubicador.RecomendacionesIniciadas == 1);
        using var corte = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        corte.CancelAfter(TimeSpan.FromMilliseconds(300));

        var segunda = () => proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), corte.Token);

        await segunda.Should().ThrowAsync<OperationCanceledException>();
        compuerta.SetResult();
        (await primera).EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task ElCupoGlobalPermiteHastaElMaximoYSeSueltaUnaVez()
    {
        using var cupo = new clsCupoLlamadasCubicador(Options.Create(new clsOpcionesCubicador { MaximoConcurrente = 2 }));

        var uno = await cupo.TomarAsync(TimeSpan.Zero, Ct);
        var dos = await cupo.TomarAsync(TimeSpan.Zero, Ct);
        var tres = await cupo.TomarAsync(TimeSpan.FromMilliseconds(50), Ct);

        uno.Should().NotBeNull();
        dos.Should().NotBeNull();
        tres.Should().BeNull("los dos cupos están tomados");
        uno!.Dispose();
        uno.Dispose();
        cupo.Libres.Should().Be(1, "soltar dos veces el mismo cupo no regala otro");
        var otro = await cupo.TomarAsync(TimeSpan.Zero, Ct);
        otro.Should().NotBeNull();
        var cuarto = cupo.TomarAsync(TimeSpan.FromSeconds(5), Ct);
        dos!.Dispose();
        (await cuarto).Should().NotBeNull("la espera recibe el cupo que se suelta");
    }

    /// <summary>
    /// Opción C: tras un POST sin respuesta se espera <c>Cubicador:EsperaAntesReconsultaSegundos</c> y la reconsulta tiene su propio límite
    /// (más largo que el de las consultas): el cubicador la responde cuando el motor termina. La huella se sigue comprobando.
    /// </summary>
    [Fact]
    public async Task LaReconsultaEsperaAntesYTieneSuPropioLimite()
    {
        _cubicador.Recomendar = _ => AsignadoDeLaCarga();
        _cubicador.TiempoAgotadoTrasGuardar = true;
        _cubicador.DemoraReconsulta = TimeSpan.FromSeconds(2);
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1";
        valores["Cubicador:EsperaAntesReconsultaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaReconsultaSegundos"] = "10";
        using var proveedor = Proveedor(valores, _cubicador);
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var r = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(PeticionConPesos(), Ct);

        r.EsExitoso.Should().BeTrue("2 s de reconsulta caben en sus 10 s aunque las consultas tengan 1 s");
        cronometro.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(3.5), "1 s del POST + 1 s de espera + 2 s de la reconsulta");
        _cubicador.Recomendaciones.Should().ContainSingle();
        _cubicador.Reconsultas.Should().ContainSingle("respondió en el primer intento");
    }

    [Fact]
    public async Task UnaReconsultaQueNoRespondeASuLimiteEsUnaCaida()
    {
        _cubicador.Recomendar = _ => AsignadoDeLaCarga();
        _cubicador.TiempoAgotadoTrasGuardar = true;
        _cubicador.DemoraReconsulta = TimeSpan.FromSeconds(10);
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1";
        valores["Cubicador:EsperaAntesReconsultaSegundos"] = "0";
        valores["Cubicador:TiempoEsperaReconsultaSegundos"] = "2";
        using var proveedor = Proveedor(valores, _cubicador);
        var cronometro = System.Diagnostics.Stopwatch.StartNew();

        var r = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(PeticionConPesos(), Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        clsErroresCubicador.EsCaida(r.Error).Should().BeTrue();
        cronometro.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "la reconsulta se corta a sus 2 s, reintentos incluidos");
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        for (var i = 0; i < 200 && !condicion(); i++)
        {
            await Task.Delay(25, Ct);
        }

        condicion().Should().BeTrue();
    }


    /// <summary>
    /// D3b: una recomendación que tarda más que el límite de las consultas pero menos que el suyo responde; un catálogo con la misma demora
    /// agota su límite corto.
    /// </summary>
    [Fact]
    public async Task ElPostTieneSuPropioLimiteYLasConsultasSiguenCortas()
    {
        _cubicador.DemoraRecomendacion = TimeSpan.FromSeconds(3);
        _cubicador.DemoraCatalogo = TimeSpan.FromSeconds(3);
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "10";
        using var proveedor = Proveedor(valores, _cubicador);
        var cubicador = proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>();

        var recomendacion = await cubicador.RecomendarAsync(Peticion(), Ct);
        var catalogo = await cubicador.ListarTiposCarroceriaAsync(Ct);

        recomendacion.EsExitoso.Should().BeTrue("3 s caben en los 10 s del POST");
        _cubicador.Recomendaciones.Should().ContainSingle();
        _cubicador.Reconsultas.Should().BeEmpty("el POST respondió a tiempo");
        catalogo.EsFallo.Should().BeTrue("3 s no caben en el segundo de las consultas");
    }

    private static clsOpcionesCubicador Valido(clsOpcionesCubicador opciones)
    {
        opciones.UrlBase = clsCubicadorFalso.UrlBase;
        opciones.ApiKey = clsCubicadorFalso.ApiKey;
        return opciones;
    }

    [Fact]
    public async Task UnPostFallidoNoSeReintenta()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        using var proveedor = Proveedor(Configurado(), _cubicador);

        var resultado = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        _cubicador.Peticiones.Should().ContainSingle("el POST de la recomendación no es idempotente: nunca se reintenta");
    }

    [Fact]
    public async Task UnGetFallidoSiSeReintenta()
    {
        _cubicador.Caido = HttpStatusCode.ServiceUnavailable;
        using var proveedor = Proveedor(Configurado(), _cubicador);

        var resultado = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().ListarTiposMercanciaAsync(Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        _cubicador.Peticiones.Should().HaveCount(1 + clsCubicadorHttp.ReintentosLectura);
    }

    [Fact]
    public async Task SiElCubicadorNoRespondeATiempoNoEstaDisponible()
    {
        _cubicador.Demora = TimeSpan.FromSeconds(10);
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);

        var resultado = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Detalle.Should().Contain("tiempo agotado");
        clsErroresCubicador.EsCaida(resultado.Error).Should().BeTrue();
        _cubicador.Recomendaciones.Should().ContainSingle("HU-023 (#176): el POST nunca se reintenta; solo se reconsulta con GET");
    }

    // ---------------------------------------------------------------- HU-023 (#176): contrato vigente del cubicador

    [Fact]
    public async Task ElEjemploDeLaGuiaConFlotaMixtaLeeNumeroNombreAlertasYCadaPosicionSinAgruparElCodigoRepetido()
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.GuiaAsignadoFlotaMixta;

        var r = (await Cubicador().RecomendarAsync(Peticion(), Ct)).Valor;

        r.IdRecomendacion.Should().Be(Guid.Parse("C17B9E44-2A31-4F6B-9D02-1E8C3A5F7B64"), "los ids llegan en MAYÚSCULAS");
        r.Vehiculos.Should().HaveCount(2);
        var tracto = r.Vehiculos[0];
        tracto.NumeroVehiculo.Should().Be(1);
        tracto.NombreVehiculo.Should().Be("Tractomula 3S3");
        tracto.AlertaBajoLlenado.Should().BeFalse();
        tracto.Heterogenea.Should().BeFalse();
        tracto.Mercancias.Should().HaveCount(2, "dos filas del mismo código con medidas distintas: no se pisan");
        tracto.Mercancias!.Select(m => m.IndicePosicion).Should().Equal(0, 1);
        tracto.Mercancias.Select(m => m.CodigoMercancia).Should().AllBe("CAJAS_CARTON");
        tracto.TotalesPorCodigo().Should().ContainSingle().Which.Should().Match<clsMercanciaVehiculo>(
            m => m.Cantidad == 800 && m.PesoKg == 10_900m && m.VolumenM3 == 31.2m, "quien agrupa por código suma, no toma la primera ni la última");
        var sencillo = r.Vehiculos[1];
        sencillo.CodigoVehiculo.Should().Be("C2", "flota mixta");
        sencillo.NumeroVehiculo.Should().Be(2);
        sencillo.AlertaBajoLlenado.Should().BeTrue();
        sencillo.Mercancias.Should().ContainSingle().Which.IndicePosicion.Should().Be(2);
    }

    [Fact]
    public async Task UnIndicePosicionEnNullDeUnaCorridaViejaNoEsUnError()
    {
        _cubicador.Recomendar = _ => clsCubicadorFalso.Asignado(clsCubicadorFalso.VehiculoCompleto(
            1, "3S3", "Tractomula 3S3", "FURGON", 10_000m, false, false,
            clsCubicadorFalso.MercanciaVehiculo(null, "CAFE_EN_ESTIBAS", 20, 9_000m, 30m), clsCubicadorFalso.MercanciaVehiculo(null, "cafe_en_estibas", 1, 1_000m, 2m)));

        var r = (await Cubicador().RecomendarAsync(Peticion(), Ct)).Valor;

        r.Vehiculos[0].Mercancias.Should().HaveCount(2).And.OnlyContain(m => m.IndicePosicion == null);
        r.Vehiculos[0].TotalesPorCodigo().Should().ContainSingle("los códigos se comparan sin distinguir mayúsculas")
            .Which.Cantidad.Should().Be(21);
    }

    [Theory]
    [InlineData("EXTRADIMENSIONADO")]
    [InlineData("SIN_CANDIDATOS_DIMENSIONALES")]
    [InlineData("SUPERA_K_MAX")]
    [InlineData("SIN_CARROCERIA_COMPATIBLE")]
    [InlineData("BUSQUEDA_AGOTADA")]
    public async Task LosCincoMotivosDeSinSeleccionSonResultadosValidos(string motivo)
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.SinSeleccion(motivo);

        var r = await Cubicador().RecomendarAsync(Peticion(), Ct);

        r.EsExitoso.Should().BeTrue("HU-023 (#176): BUSQUEDA_AGOTADA ya no es CUBICADOR_RESPUESTA_INVALIDA");
        r.Valor.MotivoSinSeleccion.Should().Be(motivo);
        clsMotivosSinSeleccion.Mensaje(motivo).Should().NotContainAny("no cabe", "no caben");
    }

    [Fact]
    public void ElMensajeDeBusquedaAgotadaNoDiceQueLaCargaNoCabe() =>
        clsMotivosSinSeleccion.Mensaje(clsMotivosSinSeleccion.BusquedaAgotada).Should().Contain("algoritmo").And.NotContain("cabe");

    [Fact]
    public async Task LaExtradimensionDeLaGuiaTraeMedidasEstandarYPiezasConSuPosicion()
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.GuiaExtradimension;

        var r = (await Cubicador().RecomendarAsync(Peticion(), Ct)).Valor;

        r.MotivoSinSeleccion.Should().Be("EXTRADIMENSIONADO");
        r.Extradimension.Should().BeEquivalentTo(new clsExtradimensionCubicador(
            true, new clsMedidasEstandarCubicador(1220m, 260m, 300m, 34000m),
            [new clsPiezaExtradimension(1, "9F2C4A6E-1D3B-4F5A-8C7E-0B1D2F3A4C5E", "VIGAS_ESTRUCTURAS", ["largo 1400.0 cm > 1220.0 cm"])]));
        r.Advertencias.Should().ContainSingle().Which.Mensaje.Should().Be("2 pieza(s) exceden las medidas estándar.");
    }

    [Fact]
    public async Task LaCondicionadaDeLaGuiaUneElComentarioDelCatalogoDeTiposDeMercancia()
    {
        _cubicador.TiposMercancia.Add(new clsTipoMercanciaFalso("bobinas_acero", "Bobinas de acero", Condicionada: true));
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.GuiaCondicionada;

        var r = (await Cubicador().RecomendarAsync(Peticion(), Ct)).Valor;

        r.Condicionada.Should().BeTrue();
        r.Asignado.Should().BeTrue("la recomendación condicionada es válida");
        r.MercanciasCondicionadas.Should().Equal(new clsMercanciaCondicionada("BOBINAS_ACERO", "Requiere aprobación de AdminTarifas"));
    }

    [Fact]
    public async Task LaCondicionadaSinCatalogoQuedaSinComentarioPeroNoFalla()
    {
        _cubicador.CuerpoRecomendacion = clsEjemplosContratoCubicador.GuiaCondicionada;

        var r = await Cubicador().RecomendarAsync(Peticion(), Ct);

        r.EsExitoso.Should().BeTrue();
        r.Valor.MercanciasCondicionadas.Should().Equal(new clsMercanciaCondicionada("BOBINAS_ACERO", null));
    }

    [Fact]
    public async Task Un422DelCubicadorSinMercanciaEsUnRechazoVisibleNoUnaCaida()
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.UnprocessableEntity, "VALIDACION");

        var r = await Cubicador().RecomendarAsync(Peticion(), Ct);

        // D1 (E2E 2026-10-16): antes era CUBICADOR_RESPUESTA_INVALIDA (503, "avisa a soporte").
        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorPeticionRechazada);
        r.Error.Mensaje.Should().StartWith(clsCubicadorHttp.MensajePeticionRechazada);
        r.Error.Detalle.Should().Contain("422");
        clsErroresCubicador.EsCaida(r.Error).Should().BeFalse("un 422 nunca va al respaldo");
        _cubicador.Peticiones.Count(p => p.Metodo == HttpMethod.Post).Should().Be(1, "un 422 no se reintenta");
    }

    /// <summary>D1 (E2E 2026-10-16): el 422 de FastAPI que señala una mercancía llega como 400 con el ítem (base 0) y el campo.</summary>
    [Theory]
    [InlineData("largoCm", "DIMENSIONES_INVALIDAS")]
    [InlineData("pesoKg", "DIMENSIONES_INVALIDAS")]
    [InlineData("cantidad", "POSICION_INVALIDA")]
    public async Task Un422QueSenalaUnaMercanciaEsElRechazoDeEsaMercancia(string campo, string codigo)
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.UnprocessableEntity, "VALIDACION");
        _cubicador.CuerpoErrorRecomendacion =
            $$"""{"detail":[{"type":"greater_than","loc":["body","mercancias",1,"{{campo}}"],"msg":"Input should be greater than 0","input":0}]}""";

        var r = await Cubicador().RecomendarAsync(Peticion(), Ct);

        r.Error.Codigo.Should().Be(codigo);
        r.Error.Mensaje.Should().Contain("mercancía 2").And.Contain("Input should be greater than 0");
        r.Error.Datos.Should().Contain("item", 1).And.Contain("campo", campo);
        clsErroresCubicador.EsCaida(r.Error).Should().BeFalse();
    }


    [Fact]
    public async Task MasDeDiezMilUnidadesNoLlamaAlCubicadorYDevuelveCubicacionDemasiadoGrande()
    {
        var peticion = new clsPeticionRecomendacion(RutaId, null, null,
            [new clsMercanciaCubicador("CAJAS_CARTON", 6_000, 40m, 30m, 30m, 20m), new clsMercanciaCubicador("BEBIDAS", 4_001, 50m, 40m, 40m, 20m)]);

        var r = await Cubicador().RecomendarAsync(peticion, Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.CubicacionDemasiadoGrande);
        _cubicador.Peticiones.Should().BeEmpty("se valida antes de llamar");
    }

    [Fact]
    public async Task ExactamenteDiezMilUnidadesSiSeMandan()
    {
        var peticion = new clsPeticionRecomendacion(RutaId, null, null,
            [new clsMercanciaCubicador("CAJAS_CARTON", 6_000, 40m, 30m, 30m, 20m), new clsMercanciaCubicador("BEBIDAS", 4_000, 50m, 40m, 40m, 20m)]);

        (await Cubicador().RecomendarAsync(peticion, Ct)).EsExitoso.Should().BeTrue();
        _cubicador.Recomendaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task El400CubicacionDemasiadoGrandeDelCubicadorSeTraduceIgual()
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.BadRequest, "CUBICACION_DEMASIADO_GRANDE");

        var r = await Cubicador().RecomendarAsync(Peticion(), Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.CubicacionDemasiadoGrande);
        clsErroresCubicador.EsCaida(r.Error).Should().BeFalse();
    }

    [Fact]
    public async Task TrasUnTiempoAgotadoDelPostReconsultaYUsaLaCorridaQueQuedoCreadaConUnSoloPost()
    {
        _cubicador.Recomendar = _ => AsignadoDeLaCarga();
        _cubicador.TiempoAgotadoTrasGuardar = true;
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);

        var r = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(PeticionConPesos(), Ct);

        r.EsExitoso.Should().BeTrue("la corrida existe: se usa sin volver a pedirla");
        r.Valor.Vehiculos.Should().ContainSingle().Which.Heterogenea.Should().BeTrue();
        _cubicador.Recomendaciones.Should().ContainSingle("el POST nunca se reintenta a ciegas");
        var reconsulta = _cubicador.Reconsultas.Should().ContainSingle().Subject;
        reconsulta.Url.Query.Should().Contain("idLineaSolicitud=" + RutaId).And.Contain("origenSistema=COTIFLY");
    }

    [Fact]
    public async Task TrasUnTiempoAgotadoSinCorridaEsUnaCaidaConUnSoloPost()
    {
        _cubicador.TiempoAgotadoSinGuardar = true;
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);

        var r = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        clsErroresCubicador.EsCaida(r.Error).Should().BeTrue();
        _cubicador.Recomendaciones.Should().ContainSingle();
        _cubicador.Reconsultas.Should().NotBeEmpty();
    }

    [Fact]
    public async Task UnaCorridaReconsultadaDeOtraCargaNoSeUsa()
    {
        // Corrida anterior de la misma ruta con otras cantidades (recalcular con cambios de carga).
        _cubicador.Corridas[RutaId] = System.Text.Json.JsonSerializer.Serialize(clsCubicadorFalso.Asignado(clsCubicadorFalso.VehiculoCompleto(
            1, "C2", "Sencillo C2", "FURGON", 1_000m, false, false,
            clsCubicadorFalso.MercanciaVehiculo(0, "CAFE_EN_ESTIBAS", 5, 1_000m, 3m), clsCubicadorFalso.MercanciaVehiculo(1, "BOBINAS", 1, 10m, 1m))));
        _cubicador.TiempoAgotadoSinGuardar = true;
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        valores["Cubicador:TiempoEsperaRecomendacionSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);

        var r = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible, "la corrida guardada es de otra carga (5 unidades, no 20)");
        _cubicador.Recomendaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task UnaCorridaReconsultadaConLasMismasCantidadesYOtroPesoNoSeUsa()
    {
        // Auditoría A3: recalcular con el mismo rutaId tras cambiar el peso (999 kg y no 14.000): mismas unidades, otra carga.
        var r = await ReconsultarCorridaAnteriorAsync(AsignadoDeLaCarga(pesoCafe: 999m));

        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible, "la corrida guardada pesa otra cosa");
        clsErroresCubicador.EsCaida(r.Error).Should().BeTrue("sigue el flujo del tiempo agotado");
        _cubicador.Recomendaciones.Should().ContainSingle("el POST nunca se reintenta");
        _cubicador.Reconsultas.Should().NotBeEmpty();
    }

    [Fact]
    public async Task UnaSinSeleccionAnteriorReconsultadaNoSeUsa()
    {
        // Auditoría A3: una SIN_SELECCION no trae detalle por posición: no se puede atribuir a esta petición.
        var r = await ReconsultarCorridaAnteriorAsync(clsCubicadorFalso.SinSeleccion("SUPERA_K_MAX"));

        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        clsErroresCubicador.EsCaida(r.Error).Should().BeTrue();
    }

    [Fact]
    public async Task UnaCorridaReconsultadaCoincidenteSeUsa()
    {
        var r = await ReconsultarCorridaAnteriorAsync(AsignadoDeLaCarga());

        r.EsExitoso.Should().BeTrue("misma cantidad y mismo peso en cada posición");
        r.Valor.Vehiculos.Should().ContainSingle().Which.PesoTotalKg.Should().Be(15_700m);
        _cubicador.Recomendaciones.Should().ContainSingle();
    }

    [Fact]
    public void LaHuellaToleraElRedondeoDelPesoPeroNoOtroPeso()
    {
        Diferencia(AsignadoDeLaCarga(pesoCafe: 14_000.05m), PeticionConPesos()).Should().BeNull("redondeo de la fila");
        Diferencia(AsignadoDeLaCarga(pesoCafe: 14_001m), PeticionConPesos()).Should().Contain("posición 0 pesa");
        Diferencia(AsignadoDeLaCarga(pesoCafe: 13_999m), PeticionConPesos()).Should().Contain("posición 0 pesa");
    }

    [Theory]
    [InlineData("12.3", "redondeo a un decimal de 12,35")]
    [InlineData("12.4", "redondeo a un decimal hacia arriba")]
    [InlineData("12.299999999999999", "ruido de un double serializado")]
    [InlineData("12", "redondeo a kilo entero")]
    [InlineData("12.35", "exacto")]
    public void LaHuellaToleraElRedondeoDelCubicadorEnUnaFilaConDecimales(string pesoFila, string porque)
    {
        // Auditoría de f3c79d5: 1 unidad de 12,35 kg (máximo 2 decimales en Cotifly); el cubicador confirmó (2026-10-15) que redondea
        // el pesoKg a 2 decimales; estos casos más gruesos (un decimal, kilo entero) siguen cubiertos por la tolerancia de 0,5 kg.
        var peticion = new clsPeticionRecomendacion(
            RutaId, "Carga Suelta", "1234567", [new clsMercanciaCubicador("CAFE_EN_ESTIBAS", 1, 120m, 100m, 150m, 12.35m)]);
        var json = "{\"resultado\":\"ASIGNADO\",\"vehiculos\":[{\"mercancias\":[{\"indicePosicion\":0,\"codigoMercancia\":\"CAFE_EN_ESTIBAS\","
            + "\"cantidad\":1,\"pesoKg\":" + pesoFila + "}]}]}";
        using var documento = JsonDocument.Parse(json);

        clsCubicadorHttp.DiferenciaConLaCarga(documento.RootElement, peticion).Should().BeNull(porque);
    }

    [Fact]
    public void LaHuellaRechazaMedioKiloOMasDeDiferenciaPorFila()
    {
        var peticion = new clsPeticionRecomendacion(
            RutaId, "Carga Suelta", "1234567", [new clsMercanciaCubicador("CAFE_EN_ESTIBAS", 1, 120m, 100m, 150m, 12.35m)]);
        using var documento = JsonDocument.Parse(
            "{\"resultado\":\"ASIGNADO\",\"vehiculos\":[{\"mercancias\":[{\"indicePosicion\":0,\"codigoMercancia\":\"CAFE_EN_ESTIBAS\",\"cantidad\":1,\"pesoKg\":12.9}]}]}");

        clsCubicadorHttp.DiferenciaConLaCarga(documento.RootElement, peticion).Should().Contain("posición 0 pesa");
        Diferencia(AsignadoDeLaCarga(cantidadCafe: 19), PeticionConPesos()).Should().Contain("19 unidad(es)");
    }

    [Fact]
    public void LaHuellaRepartidaEnVariosCamionesSeSumaPorPosicion()
    {
        var flota = clsCubicadorFalso.Asignado(
            clsCubicadorFalso.VehiculoCompleto(1, "3S3", "Tractomula 3S3", "FURGON", 7_000m, false, false,
                clsCubicadorFalso.MercanciaVehiculo(0, "CAFE_EN_ESTIBAS", 10, 7_000m, 18m)),
            clsCubicadorFalso.VehiculoCompleto(2, "C2", "Sencillo C2", "FURGON", 8_700m, false, true,
                clsCubicadorFalso.MercanciaVehiculo(0, "cafe_en_estibas", 10, 7_000m, 18m), clsCubicadorFalso.MercanciaVehiculo(1, "BOBINAS", 1, 1_700m, 7.6m)));

        Diferencia(flota, PeticionConPesos()).Should().BeNull();
    }

    [Fact]
    public void SinDetalleComprobableLaCorridaNoSeAtribuye()
    {
        var sinIndice = clsCubicadorFalso.Asignado(clsCubicadorFalso.VehiculoCompleto(1, "3S3", "Tractomula 3S3", "FURGON", 15_700m, false, false,
            clsCubicadorFalso.MercanciaVehiculo(null, "CAFE_EN_ESTIBAS", 20, 14_000m, 36m), clsCubicadorFalso.MercanciaVehiculo(null, "BOBINAS", 1, 1_700m, 7.6m)));
        var sinDetalle = clsCubicadorFalso.Asignado(clsCubicadorFalso.Vehiculo("3S3", "FURGON", 15_700m));

        Diferencia(sinIndice, PeticionConPesos()).Should().Contain("indicePosicion");
        Diferencia(sinDetalle, PeticionConPesos()).Should().Contain("sin detalle");
        Diferencia(AsignadoDeLaCarga(), Peticion()).Should().Contain("no trae el peso", "la petición sin peso no se puede comparar");
        Diferencia(clsCubicadorFalso.SinSeleccion("BUSQUEDA_AGOTADA"), PeticionConPesos()).Should().Contain("no se puede comprobar");
    }

    [Fact]
    public void UnaSinSeleccionConElEcoDeLaPeticionSoloSeAceptaSiCoincide()
    {
        static Dictionary<string, object?> ConEco(decimal pesoCafe, decimal largoCafe)
        {
            var respuesta = clsCubicadorFalso.SinSeleccion("BUSQUEDA_AGOTADA");
            respuesta["mercancias"] = new List<object>
            {
                new { codigoMercancia = "CAFE_EN_ESTIBAS", cantidad = 20, largoCm = largoCafe, anchoCm = 100m, altoCm = 150m, pesoKg = pesoCafe },
                new { codigoMercancia = "BOBINAS", cantidad = 1, largoCm = 1200m, anchoCm = 235m, altoCm = 269m, pesoKg = 1_700m },
            };
            return respuesta;
        }

        Diferencia(ConEco(700m, 120m), PeticionConPesos()).Should().BeNull();
        Diferencia(ConEco(50m, 120m), PeticionConPesos()).Should().Contain("peso por unidad");
        Diferencia(ConEco(700m, 130m), PeticionConPesos()).Should().Contain("largoCm");
    }

    [Fact]
    public void UnCodigoDistintoEnLaPosicionNoCoincide()
    {
        var otra = clsCubicadorFalso.Asignado(clsCubicadorFalso.VehiculoCompleto(1, "3S3", "Tractomula 3S3", "FURGON", 15_700m, false, false,
            clsCubicadorFalso.MercanciaVehiculo(0, "BEBIDAS", 20, 14_000m, 36m), clsCubicadorFalso.MercanciaVehiculo(1, "BOBINAS", 1, 1_700m, 7.6m)));

        Diferencia(otra, PeticionConPesos()).Should().Contain("BEBIDAS");
    }

    [Fact]
    public async Task ElCatalogoDeVehiculosSaleDeVehiculosCatalogo()
    {
        var tipos = await Cubicador().ListarTiposVehiculoAsync(Ct);

        tipos.Valor.Select(t => t.Codigo).Should().Contain(["2S2", "3S3", "C2"]);
        _cubicador.Peticiones.Should().ContainSingle().Which.Url.AbsolutePath.Should().EndWith("/vehiculos-catalogo");
    }

    [Fact]
    public async Task UnCatalogoColgadoSeCortaEnSuTopePropioYNoEnElTiempoDeLaTuberia()
    {
        _cubicador.TiposMercancia.Add((_tipoCafe, "Café en estibas"));
        _cubicador.DemoraCatalogo = TimeSpan.FromSeconds(60);
        var valores = Configurado();
        valores["Cubicador:TiempoMaximoCatalogoSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);
        var reloj = System.Diagnostics.Stopwatch.StartNew();

        var resultado = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().ListarTiposMercanciaAsync(Ct);

        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "el tope del catálogo (1 s) gana al de la tubería (3 × 30 s + 2 s)");
        resultado.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible);
        resultado.Error.Detalle.Should().Contain("tiempo máximo del catálogo");
    }

    [Fact]
    public void ElTiempoTotalDejaCaberLosReintentosDelGet()
    {
        var intento = TimeSpan.FromSeconds(30);

        clsCubicadorHttp.TiempoTotal(intento).Should().BeGreaterThan(intento * (clsCubicadorHttp.ReintentosLectura + 1));
    }

    public void Dispose()
    {
        _http.Dispose();
        _cubicador.Dispose();
    }

    private clsCubicadorHttp Cubicador(clsOpcionesCubicador? opciones = null) => new(
        _http,
        Options.Create(opciones ?? new clsOpcionesCubicador { UrlBase = clsCubicadorFalso.UrlBase, ApiKey = clsCubicadorFalso.ApiKey }),
        _cacheMercancias,
        _cacheCarrocerias,
        _cacheVehiculos,
        NullLogger<clsCubicadorHttp>.Instance);

    private readonly clsCacheCatalogoCubicador<clsTipoMercanciaCubicador> _cacheMercancias = new(TimeProvider.System);
    private readonly clsCacheCatalogoCubicador<clsTipoCarroceriaCubicador> _cacheCarrocerias = new(TimeProvider.System);
    private readonly clsCacheCatalogoCubicador<clsTipoVehiculoCubicador> _cacheVehiculos = new(TimeProvider.System);

    private clsCubicadorHealthCheck Sonda(clsOpcionesCubicador opciones)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(Cubicador(opciones));
        return new clsCubicadorHealthCheck(servicios.BuildServiceProvider(), Options.Create(opciones), NullLogger<clsCubicadorHealthCheck>.Instance);
    }

    private static Dictionary<string, string?> Configurado() => new()
    {
        ["Cubicador:UrlBase"] = clsCubicadorFalso.UrlBase,
        ["Cubicador:ApiKey"] = clsCubicadorFalso.ApiKey,
    };

    private static ServiceProvider Proveedor(Dictionary<string, string?> valores, clsCubicadorFalso? falso = null)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new clsEntornoFalso("Development"));
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        if (falso is not null)
        {
            servicios.AddHttpClient(clsCubicadorHttp.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => falso);
        }

        return servicios.BuildServiceProvider();
    }
}
