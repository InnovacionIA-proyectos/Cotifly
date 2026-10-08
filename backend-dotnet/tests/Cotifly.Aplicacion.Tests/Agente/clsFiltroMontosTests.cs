using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Dominio.Agente;
using Cotifly.Pruebas.Comun;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// HU-013.16 (#126): filtro de montos del asistente (<see cref="clsNumerosDelMensaje"/> y <see cref="clsReglasRespuestaAgente.Montos"/>),
/// del que depende la regla "la IA nunca escribe un monto que nada respalda" (ADR-018 §2b). Casos adversariales: separadores mixtos,
/// espacios de miles, dígitos de otros sistemas, montos en letras, sufijos de miles y millones, USD y TRM, porcentajes y códigos de
/// vehículo, y cifras del comercial frente a cifras inventadas. El contexto es el del asistente: la salida de
/// <c>consultar_cotizacion</c> (sin montos: versión 1, márgenes, cliente) y los mensajes del comercial.
/// </summary>
public sealed class clsFiltroMontosTests
{
    private static readonly string[] _declaradas = ["consultar_cotizacion", "proponer_ajuste_margen", "proponer_re_solicitud", "buscar_servicio_conexo"];

    /// <summary>Lo que ve el modelo en un turno típico: la consulta (versión 1, márgenes 15 %, cliente "3M Colombia", 3S3) y nada más.</summary>
    private static clsContextoValidacionRespuesta Asistente(params string[] mensajes) =>
        new(
            clsEstadoConversacion.Vacio with { FolioEnProgreso = "PCN-900555111-AP-05102026-001", Version = 1, Moneda = "COP" },
            [
                clsResultadoHerramienta.Exito("consultar_cotizacion", new Dictionary<string, object?>
                {
                    ["folio"] = "PCN-900555111-AP-05102026-001",
                    ["version"] = 1,
                    ["cliente"] = "3M Colombia",
                    ["tipo_vehiculo"] = "3S3",
                    ["moneda"] = "COP",
                    ["margen_actual"] = 0.15m,
                    ["margen_sugerido"] = 0.15m,
                    ["conexos"] = new[] { new Dictionary<string, object?> { ["nombre"] = "Cargue", ["cantidad"] = 2 } },
                }),
            ],
            mensajes.Length == 0 ? ["súbele el margen al 18 %"] : mensajes,
            _declaradas);

    private static IEnumerable<string> Codigos(string texto, clsContextoValidacionRespuesta contexto) =>
        clsReglasRespuestaAgente.Validar(clsChatClientGuionizado.JsonFinal(texto), contexto).Fallos.Select(f => f.Codigo);

    // ---------- clsNumerosDelMensaje: valores posibles de un número ----------

    [Theory]
    [InlineData("2.850.000", 2_850_000)]
    [InlineData("2,850,000", 2_850_000)]
    [InlineData("2850000", 2_850_000)]
    [InlineData("2'850.000", 2_850_000)]
    [InlineData("2’850.000", 2_850_000)]
    [InlineData("2 850 000", 2_850_000)]
    [InlineData("2 850 000", 2_850_000)]
    [InlineData("2 850 000", 2_850_000)]
    [InlineData("2 850 000", 2_850_000)]
    [InlineData("2.850.000,50", 2_850_000.50)]
    [InlineData("2,850,000.50", 2_850_000.50)]
    [InlineData("777,44", 777.44)]
    [InlineData("３.４００.０００", 3_400_000)]
    [InlineData("٣٤٠٠", 3400)]
    public void CadaFormatoDaSuValor(string numero, double valor) =>
        clsNumerosDelMensaje.CandidatosDe(numero).Should().Contain((decimal)valor);

