using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Precio.Services;

/// <summary>
/// Caché de la TRM por fecha de negocio (HU-012.1): la TRM se fija por día y no cambia intradía, así que solo la primera cotización en
/// USD de cada día consulta la fuente. Decorador de <see cref="IClsProveedorTrm"/> (singleton).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>La clave es la fecha que pide el caso de uso (el "hoy" de America/Bogota, <c>IClsCalendarioNegocio</c>), nunca la fecha UTC.</item>
/// <item>Una entrada vive <see cref="Duracion"/> según <see cref="IClsReloj"/>: si la Superintendencia corrige un valor, se toma en la
/// siguiente consulta tras el vencimiento. Al guardar se descartan las vencidas (a lo sumo unas pocas fechas en memoria).</item>
/// <item>Los fallos no se guardan: el comercial puede reintentar enseguida (HU-012, "puede reintentar").</item>
/// <item>Una sola consulta a la vez hacia la fuente: varias cotizaciones simultáneas sin caché esperan la misma respuesta.</item>
/// </list>
/// </remarks>
public sealed class clsProveedorTrmEnCache(IClsProveedorTrm fuente, IClsReloj reloj) : IClsProveedorTrm, IDisposable
{
    /// <summary>Vida de una TRM en la caché.</summary>
    public static readonly TimeSpan Duracion = TimeSpan.FromHours(6);

    private readonly Dictionary<DateOnly, (clsTrm Trm, DateTimeOffset Vence)> _entradas = [];
    private readonly SemaphoreSlim _consulta = new(1, 1);
    private readonly Lock _candado = new();

    public async Task<clsResultado<clsTrm>> ObtenerAsync(DateOnly fecha, CancellationToken cancellationToken = default)
    {
        if (Vigente(fecha) is { } enCache)
        {
            return enCache;
        }

        await _consulta.WaitAsync(cancellationToken);
        try
        {
            // Otra consulta pudo llenarla mientras se esperaba el turno.
            if (Vigente(fecha) is { } recienLlegada)
            {
                return recienLlegada;
            }

            var resultado = await fuente.ObtenerAsync(fecha, cancellationToken);
            if (resultado.EsExitoso)
            {
                Guardar(fecha, resultado.Valor);
            }

            return resultado;
        }
        finally
        {
            _consulta.Release();
        }
    }

    public void Dispose() => _consulta.Dispose();

    private clsTrm? Vigente(DateOnly fecha)
    {
        lock (_candado)
        {
            return _entradas.TryGetValue(fecha, out var entrada) && reloj.AhoraUtc < entrada.Vence ? entrada.Trm : null;
        }
    }

    private void Guardar(DateOnly fecha, clsTrm trm)
    {
        var ahora = reloj.AhoraUtc;
        lock (_candado)
        {
            foreach (var vencida in _entradas.Where(e => ahora >= e.Value.Vence).Select(e => e.Key).ToList())
            {
                _entradas.Remove(vencida);
            }

            _entradas[fecha] = (trm, ahora + Duracion);
        }
    }
}
