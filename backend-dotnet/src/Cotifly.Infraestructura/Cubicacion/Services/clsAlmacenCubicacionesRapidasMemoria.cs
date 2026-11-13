using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cubicacion.DTOs;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Cubicacion.Services;

/// <summary>
/// Cubicaciones rápidas en la memoria del proceso. La API corre con <b>una réplica</b>
/// (<c>infra/stack.dotnet.*.yml</c>, <c>replicas: 1</c>), así que basta. Cada cubicación vive <c>Cubicador:RetencionCubicacionMinutos</c>
/// (15 por defecto) desde su último cambio y caben <c>Cubicador:MaximoCubicacionesEnMemoria</c> (500). Lleno, se descartan primero las
/// terminadas más viejas; si todas siguen en proceso, no se acepta otra.
/// </summary>
/// <remarks>
/// Limitaciones (documentadas para el front): si el proceso se reinicia (despliegue, caída) las cubicaciones se pierden y
/// <c>GET /web/cubicacion/{id}</c> responde 404: el front vuelve a cubicar. Durante un despliegue <c>start-first</c> corren dos tareas unos
/// segundos y una consulta puede caer en la otra (404). Con más de una réplica hace falta una tabla compartida en vez de esta clase.
/// </remarks>
public sealed class clsAlmacenCubicacionesRapidasMemoria(IClsReloj reloj, IOptions<clsOpcionesCubicador> opciones) : IClsAlmacenCubicacionesRapidas
{
    private readonly Lock _candado = new();
    private readonly Dictionary<Guid, clsCubicacionRapida> _cubicaciones = [];

    private TimeSpan Retencion => TimeSpan.FromMinutes(opciones.Value.RetencionCubicacionMinutos);

    /// <summary>Cubicaciones guardadas ahora (vencidas incluidas hasta la siguiente limpieza; para pruebas).</summary>
    public int Cantidad
    {
        get
        {
            lock (_candado)
            {
                return _cubicaciones.Count;
            }
        }
    }

    public clsCubicacionRapida? Crear(Guid usuarioId)
    {
        var ahora = reloj.AhoraUtc;
        lock (_candado)
        {
            Limpiar(ahora);
            var maximo = Math.Max(1, opciones.Value.MaximoCubicacionesEnMemoria);
            while (_cubicaciones.Count >= maximo)
            {
                var vieja = _cubicaciones.Values.Where(c => c.Terminada).OrderBy(c => c.ExpiraUtc).FirstOrDefault();
                if (vieja is null)
                {
                    return null;
                }

                _cubicaciones.Remove(vieja.Id);
            }

            var nueva = new clsCubicacionRapida(Guid.NewGuid(), usuarioId, clsEstadosCubicacionRapida.EnProceso, ahora, ahora + Retencion);
            _cubicaciones[nueva.Id] = nueva;
            return nueva;
        }
    }

    public clsCubicacionRapida? Obtener(Guid id, Guid usuarioId)
    {
        var ahora = reloj.AhoraUtc;
        lock (_candado)
        {
            if (!_cubicaciones.TryGetValue(id, out var cubicacion))
            {
                return null;
            }

            if (cubicacion.ExpiraUtc <= ahora)
            {
                _cubicaciones.Remove(id);
                return null;
            }

            // Sin IDOR: la de otro usuario es igual que una que no existe.
            return cubicacion.UsuarioId == usuarioId ? cubicacion : null;
        }
    }

    public void Completar(Guid id, clsResultadoCubicacion resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);
        Terminar(id, c => c with { Estado = clsEstadosCubicacionRapida.Completada, Resultado = resultado, Error = null });
    }

    public void Fallar(Guid id, clsErrorDominio error)
    {
        ArgumentNullException.ThrowIfNull(error);
        Terminar(id, c => c with { Estado = clsEstadosCubicacionRapida.Fallida, Resultado = null, Error = error });
    }

    public void Descartar(Guid id)
    {
        lock (_candado)
        {
            _cubicaciones.Remove(id);
        }
    }

    private void Terminar(Guid id, Func<clsCubicacionRapida, clsCubicacionRapida> cambio)
    {
        var ahora = reloj.AhoraUtc;
        lock (_candado)
        {
            if (_cubicaciones.TryGetValue(id, out var cubicacion))
            {
                _cubicaciones[id] = cambio(cubicacion) with { ExpiraUtc = ahora + Retencion };
            }
        }
    }

    private void Limpiar(DateTimeOffset ahora)
    {
        foreach (var vencida in _cubicaciones.Values.Where(c => c.ExpiraUtc <= ahora).Select(c => c.Id).ToList())
        {
            _cubicaciones.Remove(vencida);
        }
    }
}
