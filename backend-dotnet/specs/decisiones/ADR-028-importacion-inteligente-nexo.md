# ADR-028 — Cotifly reutiliza la importación inteligente de NEXO (HU-044.1)

- **Estado:** Propuesta (2026-11-06). Issue #252 (HU-044.1), milestone HU-044 «Importación inteligente del Excel de licitación con NEXO».
  Pendiente del visto bueno del PR.
- **Relacionadas:** ADR-002 (puertos y adaptadores), ADR-015 (costeo en NEXO, identidad `X-Portal-clsUsuario-Id`), ADR-017 (licitaciones
  desde Excel: `analizar-excel` y mapeo), ADR-024 (Excel de mercancías leído en local), #278 (contrato único de NEXO, fase 0:
  `IA_TOPE_DIARIO`), HU-031.1 (#194, diccionario de alias de NEXO). En NEXO: S6-HU-34 (importación inteligente), S6-HU-34.9 (NEXO#410,
  cliente por NIT) y el PR #433 (`leer-estructura` y `recetas-lectura`; su llegada a NEXO QA está pendiente de confirmar).
- **Origen:** plan «Cotifly reutiliza la importación inteligente de Excel de NEXO» (2026-10-29,
  `docs/backlog/2026-10-29_ISSUES-IMPORTACION-INTELIGENTE-COTIFLY.md` en NEXO) y las decisiones de Sara del 2026-10-29.

## Contexto

Con HU-011 (ADR-017) el comercial sube el pliego, Cotifly lo reenvía a `analizar-excel` de NEXO y el front muestra los encabezados para que
el comercial mapee **a mano** cada columna. NEXO construyó después, para su propio portal, una importación inteligente:

- `sugerir-mapeo` propone el campo de cada columna (plantilla del cliente → diccionario del cliente → IA con los datos sensibles ocultos);
- `aplicar-mapeo` convierte todas las filas con las mismas reglas de su captura;
- `normalizar-valores` lleva los textos libres a sus catálogos (vehículo, carrocería, línea de negocio, ciudad);
- `plantillas-mapeo` y `diccionario-cliente` recuerdan lo que el comercial confirmó;
- desde el PR #433, `leer-estructura` lee los Excel que `analizar-excel` no entiende (tablas cruzadas con los orígenes en las columnas,
  encabezados en varias filas, datos sueltos fuera de la tabla, varias hojas iguales) con una **receta de lectura** que propone la IA y
  NEXO ejecuta de forma determinista, y `recetas-lectura` guarda la receta confirmada por cliente.

Los pliegos de licitación son justo esos archivos. La pregunta era si Cotifly replicaba la funcionalidad o la usaba.

## Decisión

1. **No se replica.** Cotifly es un puente sin estado hacia NEXO: el puerto `IClsImportacionExcelNexo` (Aplicación) y el adaptador
   `clsImportacionExcelNexo` (Infraestructura) reenvían a `/api/captura-solicitudes/{sugerir-mapeo, aplicar-mapeo, normalizar-valores,
   plantillas-mapeo, leer-estructura, recetas-lectura}` y a `/api/diccionario-cliente` (y `/opciones`). Replicar significaría duplicar el
   prompt, el enmascaramiento de datos sensibles, las reglas de conversión y los catálogos, y que el mismo pliego se leyera distinto en NEXO y
   en Cotifly. La captura termina en NEXO de todas formas: lo que sale de `aplicar-mapeo` es, por construcción, lo que la captura de NEXO
   acepta.
2. **Sin tablas propias.** Las plantillas, las recetas y el diccionario viven en NEXO (`tbl_plantillas_mapeo_cliente`,
   `tbl_recetas_lectura_cliente` y el diccionario por cliente). Cotifly no guarda copias ni historial de la conversación de corrección (esa la
   lleva el front). No hay script de base de datos en esta HU.
