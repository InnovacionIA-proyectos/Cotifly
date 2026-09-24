using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Aceptacion.Tests.Soporte;

/// <summary>
/// Estado de un escenario (Reqnroll lo crea por escenario y lo inyecta en los pasos): proveedor de
/// servicios real (<c>AddAplicacion</c> + <c>AddInfraestructura</c>) sobre la base de pruebas, reloj falso,
/// fuente SAP TM de muestra y resultados.
/// </summary>
public sealed class clsContextoEscenario : IDisposable
{
    private ServiceProvider _proveedor;

    public clsContextoEscenario()
    {
        _proveedor = clsServiciosPruebas.Crear(
            clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            Reloj,
            configurar: s => s.AddSingleton<IClsFuenteMargen>(FuenteSap));
    }

    public clsRelojFalso Reloj { get; } = new();

    /// <summary>Fuente SAP TM del escenario por defecto: la muestra del Python en memoria.</summary>
    public clsFuenteMargenMuestra FuenteSap { get; } = new();

    /// <summary>SAP TM falso (sin red) que atiende al adaptador OData cuando el escenario usa <see cref="UsarFuenteSapReal"/>.</summary>
    public clsSapTmFalso SapFalso { get; } = new();

    /// <summary>Comerciales del escenario por nombre de usuario.</summary>
    public Dictionary<string, Guid> Usuarios { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Error de la última operación que se esperaba que fallara.</summary>
    public Exception? UltimoError { get; set; }

    /// <summary>Folios de la última consulta de "mis cotizaciones".</summary>
    public IReadOnlyList<string> FoliosConsultados { get; set; } = [];

    /// <summary>
    /// Rehace el proveedor con el adaptador real de SAP TM que elige <c>SapTm:Modo</c> (#13): <c>export_json</c> sobre la
    /// muestra del Python u <c>odata</c> contra <see cref="SapFalso"/>, que responde con esa misma muestra.
    /// </summary>
    public void UsarFuenteSapReal(string modo)
    {
        var configuracion = new Dictionary<string, string?>
        {
            ["SapTm:Modo"] = modo,
            ["SapTm:ExportPath"] = clsSapTmFalso.RutaMuestra,
            ["SapTm:Url"] = clsSapTmFalso.UrlBase,
            ["SapTm:User"] = "svc_cotifly",
            ["SapTm:Pass"] = "Kq7-Vr2m-Xz9p",
        };

        _proveedor.Dispose();
        _proveedor = clsServiciosPruebas.Crear(
            clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            Reloj,
            configuracionExtra: configuracion,
            configurar: s => s.AddHttpClient(clsFuenteMargenOData.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => SapFalso));
    }

    /// <summary>Ejecuta <paramref name="accion"/> en un ámbito nuevo (como una petición) y guarda los cambios.</summary>
    public async Task EnAmbitoAsync(Func<IServiceProvider, Task> accion)
    {
        ArgumentNullException.ThrowIfNull(accion);
        await using var ambito = _proveedor.CreateAsyncScope();
        await accion(ambito.ServiceProvider);
        await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync();
    }

    /// <summary>Consulta en un ámbito nuevo, sin guardar.</summary>
    public async Task<T> ConsultarAsync<T>(Func<IServiceProvider, Task<T>> consulta)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        await using var ambito = _proveedor.CreateAsyncScope();
        return await consulta(ambito.ServiceProvider);
    }

    public void Dispose()
    {
        _proveedor.Dispose();
        SapFalso.Dispose();
    }
}
