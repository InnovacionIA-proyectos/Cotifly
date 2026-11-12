using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Cotifly.Dominio.Sesiones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Contexto de <c>dbo_cotifly</c> (lectura y escritura), Database First (ADR-008): el mapeo es Fluent
/// API escrito a mano contra las tablas que crean los scripts <c>db/migrations_mssql/0001</c> a <c>0010</c> (o
/// <c>db/scripts/01_crear_esquema.sql</c>; ver <c>Configurations/</c>). No hay migraciones EF: los cambios de esquema van en scripts nuevos.
/// </summary>
public sealed class clsCotiflyDbContext(DbContextOptions<clsCotiflyDbContext> options) : DbContext(options)
{
    public const string Esquema = "dbo_cotifly";
    public const string Collation = "Latin1_General_100_CI_AS_SC_UTF8";
    public const string CollationBinaria = "Latin1_General_100_BIN2";
    public const string TipoFecha = "datetimeoffset";
    public const string TipoTexto = "nvarchar(max)";

    /// <summary>SQL Server 2019 (evita que EF genere SQL que no existe en esa versión).</summary>
    public const int NivelCompatibilidad = 150;

    public DbSet<clsCotizacionSolicitud> CotizacionesSolicitud => Set<clsCotizacionSolicitud>();
    public DbSet<clsLineaSolicitud> CotizacionLineasSolicitud => Set<clsLineaSolicitud>();
    public DbSet<clsVehiculoLineaSolicitud> CotizacionVehiculosLineaSolicitud => Set<clsVehiculoLineaSolicitud>();
    public DbSet<clsMercancia> CotizacionMercancias => Set<clsMercancia>();
    public DbSet<clsServicioConexoRuta> CotizacionServiciosConexos => Set<clsServicioConexoRuta>();
    public DbSet<clsArchivoLicitacion> ArchivosLicitacion => Set<clsArchivoLicitacion>();
    public DbSet<clsCotizacionPdfHistorial> CotizacionPdfsHistorial => Set<clsCotizacionPdfHistorial>();
    public DbSet<clsMargenAjuste> MargenAjustes => Set<clsMargenAjuste>();
    public DbSet<clsCorreo> Correos => Set<clsCorreo>();
    public DbSet<clsCorreoDestinatario> CorreoDestinatarios => Set<clsCorreoDestinatario>();
    public DbSet<clsSesion> Sesiones => Set<clsSesion>();
    public DbSet<clsEventoConversacion> EventosConversacion => Set<clsEventoConversacion>();
    public DbSet<clsEventoHerramienta> EventosHerramienta => Set<clsEventoHerramienta>();
    public DbSet<clsUsuarioWeb> UsuariosWeb => Set<clsUsuarioWeb>();
    public DbSet<clsMargenPolitica> MargenesPolitica => Set<clsMargenPolitica>();
    public DbSet<clsSapMapeoTrqType> SapMapeoTrqType => Set<clsSapMapeoTrqType>();
    public DbSet<clsRol> Roles => Set<clsRol>();
    public DbSet<clsUsuarioRol> UsuariosRoles => Set<clsUsuarioRol>();
    public DbSet<clsTokenRefresco> TokensRefresco => Set<clsTokenRefresco>();
    public DbSet<clsTokenRevocado> TokensRevocados => Set<clsTokenRevocado>();

    /// <summary>HU-045.2 (#264): la tasa del seguro de la mercancía (script 20; EF solo la lee, la cambia Datos).</summary>
    public DbSet<clsTarifaSeguro> TarifasSeguro => Set<clsTarifaSeguro>();

    /// <summary>HU-046.3 (#269): los tramos de la política de seguros (script 21; EF solo los lee, la cambia Datos).</summary>
    public DbSet<clsPoliticaSeguroTramoFila> PoliticaSeguroTramos => Set<clsPoliticaSeguroTramoFila>();

    /// <summary>HU-046.3 (#269): las medidas de la política de seguros (script 21).</summary>
    public DbSet<clsPoliticaSeguroMedidaFila> PoliticaSeguroMedidas => Set<clsPoliticaSeguroMedidaFila>();

    /// <summary>HU-046.3 (#269): qué medida aplica en cada tramo y con qué nivel (script 21).</summary>
    public DbSet<clsPoliticaSeguroTramoMedidaFila> PoliticaSeguroTramoMedidas => Set<clsPoliticaSeguroTramoMedidaFila>();

    /// <summary>HU-046.3 (#269): los códigos de conexo de Nexo que cumplen cada medida (script 21).</summary>
    public DbSet<clsPoliticaSeguroMedidaConexoFila> PoliticaSeguroMedidaConexos => Set<clsPoliticaSeguroMedidaConexoFila>();

    /// <summary>HU-042.2 (#243): copia local del margen de SAP TM (script 17; la escribe <c>sp_sincronizar_margen_sap</c>, que llama la sincronización del backend; EF solo la lee).</summary>
    public DbSet<clsSapMargenClienteFila> SapMargenCliente => Set<clsSapMargenClienteFila>();

    /// <summary>HU-042.2 (#243): log de corridas de la sincronización que mantiene <see cref="SapMargenCliente"/> (script 17).</summary>
    public DbSet<clsSapMargenSyncLogFila> SapMargenSyncLog => Set<clsSapMargenSyncLogFila>();

    /// <summary>
    /// HU-042.2 (#243): tablas cuyo script (<c>db/scripts/17</c>, de Datos) no fija <c>COLLATE</c> en las columnas de texto: heredan la
    /// collation de la base. El modelo no les asigna <see cref="Collation"/> para no describir algo que el script no declara.
    /// </summary>
    public static readonly IReadOnlySet<string> TablasConCollationDeLaBase =
        new HashSet<string>(StringComparer.Ordinal) { "SapMargenCliente", "SapMargenSyncLog" };

    /// <summary>
    /// Database First: el modelo solo declara los índices que existen en los scripts. Se quita la
    /// convención que crea un índice por cada FK (el 0001 no indexa todas sus FK y la prueba de deriva
    /// compara índices en ambos sentidos).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Esquema);

        var espacioConfiguraciones = typeof(clsCotiflyDbContext).Namespace + ".Configurations";
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(clsCotiflyDbContext).Assembly,
            tipo => tipo.Namespace == espacioConfiguraciones);

        // Collation explícita de cada columna de texto, igual que en los scripts (documenta el esquema;
        // EF no la usa en consultas). La binaria (tokenHash) se fija en su configuración.
        foreach (var propiedad in modelBuilder.Model.GetEntityTypes()
                     .Where(e => e.GetTableName() is not { } tabla || !TablasConCollationDeLaBase.Contains(tabla))
                     .SelectMany(e => e.GetProperties())
                     .Where(p => p.GetCollation() is null
                                 && p.GetColumnType()?.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase) == true))
        {
            propiedad.SetCollation(Collation);
        }
    }
}
