using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Mercancía de una ruta (<c>dbo_cotifly.CotizacionMercancias</c>, ex <c>CotizacionPosiciones</c>; script 0009, HU-021, ADR-019): lo que
/// el comercial declara que va a mandar en la ruta, en su orden. Cuelga solo de la ruta (el reparto por camión lo guarda el cubicador).
/// <see cref="CodigoMercancia"/> es el código estable del catálogo del cubicador (HU-023.3, #158; script <c>02</c>: reemplaza a
/// <c>idTipoMercancia</c>): obligatorio en las mercancías nuevas (<see cref="Crear"/> y la captura lo exige), vacío en las filas
/// anteriores al <c>02</c> (si hay que recalcular sus camiones, el comercial vuelve a escoger la mercancía).
/// <see cref="CreatedAt"/> lo asigna la persistencia.
/// </summary>
public sealed class clsMercancia
{
    public const string CodigoError = clsCodigosError.SolicitudCosteoInvalida;

    private clsMercancia()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Ruta a la que pertenece (obligatoria).</summary>
    public Guid LineaSolicitudId { get; private set; }

    /// <summary>1 para la primera mercancía de la ruta (único por ruta: <c>uq_cotizacionMercancias_lineaSolicitudId_orden</c>).</summary>
    public int Orden { get; private set; }

    /// <summary>Código del tipo de mercancía en el catálogo del cubicador (por ejemplo <c>BEBIDAS</c>); lo que viaja en <c>POST /recomendaciones</c>.</summary>
    public string? CodigoMercancia { get; private set; }

    /// <summary>Nombre de la mercancía, copiado del catálogo al escogerla (ex <c>descripcion</c>).</summary>
    public string? NombreMercancia { get; private set; }

    public int Cantidad { get; private set; }
    public decimal LargoCm { get; private set; }
    public decimal AnchoCm { get; private set; }
    public decimal AltoCm { get; private set; }
    public decimal? PesoKg { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Mercancía nueva con su código del catálogo del cubicador (obligatorio: <c>SOLICITUD_COSTEO_INVALIDA</c> si falta) y las reglas
    /// de la posición de Nexo (<see cref="clsPosicionCarga.Validar"/>).
    /// </summary>
    public static clsResultado<clsMercancia> Crear(Guid lineaSolicitudId, int orden, string? codigoMercancia, clsPosicionCarga posicion)
    {
        ArgumentNullException.ThrowIfNull(posicion);
        if (string.IsNullOrWhiteSpace(codigoMercancia))
        {
            return new clsErrorDominio(CodigoError, $"La mercancía {orden} debe traer el código del catálogo del cubicador (codigo_mercancia).");
        }

        var conCodigo = posicion with { CodigoMercancia = codigoMercancia.Trim() };
        return conCodigo.Validar(orden) is { } error ? error : Nueva(lineaSolicitudId, orden, conCodigo);
    }

    /// <summary>
    /// Mercancías de una captura ya validada (formulario y licitación; la captura nueva exige el código, HU-023.3) o de una copia de
    /// rutas guardadas (re-solicitud, <c>-vN</c>), con su <see cref="clsPosicionCarga.CodigoMercancia"/> si lo trae. La forma ya la
    /// validó <see cref="clsSolicitudCosteo.Validar"/> o <see cref="clsLineaSolicitudCosteo.Errores"/>.
    /// </summary>
    public static IReadOnlyList<clsMercancia> DesdeCaptura(Guid lineaSolicitudId, IEnumerable<clsPosicionCarga> posiciones)
    {
        ArgumentNullException.ThrowIfNull(posiciones);
        return [.. posiciones.Select((p, i) => Nueva(lineaSolicitudId, i + 1, p))];
    }

    /// <summary>
    /// HU-028 (#186): la mercancía toma el contenido editado de <paramref name="p"/> en el mismo orden (ya validado por la captura), con su
    /// <see cref="clsPosicionCarga.CodigoMercancia"/>. Se actualiza en su lugar para no repetir el orden de la ruta al guardar.
    /// </summary>
    internal void Actualizar(int orden, clsPosicionCarga p)
    {
        ArgumentNullException.ThrowIfNull(p);
        ArgumentOutOfRangeException.ThrowIfLessThan(orden, 1);
        Orden = orden;
        CodigoMercancia = string.IsNullOrWhiteSpace(p.CodigoMercancia) ? null : p.CodigoMercancia.Trim();
        NombreMercancia = string.IsNullOrWhiteSpace(p.Descripcion) ? null : p.Descripcion.Trim();
        Cantidad = p.Cantidad;
        LargoCm = p.LargoCm;
        AnchoCm = p.AnchoCm;
        AltoCm = p.AltoCm;
        PesoKg = p.PesoKg;
    }

    /// <summary>La mercancía como posición de la captura (para reenviarla a Nexo o pedir la recomendación del cubicador).</summary>
    public clsPosicionCarga APosicion() => new(NombreMercancia, Cantidad, LargoCm, AnchoCm, AltoCm, PesoKg, CodigoMercancia: CodigoMercancia);

    private static clsMercancia Nueva(Guid lineaSolicitudId, int orden, clsPosicionCarga p)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(orden, 1);
        return new clsMercancia
        {
            Id = Guid.NewGuid(),
            LineaSolicitudId = lineaSolicitudId,
            Orden = orden,
            CodigoMercancia = string.IsNullOrWhiteSpace(p.CodigoMercancia) ? null : p.CodigoMercancia.Trim(),
            NombreMercancia = string.IsNullOrWhiteSpace(p.Descripcion) ? null : p.Descripcion.Trim(),
            Cantidad = p.Cantidad,
            LargoCm = p.LargoCm,
            AnchoCm = p.AnchoCm,
            AltoCm = p.AltoCm,
            PesoKg = p.PesoKg,
        };
    }
}
