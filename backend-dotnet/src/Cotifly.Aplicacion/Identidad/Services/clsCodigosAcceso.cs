using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Identidad.Services;

/// <summary>
/// Códigos de error de <c>/api/auth/*</c> (la Api los traduce a HTTP con <c>ErrorWeb</c>) y motivos que quedan en la
/// auditoría de acceso. El motivo real nunca sale en la respuesta: hacia afuera todo rechazo es el mismo 401 genérico.
/// </summary>
public static class clsCodigosAcceso
{
    /// <summary>401 <c>{"error":"Credenciales inválidas"}</c> (login).</summary>
    public const string CredencialesInvalidas = "CREDENCIALES_INVALIDAS";

    /// <summary>401 <c>{"error":"Inicia sesión de nuevo."}</c> (refresh y endpoints protegidos).</summary>
    public const string SesionExpirada = "SESION_EXPIRADA";

    /// <summary>400 con <c>ErrorWeb</c> (cuerpo del login inválido).</summary>
    public const string SolicitudInvalida = "SOLICITUD_INVALIDA";

    public const string MensajeCredencialesInvalidas = "Credenciales inválidas";
    public const string MensajeSesionExpirada = "Inicia sesión de nuevo.";

    /// <summary>Largo máximo de <c>idUsuario</c> (<c>LoginClienteProRequest</c>).</summary>
    public const int LargoMaximoIdUsuario = 100;

    /// <summary>Largo máximo de <c>nombreUsuario</c> informativo (<c>LoginClienteProRequest</c>).</summary>
    public const int LargoMaximoNombreUsuario = 200;

    // Herramientas de la auditoría de acceso (EventosHerramienta, hilo "auth/{usuario}").
    public const string HerramientaLogin = "login_clientepro";
    public const string HerramientaRefresh = "refresh_sesion";
    public const string HerramientaLogout = "logout";

    /// <summary><c>POST /api/auth/elegir-rol</c> (cada elección, aceptada o rechazada).</summary>
    public const string HerramientaEleccionRol = "elegir_rol";

    /// <summary>Largo máximo del ticket de elección que se intenta validar (un JWT HS256 pequeño).</summary>
    public const int LargoMaximoTicket = 4096;

    /// <summary>Prefijo del hilo de auditoría de acceso: <c>auth/{idUsuario}</c>.</summary>
    public const string PrefijoHilo = "auth/";

    /// <summary>Actor de los deltas de la reconciliación de roles en la auditoría (no es una persona: lo hace el login).</summary>
    public const string ActorReconciliacion = "sistema/login";

    public static clsErrorDominio ErrorCredencialesInvalidas() => new(CredencialesInvalidas, MensajeCredencialesInvalidas);

    public static clsErrorDominio ErrorSesionExpirada() => new(SesionExpirada, MensajeSesionExpirada);
}

/// <summary>Motivos (y reglas) auditados del login, el refresh y el logout.</summary>
public static class clsMotivosAcceso
{
    // Éxitos.
    public const string AltaJit = "alta_jit";
    public const string UsuarioExistente = "usuario_existente";
    public const string Renovado = "renovado";
    public const string SesionCerrada = "sesion_cerrada";

    /// <summary>La verificación pasó con varios roles; se emitió el ticket de elección, todavía sin sesión.</summary>
    public const string EleccionRolPendiente = "eleccion_rol_pendiente";

    /// <summary>Sesión emitida con el rol elegido.</summary>
    public const string RolElegido = "rol_elegido";

    // Rechazos (todos responden el mismo 401 genérico).
    public const string UsuarioNoEncontrado = "usuario_no_encontrado";
    public const string AlmacenNoDisponible = "almacen_no_disponible";
    public const string SinRolAutorizado = "sin_rol_autorizado";
    public const string RolExternoCambio = "rol_externo_cambio";
    public const string SinAsignacionActiva = "sin_asignacion_activa";
    public const string NombreUsuarioCambio = "nombre_usuario_cambio";
    public const string IdentidadInconsistente = "identidad_inconsistente";
    public const string AltaConcurrente = "alta_concurrente";
    public const string ErrorInterno = "error_interno";
    public const string RefreshDesconocido = "refresh_desconocido";
    public const string RefreshVencido = "refresh_vencido";
    public const string RefreshReutilizado = "refresh_reutilizado";
    public const string RotacionConcurrente = "rotacion_concurrente";

    // Rechazos de la elección del rol (todos responden el mismo 401 "Inicia sesión de nuevo.").
    public const string TicketInvalido = "ticket_invalido";
    public const string TicketVencido = "ticket_vencido";
    public const string TicketReutilizado = "ticket_reutilizado";
    public const string RolNoDisponible = "rol_no_disponible";

    /// <summary>Refresh sin <c>rolSesion</c> (emitido antes del script 16) de un usuario que tiene varios roles: debe elegir.</summary>
    public const string EleccionRolRequerida = "eleccion_rol_requerida";

    // Reglas de resolución del rol interno (nunca implícitas: quedan en la auditoría).
    public const string ReglaMapeoUnico = "mapeo_unico";
    public const string ReglaPrecedenciaGerente = "precedencia_gerente_comercial";

    /// <summary>Con el rol de director y otro rol mapeado gana <c>director</c>.</summary>
    public const string ReglaPrecedenciaDirector = "precedencia_director";

    /// <summary>
    /// El usuario tiene varias asignaciones activas (o es un alta con varios roles externos): no gana ninguno,
    /// elige con cuál entra (ADR-025).
    /// </summary>
    public const string ReglaMultiRol = "multi_rol";

    // Deltas de la reconciliación (ADR-009) de roles en el login (actor "sistema/login", sin revisor humano). Van en la
    // auditoría del login como listas de códigos; no son motivos de rechazo.

    /// <summary>Rol ORDINARIO que faltaba y el login auto-otorgó (creó o reactivó la asignación) por su respaldo externo.</summary>
    public const string RolOtorgadoAuto = "rol_otorgado_auto";

    /// <summary>Asignación que el login auto-revocó (desactivó) porque su rol externo ya no la respalda (fail-safe, todos los roles).</summary>
    public const string RolRevocadoAuto = "rol_revocado_auto";
}
