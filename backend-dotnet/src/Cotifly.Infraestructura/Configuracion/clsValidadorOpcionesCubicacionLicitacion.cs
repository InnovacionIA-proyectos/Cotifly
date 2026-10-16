using Cotifly.Aplicacion.Licitaciones.Services;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// HU-023.5 (#160): <c>Cubicador:ReclamoSegundos</c> debe ser mayor que lo que puede durar la llamada de una ruta al cubicador más
/// <see cref="MargenSegundos"/>: si el reclamo de una ruta vence antes de que termine su llamada, otra réplica la tomaría y la llamaría
/// dos veces. D3b (E2E 2026-10-16): la llamada es el <c>POST /recomendaciones</c> (<c>Cubicador:TiempoEsperaRecomendacionSegundos</c>) y,
/// si no responde, la reconsulta con GET y sus reintentos (<see cref="Cubicacion.Services.clsCubicadorHttp.TiempoTotal"/> de
/// <c>Cubicador:TiempoEsperaSegundos</c>).
/// </summary>
public sealed class clsValidadorOpcionesCubicacionLicitacion(IOptions<clsOpcionesCubicador> cubicador) : IValidateOptions<clsOpcionesCubicacionLicitacion>
{
    /// <summary>Margen sobre el tiempo de espera de una llamada (guardar el resultado, la base, el cupo).</summary>
    public const int MargenSegundos = 30;

    public ValidateOptionsResult Validate(string? name, clsOpcionesCubicacionLicitacion options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var minimo = Minimo(cubicador.Value);
        return options.ReclamoSegundos > minimo
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"'{clsOpcionesCubicacionLicitacion.Seccion}:ReclamoSegundos' ({options.ReclamoSegundos}) debe ser mayor que "
                + $"'{clsOpcionesCubicador.Seccion}:TiempoEsperaRecomendacionSegundos' + la reconsulta con reintentos de "
                + $"'{clsOpcionesCubicador.Seccion}:TiempoEsperaSegundos' + {MargenSegundos} s ({minimo}).");
    }

    /// <summary>Segundos que puede durar la llamada de una ruta (POST y reconsulta con sus reintentos) más el margen.</summary>
    public static int Minimo(clsOpcionesCubicador cubicador)
    {
        ArgumentNullException.ThrowIfNull(cubicador);
        var reconsulta = Cubicacion.Services.clsCubicadorHttp.TiempoTotal(TimeSpan.FromSeconds(cubicador.TiempoEsperaSegundos));
        return cubicador.TiempoEsperaRecomendacionSegundos + (int)Math.Ceiling(reconsulta.TotalSeconds) + MargenSegundos;
    }
}
