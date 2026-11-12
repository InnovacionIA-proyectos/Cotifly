using Cotifly.Dominio.Identidad;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Cotifly.Infraestructura.Comun.Persistence.clsCotiflyDbContext;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary>
/// <c>dbo_cotifly.UsuariosWeb</c> (script 0001 §3): índice único filtrado sobre <c>nombreUsuario</c>; <c>idUsuarioExterno</c>
/// y su índice único filtrado vienen del script 0002 §1bis.
/// </summary>
internal sealed class clsUsuarioWebConfiguracion : IEntityTypeConfiguration<clsUsuarioWeb>
{
    public void Configure(EntityTypeBuilder<clsUsuarioWeb> builder)
    {
        builder.ToTable("UsuariosWeb");
        builder.HasKey(u => u.Id).HasName("pk_usuariosWeb");
        builder.Property(u => u.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(u => u.NombreUsuario).Columna("nombreUsuario", "nvarchar(255)").HasMaxLength(clsUsuarioWeb.LargoMaximoNombreUsuario);
        builder.Property(u => u.NombreCompleto).Columna("nombreCompleto", TipoTexto);
        builder.Property(u => u.Email).Columna("email", TipoTexto);
        builder.Property(u => u.Telefono).Columna("telefono", TipoTexto);
        builder.Property(u => u.Regional).Columna("regional", TipoTexto);
        builder.Property(u => u.CreatedAt).Fecha("createdAt");
        builder.Property(u => u.UltimaSesion).Fecha("ultimaSesion");
        builder.Property(u => u.IdUsuarioExterno).Columna("idUsuarioExterno", "bigint");

        builder.HasIndex(u => u.NombreUsuario).IsUnique()
            .HasFilter("[nombreUsuario] IS NOT NULL")
            .HasDatabaseName("uq_usuariosWeb_nombreUsuario");
        builder.HasIndex(u => u.IdUsuarioExterno).IsUnique()
            .HasFilter("[idUsuarioExterno] IS NOT NULL")
            .HasDatabaseName("uq_usuariosWeb_idUsuarioExterno");
    }
}

/// <summary><c>dbo_cotifly.Roles</c> (script 0002 §2): código interno único y rol externo único cuando existe.</summary>
internal sealed class clsRolConfiguracion : IEntityTypeConfiguration<clsRol>
{
    public void Configure(EntityTypeBuilder<clsRol> builder)
    {
        builder.ToTable("Roles", t => t.HasCheckConstraint(
            "ck_roles_codigo", "codigo IN ('comercial', 'gerente_comercial', 'director')"));
        builder.HasKey(r => r.Id).HasName("pk_roles");
        builder.Property(r => r.Id).Columna("id", "int").UseIdentityColumn();
        builder.Property(r => r.Codigo).Columna("codigo", "nvarchar(30)").HasMaxLength(30)
            .HasConversion(new clsConvertidorCodigoEnum<RolInterno>());
        builder.Property(r => r.Descripcion).Columna("descripcion", "nvarchar(200)").HasMaxLength(clsRol.LargoMaximoDescripcion);
        builder.Property(r => r.IdRolExterno).Columna("idRolExterno", "int");
        builder.Property(r => r.Activo).Columna("activo", "bit");
        // Rol sensible que no se auto-otorga en el login (script 23).
        builder.Property(r => r.RequiereAltaManual).Columna("requiereAltaManual", "bit");
        builder.Property(r => r.UpdatedAt).Fecha("updatedAt");
        builder.HasIndex(r => r.Codigo).IsUnique().HasDatabaseName("uq_roles_codigo");
        builder.HasIndex(r => r.IdRolExterno).IsUnique()
            .HasFilter("[idRolExterno] IS NOT NULL")
            .HasDatabaseName("uq_roles_idRolExterno");
    }
}

/// <summary>
/// <c>dbo_cotifly.UsuariosRoles</c> (script 0002 §2bis): un rol una vez por usuario. Sin
/// <c>uq_usuariosRoles_usuarioId_activo</c> (la retira el script 16), un usuario puede tener varias asignaciones activas (una por rol).
/// </summary>
internal sealed class clsUsuarioRolConfiguracion : IEntityTypeConfiguration<clsUsuarioRol>
{
    public void Configure(EntityTypeBuilder<clsUsuarioRol> builder)
    {
        builder.ToTable("UsuariosRoles");
        builder.HasKey(r => r.Id).HasName("pk_usuariosRoles");
        builder.Property(r => r.Id).Columna("id", "int").UseIdentityColumn();
        builder.Property(r => r.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(r => r.RolId).Columna("rolId", "int");
        builder.Property(r => r.AsignadoAt).Fecha("asignadoAt");
        builder.Property(r => r.Activo).Columna("activo", "bit");
        builder.Property(r => r.RevisadoPor).Columna("revisadoPor", "uniqueidentifier");
        builder.Property(r => r.RevisadoAt).Fecha("revisadoAt");

        builder.HasIndex(r => new { r.UsuarioId, r.RolId }).IsUnique().HasDatabaseName("uq_usuariosRoles_usuarioId_rolId");
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(r => r.UsuarioId)
            .HasConstraintName("fk_usuariosRoles_usuarioId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsRol>().WithMany().HasForeignKey(r => r.RolId)
            .HasConstraintName("fk_usuariosRoles_rolId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(r => r.RevisadoPor)
            .HasConstraintName("fk_usuariosRoles_revisadoPor").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.TokensRefresco</c> (script 0002 §3; <c>rolSesion</c> y su CHECK, script 16).</summary>
internal sealed class clsTokenRefrescoConfiguracion : IEntityTypeConfiguration<clsTokenRefresco>
{
    public void Configure(EntityTypeBuilder<clsTokenRefresco> builder)
    {
        builder.ToTable("TokensRefresco", t =>
        {
            t.HasCheckConstraint("ck_tokensRefresco_vigencia", "expiraAt > createdAt");
            t.HasCheckConstraint(
                "ck_tokensRefresco_rolSesion", "rolSesion IS NULL OR rolSesion IN ('comercial', 'gerente_comercial', 'director')");
        });
        builder.HasKey(t => t.Id).HasName("pk_tokensRefresco");
        builder.Property(t => t.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(t => t.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(t => t.TokenHash).Columna("tokenHash", "char(64)")
            .HasMaxLength(clsTokenRefresco.LargoHash).IsFixedLength().IsUnicode(false).IsRequired()
            .UseCollation(CollationBinaria);
        builder.Property(t => t.FamiliaId).Columna("familiaId", "uniqueidentifier");
        builder.Property(t => t.CreatedAt).Fecha("createdAt");
        builder.Property(t => t.ExpiraAt).Fecha("expiraAt");
        builder.Property(t => t.RevocadoAt).Fecha("revocadoAt");
        builder.Property(t => t.ReemplazadoPorId).Columna("reemplazadoPorId", "uniqueidentifier");
        builder.Property(t => t.RolSesion).Columna("rolSesion", "nvarchar(30)").HasMaxLength(30)
            .HasConversion(new clsConvertidorCodigoEnum<RolInterno>());

        builder.HasIndex(t => t.TokenHash).IsUnique().HasDatabaseName("uq_tokensRefresco_tokenHash");
        builder.HasIndex(t => t.UsuarioId).HasDatabaseName("ix_tokensRefresco_usuarioId");
        builder.HasIndex(t => t.FamiliaId).HasDatabaseName("ix_tokensRefresco_familiaId");
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(t => t.UsuarioId)
            .HasConstraintName("fk_tokensRefresco_usuarioId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsTokenRefresco>().WithMany().HasForeignKey(t => t.ReemplazadoPorId)
            .HasConstraintName("fk_tokensRefresco_reemplazadoPorId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.TokensRevocados</c> (script 0002 §4).</summary>
internal sealed class clsTokenRevocadoConfiguracion : IEntityTypeConfiguration<clsTokenRevocado>
{
    public void Configure(EntityTypeBuilder<clsTokenRevocado> builder)
    {
        builder.ToTable("TokensRevocados");
        builder.HasKey(t => t.Jti).HasName("pk_tokensRevocados");
        builder.Property(t => t.Jti).Columna("jti", "nvarchar(64)").HasMaxLength(clsTokenRevocado.LargoMaximoJti).ValueGeneratedNever();
        builder.Property(t => t.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(t => t.ExpiraAt).Fecha("expiraAt");
        builder.Property(t => t.CreatedAt).Fecha("createdAt");

        builder.HasIndex(t => t.ExpiraAt).HasDatabaseName("ix_tokensRevocados_expiraAt");
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(t => t.UsuarioId)
            .HasConstraintName("fk_tokensRevocados_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}
