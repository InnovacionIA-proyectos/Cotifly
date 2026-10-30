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

    /// <summary>
    /// El gerente no tiene regional (ni en <c>DimEjecutivo</c> ni en <c>UsuariosWeb.regional</c>): la Api responde 409 con
    /// <c>ErrorNegocio</c> (HU-010.1, ADR-016). Nunca se cae a "todas las regionales".
    /// </summary>
    public const string RegionalNoAsignada = "REGIONAL_NO_ASIGNADA";

    /// <summary>La fuente de la regional (<c>DimEjecutivo</c>) está configurada pero falla: 503 con <c>ErrorNegocio</c>.</summary>
    public const string RegionalNoDisponible = "REGIONAL_NO_DISPONIBLE";

    /// <summary>
    /// HU-037.3 (#211): un <c>gerente_comercial</c> pidió con <c>?regional=</c> una regional que no es la suya. La Api responde 403 con
    /// <c>ErrorNegocio</c> (no el 403 genérico de rol: el rol sí puede usar el endpoint, lo que no puede es esa regional).
    /// </summary>
    public const string RegionalNoPermitida = "REGIONAL_NO_PERMITIDA";

    /// <summary>
    /// D4 (E2E 2026-10-16): la misma <c>Idempotency-Key</c> llegó con otro cuerpo (ADR-018 §8). Antes era 400 <c>ErrorWeb</c>
    /// (<c>{"error": "texto"}</c>); ahora 409 con <c>ErrorNegocio</c> y un código estable, como los demás conflictos de estado.
    /// </summary>
    public const string IdempotencyKeyReutilizada = "IDEMPOTENCY_KEY_REUTILIZADA";

    /// <summary>Mensaje de <see cref="IdempotencyKeyReutilizada"/>.</summary>
    public const string MensajeIdempotencyKeyReutilizada =
        "Esta Idempotency-Key ya se usó con otra petición: genera una clave nueva para cada intención.";

    /// <summary>
    /// #173/#191: <c>detalle.motivo</c> de <c>ESTADO_NO_PERMITE</c> (409) cuando otra petición con la misma <c>Idempotency-Key</c> no ha
    /// terminado (en esta réplica o en otra) o la primera captura dejó el folio sin terminar de registrar el envío a Nexo.
    /// </summary>
    public const string MotivoCapturaEnCurso = "CAPTURA_EN_CURSO";
}
