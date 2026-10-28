namespace Cotifly.Aplicacion.Cotizaciones.DTOs;

/// <summary>
/// HU-018.1 (#155, parte C): lo que escribe el Excel de una cotización (<c>GET /web/cotizaciones/{folio}/excel</c>). Todo lo interno
/// (costos de Nexo y márgenes) va SOLO en las propiedades <c>Interno</c>: <see cref="SinCostos"/> las pone en <c>null</c> para el gerente
/// y el director en solo lectura, y el exportador no escribe ninguna columna de costo ni de margen sin ellas (con el margen y el precio se
/// deduce el costo). La prueba de arquitectura <c>clsCostoBrutoSinFugasTests</c> exige que ningún campo <c>Costo*</c> ni <c>Margen*</c>
/// quede fuera de un <c>Interno</c>.
/// </summary>
/// <param name="Folio">Folio de la cotización.</param>
/// <param name="Cliente">Nombre del cliente.</param>
/// <param name="NitCliente">NIT del cliente.</param>
/// <param name="Comercial">Nombre del comercial dueño.</param>
/// <param name="Tipo">Tipo de solicitud (<c>spot</c>, <c>licitacion</c>...).</param>
/// <param name="Moneda">Moneda de los precios (<c>COP</c> o <c>USD</c>); los costos de Nexo siempre en COP.</param>
/// <param name="TrmAplicada">TRM fijada al crear (0 en COP).</param>
/// <param name="EstadoCosteo">Estado del costeo de la cabecera.</param>
/// <param name="Resultado"><c>ganada</c>, <c>perdida</c> o <c>null</c>.</param>
/// <param name="FinalizadaAt">Cuándo se finalizó (HU-036): el precio quedó congelado.</param>
/// <param name="PrecioCongelado">El precio no cambia con el costeo de Nexo (cotización finalizada o con PDF, HU-036).</param>
/// <param name="GeneradoEn">Momento de la exportación (UTC).</param>
/// <param name="SoloLectura">Versión del gerente o el director (sin costos ni márgenes).</param>
/// <param name="Rutas">Las rutas en su orden.</param>
/// <param name="Totales">Totales de la cotización.</param>
public sealed record clsExcelCotizacion(
    string Folio,
    string? Cliente,
    string? NitCliente,
    string? Comercial,
    string? Tipo,
    string Moneda,
    decimal TrmAplicada,
    string? EstadoCosteo,
    string? Resultado,
    DateTimeOffset? FinalizadaAt,
    bool PrecioCongelado,
    DateTimeOffset GeneradoEn,
    bool SoloLectura,
    IReadOnlyList<clsExcelRuta> Rutas,
    clsExcelTotales Totales)
{
    /// <summary>La versión del gerente y del director: la misma información sin ningún dato interno (costos y márgenes).</summary>
    public clsExcelCotizacion SinCostos() => this with
    {
        SoloLectura = true,
        Rutas = [.. Rutas.Select(r => r.SinCostos())],
        Totales = Totales with { Interno = null },
    };
}

/// <summary>Una ruta del Excel: lo que ven todos, y en <see cref="Interno"/> sus costos y su margen (solo el dueño).</summary>
/// <param name="Orden">Orden de la ruta (1, 2...).</param>
/// <param name="Origen">Origen (nombre copiado de la ruta).</param>
/// <param name="Destino">Destino.</param>
/// <param name="Estado">Estado del costeo de la ruta, legible.</param>
/// <param name="Rechazada">La Mesa de Costeo la rechazó: sin precio, sin conexos y no suma (HU-028.1).</param>
/// <param name="MotivoRechazo">Por qué la rechazó la Mesa (texto de Nexo).</param>
/// <param name="PesoKg">Peso total declarado de la ruta.</param>
/// <param name="CamionesQueCuentan">Camiones que cuentan en el precio (sin descartados ni rechazados).</param>
/// <param name="PrecioTransporte">Tarifa del transporte de la ruta (sin conexos), en la moneda; la del PDF.</param>
/// <param name="PrecioConexos">Suma de los precios de sus conexos.</param>
/// <param name="PrecioRuta">Precio de la ruta (<c>precioFinalLinea</c>: transporte + conexos; congelado si la cotización está finalizada).</param>
/// <param name="Camiones">Sus camiones en orden.</param>
/// <param name="Conexos">Sus servicios conexos con precio.</param>
/// <param name="Interno">Costos de Nexo (COP) y margen de la ruta; <c>null</c> en la versión sin costos.</param>
public sealed record clsExcelRuta(
    int Orden,
    string Origen,
    string Destino,
    string Estado,
    bool Rechazada,
    string? MotivoRechazo,
    decimal? PesoKg,
    int CamionesQueCuentan,
    decimal? PrecioTransporte,
    decimal? PrecioConexos,
    decimal? PrecioRuta,
    IReadOnlyList<clsExcelCamion> Camiones,
    IReadOnlyList<clsExcelConexo> Conexos,
    clsExcelInternoRuta? Interno)
{
    /// <summary>La ruta sin datos internos (ni los de sus camiones y conexos).</summary>
    public clsExcelRuta SinCostos() => this with
    {
        Interno = null,
        Camiones = [.. Camiones.Select(c => c with { Interno = null })],
        Conexos = [.. Conexos.Select(c => c with { Interno = null })],
    };
}

