using System.Globalization;
using ClosedXML.Excel;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Aplicacion.Cotizaciones.Interfaces;

namespace Cotifly.Infraestructura.Cotizaciones.Services;

/// <summary>
/// HU-018.1 (#155, parte C): el Excel de una cotización con ClosedXML, con los estilos de la exportación de la licitación
/// (<see cref="Licitaciones.Services.clsExportadorExcelLicitacionClosedXml"/>: encabezado en negrita con fondo, pesos sin decimales y
/// dólares con dos). Cuatro hojas: <see cref="HojaResumen"/> (cabecera y totales), <see cref="HojaRutas"/> (una fila por ruta),
/// <see cref="HojaCamiones"/> (una fila por camión) y <see cref="HojaConexos"/> (una fila por servicio conexo).
/// <para>Las columnas de costo (siempre en COP, el costeo de Nexo) y de margen solo se escriben en la versión interna (la del dueño). Con
/// <see cref="clsExcelCotizacion.SoloLectura"/> no se escriben ni sus encabezados: el gerente y el director no reciben ninguna columna de costo
/// ni de margen. Solo escribe: no abre ningún archivo, así que no usa el cupo de lecturas de Excel.</para>
/// </summary>
public sealed class clsExportadorExcelCotizacionClosedXml : IClsExportadorExcelCotizacion
{
    public const string HojaResumen = "Resumen";
    public const string HojaRutas = "Rutas";
    public const string HojaCamiones = "Camiones";
    public const string HojaConexos = "Conexos";

    /// <summary>Texto de un precio que no hay (ruta rechazada, sin costeo o camión que no cuenta).</summary>
    public const string SinPrecio = "Sin precio";

    /// <summary>HU-045.3 (#265): columna (hoja Rutas) y dato (Resumen) del seguro de la mercancía, con la moneda.</summary>
    public const string EtiquetaSeguro = "Seguro de la mercancía";

    /// <summary>HU-045.3 (#265): la ruta tiene el seguro marcado sin valor de la mercancía (no suma en su precio).</summary>
    public const string SeguroSinValorMercancia = "Sin valor de la mercancía";

    public const string VersionInterna = "Interna (con costos y márgenes)";
    public const string VersionSoloLectura = "Solo lectura (sin costos ni márgenes)";

    private const string FormatoPesos = "#,##0";
    private const string FormatoMargen = "0.00%";
    private const string FormatoPeso = "#,##0.##";
    private const string FormatoCantidad = "#,##0.##";

    private static readonly XLColor _encabezado = XLColor.FromHtml("#E8EEF7");
    private static readonly XLColor _interno = XLColor.FromHtml("#FCE4D6");
    private static readonly XLColor _pendiente = XLColor.FromHtml("#FFF2CC");

    /// <summary>Formato numérico de la moneda (ADR-014): pesos sin decimales, dólares con dos.</summary>
    public static string Formato(string moneda) => Licitaciones.Services.clsExportadorExcelLicitacionClosedXml.Formato(moneda);

    /// <summary>Encabezado de una columna de precio, con la moneda (<c>Precio ruta (COP)</c>).</summary>
    public static string Precio(string etiqueta, string moneda) => $"{etiqueta} ({moneda})";

    /// <summary>Encabezado de una columna de costo: siempre en COP (el costeo de Nexo nunca se convierte).</summary>
    public static string Costo(string etiqueta) => $"{etiqueta} (COP)";

    public byte[] Generar(clsExcelCotizacion datos)
    {
        ArgumentNullException.ThrowIfNull(datos);
        using var libro = new XLWorkbook();
        EscribirResumen(libro.Worksheets.Add(HojaResumen), datos);
        EscribirRutas(libro.Worksheets.Add(HojaRutas), datos);
        EscribirCamiones(libro.Worksheets.Add(HojaCamiones), datos);
        EscribirConexos(libro.Worksheets.Add(HojaConexos), datos);
        using var memoria = new MemoryStream();
        libro.SaveAs(memoria);
        return memoria.ToArray();
    }

    /// <summary>¿Se escriben las columnas internas? Solo en la versión del dueño (y nunca si faltan los datos internos).</summary>
    private static bool Interna(clsExcelCotizacion datos) => !datos.SoloLectura;

