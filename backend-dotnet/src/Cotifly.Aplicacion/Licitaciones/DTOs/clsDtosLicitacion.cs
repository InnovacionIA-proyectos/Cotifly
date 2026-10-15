using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Licitaciones.DTOs;

/// <summary>
/// <c>AnalisisExcelDto</c> de Nexo (<c>POST /api/captura-solicitudes/analizar-excel</c>, HU-011.1): las hojas del Excel del cliente
/// con su fila de encabezado, los encabezados y las filas con datos como texto, para que el comercial mapee las columnas. No se guarda.
/// </summary>
/// <remarks>
/// HU-023.5 (#160): <paramref name="CatalogoMercanciasDisponible"/> dice si se pudo leer el catálogo de tipos de mercancía del cubicador
/// para sugerir el <c>codigoMercancia</c> de cada fila (<see cref="clsHojaExcel.Mercancias"/>); <c>false</c> sin cubicador o si no
/// respondió (entonces solo se usa la columna de código del Excel, si la trae).
/// </remarks>
public sealed record clsAnalisisExcel(string Archivo, IReadOnlyList<clsHojaExcel> Hojas, bool CatalogoMercanciasDisponible = false);

/// <summary>
/// Una hoja analizada. <paramref name="Filas"/> trae hasta 1000 filas; <paramref name="TotalFilas"/>, el total real.
/// <paramref name="FilasExcel"/> (HU-013.15, #124): el número de fila de Excel (base 1, encabezado incluido) de cada elemento de
/// <paramref name="Filas"/>, el que ve el comercial en su archivo; lo calcula Cotifly con la misma regla de Nexo. <c>null</c> si no se
/// pudo ubicar (la hoja no cuadra con el análisis de Nexo).
/// </summary>
/// <remarks>
/// HU-023.5 (#160): <paramref name="ColumnaMercancia"/> y <paramref name="ColumnaCodigoMercancia"/> son los encabezados que Cotifly
/// reconoció como la descripción de la mercancía y como su código (<c>null</c> si no hay); <paramref name="Mercancias"/> trae, por cada
/// fila con mercancía, el código sugerido (<see cref="clsCodigoMercanciaFila"/>). <c>null</c> si la hoja no tiene ninguna de esas columnas.
/// </remarks>
public sealed record clsHojaExcel(
    string Nombre,
    int FilaEncabezado,
    IReadOnlyList<string> Encabezados,
    IReadOnlyList<IReadOnlyList<string>> Filas,
    int TotalFilas,
    IReadOnlyList<int>? FilasExcel = null,
    string? ColumnaMercancia = null,
    string? ColumnaCodigoMercancia = null,
    IReadOnlyList<clsCodigoMercanciaFila>? Mercancias = null);

/// <summary>
/// HU-023.5 (#160): el código de mercancía de una fila del análisis. <paramref name="Fila"/> es el índice en <c>hojas[h].filas</c>
/// (base 0, el mismo <c>fila</c> de <c>lineas[]</c>), <paramref name="Texto"/> lo que dice la columna de la mercancía.
/// <list type="bullet">
/// <item>Con la columna de código del Excel: <paramref name="CodigoMercancia"/> es ese valor tal cual, <paramref name="Origen"/> =
/// <c>excel</c> y <paramref name="Sugerido"/> = <c>false</c> (con el nombre del catálogo, si el código está en él).</item>
/// <item>Si no: el código más parecido del catálogo (<c>clsSugeridorMercancia</c>, compartido con la #157), <paramref name="Origen"/> = <c>parecido</c> y
/// <paramref name="Sugerido"/> = <c>true</c>, con su <paramref name="Similitud"/> (0 a 1). El comercial lo confirma o lo cambia.</item>
/// <item>Sin nada parecido (o sin catálogo): <paramref name="CodigoMercancia"/> vacío y <paramref name="Sugerido"/> = <c>false</c>.</item>
/// </list>
/// </summary>
public sealed record clsCodigoMercanciaFila(
    int Fila,
    string? Texto,
    string? CodigoMercancia,
    string? NombreMercancia,
    bool Sugerido,
    string? Origen,
    decimal? Similitud)
{
    /// <summary>El código viene de la columna de código del Excel.</summary>
    public const string OrigenExcel = "excel";

    /// <summary>El código es el más parecido del catálogo del cubicador.</summary>
    public const string OrigenParecido = "parecido";
}

