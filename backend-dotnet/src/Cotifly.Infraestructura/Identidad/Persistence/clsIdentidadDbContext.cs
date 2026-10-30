using Cotifly.Infraestructura.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Identidad.Persistence;

/// <summary>
/// Contexto de SOLO LECTURA sobre la base de identidad del portal (ADR-008, ADR-009 §identidad compartida con Nexo, #172):
/// la misma que lee Nexo en cada ambiente (QA: <c>dbSolucionesTI_Prueba</c>; el nombre va solo en <c>ConnectionStrings:Identidad</c>): usuarios corporativos (<c>tblSAPUsuariosApp</c>), sus
/// roles (<c>tblSAPUsuarioRoles</c>) y el catálogo de roles de ClientePro (<c>tblSAPRoles</c>) para la verificación doble
/// del login (ADR-009). Sin tracking y <c>SaveChanges</c> lanza <see cref="InvalidOperationException"/>. La garantía real
/// de solo lectura son los permisos del login <c>cotifly_app</c> (SELECT sobre estas columnas; CLAVE nunca se mapea).
/// Las filas son tipos internos: fuera de Infraestructura se expone el puerto <c>IClsAlmacenIdentidadExterno</c>.
/// </summary>
/// <remarks>
/// Tipos reales, los mismos que Nexo verificó contra <c>sys.columns</c> de <c>dbSolucionesTI_Prueba</c> (2026-09-01,
/// <c>IdentidadPortalDbContext</c>): <c>tblSAPUsuariosApp.ID</c> es <c>numeric(18,0)</c> (se lee como <see cref="decimal"/> y el
/// almacén lo convierte a <see cref="long"/>), mientras que <c>tblSAPUsuarioRoles.IDUSUARIO</c> e <c>IDROLES</c> y
/// <c>tblSAPRoles.IDROLES</c> son <c>int</c>. <c>tblSAPUsuarioRoles</c> se mapea con la clave compuesta (<c>IDUSUARIO</c>,
/// <c>IDROLES</c>), igual que en Nexo: Cotifly no lee ninguna columna <c>ID</c> de esa tabla, así que funciona tenga o no tenga una
/// (#172). Los textos se mapean como VARCHAR sin Unicode para que los parámetros sean VARCHAR (sin conversión implícita que impida
/// usar índices); si la columna real fuera NVARCHAR, SQL Server convierte el parámetro y la lectura no cambia.
/// </remarks>
public sealed class clsIdentidadDbContext : DbContext
{
    public const string MensajeSoloLectura = "clsIdentidadDbContext es de solo lectura: la base de identidad del portal no se modifica desde Cotifly.";

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
            // Clave compuesta (como Nexo): la columna ID de tblSAPUsuarioRoles no se mapea ni se lee (#172).
            e.HasKey(r => new { r.IdUsuario, r.IdRoles });
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

/// <summary>Fila de <c>tblSAPUsuarioRoles</c> (<c>IDUSUARIO</c> → <c>tblSAPUsuariosApp.ID</c>, <c>IDROLES</c> → <c>tblSAPRoles</c>), sin su <c>ID</c>.</summary>
internal sealed class clsFilaUsuarioRolSap
{
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
