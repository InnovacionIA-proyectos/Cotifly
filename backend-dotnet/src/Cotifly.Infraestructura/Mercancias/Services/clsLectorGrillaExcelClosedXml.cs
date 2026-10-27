using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using Cotifly.Aplicacion.Mercancias.DTOs;
using Cotifly.Aplicacion.Mercancias.Interfaces;
using Cotifly.Dominio.Comun;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Mercancias.Services;

/// <summary>
/// Lectura local del Excel de mercancías como grilla cruda con ClosedXML (HU-035.1, #202, ADR-024), la misma librería del Excel de
/// licitaciones. No usa Nexo ni su regla de análisis salvo la detección del encabezado (la primera de las 15 primeras filas usadas con 3
/// valores distintos; si ninguna, la primera fila con datos).
/// </summary>
/// <remarks>
/// <para><b>Acotado contra archivos maliciosos o corruptos</b> (nunca un 500 por el contenido):</para>
/// <list type="bullet">
/// <item>antes de abrirlo, el paquete se revisa como zip: firma <c>PK</c> (un <c>.xls</c> antiguo o un libro cifrado empiezan por
/// <c>D0 CF 11 E0</c>), a lo sumo <see cref="MaximoEntradasZip"/> entradas, la parte <c>xl/workbook.xml</c> y, <b>descomprimiendo de
/// verdad</b> (no se confía en el tamaño declarado), a lo sumo <see cref="MaximoDescomprimidoBytes"/> en total: así una bomba zip no
/// llega a ClosedXML, que carga el libro entero en memoria;</item>
/// <item>después, con un lector XML de flujo sin DTD, a lo sumo <see cref="MaximoElementosXml"/> elementos en todas las partes del libro:
/// así un libro pequeño y muy denso (millones de celdas mínimas) tampoco llega a ClosedXML;</item>
/// <item>las fórmulas nunca se evalúan: se devuelve el valor que Excel guardó (<see cref="IXLCell.CachedValue"/>);</item>
/// <item>topes de hojas, columnas, filas, largo de celda y celdas de toda la respuesta, con el truncado indicado en la respuesta;</item>
/// <item>a lo sumo <see cref="MaximoSimultaneos"/> lecturas a la vez (memoria del proceso); si el cupo no se libera en
/// <see cref="EsperaCupo"/>, <c>SERVICIO_OCUPADO</c>;</item>
/// <item>la cancelación del llamador se respeta.</item>
/// </list>
/// <para><b>Hojas ocultas</b> (<c>hidden</c> y <c>veryHidden</c>): se leen como las visibles y vienen marcadas con
/// <see cref="clsHojaExcelMercancias.Oculta"/> (decisión del usuario, #202).</para>
/// <para><b>Formato estable</b> (sin depender de la cultura ni del formato visual de la celda): números invariantes con hasta 15 cifras
/// significativas, fechas ISO (<c>yyyy-MM-dd</c> o <c>yyyy-MM-ddTHH:mm:ss</c>), duraciones <c>HH:mm:ss</c>, booleanos
/// <c>true</c>/<c>false</c>, errores con su texto de Excel y el texto sin espacios al borde y con los saltos de línea como un espacio.</para>
/// </remarks>
public sealed class clsLectorGrillaExcelClosedXml : IClsLectorGrillaExcel, IDisposable
{
    /// <summary>Hojas que se leen (en el orden del libro).</summary>
    public const int MaximoHojas = 20;

    /// <summary>Columnas que se leen desde la primera usada. Un Excel de mercancías tiene unas 6 a 10.</summary>
    public const int MaximoColumnas = 50;

    /// <summary>Filas con datos por hoja (las mismas 1000 del análisis de licitaciones).</summary>
    public const int MaximoFilas = 1000;

    /// <summary>Caracteres por celda (Excel admite 32.767).</summary>
    public const int MaximoLargoCelda = 500;

    /// <summary>Celdas en toda la respuesta (encabezados y filas de todas las hojas): acota el JSON aunque haya muchas hojas anchas.</summary>
    public const int MaximoCeldas = 100_000;

