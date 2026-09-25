using Cotifly.Aplicacion.Margen.Interfaces;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Fuente de margen por defecto mientras no existen los adaptadores reales de SAP TM (<c>export_json</c> | <c>odata</c>,
/// #13): no está configurada y nunca tiene registro (devuelve <c>null</c>, no lanza). El resolver usa entonces la
/// política de la línea con el detalle "SAP TM no configurado: se usa la política de la línea". Una caída real de
/// un adaptador configurado sigue siendo <c>MARGEN_FUENTE_NO_DISPONIBLE</c> (paridad con el Python).
/// </summary>
public sealed class clsFuenteMargenNoConfigurada : IClsFuenteMargen
{
    public const string ModoNoConfigurada = "no_configurada";

    public string Modo => ModoNoConfigurada;

    public bool EstaConfigurada => false;

    public Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default) =>
        Task.FromResult<decimal?>(null);
}
