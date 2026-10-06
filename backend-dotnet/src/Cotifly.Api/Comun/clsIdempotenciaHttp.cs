using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Comun.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;

namespace Cotifly.Api.Comun;

/// <summary>
/// <c>Idempotency-Key</c> opcional de las mutaciones que llaman a un POST no idempotente de Nexo (ADR-018 §8): agregar conexos (#99) y,
/// con el mismo atributo, re-solicitar el costeo (#100). Una repetición con la misma clave, el mismo comercial y la misma ruta en
/// <see cref="clsAlmacenIdempotencia.Vigencia"/> devuelve la respuesta guardada de la primera petición (estado, cuerpo y <c>ETag</c>)
/// sin volver a ejecutarla; la misma clave con otro cuerpo responde 400 <c>ErrorWeb</c>. Dos peticiones simultáneas con la misma clave
/// (doble clic) se serializan: la segunda espera a la primera y recibe su respuesta.
/// </summary>
/// <remarks>
/// Se guardan las respuestas 2xx y las de error cuyo <c>detalle</c> trae <c>agregado_en_nexo</c> (Nexo ya ejecutó el POST: repetirlo
/// duplicaría el efecto). Los demás errores no tienen efecto en Nexo y la repetición se vuelve a evaluar. Sin el encabezado la acción
/// corre normal. Corre después del <c>If-Match</c> (<see cref="clsRequiereIfMatchAttribute"/>, filtro de recurso).
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class clsIdempotenteAttribute : Attribute, IAsyncActionFilter
{
    public const string Encabezado = "Idempotency-Key";
    public const int LargoMinimo = 8;
    public const int LargoMaximo = 64;

    /// <summary>Clave del <c>detalle</c> de un error que se guarda igual (el POST de Nexo ya se ejecutó).</summary>
    public const string ClaveEfectoEnNexo = "agregado_en_nexo";

    public const string MensajeClaveInvalida = "Idempotency-Key inválida: debe tener entre 8 y 64 caracteres.";
    public const string MensajeOtroCuerpo = "La Idempotency-Key ya se usó con otro cuerpo: genera una clave nueva para esta acción.";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var valor = http.Request.Headers[Encabezado].ToString().Trim();
        if (valor.Length == 0)
        {
            await next();
            return;
        }

        if (valor.Length is < LargoMinimo or > LargoMaximo)
        {
            context.Result = new BadRequestObjectResult(new clsErrorWeb(MensajeClaveInvalida));
            return;
        }

        if (http.RequestServices.GetRequiredService<IClsContextoUsuario>().UsuarioId is not { } usuarioId)
        {
            await next();
            return;
        }

        var almacen = http.RequestServices.GetRequiredService<clsAlmacenIdempotencia>();
        var clave = $"{usuarioId:N}|{http.Request.Method}|{http.Request.Path}|{valor}";
        var huella = Huella(context.ActionArguments);
        var candado = almacen.Candado(clave);
        await candado.WaitAsync(http.RequestAborted);
        try
        {
            if (almacen.Obtener(clave) is { } guardada)
            {
                context.Result = guardada.Huella == huella
                    ? new ObjectResult(guardada.Valor) { StatusCode = guardada.Estado }
                    : new BadRequestObjectResult(new clsErrorWeb(MensajeOtroCuerpo));
                return;
            }

            var ejecutada = await next();
            if (ejecutada.Exception is null && ejecutada.Result is ObjectResult resultado && SeGuarda(resultado))
            {
                almacen.Guardar(clave, new clsRespuestaIdempotente(huella, resultado.StatusCode ?? StatusCodes.Status200OK, resultado.Value));
            }
        }
        finally
        {
            candado.Release();
        }
    }

    /// <summary>¿Se guarda la respuesta? 2xx, o un error de negocio cuyo efecto en Nexo ya ocurrió (<see cref="ClaveEfectoEnNexo"/>).</summary>
    public static bool SeGuarda(ObjectResult resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        var estado = resultado.StatusCode ?? StatusCodes.Status200OK;
        return estado is >= 200 and < 300
               || resultado.Value is clsErrorNegocioWeb { Error.Detalle: { } detalle } && detalle.ContainsKey(ClaveEfectoEnNexo);
    }

    /// <summary>SHA-256 de los argumentos de la acción (cuerpo y ruta), sin el token de cancelación.</summary>
    public static string Huella(IDictionary<string, object?> argumentos)
    {
        ArgumentNullException.ThrowIfNull(argumentos);
        var ordenados = argumentos.Where(a => a.Value is not CancellationToken).OrderBy(a => a.Key, StringComparer.Ordinal)
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(ordenados, clsJsonWeb.Opciones);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}

/// <summary>Respuesta guardada de una petición con <c>Idempotency-Key</c>: la huella del cuerpo, el estado y el valor.</summary>
public sealed record clsRespuestaIdempotente(string Huella, int Estado, object? Valor);

/// <summary>
/// Respuestas de <see cref="clsIdempotenteAttribute"/> en memoria del proceso (singleton), <see cref="Vigencia"/> cada una y hasta
/// <see cref="MaximoEntradas"/>. Con varias réplicas detrás del balanceador, un doble clic que cae en réplicas distintas no se
/// detecta aquí: lo frena el <c>If-Match</c> al guardar (pendiente: almacén compartido si se escala horizontalmente).
/// </summary>
public sealed class clsAlmacenIdempotencia : IDisposable
{
    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(24);
    public const int MaximoEntradas = 20_000;
    private const int NumeroCandados = 64;

    private readonly MemoryCache _respuestas = new(new MemoryCacheOptions { SizeLimit = MaximoEntradas });
    private readonly SemaphoreSlim[] _candados = [.. Enumerable.Range(0, NumeroCandados).Select(_ => new SemaphoreSlim(1, 1))];

    /// <summary>Candado de la clave (por franjas: dos claves distintas pueden compartirlo, nunca una clave dos candados).</summary>
    public SemaphoreSlim Candado(string clave)
    {
        ArgumentNullException.ThrowIfNull(clave);
        return _candados[(int)((uint)StringComparer.Ordinal.GetHashCode(clave) % NumeroCandados)];
    }

    public clsRespuestaIdempotente? Obtener(string clave) =>
        _respuestas.TryGetValue(clave, out clsRespuestaIdempotente? guardada) ? guardada : null;

    public void Guardar(string clave, clsRespuestaIdempotente respuesta) =>
        _respuestas.Set(clave, respuesta, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Vigencia, Size = 1 });

    public void Dispose()
    {
        _respuestas.Dispose();
        foreach (var candado in _candados)
        {
            candado.Dispose();
        }
    }
}
