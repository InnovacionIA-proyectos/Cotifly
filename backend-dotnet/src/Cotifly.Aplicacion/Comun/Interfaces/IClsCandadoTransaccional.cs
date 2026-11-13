using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Comun.Interfaces;

/// <summary>
/// Transacción con candado exclusivo con nombre sobre <c>dbo_cotifly</c> (en SQL Server, <c>sp_getapplock</c> con dueño
/// <c>Transaction</c>). Serializa entre peticiones, procesos y réplicas las operaciones que calculan un valor a partir de lo ya
/// guardado y lo insertan, como el consecutivo del folio (ADR-011): el candado se libera al confirmar o deshacer.
/// </summary>
public interface IClsCandadoTransaccional
{
    /// <summary>
    /// Abre una transacción en la unidad de trabajo del ámbito, toma el candado <paramref name="recurso"/> (espera hasta que
    /// se libere) y ejecuta <paramref name="operacion"/>, que puede guardar con <see cref="IClsUnitOfWork"/> dentro de la misma
    /// transacción. Confirma si el resultado es exitoso y deshace si es un fallo o si la operación lanza.
    /// Si el candado no se obtiene en los intentos configurados, devuelve <c>SERVICIO_OCUPADO</c> sin haber ejecutado
    /// <paramref name="operacion"/>; la operación corre como mucho una vez.
    /// </summary>
    Task<clsResultado<TValor>> EjecutarAsync<TValor>(
        string recurso,
        Func<CancellationToken, Task<clsResultado<TValor>>> operacion,
        CancellationToken cancellationToken = default);
}
