# Propuesta de refactor de la base de datos — integración con Nexo + migración a SQL Server

Organiza el plan de refactor de la BD de Cotifly para integrarse con Nexo (costeo,
clientes, ciudades, usuarios), estandarizar nombres en camelCase, y migrar el motor
de PostgreSQL a SQL Server. Es una **propuesta**: no se ha ejecutado ningún cambio de
esquema — sigue el mismo patrón de este proyecto de separar *evaluar/proponer* de
*ejecutar* en cambios de BD.

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
- `fuente_tarifa` fijo en `"nexo"` tiene sentido: el detalle real de la fuente
  (Sicetac/Tarifario/Histórica/Manual) vive **por línea** en
  `cotizacion_lineas.Fuente` en Nexo, no en Cotifly — quien necesite ese detalle
  consulta Nexo vía `costeo_id`, Cotifly solo necesita saber que el costeo vino de ahí.
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
| Eliminar | `origen`, `destino` — pasan a las tablas de Nexo, por línea de solicitud |
| Eliminar | `payload` (jsonb) — sin consumidor real (auditoría previa, `docs/DICCIONARIO_BASE_DE_DATOS.md`); el PDF generado ya es el artefacto definitivo (`pdf_path`). Se elimina también para no arrastrar la migración JSONB→texto de SQL Server (§3.2) sobre un dato que nadie usa |
| Mantener | `space_id`/`thread_id` — aunque hoy nadie los lee desde esta tabla puntual, se conservan como trazabilidad barata ("de qué conversación salió esta cotización"), a diferencia de `payload` sí tienen valor de auditoría futuro y costo cero de mantener |
| Agregar | `costeo_id` — referencia a `cotizaciones.Id` en la BD de Nexo |
| Modificar | `folio` — complementar con BP del cliente u otro identificador más representativo (formato exacto pendiente de definir) |
| Modificar | `linea_negocio` → `linea_negocio_id` (FK a `lineasNegocioMargenesPolitica`, ver §2.7) |
| Modificar | `cliente_id_sap` → `BD_Cliente`, autocompletado desde el endpoint de clientes de Nexo (`clienteId`, ver §2.1c) |
| Modificar | `costo` → se saca automático de `cotizaciones.Total` en la BD de Nexo (costeo final + servicios conexos, vía `costeo_id`) |
| Modificar | `origen_margen` → se reubica justo después de `margen_aplicado`; deja de limitarse a `sap_tm`/`politica_linea`, se agrega `negociado` cuando el comercial lo pone manual |
| Mantener (fijo) | `fuente_tarifa` — se conserva, valor fijo `"nexo"` (el detalle real por línea vive en Nexo, ver §2.1a) |
| Modificar → **se divide en 2** | `estado` se reemplaza por `estado_costeo` y `estado_envio` (ver **decisión revisada** abajo) |
| Modificar | `comercial_email` → `comercial_id` (FK a `usuarios_web.id`) |
| Modificar → **decisión revisada** | `efectiva`/`ganada` **deja de ser un campo propio de Cotifly**: Nexo ya trackea `Resultado` (`SinDefinir`/`Ganada`/`Perdida`) por cotización, y se decidió que **Nexo es la fuente de verdad** (para no tener dos sistemas con su propia opinión sobre si el negocio se ganó). Cotifly **lee/muestra** ese valor vía `costeo_id`, no lo escribe — el botón "ganada/perdida" que se planeaba agregar en el front de Cotifly **ya no aplica** (esa acción vive en Nexo). Si igual se quiere una acción rápida desde Cotifly, tendría que escribir en Nexo vía su API, no en un campo local — **a confirmar con el equipo si se necesita eso o basta con reflejar el dato**. |
| Modificar | `seguimiento_nota` → `motivo_rechazo` — Nexo no trackea "por qué" se perdió, solo el resultado — este campo **sí se queda como propio de Cotifly** (anotación comercial). Solo tiene contenido cuando `Resultado=Perdida`; vacío/null en `SinDefinir` o `Ganada`. |
| Modificar | `seguimiento_actualizado` → `marcado_at` — pasa a ser la fecha en que Cotifly **leyó/sincronizó** el resultado de Nexo (no la de una acción propia), o la fecha del `motivo_rechazo` si aplica |
| Nota | El chat debe identificar automático `BD_Cliente` (endpoint de clientes de Nexo, ver §2.1c) y la ciudad (endpoint de ciudades de Nexo, ver §2.1b) |

**`estado_costeo` revisado** (aprovechando lo que ya sabe Nexo, en vez de reinventar
un enum paralelo): se deriva de `cotizaciones.Estado` de Nexo (`Borrador`/`Entregada`)
combinado con si alguna `cotizacion_lineas.Fuente` de esa cotización es `Manual`
(indica que no todo se resolvió solo):
- `Estado=Borrador` + ninguna línea `Manual` → `autocosteo` (en progreso, va bien).
- `Estado=Borrador` + alguna línea `Manual` → `pendiente_validacion` (esperando al
  administrador de fletes).
