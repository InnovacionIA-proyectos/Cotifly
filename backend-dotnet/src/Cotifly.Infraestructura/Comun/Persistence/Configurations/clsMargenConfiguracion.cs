using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Cotifly.Infraestructura.Comun.Persistence.clsCotiflyDbContext;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary><c>dbo_cotifly.MargenesPolitica</c> (script 0001 §1).</summary>
internal sealed class clsMargenPoliticaConfiguracion : IEntityTypeConfiguration<clsMargenPolitica>
{
    public void Configure(EntityTypeBuilder<clsMargenPolitica> builder)
    {
        builder.ToTable("MargenesPolitica");
        builder.HasKey(m => m.Id).HasName("pk_margenesPolitica");
        builder.Property(m => m.Id).Columna("id", "int").UseIdentityColumn();
        builder.Property(m => m.LineaNegocio).Columna("lineaNegocio", "nvarchar(40)").HasMaxLength(clsMargenPolitica.LargoMaximoLinea).IsRequired();
        builder.Property(m => m.MapeoLineasNexo).Columna("mapeoLineasNexo", TipoTexto);
        builder.Property(m => m.Margen).Columna("margen", "decimal(9,4)");
        builder.Property(m => m.MetodoCalculo).Columna("metodoCalculo", TipoTexto).IsRequired();
        builder.Property(m => m.Activa).Columna("isActive", "bit");
        builder.Property(m => m.UpdatedAt).Fecha("updatedAt");
        builder.HasIndex(m => m.LineaNegocio).IsUnique().HasDatabaseName("uq_margenesPolitica_lineaNegocio");
    }
}

/// <summary>
/// <c>dbo_cotifly.SapMapeoTrqType</c> (script 0001 §2), PK compuesta. HU-042.4 (#247, <c>db/scripts/18_sap_mapeo_fk_margenes_politica.sql</c>):
/// <c>lineaNegocioId</c> con su FK a <c>MargenesPolitica</c> (sin el 18, <c>/health/ready</c> dice "falta 18"); <c>lineaNegocio</c> (texto)
/// sigue mapeada mientras exista en la tabla.
/// </summary>
internal sealed class clsSapMapeoTrqTypeConfiguracion : IEntityTypeConfiguration<clsSapMapeoTrqType>
{
    public void Configure(EntityTypeBuilder<clsSapMapeoTrqType> builder)
    {
        builder.ToTable("SapMapeoTrqType");
        builder.HasKey(m => new { m.TrqType, m.SalesOrg }).HasName("pk_sapMapeoTrqType");
        builder.Property(m => m.TrqType).Columna("trqType", "nvarchar(8)").HasMaxLength(8);
        builder.Property(m => m.SalesOrg).Columna("salesOrg", "nvarchar(12)").HasMaxLength(12);
        builder.Property(m => m.LineaNegocio).Columna("lineaNegocio", "nvarchar(40)").HasMaxLength(clsMargenPolitica.LargoMaximoLinea).IsRequired();
        builder.Property(m => m.LineaNegocioId).Columna("lineaNegocioId", "int");
        builder.Property(m => m.Activo).Columna("activo", "bit");
        builder.Ignore(m => m.EsComodin);
        builder.HasOne<clsMargenPolitica>().WithMany().HasForeignKey(m => m.LineaNegocioId)
            .HasConstraintName("fk_sapMapeoTrqType_lineaNegocioId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.MargenAjustes</c> (script 0001 §5; 0009: la ruta ajustada, HU-021).</summary>
internal sealed class clsMargenAjusteConfiguracion : IEntityTypeConfiguration<clsMargenAjuste>
{
    public void Configure(EntityTypeBuilder<clsMargenAjuste> builder)
    {
        builder.ToTable("MargenAjustes");
        builder.HasKey(a => a.Id).HasName("pk_margenAjustes");
        builder.Property(a => a.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(a => a.CotizacionId).Columna("cotizacionId", "uniqueidentifier");
        builder.Property(a => a.LineaSolicitudId).Columna("lineaSolicitudId", "uniqueidentifier");
        builder.Property(a => a.Folio).Columna("folio", TipoTexto);
        builder.Property(a => a.MargenPolitica).Columna("margenPolitica", "decimal(9,4)");
        builder.Property(a => a.MargenAplicado).Columna("margenAplicado", "decimal(9,4)");
        builder.Property(a => a.Diferencia).Columna("diferencia", "decimal(9,4)");
        builder.Property(a => a.Motivo).Columna("motivo", TipoTexto);
        builder.Property(a => a.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(a => a.CreatedAt).Fecha("createdAt");

        builder.HasIndex(a => a.CotizacionId).HasDatabaseName("ix_margenAjustes_cotizacionId");
        builder.HasIndex(a => a.LineaSolicitudId).HasDatabaseName("ix_margenAjustes_lineaSolicitudId").HasFilter("[lineaSolicitudId] IS NOT NULL");
        builder.HasOne<clsLineaSolicitud>().WithMany().HasForeignKey(a => a.LineaSolicitudId)
            .HasConstraintName("fk_margenAjustes_lineaSolicitudId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsCotizacionSolicitud>().WithMany().HasForeignKey(a => a.CotizacionId)
            .HasConstraintName("fk_margenAjustes_cotizacionId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(a => a.UsuarioId)
            .HasConstraintName("fk_margenAjustes_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}
