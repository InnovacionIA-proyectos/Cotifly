using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Seguro de la mercancía por ruta (ADR-032): el comercial incluye o quita el seguro de la mercancía de una ruta en el costeo y la cotización recalcula esa
/// ruta y su total. Las versiones nuevas (<c>-vN</c>) copian el seguro de la ruta equivalente.
/// </summary>
public sealed partial class clsCotizacionSolicitud
{
    /// <summary>
    /// Invariantes de un cambio del seguro de la ruta <paramref name="ruta"/>, en este orden: ruta
    /// rechazada por la Mesa (<c>RUTA_RECHAZADA</c>) y la regla del PDF y de la finalizada (<c>PDF_YA_ENVIADO</c>, el mismo 409 que el
    /// margen). La falta de valor de la mercancía la decide <see cref="clsLineaSolicitud.IncluirSeguro"/>.
    /// </summary>
    public clsErrorDominio? ValidarCambioSeguro(clsLineaSolicitud ruta)
    {
        ArgumentNullException.ThrowIfNull(ruta);
        return ValidarRutaNoRechazada(ruta) ?? ValidarCambioEtapaFinal();
    }

    /// <summary>
    /// Incluye (<paramref name="incluir"/>, con la <paramref name="tasaVigente"/>) o quita el seguro de la ruta
    /// <paramref name="ordenRuta"/> y recalcula esa ruta y la cabecera (el seguro se suma al precio sin margen). Valida primero
    /// (<see cref="ValidarCambioSeguro"/>); sin valor de la mercancía, <c>SEGURO_SIN_VALOR_MERCANCIA</c> y nada cambia. Marcar una ruta que
    /// ya lo tiene, o quitarlo de una que no lo tiene, no cambia nada. Devuelve si algo cambió, o el error.
    /// </summary>
    public clsResultado<bool> CambiarSeguro(int ordenRuta, bool incluir, decimal tasaVigente, DateTimeOffset ahora)
    {
        var ruta = RutaDeOrden(ordenRuta);
        if (ValidarCambioSeguro(ruta) is { } invalido)
        {
            return invalido;
        }

        var cambio = incluir ? ruta.IncluirSeguro(tasaVigente) : ruta.QuitarSeguro();
        if (cambio.EsFallo || !cambio.Valor)
        {
            return cambio;
        }

        var recalculo = RecalcularRuta(ruta, ahora);
        return recalculo.EsFallo ? recalculo.Error : true;
    }

    /// <summary>
    /// Versión nueva de la re-solicitud (<c>-vN</c>, rutas equivalentes por orden): cada ruta copia el seguro de la ruta de
    /// <paramref name="anterior"/> con el mismo orden (<see cref="clsLineaSolicitud.CopiarSeguro"/>; si cambió el valor de la mercancía, con
    /// la <paramref name="tasaVigente"/>). No recalcula: la versión nueva todavía no tiene costeo.
    /// </summary>
    public void CopiarSegurosDe(clsCotizacionSolicitud anterior, decimal? tasaVigente)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        var porOrden = anterior._rutas.ToDictionary(r => r.Orden);
        foreach (var ruta in _rutas)
        {
            if (porOrden.TryGetValue(ruta.Orden, out var previa))
            {
                ruta.CopiarSeguro(previa, tasaVigente);
            }
        }
    }

    /// <summary>
    /// Versión editada: igual que <see cref="CopiarSegurosDe(clsCotizacionSolicitud, decimal?)"/>, con la ruta de
    /// esta versión que edita cada ruta original (<paramref name="rutaAnteriorDe"/>: id de la ruta nueva → id de la original).
    /// </summary>
    public void CopiarSegurosDe(clsCotizacionSolicitud anterior, IReadOnlyDictionary<Guid, Guid> rutaAnteriorDe, decimal? tasaVigente)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        ArgumentNullException.ThrowIfNull(rutaAnteriorDe);
        foreach (var ruta in _rutas)
        {
            if (rutaAnteriorDe.TryGetValue(ruta.Id, out var anteriorId) && anterior._rutas.FirstOrDefault(r => r.Id == anteriorId) is { } previa)
            {
                ruta.CopiarSeguro(previa, tasaVigente);
            }
        }
    }
}
