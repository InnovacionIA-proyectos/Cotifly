namespace Cotifly.Aplicacion.Comun;

/// <summary>
/// Códigos de error de los casos de uso de <c>/web/*</c> que no son de negocio de una herramienta (no van en
/// <c>ErrorNegocio</c>): la Api los traduce a 401, 403 o 404 con <c>ErrorWeb</c>.
/// </summary>
public static class clsCodigosAplicacion
{
    /// <summary>La petición no trae un usuario autenticado válido (la Api responde 401).</summary>
    public const string NoAutenticado = "NO_AUTENTICADO";

    /// <summary>El rol del usuario no permite la operación (la Api responde 403). Revalida la política del endpoint.</summary>
    public const string SinPermiso = "SIN_PERMISO";

    /// <summary>El recurso no existe o no es del usuario: indistinguibles a propósito (la Api responde 404).</summary>
    public const string NoEncontrado = "NO_ENCONTRADO";

    /// <summary>La entrada no cumple el contrato (la Api responde 400 con <c>ErrorWeb</c>).</summary>
    public const string SolicitudInvalida = "SOLICITUD_INVALIDA";
}
