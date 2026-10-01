using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Validators;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Tests.Licitaciones;

/// <summary>Reporte por fila de una licitación (HU-011.2, <c>docs/HU-011-formato-excel-licitacion.md</c> §5).</summary>
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
    public void ConArchivoLaHojaYLaFilaSonObligatoriasUnicasYDentroDeLaHoja()
    {
        var errores = clsValidadorLicitacion.Validar(Entrada([Linea(null), Linea(1), Linea(1), Linea(-1), Linea(9)], _archivo, hoja: null), filasEnHoja: 5);

        errores.Should().Contain(e => e.Campo == "hoja" && e.Codigo == clsCodigosLineaSolicitud.CampoRequerido);
        errores.Where(e => e.Codigo == clsCodigosLineaSolicitud.FilaInvalida).Select(e => e.Indice).Should().Equal(0, 2, 3, 4);
    }

    [Fact]
    public void SinArchivoLaFilaEsOpcional() =>
        clsValidadorLicitacion.Validar(Entrada([Linea(null), Linea(null)], hoja: null), filasEnHoja: null).Should().BeEmpty();

    [Fact]
    public void SinLineasOConMasDeMilSeRechazaLaLicitacion()
    {
        clsValidadorLicitacion.Validar(Entrada([]), null).Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.LineasRequeridas);
        clsValidadorLicitacion.Validar(Entrada([.. Enumerable.Range(0, 1001).Select(i => Linea(null))]), null)
            .Should().ContainSingle(e => e.Codigo == clsCodigosLineaSolicitud.DemasiadasLineas);
    }
}
