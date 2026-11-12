using System.Globalization;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Aplicacion.Licitaciones.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;

namespace Cotifly.Infraestructura.Licitaciones.Services;

/// <summary>
/// Exportación del Excel de una licitación con ClosedXML (HU-011.3, ADR-017 §5).
/// <list type="bullet">
/// <item><b>Con el original:</b> abre el archivo del cliente, ubica la hoja mapeada y sus filas con la regla de Nexo
/// (<see cref="clsLectorExcelLicitacionClosedXml.Detectar"/>) y agrega cuatro columnas a la derecha de la última usada: precio final
/// (en la moneda de la cotización), estado del costeo, servicios conexos y (HU-023.5, #160) los camiones de la ruta. No modifica ninguna
/// celda existente. Las filas del pliego
/// que no se enviaron quedan "Sin enviar".</item>
/// <item><b>Sin original:</b> una hoja <c>Licitación</c> con los campos mapeados y las mismas columnas.</item>
/// <item>Siempre una hoja <see cref="HojaResumen"/> con el folio, el cliente, la moneda (y la TRM en USD), el total y los conexos, y una
/// hoja <see cref="HojaCamiones"/> con un camión por fila (HU-023.5).</item>
/// </list>
/// Las líneas sin costeo llevan <see cref="SinCosteo"/> en la columna del precio, con fondo amarillo. Nunca escribe costos de Nexo (no
/// los recibe). Siempre guarda <c>.xlsx</c>: ClosedXML no conserva las macros de un <c>.xlsm</c>.
/// <para><b>Original guardado</b> (HU-035.2, #213): antes de abrirlo con ClosedXML pasa por los mismos controles del Excel subido
/// (<see cref="clsLecturaExcelAcotada"/>: firma, 1000 entradas, 64 MB descomprimidos, 1.000.000 de elementos XML sin DTD) y por el cupo
/// de lecturas de Excel del proceso. Un original guardado antes de esos controles que no los pasa responde <c>EXCEL_INVALIDO</c> o
/// <c>ARCHIVO_DEMASIADO_GRANDE</c> con <c>detalle.motivo</c> (400, como un original ilegible), y el cupo lleno <c>SERVICIO_OCUPADO</c>
/// (503); nunca un 500 ni una lectura sin tope. Sin original no se abre nada del cliente y no se toma el cupo.</para>
/// </summary>
public sealed class clsExportadorExcelLicitacionClosedXml(clsLecturaExcelAcotada lectura) : IClsExportadorExcelLicitacion
{
    private const string Documento = "licitación (exportación)";

    private readonly clsLecturaExcelAcotada _lectura = lectura ?? throw new ArgumentNullException(nameof(lectura));

    public const string HojaResumen = "Resumen Cotifly";
    public const string HojaSinOriginal = "Licitación";
    public const string SinCosteo = "SIN COSTEO";

    /// <summary>HU-044.10 (#293, parte 2): hoja nueva con las líneas agregadas a mano cuando se exporta sobre el Excel original (analizar_excel).</summary>
    public const string HojaAgregadas = "Agregadas en Cotifly";

    /// <summary>HU-044.10 (#293, parte 2): columna que marca una línea agregada a mano (la 10 de la hoja «Licitación», o en la hoja «Agregadas en Cotifly»).</summary>
    public const string ColumnaReferencia = "Referencia";

    /// <summary>HU-044.10 (#293, parte 2): valor de la columna <see cref="ColumnaReferencia"/> en una línea agregada a mano.</summary>
    public const string ReferenciaAgregada = "Agregada en Cotifly";

    /// <summary>HU-044.10 (#293, parte 2): etiqueta del conteo de líneas agregadas a mano en «Resumen Cotifly».</summary>
    public const string EtiquetaAgregadas = "Líneas agregadas en Cotifly";

    /// <summary>HU-028.1 (#183): etiqueta del conteo de líneas rechazadas por la Mesa de Costeo en "Resumen Cotifly".</summary>
    public const string EtiquetaRechazadas = "Líneas rechazadas en NEXO (sin costeo)";

