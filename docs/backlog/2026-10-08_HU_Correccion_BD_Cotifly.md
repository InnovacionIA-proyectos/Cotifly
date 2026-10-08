# Milestone e Issues — [Corrección con BD] · Cotifly

**Fecha de redacción:** 2026-10-08. Borrador listo para copiar/pegar en GitHub. Mismo formato de `docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md`: la milestone es la historia completa en Gherkin, y cada issue técnico ("tarea-tecnica") es un checklist explícito, ya con nombre, labels, repo y persona asignada.

**Cómo usar este documento**
1. El bloque `## HU-021` es una **Milestone nueva**: copiar desde `> **HU en Notion:**` hasta el final de "Alcance en este repo" como descripción de la milestone.
2. Cada `### [HU-021.N]` es un **Issue nuevo**, asociado a esa milestone. El título del issue es la línea del encabezado sin el `###`; los labels, el repo y el asignado ya están en las líneas de abajo.
3. Numeración: **HU-021** (la rama de la propuesta se llama `118-hu-0211-...`). El Sprint 5 (`docs/backlog/2026-10-07_HU_Sprint5_Cotifly.md`) empieza en HU-014 y se hace **después** de esta.
4. **Repo donde se crean los issues (1):** [`InnovacionIA-proyectos/Cotifly`](https://github.com/InnovacionIA-proyectos/Cotifly), HU-021.1 a 021.14. No se crea ningún issue en `NEXO`, `clientepro-frontend` ni `administracion_embarqa_nexo`: **este documento es solo backend y base de datos de Cotifly**, y las HU del frontend se redactan aparte. Lo que se le pide a Nexo y al cubicador **no son issues**: está en `docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md`, y las HU que dependen de eso lo dicen en su nota "Depende de".
5. `> **HU en Notion:**` queda con un placeholder: crear la HU en Notion y pegar el link real.
6. **Sprint:** Sprint 5, pero se hace **antes** que HU-014 a HU-020. **Épica:** E1 — Migración de Backend y Datos (la de HU-002 y HU-003: esta HU corrige lo que ellas dejaron).
7. Roles del equipo (fijos): **Duban** = Backend, **Sara** = Frontend y Datos, **Kevin** = QA. Aquí Sara aparece solo como Datos.
8. Todos los issues llevan el prefijo **[Corrección con BD]** en el título y el label `corrección-bd`.

**Cambios hechos después de crear las issues (2026-10-08, al integrar `QA` en la rama):**
1. Mi migración pasó de `0008` a **`0009`**, porque `QA` ya trae su propio `0008_horas_por_linea.sql` (HU-013.11). Afecta a la milestone y a HU-021.1, 021.2, 021.10 y 021.12.
2. **Nexo sí acepta las horas de cargue y descargue por línea**: se retira la petición N2 y la regla de mandar "el mayor valor". Afecta a HU-021.3 y 021.7.
3. `QA` retiró la integración con Google Chat (HU-013.9): el agente sigue en el chat web. Afecta al escenario Gherkin de la milestone.
4. El `0001` **no se edita** (es un script ya aplicado y las pruebas dependen de él): afecta a HU-021.2.

---

## HU-021 — [Corrección con BD] Adaptar Cotifly a la reestructuración de la base de datos (solicitud → rutas → camiones)

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 (se hace antes de HU-014) · **Épica:** E1 — Migración de Backend y Datos · **Talla:** XL · **Roles:** Backend (Duban), Datos (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo de backend y datos, **quiero** que Cotifly trabaje sobre el nuevo esquema de cotizaciones (solicitud → rutas → camiones, con las mercancías y los conexos por ruta, el margen y el precio por ruta, y un cubicador que recomienda los camiones), **para** que una solicitud con varias rutas, y varios vehículos por ruta, se cotice, se cueste en Nexo y se le presente al cliente correctamente, sin las columnas duplicadas ni los campos de ruta y de vehículo mezclados en la cabecera que dejaron las migraciones 0004 a 0007.

### Contexto técnico: por qué es lo primero

El 2026-10-08 se borró y recreó el schema `dbo_cotifly` en `dbTarifario_Nexo_Pruebas` (la base compartida) con el esquema nuevo (`db/scripts/01_crear_esquema.sql`). **El backend actual no funciona contra esa base**: espera `Cotizaciones`, `CotizacionPosiciones`, `valorAprovisionamiento`, etc., que ya no existen. Mientras no se haga esta HU, nadie del equipo puede probar contra esa base, y nada del Sprint 5 se puede construir encima.

Los datos anteriores (5 cotizaciones de prueba, el usuario `dguerra`, sesiones y tokens) quedaron en un respaldo dentro de la misma base y no se pueden pasar al esquema nuevo tal cual. Las pruebas de ahora en adelante se hacen con datos nuevos creados por el flujo nuevo.

**Antes de empezar (Duban):** confirmar que el freeze del backend (anotado desde el 2026-09-29) ya se levantó.

**Qué cambia, en una frase.** Una cotización deja de ser "una ruta, un vehículo, todo en la cabecera" y pasa a tres niveles: **solicitud → rutas → camiones**. Las mercancías y los conexos cuelgan de la ruta; los costos de Nexo, del camión; el margen y el precio, de la ruta; el precio total, de la cabecera. Un cubicador externo recomienda los camiones. Detalle en la propuesta.

**Orden y dependencias.**

| Fase | Issues | Se puede hacer sin el cubicador |
|---|---|---|
| 1. Base de datos validada | HU-021.2 | Sí |
| 2. Núcleo del backend | HU-021.3, 021.4 | Sí |
| 3. Flujos sobre el núcleo | HU-021.6, 021.7, 021.8, 021.9, 021.10 | Sí, con "una ruta = un camión" y el vehículo digitado como hoy |
| 4. Cubicador | HU-021.5 (y el tramo del cubicador en 021.6) | **No**: necesita que el cubicador tenga `POST /recomendaciones` |
| 5. Contrato de la API | HU-021.11 | Parcial |
| 6. Pruebas y documentación | HU-021.12, 021.14 | Transversal |

HU-021.13 (reglas pendientes de la propuesta original) es independiente y se puede hacer en cualquier momento.

**Estimado del backend:** 4 a 5 semanas de una persona con Claude Code. Es un cálculo grueso sacado de recorrer el código, no de implementarlo.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Una solicitud con varias rutas y varios camiones por ruta
Dado una solicitud con 2 rutas
Y el cubicador recomienda 3 camiones para la primera ruta y 1 para la segunda
Cuando se guarda la cotización
Entonces queda 1 cabecera, 2 rutas y 4 camiones
Y las mercancías y los conexos cuelgan de su ruta, no de un camión
Y el comercial no digitó vehículo ni carrocería

Escenario: Costo, margen y precio por ruta
Dado una ruta con 3 camiones cuyo flete devolvió Nexo
Cuando se calcula la ruta
Entonces su costo es la suma de los fletes, los retornos, un levante de contenedor por cada camión y los conexos
Y el margen se aplica una sola vez sobre ese total, con el método de su línea de negocio
Y el precio de la solicitud es la suma de los precios de sus rutas

Escenario: El cubicador no puede recomendar, o la mercancía está condicionada
Dado una mercancía para la que el cubicador responde "sin selección automática", o que está condicionada y pendiente de aprobación
Cuando se envía la solicitud
Entonces la solicitud viaja completa a Nexo, con el camión sin vehículo
Y la resuelve la Mesa de Costeo, sin bloquear ni partir el envío

Escenario: Envío a Nexo por camión y regreso de costos sin cruces
Dado una ruta con varios camiones
Cuando Cotifly envía la solicitud y luego consulta el costeo
Entonces a Nexo llega una línea por camión con los datos de su ruta
Y cada camión recibe su propio flete y su propio estado, sin cruzar los de camiones distintos

Escenario: Las cotizaciones del agente de chat tienen ruta y camión
Dado una cotización creada por el agente de chat
Cuando se guarda
Entonces tiene su ruta y su camión igual que las del formulario, y no solo costo y precio en la cabecera

Escenario: Instalación desde cero y migración de una base existente
Dado una base vacía
Cuando se ejecuta la instalación desde cero
Entonces quedan 20 tablas, 2 vistas y la configuración inicial sembrada
Y una base con el esquema viejo, migrada con el 0009, queda con el mismo esquema, sin perder sus datos y con un respaldo de las tablas que cambió

Escenario: El frontend de hoy sigue funcionando mientras sale el nuevo
Dado el frontend actual enviando el formato plano viejo (una línea, un vehículo)
Cuando la solicitud llega a la API
Entonces se convierte a una ruta con un camión, hasta que el frontend nuevo salga
```

### Trazabilidad (event storming)
Fuentes: `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md` (el diseño de las tablas nuevas, columna por columna), `docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md` (lo que el backend tiene que cambiar, capa por capa, con archivo y línea) y `docs/database/PROPUESTA_REFACTOR_BD_COTIFLY.md` (HU-002.1, aprobada el 2026-09-22; se verificó el 2026-10-08 punto por punto contra el backend, resultado en HU-021.13). Corrige lo que dejaron HU-002 y HU-003 (esquema y backend) y lo que agregaron HU-005, HU-011 y HU-013 (costeo en Nexo, líneas de licitación, captura). Entrega la base sobre la que se construye el Sprint 5 (HU-014 a HU-020). Depende del cubicador (equipo de NEXO) para la fase 4.

### Alcance en este repo (Cotifly)
1. Validar y cerrar el esquema nuevo de la base de datos y su migración (HU-021.1, 021.2; 2 a 3 días de la migración, más pruebas).
2. Núcleo del backend: dominio y persistencia (HU-021.3, 021.4; 5 a 6 días).
3. Flujos sobre el núcleo: captura, envío a Nexo, margen y conexos, PDF y versiones, métricas (HU-021.6 a 021.10; 13 a 16 días).
4. Integración con el cubicador (HU-021.5; 2 a 3 días, **bloqueada hasta que el cubicador tenga `POST /recomendaciones`**).
5. Contrato de la API (HU-021.11; 1 a 2 días), reglas pendientes de la propuesta original (HU-021.13), pruebas y documentación (HU-021.12, 021.14; 4 a 5 días).

**Estimado del backend: 4 a 5 semanas de una persona con Claude Code** (cálculo grueso sacado de recorrer el código, no de implementarlo).

**HU también en:** ninguna. Lo que se le pide a Nexo y al cubicador está en `docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md` (no son issues) y se cita en las HU que dependen de ello (HU-021.5, 021.6, 021.7 y 021.12). El frontend queda fuera de este documento.

#### Issues en Cotifly

### [HU-021.1] [Corrección con BD] Evaluación y propuesta de la reestructuración
**Labels:** `cotifly`, `datos`, `tarea-tecnica`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-021 (milestone). Este issue NO ejecuta ningún cambio sobre el esquema real: evalúa y propone.** (La ejecución son los issues siguientes.)

- [x] Auditar las tablas de cotizaciones contra el uso real en el backend y contra lo que Nexo y el cubicador necesitan.
- [x] Redactar la propuesta con el diseño final, columna por columna, y las decisiones tomadas (`docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md`).
- [x] Documentar el impacto en el backend (`docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md`).
- [x] Escribir el script de instalación desde cero, los seeders y el ejecutador (`db/scripts/`, `db/seeder/`, `db/ejecutar-bd.ps1` y `.sh`).
- [x] Escribir la migración para la base existente (`db/migrations_mssql/0009_reestructuracion_cotizaciones.sql`).
- [ ] **Visto bueno del DBA y de Duban a la propuesta**, dejando registrada aquí la decisión punto por punto (aprobado, aprobado con ajustes o rechazado). Este issue no se cierra sin eso.

### [HU-021.2] [Corrección con BD] Base de datos: validar y cerrar el esquema
**Labels:** `cotifly`, `datos`, `tarea-tecnica`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos) — el visto bueno final es del DBA
**Parte de HU-021 (milestone). Depende de HU-021.1.**

**Lo que ya se hizo (2026-10-08):** la instalación desde cero se corrió sobre `dbTarifario_Nexo_Pruebas`: 20 tablas, 2 vistas, 6 líneas de negocio, 5 mapeos SAP y 2 roles, sin errores. Se restauraron los roles externos (`comercial` = 34, `gerente_comercial` = 31).

- [ ] **Comparar** el esquema de `01_crear_esquema.sql` contra el de una base armada con las migraciones `0001` a `0009`, en dos bases vacías (diferencias en `INFORMATION_SCHEMA.COLUMNS`, restricciones e índices). Hoy solo se comparó contra la propuesta.
- [ ] **Probar el `0009`** sobre una copia de una base con el esquema viejo (el `0001` a `0008` aplicados y con datos), nunca sobre la compartida. Verificar las consultas de control del propio script (costos que cuadran, cada cotización con ruta y cada ruta con camión).
- [ ] Probar el servicio `db-init` del `docker-compose.yml` (`docker compose up sqlserver db-init`). Está escrito pero no se ha ejecutado; `sqlcmd` de la imagen de SQL Server 2019 es una versión vieja y puede no aceptar alguna opción.
- [ ] **Agregar el `CHECK` de `estadoEnvio`** (`sin_enviar`, `enviada`, `reenviada`) al `01_crear_esquema.sql` y al `0009`. Es la parte de corto plazo de HU-014.1: ni el esquema nuevo lo trae. En la base compartida ya recreada hay que agregarlo con un `ALTER` puntual.
- [ ] Conseguir el **`TrqType` de SAP para `DevolucionContenedor`** (negocio/SAP) y sembrarlo en `db/seeder/02_sap_mapeo_trqtype.sql`. No se inventa.
- [ ] **Visto bueno del DBA** sobre `01_crear_esquema.sql` y `0009`, como los scripts anteriores.
- [ ] Decidir qué se hace con `db/migrations/` (Alembic, el stack viejo de Postgres). `deploy/docker-compose.swarm.yml` todavía levanta un Postgres y un orquestador; confirmar con quien llevó el despliegue si siguen en uso antes de archivar nada.
- [ ] **Script de reversa del `0009`** en `db/migrations_mssql/rollback/` (como el del `0006`). Hoy no existe: la vuelta atrás sería manual, desde las tablas `Respaldo0009_*` que el propio script crea.
- [ ] Regla permanente: **cada migración nueva `000N` se refleja también en `db/scripts/01_crear_esquema.sql`**, para que una instalación desde cero y una base migrada queden idénticas. **Y no se edita una migración que ya está aplicada** (el `0001` al `0008` son de `QA` y de otros ambientes): un intento de cambiar el seeder del `0001` rompió las pruebas de integración del PR (`clsPasosAdmin`, `clsPasosMargen`, `clsPdfAdminMetricasEndpointsTests`, `clsEsquemaTests` esperan las 8 líneas de negocio originales). Los cambios van en un script nuevo.

### [HU-021.3] [Corrección con BD] Dominio: rutas, camiones, mercancías y conexos
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.2** (esquema validado). Detalle: `CAMBIOS_BACKEND` §3 y §9. Estimado: 3 a 4 días.

- [ ] `clsCotizacion` (cabecera): quitar las propiedades que salen (`LineaNegocioId`, `Costo`, márgenes, ruta, vehículo, `ServiciosConexos`, `CosteoId`, `CosteoUltimaConsultaAt`…) y los métodos de margen y costo que pasan a la ruta. Se sugiere dejar el nombre de la clase.
- [ ] `clsCotizacionLineaSolicitud` → la **ruta**: renombres (`CotizacionSolicitudId`, `RutaOrigenIndice`, `PrecioFinalLinea`, `ValorLevanteContenedor`), campos nuevos (línea de negocio, márgenes, totales de costo; las horas de cargue y descargue ya existen por línea desde el `0008_horas_por_linea`), municipios por id, y `RetornaContenedor`/`LevanteContenedorDiferente` como `bool` (no `bool?`).
- [ ] Mover a la ruta la lógica de margen (`ValidarAjusteMargen`, `AplicarMargenNegociado`, `RefrescarMargenSugerido`).
- [ ] **Entidad nueva `clsCotizacionVehiculoLinea`** (camión): vehículo y carrocería opcionales, peso, costos, id de la línea en Nexo, estado, id de la recomendación del cubicador.
- [ ] Entidad nueva para los **conexos** de la ruta. Reemplaza `clsServicioConexoCosteo` y `clsServiciosConexosJson`.
- [ ] `clsCotizacionPosicion` → mercancía: cuelga solo de la ruta, `NombreMercancia`, `IdTipoMercancia` (obligatorio en las filas nuevas, validado aquí).
- [ ] Implementar las **reglas de totales** (`CAMBIOS_BACKEND` §9.1): totales de la ruta, `costoFinal`, `precioFinalLinea` con la misma fórmula de hoy, `precioFinal` de la cabecera. Se guardan, no se calculan al vuelo; se recalculan en la misma transacción.
- [ ] Implementar la regla de **resumen de estado** (§9.2), igual para ruta ← camiones y cabecera ← rutas.
- [ ] `clsSolicitudCosteo`, `clsLineaSolicitudCosteo`, `clsCambiosCarga`: pasan a lista de rutas; la validación de peso es por camión.
- [ ] `clsMargenAjuste`: agregar la ruta ajustada.
- [ ] Pruebas unitarias de todo lo anterior (ver HU-021.12).

### [HU-021.4] [Corrección con BD] Persistencia: EF Core, repositorios y SQL directo
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.3.** Detalle: `CAMBIOS_BACKEND` §4. Estimado: 2 días.

- [ ] `clsCotizacionConfiguracion.cs`: tabla `CotizacionesSolicitud`, quitar los mapeos y `CHECK`s de las columnas que salen, mapear las tablas nuevas (camiones y conexos), `CotizacionMercancias`, y los FK nuevos (ruta → línea de negocio, mercancías → ruta, ajustes → ruta).
- [ ] `clsMargenConfiguracion.cs` (ajustes por ruta), `clsCotiflyDbContext.cs` (`DbSet` nuevos).
- [ ] Repositorios: posiciones y líneas por ruta; métodos nuevos para camiones y conexos; "Mis cotizaciones" con la línea de negocio de la ruta 1 (o "Varias").
- [ ] **SQL directo que EF no detecta**: `clsUnitOfWork.cs:68` (`SELECT versionNegocio FROM ...Cotizaciones`), `clsBaseDatosHealthCheck.cs:41-54` (agregar un nivel que verifique las tablas nuevas), `clsMetricasRepository.cs:24-28 y 38-76`, los scripts de `scripts/sql/` y `scripts/respaldo/` que nombran las tablas viejas.
- [ ] **Correo** (solo cambia el nombre de la tabla a la que apuntan): `clsCorreoConfiguracion.cs:34` (FK de `Correos` a la cotización), `clsCorreoRepository.cs:34` (subconsulta `cotizacionesDelCliente`, la que usa `GET /web/clientes/{nit}/destinatarios-previos`), y las FK de `CotizacionArchivosLicitacion` y `CotizacionPdfsHistorial` (`clsCotizacionConfiguracion.cs:229 y 252`). Probar que los destinatarios previos siguen apareciendo.
- [ ] Tolerar en `ListarConLineaPorUsuarioAsync` una fila con un código inesperado en vez de tumbar toda la consulta (es la parte de mediano plazo de HU-014.1; se hace aquí porque la consulta se reescribe de todos modos).

### [HU-021.5] [Corrección con BD] Integración con el cubicador
**Labels:** `cotifly`, `backend`, `integración`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.3 y 021.4, y del cubicador** (ver abajo). Detalle: `CAMBIOS_BACKEND` §6. Estimado: 2 a 3 días.

> **Depende de lo que se le pide al cubicador** (`docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md`, §4). **Bloqueante (C1):** `POST /recomendaciones`, `GET /tipos-mercancia` y `GET /tipos-carroceria` funcionando; hoy el cubicador solo tiene el diseño. **Sin eso este issue no se puede probar de punta a punta**, y se puede escribir y probar solo contra un servidor falso. También necesita: **C2** peso total por vehículo en la respuesta (es el que se le manda a Nexo), **C3** que no sobrescriba recomendaciones (Cotifly guarda el id exacto que usó), **C4** mismos códigos de vehículo y carrocería que Nexo, **C5** `Bearer` y **C6** que "sin selección" y "condicionada" vengan como resultado, no como error. Y de **N3** (Nexo) si se quiere que el admin de fletes ponga el vehículo de un camión que llegó vacío.

- [ ] `IClsCubicador` (Aplicación) y `clsCubicadorHttp` (Infraestructura), mismo patrón de `clsCosteoNexo`: opciones validadas (`Cubicador:UrlBase`, `Cubicador:ApiKey`), `HttpClient` propio `"Cubicador"` **sin reintento de POST**, fábrica con versión "no disponible", todo vía `clsResultado`.
- [ ] Autenticación: `Authorization: Bearer <CUBICADOR_API_KEY>` (confirmado por la sesión de NEXO el 2026-10-08 para los endpoints que ya existen). **Reconfirmar cuando `/recomendaciones` exista.**
- [ ] Enviar al cubicador, por ruta: `IdLineaSolicitud` = id de la ruta, `OrigenSistema = COTIFLY`, y por mercancía `idTipoMercancia`, medidas, cantidad y peso.
- [ ] Leer las filas de su Tabla 7 (vehículo, carrocería, peso, id, resultado) y crear un camión por cada una.
- [ ] Reglas: "sin selección automática" → camión con vehículo y carrocería vacíos (lo resuelve la Mesa de Costeo de Nexo); mercancía condicionada pendiente de aprobación → la solicitud se envía completa; si el cubicador no responde, la captura falla con un error claro y no se manda nada a Nexo.
- [ ] Endpoint `GET /web/tipos-mercancia` que pasa el catálogo del cubicador, para el desplegable del formulario.

### [HU-021.6] [Corrección con BD] Captura: formulario, licitación y agente de chat
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.3 y 021.4** (y de HU-021.5 para pedir camiones al cubicador). Detalle: `CAMBIOS_BACKEND` §2 y §5.1 a §5.2. Estimado: 3 a 4 días.

> **Depende del cubicador** (`docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md`, §4, peticiones C1 y C4): el tramo "pedir recomendación y crear los camiones" y el desplegable de mercancías (`GET /tipos-mercancia`) no se pueden terminar hasta que el cubicador los tenga. **Todo lo demás de este issue se puede hacer ya**, con "una ruta = un camión" y el vehículo digitado como hoy.

- [ ] `clsEnvioCosteoService`: crear cabecera → ruta → mercancías → conexos → (pedir recomendación) → camiones. Validar tipología y clase de viaje en la ruta; vehículo y carrocería ya no los digita el comercial. Ajustar `clsValidadorCatalogosCaptura.cs:53-56`, que hoy valida tipología, carrocería y tipo de vehículo juntos: pasa a validar solo lo de la ruta, y el vehículo del camión solo cuando lo corrige alguien a mano.
- [ ] Margen resuelto **por ruta** (cada ruta puede tener otra línea de negocio): `ResolverMargenAsync`.
- [ ] Licitación (`clsLicitacionService`): cada fila del Excel es una ruta con `RutaOrigenIndice` = índice de la fila. Rehacer la exportación y la vista (una fila por ruta, camiones como detalle; origen y destino desde el catálogo de municipios).
- [ ] **Flujo del agente de chat** (`clsGenerarPdfService.cs:287-307`, `clsEstadoConversacion`, `clsEstadoConversacionService.cs:123-149`): las cotizaciones del chat **hoy no tienen líneas**, con costo, margen y precio solo en la cabecera. Al salir esas columnas de la cabecera, el chat tiene que crear también una ruta y un camión (con el flete en `costoFlete`). Sembrar el estado del chat desde la ruta.
- [ ] Mientras el cubicador no exista: una ruta = un camión, con el vehículo digitado como hoy.

### [HU-021.7] [Corrección con BD] Envío a Nexo por camión, polling y emparejamiento
**Labels:** `cotifly`, `backend`, `integración`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.3 y 021.4.** Detalle: `CAMBIOS_BACKEND` §3.6, §5.3 y §5.4. Estimado: 3 a 4 días.

> **Se mejora con lo que se le pide a Nexo** (`docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md`, §3). **N1** (campo de ruta en la línea): **no bloquea**, pero sin él el emparejamiento de camiones iguales de una misma ruta depende del orden de envío y el estado podría quedar en el camión equivocado; si Nexo lo acepta, la llave de emparejamiento lo usa. **N2** (horas por línea): **ya resuelta**, Nexo las acepta por línea (HU-013.11). **N3** (vehículo editable en la Mesa) y **N4** (confirmar línea sin vehículo con una prueba real): solo importan para los camiones que llegan sin vehículo.

- [ ] `clsCosteoNexo.cs:340-356`: armar **una línea por camión**, con los datos de su ruta. El nombre del municipio se resuelve desde su id contra `GET /api/captura-solicitudes/municipios`. Sin `posiciones[]`: a Nexo le basta el peso del camión (ver `docs/conexion_nexo/PROXIMOS_ALCANCES.md`). Encabezado sin `ValorEstimado`/`Referencia`. Las horas de cargue y descargue van **por línea** (cada camión lleva las de su ruta): Nexo ya las acepta en `CapturaLineaRequest` (HU-013.11, `0008_horas_por_linea`) y `QA` ya las guarda por línea.
- [ ] **Emparejador nuevo** (`clsEmparejadorLineasNexo`): empareja camiones, no líneas de ruta. Es lo más frágil del cambio. Si Nexo agrega el campo de ruta, la llave lo usa; mientras tanto, origen y destino resueltos, vehículo, carrocería, peso y el orden de envío.
- [ ] Polling (`clsPollingCosteoService`, `clsPrecioCosteoService`): registrar flete y retorno en el **camión**, asignar `LineaSolicitudNexoId` por camión, recalcular ruta y cabecera. Quitar `CosteoUltimaConsultaAt` y `CosteoId` (ahora `CotizacionNexoId`).
- [ ] Leer el vehículo de cada línea cuando la Mesa de Costeo se lo asigna a una que iba vacía.
- [ ] Probar con un servidor de Nexo real o falso el caso de **dos camiones iguales en la misma ruta** (empate por orden).
- [ ] Sin el campo de ruta de Nexo (N1) hay que probar el emparejamiento con camiones iguales en la misma ruta, que es el caso frágil. Si Nexo lo acepta, probar también con el campo.

### [HU-021.8] [Corrección con BD] Margen, conexos y precio por ruta
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.3 y 021.4.** Detalle: `CAMBIOS_BACKEND` §5.5 y §5.6. Estimado: 3 días.

- [ ] `clsAjusteMargenCosteoService`: el ajuste se hace **sobre una ruta**; la petición a la API dice cuál. El `MargenAjuste` guarda la ruta. Recalcular desde los totales de la ruta, no desde `linea.Costo` y el JSON.
- [ ] `clsAjusteMargenService`, `clsMargenResolverService`, `clsMargenResuelto`: por ruta.
- [ ] Conexos (`clsServiciosConexosCosteoService`, `clsCosteoCotizacionService.SeleccionarServiciosConexosAsync`): escribir y leer de la tabla nueva; ubicar la ruta; la validación "ya está en Nexo" mira si algún camión de la ruta tiene línea en Nexo; si hay que mandar conexos a Nexo, se pegan a la línea del primer camión.
- [ ] Después de cualquier cambio de conexos: recalcular `costoServiciosConexos` y los totales, invalidar el PDF y subir `versionNegocio` (esto último ya se hace hoy).

### [HU-021.9] [Corrección con BD] PDF, textos, re-solicitud, nueva versión y "Mis cotizaciones"
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.3, 021.4 y 021.8.** Detalle: `CAMBIOS_BACKEND` §5.7 a §5.9. Estimado: 3 a 4 días.

- [ ] `clsCosteoCotizacionService`: datos del PDF por ruta; PDF de una ruta sale de la ruta y sus camiones ("2 × tractomula furgón 3S3"); PDF de pliego, una sección por ruta; `EsPliego` = "tiene más de una ruta"; la validación del total ahora es `precioFinal` = suma de `precioFinalLinea`; rehacer `Vista()`.
- [ ] `clsTextosCosteoService`: línea de negocio, origen, destino y vehículo desde ruta y camiones; quitar el respaldo a la cabecera.
- [ ] `clsReSolicitudCosteoService` y `clsNuevaVersionService` (`clsDatosNuevaVersion`): copiar por ruta; si cambian mercancías o peso, borrar los camiones de la ruta y volver a pedir la recomendación.
- [ ] `clsMisCotizacionesService` y `clsCotizacionResumen`: margen y origen del margen pasan a ser de la ruta; línea de negocio de la ruta 1 o "Varias".
- [ ] `clsResultadoCotizacionService`: `CosteoId` → `CotizacionNexoId`.

### [HU-021.10] [Corrección con BD] Métricas y vistas
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.4.** Se coordina con HU-014.4 (retirar el borrador obsoleto del tablero). Detalle: `CAMBIOS_BACKEND` §3.7 y §4.4. Estimado: 1 día.

- [ ] `clsCotizacionMetrica`, `clsCalculadoraMetricas`, `clsMetricasRepository` (CTE del tablero, líneas 38-76): leer costo, margen y precio por ruta. Decidir cómo se cuenta una solicitud con rutas de varias líneas de negocio: recomendación, **seguir contando cotizaciones**, con el margen promedio ponderado por costo.
- [ ] **Decidir con HU-014.4:** las vistas `vActividadPorComercial` y `vAjustesMargenPorComercial` ya están adaptadas en `0009` y en `01_crear_esquema.sql`, pero HU-014.4 propone retirarlas junto con los endpoints obsoletos que las leen. Si HU-014.4 se hace primero, se quitan del esquema en vez de adaptarlas. HU-014.4 depende de que el tablero de HU-010 esté validado en producción.

### [HU-021.11] [Corrección con BD] Contrato de la API (DTOs y `openapi.yaml`)
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Depende de HU-021.6 a 021.9.** Detalle: `CAMBIOS_BACKEND` §7. Estimado: 1 a 2 días.
**El frontend no está en este documento:** sus HU se redactan aparte. Este contrato rompe el que usa el frontend de hoy, por eso se recomienda mantener el formato viejo mientras tanto (último punto de la lista).

- [ ] `clsSolicitudCosteoWebRequest` pasa de una línea plana a una lista de rutas (línea de negocio, municipios por id, condiciones, horas, valor del levante, mercancías con `idTipoMercancia`, conexos), sin vehículo ni carrocería. Posiciones con `nombreMercancia` e `idTipoMercancia`.
- [ ] `GET /web/cotizaciones/{folio}/costeo` devuelve rutas con sus camiones, mercancías, conexos, totales, margen y precio. Las peticiones de margen y conexos dicen a qué ruta aplican. `clsDtosLicitacionWeb`: sin vehículo, con mercancías.
- [ ] Actualizar `specs/openapi.yaml` y `docs/docs_front/CONTRATO_API_WEB.md`.
- [ ] Mientras el frontend nuevo no esté listo, aceptar el formato plano viejo convirtiéndolo a "una ruta, un camión" (1 día más). Quitarlo cuando el frontend nuevo salga.

### [HU-021.12] [Corrección con BD] Pruebas y QA
**Labels:** `cotifly`, `backend`, `qa`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA) + Duban (Backend)
**Parte de HU-021 (milestone). Transversal: se arma a medida que avanzan las demás.** Detalle: `CAMBIOS_BACKEND` §8. Estimado: 4 a 5 días.

- [ ] Adaptar los ~65 archivos de prueba que usan los nombres y campos viejos. Los que más cambian: `clsEsquemaTests` (compara EF contra el esquema, con listas de columnas), `clsEnvioCosteoServiceTests`, `clsReSolicitudCosteoServiceTests`, `clsPrecioCosteoLineasTests`, `clsAjusteMargenCosteoServiceTests`, `clsServiciosConexosCosteoServiceTests`, `clsCotizacionTests`, `clsLineasSolicitudTests`, `clsSolicitudCosteoTests`, `clsPdfPliegoTests`.
- [ ] Las pruebas con **SQL directo** fallan al correr, no al compilar: `Api.Tests/Web/*Endpoints*`, `clsMetricasTableroRepositoryTests`, `Aceptacion.Tests` (`clsPasosMetricas.cs:52`, `clsPasosAgente.cs:339`, `clsApiWebPruebas.cs:230`).
- [ ] Quitar las aserciones de `CosteoUltimaConsultaAt` (`clsDerivacionEstadoCosteoTests.cs:73`, `clsPollingCosteoServiceTests.cs:55`) y rehacer el fixture `clsSolicitudCosteoMuestra`.
- [ ] **Regla de las pruebas de esquema:** no agregar el `0009` a la cadena de las pruebas (Testcontainers) hasta que el backend esté adaptado. Cuando lo esté, pueden aplicar `db/scripts/01_crear_esquema.sql` en vez de `0001` a `0009`.
- [ ] Pruebas nuevas: totales de la ruta, resumen de estados, emparejamiento de camiones (con empates), ruta con camión sin vehículo, adaptador del cubicador con un servidor falso, y migración de datos (`0009` contra una copia).
- [ ] Prueba de punta a punta: formulario y licitación → cubicador → Nexo → polling → margen → PDF. **Depende del cubicador y de Nexo** (`docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md`): hasta que el cubicador tenga `POST /recomendaciones` (C1), la prueba se hace con "una ruta = un camión" y un cubicador falso.

### [HU-021.13] [Corrección con BD] Reglas pendientes de la propuesta original de refactor (HU-002.1)
**Labels:** `cotifly`, `backend`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-021 (milestone). Independiente: se puede hacer en cualquier momento.** Origen: `docs/database/PROPUESTA_REFACTOR_BD_COTIFLY.md`, verificado el 2026-10-08 contra el código. Estimado: menos de 1 día.

**Pendiente de verdad:**
- [ ] **`motivoRechazo` = "No aplica" cuando se marca ganada** (§2.1: no se deja `null`; solo queda vacío mientras no se ha marcado ningún resultado). Hoy `clsCotizacion.cs:662-667` (`MarcarSeguimiento`) guarda el motivo tal cual, y `clsSeguimientoService.cs:54` y `clsResultadoCotizacionService.cs:127` lo pasan sin tocar.
- [ ] **`CHECK` de `estadoEnvio`**: es el mismo punto de HU-021.2 / HU-014.1; se anota aquí para no perderlo.

**A verificar:**
- [ ] El frontend marca ganada/perdida contra `POST /web/cotizaciones/{folio}/seguimiento`, pero para las cotizaciones del flujo de Nexo el backend las rechaza ahí con `USAR_RESULTADO_NEXO` (`clsSeguimientoService.cs:46-52`) y espera `POST /web/cotizaciones/{folio}/resultado` (`clsResultadoCotizacionService.cs`). Confirmar a cuál llama el frontend. Si es `/seguimiento`, el botón falla para esas cotizaciones. Afecta a HU-019.1.
- [ ] `clsInyeccionEstadoFilter.cs:49` todavía lista `fuente_tarifa` y `cliente_id_sap` entre los argumentos que se le quitan al modelo. Es una lista defensiva, probablemente inofensiva; revisar si conviene dejarla.

**Decisiones a cerrar con negocio:**
- [ ] **Formato del folio:** la propuesta decía "complementar con el BP del cliente"; lo implementado es `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}`, con el NIT (opcional) y las iniciales del comercial. Decidir si se acepta lo implementado y se actualiza la propuesta.

**Ya resuelto (no hay que hacer nada):**
- [x] `MargenesPolitica.metodoCalculo` **sí se usa** (cálculo del precio, validación del margen y PDF): la decisión abierta que dejó la auditoría vieja ya no aplica.
- [x] `estadoEnvio` pasa a `reenviada` en el segundo envío (`clsCotizacion.cs:670-673`).
- [x] Destinatarios previos del cliente precargados (`GET /web/clientes/{nit}/destinatarios-previos`).
- [x] Ganada/perdida llama primero a Nexo y solo guarda local si responde 200; el 409 `RESULTADO_YA_DEFINIDO` no cambia nada local (`clsResultadoCotizacionService.cs:92-107`).
- [x] El resultado se rechaza mientras la cotización está en `pendiente_costeo` (`COTIZACION_NO_ENTREGADA`).
- [x] Endpoints de administración de líneas de negocio (`/web/admin/lineas-negocio`, política `GerenteComercial`).
- [x] `origenCliente` (cliente/prospecto) persistido y usado.
- [x] Sin referencias a `comercial_email`, `bdCliente` ni `Sesiones.contexto` en el backend.

### [HU-021.14] [Corrección con BD] Actualizar la documentación
**Labels:** `cotifly`, `datos`, `documentación`, `corrección-bd`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos) + Duban (Backend)
**Parte de HU-021 (milestone). Depende de que lo anterior esté hecho.** Hoy estos documentos describen el esquema viejo.

- [ ] `BASE_DE_DATOS.md` (diagrama ER y migraciones), `docs/database/DICCIONARIO_BASE_DE_DATOS.md`, `docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`: reflejar el esquema nuevo.
- [ ] ADR afectados: `ADR-014` (columnas de moneda), `ADR-015` (costeo en Nexo), `ADR-017` (licitaciones multilínea), `ADR-018` (PDF y concurrencia). Agregar un ADR nuevo con la decisión de las tres capas solicitud → ruta → camión y de que cubica el sistema donde nace la solicitud.
- [ ] `backend-dotnet/docs/` (`02-arquitectura`, `03-infraestructura`, `06-ejecucion-local`, `08-testing`, `09-estructura-del-proyecto`, `10-operacion`) y `specs/features/persistencia.feature`.
- [ ] `DEPLOY.md` (la precondición "`0001` a `0005` aplicados") y la tabla de errores de `/health/ready` en `10-operacion` (hoy habla de "falta 0004", "falta 0007"): agregar el nivel del esquema nuevo.
- [ ] `db/README.md`: quitar la advertencia de "sin probar" cuando HU-021.2 esté cerrada.
- [ ] **Rutas de documentos dentro de `backend-dotnet/`** (28 archivos: docs, ADR, `openapi.yaml`, comentarios en código). Los documentos de `docs/` se reorganizaron el 2026-10-08 y esas referencias siguen con la ruta vieja. La lista exacta y el mapa de rutas viejas y nuevas están en el punto 11 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`.

---

## Dependencias externas (no son issues)

Lo que se le pide a **Nexo** (N1 a N4) y al **cubicador** (C1 a C6) está en `docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md`, con la prioridad de cada petición y qué pasa si no se hace. Aquí solo el resumen de qué HU depende de qué:

| Con quién | Qué | Afecta a |
|---|---|---|
| Cubicador | C1 a C6: `POST /recomendaciones` y catálogos funcionando, peso por vehículo, no sobrescribir recomendaciones, mismos códigos que Nexo, `Bearer`, resultados que no son error | HU-021.5, 021.6, 021.12 |
| Nexo | N1: campo de ruta en la línea (para emparejar camiones) | HU-021.7 |
| Nexo | N3: vehículo editable en la Mesa; N4: confirmar línea sin vehículo. (N2, horas por línea, ya está resuelta: Nexo las acepta por línea desde HU-013.11) | HU-021.5, 021.7 |
| Negocio / SAP | El `TrqType` de SAP para `DevolucionContenedor`. No se inventa: hay que pedirlo. | HU-021.2 |
| DBA | Visto bueno de `01_crear_esquema.sql` y `0009` | HU-021.2 |

## Riesgos

1. **El emparejamiento con Nexo es lo más frágil.** Sin el campo de ruta, dos camiones iguales de la misma ruta solo se distinguen por el orden de envío; sus costos serían iguales, pero el estado podría mostrarse en el camión equivocado.
2. **El cubicador todavía no existe en código.** La fase 4 depende de él.
3. **El costo viejo no se puede desglosar exacto** al migrar con el `0009`: el flete se calcula como costo menos levante, y el retorno queda en 0.
4. **Métricas:** una solicitud con rutas de varias líneas de negocio obliga a decidir cómo contarla (HU-021.10).
5. **El contrato de la API cambia** (HU-021.11): hasta que el frontend nuevo esté listo, el backend tiene que aceptar el formato plano viejo, o el frontend actual deja de funcionar.
