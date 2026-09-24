using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Mapeo TrqType de SAP TM ↔ línea de negocio (<c>dbo_cotifly.SapMapeoTrqType</c>).
/// PK compuesta (<see cref="TrqType"/>, <see cref="SalesOrg"/>); <c>SalesOrg = ""</c> es el comodín
/// y una fila con organización específica tiene prioridad sobre él.
/// </summary>
public sealed class clsSapMapeoTrqType
{
    public const string CodigoError = "MAPEO_TRQTYPE_INVALIDO";
    public const string SalesOrgComodin = "";

    private clsSapMapeoTrqType()
    {
    }

    public string TrqType { get; private set; } = string.Empty;
    public string SalesOrg { get; private set; } = SalesOrgComodin;
    public string LineaNegocio { get; private set; } = string.Empty;
    public bool Activo { get; private set; }

    public bool EsComodin => SalesOrg.Length == 0;

    public static clsResultado<clsSapMapeoTrqType> Crear(string trqType, string lineaNegocio, string? salesOrg = SalesOrgComodin)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "trqType", trqType, 8),
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
            LineaNegocio = lineaNegocio.Trim(),
            Activo = true,
        };
    }

    public void Desactivar() => Activo = false;
}