/// <summary>Costos de Nexo de la ruta (siempre en COP, sin margen) y su margen. Solo el dueño.</summary>
/// <param name="CostoFletes">Fletes de los camiones que cuentan.</param>
/// <param name="CostoRetorno">Retorno del contenedor.</param>
/// <param name="CostoLevante">Levante del contenedor.</param>
/// <param name="CostoConexos">Costo de los servicios conexos.</param>
/// <param name="CostoBruto">Costo bruto de la ruta (<c>costoFinal</c>: fletes + retorno + levante + conexos).</param>
/// <param name="MargenAplicado">Margen aplicado (fracción: 0,2 = 20 %).</param>
/// <param name="MargenSugerido">Margen sugerido (SAP TM o política de la línea).</param>
/// <param name="OrigenMargen">De dónde salió el margen.</param>
/// <param name="MetodoCalculo"><c>margen</c> o <c>markup</c>.</param>
public sealed record clsExcelInternoRuta(
    decimal? CostoFletes,
    decimal? CostoRetorno,
    decimal? CostoLevante,
    decimal? CostoConexos,
    decimal? CostoBruto,
    decimal? MargenAplicado,
    decimal? MargenSugerido,
    string? OrigenMargen,
    string? MetodoCalculo);

/// <summary>Un camión de la ruta.</summary>
/// <param name="Numero">Su número en la ruta (1, 2...).</param>
/// <param name="Vehiculo">Tipo de vehículo (código).</param>
/// <param name="Carroceria">Carrocería (código).</param>
/// <param name="PesoKg">Peso de carga del camión.</param>
/// <param name="Estado">Estado del costeo del camión, legible.</param>
/// <param name="Cuenta">Cuenta en el precio (no está descartado ni rechazado).</param>
/// <param name="PrecioTransporte">Precio del transporte del camión (su costo con el margen de la ruta), en la moneda; <c>null</c> si no cuenta.</param>
/// <param name="Interno">Costos de Nexo del camión (COP) y el margen; <c>null</c> en la versión sin costos.</param>
public sealed record clsExcelCamion(
    int Numero,
    string? Vehiculo,
    string? Carroceria,
    decimal? PesoKg,
    string Estado,
    bool Cuenta,
    decimal? PrecioTransporte,
    clsExcelInternoCamion? Interno);

/// <summary>Costos de Nexo de un camión (COP, sin margen) y el margen de su ruta. Solo el dueño.</summary>
/// <param name="CostoFlete">Flete.</param>
/// <param name="CostoRetorno">Retorno del contenedor.</param>
/// <param name="CostoLevante">Levante del contenedor.</param>
/// <param name="CostoTotal">Flete + retorno + levante.</param>
/// <param name="Margen">Margen de la ruta con que se calcula su precio.</param>
public sealed record clsExcelInternoCamion(decimal? CostoFlete, decimal? CostoRetorno, decimal? CostoLevante, decimal? CostoTotal, decimal? Margen);

/// <summary>Un servicio conexo de la ruta con su precio (margen de la ruta).</summary>
/// <param name="Nombre">Nombre del servicio.</param>
/// <param name="Categoria">Categoría del catálogo de Nexo.</param>
/// <param name="Cantidad">Cantidad.</param>
/// <param name="Precio">Precio total del conexo (cantidad incluida), en la moneda.</param>
/// <param name="Interno">Su costo de Nexo (COP); <c>null</c> en la versión sin costos.</param>
public sealed record clsExcelConexo(string Nombre, string Categoria, decimal Cantidad, decimal Precio, clsExcelInternoConexo? Interno);

/// <summary>Costo de Nexo de un conexo (COP, cantidad incluida). Solo el dueño.</summary>
/// <param name="Costo">Costo total del conexo.</param>
public sealed record clsExcelInternoConexo(decimal Costo);

/// <summary>Totales de la cotización (sin las rutas rechazadas en los precios).</summary>
/// <param name="Rutas">Rutas.</param>
/// <param name="Camiones">Camiones que cuentan.</param>
/// <param name="RutasRechazadas">Rutas rechazadas por la Mesa de Costeo.</param>
/// <param name="PesoKg">Peso total.</param>
/// <param name="PrecioTransporte">Suma de las tarifas de transporte; <c>null</c> si alguna ruta no tiene precio.</param>
/// <param name="PrecioConexos">Suma de los conexos.</param>
/// <param name="PrecioFinal">Precio final de la cotización (congelado si está finalizada).</param>
/// <param name="Interno">Costo bruto total (COP); <c>null</c> en la versión sin costos.</param>
public sealed record clsExcelTotales(
    int Rutas,
    int Camiones,
    int RutasRechazadas,
    decimal? PesoKg,
    decimal? PrecioTransporte,
    decimal? PrecioConexos,
    decimal? PrecioFinal,
    clsExcelInternoTotales? Interno);

/// <summary>Costo bruto total de las rutas que cuentan (COP). Solo el dueño.</summary>
/// <param name="CostoBruto">Suma de los costos brutos; <c>null</c> si alguna ruta no lo tiene.</param>
public sealed record clsExcelInternoTotales(decimal? CostoBruto);
