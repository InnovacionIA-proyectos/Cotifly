/* ============================================================================
   Cotifly — datos FICTICIOS para probar-respaldo-local.sh (HU-006.7, #31). Solo pruebas locales.
   Cubre lo que puede romper un respaldo con bcp: Unicode y emoji, saltos de línea y tabuladores, NULL, JSON,
   DECIMAL, DATETIMEOFFSET, CHAR con collation BIN2, IDENTITY, llaves foráneas (también compuestas y a sí misma),
   índices únicos filtrados y filas grandes (500 x ~10 KB).
   Se ejecuta con sqlcmd -I (QUOTED_IDENTIFIER ON: índices filtrados) sobre dbTarifario_Nexo_Pruebas con 0001-0010 (o
   01_crear_esquema.sql): desde HU-021 la cotización es CotizacionesSolicitud con sus rutas, camiones y conexos (ADR-019).
   ============================================================================ */
SET NOCOUNT ON;
DECLARE @u UNIQUEIDENTIFIER = NEWID(), @u2 UNIQUEIDENTIFIER = NEWID();
DECLARE @c UNIQUEIDENTIFIER = NEWID(), @c2 UNIQUEIDENTIFIER = NEWID(), @m UNIQUEIDENTIFIER = NEWID(), @r UNIQUEIDENTIFIER = NEWID();
DECLARE @t1 UNIQUEIDENTIFIER = NEWID(), @t2 UNIQUEIDENTIFIER = NEWID(), @familia UNIQUEIDENTIFIER = NEWID();
DECLARE @linea INT = (SELECT MIN(id) FROM dbo_cotifly.MargenesPolitica);

UPDATE dbo_cotifly.Roles SET idRolExterno = 34 WHERE codigo = N'comercial';
UPDATE dbo_cotifly.Roles SET idRolExterno = 35 WHERE codigo = N'gerente_comercial';

INSERT dbo_cotifly.UsuariosWeb (id, nombreUsuario, nombreCompleto, email, idUsuarioExterno, ultimaSesion)
VALUES (@u, N'APEREZ', N'Ana Pérez Núñez', N'aperez@example.com', 101, SYSDATETIMEOFFSET()),
       (@u2, N'LGOMEZ', N'Luis Gómez', N'lgomez@example.com', 102, NULL),
       (NEWID(), NULL, N'Usuario sin nombre (índice filtrado)', NULL, NULL, NULL);
INSERT dbo_cotifly.UsuariosRoles (usuarioId, rolId, activo)
VALUES (@u, (SELECT id FROM dbo_cotifly.Roles WHERE codigo = N'comercial'), 1),
       (@u2, (SELECT id FROM dbo_cotifly.Roles WHERE codigo = N'gerente_comercial'), 1);
INSERT dbo_cotifly.TokensRefresco (id, usuarioId, tokenHash, familiaId, expiraAt, revocadoAt, reemplazadoPorId)
VALUES (@t2, @u, REPLICATE('b', 64), @familia, DATEADD(day, 30, SYSDATETIMEOFFSET()), NULL, NULL),
       (@t1, @u, REPLICATE('a', 64), @familia, DATEADD(day, 30, SYSDATETIMEOFFSET()), SYSDATETIMEOFFSET(), @t2);

INSERT dbo_cotifly.CotizacionesSolicitud (id, folio, clienteNombre, nitCliente, origenCliente, precioFinal, moneda, trmAplicada, usuarioId, estadoEnvio, version)
VALUES (@c, N'PCN-900123456-AP-28092026-001', N'Transportes Ñandú S.A.S. — “prueba” 🚚', N'900123456', N'cliente', 1219512.80, N'COP', 0, @u, N'enviada', 1),
       (@c2, N'PCN-AP-28092026-002', NULL, NULL, NULL, NULL, N'USD', 3950.1234, @u, NULL, 2);
INSERT dbo_cotifly.CotizacionLineasSolicitud (id, cotizacionSolicitudId, orden, lineaNegocioId, origenNombre, destinoNombre, costoFletesTotal, costoFinal,
    margenPoliticaSugerido, margenAplicado, origenMargen, metodoCalculo, precioFinalLinea)
VALUES (@r, @c, 1, @linea, N'Bogotá, D.C.', N'Medellín', 1000000.50, 1000000.50, 0.15, 0.18, N'negociado', N'margen', 1219512.80);
INSERT dbo_cotifly.CotizacionVehiculosLineaSolicitud (lineaSolicitudId, tipoVehiculo, carroceria, pesoKg, costoFlete)
VALUES (@r, N'3S3', N'PORTACONTENEDORES', 28500.5, 1000000.50);
INSERT dbo_cotifly.CotizacionServiciosConexos (lineaSolicitudId, idServicioConexo, nombreServicioConexo, precio, cantidad)
VALUES (@r, N'CX-1', N'Cargue — “nocturno” 🌙', 85000, 1);
INSERT dbo_cotifly.MargenAjustes (cotizacionId, lineaSolicitudId, folio, margenPolitica, margenAplicado, diferencia, motivo, usuarioId)
VALUES (@c, @r, N'PCN-900123456-AP-28092026-001', 0.15, 0.18, 0.03, N'Negociado: línea "Nacional"; cliente estratégico', @u);
INSERT dbo_cotifly.Correos (id, cotizacionId, asunto, estado, confirmadoPor, error) VALUES (@m, @c, N'Cotización PCN-900123456', N'enviado', @u, NULL);
INSERT dbo_cotifly.CorreoDestinatarios (correoId, email, tipo) VALUES (@m, N'cliente@example.com', N'para'), (@m, N'copia@example.com', N'cc');
INSERT dbo_cotifly.Sesiones (spaceId, threadId, estado, usuarioId, estadoAgente) VALUES (N'web/s1', N'web/s1', N'activa', @u, N'{"linea":"Nacional","pasos":[1,2]}');
INSERT dbo_cotifly.EventosConversacion (spaceId, threadId, usuarioId, rol, contenido, herramientas)
VALUES (N'web/s1', N'web/s1', @u, N'user', N'Hola, necesito cotizar
con salto de línea y tabulador	fin', N'[{"n":"obtener_margen"}]');
DECLARE @i INT = 0;
WHILE @i < 500
BEGIN
    INSERT dbo_cotifly.EventosHerramienta (herramienta, input, outputResumen, exito, duracionMs)
    VALUES (N'obtener_margen', N'{"i":' + CAST(@i AS nvarchar(10)) + N'}', N'{"t":"' + REPLICATE(CAST(N'x' AS nvarchar(max)), 5000) + N'"}', @i % 2, @i);
    SET @i += 1;
END
INSERT dbo_cotifly.TokensRevocados (jti, usuarioId, expiraAt) VALUES (N'jti-prueba', @u, DATEADD(day, 1, SYSDATETIMEOFFSET()));
