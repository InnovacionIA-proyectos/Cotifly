using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// <c>Idempotency-Key</c> del proyecto (ADR-018 §8 y §12): la ÚNICA abstracción de idempotencia. La usan
/// <c>POST .../costeo/re-solicitar</c> (#100) y <c>POST .../costeo/servicios-conexos</c> (#99). Una repetición con la misma clave en el
/// mismo ámbito durante <see cref="Vigencia"/> devuelve el resultado guardado de la primera petición sin volver a ejecutarla: un doble
/// clic no reenvía a Nexo, no duplica el conexo ni crea <c>-v2</c> y <c>-v3</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>ambito</c> = <c>"{operacion}:{usuarioId}"</c>; <c>huella</c> = hash (SHA-256) del folio y del cuerpo de la petición.</item>
/// <item>Dos peticiones simultáneas con la misma clave se serializan con un semáforo POR CLAVE: la segunda espera a la primera y
/// recibe su resultado (si se guardó) o se ejecuta (si no).</item>
/// <item>La misma clave con otra huella es <c>SOLICITUD_INVALIDA</c> (400 <c>ErrorWeb</c>).</item>
/// <item><c>seGuarda</c> decide qué resultados consumen la clave; por defecto (<c>null</c>), solo los éxitos. Lo que no se guarda no
/// consume la clave: la repetición se vuelve a ejecutar.</item>
/// <item>Se purgan las claves vencidas y las fallidas que nadie está usando.</item>
/// </list>
/// Implementación vigente: en la memoria del proceso, válida mientras la API corre con <c>replicas: 1</c>
/// (<c>infra/stack.dotnet.dokploy.yml</c>). Al escalar se crea una tabla con el siguiente script libre, hoy el <c>0011</c> (ADR-018 §12; el <c>0009</c> es la reestructuración de
/// HU-021 y el <c>0010</c>, los nombres de municipio, ADR-019).
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
    /// o el guardado si ya se ejecutó con la misma <paramref name="huella"/> y <paramref name="seGuarda"/> lo aceptó.
    /// </summary>
    Task<clsResultado<T>> EjecutarUnaVezAsync<T>(
        string ambito,
        string clave,
        string huella,
        Func<CancellationToken, Task<clsResultado<T>>> accion,
        Func<clsResultado<T>, bool>? seGuarda,
        CancellationToken ct);
}
