# Propuesta de refactor de la base de datos de Cotifly — integración con Nexo + migración a SQL Server

Organiza el plan de refactor de la BD de Cotifly para integrarse con Nexo (costeo,
clientes, ciudades, usuarios), estandarizar nombres en camelCase, y migrar el motor
de PostgreSQL a SQL Server. Es una **propuesta**: no se ha ejecutado ningún cambio de
esquema — sigue el mismo patrón de este proyecto de separar *evaluar/proponer* de
*ejecutar* en cambios de BD.

## Estado de aprobación (HU-002.1)

**Aprobado tal cual, sin ajustes — 2026-09-22.** §0-2 (alcance real de HU-002.1: auditoría
y propuesta de reestructuración del esquema actual) queda aprobado punto por punto,
incluyendo las 3 FK candidatas de §2.9 con su evidencia SQL. Los datos provisionales de
`margenes_politica`/`mapeo_trqtype` no se bloquean con una validación previa de Gerencia
Comercial: quedan como están y se ajustan directamente desde el panel admin (§2.7) una vez
entregada la app. Registro completo de la decisión en
`docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md` (checklist de `[HU-002.1]`). §2.8 y §3 siguen
marcadas como fuera de este alcance, pendientes de convertirse en HU(s) separadas.

## 0. Bloqueadores

Ninguno. Los 4 insumos que faltaban (estructura de costeo de Nexo, catálogo de
ciudades, catálogo de clientes, `tblSAPUsuariosApp` + autenticación vía ClientePro)
ya están confirmados y documentados en §2.1a, §2.1b, §2.1c y §2.6. El plan de
esquema (§2) queda **100% listo para convertirse en HU de ejecución**.

## 1. Convención camelCase — advertencia técnica

**Postgres pliega a minúsculas cualquier identificador sin comillas dobles.** Una
columna creada como `clienteIdSap` sin comillas queda guardada como `clienteidsap` —
se pierde el camelCase sin que nadie lo note. Para que funcione de verdad, **todo**
el SQL (migraciones, `shared/sheets_sync.py` que usa `text()` con SQL crudo, la
config del ORM) tendría que citar los identificadores siempre entre comillas dobles.
Es más fricción de la que parece — vale la pena confirmarlo con el equipo backend
antes de aplicarlo a las 9 tablas. (Este problema **desaparece** si se migra a SQL
Server, ver §3 — SQL Server no pliega identificadores, camelCase es nativo ahí.)

## 2. Tablas — plan de cambios

### 2.1a Estructura real de Nexo (referencia, confirmada 2026-09-22)

Nexo modela el costeo en 3 tablas:

- **`cotizaciones`** (cabecera): `Id`, `SolicitudId`, `Estado` (`Borrador`/`Entregada`),
  `Resultado` (`SinDefinir`/`Ganada`/`Perdida`), `Total` (calculado: flete +
  aprovisionamiento + retorno de todas las líneas + servicios conexos, se recalcula
  solo), `EntregadaPor`, `EntregadaAt`, `RowVersion` (concurrencia optimista),
  `CreatedAt`/`UpdatedAt`.
- **`cotizacion_lineas`** (una fila por línea de solicitud costeada): `Id`,
  `CotizacionId`, `LineaSolicitudId` (origen/destino/vehículo/peso viven ahí, no se
  duplican), `ValorFlete`, `ValorAprovisionamiento`, `ValorRetornoContenedor`,
  `Fuente` (`Sicetac`/`Tarifario`/`Historica`/`Manual` — **por línea**, no por
  cotización completa).
- **`cotizacion_conexos`**: servicios conexos (cargue, descargue, estiba) aplicados a
  toda la cotización, con `Cantidad` y `ValorAplicado`.

Esto **confirma y afina** varias decisiones de §2.1:
- `costeo_id` referencia `cotizaciones.Id` de Nexo (la cabecera); `costo` se saca de
  `cotizaciones.Total` (el nombre real es `Total`, no `total`).
- `fuente_tarifa` **se elimina** (decisión revisada 2026-09-22): como la **única**
  fuente de costeo de la que Cotifly va a recibir datos de ahora en más es Nexo, un
  campo que siempre valdría lo mismo no aporta nada. El detalle real de *cuál*
  fuente puntual (Sicetac/Tarifario/Histórica/Manual) ya vive **por línea** en
  `cotizacion_lineas.Fuente` en Nexo — quien necesite ese detalle consulta Nexo vía
  `costeo_id`, no hace falta duplicarlo en Cotifly ni siquiera como valor fijo.
- **Nexo ya trackea Ganada/Perdida y el estado del costeo** — ver decisiones abajo,
  esto cambia cómo se diseñan `ganada`/`estado` en Cotifly.

### 2.1b Catálogo de ciudades de Nexo (referencia, confirmado 2026-09-22)

Dos endpoints, sin autenticación propia documentada aún (a confirmar si requieren
el mismo bearer que el resto de Nexo):

- `GET /api/captura-solicitudes/departamentos` → `string[]` con los nombres de
  departamento, ordenados.
- `GET /api/captura-solicitudes/ciudades?departamento=X&q=texto` → lista de
  `{ municipio: string, departamento: string }`. `departamento` y `q` son **ambos
  opcionales y combinables** (solo uno, el otro, o los dos juntos).

**Cómo lo usa el flujo de captura automática de ciudad** (la nota que quedaba
pendiente en §2.1): cuando el agente extrae una ciudad del texto libre del chat
(ej. "Buenaventura"), llama
`GET /api/captura-solicitudes/ciudades?q=Buenaventura` — si devuelve **una sola**
coincidencia, la usa directo; si devuelve **varias** (ej. un municipio con el mismo
nombre en más de un departamento), debe preguntarle al comercial cuál es, igual que
ya hace `buscar_tarifa` cuando hay varias candidatas y no una única tarifa exacta
(mismo patrón de "nunca inventar/asumir", ver `mcp_server/tarifas/drive_provider.py`).
`GET /departamentos` sirve para poblar un selector en el front (ej. si se agrega un
combo Departamento → Ciudad en el formulario manual, cascada clásica).

### 2.1c Catálogo de clientes de Nexo (referencia, confirmado 2026-09-22)

- `GET /api/captura-solicitudes/clientes?q=...` — en
  `CapturaSolicitudesController.Clientes` (`CapturaSolicitudesController.cs`), que
  llama a `BuscarClientesQuery`/`BuscarClientesQueryHandler`
  (`BuscarClientesQuery.cs`).
- Busca primero en `DimBPSolicitante` (clientes reales, con BP/NIT); **solo si no
  encuentra nada ahí** cae a `tblProspectos`.
- Cada resultado trae `{ clienteId, bp, nit, nombre, origen }`, con
  `origen = "cliente" | "prospecto"`.

**Ajuste a `BD_Cliente` (§2.1):** el valor que se guarda es `clienteId`. Punto a
decidir con el equipo: el endpoint distingue si el match fue un **cliente real**
(con BP/NIT en `DimBPSolicitante`) o apenas un **prospecto** (`tblProspectos`, sin
NIT todavía) — vale la pena guardar también ese `origen` en `cotizaciones` (o
derivarlo on-demand consultando Nexo), porque una cotización a un prospecto sin BP
formal es un caso de negocio distinto (ej. no debería poder facturarse igual). No
es bloqueante, pero se deja anotado para no perder el matiz.

### 2.1 `cotizaciones` (nombre igual)

