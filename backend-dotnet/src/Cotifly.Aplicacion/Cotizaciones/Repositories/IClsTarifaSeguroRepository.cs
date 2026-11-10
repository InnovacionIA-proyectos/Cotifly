namespace Cotifly.Aplicacion.Cotizaciones.Repositories;

/// <summary>
/// HU-045.2 (#264): lectura de la tasa vigente del seguro de la mercancía (<c>dbo_cotifly.TarifasSeguro</c>, script 20). Solo lectura: la
/// tasa la cambia Datos con un script de operación, no el backend.
/// </summary>
public interface IClsTarifaSeguroRepository
{
    /// <summary>La tasa activa (<c>isActive = 1</c>; hay a lo sumo una), o <c>null</c> si no hay ninguna.</summary>
    Task<decimal?> ObtenerTasaVigenteAsync(CancellationToken cancellationToken = default);
}
