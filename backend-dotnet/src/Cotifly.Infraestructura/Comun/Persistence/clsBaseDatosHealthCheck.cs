using System.Text;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Readiness de la base <c>dbo_cotifly</c> para <c>GET /health/ready</c> (rehecha para HU-021, ADR-019): la cadena debe estar
/// configurada, la base debe responder y el esquema debe tener TODAS las tablas y columnas que mapea el modelo EF (se leen del propio
/// modelo, así la sonda no se queda atrás cuando cambia el esquema). Si falta algo, la descripción dice qué script falta:
/// <list type="bullet">
/// <item><see cref="DescripcionEsquemaIncompleto"/>: falta el 0002 (<c>Sesiones.estadoAgente</c> o <c>TokensRefresco</c>);</item>
/// <item><see cref="DescripcionEsquemaSin0009"/>: la base sigue con <c>Cotizaciones</c> (esquema anterior a la reestructuración);</item>
/// <item><see cref="DescripcionEsquemaSin0010"/>: solo faltan las columnas del 0010 (nombres de municipio, id de la solicitud de Nexo,
/// nombre del conexo);</item>
/// <item><see cref="DescripcionEsquemaSin02"/>: solo faltan las columnas del cubicador de <c>db/scripts/02_alter_cubicador.sql</c>
/// (HU-023.3, #158);</item>
/// <item><see cref="DescripcionEsquemaSin03"/>: solo faltan <c>ordenVehiculo</c> o <c>idServicioConexoCatalogo</c> de
/// <c>db/scripts/03_orden_vehiculo_y_conexo_catalogo.sql</c> (HU-014, #188);</item>
/// <item><see cref="DescripcionEsquemaSin04"/>: solo faltan las columnas de la cubicación en segundo plano (HU-023.5, #160);</item>
/// <item><see cref="DescripcionEsquemaSin05"/>: solo falta <c>categoriaServicioConexo</c> de <c>db/scripts/05_categoria_conexo.sql</c>
/// (HU-014, #188);</item>
/// <item><see cref="DescripcionEsquemaSin06"/>: solo falta la tabla <c>CapturasIdempotentes</c> (o alguna de sus columnas) de
/// <c>db/scripts/06_idempotencia_captura.sql</c> (#191: la <c>Idempotency-Key</c> persistente; no se degrada a memoria);</item>
/// <item><see cref="DescripcionEsquemaSin09"/>: solo falta <c>CotizacionesSolicitud.scorePrioridad</c> de
/// <c>db/scripts/09_score_prioridad.sql</c> (HU-018.1, #155);</item>
/// <item><see cref="DescripcionEsquemaSin10"/>: solo faltan <c>motivoRechazo</c> o <c>rechazadaAt</c> del camión de
/// <c>db/scripts/10_rechazo_linea.sql</c> (HU-028.1, #183);</item>
/// <item><see cref="DescripcionEsquemaSin11"/>: solo falta <c>CotizacionLineasSolicitud.pesoConTaraKg</c> de
/// <c>db/scripts/11_peso_con_tara.sql</c> (HU-030.1, #184);</item>
/// <item><see cref="DescripcionEsquemaSin12"/>: solo faltan <c>tipoVehiculoElegido</c> o <c>carroceriaElegida</c> de la ruta de
/// <c>db/scripts/12_vehiculo_elegido.sql</c> (HU-032, #197; el mismo script deja las medidas de la mercancía en NULL);</item>
/// <item><see cref="DescripcionEsquemaSin13"/>: solo faltan columnas de <c>db/scripts/13_correo_mailsender.sql</c> (HU-033, #199: el
/// seguimiento de MailSender en <c>Correos</c> y el resultado diferido a Nexo en la cabecera);</item>
/// <item><see cref="DescripcionEsquemaSin14"/>: solo faltan <c>finalizadaAt</c> o <c>finalizadaPor</c> de la cabecera de
/// <c>db/scripts/14_cotizacion_finalizada.sql</c> (HU-036, #205);</item>
/// <item><see cref="DescripcionEsquemaSin16"/>: solo falta <c>TokensRefresco.rolSesion</c> de
/// <c>db/scripts/16_multiples_roles_activos.sql</c> (HU-040.1, #229);</item>
/// <item><see cref="DescripcionEsquemaSin17"/>: solo faltan las tablas <c>SapMargenCliente</c> o <c>SapMargenSyncLog</c> de
/// <c>db/scripts/17_sap_margen_sincronizacion.sql</c> (HU-042.1 #242; las lee HU-042.2 #243);</item>
/// <item><see cref="DescripcionEsquemaSin18"/>: solo falta <c>SapMapeoTrqType.lineaNegocioId</c> de
/// <c>db/scripts/18_sap_mapeo_fk_margenes_politica.sql</c> (HU-042.4, #247);</item>
/// <item><see cref="DescripcionEsquemaSin19"/>: solo falta <c>CotizacionServiciosConexos.precioVenta</c> de
/// <c>db/scripts/19_precio_venta_conexos.sql</c> (HU-043.1, #245; el backend la mapea desde #261);</item>
/// <item><see cref="DescripcionEsquemaSin20"/>: solo faltan la tabla <c>TarifasSeguro</c> o las columnas del seguro de la ruta de
/// <c>db/scripts/20_seguro_mercancia.sql</c> (HU-045.1, #263; el backend las mapea desde HU-045.2, #264);</item>
/// <item>cualquier otra ausencia: <c>esquema incompleto: falta Tabla.columna</c> (las primeras, sin datos de conexión).</item>
/// </list>
/// La sonda tiene un tiempo máximo propio (<see cref="TiempoMaximo"/>). Las descripciones no incluyen la cadena ni el servidor.
/// </summary>
public sealed class clsBaseDatosHealthCheck(
    clsCotiflyDbContext contexto, IOptions<clsOpcionesConexiones> opciones, IOptions<clsOpcionesBaseDatos> opcionesBaseDatos) : IHealthCheck
{
    public const string Nombre = "base-datos-cotifly";
    public const string DescripcionSinCadena = "La cadena de conexión 'ConnectionStrings:Cotifly' no está configurada.";
    public const string DescripcionNoDisponible = "La base de datos de Cotifly no está disponible.";
    public const string DescripcionEsquemaIncompleto = "esquema incompleto: falta 0002";

    /// <summary>La base tiene el esquema anterior (<c>Cotizaciones</c>): falta la reestructuración de HU-021.</summary>
    public const string DescripcionEsquemaSin0009 = "esquema incompleto: falta 0009";

    /// <summary>Está el 0009, pero no el 0010 (nombres de municipio, id de la solicitud de Nexo y nombre del conexo).</summary>
    public const string DescripcionEsquemaSin0010 = "esquema incompleto: falta 0010";

    /// <summary>HU-023.3 (#158): están el 0009 y el 0010, pero no las columnas del cubicador de <c>db/scripts/02_alter_cubicador.sql</c>.</summary>
    public const string DescripcionEsquemaSin02 = "esquema incompleto: falta 02";

    /// <summary>Columnas que agrega <c>db/scripts/02_alter_cubicador.sql</c> (HU-023.3).</summary>
    public static readonly IReadOnlySet<string> Columnas02 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionMercancias.codigoMercancia",
        "CotizacionVehiculosLineaSolicitud.volumenTotalM3",
        "CotizacionVehiculosLineaSolicitud.porcentajeLlenadoVolumen",
        "CotizacionVehiculosLineaSolicitud.porcentajeLlenadoPeso",
        "CotizacionLineasSolicitud.idRecomendacion",
        "CotizacionLineasSolicitud.resultadoCubicacion",
        "CotizacionLineasSolicitud.motivoSinSeleccion",
        "CotizacionLineasSolicitud.condicionada",
    };

    /// <summary>HU-014 (#188): están el 0010 y el 02, pero no las columnas de <c>db/scripts/03_orden_vehiculo_y_conexo_catalogo.sql</c>.</summary>
    public const string DescripcionEsquemaSin03 = "esquema incompleto: falta 03";

    /// <summary>Columnas que agrega <c>db/scripts/03_orden_vehiculo_y_conexo_catalogo.sql</c> (HU-023.3; las mapea el backend desde #188).</summary>
    public static readonly IReadOnlySet<string> Columnas03 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionVehiculosLineaSolicitud.ordenVehiculo",
        "CotizacionServiciosConexos.idServicioConexoCatalogo",
    };

    /// <summary>HU-023.5 (#160): están el 0010, el 02 y el 03, pero no las columnas de la cubicación en segundo plano del <c>04</c>.</summary>
    public const string DescripcionEsquemaSin04 = "esquema incompleto: falta 04";

    /// <summary>Columnas que agrega <c>db/scripts/pendientes/04_cubicacion_segundo_plano.sql</c> (pendiente de @scalderon-bs y del DBA) (HU-023.5).</summary>
    public static readonly IReadOnlySet<string> Columnas04 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionLineasSolicitud.estadoCubicacion",
        "CotizacionLineasSolicitud.cubicacionIntentos",
        "CotizacionLineasSolicitud.cubicacionDisponibleAt",
        "CotizacionLineasSolicitud.errorCubicacion",
        "CotizacionLineasSolicitud.avisosCubicacion",
    };

    /// <summary>HU-014 (#188): está todo hasta el 04, pero no <c>categoriaServicioConexo</c> de <c>db/scripts/05_categoria_conexo.sql</c>.</summary>
    public const string DescripcionEsquemaSin05 = "esquema incompleto: falta 05";

    /// <summary>Columnas que agrega <c>db/scripts/05_categoria_conexo.sql</c> (#173; la mapea el backend desde #188).</summary>
    public static readonly IReadOnlySet<string> Columnas05 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionServiciosConexos.categoriaServicioConexo",
    };

    /// <summary>#191: está todo hasta el 05, pero no la tabla <c>CapturasIdempotentes</c> de <c>db/scripts/06_idempotencia_captura.sql</c>.</summary>
    public const string DescripcionEsquemaSin06 = "esquema incompleto: falta 06";

    /// <summary>
    /// Columnas de <c>dbo_cotifly.CapturasIdempotentes</c> (<c>db/scripts/06_idempotencia_captura.sql</c>), mapeadas en el modelo EF
    /// (<see cref="clsCapturaIdempotenteFila"/>) y usadas con SQL propio por <see cref="clsAlmacenIdempotenciaSql"/> y
    /// <see cref="clsRegistroCapturasIdempotentesSql"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> Columnas06 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CapturasIdempotentes.id",
        "CapturasIdempotentes.usuarioId",
        "CapturasIdempotentes.operacion",
        "CapturasIdempotentes.clave",
        "CapturasIdempotentes.huella",
        "CapturasIdempotentes.estado",
        "CapturasIdempotentes.tomadaPor",
        "CapturasIdempotentes.enCursoHasta",
        "CapturasIdempotentes.resultado",
        "CapturasIdempotentes.cotizacionId",
        "CapturasIdempotentes.folio",
        "CapturasIdempotentes.solicitudNexoId",
        "CapturasIdempotentes.scorePrioridad",
        "CapturasIdempotentes.createdAt",
        "CapturasIdempotentes.updatedAt",
        "CapturasIdempotentes.expiraAt",
    };

    /// <summary>HU-018.1 (#155): está todo lo anterior, pero no la columna <c>scorePrioridad</c> de <c>db/scripts/09_score_prioridad.sql</c>.</summary>
    public const string DescripcionEsquemaSin09 = "esquema incompleto: falta 09";

    /// <summary>Columnas que agrega <c>db/scripts/09_score_prioridad.sql</c> (HU-018.1).</summary>
    public static readonly IReadOnlySet<string> Columnas09 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionesSolicitud.scorePrioridad",
    };

    /// <summary>HU-028.1 (#183): está todo lo anterior, pero no las columnas del rechazo de <c>db/scripts/10_rechazo_linea.sql</c>.</summary>
    public const string DescripcionEsquemaSin10 = "esquema incompleto: falta 10";

    /// <summary>Columnas que agrega <c>db/scripts/10_rechazo_linea.sql</c> (HU-028.2, #182; las mapea el backend desde #183).</summary>
    public static readonly IReadOnlySet<string> Columnas10 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionVehiculosLineaSolicitud.motivoRechazo",
        "CotizacionVehiculosLineaSolicitud.rechazadaAt",
    };

    /// <summary>HU-030.1 (#184): está todo lo anterior, pero no la columna <c>pesoConTaraKg</c> de <c>db/scripts/11_peso_con_tara.sql</c>.</summary>
    public const string DescripcionEsquemaSin11 = "esquema incompleto: falta 11";

    /// <summary>Columnas que agrega <c>db/scripts/11_peso_con_tara.sql</c> (HU-030.1).</summary>
    public static readonly IReadOnlySet<string> Columnas11 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionLineasSolicitud.pesoConTaraKg",
    };

    /// <summary>HU-032 (#197): está todo lo anterior, pero no las columnas del vehículo elegido de <c>db/scripts/12_vehiculo_elegido.sql</c>.</summary>
    public const string DescripcionEsquemaSin12 = "esquema incompleto: falta 12";

    /// <summary>
    /// Columnas que agrega <c>db/scripts/12_vehiculo_elegido.sql</c> (HU-032). El mismo script (en la misma transacción) deja
    /// <c>largoCm</c>, <c>anchoCm</c> y <c>altoCm</c> de la mercancía en NULL: con las columnas, también está eso.
    /// </summary>
    public static readonly IReadOnlySet<string> Columnas12 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionLineasSolicitud.tipoVehiculoElegido",
        "CotizacionLineasSolicitud.carroceriaElegida",
    };

    /// <summary>HU-033 (#199): está todo lo anterior, pero no las columnas de <c>db/scripts/13_correo_mailsender.sql</c>.</summary>
    public const string DescripcionEsquemaSin13 = "esquema incompleto: falta 13";

    /// <summary>Columnas que agrega <c>db/scripts/13_correo_mailsender.sql</c> (HU-033, #199).</summary>
    public static readonly IReadOnlySet<string> Columnas13 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Correos.idMailSender",
        "Correos.redirigidoA",
        "Correos.intentosEntrega",
        "Correos.enviadoAt",
        "Correos.estadoActualizadoAt",
        "Correos.siguienteConsultaAt",
        "Correos.encoladoAt",
        "CotizacionesSolicitud.sincronizacionResultadoNexo",
        "CotizacionesSolicitud.comentarioResultadoPendiente",
        "CotizacionesSolicitud.resultadoSincronizadoAt",
        "CotizacionesSolicitud.errorSincronizacionResultado",
        "CotizacionesSolicitud.intentosSincronizacionResultado",
    };

    /// <summary>HU-036 (#205): está todo lo anterior, pero no las columnas de <c>db/scripts/14_cotizacion_finalizada.sql</c>.</summary>
    public const string DescripcionEsquemaSin14 = "esquema incompleto: falta 14";

    /// <summary>Columnas que agrega <c>db/scripts/14_cotizacion_finalizada.sql</c> (HU-036, #205).</summary>
    public static readonly IReadOnlySet<string> Columnas14 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionesSolicitud.finalizadaAt",
        "CotizacionesSolicitud.finalizadaPor",
    };

    /// <summary>HU-040.1 (#229): está todo lo anterior, pero no <c>TokensRefresco.rolSesion</c> de <c>db/scripts/16_multiples_roles_activos.sql</c>.</summary>
    public const string DescripcionEsquemaSin16 = "esquema incompleto: falta 16";

    /// <summary>Columnas que agrega <c>db/scripts/16_multiples_roles_activos.sql</c> (HU-040.1, #229).</summary>
    public static readonly IReadOnlySet<string> Columnas16 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TokensRefresco.rolSesion",
    };

    /// <summary>HU-042.2 (#243): está todo lo anterior, pero no las tablas de la copia local del margen de SAP de <c>db/scripts/17</c>.</summary>
    public const string DescripcionEsquemaSin17 = "esquema incompleto: falta 17";

    /// <summary>Columnas de las dos tablas que crea <c>db/scripts/17_sap_margen_sincronizacion.sql</c> (HU-042.1, #242).</summary>
    public static readonly IReadOnlySet<string> Columnas17 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SapMargenCliente.id",
        "SapMargenCliente.salesOrgId",
        "SapMargenCliente.trqType",
        "SapMargenCliente.bpSap",
        "SapMargenCliente.nit",
        "SapMargenCliente.margenRecomendado",
        "SapMargenCliente.activo",
        "SapMargenCliente.creadoAt",
        "SapMargenCliente.actualizadoAt",
        "SapMargenSyncLog.id",
        "SapMargenSyncLog.iniciadoAt",
        "SapMargenSyncLog.finalizadoAt",
        "SapMargenSyncLog.filasInsertadas",
        "SapMargenSyncLog.filasActualizadas",
        "SapMargenSyncLog.filasDesactivadas",
        "SapMargenSyncLog.status",
        "SapMargenSyncLog.mensajeError",
    };

    /// <summary>HU-042.4 (#247): está todo lo anterior, pero no <c>SapMapeoTrqType.lineaNegocioId</c> de <c>db/scripts/18</c>.</summary>
    public const string DescripcionEsquemaSin18 = "esquema incompleto: falta 18";

    /// <summary>Columnas que agrega <c>db/scripts/18_sap_mapeo_fk_margenes_politica.sql</c> (HU-042.4, #247).</summary>
    public static readonly IReadOnlySet<string> Columnas18 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SapMapeoTrqType.lineaNegocioId",
    };

    /// <summary>#261 punto 4: está todo lo anterior, pero no <c>precioVenta</c> de <c>db/scripts/19_precio_venta_conexos.sql</c> (HU-043.1, #245).</summary>
    public const string DescripcionEsquemaSin19 = "esquema incompleto: falta 19";

    /// <summary>Columnas que agrega <c>db/scripts/19_precio_venta_conexos.sql</c> (HU-043.1, #245).</summary>
    public static readonly IReadOnlySet<string> Columnas19 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CotizacionServiciosConexos.precioVenta",
    };

    /// <summary>HU-045.2 (#264): está todo lo anterior, pero no lo de <c>db/scripts/20_seguro_mercancia.sql</c> (HU-045.1, #263).</summary>
    public const string DescripcionEsquemaSin20 = "esquema incompleto: falta 20";

    /// <summary>Columnas que agrega <c>db/scripts/20_seguro_mercancia.sql</c> (HU-045.1, #263): la tabla de la tasa y el seguro de la ruta.</summary>
    public static readonly IReadOnlySet<string> Columnas20 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "TarifasSeguro.id",
        "TarifasSeguro.tasa",
        "TarifasSeguro.vigenteDesde",
        "TarifasSeguro.isActive",
        "TarifasSeguro.updatedAt",
        "CotizacionLineasSolicitud.incluyeSeguro",
        "CotizacionLineasSolicitud.tasaSeguroAplicada",
        "CotizacionLineasSolicitud.costoSeguro",
    };

    /// <summary>Prefijo de la descripción cuando falta otra tabla o columna del modelo.</summary>
    public const string PrefijoEsquemaIncompleto = "esquema incompleto: falta ";

    public const string DescripcionDisponible = "La base de datos de Cotifly responde.";

    /// <summary>
    /// HU-023 (#176): sana, con un aviso informativo: falta <c>db/scripts/07_motivo_busqueda_agotada.sql</c> (el motivo
    /// <c>BUSQUEDA_AGOTADA</c> se guarda en NULL y queda como advertencia de la ruta).
    /// </summary>
    public const string DescripcionSin07 =
        "La base de datos de Cotifly responde. Aviso: falta el script 07 (BUSQUEDA_AGOTADA se guarda en NULL con un aviso en la ruta).";

    /// <summary>
    /// HU-023 (#176, auditoría A2): sana, con un aviso: el login no puede leer la definición del CHECK del motivo (le falta
    /// <c>VIEW DEFINITION</c>), así que no se sabe si el 07 está aplicado. Mientras tanto se degrada como sin el 07.
    /// </summary>
    public const string DescripcionSin07NoVerificable =
        "La base de datos de Cotifly responde. Aviso: no se puede verificar el script 07 (falta VIEW DEFINITION para el login de la API); "
        + "BUSQUEDA_AGOTADA se guarda en NULL con un aviso en la ruta. Otorgue VIEW DEFINITION o configure BaseDatos__MotivoBusquedaAgotada.";

    /// <summary>HU-023 (#176): sana; <c>BaseDatos:MotivoBusquedaAgotada=deshabilitado</c> (BUSQUEDA_AGOTADA se guarda en NULL a propósito).</summary>
    public const string DescripcionMotivoDeshabilitado =
        "La base de datos de Cotifly responde. Aviso: BUSQUEDA_AGOTADA deshabilitado por configuración (BaseDatos__MotivoBusquedaAgotada); se guarda en NULL con un aviso en la ruta.";

    /// <summary>HU-023 (#176): sana, pero <c>BaseDatos:MotivoBusquedaAgotada=habilitado</c> y la base no tiene el 07: guardar ese motivo fallará.</summary>
    public const string DescripcionMotivoHabilitadoSin07 =
        "La base de datos de Cotifly responde. Aviso: BaseDatos__MotivoBusquedaAgotada=habilitado pero falta el script 07: guardar una ruta con BUSQUEDA_AGOTADA fallará.";

    /// <summary>Tiempo máximo de la sonda completa (conexión + verificación de esquema).</summary>
    public static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(5);

    /// <summary>
    /// El <c>CHECK</c> de <c>estadoEnvio</c>: el modelo no lo describe, así que la sonda lo verifica aparte. Lo trae el 0009 (y
    /// <c>01_crear_esquema.sql</c>), pero la base compartida se instaló antes; el 0010 lo crea si falta.
    /// </summary>
    public const string CheckEstadoEnvio = "CotizacionesSolicitud.ck_cotizaciones_estadoEnvio";

    /// <summary>Columnas (y el <c>CHECK</c> de <c>estadoEnvio</c>) que agrega el 0010 (borrador pendiente de aprobación).</summary>
    public static readonly IReadOnlySet<string> Columnas0010 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CheckEstadoEnvio,
        "CotizacionesSolicitud.solicitudNexoId",
        "CotizacionLineasSolicitud.origenNombre",
        "CotizacionLineasSolicitud.destinoNombre",
        "CotizacionLineasSolicitud.ciudadDevolucionNombre",
        "CotizacionLineasSolicitud.ciudadLevanteNombre",
        "CotizacionServiciosConexos.nombreServicioConexo",
    };

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(opciones.Value.Cotifly))
        {
            return HealthCheckResult.Unhealthy(DescripcionSinCadena);
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limite.CancelAfter(TiempoMaximo);

        try
        {
            if (!await contexto.Database.CanConnectAsync(limite.Token))
            {
                return HealthCheckResult.Unhealthy(DescripcionNoDisponible);
            }

            var faltantes = await contexto.Database.SqlQueryRaw<string>(ConsultaFaltantes(contexto.Model)).ToListAsync(limite.Token);
            var cotizacionesViejas = faltantes.Count > 0
                && await contexto.Database.SqlQueryRaw<int>(
                    "SELECT CASE WHEN OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL THEN 0 ELSE 1 END AS [Value]").SingleAsync(limite.Token) == 1;
            var clasificado = Clasificar(faltantes, cotizacionesViejas);
            if (clasificado.Status != HealthStatus.Healthy)
            {
                return clasificado;
            }

            // HU-023 (#176): el 07 (BUSQUEDA_AGOTADA en el CHECK del motivo) no es obligatorio: sin él, el motivo se guarda en NULL con
            // un aviso. Solo se informa en la descripción; la sonda sigue sana. Con "deshabilitado" no se consulta.
            var modo = opcionesBaseDatos.Value.MotivoBusquedaAgotadaNormalizado;
            EstadoScript07? estado = modo == clsOpcionesBaseDatos.ModoDeshabilitado
                ? null
                : clsDetectorMotivosBd.Interpretar(
                    await contexto.Database.SqlQueryRaw<int>(clsDetectorMotivosBd.Consulta + " AS [Value]").SingleAsync(limite.Token));
            return ConAvisoScript07(clasificado, modo, estado);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(DescripcionNoDisponible, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(DescripcionNoDisponible, ex);
        }
    }

    /// <summary>
    /// HU-023 (#176): la sonda sana con el aviso del script 07 según <c>BaseDatos:MotivoBusquedaAgotada</c> (<paramref name="modo"/>,
    /// normalizado) y lo que dijo la base (<paramref name="estado"/>; <c>null</c> si no se consultó).
    /// </summary>
    public static HealthCheckResult ConAvisoScript07(HealthCheckResult sana, string modo, EstadoScript07? estado) => modo switch
    {
        clsOpcionesBaseDatos.ModoDeshabilitado => HealthCheckResult.Healthy(DescripcionMotivoDeshabilitado),
        clsOpcionesBaseDatos.ModoHabilitado => estado == EstadoScript07.NoAplicado ? HealthCheckResult.Healthy(DescripcionMotivoHabilitadoSin07) : sana,
        _ => estado switch
        {
            EstadoScript07.Aplicado => sana,
            EstadoScript07.NoVerificable => HealthCheckResult.Healthy(DescripcionSin07NoVerificable),
            _ => HealthCheckResult.Healthy(DescripcionSin07),
        },
    };

    /// <summary>Resultado de la sonda según las columnas que faltan (<c>Tabla.columna</c>) y si la base aún tiene <c>Cotizaciones</c>.</summary>
    public static HealthCheckResult Clasificar(IReadOnlyCollection<string> faltantes, bool cotizacionesViejas)
    {
        ArgumentNullException.ThrowIfNull(faltantes);
        if (faltantes.Count == 0)
        {
            return HealthCheckResult.Healthy(DescripcionDisponible);
        }

        // HU-040.1 (#229): TokensRefresco.rolSesion es del 16, no del 0002.
        if (faltantes.Any(f => (f.StartsWith("TokensRefresco.", StringComparison.OrdinalIgnoreCase) && !Columnas16.Contains(f))
                               || string.Equals(f, "Sesiones.estadoAgente", StringComparison.OrdinalIgnoreCase)))
        {
            return HealthCheckResult.Unhealthy(DescripcionEsquemaIncompleto);
        }

        if (cotizacionesViejas && faltantes.Any(f => f.StartsWith("CotizacionesSolicitud.", StringComparison.OrdinalIgnoreCase)))
        {
            return HealthCheckResult.Unhealthy(DescripcionEsquemaSin0009);
        }

        // Faltan columnas de los ajustes que van después del 0009 (0010, 02 del cubicador, 03, 04, 05, 06, 09, 10, 11, 12, 13, 14, 16, 17, 18, 19 y 20):
        // se pide el primero que falta, en el orden en que se aplican.
        if (faltantes.All(f => Columnas0010.Contains(f) || Columnas02.Contains(f) || Columnas03.Contains(f) || Columnas04.Contains(f)
                               || Columnas05.Contains(f) || Columnas06.Contains(f) || Columnas09.Contains(f) || Columnas10.Contains(f)
                               || Columnas11.Contains(f) || Columnas12.Contains(f) || Columnas13.Contains(f)
                               || Columnas14.Contains(f) || Columnas16.Contains(f) || Columnas17.Contains(f) || Columnas18.Contains(f)
                               || Columnas19.Contains(f) || Columnas20.Contains(f)))
        {
            return HealthCheckResult.Unhealthy(
                faltantes.Any(Columnas0010.Contains) ? DescripcionEsquemaSin0010
                : faltantes.Any(Columnas02.Contains) ? DescripcionEsquemaSin02
                : faltantes.Any(Columnas03.Contains) ? DescripcionEsquemaSin03
                : faltantes.Any(Columnas04.Contains) ? DescripcionEsquemaSin04
                : faltantes.Any(Columnas05.Contains) ? DescripcionEsquemaSin05
                : faltantes.Any(Columnas06.Contains) ? DescripcionEsquemaSin06
                : faltantes.Any(Columnas09.Contains) ? DescripcionEsquemaSin09
                : faltantes.Any(Columnas10.Contains) ? DescripcionEsquemaSin10
                : faltantes.Any(Columnas11.Contains) ? DescripcionEsquemaSin11
                : faltantes.Any(Columnas12.Contains) ? DescripcionEsquemaSin12
                : faltantes.Any(Columnas13.Contains) ? DescripcionEsquemaSin13
                : faltantes.Any(Columnas14.Contains) ? DescripcionEsquemaSin14
                : faltantes.Any(Columnas16.Contains) ? DescripcionEsquemaSin16
                : faltantes.Any(Columnas17.Contains) ? DescripcionEsquemaSin17
                : faltantes.Any(Columnas18.Contains) ? DescripcionEsquemaSin18
                : faltantes.Any(Columnas19.Contains) ? DescripcionEsquemaSin19
                : DescripcionEsquemaSin20);
        }

        return HealthCheckResult.Unhealthy(PrefijoEsquemaIncompleto + string.Join(", ", faltantes.Order(StringComparer.Ordinal).Take(5)));
    }

    /// <summary>
    /// <c>Tabla.columna</c> de cada columna del modelo EF que no existe en <c>dbo_cotifly</c> (<c>COL_LENGTH</c> es NULL si no existe la
    /// columna o la tabla), y <see cref="CheckEstadoEnvio"/> si falta ese <c>CHECK</c>. Los nombres salen del modelo (código), nunca de
    /// datos del usuario.
    /// </summary>
    public static string ConsultaFaltantes(IModel modelo)
    {
        ArgumentNullException.ThrowIfNull(modelo);
        var filas = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entidad in modelo.GetEntityTypes())
        {
            if (entidad.GetTableName() is not { } tabla)
            {
                continue;
            }

            var objeto = StoreObjectIdentifier.Table(tabla, entidad.GetSchema());
            foreach (var propiedad in entidad.GetProperties())
            {
                if (propiedad.GetColumnName(objeto) is { } columna)
                {
                    filas.Add($"(N'{tabla}', N'{columna}')");
                }
            }
        }

        var sql = new StringBuilder("SELECT CONCAT(t.tabla, N'.', t.columna) AS [Value] FROM (VALUES ");
        sql.AppendJoin(", ", filas);
        sql.Append(") AS t(tabla, columna) WHERE COL_LENGTH(N'dbo_cotifly.' + t.tabla, t.columna) IS NULL");
        sql.Append($" UNION ALL SELECT N'{CheckEstadoEnvio}' WHERE NOT EXISTS (SELECT 1 FROM sys.check_constraints ")
            .Append("WHERE name = N'ck_cotizaciones_estadoEnvio' AND parent_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud'))");
        return sql.ToString();
    }
}
