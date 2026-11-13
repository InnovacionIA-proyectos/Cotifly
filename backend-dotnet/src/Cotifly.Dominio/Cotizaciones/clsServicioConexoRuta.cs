namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Servicio conexo de una ruta (<c>dbo_cotifly.CotizacionServiciosConexos</c>, script 0009, ADR-019). Es global a la ruta: la
/// <see cref="Cantidad"/> ya dice cuántos son. <see cref="Precio"/> es el costo
/// unitario de Nexo al aplicarlo (interno, no se recalcula si Nexo lo cambia después); al cliente se cotiza
/// <see cref="PrecioVenta"/>, sin margen. En Nexo los conexos de la ruta viajan pegados a la línea de su primer camión. <see cref="Nombre"/> es una copia del nombre del servicio (script 0010, mismo criterio que
/// el vehículo); vacío en las filas migradas.
/// </summary>
public sealed class clsServicioConexoRuta
{
    public const int LargoMaximoId = 64;
    public const int LargoMaximoNombre = 200;

    /// <summary>Largo de <c>categoriaServicioConexo</c> (script 05).</summary>
    public const int LargoMaximoCategoria = 20;

    /// <summary>Largo de <c>codigoServicio</c> (script 21).</summary>
    public const int LargoMaximoCodigoServicio = 40;

    private clsServicioConexoRuta()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Ruta a la que se aplica.</summary>
    public Guid LineaSolicitudId { get; private set; }

    /// <summary>Id del servicio en Nexo (<c>idServicioConexo</c>): el conexo de la cotización de Nexo (único por ruta).</summary>
    public string IdServicioConexo { get; private set; } = string.Empty;

    /// <summary>Nombre del servicio (copia); <c>null</c> en las filas migradas del JSON.</summary>
    public string? Nombre { get; private set; }

    /// <summary>
    /// El id del CATÁLOGO de Nexo del servicio (<c>idServicioConexoCatalogo</c>, script 03). <see cref="IdServicioConexo"/>
    /// es el del catálogo en un conexo capturado, pero el de la FILA de la cotización de Nexo en uno agregado o importado; este
    /// campo dice siempre el del catálogo, así que no hay que resolverlo por nombre (<c>clsResolutorConexosCatalogo</c>). <c>null</c> en las
    /// filas sin él (se resuelven por nombre) y si Nexo no lo informó.
    /// </summary>
    public string? IdServicioConexoCatalogo { get; private set; }

    /// <summary>
    /// La categoría del servicio en el catálogo de Nexo (<c>categoriaServicioConexo</c>, script 05: <c>logistico</c>,
    /// <c>adicional</c>...), copiada al capturar o importar. <c>null</c> en las filas sin ella: el costeo la lee del catálogo.
    /// </summary>
    public string? Categoria { get; private set; }

    /// <summary>
    /// El código del servicio en el catálogo de Nexo (<c>codigoServicio</c>, script 21; p. ej. <c>AVT_AUTO</c>), con el
    /// que la política de seguros sabe si el conexo cumple una medida (los ids de Nexo son de una fila de tarifa por trayecto, no del
    /// servicio). Se guarda al agregar el conexo; <c>null</c> en las filas sin él o si Nexo no lo informó: el backend las completa
    /// la siguiente vez que lee el catálogo (<see cref="CompletarCodigoServicio"/>).
    /// </summary>
    public string? CodigoServicio { get; private set; }

    /// <summary>Costo unitario de Nexo al aplicarlo (<c>valorAplicado ÷ cantidad</c> o <c>costoUnitario</c> del catálogo). Interno.</summary>
    public decimal Precio { get; private set; }

    /// <summary>
    /// Precio de venta unitario del conexo (<c>precioVenta</c> del script 19): el
    /// <c>precioVentaUnitario</c> de la cotización de Nexo (lo fija Nexo al agregar el conexo) o, en un conexo capturado o agregado en
    /// Cotifly, el <c>precioVenta</c> del catálogo; sin él (Nexo no lo informa o el servicio no tiene precio de venta), el costo
    /// (<see cref="Precio"/>). Es lo que se cotiza al cliente, sin margen.
    /// </summary>
    public decimal PrecioVenta { get; private set; }

