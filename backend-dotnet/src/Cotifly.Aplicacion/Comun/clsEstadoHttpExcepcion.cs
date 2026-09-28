using System.Net;
using System.Reflection;

namespace Cotifly.Aplicacion.Comun;

/// <summary>
/// Código HTTP de una excepción de un SDK de proveedor sin depender del SDK: <see cref="HttpRequestException.StatusCode"/> o una
/// propiedad pública <c>Status</c> / <c>StatusCode</c> numérica (<c>ClientResultException.Status</c> del SDK de OpenAI, excepciones
/// del SDK de Anthropic), en la excepción o en sus internas. Lo usan el respaldo de proveedores (Infraestructura) y el validador de
/// la respuesta final (#16).
/// </summary>
public static class clsEstadoHttpExcepcion
{
    public static int? Estado(Exception? error)
    {
        for (var actual = error; actual is not null; actual = actual.InnerException)
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
