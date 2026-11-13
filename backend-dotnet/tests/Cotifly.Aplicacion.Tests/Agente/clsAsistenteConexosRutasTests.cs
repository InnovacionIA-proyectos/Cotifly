using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// El mismo conexo agregado o quitado en varias rutas desde el asistente: UNA sola propuesta (<c>agregar_servicios_conexos_lineas</c> o
/// <c>quitar_servicios_conexos_lineas</c>) hacia los endpoints en lote, con sus mismas reglas y sin que la IA reciba montos.
/// </summary>
public sealed class clsAsistenteConexosRutasTests
{
    private const string Folio = "PCN-S3";
    private const string Cargue = "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d";
    private const string CargueNocturno = "0b4d8e1f-2a3c-4d5e-8f90-a1b2c3d4e5f6";

    private readonly clsContextoAsistenteFolio _contexto = new();
    private readonly IClsServiciosConexosCosteoService _conexos = Substitute.For<IClsServiciosConexosCosteoService>();
    private readonly IClsCategoriasConexos _categorias = Substitute.For<IClsCategoriasConexos>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public clsAsistenteConexosRutasTests() =>
        _categorias.NombresAsync(Arg.Any<clsCotizacionSolicitud>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, string>)new Dictionary<string, string>());

    private clsHerramientasAsistenteFolio Herramientas(clsCotizacionSolicitud cotizacion)
    {
        _contexto.Iniciar(Guid.NewGuid(), cotizacion, "Importacion", "web/s1/" + cotizacion.Folio);
        return new clsHerramientasAsistenteFolio(_contexto, _conexos, Substitute.For<IClsReSolicitudCosteoService>(), _categorias);
    }

    /// <summary>Spot de 3 rutas al 15 % con su línea de Nexo (fletes 850.000, 1.700.000 y 425.000).</summary>
    private static clsCotizacionSolicitud Spot() =>
        clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m], folio: Folio, tipo: TipoSolicitud.Spot);

    private static clsServicioConexoCatalogo Servicio(string id, string nombre) =>
        new(id, "CAR-01", nombre, "logistico", "Unidad", null, null, null, null, null, null);

    private void Catalogo(string texto, params clsServicioConexoCatalogo[] servicios) =>
        _conexos.BuscarAsync(Arg.Any<Guid>(), texto, null, 1, clsHerramientasAsistenteFolio.MaximoServicios, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsPaginaServiciosConexos>.Exito(new clsPaginaServiciosConexos(servicios.Length, 1, 10, servicios)));

    [Fact]
    public async Task AgregarElMismoConexoATodasLasRutasEsUnaSolaPropuesta()
    {
        var cotizacion = Spot();
        Catalogo("cargue", Servicio(Cargue, "Cargue en puerto"));
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerAgregarConexoRutasAsync("cargue", 2m, null, true, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        var propuesta = _contexto.Propuesta!;
        propuesta.Tipo.Should().Be(clsAccionPropuesta.TipoAgregarServiciosConexosLineas);
        propuesta.Metodo.Should().Be("POST");
        propuesta.Endpoint.Should().Be($"/web/cotizaciones/{Folio}/costeo/servicios-conexos/lineas");
        propuesta.Cuerpo.Keys.Should().Equal("todas", "servicio_id", "cantidad");
        propuesta.Cuerpo["todas"].Should().Be(true);
        propuesta.Cuerpo["servicio_id"].Should().Be(Cargue);
        propuesta.Cuerpo["cantidad"].Should().Be(2m);
        propuesta.Resumen.Should().StartWith("Agregar 2 (Unidad) de «Cargue en puerto» a todas las rutas (3) en Nexo.");
        SinMontos(resultado);
        cotizacion.Rutas.Should().OnlyContain(r => r.Conexos.Count == 0, "proponer no cambia la cotización");
    }

    [Fact]
    public async Task AgregarEnUnaLicitacionSeProponeComoEnUnaSpot()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m], folio: "PCN-L3");
        Catalogo("cargue", Servicio(Cargue, "Cargue en puerto"));

        var resultado = await Herramientas(cotizacion).ProponerAgregarConexoRutasAsync("cargue", 2m, null, true, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        var propuesta = _contexto.Propuesta!;
        propuesta.Tipo.Should().Be(clsAccionPropuesta.TipoAgregarServiciosConexosLineas);
        propuesta.Endpoint.Should().Be("/web/cotizaciones/PCN-L3/costeo/servicios-conexos/lineas");
        propuesta.Cuerpo["todas"].Should().Be(true);
        propuesta.Resumen.Should().StartWith("Agregar 2 (Unidad) de «Cargue en puerto» a todas las rutas (3) en Nexo.");
        SinMontos(resultado);
    }

    [Fact]
    public async Task ConVariasRutasYSinIndicarCualesPreguntaAntesDeBuscar()
    {
        var herramientas = Herramientas(Spot());

        var resultado = await herramientas.ProponerAgregarConexoRutasAsync("cargue", 2m, null, null, null, Ct);

        _contexto.Propuesta.Should().BeNull();
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        salida["requiere_ruta"].Should().Be(true);
        salida["mensaje"].Should().Be(clsHerramientasAsistenteFolio.MensajePreguntarRutas);
        await _conexos.DidNotReceiveWithAnyArgs().BuscarAsync(default, default!, default, default, default, Ct);
    }

    [Fact]
    public async Task ConVariosServiciosParecidosPreguntaCualSinProponer()
    {
        Catalogo("cargue", Servicio(Cargue, "Cargue en puerto"), Servicio(CargueNocturno, "Cargue nocturno"));
        var herramientas = Herramientas(Spot());

        var resultado = await herramientas.ProponerAgregarConexoRutasAsync("cargue", 2m, null, null, [1, 3], Ct);

        _contexto.Propuesta.Should().BeNull();
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        salida["propuesta"].Should().BeNull();
        ((IEnumerable<Dictionary<string, object?>>)salida["servicios"]!).Select(s => s["nombre"]).Should().Equal("Cargue en puerto", "Cargue nocturno");
    }

    [Fact]
    public async Task LasRutasQueYaLoTienenNoCambianYSiTodasLoTienenNoHayPropuesta()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(Spot(), Cargue, "Cargue en puerto", 85_000m, 1m, orden: 2);
        Catalogo("cargue", Servicio(Cargue, "Cargue en puerto"));
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerAgregarConexoRutasAsync("cargue", 2m, null, null, [2, 1], Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        _contexto.Propuesta!.Cuerpo["lineas"].Should().BeEquivalentTo(new[] { 1, 2 }, o => o.WithStrictOrdering());
        _contexto.Propuesta.Resumen.Should().StartWith("Agregar 2 (Unidad) de «Cargue en puerto» a la ruta 1 en Nexo.")
            .And.Contain("La ruta 2 ya lo tiene y no cambia.");

        var ninguna = await Herramientas(cotizacion).ProponerAgregarConexoRutasAsync("cargue", 2m, null, null, [2], Ct);

        ninguna.Error!.Codigo.Should().Be(clsCodigosError.ConexoInvalido);
        _contexto.Propuesta.Should().BeNull();
    }

    [Fact]
    public async Task QuitarUnConexoDeTodasLasRutasEsUnaSolaPropuestaSinTildesNiMayusculas()
    {
        var cotizacion = Spot();
        clsCotizacionMuestra.ConConexo(cotizacion, Cargue, "Cargue en puerto", 85_000m, 1m, orden: 1);
        clsCotizacionMuestra.ConConexo(cotizacion, Cargue, "Cargue en puerto", 85_000m, 2m, orden: 3);
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerQuitarConexoRutasAsync("CÁRGUE en Puerto", null, true, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        var propuesta = _contexto.Propuesta!;
        propuesta.Tipo.Should().Be(clsAccionPropuesta.TipoQuitarServiciosConexosLineas);
        propuesta.Metodo.Should().Be("POST");
        propuesta.Endpoint.Should().Be($"/web/cotizaciones/{Folio}/costeo/servicios-conexos/lineas/quitar");
        propuesta.Cuerpo.Keys.Should().Equal("todas", "servicio_id");
        propuesta.Cuerpo["servicio_id"].Should().Be(Cargue);
        propuesta.Resumen.Should().StartWith("Quitar «Cargue en puerto» de las rutas 1 y 3:").And.Contain("La ruta 2 no lo tiene y no cambia.");
        SinMontos(resultado);
        cotizacion.Rutas.Sum(r => r.Conexos.Count).Should().Be(2, "proponer no cambia la cotización");
    }

    // Criterio de aceptación: quitar un conexo que la cotización no tiene no propone nada y muestra los que sí tiene.
    [Fact]
    public async Task QuitarUnConexoQueNoEstaMuestraLosQueTieneLaCotizacionSinProponer()
    {
        var cotizacion = clsCotizacionMuestra.ConConexo(Spot(), Cargue, "Cargue en puerto", 85_000m, 1m, orden: 2);
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerQuitarConexoRutasAsync("almacenamiento", null, true, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _contexto.Propuesta.Should().BeNull();
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        salida["propuesta"].Should().BeNull();
        ((IEnumerable<Dictionary<string, object?>>)salida["conexos"]!).Select(c => c["nombre"]).Should().Equal("Cargue en puerto");
        SinMontos(resultado);
    }

    [Fact]
    public async Task QuitarConVariosConexosQueCoincidenPreguntaCual()
    {
        var cotizacion = Spot();
        clsCotizacionMuestra.ConConexo(cotizacion, Cargue, "Cargue en puerto", 85_000m, 1m, orden: 1);
        clsCotizacionMuestra.ConConexo(cotizacion, CargueNocturno, "Cargue nocturno", 40_000m, 1m, orden: 2);
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerQuitarConexoRutasAsync("cargue", null, true, null, Ct);

        _contexto.Propuesta.Should().BeNull();
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        ((IEnumerable<Dictionary<string, object?>>)salida["conexos"]!).Select(c => c["servicio_id"]).Should().Equal(Cargue, CargueNocturno);

        var elegido = await herramientas.ProponerQuitarConexoRutasAsync("cargue", CargueNocturno, true, null, Ct);

        elegido.EsExitoso.Should().BeTrue(elegido.Error?.Mensaje);
        _contexto.Propuesta!.Cuerpo["servicio_id"].Should().Be(CargueNocturno);
    }

    /// <summary>Ningún monto llega al modelo: ni costos ni valores de venta de los conexos.</summary>
    private static void SinMontos(clsResultadoHerramienta resultado)
    {
        var json = JsonSerializer.Serialize(resultado.ParaModelo());
        foreach (var monto in new[] { "850000", "1700000", "425000", "85000", "40000", "1000000", "2000000", "500000" })
        {
            json.Should().NotContain(monto, "la IA nunca recibe montos");
        }

        json.Should().NotContain("precio").And.NotContain("costo");
    }
}