    private static void EscribirResumen(IXLWorksheet hoja, clsExcelCotizacion datos)
    {
        var formato = Formato(datos.Moneda);
        var f = 1;
        hoja.Cell(f, 1).Value = $"Cotización {datos.Folio} - Botero Soto Soluciones Logísticas (Cotifly)";
        hoja.Cell(f, 1).Style.Font.SetBold().Font.SetFontSize(13);
        f += 2;

        void Dato(string etiqueta, XLCellValue valor, string? formatoNumero = null, bool interno = false)
        {
            hoja.Cell(f, 1).Value = etiqueta;
            hoja.Cell(f, 1).Style.Font.SetBold();
            hoja.Cell(f, 2).Value = valor;
            if (formatoNumero is not null)
            {
                hoja.Cell(f, 2).Style.NumberFormat.Format = formatoNumero;
            }

            if (interno)
            {
                hoja.Range(f, 1, f, 2).Style.Fill.SetBackgroundColor(_interno);
            }

            f++;
        }

        Dato("Folio", datos.Folio);
        Dato("Cliente", datos.Cliente ?? "—");
        Dato("NIT", datos.NitCliente ?? "—");
        Dato("Comercial", datos.Comercial ?? "—");
        Dato("Tipo", datos.Tipo ?? "—");
        Dato("Moneda", datos.Moneda);
        if (!string.Equals(datos.Moneda, "COP", StringComparison.OrdinalIgnoreCase) && datos.TrmAplicada > 0m)
        {
            Dato("TRM aplicada", datos.TrmAplicada, "#,##0.00");
        }

        Dato("Estado del costeo", datos.EstadoCosteo ?? "—");
        Dato("Resultado", datos.Resultado ?? "Sin resultado");
        Dato("Finalizada", datos.FinalizadaAt is { } finalizada ? Fecha(finalizada) : "No");
        Dato("Precio congelado", datos.PrecioCongelado ? "Sí" : "No");
        Dato("Versión del archivo", datos.SoloLectura ? VersionSoloLectura : VersionInterna);
        f++;

        var totales = datos.Totales;
        Dato("Rutas", totales.Rutas);
        Dato("Camiones", totales.Camiones);
        Dato("Rutas rechazadas en NEXO (no suman)", totales.RutasRechazadas);
        Dato("Peso total (kg)", totales.PesoKg is { } peso ? peso : "—", FormatoPeso);
        DatoPrecio(Precio("Precio transporte", datos.Moneda), totales.PrecioTransporte);
        DatoPrecio(Precio("Precio conexos", datos.Moneda), totales.PrecioConexos);
        DatoPrecio(Precio(EtiquetaSeguro, datos.Moneda), totales.PrecioSeguro);
        DatoPrecio(Precio("Precio final", datos.Moneda), totales.PrecioFinal);
        if (Interna(datos) && totales.Interno is { } interno)
        {
            Dato(Costo("Costo bruto total"), interno.CostoBruto is { } costo ? costo : "—", FormatoPesos, interno: true);
        }

        Dato("Generado", Fecha(datos.GeneradoEn));
        hoja.Columns().AdjustToContents();

        void DatoPrecio(string etiqueta, decimal? valor)
        {
            if (valor is { } numero)
            {
                Dato(etiqueta, numero, ConCentavos(numero, formato));
            }
            else
            {
                Dato(etiqueta, SinPrecio);
                hoja.Cell(f - 1, 2).Style.Fill.SetBackgroundColor(_pendiente);
            }
        }
    }

