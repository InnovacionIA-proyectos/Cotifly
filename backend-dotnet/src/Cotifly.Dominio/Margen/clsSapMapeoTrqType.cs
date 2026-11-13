using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Mapeo TrqType de SAP TM ↔ línea de negocio (<c>dbo_cotifly.SapMapeoTrqType</c>).
/// PK compuesta (<see cref="TrqType"/>, <see cref="SalesOrg"/>); <c>SalesOrg = ""</c> es el comodín
/// y una fila con organización específica tiene prioridad sobre él.
/// </summary>
/// <remarks>
/// La línea se relaciona por <see cref="LineaNegocioId"/> (FK a <c>MargenesPolitica(id)</c>, el mismo id de
/// <c>CotizacionLineasSolicitud.lineaNegocioId</c>; script <c>db/scripts/18_sap_mapeo_fk_margenes_politica.sql</c>).
/// <see cref="LineaNegocio"/> (texto) es una columna de transición: el backend no la usa para resolver, solo la escribe al crear un
/// mapeo porque la columna es <c>NOT NULL</c>.
/// </remarks>
public sealed class clsSapMapeoTrqType
{
    public const string CodigoError = "MAPEO_TRQTYPE_INVALIDO";
    public const string SalesOrgComodin = "";

    private clsSapMapeoTrqType()
    {
    }

    public string TrqType { get; private set; } = string.Empty;
    public string SalesOrg { get; private set; } = SalesOrgComodin;

    /// <summary>Id de la línea en <c>MargenesPolitica</c>: la relación que usa el backend.</summary>
    public int LineaNegocioId { get; private set; }

    /// <summary>Nombre de la línea en texto (columna de transición, que la resolución no lee).</summary>
    public string LineaNegocio { get; private set; } = string.Empty;
    public bool Activo { get; private set; }

    public bool EsComodin => SalesOrg.Length == 0;

    /// <summary>
    /// Mapeo activo del TrqType a la línea <paramref name="lineaNegocioId"/> (id de <c>MargenesPolitica</c>). <paramref name="lineaNegocio"/>
    /// es el nombre de esa misma línea, para la columna de texto de transición.
    /// </summary>
    public static clsResultado<clsSapMapeoTrqType> Crear(string trqType, int lineaNegocioId, string lineaNegocio, string? salesOrg = SalesOrgComodin)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "trqType", trqType, 8),
            lineaNegocioId > 0 ? null : new clsErrorDominio(CodigoError, "El campo 'lineaNegocioId' debe ser el id de una línea de negocio."),
            clsValidacion.Requerido(CodigoError, "lineaNegocio", lineaNegocio, clsMargenPolitica.LargoMaximoLinea),
            clsValidacion.LargoMaximo(CodigoError, "salesOrg", salesOrg?.Trim(), 12));

        if (error is not null)
        {
            return error;
        }

        return new clsSapMapeoTrqType
        {
            TrqType = trqType.Trim().ToUpperInvariant(),
            SalesOrg = salesOrg?.Trim() ?? SalesOrgComodin,
            LineaNegocioId = lineaNegocioId,
            LineaNegocio = lineaNegocio.Trim(),
            Activo = true,
        };
    }

    public void Desactivar() => Activo = false;
}
