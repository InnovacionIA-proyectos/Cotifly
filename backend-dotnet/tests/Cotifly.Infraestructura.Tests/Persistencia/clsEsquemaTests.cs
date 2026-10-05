using Cotifly.Infraestructura.Comun.Persistence;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Scripts <c>db/migrations_mssql</c> aplicados en SQL Server 2019 y verificación de deriva entre el modelo
/// EF (Database First) y el esquema real (ADR-008): tablas, columnas, tipos y nulabilidad.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsEsquemaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    private static readonly string[] _tablasEsperadas =
    [
        "CorreoDestinatarios", "Correos", "CotizacionArchivosLicitacion", "CotizacionLineasSolicitud", "CotizacionPosiciones", "Cotizaciones", "EventosConversacion", "EventosHerramienta",
        "MargenAjustes", "MargenesPolitica", "Roles", "SapMapeoTrqType", "Sesiones",
        "TokensRefresco", "TokensRevocados", "UsuariosRoles", "UsuariosWeb",
    ];

    [Fact]
    public async Task LosScripts0001A0005CreanLasDiecisieteTablas()
    {
        var tablas = await LeerAsync(
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo_cotifly' AND TABLE_TYPE = 'BASE TABLE'",
            r => r.GetString(0));

        tablas.Should().BeEquivalentTo(_tablasEsperadas);
    }

    // Idempotencia (#91): cada prueba re-aplica el script en una base AISLADA (nombre único, 0001 a 0005 aplicados) y no en la
    // compartida. En la compartida, otras pruebas de la colección dejan cotizaciones del formulario ampliado sin línea (columnas de
    // línea en Cotizaciones) y el backfill del 0005 les agrega su línea legítimamente al re-aplicarlo: el conteo global "crecía" sin
    // que el script duplicara nada. En la base aislada nadie más escribe, así que se compara el esquema y el conteo de filas de
    // CADA tabla, y además las filas propias de la prueba.
    [Fact]
    public async Task ElScript0002EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0002", clsBaseDatosPruebas.Scripts.Length);
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
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0004", clsBaseDatosPruebas.Scripts.Length);
        var cadena = baseAislada.Cadena;

        await ReaplicarYVerificarIdempotenciaAsync(cadena, "0004_costeo_nexo.sql");
    }

    [Fact]
    public async Task ElScript0005EsIdempotente()
    {
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0005", clsBaseDatosPruebas.Scripts.Length);
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
        var columnas = await LeerColumnasAsync();

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
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");
        string Linea(string columnas, string valores) =>
            $"INSERT INTO dbo_cotifly.CotizacionLineasSolicitud (cotizacionId, origen, destino, {columnas}) VALUES ('{id}', 'A', 'B', {valores})";

        var orden = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, Linea("orden", "0"));
        var estado = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, Linea("orden, estadoCosteo", "1, 'costeado'"));
        var montos = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, Linea("orden, precioFinal", "1, -1"));
        var fila = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly, Linea("orden, filaOrigen", "1, -1"));
        var archivo = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.CotizacionArchivosLicitacion (cotizacionId, nombreArchivo, tipoContenido, tamanoBytes, sha256, hoja, contenido) VALUES ('{id}', 'a.xlsx', 'x', 5, 'h', 'Hoja', 0x01)");

        (await orden.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_orden");
        (await estado.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_estadoCosteo");
        (await montos.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_montos");
        (await fila.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionLineasSolicitud_filaOrigen");
        (await archivo.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizacionArchivosLicitacion_tamano");
    }

    [Fact]
    public async Task ElScript0007EsIdempotenteYAgregaNotasYMunicipios()
    {
        // Mismo patrón que #91: base AISLADA, filas propias antes de re-aplicar y verificación de que siguen iguales.
        await using var baseAislada = await BaseDatos.CrearBaseConScriptsAsync("dbIdem0007", clsBaseDatosPruebas.Scripts.Length);
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
    public async Task ElCheckDel0007RechazaIdsDeMunicipioNoPositivos()
    {
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var cero = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
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
        var columnas = await LeerColumnasAsync();
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
        columnas.Keys.Count(k => k.Tabla == "Cotizaciones").Should().Be(28 + nuevas.Length + 1, "0001 crea 28 columnas, el 0004 agrega 29 y el 0007 agrega notas");
    }

    [Fact]
    public async Task LosCheckDel0004RechazanTipoDeSolicitudJsonYMedidasInvalidas()
    {
        var id = Guid.NewGuid();
        await clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.Cotizaciones (id, folio) VALUES ('{id}', '{FolioUnico()}')");

        var tipo = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET tipoSolicitud = 'urgente' WHERE id = '{id}'");
        var estado = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET estadoCosteo = 'costeado' WHERE id = '{id}'");
        var json = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.Cotizaciones SET serviciosConexos = 'no es json' WHERE id = '{id}'");
        var cantidad = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"INSERT INTO dbo_cotifly.CotizacionPosiciones (cotizacionId, orden, cantidad, largoCm, anchoCm, altoCm) VALUES ('{id}', 1, 0, 10, 10, 10)");
        var medidas = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
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
        bd.Where(x => x.Contains(":ix_", StringComparison.Ordinal)).Should().HaveCount(12, "el 0002 crea 10 índices ix_, el 0004 uno y el 0005 uno (posiciones por línea)");
        bd.Should().Contain(x => x.StartsWith("Cotizaciones:ix_cotizaciones_costeoSiguienteConsultaAt unico=False", StringComparison.Ordinal)
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
        porColumna[("Cotizaciones", "folio")].Should().Be(clsCotiflyDbContext.Collation);
    }

    [Fact]
    public async Task ElSeedDelScript0001TraeLasOchoLineasYSusMapeos()
    {
        var lineas = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.MargenesPolitica WHERE lineaNegocio IN ('Nacional','Distribucion','Importacion','Exportacion','Internacional','Almacenamiento','Almacenamiento3PL','ITR')");
        var mapeos = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT COUNT(*) FROM dbo_cotifly.SapMapeoTrqType WHERE salesOrg = ''");

        lineas.Should().Be(8);
        mapeos.Should().BeGreaterThanOrEqualTo(8);
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
            await c.Cotizaciones.Take(1).ToListAsync(Ct);
            await c.CotizacionPosiciones.Take(1).ToListAsync(Ct);
            await c.CotizacionLineasSolicitud.Take(1).ToListAsync(Ct);
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
        await clsBaseDatosPruebas.EjecutarScriptAsync(BaseDatos.CadenaCotifly, clsBaseDatosPruebas.RutaScript("0002_identidad_estado_agente.sql"));

        var comercial = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
            "SELECT idRolExterno FROM dbo_cotifly.Roles WHERE codigo = 'comercial'");
        var filas = await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly, "SELECT COUNT(*) FROM dbo_cotifly.Roles");

        comercial.Should().Be(clsBaseDatosPruebas.RolExternoComercial);
        filas.Should().Be(2);
    }

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
            WHERE c.TABLE_SCHEMA = 'dbo_cotifly' AND t.TABLE_TYPE = 'BASE TABLE'
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
