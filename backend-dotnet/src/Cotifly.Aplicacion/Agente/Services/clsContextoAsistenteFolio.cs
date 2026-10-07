using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Agente.Services;

/// <summary>
/// Contexto del turno del asistente por folio (HU-013.6, #102; uno por ámbito de DI): la cotización de la conversación (ya validado
/// que es del comercial), su línea de negocio y la propuesta del turno. Las herramientas leen la cotización de aquí, nunca de un
/// argumento del LLM: el asistente solo opera sobre ESA cotización.
/// </summary>
public sealed class clsContextoAsistenteFolio
{
    private clsCotizacion? _cotizacion;

    public Guid UsuarioId { get; private set; }

    /// <summary>Cotización de la conversación (cargada y validada por el runner antes del loop).</summary>
    public clsCotizacion Cotizacion => _cotizacion ?? throw new InvalidOperationException("El contexto del asistente no se inició.");

    /// <summary>Nombre de la línea de negocio (<c>MargenPoliticas.lineaNegocio</c>), o <c>null</c>.</summary>
    public string? LineaNegocio { get; private set; }

    /// <summary>Última propuesta del turno (una por turno como máximo: una nueva reemplaza la anterior), o <c>null</c>.</summary>
    public clsAccionPropuesta? Propuesta { get; private set; }

    public bool Iniciado => _cotizacion is not null;

    public void Iniciar(Guid usuarioId, clsCotizacion cotizacion, string? lineaNegocio)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        UsuarioId = usuarioId;
        _cotizacion = cotizacion;
        LineaNegocio = lineaNegocio;
        Propuesta = null;
    }

    public void Proponer(clsAccionPropuesta propuesta)
    {
        ArgumentNullException.ThrowIfNull(propuesta);
        Propuesta = propuesta;
    }
}
