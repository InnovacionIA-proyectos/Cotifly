using System.Collections.Concurrent;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Dominio.Comun;

namespace Cotifly.Infraestructura.Precio.Services;

/// <summary>
/// Caché de la TRM por fecha de negocio (ADR-014 §2): la primera TRM válida que se obtiene para un día queda fija hasta el fin de
/// ese día en America/Bogota, así todas las cotizaciones del día usan la misma TRM (mañana y tarde). Decorador de
/// <see cref="IClsProveedorTrm"/> (singleton). Cada TRM que entrega se registra en <see cref="clsMemoriaTrm"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>La clave es la fecha que pide el caso de uso (el "hoy" de America/Bogota, <c>IClsCalendarioNegocio</c>), nunca la fecha UTC.</item>
/// <item>Una TRM vive hasta las 00:00 del día siguiente a su fecha en la zona de negocio (<see cref="IClsCalendarioNegocio.InicioDelDia"/>);
/// para una fecha ya pasada, <see cref="Duracion"/> desde que se obtuvo. Al guardar se descartan las entradas vencidas.</item>
/// <item>Consulta compartida por fecha: las llamadas simultáneas sin caché esperan la misma consulta a la fuente y reciben el mismo
/// resultado, también si falla (una fuente caída no encola N consultas de N comerciales). La consulta compartida no usa el token de
/// ningún llamador (el adaptador tiene su propio límite de tiempo); cada llamador puede dejar de esperar con el suyo.</item>
/// <item>Un fallo se recuerda solo <see cref="DuracionFallo"/> (caché negativa): durante ese lapso los reintentos responden el mismo
/// <c>TRM_NO_DISPONIBLE</c> sin golpear la fuente; después, el comercial puede reintentar. La TRM es pública e igual para todos los
/// comerciales: compartir el fallo no filtra nada.</item>
/// </list>
/// </remarks>
public sealed class clsProveedorTrmEnCache(
    IClsProveedorTrm fuente, IClsReloj reloj, IClsCalendarioNegocio calendario, clsMemoriaTrm memoria) : IClsProveedorTrm
{
    /// <summary>Vida de una TRM de una fecha ya pasada (la de hoy o una futura vive hasta el fin de su día).</summary>
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

        if (resultado.EsFallo)
        {
            _entradas[fecha] = new clsEntrada(resultado, ahora + DuracionFallo);
            return;
        }

        memoria.Registrar(resultado.Valor);
        var finDelDia = calendario.InicioDelDia(fecha.AddDays(1));
        _entradas[fecha] = new clsEntrada(resultado, finDelDia > ahora ? finDelDia : ahora + Duracion);
    }

    private sealed record clsEntrada(clsResultado<clsTrm> Resultado, DateTimeOffset Vence);
}
