using ClosedXML.Excel;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Licitaciones.Services;

/// <summary>
/// Lectura del Excel original con ClosedXML (MIT). La detección del encabezado y de las filas con datos es un PUERTO 1:1 de
/// <c>ExcelSolicitudesAdapter.AnalizarAsync</c> de Nexo (repo <c>NEXO</c>, <c>src/CabinaFletes.Infrastructure/Solicitudes/Adaptadores</c>):
/// así el índice <c>fila</c> que el frontend tomó de <c>hojas[h].filas</c> apunta a la misma fila del archivo. Si Nexo cambia su
/// regla, esta clase debe cambiar igual (lo cubre la prueba con el mismo pliego de ejemplo).
/// </summary>
public sealed class clsLectorExcelLicitacionClosedXml : IClsLectorExcelLicitacion
{
    /// <summary>Filas usadas en las que se busca el encabezado (las mismas 15 de Nexo).</summary>
    public const int FilasBusquedaEncabezado = 15;

    public clsResultado<IReadOnlyList<int>> FilasDeDatos(byte[] contenido, string hoja)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        ArgumentException.ThrowIfNullOrWhiteSpace(hoja);
        XLWorkbook libro;
        try
        {
            libro = new XLWorkbook(new MemoryStream(contenido, writable: false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new clsErrorDominio(clsCodigosError.ExcelInvalido, "No se pudo leer el archivo Excel.");
        }

        using (libro)
        {
            if (!libro.TryGetWorksheet(hoja, out var hojaExcel))
            {
                return new clsErrorDominio(clsCodigosError.ExcelSinDatos, $"El archivo no tiene la hoja '{hoja}'.");
            }

            var filas = Detectar(hojaExcel);
            return filas is null
                ? new clsErrorDominio(clsCodigosError.ExcelSinDatos, $"La hoja '{hoja}' no tiene una fila de encabezado con datos.")
                : clsResultado<IReadOnlyList<int>>.Exito(filas.Value.Datos);
        }
    }

    /// <summary>
    /// Regla de Nexo sobre una hoja: (fila del encabezado, última fila del encabezado, columnas usadas, filas con datos), o
    /// <c>null</c> si la hoja no tiene datos o ninguna de las 15 primeras filas tiene 3 valores distintos.
    /// </summary>
    internal static (int Encabezado, int FinEncabezado, int PrimeraColumna, int UltimaColumna, IReadOnlyList<int> Datos)? Detectar(IXLWorksheet ws)
    {
        var usado = ws.RangeUsed();
        if (usado is null)
        {
            return null;
        }

        var primeraFila = usado.FirstRow().RowNumber();
        var ultimaFila = usado.LastRow().RowNumber();
        var primeraCol = usado.FirstColumn().ColumnNumber();
        var ultimaCol = usado.LastColumn().ColumnNumber();

        var combinadas = new Dictionary<(int Fila, int Col), string>();
        foreach (var rango in ws.MergedRanges)
        {
            var valor = rango.FirstCell().GetFormattedString().Trim();
            if (valor.Length == 0)
            {
                continue;
            }

            var dir = rango.RangeAddress;
            for (var r = dir.FirstAddress.RowNumber; r <= dir.LastAddress.RowNumber; r++)
            {
                for (var c = dir.FirstAddress.ColumnNumber; c <= dir.LastAddress.ColumnNumber; c++)
                {
                    combinadas[(r, c)] = valor;
                }
            }
        }

        string Celda(int r, int c) => combinadas.TryGetValue((r, c), out var v) ? v : ws.Cell(r, c).GetFormattedString().Trim();

        var filaEncabezado = 0;
        for (var r = primeraFila; r <= Math.Min(ultimaFila, primeraFila + FilasBusquedaEncabezado); r++)
        {
            var distintos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var c = primeraCol; c <= ultimaCol; c++)
            {
                var v = Celda(r, c);
                if (v.Length > 0)
                {
                    distintos.Add(v);
                }
            }

            if (distintos.Count >= 3)
            {
                filaEncabezado = r;
                break;
            }
        }

        if (filaEncabezado == 0)
        {
            return null;
        }

        var filaSubEncabezado = 0;
        if (filaEncabezado < ultimaFila)
        {
            var rotulos = new List<string>();
            for (var c = primeraCol; c <= ultimaCol; c++)
            {
                var v = Celda(filaEncabezado, c);
                if (v.Length > 0)
                {
                    rotulos.Add(v);
                }
            }

            if (rotulos.Count != rotulos.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            {
                var sub = new List<string>();
                for (var c = primeraCol; c <= ultimaCol; c++)
                {
                    var v = Celda(filaEncabezado + 1, c);
                    if (v.Length > 0)
                    {
                        sub.Add(v);
                    }
                }

                if (sub.Count >= 2 && sub.All(v => !EsNumerico(v)))
                {
                    filaSubEncabezado = filaEncabezado + 1;
                }
            }
        }

        var datos = new List<int>();
        var fin = Math.Max(filaEncabezado, filaSubEncabezado);
        for (var r = fin + 1; r <= ultimaFila; r++)
        {
            var vacia = true;
            for (var c = primeraCol; c <= ultimaCol && vacia; c++)
            {
                vacia = Celda(r, c).Length == 0;
            }

            if (!vacia)
            {
                datos.Add(r);
            }
        }

        return (filaEncabezado, fin, primeraCol, ultimaCol, datos);
    }

    private static bool EsNumerico(string s)
    {
        var limpio = s.Replace("$", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('%');
        return limpio.Length > 0 && limpio.All(char.IsDigit);
    }
}