    [Fact]
    public void UnSeparadorAmbiguoDaUnCandidatoPorLecturaSinRepetidos()
    {
        clsNumerosDelMensaje.CandidatosDe("2,85").Should().Equal(285m, 2.85m);
        clsNumerosDelMensaje.CandidatosDe("2.850").Should().Equal(2850m, 2.85m);
        clsNumerosDelMensaje.CandidatosDe("2850").Should().Equal(2850m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("99999999999999999999999999999999999")]
    public void LoQueNoEsUnNumeroLegibleNoDaCandidatos(string? numero) =>
        clsNumerosDelMensaje.CandidatosDe(numero).Should().BeEmpty();

    [Fact]
    public void SeparadoresMezcladosImposiblesDanLecturasQueNoRespaldanNada() =>
        clsNumerosDelMensaje.CandidatosDe("1.234,567.89").Should().Equal(123_456_789m, 1234.56789m);

    // ---------- Montos que el modelo NO puede escribir (nada los respalda) ----------

    [Theory]
    // Huecos reales encontrados en HU-013.16: el espacio de miles partía el número y "$1" quedaba respaldado por "version": 1.
    [InlineData("El nuevo precio es $1 109 756.")]
    [InlineData("El nuevo precio es $1 109 756.")]
    [InlineData("Queda en 1 109 756 pesos.")]
    // Dígitos de otros sistemas: antes no se podían leer y el monto se descartaba.
    [InlineData("El precio queda en $３.４００.０００.")]
    // Montos en letras.
    [InlineData("Te sale en tres millones de pesos.")]
    [InlineData("Son dos mil quinientos dólares por el viaje.")]
    [InlineData("El ajuste suma medio millón de pesos.")]
    [InlineData("Queda en un millón y medio.")]
    [InlineData("Cuesta trescientos mil pesos más.")]
    // Sufijos de miles y miles de millones.
    [InlineData("Te lo dejo en 3k.")]
    [InlineData("Son $3 mil más.")]
    [InlineData("El cliente factura 3 mil millones al año.")]
    // Palabras de dinero nuevas: monto, cuesta, vale, tasa, tipo de cambio.
    [InlineData("El monto final es 3400000.")]
    [InlineData("Cuesta 3400000 en total.")]
    [InlineData("Con una tasa de 4.100 queda así.")]
    [InlineData("Con el tipo de cambio en 4100 sube un poco.")]
    // USD, euros, TRM y signo menos.
    [InlineData("En dólares son US$777,44.")]
    [InlineData("En dólares son 777,44 USD.")]
    [InlineData("La TRM aplicada es 4.000.")]
    [InlineData("Son € 2.500 de diferencia.")]
    [InlineData("El ajuste es de -150.000 pesos.")]
    // Separadores clásicos sin moneda ni palabra clave.
    [InlineData("Quedaría en 1,234,567.")]
    [InlineData("Quedaría en 3.109.756.")]
    public void UnMontoInventadoSeRechaza(string texto) =>
        Codigos(texto, Asistente()).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);

    // ---------- Lo que NO es un monto ----------

    [Theory]
    [InlineData("Te propongo subir el margen al 18 % (hoy está en 15 %).")]
    [InlineData("Te propongo subir el margen al 18%.")]
    [InlineData("El margen pasa de 0,15 a 0,18.")]
    [InlineData("El vehículo es un 3S3 y el remolque un 2S2.")]
    [InlineData("Para un C3S3 o un 3S2 la propuesta es la misma.")]
    [InlineData("La carga pesa 34 toneladas y recorre 1 200 km.")]
    [InlineData("Son 12 500 kilos en 3 posiciones.")]
    [InlineData("Recorre 1 200 kilómetros en 2 días.")]
    [InlineData("Mil gracias por el dato; reviso la versión 1.")]
    [InlineData("La cotización del cliente 3M Colombia está costeada.")]
    [InlineData("El NIT 900 555 111 está registrado.")]
    [InlineData("Te llamo al 300 123 4567 si hace falta.")]
    [InlineData("La cotización se creó el 2026-10-05 y vence el 08/10/2026.")]
    [InlineData("Agrego 2 cargues a la cotización PCN-900555111-AP-05102026-001.")]
    public void LosPorcentajesCodigosUnidadesYFechasNoSonMontos(string texto) =>
        Codigos(texto, Asistente()).Should().BeEmpty();