| Acción | Detalle |
|---|---|
| Eliminar | `origen`, `destino` — ya estan incluidas en las tablas de Nexo, por línea de solicitud |
| Eliminar | `payload` (jsonb) — sin consumidor real (auditoría previa, `docs/database/DICCIONARIO_BASE_DE_DATOS.md`); el PDF generado ya es el artefacto definitivo (`pdf_path`). Se elimina también para no arrastrar la migración JSONB→texto de SQL Server (§3.2) sobre un dato que nadie usa |
| Mantener | `space_id`/`thread_id` — aunque hoy nadie los lee desde esta tabla puntual, se conservan como trazabilidad barata ("de qué conversación salió esta cotización"), a diferencia de `payload` sí tienen valor de auditoría futuro y costo cero de mantener |
| Agregar | `costeo_id` — referencia a `cotizaciones.Id` en la BD de Nexo |
| Modificar | `folio` — complementar con BP del cliente u otro identificador más representativo (formato exacto pendiente de definir) |
| Modificar | `linea_negocio` (texto, ej. "Nacional") → `linea_negocio_id` (**entero**, FK a `lineasNegocioMargenesPolitica.id`, ver §2.7) — mismo caso que `usuario_id`: no es solo el nombre, cambia el tipo de dato de texto a entero |
| Modificar | `cliente_id_sap` → `BD_Cliente`, autocompletado desde el endpoint de clientes de Nexo (`clienteId`, ver §2.1c) |
| Modificar | `costo` → se saca automático de `cotizaciones.Total` en la BD de Nexo (costeo final + servicios conexos, vía `costeo_id`) |
| Modificar | `origen_margen` → se reubica justo después de `margen_aplicado`; deja de limitarse a `sap_tm`/`politica_linea`, se agrega `negociado` cuando el comercial lo pone manual |
| Eliminar | `fuente_tarifa` — la única fuente de costeo hoy es Nexo, un valor que siempre sería el mismo no aporta nada; el detalle real (Sicetac/Tarifario/Histórica/Manual) vive por línea en Nexo, ver §2.1a. **Nota de implementación:** `shared/sheets_sync.py` hoy exporta esta columna a la hoja de Looker — al eliminarla, ese export debe ajustarse (quitar la columna, o dejarla fija con el texto "Nexo" sin leer de BD) |
| Modificar → **se divide en 2** | `estado` se reemplaza por `estado_costeo` y `estado_envio` (ver **decisión revisada** abajo) |
| Modificar | `comercial_email` → `usuario_id` (FK a `usuarios_web.id`) |
| Modificar → **decisión revisada 3 (2026-09-22, confirmado con el contrato real del endpoint de Nexo)** | `efectiva` → `ganada` **se queda como campo propio de Cotifly**, y el botón "ganada/perdida" **vive en el front de Cotifly**. Al oprimirlo, el backend de Cotifly hace **dos escrituras**: (1) llama a `POST /api/cotizaciones/{id}/resultado` de Nexo — **NO un `UPDATE` directo a su BD**, porque ese endpoint tiene lógica de negocio real detrás (idempotencia, rechazo 409 si ya estaba definido, "entrega implícita" si seguía en Borrador, y alimenta la Biblioteca de Tarifas Ganadas — ver detalle completo en §2.8) — y (2) actualiza localmente `cotizaciones.ganada` y `motivo_rechazo` con el mismo `resultado`/`motivo` que se mandó a Nexo. Si Nexo responde `409 RESULTADO_YA_DEFINIDO`, Cotifly no debe guardar el cambio local tampoco (quedarían desincronizados) — el front debe deshabilitar el botón si `ganada` ya tiene un valor. |
| Modificar | `seguimiento_nota` → `motivo_rechazo` — **corrección:** Nexo **sí** trackea el motivo — su endpoint `POST /api/cotizaciones/{id}/resultado` recibe `motivo`/`comentario` y, si es Perdida, los guarda en `CondicionesServicio`. `motivo_rechazo` en Cotifly no es una anotación independiente: es el **mismo valor** que se manda en el body de esa llamada a Nexo, guardado también acá para no depender de una segunda consulta. Si `resultado="perdida"` guarda el motivo real; si `resultado="ganada"` se llena automático con el literal **"No aplica"** (no se deja `null`); solo queda `null`/vacío mientras no se ha marcado ningún resultado todavía. |
| Modificar | `seguimiento_actualizado` → `marcado_at` — pasa a ser la fecha en que se oprimió el botón ganada/perdida en Cotifly (el momento del `POST` a Nexo y del guardado local, ver §2.8) |
| Nota | **Identificación de cliente por chat (detalle del flujo):** cuando el comercial nombra un cliente en texto libre, el chat busca en `DimBPSolicitante`/`tblProspectos` vía el endpoint de clientes de Nexo (§2.1c), con el filtro **más parecido posible** (no exacto exclusivamente) — y solo ahí lo asocia a `BD_Cliente`. Si encuentra una coincidencia por similitud (no exacta), **debe mostrarle al comercial con cuál cliente encontró parecido y pedirle que confirme si es ese o no** — nunca lo asume solo. Si el comercial dice que no es, **no se abre un campo de texto libre** — se le presenta el **mismo buscador de cliente del formulario** (el componente que consulta en vivo el endpoint de clientes de Nexo, §2.1c) para que busque y seleccione el correcto ahí, siempre dentro del catálogo real de Nexo, nunca tecleando un nombre suelto sin vincular a `BD_Cliente`. Mismo patrón para la ciudad (endpoint de ciudades de Nexo, ver §2.1b) — nunca inventar/asumir sin confirmar cuando hay ambigüedad. |

**`estado_costeo` revisado** (aprovechando lo que ya sabe Nexo, en vez de reinventar
un enum paralelo): se deriva de `cotizaciones.Estado` de Nexo (`Borrador`/`Entregada`)
combinado con **cuántas líneas de la solicitud original ya tienen fila en
`cotizacion_lineas`** — **corrección importante** sobre la primera versión de esta
lógica: no sirve chequear "¿alguna línea tiene `Fuente=Manual`?", porque una ruta
que el sistema no pudo costear sola **no aparece con `Fuente=Manual` — simplemente
no tiene fila todavía** en `cotizacion_lineas` (el `Fuente=Manual` solo existe
*después* de que el administrador de fletes ya entró y la completó a mano).

**Confirmado contra el modelo real de Nexo (2026-09-22) — dos tablas, no una:**
- Total de líneas pedidas: `lineas_solicitud` (schema `dbo_NEXO_prueba`),
  `SolicitudId` (uniqueidentifier, FK a `solicitudes.Id`).
- Cuáles ya tienen precio: cruzar contra `cotizacion_lineas` por
  `LineaSolicitudId` (FK a `lineas_solicitud.Id`) — una línea está "costeada" si
  existe **al menos una** fila en `cotizacion_lineas` con ese `LineaSolicitudId`.
- Es la **misma regla que ya usa hoy la Bandeja Priorizada en producción**
  (`EfSolicitudesCatalogoRepository.cs:55-63`) para detectar "sin flete" — se
  reutiliza tal cual, no se reinventa:
  ```sql
  -- pendiente_costeo: al menos una línea sin fila en cotizacion_lineas
  EXISTS (
    SELECT 1 FROM lineas_solicitud l
    WHERE l.SolicitudId = @id
      AND NOT EXISTS (SELECT 1 FROM cotizacion_lineas cl WHERE cl.LineaSolicitudId = l.Id)
  )
  ```
