using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Aceptacion.Tests.Soporte;

/// <summary>
/// Estado de un escenario (Reqnroll lo crea por escenario y lo inyecta en los pasos): proveedor de
/// servicios real (<c>AddInfraestructura</c>) sobre la base de pruebas, reloj falso y resultados.
/// </summary>
public sealed class clsContextoEscenario : IDisposable
{
    private readonly ServiceProvider _proveedor;

    public clsContextoEscenario()
    {
        _proveedor = clsServiciosPruebas.Crear(clsEntornoAceptacion.BaseDatos.CadenaCotifly, Reloj);
    }

    public clsRelojFalso Reloj { get; } = new();

    /// <summary>Comerciales del escenario por nombre de usuario.</summary>
    public Dictionary<string, Guid> Usuarios { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Error de la última operación que se esperaba que fallara.</summary>
    public Exception? UltimoError { get; set; }

    /// <summary>Folios de la última consulta de "mis cotizaciones".</summary>
    public IReadOnlyList<string> FoliosConsultados { get; set; } = [];

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

    public void Dispose() => _proveedor.Dispose();
}
