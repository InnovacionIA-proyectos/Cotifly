# Nuevas Milestones e Issues — Cotifly / ClientePro

Borrador listo para copiar/pegar en GitHub. Sigue el mismo formato usado en EmbarQA (HU-6XX): milestone = historia completa en Gherkin, issues técnicos ("tarea-tecnica") = checklist explícito sin nada a criterio del desarrollador.

**Cómo usar este documento**
1. Cada `## HU-0XX` es una **Milestone nueva** → copiar el bloque completo (desde `> **HU en Notion:**` hasta el final de "Alcance en este repo") como descripción de la milestone.
2. Cada `### [HU-0XX.N]` dentro es un **Issue nuevo**, asociado a esa milestone, con sus labels ya sugeridas.
3. Numeración: proyecto nuevo, se empieza en **HU-001**. Esta numeración es propia de la iniciativa "migración a ClientePro". Nota: en el git corporativo el código de Cotifly figura como `BSGit/bot-cotizador` — es el mismo proyecto, aquí se le llama `Cotifly`.
3.1. **Repos donde se crean los issues de este documento (3 en total):**
   - [`InnovacionIA-proyectos/Cotifly`](https://github.com/InnovacionIA-proyectos/Cotifly) — backend actual (la migración a .NET, y lo que falte del sistema existente), y el frontend viejo (`chat.html`) que se retira una vez migrado a ClientePro.
   - [`InnovacionIA-proyectos/clientepro-frontend`](https://github.com/InnovacionIA-proyectos/clientepro-frontend) — todo el desarrollo del nuevo módulo de frontend, integrado dentro de ClientePro.
   - [`InnovacionIA-proyectos/NEXO`](https://github.com/InnovacionIA-proyectos/NEXO) — backend de Nexo; aquí se ajusta lo relacionado con el catálogo de clientes (ver HU-005) y cualquier otro cambio que la integración le exija a Nexo.
   - Nota: **no** se crea ningún issue en `administracion_embarqa_nexo` (el frontend de Nexo) — ese repo no se toca en esta iniciativa, solo su backend (`NEXO`).
4. `> **HU en Notion:**` queda con un placeholder — debe crearse la HU en Notion primero y pegar el link real.
5. `**Sprint:**` queda fijado en **Sprint 4** para las 7 HUs — crear el label `sprint-4` en GitHub si todavía no existe. `**Épica:**` ya quedó asignada a cada HU (ver punto 5.1) — crear las 4 épicas en GitHub/Notion antes de crear las HUs.
5.1. **Épicas de esta iniciativa (4 en total):**
   - **E1 — Migración de Backend y Datos**: HU-002, HU-003.
   - **E2 — Migración de Frontend a ClientePro**: HU-004.
   - **E3 — Integración Cotifly ↔ Nexo**: HU-005.
   - **E4 — Despliegue, Configuración y Seguridad en Producción**: HU-001, HU-006, HU-007.
6. Roles del equipo (fijos para toda la iniciativa): **Duban** = Backend, **Sara** = Frontend y Datos, **Kevin** = QA.
7. Donde el trabajo real cae en otro repo, lo digo explícitamente en "Alcance en este repo" y dejo el borrador espejo para ese otro repo en un bloque aparte, claramente marcado.

---

## HU-001 — Hardening inmediato: fallar en arranque si quedan secretos por defecto en producción

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E4 — Despliegue, Configuración y Seguridad en Producción · **Talla:** S · **Roles:** Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** responsable de la operación, **quiero** que el backend actual (Python, en producción hoy) falle explícitamente al arrancar si detecta que sigue usando los valores por defecto de secretos sensibles, **para** que un despliegue no quede corriendo en producción con el token de ejemplo por descuido — corrigiendo un riesgo real ya identificado por QA, sin esperar a que termine la migración a .NET (HU-003).

### Contexto técnico (hallazgo de QA)
`shared/config.py` define valores de fallback inseguros para configuración sensible: `mcp_auth_token = "cambiar-en-despliegue"`, `web_session_secret = "dev-secret-cambia"`. Son razonables como default de desarrollo, pero hoy nada impide que el servicio arranque en producción con esos mismos valores si alguien olvida configurarlos — nadie se entera hasta que ya es un problema de seguridad. Como el backend Python ya está en producción real (ver auditoría de HU-006), esta corrección aplica sobre el código actual, no solo sobre el futuro backend .NET.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Arranque bloqueado con secretos por defecto en producción
Dado el servicio configurado con ENTORNO=prod (o el nombre real de la variable de entorno/ambiente)
Cuando `mcp_auth_token` o `web_session_secret` siguen siendo su valor de fallback por defecto
Entonces el servicio falla explícitamente al arrancar, con un mensaje de error claro indicando qué variable falta configurar
Y no queda expuesto ni un solo minuto en producción con el secreto de ejemplo

Escenario: Arranque normal en desarrollo
Dado el servicio configurado sin ENTORNO=prod (desarrollo local)
Cuando los secretos siguen en su valor por defecto
Entonces el servicio arranca normalmente, sin exigir configuración adicional para el flujo de desarrollo
```

### Trazabilidad (event storming)
Afecta: `shared/config.py` (backend Python actual, repo `Cotifly`). Mismo principio a aplicar luego en la configuración del backend .NET (HU-003.2) y a verificar en HU-007.2 (verificación post-configuración de producción).

### Alcance en este repo (Cotifly)
Agregar una validación de arranque en el backend Python actual que compare cada secreto sensible contra su valor de fallback conocido y detenga el servicio con un error claro si coinciden y el entorno es de producción.

#### Issue en Cotifly

### [HU-001.1] Chequeo de arranque contra secretos por defecto en producción
**Labels:** `cotifly`, `backend`, `seguridad`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-001 (milestone).**
- [ ] Listar todos los valores de configuración sensible con fallback inseguro en `shared/config.py` (al menos `mcp_auth_token` y `web_session_secret`; revisar si hay otros con el mismo patrón).
- [ ] Implementar una validación en el arranque del servicio (`orquestador` y `mcp_server`) que, cuando el entorno sea de producción, compare cada uno de esos valores contra su default conocido y detenga el arranque con un mensaje de error explícito si coinciden.
- [ ] Confirmar que en desarrollo (entorno no productivo) el servicio sigue arrancando sin cambios, sin exigir configuración adicional.
- [ ] Desplegar este chequeo contra el entorno real de Dokploy y confirmar que los valores configurados hoy en producción ya son distintos del default (si no lo son, corregirlos de inmediato como parte de este mismo issue, coordinado con HU-006.5).

---

## HU-002 — Reestructuración, limpieza y migración del esquema de base de datos a SQL Server 2019

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Datos (Sara), Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo de datos y backend, **quiero** primero **evaluar** el esquema actual de PostgreSQL y producir una **propuesta** de reestructuración (qué columnas/constraints se sugiere eliminar, qué foreign keys se sugiere agregar y cuáles quedan como decisión abierta), y solo **una vez aprobada esa propuesta**, ejecutarla y migrar el esquema resultante a **SQL Server 2019** (misma instancia corporativa decidida en HU-003.1), **para** que el backend .NET de HU-003 se construya sobre un modelo de datos íntegro, sin columnas huérfanas ni ambigüedad de relaciones, y sin borrar ni conectar nada sin que el equipo lo haya validado antes.

### Contexto técnico (auditoría ya realizada sobre el esquema real)
Se auditó el esquema real tras aplicar las 5 migraciones Alembic actuales (no solo la documentación) contra el uso real en `orquestador/` y `mcp_server/`. Hallazgos concretos:

- **Columnas escritas pero nunca leídas por la aplicación:** `margenes_politica.metodo_calculo` (el cálculo real usa el `metodo_calculo` que llega en el payload del agente, no esta columna), `cotizaciones.payload` (JSONB, se escribe siempre en `generar_pdf` pero ningún endpoint lo expone), `cotizaciones.seguimiento_actualizado` (se escribe en `marcar_seguimiento` pero no se muestra ni en `/web/mis-cotizaciones` ni en el sync de Sheets).
- **`usuarios_web` redundante e incompleta:** duplica `email`/`nombre` que ya vive en ClientePro (redundante desde que HU-004 reusa la sesión de ClientePro), y no tiene columna para `celular` — `armar_cotizacion.py:434` imprime lo que venga en `comercial.cel`, y al no existir dato real ni columna que lo respalde, el LLM inventa un placeholder tipo "+57 300 000 0000" en el PDF. Candidata a adelgazar (quitar `email`/`nombre`, vincular por el identificador de usuario de ClientePro) y agregar `celular` capturado una sola vez desde el frontend.
- **Ruido inofensivo:** `UniqueConstraint("trq_type","sales_org")` en `mapeo_trqtype` es redundante — esa combinación ya es la PK compuesta de la tabla.
- **FK ya existentes:** `ajustes_margen.cotizacion_id → cotizaciones.id` (nullable por diseño: el ajuste de margen se registra antes de generar el PDF/folio) y `correos_enviados.cotizacion_id → cotizaciones.id` (NOT NULL).
- **FK faltante y segura de agregar:** `eventos_conversacion (space_id, thread_id) → sesiones (space_id, thread_id)` — confirmado en código que la sesión siempre existe antes de crear el evento.
- **FK faltante y riesgosa (no agregar sin resolver antes):** `cotizaciones.comercial_email → usuarios_web.email` — los comerciales que cotizan por Google Chat nunca pasan por el alta de `usuarios_web` (que solo se puebla desde el login de la Consola web), así que hoy pueden existir `comercial_email` sin fila correspondiente.
- **Dato de configuración marcado como provisional en el propio código:** `margenes_politica` y `mapeo_trqtype` están sembrados con valores parejos (15% en todas las líneas) y marcados explícitamente como *"pendiente de validación con Gerencia Comercial"* desde la migración 0002.
- **Decisión ya tomada (ver HU-003):** "conectar con la BD de la compañía" significa alojar esta base en la **misma instancia SQL Server 2019 corporativa**, en su propio esquema — no se requiere integración cruzada con otras bases/maestros de la compañía (ej. no existe hoy, ni se va a crear aquí, un maestro de clientes compartido; `cliente_nombre`/`cliente_id_sap` siguen siendo texto libre por diseño, ya que SAP TM es una fuente externa consultada, no un maestro replicado).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Propuesta de reestructuración antes de ejecutar cualquier cambio
Dado el esquema actual de PostgreSQL ya auditado
Cuando se redacta la propuesta de reestructuración (HU-002.1)
Entonces esa propuesta lista, columna por columna y relación por relación, qué se sugiere eliminar, qué se sugiere conectar/exponer, qué FK se sugiere agregar y qué queda como decisión abierta — sin haber ejecutado todavía ningún cambio sobre el esquema real
Y la propuesta se presenta a negocio/equipo para su aprobación antes de pasar a HU-002.2

Escenario: Solo se ejecuta lo aprobado
Dado que la propuesta de HU-002.1 fue revisada por el equipo
Cuando se ejecuta HU-002.2
Entonces solo se eliminan las columnas/constraints, y solo se agregan las FK, que quedaron explícitamente aprobadas en la propuesta
Y ningún cambio se aplica sobre el esquema real que no haya pasado primero por esa aprobación

Escenario: Esquema objetivo con tipos equivalentes en SQL Server
Dado el esquema actual en PostgreSQL (UUID, JSONB, text[], timestamptz)
Cuando se diseña el esquema objetivo en SQL Server 2019
Entonces cada tipo tiene un equivalente explícito y documentado (uniqueidentifier, nvarchar(max)/tabla relacionada, datetimeoffset) sin pérdida de información

Escenario: Migración física completa
Dado el esquema objetivo ya diseñado y aprobado
Cuando se ejecuta contra la instancia SQL Server 2019 corporativa
Entonces las tablas, FKs y vistas quedan creadas correctamente, y si se decide migrar datos existentes, el conteo de filas por tabla coincide entre origen (PostgreSQL) y destino (SQL Server)
```

### Trazabilidad (event storming)
Basado en el esquema PostgreSQL actual (5 migraciones Alembic, `shared/models.py`, `shared/repos.py`). Entrega el esquema objetivo que consume HU-003.3 (milestone HU-003) para implementar el `DbContext` de EF Core — este HU-002 define y valida el modelo de datos; HU-003.3 lo implementa técnicamente en .NET. Depende de la decisión de instancia SQL Server 2019 tomada en HU-003.1.

### Alcance en este repo (Cotifly)
1. Auditar y decidir, junto con negocio/Gerencia Comercial, cada columna/constraint candidata a eliminar, exponer o mantener (HU-002.1).
2. Agregar las FK seguras y documentar explícitamente las que quedan pendientes de decisión, sin forzar ninguna que rompa datos existentes (HU-002.2).
3. Diseñar el esquema objetivo completo en SQL Server 2019, con equivalencias de tipos y convención de nombres `tbl`/`tblSap` del estándar .NET (HU-002.3).
4. Ejecutar la migración física del esquema (y de los datos, si el equipo lo decide) contra la instancia SQL Server 2019 corporativa (HU-002.4).

**HU también en:** ninguna — es un ajuste interno de datos de `Cotifly`. Entrega insumo directo a HU-003 (backend .NET) de este mismo repo.

#### Issues en Cotifly

### [HU-002.1] Evaluación y propuesta de reestructuración del esquema actual
**Labels:** `cotifly`, `datos`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-002 (milestone). Este issue NO ejecuta ningún cambio sobre el esquema real — solo evalúa y propone.**
- [x] Auditar el esquema actual de PostgreSQL (tablas, columnas, constraints) contra el uso real en el código de `orquestador/` y `mcp_server/`, identificando columnas/constraints que parezcan sin uso real, redundantes, o relaciones que hoy son texto libre y podrían ser FK.
- [x] Redactar un documento de propuesta que liste, uno por uno, cada hallazgo con: qué es, evidencia encontrada (dónde se escribe, si se lee o no, dónde), y una recomendación (eliminar / conectar-exponer / agregar FK / dejar como decisión abierta) — sin aplicar todavía ningún cambio.
- [x] Incluir en la propuesta una sección aparte para cada FK candidata, señalando explícitamente si es segura de agregar (sin riesgo de romper datos existentes) o si depende de resolver algo antes (ej. unificar un flujo de alta de datos) para no romper información ya guardada.
- [x] Incluir en la propuesta el estado de los datos de configuración que el propio código marca como "provisionales" (si los hay), para que el equipo confirme si ya fueron validados por el área de negocio correspondiente o siguen pendientes.
- [x] Incluir en la propuesta el rediseño de `usuarios_web`: hoy duplica `email`/`nombre` que ya vive en ClientePro (redundante desde que HU-004 reusa la sesión de ClientePro en vez del login propio), y no tiene ningún campo para el `celular` del comercial — hallazgo confirmado: `armar_cotizacion.py:434` imprime lo que venga en `comercial.cel`, y como no hay dato real ni columna que lo respalde, el LLM termina inventando un placeholder tipo "+57 300 000 0000" en el PDF. Proponer adelgazar la tabla (quitar `email`/`nombre`, vincularla por el identificador de usuario de ClientePro) y agregar `celular`, capturado una sola vez desde el frontend (HU-004) en vez de generado por el LLM.
- [x] Presentar la propuesta al equipo/negocio y dejar registrada en este issue la decisión final punto por punto (aprobado tal cual, aprobado con ajustes, o rechazado) antes de cerrarlo — este issue no se cierra sin esa aprobación explícita.

**Decisión final (2026-09-22):** Propuesta (`docs/PROPUESTA_REFACTOR_BD_COTIFLY.md`, §0-2) **aprobada tal cual, sin ajustes**, incluyendo las 3 FK candidatas documentadas en §2.9 con su evidencia SQL. Sobre los datos provisionales de `margenes_politica`/`mapeo_trqtype` (punto 4): **se decide no bloquear el cierre de este issue con la validación previa de Gerencia Comercial** — los valores quedan como están hoy, y Gerencia Comercial los ajustará directamente en el panel admin (§2.7) una vez la app esté entregada, en vez de exigir un sello formal antes de ejecutar. HU-002.1 queda **cerrada**, HU-002.2 desbloqueada.

### [HU-002.2] Ejecutar la propuesta aprobada
**Labels:** `cotifly`, `datos`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos) + Duban (Backend — el alcance final incluyó reescribir código de `mcp_server/`, `shared/` y `orquestador/`, no solo migraciones)
**Parte de HU-002 (milestone). Depende de HU-002.1 — solo se ejecuta lo que quedó aprobado ahí, nada más.**
- [x] Aplicar sobre el esquema únicamente los cambios que quedaron aprobados en HU-002.1 (eliminar lo aprobado para eliminar, conectar/exponer lo aprobado para conectar, agregar las FK aprobadas). Migración `0006_hu002_2_esquema_completo`.
- [x] Para la FK riesgosa (`usuario_id`/`confirmado_por` → `usuarios_web.id`): **decisión revisada 2026-09-22** — se ejecuta directo sin backfill, columna nullable. Se acepta perder la trazabilidad histórica de "quién" en `cotizaciones`, `ajustes_margen`, `eventos_conversacion`, `correos_enviados` (no hay forma de mapear un correo a un id de SAP sin ClientePro/HU-004).
- [x] **Decisión revisada 2026-09-22 — ya no queda nada como "decisión abierta" para más adelante:** el equipo decidió ejecutar también las partes que dependían de Nexo/ClientePro (antes aplazadas), aceptando sus consecuencias:
  - `cotizaciones`: eliminadas `origen`/`destino`/`fuente_tarifa`; `estado` dividido en `estado_costeo` (sin poblar hasta HU-005) y `estado_envio`; agregadas `costeo_id`/`bd_cliente` (sin poblar hasta HU-005) y `linea_negocio_id` (**corrección 2026-09-22**: no dependía de Nexo, poblada vía migración `0008` con un cruce local contra `margenes_politica`, sembrada desde `0002`).
  - `margenes_politica`: agregado `id` (nueva PK serial) e `is_active`; `linea_negocio` pasa a `UNIQUE`.
  - `usuarios_web`: rediseño completo — PK pasa a `id` (entero, el id de SAP que entregará ClientePro); se eliminan `email`/`nombre`; la tabla se vació (`TRUNCATE`, no había id de SAP real que asignarle a ninguna fila existente).
  - **Consecuencia aceptada:** `POST /web/login` quedó desactivado (sin `email` no hay cómo identificar a nadie) y con él `GET /web/mis-cotizaciones`, `POST /web/cotizaciones/{folio}/enviar` y `POST /web/cotizaciones/{folio}/seguimiento`. Cotizar por Google Chat (identidad propia de Google, no pasa por este login) no se vio afectado.
  - `margenes_politica.metodo_calculo` (sin uso real, detectado en la auditoría) y el `UniqueConstraint` redundante de `mapeo_trqtype` **sí quedan como decisión abierta** — nunca hubo una recomendación explícita de eliminarlos en la propuesta.
- [x] Actualizar el diagrama ER (`BASE_DE_DATOS.md`) y el diccionario (`docs/DICCIONARIO_BASE_DE_DATOS.md`) reflejando exactamente el esquema resultante.
- [x] Ajustar el código que dependía del esquema anterior para que no truene: `mcp_server/server.py`, `mcp_server/correo.py`, `shared/repos.py`, `orquestador/web.py`, `orquestador/sesiones.py`, `shared/sheets_sync.py`, y las 2 vistas SQL que sobreviven (`v_ajustes_margen_por_comercial`, `v_actividad_por_comercial`, recreadas con `usuario_id`/`estado_envio`); `v_trazabilidad_cotizaciones` se descartó (sin consumidor, dependía de columnas eliminadas).
- [x] **Completado en un segundo paso (2026-09-22, migración `0007_ganada_perdida`):** se terminó de construir el esquema de §2.1/§2.8 que había quedado a medias — `efectiva`/`seguimiento_nota`/`seguimiento_actualizado` → `ganada`/`motivo_rechazo`/`marcado_at`. Mismo criterio que el resto: la columna ya existe, sin poblar hasta que HU-005 construya el botón y la sincronización con el `Resultado` de Nexo.
- [x] **Test suite actualizado (2026-09-22):** las 67+ referencias a campos viejos en 18 archivos de `tests/` corregidas. Suite corrida de verdad (no solo revisada): **246/250 pasan**, 1 skip, 4 fallas confirmadas como **preexistentes y ajenas a esta HU** (dependen de configuración de entorno — `PDF_STORAGE_MODO`, fixtures de tarifario — o de un texto de `chat.html` ya desactualizado antes de este cambio, no del esquema). Detalle de las 4 fallas y por qué no son de esta HU: ver el resumen de la sesión.
- [x] **Bug de producción encontrado y corregido fuera del alcance original:** `orquestador/sesiones_api.py` (`GET /sesiones/{space_id}`) todavía referenciaba `EventoConversacion.usuario_email` directo — habría lanzado `AttributeError` en el primer uso real. Corregido a `usuario_id`.
- [x] `alembic upgrade head` → `downgrade base` → `upgrade head` verificado end-to-end (`test_migraciones_roundtrip`): las migraciones `0006`/`0007` revierten y re-aplican limpio.

### [HU-002.3] Diseño del esquema objetivo en SQL Server 2019
**Labels:** `cotifly`, `datos`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-002 (milestone). Depende de HU-002.1 y HU-002.2 (el esquema objetivo ya debe reflejar las decisiones tomadas ahí).**
- [ ] **Fuente de verdad:** usar `BASE_DE_DATOS.md` y `docs/DICCIONARIO_BASE_DE_DATOS.md` (ya actualizados con las migraciones `0006`/`0007` de HU-002.2) como el esquema actual a mapear — no `docs/PROPUESTA_REFACTOR_BD_COTIFLY.md` directamente, que se escribió antes de ejecutar esas migraciones y puede tener pequeños desfaces de nombres/columnas frente al esquema real.
- [ ] Definir la equivalencia de tipos para cada caso presente en el esquema actual (post HU-002.2): `UUID` → `uniqueidentifier`, `JSONB` → `nvarchar(max)` (con `CHECK (ISJSON(...) = 1)`), `timestamptz` → `datetimeoffset`, `Integer`/`Boolean`/`Text` de las columnas nuevas de HU-002.2 (`costeo_id`, `usuario_id`, `ganada`, `is_active`, etc.) ya cubiertos por las reglas generales.
- [ ] Usar el diseño de tipos, nombres camelCase y la decisión de `correos_enviados.para`/`cc` → tabla nueva `tblCorreoDestinatarios` (no columna JSON, SQL Server no soporta arrays nativos) ya cerrados en §3.2/§3.5 de la propuesta como insumo directo — no rediseñar ni volver a evaluar desde cero.
- [ ] Migrar las 2 vistas de solo lectura que sobreviven (`v_ajustes_margen_por_comercial`, `v_actividad_por_comercial`) a T-SQL equivalente — `v_trazabilidad_cotizaciones` se descartó en HU-002.2 (sin consumidor, dependía de columnas ya eliminadas).
- [ ] Aplicar la convención `tbl`/`tblSap` del estándar `dotnet-enterprise-architect` (HU-003) a cada tabla: `cotizaciones`→`tblCotizaciones`, `ajustes_margen`→`tblMargenAjustes`, `correos_enviados`→`tblCorreos`, `eventos_conversacion`→`tblEventosConversacion`, `eventos_herramienta`→`tblEventosHerramienta`, `sesiones`→`tblSesiones`, `usuarios_web`→`tblUsuariosWeb`, `margenes_politica`→`tblMargenesPolitica`, `mapeo_trqtype`→`tblSapMapeoTrqType`.
- [ ] **Decisión de diseño (ya no bloqueante — corrección 2026-09-22):** hoy `cotizaciones` tiene 2 FK simultáneas hacia `margenes_politica`, **ambas pobladas y consistentes** (`linea_negocio`, texto; `linea_negocio_id`, entero, backfill en migración `0008`) — decidir si en SQL Server se conserva el texto como conveniencia de lectura o se retira ahora que el id ya está disponible (ninguna de las dos opciones depende de Nexo/HU-005).
- [ ] Entregar el documento/diagrama del esquema objetivo (tablas, columnas, tipos, FKs, **constraints `CHECK`/`UNIQUE`** — ej. `ck_sesiones_estado`, `uq_margenes_politica_linea_negocio` — y vistas) como insumo directo para HU-003.3 (milestone HU-003), sin dejar ninguna tabla del esquema actual sin mapear.
- [x] **Confirmado con el DBA (2026-09-22):** ni Nexo ni ClientePro tienen una collation explícita definida (usan la del servidor por defecto) — no hay nada con qué chocar. Se usa `Latin1_General_100_CI_AS_SC_UTF8` (novedad de SQL Server 2019, soporte UTF-8) para columnas de texto con tildes/ñ, tal como se había propuesto en §3.3. Riesgo de fricción por cruce de texto entre bases además descartado: la integración con Nexo es HTTP/polling, no conexión directa a su BD (ver HU-005).
- [ ] Usar **ODBC Driver 17** (no 18) para la conexión — el 18 exige TLS por defecto y rompe contra una instancia sin certificado propio (§3.3 de la propuesta).

### [HU-002.4] Migración física a la instancia SQL Server 2019 corporativa
**Labels:** `cotifly`, `datos`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos) + Duban (Backend, ejecución técnica de la migración)
**Parte de HU-002 (milestone). Depende de HU-002.3.**
- [x] Datos de conexión reales a la instancia SQL Server 2019 corporativa (servidor, nombre de base, usuario de servicio): los provee directamente el responsable del proyecto (no hay que gestionarlo con TI por separado) — confirmar el nombre de base a usar, siguiendo convención corporativa si existe una.
- [ ] Crear la base de datos de Cotifly en esa instancia y ejecutar el esquema objetivo de HU-002.3 (tablas, FKs, vistas).
- [ ] Decidir junto con el equipo de HU-003 si esta migración se ejecuta vía script T-SQL directo o vía las migraciones EF Core de HU-003.3 (evitar hacerlo por ambos caminos a la vez).
- [ ] Si el equipo decide conservar los datos históricos de PostgreSQL, migrarlos (export/import o script dedicado) y validar que el conteo de filas por tabla coincide entre origen y destino.
- [ ] Confirmar que el backend (Python o .NET, según el estado de la convivencia en ese momento) puede conectarse y operar contra la BD ya migrada en un entorno de pruebas antes de dar el paso en producción.

### [HU-002.5] Validación QA de integridad de datos tras la migración a SQL Server
**Labels:** `cotifly`, `qa`, `datos`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA) + Sara (Datos)
**Parte de HU-002 (milestone). Depende de HU-002.4.**
- [ ] Confirmar que el conteo de filas por tabla coincide entre PostgreSQL (origen) y SQL Server 2019 (destino), para cada tabla migrada.
- [ ] Probar cada FK agregada en HU-002.2 con al menos un caso real que confirme que la integridad referencial funciona como se espera (ej. intentar insertar un registro huérfano y confirmar que la base lo rechaza).
- [ ] Validar que las 3 vistas migradas (`v_ajustes_margen_por_comercial`, `v_trazabilidad_cotizaciones`, `v_actividad_por_comercial`) devuelven resultados equivalentes a las originales de PostgreSQL para un mismo conjunto de datos de prueba.
- [ ] Dejar documentado el resultado de esta validación como requisito de cierre antes de que HU-003.3 (conexión del backend .NET a la nueva base) se dé por completada.
- [ ] Dejar evidencia trazable de esta ejecución (fecha, quién la corrió, contra qué entorno) en el propio issue o en un commit con mensaje descriptivo — no basta con que "haya pasado", debe quedar registro verificable de cuándo y contra qué sistema real se corrió, dado que hoy el historial de git del repo no permite reconstruir esa trazabilidad por sí solo.

