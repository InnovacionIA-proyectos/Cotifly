using Cotifly.Dominio.Identidad;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Cotifly.Infraestructura.Comun.Persistence.clsCotiflyDbContext;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary><c>dbo_cotifly.UsuariosWeb</c> (script 0001 §3): índice único filtrado sobre <c>nombreUsuario</c>.</summary>
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

        builder.HasIndex(u => u.NombreUsuario).IsUnique()
            .HasFilter("[nombreUsuario] IS NOT NULL")
            .HasDatabaseName("uq_usuariosWeb_nombreUsuario");
    }
}

/// <summary><c>dbo_cotifly.RolesMapeo</c> (script 0002 §2).</summary>
internal sealed class clsRolMapeoConfiguracion : IEntityTypeConfiguration<clsRolMapeo>
{
    public void Configure(EntityTypeBuilder<clsRolMapeo> builder)
    {
        builder.ToTable("RolesMapeo", t => t.HasCheckConstraint(
            "ck_rolesMapeo_rolInterno", "rolInterno IN ('comercial', 'gerente_comercial')"));
        builder.HasKey(r => r.Id).HasName("pk_rolesMapeo");
        builder.Property(r => r.Id).Columna("id", "int").UseIdentityColumn();
        builder.Property(r => r.RolExterno).Columna("rolExterno", "nvarchar(100)").HasMaxLength(clsRolMapeo.LargoMaximoRolExterno).IsRequired();
        builder.Property(r => r.RolInterno).Columna("rolInterno", "nvarchar(30)").HasMaxLength(30)
            .HasConversion(new clsConvertidorCodigoEnum<RolInterno>());
        builder.Property(r => r.Activo).Columna("activo", "bit");
        builder.Property(r => r.UpdatedAt).Fecha("updatedAt");
        builder.HasIndex(r => r.RolExterno).IsUnique().HasDatabaseName("uq_rolesMapeo_rolExterno");
    }
}

/// <summary><c>dbo_cotifly.TokensRefresco</c> (script 0002 §3).</summary>
internal sealed class clsTokenRefrescoConfiguracion : IEntityTypeConfiguration<clsTokenRefresco>
{
    public void Configure(EntityTypeBuilder<clsTokenRefresco> builder)
    {
        builder.ToTable("TokensRefresco", t => t.HasCheckConstraint("ck_tokensRefresco_vigencia", "expiraAt > createdAt"));
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
