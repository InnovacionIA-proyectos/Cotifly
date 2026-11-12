namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// La equivalencia entre el código de vehículo que trae el catálogo de Nexo desde SAP
/// (<c>GET /api/catalogos/tipos-vehiculo</c>: «2», «3») y el <b>código NEXO</b> canónico que entiende SICE-TAC/Plaza
/// (<c>C2</c>, <c>C3</c>). Es la <c>tbl_mapeo_vehiculo_tarifario</c> de Nexo (<c>C2</c> ↔ «2», <c>C3</c> ↔ «3»): la
/// <b>única fuente</b> de esta equivalencia en Cotifly, para que ni <c>GET /web/catalogos-flete</c>, ni la captura
/// (<see cref="clsCodigosConocidosNexo"/> / <c>clsResolucionCodigosNexo</c>), ni el front repartan constantes propias.
/// </summary>
/// <remarks>
/// <para><b>Por qué y no el diccionario de alias.</b> El diccionario de alias de Nexo resuelve texto libre
/// hacia el código del catálogo (<c>C2</c> → «2»): justo el sentido contrario al que hace falta aquí. Mandar «2»/«3» a
/// <c>POST /api/captura-solicitudes</c> deja la configuración en cuarentena y Plaza busca mal; hay que mandar siempre el
/// código NEXO (<c>C2</c>, <c>C3</c>, <c>2L3</c>…). En Cotifly la traducción la hace el backend, para que ningún front
/// tenga que acordarse.</para>
/// <para>Solo «2» y «3» difieren entre SAP y Nexo; el resto de códigos (<c>2L2</c>, <c>2S3</c>, <c>3S3</c>…) es idéntico
/// en los dos sistemas y no pasa por aquí (lo resuelve el catálogo tal cual).</para>
/// </remarks>
public static class clsEquivalenciaVehiculoNexo
{
    /// <summary>La <c>tbl_mapeo_vehiculo_tarifario</c>: código SAP del catálogo ↔ código NEXO de SICE-TAC.</summary>
    public static readonly IReadOnlyList<(string CodigoSap, string CodigoNexo)> Mapeo = [("2", "C2"), ("3", "C3")];

    /// <summary>
    /// El código NEXO canónico de <paramref name="codigo"/> si es uno de los que difieren entre SAP y Nexo (el código SAP
    /// «2»/«3» o el propio código NEXO <c>C2</c>/<c>C3</c>, escrito de cualquier forma equivalente); <c>null</c> si no está
    /// en el mapeo (el resto de códigos no cambia y lo resuelve el catálogo tal cual).
    /// </summary>
    public static string? ACodigoNexo(string? codigo)
    {
        if (string.IsNullOrWhiteSpace(codigo))
        {
            return null;
        }

        var valor = codigo.Trim();
        foreach (var (codigoSap, codigoNexo) in Mapeo)
        {
            if (clsVehiculoLineaSolicitud.MismoCodigo(valor, codigoSap) || clsVehiculoLineaSolicitud.MismoCodigo(valor, codigoNexo))
            {
                return codigoNexo;
            }
        }

        return null;
    }
}
