using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Dominio.Comun;
using Cotifly.Infraestructura.Precio.Services;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Aceptacion.Tests.Soporte;

/// <summary>
/// Estado de un escenario (Reqnroll lo crea por escenario y lo inyecta en los pasos): proveedor de
/// servicios real (<c>AddAplicacion</c> + <c>AddInfraestructura</c>) sobre la base de pruebas y la <c>dbsolucionesti</c>
/// falsa, reloj falso, fuente SAP TM de muestra (o la copia local real) y resultados.
/// </summary>
public sealed class clsContextoEscenario : IDisposable
{
    private readonly Dictionary<string, string?> _configuracion;
    private ServiceProvider _proveedor;
    private string _entorno = "Development";
    private bool _copiaLocalSap;

    public clsContextoEscenario()
    {
        // Carpeta de PDFs fija durante el escenario: sobrevive a Reconfigurar.
        _configuracion = new Dictionary<string, string?>
        {
            ["Pdf:StorageDir"] = clsServiciosPruebas.CarpetaPdfsTemporal(),

            // Los escenarios del PDF legado (cotización "del chat") lo encienden; en producción está apagado.
            ["Pdf:PermitirCotizacionChat"] = "true",

            // La TRM nunca sale a la red: los adaptadores reales consultan las fuentes falsas del escenario. Por defecto solo la
            // Superintendencia; los escenarios de DolarApi eligen el proveedor con UsarProveedorTrm.
            ["Trm:Url"] = clsTrmFalso.Url,
            ["Trm:UrlDolarApi"] = clsDolarApiFalso.Url,
            ["Trm:Proveedor"] = "superfinanciera",

            // El correo sale por el MailSender falso; fuera de Production va al buzón de pruebas.
            ["Correo:MailSender:UrlBase"] = clsMailSenderFalso.UrlBase,
            ["Correo:Proteccion:BuzonPruebas"] = clsMailSenderFalso.BuzonPruebas,
        };
        _proveedor = CrearProveedor();
    }

    public clsRelojFalso Reloj { get; } = new();

    /// <summary>Fuente SAP TM del escenario por defecto: la muestra del Python en memoria.</summary>
    public clsFuenteMargenMuestra FuenteSap { get; } = new();

    /// <summary>Datos abiertos de la Superintendencia Financiera falsos (TRM) que atienden al adaptador real.</summary>
    public clsTrmFalso TrmFalso { get; } = new();

    /// <summary>DolarApi falso (TRM, ADR-014) que atiende al adaptador real.</summary>
    public clsDolarApiFalso DolarApiFalso { get; } = new();

    /// <summary>Comerciales del escenario por nombre de usuario.</summary>
    public Dictionary<string, Guid> Usuarios { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MailSender falso del escenario (nunca envía correos reales).</summary>
    public clsMailSenderFalso MailSender { get; } = new();

    /// <summary>Correos que recibió el MailSender falso.</summary>
    public int CorreosRecibidos => MailSender.Recibidos.Count;

    /// <summary>Rehace el proveedor en otro ambiente (<c>Production</c> admite enviar sin protección).</summary>
    public void UsarEntorno(string entorno, IDictionary<string, string?> configuracion)
    {
        _entorno = entorno;
        Reconfigurar(configuracion);
    }

    /// <summary>
    /// Una vuelta del worker de correos solo con los correos en curso de <paramref name="folio"/> (como lo haría el worker
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

    /// <summary>Error de negocio de la última operación de PDF o correo, o <c>null</c> si salió bien.</summary>
    public clsErrorDominio? UltimoErrorNegocio { get; set; }

    /// <summary>Bytes del último PDF generado.</summary>
    public byte[]? UltimoPdf { get; set; }

    /// <summary>Error de la última operación que se esperaba que fallara.</summary>
    public Exception? UltimoError { get; set; }

    /// <summary>Folios de la última consulta de "mis cotizaciones".</summary>
    public IReadOnlyList<string> FoliosConsultados { get; set; } = [];

    /// <summary>
    /// Rehace el proveedor con la fuente real del margen de SAP TM (<c>clsFuenteMargenTablaLocal</c>, la copia local
    /// <c>dbo_cotifly.SapMargenCliente</c> de la base de aceptación) en vez de la muestra en memoria.
    /// </summary>
    public void UsarCopiaLocalSap()
    {
        _copiaLocalSap = true;
        _proveedor.Dispose();
        _proveedor = CrearProveedor();
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
                if (!_copiaLocalSap)
                {
                    s.AddSingleton<IClsFuenteMargen>(FuenteSap);
                }

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
        TrmFalso.Dispose();
        DolarApiFalso.Dispose();
        MailSender.Dispose();
    }
}
