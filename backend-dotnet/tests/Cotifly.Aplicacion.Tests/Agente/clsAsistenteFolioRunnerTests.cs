using System.Text.Json;
using System.Text.RegularExpressions;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Aplicacion.Precio.Services;
using Cotifly.Aplicacion.Tests.Costeo;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using FunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Asistente por folio (ADR-018 §2b) con el <see cref="IChatClient"/> guionizado y datos ficticios: el folio debe ser
/// del comercial, las herramientas solo leen o proponen, la propuesta (cuerpo, endpoint, <c>row_version</c> y resumen con precios) la
/// arma el servidor, ningún mensaje que recibe el modelo trae montos (COP ni USD) y la cotización no cambia.
/// </summary>
public sealed partial class clsAsistenteFolioRunnerTests
{
    private const string Folio = "PCN-900555111-AP-05102026-001";
    private const string Espacio = "web/sesion-folio-001";
    private const string Precinto1 = "0b6e2a51-1111-4c55-9a0e-1d2f3a4b5c61";
    private const string Precinto2 = "0b6e2a51-2222-4c55-9a0e-1d2f3a4b5c62";
    private static readonly string _hilo = $"{Espacio}/{Folio}";

    private readonly clsChatClientGuionizado _modelo = new();
    private readonly clsSesionRepositoryMemoria _sesiones = new();
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsCotizacionRepository _cotizaciones = Substitute.For<IClsCotizacionRepository>();
    private readonly IClsMargenPoliticaRepository _politicas = Substitute.For<IClsMargenPoliticaRepository>();
    private readonly IClsUnitOfWork _unidad = Substitute.For<IClsUnitOfWork>();
    private readonly IClsCalendarioNegocio _calendario = Substitute.For<IClsCalendarioNegocio>();
    private readonly IClsAuditoriaService _auditoria = Substitute.For<IClsAuditoriaService>();
    private readonly IClsServiciosConexosCosteoService _conexos = Substitute.For<IClsServiciosConexosCosteoService>();
    private readonly IClsReSolicitudCosteoService _reSolicitud = Substitute.For<IClsReSolicitudCosteoService>();
    private readonly clsRelojFalso _reloj = new();
    private readonly clsUsuarioWeb _ana;
    private clsContextoAsistenteFolio _contextoFolio = new();
    private readonly Dictionary<clsCotizacionSolicitud, (decimal? Precio, decimal? Margen, int Conexos)> _inicial = [];

