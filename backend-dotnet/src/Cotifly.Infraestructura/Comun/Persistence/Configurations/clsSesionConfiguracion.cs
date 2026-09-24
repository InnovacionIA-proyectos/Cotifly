using Cotifly.Dominio.Auditoria;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Sesiones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using static Cotifly.Infraestructura.Comun.Persistence.clsCotiflyDbContext;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary>
/// <c>dbo_cotifly.Sesiones</c> (script 0001 §8 + 0002 §1): PK compuesta (spaceId, threadId);
/// <c>estadoAgente</c> es el JSON de <c>clsEstadoConversacion</c>.
/// </summary>
internal sealed class clsSesionConfiguracion : IEntityTypeConfiguration<clsSesion>
{
    public void Configure(EntityTypeBuilder<clsSesion> builder)
    {
        builder.ToTable("Sesiones", t =>
        {
            t.HasCheckConstraint("ck_sesiones_estado", "estado IS NULL OR estado IN ('activa', 'archivada')");
            t.HasCheckConstraint("ck_sesiones_estadoAgente_json", "estadoAgente IS NULL OR ISJSON(estadoAgente) = 1");
        });
        builder.HasKey(s => new { s.SpaceId, s.ThreadId }).HasName("pk_sesiones");
        builder.Property(s => s.SpaceId).Columna("spaceId", "nvarchar(128)").HasMaxLength(clsSesion.LargoMaximoId);
        builder.Property(s => s.ThreadId).Columna("threadId", "nvarchar(128)").HasMaxLength(clsSesion.LargoMaximoId);
        builder.Property(s => s.Estado).Columna("estado", TipoTexto)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoSesion>());
        builder.Property(s => s.UltimaActividad).Fecha("ultimaActividad");
        builder.Property(s => s.EstadoAgente).Columna("estadoAgente", TipoTexto)
            .HasConversion((ValueConverter)new clsConvertidorEstadoConversacion());
        builder.Property(s => s.UsuarioId).Columna("usuarioId", "uniqueidentifier");

        // Script 0002: ultimaActividad INCLUDE (estado), para el barrido de sesiones inactivas.
        builder.HasIndex(s => s.UltimaActividad).IncludeProperties(s => s.Estado).HasDatabaseName("ix_sesiones_ultimaActividad");
        builder.HasIndex(s => s.UsuarioId).HasDatabaseName("ix_sesiones_usuarioId");
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(s => s.UsuarioId)
            .HasConstraintName("fk_sesiones_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.EventosConversacion</c> (script 0001 §9): FK compuesta a <c>Sesiones</c>.</summary>
internal sealed class clsEventoConversacionConfiguracion : IEntityTypeConfiguration<clsEventoConversacion>
{
    public void Configure(EntityTypeBuilder<clsEventoConversacion> builder)
    {
        builder.ToTable("EventosConversacion", t => t.HasCheckConstraint(
            "ck_eventosConversacion_herramientas_json", "herramientas IS NULL OR ISJSON(herramientas) = 1"));
        builder.HasKey(e => e.Id).HasName("pk_eventosConversacion");
        builder.Property(e => e.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(e => e.SpaceId).Columna("spaceId", "nvarchar(128)").HasMaxLength(clsSesion.LargoMaximoId);
        builder.Property(e => e.ThreadId).Columna("threadId", "nvarchar(128)").HasMaxLength(clsSesion.LargoMaximoId);
        builder.Property(e => e.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(e => e.Rol).Columna("rol", TipoTexto).IsRequired();
        builder.Property(e => e.Contenido).Columna("contenido", TipoTexto);
        builder.Property(e => e.Herramientas).Columna("herramientas", TipoTexto);
        builder.Property(e => e.CreatedAt).Fecha("createdAt");

        builder.HasIndex(e => new { e.SpaceId, e.ThreadId, e.CreatedAt }).HasDatabaseName("ix_eventosConversacion_sesion_createdAt");
        builder.HasOne<clsSesion>().WithMany().HasForeignKey(e => new { e.SpaceId, e.ThreadId })
            .HasConstraintName("fk_eventosConversacion_sesion").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(e => e.UsuarioId)
            .HasConstraintName("fk_eventosConversacion_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.EventosHerramienta</c> (script 0001 §10), sin FKs.</summary>
internal sealed class clsEventoHerramientaConfiguracion : IEntityTypeConfiguration<clsEventoHerramienta>
{
    public void Configure(EntityTypeBuilder<clsEventoHerramienta> builder)
    {
        builder.ToTable("EventosHerramienta", t =>
        {
            t.HasCheckConstraint("ck_eventosHerramienta_input_json", "input IS NULL OR ISJSON(input) = 1");
            t.HasCheckConstraint("ck_eventosHerramienta_outputResumen_json", "outputResumen IS NULL OR ISJSON(outputResumen) = 1");
        });
        builder.HasKey(e => e.Id).HasName("pk_eventosHerramienta");
        builder.Property(e => e.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(e => e.Herramienta).Columna("herramienta", TipoTexto).IsRequired();
        builder.Property(e => e.Input).Columna("input", TipoTexto);
        builder.Property(e => e.OutputResumen).Columna("outputResumen", TipoTexto);
        builder.Property(e => e.Exito).Columna("exito", "bit");
        builder.Property(e => e.DuracionMs).Columna("duracionMs", "int");
        builder.Property(e => e.ThreadId).Columna("threadId", TipoTexto);
        builder.Property(e => e.CreatedAt).Fecha("createdAt");
    }
}
