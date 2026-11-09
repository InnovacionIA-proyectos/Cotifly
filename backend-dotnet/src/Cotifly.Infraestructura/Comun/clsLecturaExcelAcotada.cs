using System.IO.Compression;
using System.Xml;
using Cotifly.Dominio.Comun;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Logging;

namespace Cotifly.Infraestructura.Comun;

/// <summary>
/// Controles compartidos para abrir con ClosedXML un Excel subido por un usuario (HU-035.1 #202 y HU-035.2 #213, ADR-024). Los usan el
/// lector del Excel de mercancías (<c>clsLectorGrillaExcelClosedXml</c>) y el del pliego de licitación
/// (<c>clsLectorExcelLicitacionClosedXml</c>). Singleton: el cupo es uno para todas las lecturas de Excel del proceso.
/// </summary>
/// <remarks>
/// <para>ClosedXML carga el libro entero en memoria (un objeto por elemento XML). Antes de abrirlo:</para>
/// <list type="bullet">
/// <item><see cref="VerificarPaquete"/>: firma <c>PK</c> (un <c>.xls</c> antiguo o un libro cifrado empiezan por <c>D0 CF 11 E0</c>), a lo
/// sumo <see cref="MaximoEntradasZip"/> entradas, el libro (<c>xl/workbook.xml</c> o la parte a la que apunta <c>_rels/.rels</c>) y, <b>descomprimiendo de verdad</b> (no se confía en el
/// tamaño declarado), a lo sumo <see cref="MaximoDescomprimidoBytes"/> en total: una bomba zip no llega a ClosedXML;</item>
/// <item><see cref="VerificarElementos"/>: con un lector XML de flujo sin DTD ni resolutor, a lo sumo <see cref="MaximoElementosXml"/>
/// elementos en todas las partes del libro: un libro pequeño y muy denso (millones de celdas mínimas) tampoco llega a ClosedXML;</item>
/// <item><see cref="LeerAsync{T}"/>: a lo sumo <see cref="MaximoSimultaneos"/> lecturas a la vez en el proceso (las dos de arriba y la de
/// ClosedXML); si el cupo no se libera en <see cref="EsperaCupo"/>, <c>SERVICIO_OCUPADO</c> (503 con <c>Retry-After</c>). La cancelación
/// del llamador se respeta y siempre libera el cupo.</item>
/// </list>
/// <para>Sin códigos nuevos: <c>EXCEL_INVALIDO</c> y <c>ARCHIVO_DEMASIADO_GRANDE</c> con el motivo en <c>detalle.motivo</c>.</para>
/// </remarks>
public sealed class clsLecturaExcelAcotada : IDisposable
{
    /// <summary>Tamaño total descomprimido del paquete (bomba zip): 64 MB. Un Excel real descomprime a unos pocos MB.</summary>
    public const long MaximoDescomprimidoBytes = 64L * 1024 * 1024;

    /// <summary>Entradas del paquete zip. Un libro normal tiene decenas.</summary>
    public const int MaximoEntradasZip = 1000;

    /// <summary>
    /// Elementos XML en todas las partes del libro (celdas, valores, cadenas compartidas, estilos...): acota la memoria de ClosedXML, que
    /// crea un objeto por elemento. 64 MB de celdas mínimas (<c>&lt;c&gt;&lt;v&gt;1&lt;/v&gt;&lt;/c&gt;</c>) comprimen a unos 200 KB y
    /// son casi 8 millones de elementos: más de 1 GB y 15 s de CPU por lectura. Un millón alcanza para unas 300.000 celdas y deja la
    /// lectura en unos 200 MB.
    /// </summary>
    public const int MaximoElementosXml = 1_000_000;

    /// <summary>Lecturas simultáneas en el proceso (todas las de Excel: mercancías y licitación).</summary>
    public const int MaximoSimultaneos = 4;

    /// <summary>Espera máxima por un lugar en el cupo de lecturas.</summary>
    public static readonly TimeSpan EsperaCupo = TimeSpan.FromSeconds(15);