    /// <summary>Filas usadas en las que se busca el encabezado (las mismas 15 de licitaciones).</summary>
    public const int FilasBusquedaEncabezado = 15;

    /// <summary>Tamaño total descomprimido del paquete (bomba zip): 64 MB. Un Excel de mercancías real descomprime a unos pocos MB.</summary>
    public const long MaximoDescomprimidoBytes = 64L * 1024 * 1024;

    /// <summary>Entradas del paquete zip. Un libro normal tiene decenas.</summary>
    public const int MaximoEntradasZip = 1000;

    /// <summary>
    /// Elementos XML en todas las partes del libro (celdas, valores, cadenas compartidas, estilos...): acota la memoria de ClosedXML, que
    /// crea un objeto por elemento. 64 MB de celdas mínimas (<c>&lt;c&gt;&lt;v&gt;1&lt;/v&gt;&lt;/c&gt;</c>) comprimen a unos 200 KB y
    /// son casi 8 millones de elementos: más de 1 GB y 15 s de CPU por lectura. Un millón alcanza para unas 300.000 celdas (decenas de
    /// miles de filas de mercancías) y deja la lectura en unos 200 MB.
    /// </summary>
    public const int MaximoElementosXml = 1_000_000;

    /// <summary>Lecturas simultáneas en el proceso.</summary>
    public const int MaximoSimultaneos = 4;

    /// <summary>Espera máxima por un lugar en el cupo de lecturas.</summary>
    public static readonly TimeSpan EsperaCupo = TimeSpan.FromSeconds(15);

    /// <summary>Motivo (<c>detalle.motivo</c>) de <c>ARCHIVO_DEMASIADO_GRANDE</c> cuando el paquete descomprimido pasa el tope.</summary>
    public const string MotivoDescomprimido = "DESCOMPRIMIDO_EXCEDE_LIMITE";

    /// <summary>Motivos de <c>EXCEL_INVALIDO</c>.</summary>
    public const string MotivoNoEsXlsx = "NO_ES_XLSX";
    public const string MotivoCifradoOXls = "CIFRADO_O_XLS";
    public const string MotivoZipInvalido = "ZIP_INVALIDO";
    public const string MotivoDemasiadasEntradas = "DEMASIADAS_ENTRADAS";
    public const string MotivoSinLibro = "SIN_LIBRO";
    public const string MotivoLibroIlegible = "LIBRO_ILEGIBLE";

    private const string MensajeInvalido = "No se pudo leer el archivo: no es un Excel (.xlsx o .xlsm) válido.";

    private readonly SemaphoreSlim _cupo;
    private readonly TimeSpan _espera;
    private readonly ILogger<clsLectorGrillaExcelClosedXml> _logger;

    public clsLectorGrillaExcelClosedXml(ILogger<clsLectorGrillaExcelClosedXml> logger)
        : this(logger, EsperaCupo, MaximoSimultaneos)
    {
    }

