using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Pruebas adversariales de la escritura del diccionario del cliente: aislamiento entre clientes, términos generales intocables por
/// cualquier vía, rol activo de un usuario con varios roles, largos en el límite exacto, unicode, cuerpos malformados y NEXO caído en
/// medio de la comprobación previa. Ninguna escritura llega a NEXO cuando Cotifly debe cortar antes.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsDiccionarioClienteAdversarialTests : IDisposable
{
    private const string Ruta = "/web/diccionario-cliente";
    private const string RutaId = "/web/diccionario-cliente/{id}";
    private const string RutaNexo = "/api/diccionario-cliente";
    private const string NitA = "900.555.111-2";
    private const string NitANormalizado = "900555111";
    private const string NitB = "800111222";

    private readonly clsNexoFalso _nexo = new();
    private readonly clsApiWebPruebas _api;
    private readonly clsBaseDatosPruebas _baseDatos;

    public clsDiccionarioClienteAdversarialTests(clsEntornoApiWeb entorno)
    {
        ArgumentNullException.ThrowIfNull(entorno);
        _baseDatos = entorno.BaseDatos;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Cuerpo(
        string? nit = NitA, string? termino = "Tracto", string? tipo = "valor", string? concepto = "3S3", string? contexto = "tipoVehiculo",
        string? ejemplo = null, bool? activo = null) =>
        JsonSerializer.Serialize(new { nit_cliente = nit, termino, tipo, concepto, contexto, ejemplo, activo });

    private Task<clsSesionPrueba> ComercialAsync() => _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);

    private Guid TerminoDe(string nit, string termino = "Mula del cliente", bool activo = true)
    {
        var nuevo = new clsTerminoFalso(clsNexoFalso.ClienteIdDeNit(nit), termino, "valor", "3S2", "tipoVehiculo") { Activo = activo };
        _nexo.Diccionario.Add(nuevo);
        return nuevo.Id;
    }

    private Guid General() => _nexo.Diccionario.First(t => t.ClienteId is null).Id;

    private int EscriturasEnNexo() =>
        _nexo.Peticiones.Count(p => p.Metodo != HttpMethod.Get && p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));

    // --- Aislamiento entre clientes ---

    [Fact]
    public async Task BorrarConMiNitUnTerminoDeOtroClienteEs404YNoLoBorra()
    {
        var sesion = await ComercialAsync();
        var ajeno = TerminoDe(NitB, "Mula ajena");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{ajeno}?nit_cliente={NitANormalizado}", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Delete, HttpStatusCode.NotFound);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("TERMINO_NO_ENCONTRADO");
        _nexo.Diccionario.Should().Contain(t => t.Id == ajeno);
        EscriturasEnNexo().Should().Be(0);
    }

    [Fact]
    public async Task LaConsultaConMiNitNoTraeTerminosDeOtroCliente()
    {
        var sesion = await ComercialAsync();
        TerminoDe(NitA, "Mula mía");
        TerminoDe(NitB, "Mula ajena");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={Uri.EscapeDataString(NitA)}", sesion.Access);

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Get, HttpStatusCode.OK);
        var terminos = json.GetProperty("terminos").EnumerateArray().Select(t => t.GetProperty("termino").GetString()).ToList();
        terminos.Should().Contain("Mula mía").And.NotContain("Mula ajena");
        json.GetProperty("aviso").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// Observación (no defecto): Cotifly no ata el NIT al comercial. Con el NIT del otro cliente (que puede leer de cualquier licitación)
    /// un comercial edita los términos de ese cliente, igual que hoy pasa con las plantillas y los formatos de lectura. Ni la issue ni el
    /// contrato de NEXO piden ese control (NEXO deja escribir a cualquier Comercial).
    /// </summary>
    [Fact]
    public async Task ConElNitDelOtroClienteUnComercialEditaSusTerminosNoHayControlDeCartera()
    {
        var sesion = await ComercialAsync();
        var ajeno = TerminoDe(NitB, "Mula ajena");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{ajeno}", sesion.Access, Cuerpo(nit: NitB, termino: "Mula cambiada"));

        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);
        _nexo.Diccionario.Single(t => t.Id == ajeno).Termino.Should().Be("Mula cambiada");
    }

    /// <summary>
    /// Si NEXO dejara de filtrar por NIT y en el diccionario solo hubiera términos de UN cliente (otro), la respuesta trae un único cliente;
    /// la consulta de control con un NIT sin cliente devuelve ese mismo término y delata que NEXO no filtró: no se muestra ni se edita.
    /// </summary>
    [Fact]
    public async Task SiNexoNoFiltraYSoloHayTerminosDeOtroClienteNoSeEditan()
    {
        var sesion = await ComercialAsync();
        var ajeno = TerminoDe(NitB, "Mula ajena");
        _nexo.DiccionarioIgnoraNit = true;

        using var consulta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={NitANormalizado}", sesion.Access);
        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{ajeno}", sesion.Access, Cuerpo(termino: "Mula cambiada"));

        _nexo.Diccionario.Single(t => t.Id == ajeno).Termino.Should().Be("Mula ajena", "un término de otro cliente no se edita");
        editar.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await VerificarContratoAsync(consulta, Ruta, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("terminos").EnumerateArray().Should().OnlyContain(t => t.GetProperty("general").GetBoolean());
    }

    [Fact]
    public async Task SiNexoNoFiltraYSoloHayTerminosDeOtroClienteNoSeBorran()
    {
        var sesion = await ComercialAsync();
        var ajeno = TerminoDe(NitB, "Mula ajena");
        _nexo.DiccionarioIgnoraNit = true;

        using var borrar = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{ajeno}?nit_cliente={NitANormalizado}", sesion.Access);

        borrar.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await VerificarErrorNegocioAsync(borrar, "TERMINO_NO_ENCONTRADO");
        _nexo.Diccionario.Should().Contain(t => t.Id == ajeno);
        _nexo.Peticiones.Should().NotContain(p => p.Metodo == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("900.555.111-2")]
    [InlineData("900555111")]
    [InlineData(" 900 555 111-7 ")]
    public async Task ElNitEnCualquierFormatoVeYEditaLosTerminosDeSuCliente(string nit)
    {
        var sesion = await ComercialAsync();
        var mio = TerminoDe(NitA, "Mula mía");

        using var consulta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={Uri.EscapeDataString(nit)}", sesion.Access);
        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{mio}", sesion.Access, Cuerpo(nit: nit, termino: "Mula mía editada"));

        var json = await VerificarContratoAsync(consulta, Ruta, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("terminos").EnumerateArray().Should().Contain(t => t.GetProperty("id").GetGuid() == mio && !t.GetProperty("general").GetBoolean());
        editar.StatusCode.Should().Be(HttpStatusCode.OK);
        _nexo.Diccionario.Single(t => t.Id == mio).Termino.Should().Be("Mula mía editada");
    }

    [Fact]
    public async Task UnTerminoSinDatosDeClienteNoSeMuestraNiSeEdita()
    {
        var sesion = await ComercialAsync();
        var mio = TerminoDe(NitA, "Mula mía");
        _nexo.DiccionarioSinClienteId = true;

        using var consulta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={NitANormalizado}", sesion.Access);
        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{mio}", sesion.Access, Cuerpo(termino: "Cambiada"));

        var json = await VerificarContratoAsync(consulta, Ruta, HttpMethod.Get, HttpStatusCode.OK);
        json.GetProperty("terminos").GetArrayLength().Should().Be(0, "sin clienteId no se sabe si es general ni de quién es");
        editar.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _nexo.Diccionario.Single(t => t.Id == mio).Termino.Should().Be("Mula mía");
    }

    // --- Términos generales: por ninguna vía ---

    [Fact]
    public async Task UnPutAUnGeneralConCamposDeClienteEnElCuerpoSigueSiendo403()
    {
        var sesion = await ComercialAsync();
        var general = General();
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = NitA, termino = "Tractomula", tipo = "valor", concepto = "3S3", contexto = "tipoVehiculo",
            general = false, cliente_id = clsNexoFalso.ClienteIdDeNit(NitA), clienteId = clsNexoFalso.ClienteIdDeNit(NitA), clienteNit = NitA,
        });

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{general}", sesion.Access, cuerpo);

        respuesta.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        if (respuesta.StatusCode == HttpStatusCode.Forbidden)
        {
            await VerificarErrorNegocioAsync(respuesta, "TERMINO_GENERAL_NO_EDITABLE");
        }

        EscriturasEnNexo().Should().Be(0);
        _nexo.Diccionario.Single(t => t.Id == general).ClienteId.Should().BeNull();
    }

    [Fact]
    public async Task CamposDeClienteColadosEnElCuerpoDeUnPutPropioNoViajanANexo()
    {
        var sesion = await ComercialAsync();
        var propio = TerminoDe(NitA);
        var cuerpo = JsonSerializer.Serialize(new
        {
            nit_cliente = NitA, termino = "Mula editada", tipo = "valor", concepto = "3S2", contexto = "tipoVehiculo",
            general = true, cliente_id = (Guid?)null, clienteId = Guid.NewGuid(), clienteNit = NitB, id = Guid.NewGuid(),
        });

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{propio}", sesion.Access, cuerpo);

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Put, HttpStatusCode.OK);
        json.GetProperty("general").GetBoolean().Should().BeFalse();
        var enviado = JsonNode.Parse(_nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Put).Cuerpo!)!.AsObject();
        enviado.Select(p => p.Key).Should().BeSubsetOf(["termino", "tipo", "concepto", "contexto", "ejemplo", "activo"]);
        _nexo.Diccionario.Single(t => t.Id == propio).ClienteId.Should().Be(clsNexoFalso.ClienteIdDeNit(NitA));
    }

    [Fact]
    public async Task UnPostConGeneralTrueYSinNitNoCreaUnTerminoGeneral()
    {
        var sesion = await ComercialAsync();
        var cuerpo = JsonSerializer.Serialize(new { termino = "Tracto", tipo = "valor", concepto = "3S3", contexto = "tipoVehiculo", general = true });

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, cuerpo);

        respuesta.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await VerificarErrorNegocioAsync(respuesta, "CLIENTE_REQUERIDO");
        EscriturasEnNexo().Should().Be(0);
    }

    [Fact]
    public async Task BorrarUnGeneralConElNitDeCualquierClienteSigueSiendo403()
    {
        var sesion = await ComercialAsync();
        var general = General();

        using var conB = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{general}?nit_cliente={NitB}", sesion.Access);
        using var conPuntos = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{general}?nit_cliente={Uri.EscapeDataString(NitA)}", sesion.Access);

        await VerificarErrorNegocioAsync(conB, "TERMINO_GENERAL_NO_EDITABLE");
        await VerificarErrorNegocioAsync(conPuntos, "TERMINO_GENERAL_NO_EDITABLE");
        conB.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        EscriturasEnNexo().Should().Be(0);
    }

    [Fact]
    public async Task UnIdEnMayusculasOConLlavesSeComparaComoGuid()
    {
        var sesion = await ComercialAsync();
        var general = General();

        using var mayusculas = await _api.EnviarAsync(HttpMethod.Delete,
            $"{Ruta}/{general.ToString().ToUpperInvariant()}?nit_cliente={NitANormalizado}", sesion.Access);
        using var llaves = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{general:B}?nit_cliente={NitANormalizado}", sesion.Access);

        await VerificarErrorNegocioAsync(mayusculas, "TERMINO_GENERAL_NO_EDITABLE");
        await VerificarErrorNegocioAsync(llaves, "TERMINO_GENERAL_NO_EDITABLE");
        EscriturasEnNexo().Should().Be(0);
    }

    // --- Autorización ---

    [Fact]
    public async Task UnUsuarioConVariosRolesQueEligioComercialEscribeYElQueEligioGerenteNo()
    {
        var persona = $"dic{Guid.NewGuid():N}"[..20];
        await _baseDatos.CrearPersonaExternaAsync(persona, "Persona multirol", $"{persona}@boterosoto.com.co",
            clsBaseDatosPruebas.RolExternoComercial, clsBaseDatosPruebas.RolExternoGerente);
        var comoComercial = await ElegirRolAsync(persona, "comercial");
        var comoGerente = await ElegirRolAsync(persona, "gerente_comercial");

        using var gerente = await _api.EnviarAsync(HttpMethod.Post, Ruta, comoGerente, Cuerpo(termino: "Del gerente"));
        using var gerenteConsulta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={NitANormalizado}", comoGerente);
        using var gerenteOpciones = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}/opciones", comoGerente);
        using var comercial = await _api.EnviarAsync(HttpMethod.Post, Ruta, comoComercial, Cuerpo(termino: "Del comercial"));

        var json = await VerificarContratoAsync(gerente, Ruta, HttpMethod.Post, HttpStatusCode.Forbidden);
        json.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
        gerenteConsulta.StatusCode.Should().Be(HttpStatusCode.OK);
        gerenteOpciones.StatusCode.Should().Be(HttpStatusCode.OK);
        comercial.StatusCode.Should().Be(HttpStatusCode.Created);
        _nexo.Diccionario.Should().NotContain(t => t.Termino == "Del gerente");
    }

    [Fact]
    public async Task SinSesionNingunaOperacionDelDiccionarioResponde()
    {
        var id = TerminoDe(NitA);

        using var consulta = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}?nit_cliente={NitANormalizado}", null);
        using var opciones = await _api.EnviarAsync(HttpMethod.Get, $"{Ruta}/opciones", null);
        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{id}", null, Cuerpo());
        using var borrar = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{id}?nit_cliente={NitANormalizado}", null);
        using var tokenFalso = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{id}?nit_cliente={NitANormalizado}", "no.es.token");

        foreach (var r in new[] { consulta, opciones, editar, borrar, tokenFalso })
        {
            r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));
    }

    // --- Validaciones en el límite ---

    [Theory]
    [InlineData("termino", 200, HttpStatusCode.Created)]
    [InlineData("termino", 201, HttpStatusCode.BadRequest)]
    [InlineData("concepto", 100, HttpStatusCode.Created)]
    [InlineData("concepto", 101, HttpStatusCode.BadRequest)]
    [InlineData("ejemplo", 500, HttpStatusCode.Created)]
    [InlineData("ejemplo", 501, HttpStatusCode.BadRequest)]
    [InlineData("contexto", 51, HttpStatusCode.BadRequest)]
    [InlineData("tipo", 101, HttpStatusCode.BadRequest)]
    public async Task LosLargosEnElLimiteExacto(string campo, int largo, HttpStatusCode esperado)
    {
        var sesion = await ComercialAsync();
        var texto = new string('ñ', largo);
        var cuerpo = campo switch
        {
            "termino" => Cuerpo(termino: texto),
            "concepto" => Cuerpo(concepto: texto),
            "ejemplo" => Cuerpo(ejemplo: texto),
            "contexto" => Cuerpo(contexto: texto),
            _ => Cuerpo(tipo: texto),
        };

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, cuerpo);

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, esperado);
        if (esperado == HttpStatusCode.BadRequest)
        {
            json.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String, "los largos de más son ErrorWeb");
            EscriturasEnNexo().Should().Be(0);
        }
    }

    [Fact]
    public async Task LosEspaciosDeLosExtremosNoCuentanParaElLargoYNoViajan()
    {
        var sesion = await ComercialAsync();
        var termino = new string('x', 200);

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(termino: $"   {termino}\t ", ejemplo: "  \n "));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Created);
        var enviado = JsonNode.Parse(_nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Post).Cuerpo!)!.AsObject();
        enviado["termino"]!.GetValue<string>().Should().Be(termino);
        enviado.ContainsKey("ejemplo").Should().BeFalse("un ejemplo en blanco viaja como ausente");
    }

    [Fact]
    public async Task UnicodeYCaracteresRarosViajanTalCual()
    {
        var sesion = await ComercialAsync();
        const string Termino = "Tractomula \"3S3\" ñandú 🚚 <b>&amp;</b> ' OR 1=1 --";

        using var respuesta = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, Cuerpo(termino: Termino, ejemplo: "línea1\nlínea2"));

        var json = await VerificarContratoAsync(respuesta, Ruta, HttpMethod.Post, HttpStatusCode.Created);
        json.GetProperty("termino").GetString().Should().Be(Termino);
        var enviado = JsonNode.Parse(_nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Post).Cuerpo!)!;
        enviado["termino"]!.GetValue<string>().Should().Be(Termino);
        enviado["ejemplo"]!.GetValue<string>().Should().Be("línea1\nlínea2");
    }

    [Fact]
    public async Task ActivoNuloUOmitidoEsTrueTambienAlEditarUnTerminoInactivo()
    {
        var sesion = await ComercialAsync();
        var inactivo = TerminoDe(NitA, "Mula vieja", activo: false);
        var cuerpo = JsonSerializer.Serialize(new { nit_cliente = NitA, termino = "Mula vieja", tipo = "valor", concepto = "3S2", contexto = "tipoVehiculo" });

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{inactivo}", sesion.Access, cuerpo);

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Put, HttpStatusCode.OK);
        json.GetProperty("activo").GetBoolean().Should().BeTrue("PUT reemplaza los editables y activo ausente es true, como en NEXO");
        JsonNode.Parse(_nexo.Peticiones.Single(p => p.Metodo == HttpMethod.Put).Cuerpo!)!["activo"]!.GetValue<bool>().Should().BeTrue();
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"texto\"")]
    [InlineData("""{"nit_cliente":"900555111","termino":"T","tipo":"valor","concepto":"3S3","contexto":"tipoVehiculo","activo":"si"}""")]
    [InlineData("""{"nit_cliente":900555111,"termino":"T","tipo":"valor","concepto":"3S3"}""")]
    [InlineData("""{"nit_cliente":"900555111","termino":["T"],"tipo":"valor","concepto":"3S3"}""")]
    public async Task UnCuerpoMalformadoEs400ErrorWebSinLlamarANexo(string cuerpo)
    {
        var sesion = await ComercialAsync();
        var propio = TerminoDe(NitA);

        using var crear = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, cuerpo);
        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{propio}", sesion.Access, cuerpo);

        var jsonCrear = await VerificarContratoAsync(crear, Ruta, HttpMethod.Post, HttpStatusCode.BadRequest);
        var jsonEditar = await VerificarContratoAsync(editar, RutaId, HttpMethod.Put, HttpStatusCode.BadRequest);
        jsonCrear.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
        jsonEditar.GetProperty("error").ValueKind.Should().Be(JsonValueKind.String);
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnCuerpoVacioEs400SinLlamarANexo()
    {
        var sesion = await ComercialAsync();

        using var crear = await _api.EnviarAsync(HttpMethod.Post, Ruta, sesion.Access, string.Empty);

        crear.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("---")]
    [InlineData("1234567890123456789012345678901")]
    public async Task UnNitInvalidoAlEditarOBorrarEs400SinEscribir(string nit)
    {
        var sesion = await ComercialAsync();
        var propio = TerminoDe(NitA);

        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{propio}", sesion.Access, Cuerpo(nit: nit));
        using var borrar = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{propio}?nit_cliente={nit}", sesion.Access);

        await VerificarErrorNegocioAsync(editar, "CLIENTE_NIT_INVALIDO");
        await VerificarErrorNegocioAsync(borrar, "CLIENTE_NIT_INVALIDO");
        editar.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        borrar.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _nexo.Peticiones.Should().NotContain(p => p.Url.AbsolutePath.StartsWith(RutaNexo, StringComparison.Ordinal));
    }

    // --- NEXO caído en la comprobación previa ---

    [Fact]
    public async Task ConNexoCaidoEditarYBorrarSon503SinEscribir()
    {
        var sesion = await ComercialAsync();
        var propio = TerminoDe(NitA);
        _nexo.Caido = HttpStatusCode.BadGateway;

        using var editar = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{propio}", sesion.Access, Cuerpo());
        using var borrar = await _api.EnviarAsync(HttpMethod.Delete, $"{Ruta}/{propio}?nit_cliente={NitANormalizado}", sesion.Access);

        var jsonEditar = await VerificarContratoAsync(editar, RutaId, HttpMethod.Put, HttpStatusCode.ServiceUnavailable);
        var jsonBorrar = await VerificarContratoAsync(borrar, RutaId, HttpMethod.Delete, HttpStatusCode.ServiceUnavailable);
        jsonEditar.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
        jsonBorrar.GetProperty("error").GetProperty("codigo").GetString().Should().Be("NEXO_NO_DISPONIBLE");
        EscriturasEnNexo().Should().Be(0);
        _nexo.Diccionario.Should().Contain(t => t.Id == propio);
    }

    [Fact]
    public async Task EditarAUnDuplicadoDelMismoClienteEs409()
    {
        var sesion = await ComercialAsync();
        TerminoDe(NitA, "Mula A");
        var otro = TerminoDe(NitA, "Mula B");

        using var respuesta = await _api.EnviarAsync(HttpMethod.Put, $"{Ruta}/{otro}", sesion.Access,
            Cuerpo(termino: " MULA A ", concepto: "3S2"));

        var json = await VerificarContratoAsync(respuesta, RutaId, HttpMethod.Put, HttpStatusCode.Conflict);
        json.GetProperty("error").GetProperty("codigo").GetString().Should().Be("TERMINO_DUPLICADO");
    }

    private async Task<string> ElegirRolAsync(string persona, string rol)
    {
        using var login = await _api.Cliente.PostAsJsonAsync("/api/auth/login-clientepro", new { idUsuario = persona }, Ct);
        using var eleccion = JsonDocument.Parse(await login.Content.ReadAsStringAsync(Ct));
        var ticket = eleccion.RootElement.GetProperty("ticketEleccion").GetString();
        using var elegido = await _api.Cliente.PostAsJsonAsync("/api/auth/elegir-rol", new { ticket, rol }, Ct);
        elegido.StatusCode.Should().Be(HttpStatusCode.OK);
        using var par = JsonDocument.Parse(await elegido.Content.ReadAsStringAsync(Ct));
        par.RootElement.GetProperty("user").GetProperty("rol").GetString().Should().Be(rol);
        return par.RootElement.GetProperty("access").GetString()!;
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