    /// <summary>Motivo (<c>detalle.motivo</c>) de <c>ARCHIVO_DEMASIADO_GRANDE</c> cuando el paquete descomprimido pasa un tope.</summary>
    public const string MotivoDescomprimido = "DESCOMPRIMIDO_EXCEDE_LIMITE";

    /// <summary>Motivos de <c>EXCEL_INVALIDO</c>.</summary>
    public const string MotivoNoEsXlsx = "NO_ES_XLSX";
    public const string MotivoCifradoOXls = "CIFRADO_O_XLS";
    public const string MotivoZipInvalido = "ZIP_INVALIDO";
    public const string MotivoDemasiadasEntradas = "DEMASIADAS_ENTRADAS";
    public const string MotivoSinLibro = "SIN_LIBRO";
    public const string MotivoLibroIlegible = "LIBRO_ILEGIBLE";

    /// <summary>Mensaje de <c>EXCEL_INVALIDO</c> (el motivo va en <c>detalle.motivo</c>).</summary>
    public const string MensajeInvalido = "No se pudo leer el archivo: no es un Excel (.xlsx o .xlsm) válido.";

    /// <summary>Tope de <c>_rels/.rels</c> al buscar el libro: un archivo de relaciones real tiene unos cientos de bytes.</summary>
    private const int MaximoRelacionesBytes = 64 * 1024;

    /// <summary>Tipo del dibujo VML (comentarios, controles heredados): es XML aunque el tipo no termine en <c>+xml</c>.</summary>
    private const string TipoVml = "application/vnd.openxmlformats-officedocument.vmlDrawing";

    private readonly SemaphoreSlim _cupo;
    private readonly TimeSpan _espera;
    private readonly ILogger<clsLecturaExcelAcotada> _logger;

    public clsLecturaExcelAcotada(ILogger<clsLecturaExcelAcotada> logger)
        : this(logger, EsperaCupo, MaximoSimultaneos)
    {
    }

