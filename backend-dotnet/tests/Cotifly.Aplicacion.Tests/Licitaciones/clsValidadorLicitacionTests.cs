using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Services;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>Reporte por fila de una licitación (<c>docs/conexion_nexo/HU-011-formato-excel-licitacion.md</c> §5).</summary>
public sealed class clsValidadorLicitacionTests
{
    private static readonly clsArchivoSubido _archivo = new("pliego.xlsx", [1]);

    private static clsLineaLicitacionEntrada Linea(int? fila, string destino = "BOGOTA, D.C.", decimal? peso = 9000m) =>
        new(fila, new clsLineaSolicitudCosteo { Origen = "YUMBO", Destino = destino, PesoKg = peso });

    private static clsEntradaLicitacion Entrada(IReadOnlyList<clsLineaLicitacionEntrada> lineas, clsArchivoSubido? archivo = null, string? hoja = "Pliego", string? nit = "900555111") =>
        new(nit, "Cliente", "Nacional", new clsEncabezadoSolicitudCosteo { Tipo = TipoSolicitud.Licitacion }, hoja, lineas, archivo);

    [Fact]
    public void UnaLicitacionValidaNoTieneErrores() =>
        clsValidadorLicitacion.Validar(Entrada([Linea(0), Linea(1)], _archivo), filasEnHoja: 2).Should().BeEmpty();

    [Fact]
    public void ReportaTodasLasFilasConErrorConIndiceFilaCampoYCodigo()
    {
        var errores = clsValidadorLicitacion.Validar(Entrada([Linea(0, destino: ""), Linea(1), Linea(2, peso: null)], _archivo), filasEnHoja: 3);

        errores.Select(e => (e.Indice, e.Fila, e.Campo, e.Codigo)).Should().BeEquivalentTo(new (int?, int?, string, string)[]
        {
            (0, 0, "destino", clsCodigosLineaSolicitud.LineaIncompleta),
            (2, 2, "peso_kg", clsCodigosLineaSolicitud.PesoRequerido),
        });
    }

    [Fact]
    public void ElEncabezadoSeReportaSinIndice()
    {
        var errores = clsValidadorLicitacion.Validar(Entrada([Linea(null)], nit: null) with { LineaNegocio = " " }, filasEnHoja: null);

        errores.Should().OnlyContain(e => e.Indice == null);
        errores.Select(e => e.Campo).Should().BeEquivalentTo(["nit_cliente", "linea_negocio"]);
    }

    [Fact]
    public void ConArchivoLaHojaEsObligatoriaYLaFilaUnicaYDentroDeLaHoja()
    {
        var errores = clsValidadorLicitacion.Validar(Entrada([Linea(null), Linea(1), Linea(1), Linea(-1), Linea(9)], _archivo, hoja: null), filasEnHoja: 5);

        errores.Should().Contain(e => e.Campo == "hoja" && e.Codigo == clsCodigosLineaSolicitud.CampoRequerido);
        // La línea a mano (fila null, índice 0) NO es error; sí la repetida, la negativa y la fuera de la hoja.
        errores.Where(e => e.Codigo == clsCodigosLineaSolicitud.FilaInvalida).Select(e => e.Indice).Should().Equal(2, 3, 4);
    }

    // Con archivo, una línea con fila null es una línea agregada a mano (válida). La unicidad ignora los null.
    [Fact]
    public void ConArchivoUnaLineaConFilaNullEsUnaLineaAMano() =>
        clsValidadorLicitacion.Validar(Entrada([Linea(0), Linea(null), Linea(1)], _archivo), filasEnHoja: 2).Should().BeEmpty();

    [Fact]
    public void ConArchivoUnaLicitacionSoloDeLineasAManoEsValida() =>
        clsValidadorLicitacion.Validar(Entrada([Linea(null), Linea(null)], _archivo), filasEnHoja: 2).Should().BeEmpty();