    /// <summary>HU-028.1 (#183): total de "Resumen Cotifly" cuando la Mesa de Costeo rechazó todas las líneas.</summary>
    public const string TodasRechazadas = "Sin costeo: todas las líneas fueron rechazadas en NEXO";

    /// <summary>HU-028.1 (#183): columna de la hoja "Camiones Cotifly" que marca el camión rechazado por la Mesa de Costeo.</summary>
    public const string ColumnaCosteoCamion = "Costeo";
    public const string SinEnviar = "Sin enviar";
    public const string ColumnaEstado = "Estado costeo Cotifly";
    public const string ColumnaConexos = "Servicios conexos Cotifly";

    /// <summary>HU-023.5 (#160): columna con los camiones de cada ruta (uno por línea de la celda).</summary>
    public const string ColumnaCamiones = "Camiones Cotifly";

    /// <summary>HU-023.5 (#160): hoja con un camión por fila (vehículo, carrocería, peso, volumen y llenado).</summary>
    public const string HojaCamiones = "Camiones Cotifly";

    /// <summary>Columnas que agrega Cotifly a la derecha del pliego: precio, estado, conexos y camiones.</summary>
    public const int ColumnasCotifly = 4;
    public const string ConexosDeLaLicitacion = "Aplican a toda la licitación (ver hoja Resumen Cotifly)";

    /// <summary>El total es la suma de los precios ya redondeados de las líneas, sin servicios conexos (ADR-017 §3).</summary>
    public const string EtiquetaTotal = "Total de la licitación (sin servicios conexos)";

    /// <summary>
    /// HU-044.7 (#290): nota del resumen cuando la licitación se leyó con la IA de NEXO (<c>leer_estructura</c>): los precios van en la hoja
    /// «Licitación» nueva, no sobre el archivo original del cliente.
    /// </summary>
    public const string NotaLeidaConIa =
        "Esta licitación se leyó con la IA de NEXO (lectura de estructura). Los precios se exportan en la hoja «" + HojaSinOriginal
        + "»: NO se escriben sobre el archivo original del cliente.";

    private static readonly XLColor _pendiente = XLColor.FromHtml("#FFF2CC");
    private static readonly XLColor _encabezado = XLColor.FromHtml("#E8EEF7");

    /// <summary>Encabezado de la columna del precio, con la moneda (ej. <c>Precio final Cotifly (COP)</c>).</summary>
    public static string ColumnaPrecio(string moneda) => $"Precio final Cotifly ({moneda})";

    /// <summary>Formato numérico de la moneda (ADR-014): pesos sin decimales, dólares con dos.</summary>
    public static string Formato(string moneda) => string.Equals(moneda, "COP", StringComparison.OrdinalIgnoreCase) ? "#,##0" : "#,##0.00";

    /// <summary>
    /// Con original, dentro del cupo compartido y tras los controles del paquete (<see cref="clsLecturaExcelAcotada.LeerAsync{T}"/>);
    /// sin original, directamente (no hay archivo del cliente que abrir).
    /// </summary>
    public Task<clsResultado<byte[]>> GenerarAsync(clsExportacionLicitacion datos, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(datos);
        return datos.Original is { Length: > 0 } original
            ? _lectura.LeerAsync(original, Documento, _ => GenerarVerificado(datos), cancellationToken)
            : Task.FromResult(GenerarVerificado(datos));
    }

    /// <summary>Sin el cupo (sincrónica, pruebas): los controles del original (si lo hay) y después la generación.</summary>
    internal static clsResultado<byte[]> Generar(clsExportacionLicitacion datos)
    {
        ArgumentNullException.ThrowIfNull(datos);
        return datos.Original is { Length: > 0 } original && clsLecturaExcelAcotada.Verificar(original) is { } rechazo
            ? rechazo
            : GenerarVerificado(datos);
    }