- **Bonus, para distinguir *cómo* se costeó una línea ya resuelta** (no para
  detectar pendientes — eso lo hace la consulta de arriba):
  `lineas_solicitud.EstadoLinea` (`PorCostear`/`AutoResuelta`/`Revisada`) — así
  distingue la propia Bandeja si una línea con precio la resolvió sola la
  Biblioteca de Tarifas (`AutoResuelta`) o si alguien la ajustó a mano
  (`Revisada`). Útil para un futuro reporte de "cuánta intervención manual hace
  falta por línea", no es necesario para `estado_costeo` en sí.

Con eso:
- `Estado=Borrador` + el `EXISTS` de arriba es **falso** (ninguna línea sin costear)
  → **`autocosteo`**: el sistema encontró solo todas las tarifas de las rutas
  pedidas, no necesita intervención del administrador de fletes.
- `Estado=Borrador` + el `EXISTS` es **verdadero** (alguna línea sigue sin fila en
  `cotizacion_lineas`) → **`pendiente_costeo`**: sigue faltando al menos una
  tarifa, esperando a que el administrador de fletes la complete en su visual de
  Nexo.
- `Estado=Entregada` → **`entregada`** (renombrado desde el `entregado_admin`
  original: **no siempre implica que un admin intervino** — hay 2 caminos reales a
  este estado, confirmados con los endpoints de Nexo):
  1. **Explícito**: el administrador de fletes completa las tarifas que faltaban y
     oprime "entregar" (`POST /api/cotizaciones/{id}/entregar`, roles
     `AdminTarifas`/`CoordinadorFletes`/`Admin` — nunca `Comercial`). Ese endpoint
     recalcula el `Total`, marca `Entregada`+`EntregadaPor`+`EntregadaAt`, y dispara
     (async, vía Outbox) una notificación por correo — pero **ojo, es una
     notificación interna de Nexo**: le llega al usuario de Nexo que creó la
     solicitud (`Solicitud.Creador`), **no al cliente final ni a un "comercial
     responsable"** (eso se quitó a propósito en Nexo, issue #22). Esto es
     **totalmente aparte** del correo con el PDF de la cotización que Cotifly le
     manda al cliente (`enviar_correo`/`correos_enviados`) — no hay que
     confundirlos ni duplicar nada.
  2. **Implícito, y solo desde `autocosteo` — corrección importante**: si la
     cotización seguía en `Borrador` y el **comercial** (no el admin de fletes: ese
     rol no marca ganada/perdida, es una acción exclusivamente comercial) marca
     ganada/perdida directo (`POST /api/cotizaciones/{id}/resultado`), Nexo la pasa
     a `Entregada` sola, sin correo de entrega, registrado como `entrega_implicita`.
     **Pero esto solo puede pasar viniendo de `autocosteo`, nunca de
     `pendiente_costeo`**: si a la cotización le falta precio (alguna línea sin
     tarifa, `pendiente_costeo`), el comercial no tiene nada que ofrecerle al
     cliente todavía — no puede enviarla, y por lo tanto tampoco tiene sentido que
     la marque ganada o perdida. La entrega implícita solo aplica cuando el sistema
     ya resolvió todo solo (`autocosteo`) y el comercial define el resultado sin
     pasar por el paso formal de "entregar".

  Para `estado_costeo`, Cotifly no necesita distinguir por cuál de los 2 caminos
  llegó a `Entregada` — con leer `Estado=Entregada` alcanza. Lo que sí importa es
  que **el botón ganada/perdida en el front de Cotifly (§2.8) debe estar
  deshabilitado mientras `estado_costeo` sea `pendiente_costeo`** — no hay precio
  todavía, no se puede marcar resultado.

`estado_envio` (`sin_enviar`/`enviada`/`reenviada`) sigue siendo 100% de Cotifly —
Nexo no sabe nada del envío del PDF al cliente, eso es exclusivo del flujo de correo
de Cotifly. **Aprovechar para arreglar un bug encontrado en la auditoría:** hoy
`estado="reenviada"` está documentado pero nunca se escribe — `enviar_cotizacion`
(`orquestador/web.py`) siempre pone `"enviada"`, incluso si ya se había enviado
antes. Al implementar `estado_envio`, agregar la lógica: si ya estaba `enviada`
(o `reenviada`), el siguiente envío la pasa a `reenviada`.

### 2.2 `ajustes_margen`
| Acción | Detalle |
|---|---|
| Modificar | `comercial_email` → `usuario_id` (FK a `usuarios_web.id`) |

### 2.3 `correos_enviados`
| Acción | Detalle |
|---|---|
| Modificar | `confirmado_por` — **se deja el mismo nombre** (no se renombra a `usuario_id`), pero cambia de tipo: hoy es texto (correo), pasa a ser **entero, FK a `usuarios_web.id`** |
| Eliminar | `para`, `cc` (arrays) — SQL Server no tiene tipo `ARRAY` (§3.2). Se mudan a la tabla nueva `correosDestinatarios`, ver abajo |

**Tabla nueva: `correosDestinatarios`** (reemplaza los arrays `para`/`cc` de arriba) —
una fila por cada correo destinatario de un envío, sea "para" o "cc":

| Columna | Tipo | Detalle |
|---|---|---|
| `id` | UUID | PK |
| `correoEnviadoId` | UUID | FK a `correosEnviados.id` |
| `email` | texto | el destinatario |
| `tipo` | texto | `'para'` o `'cc'` |

Con esto, "¿a qué correos le hemos enviado cotizaciones antes a este cliente?" se
saca con un `JOIN` (`correosDestinatarios` → `correosEnviados` → `cotizaciones`,
que ya tiene `bdCliente`), sin necesidad de guardar el cliente duplicado en la
tabla de destinatarios.

**Nueva funcionalidad de front, habilitada por este `JOIN`:** cuando el comercial
va a enviar una cotización a un **cliente que ya existe** (con envíos previos),
el formulario de envío debe **traer precargados los correos (`para`/`cc`) que ya
se usaron antes para ese mismo `bdCliente`** — con la posibilidad de **editarlos o
quitarlos** si alguno quedó incorrecto o cambió. Evita que el comercial tenga que
volver a teclear los mismos destinatarios cada vez que cotiza para un cliente
recurrente.

### 2.4 `eventos_conversacion`
| Acción | Detalle |
|---|---|
| Modificar | `usuario_email` → `usuario_id` (FK a `usuarios_web.id`) — *corrección: esta tabla no tenía `comercial_email`, el campo equivalente era `usuario_email`* |

### 2.5 `eventos_herramienta` y `sesiones`

Se renombran a camelCase (`eventosHerramienta`, `sesiones`) igual que las demás 7
tablas. Además, del hallazgo de columnas sin uso real (auditoría previa), se
decidió aprovechar el refactor para cerrar 4 puntos que quedaban como "decisión
abierta":