    /// <summary>Unidades del servicio (decimal: Nexo maneja la cantidad de los conexos con decimales).</summary>
    public decimal Cantidad { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Costo del conexo en la ruta: <see cref="Precio"/> × <see cref="Cantidad"/>.</summary>
    public decimal Costo => Precio * Cantidad;

    /// <summary>Venta del conexo en la ruta, en pesos y sin margen: <see cref="PrecioVenta"/> × <see cref="Cantidad"/>.</summary>
    public decimal Venta => PrecioVenta * Cantidad;

    /// <summary>
    /// Conexo nuevo de la ruta. <paramref name="precioVenta"/> se guarda a 2 decimales (half-to-even, como el costo);
    /// <c>null</c> = sin precio de venta: se cotiza al costo. Una venta 0 es un conexo sin cobro, no "al costo".
    /// </summary>
    internal static clsServicioConexoRuta Crear(
        Guid lineaSolicitudId, string idServicioConexo, string? nombre, decimal precio, decimal cantidad,
        string? idServicioConexoCatalogo = null, string? categoria = null, decimal? precioVenta = null, string? codigoServicio = null)
    {
        var id = Validar(idServicioConexo, precio, cantidad, precioVenta);
        return new clsServicioConexoRuta
        {
            Id = Guid.NewGuid(),
            LineaSolicitudId = lineaSolicitudId,
            IdServicioConexo = id,
            Nombre = LimpiarNombre(nombre),
            Precio = precio,
            PrecioVenta = VentaRedondeada(precioVenta) ?? precio,
            Cantidad = cantidad,
            IdServicioConexoCatalogo = Acotar(idServicioConexoCatalogo, LargoMaximoId),
            Categoria = Acotar(categoria, LargoMaximoCategoria),
            CodigoServicio = Acotar(codigoServicio, LargoMaximoCodigoServicio),
        };
    }

    /// <summary>
    /// Cambia el precio, la venta, la cantidad o el nombre (el mismo conexo leído otra vez). Devuelve <c>true</c> si cambió alguno.
    /// Si trae el id del catálogo o la categoría y la fila no los tenía, los completa; no
    /// reemplaza uno ya guardado ni cuenta como cambio (la persistencia sí lo guarda). <paramref name="precioVenta"/>
    /// <c>null</c> = no se conoce: se conserva la venta guardada, salvo que fuera el costo por defecto (venta = costo anterior), que sigue
    /// al costo nuevo. Una venta conocida nunca vuelve al costo por una lectura sin venta.
    /// </summary>
    internal bool Actualizar(
        string? nombre, decimal precio, decimal cantidad, string? idServicioConexoCatalogo = null, string? categoria = null, decimal? precioVenta = null,
        string? codigoServicio = null)
    {
        Validar(IdServicioConexo, precio, cantidad, precioVenta);
        var antes = (Nombre, Precio, PrecioVenta, Cantidad);
        Nombre = LimpiarNombre(nombre) ?? Nombre;
        // Sin venta: una venta igual al costo se toma como "al costo" y sigue al costo nuevo, aunque fuera una venta conocida que coincidía
        // (riesgo aceptado, ADR-026: solo pasa si Nexo no informa la venta y cambia el costo).
        PrecioVenta = VentaRedondeada(precioVenta) ?? (PrecioVenta == Precio ? precio : PrecioVenta);
        Precio = precio;
        Cantidad = cantidad;
        IdServicioConexoCatalogo ??= Acotar(idServicioConexoCatalogo, LargoMaximoId);
        Categoria ??= Acotar(categoria, LargoMaximoCategoria);
        // Como el id del catálogo, completa el código de una fila que no lo tenía (no cuenta como cambio del precio).
        CompletarCodigoServicio(codigoServicio);
        return antes != (Nombre, Precio, PrecioVenta, Cantidad);
    }

    /// <summary>
    /// Guarda el código del servicio del catálogo de Nexo si la fila no lo tenía. Nunca reemplaza uno ya guardado ni guarda uno vacío o que no cabe en la columna. Devuelve si lo completó. No cambia el precio.
    /// </summary>
    internal bool CompletarCodigoServicio(string? codigoServicio)
    {
        if (CodigoServicio is not null || Acotar(codigoServicio, LargoMaximoCodigoServicio) is not { } codigo)
        {
            return false;
        }

        CodigoServicio = codigo;
        return true;
    }

    /// <summary>
    /// Cambia solo la venta unitaria (la que Nexo fijó al agregar el conexo, leída por el polling). Devuelve <c>true</c>
    /// si cambió. No toca el costo ni la cantidad.
    /// </summary>
    internal bool CambiarVenta(decimal precioVenta)
    {
        Validar(IdServicioConexo, Precio, Cantidad, precioVenta);
        var nueva = VentaRedondeada(precioVenta)!.Value;
        if (nueva == PrecioVenta)
        {
            return false;
        }

        PrecioVenta = nueva;
        return true;
    }

    /// <summary>La venta a 2 decimales, half-to-even (como <c>valorAplicado ÷ cantidad</c> del costo).</summary>
    private static decimal? VentaRedondeada(decimal? precioVenta) =>
        precioVenta is { } venta ? decimal.Round(venta, 2, MidpointRounding.ToEven) : null;

    /// <summary>El texto recortado, o <c>null</c> si viene vacío o no cabe en la columna (no se guarda un valor truncado).</summary>
    private static string? Acotar(string? valor, int largo) =>
        valor?.Trim() is { Length: > 0 } limpio && limpio.Length <= largo ? limpio : null;

    private static string Validar(string idServicioConexo, decimal precio, decimal cantidad, decimal? precioVenta = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idServicioConexo);
        var id = idServicioConexo.Trim();
        if (id.Length > LargoMaximoId)
        {
            throw new ArgumentException($"El id del servicio conexo admite hasta {LargoMaximoId} caracteres.", nameof(idServicioConexo));
        }

        if (precio < 0m || cantidad <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(precio), "El precio del conexo no puede ser negativo y la cantidad debe ser mayor que 0.");
        }

        if (precioVenta < 0m)
        {
            // El CHECK ck_cotizacionServiciosConexos_montos del script 19 exige precioVenta >= 0.
            throw new ArgumentOutOfRangeException(nameof(precioVenta), "El precio de venta del conexo no puede ser negativo.");
        }

        return id;
    }

    private static string? LimpiarNombre(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
        {
            return null;
        }

        var limpio = nombre.Trim();
        return limpio.Length > LargoMaximoNombre ? limpio[..LargoMaximoNombre] : limpio;
    }
}
