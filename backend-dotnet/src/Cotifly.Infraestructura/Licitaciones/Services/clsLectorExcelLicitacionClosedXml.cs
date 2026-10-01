using ClosedXML.Excel;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Licitaciones.Services;

/// <summary>
/// Lectura del Excel original con ClosedXML (MIT). La detección del encabezado y de las filas con datos es un PUERTO de
/// <c>ExcelSolicitudesAdapter.AnalizarAsync</c> de Nexo (repo <c>NEXO</c>, <c>src/CabinaFletes.Infrastructure/Solicitudes/Adaptadores</c>):
/// así el índice <c>fila</c> que el frontend tomó de <c>hojas[h].filas</c> apunta a la misma fila del archivo. Si Nexo cambia su
/// regla, esta clase debe cambiar igual (lo cubre la prueba de paridad con el pliego de ejemplo).
/// </summary>
/// <remarks>
/// <para><b>Acotado contra archivos maliciosos</b> (a diferencia de Nexo, que recorre todo el rango usado): una celda lejana
/// (<c>XFD3000</c>) o una combinada enorme (<c>A2:XFD1048576</c>) harían recorrer o expandir millones de celdas. Aquí:</para>
/// <list type="bullet">
/// <item>solo se leen las celdas con contenido (no se crean celdas vacías) y solo las primeras <see cref="MaximoColumnas"/> columnas;</item>
/// <item>las combinadas no se expanden: se consultan como rangos, recortados a ese rectángulo, y más de <see cref="MaximoCombinadas"/>
/// es <c>EXCEL_INVALIDO</c>;</item>
/// <item>se detiene al encontrar <see cref="MaximoFilasDatos"/> filas con datos (las mismas 1000 que devuelve el análisis de Nexo);</item>
/// <item>la cancelación del llamador se respeta.</item>
/// </list>
/// <para>En un pliego normal (menos de 200 columnas) el resultado es idéntico al de Nexo.</para>
/// </remarks>
public sealed class clsLectorExcelLicitacionClosedXml : IClsLectorExcelLicitacion
{
    /// <summary>Filas usadas en las que se busca el encabezado (las mismas 15 de Nexo).</summary>
    public const int FilasBusquedaEncabezado = 15;

    /// <summary>Columnas que se leen desde la primera usada. Un pliego real tiene decenas.</summary>
    public const int MaximoColumnas = 200;

    /// <summary>Filas con datos que se ubican (las 1000 de <c>hojas[h].filas</c> del análisis de Nexo).</summary>
    public const int MaximoFilasDatos = 1000;

    /// <summary>Rangos combinados por hoja que se aceptan.</summary>
    public const int MaximoCombinadas = 10_000;

    /// <summary>Última columna de Excel (<c>XFD</c>).</summary>
    public const int UltimaColumnaExcel = 16_384;

    public clsResultado<IReadOnlyList<int>> FilasDeDatos(byte[] contenido, string hoja, CancellationToken cancellationToken = default)
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

