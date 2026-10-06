namespace Cotifly.Aplicacion.Pdf.DTOs;

/// <summary>Variante del layout: tarifa única (<c>armar_cotizacion.py</c>) o multi-sección (<c>armar_multi.py</c>).</summary>
public enum VarianteDocumento
{
    TarifaUnica,
    MultiSeccion,

    /// <summary>
    /// Pliego de licitación (HU-011.5, ADR-017 §6): el diseño multi-sección (tablas por origen) con un resumen de todas las líneas y
    /// el total antes de las tablas.
    /// </summary>
    Pliego,
}

/// <summary>Resumen del pliego (HU-011.5): una fila por línea con su tarifa ya formateada y el total del pliego.</summary>
public sealed record clsResumenPliegoDocumento(string Titulo, IReadOnlyList<clsFilaPliegoDocumento> Filas, string EtiquetaTotal, string Total, string Nota);

/// <summary>Fila del resumen del pliego: número de línea, ruta, vehículo y peso, y tarifa formateada.</summary>
public sealed record clsFilaPliegoDocumento(string Numero, string Ruta, string VehiculoPeso, string Tarifa);

/// <summary>
/// Documento listo para maquetar: textos ya resueltos (reemplazo del payload o estándar), montos ya formateados, bloques
/// condicionales ya decididos y firma con los datos reales del comercial. Lo arma <c>clsConstructorDocumentoCotizacion</c>
/// (Aplicación, probado sin PDF) y lo maqueta <c>IClsGeneradorPdf</c> (Infraestructura), que no toma decisiones de negocio.
/// Todos los textos van limpios de caracteres de control y se insertan como texto, nunca como marcado.
/// </summary>
public sealed record clsDocumentoCotizacion
{
    public required VarianteDocumento Variante { get; init; }

    /// <summary>Ej. <c>Yumbo, 24 de septiembre de 2026</c>.</summary>
    public required string FechaLarga { get; init; }

    /// <summary>Folio de la cotización (se imprime como referencia, arriba a la derecha).</summary>
    public required string Referencia { get; init; }

    public required string Cliente { get; init; }

    public required IReadOnlyList<string> Contactos { get; init; }

    public required string Asunto { get; init; }

    /// <summary>Párrafos de la introducción; en la variante multi, los que empiezan por <c>·</c> o <c>•</c> son viñetas.</summary>
    public required IReadOnlyList<clsParrafoDocumento> Introduccion { get; init; }

    /// <summary>Clave de la imagen alusiva de la línea (<c>nacional</c>, <c>andino</c>...), o <c>null</c> si la línea no tiene.</summary>
    public string? ImagenLinea { get; init; }

    /// <summary>Frase bajo la imagen de la línea, si la línea tiene.</summary>
    public string? LeyendaImagen { get; init; }

    /// <summary>Ej. <c>TRANSPORTE NACIONAL EN CALIDAD DE EXPORTACIONES:</c> (solo tarifa única).</summary>
    public string? Subtitulo { get; init; }

    public required IReadOnlyList<clsSeccionDocumento> Secciones { get; init; }

    /// <summary>Resumen de las líneas del pliego (solo en <see cref="VarianteDocumento.Pliego"/>).</summary>
    public clsResumenPliegoDocumento? Pliego { get; init; }

    /// <summary>Nota general bajo la tabla (tarifa única). En multi cada sección lleva la suya.</summary>
    public string? NotaGeneral { get; init; }

    /// <summary>Nota de decretos (tiempos logísticos); solo tarifa única.</summary>
    public string? NotaDecreto { get; init; }

    /// <summary>
    /// Nota de la cotización en dólares (HU-012.3): TRM mínima, la aplicada en la cotización (<c>clsTextosEstandar.NotaTrmMinima</c>). Solo en USD (en ambas variantes); en COP
    /// es <c>null</c> y el PDF no trae ninguna aclaración de TRM.
    /// </summary>
    public string? NotaMoneda { get; init; }

    /// <summary>Datos aduaneros, mercancía extradimensionada o refrigerada ("Detalle adicional del servicio").</summary>
    public required IReadOnlyList<clsBloqueDocumento> BloquesAdicionales { get; init; }

    public required IReadOnlyList<string> Incluye { get; init; }

    public required IReadOnlyList<string> NoIncluye { get; init; }

    public required string Facturacion { get; init; }

    /// <summary>Párrafos del texto de seguro.</summary>
    public required IReadOnlyList<string> Seguro { get; init; }

    public string? DiasLibres { get; init; }

    /// <summary><c>Horas logísticas adicionales</c> (tarifa única) o <c>Stand by</c> (multi).</summary>
    public required string TituloStandBy { get; init; }

    public required string StandBy { get; init; }

    public required string Vigencia { get; init; }

    public required clsFraseFinalDocumento FraseFinal { get; init; }

    public required clsFirmaDocumento Firma { get; init; }
}

/// <summary>Párrafo de la introducción.</summary>
public sealed record clsParrafoDocumento(string Texto, bool EsVineta = false);

/// <summary>Tabla de tarifa: título (franja verde), origen (franja azul) y filas.</summary>
public sealed record clsSeccionDocumento(string Titulo, string Origen, IReadOnlyList<clsFilaDocumento> Filas, string? NotaGeneral = null);

/// <summary>Fila de la tabla con la tarifa ya formateada (<c>$3.000.000</c>, <c>US$1.200</c> o <c>—</c>).</summary>
public sealed record clsFilaDocumento(string Destino, string VehiculoPeso, string Tarifa, string Nota);

/// <summary>Bloque clave-valor del "Detalle adicional del servicio".</summary>
public sealed record clsBloqueDocumento(string Titulo, IReadOnlyList<clsDatoDocumento> Datos);

/// <summary>Par etiqueta-valor de un bloque adicional.</summary>
public sealed record clsDatoDocumento(string Etiqueta, string Valor);

/// <summary>Frase de cierre centrada; <see cref="Enlace"/> se imprime como hipervínculo en la línea siguiente.</summary>
public sealed record clsFraseFinalDocumento(string Texto, string? Enlace);

/// <summary>Firma del comercial. <see cref="Celular"/> sale de <c>UsuariosWeb.telefono</c>; si es nulo no se imprime "Cel".</summary>
public sealed record clsFirmaDocumento(string Nombre, string? Cargo, string? Email, string? Celular);
