using Cotifly.Aplicacion.Licitaciones.Services;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// HU-023.5 (#160): <c>Cubicador:ReclamoSegundos</c> debe ser mayor que lo que puede durar la llamada de una ruta al cubicador más
/// <see cref="MargenSegundos"/>: si el reclamo de una ruta vence antes de que termine su llamada, otra réplica la tomaría y la llamaría
/// dos veces. La llamada (<see cref="Cubicacion.Services.clsCubicadorHttp.DuracionMaximaLlamada"/>) es la espera por el cupo global, el
/// <c>POST /recomendaciones</c> y, si no responde, la espera previa y la reconsulta con GET. Opción C (D3, E2E 2026-10-16):
/// <c>Cubicador:MaximoParalelo</c> no puede superar <c>Cubicador:MaximoConcurrente</c>, el cupo global del proceso.
/// </summary>
public sealed class clsValidadorOpcionesCubicacionLicitacion(IOptions<clsOpcionesCubicador> cubicador) : IValidateOptions<clsOpcionesCubicacionLicitacion>
{
    /// <summary>Margen sobre el tiempo de espera de una llamada (guardar el resultado, la base, el cupo).</summary>
    public const int MargenSegundos = 30;

    public ValidateOptionsResult Validate(string? name, clsOpcionesCubicacionLicitacion options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = new List<string>();
        var minimo = Minimo(cubicador.Value);
        if (options.ReclamoSegundos <= minimo)
        {
            errores.Add($"'{clsOpcionesCubicacionLicitacion.Seccion}:ReclamoSegundos' ({options.ReclamoSegundos}) debe ser mayor que "
                + $"'{clsOpcionesCubicador.Seccion}:EsperaMaximaCupoSegundos' + '{clsOpcionesCubicador.Seccion}:TiempoEsperaRecomendacionSegundos' + 2 + "
                + $"'{clsOpcionesCubicador.Seccion}:EsperaAntesReconsultaSegundos' + '{clsOpcionesCubicador.Seccion}:TiempoEsperaReconsultaSegundos' + "
                + $"{MargenSegundos} s ({minimo}).");
        }

        if (options.MaximoParalelo > cubicador.Value.MaximoConcurrente)
        {
            errores.Add($"'{clsOpcionesCubicacionLicitacion.Seccion}:MaximoParalelo' ({options.MaximoParalelo}) no puede superar "
                + $"'{clsOpcionesCubicador.Seccion}:MaximoConcurrente' ({cubicador.Value.MaximoConcurrente}): el cupo global de llamadas al cubicador.");
        }

        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }

    /// <summary>Segundos que puede durar la llamada de una ruta (cupo, POST, espera y reconsulta) más el margen.</summary>
    public static int Minimo(clsOpcionesCubicador cubicador)
    {
        ArgumentNullException.ThrowIfNull(cubicador);
        return (int)Math.Ceiling(Cubicacion.Services.clsCubicadorHttp.DuracionMaximaLlamada(cubicador).TotalSeconds) + MargenSegundos;
    }
}
