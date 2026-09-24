namespace Cotifly.Aplicacion.Tarifas.DTOs;

/// <summary>Entrada de <c>buscar_tarifa</c> (mismos campos que <c>EntradaBuscarTarifa</c> del Python).</summary>
/// <param name="LineaNegocio">Línea de negocio.</param>
/// <param name="Origen">Ciudad o punto de origen (texto libre del LLM).</param>
/// <param name="Destino">Ciudad o punto de destino (texto libre del LLM).</param>
/// <param name="TipoVehiculo">Tipo de vehículo, si se conoce.</param>
/// <param name="TipoCarga">Tipo de carga, si se conoce.</param>
/// <param name="PesoKg">Peso de la carga en kg, si se conoce.</param>
public sealed record clsEntradaBuscarTarifa(
    string LineaNegocio,
    string Origen,
    string Destino,
    string? TipoVehiculo = null,
    string? TipoCarga = null,
    decimal? PesoKg = null);