    private static clsResultado<byte[]> GenerarVerificado(clsExportacionLicitacion datos)
    {
        XLWorkbook libro;
        try
        {
            libro = datos.Original is { Length: > 0 } original ? new XLWorkbook(new MemoryStream(original, writable: false)) : new XLWorkbook();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return clsLecturaExcelAcotada.Invalido(clsLecturaExcelAcotada.MotivoLibroIlegible);
        }

        using (libro)
        {
            if (datos.Original is not { Length: > 0 })
            {
                return Escribir(libro, datos, original: false);
            }

            // Un original que ClosedXML abrió pero no puede recorrer o volver a guardar es un problema del archivo del cliente, no del
            // servidor: EXCEL_INVALIDO (LIBRO_ILEGIBLE), como en la lectura (HU-035.2, #213).
            try
            {
                return Escribir(libro, datos, original: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                return clsLecturaExcelAcotada.Invalido(clsLecturaExcelAcotada.MotivoLibroIlegible);
            }
        }
    }

    private static clsResultado<byte[]> Escribir(XLWorkbook libro, clsExportacionLicitacion datos, bool original)
    {
        // HU-044.10 (#293, parte 2): una línea agregada a mano es una línea con fila null, pero SOLO cuando hay archivo (con analizar_excel
        // y original, o con leer_estructura). Sin archivo, todas las líneas tienen fila null y NO son «agregadas a mano».
        var conArchivo = datos.Original is { Length: > 0 } || datos.LeidaConIa;
        var agregadas = conArchivo ? datos.Lineas.Where(l => l.Fila is null).ToList() : [];

        List<clsLineaExportacion> sinUbicar = [];
        var escrito = original
            ? EscribirEnOriginal(libro, datos, sinUbicar)
            : EscribirSinOriginal(libro, datos, marcarAgregadas: agregadas.Count > 0);
        if (escrito is not null)
        {
            return escrito;
        }

        // Con analizar_excel y original, las de a mano no caben sobre el pliego: van a una hoja nueva «Agregadas en Cotifly».
        if (original && agregadas.Count > 0)
        {
            EscribirAgregadas(libro, datos, agregadas);
        }

        EscribirResumen(libro, datos, sinUbicar, agregadas.Count);
        EscribirCamiones(libro, datos);
        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return ComoXlsx(memoria);
    }

    /// <summary>
    /// Deja el paquete como un <c>.xlsx</c> de verdad: si el original era <c>.xlsm</c>, ClosedXML conserva el tipo de libro con
    /// macros y el <c>vbaProject.bin</c>, y Excel no abre un archivo así con extensión <c>.xlsx</c>. Se cambia el tipo de documento y
    /// se quita la parte VBA (las macros del cliente no se exportan).
    /// </summary>
    private static byte[] ComoXlsx(MemoryStream memoria)
    {
        using (var documento = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(memoria, true))
        {
            if (documento.DocumentType != DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook)
            {
                documento.ChangeDocumentType(DocumentFormat.OpenXml.SpreadsheetDocumentType.Workbook);
            }

            if (documento.WorkbookPart?.VbaProjectPart is { } vba)
            {
                documento.WorkbookPart.DeletePart(vba);
            }
        }

        return memoria.ToArray();
    }

    private static clsErrorDominio? EscribirEnOriginal(XLWorkbook libro, clsExportacionLicitacion datos, List<clsLineaExportacion> sinUbicar)
    {
        IXLWorksheet? hoja = null;
        clsHojaDetectada? detectado = null;
        if (datos.Hoja is not null && libro.TryGetWorksheet(datos.Hoja, out var encontrada))
        {
            hoja = encontrada;
            detectado = clsLectorExcelLicitacionClosedXml.Detectar(encontrada, out _);
        }

        if (detectado is null || hoja is null)
        {
            return new clsErrorDominio(clsCodigosError.ExcelSinDatos, $"El Excel original ya no tiene la hoja '{datos.Hoja}' con datos.");
        }

        // A la derecha de la última columna usada de VERDAD (el lector solo lee 200): nunca se pisa una celda del cliente.
        var columna = detectado.UltimaColumnaReal + 1;
        if (columna + ColumnasCotifly - 1 > clsLectorExcelLicitacionClosedXml.UltimaColumnaExcel)
        {
            return new clsErrorDominio(clsCodigosError.ExcelInvalido, $"La hoja '{datos.Hoja}' no tiene columnas libres a la derecha para los precios.");
        }
        Encabezados(hoja, detectado.Encabezado, columna, datos.Moneda);
        if (detectado.FinEncabezado > detectado.Encabezado)
        {
            // Subencabezado: las columnas nuevas ocupan las dos filas del encabezado, como las del cliente.
            for (var c = columna; c < columna + ColumnasCotifly; c++)
            {
                hoja.Range(detectado.Encabezado, c, detectado.FinEncabezado, c).Merge();
            }
        }

        var porFila = datos.Lineas.Where(l => l.Fila is { } f && f < detectado.Datos.Count).ToDictionary(l => l.Fila!.Value);
        // HU-044.10 (#293, parte 2): una fila que ya no está en el pliego sigue en «sin ubicar»; una línea a mano (fila null) NO, va a la
        // hoja «Agregadas en Cotifly».
        sinUbicar.AddRange(datos.Lineas.Where(l => l.Fila is { } f && f >= detectado.Datos.Count));

        // "Sin enviar" solo entre la primera y la última fila enviada: una fila de totales o de notas del cliente al final (o al
        // principio) no es una ruta que el comercial dejó por fuera.
        var primera = porFila.Count == 0 ? 0 : porFila.Keys.Min();
        var ultima = porFila.Count == 0 ? -1 : porFila.Keys.Max();
        for (var i = primera; i <= ultima; i++)
        {
            var fila = detectado.Datos[i];
            if (porFila.TryGetValue(i, out var linea))
            {
                EscribirLinea(hoja, fila, columna, linea, datos);
            }
            else
            {
                hoja.Cell(fila, columna + 1).Value = SinEnviar;
            }
        }

        return null;
    }

    private static clsErrorDominio? EscribirSinOriginal(XLWorkbook libro, clsExportacionLicitacion datos, bool marcarAgregadas)
    {
        var hoja = libro.Worksheets.Add(HojaSinOriginal);
        // HU-044.10 (#293, parte 2): con leer_estructura (o sin original), las líneas a mano van al final de la hoja «Licitación», con la
        // columna «Referencia» (la 10, solo si hay alguna a mano).
        var lineas = marcarAgregadas ? datos.Lineas.OrderBy(l => l.Fila is null ? 1 : 0).ToList() : (IReadOnlyList<clsLineaExportacion>)datos.Lineas;
        EscribirFilasComoLicitacion(hoja, datos, lineas, marcarAgregadas);
        return null;
    }

    /// <summary>
    /// HU-044.10 (#293, parte 2): la hoja nueva «Agregadas en Cotifly» con las líneas agregadas a mano cuando se exportó sobre el Excel
    /// original (analizar_excel): las columnas de la hoja «Licitación» más la columna «Referencia» = «Agregada en Cotifly».
    /// </summary>
    private static void EscribirAgregadas(XLWorkbook libro, clsExportacionLicitacion datos, IReadOnlyList<clsLineaExportacion> agregadas)
    {
        var nombre = HojaAgregadas;
        for (var n = 2; libro.Worksheets.Contains(nombre); n++)
        {
            nombre = $"{HojaAgregadas} {n}";
        }

        EscribirFilasComoLicitacion(libro.Worksheets.Add(nombre), datos, agregadas, conReferencia: true);
    }

    /// <summary>Las filas con las columnas de la hoja «Licitación» (campos mapeados + columnas de Cotifly) y, si <paramref name="conReferencia"/>, la columna «Referencia».</summary>
    private static void EscribirFilasComoLicitacion(
        IXLWorksheet hoja, clsExportacionLicitacion datos, IReadOnlyList<clsLineaExportacion> lineas, bool conReferencia)
    {
        string[] campos = ["Línea", "Origen", "Destino", "Tipo de vehículo", "Peso (kg)"];
        for (var c = 0; c < campos.Length; c++)
        {
            hoja.Cell(1, c + 1).Value = campos[c];
        }

        var columna = campos.Length + 1;
        Encabezados(hoja, 1, columna, datos.Moneda);
        hoja.Range(1, 1, 1, campos.Length).Style.Font.SetBold().Fill.SetBackgroundColor(_encabezado);
        var columnaReferencia = columna + ColumnasCotifly;
        if (conReferencia)
        {
            hoja.Cell(1, columnaReferencia).Value = ColumnaReferencia;
            hoja.Cell(1, columnaReferencia).Style.Font.SetBold().Fill.SetBackgroundColor(_encabezado);
            hoja.Column(columnaReferencia).Width = 26;
        }

        var fila = 2;
        foreach (var linea in lineas)
        {
            hoja.Cell(fila, 1).Value = linea.Orden;
            hoja.Cell(fila, 2).Value = linea.Origen;
            hoja.Cell(fila, 3).Value = linea.Destino;
            hoja.Cell(fila, 4).Value = linea.TipoVehiculo ?? string.Empty;
            if (linea.PesoKg is { } peso)
            {
                hoja.Cell(fila, 5).Value = peso;
            }

            EscribirLinea(hoja, fila, columna, linea, datos);
            if (conReferencia && linea.Fila is null)
            {
                hoja.Cell(fila, columnaReferencia).Value = ReferenciaAgregada;
            }

            fila++;
        }

        hoja.Columns().AdjustToContents();
    }

    private static void Encabezados(IXLWorksheet hoja, int fila, int columna, string moneda)
    {
        hoja.Cell(fila, columna).Value = ColumnaPrecio(moneda);
        hoja.Cell(fila, columna + 1).Value = ColumnaEstado;
        hoja.Cell(fila, columna + 2).Value = ColumnaConexos;
        hoja.Cell(fila, columna + 3).Value = ColumnaCamiones;
        hoja.Range(fila, columna, fila, columna + ColumnasCotifly - 1).Style.Font.SetBold().Fill.SetBackgroundColor(_encabezado);
        for (var c = columna; c < columna + ColumnasCotifly; c++)
        {
            hoja.Column(c).Width = 26;
        }

        hoja.Column(columna + 3).Width = 48;
    }

    private static void EscribirLinea(IXLWorksheet hoja, int fila, int columna, clsLineaExportacion linea, clsExportacionLicitacion datos)
    {
        var precio = hoja.Cell(fila, columna);
        if (linea.PrecioFinal is { } valor)
        {
            precio.Value = valor;
            precio.Style.NumberFormat.Format = Formato(datos.Moneda);
        }
        else
        {
            precio.Value = SinCosteo;
            hoja.Range(fila, columna, fila, columna + 1).Style.Fill.SetBackgroundColor(_pendiente);
        }

        hoja.Cell(fila, columna + 1).Value = linea.Estado;
        hoja.Cell(fila, columna + 2).Value = datos.ServiciosConexos.Count == 0 ? string.Empty : ConexosDeLaLicitacion;
        var camiones = hoja.Cell(fila, columna + 3);
        camiones.Value = TextoCamiones(linea);
        camiones.Style.Alignment.SetWrapText(true);
    }

    /// <summary>
    /// HU-023.5 (#160): la celda de camiones de una ruta: un camión por línea (<c>"1 × Tractocamión 3S3 / Furgón · 16.000 kg · 81,25 % vol."</c>)
    /// y, si la cubicación no terminó, su estado.
    /// </summary>
    public static string TextoCamiones(clsLineaExportacion linea)
    {
        ArgumentNullException.ThrowIfNull(linea);
        var lineas = (linea.Camiones ?? []).Select(c => $"{c.Orden}. {c.Resumen()}").ToList();
        if (linea.Cubicacion is { } cubicacion && !string.Equals(cubicacion, "Cubicada", StringComparison.Ordinal))
        {
            lineas.Insert(0, cubicacion);
        }

        return string.Join("\n", lineas);
    }

    /// <summary>HU-023.5 (#160): hoja con un camión por fila, para filtrar y sumar (la celda de la ruta es para leer).</summary>
    private static void EscribirCamiones(XLWorkbook libro, clsExportacionLicitacion datos)
    {
        var nombre = HojaCamiones;
        for (var n = 2; libro.Worksheets.Contains(nombre); n++)
        {
            nombre = $"{HojaCamiones} {n}";
        }

        var hoja = libro.Worksheets.Add(nombre);
        string[] campos =
        [
            "Línea", "Ruta", "Camión", "Vehículo", "Carrocería", "Peso (kg)", "Volumen (m³)", "Llenado volumen (%)", "Llenado peso (%)", "Cubicación",
            ColumnaCosteoCamion,
        ];
        for (var c = 0; c < campos.Length; c++)
        {
            hoja.Cell(1, c + 1).Value = campos[c];
        }

        hoja.Range(1, 1, 1, campos.Length).Style.Font.SetBold().Fill.SetBackgroundColor(_encabezado);
        var fila = 2;
        foreach (var linea in datos.Lineas)
        {
            foreach (var camion in linea.Camiones ?? [])
            {
                hoja.Cell(fila, 1).Value = linea.Orden;
                hoja.Cell(fila, 2).Value = $"{linea.Origen} → {linea.Destino}";
                hoja.Cell(fila, 3).Value = camion.Orden;
                hoja.Cell(fila, 4).Value = camion.Vehiculo is null && camion.Carroceria is null ? clsCamionExportacion.SinVehiculo : camion.Vehiculo ?? string.Empty;
                hoja.Cell(fila, 5).Value = camion.Carroceria ?? string.Empty;
                Numero(hoja.Cell(fila, 6), camion.PesoKg, "#,##0.##");
                Numero(hoja.Cell(fila, 7), camion.VolumenTotalM3, "#,##0.###");
                Numero(hoja.Cell(fila, 8), camion.PorcentajeLlenadoVolumen, "0.##");
                Numero(hoja.Cell(fila, 9), camion.PorcentajeLlenadoPeso, "0.##");
                hoja.Cell(fila, 10).Value = linea.Cubicacion ?? string.Empty;
                // HU-028.1 (#183): el camión rechazado por la Mesa de Costeo, sin el motivo.
                hoja.Cell(fila, 11).Value = camion.Rechazado ? clsCamionExportacion.TextoRechazado : string.Empty;
                fila++;
            }
        }

        hoja.Columns().AdjustToContents();
    }

    private static void Numero(IXLCell celda, decimal? valor, string formato)
    {
        if (valor is { } numero)
        {
            celda.Value = numero;
            celda.Style.NumberFormat.Format = formato;
        }
    }

    private static void EscribirResumen(
        XLWorkbook libro, clsExportacionLicitacion datos, IReadOnlyList<clsLineaExportacion> sinUbicar, int agregadas)
    {
        var nombre = HojaResumen;
        for (var n = 2; libro.Worksheets.Contains(nombre); n++)
        {
            nombre = $"{HojaResumen} {n}";
        }

        var hoja = libro.Worksheets.Add(nombre);
        var formato = Formato(datos.Moneda);
        var f = 1;
        hoja.Cell(f, 1).Value = $"Licitación {datos.Folio} - Botero Soto Soluciones Logísticas (Cotifly)";
        hoja.Cell(f, 1).Style.Font.SetBold().Font.SetFontSize(13);
        f += 2;

        void Dato(string etiqueta, XLCellValue valor, string? formatoNumero = null)
        {
            hoja.Cell(f, 1).Value = etiqueta;
            hoja.Cell(f, 1).Style.Font.SetBold();
            hoja.Cell(f, 2).Value = valor;
            if (formatoNumero is not null)
            {
                hoja.Cell(f, 2).Style.NumberFormat.Format = formatoNumero;
            }

            f++;
        }

        // HU-028.1 (#183): las rechazadas por la Mesa de Costeo no son pendientes ni suman en el total.
        var rechazadas = datos.Lineas.Count(l => l.Rechazada);
        var costeadas = datos.Lineas.Count(l => l.PrecioFinal is not null && !l.Rechazada);
        var pendientes = datos.Lineas.Count - costeadas - rechazadas;
        Dato("Folio", datos.Folio);
        Dato("Cliente", datos.Cliente ?? "—");
        Dato("NIT", datos.NitCliente ?? "—");
        Dato("Moneda", datos.Moneda);
        if (!string.Equals(datos.Moneda, "COP", StringComparison.OrdinalIgnoreCase) && datos.TrmAplicada > 0m)
        {
            Dato("TRM aplicada", datos.TrmAplicada, "#,##0.00");
        }

        Dato("Líneas", datos.Lineas.Count);
        Dato("Líneas costeadas", costeadas);
        Dato("Líneas sin costeo", pendientes);
        Dato(EtiquetaRechazadas, rechazadas);
        // HU-044.10 (#293, parte 2): solo si hubo líneas agregadas a mano, para que sin ellas el Excel quede igual que hoy.
        if (agregadas > 0)
        {
            Dato(EtiquetaAgregadas, agregadas);
        }
        if (pendientes == 0 && datos.PrecioTotal is { } total)
        {
            Dato(EtiquetaTotal, total, formato);
        }
        else if (pendientes == 0 && rechazadas > 0 && costeadas == 0)
        {
            Dato(EtiquetaTotal, TodasRechazadas);
            hoja.Cell(f - 1, 2).Style.Fill.SetBackgroundColor(_pendiente);
        }
        else
        {
            Dato(EtiquetaTotal, $"Pendiente: {pendientes} línea(s) sin costeo");
            hoja.Cell(f - 1, 2).Style.Fill.SetBackgroundColor(_pendiente);
        }

        Dato("Generado", datos.GeneradoEn.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
        // HU-044.7 (#290): si la leyó la IA de NEXO, se avisa que los precios van en la hoja nueva, no sobre el original.
        if (datos.LeidaConIa)
        {
            hoja.Cell(f, 1).Value = NotaLeidaConIa;
            hoja.Cell(f, 1).Style.Font.SetItalic().Fill.SetBackgroundColor(_pendiente);
            f++;
        }

        f++;

        hoja.Cell(f, 1).Value = $"Servicios conexos incluidos ({datos.ServiciosConexos.Count})";
        hoja.Cell(f, 1).Style.Font.SetBold();
        f++;
        hoja.Cell(f, 1).Value = "Servicio";
        hoja.Cell(f, 2).Value = "Cantidad";
        hoja.Cell(f, 3).Value = ColumnaPrecio(datos.Moneda);
        hoja.Range(f, 1, f, 3).Style.Font.SetBold().Fill.SetBackgroundColor(_encabezado);
        f++;
        if (datos.ServiciosConexos.Count == 0)
        {
            hoja.Cell(f, 1).Value = "Sin servicios conexos";
        }

        foreach (var conexo in datos.ServiciosConexos)
        {
            hoja.Cell(f, 1).Value = conexo.Nombre;
            hoja.Cell(f, 2).Value = conexo.Cantidad;
            hoja.Cell(f, 3).Value = conexo.Precio;
            hoja.Cell(f, 3).Style.NumberFormat.Format = formato;
            f++;
        }

        // Líneas cuya fila ya no está en el pliego (o sin fila): no se pierden en silencio.
        if (sinUbicar.Count > 0)
        {
            f++;
            hoja.Cell(f, 1).Value = $"Líneas sin fila en el pliego original ({sinUbicar.Count})";
            hoja.Cell(f, 1).Style.Font.SetBold();
            f++;
            hoja.Cell(f, 1).Value = "Línea";
            hoja.Cell(f, 2).Value = "Ruta";
            hoja.Cell(f, 3).Value = ColumnaPrecio(datos.Moneda);
            hoja.Cell(f, 4).Value = ColumnaEstado;
            hoja.Range(f, 1, f, 4).Style.Font.SetBold().Fill.SetBackgroundColor(_encabezado);
            f++;
            foreach (var linea in sinUbicar)
            {
                hoja.Cell(f, 1).Value = linea.Orden;
                hoja.Cell(f, 2).Value = $"{linea.Origen} → {linea.Destino}";
                if (linea.PrecioFinal is { } precio)
                {
                    hoja.Cell(f, 3).Value = precio;
                    hoja.Cell(f, 3).Style.NumberFormat.Format = formato;
                }
                else
                {
                    hoja.Cell(f, 3).Value = SinCosteo;
                }

                hoja.Cell(f, 4).Value = linea.Estado;
                f++;
            }
        }

        hoja.Columns().AdjustToContents();
    }
}
