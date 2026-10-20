using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Cliente de la solicitud de Nexo (HU-013.10, G14): Nexo exige <c>clienteBp</c> o <c>clienteId</c> (uuid del catálogo de Nexo),
/// nunca el NIT. Ambos salen del catálogo de Nexo (búsqueda por NIT), nunca del formulario. Exactamente uno de los dos.
/// </summary>
public sealed record clsClienteNexo
{
    private clsClienteNexo(string? bp, Guid? id)
    {
        Bp = bp;
        Id = id;
    }

    /// <summary>BP de SAP del cliente (<c>clienteBp</c>).</summary>
    public string? Bp { get; }

    /// <summary>Id del cliente o prospecto en el catálogo de Nexo (<c>clienteId</c>); solo si no tiene BP.</summary>
    public Guid? Id { get; }

    public static clsClienteNexo PorBp(string bp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bp);
        return new clsClienteNexo(bp.Trim(), null);
    }

    public static clsClienteNexo PorId(Guid id) =>
        id == Guid.Empty ? throw new ArgumentException("El id del cliente de Nexo no puede ser vacío.", nameof(id)) : new clsClienteNexo(null, id);
}

/// <summary>
/// Lo que se envía a Nexo en <c>POST /api/captura-solicitudes</c> (ADR-015, ADR-017): el cliente por su BP o su id del catálogo de Nexo
/// (nunca del formulario), el encabezado, las líneas (una en el formulario de HU-005.5, N en una licitación) y la línea de negocio de
/// Nexo (<c>MargenesPolitica.mapeoLineasNexo</c>, nula si la línea de Cotifly no tiene equivalente), que viaja en cada línea.
/// </summary>
public sealed record clsSolicitudNexo
{
    public clsSolicitudNexo(clsClienteNexo cliente, clsEncabezadoSolicitudCosteo encabezado, IReadOnlyList<clsLineaSolicitudCosteo> lineas, string? lineaNegocioNexo)
    {
        ArgumentNullException.ThrowIfNull(cliente);
        ArgumentNullException.ThrowIfNull(encabezado);
        ArgumentNullException.ThrowIfNull(lineas);
        if (lineas.Count == 0)
        {
            throw new ArgumentException("La solicitud debe traer al menos una línea.", nameof(lineas));
        }

        Cliente = cliente;
        Encabezado = encabezado;
        Lineas = lineas;
        LineaNegocioNexo = lineaNegocioNexo;
    }

    /// <summary>Solicitud de un cliente con BP.</summary>
    public clsSolicitudNexo(string clienteBp, clsEncabezadoSolicitudCosteo encabezado, IReadOnlyList<clsLineaSolicitudCosteo> lineas, string? lineaNegocioNexo)
        : this(clsClienteNexo.PorBp(clienteBp), encabezado, lineas, lineaNegocioNexo)
    {
    }

    /// <summary>Solicitud de una sola línea (formulario ampliado de HU-005.5) de un cliente con BP.</summary>
    public clsSolicitudNexo(string clienteBp, clsSolicitudCosteo solicitud, string? lineaNegocioNexo)
        : this(clienteBp, (solicitud ?? throw new ArgumentNullException(nameof(solicitud))).Encabezado, [solicitud.ALinea()], lineaNegocioNexo)
    {
    }

    public clsClienteNexo Cliente { get; }

    /// <summary>BP del cliente (<c>null</c> si viaja por <see cref="clsClienteNexo.Id"/>).</summary>
    public string? ClienteBp => Cliente.Bp;

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
    /// <summary>
    /// HU-023.4: ¿las líneas de los camiones del cubicador llevaron TODAS sus códigos de vehículo y carrocería (<c>Nexo:EnviarCodigosCubicador</c>
    /// = <c>siempre</c> al enviar)? Con <c>solo_conocidos</c> (HU-014 #188) unas sí y otras no: <c>false</c>, el caso seguro. El envío lo
    /// registra (<see cref="clsEnvioNexoRegistrado"/>) para que el polling compare contra lo que viajó.
    /// </summary>
    public bool CodigosCubicadorEnviados { get; init; }

    /// <summary>
    /// HU-031.1 (#194): los códigos de vehículo de Cotifly que viajaron con otro código porque el diccionario de alias de Nexo los tradujo
    /// (código de Cotifly → código enviado, <c>C2</c> → <c>2</c>). Vacío si ninguno. El envío lo registra con
    /// <see cref="CodigosCubicadorEnviados"/> (<see cref="clsEnvioNexoRegistrado"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> EquivalenciasVehiculo { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// HU-018.1 (#155): <c>scorePrioridad</c> del <c>CapturaResultDto</c> (COTIFLY-CONTRATO.md §5); <c>null</c> si Nexo no lo mandó, vino
    /// en <c>null</c> o no es un número. Se guarda en la cabecera (<c>CotizacionesSolicitud.scorePrioridad</c>, script 09).
    /// </summary>
    public decimal? ScorePrioridad { get; init; }

    /// <summary>¿Nexo ya costeó todas las líneas? (la cotización nace en <c>autocosteo</c>).</summary>
    public bool CosteoCompleto => !CosteoEnProceso && Lineas > 0 && LineasPorCostear == 0;
}

/// <summary>Cuerpo de <c>POST /api/cotizaciones/{id}/resultado</c> de Nexo: <c>comentario</c> se reenvía y no se persiste en Cotifly.</summary>
public sealed record clsResultadoNexo(bool Ganada, string? Motivo, string? Comentario);
