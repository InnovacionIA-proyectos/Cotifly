using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Municipios.DTOs;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// HU-005.13 en el agente: <c>resolver_municipio</c> resuelve el origen y el destino escritos en texto libre contra el catálogo de
/// municipios de Nexo; <c>clsEstadoConversacionService</c> guarda el municipio o los candidatos (ADR-004) y el filtro de inyección no
/// deja cotizar con un municipio ambiguo sin elegir. El modelo va guionizado y Nexo es un doble: sin red.
/// </summary>
public sealed class clsResolverMunicipioTests
{
    private static readonly clsMunicipioResuelto _unionAntioquia = new(1, "La Unión", "Antioquia", "Colombia", "05400");
    private static readonly clsMunicipioResuelto _unionNarino = new(2, "La Unión", "Nariño", "Colombia", "52399");
    private static readonly clsMunicipioResuelto _unionValle = new(3, "La Unión", "Valle del Cauca", "Colombia", "76400");
    private static readonly clsMunicipioResuelto _yumbo = new(5, "Yumbo", "Valle del Cauca", "Colombia", "76892");
    private static readonly clsMunicipioResuelto _sanPedroAntioquia = new(10, "San Pedro", "Antioquia", "Colombia");
    private static readonly clsMunicipioResuelto _sanPedroSucre = new(11, "San Pedro", "Sucre", "Colombia");
    private static readonly clsMunicipioResuelto _argeliaAntioquia = new(20, "Argelia", "Antioquia", "Colombia");
    private static readonly clsMunicipioResuelto _argeliaCauca = new(21, "Argelia", "Cauca", "Colombia");
    private static readonly clsMunicipioResuelto _argeliaValle = new(22, "Argelia", "Valle del Cauca", "Colombia");

    private readonly clsEstadoConversacionService _servicio = new();
    private readonly clsArnesAgente _arnes = new();

    // --- Estado (clsEstadoConversacionService) ---

    [Fact]
    public void UnaSolaCoincidenciaGuardaElMunicipioCanonicoEnElCampo()
    {
        var estado = Aplicar(clsEstadoConversacion.Vacio, Unica(_yumbo, "Yumbo"), "origen");

        estado.OrigenMunicipio.Should().Be(_yumbo);
        estado.Origen.Should().Be("Yumbo, Valle del Cauca");
        estado.CandidatosOrigen.Should().BeEmpty();
        estado.DestinoMunicipio.Should().BeNull();
        estado.TieneMunicipiosPorConfirmar().Should().BeFalse();
    }

    [Fact]
    public void VariasCoincidenciasQuedanComoCandidatosDelCampoSinMunicipio()
    {
        var estado = Aplicar(clsEstadoConversacion.Vacio, Varias("La Unión", _unionAntioquia, _unionNarino, _unionValle), "destino");

        estado.DestinoMunicipio.Should().BeNull();
        estado.Destino.Should().Be("La Unión");
        estado.CandidatosDestino.Should().Equal(_unionAntioquia, _unionNarino, _unionValle);
        estado.CandidatosOrigen.Should().BeEmpty();
        estado.TieneMunicipiosPorConfirmar().Should().BeTrue();
    }

    [Fact]
    public void UnMunicipioAmbiguoDescartaElPrecioYLaCotizacionDeLaRutaAnterior()
    {
        var anterior = clsEstadoConversacion.Vacio with
        {
            Origen = "Yumbo, Valle del Cauca",
            OrigenMunicipio = _yumbo,
            Costo = 2_850_000m,
            FuenteCosto = clsEstadoConversacion.FuenteCostoManual,
            PrecioFinal = 3_294_798m,
            ConfirmacionPendiente = true,
            CotizacionId = Guid.NewGuid(),
            FolioEnProgreso = "PCN-NV-24092026-001",
        };

        var estado = Aplicar(anterior, Varias("La Unión", _unionAntioquia, _unionNarino), "origen");

        estado.OrigenMunicipio.Should().BeNull();
        estado.Costo.Should().BeNull();
        estado.PrecioFinal.Should().BeNull();
        estado.ConfirmacionPendiente.Should().BeFalse();
        estado.CotizacionId.Should().BeNull("otra ruta es otra cotización, no una versión de la anterior");
    }

