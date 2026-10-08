using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Cotifly.Infraestructura.Comun.Persistence.clsCotiflyDbContext;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary><c>dbo_cotifly.Correos</c> (script 0001 §6) con sus destinatarios.</summary>
internal sealed class clsCorreoConfiguracion : IEntityTypeConfiguration<clsCorreo>
{
    public void Configure(EntityTypeBuilder<clsCorreo> builder)
    {
        builder.ToTable("Correos");
        builder.HasKey(c => c.Id).HasName("pk_correos");
        builder.Property(c => c.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(c => c.CotizacionId).Columna("cotizacionId", "uniqueidentifier");
        builder.Property(c => c.Asunto).Columna("asunto", TipoTexto);
        builder.Property(c => c.MessageId).Columna("messageId", TipoTexto);
        builder.Property(c => c.ConfirmadoPor).Columna("confirmadoPor", "uniqueidentifier");
        builder.Property(c => c.Estado).Columna("estado", TipoTexto)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoCorreo>());
        builder.Property(c => c.Error).Columna("error", TipoTexto);
        builder.Property(c => c.CreatedAt).Fecha("createdAt");
        builder.Ignore(c => c.Para);
        builder.Ignore(c => c.Cc);

        builder.HasMany(c => c.Destinatarios).WithOne().HasForeignKey(d => d.CorreoId)
            .HasConstraintName("fk_correoDestinatarios_correoId").OnDelete(DeleteBehavior.NoAction);
        builder.Navigation(c => c.Destinatarios).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(c => new { c.CotizacionId, c.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_correos_cotizacionId_createdAt");
        builder.HasOne<clsCotizacionSolicitud>().WithMany().HasForeignKey(c => c.CotizacionId)
            .HasConstraintName("fk_correos_cotizacionId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(c => c.ConfirmadoPor)
            .HasConstraintName("fk_correos_confirmadoPor").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.CorreoDestinatarios</c> (script 0001 §7).</summary>
internal sealed class clsCorreoDestinatarioConfiguracion : IEntityTypeConfiguration<clsCorreoDestinatario>
{
    public void Configure(EntityTypeBuilder<clsCorreoDestinatario> builder)
    {
        builder.ToTable("CorreoDestinatarios", t => t.HasCheckConstraint("ck_correoDestinatarios_tipo", "tipo IN ('para', 'cc')"));
        builder.HasKey(d => d.Id).HasName("pk_correoDestinatarios");
        builder.Property(d => d.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(d => d.CorreoId).Columna("correoId", "uniqueidentifier");
        builder.Property(d => d.Email).Columna("email", "nvarchar(320)").HasMaxLength(clsCorreoDestinatario.LargoMaximoEmail).IsRequired();
        builder.Property(d => d.Tipo).Columna("tipo", "nvarchar(10)").HasMaxLength(10)
            .HasConversion(new clsConvertidorCodigoEnum<TipoDestinatario>());
        builder.HasIndex(d => d.CorreoId).HasDatabaseName("ix_correoDestinatarios_correoId");
    }
}