    /// <summary>Con otra espera y otro cupo (pruebas).</summary>
    internal clsLecturaExcelAcotada(ILogger<clsLecturaExcelAcotada> logger, TimeSpan espera, int simultaneos)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(simultaneos, 1);
        _logger = logger;
        _espera = espera;
        _cupo = new SemaphoreSlim(simultaneos, simultaneos);
    }

    /// <summary>El semáforo del cupo (pruebas: ocuparlo a mano).</summary>
    internal SemaphoreSlim Cupo => _cupo;

    /// <summary>
    /// Dentro del cupo: <see cref="Verificar"/> y, si el archivo pasa, <paramref name="leer"/> (la lectura con ClosedXML del llamador).
    /// <c>SERVICIO_OCUPADO</c> si el cupo no se libera en la espera; el error de la verificación si no pasa. <paramref name="documento"/>
    /// solo nombra el archivo en el log ("mercancías", "licitación").
    /// </summary>
    public async Task<clsResultado<T>> LeerAsync<T>(
        byte[] contenido, string documento, Func<CancellationToken, clsResultado<T>> leer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        ArgumentNullException.ThrowIfNull(leer);
        if (!await _cupo.WaitAsync(_espera, cancellationToken))
        {
            _logger.LogWarning("Cupo de lecturas de Excel lleno tras {Segundos} s ({Documento}): se responde SERVICIO_OCUPADO.", _espera.TotalSeconds, documento);
            return new clsErrorDominio(clsCodigosError.ServicioOcupado, "Hay muchos archivos analizándose en este momento: intenta de nuevo en unos segundos.");
        }

        try
        {
            if (Verificar(contenido, cancellationToken) is { } rechazo)
            {
                _logger.LogInformation("Excel de {Documento} rechazado antes de abrirlo: {Codigo} ({Motivo}).", documento, rechazo.Codigo, rechazo.Detalle);
                return rechazo;
            }

            return leer(cancellationToken);
        }
        finally
        {
            _cupo.Release();
        }
    }

    public void Dispose() => _cupo.Dispose();

    /// <summary>Sin el cupo: <see cref="VerificarPaquete"/> y después <see cref="VerificarElementos"/>. <c>null</c> si se puede abrir.</summary>
    public static clsErrorDominio? Verificar(byte[] contenido, CancellationToken cancellationToken = default) =>
        VerificarPaquete(contenido, cancellationToken) ?? VerificarElementos(contenido, cancellationToken);

    /// <summary>
    /// Revisa el paquete antes de abrirlo: firma, entradas, el libro (<c>xl/workbook.xml</c> o el de <c>_rels/.rels</c>) y tamaño descomprimido REAL (leyendo cada entrada).
    /// <c>null</c> si se puede abrir.
    /// </summary>
    public static clsErrorDominio? VerificarPaquete(byte[] contenido, CancellationToken cancellationToken = default)
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

            if (!TieneLibro(zip))
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
    /// Cuenta los elementos XML de <c>[Content_Types].xml</c>, de las relaciones (<c>.rels</c>, que System.IO.Packaging lee enteras al
    /// abrir el paquete; validación de #277) y de todas las partes XML del paquete (las mismas que abriría ClosedXML, resueltas por sus
    /// relaciones; ver <see cref="EsParteXml"/>) con un lector de flujo, sin DTD ni entidades externas. <c>ARCHIVO_DEMASIADO_GRANDE</c> (<see cref="MotivoDescomprimido"/>) si pasan
    /// de <see cref="MaximoElementosXml"/>; <c>EXCEL_INVALIDO</c> (<see cref="MotivoLibroIlegible"/>) si una parte no es XML válido o trae
    /// un DTD; <c>null</c> si se puede abrir. Se llama después de <see cref="VerificarPaquete"/>: el contenido descomprimido ya está acotado.
    /// </summary>
    public static clsErrorDominio? VerificarElementos(byte[] contenido, CancellationToken cancellationToken = default)
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
        long elementos = 0;
        bool Excede(Stream flujo)
        {
            using var lector = XmlReader.Create(flujo, ajustes);
            while (lector.Read())
            {
                if (lector.NodeType == XmlNodeType.Element && ++elementos > MaximoElementosXml)
                {
                    return true;
                }
            }

            return false;
        }

        try
        {
            // Primero lo que System.IO.Packaging lee entero al abrir el paquete y no son partes: [Content_Types].xml y las relaciones.
            using (var zip = new ZipArchive(new MemoryStream(contenido, writable: false), ZipArchiveMode.Read))
            {
                foreach (var entrada in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (EsEntradaDelPaquete(entrada.FullName) && Excede(entrada.Open()))
                    {
                        return DemasiadosElementos();
                    }
                }
            }

            using var documento = SpreadsheetDocument.Open(new MemoryStream(contenido, writable: false), isEditable: false);
            foreach (var parte in documento.GetAllParts())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // La parte principal se cuenta siempre: el SDK no fija su tipo y la abre con cualquiera (ver EsParteXml).
                if ((parte is WorkbookPart || EsParteXml(parte.ContentType)) && Excede(parte.GetStream(FileMode.Open, FileAccess.Read)))
                {
                    return DemasiadosElementos();
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

    /// <summary>
    /// ¿La parte se lee como XML? HU-035.3 (#277): sí si su tipo, sin parámetros, termina en <c>+xml</c> o es <c>application/xml</c> o
    /// <c>text/xml</c>, y también el dibujo VML (<see cref="TipoVml"/>, sin <c>+xml</c>), que ClosedXML lee como XML para los comentarios.
    /// Las demás son binarias y se saltan: imágenes, <c>vbaProject</c>, objetos incrustados y la configuración de impresión que guarda
    /// Excel (<c>xl/printerSettings/printerSettings1.bin</c>, de tipo <c>...spreadsheetml.printerSettings</c>). Antes bastaba «xml» en
    /// el tipo y esas partes de <c>openxmlformats</c> llegaban al lector XML: un libro con la impresión configurada era
    /// <c>LIBRO_ILEGIBLE</c>. Cambiar el tipo en <c>[Content_Types].xml</c> no salta el conteo: el SDK de Open XML exige el tipo de las
    /// partes de tipo fijo (hoja, cadenas, estilos, VML...) y si no coincide el libro no abre (<c>LIBRO_ILEGIBLE</c>); la parte principal
    /// (<c>xl/workbook.xml</c>) no tiene tipo fijo, así que <see cref="VerificarElementos"/> la cuenta siempre, declare el tipo que declare.
    /// Las demás partes sin tipo fijo (imágenes, XML personalizado, propiedades, incrustadas, miniatura) ClosedXML no las lee como XML.
    /// </summary>
    internal static bool EsParteXml(string tipo)
    {
        var sinParametros = tipo.Split(';')[0].Trim();
        return sinParametros.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || sinParametros.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || sinParametros.Equals("text/xml", StringComparison.OrdinalIgnoreCase)
            || sinParametros.Equals(TipoVml, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ¿El paquete trae el libro? Excel lo guarda en <c>xl/workbook.xml</c>, pero otras herramientas (por ejemplo el SDK de Open XML al
    /// convertir un <c>.xlsx</c> en <c>.xlsm</c>, que lo deja en <c>xl/workbook2.xml</c>) le ponen otro nombre: el libro es la parte a la
    /// que apunta la relación <c>officeDocument</c> de <c>_rels/.rels</c> (como lo resuelve ClosedXML). Se lee ese archivo de relaciones
    /// sin DTD y con un tope de bytes; si no se puede leer, vale el nombre de Excel.
    /// </summary>
    private static bool TieneLibro(ZipArchive zip)
    {
        bool Existe(string nombre) => zip.Entries.Any(e => string.Equals(e.FullName, nombre, StringComparison.OrdinalIgnoreCase));
        if (Existe("xl/workbook.xml"))
        {
            return true;
        }

        var relaciones = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, "_rels/.rels", StringComparison.OrdinalIgnoreCase));
        if (relaciones is null || relaciones.Length > MaximoRelacionesBytes)
        {
            return false;
        }

        try
        {
            var ajustes = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximoRelacionesBytes };
            using var lector = XmlReader.Create(relaciones.Open(), ajustes);
            while (lector.Read())
            {
                if (lector.NodeType == XmlNodeType.Element && lector.LocalName == "Relationship"
                    && lector.GetAttribute("Type") is { } tipo && tipo.EndsWith("/officeDocument", StringComparison.Ordinal)
                    && lector.GetAttribute("Target") is { Length: > 0 } destino)
                {
                    return Existe(Uri.UnescapeDataString(destino).TrimStart('/'));
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            return false;
        }

        return false;
    }

    /// <summary><c>EXCEL_INVALIDO</c> con <paramref name="motivo"/> en <c>detalle.motivo</c>.</summary>
    public static clsErrorDominio Invalido(string motivo) => new(clsCodigosError.ExcelInvalido, MensajeInvalido, motivo);

    /// <summary>¿La entrada del zip es de las que lee System.IO.Packaging al abrir el paquete sin ser una parte?</summary>
    private static bool EsEntradaDelPaquete(string nombre) =>
        nombre.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase) || nombre.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static clsErrorDominio DemasiadosElementos() => new(clsCodigosError.ArchivoDemasiadoGrande,
        "El Excel tiene demasiadas celdas para analizarlo: deja solo las hojas y filas que se necesitan.", MotivoDescomprimido);

    private static clsErrorDominio DemasiadoGrande() => new(clsCodigosError.ArchivoDemasiadoGrande,
        "El contenido del Excel descomprimido supera 64 MB: deja solo las hojas y filas que se necesitan.", MotivoDescomprimido);
}
