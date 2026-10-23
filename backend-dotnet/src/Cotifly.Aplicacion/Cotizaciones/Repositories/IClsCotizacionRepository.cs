using Cotifly.Aplicacion.Costeo.DTOs;
using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.Repositories;

/// <summary>
/// Puerto de persistencia de las solicitudes (<c>CotizacionesSolicitud</c> y su agregado: rutas, camiones, mercancías y conexos; HU-021,
/// ADR-019). Los métodos <c>Obtener*</c> devuelven la cabecera con TODO su agregado, seguido por la unidad de trabajo. Los métodos "DelUsuario" aplican el
/// aislamiento por comercial (ADR-009): si la cotización no es del usuario, devuelven <c>null</c>
/// igual que si no existiera (el endpoint responde 404).
/// </summary>
/// <summary>Datos del Excel original de una licitación sin su contenido.</summary>
public sealed record clsArchivoLicitacionResumen(string NombreArchivo, string Hoja, int TamanoBytes);

public interface IClsCotizacionRepository
{
    /// <summary>Límite de <see cref="ListarPorUsuarioAsync"/> (contrato: máximo 100).</summary>
    const int LimitePorDefecto = 100;

    /// <summary>
    /// Agrega una solicitud nueva CON su agregado (rutas, camiones, mercancías y conexos, HU-021): todo se escribe al guardar la unidad
    /// de trabajo.
    /// </summary>
    void Agregar(clsCotizacionSolicitud cotizacion);

    /// <summary>Agrega un PDF invalidado al historial (script 0006, ADR-018 §11); se escribe al guardar.</summary>
    void AgregarHistorialPdf(clsCotizacionPdfHistorial historial);

    /// <summary>
    /// HU-021.8 (#135): guarda los conexos de Nexo previos a un POST de conexos incierto y la ruta pedida (<see cref="clsConexosPendientesNexo"/>),
    /// en la misma unidad de trabajo que la marca de conexos pendientes. Se guarda en <c>EventosHerramienta</c> (sin columna nueva).
    /// </summary>
    void RegistrarConexosPendientesNexo(clsConexosPendientesNexo pendientes);

