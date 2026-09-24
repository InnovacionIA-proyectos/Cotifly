using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura;

/// <summary>
/// Registro de los adaptadores de infraestructura que implementan los puertos de Aplicación
/// y de las opciones de configuración validadas al arrancar (ver <see cref="clsRegistroOpciones"/>).
/// Es el único tipo de esta capa que la Api puede usar (ADR-002, verificado en Cotifly.Arquitectura.Tests).
/// </summary>
public static class clsDependenciasInfraestructura
{
    public static IServiceCollection AddInfraestructura(this IServiceCollection servicios, IConfiguration configuracion)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        ArgumentNullException.ThrowIfNull(configuracion);

        // Opciones (cada HU agrega las suyas aquí: #9 ConnectionStrings, #12 Jwt/IA/GChat...).
        servicios.AddOpcionesValidadas<clsOpcionesZona>(configuracion, clsOpcionesZona.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesZona>, clsValidadorOpcionesZona>();

        // Adaptadores de puertos transversales.
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<IClsReloj, clsRelojSistema>();

        return servicios;
    }
}
