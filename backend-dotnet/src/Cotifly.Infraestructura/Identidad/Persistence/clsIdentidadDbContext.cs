using Cotifly.Infraestructura.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Identidad.Persistence;

/// <summary>
/// Contexto de SOLO LECTURA sobre <c>dbsolucionesti</c> (ADR-008): usuarios corporativos (<c>tblSAPUsuariosApp</c>), sus
/// roles (<c>tblSAPUsuarioRoles</c>) y el catálogo de roles de ClientePro (<c>tblSAPRoles</c>) para la verificación doble
/// del login (ADR-009). Sin tracking y <c>SaveChanges</c> lanza <see cref="InvalidOperationException"/>. La garantía real
/// de solo lectura son los permisos del login <c>cotifly_app</c> (SELECT sobre estas columnas; CLAVE nunca se mapea).
/// Las filas son tipos internos: fuera de Infraestructura se expone el puerto <c>IClsAlmacenIdentidadExterno</c>.
/// </summary>
/// <remarks>
/// Tipos reales: <c>tblSAPUsuariosApp.ID</c> es NUMERIC (se lee como <see cref="decimal"/>), mientras que
/// <c>tblSAPUsuarioRoles.IDUSUARIO</c> e <c>IDROLES</c> y <c>tblSAPRoles.IDROLES</c> son INT. Los textos son VARCHAR y se
/// mapean sin Unicode para que los parámetros sean VARCHAR (sin conversión implícita que impida usar índices).
/// </remarks>
public sealed class clsIdentidadDbContext : DbContext
{
    public const string MensajeSoloLectura = "clsIdentidadDbContext es de solo lectura: dbsolucionesti no se modifica desde Cotifly.";

    public clsIdentidadDbContext(DbContextOptions<clsIdentidadDbContext> options, IOptions<clsOpcionesIdentidad> opciones)
        : base(options)
    {
        ArgumentNullException.ThrowIfNull(opciones);
        Esquema = opciones.Value.Esquema;
        TablaUsuarios = opciones.Value.TablaUsuarios;
        TablaUsuarioRoles = opciones.Value.TablaUsuarioRoles;
        TablaRoles = opciones.Value.TablaRoles;
    }

    /// <summary>Esquema configurado (parte de la clave de caché del modelo, igual que las tablas).</summary>
    public string Esquema { get; }

    public string TablaUsuarios { get; }

    public string TablaUsuarioRoles { get; }

    public string TablaRoles { get; }

    /// <summary>Usuarios corporativos (sin tracking).</summary>
    internal IQueryable<clsFilaUsuarioSap> Usuarios => Set<clsFilaUsuarioSap>().AsNoTracking();

    /// <summary>Roles de ClientePro asignados a cada usuario (sin tracking).</summary>
    internal IQueryable<clsFilaUsuarioRolSap> UsuarioRoles => Set<clsFilaUsuarioRolSap>().AsNoTracking();

    /// <summary>Catálogo de roles de ClientePro (sin tracking).</summary>
    internal IQueryable<clsFilaRolSap> Roles => Set<clsFilaRolSap>().AsNoTracking();

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

        modelBuilder.Entity<clsFilaUsuarioSap>(e =>
        {
            e.ToTable(TablaUsuarios, Esquema);
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("ID").HasColumnType("numeric(18,0)").ValueGeneratedNever();
            e.Property(u => u.NombreUsuario).HasColumnName("NOMBREUSUARIO").HasColumnType("varchar(50)").IsUnicode(false).HasMaxLength(50);
            e.Property(u => u.DescripcionUsuario).HasColumnName("DESCRIPCIONUSUARIO").HasColumnType("varchar(250)").IsUnicode(false).HasMaxLength(250);
            e.Property(u => u.Email).HasColumnName("EMAIL").HasColumnType("varchar(100)").IsUnicode(false).HasMaxLength(100);
        });

        modelBuilder.Entity<clsFilaUsuarioRolSap>(e =>
        {
            e.ToTable(TablaUsuarioRoles, Esquema);
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("ID").HasColumnType("int").ValueGeneratedNever();
            e.Property(r => r.IdUsuario).HasColumnName("IDUSUARIO").HasColumnType("int");
            e.Property(r => r.IdRoles).HasColumnName("IDROLES").HasColumnType("int");
        });

        modelBuilder.Entity<clsFilaRolSap>(e =>
        {
            e.ToTable(TablaRoles, Esquema);
            e.HasKey(r => r.IdRoles);
            e.Property(r => r.IdRoles).HasColumnName("IDROLES").HasColumnType("int").ValueGeneratedNever();
            e.Property(r => r.Descripcion).HasColumnName("DESCRIPCION").HasColumnType("varchar(50)").IsUnicode(false).HasMaxLength(50);
        });
    }
}

/// <summary>Fila de <c>tblSAPUsuariosApp</c> sin <c>CLAVE</c>. <c>ID</c> es NUMERIC.</summary>
internal sealed class clsFilaUsuarioSap
{
    public decimal Id { get; set; }
    public string? NombreUsuario { get; set; }
    public string? DescripcionUsuario { get; set; }
    public string? Email { get; set; }
}

/// <summary>Fila de <c>tblSAPUsuarioRoles</c> (<c>IDUSUARIO</c> → <c>tblSAPUsuariosApp.ID</c>, <c>IDROLES</c> → <c>tblSAPRoles</c>).</summary>
internal sealed class clsFilaUsuarioRolSap
{
    public int Id { get; set; }
    public int IdUsuario { get; set; }
    public int IdRoles { get; set; }
}

/// <summary>Fila de <c>tblSAPRoles</c>.</summary>
internal sealed class clsFilaRolSap
{
    public int IdRoles { get; set; }
    public string? Descripcion { get; set; }
}

/// <summary>El modelo de <see cref="clsIdentidadDbContext"/> depende del esquema y las tablas configuradas.</summary>
internal sealed class clsIdentidadModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is clsIdentidadDbContext identidad
            ? (context.GetType(), identidad.Esquema, identidad.TablaUsuarios, identidad.TablaUsuarioRoles, identidad.TablaRoles, designTime)
            : (context.GetType(), designTime);
}