/// <summary>Archivo subido por el comercial (multipart): nombre y bytes.</summary>
public sealed record clsArchivoSubido(string? Nombre, byte[] Contenido);

/// <summary>
/// Una ruta de la captura (fila de una licitación o ruta del formulario, HU-021.6): los campos de <c>CapturaLineaRequest</c>, la fila
/// del Excel de donde salió (<c>null</c> en el formulario), su línea de negocio (<c>null</c> = la del encabezado) y sus servicios
/// conexos del catálogo de Nexo (id y cantidad; el precio se toma del catálogo, decisión 3).
/// </summary>
public sealed record clsLineaLicitacionEntrada(
    int? Fila,
    clsLineaSolicitudCosteo Linea,
    string? LineaNegocio = null,
    IReadOnlyList<clsConexoCapturaEntrada?>? Conexos = null);

/// <summary>Un servicio conexo pedido en la captura de una ruta (HU-021.6, decisión 3): el id del catálogo de Nexo y la cantidad.</summary>
public sealed record clsConexoCapturaEntrada(string? IdServicioConexo, decimal? Cantidad);

/// <summary>
/// Entrada de <c>POST /web/licitaciones</c> (HU-011.2, ADR-017): el cliente (NIT del buscador), la línea de negocio de Cotifly, el
/// encabezado (siempre <c>licitacion</c>), la hoja mapeada, las líneas ya mapeadas, opcionalmente el Excel original y la moneda
/// (<c>COP</c> por defecto o <c>USD</c>, ADR-014 §5: en USD los precios se convierten con la TRM del día de creación).
/// </summary>
public sealed record clsEntradaLicitacion(
    string? NitCliente,
    string? ClienteNombre,
    string? LineaNegocio,
    clsEncabezadoSolicitudCosteo Encabezado,
    string? Hoja,
    IReadOnlyList<clsLineaLicitacionEntrada> Lineas,
    clsArchivoSubido? Archivo,
    string? Moneda = null);

/// <summary>
/// Un error del reporte de validación (<c>ErrorFilaLicitacion</c>): <paramref name="Indice"/> es la posición en <c>lineas[]</c>
/// (base 0; <c>null</c> si el error es del encabezado), <paramref name="Fila"/> la fila del análisis que mandó el frontend (índice
/// base 0 en <c>hojas[h].filas</c>, técnico) y <paramref name="FilaExcel"/> (HU-013.15, #124) el número de fila de la hoja de Excel
/// (base 1, encabezado incluido), el que ve el comercial: solo con el archivo adjunto y una <c>fila</c> que existe en la hoja.
/// </summary>
public sealed record clsErrorFilaLicitacion(int? Indice, int? Fila, string Campo, string Codigo, string Mensaje, int? FilaExcel = null);

/// <summary>
/// Resultado de crear una licitación: la cotización creada, o un error de negocio (<see cref="Error"/>), o el reporte por fila
/// (<see cref="Errores"/>, con <see cref="Error"/> = <c>LICITACION_INVALIDA</c>). Nada queda guardado si hay error.
/// </summary>
public sealed record clsResultadoLicitacion(clsCotizacionSolicitud? Cotizacion, Dominio.Comun.clsErrorDominio? Error, IReadOnlyList<clsErrorFilaLicitacion> Errores)
{
    public static clsResultadoLicitacion Exito(clsCotizacionSolicitud cotizacion) => new(cotizacion, null, []);

    public static clsResultadoLicitacion Fallo(Dominio.Comun.clsErrorDominio error) => new(null, error, []);

    /// <summary>
    /// Reporte por fila. <paramref name="catalogoNoDisponible"/> (HU-013.11) es el código del catálogo que no respondió
    /// (<c>CATALOGOS_FLETE_NO_DISPONIBLE</c> o <c>MUNICIPIOS_NO_DISPONIBLE</c>): va en <c>error.detalle.motivo</c> y el mensaje avisa
    /// que lo que dependía de él se valida al volver a enviarla.
    /// </summary>
    public static clsResultadoLicitacion Invalida(IReadOnlyList<clsErrorFilaLicitacion> errores, string? catalogoNoDisponible = null) => new(
        null,
        new Dominio.Comun.clsErrorDominio(Dominio.Comun.clsCodigosError.LicitacionInvalida,
            $"La licitación tiene {errores.Count} error(es): corrige las filas indicadas y vuelve a enviarla."
            + (catalogoNoDisponible is null
                ? string.Empty
                : " Un catálogo de Nexo no respondió: lo que depende de él (tipo de vehículo, clase de viaje o municipios) se validará al volver a enviarla."),
            catalogoNoDisponible),
        errores);
}