    private static void EscribirRutas(IXLWorksheet hoja, clsExcelCotizacion datos)
    {
        var interna = Interna(datos);
        var formato = Formato(datos.Moneda);
        List<string> campos = ["Ruta", "Origen", "Destino", "Estado", "Motivo de rechazo", "Peso (kg)", "Camiones"];
        var inicioInterno = campos.Count + 1;
        if (interna)
        {
            campos.AddRange([
                Costo("Costo fletes"), Costo("Costo retorno"), Costo("Costo levante"), Costo("Costo conexos"), Costo("Costo bruto"),
                "Margen aplicado", "Margen sugerido", "Origen del margen", "Método de cálculo",
            ]);
        }

        var finInterno = campos.Count;
        // HU-045.3 (#265): el seguro de la mercancía en su columna, aparte del transporte y de los conexos (NOGE0076).
        campos.AddRange([
            Precio("Precio transporte", datos.Moneda), Precio("Precio conexos", datos.Moneda), Precio(EtiquetaSeguro, datos.Moneda),
            Precio("Precio ruta", datos.Moneda),
        ]);
        Encabezados(hoja, campos, interna ? (inicioInterno, finInterno) : null);

        var fila = 2;
        foreach (var ruta in datos.Rutas)
        {
            var c = 1;
            hoja.Cell(fila, c++).Value = ruta.Orden;
            hoja.Cell(fila, c++).Value = ruta.Origen;
            hoja.Cell(fila, c++).Value = ruta.Destino;
            hoja.Cell(fila, c++).Value = ruta.Estado;
            hoja.Cell(fila, c++).Value = ruta.MotivoRechazo ?? string.Empty;
            Numero(hoja.Cell(fila, c++), ruta.PesoKg, FormatoPeso);
            hoja.Cell(fila, c++).Value = ruta.CamionesQueCuentan;
            if (interna)
            {
                var interno = ruta.Interno;
                Numero(hoja.Cell(fila, c++), interno?.CostoFletes, FormatoPesos);
                Numero(hoja.Cell(fila, c++), interno?.CostoRetorno, FormatoPesos);
                Numero(hoja.Cell(fila, c++), interno?.CostoLevante, FormatoPesos);
                Numero(hoja.Cell(fila, c++), interno?.CostoConexos, FormatoPesos);
                Numero(hoja.Cell(fila, c++), interno?.CostoBruto, FormatoPesos);
                Numero(hoja.Cell(fila, c++), interno?.MargenAplicado, FormatoMargen);
                Numero(hoja.Cell(fila, c++), interno?.MargenSugerido, FormatoMargen);
                hoja.Cell(fila, c++).Value = interno?.OrigenMargen ?? string.Empty;
                hoja.Cell(fila, c++).Value = interno?.MetodoCalculo ?? string.Empty;
            }

            PrecioCelda(hoja.Cell(fila, c++), ruta.PrecioTransporte, formato);
            PrecioCelda(hoja.Cell(fila, c++), ruta.PrecioConexos, formato);
            SeguroCelda(hoja.Cell(fila, c++), ruta, formato);
            PrecioCelda(hoja.Cell(fila, c), ruta.PrecioRuta, formato);
            fila++;
        }

        // Fila de totales (sin las rutas rechazadas, ADR-017 §3).
        hoja.Cell(fila, 1).Value = "Total";
        hoja.Row(fila).Style.Font.SetBold();
        var columnaPrecio = campos.Count - 3;
        if (interna)
        {
            // "Costo bruto" es la quinta columna interna.
            Numero(hoja.Cell(fila, inicioInterno + 4), datos.Totales.Interno?.CostoBruto, FormatoPesos);
        }

        PrecioCelda(hoja.Cell(fila, columnaPrecio), datos.Totales.PrecioTransporte, formato);
        PrecioCelda(hoja.Cell(fila, columnaPrecio + 1), datos.Totales.PrecioConexos, formato);
        PrecioCelda(hoja.Cell(fila, columnaPrecio + 2), datos.Totales.PrecioSeguro, formato);
        PrecioCelda(hoja.Cell(fila, columnaPrecio + 3), datos.Totales.PrecioFinal, formato);
        Terminar(hoja);
    }

    private static void EscribirCamiones(IXLWorksheet hoja, clsExcelCotizacion datos)
    {
        var interna = Interna(datos);
        var formato = Formato(datos.Moneda);
        List<string> campos = ["Ruta", "Origen → destino", "Camión", "Vehículo", "Carrocería", "Peso (kg)", "Estado", "Cuenta en el precio"];
        var inicioInterno = campos.Count + 1;
        if (interna)
        {
            campos.AddRange([Costo("Costo flete"), Costo("Costo retorno"), Costo("Costo levante"), Costo("Costo camión"), "Margen"]);
        }

        var finInterno = campos.Count;
        campos.Add(Precio("Precio transporte", datos.Moneda));
        Encabezados(hoja, campos, interna ? (inicioInterno, finInterno) : null);

        var fila = 2;
        foreach (var ruta in datos.Rutas)
        {
            foreach (var camion in ruta.Camiones)
            {
                var c = 1;
                hoja.Cell(fila, c++).Value = ruta.Orden;
                hoja.Cell(fila, c++).Value = $"{ruta.Origen} → {ruta.Destino}";
                hoja.Cell(fila, c++).Value = camion.Numero;
                hoja.Cell(fila, c++).Value = camion.Vehiculo ?? (camion.Carroceria is null ? "Sin vehículo (Mesa de Costeo)" : string.Empty);
                hoja.Cell(fila, c++).Value = camion.Carroceria ?? string.Empty;
                Numero(hoja.Cell(fila, c++), camion.PesoKg, FormatoPeso);
                hoja.Cell(fila, c++).Value = camion.Estado;
                hoja.Cell(fila, c++).Value = camion.Cuenta ? "Sí" : "No";
                if (interna)
                {
                    var interno = camion.Interno;
                    Numero(hoja.Cell(fila, c++), interno?.CostoFlete, FormatoPesos);
                    Numero(hoja.Cell(fila, c++), interno?.CostoRetorno, FormatoPesos);
                    Numero(hoja.Cell(fila, c++), interno?.CostoLevante, FormatoPesos);
                    Numero(hoja.Cell(fila, c++), interno?.CostoTotal, FormatoPesos);
                    Numero(hoja.Cell(fila, c++), interno?.Margen, FormatoMargen);
                }

                PrecioCelda(hoja.Cell(fila, c), camion.PrecioTransporte, formato);
                fila++;
            }
        }

        Terminar(hoja);
    }

