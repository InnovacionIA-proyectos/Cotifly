using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// El mismo margen en varias rutas desde el asistente: UNA sola propuesta <c>ajustar_margen_lineas</c> hacia
/// <c>PUT .../costeo/margen/lineas</c>, con las mismas reglas del endpoint en lote, un resumen con los precios del servidor y sin que la IA
/// reciba montos.
/// </summary>
public sealed class clsAsistenteMargenRutasTests
{
    private readonly clsContextoAsistenteFolio _contexto = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private clsHerramientasAsistenteFolio Herramientas(clsCotizacionSolicitud cotizacion)
    {
        _contexto.Iniciar(Guid.NewGuid(), cotizacion, "Importacion", "web/s1/" + cotizacion.Folio);
        return new clsHerramientasAsistenteFolio(_contexto, Substitute.For<IClsServiciosConexosCosteoService>(), Substitute.For<IClsReSolicitudCosteoService>(), Substitute.For<IClsCategoriasConexos>());
    }

    /// <summary>3 rutas al 15 %: precios 1.000.000, 2.000.000 (+ 170.000 del conexo de la ruta 2) y 500.000; total 3.670.000.</summary>
    private static clsCotizacionSolicitud TresRutas()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m], folio: "PCN-L1");
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m, 2m, orden: 2);
        return cotizacion;
    }

    [Fact]
    public async Task ElMismoMargenEnTodasLasRutasEsUnaSolaPropuestaConTodasTrue()
    {
        var cotizacion = TresRutas();
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerAjusteMargenRutasAsync(0.20m, null, true, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        var propuesta = _contexto.Propuesta!;
        propuesta.Tipo.Should().Be(clsAccionPropuesta.TipoAjustarMargenLineas);
        propuesta.Metodo.Should().Be("PUT");
        propuesta.Endpoint.Should().Be("/web/cotizaciones/PCN-L1/costeo/margen/lineas");
        propuesta.Cuerpo.Keys.Should().Equal("todas", "margen", "justificacion");
        propuesta.Cuerpo["todas"].Should().Be(true);
        propuesta.Cuerpo["margen"].Should().Be(0.20m);
        propuesta.RowVersion.Should().Be(clsCosteoCotizacionService.VersionEtag(cotizacion));
        propuesta.Resumen.Should().StartWith("Ajustar el margen a 20 % en todas las rutas (3): el total pasa de")
            .And.Contain("3.670.000").And.Contain("3.888.750", "1.062.500 + 2.125.000 + 170.000 + 531.250")
            .And.Contain("Los servicios conexos no cambian");
        propuesta.Resumen.Length.Should().BeLessThanOrEqualTo(clsAccionPropuesta.LargoMaximoResumen);
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        salida["propuesta"].Should().Be(clsAccionPropuesta.TipoAjustarMargenLineas);
        SinMontos(salida);
        cotizacion.RutasOrdenadas.Should().OnlyContain(r => r.MargenAplicado == 0.15m, "proponer no cambia la cotización");
    }

    [Fact]
    public async Task ConVariasRutasYSinIndicarCualesPreguntaSinProponer()
    {
        var herramientas = Herramientas(TresRutas());

        var resultado = await herramientas.ProponerAjusteMargenRutasAsync(0.20m, null, null, null, Ct);

        _contexto.Propuesta.Should().BeNull();
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        salida["requiere_ruta"].Should().Be(true);
        salida["mensaje"].Should().Be(clsHerramientasAsistenteFolio.MensajePreguntarRutas);
        ((IReadOnlyList<IReadOnlyDictionary<string, object?>>)salida["rutas"]!).Select(r => r["linea_orden"]).Should().Equal(1, 2, 3);
        SinMontos(salida);
    }

    [Fact]
    public async Task ConUnaSolaRutaLaToma()
    {
        var herramientas = Herramientas(clsCotizacionMuestra.Costeada(folio: "PCN-1"));

        var resultado = await herramientas.ProponerAjusteMargenRutasAsync(0.18m, null, null, null, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        _contexto.Propuesta!.Cuerpo["lineas"].Should().BeEquivalentTo(new[] { 1 });
        _contexto.Propuesta.Cuerpo.Should().NotContainKey("todas");
        _contexto.Propuesta.Resumen.Should().StartWith("Ajustar el margen a 18 % en la ruta 1");
    }

    [Fact]
    public async Task LasRutasVanOrdenadasYElResumenAvisaLasQueYaTienenEseMargen()
    {
        var cotizacion = TresRutas();
        cotizacion.RutasOrdenadas[1].RegistrarMargenCosteo(0.20m, 0.20m, Cotifly.Dominio.Margen.OrigenMargen.PoliticaLinea, "margen");
        cotizacion.RecalcularTotales(clsCotizacionMuestra.Ahora);
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerAjusteMargenRutasAsync(0.20m, null, null, [3, 1, 2], Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        var propuesta = _contexto.Propuesta!;
        propuesta.Cuerpo["lineas"].Should().BeEquivalentTo(new[] { 1, 2, 3 }, o => o.WithStrictOrdering());
        propuesta.Cuerpo.Should().NotContainKey("todas");
        propuesta.Resumen.Should().StartWith("Ajustar el margen a 20 % en las rutas 1 y 3")
            .And.Contain("La ruta 2 ya tiene ese margen y no cambia.");
    }

    [Fact]
    public async Task SiNingunaRutaCambiaNoHayPropuesta()
    {
        var herramientas = Herramientas(TresRutas());

        var resultado = await herramientas.ProponerAjusteMargenRutasAsync(0.15m, null, true, null, Ct);

        resultado.Error!.Codigo.Should().Be(clsCodigosError.AjusteMargenInvalido);
        resultado.Error.Mensaje.Should().Contain("15 %");
        _contexto.Propuesta.Should().BeNull();
    }

    [Fact]
    public async Task ElErrorDeUnaRutaLlegaALaIaConSuNumeroYSinDetalle()
    {
        var cotizacion = TresRutas();
        cotizacion.RutasOrdenadas[1].RegistrarMargenCosteo(0.15m, 0.25m, Cotifly.Dominio.Margen.OrigenMargen.SapTm, "margen");
        cotizacion.RecalcularTotales(clsCotizacionMuestra.Ahora);
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerAjusteMargenRutasAsync(0.20m, null, true, null, Ct);

        resultado.Error!.Codigo.Should().Be(clsCodigosError.JustificacionRequerida);
        resultado.Error.Mensaje.Should().StartWith("Ruta 2: ");
        resultado.Error.Datos.Should().BeNull("el detalle estructurado no va al modelo");
        _contexto.Propuesta.Should().BeNull();
    }

    [Fact]
    public async Task TodasYLineasALaVezEsLaMismaSolicitudInvalidaDelEndpoint()
    {
        var herramientas = Herramientas(TresRutas());

        var resultado = await herramientas.ProponerAjusteMargenRutasAsync(0.20m, null, true, [1], Ct);

        resultado.Error!.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        _contexto.Propuesta.Should().BeNull();
    }

    /// <summary>Ningún monto llega al modelo: ni precios, ni costos, ni valores de conexos.</summary>
    private static void SinMontos(IReadOnlyDictionary<string, object?> salida)
    {
        var json = JsonSerializer.Serialize(salida);
        foreach (var monto in new[] { "850000", "1700000", "425000", "1000000", "2000000", "500000", "85000", "170000", "3670000", "3888750", "3.670.000", "3.888.750" })
        {
            json.Should().NotContain(monto, "la IA nunca recibe montos");
        }

        json.Should().NotContain("precio").And.NotContain("costo");
    }
}
