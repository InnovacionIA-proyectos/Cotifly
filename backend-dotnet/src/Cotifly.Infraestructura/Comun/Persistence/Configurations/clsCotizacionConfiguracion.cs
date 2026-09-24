using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Cotifly.Infraestructura.Comun.Persistence.clsCotiflyDbContext;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary><c>dbo_cotifly.Cotizaciones</c> (script 0001 §4).</summary>
internal sealed class clsCotizacionConfiguracion : IEntityTypeConfiguration<clsCotizacion>
{
    public void Configure(EntityTypeBuilder<clsCotizacion> builder)
    {
        builder.ToTable("Cotizaciones", t => t.HasCheckConstraint(
            "ck_cotizaciones_origenCliente", "origenCliente IS NULL OR origenCliente IN ('cliente', 'prospecto')"));

        builder.HasKey(c => c.Id).HasName("pk_cotizaciones");
        builder.Property(c => c.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(c => c.Folio).Columna("folio", "nvarchar(50)").HasMaxLength(clsCotizacion.LargoMaximoFolio).IsRequired();
        builder.Property(c => c.LineaNegocioId).Columna("lineaNegocioId", "int");
        builder.Property(c => c.ClienteNombre).Columna("clienteNombre", TipoTexto);
        builder.Property(c => c.NitCliente).Columna("nitCliente", TipoTexto);
        builder.Property(c => c.OrigenCliente).Columna("origenCliente", "nvarchar(20)")
            .HasConversion(new clsConvertidorCodigoEnum<OrigenCliente>());
        builder.Property(c => c.CosteoId).Columna("costeoId", TipoTexto);
        builder.Property(c => c.Costo).Columna("costo", "decimal(18,2)");
        builder.Property(c => c.MargenPolitica).Columna("margenPolitica", "decimal(9,4)");
        builder.Property(c => c.MargenAplicado).Columna("margenAplicado", "decimal(9,4)");
        builder.Property(c => c.OrigenMargen).Columna("origenMargen", TipoTexto)
            .HasConversion(new clsConvertidorCodigoEnum<OrigenMargen>());
        builder.Property(c => c.MetodoCalculo).Columna("metodoCalculo", TipoTexto);
        builder.Property(c => c.PrecioFinal).Columna("precioFinal", "decimal(18,2)");
        builder.Property(c => c.Moneda).Columna("moneda", TipoTexto);
        builder.Property(c => c.TrmAplicada).Columna("trmAplicada", "decimal(18,4)");
        builder.Property(c => c.PdfPath).Columna("pdfPath", TipoTexto);
        builder.Property(c => c.EstadoCosteo).Columna("estadoCosteo", TipoTexto);
        builder.Property(c => c.EstadoEnvio).Columna("estadoEnvio", TipoTexto)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoEnvio>());
        builder.Property(c => c.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(c => c.SpaceId).Columna("spaceId", TipoTexto);
        builder.Property(c => c.ThreadId).Columna("threadId", TipoTexto);
        builder.Property(c => c.Ganada).Columna("ganada", "bit");
        builder.Property(c => c.MotivoRechazo).Columna("motivoRechazo", TipoTexto);
        builder.Property(c => c.Version).Columna("version", "int");
        builder.Property(c => c.MarcadoAt).Fecha("marcadoAt");
        builder.Property(c => c.CosteoListoAt).Fecha("costeoListoAt");
        builder.Property(c => c.CreatedAt).Fecha("createdAt");
        builder.Property(c => c.UpdatedAt).Fecha("updatedAt");

        builder.HasIndex(c => c.Folio).IsUnique().HasDatabaseName("uq_cotizaciones_folio");
        // Script 0002: (usuarioId, createdAt DESC) para "mis cotizaciones" (más recientes primero).
        builder.HasIndex(c => new { c.UsuarioId, c.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_cotizaciones_usuarioId_createdAt");

        builder.HasOne<clsMargenPolitica>().WithMany().HasForeignKey(c => c.LineaNegocioId)
            .HasConstraintName("fk_cotizaciones_lineaNegocioId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(c => c.UsuarioId)
            .HasConstraintName("fk_cotizaciones_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}
