using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Colección de las pruebas de integración: comparten un único SQL Server 2019 (se crea solo si corre
/// alguna prueba de la colección, así <c>--filter-not-trait "Categoria=Integracion"</c> no necesita Docker).
/// </summary>
[CollectionDefinition(Nombre)]
public sealed class clsColeccionSqlServer : ICollectionFixture<clsBaseDatosPruebas>
{
    public const string Nombre = "SqlServer";
}

/// <summary>
/// Base de las pruebas de integración de persistencia: un proveedor de servicios real por prueba con
/// reloj falso. Cada prueba usa datos propios (folios y usuarios únicos) porque la base es compartida.
/// Las clases derivadas declaran <c>[Collection(clsColeccionSqlServer.Nombre)]</c> y
/// <c>[Trait("Categoria", "Integracion")]</c> (los atributos de xUnit no se heredan).
/// </summary>
public abstract class clsPruebaIntegracion : IAsyncDisposable
{
    private readonly ServiceProvider _proveedor;

    protected clsPruebaIntegracion(clsBaseDatosPruebas baseDatos)
        : this(baseDatos, configurar: null)
    {
    }

    /// <param name="baseDatos">SQL Server compartido de la colección.</param>
    /// <param name="configurar">Reemplazos de prueba sobre el registro real (ej. la fuente de margen SAP TM de muestra).</param>
    protected clsPruebaIntegracion(clsBaseDatosPruebas baseDatos, Action<IServiceCollection>? configurar)
    {
        ArgumentNullException.ThrowIfNull(baseDatos);
        BaseDatos = baseDatos;
        _proveedor = clsServiciosPruebas.Crear(baseDatos.CadenaCotifly, Reloj, baseDatos.CadenaIdentidad, configurar: configurar);
    }

    protected clsBaseDatosPruebas BaseDatos { get; }

    protected clsRelojFalso Reloj { get; } = new();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Nuevo ámbito (equivale a una petición HTTP: su propio DbContext y unidad de trabajo).</summary>
    protected AsyncServiceScope NuevoAmbito() => _proveedor.CreateAsyncScope();

    /// <summary>Ejecuta <paramref name="accion"/> en un ámbito nuevo y guarda los cambios.</summary>
    protected async Task EnAmbitoAsync(Func<IServiceProvider, Task> accion)
    {
        ArgumentNullException.ThrowIfNull(accion);
        await using var ambito = NuevoAmbito();
        await accion(ambito.ServiceProvider);
        await ambito.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosAsync(Ct);
    }

    /// <summary>Ejecuta una consulta en un ámbito nuevo (sin guardar).</summary>
    protected async Task<T> ConsultarAsync<T>(Func<IServiceProvider, Task<T>> consulta)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        await using var ambito = NuevoAmbito();
        return await consulta(ambito.ServiceProvider);
    }

    protected static clsCotiflyDbContext Contexto(IServiceProvider servicios) =>
        servicios.GetRequiredService<clsCotiflyDbContext>();

    /// <summary>Folio único por prueba (la base es compartida y <c>folio</c> es UNIQUE).</summary>
    protected static string FolioUnico() => $"PCN-T-{Guid.NewGuid():N}";

    /// <summary>Crea y guarda un comercial con nombre único; devuelve su id.</summary>
    protected async Task<Guid> CrearUsuarioAsync(string? nombreUsuario = null)
    {
        var usuario = clsUsuarioWeb.Crear(
            nombreUsuario ?? $"u{Guid.NewGuid():N}", "Comercial de prueba", "comercial@boterosoto.com.co", "Antioquia", Reloj.AhoraUtc).Valor;
        await EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsUsuarioWebRepository>().Agregar(usuario);
            return Task.CompletedTask;
        });
        return usuario.Id;
    }

    /// <summary>Cotización válida en memoria (sin guardar).</summary>
    protected static clsCotizacion NuevaCotizacion(Guid? usuarioId, string? folio = null, decimal precio = 1_500_000m) =>
        clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = folio ?? FolioUnico(),
            UsuarioId = usuarioId,
            LineaNegocioId = 1,
            ClienteNombre = "Cliente de prueba S.A.S.",
            NitCliente = "900123456",
            OrigenCliente = OrigenCliente.Cliente,
            Costo = 1_275_000m,
            MargenPolitica = 0.15m,
            MargenAplicado = 0.15m,
            OrigenMargen = OrigenMargen.PoliticaLinea,
            MetodoCalculo = "margen",
            PrecioFinal = precio,
            SpaceId = "web/prueba",
            ThreadId = "web/prueba",
        }).Valor;

    /// <summary>Guarda una cotización en un ámbito propio.</summary>
    protected Task GuardarAsync(clsCotizacion cotizacion) =>
        EnAmbitoAsync(s =>
        {
            s.GetRequiredService<IClsCotizacionRepository>().Agregar(cotizacion);
            return Task.CompletedTask;
        });

    public async ValueTask DisposeAsync()
    {
        await _proveedor.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
