# ADR-027 — Margen de SAP TM desde la copia local (HU-042.2) y mapeo TrqType por id de línea (HU-042.4)

- **Estado:** Propuesta (2026-10-29). Issues #243 (HU-042.2) y #247 (HU-042.4), milestone HU-042. Plan aprobado por el usuario; pendiente
  del visto bueno del PR.
- **Relacionadas:** ADR-002 (puertos y adaptadores), ADR-004 (SAP solo con BP, nunca con el NIT), ADR-008 (Database First), ADR-015 §5
  (margen al enviar a Nexo), HU-003.7 (#13, modos `export_json`/`odata`), HU-009 (#35, #36: SAP en vivo en producción), HU-009 #139
  (margen anómalo), HU-042.1 (#242, la copia local y el job de Datos).
- **Reemplaza en parte:** la decisión de #13 de elegir la fuente por `SapTm:Modo` (export JSON montado u OData en vivo).

## Contexto

Hasta HU-042 el margen recomendado de SAP TM (`RentabClienteSet`) se leía de un export JSON montado en el contenedor o de SAP en vivo por
OData (`SapTm:Modo`). En la práctica ninguno se usaba en producción (modo vacío: todo salía de la política de la línea) y el modo en vivo
exigía red desde el worker de Dokploy hasta SAP, un usuario de servicio y su clave en Dokploy (HU-009.1 y HU-009.2, abiertas).

HU-042.1 (#242, Datos) dejó una copia local en la misma base: `dbo_cotifly.SapMargenCliente` (una fila por organización, TrqType y BP,
`margenRecomendado` en porcentaje, `activo` como borrado lógico, `actualizadoAt`) y `dbo_cotifly.SapMargenSyncLog` (una fila por corrida
del job, `status` `ok`/`error`/`omitido`). La llena un SQL Server Agent Job diario (`db/jobs/sincronizar_margen_sap.ps1`) que corre en el
servidor de base de datos con sus propias credenciales de SAP. Las pruebas del job mostraron 1.276 filas.

Por otro lado, `SapMapeoTrqType.lineaNegocio` era texto libre que debía coincidir con `MargenesPolitica.lineaNegocio`, sin FK. El script
`18` de Datos agregó `lineaNegocioId INT NOT NULL` con FK a `MargenesPolitica(id)` (el mismo id de `CotizacionLineasSolicitud`).

## Decisión

1. **Una sola fuente: la copia local.** `clsFuenteMargenTablaLocal` (Infraestructura) implementa `IClsFuenteMargen` leyendo
   `SapMargenCliente` con el `DbContext` de la petición (scoped, `AsNoTracking`). Cotifly ya no llama a SAP al cotizar. Se retiran
   `clsFuenteMargenOData`, `clsFuenteMargenExportJson`, `clsFormatoRentabSap`, `clsFabricaFuenteMargen`, `clsFuenteMargenNoConfigurada`,
   `clsOpcionesSapTm` con su validador, el cliente HTTP con su tubería de resiliencia y las pruebas de esos modos.
2. **El puerto devuelve un resultado, no un número.** `ConsultarAsync` responde `clsConsultaMargenSap`: `NoConfigurada`, `SinRegistro` o
   `ConValor` con el porcentaje tal como viene de SAP, la fecha de la fila (`actualizadoAt`) y la de la última sincronización `ok`. El
   resolver sigue dividiendo por 100 una sola vez y sigue marcando el anómalo fuera de [0, 60 %].
3. **"No configurado" = nunca sincronizada.** Si `SapMargenSyncLog` no tiene ninguna corrida `ok`, la fuente responde `NoConfigurada` sin
   leer la tabla y el resolver usa la política con el motivo "SAP TM no configurado" (local y QA sin el job siguen funcionando igual que con
   `SapTm:Modo` vacío). Un error de base (conexión, tiempo agotado, tabla ausente) se traduce a `clsFuenteMargenNoDisponibleException`:
   política con "SAP TM no disponible" y un warning, como antes con SAP caído.
4. **Búsqueda.** Solo filas `activo = 1` por (organización, TrqType, BP), con organización y TrqType recortados y en mayúsculas. El BP se
   compara **sin ceros a la izquierda en los dos lados**: SAP hoy lo manda sin ceros (`580`) y el catálogo de clientes puede traerlo con
   ellos (`0000000580`). La consulta usa el índice único `(salesOrgId, trqType, bpSap)` por sus dos primeras columnas y filtra por el
   final del BP; las candidatas se comparan en memoria (gana la coincidencia exacta y luego la más corta). Una fila inactiva o ausente es
   "sin registro en SAP TM". Se mantiene ADR-004: sin BP no se busca.
5. **La copia no caduca; se avisa.** El detalle dice `Margen propuesto SAP TM (tabla local, actualizado AAAA-MM-DD) para cliente {bp} en
   {TrqType}: {valor}%` (la fecha es la de la fila, en la zona en que la guardó el job). Si la última corrida `ok` tiene más de
   `SapTm:AvisoSincronizacionHoras` (48 por defecto, entero > 0, validado al arrancar) se agrega ` — sincronización con más de N h
   (última: …): el dato puede estar desactualizado.` y el margen se usa igual. Sin caché en memoria: dos consultas indexadas por
   resolución; el envío a Nexo ya resuelve una vez por línea de negocio, no por ruta.
6. **`origen` sigue siendo `sap_tm`.** El frontend no cambia (decisión del plan; la pregunta abierta de #243 se responde así): lo que
   distingue la copia del vivo es el `detalle`.
7. **Variables retiradas sin romper el arranque.** `SapTm__Modo`, `__Url`, `__User`, `__Pass`, `__SapClient` y `__ExportPath` ya no se
   enlazan. Si un `.env` viejo las trae con valor, la API arranca y `clsAvisosArranqueSapTm` registra un warning por variable ("variable
   obsoleta, se ignora") con el nombre y nunca el valor. Quedan `SapTm__SalesOrgDefault` y la nueva `SapTm__AvisoSincronizacionHoras`.
8. **Mapeo por id (HU-042.4).** `clsSapMapeoTrqType.LineaNegocioId` mapeado con su FK; `TrqTypeDeAsync(lineaNegocioId, salesOrg)` y
   `LineaDeAsync` (que devuelve el nombre de `MargenesPolitica` cruzando por el id) mantienen el comodín
   `salesOrg IN (@org, '') ORDER BY salesOrg DESC`. El resolver busca la política por nombre (sin tildes ni mayúsculas, como antes) y pasa
   `politica.Id`. **Sin política no hay TrqType**: ya no existe el caso "margen de SAP para una línea sin política" del Python, y el
   resolver deja de devolver `MARGEN_FUENTE_NO_DISPONIBLE` (una línea desconocida es `MARGEN_LINEA_DESCONOCIDA`). La columna de texto
   sigue en la tabla (se escribe al crear un mapeo porque es `NOT NULL`) hasta que Datos la retire.
9. **Esquema y readiness.** EF mapea `SapMargenCliente`, `SapMargenSyncLog` (con el CHECK del estado y sus índices) y `lineaNegocioId`:
   sin el `17` o el `18`, `/health/ready` responde "falta 17" o "falta 18". Las columnas de texto del `17` no fijan `COLLATE` y heredan la
   de la base; el modelo no les asigna collation (`TablasConCollationDeLaBase`). El seeder `02` llena `lineaNegocioId` cuando el `18` ya
   está aplicado (antes, una instalación desde cero fallaba por el `NOT NULL`).

## Alternativas consideradas

- **Mantener `odata` como respaldo de la copia.** Descartada: duplica la integración, mantiene la necesidad de red y credenciales en el
  worker y nadie la usaba. Si la copia falla, el respaldo es la política (como con SAP caído).
- **Caché en memoria de la tabla.** Descartada por ahora: 1.276 filas, consulta por índice único y una resolución por línea de negocio al
  enviar; una caché agrega invalidación sin beneficio medible.
- **Caducar la copia vieja (volver a la política).** Descartada por el usuario: un margen de SAP de hace 3 días es mejor que la política;
  basta con avisarlo.
- **Un `origen` nuevo (`sap_tm_local`).** Descartada: obligaba a tocar el frontend sin aportar al comercial más que el detalle.
- **Normalizar el BP con una columna calculada o en SQL.** Descartada: tocaba el esquema de Datos; el filtro por final del BP sobre el
  rango del índice basta.

## Consecuencias

- Cotifly no necesita red hacia SAP; la necesita el servidor SQL donde corre el job de Datos (`docs/03-infraestructura.md`).
- **HU-009.1 y HU-009.2 (#35, #36) quedan obsoletas** para Cotifly: la ruta de red y el usuario de servicio de SAP son del job de Datos.
  No se editan sus issues (decisión del usuario); queda anotado aquí y en el reporte del PR para que se cierren o reorienten en su
  milestone.
- Un cliente con varias filas en el mismo TrqType recibe la suya (antes el modo `odata` tomaba `results[0]`).
- La regla de anómalos de #139 no cambia: el envío a Nexo aplica la política y guarda el de SAP como marca.
- Pruebas: integración con la copia sembrada en bases aisladas (la compartida nunca tiene una corrida `ok`), esquema, readiness, BDD
  (`margen.feature`) y API (#139 con la fuente real).

## Pendientes

| Pendiente | Dueño |
|---|---|
| Aplicar `17` y `18` en cada base antes de la imagen (ya aplicados en la compartida de pruebas el 2026-10-28) | Datos (@scalderon-bs) |
| Instalar el job en el servidor SQL de producción cuando exista | Datos |
| Script que retira `SapMapeoTrqType.lineaNegocio` (texto) después de desplegar esta imagen | Datos, avisado por Backend |
| Borrar de Dokploy las variables `SapTm__*` retiradas | Backend / Infraestructura |
| Cerrar o reorientar HU-009.1/HU-009.2 en su milestone | Usuario |
