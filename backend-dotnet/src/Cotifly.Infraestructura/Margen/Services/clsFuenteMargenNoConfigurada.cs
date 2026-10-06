using Cotifly.Aplicacion.Margen.Interfaces;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Fuente de margen cuando <c>SapTm:Modo</c> está vacío (valor por defecto; con <c>export_json</c> u <c>odata</c> se usan
/// <see cref="clsFuenteMargenExportJson"/> o <see cref="clsFuenteMargenOData"/>, #13): no está configurada y nunca tiene
/// registro (devuelve <c>null</c>, no lanza). El resolver usa entonces la
/// política de la línea con el detalle "SAP TM no configurado: se usa la política de la línea". Una caída de un
/// adaptador configurado también cae a la política, con el detalle "SAP TM no disponible" y un warning (#13).
/// </summary>
public sealed class clsFuenteMargenNoConfigurada : IClsFuenteMargen
{
    public const string ModoNoConfigurada = "no_configurada";

    public string Modo => ModoNoConfigurada;

    public bool EstaConfigurada => false;

    public Task<decimal?> ObtenerValorAsync(string salesOrg, string trqType, string bpCliente, CancellationToken cancellationToken = default) =>
        Task.FromResult<decimal?>(null);
}
