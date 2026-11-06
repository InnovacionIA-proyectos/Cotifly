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
using Microsoft.Extensions.Logging;
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
        servicios.AddOpcionesValidadas<clsOpcionesBaseDatos>(configuracion, clsOpcionesBaseDatos.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesBaseDatos>, clsValidadorOpcionesBaseDatos>();
        servicios.AddOpcionesValidadas<clsOpcionesIdentidad>(configuracion, clsOpcionesIdentidad.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesRegional>(configuracion, clsOpcionesRegional.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesMargen>(configuracion, clsOpcionesMargen.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesPdf>(configuracion, clsOpcionesPdf.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesAlmacenPdf>(configuracion, clsOpcionesAlmacenPdf.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesAlmacenPdf>, clsValidadorOpcionesAlmacenPdf>();
        servicios.AddOpcionesValidadas<clsOpcionesCorreo>(configuracion, clsOpcionesCorreo.Seccion);
        // HU-033 (#199): fuera de Production, con el correo habilitado, la protección "redirigir" con buzón de pruebas es obligatoria.
        servicios.AddSingleton<IValidateOptions<clsOpcionesCorreo>, clsValidadorOpcionesCorreo>();
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
        servicios.AddOpcionesValidadas<clsOpcionesCaptura>(configuracion, clsOpcionesCaptura.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesCaptura>, clsValidadorOpcionesCaptura>();
        // HU-018.1 (#155): lista de motivos de pérdida (por defecto la de Nexo) y el motivo que exige comentario.
        servicios.AddOpcionesValidadas<Aplicacion.Cotizaciones.Services.clsOpcionesResultado>(configuracion, Aplicacion.Cotizaciones.Services.clsOpcionesResultado.Seccion);
        servicios.AddSingleton<IValidateOptions<Aplicacion.Cotizaciones.Services.clsOpcionesResultado>, clsValidadorOpcionesResultado>();
        // HU-032 (#197, M1): warning al arrancar si la lista de clases exentas no incluye Contenedor (no lo impide).
        servicios.AddHostedService<clsAvisosArranqueCaptura>();
        servicios.AddOpcionesValidadas<clsOpcionesTrm>(configuracion, clsOpcionesTrm.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesTrm>, clsValidadorOpcionesTrm>();
        servicios.AddOpcionesValidadas<clsOpcionesCandadoSql>(configuracion, clsOpcionesCandadoSql.Seccion);
        servicios.AddOpcionesValidadas<clsOpcionesCubicador>(configuracion, clsOpcionesCubicador.Seccion);
        // HU-023.2 (#157): "Calcular cubicaje" (parecido de la descripción y el plan B de la sugerencia legada).
        servicios.AddOpcionesValidadas<Aplicacion.Cubicacion.Services.clsOpcionesCubicacion>(configuracion, Aplicacion.Cubicacion.Services.clsOpcionesCubicacion.Seccion);
        servicios.AddSingleton<IValidateOptions<clsOpcionesCubicador>, clsValidadorOpcionesCubicador>();
        servicios.AddOpcionesValidadas<Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion>(
            configuracion, Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion.Seccion);
        servicios.AddSingleton<IValidateOptions<Aplicacion.Licitaciones.Services.clsOpcionesCubicacionLicitacion>, clsValidadorOpcionesCubicacionLicitacion>();

        // Adaptadores de puertos transversales.
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.TryAddSingleton<IClsReloj, clsRelojSistema>();
        servicios.TryAddSingleton<IClsCalendarioNegocio, clsCalendarioNegocio>();

        // Idempotency-Key de la captura y de las mutaciones de la etapa final (ADR-018 §8, #100, #173). #191: en la tabla del 06
        // (dbo_cotifly.CapturasIdempotentes), compartida entre réplicas y a prueba de reinicios; sin la tabla, /health/ready dice
        // "falta 06" (no se degrada a memoria). El registro de la captura en curso usa la misma fila, dentro de la transacción del folio.
        servicios.TryAddSingleton<IClsAlmacenIdempotencia, clsAlmacenIdempotenciaSql>();
        servicios.TryAddScoped<IClsRegistroCapturasIdempotentes, clsRegistroCapturasIdempotentesSql>();
        servicios.AddHostedService<clsLimpiezaIdempotenciaHostedService>();

        AgregarPersistencia(servicios);

        // Margen de SAP TM: la copia local y su sincronización con SAP desde el backend (HU-042.2, #243). TryAdd: un adaptador
        // registrado antes (pruebas) se respeta.
        AgregarFuenteMargen(servicios, configuracion);

        // TRM oficial del día para cotizar en USD (HU-012.1, ADR-014).
        AgregarTrm(servicios);

        // Catálogos del formulario de captura que también proxean Nexo (municipios, tipos de vehículo/carrocería/viaje). La cubicación
        // rápida de Nexo (POST /api/cubicacion) se retiró en HU-023.2 (#157): "Calcular cubicaje" usa el cubicador (AgregarCubicador).
        AgregarMunicipios(servicios);
        AgregarCatalogosFlete(servicios);
        AgregarCubicador(servicios);
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
        // HU-040.2 (#230, ADR-025): ticket de elección del rol (JWT de 5 minutos con clave derivada).
        servicios.TryAddSingleton<IClsEmisorTicketEleccion, clsEmisorTicketEleccionJwt>();
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
    /// PDF (MigraDoc con fuentes embebidas, almacén en volumen) y correo por MailSender (HU-033, #199, ADR-023): cliente HTTP tipado
    /// <see cref="clsTransporteMailSender"/> con <c>Correo:MailSender:UrlBase</c> y su tubería (tiempo <c>TimeoutSegundos</c>, sin reintentar los
    /// POST, sin registrar cabeceras), y el worker que consulta el estado de los correos encolados (<see cref="clsWorkerEstadoCorreos"/>).
    /// TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarPdfYCorreo(IServiceCollection servicios)
    {
        servicios.TryAddSingleton<IClsGeneradorPdf, clsGeneradorPdfMigraDoc>();
        servicios.TryAddSingleton<IClsAlmacenPdf, clsAlmacenPdfVolumen>();
        var tuberia = servicios.AddHttpClient<clsTransporteMailSender>(clsTransporteMailSender.NombreCliente, (sp, cliente) =>
            {
                cliente.Timeout = Timeout.InfiniteTimeSpan; // el límite lo pone la tubería
                var url = sp.GetRequiredService<IOptions<clsOpcionesCorreoTransporte>>().Value.MailSender.UrlBase;
                if (Uri.TryCreate(url?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var raiz))
                {
                    cliente.BaseAddress = raiz;
                }
            })
            .RedactLoggedHeaders(_ => true)
            .AddStandardResilienceHandler();
        servicios.AddOptions<HttpStandardResilienceOptions>(tuberia.PipelineName)
            .Configure<IOptions<clsOpcionesCorreoTransporte>>((resiliencia, opciones) =>
                clsTransporteMailSender.ConfigurarResiliencia(resiliencia, opciones.Value.MailSender.Timeout));
        servicios.TryAddScoped<IClsTransporteCorreo>(sp => sp.GetRequiredService<clsTransporteMailSender>());
        servicios.TryAddScoped<IClsSeguimientoCorreoExterno>(sp => sp.GetRequiredService<clsTransporteMailSender>());
        servicios.AddHostedService<clsWorkerEstadoCorreos>();
    }

    /// <summary>
    /// Margen de SAP TM (HU-042.2, #243, ADR-027). Al cotizar, <see cref="clsFuenteMargenTablaLocal"/> lee la copia local
    /// <c>dbo_cotifly.SapMargenCliente</c> con el <c>DbContext</c> de la petición (scoped). La copia la mantiene el propio backend:
    /// <see cref="clsWorkerSincronizacionMargenSap"/> llama a <see cref="clsSincronizacionMargenSapSql"/> (singleton, con su candado por réplica),
    /// que descarga <c>RentabClienteSet</c> con el cliente HTTP con nombre <see cref="clsClienteRentabSap.NombreCliente"/> (sin cookies, gzip,
    /// sin registrar cabeceras, sin la tubería de reintentos: el límite lo pone <c>SapTm:TimeoutSegundos</c>), resuelve el NIT en el DW y llama al
    /// stored procedure. Sin <c>SapTm:Url</c>/<c>User</c>/<c>Pass</c>, o con la URL o el mandante mal escritos, el worker no corre (la API
    /// arranca igual). Las variables retiradas
    /// (<see cref="clsAvisosArranqueSapTm.VariablesObsoletas"/>) no impiden arrancar: el servicio alojado avisa que se ignoran. TryAdd: un
    /// adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarFuenteMargen(IServiceCollection servicios, IConfiguration configuracion)
    {
        servicios.TryAddScoped<IClsFuenteMargen, clsFuenteMargenTablaLocal>();

        servicios.AddOpcionesValidadas<clsOpcionesSapTmSincronizacion>(configuracion, clsOpcionesSapTmSincronizacion.Seccion);
        servicios.AddHttpClient(clsClienteRentabSap.NombreCliente, cliente => cliente.Timeout = Timeout.InfiniteTimeSpan) // el límite lo pone el cliente
            .ConfigurePrimaryHttpMessageHandler(clsClienteRentabSap.CrearManejadorPrimario)
            .RedactLoggedHeaders(_ => true);
        servicios.TryAddSingleton<clsClienteRentabSap>();
        servicios.TryAddSingleton<clsDirectorioNitDwOperaciones>();
        servicios.TryAddSingleton<clsCoordinacionSincronizacionMargenSap>();
        servicios.TryAddSingleton<IClsSincronizacionMargenSap, clsSincronizacionMargenSapSql>();
        servicios.AddHostedService<clsWorkerSincronizacionMargenSap>();

        var obsoletas = clsAvisosArranqueSapTm.VariablesObsoletas(configuracion);
        servicios.AddHostedService(proveedor =>
            new clsAvisosArranqueSapTm(obsoletas, proveedor.GetRequiredService<ILogger<clsAvisosArranqueSapTm>>()));
    }

    /// <summary>
    /// TRM (HU-012.1, ADR-014): la cadena que elige <c>Trm:Proveedor</c> (<see cref="ProveedorTrm"/>) detrás de la caché por día
    /// <see cref="clsProveedorTrmEnCache"/>, todo singleton. Por defecto DolarApi (<see cref="clsProveedorTrmDolarApi"/>) con respaldo en los
    /// datos abiertos de la Superintendencia Financiera (<see cref="clsProveedorTrmSuperfinanciera"/>). Cada fuente tiene su cliente HTTP con
    /// nombre (se crea por consulta con <c>IHttpClientFactory</c>), la tubería estándar de resiliencia con el tiempo de
    /// <c>Trm:TiempoEsperaSegundos</c> y sin registrar cabeceras. Los eventos de Polly de cada intento bajan a Debug. TryAdd:
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
    /// tipado con la tubería estándar de resiliencia (8 s en total) y sin registrar el header <c>X-Portal-clsUsuario-Id</c> en los
    /// logs. TryAdd: un adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarBuscadorClientes(IServiceCollection servicios)
    {
        servicios.TryAddScoped<clsBuscadorClientesNoDisponible>();
        AgregarCupoNexo(servicios);
        servicios.AddHttpClient<clsBuscadorClientesNexo>(clsBuscadorClientesNexo.NombreCliente)
            .RedactLoggedHeaders(_ => true)
            .AddHttpMessageHandler<clsCupoNexoHandler>()
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

        // HU-031.1 (#194): diccionario de alias de Nexo (NEXO #321), mismo cliente HTTP y las mismas reglas de caché que los maestros.
        servicios.TryAddSingleton<clsCacheAliasNexo>();
        servicios.AddHttpClient<clsAliasNexo>(clsBuscadorClientesNexo.NombreCliente);
        servicios.TryAddScoped<IClsAliasNexoProvider>(clsFabricaAliasNexo.Crear);

        servicios.TryAddScoped<clsCatalogosFleteNoDisponible>();
        servicios.TryAddScoped<clsCatalogosFleteNexo>();
        servicios.TryAddScoped<IClsCatalogosFleteProvider>(clsFabricaCatalogosFlete.Crear);
    }

    /// <summary>
    /// Cubicador (HU-021.5, #133) elegido por <c>Cubicador:UrlBase</c> al resolver (<see cref="clsFabricaCubicador.Crear"/>): configurado →
    /// <see cref="clsCubicadorHttp"/>; vacío → <see cref="clsCubicadorNoDisponible"/> (la captura crea un camión sin vehículo por ruta). Cliente
    /// HTTP propio (<see cref="clsCubicadorHttp.NombreCliente"/>) con su tubería: <c>Cubicador:TiempoEsperaSegundos</c> para las consultas y
    /// <c>Cubicador:TiempoEsperaRecomendacionSegundos</c> para el POST (D3b), sin
    /// reintentar los POST y sin registrar cabeceras (la llave viaja en <c>Authorization</c>). Los eventos de Polly de cada intento bajan a
    /// Debug, como en SAP TM. Los catálogos (tipos de mercancía, carrocerías y vehículos, HU-023.1) se guardan en <see cref="clsCacheCatalogoCubicador{TFila}"/> (singleton por catálogo). TryAdd: un
    /// adaptador registrado antes (pruebas) se respeta.
    /// </summary>
    private static void AgregarCubicador(IServiceCollection servicios)
    {
        servicios.TryAddScoped<clsCubicadorNoDisponible>();
        servicios.TryAddSingleton(typeof(clsCacheCatalogoCubicador<>));
        // Opción C: un solo cupo global por proceso para POST /recomendaciones (Cubicador:MaximoConcurrente).
        servicios.TryAddSingleton<clsCupoLlamadasCubicador>();
        // Sin el límite de 100 s del HttpClient: los límites los pone la tubería (el POST puede esperar hasta 300 s).
        var tuberia = servicios.AddHttpClient<clsCubicadorHttp>(clsCubicadorHttp.NombreCliente, cliente => cliente.Timeout = Timeout.InfiniteTimeSpan)
            .RedactLoggedHeaders(_ => true)
            .AddStandardResilienceHandler();
        servicios.AddOptions<HttpStandardResilienceOptions>(tuberia.PipelineName)
            .Configure<IOptions<clsOpcionesCubicador>>((resiliencia, opciones) =>
                clsCubicadorHttp.ConfigurarResiliencia(resiliencia, TimeSpan.FromSeconds(opciones.Value.TiempoEsperaSegundos),
                    TimeSpan.FromSeconds(opciones.Value.TiempoEsperaRecomendacionSegundos),
                    TimeSpan.FromSeconds(opciones.Value.TiempoEsperaReconsultaSegundos)));
        servicios.TryAddScoped<IClsCubicador>(clsFabricaCubicador.Crear);

        // Opción D: "Calcular cubicaje" asíncrono (202 y GET /web/cubicacion/{id}). Una réplica de la API (infra/stack.dotnet.*.yml):
        // las cubicaciones viven en memoria; con más réplicas haría falta una tabla compartida.
        servicios.TryAddSingleton<IClsAlmacenCubicacionesRapidas, clsAlmacenCubicacionesRapidasMemoria>();
        servicios.TryAddSingleton<clsCubicacionRapidaService>();
        servicios.TryAddSingleton<IClsCubicacionRapidaService>(sp => sp.GetRequiredService<clsCubicacionRapidaService>());
        servicios.AddHostedService(sp => sp.GetRequiredService<clsCubicacionRapidaService>());

        // HU-023.5 (#160): cubicación de la licitación en segundo plano, con cupos de llamadas compartidos entre réplicas (P6).
        servicios.TryAddScoped<IClsCupoCubicador, clsCupoCubicadorSql>();
        servicios.AddHostedService<clsWorkerCubicacionLicitacion>();

        servicios.Configure<TelemetryOptions>(telemetria =>
        {
            var anterior = telemetria.SeverityProvider;
            telemetria.SeverityProvider = argumentos =>
                argumentos.Source.PipelineName?.StartsWith(clsCubicadorHttp.NombreCliente + "-", StringComparison.Ordinal) == true
                    ? ResilienceEventSeverity.Debug
                    : anterior?.Invoke(argumentos) ?? argumentos.Event.Severity;
        });
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
        AgregarCupoNexo(servicios);
        servicios.AddHttpClient<clsCosteoNexo>(clsCosteoNexo.NombreCliente)
            .RedactLoggedHeaders(_ => true)
            .AddHttpMessageHandler<clsCupoNexoHandler>()
            .AddStandardResilienceHandler(clsCosteoNexo.ConfigurarResiliencia);
        servicios.TryAddScoped<IClsCosteoNexo>(clsFabricaCosteoNexo.Crear);
        servicios.AddHostedService<clsWorkerCosteoNexo>();

        // Servicios conexos de Nexo (HU-013.3, ADR-018 §4): mismo cliente NexoCosteo y misma identidad, vía clsCosteoNexo.
        servicios.TryAddScoped<clsServiciosConexosNoDisponible>();
        servicios.TryAddScoped<Aplicacion.Costeo.Interfaces.IClsServiciosConexosNexo>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase)
                ? sp.GetRequiredService<clsServiciosConexosNoDisponible>()
                : new clsServiciosConexosNexo(sp.GetRequiredService<clsCosteoNexo>()));

        // Costo unitario de los conexos del catálogo para capturarlos con la ruta (HU-021.6, decisión 3): mismo cliente y misma identidad.
        // Con caché en memoria de 5 minutos (clsAlmacenCostosConexos): el precio guardado es el costo de Nexo al capturar.
        servicios.TryAddSingleton<clsAlmacenCostosConexos>();
        servicios.TryAddScoped<Aplicacion.Costeo.Interfaces.IClsCostosConexosNexo>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase)
                ? new clsCostosConexosNoDisponible()
                : new clsCostosConexosEnCache(
                    new clsCostosConexosNexo(sp.GetRequiredService<clsCosteoNexo>()),
                    sp.GetRequiredService<clsAlmacenCostosConexos>(),
                    sp.GetRequiredService<Aplicacion.Comun.Interfaces.IClsReloj>()));

        // Licitaciones (HU-011, ADR-017): el análisis del Excel va a Nexo por el mismo cliente NexoCosteo (POST sin reintentos);
        // sin Nexo:UrlBase responde COSTEO_NEXO_NO_DISPONIBLE. La lectura del original es local (ClosedXML).
        servicios.TryAddScoped<Licitaciones.Services.clsAnalizadorExcelNoDisponible>();
        servicios.AddHttpClient<Licitaciones.Services.clsAnalizadorExcelNexo>(clsCosteoNexo.NombreCliente);
        servicios.TryAddScoped<Aplicacion.Licitaciones.Interfaces.IClsAnalizadorExcelNexo>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<clsOpcionesNexo>>().Value.UrlBase)
                ? sp.GetRequiredService<Licitaciones.Services.clsAnalizadorExcelNoDisponible>()
                : sp.GetRequiredService<Licitaciones.Services.clsAnalizadorExcelNexo>());
        // HU-035.2 (#213): los controles del Excel subido (bomba zip, libro denso, XXE) y el cupo de lecturas simultáneas son una sola
        // pieza para el pliego y el Excel de mercancías. Singleton: el cupo es de todo el proceso.
        servicios.TryAddSingleton<clsLecturaExcelAcotada>();
        servicios.TryAddSingleton<Aplicacion.Licitaciones.Interfaces.IClsLectorExcelLicitacion, Licitaciones.Services.clsLectorExcelLicitacionClosedXml>();
        servicios.TryAddSingleton<Aplicacion.Licitaciones.Interfaces.IClsExportadorExcelLicitacion, Licitaciones.Services.clsExportadorExcelLicitacionClosedXml>();
        // HU-018.1 (#155, parte C): Excel de la cotización ("Ver detalle"). Solo escribe (no abre archivos del cliente): sin el cupo de lecturas.
        servicios.TryAddSingleton<Aplicacion.Cotizaciones.Interfaces.IClsExportadorExcelCotizacion, Cotizaciones.Services.clsExportadorExcelCotizacionClosedXml>();

        // HU-035.1 (#202, ADR-024): Excel de mercancías de una ruta, leído localmente (ClosedXML, sin Nexo), con el cupo compartido.
        servicios.TryAddSingleton<Aplicacion.Mercancias.Interfaces.IClsLectorGrillaExcel, Mercancias.Services.clsLectorGrillaExcelClosedXml>();
    }

    /// <summary>
    /// Cupo de lecturas de Nexo (HU-023.4, #159): un bloqueo para todo Cotifly tras un 429 (Nexo limita por rol) y el manejador que lo
    /// aplica en los dos clientes de Nexo, por fuera de la tubería de resiliencia. Idempotente (lo piden clientes y costeo).
    /// </summary>
    private static void AgregarCupoNexo(IServiceCollection servicios)
    {
        servicios.TryAddSingleton<clsCupoNexo>();
        servicios.TryAddTransient<clsCupoNexoHandler>();
    }

    private static void AgregarPersistencia(IServiceCollection servicios)
    {
        servicios.AddSingleton<clsMarcasTiempoInterceptor>();
        // HU-023 (#176): BUSQUEDA_AGOTADA se guarda tal cual con el script 07 y, sin él, en NULL con un aviso (autodetectado).
        servicios.TryAddSingleton<clsDetectorMotivosBd>();
        servicios.AddSingleton<clsMotivoSinSeleccionInterceptor>();

        // dbo_cotifly: lectura y escritura (ADR-008). Sin cadena (fuera de Production) la API arranca
        // igual y /health/ready responde 503.
        servicios.AddDbContext<clsCotiflyDbContext>((proveedor, opciones) =>
        {
            var cadena = proveedor.GetRequiredService<IOptions<clsOpcionesConexiones>>().Value.Cotifly;
            ConfigurarSqlServer(opciones, cadena);
            opciones.AddInterceptors(
                proveedor.GetRequiredService<clsMotivoSinSeleccionInterceptor>(), proveedor.GetRequiredService<clsMarcasTiempoInterceptor>());
        });

        // Base de identidad del portal (la misma de Nexo; QA: dbSolucionesTI_Prueba, #172): solo lectura (login, #12).
        servicios.AddDbContext<clsIdentidadDbContext>((proveedor, opciones) =>
            ConfigurarSqlServer(opciones, proveedor.GetRequiredService<IOptions<clsOpcionesConexiones>>().Value.Identidad));

        servicios.AddScoped<IClsUnitOfWork, clsUnitOfWork>();
        servicios.AddScoped<IClsCotizacionRepository, clsCotizacionRepository>();
        // HU-045.2 (#264): la tasa vigente del seguro de la mercancía (TarifasSeguro, script 20; solo lectura).
        servicios.AddScoped<IClsTarifaSeguroRepository, clsTarifaSeguroRepository>();
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
            .AddCheck<clsBaseDatosHealthCheck>(clsBaseDatosHealthCheck.Nombre, tags: [EtiquetaReadiness])
            .AddCheck<clsCubicadorHealthCheck>(clsCubicadorHealthCheck.Nombre, tags: [EtiquetaReadiness]);
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