| Tabla | Acción | Detalle |
|---|---|---|
| `eventosHerramienta` | Mantener | `input`/`output_resumen` (jsonb) — se quedan igual. Aunque hoy nadie las lee (ni endpoint, ni vista), son baratas de guardar y sirven para depurar a mano si algo falla. No se construye ningún endpoint nuevo para exponerlas (no hay caso de negocio que lo justifique hoy). |
| `eventosHerramienta` | Eliminar índice, mantener columna | `threadId` se queda, pero se elimina el índice `ix_eventos_herr_thread` — nadie filtra por ahí hoy, es mantenimiento sin beneficio. Si más adelante se construye un endpoint "herramientas por hilo", se re-crea el índice en ese momento. |
| `sesiones` | Eliminar | `contexto` (jsonb) — se inicializa en `{}` y nunca se vuelve a leer ni escribir en ningún lado. Momento ideal para no migrarla al esquema nuevo de SQL Server. |
| `sesiones` | Agregar constraint | `estado` pasa a tener `CHECK (estado IN ('activa','archivada'))` — hoy el 100% de las filas ya cumple esos 2 valores, se agrega sin riesgo al definir el esquema nuevo desde cero. |

### 2.6 `usuarios_web` — rediseño completo

**Autenticación (confirmado 2026-09-22):** la entrada es por **ClientePro** — es
ClientePro quien loguea al usuario y quien internamente le otorga el permiso para
usar Cotifly. Cotifly **no valida credenciales ni autoriza nada por su cuenta**;
para cuando el usuario llega a Cotifly, ya viene autenticado y autorizado desde
ahí. `tblSAPUsuariosApp` es **solo tabla de perfil** (de dónde se lee nombre/email
para crear la fila en `usuarios_web`), nunca de autenticación — Cotifly no valida
`CLAVE` contra nada.

**Flujo de aprovisionamiento, paso a paso (cada vez que un usuario entra a
Cotifly):**
1. ClientePro ya autenticó al usuario y le dio permiso de usar Cotifly — llega su
   `id` (el mismo de `tblSAPUsuariosApp`).
2. Cotifly **valida si ese `id` ya existe en su propia tabla `usuariosWeb`**.
3. **Si ya existe** → no se crea nada de nuevo, sigue directo (a lo sumo actualiza
   `ultimaSesion`).
4. **Si no existe** → recién ahí se crea la fila en `usuariosWeb` con los datos de
   perfil de `tblSAPUsuariosApp` (mapeo abajo), **y solo en ese momento** (usuario
   nuevo, primera vez) se le pide el teléfono de forma obligatoria antes de dejarlo
   continuar — a un usuario que ya existe no se le vuelve a pedir.

**Mapeo real desde `tblSAPUsuariosApp`** (BD `dbsolucionesti`, columnas confirmadas):

| Columna en `tblSAPUsuariosApp` | ¿Se trae a `usuarios_web`? | Columna nueva |
|---|---|---|
| `ID` | Sí | `id` (pasa a ser **entero**, no UUID — es el mismo ID de SAP, no se genera en Cotifly) |
| `NOMBREUSUARIO` | Sí | `nombreUsuario` |
| `DESCRIPCIONUSUARIO` | Sí (es el nombre completo, ej. "Sara Calderon") | `nombreCompleto` (reemplaza el actual `nombre`) |
| `EMAIL` | Sí | `email` |
| `CLAVE` | **No, nunca** — es la contraseña. Cotifly no autentica con esto (ver arriba); copiarla sería un riesgo de seguridad innecesario y no aporta nada. | — |
| `USUARIOCREACION` / `FECHACREACION` | No | — (auditoría de cuándo se creó la cuenta *en SAP*, no relevante para Cotifly) |
| `USUARIOMODIFICACION` / `FECHAMODIFICACION` | No | — (mismo caso, auditoría interna de SAP) |

Además:
- Se pide **obligatorio** el teléfono en el primer ingreso a Cotifly — no existe en
  `tblSAPUsuariosApp`, se captura y se guarda solo acá.
- El `id` (entero, tomado de SAP) es el que usan como FK todas las demás tablas —
  como `usuario_id` en `cotizaciones`, `ajustesMargen` y `eventosConversacion`; en
  `correosEnviados` se queda con su nombre original `confirmado_por` (no se
  renombra). **Corrección importante en los 4 casos:** la columna vieja
  (`comercial_email`/`confirmado_por`/`usuario_email`) era **texto** (guardaba un
  correo) — al pasar a ser FK, el **tipo también cambia a entero** (para poder
  apuntar a este `id`), sin importar si el nombre cambió o no. No es solo un
  cambio de nombre, hay que migrar el tipo de dato en las 4 tablas.
- Se conservan `createdAt` (primer ingreso a **Cotifly**, no a SAP) y `ultimaSesion`.
- **Nota de calidad de datos:** la muestra de `tblSAPUsuariosApp` trae bastantes filas
  de prueba/basura (usuarios como `"ss"`, `"444"`, `"null"` literal, emails vacíos o
  `pruebas@pruebas.com`). No es un problema para el diseño: como la fila en
  `usuarios_web` solo se crea **cuando ese usuario específico entra a Cotifly** (no se
  importa la tabla completa), esos registros basura nunca generan fila si nadie con
  esas credenciales entra a la app.

### 2.7 `margenes_politica` → `lineasNegocioMargenesPolitica`

> **Corrección de nombre (HU-002.3, 2026-09-22):** el nombre final que quedó en firme
> es el **corto**, `MargenesPolitica` (sin el prefijo `tbl`, retirado también en
> HU-002.3) — no `lineasNegocioMargenesPolitica`/`LineasNegocioMargenesPolitica` como
> dice esta sección y el resto de este documento. Ya está así en
> `db/migrations_mssql/0001_script_bd_cotifly_sqlserver.sql` y en `docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`.
> El resto del contenido de §2.7 (columnas, decisiones de diseño) sigue vigente, solo
> cambia el nombre de la tabla.

| Acción | Detalle |
|---|---|
| Agregar | `id` (serial/autoincremental, **pasa a ser la nueva PK**), `is_active` (boolean) |
| Modificar | `linea_negocio` deja de ser PK, pasa a `UNIQUE` — `cotizaciones.linea_negocio_id` (§2.1) referencia el `id` nuevo, no este campo |
| Nota | Actualizar el seeder para que coincida con las líneas de negocio reales de Nexo (evitar error en su backend) |
| Nuevo requerimiento | **No es CRUD completo** — las líneas de negocio en sí no se crean ni se borran desde acá (siguen siendo las que siembra el seeder, alineadas con Nexo). Lo que sí hay que construir, **backend + front admin**: edición de `margen` y `metodoCalculo` por línea, y activar/desactivar (`isActive`) — hoy esta tabla solo se edita a mano en BD (§0 histórico). |

## 2.8 Implicaciones en el código de Cotifly (más allá del esquema)

> ⚠️ **Fuera del alcance original de HU-002.1** (evaluación/refactor de esquema).
> Esta sección es **rediseño de flujo de negocio** (qué herramientas del agente
> quedan obsoletas, qué se reescribe) — queda documentada acá como insumo para una
> **HU futura aparte**, no se ejecuta junto con el refactor de esquema de §0-§2.

Todo §2 es esquema. Pero el costeo pasando a Nexo **no es un cambio de columnas —
es un cambio del flujo completo del agente**. Esto es lo que queda obsoleto o hay
que reescribir, en backend y front, para que el esquema nuevo tenga sentido:

### Backend — MCP Server (`mcp_server/`)

- **`mcp_server/tarifas/`** (`buscar_tarifa`, `DriveFlatFileProvider`, `TarifaRouter`,
  el tarifario local de prueba que armamos en `tests/fixtures/tarifas/`) — **queda
  obsoleto por completo**. Ya no se busca tarifa en Drive ni en archivos locales:
  el costeo (flete + aprovisionamiento + retorno + conexos) lo hace Nexo.