    public clsAsistenteFolioRunnerTests()
    {
        _ana = clsUsuarioWeb.Crear("aprueba", "Ana Prueba", "ana.prueba@ejemplo.test", null, _reloj.AhoraUtc).Valor;
        _usuarios.ObtenerPorIdAsync(_ana.Id, Arg.Any<CancellationToken>()).Returns(_ana);
        _calendario.Hoy.Returns(new DateOnly(2026, 10, 7));

        // La validación de la re-solicitud es del caso de uso (con la base y el catálogo): aquí, su parte de dominio (entrada y estado).
        _reSolicitud.ValidarAsync(Arg.Any<Guid>(), Arg.Any<clsCotizacionSolicitud>(), Arg.Any<clsEntradaReSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(ci => clsReSolicitudCosteoService.ValidarEntrada(ci.Arg<clsEntradaReSolicitudCosteo>())
                           ?? ci.Arg<clsCotizacionSolicitud>().ValidarReSolicitud(ci.Arg<clsEntradaReSolicitudCosteo>().VersionNueva));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnFolioAjenoOInexistenteEsNoEncontradoSinLlamarAlModeloNiAbrirLaSesion()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(Guid.NewGuid(), Folio), dueno: Guid.NewGuid());

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("¿Cuál es el precio?"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        _modelo.Llamadas.Should().BeEmpty();
        _sesiones.Eventos.Should().BeEmpty();
    }

    [Fact]
    public async Task ElModeloSoloRecibeLasHerramientasDeLecturaYPropuesta()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Cerrar("La cotización está costeada y lista para el PDF.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("¿En qué va?"), Ct);

        resultado.EsExitoso.Should().BeTrue();
        _modelo.Llamadas[0].Herramientas.Should().BeEquivalentTo(
            "consultar_cotizacion", "proponer_ajuste_margen", "proponer_re_solicitud", "buscar_servicio_conexo",
            "proponer_ajuste_margen_rutas", "proponer_agregar_conexo_rutas", "proponer_quitar_conexo_rutas");
        _modelo.Llamadas[0].Sistema.Should().Contain(Folio);
        resultado.Valor.AccionPropuesta.Should().BeNull();
    }

    [Fact]
    public async Task ProponerElMargenArmaLaAccionConElResumenDelServidorYNoCambiaNada()
    {
        var cotizacion = Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("consultar_cotizacion", new { })
            .Llamar("proponer_ajuste_margen", new { margen = 0.18 })
            .Proponer("Te propongo subir el margen al 18 %. Revisa el resumen y confírmalo con el botón.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Súbele el margen al 18 %"), Ct);

        // Tras la propuesta el turno va directo a la llamada final (sin herramientas): 3 llamadas, no 4.
        _modelo.Llamadas.Should().HaveCount(3);
        _modelo.Llamadas[2].Herramientas.Should().BeEmpty();
        resultado.Valor.DetenerPor.Should().Be(clsAsistenteFolioRunner.CierrePropuesta);
        resultado.Valor.Cierre.Should().Be(clsAsistenteFolioRunner.CierreLlamadaFinal);
        resultado.Valor.Respuesta.Tipo.Should().Be(TipoRespuestaAgente.ConfirmacionRequerida);

        var accion = resultado.Valor.AccionPropuesta!;
        accion.Tipo.Should().Be("ajustar_margen");
        accion.Metodo.Should().Be("PUT");
        accion.Endpoint.Should().Be($"/web/cotizaciones/{Folio}/costeo/margen");
        accion.Cuerpo["margen"].Should().Be(0.18m);
        accion.Cuerpo["justificacion"].Should().BeNull();
        accion.RowVersion.Should().Be("7");
        // El precio de la ruta incluye el conexo, así que el resumen habla del precio total. El margen es solo del
        // flete (2.550.000 / 0,85 = 3.000.000 → / 0,82 = 3.109.756) y el conexo suma su venta sin margen (85.000).
        accion.Resumen.Should().Be(
            "Ajustar el margen de 15 % a 18 %: el precio total pasa de $3.085.000 a $3.194.756 COP. Los servicios conexos no cambian: se cotizan a su precio de venta, sin margen.");
        resultado.Valor.Texto.Should().NotContain("3.194.756", "el texto es del LLM y el LLM no conoce los montos");
        resultado.Valor.HerramientasInvocadas.Should().Equal("consultar_cotizacion", "proponer_ajuste_margen");
        SinCambios(cotizacion);
    }

    // Criterio de aceptación: «pon 20 % en todas las rutas» es UNA propuesta hacia el endpoint en lote y la IA no ve montos.
    [Fact]
    public async Task ElMismoMargenEnTodasLasRutasEsUnaSolaPropuestaHaciaElEndpointEnLote()
    {
        var cotizacion = Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen_rutas", new { margen = 0.2, todas = true })
            .Proponer("Te propongo el 20 % en todas las rutas. Revisa el resumen y confírmalo con el botón.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Pon 20 % en todas las rutas"), Ct);

