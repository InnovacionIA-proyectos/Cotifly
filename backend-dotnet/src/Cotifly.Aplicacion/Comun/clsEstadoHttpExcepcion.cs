using System.Net;
using System.Reflection;

namespace Cotifly.Aplicacion.Comun;

/// <summary>
/// Código HTTP de una excepción de un SDK de proveedor sin depender del SDK: <see cref="HttpRequestException.StatusCode"/> o una
/// propiedad pública <c>Status</c> / <c>StatusCode</c> numérica (<c>ClientResultException.Status</c> del SDK de OpenAI, excepciones
/// del SDK de Anthropic), en la excepción o en sus internas. Lo usan el respaldo de proveedores (Infraestructura) y el validador de
/// la respuesta final.
/// </summary>
public static class clsEstadoHttpExcepcion
{
    /// <summary>
    /// Tope de excepciones internas que se revisan: una cadena anómala (cíclica, armada por reflexión) no deja el filtro de
    /// excepciones del respaldo de IA en un ciclo infinito.
    /// </summary>
    public const int MaximoInternas = 64;

    public static int? Estado(Exception? error)
    {
        var revisadas = 0;
        for (var actual = error; actual is not null && revisadas < MaximoInternas; actual = actual.InnerException, revisadas++)
        {
            if (actual is HttpRequestException { StatusCode: { } codigo })
            {
                return (int)codigo;
            }

            foreach (var nombre in new[] { "Status", "StatusCode" })
            {
                var propiedad = actual.GetType().GetProperty(nombre, BindingFlags.Public | BindingFlags.Instance);
                var valor = propiedad?.GetValue(actual);
                switch (valor)
                {
                    case int entero when entero > 0:
                        return entero;
                    case HttpStatusCode http:
                        return (int)http;
                }
            }
        }

        return null;
    }
}