---

## HU-003 — Migración del backend de Cotifly a C# / .NET 10 con Clean Architecture

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** XL · **Roles:** Backend (Duban), Datos (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo de TI de Botero Soto, **quiero** que el backend de Cotifly esté implementado en C# / .NET 10 siguiendo Clean Architecture (Monolito Modular Moderno) y persistiendo en SQL Server 2019, **para** alinearlo con el estándar corporativo de desarrollo (`dotnet-enterprise-architect`), dejar de depender de un stack Python aislado del resto de sistemas de la compañía, y facilitar su mantenimiento por el equipo interno.

### Contexto técnico (estándar aplicable y estado actual)
- **Estándar obligatorio:** skill `dotnet-enterprise-architect` — Clean Architecture de 4 capas (Dominio, Aplicación, API, Infraestructura), Monolito Modular Moderno (módulos por área de negocio, sin microservicios), sin CQRS salvo justificación, convenciones de nombres `cls` para clases y `tbl` para tablas (`tblSap` para tablas de SAP), Docker + `docker-compose.yml` obligatorios, `.gitignore` mínimo (`.env`, `bin/`, `obj/`), estrategia de testing completa, y documentación técnica con las 9 secciones que exige la skill (descripción, arquitectura, infraestructura, flujo del sistema, stack, ejecución local, API, testing, estructura del proyecto).
- **Backend actual a reemplazar (Python, repo `Cotifly`):** `orquestador/` (API FastAPI: webhook de Chat, loop agéntico, sesiones, endpoints `/web/*`), `mcp_server/` (servidor FastMCP con las herramientas de negocio: `margen/`, `tarifas/`, `pdf/`, `correo.py`, `auditoria.py`, `precio.py`), `shared/` (config, modelos ORM, repos), `migrations/` (Alembic sobre PostgreSQL 16).
- **Decisión ya tomada:** el nuevo backend .NET **sí reimplementa también el loop agéntico con LLM** (el "Asistente" conversacional que interpreta lenguaje natural y orquesta las herramientas de negocio, junto con el webhook de Google Chat) — deja de existir en Python, no hay convivencia para esta pieza. Ver HU-003.1 para la elección concreta del mecanismo de orquestación en .NET (ej. Semantic Kernel, cliente HTTP directo al proveedor LLM equivalente a LiteLLM).
- **Decisión ya tomada:** el `mcp_server` (herramientas de negocio) se absorbe dentro del monolito .NET como parte de las capas de Aplicación/Infraestructura — deja de existir como servicio MCP separado.
- **Decisión ya tomada:** la base de datos pasa de PostgreSQL 16 a **SQL Server 2019** (instancia corporativa ya existente, la misma familia de motor que usan otros sistemas de la compañía).
- **Contrato de referencia:** los endpoints `/web/*` documentados en HU-004.1 (repo `Cotifly`, milestone HU-004) deben reproducirse con el mismo comportamiento en la nueva API .NET, para que el módulo Angular de HU-004 (`clientepro-frontend`) no tenga que cambiar nada al cambiar de backend.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Estructura de 4 capas
Dado el nuevo backend .NET
Cuando se revisa la solución
Entonces existen exactamente las capas Dominio, Aplicación, API e Infraestructura, cada una con solo las responsabilidades que el estándar define para ella
Y no hay lógica de negocio en la capa API ni acceso a datos directo desde el Dominio

Escenario: Monolito Modular Moderno
Dado el catálogo de funcionalidades de Cotifly (Tarifas, Margen, Precio, PDF, Correo, Auditoría, Cotizaciones)
Cuando se organiza el código
Entonces cada una es un módulo de negocio identificable (carpeta propia, bajo acoplamiento con los demás)
Y no se introduce separación en microservicios ni se prepara el proyecto para ello

Escenario: Convenciones de nombres aplicadas
Dado cualquier clase o tabla nueva del proyecto
Cuando se revisa su nombre
Entonces las clases usan el prefijo `cls` (ej. `clsCotizacionService`), las tablas generales usan el prefijo `tbl` y las tablas de origen SAP usan el prefijo `tblSap`
Y las propiedades públicas y métodos siguen PascalCase, variables y parámetros camelCase

Escenario: Persistencia en SQL Server 2019
Dado el esquema de datos actual en PostgreSQL (cotizaciones, ajustes de margen, correos, sesiones)
Cuando se migra a SQL Server 2019 vía Entity Framework Core
Entonces el nuevo esquema conserva la misma información y relaciones, con nombres de tabla ajustados a la convención `tbl`
Y existen migraciones EF Core versionadas equivalentes a las que hoy provee Alembic

Escenario: Paridad funcional con el backend Python
Dado un cliente (el módulo Angular de HU-004) que hoy consume /web/login, /web/mis-cotizaciones, /web/cotizaciones/{folio}/enviar y /web/cotizaciones/{folio}/seguimiento contra el backend Python
Cuando esos mismos endpoints se consumen contra el nuevo backend .NET
Entonces el request/response es equivalente al contrato congelado en HU-004.1, sin requerir cambios en el frontend

Escenario: Agente LLM reimplementado en .NET
Dado que el equipo decidió migrar también el loop agéntico con LLM (no se queda en Python)
Cuando se diseña e implementa el endpoint /web/chat y el webhook de Google Chat en el nuevo backend
Entonces ambos quedan resueltos en .NET, sin depender de que el backend Python siga vivo para esta funcionalidad

Escenario: El identificador del cliente no se pierde entre turnos
Dado un comercial que identifica un cliente por nombre en un turno de chat, y confirma la cotización en un turno posterior
Cuando el agente genera el PDF final
Entonces el identificador del cliente resuelto (vía la búsqueda de HU-005.2, no texto libre) llega completo hasta la cotización persistida, sin depender de que el LLM lo recuerde por su cuenta — corrigiendo el hallazgo confirmado en el backend Python actual (3 nombres de campo distintos sin mapeo, sin estado estructurado entre turnos)

Escenario: Aislamiento de cotizaciones por comercial
Dado que el comercial A y el comercial B tienen cada uno sus propias cotizaciones
Cuando el comercial A consulta /web/mis-cotizaciones, o intenta marcar seguimiento o reenviar por correo una cotización del comercial B
Entonces nunca ve ni puede tocar cotizaciones del comercial B (mismo comportamiento que ya garantiza el backend Python actual)

Escenario: Empaquetado y pruebas según el estándar
Dado el nuevo backend terminado
Cuando se revisa el repositorio
Entonces incluye Dockerfile y docker-compose.yml funcionales, pruebas unitarias e de integración ejecutables automáticamente, .gitignore mínimo exigido, y documentación técnica con las 9 secciones de la skill
```

### Trazabilidad (event storming)
Reemplaza: `orquestador/` + `mcp_server/` + `shared/` (Python, repo `Cotifly`). Contrato de referencia a replicar: HU-004.1 (endpoints `/web/*`). Consumido por: módulo Angular de HU-004 (`clientepro-frontend`). Integraciones externas heredadas: SAP TM (export JSON / futuro OData), Google Drive (tarifario), SMTP, Google Sheets (sincronización Looker Studio), S3/MinIO (opcional, almacenamiento de PDFs). Base de datos: SQL Server 2019 corporativo, reemplazando PostgreSQL 16. Estándar obligatorio: skill `dotnet-enterprise-architect`.

### Alcance en este repo (Cotifly)
1. Decisión ya tomada (HU-003.1): el desarrollo .NET vive en este mismo repo `Cotifly` (carpeta nueva, conviviendo temporalmente con el código Python mientras se migra) — no se crea un repo aparte.
2. Construir el backend completo en C# / .NET 10 con Clean Architecture, absorbiendo toda la lógica de negocio de `mcp_server/` y la superficie de API de `orquestador/`.
3. Migrar el esquema de base de datos de PostgreSQL a SQL Server 2019, conservando la información y trazabilidad ya existente (cotizaciones, ajustes de margen, correos, sesiones).
4. Mantener el comportamiento de negocio idéntico al actual (misma política de márgenes, mismo cálculo de precio, mismo formato de PDF, mismo flujo de correo) salvo que el equipo decida explícitamente un cambio.
5. El retiro definitivo del backend Python (`orquestador/`, `mcp_server/`) es una tarea posterior, una vez el backend .NET esté validado en producción y HU-004 (frontend) esté apuntando a él — no se borra código Python dentro de esta HU.

**HU también en:** ninguna — todo el trabajo de esta migración es interno a `Cotifly`. Relacionada con HU-004 (`clientepro-frontend`), que consume la API que aquí se reconstruye.

#### Issues en Cotifly

### [HU-003.1] Decisiones de arquitectura y estrategia de migración
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Gatilla el resto de issues de esta HU — ninguno de los siguientes se empieza sin cerrar este.**
- [x] Decisión tomada: el loop agéntico con LLM (Asistente conversacional, webhook de Google Chat) se migra a .NET junto con el resto del backend — no se queda en Python. Elegir el mecanismo concreto de orquestación (ej. Semantic Kernel, cliente HTTP directo al proveedor LLM equivalente a LiteLLM) y dejarlo documentado en este issue y en la HU en Notion.
- [x] Decisión tomada: el desarrollo .NET vive en el repo `Cotifly` (carpeta nueva, conviviendo temporalmente con el código Python) — no se crea un repo nuevo.
- [ ] Decidir la estrategia de corte: *big bang* (se apaga Python y se enciende .NET de una vez) vs. incremental/*strangler* (módulo por módulo, con ambos backends convivendo y el frontend apuntando a uno u otro por configuración) — documentar la decisión y sus implicaciones para HU-004.
- [ ] Elegir la librería de generación de PDF en .NET que reemplace a WeasyPrint (ej. QuestPDF u otra ya aprobada por el equipo), confirmando que puede reproducir el layout actual de la cotización (revisar `armar_cotizacion.py`/`armar_multi.py` como referencia de layout).
- [x] Datos de conexión reales a la instancia SQL Server 2019 corporativa (servidor, nombre de base, usuario de servicio): los provee directamente el responsable del proyecto.
- [ ] Confirmar si `mcp_server`/`orquestador` seguirán expuestos durante la convivencia (para no romper integraciones externas que los usen hoy, como el webhook de Google Chat) y documentarlo.
- [x] Decisión tomada: el agente LLM se limita a **redacción** (componer la respuesta en lenguaje natural, mantener la conversación) — **no** se usa para **extracción/consulta de datos**. Toda lectura de tarifas, márgenes, cotizaciones, clientes, etc. se hace por código determinístico (herramientas explícitas: Tarifas, Margen, Precio, PDF, Correo — HU-003.4/HU-003.5), nunca por el LLM interpretando o generando directamente una consulta a la base de datos. Documentar esta regla como principio de diseño del agente antes de implementarlo (HU-003.6).
- [ ] **Diseñar un estado de conversación estructurado (slot-filling) en el orquestador .NET** que retenga los datos ya resueltos (ej. cliente identificado, línea de negocio, folio en progreso) y los reinyecte automáticamente en cada llamada a herramienta, en vez de depender de que el LLM los re-infiera del historial de texto en cada turno. Causa raíz confirmada en el backend Python actual: `cliente_id_sap` no llega de forma confiable desde el chat hasta `obtener_margen`/`generar_pdf` porque (a) hay 3 nombres distintos para el mismo dato en el código actual (`cliente`, `cliente_id`, `cliente_id_sap`) sin ningún mapeo programático entre ellos, y (b) el orquestador Python solo reconstruye el hilo de texto entre turnos, no los tool-calls/resultados previos — así que cuando "generar_pdf" ocurre en un turno distinto al de "obtener_margen" (típico, porque el Paso 4 exige esperar confirmación del usuario antes de generar el documento), el LLM tiene que re-adivinar el dato sin ayuda de ningún campo ya resuelto. En .NET, unificar a un solo nombre de campo para el identificador de cliente en todas las herramientas, y que el orquestador (no el LLM) sea responsable de propagarlo entre pasos.
- [ ] **Conectar el modo de chat en lenguaje natural con la búsqueda real de clientes (HU-005.2)**: hoy, si el usuario escribe "cotiza para Falabella" en el chat, el LLM copia ese texto literal al campo cliente sin resolverlo contra ningún catálogo (no existe tabla de clientes en la BD). En .NET, cuando el agente necesite identificar un cliente por nombre durante la conversación, debe invocar la misma herramienta de búsqueda de clientes que consume el catálogo de Nexo (HU-005.2), en vez de dejar que el LLM copie texto libre al payload — esto resuelve a la vez la falta de catálogo y la propagación poco confiable del ID.

### [HU-003.2] Esqueleto de la solución Clean Architecture y convenciones de nombres
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Depende de HU-003.1.**
- [ ] Crear la solución .NET 10 con los 4 proyectos de capa (`Dominio`, `Aplicacion`, `Api`, `Infraestructura`) y las referencias correctas entre ellos (Api y Aplicacion dependen de Dominio; Infraestructura implementa interfaces definidas en Dominio/Aplicacion; Dominio no depende de nada).
- [ ] Crear la carpetación por grupos funcionales dentro de cada capa (ej. `Cotizaciones/`, `Tarifas/`, `Margen/`, `Pdf/`, `Correo/`, `Auditoria/`) siguiendo el ejemplo de la skill (`DTOXxx/`, `Validators/`, `Services/`, `Repositories/`).
- [ ] Configurar análisis estático/linters que verifiquen la convención de nombres (`cls` para clases) y agregarla a la plantilla de nuevas clases (snippet o `.editorconfig`).
- [ ] Crear `.gitignore` con, como mínimo, `.env`, `bin/`, `obj/`, archivos temporales y artefactos de compilación.
- [ ] Configurar manejo de variables de entorno/secretos (sin hardcodear nada), documentando cada variable esperada en un `.env.example`.

### [HU-003.3] Modelo de dominio y persistencia en SQL Server 2019
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Depende de HU-003.2 y del esquema objetivo entregado en HU-002.3 (milestone HU-002) — no rediseñar el esquema aquí, implementarlo tal como quedó definido allá.**
- [ ] Modelar las entidades de Dominio equivalentes a los modelos SQLAlchemy actuales (cotizaciones, ajustes de margen, correos, sesiones), sin dependencias de tecnología dentro de la capa Dominio.
- [ ] Definir las interfaces de repositorio en Dominio/Aplicación (ej. `IClsCotizacionRepository`) y sus implementaciones con Entity Framework Core en Infraestructura.
- [ ] Crear el `DbContext` apuntando a SQL Server 2019, con las tablas nombradas según la convención `tbl` (ej. `tblCotizaciones`, `tblMargenAjustes`, `tblCorreos`, `tblSesiones`) y `tblSap*` para cualquier tabla que refleje datos de origen SAP.
- [ ] Generar las migraciones EF Core equivalentes a las 5 migraciones Alembic actuales, revisando con el equipo de datos (Sara) que no se pierda ninguna regla de integridad (constraints, índices) ya existente en PostgreSQL.
- [ ] Documentar el proceso de migración de datos históricos (si aplica) de PostgreSQL a SQL Server 2019, o la decisión explícita de partir en limpio si el equipo la toma.

### [HU-003.4] Módulo de negocio: Tarifas, Margen y Precio
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Depende de HU-003.3.**
- [ ] Reimplementar el proveedor de tarifario hoy en `mcp_server/tarifas/` (`DriveFlatFileProvider`, lectura de Google Drive vía CSV/XLSX) como servicio de Infraestructura del módulo Tarifas.
- [ ] Reimplementar la política de márgenes de `mcp_server/margen/` (fuentes export/OData) como caso de uso del módulo Margen en la capa Aplicación.
- [ ] Reimplementar `mcp_server/precio.py` como caso de uso del módulo Precio que orquesta Tarifas + Margen, con la misma fórmula de cálculo que el backend Python.
- [ ] Escribir pruebas unitarias que comparen, para un conjunto fijo de casos de prueba, el resultado del cálculo de precio en .NET contra el resultado que hoy produce el backend Python (paridad de negocio, no solo de compilación).

### [HU-003.5] Módulo de negocio: PDF y Correo
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Depende de HU-003.3 y de la librería de PDF elegida en HU-003.1.**
- [ ] Reimplementar la generación de PDF de cotización (hoy `armar_cotizacion.py`/`armar_multi.py` con WeasyPrint) usando la librería .NET elegida en HU-003.1, reproduciendo el mismo layout, assets e imágenes corporativas.
- [ ] Obtener el `celular` del comercial desde la tabla rediseñada (HU-002.1, capturado una sola vez desde el frontend en HU-004.3), nunca dejar que el LLM lo genere/invente. Si el comercial todavía no tiene el dato guardado, el PDF debe omitir el campo (o marcarlo explícitamente como "no registrado") en vez de mostrar un número de teléfono falso — corrigiendo el hallazgo confirmado en Python (`armar_cotizacion.py:434` sin dato real, LLM inventando un placeholder tipo "+57 300 000 0000").
- [ ] Confirmar con el equipo cuál de los 3 transportes de correo hoy configurables (`smtp_relay`, `wscorreos`, `smtp`) es realmente el de producción; documentar los otros dos como legacy/retirados si ya no aplican, en vez de mantener 3 caminos de fallo distintos sin necesidad. Nota: `correo_habilitado` está en `False` hoy ("funcionalidad diferida" según `shared/config.py`) — confirmar también si sigue siendo así antes de decidir cuál transporte migrar.
- [ ] Reimplementar el envío de correo (hoy `mcp_server/correo.py` vía SMTP) como servicio de Infraestructura del módulo Correo, conservando plantillas y remitente actuales, usando solo el transporte confirmado como real en el punto anterior.
- [ ] Reimplementar `mcp_server/auditoria.py` como servicio transversal de Auditoría, registrando como mínimo los mismos eventos que hoy se auditan (ajustes de margen, envíos de correo, generación de cotizaciones).
- [ ] Comparar visualmente al menos 3 PDFs generados en .NET contra sus equivalentes generados hoy en Python para el mismo folio de prueba, dejando evidencia adjunta en este issue.

### [HU-003.6] API REST con paridad de contrato + Auditoría de acceso
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Depende de HU-003.4 y HU-003.5.**
- [ ] Implementar los endpoints `/web/login`, `/web/mis-cotizaciones`, `/web/cotizaciones/{folio}/enviar` y `/web/cotizaciones/{folio}/seguimiento` reproduciendo exactamente el contrato documentado en HU-004.1 (mismo shape de request/response, mismos códigos de estado).
- [ ] Implementar `/web/chat` en .NET, incluyendo el loop agéntico que orquesta las herramientas de negocio (Tarifas, Margen, Precio, PDF, Correo) — confirmado en HU-003.1 que este endpoint migra completo, no se queda sirviéndose desde Python.
- [ ] Confirmar en la implementación que el LLM solo redacta la respuesta final al usuario y decide qué herramienta invocar — nunca genera ni ejecuta directamente una consulta a la base de datos (regla ya definida en HU-003.1); toda esa lógica vive en las herramientas de negocio ya existentes.
- [ ] Implementar el estado de conversación estructurado (slot-filling) definido en HU-003.1: el identificador de cliente (y demás datos ya resueltos) se guarda en ese estado apenas se conoce, y el orquestador lo reinyecta automáticamente en las siguientes llamadas a herramienta (`obtener_margen`, `generar_pdf`, etc.) sin depender de que el LLM lo recuerde/re-infiera del texto — incluyendo el caso de que "generar_pdf" ocurra en un turno posterior a "obtener_margen" (después de la confirmación del usuario).
- [ ] Cuando el usuario mencione un cliente por nombre en texto libre durante el chat, invocar la herramienta de búsqueda de clientes (HU-005.2, catálogo de Nexo) para resolverlo a un identificador real, en vez de copiar el texto libre directo al payload de la cotización.
- [ ] Escribir un test de integración que reproduzca el escenario exacto del hallazgo en Python (cliente identificado en un turno, confirmación y generación del PDF en el turno siguiente) y confirme que el identificador de cliente llega completo hasta la cotización persistida.
- [ ] Configurar CORS en la nueva API para aceptar el dominio real de `clientepro-frontend`, igual que se exigió en HU-004.1 para el backend Python.
- [ ] Configurar autenticación equivalente a la actual (token Bearer) o la que el equipo decida al integrar con la sesión de ClientePro (ver HU-004, escenario "Sesión única con ClientePro").
- [ ] **Preservar el aislamiento por comercial que ya existe en Python** (`CotizacionRepo.por_comercial` filtra `/web/mis-cotizaciones` por `comercial_email == usuario autenticado`; `enviar_cotizacion` y `marcar_seguimiento` devuelven 404/None si la cotización no es del comercial autenticado): en .NET, un comercial nunca debe poder ver, marcar seguimiento, ni reenviar por correo una cotización de otro comercial. Escribir un test de integración que confirme explícitamente que el comercial A no puede ver ni tocar cotizaciones del comercial B.
- [ ] Probar de punta a punta el módulo Angular de HU-004 contra esta nueva API en un entorno de pruebas, confirmando que no requiere ningún cambio de código para funcionar.

### [HU-003.7] Integraciones externas: SAP TM, Google Drive y Google Sheets
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Depende de HU-003.4.**
- [ ] **Nota (ver HU-005):** el costeo del flete y de servicios conexos pasa a poder venir de Nexo vía la integración de HU-005, en vez del tarifario plano de Drive. Confirmar con el equipo si el tarifario de Drive se retira, se mantiene como respaldo, o se usa solo para líneas de negocio que Nexo no cubre, antes de dar este issue por cerrado.
- [ ] **Nota (ver HU-009):** ya se confirmó que la conexión OData en vivo a SAP TM es necesaria y que su implementación en Python ya funciona (`odata_fuente.py`, probada contra el host real) — no dejar esto como "interfaz sin implementación": reimplementar en .NET el cliente OData completo (consulta a `RentabClienteSet` por sales_org/trq_type/cliente), no solo el modo `export_json`.
- [ ] Reimplementar la lectura del export de SAP TM (`datos/sap_export.json` como referencia de formato) y el cliente OData en vivo (ver nota anterior) como servicios de Infraestructura, intercambiables por configuración igual que hoy en Python (`SAP_TM_MODO=export_json|odata`).
- [ ] Reimplementar la lectura del tarifario desde Google Drive (mismo mecanismo de autenticación de cuenta de servicio que usa hoy Python, ver `secrets/google-sa.json` como placeholder de configuración esperada), solo si el punto anterior confirma que sigue siendo necesario.
- [ ] Reimplementar la sincronización a Google Sheets (para Looker Studio) si el equipo confirma que sigue vigente tras la migración; si ya no se usa, documentar la decisión de no migrarla.
- [ ] Confirmar si la integración con S3/MinIO para almacenamiento de PDFs se mantiene, se reemplaza, o se descarta, y documentar la decisión.

### [HU-003.8] Docker, testing automatizado y documentación técnica
**Labels:** `cotifly`, `backend`, `qa`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend) + Kevin (QA, ronda de regresión)
**Parte de HU-003 (milestone). Depende de HU-003.6.**
- [ ] Crear `Dockerfile` multi-stage (build con SDK, runtime sin SDK) y `docker-compose.yml` para ejecución local, con soporte para desarrollo y QA.
- [ ] Cubrir con pruebas unitarias los módulos de negocio (Tarifas, Margen, Precio, PDF, Correo, Auditoría) y con pruebas de integración los endpoints `/web/*`, ejecutables automáticamente en CI.
- [ ] Escribir la documentación técnica completa exigida por la skill: descripción del proyecto, arquitectura (diagrama de capas y módulos), infraestructura (Docker, variables de entorno, SQL Server, servicios externos), flujo del sistema, stack tecnológico, ejecución local, API (endpoints/request/response/errores/auth), testing y estructura del proyecto — sin emojis, apta para un equipo profesional.
- [ ] Ejecutar con Kevin (QA) una ronda de regresión manual de los 3 flujos principales (nueva cotización, mis cotizaciones, envío de correo) contra el backend .NET desplegado en un entorno de pruebas, antes de considerar la migración lista para producción.

### [HU-003.9] Plan y ejecución de pruebas QA de paridad funcional (.NET vs Python)
**Labels:** `cotifly`, `qa`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA)
**Parte de HU-003 (milestone). Depende de HU-003.6.**
- [ ] Definir con Kevin (QA) un set fijo de al menos 10 casos de prueba reales, cubriendo distintas líneas de negocio, para comparar el resultado exacto (tarifa, margen, precio, PDF generado) entre el backend Python actual y el nuevo backend .NET.
- [ ] Ejecutar ese set de pruebas contra ambos backends y documentar cualquier diferencia encontrada, priorizándola con el equipo antes de considerar la migración funcionalmente completa.
- [ ] Probar explícitamente los 5 endpoints `/web/*` del nuevo backend (con Postman o equivalente), confirmando los mismos códigos de estado y shape de respuesta que el contrato congelado en HU-004.1.
- [ ] Dejar registrada en este issue la aprobación explícita de QA antes de que HU-006 (despliegue) considere el backend .NET listo para producción.

### [HU-003.10] Validador de respuestas de IA (Instructor)
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-003 (milestone). Depende de HU-003.6.**
- [ ] Incorporar un validador de respuestas del LLM (librería *Instructor* o su equivalente en .NET) que fuerce cada respuesta del agente a ajustarse a un esquema estructurado definido (ej. tipo de acción, herramienta a invocar, texto de respuesta), en vez de aceptar texto libre sin validar.
- [ ] Definir el esquema esperado por tipo de interacción del agente (ej. respuesta conversacional simple, invocación de herramienta, mensaje de error) y documentarlo.
- [ ] Si la respuesta del LLM no pasa la validación del esquema, definir el comportamiento de reintento/fallback (ej. reintentar una vez con un mensaje de corrección, o responder con un mensaje de error controlado al usuario) — nunca dejar pasar una respuesta sin validar al flujo de negocio.
- [ ] Probar el validador con al menos 3 casos de respuesta malformada simulada (ej. JSON incompleto, campo con tipo incorrecto, alucinación de una herramienta que no existe) y confirmar que el sistema reacciona según lo definido, sin romperse.
- [ ] Documentar esta validación como parte de la arquitectura del agente en la documentación técnica de HU-003.8.

---

## HU-004 — Migración del módulo web de Cotifly a Angular 17.3.8 dentro de ClientePro

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E2 — Migración de Frontend a ClientePro · **Talla:** L · **Roles:** Backend (Duban), Frontend (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly` + `clientepro-frontend`

### Historia
**Como** comercial de Botero Soto, **quiero** generar, consultar y hacer seguimiento a mis cotizaciones de flete desde el mismo portal ClientePro que ya uso a diario, **para** no tener que entrar a una página aparte (`chat.html`, servida hoy por el propio backend de Cotifly) para cotizar.

### Contexto técnico (levantado del código actual)
- El frontend actual de Cotifly es un único archivo `orquestador/static/chat.html` (HTML + CSS + JS vanilla embebido, sin framework, sin build), servido directamente por el backend FastAPI. No es un proyecto Angular ni tiene componentización — la migración es una **reconstrucción como módulo Angular**, no una traducción línea a línea.
- Funcionalidades cubiertas hoy por `chat.html`: login propio del bot (correo + nombre, sin password), formulario de "Nueva cotización" (con resultado paso a paso: Tarifa → Margen → Precio → PDF, y descarga del PDF generado), listado "Mis cotizaciones" (marcar efectiva/no efectiva, reenviar por correo), y un "Asistente" de chat conversacional con el bot.
- Endpoints reales consumidos hoy (backend `orquestador`, repo `Cotifly`): `POST /web/login`, `POST /web/chat`, `GET /web/mis-cotizaciones`, `POST /web/cotizaciones/{folio}/enviar`, `POST /web/cotizaciones/{folio}/seguimiento`. Auth actual: token Bearer devuelto por `/web/login`, sin mecanismo de guard formal.
- Destino: `clientepro-frontend`, Angular objetivo **17.3.8** (el `package.json` actual mezcla paquetes `@angular/*` en `^17.3.8` con Angular Material todavía en `16.1.4`). **Restricción confirmada con el equipo: no se toca el `package.json` raíz ni ninguna dependencia compartida de ClientePro** — el módulo nuevo se construye con las versiones que ya están instaladas hoy (incluida Angular Material `16.1.4`), sin actualizar nada del resto del proyecto; solo se puede agregar lo propio del módulo nuevo. Patrón modular a seguir: `src/app/components/informes-embarque/` (único módulo ya modularizado correctamente en ese repo — routing propio, `*.module.ts`, `pages/`, `components/`, `models/`, `services/`). El `CLAUDE.md` que el README de `clientepro-frontend` menciona como fuente de reglas de arquitectura no existe físicamente en el repo; se usa `informes-embarque/` como estándar de facto en su lugar.
- Autenticación de ClientePro hoy es ad hoc (localStorage `usuario`/`roles` + `user-role.service.ts`, sin `canActivate` guards formales) — el nuevo módulo debe integrarse a ese mismo esquema de sesión, no mantener el login propio del bot.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Estructura modular del nuevo módulo
Dado que se crea el módulo de Cotifly dentro de clientepro-frontend
Cuando se revisa su estructura de carpetas
Entonces sigue exactamente el patrón de components/informes-embarque/ (module.ts, routing propio, pages/, components/, models/, services/)
Y no se agrega lógica de acceso a datos directamente en los componentes (toda llamada HTTP pasa por un servicio)

Escenario: Sesión única con ClientePro
Dado un comercial ya autenticado en ClientePro (localStorage usuario/roles)
Cuando entra al módulo de cotización
Entonces no se le pide un login aparte (el propio de chat.html desaparece)
Y el módulo reutiliza la sesión/usuario ya activo para identificar quién cotiza

Escenario: Nueva cotización
Dado un comercial en la vista "Nueva cotización" del nuevo módulo
Cuando completa el formulario (origen, destino, peso, tipo de vehículo, cliente, ID SAP opcional) y presiona "Cotizar"
Entonces ve el progreso paso a paso (Tarifa → Margen → Precio → PDF) igual que en chat.html
Y al completarse puede descargar el PDF generado

Escenario: Mis cotizaciones
Dado un comercial en la vista "Mis cotizaciones"
Cuando abre el listado
Entonces ve sus cotizaciones con folio, cliente y estado de seguimiento
Y puede marcarlas como "Efectiva" / "No efectiva" / limpiar el estado, y reenviarlas por correo, sin recargar la página

Escenario: Asistente conversacional
Dado un comercial en la vista "Asistente"
Cuando escribe un mensaje libre pidiendo una cotización
Entonces el módulo mantiene una sesión de conversación (sesion_id) y muestra la respuesta del bot con el mismo render de markdown básico que tiene chat.html hoy
Y si el bot genera un PDF durante la conversación, aparece como enlace de descarga en el propio chat

Escenario: Acceso desde el menú de ClientePro
Dado un comercial autenticado en ClientePro
Cuando revisa el menú de navegación
Entonces encuentra un nuevo ítem "Cotizador" que lo lleva al módulo migrado
Y el acceso respeta el mismo esquema de roles que ya usa ClientePro (visible únicamente para el rol comercial — decisión ya confirmada, ningún otro rol lo ve)
```

### Trazabilidad (event storming)
Backend consumido: API web de Cotifly (`POST /web/login`, `POST /web/chat`, `GET /web/mis-cotizaciones`, `POST /web/cotizaciones/{folio}/enviar`, `POST /web/cotizaciones/{folio}/seguimiento`) — repo `Cotifly` (orquestador). Fuente visual a reemplazar: `orquestador/static/chat.html`. Patrón de referencia: `clientepro-frontend/src/app/components/informes-embarque/`. Esquema de sesión a reutilizar: `user-role.service.ts` + `localStorage` de `clientepro-frontend`.

### Alcance en este repo (Cotifly)
1. Congelar y documentar el contrato real de los 5 endpoints `/web/*` que hoy consume `chat.html`, para que el equipo frontend no tenga que leer el código Python para integrar.
2. Confirmar y ajustar CORS del backend `orquestador` para aceptar el dominio real de `clientepro-frontend` (hoy `chat.html` funciona porque se sirve desde el propio backend; al vivir en otro dominio/proyecto Angular deja de ser "same-origin").
3. Definir junto al equipo si el login propio del bot (`POST /web/login`) se mantiene tal cual (el nuevo módulo lo llama por detrás usando el correo ya logueado en ClientePro) o si se reemplaza por un mecanismo que confíe en la sesión que ClientePro ya validó — documentar la decisión.
4. `chat.html` y `orquestador/static/` se retiran (o se dejan solo como fallback temporal, a decidir) una vez el módulo Angular esté en producción — no se borra en esta HU, queda como tarea de limpieza posterior.

**HU también en:** `clientepro-frontend` (ahí va **todo** el desarrollo del módulo Angular).

#### Issues en Cotifly

### [HU-004.1] Congelar y documentar el contrato de la API web de Cotifly para el frontend
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-004 (milestone).**
- [ ] Ejecutar cada uno de los 5 endpoints `/web/*` (`login`, `chat`, `mis-cotizaciones`, `cotizaciones/{folio}/enviar`, `cotizaciones/{folio}/seguimiento`) y capturar el request/response real de cada uno, incluyendo casos de error (401, folio inexistente, etc.).
- [ ] Documentar el mecanismo de auth actual (`Bearer` emitido por `/web/login`, sin password) y dejar explícita la decisión tomada con el equipo sobre si se mantiene así o se adapta a la sesión de ClientePro.
- [ ] Confirmar y, si hace falta, agregar el dominio real de `clientepro-frontend` a la configuración CORS del backend `orquestador`.
- [ ] Documentar el manejo de `sesion_id` en `/web/chat` (cómo se genera, cuánto vive, qué pasa si expira).
- [ ] Entregar al equipo frontend un documento (comentario en este issue o página en Notion) con los 5 contratos capturados.
- [ ] Cerrar este issue solo cuando el equipo frontend confirme que recibió el contrato.

---

#### Borrador espejo — PARA CREAR EN `clientepro-frontend` (no en Cotifly)

> **HU en Notion:** mismo link de HU-004 arriba
> **Sprint:** Sprint 4 · **Épica:** E2 — Migración de Frontend a ClientePro · **Talla:** L · **Roles:** Frontend (Sara), QA (Kevin)

**Alcance en este repo:** todo el desarrollo del nuevo módulo Angular. **HU también en:** `Cotifly` (contrato de API + CORS, ver HU-004.1 de ese repo).

### [HU-004.2] Confirmar que el módulo nuevo se construye con las dependencias ya instaladas, sin tocar nada compartido
**Labels:** `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Restricción confirmada con el equipo: no se modifica el `package.json` raíz ni ninguna dependencia compartida de ClientePro — solo se puede agregar lo propio del módulo nuevo, sin afectar el resto del proyecto.**
- [ ] Confirmar qué versión de Angular Material (`16.1.4`) y de `@angular/core` (`^17.3.8`) están realmente instaladas hoy en `node_modules`/`package-lock.json` (no solo lo que dice `package.json`), ya que ese es el set de versiones con el que el módulo nuevo tiene que trabajar tal cual está.
- [ ] Construir el módulo nuevo usando únicamente esas versiones ya instaladas — ningún componente de Angular Material que solo exista en una versión más nueva que `16.1.4`.
- [ ] Si en algún punto de HU-004.3 en adelante se necesita un componente/API que no existe en `16.1.4`, no actualizar la dependencia por cuenta propia: documentarlo en este issue como limitación conocida y buscar una alternativa dentro de lo ya disponible (o escalarlo aparte, fuera de esta HU, a quien sí tenga autoridad sobre las dependencias compartidas de ClientePro).
- [ ] Confirmar que `ng build` y `ng serve` siguen funcionando sin errores nuevos una vez agregado el módulo (sin haber tocado el `package.json` raíz).

### [HU-004.3] Estructura del módulo `cotifly` y servicio HTTP
**Labels:** `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Depende de HU-004.2 y de HU-004.1 (Cotifly).**
- [ ] Crear `components/cotifly/` replicando exactamente la estructura de `components/informes-embarque/`: `cotifly.module.ts`, `cotifly-routing.module.ts`, `pages/`, `components/`, `models/`, `services/`.
- [ ] Crear `models/` con las interfaces TypeScript de los payloads/respuestas documentados en HU-004.1 de Cotifly (cotización, folio, seguimiento, mensaje de chat).
- [ ] Crear `services/cotifly.service.ts` con un método por endpoint (`login` si aplica, `chat`, `misCotizaciones`, `enviarCorreo`, `actualizarSeguimiento`), sin lógica de UI dentro del servicio.
- [ ] Registrar la ruta del módulo en el routing principal, protegida con el mismo esquema de roles que ya usa `user-role.service.ts`.
- [ ] Agregar el ítem de menú "Cotizador" en la navegación, visible únicamente para el rol comercial (ya confirmado con negocio; ningún otro rol lo ve, ni siquiera en modo lectura).
- [ ] Al entrar por primera vez al módulo, si el backend indica que el comercial no tiene `celular` guardado (ver HU-002.1), mostrar un modal de una sola pregunta ("¿Cuál es tu número de celular? Aparecerá en tus cotizaciones") antes de dejarlo generar su primera cotización — guardarlo una sola vez, nunca volver a pedirlo salvo que el comercial lo edite explícitamente.

### [HU-004.4] Vista "Nueva cotización"
**Labels:** `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Depende de HU-004.3. Esta versión cubre el formulario actual (6 campos, MVP); la ampliación con los campos de Nexo (vehículo, carrocería, contenedor, cubicaje, cliente por catálogo) es HU-005, no se agrega aquí.**
- [ ] Página con el formulario equivalente al de `chat.html`: origen, destino, peso, tipo de vehículo/carga, cliente, ID SAP (opcional), y el campo de texto libre para pegar la solicitud completa.
- [ ] Componente de progreso con los 4 pasos (Tarifa, Margen, Precio, PDF) reflejando el estado real devuelto por el backend, igual que `pasosHTML()` en `chat.html`.
- [ ] Botón de descarga del PDF una vez generado (usar la URL que devuelve el backend).
- [ ] Manejo de error visible (toast o mensaje inline) si el backend responde error en cualquier paso, sin dejar el formulario en un estado de carga infinito.

### [HU-004.5] Vista "Mis cotizaciones"
**Labels:** `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Depende de HU-004.3.**
- [ ] Tabla (Angular Material `mat-table`, consistente con el resto del portal) con folio, cliente y estado de seguimiento.
- [ ] Acciones por fila: marcar "Efectiva", "No efectiva", "Limpiar", y "Enviar" (reenvío de correo), llamando a los endpoints correspondientes y refrescando la fila sin recargar toda la tabla.
- [ ] Mostrar la nota de seguimiento (`seguimiento_nota`) cuando exista.
- [ ] Spinner mientras carga el listado; mensaje "No tienes cotizaciones registradas" si vuelve vacío.

### [HU-004.6] Vista "Asistente" (chat conversacional)
**Labels:** `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Depende de HU-004.3.**
- [ ] Componente de chat con historial de mensajes (usuario/bot) y `sesion_id` generado en el cliente (igual que `nuevaSesion()` en `chat.html`), reiniciable manualmente.
- [ ] Render de markdown básico de las respuestas del bot (listas, negrita, enlaces) — reutilizar o reimplementar la lógica mínima de `mdInline()`/`mdBlock()` de `chat.html`.
- [ ] Si la respuesta del bot incluye un PDF generado, mostrarlo como enlace de descarga dentro de la burbuja del chat.
- [ ] Manejo de sesión expirada (401): mostrar aviso claro y permitir reiniciar sesión de chat sin perder la sesión de ClientePro.

### [HU-004.7] Retirar `chat.html` una vez el módulo esté validado en producción
**Labels:** `frontend`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly` (aunque queda listada junto a los issues de clientepro-frontend por orden numérico, esta tarea es 100% de Cotifly)
**Asignado a:** Duban (Backend — es tarea de Cotifly, no de clientepro-frontend, ver nota de repo arriba)
**Depende de HU-004.4, HU-004.5 y HU-004.6 en producción.**
- [ ] Confirmar con negocio que ningún usuario sigue usando la URL de `chat.html` directamente (revisar logs de acceso del backend `orquestador` si existen).
- [ ] Retirar el montaje de `orquestador/static/` en el backend (o dejarlo redirigiendo al nuevo módulo en ClientePro) — coordinar este paso como tarea de Cotifly, no de este repo.
- [ ] Actualizar el `README.md` de `Cotifly` quitando la referencia a la consola web propia una vez retirada.

### [HU-004.8] Pruebas QA del módulo migrado
**Labels:** `frontend`, `qa`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend) + Kevin (QA)
**Parte de HU-004 (milestone). Depende de HU-004.4, HU-004.5 y HU-004.6.**
- [ ] Ejecutar y documentar casos de prueba manuales para: login integrado (sin pedir credenciales aparte de la sesión de ClientePro), nueva cotización (formulario completo, pasos Tarifa→Margen→Precio→PDF, descarga del PDF), mis cotizaciones (marcar efectiva/no efectiva/limpiar, reenviar correo), asistente (mensaje libre, sesión, render de markdown, manejo de sesión expirada).
- [ ] Confirmar que el ítem de menú "Cotizador" respeta el control de roles definido en HU-004.3 (solo visible para el rol comercial), probando con un usuario comercial y con al menos un usuario de otro rol para confirmar que no lo ve.
- [ ] Ejecutar una regresión manual de al menos 2 módulos ya existentes de ClientePro (ej. `tablero-gestion`, `informes-embarque`) tras agregar el módulo nuevo, confirmando que nada se rompió fuera de él — no debería haber cambios en el resto del proyecto, ya que HU-004.2 no toca dependencias compartidas.
- [ ] Registrar cualquier defecto encontrado como issue aparte, referenciado desde este issue; no cerrar este issue mientras queden defectos críticos sin resolver.

---

## HU-005 — Integración Cotifly ↔ Nexo: envío de solicitudes y consulta del costeo (polling)

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E3 — Integración Cotifly ↔ Nexo · **Talla:** XL · **Roles:** Frontend (Sara), Backend (Duban), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly` + `clientepro-frontend` + `NEXO`

### Historia
**Como** comercial que hoy usa tanto Nexo (`cabina-fletes`, solicitudes spot/licitación) como Cotifly, **quiero** crear la solicitud desde el formulario de "Nueva cotización" de Cotifly (con el mismo nivel de detalle que el de Nexo) y que esa solicitud se envíe automáticamente a Nexo, para que el admin de fletes la costee (o ajuste) allá, y ver el resultado reflejado de vuelta en Cotifly sin tener que entrar a Nexo, **para** cotizar al cliente final con el costo de flete y servicios conexos ya validados por operaciones, aplicando el margen comercial y el precio final desde Cotifly.

### Arquitectura del flujo (confirmada con el equipo)
1. El comercial crea la solicitud en Cotifly (formulario ampliado, alineado a los campos de Nexo — ver HU-005.1 a HU-005.4).
2. Cotifly envía la solicitud a Nexo consumiendo directamente su backend (`POST /captura-solicitudes` de `cabina-api`, o el endpoint que se confirme) — sigue siendo HTTP, pero sin ninguna capa de integración especial de por medio: se llaman los mismos endpoints que ya usa el frontend de Nexo.
3. Nexo la recibe como una solicitud normal de su Mesa de Costeo. Si el admin de fletes necesita ajustar algo, lo hace directamente en Nexo.
4. **Nexo calcula y es dueño de:** el costeo del flete y el precio de los servicios conexos (costo operativo).
5. **Cotifly conserva y es dueño de:** la política de margen, la tarifa final y el precio al cliente — igual que hoy (módulo Margen/Precio de HU-003.4), pero usando como "costo" el valor que devuelve Nexo en vez del tarifario plano de Google Drive.
6. **Sin webhook:** Cotifly consulta periódicamente (polling) el estado/costeo de la solicitud directamente en el backend de Nexo, igual que ya hace el propio frontend de Nexo hoy (polling cada pocos segundos hasta un máximo de intentos) — Nexo no tiene que construir ni exponer nada nuevo para esta integración, Cotifly solo consume lo que ya existe.
7. Cuando el polling detecta que el costeo ya está disponible, Cotifly aplica su margen y calcula la tarifa final/precio al cliente, y el comercial ve el resultado actualizado en "Mis cotizaciones" sin salir de Cotifly.

**Nota de reparto de responsabilidades (confirmada con el equipo):** Nexo ya tiene su propia "Bandeja" de solicitudes (capacidad `verBandeja` del rol Comercial en `administracion_embarqa_nexo`) — esa vista **no se reemplaza ni se duplica**. Se conservan ambas, porque muestran cosas distintas por **estado**:
- **Bandeja de Nexo:** solo las solicitudes **pendientes** — las que todavía están esperando que se les devuelva el valor del flete (costeo en curso, sin margen aplicado todavía).
- **"Mis cotizaciones" de Cotifly:** las cotizaciones que **ya tienen** el costeo de Nexo aplicado más el margen de Cotifly — es decir, las que ya llegaron a precio final para el cliente.

Una solicitud nace en la bandeja de Nexo (pendiente de flete) y, cuando el polling de Cotifly detecta que el costeo ya está listo, "aparece"/se actualiza en "Mis cotizaciones" de Cotifly ya con margen y precio final — no es que una vista sustituya a la otra, es la misma solicitud vista en dos momentos/roles distintos del proceso.

### Contexto técnico (formularios comparados)
- **Nexo** (`administracion_embarqa_nexo`, módulo `cabina-fletes`, página `captura` — rol Comercial, capacidad `verCaptura`): formulario "Nueva solicitud" con encabezado (`tipo` spot/licitación, `deadline`, `horasCargue`, `horasDescargue`) + N "líneas a costear" (`origen`, `destino`, `peso`, `valorMercancia`, `lineaNegocio`, `claseViaje`, `tipoVehiculo` —catálogo cerrado tipo SICE-TAC: `2`,`2_5`,`2_8`,`2L1`,`2L2`,`2L3`,`2S2`,`2S3`,`3`,`3S2`,`3S3`,`CA`,`V2`,`V3`,`V4`—, `carroceria` —ESTACAS/ESTIBAS/FURGON/FURGON REFRIGERADO/PLATAFORMA/PORTACONTENEDORES/TANQUE/TRAYLER/VOLCO—, `claseContenedor` —CNT20.IT/RE/ST, CNT40.HC/RE—, `exclusivo`, `consolidado`, `retornaContenedor` + `ciudadDevolucion`, `levanteContenedorDiferente` + `ciudadLevante`, `valorAprovisionamiento`) + N "posiciones" por línea (`descripcion`, `cantidad`, `largo`, `ancho`, `alto`, `peso`, en cm/kg, usadas para cubicaje 3D). Cliente se selecciona por autocompletado (`clienteId`/`clienteBp`) contra `GET /captura-solicitudes/clientes?q=`, no es texto libre.
- **Cotifly** (formulario "Nueva cotización" migrado en HU-004.4): hoy solo 6 campos de texto libre (`origen`, `destino`, `peso`, `tipo de vehículo/carga` en texto libre, `cliente` en texto libre, `ID SAP` opcional), más un modo de texto libre completo que el agente LLM interpreta.
- **Decisión ya tomada con el equipo:** se lleva el set completo de campos de Nexo a Cotifly (no solo alinear nombres); el campo cliente pasa a usar el mismo mecanismo de autocompletado por catálogo; y se **mantienen ambos modos de captura** en Cotifly (texto libre interpretado por el agente + formulario estructurado ampliado), igual que Nexo mantiene manual + importar Excel como modos alternativos.
- **Decisión ya tomada con el equipo:** Cotifly consulta el catálogo de clientes llamando al backend de Nexo (repo `NEXO`, endpoint `/captura-solicitudes/clientes` o el que se confirme) — no se construye un endpoint propio en Cotifly. Hoy ese endpoint en Nexo toma el cliente de `DimBpSolicitante`; se debe ampliar en el repo `NEXO` para que también combine con la tabla `tblProspectos` (base de datos `dbSolucionesTI`), de forma que el autocompletado devuelva resultados de ambas fuentes. Ver HU-005.2 y el borrador espejo para el repo `NEXO` al final de esta HU.
- **Decisión ya tomada con el equipo:** el costeo del flete y de los servicios conexos pasa a venir de Nexo (vía la API de esta HU), no del tarifario plano de Google Drive que usa hoy `mcp_server/tarifas/` (HU-003.4/HU-003.7 en `Cotifly`) — a confirmar en HU-003.7 si el tarifario de Drive se retira, se mantiene como fuente de respaldo, o se usa solo para líneas de negocio que Nexo no cubre.
- **Pendiente de decidir con el equipo:** el mecanismo exacto de autenticación con el que Cotifly va a llamar a los endpoints de Nexo (API key compartida, JWT, IP allowlist, etc.), tanto para enviar la solicitud como para el polling periódico de estado. Ver HU-005.5 y HU-005.6.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Mapeo de campos documentado antes de construir
Dado el listado completo de campos de Nexo y de Cotifly
Cuando se redacta el mapeo (HU-005.1)
Entonces cada campo de Nexo queda clasificado explícitamente como "ya existe en Cotifly (mismo nombre/catálogo)", "nuevo campo a agregar" o "no aplica a Cotifly, con justificación"
Y ese mapeo se aprueba con el equipo antes de tocar el formulario Angular

Escenario: Selector de cliente unificado
Dado un comercial llenando el formulario de "Nueva cotización" en Cotifly
Cuando busca un cliente por nombre, NIT o código
Entonces obtiene resultados de un catálogo real (no un campo de texto libre), igual que en Nexo
Y el folio de la cotización queda asociado a un cliente identificado, no a un nombre escrito a mano

Escenario: Formulario ampliado con campos estructurados de Nexo
Dado el formulario "Nueva cotización" ya ampliado
Cuando el comercial selecciona clase de viaje "Cupo"
Entonces el campo tipo de vehículo (catálogo SICE-TAC) se vuelve requerido, igual que en Nexo
Y si selecciona clase de viaje "Contenedor", aparecen los campos de clase de contenedor, retorno de contenedor y ciudad de devolución, con el mismo comportamiento condicional que tiene Nexo

Escenario: Cubicaje por posiciones opcional
Dado un comercial que conoce las dimensiones de la mercancía
Cuando agrega una o más posiciones (cantidad, largo, ancho, alto, peso)
Entonces el formulario permite calcular/sugerir el cubicaje total, igual que en Nexo
Y si no agrega ninguna posición, el formulario sigue funcionando solo con el peso total (comportamiento actual de Cotifly)

Escenario: Ambos modos de captura conviven
Dado un comercial en el formulario de "Nueva cotización"
Cuando prefiere pegar la solicitud en lenguaje natural en vez de llenar el formulario campo por campo
Entonces puede seguir haciéndolo (el agente LLM la interpreta), sin que la ampliación de campos estructurados elimine esa opción

Escenario: Envío de la solicitud a Nexo
Dado un comercial que completa y confirma el formulario de "Nueva cotización" en Cotifly
Cuando lo envía
Entonces Cotifly hace POST a la API de Nexo con el payload equivalente al de `CapturaSolicitudRequest`, y guarda el identificador de la solicitud devuelto por Nexo junto a la cotización en Cotifly
Y si Nexo responde con error, el comercial ve un mensaje claro y la cotización queda marcada para reintentar el envío, sin perder los datos ya capturados

Escenario: Cotifly detecta el costeo vía polling
Dado que el admin de fletes en Nexo completa o ajusta el costeo (flete + servicios conexos) de una solicitud creada desde Cotifly
Cuando el proceso de polling de Cotifly consulta el estado de esa solicitud directamente en el backend de Nexo
Entonces detecta que el costeo ya está disponible y actualiza el estado de la cotización correspondiente
Y si la solicitud consultada ya no existe o Nexo responde con un identificador no reconocido, el error queda registrado en auditoría sin detener el resto del proceso de polling

Escenario: Cotifly aplica su margen sobre el costeo recibido
Dado un costeo de flete + servicios conexos ya recibido desde Nexo para una solicitud
Cuando Cotifly procesa esa notificación
Entonces aplica la política de margen vigente (módulo Margen de HU-003.4) sobre ese costo, calcula la tarifa final y el precio al cliente, y genera/actualiza el PDF de la cotización
Y el costo bruto que llega de Nexo nunca se muestra directamente al comercial como si fuera el precio final, sin pasar primero por el margen

Escenario: El comercial ve el resultado sin salir de Cotifly
Dado que Nexo ya devolvió el costeo de una solicitud
Cuando el comercial abre "Mis cotizaciones" en Cotifly
Entonces ve el estado actualizado (ej. "en costeo", "costeado", "ajustado por operaciones") y el precio final ya calculado, sin necesidad de entrar a Nexo para consultarlo

Escenario: Aislamiento por comercial en "Mis cotizaciones" de Cotifly
Dado que el comercial A y el comercial B tienen cada uno sus propias cotizaciones
Cuando el comercial A revisa "Mis cotizaciones" en Cotifly
Entonces no ve las cotizaciones del comercial B (comportamiento que Cotifly ya garantiza hoy vía comercial_email — ver HU-003.6)
```

**Nota:** la Bandeja de Nexo hoy **no** filtra por comercial (un comercial ve las solicitudes de todos) — es un hallazgo real, no una hipótesis. Ese fix se registra aparte, en **HU-008** (independiente de esta HU, porque es un problema de seguridad ya existente en Nexo hoy, no algo que dependa de que la integración con Cotifly avance).

### Trazabilidad (event storming)
Fuente de comparación y de integración: `administracion_embarqa_nexo/src/app/cabina-fletes/pages/captura/` (componente, modelo `captura-api.models.ts`, servicio `captura-api.service.ts`), endpoint `POST /captura-solicitudes` de Nexo (`cabina-api`). Consumido/generado por: módulo Margen y Precio de HU-003.4 (`Cotifly`), que pasa a usar el costeo de Nexo como fuente de "costo" en vez del tarifario plano de Google Drive (ver nota cruzada en HU-003.7). Extiende: HU-004.4 (Vista "Nueva cotización" en `clientepro-frontend`) y HU-004.5 (Vista "Mis cotizaciones", que debe reflejar el estado que llega de Nexo). Requiere cambios de esquema (nuevas columnas, tabla de posiciones/cubicaje, e identificador de la solicitud en Nexo) coordinados con HU-002.

### Alcance en este repo
Este alcance es transversal: **frontend** en `clientepro-frontend` (formulario ampliado y estado en "Mis cotizaciones"), **backend/integración** en `Cotifly` (llamada directa al backend de Nexo, polling periódico de estado/costeo, aplicación de margen sobre el costeo recibido).

1. Mapear campo por campo Nexo vs. Cotifly y definir catálogos a reutilizar (HU-005.1).
2. Definir el mecanismo de búsqueda de clientes a usar en Cotifly (HU-005.2).
3. Ampliar el formulario Angular con los campos estructurados y su comportamiento condicional (HU-005.3).
4. Agregar cubicaje por posiciones (HU-005.4).
5. Implementar el envío de la solicitud directamente al backend de Nexo (HU-005.5).
6. Implementar el polling periódico de estado/costeo directamente contra el backend de Nexo (HU-005.6).
7. Aplicar el margen sobre el costeo recibido y calcular tarifa final/precio al cliente, actualizando "Mis cotizaciones" (HU-005.7).
8. Ajustar el esquema de datos para persistir los nuevos campos, el identificador de la solicitud en Nexo y su estado (HU-005.8, coordinado con HU-002).

**HU también en:** `clientepro-frontend` (todo el desarrollo de UI de esta HU vive ahí, dentro del módulo ya creado en HU-004) y `NEXO` (ajuste del buscador de clientes para combinar `DimBpSolicitante` con `tblProspectos`, ver borrador espejo al final de esta HU, y confirmar el endpoint de recepción de solicitudes y el de consulta de estado/costeo para el polling — Nexo no necesita construir nada nuevo, solo confirmar sus endpoints ya existentes).

#### Issues

### [HU-005.1] Mapeo de campos entre Nexo y Cotifly, y catálogos a reutilizar
**Labels:** `cotifly`, `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly` (el resultado de este mapeo lo usa también `clientepro-frontend` para construir el formulario en HU-005.3)
**Asignado a:** Duban (Backend) + Sara (Frontend, consume el mapeo en HU-005.3)
**Parte de HU-005 (milestone). Gatilla el resto de issues de esta HU.**
- [ ] Construir una tabla campo por campo: columna de Nexo, campo equivalente en Cotifly (si existe), tipo de dato, catálogo/opciones, y clasificación (`ya existe`/`nuevo`/`no aplica`).
- [ ] Para cada catálogo cerrado de Nexo (tipos de vehículo SICE-TAC, carrocerías, clases de contenedor, líneas de negocio), decidir si Cotifly los reutiliza tal cual (mismos códigos/textos) o los adapta, y documentar la decisión.
- [ ] Confirmar con negocio si el concepto `tipo` (spot/licitación) y `lineaNegocio` de Nexo tienen equivalente en el flujo de Cotifly, o si son conceptos propios de Nexo sin traslado directo.
- [ ] Presentar la tabla de mapeo al equipo y dejar registrada la aprobación en este issue antes de cerrarlo.

### [HU-005.2] Construir el buscador de clientes en Cotifly, consumiendo el endpoint de Nexo
**Labels:** `cotifly`, `backend`, `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly` (el 3er punto del checklist — el autocompletado en el formulario Angular — se implementa en `clientepro-frontend`)
**Asignado a:** Duban (Backend) + Sara (Frontend, 3er punto del checklist)
**Parte de HU-005 (milestone). Depende de HU-005.1 y de la ampliación del endpoint de clientes en el repo `NEXO` (ver borrador espejo al final de esta HU).**
- [ ] Confirmar con el equipo de Nexo el contrato exacto del endpoint de clientes (`/captura-solicitudes/clientes` u otro) una vez ampliado para combinar `DimBpSolicitante` y `tblProspectos`, incluyendo qué campo identifica el origen de cada resultado (para poder distinguir un prospecto de un cliente ya facturado, si aplica).
- [ ] Implementar en el backend de Cotifly el consumo de ese endpoint de Nexo (incluyendo CORS/autenticación entre sistemas — ver HU-005.5/HU-005.6 para el mecanismo de autenticación general entre Cotifly y Nexo).
- [ ] Reemplazar el campo de texto libre `cliente`/`ID SAP` del formulario Angular por un autocompletado (mismo patrón de UX que Nexo: búsqueda por nombre/NIT/código, debounce, mínimo de caracteres).
- [ ] Guardar en la cotización el identificador real del cliente seleccionado (no solo el nombre en texto), ajustando el esquema de datos según lo que resulte de HU-005.5.

### [HU-005.3] Ampliar el formulario "Nueva cotización" con los campos estructurados de Nexo
**Labels:** `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-005 (milestone). Depende de HU-005.1.**
- [ ] Agregar al formulario Angular los campos de encabezado: `tipo` (spot/licitación), `deadline`, `horasCargue`, `horasDescargue`, según el mapeo aprobado en HU-005.1.
- [ ] Agregar los campos de línea: `tipoVehiculo` (catálogo SICE-TAC), `carroceria`, `claseViaje`, `claseContenedor`, `lineaNegocio`, `valorMercancia`, `exclusivo`, `consolidado`, `retornaContenedor` + `ciudadDevolucion` condicional, `levanteContenedorDiferente` + `ciudadLevante` condicional — replicando el mismo comportamiento condicional (mostrar/ocultar y requerido/opcional) que tiene Nexo.
- [ ] Mantener visible y funcional el modo de texto libre existente (textarea interpretado por el agente LLM) como alternativa al formulario estructurado, igual que Nexo mantiene manual + importar Excel.
- [ ] Validar con al menos 3 casos reales de cotización (uno spot, uno con contenedor, uno con cupo/vehículo específico) que el formulario ampliado se comporta igual que el de Nexo para esos mismos casos.

### [HU-005.4] Cubicaje por posiciones en el formulario de Cotifly
**Labels:** `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-005 (milestone). Depende de HU-005.3.**
- [ ] Agregar el sub-formulario de "posiciones" (FormArray: `descripcion`, `cantidad`, `largo`, `ancho`, `alto`, `peso`) dentro de cada línea de cotización, igual que en Nexo.
- [ ] Decidir con el equipo si se reutiliza el mismo endpoint de cubicación 3D de Nexo (`POST /cubicacion`) o se construye uno propio para Cotifly, y documentar la decisión.
- [ ] Confirmar que, si no se agrega ninguna posición, el formulario sigue funcionando solo con el campo de peso total (no romper el flujo actual simplificado).

### [HU-005.5] Envío de la solicitud directamente al backend de Nexo
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-005 (milestone). Depende de HU-005.1 a HU-005.4 (el payload debe ya estar completo y validado).**
- [ ] Coordinar con el equipo de Nexo (repo `NEXO`, backend `cabina-api`) la confirmación del endpoint real a usar (`POST /captura-solicitudes`, el mismo que ya usa el frontend de Nexo — no un endpoint especial de integración) y el mecanismo de autenticación (API key, JWT de servicio, IP allowlist, etc.).
- [ ] Implementar en el backend de Cotifly el cliente HTTP que arma el payload `CapturaSolicitudRequest`/`CapturaLineaRequest`/`CapturaPosicionRequest` a partir de los datos capturados en el formulario ampliado.
- [ ] Guardar en la cotización de Cotifly el identificador de la solicitud devuelto por Nexo (`id`) para poder consultar su estado más adelante (ver HU-005.6).
- [ ] Manejar explícitamente los casos de error de Nexo (timeout, 4xx, 5xx): la cotización queda en un estado claro (ej. "pendiente de envío"/"error al enviar") y el comercial puede reintentar sin volver a llenar el formulario.

### [HU-005.6] Polling: consulta periódica del estado/costeo directamente en Nexo
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-005 (milestone). Depende de HU-005.5 (necesita el identificador de la solicitud en Nexo para consultar su estado).**
- [ ] Confirmar con el equipo de Nexo el endpoint real para consultar el estado/costeo de una solicitud ya creada (ej. `GET /captura-solicitudes/{id}`, el mismo que ya usa el frontend de Nexo para su propio polling) y el shape de la respuesta, tanto cuando el costeo ya está listo como cuando sigue pendiente.
- [ ] Implementar en el backend de Cotifly un mecanismo de polling (job en background / tarea programada) que consulte periódicamente el estado de cada solicitud enviada a Nexo que todavía no tenga costeo, usando el identificador guardado en HU-005.5.
- [ ] Definir el intervalo y la estrategia de reintentos del polling (ej. cada pocos segundos al inicio, igual que hace el propio frontend de Nexo, y luego espaciarlo — no tiene sentido preguntar cada pocos segundos indefinidamente si una licitación puede tardar horas o días en costearse).
- [ ] Manejar el caso de una solicitud que nunca se llega a costear (queda pendiente mucho tiempo): dejar ese estado visible en "Mis cotizaciones" en vez de que el polling siga corriendo en silencio para siempre.
- [ ] Manejar los casos de error al consultar Nexo (timeout, 4xx, 5xx, identificador ya no reconocido): registrar el evento como error de auditoría sin detener el polling del resto de solicitudes pendientes.
- [ ] Dejar registrado en la auditoría de Cotifly cada cambio de estado detectado por el polling, para poder investigar discrepancias futuras.

### [HU-005.7] Aplicar margen sobre el costeo de Nexo y actualizar "Mis cotizaciones"
**Labels:** `cotifly`, `backend`, `frontend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly` (el 3er punto del checklist — actualizar la vista "Mis cotizaciones" — se implementa en `clientepro-frontend`)
**Asignado a:** Duban (Backend) + Sara (Frontend, 3er punto del checklist)
**Parte de HU-005 (milestone). Depende de HU-005.6.**
- [ ] Al detectar vía polling (HU-005.6) que ya hay un costeo válido (flete + servicios conexos), invocar el módulo Margen/Precio existente (HU-003.4) usando ese costeo como el "costo" de entrada, en vez del tarifario plano de Google Drive.
- [ ] Calcular la tarifa final y el precio al cliente, y generar/actualizar el PDF de la cotización con esos valores (reutilizando el módulo PDF de HU-003.5).
- [ ] Actualizar el estado de la cotización visible en "Mis cotizaciones" (HU-004.5) reflejando el estado real (ej. "en costeo" mientras se espera a Nexo, "costeado" una vez el polling detecta el resultado y se calcula el precio final).
- [ ] Confirmar que en ningún punto del flujo se le muestra al comercial el costo bruto de Nexo como si fuera el precio final, sin haber pasado por el margen.

### [HU-005.8] Persistencia: nuevos campos, identificador de Nexo y estado de la solicitud
**Labels:** `cotifly`, `backend`, `datos`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend) + Sara (Datos)
**Parte de HU-005 (milestone). Coordinar con HU-002 (reestructuración de BD) antes de ejecutar — no crear columnas/tablas nuevas por fuera de ese proceso de evaluación/aprobación.**
- [x] **Ya ejecutado en HU-002.2 (2026-09-22), adelantado sin esperar a esta HU:** `cotizaciones` ya tiene `costeo_id` (identificador de la solicitud en Nexo), `bd_cliente` (cliente por identificador) y `estado_costeo` (estado del flujo de integración) — existen en el esquema, **vacíos**, sin código todavía que los llene. No hay que volver a proponerlos ni crearlos, solo construir el código de esta HU que los popule (`crear_solicitud_nexo`, `consultar_costeo_nexo`, polling).
- [ ] **Sigue pendiente de proponer/crear:** los campos ampliados del formulario que no se cubrieron en HU-002.2 (`vehículo`, `carrocería`, `clase de contenedor`, `valor mercancía`, `exclusivo`, `consolidado` — el costeo en sí, flete + servicios conexos, no se persiste aparte, se consulta en Nexo vía `costeo_id`) y la tabla nueva de "posiciones" (relación uno a muchos con la cotización, para el cubicaje). Coordinar con HU-002 antes de crearlas, mismo patrón de evaluación/aprobación.
- [ ] Una vez aprobado ese cambio de esquema pendiente, implementarlo (en el backend vigente en ese momento: Python o .NET, según el avance de HU-003).
- [ ] Confirmar que el flujo actual de generación de PDF y de "Mis cotizaciones" no se rompe con los campos nuevos (los campos opcionales no aplicables no deben generar errores).

### [HU-005.9] Pruebas QA end-to-end de la integración con Nexo
**Labels:** `frontend`, `qa`, `tarea-tecnica`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend) + Kevin (QA)
**Parte de HU-005 (milestone). Depende de HU-005.7.**
- [ ] Probar el flujo completo con al menos 3 casos reales (spot simple, con contenedor, con cupo/vehículo específico): crear solicitud en Cotifly → confirmar que llega a Nexo → ejecutar/simular el ajuste del admin de fletes en Nexo → confirmar que el polling de Cotifly detecta el costeo → confirmar que el margen se aplica correctamente → confirmar que "Mis cotizaciones" refleja el estado final.
- [ ] Probar los casos de error: Nexo no disponible al enviar la solicitud, Nexo no disponible durante el polling, respuesta de polling con un identificador de solicitud que Cotifly no reconoce, respuesta de polling malformada — confirmar que ninguno de estos casos rompe el sistema ni deja al comercial sin información sobre qué pasó.
- [ ] Confirmar con un caso real que el costo bruto de Nexo nunca se expone al comercial sin pasar por el margen.
- [ ] Dejar registrada en este issue la aprobación explícita de QA de esta integración antes de considerarla lista para producción.

---

#### Borrador espejo — PARA CREAR EN `NEXO` (no en Cotifly)

> **HU en Notion:** mismo link de HU-005 arriba
> **Sprint:** Sprint 4 · **Épica:** E3 — Integración Cotifly ↔ Nexo

**Alcance en este repo:** ajustar el backend de Nexo para que el catálogo de clientes combine dos fuentes. **HU también en:** `Cotifly` (consumo del endpoint, HU-005.2).

### [HU-005.10] Ampliar el buscador de clientes para combinar `DimBpSolicitante` con `tblProspectos`
**Labels:** `nexo`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `NEXO`
**Asignado a:** Equipo de Nexo (no es Duban/Sara/Kevin — issue en el repo NEXO)
**Parte de HU-005 (milestone).**
- [ ] Confirmar la conexión desde el backend de Nexo hacia la base de datos `dbSolucionesTI` (si hoy no existe esa conexión, agregarla) para poder consultar la tabla `tblProspectos`.
- [ ] Ajustar el endpoint de búsqueda de clientes (`/captura-solicitudes/clientes` o el que corresponda) para que combine los resultados de `DimBpSolicitante` (fuente actual) con `tblProspectos` (fuente nueva), sin duplicar un mismo cliente/prospecto si aparece en ambas.
- [ ] Definir y devolver en la respuesta un campo que distinga el origen de cada resultado (ej. `origen: "cliente"` vs `origen: "prospecto"`), para que Cotifly pueda mostrarlo si lo necesita.
- [ ] Probar la búsqueda con al menos un caso real de cada fuente (un cliente que solo esté en `DimBpSolicitante` y un prospecto que solo esté en `tblProspectos`) y confirmar que ambos aparecen en el resultado.
- [ ] Confirmar con el equipo de Cotifly (HU-005.2) que el contrato de respuesta ya ampliado no rompe lo que el frontend espera consumir.

---

## HU-006 — Despliegue del backend .NET de Cotifly en Dokploy

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E4 — Despliegue, Configuración y Seguridad en Producción · **Talla:** L · **Roles:** Backend (Duban), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo de TI, **quiero** desplegar el backend .NET resultante de HU-003 (con la base de datos ya migrada en HU-002) en Dokploy, con TLS activo, secretos fuera del repositorio y un proceso de build/deploy reproducible, **para** reemplazar el despliegue actual del backend Python y aprovechar la migración para cerrar de una vez la deuda técnica de infraestructura ya identificada, en vez de arrastrarla al nuevo stack.

### Contexto técnico (estado real del despliegue actual, auditado)
El despliegue actual en Dokploy tiene dos versiones que **no coinciden**: un manifiesto "ideal" documentado (`DEPLOY.md`, `infra/stack.dokploy.yml`, `infra/stack.staging.yml`, con TLS vía Traefik) y el manifiesto que **realmente corre hoy** (`deploy/docker-compose.swarm.yml`), que tiene varios atajos de emergencia:

- **Sin TLS activo**: el overlay de red (VXLAN) entre los nodos del clúster Dokploy está caído, así que hoy `bot-orquestador` se expone por `mode: host` en el puerto `8091` directo, en texto plano, sin pasar por Traefik.
- **Credenciales hardcodeadas en git**: usuario/contraseña de `bot-db` (Postgres) y `bot-minio` (MinIO) están en texto plano dentro de `deploy/docker-compose.swarm.yml`, un archivo versionado.
- **Sin registry de imágenes ni pipeline real**: las imágenes se construyen a mano por SSH en el nodo worker (`deploy/rebuild-on-worker.sh`), se etiquetan `:latest` y se hace "Redeploy" manual en el panel de Dokploy.
- **Verificación de JWT desactivada**: `GCHAT_VERIFICAR_JWT=false` en producción real — el webhook de Google Chat no valida la firma del emisor.
- **Sin backups automatizados confirmados**: existen `scripts/backup_db.sh`/`restore_db.sh`, pero no hay evidencia de que estén programados como tarea recurrente.
- **Un solo nodo real**: aunque el clúster tiene manager + worker, todos los servicios están anclados al worker por el problema de red — en la práctica es mono-nodo.

La topología de servicios en sí (un servicio expuesto tipo API, un servicio interno de lógica de negocio, una BD, storage tipo MinIO) **sí es reutilizable casi igual** para .NET — lo que cambia es el Dockerfile de cada servicio, el nombre/tag de imagen, y posiblemente el puerto interno (Kestrel).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Diagnóstico antes de tocar la red de producción
Dado que el overlay de red del clúster está caído y hoy se usa un workaround (mode: host, sin TLS)
Cuando se evalúa cómo restablecer TLS para el nuevo backend
Entonces existe una propuesta documentada con la causa raíz y las opciones consideradas, aprobada por el equipo, antes de aplicar cualquier cambio de red en el clúster real

Escenario: TLS activo en el backend .NET
Dado el backend .NET ya desplegado
Cuando un cliente externo (ej. el módulo Angular de HU-004) lo consume
Entonces la conexión va cifrada (HTTPS vía Traefik/Let's Encrypt), sin depender del bypass en texto plano que existe hoy

Escenario: Sin secretos en el repositorio
Dado cualquier manifiesto de despliegue nuevo para el stack .NET
Cuando se revisa su contenido versionado en git
Entonces ninguna credencial real (BD, MinIO, API keys) aparece en texto plano — todas se inyectan vía variables de entorno de Dokploy o secretos, nunca committeadas

Escenario: Build reproducible con imágenes versionadas
Dado un cambio de código en el backend .NET
Cuando se prepara un despliegue
Entonces la imagen se construye y versiona en un registry (no `:latest` a mano por SSH), y el despliegue en Dokploy referencia una versión concreta

Escenario: Despliegue sin downtime
Dado un despliegue de una nueva versión del backend .NET
Cuando Dokploy aplica la actualización
Entonces usa `start-first` (o equivalente) para no dejar el servicio caído durante el rollout, con rollback automático si el healthcheck falla

Escenario: Backups programados
Dado el backend .NET ya conectado a SQL Server 2019
Cuando pasa un día calendario
Entonces existe un backup automático reciente de la base de datos, sin intervención manual

Escenario: Autenticación del webhook de Google Chat resuelta explícitamente
Dado que hoy la verificación de JWT del webhook está desactivada
Cuando se despliega el nuevo backend
Entonces la verificación queda activada, o si se decide mantenerla desactivada, existe una justificación documentada y aceptada por el equipo (no queda así "por defecto" sin decisión consciente)

Escenario: Checklist de salida a producción
Dado el nuevo despliegue ya funcionando en un entorno de pruebas
Cuando se revisa el checklist de esta HU
Entonces cada verificación (TLS, secretos, backups, healthchecks, smoke tests de los 3 flujos principales) está marcada explícitamente antes de dar el corte a producción por cerrado
```

### Trazabilidad (event storming)
Depende de: HU-003 (backend .NET construido) y HU-002 (esquema ya migrado a SQL Server 2019). Reemplaza: `deploy/docker-compose.swarm.yml`, `infra/stack.dokploy.yml`, `infra/stack.staging.yml`, `deploy/rebuild-on-worker.sh`, los `Dockerfile` de `orquestador/`/`mcp_server/` (Python). El webhook/agente de Google Chat migra a .NET junto con el resto del backend (decisión ya tomada en HU-003.1) — el servicio .NET expone tanto `/health` como el webhook de Chat, sin depender de que Python siga vivo.

### Alcance en este repo (Cotifly)
1. Diagnosticar y proponer, sin ejecutar todavía, la solución para restablecer TLS dado el problema de red del clúster (HU-006.1), y solo después ejecutar la solución aprobada (HU-006.2).
2. Construir Dockerfiles multi-stage para los servicios .NET y montar un pipeline de build con registry de imágenes versionadas, reemplazando el rebuild manual por SSH.
3. Sacar del repositorio toda credencial hardcodeada, migrándolas a variables de entorno/secretos gestionados por Dokploy.
4. Actualizar los manifiestos de despliegue (staging y producción) para el nuevo stack .NET + SQL Server 2019.
5. Programar backups automatizados de SQL Server y resolver explícitamente la verificación de JWT del webhook de Google Chat.
6. Ejecutar un checklist de salida a producción con pruebas de humo de los 3 flujos principales (nueva cotización, mis cotizaciones, correo) antes de considerar el corte definitivo.

**HU también en:** ninguna — es infraestructura interna de `Cotifly`.

#### Issues en Cotifly

### [HU-006.1] Diagnóstico y propuesta para restablecer TLS en el clúster
**Labels:** `cotifly`, `backend`, `infra`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-006 (milestone). Este issue NO ejecuta ningún cambio de red — solo diagnostica y propone.**
- [ ] Confirmar la causa raíz exacta del overlay VXLAN caído entre los nodos del clúster Dokploy (revisar logs/estado de red de ambos nodos con el equipo de infraestructura que administra el clúster).
- [ ] Documentar al menos dos opciones de solución (ej. reparar el overlay entre nodos, o formalizar el esquema mono-nodo actual con Traefik apuntando directo al worker, u otra alternativa) con sus pros/contras.
- [ ] Presentar la propuesta al equipo y dejar registrada la decisión aprobada en este issue antes de cerrarlo.

### [HU-006.2] Ejecutar la solución de TLS aprobada
**Labels:** `cotifly`, `backend`, `infra`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-006 (milestone). Depende de HU-006.1 — solo ejecuta lo aprobado ahí.**
- [ ] Aplicar la solución de red aprobada en HU-006.1.
- [ ] Configurar los labels de Traefik en el servicio expuesto del backend .NET (`traefik.enable`, router con `Host()`, `entrypoints=websecure`, `certresolver=letsencrypt`), siguiendo el patrón ya documentado en `infra/stack.dokploy.yml`.
- [ ] Confirmar con una petición real desde fuera del clúster que el tráfico llega cifrado (HTTPS) y no por el puerto directo en texto plano.
- [ ] Actualizar `DEPLOY.md` (o su equivalente para el stack .NET) para que refleje la configuración de red real, eliminando la referencia al workaround `mode: host` una vez resuelto.

### [HU-006.3] Dockerfiles multi-stage para el backend .NET
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-006 (milestone). Depende de que exista código .NET compilable (HU-003.2 en adelante).**
- [ ] Crear un `Dockerfile` multi-stage por servicio expuesto (build con SDK de .NET, runtime con la imagen ASP.NET más liviana), sin dejar el SDK de compilación en la imagen final.
- [ ] Definir el puerto interno del servicio (Kestrel) y documentarlo, ajustando el resto de manifiestos si difiere del 8000/8080 actual de Python.
- [ ] Implementar el `HEALTHCHECK` del Dockerfile contra el endpoint `/health` ya existente como contrato (equivalente al que hoy tiene `orquestador/Dockerfile`).
- [ ] Confirmar que la imagen final no incluye tests, documentación ni artefactos de build innecesarios (alineado con el estándar `dotnet-enterprise-architect` de HU-003).

### [HU-006.4] Registry de imágenes y pipeline de build
**Labels:** `cotifly`, `backend`, `infra`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-006 (milestone). Depende de HU-006.3.**
- [ ] Definir y configurar el registry de imágenes a usar (el que ya contempla `infra/stack.dokploy.yml` vía `${REGISTRY}`, u otro que el equipo decida).
- [ ] Automatizar el build y push de la imagen (pipeline de CI, o al menos un script versionado que reemplace a `deploy/rebuild-on-worker.sh`), etiquetando por versión, no solo `:latest`.
- [ ] Configurar Dokploy para desplegar la imagen versionada del registry, en vez de construir localmente en el nodo worker.
- [ ] Documentar el nuevo proceso de release paso a paso en `DEPLOY.md`.

### [HU-006.5] Gestión de secretos fuera del repositorio
**Labels:** `cotifly`, `backend`, `infra`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-006 (milestone).**
- [ ] Confirmar con el equipo el mecanismo de secretos a usar (variables de entorno del panel de Dokploy, Docker secrets, u otro ya estándar en la compañía).
- [ ] Migrar las credenciales de la nueva BD (SQL Server 2019) y de MinIO (si se mantiene) a ese mecanismo, sin dejar ningún valor real en archivos versionados en git.
- [ ] Revisar el resto de variables sensibles (API key del LLM si aplica según HU-003.1, credenciales SMTP, service account de Google) y confirmar que ninguna quede hardcodeada en los nuevos manifiestos.
- [ ] Dejar documentado en `DEPLOY.md` el listado de variables esperadas por el nuevo backend (equivalente a un `.env.example`, que hoy no existe en el repo pese a estar referenciado).

### [HU-006.6] Manifiestos de despliegue para el stack .NET + SQL Server
**Labels:** `cotifly`, `backend`, `infra`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-006 (milestone). Depende de HU-006.2, HU-006.3 y HU-006.5.**
- [ ] Actualizar `infra/stack.staging.yml` e `infra/stack.dokploy.yml` (o crear sus equivalentes) para referenciar las imágenes .NET y, si aplica, el nuevo servicio de base de datos (o la conexión externa a la instancia SQL Server 2019 corporativa, si no corre como contenedor propio).
- [ ] Confirmar y documentar si SQL Server corre como servicio dentro del stack de Dokploy o como instancia externa ya existente de la compañía (según lo decidido en HU-002.4) — ajustar el manifiesto según corresponda.
- [ ] Mantener (o adaptar) las redes internas/expuestas equivalentes a `bot-back`/`bot-edge` actuales, respetando qué servicio debe tener salida a internet (LLM, Drive, Sheets si aplica) y cuál debe quedar aislado.
- [ ] Probar el despliegue completo en el ambiente de staging antes de tocar producción.

### [HU-006.7] Higiene operativa: backups automatizados y autenticación del webhook de Chat
**Labels:** `cotifly`, `backend`, `infra`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-006 (milestone).**
- [ ] Programar un backup automático diario de la base de datos SQL Server 2019 (tarea de Dokploy o cron del host), adaptando `scripts/backup_db.sh`/`restore_db.sh` (o su equivalente para SQL Server) al nuevo motor.
- [ ] Confirmar y documentar la ubicación y política de retención de los backups generados.
- [ ] Evaluar con el equipo por qué `GCHAT_VERIFICAR_JWT` está desactivado hoy y decidir explícitamente si se activa en el nuevo despliegue .NET (que ahora expone también el webhook de Google Chat, ver HU-003.1) o se mantiene desactivado con justificación documentada.
- [ ] Ejecutar una prueba de restore real (no solo de backup) contra un entorno de pruebas, confirmando que el backup generado sí es utilizable.

### [HU-006.8] Checklist de salida a producción y pruebas de humo
**Labels:** `cotifly`, `qa`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA)
**Parte de HU-006 (milestone). Depende de todos los issues anteriores de esta HU.**
- [ ] Crear/actualizar un checklist explícito de "listo para producción" (TLS activo, sin secretos en el repo, imagen versionada desplegada, backup reciente confirmado, decisión de JWT documentada).
- [ ] Ejecutar con Kevin (QA) pruebas de humo de los 3 flujos principales (nueva cotización, mis cotizaciones, envío de correo) contra el backend .NET ya desplegado en el entorno de pruebas.
- [ ] Confirmar que `GET /health` responde correctamente desde fuera del clúster (vía HTTPS) antes de dar el corte a producción por cerrado.
- [ ] Documentar en `DEPLOY.md` los pasos de rollback (volver a la versión anterior de la imagen) probados al menos una vez en staging.
- [ ] Dejar registro trazable (fecha, responsable, entorno) de cada prueba de humo ejecutada contra el entorno real, para que un futuro QA pueda reconstruir qué se validó y cuándo sin depender del historial de git (hoy insuficiente para eso, según hallazgo de QA).

---

## HU-007 — Auditoría y configuración de variables de entorno para producción

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E4 — Despliegue, Configuración y Seguridad en Producción · **Talla:** M · **Roles:** Backend (Duban), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** responsable del despliegue, **quiero** una tabla explícita de cada variable de entorno/secreto que el nuevo backend .NET necesita (BD, integración con Nexo, storage de PDFs, y lo que quede de Google/LLM según las decisiones de HU-003.1/HU-003.7), con su valor real para producción, **para** evitar que Dokploy quede corriendo con credenciales o valores de ejemplo por descuido — el mismo problema que ya encontramos hoy en el despliegue Python actual (`deploy/docker-compose.swarm.yml` con usuario/contraseña de BD y MinIO hardcodeados en texto plano).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Ninguna variable sensible en su valor de ejemplo en producción
Dado el backend .NET desplegado en Dokploy (HU-006)
Cuando se revisa cada variable de la tabla de esta HU
Entonces ninguna conserva un valor de ejemplo/desarrollo (contraseñas, cadenas de conexión, API keys)

Escenario: Conexión a SQL Server correcta
Dado SQLSERVER_CONNECTION_STRING (o el nombre final que defina HU-003.2)
Cuando se despliega a producción
Entonces apunta a la instancia SQL Server 2019 corporativa real (HU-002.4), no a un servidor de pruebas

Escenario: Integración con Nexo configurada
Dado las variables de la integración con Nexo (URL base de la API, credencial de autenticación)
Cuando se despliega a producción
Entonces apuntan al entorno real de Nexo (no a un ambiente de pruebas de ese sistema)

Escenario: Nada crítico queda vacío por accidente
Dado cualquier variable marcada como crítica en la tabla de esta HU
Cuando se despliega a producción
Entonces existe una verificación post-despliegue explícita de que esa variable funciona de verdad (no solo que "no está vacía") — ver HU-007.2
```

### Trazabilidad (event storming)
Habilitador técnico de HU-006 (despliegue en Dokploy). Reemplaza el manejo actual de variables en `shared/config.py` (Python) por el que defina HU-003.2 (.NET). Depende de las decisiones de HU-003.1 (alcance del LLM/agente) y HU-003.7 (si el tarifario de Google Drive se mantiene) para saber si las variables de Google/LLM siguen siendo necesarias.

### Alcance en este repo (Cotifly)
Completar, variable por variable, la tabla siguiente (a partir de `shared/config.py` actual y de lo que resulte de HU-003/HU-002/HU-005) con el valor real de producción y quién es responsable de proveerlo. Ninguna casilla de "Valor de producción" debe quedar en blanco al cerrar esta HU.

| Variable (o equivalente .NET) | Valor hoy en Python/dev | Valor requerido en producción | Responsable |
|---|---|---|---|
| Cadena de conexión SQL Server (reemplaza `database_url`) | — (hoy `postgresql+psycopg://...`) | Cadena real a la instancia SQL Server 2019 corporativa (HU-002.4) | Backend / TI |
| URL base de la API de Nexo (nueva, ver HU-005.5) | — (no existe aún) | URL real del backend `cabina-api` de Nexo en producción, no la de pruebas | Backend |
| Credencial de autenticación hacia Nexo (nueva, ver HU-005.5) | — | Credencial real (API key/JWT de servicio) acordada con el equipo de Nexo, distinta a la de pruebas | Backend |
| `gchat_verificar_jwt` / equivalente | `false` en producción real hoy | Confirmar explícitamente si se activa, según la decisión de HU-003.1 y HU-006.7 | Backend |
| `google_sa_json`, `drive_tarifario_folder_id`/`file_id` | Service account compartida, tarifario de Drive | Confirmar si siguen vigentes según la decisión de HU-003.7 (tarifario de Drive vs. costeo de Nexo); si ya no se usan, retirarlas en vez de dejarlas configuradas "por si acaso" | Backend |
| `llm_model` / `llm_api_key` (o su equivalente de configuración .NET) | Modelo y key de desarrollo | Sí se necesitan: el agente LLM migra a .NET (decisión tomada en HU-003.1) — usar modelo/key reales de producción, distintos a desarrollo | Backend |
| `pdf_storage_modo` / `s3_*` o equivalente | Configuración local/dev | Endpoint y credenciales reales del storage de PDFs en producción (S3/MinIO o el que se decida) | Backend / TI |
| `pdf_base_url` | Dominio de desarrollo | Dominio público real desde el que se sirven los PDFs en producción | Backend |
| `correo_*` / SMTP | Configuración de desarrollo/relay interno | Confirmar el mecanismo real de envío de correo en producción y sus credenciales | Backend |
| `web_session_secret` / `internal_api_token` | Valores de ejemplo | Valores nuevos generados para producción, distintos a desarrollo, guardados en el gestor de secretos del equipo | Backend |
| `ASPNETCORE_ENVIRONMENT` (o su equivalente) | `Development` | `Production` — confirmar que esto además cierra cualquier endpoint de documentación/debug expuesto en desarrollo | Backend |

**HU también en:** ninguna — variables propias del backend de `Cotifly`.

#### Issues en Cotifly

### [HU-007.1] Completar la tabla de variables de producción
**Labels:** `cotifly`, `backend`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-007 (milestone).**
- [ ] Completar cada fila de la tabla de "Alcance en este repo" con el valor real (o la confirmación explícita de que ya no aplica y se retira) antes de cerrar este issue.
- [ ] Cargar los valores confirmados directamente en el mecanismo de variables de entorno/secretos de Dokploy (nunca en un archivo versionado en git, corrigiendo la práctica actual detectada en HU-006).
- [ ] Marcar en la tabla cuáles variables ya estaban correctas en un despliegue previo (si existe) y cuáles se corrigieron ahora.

### [HU-007.2] Verificación post-configuración contra el entorno real
**Labels:** `cotifly`, `qa`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA)
**Parte de HU-007 (milestone). Depende de HU-007.1 y de HU-006 (backend ya desplegado).**
- [ ] `GET /health` responde 200 en el entorno desplegado con las variables ya aplicadas.
- [ ] Crear una solicitud de prueba de punta a punta contra la integración real con Nexo (no un mock), confirmando que llega y que el polling detecta correctamente el costeo contra el entorno real de producción.
- [ ] Confirmar que la generación y descarga de un PDF de prueba funciona contra el storage real de producción.
- [ ] Confirmar que cualquier endpoint de documentación/debug (equivalente a Swagger) **no** es accesible en producción.
- [ ] Dejar esta verificación firmada por Kevin (QA) antes de dar la configuración de producción por cerrada.

---

## HU-008 — Aislamiento de solicitudes por comercial en la Bandeja de Nexo

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E3 — Integración Cotifly ↔ Nexo (hallazgo relacionado; el fix en sí es independiente de la integración) · **Talla:** M · **Roles:** Equipo de Nexo
> **Repos donde se crean issues de esta milestone:** `NEXO`

### Historia
**Como** comercial que usa Nexo, **quiero** que la Bandeja de solicitudes me muestre únicamente las solicitudes que yo mismo creé, **para** no ver ni poder tocar las solicitudes de otros comerciales.

### Contexto (confirmado por el equipo, no es una hipótesis)
El endpoint que alimenta la Bandeja de solicitudes (capacidad `verBandeja` del rol Comercial, en `administracion_embarqa_nexo`) **hoy no filtra por el comercial autenticado** — cualquier comercial ve las solicitudes de todos los demás. Este hallazgo salió a la luz durante el diseño de HU-005 (Integración Cotifly ↔ Nexo), pero es un problema de seguridad **ya existente en Nexo hoy**, independiente de si esa integración avanza o no — afecta a los usuarios actuales del sistema.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Comercial solo ve sus propias solicitudes
Dado el comercial A y el comercial B, cada uno con sus propias solicitudes
Cuando el comercial A abre la Bandeja
Entonces solo ve las solicitudes que él mismo creó, nunca las del comercial B

Escenario: Intento de acceso directo a una solicitud ajena
Dado una solicitud creada por el comercial B
Cuando el comercial A intenta consultar o ajustar esa solicitud directamente (por su identificador, no solo desde el listado)
Entonces el backend responde con un error de autorización, no con los datos de la solicitud

Escenario: Otros roles no se ven afectados
Dado el rol admin de fletes/operaciones, que sí necesita ver todas las solicitudes para costearlas
Cuando ese rol usa la Mesa de Costeo
Entonces sigue viendo todas las solicitudes normalmente — el fix se limita al rol Comercial
```

### Trazabilidad (event storming)
Afecta: el/los endpoint(s) de Nexo que alimentan la Bandeja y el detalle de una solicitud individual, para el rol Comercial. Hallado durante el diseño de HU-005; el endpoint de consulta de estado que Cotifly usará por polling (HU-005.6) también debe quedar cubierto por este fix.

### Alcance en este repo (NEXO)
Corregir el filtro faltante por comercial en los endpoints de Bandeja y de detalle de solicitud, sin afectar el acceso que ya necesita el rol admin de fletes/operaciones.

**HU también en:** ninguna — es un fix propio y aislado del repo `NEXO`. Relacionado con HU-005 (`Cotifly`), que depende de que la consulta de estado por polling también respete este control de acceso.

#### Issue en NEXO

### [HU-008.1] Corregir el filtro de la Bandeja para que el rol Comercial solo vea sus propias solicitudes
**Labels:** `nexo`, `backend`, `seguridad`, `tarea-tecnica`
**Repo donde se crea este issue:** `NEXO`
**Asignado a:** Equipo de Nexo (no es Duban/Sara/Kevin — issue en el repo NEXO)
**Parte de HU-008 (milestone).**
- [ ] Identificar todos los endpoints que alimentan la Bandeja y cualquier endpoint de detalle/ajuste de una solicitud puntual usados por el rol Comercial.
- [ ] Agregar el filtro por comercial autenticado (comparando contra el usuario/email del token) en cada uno de esos endpoints, tanto en el listado como en la consulta directa por identificador.
- [ ] Confirmar explícitamente que el rol admin de fletes/operaciones (Mesa de Costeo) no se ve afectado por este cambio y sigue viendo todas las solicitudes que necesita para costear.
- [ ] Probar con al menos dos usuarios comerciales de prueba distintos: cada uno debe ver solo sus propias solicitudes en la Bandeja, y recibir un error de autorización si intenta consultar/ajustar una solicitud ajena directamente por identificador.
- [ ] Confirmar que este fix no rompe el endpoint que Cotifly va a consumir por polling (HU-005.6 en `Cotifly`) para sus propias solicitudes.
- [ ] Dado que esto es una corrección de seguridad ya confirmada (no una auditoría abierta), priorizarla y desplegarla cuanto antes, sin esperar al avance de la integración con Cotifly (HU-005).

---

## HU-009 — Activar la conexión real a SAP TM para que la extracción de márgenes por cliente sea efectiva

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 4 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** L · **Roles:** Backend (Duban), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo de TI, **quiero** activar la conexión OData en vivo a SAP TM (código ya implementado y probado, pero nunca activado en producción) en vez del export JSON estático que se usa hoy, **para** que el margen por cliente que aplica el bot al cotizar sea siempre el dato real y actualizado de SAP, no un archivo fijo que puede quedar desactualizado.

### Contexto técnico (ya construido, confirmado en el código)
- El cliente OData **ya existe y está probado**: `mcp_server/margen/odata_fuente.py` (`ODataFuente`), con pruebas unitarias vía `respx` (mock) y una prueba marcada `@pytest.mark.sap` contra el host SAP real — hoy excluida del CI por defecto, nunca ejecutada contra el sistema real en este entorno.
- El cambio de fuente es **solo una bandera de configuración**: `SAP_TM_MODO=export_json` (hoy, lee `sap_tm_export_path`, un archivo fijo de ejemplo) vs. `SAP_TM_MODO=odata` (usa `sap_tm_url`/`sap_tm_user`/`sap_tm_pass` para consultar en vivo la entidad `RentabClienteSet`). No se necesita escribir código Python nuevo para activar esto.
- El resolver de margen (`mcp_server/margen/servicio.py`) ya contempla resiliencia: si la fuente SAP no responde (`FuenteMargenNoDisponible`), cae automáticamente al fallback de política por línea de negocio (`origen: "politica_linea"`), sin romper el flujo de cotización. También ya marca como "anómalo" cualquier margen fuera del rango [0%, 60%] (`servicio.py:50`).
- Lo que falta **no es código**: es conectividad de red real desde donde corre el backend hasta el host SAP, credenciales reales de servicio, y validar con datos reales que el margen extraído es correcto antes de confiar en él en producción.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Margen real extraído de SAP en vivo
Dado un cliente con Margen Propuesto configurado en SAP TM (entidad RentabClienteSet)
Cuando el bot cotiza para ese cliente y esa línea de negocio
Entonces el margen aplicado viene de la consulta OData en vivo (origen: "sap_tm"), no del export JSON estático

Escenario: Resiliencia si SAP no responde
Dado que el host SAP TM está temporalmente no disponible
Cuando el bot intenta consultar el margen por cliente
Entonces cae al fallback de política por línea de negocio (origen: "politica_linea") sin romper el flujo de cotización — comportamiento que ya existe en el código

Escenario: Margen anómalo detectado con datos reales
Dado un margen extraído de SAP fuera del rango [0%, 60%]
Cuando se aplica a una cotización real
Entonces queda marcado explícitamente como anómalo, para que el equipo lo revise antes de confiar en él en producción
```

### Trazabilidad (event storming)
Afecta: `mcp_server/margen/` (Python, repo `Cotifly`) — mismo resolver que se reimplementará en .NET (HU-003.4). Depende de: conectividad de red y credenciales que debe proveer el equipo de infraestructura/SAP (fuera del control directo de Cotifly). Relacionado con HU-003.7 (integraciones externas en .NET): la migración debe reimplementar el cliente OData completo, no solo dejarlo como interfaz sin implementación, ya que aquí se confirma que la integración real es necesaria y ya funciona en Python.

### Alcance en este repo (Cotifly)
1. Coordinar con el equipo de infraestructura/SAP la conectividad de red real y las credenciales de servicio para el endpoint OData (HU-009.1).
2. Ejecutar las pruebas ya existentes marcadas `@pytest.mark.sap` contra el host real, validar con datos de al menos algunos clientes/líneas de negocio reales, y activar `SAP_TM_MODO=odata` en producción (HU-009.2).
3. Actualizar HU-003.7 para que la migración a .NET reimplemente el cliente OData completo (no solo la interfaz que se había dejado pendiente).

**HU también en:** ninguna — el trabajo de conectividad/activación es interno a `Cotifly`; la coordinación con el equipo de SAP/infraestructura es externa y no genera un issue en otro repo de este documento.

#### Issues en Cotifly

### [HU-009.1] Obtener conectividad de red y credenciales reales para SAP TM OData
**Labels:** `cotifly`, `backend`, `infra`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-009 (milestone). Gatilla HU-009.2 — no se activa nada en producción sin cerrar este primero.**
- [ ] Confirmar con el equipo de infraestructura/SAP si el backend (hoy en Dokploy) tiene o puede tener ruta de red hasta el host SAP TM real (puede requerir VPN, whitelist de IP, o una ruta interna ya existente que usen otros sistemas de la compañía) — documentar la respuesta.
- [ ] Solicitar al equipo de SAP las credenciales reales de servicio para el endpoint OData (`sap_tm_url`, `sap_tm_user`, `sap_tm_pass`) y el nombre real de la entidad/servicio si difiere de `RentabClienteSet`.
- [ ] Confirmar con el equipo de SAP el significado exacto de los campos que devuelve la entidad (`SalesOrgId`, `TrqType`, `OrderPartyId`, `Value`) contra al menos un caso real conocido, para tener un punto de referencia antes de validar en el siguiente issue.
- [ ] Dejar registradas en este issue las credenciales/endpoints confirmados (guardados en el gestor de secretos del equipo, nunca en texto plano en este issue ni en el repo).

### [HU-009.2] Validar la integración OData contra SAP real y activarla en producción
**Labels:** `cotifly`, `backend`, `qa`, `tarea-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend) + Kevin (QA)
**Parte de HU-009 (milestone). Depende de HU-009.1.**
- [ ] Ejecutar las pruebas ya existentes marcadas `@pytest.mark.sap` (hoy excluidas del CI) contra el host SAP real, en un entorno de pruebas, usando las credenciales obtenidas en HU-009.1.
- [ ] Validar con al menos 3 combinaciones reales de cliente + línea de negocio que el margen extraído es correcto y coherente con lo que el negocio espera (no solo que la llamada HTTP funciona).
- [ ] Confirmar que el caso de margen "anómalo" (fuera de [0%,60%]) se puede reproducir o al menos queda claramente definido qué hacer operativamente si ocurre con un cliente real.
- [ ] Confirmar que el fallback a política por línea de negocio funciona correctamente si se simula que SAP no responde (timeout/5xx), sin romper una cotización en curso.
- [ ] Cambiar `SAP_TM_MODO` a `odata` en la configuración de producción (Dokploy) una vez validado todo lo anterior, y confirmar en producción real que las primeras cotizaciones usan `origen: "sap_tm"` cuando corresponde.
- [ ] Dejar registrada en este issue la aprobación explícita de QA antes de considerar esta HU cerrada.

---

## Resumen para copiar rápido

| HU | Título | Alcance real | Repo(s) donde vive el trabajo principal |
|---|---|---|---|
| HU-001 | Hardening inmediato: fallar en arranque si quedan secretos por defecto en producción | Chequeo de arranque sobre el backend Python actual, corrigiendo un riesgo real ya en producción | `Cotifly` |
| HU-002 | Reestructuración, limpieza y migración del esquema de BD a SQL Server 2019 | Auditoría de columnas/FK, diseño del esquema objetivo y migración física, como insumo de HU-003.3 | `Cotifly` |
| HU-003 | Migración del backend de Cotifly a C# / .NET 10 con Clean Architecture | Reemplazo completo de `orquestador/` + `mcp_server/` + `shared/`, persistencia en SQL Server 2019 | `Cotifly` |
| HU-004 | Migración del módulo web de Cotifly a Angular 17.3.8 en ClientePro | Contrato de API + CORS en Cotifly; módulo Angular completo en clientepro-frontend | `Cotifly`, `clientepro-frontend` |
| HU-005 | Integración Cotifly ↔ Nexo: envío de solicitudes y consulta del costeo (polling) | Mapeo de campos, buscador de clientes, formulario ampliado, cubicaje, envío a Nexo, polling de costeo, margen/precio final, persistencia y QA end-to-end | `Cotifly`, `clientepro-frontend`, `NEXO` |
| HU-006 | Despliegue del backend .NET en Dokploy | Dockerfiles, registry, TLS, secretos, manifiestos, backups y checklist de salida a producción | `Cotifly` |
| HU-007 | Auditoría y configuración de variables de entorno para producción | Tabla completa de variables (BD, integración Nexo, storage, secretos) con valor real y verificación post-despliegue | `Cotifly` |
| HU-008 | Aislamiento de solicitudes por comercial en la Bandeja de Nexo | Fix de seguridad confirmado: un comercial ve hoy las solicitudes de todos los demás | `NEXO` |
| HU-009 | Activar la conexión real a SAP TM para que la extracción de márgenes por cliente sea efectiva | Conectividad, credenciales y validación real — el código OData ya existe y está probado | `Cotifly` |

**Antes de crear esto en GitHub:**
- [ ] Crear la HU-001, HU-002, HU-003, HU-004, HU-005, HU-006, HU-007, HU-008 y HU-009 en Notion y reemplazar los links placeholder.
- [ ] Crear el label `sprint-4` en GitHub (las 9 HUs ya quedaron asignadas a Sprint 4) y crear las 4 épicas (E1 a E4, ver punto 5.1 de la introducción) antes de crear las HUs, ya que cada una queda asignada a una épica específica (HU-008 queda asociada a E3 solo como referencia cruzada, es un fix independiente; HU-009 sí queda dentro de E1).
- [ ] HU-009.1 (conectividad/credenciales) debe cerrarse **antes** de HU-009.2 (validación + activación en producción) — no se activa nada sin confirmar primero que hay ruta de red y credenciales reales.
- [ ] El archivo suelto `=0.7` de la raíz del repo (residuo de un `pip install` mal escrito, hallazgo de QA) ya se eliminó directamente del working tree — solo falta que el equipo confirme el commit de ese borrado, no requiere una HU.
- [ ] Nota de alcance: los issues de este documento se crean en **3 repos**: [`Cotifly`](https://github.com/InnovacionIA-proyectos/Cotifly), [`clientepro-frontend`](https://github.com/InnovacionIA-proyectos/clientepro-frontend) y [`NEXO`](https://github.com/InnovacionIA-proyectos/NEXO) (backend de Nexo, para HU-005.10 y HU-008.1). No se crea ningún issue en `administracion_embarqa_nexo` (frontend de Nexo) — ese repo no se toca.
- [ ] **HU-008 es una corrección de seguridad ya confirmada** (un comercial de Nexo ve hoy las solicitudes de otros comerciales) — priorizarla independientemente del resto del backlog, no esperar al avance de HU-005 ni de la migración de Cotifly.
- [ ] Confirmar con negocio la lista exacta de roles que deben ver el ítem de menú "Cotizador" en ClientePro.
- [ ] HU-004.2 (confirmar versiones ya instaladas, sin tocar nada compartido) debe ejecutarse **antes** de HU-004.3 en adelante, para que el equipo sepa desde el inicio con qué versión real de Angular Material tiene que trabajar. Restricción confirmada: no se modifica el `package.json` raíz ni ninguna dependencia compartida de ClientePro en esta HU.
- [ ] HU-003.1 (decisiones de arquitectura y estrategia de migración) debe cerrarse **antes** de crear/empezar cualquier otro issue de HU-003 — ya se confirmó que el agente LLM migra a .NET, que el desarrollo vive en el mismo repo `Cotifly`, y que los datos de conexión a SQL Server los provee directamente el responsable del proyecto; falta decidir la estrategia de corte, la librería de PDF, el mecanismo concreto de orquestación del LLM (ej. Semantic Kernel vs. cliente HTTP directo), y si `mcp_server`/`orquestador` (Python) siguen expuestos durante la convivencia.
- [ ] Coordinar HU-003.6 (paridad de contrato de API) con el equipo de HU-004 para probar el módulo Angular contra ambos backends (Python y .NET) durante la convivencia.
- [ ] HU-002.1 (propuesta) debe aprobarse **antes** de HU-002.2 (ejecución), y ambas deben cerrarse **antes** de HU-002.3 (diseño del esquema objetivo); HU-002.3 debe cerrarse **antes** de HU-003.3 (implementación EF Core) — el orden real es HU-002 completa → HU-003.3 → resto de HU-003.
- [ ] HU-006 depende del avance de HU-003 (código .NET) y HU-002 (BD ya en SQL Server 2019) — no tiene sentido montar el pipeline de despliegue antes de tener algo real que desplegar; HU-006.1 (diagnóstico de red) sí puede adelantarse en paralelo, ya que no depende de ninguna de las dos.
- [ ] HU-005 depende de HU-004.4 (formulario base ya migrado) y de HU-003.4 (módulo Margen/Precio ya existente, que HU-005.7 reutiliza); debe coordinar sus cambios de esquema con HU-002 (no crear columnas/tablas nuevas por fuera del proceso de evaluación/aprobación ya definido ahí). HU-005.1 (mapeo) debe aprobarse antes que el resto de issues de esta HU, y HU-005.5→HU-005.6→HU-005.7 deben ejecutarse en ese orden (enviar, consultar por polling, calcular).
- [ ] Coordinar con el equipo de Nexo (repo `NEXO`) la confirmación del endpoint de recepción de solicitudes y del endpoint de consulta de estado/costeo (para el polling de HU-005.6) antes de empezar a codificar HU-005.5/HU-005.6 — son los mismos endpoints que ya usa el frontend de Nexo, Nexo no construye nada nuevo. El único issue de HU-005 que se crea en el repo `NEXO` es HU-005.10 (buscador de clientes); el resto de la coordinación con ese equipo es informal (conversación entre equipos), sin issue propio. El fix de aislamiento por comercial en la Bandeja de Nexo va aparte, en HU-008 (repo `NEXO`, independiente de HU-005).
- [ ] Cada milestone (HU-002, HU-003, HU-004, HU-005) tiene ahora su propio issue de QA (`.5`/`.9`/`.8`/`.9` según corresponda) asignado a Kevin — no cerrar la milestone sin la aprobación explícita registrada en ese issue.
- [ ] HU-007 depende de que HU-003 (nombres finales de variables .NET), HU-002.4 (conexión real a SQL Server) y HU-005.5/HU-005.6 (credenciales de la integración con Nexo) ya estén al menos definidos, aunque no completamente terminados, para poder completar la tabla de variables sin adivinar nombres.
