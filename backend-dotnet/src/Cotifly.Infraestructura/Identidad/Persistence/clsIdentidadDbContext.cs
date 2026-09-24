using Cotifly.Dominio.Identidad;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Identidad.Persistence;

/// <summary>
/// Contexto de SOLO LECTURA sobre <c>dbsolucionesti</c> (ADR-008): usuarios corporativos de
/// <c>tblSAPUsuariosApp</c> para revalidar el login (#12). Sin tracking y <c>SaveChanges</c> lanza
/// <see cref="InvalidOperationException"/>. La garantía real de solo lectura son los permisos del
/// login <c>cotifly_app</c> (SELECT sin la columna CLAVE, que aquí nunca se mapea).
/// </summary>
public sealed class clsIdentidadDbContext : DbContext
{
    public const string MensajeSoloLectura = "clsIdentidadDbContext es de solo lectura: dbsolucionesti no se modifica desde Cotifly.";

    public clsIdentidadDbContext(DbContextOptions<clsIdentidadDbContext> options, IOptions<clsOpcionesIdentidad> opciones)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        Esquema = opciones.Value.Esquema;
        Tabla = opciones.Value.Tabla;
    }

    /// <summary>Esquema configurado de la tabla de usuarios (parte de la clave de caché del modelo).</summary>
    public string Esquema { get; }

    /// <summary>Tabla configurada de usuarios (parte de la clave de caché del modelo).</summary>
    public string Tabla { get; }

    /// <summary>Usuarios corporativos (sin tracking).</summary>
    public IQueryable<clsUsuarioSapApp> UsuariosSapApp => Set<clsUsuarioSapApp>().AsNoTracking();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        throw new InvalidOperationException(MensajeSoloLectura);

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(MensajeSoloLectura);

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        optionsBuilder
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ReplaceService<IModelCacheKeyFactory, clsIdentidadModelCacheKeyFactory>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<clsUsuarioSapApp>(e =>
        {
            e.ToTable(Tabla, Esquema);
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("ID").ValueGeneratedNever();
            e.Property(u => u.NombreUsuario).HasColumnName("NOMBREUSUARIO");
            e.Property(u => u.DescripcionUsuario).HasColumnName("DESCRIPCIONUSUARIO");
            e.Property(u => u.Email).HasColumnName("EMAIL");
        });
    }
}

/// <summary>El modelo de <see cref="clsIdentidadDbContext"/> depende del esquema y la tabla configurados.</summary>
internal sealed class clsIdentidadModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is clsIdentidadDbContext identidad
            ? (context.GetType(), identidad.Esquema, identidad.Tabla, designTime)
            : (context.GetType(), designTime);
}
