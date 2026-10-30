using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Precio.Services;
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
    private string _entorno = "Development";

    public clsContextoEscenario()
    {
        // Carpeta de PDFs fija durante el escenario: sobrevive a Reconfigurar (#11).
        _configuracion = new Dictionary<string, string?>
        {
            ["Pdf:StorageDir"] = clsServiciosPruebas.CarpetaPdfsTemporal(),

            // HU-021.6: los escenarios del PDF legado (cotización "del chat") lo encienden; en producción está apagado.
            ["Pdf:PermitirCotizacionChat"] = "true",

            // La TRM (HU-012.1) nunca sale a la red: los adaptadores reales consultan las fuentes falsas del escenario. Por defecto solo la
            // Superintendencia; los escenarios de DolarApi eligen el proveedor con UsarProveedorTrm.
            ["Trm:Url"] = clsTrmFalso.Url,
            ["Trm:UrlDolarApi"] = clsDolarApiFalso.Url,
            ["Trm:Proveedor"] = "superfinanciera",

            // HU-033 (#199): el correo sale por el MailSender falso; fuera de Production va al buzón de pruebas.
            ["Correo:MailSender:UrlBase"] = clsMailSenderFalso.UrlBase,
            ["Correo:Proteccion:BuzonPruebas"] = clsMailSenderFalso.BuzonPruebas,
        };
        _proveedor = CrearProveedor();
    }

    public clsRelojFalso Reloj { get; } = new();

    /// <summary>Fuente SAP TM del escenario por defecto: la muestra del Python en memoria.</summary>
    public clsFuenteMargenMuestra FuenteSap { get; } = new();

    /// <summary>SAP TM falso (sin red) que atiende al adaptador OData cuando el escenario usa <see cref="UsarFuenteSapReal"/>.</summary>
    public clsSapTmFalso SapFalso { get; } = new();

    /// <summary>Datos abiertos de la Superintendencia Financiera falsos (TRM, HU-012.1) que atienden al adaptador real.</summary>
    public clsTrmFalso TrmFalso { get; } = new();

    /// <summary>DolarApi falso (TRM, HU-012.1, ADR-014) que atiende al adaptador real.</summary>
    public clsDolarApiFalso DolarApiFalso { get; } = new();

    /// <summary>Comerciales del escenario por nombre de usuario.</summary>
    public Dictionary<string, Guid> Usuarios { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>HU-033 (#199): MailSender falso del escenario (nunca envía correos reales).</summary>
    public clsMailSenderFalso MailSender { get; } = new();

    /// <summary>Correos que recibió el MailSender falso.</summary>
    public int CorreosRecibidos => MailSender.Recibidos.Count;

    /// <summary>HU-033 (#199): rehace el proveedor en otro ambiente (<c>Production</c> admite enviar sin protección).</summary>
    public void UsarEntorno(string entorno, IDictionary<string, string?> configuracion)
    {
        _entorno = entorno;
        Reconfigurar(configuracion);
    }

    /// <summary>
    /// HU-033 (#199): una vuelta del worker de correos solo con los correos en curso de <paramref name="folio"/> (como lo haría el worker
    /// con ellos; la agenda es compartida con otros escenarios). Devuelve cuántos procesó.
    /// </summary>
    public async Task<int> ProcesarCorreosAsync(string folio)
    {
        var correos = await ConsultarAsync(async s =>
        {
            var cotizacion = await s.GetRequiredService<Aplicacion.Cotizaciones.Repositories.IClsCotizacionRepository>().ObtenerPorFolioAsync(folio);
            return await s.GetRequiredService<Aplicacion.Correo.Repositories.IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion!.Id);
        });
        var procesados = 0;
        foreach (var correo in correos.Where(c => c.SiguienteConsultaAt is not null))
        {
            var resultado = await ConsultarAsync(s => s.GetRequiredService<Aplicacion.Correo.Services.IClsSeguimientoCorreosService>()
                .ProcesarAsync(new Aplicacion.Correo.Repositories.clsConsultaCorreoPendiente(correo.Id, correo.SiguienteConsultaAt!.Value)));
            procesados += resultado == Aplicacion.Correo.Services.ResultadoConsultaCorreo.NoReclamado ? 0 : 1;
        }

        return procesados;
    }

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
            entorno: _entorno,
            configurar: s =>
            {
                s.AddHttpClient(clsFuenteMargenOData.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => SapFalso);
                UsarTrmFalsa(s);
            });
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
            entorno: _entorno,
            configurar: s =>
            {
                s.AddSingleton<IClsFuenteMargen>(FuenteSap);
                UsarTrmFalsa(s);
            });

    private void UsarTrmFalsa(IServiceCollection servicios)
    {
        servicios.AddHttpClient(clsProveedorTrmSuperfinanciera.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => TrmFalso);
        servicios.AddHttpClient(clsProveedorTrmDolarApi.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => DolarApiFalso);
        servicios.AddHttpClient(Cotifly.Infraestructura.Correo.Services.clsTransporteMailSender.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => MailSender);
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
        TrmFalso.Dispose();
        DolarApiFalso.Dispose();
        MailSender.Dispose();
    }
}
