using ClosedXML.Excel;

namespace Cotifly.Infraestructura.Tests.Licitaciones;

/// <summary>
/// Oráculo de las pruebas de paridad: copia literal de la regla de <c>ExcelSolicitudesAdapter.AnalizarAsync</c> de Nexo (repo
/// <c>NEXO</c>, <c>src/CabinaFletes.Infrastructure/Solicitudes/Adaptadores/ExcelSolicitudesAdapter.cs</c>), con la
/// misma versión de ClosedXML que Nexo. Si Nexo cambia su analizador, se actualiza esta copia y la prueba dice si Cotifly sigue igual.
/// </summary>
internal static class clsAnalizadorNexoReferencia
{
    private const int MaxFilasPorHoja = 1000;
    private const int FilasBusquedaEncabezado = 15;

    public sealed record clsHoja(string Nombre, int FilaEncabezado, IReadOnlyList<string> Encabezados, IReadOnlyList<IReadOnlyList<string>> Filas, int TotalFilas);

    public static IReadOnlyList<clsHoja> Analizar(byte[] archivo)
    {
        using var wb = new XLWorkbook(new MemoryStream(archivo));
        var hojas = new List<clsHoja>();
        foreach (var ws in wb.Worksheets)
        {
            var usado = ws.RangeUsed();
            if (usado is null)
            {
                continue;
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
                continue;
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

            var encabezados = new List<string>();
            for (var c = primeraCol; c <= ultimaCol; c++)
            {
                encabezados.Add(Celda(filaEncabezado, c));
            }

            var filas = new List<IReadOnlyList<string>>();
            var totalFilas = 0;
            for (var r = Math.Max(filaEncabezado, filaSubEncabezado) + 1; r <= ultimaFila; r++)
            {
                var fila = new List<string>(encabezados.Count);
                var vacia = true;
                for (var c = primeraCol; c <= ultimaCol; c++)
                {
                    var v = Celda(r, c);
                    if (v.Length > 0)
                    {
                        vacia = false;
                    }

                    fila.Add(v);
                }

                if (vacia)
                {
                    continue;
                }

                totalFilas++;
                if (filas.Count < MaxFilasPorHoja)
                {
                    filas.Add(fila);
                }
            }

            hojas.Add(new clsHoja(ws.Name, filaEncabezado, encabezados, filas, totalFilas));
        }

        return hojas;
    }

    private static bool EsNumerico(string s)
    {
        var limpio = s.Replace("$", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).TrimEnd('%');
        return limpio.Length > 0 && limpio.All(char.IsDigit);
    }
}
