using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.Repositories;

/// <summary>
/// Puerto de persistencia de cotizaciones (<c>Cotizaciones</c>). Los métodos "DelUsuario" aplican el
/// aislamiento por comercial (ADR-009): si la cotización no es del usuario, devuelven <c>null</c>
/// igual que si no existiera (el endpoint responde 404).
/// </summary>
public interface IClsCotizacionRepository
{
    /// <summary>Límite de <see cref="ListarPorUsuarioAsync"/> (contrato: máximo 100).</summary>
    const int LimitePorDefecto = 100;

    /// <summary>Agrega una cotización nueva (se escribe al guardar la unidad de trabajo).</summary>
    void Agregar(clsCotizacion cotizacion);

    /// <summary>Agrega las posiciones de cubicaje de una cotización (script 0004, HU-005.8); se escriben al guardar.</summary>
    void AgregarPosiciones(IEnumerable<clsCotizacionPosicion> posiciones);

    /// <summary>Posiciones de cubicaje de la cotización, en el orden del formulario (vacía si no tiene).</summary>
    Task<IReadOnlyList<clsCotizacionPosicion>> ListarPosicionesAsync(Guid cotizacionId, CancellationToken cancellationToken = default);

    /// <summary>Cotización por folio, sin filtro de dueño (uso interno de herramientas del agente).</summary>
    Task<clsCotizacion?> ObtenerPorFolioAsync(string folio, CancellationToken cancellationToken = default);

    /// <summary>Cotización por folio solo si es del usuario.</summary>
    Task<clsCotizacion?> ObtenerPorFolioDelUsuarioAsync(string folio, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>Cotización por id solo si es del usuario (ej. <c>GET /web/cotizaciones/{id}/nueva-version</c>).</summary>
    Task<clsCotizacion?> ObtenerPorIdDelUsuarioAsync(Guid id, Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>¿Existe ya una cotización con ese folio? (folio único, <c>uq_cotizaciones_folio</c>).</summary>
    Task<bool> ExisteFolioAsync(string folio, CancellationToken cancellationToken = default);

    /// <summary>
    /// Vuelve a leer de la base los valores de una cotización ya cargada en esta unidad de trabajo (descarta lo que tenía en
    /// memoria). Lo usa el envío de correo dentro de su candado, para calcular <c>estadoEnvio</c> sobre el valor vigente.
    /// </summary>
    Task RecargarAsync(clsCotizacion cotizacion, CancellationToken cancellationToken = default);

    /// <summary>Cotizaciones del comercial, de la más reciente a la más antigua, hasta <paramref name="limite"/>.</summary>
    Task<IReadOnlyList<clsCotizacion>> ListarPorUsuarioAsync(Guid usuarioId, int limite = LimitePorDefecto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Igual que <see cref="ListarPorUsuarioAsync"/>, con el nombre de la línea de negocio (JOIN con <c>MargenesPolitica</c>)
    /// para <c>GET /web/mis-cotizaciones</c>. Solo las del comercial.
    /// </summary>
    Task<IReadOnlyList<clsCotizacionConLinea>> ListarConLineaPorUsuarioAsync(Guid usuarioId, int limite = LimitePorDefecto, CancellationToken cancellationToken = default);

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