    private static void EscribirConexos(IXLWorksheet hoja, clsExcelCotizacion datos)
    {
        var interna = Interna(datos);
        var formato = Formato(datos.Moneda);
        List<string> campos = ["Ruta", "Origen → destino", "Servicio", "Categoría", "Cantidad"];
        var inicioInterno = campos.Count + 1;
        if (interna)
        {
            campos.Add(Costo("Costo"));
        }

        var finInterno = campos.Count;
        campos.Add(Precio("Precio", datos.Moneda));
        Encabezados(hoja, campos, interna ? (inicioInterno, finInterno) : null);

        var fila = 2;
        foreach (var ruta in datos.Rutas)
        {
            foreach (var conexo in ruta.Conexos)
            {
                var c = 1;
                hoja.Cell(fila, c++).Value = ruta.Orden;
                hoja.Cell(fila, c++).Value = $"{ruta.Origen} → {ruta.Destino}";
                hoja.Cell(fila, c++).Value = conexo.Nombre;
                hoja.Cell(fila, c++).Value = conexo.Categoria;
                Numero(hoja.Cell(fila, c++), conexo.Cantidad, FormatoCantidad);
                if (interna)
                {
                    Numero(hoja.Cell(fila, c++), conexo.Interno?.Costo, FormatoPesos);
                }

                PrecioCelda(hoja.Cell(fila, c), conexo.Precio, formato);
                fila++;
            }
        }

        if (fila == 2)
        {
            hoja.Cell(fila, 1).Value = "Sin servicios conexos";
        }

        Terminar(hoja);
    }

    private static void Encabezados(IXLWorksheet hoja, IReadOnlyList<string> campos, (int Desde, int Hasta)? interno)
    {
        for (var c = 0; c < campos.Count; c++)
        {
            hoja.Cell(1, c + 1).Value = campos[c];
        }

        hoja.Range(1, 1, 1, campos.Count).Style.Font.SetBold().Fill.SetBackgroundColor(_encabezado);
        if (interno is { } rango && rango.Hasta >= rango.Desde)
        {
            // Las columnas internas del dueño se distinguen a la vista (no se comparten con el cliente).
            hoja.Range(1, rango.Desde, 1, rango.Hasta).Style.Fill.SetBackgroundColor(_interno);
        }
    }

    private static void Terminar(IXLWorksheet hoja)
    {
        hoja.SheetView.FreezeRows(1);
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

    private static void PrecioCelda(IXLCell celda, decimal? valor, string formato)
    {
        if (valor is { } numero)
        {
            celda.Value = numero;
            celda.Style.NumberFormat.Format = ConCentavos(numero, formato);
        }
        else
        {
            celda.Value = SinPrecio;
            celda.Style.Fill.SetBackgroundColor(_pendiente);
        }
    }

    /// <summary>
    /// HU-045.3 (#265): el seguro de la ruta; vacía si no lo incluye (o la ruta está rechazada) y <see cref="SeguroSinValorMercancia"/>
    /// resaltada si está marcado sin valor de la mercancía.
    /// </summary>
    private static void SeguroCelda(IXLCell celda, clsExcelRuta ruta, string formato)
    {
        if (ruta.ValorSeguro is { } valor)
        {
            celda.Value = valor;
            celda.Style.NumberFormat.Format = ConCentavos(valor, formato);
        }
        else if (ruta.SeguroSinValor)
        {
            celda.Value = SeguroSinValorMercancia;
            celda.Style.Fill.SetBackgroundColor(_pendiente);
        }
    }

    /// <summary>
    /// HU-045.3 (#265, B1): en pesos (<see cref="FormatoPesos"/>), un monto con centavos (el seguro y lo que lo incluye) se muestra con 2
    /// decimales para que cuadre con lo guardado; sin centavos, en pesos enteros como siempre.
    /// </summary>
    public static string ConCentavos(decimal valor, string formato) =>
        formato == FormatoPesos && Aplicacion.Pdf.Services.clsFormatoMoneda.TieneCentavos(valor) ? "#,##0.00" : formato;

    private static string Fecha(DateTimeOffset fecha) => fecha.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
