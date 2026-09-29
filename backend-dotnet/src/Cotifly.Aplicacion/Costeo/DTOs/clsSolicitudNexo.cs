using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Lo que se envía a Nexo en <c>POST /api/captura-solicitudes</c> (ADR-015): el cliente por su BP (del catálogo de Nexo, nunca del
/// formulario), el formulario ampliado y la línea de negocio de Nexo (<c>MargenesPolitica.mapeoLineasNexo</c>, nula si la línea de
/// Cotifly no tiene equivalente).
/// </summary>
public sealed record clsSolicitudNexo(string ClienteBp, clsSolicitudCosteo Solicitud, string? LineaNegocioNexo);

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
