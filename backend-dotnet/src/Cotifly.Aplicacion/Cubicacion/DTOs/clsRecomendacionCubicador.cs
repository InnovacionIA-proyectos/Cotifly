using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cubicacion.DTOs;

/// <summary>
/// Resultados de una fila de la recomendación del cubicador (Tabla 7, <c>recomendacionVehiculo</c>; HU-021.5, #133). C6 de
/// <c>docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md</c>: "sin selección" y "condicionada" son resultados, no errores.
/// </summary>
public static class clsResultadosRecomendacion
{
    /// <summary>El cubicador eligió el vehículo y la carrocería.</summary>
    public const string Seleccionado = "SELECCIONADO";

    /// <summary>El cubicador no pudo elegir: el camión se crea sin vehículo ni carrocería y lo resuelve la Mesa de Costeo de Nexo.</summary>
    public const string SinSeleccionAutomatica = "SIN_SELECCION_AUTOMATICA";

    /// <summary>
    /// Mercancía condicionada pendiente de aprobación de AdminTarifas: Cotifly no parte ni bloquea nada; la solicitud se envía completa
    /// a la Mesa de Costeo de Nexo (decisión del 2026-10-08 con el equipo del cubicador).
    /// </summary>
    public const string Condicionada = "CONDICIONADA";
}

/// <summary>
/// Un vehículo recomendado por el cubicador para una ruta (una fila de su Tabla 7): el id de la fila (C3: Cotifly guarda el id exacto
/// que usó), el tipo de vehículo y la carrocería con los códigos de Nexo (C4), el <b>peso total del vehículo</b> (C2: es el peso que
/// viaja a Nexo en la línea del camión), el resultado (<see cref="clsResultadosRecomendacion"/>, en mayúsculas) y su motivo.
/// </summary>
public sealed record clsVehiculoRecomendado(
    Guid? IdRecomendacionVehiculo,
    string? TipoVehiculo,
    string? Carroceria,
    decimal? PesoTotalKg,
    string Resultado,
    string? Motivo = null)
{
    /// <summary>¿El cubicador no eligió vehículo (C6)?</summary>
    public bool SinSeleccion => string.Equals(Resultado, clsResultadosRecomendacion.SinSeleccionAutomatica, StringComparison.Ordinal);

    /// <summary>¿La mercancía quedó condicionada (pendiente de AdminTarifas, C6)?</summary>
    public bool Condicionada => string.Equals(Resultado, clsResultadosRecomendacion.Condicionada, StringComparison.Ordinal);
}

/// <summary>Respuesta de <c>POST /recomendaciones</c> del cubicador para una ruta: un registro por vehículo recomendado.</summary>
public sealed record clsRecomendacionCubicador(IReadOnlyList<clsVehiculoRecomendado> Vehiculos);

/// <summary>Un tipo de mercancía del catálogo del cubicador (<c>GET /tipos-mercancia</c>): el id que se manda en la recomendación y su nombre.</summary>
public sealed record clsTipoMercanciaCubicador(Guid Id, string Nombre);

/// <summary>
/// Mercancía que se manda al cubicador por ruta: <c>idTipoMercancia</c> (del catálogo del cubicador), medidas en centímetros, cantidad
/// y peso por unidad en kilogramos.
/// </summary>
public sealed record clsMercanciaCubicador(Guid? IdTipoMercancia, int Cantidad, decimal LargoCm, decimal AnchoCm, decimal AltoCm, decimal? PesoKg)
{
    /// <summary>La mercancía de la captura (<see cref="clsPosicionCarga"/>) tal como viaja al cubicador.</summary>
    public static clsMercanciaCubicador Desde(clsPosicionCarga posicion)
    {
        ArgumentNullException.ThrowIfNull(posicion);
        return new clsMercanciaCubicador(
            posicion.IdTipoMercancia, posicion.Cantidad, posicion.LargoCm, posicion.AnchoCm, posicion.AltoCm, posicion.PesoKg);
    }
}