        var accion = resultado.Valor.AccionPropuesta!;
        accion.Tipo.Should().Be("ajustar_margen_lineas");
        accion.Metodo.Should().Be("PUT");
        accion.Endpoint.Should().Be($"/web/cotizaciones/{Folio}/costeo/margen/lineas");
        accion.Cuerpo["todas"].Should().Be(true);
        accion.Cuerpo["margen"].Should().Be(0.2m);
        accion.Resumen.Should().StartWith("Ajustar el margen a 20 % en todas las rutas (1): el total pasa de $3.085.000 a");
        VerificarSinMontos("3.085.000", "3.272.500");
        SinCambios(cotizacion);
    }

    // El modelo manda las rutas como arreglo JSON: llegan a la herramienta y al cuerpo como la lista ordenada de linea_orden.
    [Fact]
    public async Task LasRutasQueMandaElModeloComoArregloLleganAlCuerpo()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen_rutas", new { margen = 0.2, lineas = new[] { 1 } })
            .Proponer("Te propongo el 20 % en la ruta 1. Revisa el resumen y confírmalo con el botón.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Pon 20 % en la ruta 1"), Ct);

        resultado.Valor.AccionPropuesta!.Cuerpo["lineas"].Should().BeEquivalentTo(new[] { 1 });
        resultado.Valor.AccionPropuesta.Cuerpo.Should().NotContainKey("todas");
    }

    [Theory]
    [InlineData(1.5, null, "AJUSTE_MARGEN_INVALIDO")]
    [InlineData(18, null, "AJUSTE_MARGEN_INVALIDO")]
    [InlineData(0.15, null, "AJUSTE_MARGEN_INVALIDO")]
    [InlineData(0.12, null, "JUSTIFICACION_REQUERIDA")]
    public async Task UnMargenInvalidoNoArmaPropuestaYElModeloRecibeElError(double margen, string? justificacion, string codigo)
    {
        var cotizacion = Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen", new { margen, justificacion }).Cerrar("No pude proponer ese margen.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Ajusta el margen"), Ct);

        resultado.Valor.AccionPropuesta.Should().BeNull();
        resultado.Valor.Salidas.Single().Error!.Codigo.Should().Be(codigo);
        _modelo.Llamadas[1].ResultadosHerramientas.Single().Should().Contain(codigo);
        SinCambios(cotizacion);
    }

    [Fact]
    public async Task PorDebajoDelSugeridoConJustificacionLaPropuestaLaLleva()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen", new { margen = 0.12, justificacion = "El cliente tiene una oferta más baja" })
            .Proponer("Te propongo bajar el margen al 12 %.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Bájalo al 12 %, tiene otra oferta"), Ct);

        var accion = resultado.Valor.AccionPropuesta!;
        accion.Cuerpo["margen"].Should().Be(0.12m);
        accion.Cuerpo["justificacion"].Should().Be("El cliente tiene una oferta más baja");
        accion.Resumen.Should().Contain("de 15 % a 12 %").And.Contain("$2.982.727")
            .And.Contain("Justificación: «El cliente tiene una oferta más baja»", "el comercial ve lo que confirma");
    }

    [Fact]
    public async Task ProponerLaReSolicitudConCambiosDeCargaArmaLaVersionNueva()
    {
        var cotizacion = Registrar(Vencida());
        _modelo.Llamar("proponer_re_solicitud", new { motivo = "El cliente cambió el peso", pesoKg = 28000 })
            .Proponer("Te propongo volver a pedir el flete con 28 toneladas.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Ahora son 28 toneladas, vuelve a pedirlo"), Ct);

        var accion = resultado.Valor.AccionPropuesta!;
        accion.Tipo.Should().Be("re_solicitar_costeo");
        accion.Metodo.Should().Be("POST");
        accion.Endpoint.Should().Be($"/web/cotizaciones/{Folio}/costeo/re-solicitar");
        accion.Cuerpo["motivo"].Should().Be("El cliente cambió el peso");
        accion.Cuerpo["nueva_version"].Should().Be(true);
        ((IReadOnlyDictionary<string, object?>)accion.Cuerpo["datos_carga"]!)["peso_kg"].Should().Be(28000m);
        accion.Resumen.Should().Contain("versión nueva").And.Contain("peso 28.000 kg").And.Contain("Motivo: «El cliente cambió el peso»");
        await _reSolicitud.Received(1).ValidarAsync(_ana.Id, cotizacion,
            Arg.Is<clsEntradaReSolicitudCosteo>(e => e.VersionNueva && e.DatosCarga!.PesoKg == 28000m && e.Motivo == "El cliente cambió el peso"),
            Arg.Any<CancellationToken>());
        SinCambios(cotizacion);
    }

    // Una propuesta que el endpoint rechazaría (peso que no cuadra con las posiciones) nunca se arma.
    [Fact]
    public async Task SiElCasoDeUsoRechazaLaReSolicitudNoHayPropuestaYElModeloRecibeElMotivo()
    {
        Registrar(Vencida());
        _reSolicitud.ValidarAsync(Arg.Any<Guid>(), Arg.Any<clsCotizacionSolicitud>(), Arg.Any<clsEntradaReSolicitudCosteo>(), Arg.Any<CancellationToken>())
            .Returns(new clsErrorDominio(clsCodigosError.SolicitudCosteoInvalida, "El peso total (28000 kg) no cuadra con la suma de las posiciones (28500 kg)."));
        _modelo.Llamar("proponer_re_solicitud", new { motivo = "El cliente cambió el peso", pesoKg = 28000 })
            .Cerrar("El peso no cuadra con las posiciones: ajústalo o actualiza las posiciones desde el detalle.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Ahora son 28 toneladas"), Ct);

        resultado.Valor.AccionPropuesta.Should().BeNull();
        resultado.Valor.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.SolicitudCosteoInvalida);
        _modelo.Llamadas[1].ResultadosHerramientas.Single().Should().Contain("posiciones");
    }

    [Fact]
    public async Task LaReSolicitudSinCambiosEnElMismoFolioCitaElMotivo()
    {
        Registrar(Vencida());
        _modelo.Llamar("proponer_re_solicitud", new { motivo = "Nexo no respondió a tiempo" }).Proponer("Te propongo volver a pedir el flete.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Vuelve a pedirlo"), Ct);

        var accion = resultado.Valor.AccionPropuesta!;
        accion.Cuerpo.Should().NotContainKey("datos_carga");
        accion.Cuerpo["nueva_version"].Should().Be(false);
        accion.Resumen.Should().Contain($"mismo folio {Folio}").And.Contain("Motivo: «Nexo no respondió a tiempo»");
        accion.Resumen.Should().NotContain("servicios conexos", "sin conexos no hay nada que avisar");
    }

    // El comercial ve ANTES de confirmar qué conexos se conservan (solo nombres) y el modelo recibe los mismos nombres.
    [Fact]
    public async Task LaReSolicitudEnElMismoFolioAvisaLosConexosQueSeConservan()
    {
        // Los conexos son filas de la ruta (costo unitario de Nexo y cantidad).
        var cotizacion = Vencida();
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-2", "Stand-by", 20_000m, 2m);
        Registrar(cotizacion);
        _modelo.Llamar("proponer_re_solicitud", new { motivo = "Nexo no respondió a tiempo" }).Cerrar("Te propongo volver a pedir el flete.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Vuelve a pedirlo"), Ct);

        var resumen = resultado.Valor.AccionPropuesta!.Resumen;
        resumen.Should().Contain("Se conservan los servicios conexos «Cargue», «Stand-by» con su costo y siguen en el precio.")
            .And.NotContain("85").And.NotContain("100.000").And.NotContain("47");
        resumen.IndexOf("conexos", StringComparison.Ordinal).Should().BeLessThan(resumen.IndexOf("Motivo:", StringComparison.Ordinal),
            "el aviso va antes del texto del modelo para que el recorte a 300 caracteres no lo oculte");
        var salida = _modelo.Llamadas[1].ResultadosHerramientas.Single();
        salida.Should().Contain("conexos_conservados").And.Contain("Stand-by").And.NotContain("85000").And.NotContain("47059");
    }

    // Peor caso (folio de 29 caracteres, -vN con 5 cambios de carga y 5 conexos de nombre largo). El aviso cabe
    // completo, con "y N más": se acorta la descripción de los cambios, nunca el aviso (que nombra los conexos que se conservan).
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnElPeorCasoElAvisoDeConexosCabeCompletoEnElResumen(bool versionNueva)
    {
        Folio.Length.Should().Be(29);
        var cotizacion = Vencida();
        foreach (var i in Enumerable.Range(1, 5))
        {
            clsCotizacionMuestra.ConConexo(cotizacion, $"CX-{i}", $"Servicio conexo de nombre muy largo para el pliego número {i}", 1m);
        }

        var cambios = new clsCambiosCargaAsistente(28_400m, 2.5m, 3.5m, true, false);
        var entrada = versionNueva
            ? clsPropuestasAsistente.EntradaReSolicitud(new string('m', 300), cambios, nuevaVersion: true)
            : clsPropuestasAsistente.EntradaReSolicitud(new string('m', 300), new clsCambiosCargaAsistente(null, null, null, null, null), nuevaVersion: false);

        var resumen = clsPropuestasAsistente.ReSolicitar(cotizacion, entrada, versionNueva ? cambios : new clsCambiosCargaAsistente(null, null, null, null, null)).Resumen;

        resumen.Length.Should().BeLessThanOrEqualTo(clsAccionPropuesta.LargoMaximoResumen);
        resumen.Should().Contain(Folio).And.Contain(" más con su costo y siguen en el precio.", "el recorte nunca oculta el aviso");
        var motivo = resumen.IndexOf("Motivo:", StringComparison.Ordinal);
        if (motivo >= 0)
        {
            resumen.IndexOf("siguen en el precio", StringComparison.Ordinal).Should().BeLessThan(motivo, "solo el motivo y lo que sigue se recortan");
        }
    }

    [Fact]
    public void ElNombreDeUnConexoSeRecortaACuarentaCaracteres()
    {
        clsPropuestasAsistente.NombreConexo("  Cargue   y  descargue ").Should().Be("Cargue y descargue");
        clsPropuestasAsistente.NombreConexo(new string('x', 60)).Should().Be(new string('x', 39) + "…");
    }

    [Fact]
    public void ElAvisoDeConexosNombraMenosSiNoCabe()
    {
        string[] largos = ["Servicio conexo de nombre muy largo número 1", "Servicio conexo de nombre muy largo número 2", "C3", "C4"];

        var aviso = clsPropuestasAsistente.AvisoConexosConservados(largos, versionNueva: false, largoMaximo: 140);

        aviso.Length.Should().BeLessThanOrEqualTo(140);
        aviso.Should().EndWith("y 3 más con su costo y siguen en el precio.");
        clsPropuestasAsistente.AvisoConexosConservados(largos, versionNueva: false, largoMaximo: 10)
            .Should().Be(" Se conservan 4 servicio(s) conexo(s) con su costo y siguen en el precio.");
    }

    [Fact]
    public void ElAvisoDeConexosNombraTresYCuentaElResto()
    {
        var aviso = clsPropuestasAsistente.AvisoConexosConservados(["A", "B", "C", "D", "E"], versionNueva: true);

        aviso.Should().Be(" La versión nueva copia los servicios conexos «A», «B», «C» y 2 más con su costo y siguen en el precio.");
        clsPropuestasAsistente.AvisoConexosConservados([], versionNueva: false).Should().BeEmpty();
        clsPropuestasAsistente.AvisoConexosConservados([new string('x', 60)], versionNueva: false).Should().Contain("«" + new string('x', 39) + "…»");
    }

    [Fact]
    public async Task LaReSolicitudSinCambiosEnUnCosteoVigenteNoSePropone()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_re_solicitud", new { motivo = "Revisar" }).Cerrar("No se puede volver a pedir todavía.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Vuelve a pedir el flete"), Ct);

        resultado.Valor.AccionPropuesta.Should().BeNull();
        resultado.Valor.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.EstadoNoPermite);
    }

    // Las reglas de conexos son las del endpoint (ValidarPrecondiciones), también LINEA_SIN_COTIZACION_NEXO.
    [Fact]
    public async Task SinCotizacionDeNexoElConexoNoSePropone()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _conexos.BuscarAsync(_ana.Id, "almacenamiento", null, 1, clsHerramientasAsistenteFolio.MaximoServicios, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaServiciosConexos>.Exito(new clsPaginaServiciosConexos(1, 1, 10,
                [new clsServicioConexoCatalogo(Precinto1, "ALM-01", "Almacenamiento", "logistico", "Día", null, null, null, null, null, null)])));
        _modelo.Llamar("buscar_servicio_conexo", new { texto = "almacenamiento", cantidad = 2 }).Cerrar("Todavía no se puede agregar.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Agrégale almacenamiento"), Ct);

        resultado.Valor.AccionPropuesta.Should().BeNull();
        resultado.Valor.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.LineaSinCotizacionNexo);
    }

    [Fact]
    public async Task BuscarUnConexoConCantidadProponeAgregarloSinCostos()
    {
        var cotizacion = Registrar(ConCotizacionNexo(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio)));
        const string IdServicio = "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d";
        _conexos.BuscarAsync(_ana.Id, "almacenamiento", null, 1, clsHerramientasAsistenteFolio.MaximoServicios, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaServiciosConexos>.Exito(
                new clsPaginaServiciosConexos(1, 1, 10, [new clsServicioConexoCatalogo(IdServicio, "ALM-01", "Almacenamiento", "logistico", "Día", null, null, null, null, null, null)])));
        _modelo.Llamar("buscar_servicio_conexo", new { texto = "almacenamiento", cantidad = 2 }).Proponer("Te propongo agregar dos días de almacenamiento.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Agrégale dos días de almacenamiento"), Ct);

        var accion = resultado.Valor.AccionPropuesta!;
        accion.Tipo.Should().Be("agregar_servicios_conexos");
        accion.Endpoint.Should().Be($"/web/cotizaciones/{Folio}/costeo/servicios-conexos");
        var item = ((IEnumerable<IReadOnlyDictionary<string, object?>>)accion.Cuerpo["items"]!).Single();
        item["linea_orden"].Should().Be(1);
        item["servicio_id"].Should().Be(IdServicio);
        item["cantidad"].Should().Be(2m);
        // El conexo se cotiza a su precio de venta, sin el margen de la ruta.
        accion.Resumen.Should().Contain("Almacenamiento").And.Contain("Se cotiza a su precio de venta de Nexo, sin margen; queda incluido en el PDF.")
            .And.NotContain("15 %").And.NotContain("margen de la ruta");
        _modelo.Llamadas[1].ResultadosHerramientas.Single().Should().Contain(IdServicio).And.NotContainEquivalentOf("costo");
        await _conexos.DidNotReceiveWithAnyArgs().AgregarAsync(default, default!, default, default, default, Ct);
        SinCambios(cotizacion);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(Precinto2, Precinto2)]
    public async Task ConVariosServiciosSoloProponeElQueEligioElComercial(string? servicioId, string? propuesto)
    {
        Registrar(ConCotizacionNexo(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio)));
        _conexos.BuscarAsync(_ana.Id, "precinto", null, 1, clsHerramientasAsistenteFolio.MaximoServicios, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaServiciosConexos>.Exito(new clsPaginaServiciosConexos(2, 1, 10,
            [
                new clsServicioConexoCatalogo(Precinto1, "P-1", "Precinto", "adicional", "Unidad", null, null, null, null, null, null),
                new clsServicioConexoCatalogo(Precinto2, "P-2", "Precinto electrónico", "adicional", "Unidad", null, null, null, null, null, null),
            ])));
        _modelo.Llamar("buscar_servicio_conexo", new { texto = "precinto", cantidad = 1, servicioId }).Cerrar("Listo.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Agrega un precinto"), Ct);

        _modelo.Llamadas[1].ResultadosHerramientas.Single().Should().Contain("Precinto electrónico");
        if (propuesto is null)
        {
            resultado.Valor.AccionPropuesta.Should().BeNull("hay dos servicios posibles y nunca se adivina cuál");
        }
        else
        {
            var item = ((IEnumerable<IReadOnlyDictionary<string, object?>>)resultado.Valor.AccionPropuesta!.Cuerpo["items"]!).Single();
            item["servicio_id"].Should().Be(propuesto);
        }
    }

    [Theory]
    [InlineData("calcular_precio")]
    [InlineData("generar_pdf")]
    [InlineData("obtener_margen")]
    [InlineData("buscar_tarifa")]
    [InlineData("buscar_cliente")]
    [InlineData("resolver_municipio")]
    [InlineData("registrar_ajuste_margen")]
    [InlineData("enviar_correo")]
    public async Task UnaHerramientaRetiradaRespondeHerramientaDesconocidaYNoSeEjecuta(string herramienta)
    {
        var cotizacion = Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar(herramienta, new { costo = 1, margen = 0.5 }).Cerrar("Esa acción no está disponible aquí.");

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Calcula el precio"), Ct);

        resultado.Valor.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.HerramientaDesconocida);
        resultado.Valor.HerramientasInvocadas.Should().BeEmpty();
        resultado.Valor.AccionPropuesta.Should().BeNull();
        SinCambios(cotizacion);
    }

    [Fact]
    public async Task NingunMensajeAlModeloTraeMontosEnPesos()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        GuionCompleto();

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("¿Cuánto nos cuesta y cuánto le cobramos?"), Ct);

        resultado.Valor.AccionPropuesta!.Resumen.Should().Contain("$3.194.756", "el resumen con montos sí lo arma el servidor");
        VerificarSinMontos("costo", "2550000", "2635000", "3085000", "3194756", "85000", "250000000", "12000000");
    }

    [Fact]
    public async Task NingunMensajeAlModeloTraeMontosEnDolares()
    {
        Registrar(EnDolares());
        GuionCompleto();

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("¿Cuánto es en dólares?"), Ct);

        // 2.550.000 con el margen + 85.000 del conexo sin margen, en dólares: 771,25 al 15 % y 798,69 al 18 %.
        resultado.Valor.AccionPropuesta!.Resumen.Should().Contain("US$798,69").And.Contain("USD");
        VerificarSinMontos("costo", "2550000", "771", "79869", "trm_aplicada", "4.000", "21,25", "85000");
        _modelo.Llamadas[1].ResultadosHerramientas[0].Should().Contain("\"moneda\":\"USD\"");
    }

    [Fact]
    public async Task LaSalidaDeConsultarCotizacionTraeSoloPorcentajesEstadoRutaLineaMonedaYConexos()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("consultar_cotizacion", new { }).Cerrar("Está costeada.");

        await Runner().EjecutarTurnoAsync(Solicitud("¿En qué va?"), Ct);

        using var salida = JsonDocument.Parse(_modelo.Llamadas[1].ResultadosHerramientas.Single());
        var raiz = salida.RootElement;
        raiz.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "folio", "version", "estado_costeo", "estado_envio", "tiene_resultado", "tiene_pdf", "pdf_desactualizado", "cliente", "linea_negocio",
            "origen", "destino", "tipo_vehiculo", "moneda", "margen_actual", "margen_sugerido", "origen_margen", "conexos", "numero_rutas", "rutas");
        // Cada ruta solo con porcentajes, origen, destino, vehículo y conexos (nombre y cantidad).
        raiz.GetProperty("rutas")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "linea_orden", "origen", "destino", "tipo_vehiculo", "margen_actual", "margen_sugerido", "origen_margen", "conexos");
        raiz.GetProperty("rutas")[0].GetProperty("conexos")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("nombre", "cantidad");
        raiz.GetProperty("margen_actual").GetDecimal().Should().Be(0.15m);
        raiz.GetProperty("margen_sugerido").GetDecimal().Should().Be(0.15m);
        raiz.GetProperty("estado_costeo").GetString().Should().Be("autocosteo");
        raiz.GetProperty("conexos")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("nombre", "cantidad", "incluido");
    }

    [Fact]
    public async Task SiElModeloEscribeUnMontoElValidadorLoRechazaYLaPropuestaSaleIgual()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _modelo.Llamar("proponer_ajuste_margen", new { margen = 0.18 })
            .ResponderFinal("El precio queda en $3.200.000.", "confirmacion_requerida", true);

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Súbelo al 18 %"), Ct);

        resultado.Valor.EsRespaldo.Should().BeTrue("un monto que nada respalda nunca llega al comercial");
        resultado.Valor.AccionPropuesta.Should().NotBeNull();
        _modelo.Llamadas.Should().HaveCount(2, "con propuesta no hay reintento: su respaldo es el texto fijo del servidor (HU-013.16)");
        resultado.Valor.ReintentosRespuesta.Should().Be(0);
        clsRespuestaChat.DesdeAsistente(resultado.Valor, clsTextosAgente.PropuestaSinTexto).Texto.Should().Be(clsTextosAgente.PropuestaSinTexto);
    }

    [Fact]
    public async Task LaSesionQuedaLigadaAlComercialYAlFolioYElHistorialNoGuardaElResumen()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        GuionCompleto();

        await Runner().EjecutarTurnoAsync(Solicitud("Súbele el margen al 18 %"), Ct);

        var sesion = await _sesiones.ObtenerAsync(Espacio, _hilo, Ct);
        sesion!.UsuarioId.Should().Be(_ana.Id);
        sesion.EstadoAgente!.FolioEnProgreso.Should().Be(Folio);
        sesion.EstadoAgente.CotizacionId.Should().NotBeNull("el estado es solo la cotización de la conversación, sin montos (HU-013.9)");
        _sesiones.Eventos.Should().OnlyContain(e => e.ThreadId == _hilo);
        string.Join("\n", _sesiones.Eventos.Select(e => e.Contenido + e.Herramientas)).Should().NotContain("3.109.756").And.NotContain("3109756");
        await _auditoria.Received().RegistrarAsync("proponer_ajuste_margen", Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), _hilo);
    }

    [Fact]
    public async Task OtroComercialNoContinuaLaSesion()
    {
        Registrar(clsAjusteMargenCosteoServiceTests.Costeada(_ana.Id, Folio));
        _sesiones.Agregar(Cotifly.Dominio.Sesiones.clsSesion.Crear(Espacio, _hilo, _reloj.AhoraUtc, Guid.NewGuid()).Valor);

        var resultado = await Runner().EjecutarTurnoAsync(Solicitud("Hola"), Ct);

        resultado.Error.Codigo.Should().Be(Cotifly.Aplicacion.Comun.clsCodigosAplicacion.NoEncontrado);
        _modelo.Llamadas.Should().BeEmpty();
    }

    private void GuionCompleto() =>
        _modelo.Llamar("consultar_cotizacion", new { })
            .Llamar("proponer_ajuste_margen", new { margen = 0.18 })
            .Proponer("Te propongo subir el margen al 18 %. Revisa el resumen y confírmalo.");

    /// <summary>
    /// Ningún mensaje que recibió el modelo (sistema, historial, llamadas, resultados de herramientas, instrucción final) contiene los
    /// textos prohibidos, también con los separadores de miles quitados (<c>3.000.000</c> → <c>3000000</c>).
    /// </summary>
    private void VerificarSinMontos(params string[] prohibidos)
    {
        var textos = _modelo.Llamadas.SelectMany(l => l.Mensajes).SelectMany(m => m.Contents.Select(Texto).Append(m.Text)).ToList();
        var todo = string.Join("\n", textos);
        var sinSeparadores = SeparadorMiles().Replace(todo, string.Empty);
        foreach (var prohibido in prohibidos)
        {
            todo.Should().NotContainEquivalentOf(prohibido);
            sinSeparadores.Should().NotContainEquivalentOf(prohibido);
        }
    }

    private static string Texto(AIContent contenido) => contenido switch
    {
        TextContent texto => texto.Text,
        FunctionCallContent llamada => llamada.Name + JsonSerializer.Serialize(llamada.Arguments),
        FunctionResultContent resultado => resultado.Result?.ToString() ?? string.Empty,
        _ => string.Empty,
    };

    // El precio y el margen se comparan con los que tenía al registrarla (la vencida no tiene precio; el margen es de la ruta 1).
    private void SinCambios(clsCotizacionSolicitud cotizacion)
    {
        var inicial = _inicial[cotizacion];
        cotizacion.VersionNegocio.Should().Be(7, "el chat nunca ejecuta mutaciones");
        cotizacion.PrecioFinal.Should().Be(inicial.Precio);
        cotizacion.PrimeraRuta!.MargenAplicado.Should().Be(inicial.Margen);
        cotizacion.Rutas.Sum(r => r.Conexos.Count).Should().Be(inicial.Conexos);
        _unidad.DidNotReceiveWithAnyArgs().GuardarCambiosSiVersionAsync(default, default, default);
    }

    private clsCotizacionSolicitud Registrar(clsCotizacionSolicitud cotizacion, Guid? dueno = null)
    {
        _cotizaciones.ObtenerPorFolioDelUsuarioAsync(cotizacion.Folio, dueno ?? _ana.Id, Arg.Any<CancellationToken>()).Returns(cotizacion);
        _inicial[cotizacion] = (cotizacion.PrecioFinal, cotizacion.PrimeraRuta?.MargenAplicado, cotizacion.Rutas.Sum(r => r.Conexos.Count));
        return cotizacion;
    }

    /// <summary>Con la cotización de Nexo y la línea de Nexo del camión (las guarda el polling): admite agregar conexos.</summary>
    private static clsCotizacionSolicitud ConCotizacionNexo(clsCotizacionSolicitud cotizacion)
    {
        cotizacion.RegistrarCotizacionNexo("COT-NEXO-1");
        cotizacion.Vehiculos[0].AsignarLineaNexo("L1");
        return cotizacion;
    }

    /// <summary>
    /// Costeada en USD con la TRM 4.000 fijada: flete 2.550.000 COP y el conexo Cargue (85.000 COP) en la ruta, al 15 % = 775 USD
    /// (el conexo va en el precio de la ruta; el conexo solo, 100.000 COP = 25 USD).
    /// </summary>
    private clsCotizacionSolicitud EnDolares()
    {
        var cotizacion = clsCotizacionMuestra.Costeada(flete: 2_550_000m, margen: 0.15m, folio: Folio, usuarioId: _ana.Id, moneda: "USD", trm: 4000m);
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m);
        return clsCotizacionMuestra.ConVersionNegocio(cotizacion, 7);
    }

    /// <summary>Del formulario, enviada a Nexo y con el polling vencido (72 h sin costeo): admite re-solicitar.</summary>
    private clsCotizacionSolicitud Vencida()
    {
        // Sin flete no hay precio; el margen resuelto al enviar queda en la ruta.
        var cotizacion = clsCotizacionMuestra.Formulario(Folio, _ana.Id);
        cotizacion.RegistrarEnvioCosteo("SOL-1", false, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        cotizacion.PrimeraRuta!.RegistrarMargenCosteo(0.15m, 0.15m, OrigenMargen.PoliticaLinea, "margen");
        cotizacion.VencerPollingCosteo("Nexo no costeó en 72 h").Should().BeTrue();
        return clsCotizacionMuestra.ConVersionNegocio(cotizacion, 7);
    }

    private clsSolicitudTurnoFolio Solicitud(string mensaje) => new(mensaje, Espacio, _hilo, _ana.Id, Folio);

    private clsAsistenteFolioRunner Runner(
        clsOpcionesAgente? opcionesAgente = null,
        IClsCandadoSesion? candado = null,
        IChatClient? modelo = null,
        clsOpcionesSesiones? opcionesSesiones = null,
        Microsoft.Extensions.Logging.ILogger<clsAsistenteFolioRunner>? logger = null)
    {
        _contextoFolio = new clsContextoAsistenteFolio();
        opcionesAgente ??= new clsOpcionesAgente();
        return new clsAsistenteFolioRunner(
            modelo ?? _modelo, _sesiones, _usuarios, _cotizaciones, _politicas, _unidad, _reloj, _calendario,
            new clsValidadorRespuestaAgente(Options.Create(opcionesAgente), NullLogger<clsValidadorRespuestaAgente>.Instance, new clsFabricaMedidoresPrueba(), _reloj),
            _auditoria, candado ?? new clsCandadoSesionFalso(), _contextoFolio,
            new clsHerramientasAsistenteFolio(_contextoFolio, _conexos, _reSolicitud, Substitute.For<IClsCategoriasConexos>()),
            Options.Create(opcionesAgente), Options.Create(opcionesSesiones ?? new clsOpcionesSesiones()), logger ?? NullLogger<clsAsistenteFolioRunner>.Instance);
    }

    [GeneratedRegex(@"(?<=\d)[.,](?=\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SeparadorMiles();
}
