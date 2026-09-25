using Cotifly.Aplicacion.Margen.Exceptions;

namespace Cotifly.Aplicacion.Margen.Interfaces;

/// <summary>
/// Puerto (Strategy, ADR-002) de la rentabilidad por cliente en SAP TM (<c>FuenteMargen</c> del Python).
/// Infraestructura elige la implementación por <c>SapTm:Modo</c> (#13): <c>export_json</c> (<c>clsFuenteMargenExportJson</c>),
/// <c>odata</c> (<c>clsFuenteMargenOData</c>) o, sin modo, <c>clsFuenteMargenNoConfigurada</c>.
/// </summary>
public interface IClsFuenteMargen
{
    /// <summary>Etiqueta de la fuente (<c>export_json</c>, <c>odata</c>, <c>no_configurada</c>...), para logs y auditoría.</summary>
    string Modo { get; }

    /// <summary>
    /// False si no hay fuente SAP TM configurada: el resolver no la consulta y usa la política de la línea
    /// (no es una caída: una caída lanza <see cref="clsFuenteMargenNoDisponibleException"/>, se registra como warning
    /// y también cae a la política, con otro motivo en el detalle).
    /// </summary>
    bool EstaConfigurada { get; }

    /// <summary>
    /// <c>Value</c> de SAP TM (porcentaje tal como viene de SAP: 13.5 = 13,5 %) para
    /// (<paramref name="salesOrg"/>, <paramref name="trqType"/>, <paramref name="bpCliente"/>), o <c>null</c> si no hay registro.
    /// </summary>
    /// <exception cref="clsFuenteMargenNoDisponibleException">La fuente configurada no se pudo leer (export ausente o malformado, SAP caído, timeout).</exception>
    Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default);
}