- **`mcp_server/margen/`** (`MargenResolver`, `ExportJsonFuente`/`ODataFuente`,
  `mapeo_trqtype`, el export de SAP `datos/sap_export.json`) — **resuelto
  2026-09-22: NO queda obsoleto, se mantiene**. Analizando los nombres reales de
  las columnas de Nexo (§2.1a): `ValorFlete`, `ValorAprovisionamiento`,
  `ValorRetornoContenedor` y `Total` (la suma de esos 3 + conexos) son puro
  **costo** — en ningún lado se menciona margen ni utilidad. Nexo resuelve el
  costo real de la ruta; **el margen lo sigue calculando Cotifly** con este mismo
  módulo (SAP directo, con fallback a `margenes_politica`), igual que hoy — solo
  cambia de dónde sale el `costo` que alimenta el cálculo. **Confirmado
  2026-09-22: Nexo no agrega ningún margen — eso es exclusivo de Cotifly.**

  **Consecuencia directa para el pendiente de HU-002 (Fase 5, datos
  provisionales):** como `margenes_politica` **no queda obsoleta**, pedirle a
  Gerencia Comercial que valide el 15% de las 8 líneas (corrección 2026-09-22: no hay
  ninguna al 50%, ni siquiera `ITR` — verificado contra la BD real) **no es esfuerzo
  perdido** — sigue
  siendo el fallback real que usa el cálculo de precio, con o sin Nexo. Ese
  pendiente de validación sigue en pie, sin depender de esta decisión.
- **`mcp_server/precio.py`** (`calcular_precio`, la distinción `margen`/`markup`) —
  **no cambia de rol** — sigue siendo el que calcula el precio final real
  (`costo` de Nexo + `margen` de `mcp_server/margen/`), igual que hoy.
- **`mcp_server/server.py` → `generar_pdf`** — el payload que arma el PDF cambia
  solo en el origen del `costo` (antes de Drive/tarifario, ahora de
  `cotizaciones.Total` de Nexo vía `costeo_id`) — `margen_aplicado`/`precio_final`
  se siguen calculando igual que hoy, dentro de Cotifly.
- **Herramientas nuevas necesarias** (no existen hoy): algo como
  `crear_solicitud_nexo` (para iniciar el costeo) y `consultar_costeo_nexo` (para
  leer `Estado`/`Resultado`/`Total` y decidir si ya se puede generar el PDF o hay
  que esperar/avisar que está `pendiente_costeo` — la consulta exacta para derivar
  esto ya quedó confirmada en §2.1). **Importante:** esto no es
  "llamar una API de Nexo" — Cotifly se conecta **directo a la BD de Nexo** (el
  costeo no tiene endpoints REST como sí tienen ciudades/clientes, §2.1b/§2.1c).
  Implica que `mcp_server` necesita una segunda conexión de BD (a la BD de Nexo,
  de solo lectura salvo que se confirme que también se escribe ahí), separada de
  su propia `DATABASE_URL` — y que cualquier cambio de esquema en Nexo puede
  romper a Cotifly sin previo aviso, al no haber un contrato de API de por medio.

### Backend — Orquestador (`orquestador/`)

- **Endpoint nuevo, en el propio Cotifly: `POST /web/cotizaciones/{folio}/resultado`**
  (nombre a definir) — lo llama el **front de Cotifly** cuando el comercial oprime
  el botón ganada/perdida ahí mismo. El handler:
  1. Llama a **`POST /api/cotizaciones/{id}/resultado` de Nexo** (`{id}` = el
     `costeo_id` guardado) con `{ resultado: "ganada"|"perdida", motivo?, comentario? }`.
     Este **es un endpoint REST real de Nexo**, con roles (`Comercial`,
     `AdminTarifas`, `CoordinadorFletes`, `Admin`) y lógica de negocio detrás — no
     se reemplaza por un `UPDATE` directo a su BD porque se perdería:
     - la **idempotencia** (doble clic no duplica nada);
     - el **rechazo 409 `RESULTADO_YA_DEFINIDO`** si ya estaba marcada con otro
       resultado (cambiar de ganada→perdida no es este botón, es una corrección
       aparte);
     - la **"entrega implícita"**: si la cotización en Nexo seguía en `Borrador`
       (con al menos una línea con flete), este endpoint la pasa a `Entregada`
       sola, sin mandar el correo de entrega — esto puede saltar el
       `estado_costeo` derivado (§2.1) directo a `entregada` en el mismo
       momento en que se marca ganada/perdida, aunque antes estuviera en
       `autocosteo`/`pendiente_costeo`;
     - la alimentación de la **Biblioteca de Tarifas Ganadas** (`TarifaHistorica`
       por línea) cuando es "Ganada" por primera vez.
  2. **A definir con el equipo:** con qué identidad/rol llama Cotifly a este
     endpoint (¿un rol de servicio propio, o el token del comercial autenticado
     que ya viene de ClientePro/Nexo, ver §2.6?) — el endpoint exige uno de los 4
     roles listados arriba.
  3. Si Nexo responde `200`, actualiza localmente `cotizaciones.ganada` y
     `motivo_rechazo` con el mismo `resultado`/`motivo` enviado. Si responde `409`,
     **no** actualiza nada local (evita desincronizar) y el front debe mostrar que
     ya estaba definido — ideal: deshabilitar el botón en Cotifly apenas `ganada`
     tenga un valor, para nunca llegar a ese 409 desde la UI.
  4. `comentario` (el segundo campo opcional del body de Nexo, distinto de
     `motivo`) — falta decidir si Cotifly lo captura como un campo separado en el
     front, o si reutiliza `motivo_rechazo` para ambos.
- **Endpoints nuevos para editar `lineasNegocioMargenesPolitica`** (§2.7) — hoy no
  existe ninguno, la tabla se edita a mano en BD. **No es CRUD completo**: las
  líneas no se crean ni se borran desde acá, solo se edita `margen`/`metodoCalculo`
  y se activa/desactiva (`isActive`) por línea existente. Hay que crear en
  `orquestador` algo como `GET /web/admin/lineas-negocio` (listar) y
  `PUT /web/admin/lineas-negocio/{id}` (editar esos 3 campos), protegido para que
  solo lo use un rol admin — y la pantalla del front que los consuma.
- **`orquestador/prompt.py`** (`SYSTEM_COTIZADOR`) — el proceso completo que sigue
  el agente cambia: hoy es *"buscar tarifa → obtener margen → calcular precio →
  generar PDF"* (4 pasos, todo dentro de Cotifly); el nuevo sería algo como *"crear
  solicitud en Nexo → esperar/consultar costeo → generar PDF con el resultado"* —
  hay que reescribir el prompt de cero, no ajustarlo.
- **`orquestador/web_auth.py` / `orquestador/web.py` → `POST /web/login`** — el
  mecanismo de login cambia completo: hoy es "cualquier correo `@boterosoto.com.co`
  sin contraseña"; con ClientePro como entrada (§2.6), este endpoint probablemente
  desaparece tal cual está y se reemplaza por recibir la identidad ya autenticada
  que venga de ClientePro (falta definir el mecanismo exacto de esa entrega —
  ¿token firmado?, ¿sesión compartida?, ver §0 histórico sobre el SSO).
