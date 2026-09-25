using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Tarifas.Interfaces;
using Cotifly.Infraestructura.Agente.Repositories;
using Cotifly.Infraestructura.Auditoria.Repositories;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Correo.Repositories;
using Cotifly.Infraestructura.Correo.Services;
using Cotifly.Infraestructura.Cotizaciones.Repositories;
using Cotifly.Infraestructura.Identidad.Persistence;
using Cotifly.Infraestructura.Identidad.Repositories;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Infraestructura.Margen.Repositories;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Metricas.Repositories;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Infraestructura.Tarifas.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Polly.Telemetry;

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

        // Opciones (cada HU agrega las suyas aquí: #12 Jwt, luego IA/GChat...).
        servicios.AddOpcionesValidadas<clsOpcionesZona>(configuracion, clsOpcionesZona.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesZona>, clsValidadorOpcionesZona>();
        servicios.AddOpcionesValidadas<clsOpcionesConexiones>(configuracion, clsOpcionesConexiones.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesConexiones>, clsValidadorOpcionesConexiones>();
        servicios.AddOpcionesValidadas<clsOpcionesIdentidad>(configuracion, clsOpcionesIdentidad.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesMargen>(configuracion, clsOpcionesMargen.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesSapTm>(configuracion, clsOpcionesSapTm.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesSapTm>, clsValidadorOpcionesSapTm>();
        servicios.AddOpcionesValidadas<clsOpcionesPdf>(configuracion, clsOpcionesPdf.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesAlmacenPdf>(configuracion, clsOpcionesAlmacenPdf.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesAlmacenPdf>, clsValidadorOpcionesAlmacenPdf>();
        servicios.AddOpcionesValidadas<clsOpcionesCorreo>(configuracion, clsOpcionesCorreo.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesCorreoTransporte>(configuracion, clsOpcionesCorreoTransporte.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesCorreoTransporte>, clsValidadorOpcionesCorreoTransporte>();
        servicios.AddOpcionesValidadas<clsOpcionesJwt>(configuracion, clsOpcionesJwt.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesJwt>, clsValidadorOpcionesJwt>();

        // Adaptadores de puertos transversales.
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<IClsReloj, clsRelojSistema>();
        servicios.TryAddSingleton<IClsCalendarioNegocio, clsCalendarioNegocio>();

        AgregarPersistencia(servicios);

        // Fuentes externas de Margen (#13) y Tarifas (#10). TryAdd: un adaptador registrado antes (pruebas, HU-005) se respeta.
        AgregarFuenteMargen(servicios);
        servicios.TryAddSingleton<IClsTarifaProvider, clsTarifaProviderNoDisponible>();

        AgregarPdfYCorreo(servicios);

        AgregarIdentidad(servicios);

        return servicios;
    }

    /// <summary>
    /// Login SSO ClientePro (#12, ADR-009): almacén corporativo de solo lectura, emisión de JWT y purga horaria de tokens
    /// vencidos. TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarIdentidad(IServiceCollection servicios)
    {
        servicios.TryAddSingleton<IClsConfiguracionJwt, clsConfiguracionJwt>();
        servicios.TryAddSingleton<IClsEmisorTokens, clsEmisorTokensJwt>();
        servicios.TryAddScoped<IClsAlmacenIdentidadExterno, clsAlmacenIdentidadSolucionesTi>();
        servicios.AddHostedService<clsLimpiezaTokensHostedService>();
    }

    /// <summary>
    /// PDF (MigraDoc con fuentes embebidas, almacén en volumen) y transporte de correo elegido por <c>Correo:Modo</c> (#11).
    /// TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarPdfYCorreo(IServiceCollection servicios)
    {
        servicios.TryAddSingleton<IClsGeneradorPdf, clsGeneradorPdfMigraDoc>();
        servicios.TryAddSingleton<IClsAlmacenPdf, clsAlmacenPdfVolumen>();
        servicios.TryAddSingleton<clsTransporteSmtpRelay>();
        servicios.TryAddSingleton<IClsTransporteCorreo>(clsFabricaTransporteCorreo.Crear);
    }

    /// <summary>
    /// Fuente SAP TM elegida por <c>SapTm:Modo</c> al resolver (<see cref="clsFabricaFuenteMargen"/>): <c>export_json</c>,
    /// <c>odata</c> o, sin modo, la no configurada (#10). El cliente OData es tipado (<c>IHttpClientFactory</c>) con la tubería
    /// estándar de resiliencia (10 s en total, reintentos ante fallos transitorios) y sin registrar cabeceras en los logs.
    /// </summary>
    private static void AgregarFuenteMargen(IServiceCollection servicios)
    {
        servicios.TryAddSingleton<clsFuenteMargenNoConfigurada>();
        servicios.TryAddSingleton<clsFuenteMargenExportJson>();
        servicios.AddHttpClient<clsFuenteMargenOData>(clsFuenteMargenOData.NombreCliente)
            .RedactLoggedHeaders(_ => true)
            .AddStandardResilienceHandler(clsFuenteMargenOData.ConfigurarResiliencia);
        servicios.TryAddTransient<IClsFuenteMargen>(clsFabricaFuenteMargen.Crear);

        // Los eventos de Polly de cada intento (reintento, timeout) traen la traza completa: en esta tubería bajan a
        // Debug; el adaptador deja una sola línea de warning por consulta fallida.
        servicios.Configure<TelemetryOptions>(telemetria =>
        {
            var anterior = telemetria.SeverityProvider;
            telemetria.SeverityProvider = argumentos =>
                argumentos.Source.PipelineName?.StartsWith(clsFuenteMargenOData.NombreCliente + "-", StringComparison.Ordinal) == true
                    ? ResilienceEventSeverity.Debug
                    : anterior?.Invoke(argumentos) ?? argumentos.Event.Severity;
        });

        servicios.AddHostedService<clsAvisosArranqueSapTm>();
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

        // dbsolucionesti: solo lectura (usuarios y roles de ClientePro para el login, #12).
        servicios.AddDbContext<clsIdentidadDbContext>((proveedor, opciones) =>
            ConfigurarSqlServer(opciones, proveedor.GetRequiredService<IOptions<clsOpcionesConexiones>>().Value.Identidad));

        servicios.AddScoped<IClsUnitOfWork, clsUnitOfWork>();
        servicios.AddScoped<IClsCotizacionRepository, clsCotizacionRepository>();
        servicios.AddScoped<IClsMargenPoliticaRepository, clsMargenPoliticaRepository>();
        servicios.AddScoped<IClsSapMapeoRepository, clsSapMapeoRepository>();
        servicios.AddScoped<IClsMargenAjusteRepository, clsMargenAjusteRepository>();
        servicios.AddScoped<IClsUsuarioWebRepository, clsUsuarioWebRepository>();
        servicios.AddScoped<IClsRolRepository, clsRolRepository>();
        servicios.AddScoped<IClsTokenRefrescoRepository, clsTokenRefrescoRepository>();
        servicios.AddScoped<IClsTokenRevocadoRepository, clsTokenRevocadoRepository>();
        servicios.AddScoped<IClsCorreoRepository, clsCorreoRepository>();
        servicios.AddScoped<IClsAuditoriaRepository, clsAuditoriaRepository>();
        servicios.AddScoped<IClsSesionRepository, clsSesionRepository>();
        servicios.AddScoped<IClsMetricasRepository, clsMetricasRepository>();
        servicios.AddScoped<IClsCandadoTransaccional, clsCandadoTransaccionalSql>();

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
