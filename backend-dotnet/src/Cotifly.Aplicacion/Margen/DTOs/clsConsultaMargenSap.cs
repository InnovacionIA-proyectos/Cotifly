namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// Respuesta de la fuente del margen de SAP TM. Con <see cref="EstadoConsultaMargenSap.ConValor"/>,
/// <see cref="Porcentaje"/> trae el valor tal como lo manda SAP (13.5 = 13,5 %; el resolver lo divide por 100 una sola vez).
/// </summary>
/// <param name="Estado">Qué encontró la fuente.</param>
/// <param name="Porcentaje">Margen recomendado en porcentaje, solo con <see cref="EstadoConsultaMargenSap.ConValor"/>.</param>
/// <param name="ActualizadoAt">Cuándo cambió la fila por última vez en la copia local (solo con valor; <c>null</c> si la fuente no lo sabe).</param>
/// <param name="UltimaSincronizacion">Fin de la última corrida <c>ok</c> de la sincronización (<c>null</c> si nunca la hubo o si la fuente no lo sabe).</param>
public sealed record clsConsultaMargenSap(
    EstadoConsultaMargenSap Estado,
    decimal? Porcentaje = null,
    DateTimeOffset? ActualizadoAt = null,
    DateTimeOffset? UltimaSincronizacion = null)
{
    /// <summary>La copia local nunca se sincronizó.</summary>
    public static clsConsultaMargenSap NoConfigurada { get; } = new(EstadoConsultaMargenSap.NoConfigurada);

    /// <summary>Sin fila activa para el cliente.</summary>
    public static clsConsultaMargenSap SinRegistro(DateTimeOffset? ultimaSincronizacion = null) =>
        new(EstadoConsultaMargenSap.SinRegistro, UltimaSincronizacion: ultimaSincronizacion);

    /// <summary>Margen de SAP TM del cliente (porcentaje tal como viene de SAP).</summary>
    public static clsConsultaMargenSap ConValor(decimal porcentaje, DateTimeOffset? actualizadoAt = null, DateTimeOffset? ultimaSincronizacion = null) =>
        new(EstadoConsultaMargenSap.ConValor, porcentaje, actualizadoAt, ultimaSincronizacion);
}
