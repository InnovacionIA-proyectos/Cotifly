using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
using Microsoft.Extensions.DependencyInjection;
using static Cotifly.Api.Tests.Web.clsEntornoApiWeb;

namespace Cotifly.Api.Tests.Web;

/// <summary>
/// Decisión 10 del usuario (HU-021.9, #136): los conexos capturados con la ruta llegan a la cotización de Nexo con un envío diferido. El
/// polling los envía UNA vez, pegados a la línea del primer camión vigente de su ruta, la primera vez que Nexo costea esa línea. Por HTTP
/// con SQL Server real y el Nexo falso: envío único, POST incierto con relectura (sin duplicados), camión descartado y dos réplicas.
/// </summary>
[Collection(clsColeccionApiWeb.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsEnvioConexosNexoEndpointsTests : IDisposable
{
    private const string Cargue = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d";
    private static readonly HttpMethod _get = HttpMethod.Get;
    private static readonly HttpMethod _post = HttpMethod.Post;
    private readonly clsEntornoApiWeb _entorno;
    private readonly clsNexoFalso _nexo = new clsNexoFalso()
        .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
        .ConServicioConexo(Cargue, "CAR-01", "Cargue en puerto", "logistico", "UND", 150_000m)
        .ConServicioConexo(Almacenamiento, "ALM-01", "Almacenamiento", "logistico", "Día", 120_000m);
    private const string Almacenamiento = "1b2c3d4e-5f60-4a71-8b92-a3b4c5d6e7f8";
    private readonly clsApiWebPruebas _api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public clsEnvioConexosNexoEndpointsTests(clsEntornoApiWeb entorno)
    {
        _entorno = entorno;
        _api = clsApiWebPruebas.ConNexo(entorno.BaseDatos, _nexo);
    }

    [Fact]
    public async Task ElPollingEnviaLosConexosCapturadosUnaSolaVezALaLineaDelPrimerCamionYElPrecioNoCambia()
    {
        var (sesion, folio) = await CapturarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        var cotizacionNexo = () => _nexo.CotizacionDe(solicitud)!;

        await _api.ProcesarPollingAsync(folio);
        _nexo.Peticiones.Count(p => p.Metodo == _post && p.Url.AbsolutePath.EndsWith("/conexos", StringComparison.Ordinal))
            .Should().Be(0, "sin la línea costeada del primer camión no hay a dónde pegarlos: se espera");

        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        await _api.ProcesarPollingAsync(folio);

        var item = _nexo.PeticionesConexos(cotizacionNexo()).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, 0));
        item["servicioConexoId"]!.GetValue<string>().Should().Be(Cargue);
        item["cantidad"]!.GetValue<decimal>().Should().Be(2m);

        await _api.ProcesarPollingAsync(folio);
        _nexo.PeticionesConexos(cotizacionNexo()).Should().ContainSingle("ya está en Nexo: no se vuelve a enviar");

        var ruta = (await RutasAsync(sesion, folio)).Single();
        var conexo = ruta.GetProperty("servicios_conexos").EnumerateArray().Should().ContainSingle("no se importa otra vez desde Nexo").Subject;
        conexo.GetProperty("id").GetString().Should().Be(Cargue);
        (await CostoConexoAsync(folio)).Should().Be(150_000m, "el precio en Cotifly sigue siendo el costo del catálogo");
        (await EventosAsync(folio, "enviado")).Should().Be(1);
    }

    [Fact]
    public async Task UnPostInciertoSeReleeYNoSeDuplica()
    {
        var (sesion, folio) = await CapturarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CortarRespuestaConexos = true;       // Nexo aplica el conexo y la respuesta no llega
        _nexo.FallarRelecturaTrasConexos = true;   // y la relectura inmediata también falla

        await _api.ProcesarPollingAsync(folio);

        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle();
        (await EventosAsync(folio, "incierto")).Should().Be(1, "sin confirmación queda incierto: no se reenvía a ciegas");

        // Observable: el costeo muestra el conexo incierto y el aviso para revisarlo en Nexo.
        var incierta = (await RutasAsync(sesion, folio)).Single();
        incierta.GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("incierto");
        incierta.GetProperty("aviso_conexos_nexo").GetString().Should().Contain("Revisa en la cotización de Nexo").And.Contain("Cargue en puerto");

        _nexo.CortarRespuestaConexos = false;
        await _api.ProcesarPollingAsync(folio);
        await _api.ProcesarPollingAsync(folio);

        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle("la lectura del polling lo muestra en Nexo: queda enviado sin otro POST");
        (await EventosAsync(folio, "enviado")).Should().Be(1);
        (await ConexosEnRutaAsync(folio)).Should().Be(1, "no se duplica en Cotifly");
        var confirmada = (await RutasAsync(sesion, folio)).Single();
        confirmada.GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("enviado");
        confirmada.GetProperty("aviso_conexos_nexo").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// La Mesa entrega en la misma consulta en que falla el envío: la cotización vuelve a la agenda solo para reintentar los conexos (sin
    /// volver a costear) y, cuando Nexo responde, llegan una sola vez; después sale de la agenda.
    /// </summary>
    [Fact]
    public async Task EntregadaEnLaMismaConsultaElEnvioFallidoSeReintentaSoloParaLosConexos()
    {
        var (sesion, folio) = await CapturarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.Entregar(solicitud);
        _nexo.ConexosFalla = HttpStatusCode.Forbidden; /* Nexo no lo aplica (un 5xx seria incierto) */

        await _api.ProcesarPollingAsync(folio);

        (await EventosAsync(folio, "fallido")).Should().Be(1);
        (await AgendadaAsync(folio)).Should().BeTrue("entregada, pero con conexos por reintentar vuelve a la agenda");
        var precio = (await RutasAsync(sesion, folio)).Single().GetProperty("precio_final_linea").GetDecimal();

        _nexo.ConexosFalla = null;
        await _api.ProcesarPollingAsync(folio);

        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Count.Should().Be(2, "el fallido y el reintento");
        _nexo.CotizacionDe(solicitud).Should().NotBeNull();
        (await EventosAsync(folio, "enviado")).Should().Be(1);
        (await AgendadaAsync(folio)).Should().BeFalse("sin nada por reintentar sale de la agenda");
        var ruta = (await RutasAsync(sesion, folio)).Single();
        ruta.GetProperty("precio_final_linea").GetDecimal().Should().Be(precio, "el reintento no vuelve a costear ni toca montos");
        ruta.GetProperty("estado_costeo").GetString().Should().Be("entregada");
        (await _api.ProcesarPollingAsync(folio)).Should().Be(0, "ya no está en la agenda");
    }

    [Fact]
    public async Task AlTopeDeReintentosQuedaFallidoDefinitivoVisibleYSaleDeLaAgenda()
    {
        using var nexo = new clsNexoFalso()
            .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
            .ConServicioConexo(Cargue, "CAR-01", "Cargue en puerto", "logistico", "UND", 150_000m);
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, nexo,
            extra: new Dictionary<string, string?> { ["CosteoNexo:MaximoReintentosConexos"] = "2" });
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var folio = (await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        var solicitud = nexo.SolicitudesCreadas[^1];
        nexo.CostearLinea(solicitud, 0, 1_000_000m);
        nexo.Entregar(solicitud);
        nexo.ConexosFalla = HttpStatusCode.Forbidden; /* Nexo no lo aplica (un 5xx seria incierto) */

        await api.ProcesarPollingAsync(folio);
        await api.ProcesarPollingAsync(folio);
        await api.ProcesarPollingAsync(folio);

        nexo.PeticionesConexos(nexo.CotizacionDe(solicitud)!).Count.Should().Be(2, "tope de 2 intentos");
        (await EventosAsync(folio, "fallido_definitivo")).Should().Be(1);
        (await AgendadaAsync(folio)).Should().BeFalse();
        using var costeo = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var ruta = (await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK)).GetProperty("rutas")[0];
        ruta.GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("fallido_definitivo");
        ruta.GetProperty("aviso_conexos_nexo").GetString().Should().Contain("Revisa en la cotización de Nexo");
    }

    /// <summary>
    /// Corrección de la segunda validación de #136: un conexo agregado con el POST queda guardado con el id de la FILA de la cotización de
    /// Nexo. En esa misma solicitud no se reenvía (ya está); en la solicitud nueva de la re-solicitud (mismo folio) y en la -vN viaja con el
    /// id del CATÁLOGO, junto con el capturado, en un solo POST que Nexo acepta.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnaMezclaDeConexoCapturadoYAgregadoLlegaConElIdDelCatalogoALaSolicitudNueva(bool versionNueva)
    {
        var (sesion, folio) = await ConAgregadoYEntregadaAsync();
        var anterior = _nexo.SolicitudesCreadas[^1];
        _nexo.PeticionesConexos(_nexo.CotizacionDe(anterior)!).Should().HaveCount(2, "el capturado (envío diferido) y el agregado a mano; ninguno se reenvía");

        var folioNuevo = await ReSolicitarAsync(sesion, folio, versionNueva);
        var nueva = _nexo.SolicitudesCreadas[^1];
        nueva.Should().NotBe(anterior);
        _nexo.CostearLinea(nueva, 0, 1_100_000m);
        await _api.ProcesarPollingAsync(folioNuevo);

        var items = _nexo.PeticionesConexos(_nexo.CotizacionDe(nueva)!).Should().ContainSingle().Subject["items"]!.AsArray();
        items.Select(i => i!["servicioConexoId"]!.GetValue<string>()).Should().BeEquivalentTo([Cargue, Almacenamiento], "siempre el id del catálogo");
        (await EventosAsync(folioNuevo, "fallido")).Should().Be(0, "Nexo aceptó el lote");
        var ruta = (await RutasAsync(sesion, folioNuevo)).Single();
        ruta.GetProperty("servicios_conexos").EnumerateArray().Select(c => c.GetProperty("estado_nexo").GetString()).Should().OnlyContain(e => e == "enviado");
    }

    /// <summary>
    /// Un conexo importado cuyo nombre ya no está en el catálogo de Nexo no tiene id del catálogo: queda fallido_definitivo con su aviso y
    /// sale del lote, así el POST (todo o nada) del conexo válido llega a Nexo.
    /// </summary>
    [Fact]
    public async Task UnConexoQueYaNoEstaEnElCatalogoNoTumbaAlValido()
    {
        var (sesion, folio) = await CapturarAsync();
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.CotizacionServiciosConexos (id, lineaSolicitudId, idServicioConexo, nombreServicioConexo, precio, cantidad, createdAt, updatedAt) "
            + "SELECT NEWID(), l.id, CONVERT(nvarchar(64), NEWID()), N'Servicio retirado del catálogo', 50000, 1, SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET() "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}'");
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);

        await _api.ProcesarPollingAsync(folio);
        await _api.ProcesarPollingAsync(folio);

        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Should().ContainSingle().Subject!;
        item["servicioConexoId"]!.GetValue<string>().Should().Be(Cargue, "el válido llega aunque el otro no se pueda resolver");
        (await EventosAsync(folio, "fallido_definitivo")).Should().Be(1);
        var ruta = (await RutasAsync(sesion, folio)).Single();
        var estados = ruta.GetProperty("servicios_conexos").EnumerateArray()
            .ToDictionary(c => c.GetProperty("nombre").GetString()!, c => c.GetProperty("estado_nexo").GetString());
        estados["Cargue en puerto"].Should().Be("enviado");
        estados["Servicio retirado del catálogo"].Should().Be("fallido_definitivo");
        ruta.GetProperty("aviso_conexos_nexo").GetString().Should().Contain("Servicio retirado del catálogo");
    }

    /// <summary>
    /// Corrección de la tercera validación de #136: una caída del catálogo de conexos de Nexo no consume intentos ni deja nada
    /// fallido_definitivo. En la solicitud nueva de una re-solicitud viajan el Cargue (id del catálogo, en la caché) y el Almacenamiento
    /// (guardado con el id de la fila de Nexo: se resuelve por nombre). Con el catálogo caído 10 ciclos, el Cargue llega igual y el
    /// Almacenamiento espera (una sola fila en_espera); cuando el catálogo vuelve, llega. Cada uno, una sola vez.
    /// </summary>
    [Fact]
    public async Task UnaCaidaDelCatalogoNoConsumeIntentosYTodoLlegaUnaSolaVezAlRecuperarse()
    {
        var (sesion, folio) = await ConAgregadoYEntregadaAsync();
        var folioNuevo = await ReSolicitarAsync(sesion, folio, versionNueva: false);
        var nueva = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(nueva, 0, 1_100_000m);

        _nexo.CatalogoFalla = HttpStatusCode.ServiceUnavailable;
        for (var ciclo = 0; ciclo < 10; ciclo++)
        {
            await _api.ProcesarPollingAsync(folioNuevo);
        }

        var cotizacionNueva = _nexo.CotizacionDe(nueva)!;
        ServiciosEnviados(cotizacionNueva).Should().Equal([Cargue], "el que ya trae el id del catálogo no depende de la búsqueda por nombre");
        (await EventosAsync(folioNuevo, "fallido")).Should().Be(0, "sin POST no hay intento fallido");
        (await EventosAsync(folioNuevo, "fallido_definitivo")).Should().Be(0);
        (await EventosAsync(folioNuevo, "en_espera")).Should().Be(1, "una sola fila mientras dura la espera");
        var enCaida = (await RutasAsync(sesion, folioNuevo)).Single().GetProperty("servicios_conexos").EnumerateArray()
            .ToDictionary(c => c.GetProperty("nombre").GetString()!, c => c.GetProperty("estado_nexo").GetString());
        enCaida["Cargue en puerto"].Should().Be("enviado");
        enCaida["Almacenamiento"].Should().Be("pendiente");

        _nexo.CatalogoFalla = null;
        await _api.ProcesarPollingAsync(folioNuevo);
        await _api.ProcesarPollingAsync(folioNuevo);

        ServiciosEnviados(cotizacionNueva).Should().BeEquivalentTo([Cargue, Almacenamiento], "cada uno llega una sola vez");
        (await EventosAsync(folioNuevo, "fallido_definitivo")).Should().Be(0);
        (await RutasAsync(sesion, folioNuevo)).Single().GetProperty("servicios_conexos").EnumerateArray()
            .Select(c => c.GetProperty("estado_nexo").GetString()).Should().OnlyContain(e => e == "enviado");
    }

    /// <summary>
    /// La espera por el catálogo tiene tope (<c>CosteoNexo:EsperaMaximaCatalogoConexosHoras</c>, 24 h por defecto). Con el reloj falso:
    /// entregada en la misma consulta y con el catálogo caído, el Cargue espera; el reintento se espacia 5 y luego 15 min, y pasadas 24 h
    /// desde el inicio de la espera queda fallido_definitivo, visible en el costeo, y la cotización sale de la agenda. Nunca hubo un POST.
    /// </summary>
    [Fact]
    public async Task UnaEsperaPorElCatalogoMasLargaQueElTopeQuedaFallidoDefinitivoYSaleDeLaAgenda()
    {
        var (sesion, folio) = await CapturarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.Entregar(solicitud);
        _nexo.CatalogoFalla = HttpStatusCode.ServiceUnavailable;

        // Una hora después de capturar: la caché del catálogo (5 min) ya venció, así que el Cargue también depende del catálogo.
        var inicio = DateTimeOffset.UtcNow.AddHours(1);
        using (clsRelojFijable.Fijar(inicio))
        {
            await _api.ProcesarPollingAsync(folio);
        }

        (await EventosAsync(folio, "en_espera")).Should().Be(1);
        (await SiguienteConsultaAsync(folio)).Should().BeCloseTo(inicio + TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(1), "primer espaciado: 5 min");

        using (clsRelojFijable.Fijar(inicio + TimeSpan.FromMinutes(10)))
        {
            await _api.ProcesarPollingAsync(folio);
        }

        (await SiguienteConsultaAsync(folio)).Should().BeCloseTo(inicio + TimeSpan.FromMinutes(25), TimeSpan.FromSeconds(1), "segundo espaciado: 15 min");
        (await EventosAsync(folio, "en_espera")).Should().Be(1, "la espera sigue en la misma fila");

        using (clsRelojFijable.Fijar(inicio + TimeSpan.FromHours(25)))
        {
            await _api.ProcesarPollingAsync(folio);
        }

        (await EventosAsync(folio, "fallido_definitivo")).Should().Be(1);
        (await EventosAsync(folio, "fallido")).Should().Be(0, "nunca hubo un POST rechazado");
        (await AgendadaAsync(folio)).Should().BeFalse("con el tope vencido sale de la agenda");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().BeEmpty();
        var ruta = (await RutasAsync(sesion, folio)).Single();
        ruta.GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("fallido_definitivo");
        ruta.GetProperty("aviso_conexos_nexo").GetString().Should().Contain("Cargue en puerto");
    }

    private Task<DateTimeOffset> SiguienteConsultaAsync(string folio) => clsBaseDatosPruebas.EscalarAsync<DateTimeOffset>(_entorno.BaseDatos.CadenaCotifly,
        $"SELECT costeoSiguienteConsultaAt FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    private List<string> ServiciosEnviados(string cotizacionNexo) =>
        [.. _nexo.PeticionesConexos(cotizacionNexo).SelectMany(p => p["items"]!.AsArray()).Select(i => i!["servicioConexoId"]!.GetValue<string>())];

    /// <summary>
    /// Captura con el Cargue, Nexo costea (el polling envía el Cargue), el comercial agrega el Almacenamiento con el POST (queda con el id de
    /// la fila de Nexo) y la Mesa entrega: la cotización queda entregada, lista para re-solicitar.
    /// </summary>
    private async Task<(clsSesionPrueba Sesion, string Folio)> ConAgregadoYEntregadaAsync()
    {
        var (sesion, folio) = await CapturarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        await _api.ProcesarPollingAsync(folio);

        using var agregar = await ConVersionAsync(_post, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { items = new[] { new { linea_orden = 1, servicio_id = Almacenamiento, cantidad = 1 } } }), await EtagAsync(sesion, folio));
        agregar.StatusCode.Should().Be(HttpStatusCode.OK, await agregar.Content.ReadAsStringAsync(Ct));
        (await clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
                "SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
                + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}' AND s.idServicioConexo <> '{Almacenamiento}' "
                + $"AND s.idServicioConexo <> '{Cargue}'"))
            .Should().Be(1, "el agregado con el POST queda con el id de la fila de Nexo, no el del catálogo");

        _nexo.Entregar(solicitud);
        await _api.ProcesarPollingAsync(folio);
        return (sesion, folio);
    }

    private async Task<string> ReSolicitarAsync(clsSesionPrueba sesion, string folio, bool versionNueva)
    {
        using var respuesta = await ConVersionAsync(_post, $"/web/cotizaciones/{folio}/costeo/re-solicitar", sesion.Access,
            clsApiWebPruebas.Json(new { motivo = "Actualizar el costo", nueva_version = versionNueva }), await EtagAsync(sesion, folio));
        var json = await VerificarContratoAsync(respuesta, "/web/cotizaciones/{folio}/costeo/re-solicitar", _post,
            versionNueva ? HttpStatusCode.Created : HttpStatusCode.OK);
        json.GetProperty("conexos_conservados").EnumerateArray().Select(c => c.GetString()).Should().BeEquivalentTo(["Cargue en puerto", "Almacenamiento"]);
        return json.GetProperty("folio").GetString()!;
    }

    private async Task<string?> EtagAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        return costeo.Headers.ETag?.Tag;
    }

    private async Task<HttpResponseMessage> ConVersionAsync(HttpMethod metodo, string ruta, string token, string cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await _api.Cliente.SendAsync(peticion, Ct);
    }

    private Task<bool> AgendadaAsync(string folio) => clsBaseDatosPruebas.EscalarAsync<bool>(_entorno.BaseDatos.CadenaCotifly,
        $"SELECT CAST(IIF(costeoSiguienteConsultaAt IS NULL, 0, 1) AS bit) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = '{folio}'");

    [Fact]
    public async Task ConElPrimerCamionDescartadoVanAlSiguienteCamionVigente()
    {
        // Captura con Nexo caído (queda en error_envio), un segundo camión en la ruta y el reintento: viajan dos líneas.
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        _nexo.AltaFalla = HttpStatusCode.ServiceUnavailable;
        using var envio = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var folio = (await VerificarContratoAsync(envio, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (id, lineaSolicitudId, tipoVehiculo, carroceria, pesoKg, costoLevanteContenedor, "
            + "estadoCosteo, createdAt, updatedAt) "
            + "SELECT NEWID(), l.id, N'2S2', N'FURGON', 6000, ISNULL(l.valorLevanteContenedor, 0), "
            + "(SELECT TOP 1 v.estadoCosteo FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v WHERE v.lineaSolicitudId = l.id), "
            + "DATEADD(SECOND, 1, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET() "
            + "FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND l.orden = 1");
        _nexo.AltaFalla = null;
        using var reintento = await _api.EnviarAsync(_post, $"/web/cotizaciones/{folio}/costeo/reintentar", sesion.Access);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK);

        // El primer camión queda descartado antes de que Nexo costee.
        await clsBaseDatosPruebas.EjecutarAsync(_entorno.BaseDatos.CadenaCotifly,
            "UPDATE v SET estadoCosteo = 'descartada' FROM dbo_cotifly.CotizacionVehiculosLineaSolicitud v "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = v.lineaSolicitudId JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId "
            + $"WHERE c.folio = '{folio}' AND v.pesoKg = 28000");
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);
        _nexo.CostearLinea(solicitud, 1, 500_000m);

        await _api.ProcesarPollingAsync(folio);

        var item = _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle().Subject["items"]!.AsArray().Single()!;
        item["lineaId"]!.GetValue<string>().Should().Be(_nexo.LineaCotizacionDe(solicitud, 1), "decisión 3: el primer camión VIGENTE");
    }

    [Fact]
    public async Task DosReplicasDelPollingEnvianLosConexosUnaSolaVez()
    {
        var (_, folio) = await CapturarAsync();
        var solicitud = _nexo.SolicitudesCreadas[^1];
        _nexo.CostearLinea(solicitud, 0, 1_000_000m);

        var resultados = await _api.ProcesarPollingEnParaleloAsync(folio, replicas: 2);

        resultados.Count(r => r == ResultadoConsultaCosteo.NoReclamada).Should().Be(1, "el reclamo de la agenda deja pasar a una sola réplica");
        _nexo.PeticionesConexos(_nexo.CotizacionDe(solicitud)!).Should().ContainSingle();
    }

    private async Task<(clsSesionPrueba Sesion, string Folio)> CapturarAsync()
    {
        var sesion = await _api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var respuesta = await _api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var json = await VerificarContratoAsync(respuesta, "/web/solicitudes-costeo", _post, HttpStatusCode.Created);
        json.GetProperty("estado_costeo").GetString().Should().Be("pendiente_costeo");
        return (sesion, json.GetProperty("folio").GetString()!);
    }

    /// <summary>Una ruta con el conexo Cargue (2 unidades) capturado con la ruta.</summary>
    private static string Formulario() => clsApiWebPruebas.Json(new
    {
        tipo = "spot",
        nit_cliente = clsCosteoEndpointsTests.Nit,
        cliente_nombre = "Longyin",
        linea_negocio = "Importacion",
        deadline = "2026-10-15T17:00:00-05:00",
        rutas = new object[]
        {
            new
            {
                linea_negocio = "Importacion",
                origen_municipio_id = 76109,
                origen = "Buenaventura",
                destino_municipio_id = 11001,
                destino = "Bogotá D.C.",
                tipologia = "Contenedor",
                clase_viaje = "Contenedor",
                clase_contenedor = "CNT40.HC",
                exclusivo = true,
                consolidado = false,
                retorna_contenedor = false,
                levante_contenedor_diferente = false,
                peso_kg = 28000,
                valor_mercancia = 250000000,
                mercancias = new object[] { new { codigo_mercancia = clsCosteoEndpointsTests.CodigoMercancia, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 } },
                servicios_conexos = new object[] { new { id_servicio_conexo = Cargue, cantidad = 2 } },
            },
        },
    });

    private async Task<List<JsonElement>> RutasAsync(clsSesionPrueba sesion, string folio)
    {
        using var costeo = await _api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var json = await VerificarContratoAsync(costeo, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK);
        return [.. json.GetProperty("rutas").EnumerateArray()];
    }

    private Task<decimal> CostoConexoAsync(string folio) => clsBaseDatosPruebas.EscalarAsync<decimal>(_entorno.BaseDatos.CadenaCotifly,
        "SELECT s.precio FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
        + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");

    private Task<int> ConexosEnRutaAsync(string folio) => clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
        "SELECT COUNT(*) FROM dbo_cotifly.CotizacionServiciosConexos s JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId "
        + $"JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE c.folio = '{folio}'");

    private Task<int> EventosAsync(string folio, string estado) => clsBaseDatosPruebas.EscalarAsync<int>(_entorno.BaseDatos.CadenaCotifly,
        $"SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta WHERE herramienta = '{Cotifly.Aplicacion.Costeo.DTOs.clsEnvioConexosNexo.Herramienta}' "
        + $"AND input LIKE '%{folio}%' AND outputResumen LIKE '%\"estado\":\"{estado}\"%'");

    /// <summary>
    /// #173: las respuestas de <c>PUT .../costeo/margen</c> y <c>PUT .../costeo/servicios-conexos</c> traen el <c>estado_nexo</c> de cada
    /// conexo igual que el GET (antes salía <c>null</c>), y la categoría es la del catálogo de Nexo (<c>adicional</c>), no <c>logistico</c> fijo.
    /// </summary>
    [Fact]
    public async Task LosPutDeMargenYDeConexosTraenElEstadoNexoYLaCategoriaDelCatalogoComoElGet()
    {
        using var nexo = new clsNexoFalso()
            .ConCliente(clsCosteoEndpointsTests.Nit, "LONGYIN S.A.S.")
            .ConServicioConexo(Cargue, "AVP_MOTO", "Acompañante vehicular en moto", "adicional", "UND", 150_000m);
        using var api = clsApiWebPruebas.ConNexo(_entorno.BaseDatos, nexo);
        var sesion = await api.IniciarSesionAsync(clsBaseDatosPruebas.RolExternoComercial);
        using var captura = await api.EnviarAsync(_post, "/web/solicitudes-costeo", sesion.Access, Formulario());
        var folio = (await VerificarContratoAsync(captura, "/web/solicitudes-costeo", _post, HttpStatusCode.Created)).GetProperty("folio").GetString()!;
        nexo.CostearLinea(nexo.SolicitudesCreadas[^1], 0, 1_000_000m);
        await api.ProcesarPollingAsync(folio);

        // Con la caché fría el GET no espera al catálogo de Nexo: responde con la categoría de respaldo y la calienta en segundo plano.
        using var frio = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        (await VerificarContratoAsync(frio, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("rutas")[0].GetProperty("servicios_conexos")[0].GetProperty("categoria").GetString()
            .Should().Be(clsCosteoCotizacionService.CategoriaConexo, "la caché estaba fría");
        await api.Factory.Services.GetRequiredService<clsAlmacenCategoriasConexos>().UltimaLectura.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        using var get = await api.EnviarAsync(_get, $"/web/cotizaciones/{folio}/costeo", sesion.Access);
        var conexoGet = (await VerificarContratoAsync(get, "/web/cotizaciones/{folio}/costeo", _get, HttpStatusCode.OK))
            .GetProperty("rutas")[0].GetProperty("servicios_conexos")[0];
        conexoGet.GetProperty("estado_nexo").GetString().Should().Be("enviado");
        conexoGet.GetProperty("categoria").GetString().Should().Be("adicional", "la categoría sale del catálogo de Nexo");

        using var margen = await EnviarConVersionAsync(api, HttpMethod.Put, $"/web/cotizaciones/{folio}/costeo/margen", sesion.Access,
            """{"margen":0.18}""", get.Headers.ETag?.Tag);
        var ajustado = await VerificarContratoAsync(margen, "/web/cotizaciones/{folio}/costeo/margen", HttpMethod.Put, HttpStatusCode.OK);
        foreach (var conexo in new[] { ajustado.GetProperty("servicios_conexos")[0], ajustado.GetProperty("rutas")[0].GetProperty("servicios_conexos")[0] })
        {
            conexo.GetProperty("estado_nexo").GetString().Should().Be("enviado", "igual que el GET");
            conexo.GetProperty("categoria").GetString().Should().Be("adicional");
        }

        using var seleccion = await EnviarConVersionAsync(api, HttpMethod.Put, $"/web/cotizaciones/{folio}/costeo/servicios-conexos", sesion.Access,
            clsApiWebPruebas.Json(new { incluidos = new[] { Cargue } }), margen.Headers.ETag?.Tag);
        var elegida = await VerificarContratoAsync(seleccion, "/web/cotizaciones/{folio}/costeo/servicios-conexos", HttpMethod.Put, HttpStatusCode.OK);
        elegida.GetProperty("rutas")[0].GetProperty("servicios_conexos")[0].GetProperty("estado_nexo").GetString().Should().Be("enviado");
        elegida.GetProperty("servicios_conexos")[0].GetProperty("categoria").GetString().Should().Be("adicional");
    }

    private async Task<HttpResponseMessage> EnviarConVersionAsync(clsApiWebPruebas api, HttpMethod metodo, string ruta, string token, string cuerpo, string? ifMatch)
    {
        using var peticion = new HttpRequestMessage(metodo, ruta);
        peticion.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (ifMatch is not null)
        {
            peticion.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        peticion.Content = new StringContent(cuerpo, System.Text.Encoding.UTF8, "application/json");
        return await api.Cliente.SendAsync(peticion, Ct);
    }

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
