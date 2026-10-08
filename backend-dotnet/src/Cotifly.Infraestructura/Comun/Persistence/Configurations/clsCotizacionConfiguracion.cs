using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Margen;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Cotifly.Infraestructura.Comun.Persistence.clsCotiflyDbContext;

namespace Cotifly.Infraestructura.Comun.Persistence.Configurations;

/// <summary>
/// <c>dbo_cotifly.CotizacionesSolicitud</c> (ex <c>Cotizaciones</c>; scripts 0001, 0004, 0006, 0007, 0009 y 0010; HU-021, ADR-019): la
/// cabecera de la solicitud, raíz del agregado. Las restricciones conservan los nombres <c>cotizaciones</c> (los deja así el 0009 y los
/// repite <c>db/scripts/01_crear_esquema.sql</c>).
/// </summary>
internal sealed class clsCotizacionSolicitudConfiguracion : IEntityTypeConfiguration<clsCotizacionSolicitud>
{
    public const string EstadosCosteo =
        "('pendiente_envio', 'error_envio', 'pendiente_costeo', 'autocosteo', 'entregada', 'descartada', 'costeo_vencido')";

    public void Configure(EntityTypeBuilder<clsCotizacionSolicitud> builder)
    {
        builder.ToTable("CotizacionesSolicitud", t =>
        {
            t.HasCheckConstraint("ck_cotizaciones_origenCliente", "origenCliente IS NULL OR origenCliente IN ('cliente', 'prospecto')");
            t.HasCheckConstraint("ck_cotizaciones_tipoSolicitud", "tipoSolicitud IS NULL OR tipoSolicitud IN ('spot', 'licitacion')");
            t.HasCheckConstraint("ck_cotizaciones_estadoEnvio", "estadoEnvio IS NULL OR estadoEnvio IN ('sin_enviar', 'enviada', 'reenviada')");
            t.HasCheckConstraint("ck_cotizaciones_textosCotizacion_json", "textosCotizacion IS NULL OR ISJSON(textosCotizacion) = 1");
            t.HasCheckConstraint("ck_cotizaciones_versionNegocio", "versionNegocio >= 0");
            t.HasCheckConstraint("ck_cotizaciones_estadoCosteo", $"estadoCosteo IS NULL OR estadoCosteo IN {EstadosCosteo}");
        });

        builder.HasKey(c => c.Id).HasName("pk_cotizaciones");
        builder.Property(c => c.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(c => c.Folio).Columna("folio", "nvarchar(50)").HasMaxLength(clsCotizacionSolicitud.LargoMaximoFolio).IsRequired();
        builder.Property(c => c.ClienteNombre).Columna("clienteNombre", TipoTexto);
        builder.Property(c => c.NitCliente).Columna("nitCliente", TipoTexto);
        builder.Property(c => c.OrigenCliente).Columna("origenCliente", "nvarchar(20)")
            .HasConversion(new clsConvertidorCodigoEnum<OrigenCliente>());
        builder.Property(c => c.TipoSolicitud).Columna("tipoSolicitud", "nvarchar(20)").HasMaxLength(20)
            .HasConversion(new clsConvertidorCodigoEnum<TipoSolicitud>());
        builder.Property(c => c.Deadline).Fecha("deadline");
        builder.Property(c => c.PrecioFinal).Columna("precioFinal", "decimal(18,2)");
        builder.Property(c => c.Moneda).Columna("moneda", TipoTexto);
        builder.Property(c => c.TrmAplicada).Columna("trmAplicada", "decimal(18,4)");
        builder.Property(c => c.PdfPath).Columna("pdfPath", TipoTexto);
        builder.Property(c => c.PdfDesactualizado).Columna("pdfDesactualizado", "bit");
        builder.Property(c => c.UsuarioId).Columna("usuarioId", "uniqueidentifier");
        builder.Property(c => c.SpaceId).Columna("spaceId", TipoTexto);
        builder.Property(c => c.ThreadId).Columna("threadId", TipoTexto);
        builder.Property(c => c.Ganada).Columna("ganada", "bit");
        builder.Property(c => c.MotivoRechazo).Columna("motivoRechazo", TipoTexto);
        builder.Property(c => c.Version).Columna("version", "int");

        // versionNegocio: la sube la aplicación en los cambios de negocio; NO es token de concurrencia global de EF: el If-Match (y la
        // versión leída por el polling) lo verifica IClsUnitOfWork.GuardarCambiosSiVersionAsync (ADR-018 §12).
        builder.Property(c => c.VersionNegocio).Columna("versionNegocio", "int");
        builder.Property(c => c.MarcadoAt).Fecha("marcadoAt");
        builder.Property(c => c.CosteoListoAt).Fecha("costeoListoAt");
        builder.Property(c => c.CreatedAt).Fecha("createdAt");
        builder.Property(c => c.UpdatedAt).Fecha("updatedAt");
        builder.Property(c => c.EstadoEnvio).Columna("estadoEnvio", TipoTexto)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoEnvio>());
        builder.Property(c => c.EstadoCosteo).Columna("estadoCosteo", TipoTexto)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoCosteo>());
        builder.Property(c => c.CotizacionNexoId).Columna("cotizacionNexoId", "nvarchar(64)").HasMaxLength(clsCotizacionSolicitud.LargoMaximoIdNexo);
        builder.Property(c => c.SolicitudNexoId).Columna("solicitudNexoId", "nvarchar(64)").HasMaxLength(clsCotizacionSolicitud.LargoMaximoIdNexo);
        builder.Property(c => c.MotivoEstadoCosteo).Columna("motivoEstadoCosteo", "nvarchar(1000)").HasMaxLength(clsCotizacionSolicitud.LargoMaximoMotivoCosteo);
        builder.Property(c => c.CosteoEnviadoAt).Fecha("costeoEnviadoAt");
        builder.Property(c => c.CosteoSiguienteConsultaAt).Fecha("costeoSiguienteConsultaAt");
        builder.Property(c => c.CosteoConsultasFallidas).Columna("costeoConsultasFallidas", "int");
        builder.Property(c => c.TextosCotizacion).Columna("textosCotizacion", TipoTexto);
        builder.Property(c => c.Notas).Columna("notas", "nvarchar(2000)").HasMaxLength(clsSolicitudCosteo.LargoNotas);

