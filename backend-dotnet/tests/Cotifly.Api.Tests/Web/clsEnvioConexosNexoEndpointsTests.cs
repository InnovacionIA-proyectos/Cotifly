using System.Net;
using System.Text.Json;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Pruebas.Comun;
using Cotifly.Pruebas.Web;
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
        .ConServicioConexo(Cargue, "CAR-01", "Cargue en puerto", "logistico", "UND", 150_000m);
    private readonly clsApiWebPruebas _api;

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
                mercancias = new object[] { new { id_tipo_mercancia = clsCosteoEndpointsTests.TipoMercancia, nombre_mercancia = "Estiba de café", cantidad = 20, largo_cm = 120, ancho_cm = 100, alto_cm = 150, peso_kg = 1400 } },
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

    public void Dispose()
    {
        _api.Dispose();
        _nexo.Dispose();
    }
}
