using Cotifly.Aplicacion.Margen.Exceptions;

namespace Cotifly.Aplicacion.Margen.Interfaces;

/// <summary>
/// Puerto (Strategy, ADR-002) de la rentabilidad por cliente en SAP TM (<c>FuenteMargen</c> del Python).
/// Las implementaciones reales (<c>export_json</c> | <c>odata</c>, según <c>SapTm:Modo</c>) llegan con #13;
/// mientras tanto Infraestructura registra una fuente no configurada.
/// </summary>
public interface IClsFuenteMargen
{
    /// <summary>Etiqueta de la fuente (<c>export_json</c>, <c>odata</c>, <c>no_configurada</c>...), para logs y auditoría.</summary>
    string Modo { get; }

    /// <summary>
    /// False si no hay fuente SAP TM configurada: el resolver no la consulta y usa la política de la línea
    /// (no es una caída, que sí se reporta como <c>MARGEN_FUENTE_NO_DISPONIBLE</c>).
    /// </summary>
    bool EstaConfigurada { get; }

    /// <summary>
    /// <c>Value</c> de SAP TM (porcentaje tal como viene de SAP: 13.5 = 13,5 %) para
    /// (<paramref name="salesOrg"/>, <paramref name="trqType"/>, <paramref name="bpCliente"/>), o <c>null</c> si no hay registro.
    /// </summary>
    /// <exception cref="clsFuenteMargenNoDisponibleException">La fuente configurada no se pudo leer (export ausente o malformado, SAP caído, timeout).</exception>
    Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default);
}