        builder.HasIndex(c => c.Folio).IsUnique().HasDatabaseName("uq_cotizaciones_folio");
        // Script 0002: (usuarioId, createdAt DESC) para "mis cotizaciones" (más recientes primero).
        builder.HasIndex(c => new { c.UsuarioId, c.CreatedAt }).IsDescending(false, true).HasDatabaseName("ix_cotizaciones_usuarioId_createdAt");
        // Script 0004: agenda del polling de Nexo (HU-005.6).
        builder.HasIndex(c => c.CosteoSiguienteConsultaAt).HasDatabaseName("ix_cotizaciones_costeoSiguienteConsultaAt")
            .HasFilter("[costeoSiguienteConsultaAt] IS NOT NULL");

        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(c => c.UsuarioId)
            .HasConstraintName("fk_cotizaciones_usuarioId").OnDelete(DeleteBehavior.NoAction);

        // El agregado: las rutas se leen y se guardan con la cabecera (backing field _rutas). ClientCascade: en la base la FK es
        // NO ACTION; EF borra en memoria lo que se quita del agregado (p. ej. un conexo) en vez de dejarlo huérfano.
        builder.HasMany(c => c.Rutas).WithOne().HasForeignKey(r => r.CotizacionSolicitudId)
            .HasConstraintName("fk_cotizacionLineasSolicitud_cotizacionId").OnDelete(DeleteBehavior.ClientCascade);
        builder.Navigation(c => c.Rutas).HasField("_rutas").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(c => c.RutasOrdenadas);
        builder.Ignore(c => c.PrimeraRuta);
        builder.Ignore(c => c.Vehiculos);
    }
}

