using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Configuracion;

/// <summary>
/// Patrón base de configuración de Cotifly (HU-001, principio asignado a HU-003.2):
/// cada sección se enlaza a una clase de opciones, se valida con DataAnnotations y con
/// el validador de secretos, y se valida al arrancar (<c>ValidateOnStart</c>): con
/// configuración inválida la aplicación no arranca.
/// </summary>
/// <remarks>
/// Cómo agrega su opción una HU (ej. #9 <c>ConnectionStrings</c>, #12 <c>Jwt</c>/<c>IA</c>):
/// <list type="number">
/// <item>Crear <c>clsOpcionesXxx</c> en <c>Configuracion/</c> con <c>const string Seccion</c>,
/// DataAnnotations (<c>[Required]</c>, <c>[Range]</c>…) y <c>[clsSecreto]</c> en las propiedades secretas.</item>
/// <item>Si hay reglas que DataAnnotations no cubre, crear <c>clsValidadorOpcionesXxx : IValidateOptions&lt;clsOpcionesXxx&gt;</c>.</item>
/// <item>En <c>AddInfraestructura</c>: <c>servicios.AddOpcionesValidadas&lt;clsOpcionesXxx&gt;(configuracion, clsOpcionesXxx.Seccion)</c>
/// y, si aplica, <c>servicios.AddSingleton&lt;IValidateOptions&lt;clsOpcionesXxx&gt;, clsValidadorOpcionesXxx&gt;()</c>.</item>
/// <item>Documentar la variable en <c>.env.example</c> (separador <c>__</c>).</item>
/// </list>
/// </remarks>
public static class clsRegistroOpciones
{
    public static OptionsBuilder<TOpciones> AddOpcionesValidadas<TOpciones>(
        this IServiceCollection servicios, IConfiguration configuracion, string seccion)
        where TOpciones : class
    {
        ArgumentNullException.ThrowIfNull(servicios);
        ArgumentNullException.ThrowIfNull(configuracion);
        ArgumentException.ThrowIfNullOrWhiteSpace(seccion);

        servicios.AddSingleton<IValidateOptions<TOpciones>>(sp =>
            new clsValidadorSecretosOpciones<TOpciones>(sp.GetRequiredService<IHostEnvironment>(), seccion));

        return servicios.AddOptions<TOpciones>()
            .Bind(configuracion.GetSection(seccion))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
