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
        builder.ToTable("Cotizaciones", t =>
        {
            t.HasCheckConstraint("ck_cotizaciones_origenCliente", "origenCliente IS NULL OR origenCliente IN ('cliente', 'prospecto')");
            t.HasCheckConstraint("ck_cotizaciones_tipoSolicitud", "tipoSolicitud IS NULL OR tipoSolicitud IN ('spot', 'licitacion')");
            t.HasCheckConstraint("ck_cotizaciones_serviciosConexos_json", "serviciosConexos IS NULL OR ISJSON(serviciosConexos) = 1");
            t.HasCheckConstraint("ck_cotizaciones_textosCotizacion_json", "textosCotizacion IS NULL OR ISJSON(textosCotizacion) = 1");
            t.HasCheckConstraint("ck_cotizaciones_estadoCosteo",
                "estadoCosteo IS NULL OR estadoCosteo IN ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido')");
        });

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
        builder.Property(c => c.EstadoCosteo).Columna("estadoCosteo", TipoTexto)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoCosteo>());
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

        // Script 0004 (HU-005.8): formulario ampliado.
        builder.Property(c => c.TipoSolicitud).Columna("tipoSolicitud", "nvarchar(20)").HasMaxLength(20)
            .HasConversion(new clsConvertidorCodigoEnum<TipoSolicitud>());
        builder.Property(c => c.Deadline).Fecha("deadline");
        builder.Property(c => c.ValorEstimado).Columna("valorEstimado", "decimal(18,2)");
        builder.Property(c => c.Referencia).Columna("referencia", "nvarchar(200)").HasMaxLength(clsSolicitudCosteo.LargoReferencia);
        builder.Property(c => c.HorasCargue).Columna("horasCargue", "decimal(6,2)");
        builder.Property(c => c.HorasDescargue).Columna("horasDescargue", "decimal(6,2)");
        builder.Property(c => c.Origen).Columna("origen", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad);
        builder.Property(c => c.Destino).Columna("destino", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad);
        builder.Property(c => c.TipoVehiculo).Columna("tipoVehiculo", "nvarchar(100)").HasMaxLength(clsSolicitudCosteo.LargoCatalogo);
        builder.Property(c => c.Carroceria).Columna("carroceria", "nvarchar(100)").HasMaxLength(clsSolicitudCosteo.LargoCatalogo);
        builder.Property(c => c.Tipologia).Columna("tipologia", "nvarchar(50)").HasMaxLength(clsSolicitudCosteo.LargoCorto);
        builder.Property(c => c.ClaseViaje).Columna("claseViaje", "nvarchar(50)").HasMaxLength(clsSolicitudCosteo.LargoCorto);
        builder.Property(c => c.ClaseContenedor).Columna("claseContenedor", "nvarchar(20)").HasMaxLength(clsSolicitudCosteo.LargoClaseContenedor);
        builder.Property(c => c.Exclusivo).Columna("exclusivo", "bit");
        builder.Property(c => c.Consolidado).Columna("consolidado", "bit");
        builder.Property(c => c.RetornaContenedor).Columna("retornaContenedor", "bit");
        builder.Property(c => c.CiudadDevolucionContenedor).Columna("ciudadDevolucionContenedor", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad);
        builder.Property(c => c.LevanteContenedorDiferente).Columna("levanteContenedorDiferente", "bit");
        builder.Property(c => c.CiudadLevanteContenedor).Columna("ciudadLevanteContenedor", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad);
        builder.Property(c => c.ValorAprovisionamiento).Columna("valorAprovisionamiento", "decimal(18,2)");
        builder.Property(c => c.PesoKg).Columna("pesoKg", "decimal(12,2)");
        builder.Property(c => c.ValorMercancia).Columna("valorMercancia", "decimal(18,2)");

        // Script 0004 (ADR-015): seguimiento de la integración con Nexo.
        builder.Property(c => c.CotizacionNexoId).Columna("cotizacionNexoId", "nvarchar(64)").HasMaxLength(64);
        builder.Property(c => c.MotivoEstadoCosteo).Columna("motivoEstadoCosteo", "nvarchar(1000)").HasMaxLength(clsCotizacion.LargoMaximoMotivoCosteo);
        builder.Property(c => c.CosteoEnviadoAt).Fecha("costeoEnviadoAt");
        builder.Property(c => c.CosteoUltimaConsultaAt).Fecha("costeoUltimaConsultaAt");
        builder.Property(c => c.CosteoSiguienteConsultaAt).Fecha("costeoSiguienteConsultaAt");
        builder.Property(c => c.CosteoConsultasFallidas).Columna("costeoConsultasFallidas", "int");
        builder.Property(c => c.ServiciosConexos).Columna("serviciosConexos", TipoTexto);

        // Script 0006 (ADR-018 §8 y §11). rowVersion: la escribe SQL Server y EF la relee tras cada INSERT/UPDATE; NO es token de
        // concurrencia global (el polling y los demás flujos siguen igual): el If-Match lo verifica IClsUnitOfWork.GuardarCambiosSiVersionAsync.
        builder.Property(c => c.RowVersion).Columna("rowVersion", "rowversion").ValueGeneratedOnAddOrUpdate().IsRequired();
        builder.Property(c => c.TextosCotizacion).Columna("textosCotizacion", TipoTexto);
        builder.Property(c => c.PdfDesactualizado).Columna("pdfDesactualizado", "bit");

        builder.HasIndex(c => c.Folio).IsUnique().HasDatabaseName("uq_cotizaciones_folio");
        // Script 0002: (usuarioId, createdAt DESC) para "mis cotizaciones" (más recientes primero).
        builder.HasIndex(c => new { c.UsuarioId, c.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_cotizaciones_usuarioId_createdAt");
        // Script 0004: agenda del polling de Nexo (HU-005.6).
        builder.HasIndex(c => c.CosteoSiguienteConsultaAt).HasDatabaseName("ix_cotizaciones_costeoSiguienteConsultaAt")
            .HasFilter("[costeoSiguienteConsultaAt] IS NOT NULL");

        builder.HasOne<clsMargenPolitica>().WithMany().HasForeignKey(c => c.LineaNegocioId)
            .HasConstraintName("fk_cotizaciones_lineaNegocioId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(c => c.UsuarioId)
            .HasConstraintName("fk_cotizaciones_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.CotizacionPosiciones</c> (script 0004 §3, HU-005.8).</summary>
internal sealed class clsCotizacionPosicionConfiguracion : IEntityTypeConfiguration<clsCotizacionPosicion>
{
    public void Configure(EntityTypeBuilder<clsCotizacionPosicion> builder)
    {
        builder.ToTable("CotizacionPosiciones", t =>
        {
            t.HasCheckConstraint("ck_cotizacionPosiciones_cantidad", "cantidad >= 1");
            t.HasCheckConstraint("ck_cotizacionPosiciones_medidas", "largoCm > 0 AND anchoCm > 0 AND altoCm > 0 AND (pesoKg IS NULL OR pesoKg >= 0)");
        });

        builder.HasKey(p => p.Id).HasName("pk_cotizacionPosiciones");
        builder.Property(p => p.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(p => p.CotizacionId).Columna("cotizacionId", "uniqueidentifier");
        builder.Property(p => p.LineaSolicitudId).Columna("lineaSolicitudId", "uniqueidentifier");
        builder.Property(p => p.Orden).Columna("orden", "int");
        builder.Property(p => p.Descripcion).Columna("descripcion", "nvarchar(200)").HasMaxLength(clsPosicionCarga.LargoDescripcion);
        builder.Property(p => p.Cantidad).Columna("cantidad", "int");
        builder.Property(p => p.LargoCm).Columna("largoCm", "decimal(10,2)");
        builder.Property(p => p.AnchoCm).Columna("anchoCm", "decimal(10,2)");
        builder.Property(p => p.AltoCm).Columna("altoCm", "decimal(10,2)");
        builder.Property(p => p.PesoKg).Columna("pesoKg", "decimal(12,2)");
        builder.Property(p => p.CreatedAt).Fecha("createdAt");

        builder.HasIndex(p => new { p.CotizacionId, p.Orden }).IsUnique().HasDatabaseName("uq_cotizacionPosiciones_cotizacionId_orden");
        builder.HasOne<clsCotizacion>().WithMany().HasForeignKey(p => p.CotizacionId)
            .HasConstraintName("fk_cotizacionPosiciones_cotizacionId").OnDelete(DeleteBehavior.NoAction);

        // Script 0005 (ADR-017): cada bulto pertenece a una línea de la solicitud (nullable en filas anteriores al backfill).
        builder.HasIndex(p => p.LineaSolicitudId).HasDatabaseName("ix_cotizacionPosiciones_lineaSolicitudId")
            .HasFilter("[lineaSolicitudId] IS NOT NULL");
        builder.HasOne<clsCotizacionLineaSolicitud>().WithMany().HasForeignKey(p => p.LineaSolicitudId)
            .HasConstraintName("fk_cotizacionPosiciones_lineaSolicitudId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.CotizacionLineasSolicitud</c> (script 0005 §1, HU-011.2, ADR-017).</summary>
internal sealed class clsCotizacionLineaSolicitudConfiguracion : IEntityTypeConfiguration<clsCotizacionLineaSolicitud>
{
    public void Configure(EntityTypeBuilder<clsCotizacionLineaSolicitud> builder)
    {
        builder.ToTable("CotizacionLineasSolicitud", t =>
        {
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_orden", "orden >= 1");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_filaOrigen", "filaOrigen IS NULL OR filaOrigen >= 0");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_montos", "(costo IS NULL OR costo >= 0) AND (precioFinal IS NULL OR precioFinal >= 0)");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_serviciosConexos_json", "serviciosConexos IS NULL OR ISJSON(serviciosConexos) = 1");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_estadoCosteo",
                "estadoCosteo IS NULL OR estadoCosteo IN ('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido')");
        });

        builder.HasKey(l => l.Id).HasName("pk_cotizacionLineasSolicitud");
        builder.Property(l => l.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(l => l.CotizacionId).Columna("cotizacionId", "uniqueidentifier");
        builder.Property(l => l.Orden).Columna("orden", "int");
        builder.Property(l => l.FilaOrigen).Columna("filaOrigen", "int");
        builder.Property(l => l.Origen).Columna("origen", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad).IsRequired();
        builder.Property(l => l.Destino).Columna("destino", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad).IsRequired();
        builder.Property(l => l.TipoVehiculo).Columna("tipoVehiculo", "nvarchar(100)").HasMaxLength(clsSolicitudCosteo.LargoCatalogo);
        builder.Property(l => l.Carroceria).Columna("carroceria", "nvarchar(100)").HasMaxLength(clsSolicitudCosteo.LargoCatalogo);
        builder.Property(l => l.Tipologia).Columna("tipologia", "nvarchar(50)").HasMaxLength(clsSolicitudCosteo.LargoCorto);
        builder.Property(l => l.ClaseViaje).Columna("claseViaje", "nvarchar(50)").HasMaxLength(clsSolicitudCosteo.LargoCorto);
        builder.Property(l => l.ClaseContenedor).Columna("claseContenedor", "nvarchar(20)").HasMaxLength(clsSolicitudCosteo.LargoClaseContenedor);
        builder.Property(l => l.Exclusivo).Columna("exclusivo", "bit");
        builder.Property(l => l.Consolidado).Columna("consolidado", "bit");
        builder.Property(l => l.RetornaContenedor).Columna("retornaContenedor", "bit");
        builder.Property(l => l.CiudadDevolucionContenedor).Columna("ciudadDevolucionContenedor", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad);
        builder.Property(l => l.LevanteContenedorDiferente).Columna("levanteContenedorDiferente", "bit");
        builder.Property(l => l.CiudadLevanteContenedor).Columna("ciudadLevanteContenedor", "nvarchar(150)").HasMaxLength(clsSolicitudCosteo.LargoCiudad);
        builder.Property(l => l.ValorAprovisionamiento).Columna("valorAprovisionamiento", "decimal(18,2)");
        builder.Property(l => l.PesoKg).Columna("pesoKg", "decimal(12,2)");
        builder.Property(l => l.ValorMercancia).Columna("valorMercancia", "decimal(18,2)");
        builder.Property(l => l.LineaSolicitudNexoId).Columna("lineaSolicitudNexoId", "nvarchar(64)").HasMaxLength(64);
        builder.Property(l => l.EstadoCosteo).Columna("estadoCosteo", "nvarchar(30)").HasMaxLength(30)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoCosteo>());
        builder.Property(l => l.Costo).Columna("costo", "decimal(18,2)");
        builder.Property(l => l.PrecioFinal).Columna("precioFinal", "decimal(18,2)");
        builder.Property(l => l.ServiciosConexos).Columna("serviciosConexos", TipoTexto);
        builder.Property(l => l.CreatedAt).Fecha("createdAt");
        builder.Property(l => l.UpdatedAt).Fecha("updatedAt");

        builder.HasIndex(l => new { l.CotizacionId, l.Orden }).IsUnique().HasDatabaseName("uq_cotizacionLineasSolicitud_cotizacionId_orden");
        builder.HasOne<clsCotizacion>().WithMany().HasForeignKey(l => l.CotizacionId)
            .HasConstraintName("fk_cotizacionLineasSolicitud_cotizacionId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.CotizacionArchivosLicitacion</c> (script 0005 §3, HU-011.2/HU-011.3, ADR-017).</summary>
internal sealed class clsArchivoLicitacionConfiguracion : IEntityTypeConfiguration<clsArchivoLicitacion>
{
    public void Configure(EntityTypeBuilder<clsArchivoLicitacion> builder)
    {
        builder.ToTable("CotizacionArchivosLicitacion", t => t.HasCheckConstraint(
            "ck_cotizacionArchivosLicitacion_tamano", "tamanoBytes BETWEEN 1 AND 10485760 AND DATALENGTH(contenido) = tamanoBytes"));

        builder.HasKey(a => a.CotizacionId).HasName("pk_cotizacionArchivosLicitacion");
        builder.Property(a => a.CotizacionId).Columna("cotizacionId", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(a => a.NombreArchivo).Columna("nombreArchivo", "nvarchar(255)").HasMaxLength(clsArchivoLicitacion.LargoNombreArchivo).IsRequired();
        builder.Property(a => a.TipoContenido).Columna("tipoContenido", "nvarchar(100)").HasMaxLength(100).IsRequired();
        builder.Property(a => a.TamanoBytes).Columna("tamanoBytes", "int");
        builder.Property(a => a.Sha256).Columna("sha256", "nvarchar(64)").HasMaxLength(64).IsRequired();
        builder.Property(a => a.Hoja).Columna("hoja", "nvarchar(31)").HasMaxLength(clsArchivoLicitacion.LargoHoja).IsRequired();
        builder.Property(a => a.Contenido).Columna("contenido", "varbinary(max)").IsRequired();
        builder.Property(a => a.CreatedAt).Fecha("createdAt");

        builder.HasOne<clsCotizacion>().WithOne().HasForeignKey<clsArchivoLicitacion>(a => a.CotizacionId)
            .HasConstraintName("fk_cotizacionArchivosLicitacion_cotizacionId").OnDelete(DeleteBehavior.NoAction);
    }
}

/// <summary><c>dbo_cotifly.CotizacionPdfsHistorial</c> (script 0006 §4, ADR-018 §11).</summary>
internal sealed class clsCotizacionPdfHistorialConfiguracion : IEntityTypeConfiguration<clsCotizacionPdfHistorial>
{
    public void Configure(EntityTypeBuilder<clsCotizacionPdfHistorial> builder)
    {
        builder.ToTable("CotizacionPdfsHistorial", t => t.HasCheckConstraint(
            "ck_cotizacionPdfsHistorial_motivo", "motivo IN ('ajuste_margen', 'servicios_conexos', 'textos', 're_solicitud')"));

        builder.HasKey(h => h.Id).HasName("pk_cotizacionPdfsHistorial");
        builder.Property(h => h.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(h => h.CotizacionId).Columna("cotizacionId", "uniqueidentifier");
        builder.Property(h => h.PdfPath).Columna("pdfPath", "nvarchar(400)").HasMaxLength(clsCotizacionPdfHistorial.LargoPdfPath).IsRequired();
        builder.Property(h => h.Motivo).Columna("motivo", "nvarchar(30)").HasMaxLength(30)
            .HasConversion(new clsConvertidorCodigoEnum<MotivoInvalidacionPdf>());
        builder.Property(h => h.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(h => h.CreatedAt).Fecha("createdAt");

        builder.HasIndex(h => h.CotizacionId).HasDatabaseName("ix_cotizacionPdfsHistorial_cotizacionId");
        builder.HasOne<clsCotizacion>().WithMany().HasForeignKey(h => h.CotizacionId)
            .HasConstraintName("fk_cotizacionPdfsHistorial_cotizacionId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(h => h.UsuarioId)
            .HasConstraintName("fk_cotizacionPdfsHistorial_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}
