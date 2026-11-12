using Cotifly.Dominio.Seguros;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-046.3 (#269): la política de seguros por valor de la mercancía (<see cref="clsPoliticaSeguros"/>) aplicada a la cotización. Las
/// reglas viven en sus tablas (script 21); la cotización solo aporta el valor total de la mercancía de cada ruta y los
/// <c>codigoServicio</c> de sus conexos (<see cref="clsServicioConexoRuta.CodigoServicio"/>).
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary><c>detalle.motivo</c> de <c>COTIZACION_NO_FINALIZABLE</c> con medidas obligatorias de la política pendientes (HU-046.3).</summary>
    public const string CodigoMedidasObligatoriasFaltantes = "medidas_obligatorias_faltantes";

    /// <summary>
    /// Largo mínimo de la justificación con que el comercial finaliza sin una medida obligatoria (contrato del 2026-11-10; sin contar los
    /// espacios de los extremos). Decisión 1 de HU-046.1.
    /// </summary>
    public const int LargoMinimoJustificacion = 10;

    /// <summary>Largo máximo de la justificación con que el comercial finaliza sin una medida obligatoria (contrato del 2026-11-10).</summary>
    public const int LargoMaximoJustificacion = 500;

    /// <summary>
    /// Las medidas obligatorias pendientes de cada ruta COTIZABLE (las rechazadas por la Mesa de Costeo no cuentan,
    /// <see cref="RutasCotizables"/>), en orden de ruta; vacío si no hay ninguna. Una ruta sin valor de la mercancía no tiene tramo ni
    /// exige nada (decisión 4: el aviso no bloquea).
    /// </summary>
    public IReadOnlyList<clsMedidasFaltantesRuta> MedidasObligatoriasPendientes(
        clsPoliticaSeguros politica, Func<clsLineaSolicitud, clsServicioConexoRuta, bool>? cuenta = null)
    {
        ArgumentNullException.ThrowIfNull(politica);
        return
        [
            .. RutasCotizables
                .Select(r => new clsMedidasFaltantesRuta(
                    r.Orden, r.OrigenNombre, r.DestinoNombre,
                    r.EvaluarPoliticaSeguros(politica, cuenta is null ? null : c => cuenta(r, c)).Pendientes))
                .Where(r => r.Medidas.Count > 0),
        ];
    }

    /// <summary>
    /// Completa el <c>codigoServicio</c> de los conexos que no lo tienen con <paramref name="codigoDe"/> (lo que dice el catálogo de Nexo
    /// del conexo, o <c>null</c> si no se sabe). No cambia el precio ni la versión de negocio: es un dato del catálogo, también en una
    /// cotización finalizada. Devuelve los conexos que completó.
    /// </summary>
    public IReadOnlyList<clsServicioConexoRuta> CompletarCodigosConexos(Func<clsServicioConexoRuta, string?> codigoDe)
    {
        ArgumentNullException.ThrowIfNull(codigoDe);
        return [.. _rutas.SelectMany(r => r.Conexos).Where(c => c.CodigoServicio is null).ToList().Where(c => c.CompletarCodigoServicio(codigoDe(c)))];
    }
}