/// <summary><c>dbo_cotifly.CotizacionLineasSolicitud</c>: la ruta (scripts 0005, 0007, 0008, 0009 y 0010; HU-021, ADR-019).</summary>
internal sealed class clsLineaSolicitudConfiguracion : IEntityTypeConfiguration<clsLineaSolicitud>
{
    public void Configure(EntityTypeBuilder<clsLineaSolicitud> builder)
    {
        builder.ToTable("CotizacionLineasSolicitud", t =>
        {
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_orden", "orden >= 1");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_filaOrigen", "rutaOrigenIndice IS NULL OR rutaOrigenIndice >= 0");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_estadoCosteo",
                $"estadoCosteo IS NULL OR estadoCosteo IN {clsCotizacionSolicitudConfiguracion.EstadosCosteo}");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_municipios",
                "(origenMunicipioId IS NULL OR origenMunicipioId > 0) AND (destinoMunicipioId IS NULL OR destinoMunicipioId > 0)");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_municipiosContenedor",
                "(ciudadDevolucionMunicipioId IS NULL OR ciudadDevolucionMunicipioId > 0) AND (ciudadLevanteMunicipioId IS NULL OR ciudadLevanteMunicipioId > 0)");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_costos",
                "(valorLevanteContenedor IS NULL OR valorLevanteContenedor >= 0) AND (costoFletesTotal IS NULL OR costoFletesTotal >= 0) "
                + "AND (costoRetornoContenedorTotal IS NULL OR costoRetornoContenedorTotal >= 0) AND (costoLevanteContenedorTotal IS NULL OR costoLevanteContenedorTotal >= 0) "
                + "AND (costoServiciosConexos IS NULL OR costoServiciosConexos >= 0) AND (costoFinal IS NULL OR costoFinal >= 0) AND (precioFinalLinea IS NULL OR precioFinalLinea >= 0)");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_horas",
                "(horasCargue IS NULL OR (horasCargue >= 0 AND horasCargue <= 240)) AND (horasDescargue IS NULL OR (horasDescargue >= 0 AND horasDescargue <= 240))");
            t.HasCheckConstraint("ck_cotizacionLineasSolicitud_origenMargen", "origenMargen IS NULL OR origenMargen IN ('sap_tm', 'politica_linea', 'negociado')");
        });

        builder.HasKey(l => l.Id).HasName("pk_cotizacionLineasSolicitud");
        builder.Property(l => l.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(l => l.CotizacionSolicitudId).Columna("cotizacionSolicitudId", "uniqueidentifier");
        builder.Property(l => l.Orden).Columna("orden", "int");
        builder.Property(l => l.RutaOrigenIndice).Columna("rutaOrigenIndice", "int");
        builder.Property(l => l.LineaNegocioId).Columna("lineaNegocioId", "int");
        builder.Property(l => l.OrigenMunicipioId).Columna("origenMunicipioId", "int");
        builder.Property(l => l.OrigenNombre).Columna("origenNombre", "nvarchar(150)").HasMaxLength(clsLineaSolicitud.LargoMaximoNombreMunicipio);
        builder.Property(l => l.DestinoMunicipioId).Columna("destinoMunicipioId", "int");
        builder.Property(l => l.DestinoNombre).Columna("destinoNombre", "nvarchar(150)").HasMaxLength(clsLineaSolicitud.LargoMaximoNombreMunicipio);
        builder.Property(l => l.Tipologia).Columna("tipologia", "nvarchar(50)").HasMaxLength(clsSolicitudCosteo.LargoCorto);
        builder.Property(l => l.ClaseViaje).Columna("claseViaje", "nvarchar(50)").HasMaxLength(clsSolicitudCosteo.LargoCorto);
        builder.Property(l => l.ClaseContenedor).Columna("claseContenedor", "nvarchar(20)").HasMaxLength(clsSolicitudCosteo.LargoClaseContenedor);
        builder.Property(l => l.Exclusivo).Columna("exclusivo", "bit");
        builder.Property(l => l.Consolidado).Columna("consolidado", "bit");
        builder.Property(l => l.RetornaContenedor).Columna("retornaContenedor", "bit");
        builder.Property(l => l.LevanteContenedorDiferente).Columna("levanteContenedorDiferente", "bit");
        builder.Property(l => l.CiudadDevolucionMunicipioId).Columna("ciudadDevolucionMunicipioId", "int");
        builder.Property(l => l.CiudadDevolucionNombre).Columna("ciudadDevolucionNombre", "nvarchar(150)").HasMaxLength(clsLineaSolicitud.LargoMaximoNombreMunicipio);
        builder.Property(l => l.CiudadLevanteMunicipioId).Columna("ciudadLevanteMunicipioId", "int");
        builder.Property(l => l.CiudadLevanteNombre).Columna("ciudadLevanteNombre", "nvarchar(150)").HasMaxLength(clsLineaSolicitud.LargoMaximoNombreMunicipio);
        builder.Property(l => l.HorasCargue).Columna("horasCargue", "decimal(6,2)");
        builder.Property(l => l.HorasDescargue).Columna("horasDescargue", "decimal(6,2)");
        builder.Property(l => l.PesoKg).Columna("pesoKg", "decimal(12,2)");
        builder.Property(l => l.ValorMercancia).Columna("valorMercancia", "decimal(18,2)");
        builder.Property(l => l.ValorLevanteContenedor).Columna("valorLevanteContenedor", "decimal(18,2)");
        builder.Property(l => l.CostoFletesTotal).Columna("costoFletesTotal", "decimal(18,2)");
        builder.Property(l => l.CostoRetornoContenedorTotal).Columna("costoRetornoContenedorTotal", "decimal(18,2)");
        builder.Property(l => l.CostoLevanteContenedorTotal).Columna("costoLevanteContenedorTotal", "decimal(18,2)");
        builder.Property(l => l.CostoServiciosConexos).Columna("costoServiciosConexos", "decimal(18,2)");
        builder.Property(l => l.CostoFinal).Columna("costoFinal", "decimal(18,2)");
        builder.Property(l => l.MargenPoliticaSugerido).Columna("margenPoliticaSugerido", "decimal(9,4)");
        builder.Property(l => l.MargenSAPSugerido).Columna("margenSAPSugerido", "decimal(9,4)");
        builder.Property(l => l.MargenAplicado).Columna("margenAplicado", "decimal(9,4)");
        builder.Property(l => l.OrigenMargen).Columna("origenMargen", "nvarchar(20)").HasMaxLength(20)
            .HasConversion(new clsConvertidorCodigoEnum<OrigenMargen>());
        builder.Property(l => l.MetodoCalculo).Columna("metodoCalculo", "nvarchar(20)").HasMaxLength(20);
        builder.Property(l => l.PrecioFinalLinea).Columna("precioFinalLinea", "decimal(18,2)");
        builder.Property(l => l.EstadoCosteo).Columna("estadoCosteo", "nvarchar(30)").HasMaxLength(30)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoCosteo>());
        builder.Property(l => l.CreatedAt).Fecha("createdAt");
        builder.Property(l => l.UpdatedAt).Fecha("updatedAt");

        builder.HasIndex(l => new { l.CotizacionSolicitudId, l.Orden }).IsUnique().HasDatabaseName("uq_cotizacionLineasSolicitud_cotizacionId_orden");
        builder.HasOne<clsMargenPolitica>().WithMany().HasForeignKey(l => l.LineaNegocioId)
            .HasConstraintName("fk_cotizacionLineasSolicitud_lineaNegocioId").OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(l => l.Vehiculos).WithOne().HasForeignKey(v => v.LineaSolicitudId)
            .HasConstraintName("fk_cotizacionVehiculosLineaSolicitud_lineaSolicitudId").OnDelete(DeleteBehavior.ClientCascade);
        builder.Navigation(l => l.Vehiculos).HasField("_vehiculos").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(l => l.Mercancias).WithOne().HasForeignKey(m => m.LineaSolicitudId)
            .HasConstraintName("fk_cotizacionMercancias_lineaSolicitudId").OnDelete(DeleteBehavior.ClientCascade);
        builder.Navigation(l => l.Mercancias).HasField("_mercancias").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(l => l.Conexos).WithOne().HasForeignKey(c => c.LineaSolicitudId)
            .HasConstraintName("fk_cotizacionServiciosConexos_lineaSolicitudId").OnDelete(DeleteBehavior.ClientCascade);
        builder.Navigation(l => l.Conexos).HasField("_conexos").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(l => l.VehiculosOrdenados);
        builder.Ignore(l => l.MercanciasOrdenadas);
    }
}

