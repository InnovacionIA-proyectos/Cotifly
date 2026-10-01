using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Lo que se envía a Nexo en <c>POST /api/captura-solicitudes</c> (ADR-015, ADR-017): el cliente por su BP (del catálogo de Nexo,
/// nunca del formulario), el encabezado, las líneas (una en el formulario de HU-005.5, N en una licitación) y la línea de negocio de
/// Nexo (<c>MargenesPolitica.mapeoLineasNexo</c>, nula si la línea de Cotifly no tiene equivalente), que viaja en cada línea.
/// </summary>
public sealed record clsSolicitudNexo
{
    public clsSolicitudNexo(string clienteBp, clsEncabezadoSolicitudCosteo encabezado, IReadOnlyList<clsLineaSolicitudCosteo> lineas, string? lineaNegocioNexo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clienteBp);
        ArgumentNullException.ThrowIfNull(encabezado);
        ArgumentNullException.ThrowIfNull(lineas);
        if (lineas.Count == 0)
        {
            throw new ArgumentException("La solicitud debe traer al menos una línea.", nameof(lineas));
        }

        ClienteBp = clienteBp;
        Encabezado = encabezado;
        Lineas = lineas;
        LineaNegocioNexo = lineaNegocioNexo;
    }

    /// <summary>Solicitud de una sola línea (formulario ampliado de HU-005.5).</summary>
    public clsSolicitudNexo(string clienteBp, clsSolicitudCosteo solicitud, string? lineaNegocioNexo)
        : this(clienteBp, (solicitud ?? throw new ArgumentNullException(nameof(solicitud))).Encabezado, [solicitud.ALinea()], lineaNegocioNexo)
    {
    }

    public string ClienteBp { get; }
    public clsEncabezadoSolicitudCosteo Encabezado { get; }
    public IReadOnlyList<clsLineaSolicitudCosteo> Lineas { get; }
    public string? LineaNegocioNexo { get; }

    /// <summary>El encabezado con la primera línea, como formulario de una línea (compatibilidad con HU-005.5).</summary>
    public clsSolicitudCosteo Solicitud => clsSolicitudCosteo.Desde(Encabezado, Lineas[0]);
}

/// <summary><c>CapturaResultDto</c> de Nexo, reducido a lo que usa Cotifly.</summary>
/// <param name="Id">Id de la solicitud en Nexo (<c>Cotizaciones.costeoId</c>).</param>
/// <param name="FletesCalculados">Líneas que Nexo costeó en la misma petición (auto-costeo).</param>
/// <param name="LineasPorCostear">Líneas que siguen sin flete.</param>
/// <param name="CosteoEnProceso">True si Nexo difirió el auto-costeo a segundo plano (más de 10 líneas).</param>
public sealed record clsSolicitudCreadaNexo(string Id, int Lineas, int FletesCalculados, int LineasPorCostear, bool CosteoEnProceso)
{
    /// <summary>¿Nexo ya costeó todas las líneas? (la cotización nace en <c>autocosteo</c>).</summary>
    public bool CosteoCompleto => !CosteoEnProceso && Lineas > 0 && LineasPorCostear == 0;
}

/// <summary>Cuerpo de <c>POST /api/cotizaciones/{id}/resultado</c> de Nexo: <c>comentario</c> se reenvía y no se persiste en Cotifly.</summary>
public sealed record clsResultadoNexo(bool Ganada, string? Motivo, string? Comentario);
