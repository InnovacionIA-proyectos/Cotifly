using Cotifly.Aplicacion.Precio.DTOs;

namespace Cotifly.Infraestructura.Precio.Services;

/// <summary>
/// Última TRM válida conocida por esta réplica (ADR-014 §1), de cualquier fecha y fuente: la registra la caché
/// (<see cref="clsProveedorTrmEnCache"/>) con cada TRM que entrega, y DolarApi la usa para rechazar un valor que se desvíe más de
/// <see cref="clsProveedorTrmDolarApi.DesviacionMaxima"/>. Singleton, seguro entre hilos. Vacía al arrancar: sin referencia no se compara.
/// </summary>
public sealed class clsMemoriaTrm
{
    private clsTrm? _ultima;

    /// <summary>Última TRM válida, o <c>null</c> si todavía no hay ninguna.</summary>
    public clsTrm? Ultima => Volatile.Read(ref _ultima);

    /// <summary>Registra una TRM válida (gana la de mayor fecha de vigencia; a igual vigencia, la más reciente).</summary>
    public void Registrar(clsTrm trm)
    {
        ArgumentNullException.ThrowIfNull(trm);
        while (true)
        {
            var actual = Volatile.Read(ref _ultima);
            if (actual is not null && actual.VigenciaHasta > trm.VigenciaHasta)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _ultima, trm, actual) == actual)
            {
                return;
            }
        }
    }
}
