using System.Net;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>Textos fijos del agente hacia el comercial (paridad con el Python donde existían) y clasificación de fallos del proveedor.</summary>
public static class clsTextosAgente
{
    /// <summary>Límite de peticiones del proveedor de IA (texto de <c>orquestador/web.py</c>).</summary>
    public const string LimiteModelo =
        "⏳ El modelo alcanzó su límite de peticiones por minuto. Espera unos segundos y reenvía el mismo mensaje — la conversación no se pierde.";

    /// <summary>Cualquier otro fallo interno del turno (texto de <c>orquestador/web.py</c>).</summary>
    public const string ErrorInterno = "Ocurrió un error interno procesando tu solicitud. Inténtalo de nuevo.";

    /// <summary>Acuse del webhook de Google Chat (<c>_RECIBO</c> de <c>orquestador/webhook.py</c>).</summary>
    public const string Recibo = "Recibí tu solicitud, la estoy procesando…";

    /// <summary>Email de Google Chat sin comercial en <c>UsuariosWeb</c> (ADR-011): no se cotiza.</summary>
    public const string SinIdentidad = "Ingresa primero a Cotifly desde ClientePro.";

    /// <summary>El hilo de Chat ya es la cotización de otro comercial (la sesión queda ligada a su dueño, ADR-004 §6).</summary>
    public const string HiloAjeno = "Este hilo pertenece a la cotización de otro comercial. Abre un hilo nuevo para cotizar.";

    /// <summary><c>sesion_id</c> de otro usuario en <c>POST /web/chat</c> (404 del contrato).</summary>
    public const string SesionNoEncontrada = "Sesión no encontrada.";

    /// <summary>La cola del webhook está llena: se acusa el evento pero no se procesa.</summary>
    public const string ColaLlena = "Estoy atendiendo muchas solicitudes en este momento. Reenvía tu mensaje en un minuto.";

    /// <summary>
    /// ¿El fallo es el límite de peticiones del proveedor (HTTP 429)? Misma heurística que el Python (tipo <c>RateLimit*</c> o
    /// "429" en el mensaje, también "rate limit" en el mensaje) más el código HTTP de la excepción o de sus internas.
    /// </summary>
    public static bool EsLimiteDePeticiones(Exception? error)
    {
        for (var actual = error; actual is not null; actual = actual.InnerException)
        {
            if (actual is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                || actual.GetType().Name.Contains("RateLimit", StringComparison.OrdinalIgnoreCase)
                || actual.Message.Contains("RateLimit", StringComparison.OrdinalIgnoreCase)
                || actual.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || actual.Message.Contains("429", StringComparison.Ordinal))
            {
                return true;
            }

            if (actual is AggregateException agregada && agregada.InnerExceptions.Any(EsLimiteDePeticiones))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Otro mensaje de la misma conversación todavía se está procesando (candado por sesión).</summary>
    public const string TurnoEnCurso = "Todavía estoy respondiendo tu mensaje anterior en esta conversación. Espera la respuesta y vuelve a escribir.";

    /// <summary>Texto para el comercial ante un fallo del turno.</summary>
    public static string ParaFallo(Exception error) => error switch
    {
        Exceptions.clsTurnoEnCursoException => TurnoEnCurso,
        _ when EsLimiteDePeticiones(error) => LimiteModelo,
        _ => ErrorInterno,
    };
}
