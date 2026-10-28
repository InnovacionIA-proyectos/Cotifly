using Cotifly.Aplicacion.Cotizaciones.Services;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// HU-018.1 (#155, parte B): la lista de <c>Resultado:MotivosPerdida</c> es válida (cada motivo de máximo 100 caracteres, el límite de
/// <c>motivo</c> en Nexo) y <c>Resultado:MotivoConComentario</c> está en ella. Con una configuración inválida la API no arranca: un motivo
/// que Nexo rechaza dejaría al comercial sin poder marcar la perdida.
/// </summary>
public sealed class clsValidadorOpcionesResultado : IValidateOptions<clsOpcionesResultado>
{
    public ValidateOptionsResult Validate(string? name, clsOpcionesResultado options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errores = options.Errores();
        return errores.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errores);
    }
}
