using Cotifly.Aplicacion.Agente.Interfaces;
using Cotifly.Aplicacion.Agente.Repositories;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.CatalogosFlete.Interfaces;
using Cotifly.Aplicacion.Clientes.Interfaces;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Correo.Interfaces;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Costeo.Interfaces;
using Cotifly.Aplicacion.Costeo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Cubicacion.Interfaces;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Aplicacion.Margen.Repositories;
using Cotifly.Aplicacion.Margen.Services;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Aplicacion.Municipios.Interfaces;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Aplicacion.Precio.Interfaces;
using Cotifly.Infraestructura.Agente.Repositories;
using Cotifly.Infraestructura.Agente.Services;
using Cotifly.Infraestructura.Auditoria.Repositories;
using Cotifly.Infraestructura.CatalogosFlete.Services;
using Cotifly.Infraestructura.Clientes.Services;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Correo.Repositories;
using Cotifly.Infraestructura.Correo.Services;
using Cotifly.Infraestructura.Costeo.Services;
using Cotifly.Infraestructura.Cotizaciones.Repositories;
using Cotifly.Infraestructura.Cubicacion.Services;
using Cotifly.Infraestructura.Identidad.Persistence;
using Cotifly.Infraestructura.Identidad.Repositories;
using Cotifly.Infraestructura.IA.Services;
using Cotifly.Infraestructura.Identidad.Services;
using Cotifly.Infraestructura.Margen.Repositories;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Metricas.Repositories;
using Cotifly.Infraestructura.Municipios.Services;
using Cotifly.Infraestructura.Pdf.Services;
using Cotifly.Infraestructura.Precio.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
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

        // Opciones (cada HU agrega las suyas aquí: #12 Jwt, luego IA, Nexo, TRM...).
        servicios.AddOpcionesValidadas<clsOpcionesZona>(configuracion, clsOpcionesZona.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesZona>, clsValidadorOpcionesZona>();
        servicios.AddOpcionesValidadas<clsOpcionesConexiones>(configuracion, clsOpcionesConexiones.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesConexiones>, clsValidadorOpcionesConexiones>();
        servicios.AddOpcionesValidadas<clsOpcionesIdentidad>(configuracion, clsOpcionesIdentidad.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesRegional>(configuracion, clsOpcionesRegional.Seccion);
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
        servicios.AddOpcionesValidadas<clsOpcionesAgente>(configuracion, clsOpcionesAgente.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesSesiones>(configuracion, clsOpcionesSesiones.Seccion);
        servicios.AddOpcionesValidadas<Aplicacion.Redaccion.Services.clsOpcionesRedaccion>(configuracion, Aplicacion.Redaccion.Services.clsOpcionesRedaccion.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesIA>(configuracion, clsOpcionesIA.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesIA>, clsValidadorOpcionesIA>();
        servicios.AddOpcionesValidadas<clsOpcionesNexo>(configuracion, clsOpcionesNexo.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesCosteoNexo>(configuracion, clsOpcionesCosteoNexo.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesTrm>(configuracion, clsOpcionesTrm.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesTrm>, clsValidadorOpcionesTrm>();

        // Adaptadores de puertos transversales.
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<IClsReloj, clsRelojSistema>();
        servicios.TryAddSingleton<IClsCalendarioNegocio, clsCalendarioNegocio>();

        // Idempotency-Key de las mutaciones de la etapa final (ADR-018 §8, #100): en memoria, una sola réplica de la API.
        servicios.TryAddSingleton<IClsAlmacenIdempotencia, clsAlmacenIdempotenciaEnMemoria>();

        AgregarPersistencia(servicios);

        // Fuente externa de Margen (#13). TryAdd: un adaptador registrado antes (pruebas, HU-005) se respeta.
        AgregarFuenteMargen(servicios);

        // TRM oficial del día para cotizar en USD (HU-012.1, ADR-014).
        AgregarTrm(servicios);

        // Catálogos del formulario de captura que también proxean Nexo (municipios, tipos de vehículo/carrocería/viaje).
        AgregarMunicipios(servicios);
        AgregarCatalogosFlete(servicios);
        AgregarCubicacion(servicios);
        AgregarCosteo(servicios);

        AgregarPdfYCorreo(servicios);

        AgregarIdentidad(servicios);

        AgregarAgente(servicios);

        return servicios;
    }

    /// <summary>
    /// Asistente IA (#12 parte C, ADR-003, ADR-018): <see cref="IChatClient"/> por <c>IA:Proveedor</c>, redactor del PDF, búsqueda de
    /// clientes en Nexo (HU-005.2, elegida por <c>Nexo:UrlBase</c>: ver <see cref="AgregarBuscadorClientes"/>),
    /// candado por sesión y barrido de sesiones inactivas. Google Chat (webhook, cola, worker y publicador) se retiró en HU-013.9 (#105).
    /// TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarAgente(IServiceCollection servicios)
    {
        servicios.TryAddSingleton<IChatClient>(clsChatClientFactory.Crear);

        // Redactor del asunto y la introducción del PDF (HU-013.5) sobre el mismo IChatClient; el respaldo lo aplica Aplicación.
        servicios.TryAddScoped<Aplicacion.Redaccion.Interfaces.IClsRedactorCotizacion, Redaccion.Services.clsRedactorCotizacionLlm>();
        AgregarBuscadorClientes(servicios);

        servicios.TryAddSingleton<IClsCandadoSesion, clsCandadoSesionSql>();
        servicios.AddHostedService<clsBarridoSesionesHostedService>();
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

        // Regional del usuario (HU-010.1, ADR-016): tabla de ejecutivos de DWCubosEBSSAP si hay ConnectionStrings:Regional. La
        // usan el tablero del gerente y el login (clsVerificadorIdentidad, #81), que comparten el adaptador y la caché.
        servicios.TryAddSingleton<clsCacheDirectorioEjecutivos>();
        servicios.TryAddScoped<clsDirectorioEjecutivosDimEjecutivo>();
        servicios.TryAddSingleton<clsDirectorioEjecutivosNoDisponible>();
        servicios.TryAddScoped<IClsDirectorioEjecutivos>(clsFabricaDirectorioEjecutivos.Crear);
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

    /// <summary>
    /// TRM (HU-012.1, ADR-014): la cadena que elige <c>Trm:Proveedor</c> (<see cref="ProveedorTrm"/>) detrás de la caché por día
    /// <see cref="clsProveedorTrmEnCache"/>, todo singleton. Por defecto DolarApi (<see cref="clsProveedorTrmDolarApi"/>) con respaldo en los
    /// datos abiertos de la Superintendencia Financiera (<see cref="clsProveedorTrmSuperfinanciera"/>). Cada fuente tiene su cliente HTTP con
    /// nombre (se crea por consulta con <c>IHttpClientFactory</c>), la tubería estándar de resiliencia con el tiempo de
    /// <c>Trm:TiempoEsperaSegundos</c> y sin registrar cabeceras. Los eventos de Polly de cada intento bajan a Debug, como en SAP TM. TryAdd:
    /// un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarTrm(IServiceCollection servicios)
    {
        (string Cliente, Func<clsOpcionesTrm, int> Segundos)[] clientes =
        [
            (clsProveedorTrmSuperfinanciera.NombreCliente, o => o.TiempoEsperaSegundos),
            (clsProveedorTrmDolarApi.NombreCliente, o => o.TiempoEsperaDolarApiSegundos),
        ];
        foreach (var (cliente, segundos) in clientes)
        {
            var tuberia = servicios.AddHttpClient(cliente)
                .RedactLoggedHeaders(_ => true)
                .AddStandardResilienceHandler();

            // Las opciones de la tubería son opciones con nombre (el de la tubería): así toman el tiempo de Trm ya validado.
            servicios.AddOptions<HttpStandardResilienceOptions>(tuberia.PipelineName)
                .Configure<IOptions<clsOpcionesTrm>>((resiliencia, opciones) =>
                    clsResilienciaTrm.Configurar(resiliencia, TimeSpan.FromSeconds(segundos(opciones.Value))));
        }

        servicios.TryAddSingleton<clsMemoriaTrm>();
        servicios.TryAddSingleton<clsProveedorTrmSuperfinanciera>();
        servicios.TryAddSingleton<clsProveedorTrmDolarApi>();
        servicios.TryAddSingleton<IClsProveedorTrm>(proveedor => new clsProveedorTrmEnCache(
            ProveedorTrm(proveedor),
            proveedor.GetRequiredService<IClsReloj>(),
            proveedor.GetRequiredService<IClsCalendarioNegocio>(),
            proveedor.GetRequiredService<clsMemoriaTrm>()));

        servicios.Configure<TelemetryOptions>(telemetria =>
        {
            var anterior = telemetria.SeverityProvider;
            telemetria.SeverityProvider = argumentos =>
                argumentos.Source.PipelineName is { } nombre && clientes.Any(c => nombre.StartsWith(c.Cliente + "-", StringComparison.Ordinal))
                    ? ResilienceEventSeverity.Debug
                    : anterior?.Invoke(argumentos) ?? argumentos.Event.Severity;
        });
    }

    /// <summary>Fuente (o cadena de fuentes) de la TRM según <c>Trm:Proveedor</c>, ya validado al arrancar.</summary>
    private static IClsProveedorTrm ProveedorTrm(IServiceProvider proveedor) =>
        proveedor.GetRequiredService<IOptions<clsOpcionesTrm>>().Value.ProveedorNormalizado switch
        {
            clsOpcionesTrm.ProveedorSuperfinanciera => proveedor.GetRequiredService<clsProveedorTrmSuperfinanciera>(),
            clsOpcionesTrm.ProveedorDolarApi => proveedor.GetRequiredService<clsProveedorTrmDolarApi>(),
            _ => new clsProveedorTrmConRespaldo(
                proveedor.GetRequiredService<clsProveedorTrmDolarApi>(),
                proveedor.GetRequiredService<clsProveedorTrmSuperfinanciera>(),
                proveedor.GetRequiredService<Microsoft.Extensions.Logging.ILogger<clsProveedorTrmConRespaldo>>()),
        };

    /// <summary>
    /// Búsqueda de clientes elegida por <c>Nexo:UrlBase</c> al resolver (<see cref="clsFabricaBuscadorClientes"/>): configurada
    /// → <see cref="clsBuscadorClientesNexo"/>; vacía → <see cref="clsBuscadorClientesNoDisponible"/> (#10, HU-005.2). Cliente
    /// tipado con la tubería estándar de resiliencia (8 s en total) y sin registrar el header <c>X-Portal-Usuario-Id</c> en los
    /// logs. TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarBuscadorClientes(IServiceCollection servicios)
    {
        servicios.TryAddScoped<clsBuscadorClientesNoDisponible>();
        servicios.AddHttpClient<clsBuscadorClientesNexo>(clsBuscadorClientesNexo.NombreCliente)
            .RedactLoggedHeaders(_ => true)
            .AddStandardResilienceHandler(clsBuscadorClientesNexo.ConfigurarResiliencia);
        servicios.TryAddScoped<IClsBuscadorClientes>(clsFabricaBuscadorClientes.Crear);
    }

    /// <summary>
    /// Catálogo de municipios elegido por <c>Nexo:UrlBase</c> al resolver (<see cref="clsFabricaMunicipios.Crear"/>):
    /// configurada → <see cref="clsMunicipiosNexo"/>; vacía → <see cref="clsMunicipiosNoDisponible"/>. Mismo cliente
    /// HTTP tipado (nombre <see cref="clsBuscadorClientesNexo.NombreCliente"/>) que la búsqueda de clientes: es el
    /// mismo servicio Nexo. No se repite <c>.RedactLoggedHeaders(...).AddStandardResilienceHandler(...)</c> aquí:
    /// <c>IHttpClientFactory</c> asocia esa configuración al nombre <c>"Nexo"</c>, no al tipo, así que volver a
    /// encadenarla en cada cliente tipado apilaría una tubería de resiliencia por cada uno (reintentos y timeouts
    /// multiplicados); <see cref="AgregarBuscadorClientes"/> ya la registra una sola vez para ese nombre. TryAdd: un
    /// adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarMunicipios(IServiceCollection servicios)
    {
        servicios.TryAddScoped<clsMunicipiosNoDisponible>();
        servicios.TryAddSingleton<clsCacheCatalogoMunicipios>();
        servicios.AddHttpClient<clsMunicipiosNexo>(clsBuscadorClientesNexo.NombreCliente);
        servicios.TryAddScoped<IClsMunicipiosProvider>(clsFabricaMunicipios.Crear);
    }

    /// <summary>
    /// Catálogos de tipo de vehículo/carrocería/clase de viaje elegidos por <c>Nexo:UrlBase</c> al resolver
    /// (<see cref="clsFabricaCatalogosFlete.Crear"/>): configurada → <see cref="clsCatalogosFleteNexo"/> sobre los catálogos maestros
    /// (<see cref="clsFabricaCatalogosMaestros.Crear"/>, HU-013.10); vacía → <see cref="clsCatalogosFleteNoDisponible"/>. Los
    /// catálogos maestros usan el mismo cliente HTTP tipado (nombre <see cref="clsBuscadorClientesNexo.NombreCliente"/>) que la
    /// búsqueda de clientes: es el mismo servicio Nexo.
    /// No repite la configuración de resiliencia/redacción por la misma razón que <see cref="AgregarMunicipios"/>.
    /// TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarCatalogosFlete(IServiceCollection servicios)
    {
        // Catálogos maestros de Nexo (HU-013.10, G1/G2): GET /api/catalogos/*, con caché en memoria y copia de respaldo.
        servicios.TryAddScoped<clsCatalogosMaestrosNoDisponible>();
        servicios.TryAddSingleton<clsCacheCatalogosMaestros>();
        servicios.AddHttpClient<clsCatalogosMaestrosNexo>(clsBuscadorClientesNexo.NombreCliente);
        servicios.TryAddScoped<IClsCatalogosMaestrosProvider>(clsFabricaCatalogosMaestros.Crear);

        servicios.TryAddScoped<clsCatalogosFleteNoDisponible>();
        servicios.TryAddScoped<clsCatalogosFleteNexo>();
        servicios.TryAddScoped<IClsCatalogosFleteProvider>(clsFabricaCatalogosFlete.Crear);
    }

    /// <summary>
    /// Cubicación rápida elegida por <c>Nexo:UrlBase</c> al resolver (<see cref="clsFabricaCubicacion.Crear"/>):
    /// configurada → <see cref="clsCubicacionNexo"/>; vacía → <see cref="clsCubicacionNoDisponible"/>. Mismo cliente
    /// HTTP tipado (nombre <see cref="clsBuscadorClientesNexo.NombreCliente"/>) que la búsqueda de clientes: es el
    /// mismo servicio Nexo. No repite la configuración de resiliencia/redacción por la misma razón que
    /// <see cref="AgregarMunicipios"/>. TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarCubicacion(IServiceCollection servicios)
    {
        servicios.TryAddScoped<clsCubicacionNoDisponible>();
        servicios.AddHttpClient<clsCubicacionNexo>(clsBuscadorClientesNexo.NombreCliente);
        servicios.TryAddScoped<IClsCubicacionProvider>(clsFabricaCubicacion.Crear);
    }

    /// <summary>
    /// Costeo en Nexo (ADR-015) elegido por <c>Nexo:UrlBase</c> al resolver (<see cref="clsFabricaCosteoNexo.Crear"/>): configurada →
    /// <see cref="clsCosteoNexo"/>; vacía → <see cref="clsCosteoNexoNoDisponible"/>. A diferencia de clientes, municipios, catálogos y
    /// cubicación, usa su propio cliente HTTP (<see cref="clsCosteoNexo.NombreCliente"/>) con su propia tubería: 30 s (el alta de Nexo
    /// auto-costea en línea) y sin reintentar los POST (el alta no es idempotente). Sin registrar cabeceras en los logs. TryAdd: un
    /// adaptador registrado antes (pruebas) se respeta. El polling (HU-005.6) corre en <see cref="clsWorkerCosteoNexo"/>.
    /// </summary>
    private static void AgregarCosteo(IServiceCollection servicios)
    {
        servicios.TryAddScoped<clsCosteoNexoNoDisponible>();
        servicios.AddHttpClient<clsCosteoNexo>(clsCosteoNexo.NombreCliente)
            .RedactLoggedHeaders(_ => true)
            .AddStandardResilienceHandler(clsCosteoNexo.ConfigurarResiliencia);
        servicios.TryAddScoped<IClsCosteoNexo>(clsFabricaCosteoNexo.Crear);
        servicios.AddHostedService<clsWorkerCosteoNexo>();

        // Servicios conexos de Nexo (HU-013.3, ADR-018 §4): mismo cliente NexoCosteo y misma identidad, vía clsCosteoNexo.
        servicios.TryAddScoped<clsServiciosConexosNoDisponible>();
        servicios.TryAddScoped<Aplicacion.Costeo.Interfaces.IClsServiciosConexosNexo>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase)
                ? sp.GetRequiredService<clsServiciosConexosNoDisponible>()
                : new clsServiciosConexosNexo(sp.GetRequiredService<clsCosteoNexo>()));

        // Licitaciones (HU-011, ADR-017): el análisis del Excel va a Nexo por el mismo cliente NexoCosteo (POST sin reintentos);
        // sin Nexo:UrlBase responde COSTEO_NEXO_NO_DISPONIBLE. La lectura del original es local (ClosedXML).
        servicios.TryAddScoped<Licitaciones.Services.clsAnalizadorExcelNoDisponible>();
        servicios.AddHttpClient<Licitaciones.Services.clsAnalizadorExcelNexo>(clsCosteoNexo.NombreCliente);
        servicios.TryAddScoped<Aplicacion.Licitaciones.Interfaces.IClsAnalizadorExcelNexo>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase)
                ? sp.GetRequiredService<Licitaciones.Services.clsAnalizadorExcelNoDisponible>()
                : sp.GetRequiredService<Licitaciones.Services.clsAnalizadorExcelNexo>());
        servicios.TryAddSingleton<Aplicacion.Licitaciones.Interfaces.IClsLectorExcelLicitacion, Licitaciones.Services.clsLectorExcelLicitacionClosedXml>();
        servicios.TryAddSingleton<Aplicacion.Licitaciones.Interfaces.IClsExportadorExcelLicitacion, Licitaciones.Services.clsExportadorExcelLicitacionClosedXml>();
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