    /// <summary>El último registro de <see cref="RegistrarConexosPendientesNexo"/> del folio, o <c>null</c> si no hay.</summary>
    Task<clsConexosPendientesNexo?> ObtenerConexosPendientesNexoAsync(string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-023.4 (#159): guarda lo que viajó en el alta a Nexo (<see cref="clsEnvioNexoRegistrado"/>) en <c>EventosHerramienta</c>, en la
    /// misma unidad de trabajo que el <c>solicitudNexoId</c>.
    /// </summary>
    void RegistrarEnvioNexo(clsEnvioNexoRegistrado envio);

    /// <summary>
    /// ¿Viajaron los códigos del cubicador en el alta de la solicitud de Nexo <paramref name="solicitudNexoId"/>? <c>null</c> si no hay
    /// registro (quien lo usa asume que no viajaron).
    /// </summary>
    Task<bool?> ObtenerCodigosCubicadorEnviadosAsync(string solicitudNexoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-031.1 (#194): los códigos de vehículo que viajaron traducidos por el alias en el alta de <paramref name="solicitudNexoId"/>
    /// (<see cref="clsEnvioNexoRegistrado.EquivalenciasVehiculo"/>: código de Cotifly → código enviado). Vacío si no hay registro o ninguno.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ObtenerEquivalenciasVehiculoAsync(string solicitudNexoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// E2E 2026-10-16: el id de la cotización de Nexo (<c>cotizacionNexoId</c>) ya guardado para la solicitud de Nexo
    /// <paramref name="solicitudNexoId"/>, o <c>null</c>. Con él el polling lee la cotización por su id en lugar de buscarla o crearla.
    /// </summary>
    Task<string?> ObtenerCotizacionNexoIdAsync(string solicitudNexoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decisión 10 (HU-021.9): agrega un registro del envío diferido de conexos a Nexo (<see cref="clsEnvioConexosNexo"/>) en
    /// <c>EventosHerramienta</c>; se escribe al guardar la unidad de trabajo.
    /// </summary>
    void RegistrarEnvioConexosNexo(clsEnvioConexosNexo envio);

    /// <summary>Los registros de <see cref="RegistrarEnvioConexosNexo"/> del folio y la solicitud de Nexo, del más antiguo al más reciente.</summary>
    Task<IReadOnlyList<clsEnvioConexosNexo>> ListarEnviosConexosNexoAsync(string folio, string solicitudNexoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// PDF invalidados de la cotización, del más antiguo al más reciente (vacía si no hay). Quedan en la unidad de trabajo: al
    /// regenerar el PDF se actualiza la referencia del archivo archivado.
    /// </summary>
    Task<IReadOnlyList<clsCotizacionPdfHistorial>> ListarHistorialPdfAsync(Guid cotizacionId, CancellationToken cancellationToken = default);

    /// <summary>Agrega el Excel original de una licitación (script 0005, HU-011.3); se escribe al guardar.</summary>
    void AgregarArchivoLicitacion(clsArchivoLicitacion archivo);

    /// <summary>
    /// Nombre y hoja del Excel original de la licitación, sin leer el contenido (hasta 10 MB), o <c>null</c> si se armó sin archivo.
    /// Para las vistas; el contenido solo se lee al exportar.
    /// </summary>
    Task<clsArchivoLicitacionResumen?> ObtenerResumenArchivoLicitacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default);

    /// <summary>Excel original de la licitación, o <c>null</c> si se armó sin archivo. Sin filtro de dueño: lo aplica el llamador.</summary>
    Task<clsArchivoLicitacion?> ObtenerArchivoLicitacionAsync(Guid cotizacionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclamo optimista de un reenvío a Nexo (HU-005.5): si la cotización sigue con el <c>updatedAt</c> leído y en
    /// <c>error_envio</c> o <c>pendiente_envio</c>, la deja en <c>pendiente_envio</c> con <c>updatedAt = ahora</c> y devuelve
    /// <c>true</c>; si otro envío la tocó antes, <c>false</c> (dos reintentos simultáneos nunca duplican la solicitud en Nexo).
    /// Escribe de inmediato, fuera de la unidad de trabajo: el llamador debe recargar la cotización.
    /// </summary>
    Task<bool> ReclamarReenvioCosteoAsync(Guid cotizacionId, DateTimeOffset updatedAtLeido, DateTimeOffset ahora, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-023.5 (#160): la cola de la cubicación en segundo plano: hasta <paramref name="lote"/> rutas en <c>pendiente</c> con
    /// <c>cubicacionDisponibleAt</c> vacío o vencido (<c>&lt;= ahora</c>), de la más antigua a la más reciente (índice filtrado de la propuesta 04).
    /// Con <paramref name="cotizacionId"/>, solo las de esa cotización.
    /// </summary>
    Task<IReadOnlyList<clsRutaPorCubicar>> ListarRutasPorCubicarAsync(
        DateTimeOffset ahora, int lote, Guid? cotizacionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-023.5: reclamo optimista de una ruta de la cola: si sigue en <c>pendiente</c> con el <c>cubicacionDisponibleAt</c> leído, lo
    /// mueve a <paramref name="reclamoHasta"/> y devuelve <c>true</c>; si otra réplica la tomó antes, <c>false</c>. Escribe de inmediato,
    /// fuera de la unidad de trabajo.
    /// </summary>
    Task<bool> ReclamarRutaPorCubicarAsync(Guid rutaId, DateTimeOffset? disponibleLeido, DateTimeOffset reclamoHasta, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-023.5: licitaciones con la cubicación terminada que no salieron a Nexo (el proceso se cortó entre la última ruta y el envío):
    /// <c>pendiente_envio</c>, sin <c>solicitudNexoId</c>, sin cambios desde <paramref name="sinCambiosDesde"/>, con alguna ruta que pasó
    /// por la cola y ninguna pendiente ni fallida. Hasta <paramref name="lote"/> ids; con <paramref name="cotizacionId"/>, solo esa.
    /// </summary>
    Task<IReadOnlyList<Guid>> ListarCubicadasSinEnviarAsync(
        DateTimeOffset sinCambiosDesde, int lote, Guid? cotizacionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Agenda del polling de Nexo (HU-005.6): hasta <paramref name="lote"/> cotizaciones con <c>costeoSiguienteConsultaAt &lt;=
    /// ahora</c>, de la más atrasada a la más reciente (índice filtrado del 0004).
    /// </summary>
    Task<IReadOnlyList<clsConsultaCosteoPendiente>> ListarConsultasCosteoPendientesAsync(DateTimeOffset ahora, int lote, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclama una cotización para consultarla (dos réplicas nunca la procesan a la vez): si <c>costeoSiguienteConsultaAt</c> sigue
    /// siendo <paramref name="esperada"/> la mueve a <paramref name="reclamoHasta"/> y devuelve <c>true</c>. Escribe de inmediato.
    /// </summary>
    Task<bool> ReclamarConsultaCosteoAsync(Guid cotizacionId, DateTimeOffset esperada, DateTimeOffset reclamoHasta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reparación de HU-013.14 (ADR-018 §17): agenda para <paramref name="ahora"/> la relectura de las cotizaciones en <c>entregada</c>
    /// con <c>solicitudNexoId</c>, sin <c>precioFinal</c>, sin PDF, sin <c>motivoEstadoCosteo</c> y sin agenda. Escribe de inmediato y
    /// devuelve cuántas agendó (idempotente: una agendada ya no cumple el filtro).
    /// </summary>
    Task<int> ReprogramarEntregadasSinPrecioAsync(DateTimeOffset ahora, CancellationToken cancellationToken = default);

    /// <summary>Cotización por id, sin filtro de dueño (uso interno del polling de Nexo).</summary>
    Task<clsCotizacionSolicitud?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Cotización por folio, sin filtro de dueño (uso interno de herramientas del agente).</summary>
    Task<clsCotizacionSolicitud?> ObtenerPorFolioAsync(string folio, CancellationToken cancellationToken = default);

    /// <summary>Cotización por folio solo si es del usuario.</summary>
    Task<clsCotizacionSolicitud?> ObtenerPorFolioDelUsuarioAsync(string folio, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Cotización por id solo si es del usuario (ej. <c>GET /web/cotizaciones/{id}/nueva-version</c>).</summary>
    Task<clsCotizacionSolicitud?> ObtenerPorIdDelUsuarioAsync(Guid id, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>¿Existe ya una cotización con ese folio? (folio único, <c>uq_cotizaciones_folio</c>).</summary>
    Task<bool> ExisteFolioAsync(string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Vuelve a leer de la base los valores de una cotización ya cargada (cabecera, rutas, camiones, mercancías y conexos que tiene en
    /// memoria) y la deja seguida por la unidad de trabajo, descartando lo que tenía. Lo usan el correo (dentro de su candado) y los flujos
    /// que reintentan después de un conflicto de versión.
    /// </summary>
    Task RecargarAsync(clsCotizacionSolicitud cotizacion, CancellationToken cancellationToken = default);

    /// <summary>Cabeceras de las cotizaciones del comercial (sin el agregado), de la más reciente a la más antigua, hasta <paramref name="limite"/>.</summary>
    Task<IReadOnlyList<clsCotizacionSolicitud>> ListarPorUsuarioAsync(Guid usuarioId, int limite = LimitePorDefecto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="ListarPorUsuarioAsync"/> (sin el agregado), con la línea de negocio y el margen de la ruta 1 (o "Varias" si
    /// sus rutas tienen distintas líneas, HU-021) para <c>GET /web/mis-cotizaciones</c>. Solo las del comercial. Una fila con un código
    /// fuera de catálogo no tumba la lista: se omite (HU-014.1).
    /// </summary>
    Task<IReadOnlyList<clsCotizacionConLinea>> ListarConLineaPorUsuarioAsync(Guid usuarioId, int limite = LimitePorDefecto, CancellationToken cancellationToken = default);

    /// <summary>
    /// HU-016.1 (#154): igual que <see cref="ListarConLineaPorUsuarioAsync"/> (misma proyección, mismo orden <c>createdAt DESC, folio DESC</c>
    /// y mismo tope), pero de varios usuarios a la vez (<c>WHERE usuarioId IN (...)</c>): el listado regional del gerente comercial. El tope
    /// es del conjunto, no por usuario. Una colección vacía devuelve una lista vacía sin consultar la base.
    /// </summary>
    Task<IReadOnlyList<clsCotizacionConLinea>> ListarPorUsuariosAsync(
        IReadOnlyCollection<Guid> usuarioIds, int limite = LimitePorDefecto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mayor consecutivo <c>NNN</c> ya usado con las <paramref name="iniciales"/> en la fecha <paramref name="segmentoFecha"/>
    /// (<c>DDMMAAAA</c>), contando todos los folios base con y sin NIT (<c>PCN-[nit-]INICIALES-DDMMAAAA-NNN</c>, ADR-011) de
    /// cualquier comercial: dos comerciales con las mismas iniciales comparten la secuencia, así el siguiente número nunca
    /// choca con <c>uq_cotizaciones_folio</c>. Los <c>-vN</c> no cuentan (no terminan en <c>NNN</c>). 0 si no hay ninguno.
    /// Se llama dentro del candado del folio (<c>IClsCandadoTransaccional</c>).
    /// </summary>
    Task<int> ObtenerUltimoConsecutivoDelDiaAsync(string iniciales, string segmentoFecha, CancellationToken cancellationToken = default);

    /// <summary>
    /// Mayor <c>version</c> de la familia de un folio base: la cotización con ese folio y las que llevan <c>{base}-vN</c>;
    /// 0 si no existe ninguna.
    /// </summary>
    Task<int> ObtenerVersionMaximaAsync(string folioBase, CancellationToken cancellationToken = default);
}
