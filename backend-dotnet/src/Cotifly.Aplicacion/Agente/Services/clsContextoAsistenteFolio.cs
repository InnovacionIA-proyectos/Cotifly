using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Contexto del turno del asistente por folio (uno por ámbito de DI): el comercial, el hilo de la sesión, la cotización
/// de la conversación (ya validado que es del comercial), su línea de negocio y la propuesta del turno. Las herramientas leen la
/// cotización de aquí, nunca de un argumento del LLM: el asistente solo opera sobre ESA cotización. El filtro de auditoría lee el hilo.
/// </summary>
public sealed class clsContextoAsistenteFolio
{
    private clsCotizacionSolicitud? _cotizacion;

    public Guid UsuarioId { get; private set; }

    /// <summary>Hilo de la sesión (<c>web/{sesion_id}/{folio}</c>): va en cada evento de <c>EventosHerramienta</c>.</summary>
    public string ThreadId { get; private set; } = string.Empty;

    /// <summary>Cotización de la conversación (cargada y validada por el runner antes del loop).</summary>
    public clsCotizacionSolicitud Cotizacion => _cotizacion ?? throw new InvalidOperationException("El contexto del asistente no se inició.");

    /// <summary>Nombre de la línea de negocio (<c>MargenPoliticas.lineaNegocio</c>), o <c>null</c>.</summary>
    public string? LineaNegocio { get; private set; }

    /// <summary>
    /// La línea de NEXO de esa línea de negocio (<c>MargenesPolitica.mapeoLineasNexo</c>), o <c>null</c>. Decide las horas por defecto
    /// del DU (1 + 1 en una devolución de contenedor) cuando el asistente propone una sola hora.
    /// </summary>
    public string? LineaNegocioNexo { get; private set; }

    /// <summary>Última propuesta del turno (una por turno como máximo: una nueva reemplaza la anterior), o <c>null</c>.</summary>
    public clsAccionPropuesta? Propuesta { get; private set; }

    public bool Iniciado => _cotizacion is not null;

    public void Iniciar(Guid usuarioId, clsCotizacionSolicitud cotizacion, string? lineaNegocio, string threadId, string? lineaNegocioNexo = null)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        UsuarioId = usuarioId;
        _cotizacion = cotizacion;
        LineaNegocio = lineaNegocio;
        LineaNegocioNexo = lineaNegocioNexo;
        ThreadId = threadId;
        Propuesta = null;
    }

    public void Proponer(clsAccionPropuesta propuesta)
    {
        ArgumentNullException.ThrowIfNull(propuesta);
        Propuesta = propuesta;
    }
}
