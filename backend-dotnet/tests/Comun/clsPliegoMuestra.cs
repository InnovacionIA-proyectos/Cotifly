using ClosedXML.Excel;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Pliego de licitación de prueba (HU-011): un Excel con plantilla "de cliente" (portada combinada en la fila 1, encabezado en la fila
/// 3, una fila vacía en medio de los datos y una columna que no se mapea) y el JSON de las líneas ya mapeadas que mandaría el frontend.
/// </summary>
public static class clsPliegoMuestra
{
    public const string Hoja = "Pliego 2026";

    private static readonly (string Origen, string Destino, string Vehiculo, decimal Toneladas)[] _rutas =
    [
        ("BUENAVENTURA", "BOGOTA, D.C.", "3S3", 32m), ("CALI", "CARTAGENA", "3S3", 28m), ("CUCUTA", "BOGOTA, D.C.", "2", 8m),
        ("MEDELLIN", "BUENAVENTURA", "3S3", 30m), ("BOGOTA, D.C.", "VILLAVICENCIO", "2S3", 18m), ("PEREIRA", "BUENAVENTURA", "3S3", 19.5m),
        ("MANIZALES", "MEDELLIN", "2", 9m), ("BOGOTA, D.C.", "SANTA MARTA", "3S3", 31m), ("IBAGUE", "BUENAVENTURA", "3S3", 25m),
        ("NEIVA", "BOGOTA, D.C.", "2S3", 15m), ("BARRANQUILLA", "BUENAVENTURA", "3S3", 33m), ("BUCARAMANGA", "BOGOTA, D.C.", "2S3", 17m),
    ];

    /// <summary>Ruta <paramref name="i"/> (base 0) del pliego, cíclica.</summary>
    public static (string Origen, string Destino, string Vehiculo, decimal Toneladas) Ruta(int i) => _rutas[i % _rutas.Length];

    /// <summary>Número de fila de Excel (base 1) del dato <paramref name="i"/>: los datos empiezan en la 4 y la 6 está vacía.</summary>
    public static int FilaExcel(int i) => i < 2 ? 4 + i : 5 + i;

    /// <summary>Excel con <paramref name="filas"/> filas de datos.</summary>
    public static byte[] Excel(int filas = 3)
    {
        using var libro = new XLWorkbook();
        var ws = libro.Worksheets.Add(Hoja);
        ws.Cell(1, 1).Value = "LICITACIÓN TRANSPORTE 2026 - AGENCIA DE ADUANAS JF";
        ws.Range(1, 1, 1, 6).Merge();
        string[] encabezados = ["ITEM", "CIUDAD ORIGEN", "CIUDAD DESTINO", "TIPO VEHÍCULO", "TON", "OBSERVACIONES"];
        for (var c = 0; c < encabezados.Length; c++)
        {
            ws.Cell(3, c + 1).Value = encabezados[c];
        }

        for (var i = 0; i < filas; i++)
        {
            var (origen, destino, vehiculo, toneladas) = Ruta(i);
            var fila = FilaExcel(i);
            ws.Cell(fila, 1).Value = i + 1;
            ws.Cell(fila, 2).Value = origen;
            ws.Cell(fila, 3).Value = destino;
            ws.Cell(fila, 4).Value = vehiculo == "2" ? "Sencillo" : "Tractomula";
            ws.Cell(fila, 5).Value = toneladas;
            ws.Cell(fila, 6).Value = i == 0 ? "Carga seca" : string.Empty;
        }

        ws.Worksheet.Workbook.Worksheets.Add("Instrucciones").Cell(1, 1).Value = "Diligencie la hoja Pliego 2026";
        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }

    /// <summary>
    /// Líneas mapeadas (objetos anónimos snake_case) de las <paramref name="filas"/> filas del pliego. HU-021.6 (decisión 7): la columna
    /// del tipo de vehículo del Excel ya no se mapea (el vehículo lo define el cubicador o la Mesa de Costeo de Nexo).
    /// </summary>
    public static object[] Lineas(int filas = 3) =>
        [.. Enumerable.Range(0, filas).Select(i =>
        {
            var (origen, destino, _, toneladas) = Ruta(i);
            return (object)new { fila = i, origen, destino, tipologia = "CargaSeca", peso_kg = toneladas * 1000m, valor_mercancia = 50_000_000m };
        })];

    /// <summary>JSON de <c>POST /web/licitaciones</c> con las líneas dadas.</summary>
    public static string Json(string nit, object[] lineas, string? hoja = Hoja, string? moneda = null) => moneda is null
        ? System.Text.Json.JsonSerializer.Serialize(new
        {
            nit_cliente = nit,
            cliente_nombre = "Agencia de Aduanas JF",
            linea_negocio = "Nacional",
            deadline = "2026-10-20T17:00:00-05:00",
            referencia = "Pliego 2026-04",
            hoja,
            lineas,
        })
        : System.Text.Json.JsonSerializer.Serialize(new
        {
            nit_cliente = nit,
            cliente_nombre = "Agencia de Aduanas JF",
            linea_negocio = "Nacional",
            deadline = "2026-10-20T17:00:00-05:00",
            referencia = "Pliego 2026-04",
            hoja,
            moneda,
            lineas,
        });
}