- `Estado=Entregada` → `entregado_admin`.

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
| Modificar | `comercial_email` → `comercial_id` (FK a `usuarios_web.id`) |

### 2.3 `correos_enviados`
| Acción | Detalle |
|---|---|
| Modificar | `confirmado_por` → `comercial_id` (FK a `usuarios_web.id`) — *corrección: esta tabla no tenía `comercial_email`, el campo equivalente era `confirmado_por`* |

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

**Mapeo real desde `tblSAPUsuariosApp`** (BD `dbsolucionesti`, columnas confirmadas):

| Columna en `tblSAPUsuariosApp` | ¿Se trae a `usuarios_web`? | Columna nueva |
|---|---|---|
| `ID` | Sí | `id` (pasa a ser **entero**, no UUID — es el mismo ID de SAP, no se genera en Cotifly) |
| `NOMBREUSUARIO` | Sí | `nombreUsuario` |
| `DESCRIPCIONUSUARIO` | Sí (es el nombre completo, ej. "Sara Calderon") | `nombreCompleto` (reemplaza el actual `nombre`) |
| `EMAIL` | Sí | `email` |
| `CLAVE` | **No, nunca** — es la contraseña (varias en texto plano en la muestra que compartiste). Cotifly no autentica con esto (ver arriba); copiarla sería un riesgo de seguridad innecesario y no aporta nada. | — |
| `USUARIOCREACION` / `FECHACREACION` | No | — (auditoría de cuándo se creó la cuenta *en SAP*, no relevante para Cotifly) |
| `USUARIOMODIFICACION` / `FECHAMODIFICACION` | No | — (mismo caso, auditoría interna de SAP) |

Además:
- Se pide **obligatorio** el teléfono en el primer ingreso a Cotifly — no existe en
  `tblSAPUsuariosApp`, se captura y se guarda solo acá.
- El `id` (entero, tomado de SAP) es el que usan como FK todas las demás tablas
  (`comercial_id`/`usuario_id`).
- Se conservan `createdAt` (primer ingreso a **Cotifly**, no a SAP) y `ultimaSesion`.
- **Nota de calidad de datos:** la muestra de `tblSAPUsuariosApp` trae bastantes filas
  de prueba/basura (usuarios como `"ss"`, `"444"`, `"null"` literal, emails vacíos o
  `pruebas@pruebas.com`). No es un problema para el diseño: como la fila en
  `usuarios_web` solo se crea **cuando ese usuario específico entra a Cotifly** (no se
  importa la tabla completa), esos registros basura nunca generan fila si nadie con
  esas credenciales entra a la app.

### 2.7 `margenes_politica` → `lineasNegocioMargenesPolitica`
| Acción | Detalle |
|---|---|
| Agregar | `id`, `is_active` (boolean) |
| Nota | Actualizar el seeder para que coincida con las líneas de negocio reales de Nexo (evitar error en su backend) |
| Nuevo requerimiento | CRUD (front admin) para editar margen y método de cálculo por línea |

## 3. Plan de migración de motor: PostgreSQL → SQL Server

### 3.1 Por qué evaluar esto ahora
El resto del ecosistema de Botero Soto (Nexo, ClientePro, otros proyectos del mismo
entorno) corre sobre SQL Server — Cotifly/Bot Cotizador es hoy el único servicio con
Postgres. Unificar motor simplifica operación (un solo tipo de backup, un solo
equipo de DBA, una sola forma de conectar BI/Looker-equivalente interno) y, como se
notó en §1, **resuelve de raíz el problema de camelCase** (SQL Server no pliega
identificadores).

### 3.2 Mapeo de tipos — lo que cambia en `shared/models.py`

| Uso actual (Postgres) | Dónde se usa | Equivalente SQL Server | Riesgo/pérdida |
|---|---|---|---|
| `UUID(as_uuid=True)` + `gen_random_uuid()` | PK de 7 tablas | `UNIQUEIDENTIFIER` + `NEWID()` (o `sa.Uuid()` de SQLAlchemy 2.x, cross-dialect) | Bajo — SQLAlchemy 2.x ya tiene un tipo `Uuid` agnóstico que compila a `UNIQUEIDENTIFIER` en mssql |
| `JSONB` | `cotizaciones.payload`, `eventos_conversacion.herramientas`, `eventos_herramienta.input/output_resumen`, `sesiones.contexto` | `NVARCHAR(MAX)` + funciones `JSON_VALUE`/`OPENJSON` de SQL Server | **Medio** — SQL Server no tiene un tipo binario JSON nativo como JSONB; se guarda como texto y se consulta con funciones JSON, más lento para filtros dentro del JSON |
| `ARRAY(Text)` | `correos_enviados.para`/`cc` | **No existe** array nativo en SQL Server | **Alto** — hay que elegir: (a) `NVARCHAR(MAX)` con JSON de la lista, o (b) normalizar a una tabla hija `correosDestinatarios(correo_id, email, tipo)`. Se recomienda (b), más limpio y consultable |
| `TIMESTAMP(timezone=True)` | `created_at`, `updated_at`, etc. en casi todas las tablas | `DATETIMEOFFSET` | Bajo, mapeo directo |
| `String(N)` / `Text` | varias | `NVARCHAR(N)` / `NVARCHAR(MAX)` | Bajo |
| `Boolean` | `activo`, `exito`, `efectiva`/`ganada`, etc. | `BIT` | Bajo — SQLAlchemy lo traduce solo |
| `Numeric` | `costo`, márgenes, precios | `NUMERIC`/`DECIMAL` | Bajo |

