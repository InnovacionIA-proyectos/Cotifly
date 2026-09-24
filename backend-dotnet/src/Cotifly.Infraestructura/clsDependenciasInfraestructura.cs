using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Infraestructura.Agente.Repositories;
using Cotifly.Infraestructura.Auditoria.Repositories;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Correo.Repositories;
using Cotifly.Infraestructura.Cotizaciones.Repositories;
using Cotifly.Infraestructura.Identidad.Persistence;
using Cotifly.Infraestructura.Identidad.Repositories;
using Cotifly.Infraestructura.Margen.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    /// <summary>Etiqueta de los health checks que decide <c>GET /health/ready</c>.</summary>
    public const string EtiquetaReadiness = "ready";

    public static IServiceCollection AddInfraestructura(this IServiceCollection servicios, IConfiguration configuracion)
    {
        ArgumentNullException.ThrowIfNull(servicios);
        ArgumentNullException.ThrowIfNull(configuracion);

        // Opciones (cada HU agrega las suyas aquí: #12 Jwt/IA/GChat...).
        servicios.AddOpcionesValidadas<clsOpcionesZona>(configuracion, clsOpcionesZona.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesZona>, clsValidadorOpcionesZona>();
        servicios.AddOpcionesValidadas<clsOpcionesConexiones>(configuracion, clsOpcionesConexiones.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesConexiones>, clsValidadorOpcionesConexiones>();
        servicios.AddOpcionesValidadas<clsOpcionesIdentidad>(configuracion, clsOpcionesIdentidad.Seccion);

        // Adaptadores de puertos transversales.
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<IClsReloj, clsRelojSistema>();

        AgregarPersistencia(servicios);

        return servicios;
    }

    private static void AgregarPersistencia(IServiceCollection servicios)
    {
        servicios.AddSingleton<clsMarcasTiempoInterceptor>();

        // dbo_cotifly: lectura y escritura (ADR-008). Sin cadena (fuera de Production) la API arranca
        // igual y /health/ready responde 503.
        servicios.AddDbContext<clsCotiflyDbContext>((proveedor, opciones) =>
        {
            var cadena = proveedor.GetRequiredService<IOptions<clsOpcionesConexiones>>().Value.Cotifly;
            ConfigurarSqlServer(opciones, cadena);
            opciones.AddInterceptors(proveedor.GetRequiredService<clsMarcasTiempoInterceptor>());
        });

        // dbsolucionesti: solo lectura (login, #12).
        servicios.AddDbContext<clsIdentidadDbContext>((proveedor, opciones) =>
            ConfigurarSqlServer(opciones, proveedor.GetRequiredService<IOptions<clsOpcionesConexiones>>().Value.Identidad));

        servicios.AddScoped<IClsUnitOfWork, clsUnitOfWork>();
        servicios.AddScoped<IClsCotizacionRepository, clsCotizacionRepository>();
        servicios.AddScoped<IClsMargenPoliticaRepository, clsMargenPoliticaRepository>();
        servicios.AddScoped<IClsSapMapeoRepository, clsSapMapeoRepository>();
        servicios.AddScoped<IClsMargenAjusteRepository, clsMargenAjusteRepository>();
        servicios.AddScoped<IClsUsuarioWebRepository, clsUsuarioWebRepository>();
        servicios.AddScoped<IClsCorreoRepository, clsCorreoRepository>();
        servicios.AddScoped<IClsAuditoriaRepository, clsAuditoriaRepository>();
        servicios.AddScoped<IClsSesionRepository, clsSesionRepository>();

        servicios.AddHealthChecks()
            .AddCheck<clsBaseDatosHealthCheck>(clsBaseDatosHealthCheck.Nombre, tags: [EtiquetaReadiness]);
    }

    private static void ConfigurarSqlServer(DbContextOptionsBuilder opciones, string? cadena)
    {
        void Sql(SqlServerDbContextOptionsBuilder sql) => sql.UseCompatibilityLevel(clsCotiflyDbContext.NivelCompatibilidad);

        if (string.IsNullOrWhiteSpace(cadena))
        {
            opciones.UseSqlServer(Sql);
        }
        else
        {
            opciones.UseSqlServer(cadena, Sql);
        }
    }
}