    [Fact]
    public void LosErroresDeUnaLineaAManoDicenLaLineaNConFilaYFilaExcelEnNull()
    {
        // Índice 1 es una línea a mano vacía (fila null, sin cuerpo): su error dice «La línea 2», con fila y fila_excel en null.
        clsLineaLicitacionEntrada vacia = new(null, null!);
        var errores = clsValidadorLicitacion.ConFilaExcel(
            clsValidadorLicitacion.Validar(Entrada([Linea(0), vacia], _archivo), filasEnHoja: 2, _filasExcel), _filasExcel);

        var error = errores.Should().ContainSingle(e => e.Indice == 1).Subject;
        error.Should().Match<clsErrorFilaLicitacion>(e => e.Fila == null && e.FilaExcel == null && e.Codigo == clsCodigosLineaSolicitud.LineaIncompleta);
        error.Mensaje.Should().Be("La línea 2 está vacía.");
    }

    // Los mensajes usan la numeración del Excel del comercial (o la línea, base 1), nunca el índice 'fila' (base 0).
    private static readonly int[] _filasExcel = [4, 5, 7];

    [Fact]
    public void UnaFilaFueraDeLaHojaSeExplicaConLaLineaYLasFilasDelExcel()
    {
        var errores = clsValidadorLicitacion.Validar(Entrada([Linea(0), Linea(1), Linea(2), Linea(3)], _archivo), filasEnHoja: 3, _filasExcel);

        var error = errores.Should().ContainSingle().Subject;
        error.Should().Match<clsErrorFilaLicitacion>(e => e.Indice == 3 && e.Fila == 3 && e.Codigo == clsCodigosLineaSolicitud.FilaInvalida);
        error.Mensaje.Should().Be("La línea 4 apunta a una fila que no está en la hoja 'Pliego': la hoja tiene 3 filas con datos (de la fila 4 a la 7 del Excel).");
    }

    [Fact]
    public void UnaFilaRepetidaSeNombraConSuFilaDelExcel()
    {
        var conExcel = clsValidadorLicitacion.Validar(Entrada([Linea(1), Linea(1)], _archivo), filasEnHoja: 3, _filasExcel);
        var sinExcel = clsValidadorLicitacion.Validar(Entrada([Linea(1), Linea(1)], hoja: null), filasEnHoja: null);

        conExcel.Should().ContainSingle().Which.Mensaje.Should().Be("La línea 2 repite la fila 5 del Excel, que ya está en otra línea.");
        sinExcel.Should().ContainSingle().Which.Mensaje.Should().Be("La línea 2 repite la 'fila' de otra línea.");
    }

    [Fact]
    public void LosMensajesDeFilaNombranLaLineaEnBaseUno()
    {
        var errores = clsValidadorLicitacion.Validar(Entrada([Linea(0), Linea(null), Linea(-1)], _archivo), filasEnHoja: 3, _filasExcel);

        // La línea 2 (fila null) es una línea a mano válida; solo la negativa de la línea 3 queda.
        errores.Select(e => e.Mensaje).Should().Equal("La línea 3 trae una 'fila' negativa.");
    }

    [Fact]
    public void CadaErrorLlevaSuFilaDelExcelSiExisteEnLaHoja()
    {
        clsErrorFilaLicitacion[] errores =
        [
            new(null, null, "nit_cliente", clsCodigosLineaSolicitud.CampoRequerido, "x"),
            new(0, 2, "peso_kg", clsCodigosLineaSolicitud.PesoRequerido, "x"),
            new(1, 9, "fila", clsCodigosLineaSolicitud.FilaInvalida, "x"),
            new(2, null, "destino", clsCodigosLineaSolicitud.LineaIncompleta, "x"),
        ];

        clsValidadorLicitacion.ConFilaExcel(errores, _filasExcel).Select(e => e.FilaExcel).Should().Equal(null, 7, null, null);
        clsValidadorLicitacion.ConFilaExcel(errores, null).Should().BeSameAs(errores, "sin archivo no hay fila de Excel");
    }

    // filas_excel se compara con el total real de la hoja (Nexo corta 'filas' en 1000), no con 'filas'.
    [Theory]
    [InlineData(3, 3, 3, true)]
    [InlineData(4, 3, 3, false)]
    [InlineData(1000, 1500, 1000, true)]
    [InlineData(999, 1500, 1000, false)]
    [InlineData(1000, 1000, 1000, true)]
    public void LasFilasDelExcelCuadranConElTotalDeLaHoja(int ubicadas, int totalNexo, int filasNexo, bool cuadra)
    {
        var hoja = new clsHojaExcel("Pliego", 1, [], [.. Enumerable.Range(0, filasNexo).Select(_ => (IReadOnlyList<string>)[])], totalNexo);

        clsLicitacionService.Cuadra(ubicadas, hoja).Should().Be(cuadra);
    }

