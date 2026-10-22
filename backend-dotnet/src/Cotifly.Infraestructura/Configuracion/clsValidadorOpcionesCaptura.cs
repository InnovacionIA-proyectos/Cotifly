using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Dominio.Cotizaciones;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// HU-032 (#197, decisión del usuario del 2026-10-21): cada valor de <c>Captura:ClasesSinVehiculoObligatorio</c> debe ser una clase de
/// viaje de Nexo escrita exactamente como en su catálogo (<see cref="clsCatalogosCaptura.ClasesViaje"/>; se recorta). Un valor desconocido
/// (<c>Carga suelta</c>, <c>CargaSuelta</c>, vacío) no eximiría ninguna ruta y el comercial vería <c>VEHICULO_REQUERIDO</c> donde no se
/// esperaba: con él la API no arranca.
/// </summary>
public sealed class clsValidadorOpcionesCaptura : IValidateOptions<clsOpcionesCaptura>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesCaptura options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var desconocidas = (options.ClasesSinVehiculoObligatorio ?? [])
            .Where(c => string.IsNullOrWhiteSpace(c) || !clsCatalogosCaptura.Contiene(clsCatalogosCaptura.ClasesViaje, c.Trim()))
            .Select(c => $"'{c}'")
            .ToList();
        return desconocidas.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"'{clsOpcionesCaptura.Seccion}:ClasesSinVehiculoObligatorio' solo admite clases de viaje de Nexo escritas como en su catálogo "
                + $"({string.Join(", ", clsCatalogosCaptura.ClasesViaje)}); no lo son: {string.Join(", ", desconocidas)}.");
    }
}
