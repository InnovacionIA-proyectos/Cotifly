namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Servicio conexo de una ruta (<c>dbo_cotifly.CotizacionServiciosConexos</c>, script 0009, HU-021, ADR-019; reemplaza los dos JSON
/// <c>serviciosConexos</c>). Es global a la ruta: la <see cref="Cantidad"/> ya dice cuántos son. <see cref="Precio"/> es el costo
/// unitario de Nexo al aplicarlo (antes del margen) y no se recalcula si Nexo lo cambia después; la ruta le aplica su margen al sumar
/// <see cref="Costo"/> a <see cref="clsLineaSolicitud.CostoServiciosConexos"/>. En Nexo los conexos de la ruta viajan pegados a la línea
/// de su primer camión (decisión 3 del 2026-10-08). <see cref="Nombre"/> es una copia del nombre del servicio (0010, mismo criterio que
/// el vehículo); vacío en las filas migradas.
/// </summary>
public sealed class clsServicioConexoRuta
{
    public const int LargoMaximoId = 64;
    public const int LargoMaximoNombre = 200;

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

    /// <summary>Costo unitario de Nexo al aplicarlo (antes del margen).</summary>
    public decimal Precio { get; private set; }

    /// <summary>Unidades del servicio (decimal: Nexo maneja la cantidad de los conexos con decimales).</summary>
    public decimal Cantidad { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Costo del conexo en la ruta: <see cref="Precio"/> × <see cref="Cantidad"/>.</summary>
    public decimal Costo => Precio * Cantidad;

    internal static clsServicioConexoRuta Crear(Guid lineaSolicitudId, string idServicioConexo, string? nombre, decimal precio, decimal cantidad)
    {
        var id = Validar(idServicioConexo, precio, cantidad);
        return new clsServicioConexoRuta
        {
            Id = Guid.NewGuid(),
            LineaSolicitudId = lineaSolicitudId,
            IdServicioConexo = id,
            Nombre = LimpiarNombre(nombre),
            Precio = precio,
            Cantidad = cantidad,
        };
    }

    /// <summary>Cambia el precio, la cantidad o el nombre (el mismo conexo leído otra vez). Devuelve <c>true</c> si cambió.</summary>
    internal bool Actualizar(string? nombre, decimal precio, decimal cantidad)
    {
        Validar(IdServicioConexo, precio, cantidad);
        var antes = (Nombre, Precio, Cantidad);
        Nombre = LimpiarNombre(nombre) ?? Nombre;
        Precio = precio;
        Cantidad = cantidad;
        return antes != (Nombre, Precio, Cantidad);
    }

    private static string Validar(string idServicioConexo, decimal precio, decimal cantidad)
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