### 3.3 Cambios de código necesarios

- **Migraciones de Alembic**: las 5 migraciones actuales **importan directo**
  `from sqlalchemy.dialects import postgresql` y usan `postgresql.UUID(...)` /
  `postgresql.JSONB()` — esto **no compila** contra SQL Server. No es "correr la
  misma migración en otro motor": hay que reescribir el historial de migraciones con
  tipos cross-dialect (`sa.Uuid()`, columnas JSON-como-texto) o partir con una
  migración baseline nueva específica para SQL Server.
- **Vistas SQL crudas** (`migrations/versions/0003_vista_trazabilidad.py`,
  `0004_trazabilidad.py`): usan `COUNT(*) FILTER (WHERE ...)` — **no soportado por
  SQL Server**. Se reescribe como `SUM(CASE WHEN condicion THEN 1 ELSE 0 END)`.
- **`shared/repos.py` → `CorreoRepo.duplicado_reciente`**: usa
  `func.make_interval(...)` (función de Postgres) — se reemplaza por
  `DATEADD(MINUTE, -ventana_min, SYSUTCDATETIME())`.
- **`shared/repos.py` → `CotizacionRepo.buscar`**: usa `.ilike(...)` — SQLAlchemy lo
  traduce automático a `LOWER(col) LIKE LOWER(patron)` en dialectos sin `ILIKE`
  nativo, funciona pero sin poder usar un índice normal (a menos que se use un índice
  computado o se dependa de la collation case-insensitive por defecto de SQL Server).
- **Driver**: cambiar `psycopg`/`psycopg[binary]` (extra actual en `pyproject.toml`)
  por `pyodbc` (o `pymssql`), y el `DATABASE_URL` de
  `postgresql+psycopg://...` a `mssql+pyodbc://...`.
- **`shared/db.py`**: revisar el `engine` (algunas opciones de pool/aislamiento
  difieren entre dialectos, a validar).

### 3.4 Cambios de infraestructura

- **`docker-compose.yml`**: el servicio `postgres:16` se reemplaza por
  `mcr.microsoft.com/mssql/server` (imagen ya usada en otros proyectos del mismo
  entorno, ej. `nexo-db`, `cabinafletes-sql`). Cambia también:
  - Puerto por defecto 1433 (hoy Postgres usa 5434 en el host para no chocar).
  - Healthcheck: de `pg_isready` a un `sqlcmd`/`SELECT 1` contra el motor.
  - Variables de entorno de inicialización (`SA_PASSWORD`/`MSSQL_SA_PASSWORD` en vez
    de `POSTGRES_USER/PASSWORD/DB`).
- **`mcp_server/Dockerfile` / `orquestador/Dockerfile`**: necesitan instalar el
  **driver ODBC de Microsoft** (`msodbcsql17`/`18`) en la imagen — Postgres no lo
  necesita porque `psycopg` es puro/no depende de un driver de sistema separado.
- **`scripts/backup_db.sh` / `restore_db.sh`**: hoy usan `pg_dump`/`pg_restore`; se
  reescriben con el equivalente de SQL Server (`sqlcmd`/`bcp`, o
  `BACKUP DATABASE`/`RESTORE DATABASE` nativos).
- **`DEPLOY.md`**: la topología de red/Traefik no cambia, pero las instrucciones de
  acceso (`docker compose exec postgres psql ...`) pasan a ser
  `sqlcmd -S ... -d ...`.

### 3.5 Fases sugeridas (para cuando esto se convierta en HU de ejecución)

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
100% ejecutable. La migración de motor (§3) sigue aparte, requiere su propia
validación técnica antes de tocar producción. Siguiente paso natural:
convertir esto en HU(s) de ejecución separadas — una para el refactor de esquema
(§2) y otra, más grande, para la migración de motor (§3) — siguiendo el mismo
patrón de este proyecto de aprobar antes de ejecutar.