/// <summary><c>dbo_cotifly.CotizacionVehiculosLineaSolicitud</c>: un camión por fila (script 0009, HU-021, ADR-019).</summary>
internal sealed class clsVehiculoLineaSolicitudConfiguracion : IEntityTypeConfiguration<clsVehiculoLineaSolicitud>
{
    public void Configure(EntityTypeBuilder<clsVehiculoLineaSolicitud> builder)
    {
        builder.ToTable("CotizacionVehiculosLineaSolicitud", t =>
        {
            t.HasCheckConstraint("ck_cotizacionVehiculosLineaSolicitud_montos",
                "(pesoKg IS NULL OR pesoKg >= 0) AND (costoFlete IS NULL OR costoFlete >= 0) AND (costoRetornoContenedor IS NULL OR costoRetornoContenedor >= 0) "
                + "AND (costoLevanteContenedor IS NULL OR costoLevanteContenedor >= 0)");
            t.HasCheckConstraint("ck_cotizacionVehiculosLineaSolicitud_estadoCosteo",
                $"estadoCosteo IS NULL OR estadoCosteo IN {clsCotizacionSolicitudConfiguracion.EstadosCosteo}");
        });

        builder.HasKey(v => v.Id).HasName("pk_cotizacionVehiculosLineaSolicitud");
        builder.Property(v => v.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(v => v.LineaSolicitudId).Columna("lineaSolicitudId", "uniqueidentifier");
        builder.Property(v => v.IdRecomendacionVehiculo).Columna("idRecomendacionVehiculo", "uniqueidentifier");
        builder.Property(v => v.TipoVehiculo).Columna("tipoVehiculo", "nvarchar(100)").HasMaxLength(clsSolicitudCosteo.LargoCatalogo);
        builder.Property(v => v.Carroceria).Columna("carroceria", "nvarchar(100)").HasMaxLength(clsSolicitudCosteo.LargoCatalogo);
        builder.Property(v => v.PesoKg).Columna("pesoKg", "decimal(12,2)");
        builder.Property(v => v.CostoFlete).Columna("costoFlete", "decimal(18,2)");
        builder.Property(v => v.CostoRetornoContenedor).Columna("costoRetornoContenedor", "decimal(18,2)");
        builder.Property(v => v.CostoLevanteContenedor).Columna("costoLevanteContenedor", "decimal(18,2)");
        builder.Property(v => v.LineaSolicitudNexoId).Columna("lineaSolicitudNexoId", "nvarchar(64)").HasMaxLength(64);
        builder.Property(v => v.EstadoCosteo).Columna("estadoCosteo", "nvarchar(30)").HasMaxLength(30)
            .HasConversion(new clsConvertidorCodigoEnum<EstadoCosteo>());
        builder.Property(v => v.CreatedAt).Fecha("createdAt");
        builder.Property(v => v.UpdatedAt).Fecha("updatedAt");

        builder.HasIndex(v => v.LineaSolicitudId).HasDatabaseName("ix_cotizacionVehiculosLineaSolicitud_lineaSolicitudId");
    }
}

/// <summary><c>dbo_cotifly.CotizacionMercancias</c> (ex <c>CotizacionPosiciones</c>; scripts 0004, 0005 y 0009; HU-021).</summary>
internal sealed class clsMercanciaConfiguracion : IEntityTypeConfiguration<clsMercancia>
{
    public void Configure(EntityTypeBuilder<clsMercancia> builder)
    {
        builder.ToTable("CotizacionMercancias", t =>
        {
            t.HasCheckConstraint("ck_cotizacionPosiciones_cantidad", "cantidad >= 1");
            t.HasCheckConstraint("ck_cotizacionPosiciones_medidas", "largoCm > 0 AND anchoCm > 0 AND altoCm > 0 AND (pesoKg IS NULL OR pesoKg >= 0)");
        });

        builder.HasKey(m => m.Id).HasName("pk_cotizacionPosiciones");
        builder.Property(m => m.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(m => m.LineaSolicitudId).Columna("lineaSolicitudId", "uniqueidentifier");
        builder.Property(m => m.Orden).Columna("orden", "int");
        builder.Property(m => m.IdTipoMercancia).Columna("idTipoMercancia", "uniqueidentifier");
        builder.Property(m => m.NombreMercancia).Columna("nombreMercancia", "nvarchar(200)").HasMaxLength(clsPosicionCarga.LargoDescripcion);
        builder.Property(m => m.Cantidad).Columna("cantidad", "int");
        builder.Property(m => m.LargoCm).Columna("largoCm", "decimal(10,2)");
        builder.Property(m => m.AnchoCm).Columna("anchoCm", "decimal(10,2)");
        builder.Property(m => m.AltoCm).Columna("altoCm", "decimal(10,2)");
        builder.Property(m => m.PesoKg).Columna("pesoKg", "decimal(12,2)");
        builder.Property(m => m.CreatedAt).Fecha("createdAt");

        builder.HasIndex(m => new { m.LineaSolicitudId, m.Orden }).IsUnique().HasDatabaseName("uq_cotizacionMercancias_lineaSolicitudId_orden");
    }
}

/// <summary><c>dbo_cotifly.CotizacionServiciosConexos</c>: los conexos de cada ruta (scripts 0009 y 0010; HU-021, ADR-019).</summary>
internal sealed class clsServicioConexoRutaConfiguracion : IEntityTypeConfiguration<clsServicioConexoRuta>
{
    public void Configure(EntityTypeBuilder<clsServicioConexoRuta> builder)
    {
        builder.ToTable("CotizacionServiciosConexos", t =>
            t.HasCheckConstraint("ck_cotizacionServiciosConexos_montos", "precio >= 0 AND cantidad > 0"));

        builder.HasKey(c => c.Id).HasName("pk_cotizacionServiciosConexos");
        builder.Property(c => c.Id).Columna("id", "uniqueidentifier").ValueGeneratedNever();
        builder.Property(c => c.LineaSolicitudId).Columna("lineaSolicitudId", "uniqueidentifier");
        builder.Property(c => c.IdServicioConexo).Columna("idServicioConexo", "nvarchar(64)").HasMaxLength(clsServicioConexoRuta.LargoMaximoId).IsRequired();
        builder.Property(c => c.Nombre).Columna("nombreServicioConexo", "nvarchar(200)").HasMaxLength(clsServicioConexoRuta.LargoMaximoNombre);
        builder.Property(c => c.Precio).Columna("precio", "decimal(18,2)");
        builder.Property(c => c.Cantidad).Columna("cantidad", "decimal(10,2)");
        builder.Property(c => c.CreatedAt).Fecha("createdAt");
        builder.Property(c => c.UpdatedAt).Fecha("updatedAt");

        builder.HasIndex(c => new { c.LineaSolicitudId, c.IdServicioConexo }).IsUnique()
            .HasDatabaseName("uq_cotizacionServiciosConexos_lineaSolicitudId_idServicioConexo");
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

        builder.HasOne<clsCotizacionSolicitud>().WithOne().HasForeignKey<clsArchivoLicitacion>(a => a.CotizacionId)
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
        builder.HasOne<clsCotizacionSolicitud>().WithMany().HasForeignKey(h => h.CotizacionId)
            .HasConstraintName("fk_cotizacionPdfsHistorial_cotizacionId").OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<clsUsuarioWeb>().WithMany().HasForeignKey(h => h.UsuarioId)
            .HasConstraintName("fk_cotizacionPdfsHistorial_usuarioId").OnDelete(DeleteBehavior.NoAction);
    }
}