    // ADR-014 §5: moneda opcional (COP por defecto), COP o USD sin distinguir mayúsculas; otro valor o vacía es error del encabezado.
    [Theory]
    [InlineData(null, true)]
    [InlineData("COP", true)]
    [InlineData("USD", true)]
    [InlineData(" usd ", true)]
    [InlineData("EUR", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void LaMonedaEsCopOUsd(string? moneda, bool valida)
    {
        var errores = clsValidadorLicitacion.Validar(Entrada([Linea(null)], hoja: null) with { Moneda = moneda }, filasEnHoja: null);

        if (valida)
        {
            errores.Should().BeEmpty();
        }
        else
        {
            errores.Should().ContainSingle().Which.Should().Match<clsErrorFilaLicitacion>(e =>
                e.Indice == null && e.Campo == "moneda" && e.Codigo == clsCodigosLineaSolicitud.ValorFueraDeRango);
        }
    }

    [Fact]
    public void SinArchivoLaFilaEsOpcional() =>
        clsValidadorLicitacion.Validar(Entrada([Linea(null), Linea(null)], hoja: null), filasEnHoja: null).Should().BeEmpty();

    // Con leer_estructura la 'fila' es el índice dentro de la tabla (entrada.Hoja = nombre de la tabla); es obligatoria y
    // única, NO hay tope por la hoja (filasEnHoja llega null porque no se relee) y los mensajes nombran «línea N de la tabla X».
    private static clsEntradaLicitacion LeerEstructura(IReadOnlyList<clsLineaLicitacionEntrada> lineas, clsArchivoSubido? archivo = null, string? tabla = "Rutas 2026") =>
        Entrada(lineas, archivo, hoja: tabla) with { OrigenLectura = clsOrigenLectura.LeerEstructura };

    [Fact]
    public void ConLeerEstructuraUnaLicitacionValidaNoTieneErroresConLaFilaHastaElTotalDeLaTabla()
    {
        // La 'fila' puede ser cualquier índice >= 0 (no se valida contra el total de la tabla, que el servidor no conoce): aquí 0 y 10.
        clsValidadorLicitacion.Validar(LeerEstructura([Linea(0), Linea(10)], _archivo), filasEnHoja: null).Should().BeEmpty();
    }

    [Fact]
    public void ConLeerEstructuraLaFilaEsUnicaYSinNegativasConMensajesQueNombranLaTabla()
    {
        var errores = clsValidadorLicitacion.Validar(LeerEstructura([Linea(null), Linea(1), Linea(1), Linea(-1)], _archivo), filasEnHoja: null);

        // También con leer_estructura la línea 1 (fila null) es una línea a mano válida; quedan la repetida y la negativa.
        errores.Where(e => e.Codigo == clsCodigosLineaSolicitud.FilaInvalida).Select(e => e.Indice).Should().Equal(2, 3);
        errores.Select(e => e.Mensaje).Should().Contain("La línea 3 de la tabla 'Rutas 2026' repite la 'fila' 1 de otra línea.")
            .And.Contain("La línea 4 de la tabla 'Rutas 2026' trae una 'fila' negativa.");
    }

    // Una línea a mano (fila null) también con leer_estructura; sus errores dicen «La línea N» (no «de la tabla»).
    [Fact]
    public void ConLeerEstructuraUnaLineaAManoVaciaDiceLaLineaNSinLaTabla()
    {
        clsLineaLicitacionEntrada vacia = new(null, null!);
        var errores = clsValidadorLicitacion.Validar(LeerEstructura([Linea(0), vacia], _archivo), filasEnHoja: null);

        errores.Should().ContainSingle(e => e.Indice == 1).Which.Mensaje.Should().Be("La línea 2 está vacía.");
    }

    [Fact]
    public void SinLineasOConMasDeMilSeRechazaLaLicitacion()
    {
        clsValidadorLicitacion.Validar(Entrada([]), null).Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.LineasRequeridas);
        clsValidadorLicitacion.Validar(Entrada([.. Enumerable.Range(0, 1001).Select(i => Linea(null))]), null)
            .Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.DemasiadasLineas);
    }
}
