using System.Collections.Concurrent;
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
/// <item>Una TRM vive <see cref="Duracion"/> según <see cref="IClsReloj"/>: si la Superintendencia corrige un valor, se toma en la siguiente
/// consulta tras el vencimiento. Al guardar se descartan las entradas vencidas.</item>
/// <item>Consulta compartida por fecha: las llamadas simultáneas sin caché esperan la misma consulta a la fuente y reciben el mismo
/// resultado, también si falla (una fuente caída no encola N consultas de N comerciales). La consulta compartida no usa el token de
/// ningún llamador (el adaptador tiene su propio límite de tiempo); cada llamador puede dejar de esperar con el suyo.</item>
/// <item>Un fallo se recuerda solo <see cref="DuracionFallo"/> (caché negativa): durante ese lapso los reintentos responden el mismo
/// <c>TRM_NO_DISPONIBLE</c> sin golpear la fuente; después, el comercial puede reintentar. La TRM es pública e igual para todos los
/// comerciales: compartir el fallo no filtra nada.</item>
/// </list>
/// </remarks>
public sealed class clsProveedorTrmEnCache(IClsProveedorTrm fuente, IClsReloj reloj) : IClsProveedorTrm
{
    /// <summary>Vida de una TRM en la caché.</summary>
    public static readonly TimeSpan Duracion = TimeSpan.FromHours(6);

    /// <summary>Vida de un fallo en la caché negativa.</summary>
    public static readonly TimeSpan DuracionFallo = TimeSpan.FromSeconds(20);

    private readonly ConcurrentDictionary<DateOnly, clsEntrada> _entradas = new();
    private readonly ConcurrentDictionary<DateOnly, Lazy<Task<clsResultado<clsTrm>>>> _enCurso = new();

    public Task<clsResultado<clsTrm>> ObtenerAsync(DateOnly fecha, CancellationToken cancellationToken = default)
    {
        if (_entradas.TryGetValue(fecha, out var entrada) && reloj.AhoraUtc < entrada.Vence)
        {
            return Task.FromResult(entrada.Resultado);
        }

        var compartida = _enCurso.GetOrAdd(fecha, f => new Lazy<Task<clsResultado<clsTrm>>>(() => ConsultarAsync(f)));
        return compartida.Value.WaitAsync(cancellationToken);
    }

    private async Task<clsResultado<clsTrm>> ConsultarAsync(DateOnly fecha)
    {
        try
        {
            clsResultado<clsTrm> resultado;
            try
            {
                resultado = await fuente.ObtenerAsync(fecha, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // El adaptador no lanza por la fuente; si algo inesperado lanza, los que esperan reciben un fallo controlado.
                resultado = new clsErrorDominio(clsCodigosError.TrmNoDisponible, "No se pudo obtener la TRM oficial del día.", ex.GetType().Name);
            }

            Guardar(fecha, resultado);
            return resultado;
        }
        finally
        {
            _enCurso.TryRemove(fecha, out _);
        }
    }

    private void Guardar(DateOnly fecha, clsResultado<clsTrm> resultado)
    {
        var ahora = reloj.AhoraUtc;
        foreach (var vencida in _entradas.Where(e => ahora >= e.Value.Vence).Select(e => e.Key).ToList())
        {
            _entradas.TryRemove(vencida, out _);
        }

        _entradas[fecha] = new clsEntrada(resultado, ahora + (resultado.EsExitoso ? Duracion : DuracionFallo));
    }

    private sealed record clsEntrada(clsResultado<clsTrm> Resultado, DateTimeOffset Vence);
}