3. **El cliente se identifica por NIT.** Con el DW configurado, `GET /clientes` de NEXO devuelve `clienteId = null` y una licitación nueva
   suele ser de un cliente que nunca cotizó en NEXO. Por eso todos los endpoints aceptan `clienteNit` (NEXO#410) y Cotifly manda siempre el
   `nit_cliente` de la licitación, normalizado como lo compara NEXO (`clsNit.Normalizar`: lo que va antes del primer guion, solo letras y
   dígitos, en mayúsculas). Cotifly **no** busca ni guarda el `clienteId` de NEXO; a diferencia del envío del costeo
   (`clsEnvioCosteoService`, que resuelve el cliente buscando por NIT), aquí el NIT viaja directo. Las lecturas no crean clientes en NEXO;
   guardar una plantilla o una receta sí (desde el DW, también prospectos), y un NIT que no es de nadie es `CLIENTE_NIT_INVALIDO`. Listar
   plantillas o recetas **exige** el NIT del lado del puerto: sin él NEXO devuelve las de todos los clientes, así que Cotifly responde
   `CLIENTE_REQUERIDO` sin llamar.
4. **El tope de IA es por comercial.** NEXO mide el gasto de IA por persona (500.000 tokens al día) y responde `429 IA_TOPE_DIARIO`. Cotifly
   lo devuelve como `IA_TOPE_DIARIO` con el mensaje de NEXO y **nunca** activa el bloqueo global de `clsCupoNexoHandler` (ese bloqueo es del
   `429 DEMASIADAS_SOLICITUDES` de los listados limitados por rol: catálogos y bandeja). Que un comercial agote su cupo no afecta a los demás.
   Por la misma razón, lo que puede gastar IA (`sugerir-mapeo`, `normalizar-valores`, `leer-estructura`, `PUT recetas-lectura`) no se
   reintenta nunca: cada intento cuenta para el tope.
5. **Las matrices y los formatos complejos los lee NEXO con `leer-estructura`.** Queda obsoleta la conversión de matrices en el front
   (decisión P3 «NEXO no soporta matrices», clientepro-frontend#141 / FE.7). Cada tabla de `tablas` tiene la forma de una hoja de
   `analizar-excel` y entra a `sugerir-mapeo` sin transformarla.
6. **La receta es JSON opaco.** Su forma (`RecetaLectura`, versión 1) es de NEXO y puede crecer. Cotifly la recibe como `JsonElement`, la
   devuelve al front y la reenvía como texto exactamente como llegó (para corregirla con una instrucción o para guardarla). No la tipa, no la
   valida y no la modifica: una receta mal formada la rechaza NEXO con `RECETA_INVALIDA`.
7. **El archivo lo valida NEXO.** El adaptador no abre el Excel: lo reenvía con su nombre original en el campo `archivo`. Los endpoints
   que lo reciben (#253 lectura, #256 recetas) solo comprueban el tamaño (≤ 10 MB) y la extensión (`.xlsx`/`.xlsm`), sin abrir el libro. Los controles
   locales del Excel subido (HU-035.2) no se aplican en esta ruta; su falso positivo con la configuración de impresión (#277) es una razón más.
   Un rechazo llega con el código de NEXO (`FORMATO_NO_SOPORTADO`, `EXCEL_INVALIDO`, `EXCEL_SIN_DATOS`) o, por tamaño (`413`),
   `ARCHIVO_DEMASIADO_GRANDE`.
8. **La clase de contenedor sigue por alias.** La importación no cambia cómo se resuelve: el diccionario de alias de NEXO (HU-031.1) y la
   captura como hasta hoy.
9. **Cliente HTTP propio, tiempos por endpoint.** `NexoImportacion`, con su tubería: cada intento con `ImportacionInteligente:TimeoutSegundos`
   (90 s por defecto) o, en `leer-estructura` y `PUT recetas-lectura`, `ImportacionInteligente:TimeoutLecturaSegundos` (240 s por defecto,
   180 como mínimo: NEXO hace hasta 3 intentos de IA de 60 s y luego ejecuta la receta o lee sin IA, y el margen evita cortar justo antes). Se reintentan (una vez, lo que admite NEXO) solo los
   `GET` y `aplicar-mapeo` (sin IA ni efecto en NEXO); las demás escrituras tampoco (`PUT plantillas-mapeo`, los `DELETE`). Un 429 nunca se
   reintenta. La petición lleva marcas (`HttpRequestOptions`) que la tubería lee para el tiempo y el reintento. Un 429 no cuenta para el circuito
   (el `IA_TOPE_DIARIO` es de un comercial, no una caída de NEXO). El cuerpo se lee dentro del tiempo del intento y con un tope
   (`ImportacionInteligente:LimiteRespuestaMegabytes`, 20 MB). Sin los logs del `HttpClient` (la URL lleva el NIT) y sin el `message` de NEXO
   en el log (solo estado y código).
10. **Errores.** Nunca lanza por NEXO. Un 400/404 con un código de la tabla de NEXO llega con ese código y el mensaje de NEXO (códigos nuevos
    en `clsCodigosError` y en el enum de `ErrorNegocio`); `VALIDATION_ERROR` (una petición de Cotifly mal armada) u otro 4xx →
    `COSTEO_NEXO_RECHAZADA` con el código en el detalle; 401/403 → `NEXO_SIN_PERMISO`; red, tiempo agotado, 5xx, circuito abierto, JSON
    inesperado, el 429 por minuto (con el `Retry-After` en el detalle) o el comercial sin `idUsuarioExterno` → `NEXO_NO_DISPONIBLE`. Sin
    IA en NEXO no hay error: `200` con `iaUsada = false` y `motivoSinIa`, y el front sigue con el mapeo manual. Un `null` dentro de una
    lista de la respuesta también es `NEXO_NO_DISPONIBLE`; `avisos` y `cambios` de la lectura se reciben con tolerancia (su forma no está
    fijada). `VALIDATION_ERROR` en `leer-estructura` lo deja pasar tal cual #253, que es quien arma ese formulario.
11. **Interruptor.** `ImportacionInteligente:Habilitada` (por defecto `true`, como interruptor de emergencia si el gasto de IA se dispara),
    validada al arrancar con los tiempos. Apagada o sin `Nexo:UrlBase`, se registra `clsImportacionExcelNoDisponible`, que responde
    `NEXO_NO_DISPONIBLE` sin llamar a NEXO.
12. **Alcance de HU-044.1.** Solo el puerto, el adaptador, la simulación en `clsNexoFalso` y este ADR. Del diccionario, solo la lectura
    (`GET diccionario-cliente` y `/opciones`); la escritura (`POST/PUT/DELETE`, `CONTRATO.md` §3.19.1 de NEXO) se agrega en #256. Los endpoints `/web/licitaciones/...`
    son #253 a #256 y las pruebas E2E, #257.

## Consecuencias

- Cotifly depende de NEXO para el mapeo asistido: si NEXO no responde, el comercial vuelve al mapeo manual de ADR-017, que no cambia.
- Lo que NEXO mejore (prompt, enmascaramiento, reglas, catálogos) llega a Cotifly sin cambios de código; un cambio de forma de la receta
  tampoco toca a Cotifly.
- El gasto de IA queda en NEXO y se mide por comercial con su identidad del portal.
- Las plantillas, recetas y términos que un comercial confirma desde Cotifly los ve el portal de NEXO y al revés (es el mismo cliente local).
- **Discrepancia abierta con NEXO QA (2026-11-06):** `GET /api/diccionario-cliente` todavía no acepta `clienteNit` (NEXO#410 cubrió el mapeo
  y las plantillas, no el diccionario): ignora el parámetro y responde los términos de todos los clientes más los generales. Cotifly lo
  manda igual para que tome efecto cuando NEXO lo agregue; mientras tanto, el endpoint que lo exponga (#253 a #256) debe saberlo.
  - **Cómo lo resuelve #256 (HU-044.5, 2026-11-06, pendiente de decisión).** `GET /web/diccionario-cliente` descarta todo término con
    cliente (`clienteId != null`) y devuelve solo los generales, con o sin NIT; con NIT añade el aviso `TERMINOS_CLIENTE_NO_DISPONIBLES`
    para que la interfaz explique que los del cliente aún no se ven. Es la opción conservadora (b) del análisis: nunca sale un término de
    otro cliente. Las otras opciones eran (a) resolver el `clienteId` de NEXO por NIT solo para filtrar, que choca con el punto 3, y (c)
    esperar a NEXO. Cuando NEXO filtre por `clienteNit`, se revisa la regla y se quita el aviso.
    El filtro **falla cerrado**: un término es general solo si NEXO mandó `clienteId: null` explícito y sin `clienteNombre`. Si
    `clienteId` no viene (un cambio de forma de NEXO), el adaptador lo marca como no leído (`ClienteIdLeido = false`) y el término no se
    muestra. Lo más seguro sería un campo `general` explícito de NEXO: se le puede pedir junto con el filtro.
  - **La escritura del diccionario no está.** `POST`, `PUT /{id}` y `DELETE /{id}` de `/web/diccionario-cliente` (y sus códigos
    `TERMINO_DUPLICADO`, `TERMINO_NO_ENCONTRADO`, `CAMPO_DESTINO_INVALIDO` y `CONTEXTO_INVALIDO`) esperan el contrato §3.19.1 de NEXO y
    S6-HU-34.10. El puerto `IClsImportacionExcelNexo` tampoco tiene esas operaciones todavía.
- **Plantillas y formatos compartidos por cliente (decisión del usuario, 2026-11-09).** Las plantillas de mapeo y los formatos de lectura son
  del **cliente**, no del comercial: cualquier comercial (o gerente o director) puede listarlos por NIT y borrarlos por id, y Cotifly no
  verifica dueño. Exigir el NIT en el `DELETE` y cruzarlo con el listado no cerraría ningún acceso, porque cualquier comercial puede
  listar cualquier NIT. El rastro queda en la auditoría de NEXO (`X-Portal-clsUsuario-Id`) y en el log de Cotifly del borrado (comercial
  e id). Si algún día se quiere restringir, el control se pide a NEXO.
- **"No encontrado" con código (#256).** `PLANTILLA_NO_ENCONTRADA` y `RECETA_NO_ENCONTRADA` responden `404` con `ErrorNegocio` y su código,
  no el `404` `ErrorWeb` sin código de las cotizaciones («no encontrada o no es tuya»): una plantilla o un formato no tienen dueño en
  Cotifly. Cambio a confirmar con el front.

## Alternativas descartadas

- **Replicar la importación en Cotifly** (prompt propio, tablas de plantillas y diccionario): dos verdades sobre el mismo pliego, el doble
  de mantenimiento y un segundo lugar donde se gasta IA con datos de clientes.
- **Resolver el `clienteId` de NEXO antes de llamar** (como el envío del costeo): no cubre a los clientes que nunca cotizaron en NEXO ni a
  los prospectos, que son justo los de una licitación nueva.
- **Convertir las matrices en el front** (P3): cada formato nuevo exigía código en el front; la receta de NEXO lo resuelve con datos.
- **Tipar la receta en Cotifly**: acoplaría Cotifly a un formato interno de NEXO que va a crecer, sin ganar nada (Cotifly no la ejecuta).
- **Reintentar como el resto de NEXO**: un reintento de una llamada con IA gasta tope del comercial y puede duplicar el gasto sin que lo vea.
