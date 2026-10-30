using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary><c>dbo_cotifly.CapturasIdempotentes</c> (<c>db/scripts/06_idempotencia_captura.sql</c>, #191).</summary>
internal sealed class clsCapturaIdempotenteConfiguracion : IEntityTypeConfiguration<clsCapturaIdempotenteFila>
{
    public void Configure(EntityTypeBuilder<clsCapturaIdempotenteFila> builder)
    {
        builder.ToTable("CapturasIdempotentes", t =>
        {
            t.HasCheckConstraint("ck_capturasIdempotentes_estado", "estado IN (N'en_curso', N'terminada', N'libre')");
            t.HasCheckConstraint("ck_capturasIdempotentes_clave", "LEN(clave) BETWEEN 8 AND 64");
            t.HasCheckConstraint("ck_capturasIdempotentes_operacion", "LEN(operacion) BETWEEN 1 AND 40");
            t.HasCheckConstraint("ck_capturasIdempotentes_enCurso",
                "estado <> N'en_curso' OR (tomadaPor IS NOT NULL AND enCursoHasta IS NOT NULL)");
            t.HasCheckConstraint("ck_capturasIdempotentes_terminada", "estado <> N'terminada' OR resultado IS NOT NULL");
            t.HasCheckConstraint("ck_capturasIdempotentes_resultado_json", "resultado IS NULL OR ISJSON(resultado) = 1");
        });
        builder.HasKey(c => c.Id).HasName("pk_capturasIdempotentes");
        builder.Property(c => c.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(c => c.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(c => c.Operacion).Columna("operacion", "nvarchar(40)").HasMaxLength(40).IsRequired();
        builder.Property(c => c.Clave).Columna("clave", "nvarchar(64)").HasMaxLength(64).IsRequired()
            .UseCollation(clsCotiflyDbContext.CollationBinaria);
        builder.Property(c => c.Huella).Columna("huella", "char(64)").HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired()
            .UseCollation(clsCotiflyDbContext.CollationBinaria);
        builder.Property(c => c.Estado).Columna("estado", "nvarchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(c => c.TomadaPor).Columna("tomadaPor", "uniqueidentifier");
        builder.Property(c => c.EnCursoHasta).Fecha("enCursoHasta");
        builder.Property(c => c.Resultado).Columna("resultado", "nvarchar(max)");
        builder.Property(c => c.CotizacionId).Columna("cotizacionId", "uniqueidentifier");
        builder.Property(c => c.Folio).Columna("folio", "nvarchar(50)").HasMaxLength(50);
        builder.Property(c => c.SolicitudNexoId).Columna("solicitudNexoId", "nvarchar(64)").HasMaxLength(64);
        builder.Property(c => c.ScorePrioridad).Columna("scorePrioridad", "decimal(9,4)").HasPrecision(9, 4);
        builder.Property(c => c.CreatedAt).Fecha("createdAt");
        builder.Property(c => c.UpdatedAt).Fecha("updatedAt");
        builder.Property(c => c.ExpiraAt).Fecha("expiraAt");

        builder.HasIndex(c => new { c.UsuarioId, c.Operacion, c.Clave }).IsUnique()
            .HasDatabaseName("uq_capturasIdempotentes_usuarioId_operacion_clave");
        builder.HasIndex(c => c.ExpiraAt).HasDatabaseName("ix_capturasIdempotentes_expiraAt");
        builder.HasIndex(c => c.Folio).HasDatabaseName("ix_capturasIdempotentes_folio")
            .IncludeProperties(c => new { c.SolicitudNexoId, c.ScorePrioridad, c.ExpiraAt })
            .HasFilter("[folio] IS NOT NULL");
    }
}