    /// <summary>Con otra espera y otro cupo (pruebas).</summary>
    internal clsLectorGrillaExcelClosedXml(ILogger<clsLectorGrillaExcelClosedXml> logger, TimeSpan espera, int simultaneos)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(simultaneos, 1);
        _logger = logger;
        _espera = espera;
        _cupo = new SemaphoreSlim(simultaneos, simultaneos);
    }

    public async Task<clsResultado<clsAnalisisExcelMercancias>> LeerAsync(
        string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        if (!await _cupo.WaitAsync(_espera, cancellationToken))
        {
            _logger.LogWarning("Cupo de lecturas del Excel de mercancías lleno tras {Segundos} s: se responde SERVICIO_OCUPADO.", _espera.TotalSeconds);
            return new clsErrorDominio(clsCodigosError.ServicioOcupado, "Hay muchos archivos analizándose en este momento: intenta de nuevo en unos segundos.");
        }

        try
        {
            return Leer(nombreArchivo ?? string.Empty, contenido, cancellationToken);
        }
        finally
        {
            _cupo.Release();
        }
    }

    public void Dispose() => _cupo.Dispose();

    /// <summary>Lectura sin el cupo (sincrónica): primero el paquete, después el libro.</summary>
    internal clsResultado<clsAnalisisExcelMercancias> Leer(string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default)
    {
        if (VerificarPaquete(contenido, cancellationToken) is { } invalido)
        {
            _logger.LogInformation("Excel de mercancías rechazado antes de abrirlo: {Codigo} ({Motivo}).", invalido.Codigo, invalido.Detalle);
            return invalido;
        }

        if (VerificarElementos(contenido, cancellationToken) is { } denso)
        {
            _logger.LogInformation("Excel de mercancías rechazado antes de abrirlo: {Codigo} ({Motivo}).", denso.Codigo, denso.Detalle);
            return denso;
        }

        XLWorkbook libro;
        try
        {
            libro = new XLWorkbook(new MemoryStream(contenido, writable: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation("ClosedXML no pudo abrir el Excel de mercancías ({Tipo}).", ex.GetType().Name);
            return Invalido(MotivoLibroIlegible);
        }

        using (libro)
        {
            try
            {
                return LeerLibro(nombreArchivo, libro, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                // Un libro que abre pero tiene partes inconsistentes: es del archivo, no del servidor.
                _logger.LogInformation("ClosedXML no pudo leer las celdas del Excel de mercancías ({Tipo}).", ex.GetType().Name);
                return Invalido(MotivoLibroIlegible);
            }
        }
    }

    /// <summary>
    /// Revisa el paquete antes de abrirlo: firma, entradas, <c>xl/workbook.xml</c> y tamaño descomprimido REAL (leyendo cada entrada). <c>null</c>
    /// si se puede abrir.
    /// </summary>
    internal static clsErrorDominio? VerificarPaquete(byte[] contenido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        if (contenido is [0xD0, 0xCF, 0x11, 0xE0, ..])
        {
            return new clsErrorDominio(clsCodigosError.ExcelInvalido,
                "El archivo está protegido con contraseña o es un Excel antiguo (.xls): guárdalo como .xlsx sin contraseña.", MotivoCifradoOXls);
        }

        if (contenido is not [0x50, 0x4B, 0x03, 0x04, ..])
        {
            return Invalido(MotivoNoEsXlsx);
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(contenido, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaximoEntradasZip)
            {
                return Invalido(MotivoDemasiadasEntradas);
            }

            if (!zip.Entries.Any(e => string.Equals(e.FullName, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase)))
            {
                return Invalido(MotivoSinLibro);
            }

            var buffer = new byte[81_920];
            long total = 0;
            foreach (var entrada in zip.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entrada.Length > MaximoDescomprimidoBytes - total)
                {
                    return DemasiadoGrande();
                }

                using var flujo = entrada.Open();
                int leidos;
                while ((leidos = flujo.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += leidos;
                    if (total > MaximoDescomprimidoBytes)
                    {
                        return DemasiadoGrande();
                    }
                }
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            return Invalido(MotivoZipInvalido);
        }
    }

    /// <summary>
    /// Cuenta los elementos XML de todas las partes XML del paquete (las mismas que abriría ClosedXML, resueltas por sus relaciones) con
    /// un lector de flujo, sin DTD ni entidades externas. <c>ARCHIVO_DEMASIADO_GRANDE</c> (<see cref="MotivoDescomprimido"/>) si pasan
    /// de <see cref="MaximoElementosXml"/>; <c>EXCEL_INVALIDO</c> (<see cref="MotivoLibroIlegible"/>) si una parte no es XML válido;
    /// <c>null</c> si se puede abrir. Se llama después de <see cref="VerificarPaquete"/>: el contenido descomprimido ya está acotado.
    /// </summary>
    internal static clsErrorDominio? VerificarElementos(byte[] contenido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        var ajustes = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };
        try
        {
            using var documento = SpreadsheetDocument.Open(new MemoryStream(contenido, writable: false), isEditable: false);
            long elementos = 0;
            foreach (var parte in documento.GetAllParts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!parte.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var lector = XmlReader.Create(parte.GetStream(FileMode.Open, FileAccess.Read), ajustes);
                while (lector.Read())
                {
                    if (lector.NodeType == XmlNodeType.Element && ++elementos > MaximoElementosXml)
                    {
                        return new clsErrorDominio(clsCodigosError.ArchivoDemasiadoGrande,
                            "El Excel tiene demasiadas celdas para analizarlo: deja solo las hojas y filas de las mercancías.", MotivoDescomprimido);
                    }
                }
            }

            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return Invalido(MotivoLibroIlegible);
        }
    }

    private static clsErrorDominio Invalido(string motivo) => new(clsCodigosError.ExcelInvalido, MensajeInvalido, motivo);

    private static clsErrorDominio DemasiadoGrande() => new(clsCodigosError.ArchivoDemasiadoGrande,
        "El contenido del Excel descomprimido supera 64 MB: deja solo las hojas y filas de las mercancías.", MotivoDescomprimido);

    private static clsResultado<clsAnalisisExcelMercancias> LeerLibro(string nombreArchivo, XLWorkbook libro, CancellationToken cancellationToken)
    {
        var totalHojas = libro.Worksheets.Count;
        var presupuesto = new clsPresupuesto(MaximoCeldas);
        var hojas = new List<clsHojaExcelMercancias>();
        foreach (var ws in libro.Worksheets.Take(MaximoHojas))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LeerHoja(ws, presupuesto, cancellationToken) is { } hoja)
            {
                hojas.Add(hoja);
            }
        }

        if (hojas.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.ExcelSinDatos, "El archivo no tiene ninguna hoja con datos.");
        }

        return new clsAnalisisExcelMercancias(nombreArchivo, hojas, totalHojas, totalHojas > MaximoHojas);
    }

    /// <summary>Una hoja como grilla, o <c>null</c> si no tiene datos.</summary>
    internal static clsHojaExcelMercancias? LeerHoja(IXLWorksheet ws, clsPresupuesto presupuesto, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(presupuesto);
        var usado = ws.RangeUsed(XLCellsUsedOptions.Contents);
        if (usado is null)
        {
            return null;
        }

        var primeraCol = usado.FirstColumn().ColumnNumber();
        var ultimaColReal = usado.LastColumn().ColumnNumber();
        var ultimaCol = Math.Min(ultimaColReal, primeraCol + MaximoColumnas - 1);
        var ancho = ultimaCol - primeraCol + 1;

        // Primera pasada: qué filas tienen algún valor en las columnas leídas (sin guardar los textos).
        var filasConValor = new SortedSet<int>();
        foreach (var celda in ws.CellsUsed(XLCellsUsedOptions.Contents))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var c = celda.Address.ColumnNumber;
            if (c >= primeraCol && c <= ultimaCol && Texto(celda).Length > 0)
            {
                filasConValor.Add(celda.Address.RowNumber);
            }
        }

        if (filasConValor.Count == 0)
        {
            return null;
        }

        var recortadas = false;
        string[] Fila(int r)
        {
            var valores = Enumerable.Repeat(string.Empty, ancho).ToArray();
            foreach (var celda in ws.Row(r).CellsUsed(XLCellsUsedOptions.Contents))
            {
                var c = celda.Address.ColumnNumber;
                if (c < primeraCol || c > ultimaCol)
                {
                    continue;
                }

                var texto = Texto(celda);
                if (texto.Length > MaximoLargoCelda)
                {
                    // Sin partir un emoji (par sustituto): una mitad suelta no es UTF-16 válido en el JSON.
                    texto = texto[..(char.IsHighSurrogate(texto[MaximoLargoCelda - 1]) ? MaximoLargoCelda - 1 : MaximoLargoCelda)];
                    recortadas = true;
                }

                valores[c - primeraCol] = texto;
            }

            return valores;
        }

        // Encabezado: la primera de las 15 primeras filas usadas con al menos 3 valores distintos; si ninguna, la primera con datos.
        var primeraFila = filasConValor.Min;
        var filaEncabezado = 0;
        string[]? encabezados = null;
        foreach (var r in filasConValor.GetViewBetween(primeraFila, primeraFila + FilasBusquedaEncabezado))
        {
            var valores = Fila(r);
            if (valores.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 3)
            {
                filaEncabezado = r;
                encabezados = valores;
                break;
            }
        }

        if (encabezados is null)
        {
            filaEncabezado = primeraFila;
            encabezados = Fila(primeraFila);
        }

        presupuesto.Consumir(ancho);
        var debajo = filasConValor.GetViewBetween(filaEncabezado + 1, int.MaxValue);
        var filas = new List<IReadOnlyList<string>>();
        var filasExcel = new List<int>();
        foreach (var r in debajo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filas.Count >= MaximoFilas || !presupuesto.Consumir(ancho))
            {
                break;
            }

            filas.Add(Fila(r));
            filasExcel.Add(r);
        }

        return new clsHojaExcelMercancias(
            ws.Name,
            filaEncabezado,
            encabezados,
            filas,
            filasExcel,
            debajo.Count,
            filas.Count < debajo.Count,
            ultimaColReal - primeraCol + 1,
            ultimaColReal > ultimaCol,
            recortadas,
            ws.Visibility != XLWorksheetVisibility.Visible);
    }

    /// <summary>
    /// Texto estable de una celda (ver la clase). De una fórmula, el valor que Excel guardó: ClosedXML nunca la evalúa aquí; sin valor
    /// guardado, vacío.
    /// </summary>
    internal static string Texto(IXLCell celda)
    {
        ArgumentNullException.ThrowIfNull(celda);
        var valor = celda.HasFormula ? celda.CachedValue : celda.Value;
        return valor.Type switch
        {
            XLDataType.Blank => string.Empty,
            XLDataType.Boolean => valor.GetBoolean() ? "true" : "false",
            XLDataType.Number => Numero(valor.GetNumber()),
            XLDataType.DateTime => Fecha(valor.GetDateTime()),
            XLDataType.TimeSpan => valor.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture),
            XLDataType.Error => valor.ToString(CultureInfo.InvariantCulture),
            _ => Limpio(valor.GetText()),
        };
    }

    /// <summary>Número invariante con hasta 15 cifras significativas (las que muestra Excel): <c>0.1 + 0.2</c> es <c>0.3</c>.</summary>
    internal static string Numero(double numero) =>
        double.IsFinite(numero) ? (numero == 0 ? 0d : numero).ToString("G15", CultureInfo.InvariantCulture) : string.Empty;

    internal static string Fecha(DateTime fecha) =>
        fecha.TimeOfDay == TimeSpan.Zero
            ? fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : fecha.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Sin espacios al borde y con los caracteres de control (saltos de línea, tabuladores) como un espacio.</summary>
    internal static string Limpio(string texto)
    {
        if (string.IsNullOrEmpty(texto))
        {
            return string.Empty;
        }

        if (!texto.Any(char.IsControl))
        {
            return texto.Trim();
        }

        var limpio = new StringBuilder(texto.Length);
        var anteriorEspacio = false;
        foreach (var c in texto)
        {
            if (char.IsControl(c))
            {
                if (!anteriorEspacio)
                {
                    limpio.Append(' ');
                    anteriorEspacio = true;
                }

                continue;
            }

            limpio.Append(c);
            anteriorEspacio = c == ' ';
        }

        return limpio.ToString().Trim();
    }

    /// <summary>Celdas que todavía caben en la respuesta (<see cref="MaximoCeldas"/>), compartido entre las hojas del libro.</summary>
    internal sealed class clsPresupuesto(int celdas)
    {
        public int Restantes { get; private set; } = celdas;

        /// <summary>Descuenta <paramref name="cantidad"/> celdas si caben; <c>false</c> (sin descontar) si no.</summary>
        public bool Consumir(int cantidad)
        {
            if (cantidad > Restantes)
            {
                return false;
            }

            Restantes -= cantidad;
            return true;
        }
    }
}
