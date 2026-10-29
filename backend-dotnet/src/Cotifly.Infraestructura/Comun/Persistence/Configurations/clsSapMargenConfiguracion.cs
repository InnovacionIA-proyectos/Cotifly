using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary>
/// <c>dbo_cotifly.SapMargenCliente</c> (<c>db/scripts/17_sap_margen_sincronizacion.sql</c>, HU-042.1 #242; la lee HU-042.2 #243). Sin el
/// 17, <c>/health/ready</c> dice "falta 17". Las columnas de texto no llevan <c>COLLATE</c> en el script: heredan la de la base
/// (<see cref="clsCotiflyDbContext.TablasConCollationDeLaBase"/>).
/// </summary>
internal sealed class clsSapMargenClienteConfiguracion : IEntityTypeConfiguration<clsSapMargenClienteFila>
{
    public void Configure(EntityTypeBuilder<clsSapMargenClienteFila> builder)
    {
        builder.ToTable("SapMargenCliente");
        builder.HasKey(m => m.Id).HasName("pk_sapMargenCliente");
        builder.Property(m => m.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(m => m.SalesOrgId).Columna("salesOrgId", "nvarchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(m => m.TrqType).Columna("trqType", "nvarchar(8)").HasMaxLength(8).IsRequired();
        builder.Property(m => m.BpSap).Columna("bpSap", "nvarchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(m => m.Nit).Columna("nit", "nvarchar(20)").HasMaxLength(20);
        builder.Property(m => m.MargenRecomendado).Columna("margenRecomendado", "decimal(9,4)").HasPrecision(9, 4);
        builder.Property(m => m.Activo).Columna("activo", "bit");
        builder.Property(m => m.CreadoAt).Fecha("creadoAt");
        builder.Property(m => m.ActualizadoAt).Fecha("actualizadoAt");

        builder.HasIndex(m => new { m.SalesOrgId, m.TrqType, m.BpSap }).IsUnique().HasDatabaseName("uq_sapMargenCliente_clave");
        builder.HasIndex(m => m.Nit).HasDatabaseName("idx_sapMargenCliente_nit").HasFilter("[nit] IS NOT NULL");
    }
}

/// <summary><c>dbo_cotifly.SapMargenSyncLog</c> (<c>db/scripts/17</c>): log de corridas del job de Datos (HU-042.1 #242).</summary>
internal sealed class clsSapMargenSyncLogConfiguracion : IEntityTypeConfiguration<clsSapMargenSyncLogFila>
{
    public void Configure(EntityTypeBuilder<clsSapMargenSyncLogFila> builder)
    {
        builder.ToTable("SapMargenSyncLog", t =>
            t.HasCheckConstraint("ck_sapMargenSyncLog_status", "status IN (N'ok', N'error', N'omitido')"));
        builder.HasKey(l => l.Id).HasName("pk_sapMargenSyncLog");
        builder.Property(l => l.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(l => l.IniciadoAt).Fecha("iniciadoAt");
        builder.Property(l => l.FinalizadoAt).Fecha("finalizadoAt");
        builder.Property(l => l.FilasInsertadas).Columna("filasInsertadas", "int");
        builder.Property(l => l.FilasActualizadas).Columna("filasActualizadas", "int");
        builder.Property(l => l.FilasDesactivadas).Columna("filasDesactivadas", "int");
        builder.Property(l => l.Status).Columna("status", "nvarchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(l => l.MensajeError).Columna("mensajeError", "nvarchar(2000)").HasMaxLength(2000);

        builder.HasIndex(l => l.IniciadoAt).IsDescending(true).HasDatabaseName("idx_sapMargenSyncLog_iniciado");
    }
}
