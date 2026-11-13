namespace Cotifly.Aplicacion.Margen.DTOs;

/// <summary>
/// Resultado completo de la resolución de margen: la salida del contrato (<see cref="Salida"/>) más el
/// contexto que el estado de la conversación necesita (ADR-004: <c>LineaNegocioId</c>, <c>MargenPolitica</c>,
/// <c>MetodoCalculo</c>) y que no forma parte de <c>SalidaMargen</c>.
/// </summary>
/// <param name="Salida">Lo que devuelve la herramienta <c>obtener_margen</c>.</param>
/// <param name="LineaNegocio">Nombre canónico de la línea (el de <c>MargenesPolitica</c>), o el recibido si no hay política.</param>
/// <param name="LineaNegocioId">Id de <c>MargenesPolitica</c> (FK <c>Cotizaciones.lineaNegocioId</c>), o <c>null</c> sin política.</param>
/// <param name="MargenPolitica">Margen de la política de la línea (referencia de un ajuste negociado), o <c>null</c> sin política.</param>
/// <param name="MetodoCalculo">Método de cálculo de la línea (<c>margen</c> por defecto).</param>
/// <param name="SincronizacionVieja">
/// La copia de SAP TM consultada tiene la última corrida <c>ok</c> más vieja que <c>SapTm:AvisoSincronizacionHoras</c>
/// (con margen de SAP o sin registro para el cliente: el dato puede faltar porque la copia está vieja); <c>null</c> si no.
/// </param>
public sealed record clsMargenResuelto(
    clsSalidaMargen Salida,
    string LineaNegocio,
    int? LineaNegocioId,
    decimal? MargenPolitica,
    string MetodoCalculo,
    clsSincronizacionSapVieja? SincronizacionVieja = null);
