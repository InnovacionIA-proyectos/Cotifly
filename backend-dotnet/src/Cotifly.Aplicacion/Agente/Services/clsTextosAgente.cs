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

    /// <summary><c>sesion_id</c> de otro usuario en <c>POST /web/chat</c> (404 del contrato).</summary>
    public const string SesionNoEncontrada = "Sesión no encontrada.";

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

    /// <summary>
    /// Respaldo del validador de la respuesta final (HU-003.10, #16): el modelo no produjo una respuesta válida ni con el reintento.
    /// Sale como error controlado (<c>200 {texto, error: true}</c> en <c>/web/chat</c>).
    /// </summary>
    public const string RespuestaNoEstructurada =
        "No pude estructurar la respuesta. Reenvía tu mensaje o reformúlalo; lo que ya calculé en esta conversación no se pierde.";

    /// <summary>
    /// HU-013.6 (#102): el turno armó una acción propuesta pero el modelo no dio un texto válido ni con el reintento. La propuesta (con
    /// su resumen del servidor) sale igual con este texto.
    /// </summary>
    public const string PropuestaSinTexto = "Preparé una propuesta para esta cotización: revisa el resumen y, si estás de acuerdo, confírmala con el botón.";

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
