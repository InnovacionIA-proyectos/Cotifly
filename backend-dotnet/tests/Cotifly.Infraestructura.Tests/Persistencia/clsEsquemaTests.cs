using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Scripts <c>db/migrations_mssql</c> aplicados en SQL Server 2019 y verificación de deriva entre el modelo
/// EF (Database First) y el esquema real (ADR-008): tablas, columnas, tipos y nulabilidad. Desde HU-021 la base compartida tiene
/// 0001 a 0010; las pruebas de los scripts anteriores (0002 a 0008) corren en una base AISLADA con el esquema viejo
/// (<see cref="clsBaseDatosPruebas.ScriptsAntesDeHu021"/>), y <c>db/scripts/01_crear_esquema.sql</c> debe dejar el mismo esquema que
/// la cadena de migraciones.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsEsquemaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static readonly string[] _tablasEsperadas =
    [
        "CorreoDestinatarios", "Correos", "CotizacionArchivosLicitacion", "CotizacionLineasSolicitud", "CotizacionMercancias", "CotizacionPdfsHistorial",
        "CotizacionServiciosConexos", "CotizacionVehiculosLineaSolicitud", "CotizacionesSolicitud", "EventosConversacion", "EventosHerramienta",
        "MargenAjustes", "MargenesPolitica", "Roles", "SapMapeoTrqType", "Sesiones",
        "TokensRefresco", "TokensRevocados", "UsuariosRoles", "UsuariosWeb",
    ];

    /// <summary>Filtro de las tablas de respaldo que deja el 0009 (<c>Respaldo0009_*</c>): no son del modelo.</summary>
    private const string SinRespaldos = "TABLE_NAME NOT LIKE 'Respaldo0009[_]%'";

    [Fact]
    public async Task LosScripts0001A0010CreanLasVeinteTablasMasLosRespaldosDel0009()
    {
        var tablas = await LeerAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'",
            r => r.GetString(0));

        tablas.Where(t => !t.StartsWith("Respaldo0009_", StringComparison.Ordinal)).Should().BeEquivalentTo(_tablasEsperadas);
        tablas.Should().Contain("Respaldo0009_Cotizaciones", "el 0009 respalda las tablas que modifica");
    }

    [Fact]
    public async Task LaInstalacionDesdeCeroDejaElMismoEsquemaQueLasMigraciones()
    {
        // HU-021: db/scripts/01_crear_esquema.sql (instalación de la base compartida) y 0001 → 0010 deben coincidir en columnas (tipo,
        // largo, nulabilidad, collation y default), PK, UQ, FK, CHECK (con su definición), defaults e índices. El orden de las columnas
        // puede variar (las migraciones agregan con ALTER) y los respaldos del 0009 solo existen en la base migrada.
        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbInstalada");

        var migrada = (await FotoEsquemaAsync(BaseDatos.CadenaCotifly)).Where(l => !l.StartsWith("Respaldo0009_", StringComparison.Ordinal)).ToList();
        var desdeCero = await FotoEsquemaAsync(instalada.Cadena);

        desdeCero.Except(migrada).Should().BeEmpty("lo que trae la instalación desde cero debe estar en la base migrada");
        migrada.Except(desdeCero).Should().BeEmpty("lo que deja la cadena de migraciones debe estar en la instalación desde cero");
        (await VistasAsync(instalada.Cadena)).Should().BeEmpty("el 01 ya no crea las vistas del borrador de métricas (HU-014.3, #152)");
        (await VistasAsync(BaseDatos.CadenaCotifly)).Should().BeEmpty("el 08 las retira de la base migrada");
    }

    [Fact]
    public async Task ElScript08RetiraLasVistasDelBorradorYEsIdempotente()
    {
        // HU-014.3 (#152): sobre una base migrada que todavía tiene las vistas (0001 a 0010, 02, 04 y 07) las borra; repetirlo, o
        // correrlo sobre una instalación desde cero (que ya no las tiene), no cambia nada.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbRetiro08", clsBaseDatosPruebas.ScriptsHasta07);
        (await VistasAsync(migrada.Cadena)).Should().BeEquivalentTo(["vActividadPorComercial", "vAjustesMargenPorComercial"], "la cadena hasta el 07 las tiene");

        await clsBaseDatosPruebas.AplicarScriptAsync(migrada.Cadena, clsBaseDatosPruebas.Script08);

        (await VistasAsync(migrada.Cadena)).Should().BeEmpty();
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script08);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem08Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script08);
    }

    [Fact]
    public void LosCortesDeLaListaDeScriptsApuntanAlScriptQueNombran()
    {
        // HU-014.3 (#152): al fusionar ramas que agregan scripts a clsBaseDatosPruebas.Scripts (08, 09, ...), git puede unir la lista sin
        // conflicto y dejar una constante de corte apuntando a otro script; las pruebas que la usan siguen pasando sobre una base equivocada.
        var scripts = clsBaseDatosPruebas.Scripts;

        scripts.Should().OnlyHaveUniqueItems();
        scripts[clsBaseDatosPruebas.ScriptsAntesDeHu021 - 1].Should().Be("0008_horas_por_linea.sql");
        scripts[clsBaseDatosPruebas.ScriptsHasta0009 - 1].Should().Be("0009_reestructuracion_cotizaciones.sql");
        scripts[clsBaseDatosPruebas.ScriptsHasta0010 - 1].Should().Be("0010_nombres_municipio.sql");
        scripts[clsBaseDatosPruebas.ScriptsHasta02 - 1].Should().Be(clsBaseDatosPruebas.Script02);
        scripts[clsBaseDatosPruebas.ScriptsHasta04 - 1].Should().Be(clsBaseDatosPruebas.Script04, "ScriptsHasta04 incluye el 04 y no el 09");
        scripts[clsBaseDatosPruebas.ScriptsSin07 - 1].Should().Be(clsBaseDatosPruebas.Script09, "ScriptsSin07 incluye el 09 (el backend mapea scorePrioridad)");
        scripts[clsBaseDatosPruebas.ScriptsSin07].Should().Be(clsBaseDatosPruebas.Script07, "ScriptsSin07 corta justo antes del 07");
        scripts[clsBaseDatosPruebas.ScriptsHasta07 - 1].Should().Be(clsBaseDatosPruebas.Script07, "ScriptsHasta07 incluye el 07");
        scripts[clsBaseDatosPruebas.ScriptsHasta07..].Should().ContainSingle().Which.Should().Be(clsBaseDatosPruebas.Script08, "después del 07 solo va el 08");
    }

    [Fact]
    public async Task ElScript0010EsIdempotenteYNoHaceNadaSobreUnaInstalacionDesdeCero()
    {
        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem0010");

        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, "0010_nombres_municipio.sql");
    }

    [Fact]
    public async Task ElScript02DelCubicadorEsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-023.3 (#158): db/scripts/02_alter_cubicador.sql (de @scalderon-bs) va después del 0010 o del 01 y se puede repetir.
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem02", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script02);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem02Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script02);
        (await clsBaseDatosPruebas.EscalarAsync<int>(instalada.Cadena,
            "SELECT CASE WHEN COL_LENGTH(N'dbo_cotifly.CotizacionMercancias', N'idTipoMercancia') IS NULL "
            + "AND COL_LENGTH(N'dbo_cotifly.CotizacionMercancias', N'codigoMercancia') IS NOT NULL THEN 1 ELSE 0 END"))
            .Should().Be(1, "el 02 cambia idTipoMercancia por codigoMercancia");
    }

    [Fact]
    public async Task ElScript04EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCero()
    {
        // HU-023.5 (#160): la cola de la cubicación en segundo plano (propuesta pendiente 04; el 01 no la trae).
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem04", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script04);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem04Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script04);
    }

    [Fact]
    public async Task HU0181_ElScript09EsIdempotenteSobreUnaBaseMigradaYSobreUnaInstalacionDesdeCeroYDejaDecimal94Null()
    {
        // HU-018.1 (#155): scorePrioridad en la cabecera (db/scripts/09; el 01 no lo trae).
        await using var migrada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem09", clsBaseDatosPruebas.Scripts.Length);
        await ReaplicarYVerificarIdempotenciaAsync(migrada.Cadena, clsBaseDatosPruebas.Script09);

        await using var instalada = await BaseDatos.CrearBaseInstaladaAsync("dbIdem09Inst");
        await ReaplicarYVerificarIdempotenciaAsync(instalada.Cadena, clsBaseDatosPruebas.Script09);
        (await clsBaseDatosPruebas.EscalarAsync<string>(instalada.Cadena,
                "SELECT CONCAT(ty.name, '(', c.precision, ',', c.scale, ')|', c.is_nullable) FROM sys.columns c "
                + "JOIN sys.types ty ON ty.user_type_id = c.user_type_id "
                + "WHERE c.object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud') AND c.name = N'scorePrioridad'"))
            .Should().Be("decimal(9,4)|1");
    }

    [Fact]
    public async Task LosCheckDel04AcotanElEstadoLosIntentosYExigenAvisosEnJson()
    {
        var definiciones = await LeerAsync(
            "SELECT name + '=' + definition FROM sys.check_constraints WHERE name IN (N'ck_cotizacionLineasSolicitud_estadoCubicacion', "
            + "N'ck_cotizacionLineasSolicitud_avisosCubicacion', N'ck_cotizacionLineasSolicitud_cubicacionIntentos')",
            r => r.GetString(0));

        definiciones.Should().HaveCount(3);
        string.Join(" | ", definiciones).Should().Contain("'pendiente'").And.Contain("'fallida'").And.Contain("isjson").And.Contain(">=(0)");
    }

    [Fact]
    public async Task LaPropuesta03PendienteSeAplicaSobreEl02EsIdempotenteYCorrigeElCheckDelMotivo()
    {
        // HU-023.3 (#158): db/scripts/pendientes/03 (pendiente de revisión de @scalderon-bs; el backend no la mapea). En una base aislada.
        const string Script03 = "../scripts_bd/pendientes/03_orden_vehiculo_y_conexo_catalogo.sql";
        await using var baseAislada = await BaseDatos.CrearBaseInstaladaAsync("dbPropuesta03");

        await clsBaseDatosPruebas.AplicarScriptAsync(baseAislada.Cadena, Script03);
        await ReaplicarYVerificarIdempotenciaAsync(baseAislada.Cadena, Script03);

        (await clsBaseDatosPruebas.EscalarAsync<string>(baseAislada.Cadena,
            "SELECT definition FROM sys.check_constraints WHERE name = N'ck_cotizacionLineasSolicitud_motivoSinSeleccion'"))
            .Should().Contain("isnull", "un motivo con resultadoCubicacion NULL ya no pasa el CHECK");
        (await clsBaseDatosPruebas.EscalarAsync<int>(baseAislada.Cadena,
            "SELECT COUNT(*) FROM sys.indexes WHERE name = N'uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo' AND has_filter = 1"))
            .Should().Be(1);
    }

    [Fact]
    public async Task ElScript0009EsIdempotenteSobreUnaBaseMigrada()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0009", clsBaseDatosPruebas.Scripts.Length);

        await ReaplicarYVerificarIdempotenciaAsync(baseAislada.Cadena, "0009_reestructuracion_cotizaciones.sql");
        await ReaplicarYVerificarIdempotenciaAsync(baseAislada.Cadena, "0010_nombres_municipio.sql");
    }

    [Fact]
    public async Task El0009MigraLasCotizacionesViejasARutasYCamionesYEl0010RecuperaLosNombres()
    {
        // HU-021 (#131): una base con el esquema viejo (0001 a 0008) y datos de prueba pasa por el 0009 y el 0010.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbMigra0009", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        Guid formulario = Guid.NewGuid(), chat = Guid.NewGuid();
        var conexos = """[{"id":"CX-1","nombre":"Cargue","categoria":"logistico","cantidad":2,"costo":100000,"precio":117647,"incluido":true},{"id":"CX-2","nombre":"Seguro","categoria":"adicional","cantidad":1,"costo":5000,"precio":5882,"incluido":false}]""";
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, tipoSolicitud, costeoId, estadoCosteo, costo, margenAplicado, origenMargen, metodoCalculo, "
            + "margenPolitica, margenSugerido, origenMargenSugerido, precioFinal, serviciosConexos, horasCargue) VALUES "
            + $"('{formulario}', 'PCN-MIG-1', 3, 'spot', 'SOL-77', 'autocosteo', 1000000, 0.15, 'politica_linea', 'margen', 0.15, 0.15, 'politica_linea', 1176471, N'{conexos}', 4); "
            + "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, costeoId, costo, margenAplicado, origenMargen, metodoCalculo, precioFinal, origen, destino) VALUES "
            + $"('{chat}', 'PCN-MIG-2', 1, 'TARIFA-9', 2000000, 0.15, 'politica_linea', 'margen', 2352941, N'Cali', N'Yumbo'); "
            + "INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, origenMunicipioId, destinoMunicipioId, tipoVehiculo, pesoKg, "
            + "ciudadDevolucionContenedor, valorAprovisionamiento, estadoCosteo, lineaSolicitudNexoId, costo, precioFinal) VALUES "
            + $"('{formulario}', 1, N'Buenaventura', N'Bogotá', 76109, 11001, '3S3', 9000, N'Buenaventura', 150000, 'autocosteo', 'L-1', 1000000, 1176471); "
            + "INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, lineaSolicitudId, orden, descripcion, cantidad, largoCm, anchoCm, altoCm, pesoKg) "
            + $"SELECT '{formulario}', id, 1, N'Café', 20, 120, 100, 150, 450 FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{formulario}';");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0009_reestructuracion_cotizaciones.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0010_nombres_municipio.sql");

        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(l.orden, '|', l.lineaNegocioId, '|', l.origenNombre, '|', l.destinoNombre, '|', l.ciudadDevolucionNombre, '|', l.margenAplicado, '|', "
            + "l.costoFletesTotal, '|', l.costoLevanteContenedorTotal, '|', l.costoServiciosConexos, '|', l.costoFinal, '|', l.precioFinalLinea, '|', l.horasCargue, '|', "
            + "v.tipoVehiculo, '|', v.costoFlete, '|', v.costoLevanteContenedor, '|', v.lineaSolicitudNexoId, '|', c.solicitudNexoId) "
            + "FROM dbo_cotifly.CotizacionesSolicitud c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id "
            + $"JOIN dbo_cotifly.CotizacionVehiculosLineaSolicitud v ON v.lineaSolicitudId = l.id WHERE c.id = '{formulario}'"))
            .Should().Be("1|3|Buenaventura|Bogotá|Buenaventura|0.1500|850000.00|150000.00|100000.00|1100000.00|1176471.00|4.00|3S3|850000.00|150000.00|L-1|SOL-77",
                "el costo viejo se reparte en flete y levante, el conexo incluido pasa a la ruta y el 0010 recupera nombres e id de la solicitud");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(COUNT(*), '|', MAX(s.nombreServicioConexo), '|', MAX(s.precio), '|', MAX(s.cantidad)) FROM dbo_cotifly.CotizacionServiciosConexos s "
            + $"JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = s.lineaSolicitudId WHERE l.cotizacionSolicitudId = '{formulario}'"))
            .Should().Be("1|Cargue|50000.00|2.00", "solo el incluido; precio unitario = costo ÷ cantidad");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(l.orden, '|', l.origenNombre, '|', l.destinoNombre, '|', v.costoFlete, '|', ISNULL(c.solicitudNexoId, '-')) FROM dbo_cotifly.CotizacionesSolicitud c "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionSolicitudId = c.id "
            + $"JOIN dbo_cotifly.CotizacionVehiculosLineaSolicitud v ON v.lineaSolicitudId = l.id WHERE c.id = '{chat}'"))
            .Should().Be("1|Cali|Yumbo|2000000.00|-", "la cotización del chat recibe su ruta y su camión, y el 0010 le devuelve la ubicación (hueco 1 del 0009)");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(m.nombreMercancia, '|', m.orden) FROM dbo_cotifly.CotizacionMercancias m JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = m.lineaSolicitudId "
            + $"WHERE l.cotizacionSolicitudId = '{formulario}'"))
            .Should().Be("Café|1");
    }

    [Fact]
    public async Task El0009ConservaAjustesCorreosYAuditoriaYRespaldaLaCotizacionDeItrQueBorra()
    {
        // HU-021.12 (#139): migración de datos de las tablas que cuelgan de la cotización (MargenAjustes, Correos, CorreoDestinatarios) y de
        // la auditoría; la cotización de prueba de ITR se borra con todo lo suyo y queda en los Respaldo0009_*.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbMigraDatos0009", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        Guid usuario = Guid.NewGuid(), cotizacion = Guid.NewGuid(), itr = Guid.NewGuid(), correo = Guid.NewGuid(), correoItr = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.UsuariosWeb (id, nombreUsuario) VALUES ('{usuario}', N'mig0009'); "
            + "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, costeoId, costo, margenAplicado, origenMargen, metodoCalculo, precioFinal, origen, destino, usuarioId, estadoEnvio) "
            + $"SELECT '{cotizacion}', 'PCN-MIG-AJ', id, 'SOL-1', 1000000, 0.20, 'negociado', 'margen', 1250000, N'Cali', N'Yumbo', '{usuario}', 'enviada' "
            + "FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = 'Importacion'; "
            + "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, lineaNegocioId, costo, margenAplicado, origenMargen, metodoCalculo, precioFinal, usuarioId) "
            + $"SELECT '{itr}', 'PCN-MIG-ITR', id, 500000, 0.15, 'politica_linea', 'margen', 588235, '{usuario}' FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio = 'ITR'; "
            + "INSERT INTO dbo_cotifly.MargenAjustes (cotizacionId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId) VALUES "
            + $"('{cotizacion}', 'PCN-MIG-AJ', 0.15, 0.20, 0.05, N'Cliente estratégico', '{usuario}'), ('{itr}', 'PCN-MIG-ITR', 0.15, 0.10, -0.05, N'ITR', '{usuario}'); "
            + "INSERT INTO dbo_cotifly.Correos (id, cotizacionId, asunto, estado, confirmadoPor) VALUES "
            + $"('{correo}', '{cotizacion}', N'Cotización PCN-MIG-AJ', 'enviado', '{usuario}'), ('{correoItr}', '{itr}', N'ITR', 'enviado', '{usuario}'); "
            + $"INSERT INTO dbo_cotifly.CorreoDestinatarios (correoId, email, tipo) VALUES ('{correo}', N'compras@cliente.com', 'para'), ('{correo}', N'jefe@cliente.com', 'cc'), "
            + $"('{correoItr}', N'itr@cliente.com', 'para'); "
            + "INSERT INTO dbo_cotifly.EventosHerramienta (herramienta, input, exito) VALUES (N'enviar_correo', N'{\"folio\":\"PCN-MIG-AJ\"}', 1);");
        var eventosAntes = await clsBaseDatosPruebas.EscalarAsync<int>(cadena, "SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0009_reestructuracion_cotizaciones.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0010_nombres_municipio.sql");

        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(a.folio, '|', a.margenAplicado, '|', a.diferencia, '|', a.motivo, '|', l.orden, '|', m.lineaNegocio) FROM dbo_cotifly.MargenAjustes a "
            + "JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = a.lineaSolicitudId JOIN dbo_cotifly.MargenesPolitica m ON m.id = l.lineaNegocioId "
            + $"WHERE a.cotizacionId = '{cotizacion}'"))
            .Should().Be("PCN-MIG-AJ|0.2000|0.0500|Cliente estratégico|1|Importacion", "el ajuste queda en la ruta 1, que hereda la línea de negocio de la cabecera");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(c.estado, '|', c.confirmadoPor, '|', (SELECT STRING_AGG(CONCAT(d.tipo, '=', d.email), ',') WITHIN GROUP (ORDER BY d.tipo DESC) "
            + $"FROM dbo_cotifly.CorreoDestinatarios d WHERE d.correoId = c.id)) FROM dbo_cotifly.Correos c WHERE c.cotizacionId = '{cotizacion}'"))
            .Should().Be($"enviado|{usuario.ToString().ToUpperInvariant()}|para=compras@cliente.com,cc=jefe@cliente.com");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT STRING_AGG(OBJECT_NAME(fk.parent_object_id), ',') WITHIN GROUP (ORDER BY OBJECT_NAME(fk.parent_object_id)) FROM sys.foreign_keys fk "
            + "WHERE fk.referenced_object_id = OBJECT_ID(N'dbo_cotifly.CotizacionesSolicitud')"))
            .Should().Be("Correos,CotizacionArchivosLicitacion,CotizacionLineasSolicitud,CotizacionPdfsHistorial,MargenAjustes",
                "las FK de las tablas hijas siguen a la tabla renombrada");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT(e.estadoEnvio, '|', e.precioFinal) FROM dbo_cotifly.CotizacionesSolicitud e WHERE e.folio = 'PCN-MIG-AJ'")).Should().Be("enviada|1250000.00");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            "SELECT CONCAT((SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE folio = 'PCN-MIG-ITR'), '|', "
            + $"(SELECT COUNT(*) FROM dbo_cotifly.Correos WHERE id = '{correoItr}'), '|', (SELECT COUNT(*) FROM dbo_cotifly.MargenAjustes WHERE folio = 'PCN-MIG-ITR'), '|', "
            + "(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_Cotizaciones WHERE folio = 'PCN-MIG-ITR'), '|', "
            + $"(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_Correos WHERE id = '{correoItr}'), '|', "
            + $"(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_CorreoDestinatarios WHERE correoId = '{correoItr}'), '|', "
            + "(SELECT COUNT(*) FROM dbo_cotifly.Respaldo0009_MargenAjustes WHERE folio = 'PCN-MIG-ITR'))"))
            .Should().Be("0|0|0|1|1|1|1", "la cotización de ITR se borra con lo suyo y queda respaldada");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, "SELECT COUNT(*) FROM dbo_cotifly.EventosHerramienta")).Should().Be(eventosAntes, "la auditoría no se toca");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            $"SELECT CONCAT(lineaNegocio, '|', numAjustes) FROM dbo_cotifly.vAjustesMargenPorComercial WHERE usuarioId = '{usuario}'"))
            .Should().Be("Importacion|1", "la vista adaptada lee la línea de negocio de la ruta del ajuste");
    }

    // Idempotencia (#91): cada prueba re-aplica el script en una base AISLADA (nombre único, 0001 a 0005 aplicados) y no en la
    // compartida. En la compartida, otras pruebas de la colección dejan cotizaciones del formulario ampliado sin línea (columnas de
    // línea en Cotizaciones) y el backfill del 0005 les agrega su línea legítimamente al re-aplicarlo: el conteo global "crecía" sin
    // que el script duplicara nada. En la base aislada nadie más escribe, así que se compara el esquema y el conteo de filas de
    // CADA tabla, y además las filas propias de la prueba.
    [Fact]
    public async Task ElScript0002EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0002", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0002_identidad_estado_agente.sql");
    }

    [Fact]
    public async Task LaBaseAisladaSeBorraAlTerminarAunqueQuedenConexionesAbiertas()
    {
        var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbBorrar", 1);
        await using var abierta = new SqlConnection(baseAislada.Cadena);
        await abierta.OpenAsync(Ct);

        await baseAislada.DisposeAsync();

        (await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaMaster, $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{baseAislada.Nombre}'"))
            .Should().Be(0, "la base auxiliar no debe quedar en el SQL Server compartido");
    }

    [Fact]
    public async Task ElScript0004EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0004", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0004_costeo_nexo.sql");
    }

    [Fact]
    public async Task ElScript0005EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0005", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        Guid spot = Guid.NewGuid(), licitacion = Guid.NewGuid(), chat = Guid.NewGuid();
        var propias = $"'{spot}', '{licitacion}', '{chat}'";
        var marca = Guid.NewGuid().ToString("N")[..12];

        // Cotizaciones con el esquema anterior (línea en las columnas de Cotizaciones, sin CotizacionLineasSolicitud): las que el
        // backfill debe convertir. La del chat no cumple el criterio (sin tipoSolicitud/origen/destino) y no recibe línea.
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud, origen, destino, tipoVehiculo, pesoKg, estadoCosteo, costo, precioFinal) VALUES "
            + $"('{spot}', 'PCN-ID-{marca}-1', 'spot', 'YUMBO', 'BOGOTA, D.C.', '3S3', 9000, 'autocosteo', 1000000, 1176471), "
            + $"('{licitacion}', 'PCN-ID-{marca}-2', 'licitacion', 'CALI', 'CARTAGENA', NULL, 500, 'pendiente_costeo', NULL, NULL); "
            + $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, costo, precioFinal) VALUES ('{chat}', 'PCN-ID-{marca}-3', 2000000, 2352941); "
            + "INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm, pesoKg) VALUES "
            + $"('{spot}', 1, 20, 120, 100, 150, 450), ('{spot}', 2, 5, 80, 60, 40, 30), ('{licitacion}', 1, 1, 100, 100, 100, 500);");

        // Primera aplicación: hace el backfill de las filas propias.
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0005_lineas_solicitud.sql");
        var lineasPropias = $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId IN ({propias})";
        var posicionesPropias = $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones WHERE cotizacionId IN ({propias})";
        var posicionesEnlazadas =
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones p JOIN dbo_cotifly.CotizacionLineasSolicitud l "
            + $"ON l.id = p.lineaSolicitudId AND l.cotizacionId = p.cotizacionId AND l.orden = 1 WHERE p.cotizacionId IN ({propias})";
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, lineasPropias)).Should().Be(2, "una línea por cotización del formulario; el chat no tiene");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, posicionesEnlazadas)).Should().Be(3, "las tres posiciones quedan en la línea 1 de su cotización");
        var huellaPropia = await HuellaFilasPropiasAsync(cadena, propias);

        // Segunda aplicación: esquema, filas por tabla y filas propias (con sus enlaces) quedan iguales.
        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0005_lineas_solicitud.sql");

        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, lineasPropias)).Should().Be(2, "re-aplicar el 0005 no duplica el backfill");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, posicionesPropias)).Should().Be(3, "re-aplicar el 0005 no agrega ni quita posiciones");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, posicionesEnlazadas)).Should().Be(3, "re-aplicar no desenlaza las posiciones");
        (await HuellaFilasPropiasAsync(cadena, propias)).Should().Equal(huellaPropia, "re-aplicar el 0005 no modifica las líneas ni los enlaces ya creados");
    }

    [Fact]
    public async Task ElScript0005NoBorraLasColumnasDeLineaDeCotizacionesYEnlazaLasPosiciones()
    {
        await using var viejo = await EsquemaViejoAsync();
        var columnas = await LeerColumnasAsync(viejo.Cadena);

        foreach (var obsoleta in new[] { "origen", "destino", "tipoVehiculo", "pesoKg", "valorMercancia" })
        {
            columnas.Should().ContainKey(("Cotizaciones", obsoleta), "el 0005 no es destructivo: las columnas de línea quedan obsoletas, no se borran");
        }

        columnas[("CotizacionPosiciones", "lineaSolicitudId")].Nula.Should().BeTrue("las posiciones anteriores siguen válidas");
        columnas[("CotizacionLineasSolicitud", "origen")].Nula.Should().BeFalse();
        columnas[("CotizacionArchivosLicitacion", "contenido")].Tipo.Should().Be("varbinary(max)");
    }

    [Fact]
    public async Task LosCheckDel0005RechazanOrdenEstadoMontosYArchivoInvalidos()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");
        string Linea(string columnas, string valores) =>
            $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, origen, destino, {columnas}) VALUES ('{id}', 'A', 'B', {valores})";

        var orden = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden", "0"));
        var estado = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden, estadoCosteo", "1, 'costeado'"));
        var montos = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden, precioFinal", "1, -1"));
        var fila = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, Linea("orden, filaOrigen", "1, -1"));
        var archivo = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionArchivosLicitacion (cotizacionId, nombreArchivo, tipoContenido, tamanoBytes, sha256, hoja, contenido) VALUES ('{id}', 'a.xlsx', 'x', 5, 'h', 'Hoja', 0x01)");

        (await orden.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_orden");
        (await estado.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_estadoCosteo");
        (await montos.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_montos");
        (await fila.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_filaOrigen");
        (await archivo.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionArchivosLicitacion_tamano");
    }

    [Fact]
    public async Task ElScript0006EsIdempotenteYNoTocaLosDatos()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0006", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, pdfPath) VALUES ('{id}', '{FolioUnico()}', 'PCN-X.pdf')");
        var huella = $"SELECT CONCAT(versionNegocio, '|', pdfDesactualizado, '|', pdfPath, '|', ISNULL(origenMargenSugerido, '-')) FROM dbo_cotifly.Cotizaciones WHERE id = '{id}'";
        var antes = await clsBaseDatosPruebas.EscalarAsync<string>(cadena, huella);

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0006_textos_y_concurrencia.sql");

        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, huella)).Should().Be(antes, "re-aplicar el 0006 no cambia las filas");
        antes.Should().Be("0|0|PCN-X.pdf|-", "una fila existente queda en la versión 0 y con su PDF vigente");
    }

    [Fact]
    public async Task ElBackfillDel0006CopiaElMargenSugeridoQueHaya()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbBackfill0006", 5);
        var cadena = baseAislada.Cadena;
        Guid sap = Guid.NewGuid(), negociada = Guid.NewGuid(), chat = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, margenPolitica, margenAplicado, origenMargen) VALUES "
            + $"('{sap}', '{FolioUnico()}', 0.15, 0.21, 'sap_tm'), ('{negociada}', '{FolioUnico()}', 0.15, 0.12, 'negociado'), "
            + $"('{chat}', '{FolioUnico()}', NULL, NULL, NULL)");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0006_textos_y_concurrencia.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0006_textos_y_concurrencia.sql");

        string Fila(Guid id) => $"SELECT CONCAT(ISNULL(CAST(margenSugerido AS nvarchar(20)), '-'), '|', ISNULL(origenMargenSugerido, '-'), '|', margenPolitica) FROM dbo_cotifly.Cotizaciones WHERE id = '{id}'";
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Fila(sap))).Should().Be("0.2100|sap_tm|0.1500");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Fila(negociada))).Should().Be("0.1500|politica_linea|0.1500", "negociada: el sugerido era la política");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena, Fila(chat))).Should().Be("-|-|");
    }

    [Fact]
    public async Task LaReversaDel0006DejaElEsquemaDel0005YSePuedeVolverAAplicar()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbReversa0006", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var con0006 = await FotoEsquemaAsync(cadena);

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "rollback/0006_textos_y_concurrencia_reversa.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "rollback/0006_textos_y_concurrencia_reversa.sql");

        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
                "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo_cotifly.Cotizaciones') AND name IN "
                + "(N'versionNegocio', N'textosCotizacion', N'pdfDesactualizado', N'margenSugerido', N'origenMargenSugerido')"))
            .Should().Be(0);
        (await clsBaseDatosPruebas.EscalarAsync<int?>(cadena, "SELECT OBJECT_ID(N'dbo_cotifly.CotizacionPdfsHistorial', N'U')")).Should().BeNull();
        (await clsBaseDatosPruebas.EscalarAsync<int?>(cadena, "SELECT COL_LENGTH(N'dbo_cotifly.vAjustesMargenPorComercial', N'numAjustesBajoSugerido')")).Should().BeNull();

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0006_textos_y_concurrencia.sql");
        (await FotoEsquemaAsync(cadena)).Should().Equal(con0006, "re-aplicar el 0006 después de la reversa deja el mismo esquema");
    }

    [Fact]
    public async Task LosCheckDel0006RechazanTextosVersionOrigenYMotivosInvalidos()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");
        Func<Task> Sql(string sql) => () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena, sql);

        (await Sql($"UPDATE dbo_cotifly.Cotizaciones SET textosCotizacion = N'no es json' WHERE id = '{id}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_textosCotizacion_json");
        (await Sql($"UPDATE dbo_cotifly.Cotizaciones SET versionNegocio = -1 WHERE id = '{id}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_versionNegocio");
        (await Sql($"UPDATE dbo_cotifly.Cotizaciones SET origenMargenSugerido = 'negociado' WHERE id = '{id}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_origenMargenSugerido");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionPdfsHistorial (cotizacionId, pdfPath, motivo) VALUES ('{id}', 'PCN-X.pdf', 'otro')").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionPdfsHistorial_motivo");
        await Sql($"INSERT INTO dbo_cotifly.CotizacionPdfsHistorial (cotizacionId, pdfPath, motivo) VALUES ('{id}', 'PCN-X.pdf', 're_solicitud')")();
    }

    [Fact]
    public async Task ElScript0007EsIdempotenteYAgregaNotasYMunicipios()
    {
        // Mismo patrón que #91: base AISLADA, filas propias antes de re-aplicar y verificación de que siguen iguales.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0007", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var id = Guid.NewGuid();
        var propias = $"'{id}'";
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud, notas) VALUES ('{id}', 'PCN-ID7-{Guid.NewGuid().ToString("N")[..12]}', 'spot', N'Cita previa'); "
            + "INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, origenMunicipioId, destinoMunicipioId) "
            + $"VALUES ('{id}', 1, 'YUMBO', 'BOGOTA', 76892, 11001);");
        var huella = await HuellaFilasPropiasAsync(cadena, propias);

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0007_captura_nexo_campos.sql");

        (await HuellaFilasPropiasAsync(cadena, propias)).Should().Equal(huella, "re-aplicar el 0007 no cambia las filas existentes");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            $"SELECT CONCAT(c.notas, '|', l.origenMunicipioId, '|', l.destinoMunicipioId) FROM dbo_cotifly.Cotizaciones c JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.cotizacionId = c.id WHERE c.id = '{id}'"))
            .Should().Be("Cita previa|76892|11001");
        var columnas = await LeerColumnasAsync(cadena);
        columnas[("Cotizaciones", "notas")].Should().Be(("nvarchar(2000)", true));
        columnas[("CotizacionLineasSolicitud", "origenMunicipioId")].Should().Be(("int", true));
        columnas[("CotizacionLineasSolicitud", "destinoMunicipioId")].Should().Be(("int", true));
    }

    [Fact]
    public async Task ElScript0008EsIdempotenteYAgregaLasHorasPorLinea()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0008", clsBaseDatosPruebas.ScriptsAntesDeHu021);
        var cadena = baseAislada.Cadena;
        var id = Guid.NewGuid();
        var propias = $"'{id}'";
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud) VALUES ('{id}', 'PCN-ID8-{Guid.NewGuid().ToString("N")[..12]}', 'licitacion'); "
            + "INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, horasCargue, horasDescargue) "
            + $"VALUES ('{id}', 1, 'YUMBO', 'BOGOTA', 4.5, 240);");
        var huella = await HuellaFilasPropiasAsync(cadena, propias);

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0008_horas_por_linea.sql");

        (await HuellaFilasPropiasAsync(cadena, propias)).Should().Equal(huella, "re-aplicar el 0008 no cambia las filas existentes");
        (await clsBaseDatosPruebas.EscalarAsync<string>(cadena,
            $"SELECT CONCAT(horasCargue, '|', horasDescargue) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{id}'"))
            .Should().Be("4.50|240.00");
        var columnas = await LeerColumnasAsync(cadena);
        columnas[("CotizacionLineasSolicitud", "horasCargue")].Should().Be(("decimal(6,2)", true));
        columnas[("CotizacionLineasSolicitud", "horasDescargue")].Should().Be(("decimal(6,2)", true));
    }

    [Fact]
    public async Task ElCheckDel0008RechazaHorasFueraDelRangoDeNexo()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var excedida = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, horasDescargue) VALUES ('{id}', 1, 'A', 'B', 240.01)");

        (await excedida.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_horas");
    }

    [Fact]
    public async Task ElCheckDel0007RechazaIdsDeMunicipioNoPositivos()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var cero = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, orden, origen, destino, origenMunicipioId) VALUES ('{id}', 1, 'A', 'B', 0)");

        (await cero.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_municipios");
    }

    [Fact]
    public async Task ElBackfillDel0005CreaUnaLineaPorCotizacionDelFormularioYEnlazaSusPosiciones()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbBackfill0005", 4);
        var cadena = baseAislada.Cadena;
        Guid conCosteo = Guid.NewGuid(), pendiente = Guid.NewGuid(), chat = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(cadena,
            "INSERT INTO dbo_cotifly.Cotizaciones (id, folio, tipoSolicitud, origen, destino, tipoVehiculo, pesoKg, estadoCosteo, costo, precioFinal) VALUES "
            + $"('{conCosteo}', 'PCN-BF-01', 'spot', 'YUMBO', 'BOGOTA, D.C.', '3S3', 9000, 'autocosteo', 1000000, 1176471), "
            + $"('{pendiente}', 'PCN-BF-02', 'licitacion', 'CALI', 'CARTAGENA', NULL, 500, 'pendiente_costeo', NULL, NULL); "
            + $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio, costo, precioFinal) VALUES ('{chat}', 'PCN-BF-03', 2000000, 2352941); "
            + "INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm, pesoKg) "
            + $"VALUES ('{conCosteo}', 1, 20, 120, 100, 150, 450);");

        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0005_lineas_solicitud.sql");
        await clsBaseDatosPruebas.AplicarScriptAsync(cadena, "0005_lineas_solicitud.sql");

        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena, "SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud"))
            .Should().Be(2, "una línea por cotización del formulario; el chat no tiene línea y re-aplicar no duplica");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{conCosteo}' AND orden = 1 AND origen = 'YUMBO' AND tipoVehiculo = '3S3' AND pesoKg = 9000 AND estadoCosteo = 'autocosteo' AND costo = 1000000 AND precioFinal = 1176471"))
            .Should().Be(1);
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionLineasSolicitud WHERE cotizacionId = '{pendiente}' AND estadoCosteo = 'pendiente_costeo' AND costo IS NULL AND precioFinal IS NULL"))
            .Should().Be(1, "sin costeo completo la línea no hereda costo ni precio");
        (await clsBaseDatosPruebas.EscalarAsync<int>(cadena,
            "SELECT COUNT(*) FROM dbo_cotifly.CotizacionPosiciones p JOIN dbo_cotifly.CotizacionLineasSolicitud l ON l.id = p.lineaSolicitudId AND l.cotizacionId = p.cotizacionId"))
            .Should().Be(1, "la posición queda enlazada a la línea 1 de su cotización");
    }

    [Fact]
    public async Task ElScript0004AgregaVeintinueveColumnasNullablesACotizacionesSalvoLasConsultasFallidas()
    {
        await using var viejo = await EsquemaViejoAsync();
        var columnas = await LeerColumnasAsync(viejo.Cadena);
        string[] nuevas =
        [
            "tipoSolicitud", "deadline", "valorEstimado", "referencia", "horasCargue", "horasDescargue", "origen", "destino",
            "tipoVehiculo", "carroceria", "tipologia", "claseViaje", "claseContenedor", "exclusivo", "consolidado", "retornaContenedor",
            "ciudadDevolucionContenedor", "levanteContenedorDiferente", "ciudadLevanteContenedor", "valorAprovisionamiento", "pesoKg",
            "valorMercancia", "cotizacionNexoId", "motivoEstadoCosteo", "costeoEnviadoAt", "costeoUltimaConsultaAt",
            "costeoSiguienteConsultaAt", "costeoConsultasFallidas", "serviciosConexos",
        ];

        nuevas.Where(c => !columnas.ContainsKey(("Cotizaciones", c))).Should().BeEmpty();
        nuevas.Where(c => c != "costeoConsultasFallidas" && !columnas[("Cotizaciones", c)].Nula).Should().BeEmpty(
            "las cotizaciones existentes (chat con costo manual) no traen estos datos");
        columnas[("Cotizaciones", "costeoConsultasFallidas")].Nula.Should().BeFalse();
        columnas.Keys.Count(k => k.Tabla == "Cotizaciones").Should().Be(28 + nuevas.Length + 5 + 1,
            "0001 crea 28 columnas, el 0004 agrega 29, el 0006 cinco (versionNegocio, textosCotizacion, pdfDesactualizado, margenSugerido y origenMargenSugerido) y el 0007 agrega notas");
    }

    [Fact]
    public async Task LosCheckDel0004RechazanTipoDeSolicitudJsonYMedidasInvalidas()
    {
        await using var viejo = await EsquemaViejoAsync();
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var tipo = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Cotizaciones SET tipoSolicitud = 'urgente' WHERE id = '{id}'");
        var estado = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Cotizaciones SET estadoCosteo = 'costeado' WHERE id = '{id}'");
        var json = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Cotizaciones SET serviciosConexos = 'no es json' WHERE id = '{id}'");
        var cantidad = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm) VALUES ('{id}', 1, 0, 10, 10, 10)");
        var medidas = () => clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm) VALUES ('{id}', 1, 1, 0, 10, 10)");

        (await tipo.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_tipoSolicitud");
        (await estado.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_estadoCosteo");
        (await json.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_serviciosConexos_json");
        (await cantidad.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionPosiciones_cantidad");
        (await medidas.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionPosiciones_medidas");
    }

    [Fact]
    public async Task LasClavesPrimariasTienenLosNombresYColumnasDelScript()
    {
        var bd = await LeerAsync(
            """
            SELECT t.name, kc.name,
                   STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.key_constraints kc
            JOIN sys.tables t ON t.object_id = kc.parent_object_id
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly' AND kc.type = 'PK'
            GROUP BY t.name, kc.name
            """,
            r => $"{r.GetString(0)}:{r.GetString(1)}({r.GetString(2)})");

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().Select(e =>
        {
            var (tabla, objeto) = Tabla(e);
            var pk = e.FindPrimaryKey()!;
            return $"{tabla}:{pk.GetName()}({string.Join(',', pk.Properties.Select(p => p.GetColumnName(objeto)))})";
        });

        ef.Should().BeEquivalentTo(bd);
    }

    [Fact]
    public async Task LasClavesForaneasCoincidenEnNombreColumnasYTablaReferenciada()
    {
        var bd = await LeerAsync(
            """
            SELECT t.name, fk.name, OBJECT_NAME(fk.referenced_object_id),
                   STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly'
            GROUP BY t.name, fk.name, fk.referenced_object_id
            """,
            r => $"{r.GetString(0)}:{r.GetString(1)}({r.GetString(3)})->{r.GetString(2)}");

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().SelectMany(e =>
        {
            var (tabla, objeto) = Tabla(e);
            return e.GetForeignKeys().Select(fk =>
                $"{tabla}:{fk.GetConstraintName()}({string.Join(',', fk.Properties.Select(p => p.GetColumnName(objeto)))})->{fk.PrincipalEntityType.GetTableName()}");
        });

        ef.Should().BeEquivalentTo(bd);
    }

    [Fact]
    public async Task LosIndicesYRestriccionesUnicasCoincidenEnColumnasOrdenIncludeUnicidadYFiltro()
    {
        var bd = await LeerIndicesAsync();

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().SelectMany(e =>
        {
            var (tabla, objeto) = Tabla(e);
            return e.GetIndexes().Select(i =>
            {
                var claves = i.Properties.Select((p, n) =>
                    p.GetColumnName(objeto) + (i.IsDescending is { Count: > 0 } d && d[n] ? " DESC" : string.Empty));
                var incluidas = (i.GetIncludeProperties() ?? [])
                    .Select(nombre => e.FindProperty(nombre)!.GetColumnName(objeto)!)
                    .Order(StringComparer.Ordinal);
                return Indice(tabla, i.GetDatabaseName()!, i.IsUnique, string.Join(',', claves), string.Join(',', incluidas), i.GetFilter());
            });
        });

        ef.Should().BeEquivalentTo(bd);
        bd.Where(x => x.Contains(":ix_", StringComparison.Ordinal)).Should().HaveCount(15,
            "el 0002 crea 10 índices ix_, el 0004 uno, el 0006 uno (historial de PDF), el 0009 quita el de posiciones por línea y agrega el de camiones y el de ajustes por ruta, y la propuesta 04 el de la cola del cubicador");
        bd.Should().Contain(x => x.StartsWith("CotizacionLineasSolicitud:ix_cotizacionLineasSolicitud_cubicacionPendiente unico=False", StringComparison.Ordinal)
            && x.Contains("claves=(cubicacionDisponibleAt,createdAt)", StringComparison.Ordinal)
            && x.EndsWith("filtro=estadocubicacion=n'pendiente'", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("CotizacionesSolicitud:ix_cotizaciones_costeoSiguienteConsultaAt unico=False", StringComparison.Ordinal)
            && x.EndsWith("filtro=costeosiguienteconsultaatisnotnull", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("UsuariosWeb:uq_usuariosWeb_nombreUsuario", StringComparison.Ordinal) && x.EndsWith("filtro=nombreusuarioisnotnull", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("UsuariosRoles:uq_usuariosRoles_usuarioId_activo unico=True", StringComparison.Ordinal) && x.EndsWith("filtro=activo=1", StringComparison.Ordinal));
        bd.Should().Contain(x => x.StartsWith("Roles:uq_roles_idRolExterno unico=True", StringComparison.Ordinal) && x.EndsWith("filtro=idrolexternoisnotnull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LosCheckDelEsquemaEstanDeclaradosEnElModelo()
    {
        var bd = await LeerAsync(
            """
            SELECT t.name, cc.name
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly'
            """,
            r => $"{r.GetString(0)}:{r.GetString(1)}");

        var modelo = await ModeloAsync();
        var ef = modelo.GetEntityTypes().SelectMany(e => e.GetCheckConstraints().Select(c => $"{e.GetTableName()}:{c.Name}"));

        // SQL Server reescribe la expresión (IN -> OR, paréntesis, corchetes): se comparan los nombres.
        ef.Should().BeEquivalentTo(bd);
    }

    [Fact]
    public async Task LaCollationDeCadaColumnaDeTextoCoincideConElModelo()
    {
        var bd = await LeerAsync(
            "SELECT TABLE_NAME, COLUMN_NAME, COLLATION_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo_cotifly' AND COLLATION_NAME IS NOT NULL",
            r => (Clave: (r.GetString(0), r.GetString(1)), Collation: r.GetString(2)));
        var porColumna = bd.ToDictionary(x => x.Clave, x => x.Collation);

        var modelo = await ModeloAsync();
        var diferencias = modelo.GetEntityTypes().SelectMany(e =>
        {
            var (tabla, objeto) = Tabla(e);
            return e.GetProperties()
                .Where(p => porColumna.ContainsKey((tabla, p.GetColumnName(objeto)!)) || p.GetCollation() is not null)
                .Select(p => (Columna: $"{tabla}.{p.GetColumnName(objeto)}", Ef: p.GetCollation(),
                    Bd: porColumna.GetValueOrDefault((tabla, p.GetColumnName(objeto)!))))
                .Where(x => x.Ef != x.Bd)
                .Select(x => $"{x.Columna}: EF '{x.Ef}' vs base '{x.Bd}'");
        });

        diferencias.Should().BeEmpty();
        porColumna[("TokensRefresco", "tokenHash")].Should().Be(clsCotiflyDbContext.CollationBinaria);
        porColumna[("CotizacionesSolicitud", "folio")].Should().Be(clsCotiflyDbContext.Collation);
    }

    [Fact]
    public async Task ElSeedDel0001ConElCambioDeCatalogoDel0009DejaLasSeisLineasDeNexo()
    {
        // HU-021 §10: el 0009 retira Almacenamiento, Almacenamiento3PL e ITR (sin equivalente en Nexo) y agrega DevolucionContenedor.
        // Base propia con todas las migraciones: la compartida recibe en paralelo líneas y mapeos temporales de otras pruebas
        // (p. ej. clsMargenRepositoryTests), y aquí se verifica el catálogo exacto que deja el seed (#147, CI inestable).
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbSeedLineas", clsBaseDatosPruebas.Scripts.Length);
        var lineas = await LeerAsync("SELECT lineaNegocio FROM dbo_cotifly.MargenesPolitica", r => r.GetString(0), baseAislada.Cadena);
        var mapeos = await clsBaseDatosPruebas.EscalarAsync<int>(baseAislada.Cadena,
            "SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType WHERE salesOrg = ''");

        lineas.Should().BeEquivalentTo(["Nacional", "Distribucion", "Importacion", "Exportacion", "Internacional", "DevolucionContenedor"]);
        mapeos.Should().Be(5, "los mapeos de las tres líneas retiradas se borran; el de DevolucionContenedor queda pendiente del código de SAP");
    }

    [Fact]
    public async Task CadaEntidadDelModeloEstaMapeadaATablaYColumnasExistentesConTiposCompatibles()
    {
        var columnasBd = await LeerColumnasAsync();
        var diferencias = new List<string>();

        var modelo = await ConsultarAsync(s => Task.FromResult(Contexto(s).Model));
        foreach (var entidad in modelo.GetEntityTypes())
        {
            var tabla = entidad.GetTableName()!;
            entidad.GetSchema().Should().Be(clsCotiflyDbContext.Esquema, $"{entidad.ClrType.Name} debe vivir en dbo_cotifly");
            var objeto = StoreObjectIdentifier.Table(tabla, clsCotiflyDbContext.Esquema);

            foreach (var propiedad in entidad.GetProperties())
            {
                var columna = propiedad.GetColumnName(objeto)!;
                if (!columnasBd.TryGetValue((tabla, columna), out var real))
                {
                    diferencias.Add($"{tabla}.{columna}: mapeada en EF pero no existe en la base");
                    continue;
                }

                var tipoEf = Normalizar(propiedad.GetColumnType());
                if (tipoEf != real.Tipo)
                {
                    diferencias.Add($"{tabla}.{columna}: EF '{tipoEf}' vs base '{real.Tipo}'");
                }

                if (propiedad.IsColumnNullable(objeto) != real.Nula)
                {
                    diferencias.Add($"{tabla}.{columna}: EF nula={propiedad.IsColumnNullable(objeto)} vs base nula={real.Nula}");
                }
            }
        }

        diferencias.Should().BeEmpty("el modelo EF debe coincidir con el esquema de los scripts (Database First)");
    }

    [Fact]
    public async Task CadaTablaYColumnaDelEsquemaEstaMapeadaEnElModelo()
    {
        var columnasBd = await LeerColumnasAsync();
        var modelo = await ConsultarAsync(s => Task.FromResult(Contexto(s).Model));

        var mapeadas = modelo.GetEntityTypes()
            .SelectMany(e =>
            {
                var tabla = e.GetTableName()!;
                var objeto = StoreObjectIdentifier.Table(tabla, clsCotiflyDbContext.Esquema);
                return e.GetProperties().Select(p => (tabla, p.GetColumnName(objeto)!));
            })
            .ToHashSet();

        columnasBd.Keys.Where(k => !mapeadas.Contains(k)).Select(k => $"{k.Tabla}.{k.Columna}")
            .Should().BeEmpty("toda columna de dbo_cotifly debe tener su propiedad en el modelo");
        modelo.GetEntityTypes().Select(e => e.GetTableName()).Should().BeEquivalentTo(_tablasEsperadas);
    }

    [Fact]
    public async Task CadaDbSetSePuedeConsultarContraElEsquemaReal()
    {
        await ConsultarAsync(async s =>
        {
            var c = Contexto(s);
            await c.CotizacionesSolicitud.Include(x => x.Rutas).ThenInclude(r => r.Vehiculos).Take(1).ToListAsync(Ct);
            await c.CotizacionLineasSolicitud.Take(1).ToListAsync(Ct);
            await c.CotizacionVehiculosLineaSolicitud.Take(1).ToListAsync(Ct);
            await c.CotizacionMercancias.Take(1).ToListAsync(Ct);
            await c.CotizacionServiciosConexos.Take(1).ToListAsync(Ct);
            await c.ArchivosLicitacion.Take(1).ToListAsync(Ct);
            await c.MargenAjustes.Take(1).ToListAsync(Ct);
            await c.Correos.Include(x => x.Destinatarios).Take(1).ToListAsync(Ct);
            await c.CorreoDestinatarios.Take(1).ToListAsync(Ct);
            await c.Sesiones.Take(1).ToListAsync(Ct);
            await c.EventosConversacion.Take(1).ToListAsync(Ct);
            await c.EventosHerramienta.Take(1).ToListAsync(Ct);
            await c.UsuariosWeb.Take(1).ToListAsync(Ct);
            await c.MargenesPolitica.Take(1).ToListAsync(Ct);
            await c.SapMapeoTrqType.Take(1).ToListAsync(Ct);
            await c.Roles.Take(1).ToListAsync(Ct);
            await c.UsuariosRoles.Take(1).ToListAsync(Ct);
            await c.TokensRefresco.Take(1).ToListAsync(Ct);
            await c.TokensRevocados.Take(1).ToListAsync(Ct);
            return true;
        });
    }

    [Fact]
    public async Task ElCheckDeEstadoAgenteRechazaJsonInvalido()
    {
        var insertar = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.Sesiones (spaceId, threadId, estado, estadoAgente) VALUES ('s-{Guid.NewGuid():N}', 't', 'activa', 'esto no es json')");

        (await insertar.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_sesiones_estadoAgente_json");
    }

    [Fact]
    public async Task ElCheckDeRolesSoloAceptaLosRolesDeCotifly()
    {
        var insertar = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            "INSERT INTO dbo_cotifly.Roles (codigo) VALUES ('admin')");

        (await insertar.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_roles_codigo");
    }

    [Fact]
    public async Task ReaplicarEl0002NoPisaElRolExternoQueFijoElDba()
    {
        // El 0002 se re-aplica sobre el esquema en que nació (antes de HU-021: crea un índice en la tabla Cotizaciones).
        await using var viejo = await EsquemaViejoAsync();
        await clsBaseDatosPruebas.EjecutarAsync(viejo.Cadena,
            $"UPDATE dbo_cotifly.Roles SET idRolExterno = {clsBaseDatosPruebas.RolExternoComercial} WHERE codigo = 'comercial'");
        await clsBaseDatosPruebas.AplicarScriptAsync(viejo.Cadena, "0002_identidad_estado_agente.sql");

        var comercial = await clsBaseDatosPruebas.EscalarAsync<int>(viejo.Cadena,
            "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'comercial'");
        var filas = await clsBaseDatosPruebas.EscalarAsync<int>(viejo.Cadena, "SELECT COUNT(*) FROM dbo_cotifly.Roles");

        comercial.Should().Be(clsBaseDatosPruebas.RolExternoComercial);
        filas.Should().Be(2);
    }

    [Fact]
    public async Task LosCheckDel0009RechazanEstadosMontosYCantidadesInvalidosEnRutasCamionesYConexos()
    {
        var cotizacion = Guid.NewGuid();
        var ruta = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.CotizacionesSolicitud (id, folio) VALUES ('{cotizacion}', '{FolioUnico()}'); "
            + $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (id, cotizacionSolicitudId, orden) VALUES ('{ruta}', '{cotizacion}', 1);");
        Func<Task> Sql(string sql) => () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, sql);

        (await Sql($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET costoFinal = -1 WHERE id = '{ruta}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_costos");
        (await Sql($"UPDATE dbo_cotifly.CotizacionLineasSolicitud SET origenMargen = 'otro' WHERE id = '{ruta}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_origenMargen");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId, estadoCosteo) VALUES ('{ruta}', 'costeado')").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionVehiculosLineaSolicitud_estadoCosteo");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId, costoFlete) VALUES ('{ruta}', -1)").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionVehiculosLineaSolicitud_montos");
        (await Sql($"INSERT INTO dbo_cotifly.CotizacionServiciosConexos (lineaSolicitudId, idServicioConexo, precio, cantidad) VALUES ('{ruta}', 'CX', 1, 0)").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizacionServiciosConexos_montos");
        (await Sql($"UPDATE dbo_cotifly.CotizacionesSolicitud SET estadoEnvio = 'perdida' WHERE id = '{cotizacion}'").Should().ThrowAsync<SqlException>())
            .Which.Message.Should().Contain("ck_cotizaciones_estadoEnvio");
    }

    /// <summary>Base aislada con el esquema anterior a HU-021 (0001 a 0008), para las pruebas de esos scripts.</summary>
    private Task<clsBaseAuxiliarPruebas> EsquemaViejoAsync() =>
        BaseDatos.CrearBaseConScriptsAsync("dbViejo", clsBaseDatosPruebas.ScriptsAntesDeHu021);

    /// <summary>Modelo de diseño: el de ejecución no conserva CHECK, INCLUDE ni otros metadatos solo relacionales.</summary>
    private Task<IModel> ModeloAsync() => ConsultarAsync(s => Task.FromResult(Contexto(s).GetService<IDesignTimeModel>().Model));

    private static (string Tabla, StoreObjectIdentifier Objeto) Tabla(IEntityType entidad)
    {
        var tabla = entidad.GetTableName()!;
        return (tabla, StoreObjectIdentifier.Table(tabla, clsCotiflyDbContext.Esquema));
    }

    private static string Indice(string tabla, string nombre, bool unico, string claves, string incluidas, string? filtro) =>
        $"{tabla}:{nombre} unico={unico} claves=({claves}) include=({incluidas}) filtro={NormalizarFiltro(filtro)}";

    /// <summary>SQL Server guarda <c>([nombreUsuario] IS NOT NULL)</c>; EF, <c>[nombreUsuario] IS NOT NULL</c>.</summary>
    private static string NormalizarFiltro(string? filtro) =>
        new string((filtro ?? string.Empty).Where(c => c is not ('(' or ')' or '[' or ']' or ' ')).ToArray()).ToLowerInvariant();

    /// <summary>Índices y restricciones UNIQUE (no las PK) de dbo_cotifly (en la base compartida o en <paramref name="cadena"/>).</summary>
    private Task<List<string>> LeerIndicesAsync(string? cadena = null) =>
        LeerAsync(
            """
            SELECT t.name, i.name, i.is_unique, i.filter_definition,
                   STRING_AGG(CASE WHEN ic.is_included_column = 0 THEN c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END END, ',')
                       WITHIN GROUP (ORDER BY ic.key_ordinal, c.name),
                   STRING_AGG(CASE WHEN ic.is_included_column = 1 THEN c.name END, ',') WITHIN GROUP (ORDER BY ic.key_ordinal, c.name)
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE SCHEMA_NAME(t.schema_id) = 'dbo_cotifly' AND i.is_primary_key = 0 AND i.name IS NOT NULL
            GROUP BY t.name, i.name, i.is_unique, i.filter_definition
            """,
            r => Indice(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetString(4),
                r.IsDBNull(5) ? string.Empty : r.GetString(5), r.IsDBNull(3) ? null : r.GetString(3)),
            cadena);

    /// <summary>
    /// Descripción ordenada de todo el esquema (columnas con tipo/nulabilidad/collation/default,
    /// claves, FK, índices y CHECK), para comparar antes y después de re-aplicar un script.
    /// </summary>
    private async Task<List<string>> FotoEsquemaAsync(string cadena)
    {
        var columnas = await LeerAsync(
            """
            SELECT CONCAT(TABLE_NAME COLLATE DATABASE_DEFAULT, '.', COLUMN_NAME COLLATE DATABASE_DEFAULT, ' ', DATA_TYPE COLLATE DATABASE_DEFAULT,
                          '(', CHARACTER_MAXIMUM_LENGTH, ',', NUMERIC_PRECISION, ',', NUMERIC_SCALE, ') ', IS_NULLABLE COLLATE DATABASE_DEFAULT, ' ',
                          COLLATION_NAME COLLATE DATABASE_DEFAULT, ' ', COLUMN_DEFAULT COLLATE DATABASE_DEFAULT)
            FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo_cotifly'
            """,
            r => r.GetString(0),
            cadena);
        var restricciones = await LeerAsync(
            """
            SELECT CONCAT(OBJECT_NAME(o.parent_object_id) COLLATE DATABASE_DEFAULT, ':', o.name COLLATE DATABASE_DEFAULT, ' ',
                          o.type_desc COLLATE DATABASE_DEFAULT, ' ', OBJECT_DEFINITION(o.object_id) COLLATE DATABASE_DEFAULT)
            FROM sys.objects o
            WHERE SCHEMA_NAME(o.schema_id) = 'dbo_cotifly' AND o.type IN ('PK', 'UQ', 'F', 'C', 'D')
            """,
            r => r.GetString(0),
            cadena);
        var indices = await LeerIndicesAsync(cadena);

        return [.. columnas.Concat(restricciones).Concat(indices).Order(StringComparer.Ordinal)];
    }

    /// <summary>Vistas de <c>dbo_cotifly</c> en <paramref name="cadena"/>.</summary>
    private Task<List<string>> VistasAsync(string cadena) =>
        LeerAsync("SELECT name FROM sys.views WHERE SCHEMA_NAME(schema_id) = 'dbo_cotifly'", r => r.GetString(0), cadena);

    /// <summary>
    /// Re-aplica <paramref name="script"/> sobre una base aislada y verifica que no cambie el esquema ni el número de filas de
    /// ninguna tabla de dbo_cotifly (la base es de la prueba: nadie más escribe en ella).
    /// </summary>
    private async Task ReaplicarYVerificarIdempotenciaAsync(string cadena, string script)
    {
        var esquemaAntes = await FotoEsquemaAsync(cadena);
        var filasAntes = await FilasPorTablaAsync(cadena);

        var reaplicar = () => clsBaseDatosPruebas.AplicarScriptAsync(cadena, script);

        await reaplicar.Should().NotThrowAsync();
        (await FotoEsquemaAsync(cadena)).Should().Equal(esquemaAntes, $"re-aplicar el {script} no debe cambiar columnas, claves, índices, CHECK ni defaults");
        (await FilasPorTablaAsync(cadena)).Should().Equal(filasAntes, $"re-aplicar el {script} no debe agregar ni quitar filas en ninguna tabla");
    }

    /// <summary>"Tabla=filas" de cada tabla de dbo_cotifly, ordenado (COUNT exacto, no la estimación de sys.partitions).</summary>
    private async Task<List<string>> FilasPorTablaAsync(string cadena)
    {
        var tablas = await LeerAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'",
            r => r.GetString(0),
            cadena);
        var conteo = string.Join(" UNION ALL ", tablas.Select(t => $"SELECT CONCAT('{t}', '=', COUNT_BIG(*)) FROM dbo_cotifly.[{t}]"));

        return [.. (await LeerAsync(conteo, r => r.GetString(0), cadena)).Order(StringComparer.Ordinal)];
    }

    /// <summary>Huella de las líneas y posiciones de las cotizaciones <paramref name="propias"/>: ids, enlaces y datos copiados.</summary>
    private Task<List<string>> HuellaFilasPropiasAsync(string cadena, string propias) =>
        LeerAsync(
            $"""
            SELECT CONCAT('L|', l.cotizacionId, '|', l.id, '|', l.orden, '|', l.origen, '|', l.destino, '|', l.estadoCosteo, '|', l.costo, '|', l.precioFinal)
            FROM dbo_cotifly.CotizacionLineasSolicitud l WHERE l.cotizacionId IN ({propias})
            UNION ALL
            SELECT CONCAT('P|', p.cotizacionId, '|', p.id, '|', p.orden, '|', p.lineaSolicitudId)
            FROM dbo_cotifly.CotizacionPosiciones p WHERE p.cotizacionId IN ({propias})
            ORDER BY 1
            """,
            r => r.GetString(0),
            cadena);

    private async Task<Dictionary<(string Tabla, string Columna), (string Tipo, bool Nula)>> LeerColumnasAsync(string? cadena = null)
    {
        var filas = await LeerAsync(
            """
            SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, c.IS_NULLABLE
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.TABLE_SCHEMA = 'dbo_cotifly' AND t.TABLE_TYPE = 'BASE TABLE' AND c.TABLE_NAME NOT LIKE 'Respaldo0009[_]%'
            """,
            r =>
            {
                var tipo = r.GetString(2);
                int? largo = r.IsDBNull(3) ? null : r.GetInt32(3);
                var tipoCompleto = tipo switch
                {
                    "nvarchar" or "varchar" or "nchar" or "char" or "varbinary" => $"{tipo}({(largo == -1 ? "max" : largo)})",
                    "decimal" or "numeric" => $"{tipo}({r.GetByte(4)},{r.GetInt32(5)})",
                    _ => tipo,
                };
                return (Tabla: r.GetString(0), Columna: r.GetString(1), Tipo: tipoCompleto, Nula: r.GetString(6) == "YES");
            },
            cadena);

        return filas.ToDictionary(f => (f.Tabla, f.Columna), f => (f.Tipo, f.Nula));
    }

    private async Task<List<T>> LeerAsync<T>(string sql, Func<SqlDataReader, T> mapear, string? cadena = null)
    {
        await using var conexion = new SqlConnection(cadena ?? BaseDatos.CadenaCotifly);
        await conexion.OpenAsync(Ct);
        await using var comando = new SqlCommand(sql, conexion);
        await using var lector = await comando.ExecuteReaderAsync(Ct);
        var filas = new List<T>();
        while (await lector.ReadAsync(Ct))
        {
            filas.Add(mapear(lector));
        }

        return filas;
    }

    private static string Normalizar(string? tipo) =>
        (tipo ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
