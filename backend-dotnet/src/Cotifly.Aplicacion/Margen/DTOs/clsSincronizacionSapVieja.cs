using System.Globalization;

namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// HU-042.2 (#243, decisión del 2026-11-04): la última sincronización <c>ok</c> de la copia local del margen de SAP TM supera
/// <c>SapTm:AvisoSincronizacionHoras</c>. El margen se usa igual (la copia no caduca); se avisa al comercial y en el log.
/// </summary>
/// <param name="Ultima">Fin de la última corrida <c>ok</c>.</param>
/// <param name="Horas">El umbral configurado (<c>SapTm:AvisoSincronizacionHoras</c>).</param>
public sealed record clsSincronizacionSapVieja(DateTimeOffset Ultima, int Horas)
{
    /// <summary>Texto del aviso para <c>aviso_margen</c>.</summary>
    public string Aviso => string.Create(CultureInfo.InvariantCulture,
        $"La copia del margen de SAP TM lleva más de {Horas} h sin sincronizarse (última: {Ultima.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)}): el margen sugerido de SAP puede estar desactualizado.");
}
