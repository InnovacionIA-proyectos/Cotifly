using System.Globalization;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Identidad del comercial ante Nexo, en un solo lugar para todos los adaptadores (clientes, municipios, catálogos, costeo, conexos y
/// licitación). Nexo no tiene login propio: cada petición lleva el encabezado <see cref="Encabezado"/> con el
/// <c>idUsuarioExterno</c> del comercial (<c>UsuariosWeb.idUsuarioExterno</c>) y Nexo resuelve su rol contra el almacén corporativo
/// (<c>clsIdentidadPortalAuthenticationHandler.HeaderIdUsuario</c> de Nexo). Sin ese encabezado, o con otro nombre, Nexo responde
/// <c>401 SESION_INVALIDA</c>.
/// </summary>
public static class clsIdentidadNexo
{
    /// <summary>
    /// Nombre exacto del encabezado que valida Nexo. Con otro nombre (por ejemplo <c>X-Portal-Usuario-Id</c>) Nexo responde 401: el
    /// nombre lleva el prefijo <c>cls</c> del renombre de clases de Nexo y así lo publica su código.
    /// </summary>
    public const string Encabezado = "X-Portal-clsUsuario-Id";

    /// <summary>Agrega a <paramref name="peticion"/> la identidad del comercial (<paramref name="idUsuarioExterno"/>).</summary>
    public static void Identificar(HttpRequestMessage peticion, long idUsuarioExterno) =>
        Identificar(peticion, idUsuarioExterno.ToString(CultureInfo.InvariantCulture));

    /// <summary>Agrega a <paramref name="peticion"/> la identidad del comercial ya en texto (reemplaza una anterior).</summary>
    public static void Identificar(HttpRequestMessage peticion, string idUsuarioExterno)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        ArgumentException.ThrowIfNullOrWhiteSpace(idUsuarioExterno);
        peticion.Headers.Remove(Encabezado);
        peticion.Headers.Add(Encabezado, idUsuarioExterno);
    }
}
