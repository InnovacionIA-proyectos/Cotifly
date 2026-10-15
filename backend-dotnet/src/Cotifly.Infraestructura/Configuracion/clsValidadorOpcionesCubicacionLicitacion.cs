using Cotifly.Aplicacion.Licitaciones.Services;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// HU-023.5 (#160): <c>Cubicador:ReclamoSegundos</c> debe ser mayor que <c>Cubicador:TiempoEsperaSegundos</c> más
/// <see cref="MargenSegundos"/>: si el reclamo de una ruta vence antes de que termine su llamada al cubicador, otra réplica la tomaría y
/// la llamaría dos veces.
/// </summary>
public sealed class clsValidadorOpcionesCubicacionLicitacion(IOptions<clsOpcionesCubicador> cubicador) : IValidateOptions<clsOpcionesCubicacionLicitacion>
{
    /// <summary>Margen sobre el tiempo de espera de una llamada (guardar el resultado, la base, el cupo).</summary>
    public const int MargenSegundos = 30;

    public ValidateOptionsResult Validate(string? name, clsOpcionesCubicacionLicitacion options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var minimo = cubicador.Value.TiempoEsperaSegundos + MargenSegundos;
        return options.ReclamoSegundos > minimo
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"'{clsOpcionesCubicacionLicitacion.Seccion}:ReclamoSegundos' ({options.ReclamoSegundos}) debe ser mayor que "
                + $"'{clsOpcionesCubicador.Seccion}:TiempoEsperaSegundos' + {MargenSegundos} s ({minimo}).");
    }
}
