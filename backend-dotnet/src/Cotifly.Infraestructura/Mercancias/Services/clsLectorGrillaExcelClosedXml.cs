using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Mercancias.DTOs;
using Cotifly.Aplicacion.Mercancias.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Comun;
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
/// <item>antes de abrirlo, los controles compartidos de <see cref="clsLecturaExcelAcotada"/> (HU-035.2, #213: los mismos del pliego de
/// licitación): firma, 1000 entradas, 64 MB descomprimidos contados de verdad, 1.000.000 de elementos XML contados en flujo sin DTD y
/// el cupo de lecturas simultáneas del proceso (<c>SERVICIO_OCUPADO</c>);</item>
/// <item>las fórmulas nunca se evalúan: se devuelve el valor que Excel guardó (<see cref="IXLCell.CachedValue"/>);</item>
/// <item>topes de hojas, columnas, filas, largo de celda y celdas de toda la respuesta, con el truncado indicado en la respuesta;</item>
/// <item>la cancelación del llamador se respeta.</item>
/// </list>
/// <para><b>Hojas ocultas</b> (<c>hidden</c> y <c>veryHidden</c>): se leen como las visibles y vienen marcadas con
/// <see cref="clsHojaExcelMercancias.Oculta"/> (decisión del usuario, #202).</para>
/// <para><b>Formato estable</b> (sin depender de la cultura ni del formato visual de la celda): números invariantes con hasta 15 cifras
/// significativas, fechas ISO (<c>yyyy-MM-dd</c> o <c>yyyy-MM-ddTHH:mm:ss</c>), duraciones <c>HH:mm:ss</c>, booleanos
/// <c>true</c>/<c>false</c>, errores con su texto de Excel y el texto sin espacios al borde y con los saltos de línea como un espacio.</para>
/// </remarks>
public sealed class clsLectorGrillaExcelClosedXml : IClsLectorGrillaExcel
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

    private readonly clsLecturaExcelAcotada _lectura;
    private readonly ILogger<clsLectorGrillaExcelClosedXml> _logger;

    public clsLectorGrillaExcelClosedXml(clsLecturaExcelAcotada lectura, ILogger<clsLectorGrillaExcelClosedXml> logger)
    {
        ArgumentNullException.ThrowIfNull(lectura);
        ArgumentNullException.ThrowIfNull(logger);
        _lectura = lectura;
        _logger = logger;
    }

    /// <summary>Dentro del cupo compartido y tras los controles del paquete (<see cref="clsLecturaExcelAcotada.LeerAsync{T}"/>).</summary>
    public Task<clsResultado<clsAnalisisExcelMercancias>> LeerAsync(
        string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        return _lectura.LeerAsync(contenido, "mercancías", ct => LeerVerificado(nombreArchivo ?? string.Empty, contenido, ct), cancellationToken);
    }

    /// <summary>Lectura sin el cupo (sincrónica, pruebas): primero los controles del paquete, después el libro.</summary>
    internal clsResultado<clsAnalisisExcelMercancias> Leer(string nombreArchivo, byte[] contenido, CancellationToken cancellationToken = default) =>
        clsLecturaExcelAcotada.Verificar(contenido, cancellationToken) is { } rechazo
            ? rechazo
            : LeerVerificado(nombreArchivo, contenido, cancellationToken);

    /// <summary>El libro con ClosedXML, ya verificado (<see cref="clsLecturaExcelAcotada.Verificar"/>).</summary>
    private clsResultado<clsAnalisisExcelMercancias> LeerVerificado(string nombreArchivo, byte[] contenido, CancellationToken cancellationToken)
    {
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
            return clsLecturaExcelAcotada.Invalido(clsLecturaExcelAcotada.MotivoLibroIlegible);
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
                return clsLecturaExcelAcotada.Invalido(clsLecturaExcelAcotada.MotivoLibroIlegible);
            }
        }
    }

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
