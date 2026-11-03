using System.ComponentModel.DataAnnotations;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Sincronización del margen de SAP TM que hace el backend (HU-042.2, #243, ADR-027; reemplaza el SQL Agent Job de la #242). Misma sección
/// <c>SapTm</c> que <c>clsOpcionesMargen</c> (que solo lleva lo que lee Aplicación al cotizar).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Inactiva sin credenciales.</b> Sin <see cref="Url"/>, <see cref="User"/> o <see cref="Pass"/> el worker no corre (lo anota en
/// Information) y el margen sigue con la copia local existente o la política de la línea. No impide arrancar.</item>
/// <item><b>Inactiva con la URL o el mandante mal escritos</b> (<see cref="ErrorConfiguracion"/>): tampoco impide arrancar la API (el margen
/// no depende de SAP al cotizar); el worker lo anota en Error y la administración responde <c>SAP_MARGEN_NO_CONFIGURADO</c>.</item>
/// <item><see cref="Url"/> es la URL del servicio OData (ej. <c>.../ZGW_RENTAB_CLI_SRV</c>). Si trae <c>/RentabClienteSet</c> al final se
/// acepta y se quita (<see cref="UrlServicio"/>). Se pide <c>{UrlServicio}/RentabClienteSet?$format=json</c>, sin <c>$filter</c>: SAP lo
/// ignora y devuelve el set completo. http se acepta (red interna); en Production el worker lo avisa al arrancar.</item>
/// <item>La fortaleza de <see cref="Pass"/> en Production la revisa <see cref="clsValidadorSecretosOpciones{TOpciones}"/>. Ningún mensaje
/// ni log incluye <see cref="Url"/>, <see cref="User"/> o <see cref="Pass"/>.</item>
/// </list>
/// </remarks>
public sealed class clsOpcionesSapTmSincronizacion
{
    public const string Seccion = "SapTm";

    /// <summary>URL base del servicio OData (ej. <c>http://sap-tm-prd.boterosoto.local:8000/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV</c>).</summary>
    public string? Url { get; set; }

    /// <summary>Usuario técnico de SAP para la autenticación Basic.</summary>
    public string? User { get; set; }

    /// <summary>Clave del usuario técnico. Secreto: solo en el <c>.env</c> o en Dokploy.</summary>
    [clsSecreto(LargoMinimo = 8, Opcional = true)]
    public string? Pass { get; set; }

    /// <summary>Mandante de SAP (<c>sap-client</c>, 3 dígitos), opcional: con valor se agrega <c>&amp;sap-client=NNN</c>.</summary>
    public string? SapClient { get; set; }

    /// <summary>Cada cuántos minutos el worker revisa si toca sincronizar. Por defecto 60.</summary>
    [Range(1, 24 * 60)]
    public int ChequeoMinutos { get; set; } = 60;

    /// <summary>Edad (minutos) de la última corrida <c>ok</c> a partir de la cual se vuelve a sincronizar. Por defecto 1440 (un día).</summary>
    [Range(1, 7 * 24 * 60)]
    public int SincronizacionMinutos { get; set; } = 1440;

    /// <summary>Límite de la descarga de SAP (cabeceras y cuerpo), en segundos. Por defecto 120.</summary>
    [Range(5, 900)]
    public int TimeoutSegundos { get; set; } = 120;

    /// <summary>Tamaño máximo de la respuesta de SAP ya descomprimida, en bytes. Por defecto 20 MB (hoy ~1500 filas, unos cientos de KB).</summary>
    [Range(1024 * 1024, 512L * 1024 * 1024)]
    public long MaximoBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Mínimo absoluto de filas que debe traer SAP para tocar la tabla. Por defecto 100.</summary>
    [Range(1, 1_000_000)]
    public int MinimoFilas { get; set; } = 100;

    /// <summary>Mínimo relativo: porcentaje de las filas activas actuales que debe traer SAP. Por defecto 80.</summary>
    [Range(0, 100)]
    public int MinimoPorcentajeActivas { get; set; } = 80;

    /// <summary>Hay URL, usuario y clave (aunque estén mal escritas).</summary>
    public bool TieneCredenciales => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(User) && !string.IsNullOrWhiteSpace(Pass);

    /// <summary>Hay URL, usuario y clave y la URL y el mandante son válidos: el worker corre.</summary>
    public bool Configurada => TieneCredenciales && ErrorConfiguracion is null;

    /// <summary><see cref="Url"/> recortada, sin <c>/</c> ni <c>/RentabClienteSet</c> al final (<c>null</c> si no hay).</summary>
    public string? UrlServicio => string.IsNullOrWhiteSpace(Url) ? null : NormalizarUrl(Url);

    /// <summary>
    /// Por qué la URL o el mandante no sirven (<c>null</c> si sirven o si no hay URL). Nombra la clave, nunca el valor.
    /// </summary>
    public string? ErrorConfiguracion
    {
        get
        {
            const string Prefijo = Seccion + ":";
            if (UrlServicio is { } texto)
            {
                if (!Uri.TryCreate(texto, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
                {
                    return $"'{Prefijo}Url' debe ser una URL http o https absoluta.";
                }

                if (url.UserInfo.Length > 0)
                {
                    return $"'{Prefijo}Url' no puede llevar usuario ni clave: use {Prefijo}User y {Prefijo}Pass.";
                }

                if (url.Query.Length > 0 || url.Fragment.Length > 0)
                {
                    return $"'{Prefijo}Url' es la URL del servicio, sin consulta ni fragmento (se agrega /RentabClienteSet?$format=json).";
                }
            }

            return SapClientNormalizado is { } mandante && !(mandante.Length == 3 && mandante.All(char.IsAsciiDigit))
                ? $"'{Prefijo}SapClient' debe tener exactamente 3 dígitos (ej. 100) o quedar vacío."
                : null;
        }
    }

    /// <summary>Quita espacios, la <c>/</c> final y un <c>/RentabClienteSet</c> final (sin distinguir mayúsculas).</summary>
    public static string NormalizarUrl(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var texto = url.Trim().TrimEnd('/');
        const string Conjunto = "/RentabClienteSet";
        return texto.EndsWith(Conjunto, StringComparison.OrdinalIgnoreCase) ? texto[..^Conjunto.Length].TrimEnd('/') : texto;
    }

    /// <summary><see cref="SapClient"/> recortado, o <c>null</c> si no hay.</summary>
    public string? SapClientNormalizado => string.IsNullOrWhiteSpace(SapClient) ? null : SapClient.Trim();
}
