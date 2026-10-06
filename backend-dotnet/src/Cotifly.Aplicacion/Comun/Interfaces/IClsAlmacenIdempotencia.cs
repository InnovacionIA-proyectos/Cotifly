using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// <c>Idempotency-Key</c> de las mutaciones que no se pueden repetir (ADR-018 §8): <c>POST .../costeo/re-solicitar</c> (#100) y, al
/// integrar, <c>POST .../costeo/servicios-conexos</c> (#99). Una repetición con la misma clave en el mismo ámbito (operación y
/// comercial) durante <see cref="Vigencia"/> devuelve el resultado guardado de la primera petición sin volver a ejecutarla: un doble
/// clic no reenvía a Nexo ni crea <c>-v2</c> y <c>-v3</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Dos peticiones simultáneas con la misma clave se serializan: la segunda espera a la primera y recibe su resultado.</item>
/// <item>La misma clave con otra <c>huella</c> (otro folio u otro cuerpo) es <c>SOLICITUD_INVALIDA</c> (400 <c>ErrorWeb</c>).</item>
/// <item>Solo se guardan los éxitos: un error (400, 409...) no consume la clave y la repetición se vuelve a ejecutar.</item>
/// </list>
/// </remarks>
public interface IClsAlmacenIdempotencia
{
    /// <summary>Tiempo durante el que se recuerda una clave (24 h, contrato <c>IdempotencyKey</c>).</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(24);

    /// <summary>Largo mínimo de la clave (contrato: 8).</summary>
    public const int LargoMinimoClave = 8;

    /// <summary>Largo máximo de la clave (contrato: 64).</summary>
    public const int LargoMaximoClave = 64;

    /// <summary>
    /// Ejecuta <paramref name="accion"/> una sola vez por (<paramref name="ambito"/>, <paramref name="clave"/>) y devuelve su resultado,
    /// o el guardado si ya se ejecutó con éxito con la misma <paramref name="huella"/>.
    /// </summary>
    Task<clsResultado<TValor>> EjecutarUnaVezAsync<TValor>(
        string ambito,
        string clave,
        string huella,
        Func<CancellationToken, Task<clsResultado<TValor>>> accion,
        CancellationToken cancellationToken = default)
        where TValor : class;
}