    [Fact]
    public void ElNombre3MSoloPasaPorqueVieneDeLaHerramienta()
    {
        var sinConsulta = Asistente() with { Salidas = [] };

        Codigos("La cotización del cliente 3M Colombia está costeada.", sinConsulta)
            .Should().Equal([clsReglasRespuestaAgente.MontoNoRespaldado], "sin la salida de la herramienta, 3M es tres millones");
        Codigos("La cotización del cliente 13M está costeada.", Asistente())
            .Should().Equal([clsReglasRespuestaAgente.MontoNoRespaldado], "el fragmento debe aparecer como palabra completa");
    }

    // ---------- Cifras del comercial frente a cifras inventadas ----------

    [Fact]
    public void UnMontoQueEscribioElComercialPuedeRepetirseYUnoDistintoNo()
    {
        var contexto = Asistente("El cliente ofrece 3.400.000 pesos por el viaje");

        Codigos("Con los 3.400.000 pesos que ofrece el cliente, el margen queda en revisión.", contexto).Should().BeEmpty();
        Codigos("Con 3 400 000 pesos el margen queda en revisión.", contexto).Should().BeEmpty("el mismo valor con otro separador");
        Codigos("Con 3,4 millones de pesos el margen queda en revisión.", contexto).Should().BeEmpty("el mismo valor con sufijo");
        Codigos("Con 3.400.001 pesos el margen queda en revisión.", contexto).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);
        Codigos("Con 3.500.000 pesos el margen queda en revisión.", contexto).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);
    }

    [Fact]
    public void UnMontoEnLetrasDelComercialPuedeRepetirseTalCual()
    {
        var contexto = Asistente("el cliente dice que tiene tres millones de pesos de presupuesto");

        Codigos("Entendido: el cliente tiene tres millones de pesos de presupuesto.", contexto).Should().BeEmpty();
        Codigos("Entendido: el cliente tiene cuatro millones de pesos de presupuesto.", contexto).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);
    }

    [Fact]
    public void UnNumeroDelComercialQueNoEsDineroNoRespaldaUnMonto()
    {
        var contexto = Asistente("Son 12.500 kg y 3 posiciones");

        Codigos("El precio es $12.500.", contexto).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);
        Codigos("Son 12.500 pesos.", contexto).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado, "solo los textos de las herramientas respaldan tal cual");
    }

    [Fact]
    public void LaTrmYLosDolaresDelComercialRespaldanYLosInventadosNo()
    {
        var contexto = Asistente("La TRM de hoy está en 4.000 y el cliente paga USD 1.200");

        Codigos("Con la TRM de 4.000 y USD 1.200, te propongo el ajuste.", contexto).Should().BeEmpty();
        Codigos("Con la TRM de 4.100 te propongo el ajuste.", contexto).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);
        Codigos("Son USD 1.250.", contexto).Should().Contain(clsReglasRespuestaAgente.MontoNoRespaldado);
    }

    // ---------- Extracción ----------

    [Fact]
    public void LosMontosSeExtraenConSuFragmentoYSusValores()
    {
        var montos = clsReglasRespuestaAgente.Montos("Precio $1 109 756; 3 mil millones; 5k; tres millones de pesos; 2.5M.");

        montos.Select(m => m.Fragmento).Should().Equal("$1 109 756", "3 mil millones", "5k", "2.5M", "tres millones");
        montos[0].Candidatos.Should().Contain(1_109_756m);
        montos[1].Candidatos.Should().Contain(3_000_000_000m);
        montos[2].Candidatos.Should().Contain(5_000m);
        montos[3].Candidatos.Should().Contain(2_500_000m);
        montos[4].Candidatos.Should().BeEmpty("un monto en letras solo se respalda tal cual");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Sin cifras.")]
    public void UnTextoSinCifrasNoTieneMontos(string? texto) =>
        clsReglasRespuestaAgente.Montos(texto).Should().BeEmpty();
}
