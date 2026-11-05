# ADR-027 — Margen de SAP TM desde la copia local (HU-042.2) y mapeo TrqType por id de línea (HU-042.4)

- **Estado:** Propuesta (2026-10-29; ampliada el 2026-11-03: la sincronización la hace el backend, §10 a §16). Issues #243 (HU-042.2) y
  #247 (HU-042.4), milestone HU-042. Plan aprobado por el usuario; pendiente del visto bueno del PR.
- **Relacionadas:** ADR-002 (puertos y adaptadores), ADR-004 (SAP solo con BP, nunca con el NIT), ADR-008 (Database First), ADR-015 §5
  (margen al enviar a Nexo), HU-003.7 (#13, modos `export_json`/`odata`), HU-009 (#35, #36: SAP en vivo en producción), HU-009 #139
  (margen anómalo), HU-042.1 (#242, la copia local y el job de Datos).
- **Reemplaza en parte:** la decisión de #13 de elegir la fuente por `SapTm:Modo` (export JSON montado u OData en vivo) y, desde el
  2026-11-03, el SQL Server Agent Job de HU-042.1 (`db/jobs/sincronizar_margen_sap.ps1`): **el job no se instala**; lo reemplaza el worker
  del backend (§10).

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
   enlazan (2026-11-03: `Url`, `User`, `Pass` y `SapClient` vuelven para la sincronización del backend, §11; siguen retiradas `Modo` y
   `ExportPath`). Si un `.env` viejo las trae con valor, la API arranca y `clsAvisosArranqueSapTm` registra un warning por variable ("variable
   obsoleta, se ignora") con el nombre y nunca el valor. Quedan `SapTm__SalesOrgDefault` y la nueva `SapTm__AvisoSincronizacionHoras`.
8. **Mapeo por id (HU-042.4).** `clsSapMapeoTrqType.LineaNegocioId` mapeado con su FK; `TrqTypeDeAsync(lineaNegocioId, salesOrg)` y
   `LineaDeAsync` (que devuelve el nombre de `MargenesPolitica` cruzando por el id) mantienen el comodín
   `salesOrg IN (@org, '') ORDER BY salesOrg DESC`. El resolver busca la política por nombre (sin tildes ni mayúsculas, como antes) y pasa
   `politica.Id`. **Sin política no hay TrqType**: ya no existe el caso "margen de SAP para una línea sin política" del Python, y el
   código `MARGEN_FUENTE_NO_DISPONIBLE` ya no lo emite nadie y se retira del contrato (`ErrorNegocio.codigo` en `openapi.yaml`, la prueba de
   reflexión exige que el enum sea exactamente lo que el código emite); una línea desconocida es `MARGEN_LINEA_DESCONOCIDA`. La columna de texto
   sigue en la tabla (se escribe al crear un mapeo porque es `NOT NULL`) hasta que Datos la retire.
9. **Esquema y readiness.** EF mapea `SapMargenCliente`, `SapMargenSyncLog` (con el CHECK del estado y sus índices) y `lineaNegocioId`:
   sin el `17` o el `18`, `/health/ready` responde "falta 17" o "falta 18". Las columnas de texto del `17` no fijan `COLLATE` y heredan la
   de la base; el modelo no les asigna collation (`TablasConCollationDeLaBase`). El seeder `02` llena `lineaNegocioId` cuando el `18` ya
   está aplicado (antes, una instalación desde cero fallaba por el `NOT NULL`).

## Ampliación (2026-11-03): la sincronización con SAP la hace el backend

Decisión del usuario (comentarios del 2026-11-03 en #243): no se instala el SQL Agent Job de la #242 (`db/jobs/sincronizar_margen_sap.ps1` y
`db/scripts/operacion/instalar_sql_agent_job_margen_sap.sql`). Se quedan las tablas `SapMargenCliente` y `SapMargenSyncLog` y el stored
procedure `sp_sincronizar_margen_sap`; el backend hace lo que hacía el script de PowerShell. **Solo se resuelve el NIT**: el nombre del
cliente queda fuera de alcance (sin columna nueva ni script de Datos).

10. **Worker.** `clsWorkerSincronizacionMargenSap` (`BackgroundService`, mismo molde que `clsWorkerCosteoNexo` y
    `clsLimpiezaIdempotenciaHostedService`: `PeriodicTimer` con `TimeProvider`, un ámbito de DI por ronda). La primera ronda espera 30 s
    (no retrasa el arranque ni `/health/ready`, que no depende de SAP). Cada `SapTm:ChequeoMinutos` (60) revisa la última corrida `ok` y
    sincroniza solo si tiene más de `SapTm:SincronizacionMinutos` (1440) o si nunca la hubo, igual que el script. Ninguna excepción tumba el
    host. Sin `SapTm:Url`, `SapTm:User` o `SapTm:Pass` queda inactivo (una línea Information) y el margen sigue con la copia existente o la
    política; **no falla el arranque**. Una URL o un mandante mal escritos tampoco (corrección del 2026-11-03: el margen no depende de SAP
    al cotizar): el worker queda inactivo con un log Error y la administración responde `SAP_MARGEN_NO_CONFIGURADO`
    (`clsOpcionesSapTmSincronizacion.ErrorConfiguracion`). `SapTm:Url` es la raíz del servicio; un `/RentabClienteSet` final se acepta y se
    quita. En Production, una URL http (red interna) se acepta con un warning.
11. **Una corrida = el script en C#** (`clsSincronizacionMargenSapSql`, singleton): (a) sin forzar, sale si todavía no toca, sin llamar a
    SAP; (b) un solo `GET {Url}/RentabClienteSet?$format=json` (más `&sap-client=NNN`), **sin `$filter`** porque SAP lo ignora y devuelve el
    set completo (~1500 filas); Basic por petición, `Accept-Encoding: gzip` con descompresión automática, sin cookies, sin redirecciones,
    `SapTm:TimeoutSegundos` (120) para cabeceras y cuerpo y `SapTm:MaximoBytes` (20 MB) sobre el cuerpo descomprimido; sin reintentos (el
    chequeo siguiente reintenta); (c) `d.results` se valida **todo o nada** (`clsLectorRentabClienteSap`): campos de texto no vacíos dentro
    del largo de su columna, `Value` texto o número en cultura invariante redondeado a 4 decimales, clave repetida o set vacío ⇒ error;
    organización y TrqType recortados y en mayúsculas, el BP recortado **con sus ceros** (el consumo ya normaliza al buscar, §4); (d) la
    guarda de mínimo (§12); (e) el NIT por BP (§13); (f) `#SapMargenStage` con `COLLATE DATABASE_DEFAULT` en el texto y PK
    `(salesOrgId, trqType, bpSap)`, `SqlBulkCopy` y `EXEC dbo_cotifly.sp_sincronizar_margen_sap` **en la misma conexión** (la temporal es de
    la sesión). Con la collation real de la base (`Latin1_General_100_CI_AS_SC_UTF8`, distinta de la del servidor) una temporal sin
    `DATABASE_DEFAULT` choca en el `MERGE`: las pruebas usan una base con esa collation. **La PK de la temporal va sin nombre**: en
    `tempdb` el nombre de una restricción es global, y con nombre la temporal de una segunda réplica fallaba con el error 2714 antes del
    `sp_getapplock` (en vez de quedar `omitido`). Un corte a mitad del cuerpo o un gzip corrupto también son `error` ("SAP TM cortó la
    respuesta…").
12. **Guarda de mínimo de filas.** Si SAP trae menos de `max(SapTm:MinimoFilas = 100, ceil(SapTm:MinimoPorcentajeActivas = 80 % × filas
    activas actuales))`, no se llama al stored procedure (que desactivaría lo que falta) y queda `error` con las filas recibidas y el mínimo
    exigido. Así un set parcial de SAP no apaga al resto de clientes.
13. **NIT por BP.** `ConnectionStrings:DwOperaciones` (opcional, secreto, usuario de solo lectura) a `DWCubosOperacionesEBS`,
    `dbo.DimBPSolicitante` (`BP`, `NIT`); es otra base que la de `ConnectionStrings:Regional`. `SELECT` parametrizado en lotes de 500 BP, el
    BP tal como lo manda SAP (como el script), con parámetros `VARCHAR(20)` como el cubo. **Sin la cadena, o sin ese BP en el DW, la fila
    conserva el NIT que ya tenía la copia** (se lee de `SapMargenCliente` por clave antes de cargar la temporal, sin tocar el stored
    procedure, cuyo `MERGE` lo pisaría con `NULL`); si no tenía, queda `NULL`. Un warning por corrida sin la cadena. **Configurada y
    caída, la corrida queda `error` y la tabla no se toca**, igual que el script. Un NIT vacío o de más de 20 caracteres se descarta; un BP
    con varios NIT distintos toma el menor en orden ordinal **de texto** (no numérico: `"1000"` va antes que `"900"`; determinista) y
    deja un warning. **Decisión aceptada (2026-11-04): el NIT se conserva siempre** que el cubo no traiga uno válido para el BP, esté o no
    configurado el cubo; por eso el NIT de un BP que desapareció del cubo puede quedar desactualizado. **Pendiente de confirmar con Datos:** SAP
    manda el BP sin ceros a la izquierda; si `DimBPSolicitante.BP` los guarda con ceros, no cruza (no se consultó el DW real).
14. **Registro.** Un fallo antes del stored procedure (SAP, validación, guarda, DW, staging) se inserta en `SapMargenSyncLog` como `error`
    con `iniciadoAt`, `finalizadoAt` y `mensajeError` (máx. 2000) **sin la URL, el usuario ni la clave** (el detalle técnico queda en Debug;
    de un error de la base solo se guarda el número de SQL Server). El stored procedure registra lo suyo (`ok`, `omitido` o su `error`); si
    no dejó su fila (sin `EXECUTE`, error 229; sin el objeto, 2812; un error de recompilación como el 207 que su `TRY` no atrapa; tiempo
    agotado o `KILL`) el backend registra el `error`. El resultado de la corrida es la fila que dejó el stored procedure: la **primera**
    iniciada entre la hora del servidor tomada antes del `EXEC` (en la misma conexión) y la hora al consultar. El stored procedure fija su
    `iniciadoAt` al empezar, así que una réplica que llegó después y quedó `omitido` no se confunde con la propia. Si la base de Cotifly no
    responde (al leer el estado o la última corrida, o al registrar el fallo), se lanza `clsSincronizacionMargenSapNoDisponibleException`:
    la administración responde 503 `SAP_MARGEN_NO_DISPONIBLE` y el worker solo lo anota. Un log Information por
    corrida con el estado, la duración y las filas; nunca credenciales (los logs de `IHttpClientFactory` muestran la URL, sin credenciales,
    y ocultan las cabeceras).
15. **Varias réplicas.** Cada réplica tiene su worker y un candado en memoria (`clsCoordinacionSincronizacionMargenSap`) que comparten el
    worker y la corrida forzada. Entre réplicas, el `sp_getapplock` del stored procedure hace que la segunda corrida simultánea quede
    `omitido`, y la corrida `ok` de una hace que a las demás ya no les toque en su chequeo. En el peor caso dos réplicas descargan el set a
    la vez (dos GET a SAP en el mismo chequeo); se acepta.
16. **Administración.** `GET /web/admin/sap-margen/estado` (última corrida, última `ok`, filas activas, próxima corrida estimada, intervalo,
    chequeo, si hay una en curso y si el NIT está configurado) y `POST /web/admin/sap-margen/sincronizar` (fuerza una corrida: ignora el
    intervalo, no el candado). Solo gerencia (`gerente_comercial` y `director`), como el resto de `/web/admin/*`: Cotifly no tiene un rol
    "administrador". **El POST es síncrono y responde 200 con el resultado** (`ok`, `error` u `omitido`): son segundos con ~1500 filas y la
    gerencia ve en la misma respuesta si SAP rechazó las credenciales o el mínimo frenó la carga; corre con el token de apagado de la API, no
    con el de la petición, para que una desconexión del cliente no deje la corrida a medias. Queda en la auditoría
    (`admin_sincronizar_margen_sap`). Códigos nuevos: `SAP_MARGEN_NO_CONFIGURADO` (503, los dos endpoints sin `Url`/`User`/`Pass` o con la
    URL o el mandante mal escritos), `SAP_MARGEN_SINCRONIZACION_EN_CURSO` (409, esta réplica ya sincroniza) y `SAP_MARGEN_NO_DISPONIBLE`
    (503, la base de Cotifly no respondió). El POST en la práctica tarda segundos; el máximo con los límites actuales es de unos 10 min (120 s de SAP, la conexión y 60 s por lote de 500 BP en el DW, 120 s para leer los NIT existentes, 120 s de la carga y 120 s del stored procedure); si el cliente o el proxy cortan antes, la corrida
    sigue y su resultado se ve en `GET .../estado`.

## Ajustes del 2026-11-04

17. **Interruptor `SapTm:SincronizacionHabilitada`** (`false` por defecto). Apagado, el worker no corre aunque haya credenciales (log
    Information; así unas `SapTm__Url/User/Pass` que quedaron del modo `odata` viejo no lo activan solas), `GET .../estado` responde 200 con
    `habilitada = false` y `POST .../sincronizar` responde 503 `SAP_MARGEN_NO_CONFIGURADO` con un mensaje que dice que está deshabilitada
    (se reusa el código: para el front es el mismo caso, "no se puede sincronizar en este ambiente", y el `mensaje` distingue). Encendido con
    la configuración incompleta o mal escrita, como antes, ahora con el motivo en `detalle.motivo` (nunca el valor). La lectura del margen
    desde la copia no depende del interruptor.
18. **Aviso de la copia vieja en `aviso_margen`.** El resolver ya ponía "sincronización con más de 48 h" en el `detalle`, que el envío a
    Nexo descartaba. Ahora: (a) al enviar, un Warning en el log por línea de negocio cuando la copia supera `SapTm:AvisoSincronizacionHoras`,
    con `sap_tm` y también con `politica_linea` por "sin registro" (el cliente puede no tener fila porque la copia está vieja); (b) la vista
    del costeo (`GET .../costeo` y las respuestas de los PUT/POST del costeo) agrega a `aviso_margen` "La copia del margen de SAP TM lleva
    más de N h sin sincronizarse (última: …)…" en las rutas cuyo sugerido salió de SAP (`sap_tm` o anómalo), después del aviso del anómalo
    si lo hay. Se evalúa **al leer** el costeo (`IClsAvisoSincronizacionMargenSap`, una consulta indexada solo si alguna ruta tiene sugerido
    de SAP): la ruta no guarda cuándo se resolvió su margen y no se agregan columnas (sin scripts de Datos). Con `politica_linea` no se
    muestra al comercial (el motivo "sin registro" no se guarda en la ruta y se confundiría con "sin BP" o "sin mapeo"); queda en el log.
19. **Base antes que SAP y respuestas rápidas.** La corrida (también la forzada) lee las filas activas antes de descargar de SAP: con la
    base caída responde 503 `SAP_MARGEN_NO_DISPONIBLE` sin hacer el GET. Las lecturas cortas (estado, última corrida, filas activas) usan
    un límite de conexión de 5 s y de orden de 15 s (la carga y el stored procedure conservan sus límites). En el E2E del 2026-11-04, con la
    base caída la administración respondió el 503 en 20 a 45 s (antes 51 a 66 s): el resto lo suman la autenticación y la auditoría, ver
    los pendientes. Solo la conectividad (red, login, base inaccesible, tiempo agotado, error fatal) es "no disponible"; un error de una
    orden con la base respondiendo en las lecturas previas (sin permiso, 229; sin el objeto, 208 o 2812) es un fallo de la corrida y queda
    como `error`, igual que el `DENY EXECUTE` del stored procedure. Con la sincronización deshabilitada o sin configuración válida,
    `proxima_corrida_estimada` es `null`.
20. **`mensajeError` sin texto de SQL Server.** Un fallo del stored procedure sin fila propia guarda "sp_sincronizar_margen_sap falló sin
    registrar la corrida (SQL N)." (solo el número), como el resto de errores de la base (§14).

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
- **Mantener el SQL Agent Job de Datos (2026-11-03).** Descartada por el usuario: el job exigía PowerShell, credenciales de SAP y del DW en
  el servidor SQL y otra pieza que operar; en el backend la sincronización comparte el despliegue, la configuración (`.env`, Dokploy), los
  logs y la administración (estado y corrida forzada). El script de Datos queda en el repo como referencia (no se toca).
- **POST de la corrida forzada asíncrono (202).** Descartada: la corrida tarda segundos y el resultado (en particular el `error`) es lo que
  la gerencia necesita ver; el estado sigue disponible en `GET .../estado`.
- **Reintentos con la tubería de resiliencia.** Descartada: es una descarga del set completo una vez al día; el chequeo siguiente (60 min)
  ya es el reintento y evita repetir un GET pesado contra SAP.

## Consecuencias

- Al cotizar, Cotifly no llama a SAP. **Desde el 2026-11-03 el worker sí necesita red desde el cluster** hacia
  `sap-tm-prd.boterosoto.local:8000` (SAP TM) y `10.10.40.24:1433` (DW), el usuario técnico de SAP y el de solo lectura del DW en Dokploy
  (`docs/03-infraestructura.md`, `DEPLOY.md`). Sin ellos la API funciona igual con la política.
- **HU-009.1 (#35, conectividad) vuelve a aplicar**, ahora para el worker de sincronización; HU-009.2 (#36) se reorienta al usuario técnico
  y su clave en Dokploy. No se editan sus issues (decisión del usuario); queda anotado aquí y en el reporte.
- Permisos del login `cotifly_app`: `EXECUTE` sobre `dbo_cotifly.sp_sincronizar_margen_sap` e `INSERT` sobre `dbo_cotifly.SapMargenSyncLog`
  (además del `SELECT` que ya usa el consumo). La tabla temporal no exige permisos extra; el `MERGE` del stored procedure corre con los
  permisos de su dueño (encadenamiento de propiedad dentro de `dbo_cotifly`).
- Un cliente con varias filas en el mismo TrqType recibe la suya (antes el modo `odata` tomaba `results[0]`).
- La regla de anómalos de #139 no cambia: el envío a Nexo aplica la política y guarda el de SAP como marca.
- Pruebas: integración con la copia sembrada en bases aisladas (la compartida nunca tiene una corrida `ok`), esquema, readiness, BDD
  (`margen.feature`) y API (#139 con la fuente real).

## Pendientes

| Pendiente | Dueño |
|---|---|
| Aplicar `17` y `18` en cada base antes de la imagen (ya aplicados en la compartida de pruebas el 2026-10-28) | Datos (@scalderon-bs) |
| ~~Instalar el job en el servidor SQL de producción~~: no se instala (2026-11-03); lo reemplaza el worker | — |
| `GRANT EXECUTE` sobre `sp_sincronizar_margen_sap` e `INSERT` sobre `SapMargenSyncLog` a `cotifly_app` | DBA / Datos |
| Ruta de red del cluster a `sap-tm-prd.boterosoto.local:8000` y `10.10.40.24:1433` (HU-009.1) | Infraestructura |
| `SapTm__Url`, `SapTm__User`, `SapTm__Pass` y `ConnectionStrings__DwOperaciones` en Dokploy (y revisar que no hayan quedado las del modo `odata` viejo: activan el worker solas) | Backend / Infraestructura |
| Confirmar si `DimBPSolicitante.BP` guarda el BP con ceros a la izquierda (SAP lo manda sin ellos; si los tiene, el NIT no cruza) | Datos |
| Script que retira `SapMapeoTrqType.lineaNegocio` (texto) después de desplegar esta imagen | Datos, avisado por Backend |
| Borrar de Dokploy `SapTm__Modo` y `SapTm__ExportPath` (las únicas que siguen retiradas) | Backend / Infraestructura |
| **Transversal, fuera de esta issue:** con la base de Cotifly caída, cualquier endpoint tarda 20 a 45 s en responder (503 en la administración de SAP, 500 en `GET /web/mis-cotizaciones`: 30 s en el E2E del 2026-11-04). Lo suman la recarga de `clsCacheTokensRevocados` en la autenticación y las escrituras con EF (por ejemplo, la auditoría de `clsSapMargenAdminService`), que usan la conexión normal (15 s de `Connect Timeout` por intento). El límite de 5 s de las lecturas de la sincronización puede no aplicar si el pool ya tiene la conexión abierta; en ese caso manda el `CommandTimeout` de 15 s, que se considera razonable. Revisar un límite corto de conexión para la API en general | Backend |
| Cerrar o reorientar HU-009.1/HU-009.2 en su milestone | Usuario |
