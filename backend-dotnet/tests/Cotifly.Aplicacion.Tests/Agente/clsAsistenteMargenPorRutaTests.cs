using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Asistente por folio con el margen por ruta (HU-021.8, #135, decisión 6, ADR-018 §2b): con varias rutas y sin indicación PREGUNTA cuál
/// (sin propuesta y sin montos); con la ruta, propone el <c>PUT .../costeo/margen</c> con <c>linea_orden</c> y un resumen con los precios
/// del servidor; <c>consultar_cotizacion</c> le muestra las rutas solo con porcentajes, origen y destino. La IA nunca recibe montos.
/// </summary>
public sealed class clsAsistenteMargenPorRutaTests
{
    private readonly clsContextoAsistenteFolio _contexto = new();

    private clsHerramientasAsistenteFolio Herramientas(clsCotizacionSolicitud cotizacion)
    {
        _contexto.Iniciar(Guid.NewGuid(), cotizacion, "Importacion", "web/s1/" + cotizacion.Folio);
        return new clsHerramientasAsistenteFolio(_contexto, Substitute.For<IClsServiciosConexosCosteoService>(), Substitute.For<IClsReSolicitudCosteoService>());
    }

    /// <summary>Pliego de 3 rutas al 15 %: precios 1.000.000, 2.000.000 y 500.000 (los fletes son 850.000, 1.700.000 y 425.000).</summary>
    private static clsCotizacionSolicitud TresRutas()
    {
        var cotizacion = clsCotizacionMuestra.LicitacionCosteada([850_000m, 1_700_000m, 425_000m], folio: "PCN-L1");
        clsCotizacionMuestra.ConConexo(cotizacion, "CX-1", "Cargue", 85_000m, 2m, orden: 2);
        return cotizacion;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ConVariasRutasYSinIndicarLaRutaPreguntaCualSinProponerNiMostrarMontos()
    {
        var herramientas = Herramientas(TresRutas());

        var resultado = await herramientas.ProponerAjusteMargenAsync(0.20m, null, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _contexto.Propuesta.Should().BeNull("sin la ruta no hay propuesta: se pregunta");
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        salida["requiere_ruta"].Should().Be(true);
        salida["mensaje"].Should().Be(clsHerramientasAsistenteFolio.MensajePreguntarRuta);
        var rutas = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)salida["rutas"]!;
        rutas.Select(r => r["linea_orden"]).Should().Equal(1, 2, 3);
        rutas.Select(r => r["destino"]).Should().Equal("Destino 1", "Destino 2", "Destino 3");
        SinMontos(salida);
    }

    [Fact]
    public async Task ConLaRutaIndicadaProponeElMargenDeEsaRutaConSuPrecioYElTotal()
    {
        var cotizacion = TresRutas();
        var herramientas = Herramientas(cotizacion);

        var resultado = await herramientas.ProponerAjusteMargenAsync(0.20m, null, 3, Ct);

        resultado.EsExitoso.Should().BeTrue(resultado.Error?.Mensaje);
        var propuesta = _contexto.Propuesta!;
        propuesta.Tipo.Should().Be(clsAccionPropuesta.TipoAjustarMargen);
        propuesta.Metodo.Should().Be("PUT");
        propuesta.Endpoint.Should().Be("/web/cotizaciones/PCN-L1/costeo/margen");
        propuesta.Cuerpo["linea_orden"].Should().Be(3);
        propuesta.Cuerpo["margen"].Should().Be(0.20m);
        propuesta.Resumen.Should().Contain("ruta 3").And.Contain("Destino 3")
            .And.Contain("500.000").And.Contain("531.250", "425.000 / 0,80")
            .And.Contain("3.670.000").And.Contain("3.701.250", "el total cambia solo por la ruta 3 (HU-043.2: los conexos de la ruta 2 sin margen)")
            .And.Contain("Las demás rutas no cambian");
        propuesta.Resumen.Length.Should().BeLessThanOrEqualTo(clsAccionPropuesta.LargoMaximoResumen);
        var salida = (IReadOnlyDictionary<string, object?>)resultado.ParaModelo();
        salida["linea_orden"].Should().Be(3);
        SinMontos(salida);
        cotizacion.RutasOrdenadas[2].MargenAplicado.Should().Be(0.15m, "proponer no cambia la cotización");
    }

    [Fact]
    public async Task LaJustificacionSeExigeContraElSugeridoDeLaRutaElegida()
    {
        var cotizacion = TresRutas();
        cotizacion.RutasOrdenadas[1].RegistrarMargenCosteo(0.15m, 0.25m, Cotifly.Dominio.Margen.OrigenMargen.SapTm, "margen");
        cotizacion.RecalcularTotales(clsCotizacionMuestra.Ahora);
        var herramientas = Herramientas(cotizacion);

        var ruta1 = await herramientas.ProponerAjusteMargenAsync(0.20m, null, 1, Ct);
        var ruta2 = await herramientas.ProponerAjusteMargenAsync(0.20m, null, 2, Ct);

        ruta1.EsExitoso.Should().BeTrue();
        ruta2.Error!.Codigo.Should().Be(clsCodigosError.JustificacionRequerida);
        ruta2.Error.Datos.Should().BeNull("el detalle estructurado no va al modelo");
    }

    [Fact]
    public async Task ConUnaSolaRutaNoHaceFaltaIndicarla()
    {
        var herramientas = Herramientas(clsCotizacionMuestra.Costeada(folio: "PCN-1"));

        var resultado = await herramientas.ProponerAjusteMargenAsync(0.18m, null, null, Ct);

        resultado.EsExitoso.Should().BeTrue();
        _contexto.Propuesta!.Cuerpo["linea_orden"].Should().Be(1);
        _contexto.Propuesta.Resumen.Should().StartWith("Ajustar el margen de 15 % a 18 %").And.NotContain("ruta 1");
    }

    [Fact]
    public async Task UnaRutaQueNoExisteEsUnErrorSinPropuesta()
    {
        var herramientas = Herramientas(TresRutas());

        var resultado = await herramientas.ProponerAjusteMargenAsync(0.20m, null, 7, Ct);

        resultado.Error!.Codigo.Should().Be(clsCodigosError.RutaInexistente);
        _contexto.Propuesta.Should().BeNull();
    }

    [Fact]
    public async Task ConsultarLaCotizacionMuestraLasRutasSoloConPorcentajesOrigenYDestino()
    {
        var herramientas = Herramientas(TresRutas());

        var salida = (IReadOnlyDictionary<string, object?>)(await herramientas.ConsultarCotizacionAsync(Ct)).ParaModelo();

        salida["numero_rutas"].Should().Be(3);
        var rutas = (IReadOnlyList<IReadOnlyDictionary<string, object?>>)salida["rutas"]!;
        rutas.Should().HaveCount(3);
        rutas[1]["margen_actual"].Should().Be(0.15m);
        rutas[1]["margen_sugerido"].Should().Be(0.15m);
        rutas[1]["origen"].Should().Be("Buenaventura");
        rutas[1]["destino"].Should().Be("Destino 2");
        SinMontos(salida);
    }

    /// <summary>Ningún monto llega al modelo: ni precios, ni costos, ni valores de conexos (solo porcentajes, nombres y cantidades).</summary>
    private static void SinMontos(IReadOnlyDictionary<string, object?> salida)
    {
        var json = JsonSerializer.Serialize(salida);
        foreach (var monto in new[] { "850000", "1700000", "425000", "1000000", "2000000", "500000", "85000", "170000", "2200000", "3700000" })
        {
            json.Should().NotContain(monto, "la IA nunca recibe montos");
        }

        json.Should().NotContain("precio").And.NotContain("costo");
    }
}