/// <summary>
/// <c>Licitacion</c>: la vista del comercial de una cotización de varias líneas (estado y precios; nunca el costo de Nexo). Sin
/// <c>referencia</c> desde #138 (decisión 1 de HU-021: la cabecera ya no la guarda).
/// </summary>
/// <remarks>
/// HU-023.5 (#160): <c>ProgresoCubicacion</c> resume la cubicación en segundo plano (<c>null</c> si ninguna ruta pasó por la cola) y
/// <c>Cubicacion</c> trae, por ruta cubicada, el resultado y los avisos del cubicador (la misma forma de <c>POST /web/solicitudes-costeo</c>).
/// </remarks>
public sealed record clsLicitacion(
    string Folio,
    string? ClienteNombre,
    string? NitCliente,
    DateTimeOffset? Deadline,
    string? EstadoCosteo,
    string? MotivoEstadoCosteo,
    string Moneda,
    decimal TrmAplicada,
    decimal? PrecioFinal,
    int LineasCosteadas,
    int LineasPendientes,
    string? ArchivoNombre,
    string? Hoja,
    IReadOnlyList<clsLineaLicitacion> Lineas,
    clsProgresoCubicacion? ProgresoCubicacion = null,
    IReadOnlyList<Cotizaciones.DTOs.clsCubicacionRutaResumen>? Cubicacion = null);

/// <summary>
/// HU-023.5 (#160): progreso de la cubicación en segundo plano de una licitación. <paramref name="Estado"/>: <c>en_curso</c> (quedan
/// rutas pendientes), <c>con_fallas</c> (no quedan pendientes pero alguna falló: la licitación no viaja a Nexo hasta reintentarla) o
/// <c>completa</c>.
/// </summary>
public sealed record clsProgresoCubicacion(string Estado, int Total, int Cubicadas, int Pendientes, int Fallidas)
{
    public const string EnCurso = "en_curso";
    public const string ConFallas = "con_fallas";
    public const string Completa = "completa";

    /// <summary>El progreso de <paramref name="cotizacion"/>, o <c>null</c> si ninguna ruta pasó por la cola.</summary>
    public static clsProgresoCubicacion? De(clsCotizacionSolicitud cotizacion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        var rutas = cotizacion.Rutas;
        if (!rutas.Any(r => r.EstadoCubicacion is not null))
        {
            return null;
        }

        var pendientes = rutas.Count(r => r.EstadoCubicacion == EstadoCubicacion.Pendiente);
        var fallidas = rutas.Count(r => r.EstadoCubicacion == EstadoCubicacion.Fallida);
        return new clsProgresoCubicacion(
            pendientes > 0 ? EnCurso : fallidas > 0 ? ConFallas : Completa,
            rutas.Count,
            rutas.Count - pendientes - fallidas,
            pendientes,
            fallidas);
    }
}

/// <summary>
/// HU-023.5 (#160): un camión de una ruta de la licitación, en su orden: códigos de vehículo y carrocería del cubicador (vacíos = sin
/// vehículo, lo resuelve la Mesa de Costeo), el peso que lleva, el volumen y los dos porcentajes de llenado.
/// </summary>
public sealed record clsCamionLicitacion(
    int Orden,
    string? TipoVehiculo,
    string? Carroceria,
    decimal? PesoKg,
    decimal? VolumenTotalM3,
    decimal? PorcentajeLlenadoVolumen,
    decimal? PorcentajeLlenadoPeso)
{
    /// <summary>Los camiones de la ruta, en su orden.</summary>
    public static IReadOnlyList<clsCamionLicitacion> De(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return [.. ruta.VehiculosOrdenados.Select((v, i) => new clsCamionLicitacion(
            i + 1, v.TipoVehiculo, v.Carroceria, v.PesoKg, v.VolumenTotalM3, v.PorcentajeLlenadoVolumen, v.PorcentajeLlenadoPeso))];
    }
}

