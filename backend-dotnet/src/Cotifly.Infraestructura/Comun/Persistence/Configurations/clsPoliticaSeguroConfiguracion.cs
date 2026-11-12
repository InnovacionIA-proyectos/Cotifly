using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary>
/// <c>dbo_cotifly.PoliticaSeguroTramos</c> (<c>db/scripts/21_politica_seguros.sql</c>). Sin el 21,
/// <c>/health/ready</c> dice "falta 21". Las columnas de texto llevan <c>COLLATE Latin1_General_100_CI_AS_SC_UTF8</c> en el script (la del
/// modelo).
/// </summary>
internal sealed class clsPoliticaSeguroTramoConfiguracion : IEntityTypeConfiguration<clsPoliticaSeguroTramoFila>
{
    public void Configure(EntityTypeBuilder<clsPoliticaSeguroTramoFila> builder)
    {
        builder.ToTable("PoliticaSeguroTramos", t => t.HasCheckConstraint("ck_politicaSeguroTramos_valores",
            "valorDesde >= 0 AND (valorHasta IS NULL OR valorHasta > valorDesde)"));
        builder.HasKey(t => t.Id).HasName("pk_politicaSeguroTramos");
        builder.Property(t => t.Id).Columna("id", "uniqueidentifier").HasDefaultValueSql("NEWID()");
        builder.Property(t => t.Nombre).Columna("nombre", "nvarchar(60)").HasMaxLength(60).IsRequired();
        builder.Property(t => t.ValorDesde).Columna("valorDesde", "decimal(18,2)");
        builder.Property(t => t.ValorHasta).Columna("valorHasta", "decimal(18,2)");
        builder.Property(t => t.Activo).Columna("isActive", "bit").HasDefaultValue(true);
        builder.Property(t => t.UpdatedAt).Fecha("updatedAt").HasDefaultValueSql("SYSDATETIMEOFFSET()");
        builder.HasIndex(t => t.ValorDesde).IsUnique().HasDatabaseName("uq_politicaSeguroTramos_valorDesde_activo").HasFilter("[isActive] = 1");
    }
}

/// <summary><c>dbo_cotifly.PoliticaSeguroMedidas</c> (script 21): el catálogo de medidas de la política de seguros.</summary>
internal sealed class clsPoliticaSeguroMedidaConfiguracion : IEntityTypeConfiguration<clsPoliticaSeguroMedidaFila>
{
    public void Configure(EntityTypeBuilder<clsPoliticaSeguroMedidaFila> builder)
    {
        builder.ToTable("PoliticaSeguroMedidas");
        builder.HasKey(m => m.Id).HasName("pk_politicaSeguroMedidas");
        builder.Property(m => m.Id).Columna("id", "uniqueidentifier").HasDefaultValueSql("NEWID()");
        builder.Property(m => m.Codigo).Columna("codigo", "nvarchar(40)").HasMaxLength(40).IsRequired();
        builder.Property(m => m.Nombre).Columna("nombre", "nvarchar(100)").HasMaxLength(100).IsRequired();
        builder.Property(m => m.EsCobrable).Columna("esCobrable", "bit").HasDefaultValue(false);
        builder.Property(m => m.Activa).Columna("isActive", "bit").HasDefaultValue(true);
        builder.Property(m => m.UpdatedAt).Fecha("updatedAt").HasDefaultValueSql("SYSDATETIMEOFFSET()");
        builder.HasIndex(m => m.Codigo).IsUnique().HasDatabaseName("uq_politicaSeguroMedidas_codigo");
    }
}

/// <summary><c>dbo_cotifly.PoliticaSeguroTramoMedidas</c> (script 21): qué medida aplica en cada tramo y con qué nivel.</summary>
internal sealed class clsPoliticaSeguroTramoMedidaConfiguracion : IEntityTypeConfiguration<clsPoliticaSeguroTramoMedidaFila>
{
    public void Configure(EntityTypeBuilder<clsPoliticaSeguroTramoMedidaFila> builder)
    {
        builder.ToTable("PoliticaSeguroTramoMedidas", t => t.HasCheckConstraint("ck_politicaSeguroTramoMedidas_nivel",
            "nivel IN ('recomendada', 'obligatoria')"));
        builder.HasKey(r => r.Id).HasName("pk_politicaSeguroTramoMedidas");
        builder.Property(r => r.Id).Columna("id", "uniqueidentifier").HasDefaultValueSql("NEWID()");
        builder.Property(r => r.TramoId).Columna("tramoId", "uniqueidentifier");
        builder.Property(r => r.MedidaId).Columna("medidaId", "uniqueidentifier");
        builder.Property(r => r.Nivel).Columna("nivel", "nvarchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(r => r.UpdatedAt).Fecha("updatedAt").HasDefaultValueSql("SYSDATETIMEOFFSET()");
        builder.HasIndex(r => new { r.TramoId, r.MedidaId }).IsUnique().HasDatabaseName("uq_politicaSeguroTramoMedidas_tramoId_medidaId");
        builder.HasIndex(r => r.MedidaId).HasDatabaseName("ix_politicaSeguroTramoMedidas_medidaId");
        builder.HasOne<clsPoliticaSeguroTramoFila>().WithMany().HasForeignKey(r => r.TramoId)
            .HasConstraintName("fk_politicaSeguroTramoMedidas_tramoId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsPoliticaSeguroMedidaFila>().WithMany().HasForeignKey(r => r.MedidaId)
            .HasConstraintName("fk_politicaSeguroTramoMedidas_medidaId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.PoliticaSeguroMedidaConexos</c> (script 21): los códigos de conexo de Nexo que cumplen cada medida.</summary>
internal sealed class clsPoliticaSeguroMedidaConexoConfiguracion : IEntityTypeConfiguration<clsPoliticaSeguroMedidaConexoFila>
{
    public void Configure(EntityTypeBuilder<clsPoliticaSeguroMedidaConexoFila> builder)
    {
        builder.ToTable("PoliticaSeguroMedidaConexos");
        builder.HasKey(c => c.Id).HasName("pk_politicaSeguroMedidaConexos");
        builder.Property(c => c.Id).Columna("id", "uniqueidentifier").HasDefaultValueSql("NEWID()");
        builder.Property(c => c.MedidaId).Columna("medidaId", "uniqueidentifier");
        builder.Property(c => c.CodigoServicio).Columna("codigoServicio", "nvarchar(40)").HasMaxLength(40).IsRequired();
        builder.Property(c => c.UpdatedAt).Fecha("updatedAt").HasDefaultValueSql("SYSDATETIMEOFFSET()");
        builder.HasIndex(c => new { c.MedidaId, c.CodigoServicio }).IsUnique()
            .HasDatabaseName("uq_politicaSeguroMedidaConexos_medidaId_codigoServicio");
        builder.HasOne<clsPoliticaSeguroMedidaFila>().WithMany().HasForeignKey(c => c.MedidaId)
            .HasConstraintName("fk_politicaSeguroMedidaConexos_medidaId").OnDelete(DeleteBehavior.NoAction);
    }
}