- El flujo de `estado_costeo` (§2.1) implica **esperar** a Nexo (puede no ser
  instantáneo si cae en `pendiente_costeo`) — el Orquestador necesita manejar
  ese "en espera" en la conversación (¿pollea a Nexo?, ¿Nexo notifica por webhook
  cuando el admin de fletes entrega?). Hoy el flujo es 100% síncrono, esto podría
  dejar de serlo.

### Front (Consola web, `orquestador/static/chat.html`)

- El formulario de "Nueva cotización" (los campos manuales Origen/Destino/Cliente/ID
  SAP que probamos) se reemplaza por selectores que llaman en vivo a los endpoints
  de Nexo: departamento→ciudad (§2.1b) y buscador de cliente (§2.1c), en vez de
  texto libre o dropdowns fijos.
- La tarjeta de "captura manual" (la de "Sin tarifa en el sistema", que usamos para
  probar el flujo de Falabella) **deja de tener sentido**: ya no hay "sin tarifa
  encontrada, ingrésala a mano" porque Nexo siempre resuelve el costeo (o lo deja
  en `pendiente_costeo` para su propio admin de fletes, no para el comercial de
  Cotifly).
- La pantalla de resultado necesita reflejar `estado_costeo` — si queda en
  `pendiente_costeo`, el front tiene que comunicarle al comercial que debe
  esperar a que el administrador de fletes de Nexo lo resuelva, no puede generar el
  PDF de inmediato como hoy.