/// <summary>
/// Datos de la exportación del Excel de una licitación (HU-011.3, ADR-017 §5): solo precios con margen y estados, nunca el costo de
/// Nexo. <see cref="Original"/> es el Excel del cliente (o <c>null</c> si la licitación se armó sin archivo).
/// </summary>
public sealed record clsExportacionLicitacion(
    string Folio,
    string? Cliente,
    string? NitCliente,
    string Moneda,
    decimal TrmAplicada,
    decimal? PrecioTotal,
    string? Hoja,
    byte[]? Original,
    IReadOnlyList<clsLineaExportacion> Lineas,
    IReadOnlyList<clsConexoExportacion> ServiciosConexos,
    DateTimeOffset GeneradoEn);

/// <summary>
/// Una línea exportada: dónde va (fila del análisis), qué es, su estado legible y su precio (<c>null</c> = sin costeo). HU-023.5 (#160):
/// con sus camiones (<paramref name="Camiones"/>, con los nombres del catálogo del cubicador si respondió) y el estado de su cubicación
/// (<paramref name="Cubicacion"/>, legible; <c>null</c> si no pasó por la cubicación en segundo plano).
/// </summary>
public sealed record clsLineaExportacion(
    int Orden,
    int? Fila,
    string Origen,
    string Destino,
    string? TipoVehiculo,
    decimal? PesoKg,
    string Estado,
    decimal? PrecioFinal,
    IReadOnlyList<clsCamionExportacion>? Camiones = null,
    string? Cubicacion = null);

/// <summary>
/// HU-023.5 (#160): un camión exportado. <paramref name="Vehiculo"/> y <paramref name="Carroceria"/> son el nombre del catálogo del
/// cubicador (o el código si el catálogo no respondió); vacíos = sin vehículo (lo resuelve la Mesa de Costeo de Nexo).
/// </summary>
public sealed record clsCamionExportacion(
    int Orden,
    string? Vehiculo,
    string? Carroceria,
    decimal? PesoKg,
    decimal? VolumenTotalM3,
    decimal? PorcentajeLlenadoVolumen,
    decimal? PorcentajeLlenadoPeso)
{
    /// <summary>Texto del camión sin vehículo.</summary>
    public const string SinVehiculo = "Sin vehículo (Mesa de Costeo)";

    /// <summary>Resumen para una celda: <c>"Tractocamión 3S3 / Furgón · 16.000 kg · 81,25 % vol."</c> (o <see cref="SinVehiculo"/>).</summary>
    public string Resumen()
    {
        var cultura = System.Globalization.CultureInfo.GetCultureInfo("es-CO");
        var partes = new List<string>
        {
            Vehiculo is null && Carroceria is null ? SinVehiculo : string.Join(" / ", new[] { Vehiculo, Carroceria }.OfType<string>()),
        };
        if (PesoKg is { } peso)
        {
            partes.Add(peso.ToString("#,##0.##", cultura) + " kg");
        }

        if (PorcentajeLlenadoVolumen is { } volumen)
        {
            partes.Add(volumen.ToString("0.##", cultura) + " % vol.");
        }

        return string.Join(" · ", partes);
    }
}

/// <summary>Servicio conexo incluido, con su precio con margen.</summary>
public sealed record clsConexoExportacion(string Nombre, string Categoria, decimal Cantidad, decimal Precio);

/// <summary>Archivo para descargar.</summary>
public sealed record clsArchivoExportado(string Nombre, byte[] Contenido, string TipoContenido);

/// <summary><c>LineaLicitacion</c>: una línea con su estado y su precio con margen (<c>null</c> mientras no tiene flete).</summary>
/// <remarks>
/// HU-023.5 (#160): con sus camiones (<paramref name="Camiones"/>), el estado de su cubicación en segundo plano
/// (<paramref name="EstadoCubicacion"/>: <c>pendiente</c>, <c>cubicada</c>, <c>fallida</c>, o <c>null</c> si se cubicó al guardarla), el
/// error si falló y lo que respondió el cubicador.
/// </remarks>
public sealed record clsLineaLicitacion(
    int Orden,
    int? Fila,
    string Origen,
    string Destino,
    string? TipoVehiculo,
    decimal? PesoKg,
    string? EstadoCosteo,
    decimal? PrecioFinal,
    string? EstadoCubicacion = null,
    string? ErrorCubicacion = null,
    string? ResultadoCubicacion = null,
    string? MotivoSinSeleccion = null,
    bool Condicionada = false,
    IReadOnlyList<clsCamionLicitacion>? Camiones = null);