            var detectado = Detectar(hojaExcel, out var error, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            return detectado is not null
                ? clsResultado<IReadOnlyList<int>>.Exito(detectado.Datos)
                : new clsErrorDominio(clsCodigosError.ExcelSinDatos, $"La hoja '{hoja}' no tiene una fila de encabezado con datos.");
        }
    }

    /// <summary>
    /// Regla de Nexo sobre una hoja, acotada: la fila del encabezado, la última fila del encabezado (subencabezado), la primera
    /// columna usada, la última columna leída, la última columna usada de verdad (para escribir a la derecha al exportar) y las
    /// filas con datos (hasta <see cref="MaximoFilasDatos"/>). <c>null</c> si la hoja no tiene datos o ninguna de las 15 primeras
    /// filas tiene 3 valores distintos; <c>EXCEL_INVALIDO</c> con demasiadas combinadas.
    /// </summary>
    internal static clsHojaDetectada? Detectar(IXLWorksheet ws, out clsErrorDominio? error, CancellationToken cancellationToken = default)
    {
        error = null;
        var usado = ws.RangeUsed();
        if (usado is null)
        {
            return null;
        }

        var primeraFila = usado.FirstRow().RowNumber();
        var ultimaFila = usado.LastRow().RowNumber();
        var primeraCol = usado.FirstColumn().ColumnNumber();
        var ultimaColReal = usado.LastColumn().ColumnNumber();
        var ultimaCol = Math.Min(ultimaColReal, primeraCol + MaximoColumnas - 1);

        // Solo celdas con contenido dentro de las columnas leídas: no se crean ni recorren celdas vacías.
        var valores = new Dictionary<(int Fila, int Col), string>();
        var filasConValor = new SortedSet<int>();
        foreach (var celda in ws.CellsUsed(XLCellsUsedOptions.Contents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var c = celda.Address.ColumnNumber;
            if (c < primeraCol || c > ultimaCol)
            {
                continue;
            }

            var texto = celda.GetFormattedString().Trim();
            if (texto.Length > 0)
            {
                valores[(celda.Address.RowNumber, c)] = texto;
                filasConValor.Add(celda.Address.RowNumber);
            }
        }

        // Combinadas como rangos (no expandidas), recortadas a las columnas leídas.
        var combinadas = new List<(int Fila1, int Fila2, int Col1, int Col2, string Valor)>();
        foreach (var rango in ws.MergedRanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (combinadas.Count >= MaximoCombinadas)
            {
                error = new clsErrorDominio(clsCodigosError.ExcelInvalido, $"La hoja '{ws.Name}' tiene más de {MaximoCombinadas} rangos combinados.");
                return null;
            }

            var dir = rango.RangeAddress;
            var col1 = Math.Max(dir.FirstAddress.ColumnNumber, primeraCol);
            var col2 = Math.Min(dir.LastAddress.ColumnNumber, ultimaCol);
            if (col1 > col2)
            {
                continue;
            }

            var primera = rango.FirstCell();
            var valor = valores.TryGetValue((primera.Address.RowNumber, primera.Address.ColumnNumber), out var v)
                ? v
                : primera.GetFormattedString().Trim();
            if (valor.Length > 0)
            {
                combinadas.Add((dir.FirstAddress.RowNumber, dir.LastAddress.RowNumber, col1, col2, valor));
            }
        }

        string Celda(int r, int c)
        {
            foreach (var m in combinadas)
            {
                if (r >= m.Fila1 && r <= m.Fila2 && c >= m.Col1 && c <= m.Col2)
                {
                    return m.Valor;
                }
            }

            return valores.TryGetValue((r, c), out var texto) ? texto : string.Empty;
        }

        bool FilaVacia(int r)
        {
            if (filasConValor.Contains(r))
            {
                return false;
            }

            foreach (var m in combinadas)
            {
                if (r >= m.Fila1 && r <= m.Fila2)
                {
                    return false;
                }
            }

            return true;
        }

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

        // Filas con datos: las que tienen algún valor (o una combinada con valor) después del encabezado, hasta 1000. Los saltos
        // entre filas con valor se recorren por el índice ordenado, no celda por celda.
        var fin = Math.Max(filaEncabezado, filaSubEncabezado);
        var datos = new List<int>();
        var r2 = fin + 1;
        while (r2 <= ultimaFila && datos.Count < MaximoFilasDatos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FilaVacia(r2))
            {
                datos.Add(r2);
                r2++;
                continue;
            }

            // Salta a la siguiente fila con valor o al inicio de la siguiente combinada que la cubra.
            var siguienteValor = filasConValor.GetViewBetween(r2, int.MaxValue).Min;
            var siguienteCombinada = combinadas.Where(m => m.Fila2 >= r2).Select(m => Math.Max(m.Fila1, r2)).DefaultIfEmpty(int.MaxValue).Min();
            var siguiente = Math.Min(siguienteValor == 0 ? int.MaxValue : siguienteValor, siguienteCombinada);
            if (siguiente == int.MaxValue)
            {
                break;
            }

            r2 = siguiente;
        }

        return new clsHojaDetectada(filaEncabezado, fin, primeraCol, ultimaCol, ultimaColReal, datos);
    }

    private static bool EsNumerico(string s)
    {
        var limpio = s.Replace("$", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('%');
        return limpio.Length > 0 && limpio.All(char.IsDigit);
    }
}

/// <summary>Hoja detectada con la regla de Nexo: filas del encabezado, columnas y filas con datos (números de fila de Excel, base 1).</summary>
internal sealed record clsHojaDetectada(
    int Encabezado, int FinEncabezado, int PrimeraColumna, int UltimaColumna, int UltimaColumnaReal, IReadOnlyList<int> Datos);