- El botón "ganada/perdida" **sí se agrega en el front de Cotifly** (ej. en "Mis
  cotizaciones") — el comercial lo oprime ahí, y ese mismo clic dispara las dos
  escrituras (Nexo + Cotifly, ver §2.8). No es de solo lectura ni viene de un push
  externo. **Debe quedar deshabilitado mientras `estado_costeo="pendiente_costeo"`**
  (sin precio todavía, no hay nada que marcar como ganado/perdido) — solo se
  habilita en `autocosteo` o `entregada`.
- La pantalla de login cambia — deja de ser "escribe tu correo corporativo",
  depende de cómo entregue la identidad ClientePro.
- El formulario de envío de correo (`para`/`cc`) **precarga los destinatarios ya
  usados antes para ese cliente** (`bdCliente`), editable/removible — ver detalle
  y `JOIN` en §2.3.

Esta sección queda como **evaluación de alto nivel**, no como diseño detallado —
el detalle de cada herramienta/endpoint nuevo necesita su propia ficha técnica
cuando esto pase a HU de ejecución (probablemente HUs separadas por lo grande que
es: una para el flujo de costeo, otra para el login).

## 2.9 FK candidatas — evidencia SQL de seguridad

Esto era lo que pedía la Fase 4 original de HU-002.1 y no se había hecho todavía:
para cada FK candidata, un veredicto explícito (segura/riesgosa) **con la consulta
que lo demuestra**, no solo la decisión de diseño.

### `eventos_conversacion(space_id, thread_id)` → `sesiones(space_id, thread_id)`
**Veredicto: SEGURA.** Verificado 2026-09-22 contra la BD real del proyecto:
```sql
SELECT
  (SELECT COUNT(*) FROM eventos_conversacion) AS total_eventos,
  (SELECT COUNT(*) FROM eventos_conversacion ec
     WHERE NOT EXISTS (
       SELECT 1 FROM sesiones s
       WHERE s.space_id = ec.space_id AND s.thread_id = ec.thread_id
     )) AS eventos_huerfanos;
```
Resultado: **52 eventos totales, 0 huérfanos.** Se puede agregar el FK sin riesgo
de romper datos existentes.

### `cotizaciones.linea_negocio` → `margenes_politica.linea_negocio` (hoy) / `lineasNegocioMargenesPolitica.id` (futuro)
**Veredicto: SEGURA, con muestra chica.** Verificado igual:
```sql
SELECT
  (SELECT COUNT(*) FROM cotizaciones) AS total_cotizaciones,
  (SELECT COUNT(*) FROM cotizaciones c
     WHERE c.linea_negocio IS NOT NULL
       AND NOT EXISTS (
         SELECT 1 FROM margenes_politica mp WHERE mp.linea_negocio = c.linea_negocio
       )) AS lineas_huerfanas;
```
Resultado: **1 cotización total, 0 huérfanas.** Técnicamente sin huérfanos, pero
con solo 1 fila en la BD de desarrollo esto no es evidencia fuerte — hay que
repetir esta misma consulta contra producción antes de aplicar el constraint ahí.

### `usuario_id` / `confirmado_por` (comercial_email/usuario_email → `usuarios_web.id`)
**Veredicto: RIESGOSA — depende de resolver algo antes, no se verifica contra
desarrollo.** A diferencia de las 2 de arriba, acá un chequeo de huérfanos contra
la BD de dev **no sirve de evidencia real**, por 2 razones estructurales:
1. `usuarios_web` **solo se llena cuando alguien entra por la Consola web** con
   login — cualquier cotización creada por **Google Chat** (sin login) nunca tuvo
   ni va a tener una fila correspondiente ahí.
2. `usuarios_web` se agregó en la migración `0005` — cualquier cotización **de
   antes de esa migración** tampoco tiene con qué mapear.

Además, el cambio de dominio es más profundo que un simple huérfano: hoy la
columna guarda un **correo** (texto); mañana debe guardar un **id entero de SAP**
— no hay una correspondencia 1:1 directa entre esos dos valores en los datos que
ya existen, hace falta un **script de migración de datos** (mapear cada correo
histórico a su `id` de SAP vía `tblSAPUsuariosApp`, y decidir qué hacer con los
que no tengan mapeo — dejarlos `NULL`, o bloquear el `FK` hasta resolverlos) antes
de poder agregar el constraint. Esto se resuelve **en la fase de ejecución**, con
datos reales de producción, no antes.

## 3. Plan de migración de motor: PostgreSQL → SQL Server 2019

> ⚠️ **Fuera del alcance original de HU-002.1** (evaluación/refactor de esquema).
> Esta sección es **migración de motor de base de datos** — un proyecto de
> infraestructura aparte, con su propia validación técnica antes de tocar
> producción. Queda documentada acá como insumo para una **HU futura aparte**, no
> se ejecuta junto con el refactor de esquema de §0-§2.

### 3.1 Por qué evaluar esto ahora
El resto del ecosistema de Botero Soto (Nexo, ClientePro, otros proyectos del mismo
entorno) corre sobre SQL Server — Cotifly/Bot Cotizador es hoy el único servicio con
Postgres. Unificar motor simplifica operación (un solo tipo de backup, un solo
equipo de DBA, una sola forma de conectar BI/Looker-equivalente interno) y, como se
notó en §1, **resuelve de raíz el problema de camelCase** (SQL Server no pliega
identificadores).

**Versión objetivo: SQL Server 2019** (`compatibility level 150`).

### 3.2 Mapeo de tipos — lo que cambia en `shared/models.py`

| Uso actual (Postgres) | Dónde se usa | Equivalente SQL Server | Riesgo/pérdida |
|---|---|---|---|
| `UUID(as_uuid=True)` + `gen_random_uuid()` | PK de **5 tablas**: `cotizaciones`, `ajustesMargen`, `correosEnviados`, `eventosConversacion`, `eventosHerramienta` (corrección: no son 7 — `sesiones` usa PK compuesta de texto, `usuariosWeb`/`lineasNegocioMargenesPolitica` usan `id` **entero** serial/de SAP, no UUID, ver abajo) | `UNIQUEIDENTIFIER` + `NEWID()` (o `sa.Uuid()` de SQLAlchemy 2.x, cross-dialect) | Bajo — SQLAlchemy 2.x ya tiene un tipo `Uuid` agnóstico que compila a `UNIQUEIDENTIFIER` en mssql |
| `Integer` + serial/autoincremental | `usuariosWeb.id` (entero de SAP, no autogenerado), `lineasNegocioMargenesPolitica.id` (serial, nuevo). **Sus FK también son `Integer`, no texto**: `usuarioId` (en `cotizaciones`/`ajustesMargen`/`eventosConversacion`, antes texto/correo), `confirmadoPor` (en `correosEnviados`, mismo nombre de antes pero cambia de texto a entero) y `lineaNegocioId` (en `cotizaciones`, antes texto) | `INT IDENTITY(1,1)` para el serial; `INT` simple para el que viene de SAP (no autoincrementa acá, lo asigna SAP) y para las FK que apuntan a ellos | Bajo — distinto de las PK UUID de arriba, no confundir |
| `JSONB` | `eventos_conversacion.herramientas`, `eventos_herramienta.input/output_resumen` (`sesiones.contexto` y `cotizaciones.payload` ya se eliminan, ver §2) | `NVARCHAR(MAX)` + funciones `JSON_VALUE`/`OPENJSON` de SQL Server | **Medio** — en **SQL Server 2019 no existe tipo `JSON` nativo** (ese tipo llega hasta SQL Server 2022); en 2019 la única opción es `NVARCHAR(MAX)` + funciones JSON de texto (soportadas desde 2016, funcionan bien en 2019), más lento para filtrar dentro del JSON que un JSONB real |
| `ARRAY(Text)` | `correos_enviados.para`/`cc` | **No existe** array nativo en SQL Server | **Decidido:** se elimina la columna de `correos_enviados` y se normaliza a la tabla nueva `correosDestinatarios(id, correoEnviadoId, email, tipo)` — una fila por destinatario, ver §2.3 |
| `TIMESTAMP(timezone=True)` | `created_at`, `updated_at`, etc. en casi todas las tablas | `DATETIMEOFFSET` | Bajo, mapeo directo |
| `String(N)` / `Text` | varias | `NVARCHAR(N)` / `NVARCHAR(MAX)` | Bajo |
| `Boolean` | `activo`, `exito`, `ganada`, etc. | `BIT` | Bajo — SQLAlchemy lo traduce solo |
| `Numeric` | `costo`, márgenes, precios | `NUMERIC`/`DECIMAL` | Bajo |

### 3.3 Cambios de código necesarios

- **Migraciones de Alembic**: las 5 migraciones actuales **importan directo**
  `from sqlalchemy.dialects import postgresql` y usan `postgresql.UUID(...)` /
  `postgresql.JSONB()` — esto **no compila** contra SQL Server. No es "correr la
  misma migración en otro motor": hay que reescribir el historial de migraciones con
  tipos cross-dialect (`sa.Uuid()`, columnas JSON-como-texto) o partir con una
  migración baseline nueva específica para SQL Server.
- **Vistas SQL crudas** (`db/migrations/versions/0003_vista_trazabilidad.py`,
  `0004_trazabilidad.py`) — **decisión: 2 de las 3 pasan a SQL Server, 1 se
  descarta**:
  - **`v_actividad_por_comercial`** → **se mantiene y se reescribe** (usa
    `COUNT(*) FILTER (WHERE ...)`, no soportado por SQL Server, se reescribe como
    `SUM(CASE WHEN condicion THEN 1 ELSE 0 END)`). Tiene consumidor real:
    `shared/sheets_sync.py` la usa para el tablero de Looker.
  - **`v_ajustes_margen_por_comercial`** → **también se mantiene y se reescribe**
    (decisión revisada 2026-09-22). Aunque no la consulta código alguno hoy
    (confirmado en la auditoría de `docs/database/DICCIONARIO_BASE_DE_DATOS.md`), es una
    métrica valiosa para Gerencia (cuánto se desvía cada comercial de la política
    de margen) y el ajuste que necesita es **barato**: solo agregar 2 `JOIN`
    locales — a `usuariosWeb` (mostrar nombre en vez del `id`) y a
    `lineasNegocioMargenesPolitica` (mostrar el nombre de la línea en vez del
    `id`). No depende de Nexo para nada, a diferencia de la siguiente.
  - **`v_trazabilidad_cotizaciones`** → **se descarta, no se migra**. Sin
    consumidor de código, y con este refactor sí sería un rediseño pesado:
    selecciona `origen`/`destino`/`fuente_tarifa`, las 3 columnas que se eliminan
    de `cotizaciones` (§2.1) — habría que reescribirla de cero contra Nexo (vía
    `costeo_id`). Si Gerencia necesita ese reporte más adelante, se arma una
    consulta nueva contra el esquema ya migrado en ese momento.
- **`shared/repos.py` → `CorreoRepo.duplicado_reciente`**: usa
  `func.make_interval(...)` (función de Postgres) — se reemplaza por
  `DATEADD(MINUTE, -ventana_min, SYSUTCDATETIME())`.
- **`shared/repos.py` → `CotizacionRepo.buscar`**: usa `.ilike(...)` — SQLAlchemy lo
  traduce automático a `LOWER(col) LIKE LOWER(patron)` en dialectos sin `ILIKE`
  nativo, funciona pero sin poder usar un índice normal (a menos que se use un índice
  computado o se dependa de la collation case-insensitive por defecto de SQL Server).
- **Driver**: cambiar `psycopg`/`psycopg[binary]` (extra actual en `pyproject.toml`)
  por `pyodbc`, y el `DATABASE_URL` de `postgresql+psycopg://...` a
  `mssql+pyodbc://...?driver=ODBC+Driver+17+for+SQL+Server`. **Usar el ODBC Driver
  17** (no el 18): el 18 cambió el default a `Encrypt=yes` y exige un certificado
  TLS válido en el servidor — en un SQL Server 2019 local/interno sin certificado
  propio, eso rompe la conexión hasta configurar `TrustServerCertificate=yes`
  explícito. Con el Driver 17 no hay ese problema por defecto.
- **`shared/db.py`**: revisar el `engine` (algunas opciones de pool/aislamiento
  difieren entre dialectos, a validar).
- **Collation**: SQL Server 2019 soporta collations **UTF-8** (novedad de esa
  versión, ej. `Latin1_General_100_CI_AS_SC_UTF8`) — usarla para las columnas de
  texto con tildes/ñ (nombres, notas, ciudades) y evitar problemas de codificación
  al migrar datos desde Postgres (que es UTF-8 nativo). Confirmar con el DBA si el
  resto de bases del entorno (Nexo, ClientePro) ya usan una collation UTF-8 o una
  Latin1 clásica, para no quedar inconsistentes entre bases que se van a cruzar
  directo (la de costeo, por conexión directa a la BD de Nexo) o por endpoints
  (ciudades/clientes).

### 3.4 Cambios de infraestructura

- **`docker-compose.yml`**: el servicio `postgres:16` se reemplaza por
  `mcr.microsoft.com/mssql/server:2019-latest` (confirmado — distinta a propósito
  de la `2022-latest` que usan `nexo-db`/`cabinafletes-sql`/`sqlserver-dev` en este
  mismo entorno, ver §3.1). Cambia también:
  - Puerto por defecto 1433 (hoy Postgres usa 5434 en el host para no chocar).
  - Healthcheck: de `pg_isready` a un `sqlcmd`/`SELECT 1` contra el motor.
  - Base de datos creada con `compatibility level 150` (el nativo de 2019).
  - Variables de entorno de inicialización (`SA_PASSWORD`/`MSSQL_SA_PASSWORD` en vez
    de `POSTGRES_USER/PASSWORD/DB`).
- **`mcp_server/Dockerfile` / `orquestador/Dockerfile`**: necesitan instalar el
  **driver ODBC de Microsoft `msodbcsql17`** en la imagen (no el 18, ver nota de
  §3.3 sobre el cambio de default de encripción) — Postgres no lo necesita porque
  `psycopg` es puro/no depende de un driver de sistema separado.
- **`scripts/backup_db.sh` / `restore_db.sh`**: hoy usan `pg_dump`/`pg_restore`; se
  reescriben con el equivalente de SQL Server (`sqlcmd`/`bcp`, o
  `BACKUP DATABASE`/`RESTORE DATABASE` nativos).
- **`DEPLOY.md`**: la topología de red/Traefik no cambia, pero las instrucciones de
  acceso (`docker compose exec postgres psql ...`) pasan a ser
  `sqlcmd -S ... -d ...`.

### 3.5 Nombres finales en camelCase (esquema completo)

§2 define los cambios por columna, pero mezclados con `snake_case` en la prosa. Acá
queda el nombre **final** de cada columna, tabla por tabla, tal como quedaría en
SQL Server (donde camelCase es nativo, sin el problema de plegado de Postgres de
§1). Una cosa quedó suelta al armar esta lista, la marco explícita:

- `BD_Cliente` (como se escribió en §2.1) **no es camelCase real** (tiene guion
  bajo y mayúsculas mezcladas) — el nombre correcto sería **`bdCliente`**. Ajustado
  abajo.

(`mapeo_trqtype` también se agrega acá para que la lista quede completa — se deja
tal cual está, solo con camelCase en los nombres, ver su fila abajo.)

| Tabla actual → nueva | Columnas finales (camelCase) |
|---|---|
| `cotizaciones` (igual) | `id`, `folio`, `lineaNegocioId` *(antes `linea_negocio`)*, `clienteNombre`, `bdCliente` *(antes `cliente_id_sap`)*, `costeoId` *(nueva)*, `costo`, `margenPolitica`, `margenAplicado`, `metodoCalculo`, `precioFinal`, `moneda`, `origenMargen`, `pdfPath`, `estadoCosteo` *(antes `estado`)*, `estadoEnvio` *(antes `estado`)*, `usuarioId` *(antes `comercial_email`)*, `spaceId`, `threadId`, `ganada` *(antes `efectiva`)*, `motivoRechazo` *(antes `seguimiento_nota`)*, `marcadoAt` *(antes `seguimiento_actualizado`)*, `createdAt`, `updatedAt`. *(Eliminadas: `origen`, `destino`, `payload`, `fuenteTarifa`. `ganada` se llena vía el botón de Cotifly que llama a `POST /api/cotizaciones/{id}/resultado` de Nexo, no se lee pasivamente de ahí — ver §2.1/§2.8.)* |
| `ajustesMargen` *(antes `ajustes_margen`)* | `id`, `cotizacionId`, `folio`, `margenPolitica`, `margenAplicado`, `diferencia`, `motivo`, `usuarioId` *(antes `comercial_email`)*, `createdAt` |
| `correosEnviados` *(antes `correos_enviados`)* | `id`, `cotizacionId`, `asunto`, `messageId`, `confirmadoPor` *(mismo nombre, cambia de texto a entero — FK a `usuariosWeb.id`)*, `estado`, `error`, `createdAt`. *(Eliminadas: `para`, `cc` — se mudan a la tabla nueva de abajo.)* |
| `correosDestinatarios` *(tabla nueva)* | `id`, `correoEnviadoId` *(FK a `correosEnviados.id`)*, `email`, `tipo` (`'para'`/`'cc'`) |
| `eventosConversacion` *(antes `eventos_conversacion`)* | `id`, `spaceId`, `threadId`, `usuarioId` *(antes `usuario_email`)*, `rol`, `contenido`, `herramientas`, `createdAt` |
| `eventosHerramienta` *(antes `eventos_herramienta`)* | `id`, `herramienta`, `input`, `outputResumen`, `exito`, `duracionMs`, `threadId`, `createdAt` |
| `sesiones` (igual) | `spaceId`, `threadId`, `estado`, `ultimaActividad`. *(Eliminada: `contexto`.)* |
| `usuariosWeb` *(antes `usuarios_web`)* | `id` *(entero, de SAP)*, `nombreUsuario`, `nombreCompleto` *(antes `nombre`)*, `email`, `telefono` *(nueva)*, `createdAt`, `ultimaSesion`. *(Nunca `clave`.)* |
| `lineasNegocioMargenesPolitica` *(antes `margenes_politica`)* | `id` *(nueva, **serial/autoincremental — pasa a ser la PK**)*, `lineaNegocio` *(**UNIQUE**, ya no PK)*, `margen`, `metodoCalculo`, `isActive` *(nueva)*, `updatedAt`. `cotizaciones.lineaNegocioId` referencia este `id`. |
| `mapeoTrqtype` *(antes `mapeo_trqtype`)* | **Se deja tal cual está** — no se le agrega `id`, no cambia su PK compuesta (`trqType`+`salesOrg`), no entra al refactor de esquema. Solo camelCase en los nombres: `trqType`, `salesOrg`, `lineaNegocio`, `activo`. |

### 3.6 Fases sugeridas (para cuando esto se convierta en HU de ejecución)

1. **Evaluación técnica** (esta sección) — ya hecha, pendiente de aprobación.
2. **Baseline de modelos/migraciones nuevo**, cross-dialect o específico de SQL
   Server, en un entorno de prueba aislado (no toca producción).
3. **Migración de datos**: script de export/import Postgres → SQL Server (no es un
   simple `pg_dump`/restore, hay que transformar tipos — especialmente los `ARRAY` y
   `JSONB`).
4. **Validación en paralelo**: correr ambos motores un tiempo, comparar resultados
   de las herramientas del MCP contra los mismos datos.
5. **Corte**: apagar Postgres, `DATABASE_URL` apunta a SQL Server en firme.

## 4. Estado de esta propuesta

Organizada y con las decisiones de diseño cerradas (§2), incluyendo las estructuras
reales de Nexo (costeo, ciudades y clientes), `tblSAPUsuariosApp` y la autenticación
vía ClientePro (2026-09-22). **Sin bloqueadores** — el diseño de esquema queda
100% ejecutable.

**Importante:** esto ya no es solo un refactor de esquema — §2.8 deja claro que
varias piezas centrales del backend (`mcp_server/tarifas/`, `mcp_server/margen/`,
el prompt del agente en `orquestador/prompt.py`) quedan obsoletas y el flujo
completo de cotización se reescribe, además del front. La migración de motor (§3)
sigue aparte, requiere su propia validación técnica antes de tocar producción.

Siguiente paso natural: convertir esto en HU(s) de ejecución **separadas** dado el
tamaño real del cambio — al menos: (1) refactor de esquema (§2), (2) reescritura
del flujo de costeo y el prompt del agente (§2.8, backend), (3) ajustes de front
(§2.8), y (4) migración de motor a SQL Server (§3) — siguiendo el mismo patrón de
este proyecto de aprobar antes de ejecutar.