    [Fact]
    public void ElMismoMunicipioNoDescartaElPrecio()
    {
        var anterior = clsEstadoConversacion.Vacio with { Origen = "Yumbo", Costo = 2_850_000m, PrecioFinal = 3_294_798m, ConfirmacionPendiente = true };

        var estado = Aplicar(anterior, Unica(_yumbo, "yumbo"), "origen");

        estado.OrigenMunicipio.Should().Be(_yumbo);
        estado.PrecioFinal.Should().Be(3_294_798m);
        estado.ConfirmacionPendiente.Should().BeTrue();
    }

    [Fact]
    public void SinCoincidenciasNoSeInventaUnMunicipio()
    {
        var anterior = clsEstadoConversacion.Vacio with { CandidatosOrigen = [_unionAntioquia, _unionNarino], Origen = "La Unión" };
        var ninguna = new clsResolucionMunicipio("Macondo", CoincidenciaMunicipio.Ninguna, [], 0);

        var estado = Aplicar(anterior, ninguna, "origen");

        estado.OrigenMunicipio.Should().BeNull();
        estado.Origen.Should().Be("La Unión", "no se escribe un origen que no está en el catálogo");
        estado.CandidatosOrigen.Should().BeEmpty();
    }

    [Fact]
    public void SinCatalogoElTextoLibreQuedaComoRutaSinBloquear()
    {
        var error = clsResultadoHerramienta.Fallo(clsNombresHerramientas.ResolverMunicipio, clsCodigosError.MunicipiosNoDisponible, "no disponible");

        var estado = _servicio.Aplicar(clsEstadoConversacion.Vacio, error, Argumentos("La Unión", "origen"));

        estado.Origen.Should().Be("La Unión");
        estado.OrigenMunicipio.Should().BeNull();
        estado.TieneMunicipiosPorConfirmar().Should().BeFalse("sin catálogo no hay a qué confirmar: se cotiza con el texto");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ruta")]
    public void UnCampoDesconocidoNoCambiaElEstado(string? campo)
    {
        var estado = Aplicar(clsEstadoConversacion.Vacio, Unica(_yumbo, "Yumbo"), campo);

        estado.Should().Be(clsEstadoConversacion.Vacio);
    }

    [Theory]
    [InlineData("La de Nariño", 2)]
    [InlineData("la union narino", 2)]
    [InlineData("Valle del Cauca", 3)]
    [InlineData("2", 2)]
    [InlineData("opción 3", 3)]
    [InlineData("la primera", 1)]
    public void ElComercialEligeElMunicipioPorDepartamentoOPosicion(string mensaje, int id)
    {
        var pendiente = clsEstadoConversacion.Vacio with { Origen = "La Unión", CandidatosOrigen = [_unionAntioquia, _unionNarino, _unionValle] };

        var reconocido = _servicio.ReconocerMensaje(pendiente, mensaje);

        reconocido.Confirmado.Should().BeFalse("elegir el municipio no confirma el PDF");
        reconocido.Estado.OrigenMunicipio!.Id.Should().Be(id);
        reconocido.Estado.CandidatosOrigen.Should().BeEmpty();
        reconocido.Estado.Origen.Should().Be(reconocido.Estado.OrigenMunicipio.Canonico());
    }

    [Fact]
    public void ConOrigenYDestinoAmbiguosLaPosicionNoEligeNingunoPeroElDepartamentoSi()
    {
        var pendiente = clsEstadoConversacion.Vacio with
        {
            CandidatosOrigen = [_unionAntioquia, _unionNarino],
            CandidatosDestino = [_unionNarino, _unionValle],
        };

        _servicio.ReconocerMensaje(pendiente, "2").Estado.Should().Be(pendiente, "«2» no dice si es el origen o el destino");

        var porDepartamento = _servicio.ReconocerMensaje(pendiente, "el de Antioquia").Estado;
        porDepartamento.OrigenMunicipio.Should().Be(_unionAntioquia);
        porDepartamento.CandidatosDestino.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("el origen es el de Antioquia")]
    [InlineData("Origen: Antioquia")]
    public void NombrarElCampoEligeSoloEnEsaLista(string mensaje)
    {
        var pendiente = clsEstadoConversacion.Vacio with
        {
            Origen = "San Pedro",
            CandidatosOrigen = [_sanPedroAntioquia, _sanPedroSucre],
            Destino = "La Unión",
            CandidatosDestino = [_unionAntioquia, _unionNarino, _unionValle],
        };

        var estado = _servicio.ReconocerMensaje(pendiente, mensaje).Estado;

        estado.OrigenMunicipio.Should().Be(_sanPedroAntioquia);
        estado.DestinoMunicipio.Should().BeNull("el comercial solo habló del origen");
        estado.CandidatosDestino.Should().HaveCount(3);
    }

    [Fact]
    public void ConLasDosListasYElDepartamentoEnAmbasNoEligeNinguna()
    {
        var pendiente = clsEstadoConversacion.Vacio with
        {
            CandidatosOrigen = [_sanPedroAntioquia, _sanPedroSucre],
            CandidatosDestino = [_unionAntioquia, _unionNarino],
        };

        _servicio.ReconocerMensaje(pendiente, "el de Antioquia").Estado.Should().Be(pendiente, "no dice si es el origen o el destino");
    }

    [Theory]
    [InlineData("el destino es Medellín, Antioquia")]
    [InlineData("Medellín, Antioquia")]
    [InlineData("no es el de Nariño")]
    [InlineData("No, la de Nariño no")]
    public void UnMensajeQueNoEligeUnCandidatoNoEligeNada(string mensaje)
    {
        var pendiente = clsEstadoConversacion.Vacio with { Origen = "La Unión", CandidatosOrigen = [_unionAntioquia, _unionNarino, _unionValle] };

        var estado = _servicio.ReconocerMensaje(pendiente, mensaje).Estado;

        estado.OrigenMunicipio.Should().BeNull();
        estado.CandidatosOrigen.Should().HaveCount(3);
    }

    // N1: un mensaje en minúsculas sobre otro lugar tampoco elige; N3: las palabras de relleno sí dejan elegir.
    [Theory]
    [InlineData("va para medellin antioquia")]
    [InlineData("el cliente es de antioquia")]
    [InlineData("la carga sale de antioquia hacia narino")]
    public void UnMensajeConPalabrasQueNoSonDeLaEleccionNoEligeNada(string mensaje)
    {
        var pendiente = clsEstadoConversacion.Vacio with { Origen = "La Unión", CandidatosOrigen = [_unionAntioquia, _unionNarino, _unionValle] };

        _servicio.ReconocerMensaje(pendiente, mensaje).Estado.Should().Be(pendiente);
    }

    [Theory]
    [InlineData("Exacto, la de Nariño")]
    [InlineData("Pues la de Nariño")]
    [InlineData("Elijo la de Nariño")]
    [InlineData("Mejor la de Nariño")]
    [InlineData("Sería la de Nariño, por favor")]
    [InlineData("Escojo esa misma, la de Nariño")]
    [InlineData("Quiero la de Nariño")]
    public void LasPalabrasDeRellenoDejanElegirPorDepartamento(string mensaje)
    {
        var pendiente = clsEstadoConversacion.Vacio with { Origen = "La Unión", CandidatosOrigen = [_unionAntioquia, _unionNarino, _unionValle] };

        _servicio.ReconocerMensaje(pendiente, mensaje).Estado.OrigenMunicipio.Should().Be(_unionNarino);
    }

    [Theory]
    [InlineData("Valle del Cauca", 22)]
    [InlineData("Argelia, Valle del Cauca", 22)]
    [InlineData("la del Cauca", 21)]
    public void DeDepartamentosAnidadosGanaElNombradoMasLargo(string mensaje, int id)
    {
        var pendiente = clsEstadoConversacion.Vacio with { Origen = "Argelia", CandidatosOrigen = [_argeliaAntioquia, _argeliaCauca, _argeliaValle] };

        _servicio.ReconocerMensaje(pendiente, mensaje).Estado.OrigenMunicipio!.Id.Should().Be(id);
    }

    [Fact]
    public void UnConfirmoConMunicipioPendienteNoConfirmaLaPrevisualizacion()
    {
        var pendiente = clsEstadoConversacion.Vacio with
        {
            Origen = "La Unión",
            CandidatosOrigen = [_unionAntioquia, _unionNarino],
            PrecioFinal = 3_000_000m,
            ConfirmacionPendiente = true,
        };

        _servicio.ReconocerMensaje(pendiente, "Confirmo").Confirmado.Should().BeFalse("el municipio sigue sin elegir");
    }

    [Fact]
    public void UnaEntradaMuyLargaNoRompeElReconocimiento()
    {
        var largo = string.Concat(Enumerable.Repeat("La Unión, Nariño (", 20_000));

        _servicio.ReconocerMensaje(clsEstadoConversacion.Vacio with { CandidatosOrigen = [_unionAntioquia, _unionNarino] }, largo).Should().NotBeNull();
    }

    [Fact]
    public void ConClientesCandidatosLaPosicionEligeAlClienteYNoAlMunicipio()
    {
        var pendiente = clsEstadoConversacion.Vacio with
        {
            CandidatosCliente = [new clsCandidatoCliente("1", "A", Dominio.Clientes.OrigenCliente.Cliente), new clsCandidatoCliente("2", "B", Dominio.Clientes.OrigenCliente.Cliente)],
            CandidatosOrigen = [_unionAntioquia, _unionNarino],
        };

        var estado = _servicio.ReconocerMensaje(pendiente, "1").Estado;

        estado.NitCliente.Should().Be("1");
        estado.CandidatosOrigen.Should().HaveCount(2);
    }

    [Fact]
    public void UnaTarifaConOtroOrigenDejaElMunicipioSinVerificar()
    {
        var anterior = clsEstadoConversacion.Vacio with { Origen = "Yumbo, Valle del Cauca", OrigenMunicipio = _yumbo };
        var tarifa = clsResultadoHerramienta.Exito(clsNombresHerramientas.BuscarTarifa, new { }, new Tarifas.DTOs.clsSalidaTarifa(false, null, "COP", "nexo"));

        var mismo = _servicio.Aplicar(anterior, tarifa, new Dictionary<string, object?> { ["origen"] = "Yumbo, Valle del Cauca" });
        var otro = _servicio.Aplicar(anterior, tarifa, new Dictionary<string, object?> { ["origen"] = "Palmira" });

        mismo.OrigenMunicipio.Should().Be(_yumbo);
        otro.OrigenMunicipio.Should().BeNull();
        otro.Origen.Should().Be("Palmira");
    }

    // --- Validador de la respuesta final (#16) ---

    [Fact]
    public void UnaPrevisualizacionConMunicipioPorConfirmarNoPasaLaValidacion()
    {
        var estado = clsEstadoConversacion.Vacio with { PrecioFinal = 3_294_798m, ConfirmacionPendiente = true, CandidatosOrigen = [_unionAntioquia, _unionNarino] };
        var crudo = Pruebas.Comun.clsChatClientGuionizado.JsonFinal("Previsualización lista.", "previsualizacion", true);

        var validacion = clsReglasRespuestaAgente.Validar(crudo, new clsContextoValidacionRespuesta(estado, [], [], []));

        validacion.Fallos.Select(f => f.Codigo).Should().Contain(clsReglasRespuestaAgente.PrevisualizacionMunicipioPendiente);
    }

    // --- Turnos con el modelo guionizado ---

    [Fact]
    public async Task ConUnaSolaCoincidenciaLaTarifaSeBuscaConElMunicipioCanonico()
    {
        _arnes.CatalogoMunicipios();
        _arnes.Modelo
            .Llamar("resolver_municipio", new { texto = "Yumbo", campo = "origen" })
            .Llamar("resolver_municipio", new { texto = "Buenaventura", campo = "destino" })
            .Llamar("buscar_tarifa", new { lineaNegocio = "Nacional", origen = "yumbo", destino = "Buenaventura", pesoKg = 34000 })
            .Cerrar("No hay tarifa para esa ruta: dame el costo del flete.");

        var resultado = (await _arnes.TurnoAsync("Cotiza Nacional de Yumbo a Buenaventura, 34 toneladas")).Valor;

        resultado.HerramientasInvocadas.Should().Equal("resolver_municipio", "resolver_municipio", "buscar_tarifa");
        var entrada = _arnes.EntradasTarifa.Should().ContainSingle().Subject;
        entrada.Origen.Should().Be("Yumbo, Valle del Cauca");
        entrada.Destino.Should().Be("Buenaventura, Valle del Cauca");
        var estado = _arnes.Estado();
        estado.OrigenMunicipio!.Id.Should().Be(5);
        estado.DestinoMunicipio!.Id.Should().Be(6);
        var salida = JsonDocument.Parse(_arnes.Modelo.Llamadas[1].ResultadosHerramientas[^1]).RootElement;
        salida.GetProperty("coincidencia").GetString().Should().Be("unica");
        salida.GetProperty("municipio").GetProperty("departamento").GetString().Should().Be("Valle del Cauca");
        clsPromptSistema.BloqueEstado(estado).Should()
            .Contain("- Ruta: Yumbo, Valle del Cauca (verificado en el catálogo de municipios) → Buenaventura, Valle del Cauca (verificado en el catálogo de municipios)");
    }

    [Fact]
    public async Task ConVariosMunicipiosElAgentePreguntaAntesDeCotizarYSigueConElQueEligeElComercial()
    {
        _arnes.CatalogoMunicipios();
        _arnes.Modelo
            .Llamar("resolver_municipio", new { texto = "La Unión", campo = "origen" })
            .Llamar("buscar_tarifa", new { lineaNegocio = "Nacional", origen = "La Unión", destino = "Bogotá" })
            .Cerrar("Hay tres municipios La Unión: Antioquia, Nariño o Valle del Cauca. ¿Cuál es?")
            .Llamar("buscar_tarifa", new { lineaNegocio = "Nacional", origen = "La Unión", destino = "Bogotá" })
            .Cerrar("No hay tarifa para esa ruta: dame el costo del flete.");

        var primero = (await _arnes.TurnoAsync("Cotiza Nacional de La Unión a Bogotá")).Valor;

        var resolucion = JsonDocument.Parse(_arnes.Modelo.Llamadas[1].ResultadosHerramientas[^1]).RootElement;
        resolucion.GetProperty("coincidencia").GetString().Should().Be("varias");
        resolucion.GetProperty("candidatos").EnumerateArray().Select(c => c.GetProperty("departamento").GetString())
            .Should().Equal("Antioquia", "Nariño", "Valle del Cauca");
        _arnes.Modelo.Llamadas[1].ResultadosHerramientas[^1].Should().NotContain("52399", "ni el id ni el código Dane van al modelo");
        var rechazo = JsonDocument.Parse(_arnes.Modelo.Llamadas[2].ResultadosHerramientas[^1]).RootElement.GetProperty("error");
        rechazo.GetProperty("codigo").GetString().Should().Be(clsCodigosError.ConfirmacionRequerida);
        _arnes.EntradasTarifa.Should().BeEmpty("con el origen ambiguo no se busca tarifa");
        primero.Texto.Should().Contain("¿Cuál es?");
        _arnes.Estado().CandidatosOrigen.Should().HaveCount(3);

        await _arnes.TurnoAsync("La de Nariño");

        _arnes.Modelo.Llamadas[4].Sistema.Should().Contain("La Unión, Nariño (verificado en el catálogo de municipios)");
        _arnes.EntradasTarifa.Should().ContainSingle().Which.Origen.Should().Be("La Unión, Nariño");
        _arnes.Estado().OrigenMunicipio.Should().Be(_unionNarino);
        _arnes.Estado().CandidatosOrigen.Should().BeEmpty();
    }

    [Fact]
    public async Task ConElMunicipioAmbiguoTampocoSeCalculaElPrecio()
    {
        _arnes.CatalogoMunicipios();
        _arnes.Modelo
            .Llamar("resolver_municipio", new { texto = "La Unión", campo = "destino" })
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("¿Cuál La Unión es el destino?");

        var resultado = (await _arnes.TurnoAsync("Cotiza Nacional de Yumbo a La Unión, costo 2.850.000")).Valor;

        resultado.Salidas[^1].Error!.Codigo.Should().Be(clsCodigosError.ConfirmacionRequerida);
        _arnes.Estado().PrecioFinal.Should().BeNull();
        clsPromptSistema.BloqueEstado(_arnes.Estado()).Should().Contain("- Ruta: La Unión (ambiguo: por confirmar)")
            .And.Contain("Municipios candidatos para el destino, por confirmar con el comercial: 1. La Unión, Antioquia; 2. La Unión, Nariño; 3. La Unión, Valle del Cauca");
    }

    [Fact]
    public async Task SinCatalogoDeMunicipiosElAgenteSigueConElTextoLibre()
    {
        _arnes.Modelo
            .Llamar("resolver_municipio", new { texto = "La Unión", campo = "origen" })
            .Llamar("buscar_tarifa", new { lineaNegocio = "Nacional", origen = "La Unión", destino = "Bogotá" })
            .Cerrar("El catálogo de municipios no está disponible; sigo con La Unión. No hay tarifa: dame el costo.");

        var resultado = (await _arnes.TurnoAsync("Cotiza Nacional de La Unión a Bogotá")).Valor;

        resultado.Salidas[0].Error!.Codigo.Should().Be(clsCodigosError.MunicipiosNoDisponible);
        _arnes.EntradasTarifa.Should().ContainSingle().Which.Origen.Should().Be("La Unión", "sin Nexo no se bloquea la cotización");
        _arnes.Estado().Origen.Should().Be("La Unión");
        _arnes.Estado().OrigenMunicipio.Should().BeNull();
    }

    [Theory]
    [InlineData("generar_pdf")]
    [InlineData("registrar_ajuste_margen")]
    [InlineData("calcular_precio")]
    [InlineData("buscar_tarifa")]
    public async Task ConElMunicipioAmbiguoElFiltroNoDejaCotizar(string herramienta)
    {
        _arnes.CatalogoMunicipios();
        object args = herramienta switch
        {
            "generar_pdf" => new { cotizacion = new { cliente = "X" } },
            "registrar_ajuste_margen" => new { margenAplicado = 0.2, motivo = "negociado" },
            "calcular_precio" => new { costo = 1_000_000, margen = 0.1, metodo = "margen" },
            _ => new { lineaNegocio = "Nacional", origen = "La Unión", destino = "Yumbo" },
        };
        _arnes.Modelo
            .Llamar("resolver_municipio", new { texto = "La Unión", campo = "origen" })
            .Llamar(herramienta, args)
            .Cerrar("¿Cuál La Unión?");

        var resultado = (await _arnes.TurnoAsync("Cotiza de La Unión a Yumbo")).Valor;

        resultado.Salidas[^1].Error!.Codigo.Should().Be(clsCodigosError.ConfirmacionRequerida);
    }

    [Theory]
    [InlineData("ruta", "La Unión")]
    [InlineData("", "La Unión")]
    [InlineData("origen", "  ")]
    public async Task ConArgumentosInvalidosLaHerramientaFallaSinConsultarNexo(string campo, string texto)
    {
        _arnes.CatalogoMunicipios();
        _arnes.Modelo
            .Llamar("resolver_municipio", new { texto, campo })
            .Cerrar("Corrijo la llamada.");

        var resultado = (await _arnes.TurnoAsync("de La Unión a Yumbo")).Valor;

        resultado.Salidas[0].Error!.Codigo.Should().Be(clsCodigosError.MunicipioArgumentoInvalido);
        await _arnes.Municipios.DidNotReceive().ObtenerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _arnes.Estado().Origen.Should().BeNull();
    }

    [Fact]
    public async Task ElPdfLlevaElOrigenYElDestinoVerificados()
    {
        _arnes.CatalogoMunicipios();
        _arnes.Modelo
            .Llamar("resolver_municipio", new { texto = "Yumbo", campo = "origen" })
            .Llamar("resolver_municipio", new { texto = "Buenaventura", campo = "destino" })
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("Previsualización lista. ¿Confirmas?")
            .Llamar("generar_pdf", new
            {
                cotizacion = new
                {
                    cliente = "Cliente del chat",
                    linea_negocio = "Nacional",
                    textos = new { intro = new[] { "Gracias." } },
                    tarifa = new { titulo = "COTIZACIÓN", origen = "YUMBO", filas = new[] { new { destino = "BUN", vehiculo_peso = "Tractomula" } } },
                },
            })
            .Cerrar("PDF listo.");

        await _arnes.TurnoAsync("Cotiza Nacional de Yumbo a Buenaventura, costo 2.850.000");
        await _arnes.TurnoAsync("Confirmo");

        var tarifa = _arnes.EntradasPdf.Should().ContainSingle().Subject.Cotizacion.Tarifa!;
        tarifa.Origen.Should().Be("Yumbo, Valle del Cauca");
        tarifa.Filas!.Should().ContainSingle().Which.Destino.Should().Be("Buenaventura, Valle del Cauca");
    }

    [Fact]
    public async Task LaSalidaDeVariasNoLlevaIdsNiDane()
    {
        _arnes.CatalogoMunicipios();
        _arnes.Modelo.Llamar("resolver_municipio", new { texto = "La Unión", campo = "origen" }).Cerrar("¿Cuál?");

        await _arnes.TurnoAsync("de La Unión");

        _arnes.Modelo.Llamadas[1].ResultadosHerramientas[^1].Should().NotContainAny("05400", "52399", "76400", "\"id\"", "codigo_dane", "codigoDane");
        clsPromptSistema.BloqueEstado(_arnes.Estado()).Should().NotContainAny("05400", "52399", "76400");
    }

    [Fact]
    public void ElPromptLeExplicaAlModeloLaHerramienta()
    {
        clsPromptSistema.Base.Should().Contain("resolver_municipio(texto, campo)")
            .And.Contain("no busques tarifa ni calcules precio hasta que elija")
            .And.Contain("MUNICIPIOS_NO_DISPONIBLE");
    }

    private clsEstadoConversacion Aplicar(clsEstadoConversacion estado, clsResolucionMunicipio resolucion, string? campo) =>
        _servicio.Aplicar(estado, clsResultadoHerramienta.Exito(clsNombresHerramientas.ResolverMunicipio, new { }, resolucion), Argumentos(resolucion.Texto, campo));

    private static Dictionary<string, object?> Argumentos(string texto, string? campo) => new() { ["texto"] = texto, ["campo"] = campo };

    private static clsResolucionMunicipio Unica(clsMunicipioResuelto municipio, string texto) => new(texto, CoincidenciaMunicipio.Unica, [municipio], 1);

    private static clsResolucionMunicipio Varias(string texto, params clsMunicipioResuelto[] candidatos) =>
        new(texto, CoincidenciaMunicipio.Varias, candidatos, candidatos.Length);
}
