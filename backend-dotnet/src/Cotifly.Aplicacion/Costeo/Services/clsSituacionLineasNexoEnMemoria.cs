using System.Collections.Concurrent;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Implementación de <see cref="IClsSituacionLineasNexo"/> en la memoria del proceso (singleton). El polling y la API corren en el mismo
/// proceso y el servicio tiene una réplica (<c>infra/stack.dotnet.*.yml</c>): lo que lee el polling lo ve <c>GET .../costeo</c>. Tras un
/// reinicio la situación vuelve con la siguiente consulta del polling (como mucho <c>CosteoNexo:IntervaloMaximoMinutos</c>). Cada
/// cotización guarda solo su última foto y se olvida pasada <see cref="Retencion"/> sin lecturas nuevas: el polling deja de consultar a
/// las <c>CosteoNexo:EdadMaximaHoras</c> (72 h por defecto).
/// </summary>
public sealed class clsSituacionLineasNexoEnMemoria(IClsReloj reloj) : IClsSituacionLineasNexo
{
    /// <summary>Tiempo que se conserva la última foto de una cotización sin que el polling la vuelva a leer.</summary>
    public static readonly TimeSpan Retencion = TimeSpan.FromHours(96);

    private readonly ConcurrentDictionary<Guid, (DateTimeOffset LeidaAt, IReadOnlyDictionary<Guid, clsSituacionLineaNexo> PorCamion)> _fotos = new();

    public void Registrar(Guid cotizacionId, IReadOnlyDictionary<Guid, clsSituacionLineaNexo> porCamion)
    {
        ArgumentNullException.ThrowIfNull(porCamion);
        var ahora = reloj.AhoraUtc;
        if (porCamion.Count == 0)
        {
            _fotos.TryRemove(cotizacionId, out _);
        }
        else
        {
            _fotos[cotizacionId] = (ahora, new Dictionary<Guid, clsSituacionLineaNexo>(porCamion));
        }

        foreach (var (id, foto) in _fotos)
        {
            if (ahora - foto.LeidaAt > Retencion)
            {
                _fotos.TryRemove(id, out _);
            }
        }
    }

    public IReadOnlyDictionary<Guid, clsSituacionLineaNexo>? Obtener(Guid cotizacionId) =>
        _fotos.TryGetValue(cotizacionId, out var foto) && reloj.AhoraUtc - foto.LeidaAt <= Retencion ? foto.PorCamion : null;
}
