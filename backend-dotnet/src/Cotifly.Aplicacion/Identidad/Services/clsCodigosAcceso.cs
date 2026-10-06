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

    /// <summary>Prefijo del hilo de auditoría de acceso: <c>auth/{idUsuario}</c>.</summary>
    public const string PrefijoHilo = "auth/";

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

    // Reglas de resolución del rol interno (nunca implícitas: quedan en la auditoría).
    public const string ReglaMapeoUnico = "mapeo_unico";
    public const string ReglaPrecedenciaGerente = "precedencia_gerente_comercial";
}
