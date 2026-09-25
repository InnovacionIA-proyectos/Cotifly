using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Aceptacion.Tests.Soporte;

/// <summary>
/// Estado de un escenario (Reqnroll lo crea por escenario y lo inyecta en los pasos): proveedor de
/// servicios real (<c>AddAplicacion</c> + <c>AddInfraestructura</c>) sobre la base de pruebas y la <c>dbsolucionesti</c>
/// falsa, reloj falso, fuente SAP TM de muestra y resultados.
/// </summary>
public sealed class clsContextoEscenario : IDisposable
{
    private readonly Dictionary<string, string?> _configuracion;
    private ServiceProvider _proveedor;
    private clsServidorSmtpFalso? _relay;

    public clsContextoEscenario()
    {
        // Carpeta de PDFs fija durante el escenario: sobrevive a Reconfigurar (#11).
        _configuracion = new Dictionary<string, string?> { ["Pdf:StorageDir"] = clsServiciosPruebas.CarpetaPdfsTemporal() };
        _proveedor = CrearProveedor();
    }

    public clsRelojFalso Reloj { get; } = new();

    /// <summary>Fuente SAP TM del escenario por defecto: la muestra del Python en memoria.</summary>
    public clsFuenteMargenMuestra FuenteSap { get; } = new();

    /// <summary>SAP TM falso (sin red) que atiende al adaptador OData cuando el escenario usa <see cref="UsarFuenteSapReal"/>.</summary>
    public clsSapTmFalso SapFalso { get; } = new();

    /// <summary>Comerciales del escenario por nombre de usuario.</summary>
    public Dictionary<string, Guid> Usuarios { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Relay SMTP falso del escenario (se crea al pedirlo por primera vez, #11).</summary>
    public clsServidorSmtpFalso Relay => _relay ??= new clsServidorSmtpFalso();

    /// <summary>Correos que recibió el relay falso (0 si el escenario no lo creó).</summary>
    public int CorreosRecibidos => _relay?.Recibidos.Count ?? 0;

    /// <summary>Error de negocio de la última operación de PDF o correo (#11), o <c>null</c> si salió bien.</summary>
    public clsErrorDominio? UltimoErrorNegocio { get; set; }

    /// <summary>Bytes del último PDF generado (#11).</summary>
    public byte[]? UltimoPdf { get; set; }

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
        foreach (var (clave, valor) in configuracion)
        {
            _configuracion[clave] = valor;
        }

        _proveedor = clsServiciosPruebas.Crear(
            clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            Reloj,
            clsEntornoAceptacion.BaseDatos.CadenaIdentidad,
            configuracionExtra: _configuracion,
            configurar: s => s.AddHttpClient(clsFuenteMargenOData.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => SapFalso));
    }

    /// <summary>Rehace el proveedor con claves de configuración adicionales (ej. <c>Correo:Habilitado</c>), conservando las anteriores.</summary>
    public void Reconfigurar(IDictionary<string, string?> configuracion)
    {
        ArgumentNullException.ThrowIfNull(configuracion);
        foreach (var (clave, valor) in configuracion)
        {
            _configuracion[clave] = valor;
        }

        _proveedor.Dispose();
        _proveedor = CrearProveedor();
    }

    private ServiceProvider CrearProveedor() =>
        clsServiciosPruebas.Crear(
            clsEntornoAceptacion.BaseDatos.CadenaCotifly,
            Reloj,
            clsEntornoAceptacion.BaseDatos.CadenaIdentidad,
            configuracionExtra: _configuracion,
            configurar: s => s.AddSingleton<IClsFuenteMargen>(FuenteSap));

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
        _relay?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
