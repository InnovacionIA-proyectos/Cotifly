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

    [Fact]
    public void ElTiempoDeEsperaPorDefectoEsDe30Segundos() => new clsOpcionesCubicador().TiempoEsperaSegundos.Should().Be(30);

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
    public async Task Un422DelCubicadorEsUnErrorVisibleNoUnaCaida()
    {
        _cubicador.ErrorRecomendacion = (HttpStatusCode.UnprocessableEntity, "VALIDACION");

        var r = await Cubicador().RecomendarAsync(Peticion(), Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorRespuestaInvalida);
        r.Error.Mensaje.Should().Contain("422");
        clsErroresCubicador.EsCaida(r.Error).Should().BeFalse("un 422 nunca va al respaldo");
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
        _cubicador.Recomendar = _ => clsCubicadorFalso.Asignado(clsCubicadorFalso.VehiculoCompleto(
            1, "3S3", "Tractomula 3S3", "FURGON", 15_700m, false, true,
            clsCubicadorFalso.MercanciaVehiculo(0, "CAFE_EN_ESTIBAS", 20, 14_000m, 36m), clsCubicadorFalso.MercanciaVehiculo(1, "BOBINAS", 1, 1_700m, 7.6m)));
        _cubicador.TiempoAgotadoTrasGuardar = true;
        var valores = Configurado();
        valores["Cubicador:TiempoEsperaSegundos"] = "1";
        using var proveedor = Proveedor(valores, _cubicador);

        var r = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);

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
        using var proveedor = Proveedor(valores, _cubicador);

        var r = await proveedor.CreateScope().ServiceProvider.GetRequiredService<IClsCubicador>().RecomendarAsync(Peticion(), Ct);

        r.Error.Codigo.Should().Be(clsCodigosError.CubicadorNoDisponible, "la corrida guardada es de otra carga (5 unidades, no 20)");
        _cubicador.Recomendaciones.Should().ContainSingle();
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
