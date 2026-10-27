# Backlog histórico Cotifly (2026-09-29 a 2026-10-20)

**Repo:** `Cotifly` · **Compilado:** 2026-10-27, a partir de 6 documentos de `docs/backlog/`:
`2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`, `2026-10-07_HU_Sprint5_Cotifly.md`,
`2026-10-14_HU_Correcciones_Nuevas_Cotifly.md`, `2026-10-16_PENDIENTES_DUBAN.md`,
`2026-10-19_HU_Sprint6_Cotifly_Backend.md` y `2026-10-20_PENDIENTES_DUBAN.md`.

**Qué es esto:** un índice único de todo lo que se identificó o creó como backlog en ese rango, con la
fecha en que cada punto se detectó por primera vez (no la fecha del documento donde se repitió después) y
su estado real al día de hoy (verificado contra GitHub, 2026-10-27). **No reemplaza** a los documentos
originales (quedan como registro detallado) ni a los pendientes vivos por persona
(`2026-10-27_PENDIENTES_SARA.md` y el `PENDIENTES_DUBAN` vigente) — esto es la foto histórica completa.

Varios puntos aparecen primero como hallazgo suelto (2026-09-29) y después se formalizan como HU con
issue de GitHub (Sprint 5 el 2026-10-07, Sprint 6 el 2026-10-19); se listan **una sola vez**, en la
sección de su primera aparición, con el estado final ya actualizado.

---

## 1. Hallazgos del 2026-09-29 — pruebas en vivo contra la base compartida

Fuente: `2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`. Varios puntos traen su propia fecha, más específica
que la del documento.

| Ítem | Fecha | Estado actual | GitHub | Resumen |
|---|---|---|---|---|
| **HU-014.1** `estadoEnvio` sin `CHECK`: un valor inválido tumbaba TODO "Mis cotizaciones" | 2026-09-29 | ✅ Resuelto, absorbido en HU-021.2/021.4 (fuera de este rango) | — (sin issue propia) | El CHECK ya quedó en `01_crear_esquema.sql`, probado. La tolerancia a filas malas se hizo al reescribir la consulta. |
| **HU-014.2** Confirmar contra qué base insertó Duban su cotización de prueba | 2026-09-29 | ⚫ Descartado (2026-10-08) | — | La base compartida se borró y recreó con el esquema nuevo; la pregunta perdió sentido. |
| **HU-015.1** Migrar tipo de vehículo / clase de viaje al catálogo maestro de Nexo | 2026-10-05 | ✅ Cerrado | [#153](https://github.com/InnovacionIA-proyectos/Cotifly/issues/153) | Cotifly los traía de un endpoint de histórico de tarifas, no del catálogo maestro (`GET /api/catalogos/*`). Alcance recortado el 2026-10-08: vehículo/carrocería pasaron a salir del cubicador, solo quedó clase de viaje. |
| **HU-015.2** Conectar clase de contenedor al catálogo real de Nexo | 2026-10-05 | ✅ Cerrado (junto con HU-015.1) | [#153](https://github.com/InnovacionIA-proyectos/Cotifly/issues/153) | Cotifly usaba un catálogo hardcodeado propio en vez de `GET /api/catalogos/clases-contenedor`. |
| **HU-015.3** Servicios conexos: catálogo disponible + enviar la selección del comercial a Nexo | 2026-10-05 | ✅ Cerrado (junto con HU-015.1/.2) | [#153](https://github.com/InnovacionIA-proyectos/Cotifly/issues/153) | El hallazgo más importante del grupo: la elección de conexos del comercial nunca llegaba a Nexo, solo se calculaba local. |
| **HU-015.4** (nota) Línea de negocio: ¿pasar a catálogo en vivo? | 2026-10-05 | ⚪ No es un hueco, solo mantenimiento — sin construir | — | Cotifly usa su propio catálogo (`MargenesPolitica`) con mapeo manual; no es un bug. |
| **HU-014.3** Commitear como definitivo el header `X-Portal-clsUsuario-Id` | 2026-10-02 (decisión: 2026-10-05) | ✅ Resuelto (#175) | [#152](https://github.com/InnovacionIA-proyectos/Cotifly/issues/152) | Nexo no iba a corregir el nombre del header; el ajuste quedó permanente del lado de Cotifly, centralizado en `clsIdentidadNexo.cs`. |
| **HU-016.1** Endpoint `GET /web/cotizaciones/regional` (listado del Gerente Comercial) | 2026-10-05 | ✅ Hecho, confirmado en QA el 2026-10-19 | [#154](https://github.com/InnovacionIA-proyectos/Cotifly/issues/154) | No existía ningún listado fila por fila de las cotizaciones de la regional; solo el agregado de métricas. |
| **HU-017.1** "Margen promedio": redefinir contra qué se compara | 2026-10-05 | 🟡 Pendiente de decisión de negocio | dentro de [#154](https://github.com/InnovacionIA-proyectos/Cotifly/issues/154) (sin issue propia) | Pedido explícito de Sara: comparar contra el promedio de márgenes de política, no contra los ajustes negociados (ADR-016 define hoy la segunda opción). Sigue sin decidir al 2026-10-19. |
| **HU-018.1** Exponer `deadline`/`origen`/`destino`/`pesoKg`/`valorMercancia` en el DTO de "Ver detalle" | 2026-10-05 | 🟢 Hecho en parte — confirmado en QA (#178) que ya trae `deadline`, `score_prioridad` y `comercial` | [#155](https://github.com/InnovacionIA-proyectos/Cotifly/issues/155) | `scoreUrgencia` sigue bloqueado (depende de que Nexo lo exponga por solicitud). |
| **HU-018.2** (catálogo de referencia) Todo lo que "Ver detalle" podría mostrar, inspirado en la Mesa de Costeo de Nexo | 2026-10-05 | ⚪ Backlog futuro, sin construir | — (nota dentro de la milestone HU-018) | Desglose de costeo, auditoría de quién hizo qué, etc. — priorizar con negocio más adelante. |
| **HU-019.1** Ganada/Perdida: motivo (lista) + comentario (libre) separados | 2026-10-05 | ✅ Resuelto distinto a lo planteado (2026-10-16) | HU-021-FE.11/#79 (parte 3) + HU-021-FE.12/#83, en `clientepro-frontend` | No hizo falta cambiar contrato: `POST .../resultado` ya separaba motivo/comentario; el frontend migró de `/seguimiento` (que fallaba con 409 para cotizaciones de Nexo) a `/resultado`. |
| **HU-020.1** "Exportar Excel" en Ver Detalle | 2026-10-05 | 🔴 Pendiente, sin issue propia | — | Botón deshabilitado desde que se construyó la pantalla; falta investigar qué exporta Nexo y contra qué endpoint. |
| Script `0009` y nueva instalación de BD desde cero | 2026-10-08 | ✅ Hecho | — | Probado el 2026-10-08 en `dbTarifario_Nexo_Pruebas`; Alembic/Postgres se retiraron. Precursor de HU-024. |
| Referencias a documentos dentro de `backend-dotnet/` (28 archivos con ruta vieja) | 2026-10-08 | 🟡 No bloquea nada (son comentarios y enlaces) — cubierto dentro del alcance de HU-022.1 | [#146](https://github.com/InnovacionIA-proyectos/Cotifly/issues/146) | La carpeta `docs/` se reorganizó el 2026-10-08; dentro de `backend-dotnet/` no se tocó nada por el freeze de ese momento. |
| **HU-022.1** Limpiar el código viejo (stack Python/Postgres) y dejar documentación profesional | 2026-10-08 | ✅ Cerrado | [#146](https://github.com/InnovacionIA-proyectos/Cotifly/issues/146) | Candidatos identificados: `mcp_server/`, `orquestador/`, Alembic, `bot-db`/`pgdata` en los compose, documentos del stack viejo. |

---

## 2. Hallazgos nuevos del 2026-10-14 — "Correcciones nuevas"

Fuente: `2026-10-14_HU_Correcciones_Nuevas_Cotifly.md` (revisado el 2026-10-16). Formaliza y confirma lo
de la sección 1 contra `origin/QA`, y agrega lo siguiente, que no estaba antes:

| Ítem | Fecha | Estado actual | GitHub | Resumen |
|---|---|---|---|---|
| CORS no permite `If-Match` ni `Idempotency-Key` desde otro origen | 2026-10-14 | ✅ Resuelto (#178), verificado en QA el 2026-10-19 | #178 | Bloqueaba en el navegador toda mutación con concurrencia optimista o idempotencia (conexos, margen, nueva versión, captura). |
| Aplicar el script `07` (motivo `BUSQUEDA_AGOTADA`) en la base compartida | 2026-10-14 | ✅ Resuelto | #176 (backend) | El backend ya conocía el motivo (#176); faltaba correr el script en la compartida — hecho. |
| Confirmar `Captura:AceptarVehiculoCliente` y las banderas de Nexo por ambiente | 2026-10-14 | 🟢 Absorbido en HU-025.1 (parte A) | [#190](https://github.com/InnovacionIA-proyectos/Cotifly/issues/190) (abierto) | Con la bandera apagada, el backend ignora el vehículo que digita el cliente. |
| **HU-010.5** Filtro por año y mes en `GET /web/metricas/regional` | 2026-10-16 (parche escrito ese día) | ✅ Cerrado | [#189](https://github.com/InnovacionIA-proyectos/Cotifly/issues/189) | Parche completo guardado en `docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`; lo necesitaba el selector año/mes del tablero del gerente (frontend #51). |
| Confirmar recorte de NEXO#297 (S5-HU-20.5) y estado de S5-HU-19.3/19.4 en el portal | 2026-10-14 | 🟡 Coordinación externa, sin confirmar en estos documentos | — (repos `NEXO` / `administracion_embarqa_nexo`) | Bandeja de normalización de vehículos en cuarentena y edición de vehículo en la Mesa, pendientes del lado del portal. |
| **HU-025.1 (parte B)** Rol `comercial` de 34 a 32 + base de identidad `dbSolucionesTI_Prueba` | 2026-10-14 | 🟢 Issue abierta (parte A sigue pendiente); el cambio del rol en sí ya se aplicó en la compartida (ver `2026-10-27_PENDIENTES_SARA.md`) | [#190](https://github.com/InnovacionIA-proyectos/Cotifly/issues/190), script [#173](https://github.com/InnovacionIA-proyectos/Cotifly/issues/173) | Tres partes en una ventana: base de datos (DBA), configuración (Duban) y frontend (Sara, `cotifly-roles.ts`). |
| **HU-026.1 (parte A)** Mapear `ordenVehiculo` e `idServicioConexoCatalogo` (columnas del script `03`) | 2026-10-14 | ✅ Cerrado | [#191](https://github.com/InnovacionIA-proyectos/Cotifly/issues/191) | Hasta mapearlas, el orden del camión salía de un desfase de 1 ms y el conexo se resolvía por nombre. |
| `TrqType` de SAP para `DevolucionContenedor` | 2026-10-14 | 🔴 Pendiente — depende de negocio/SAP, no se inventa | — | Va en `db/seeder/02_sap_mapeo_trqtype.sql` cuando llegue el valor real. |
| **HU-026.1 (parte C)** Aplicar `06_idempotencia_captura.sql` (`CapturasIdempotentes`) | 2026-10-14 | 🟢 Tabla aplicada en la compartida (2026-10-22); falta que el backend la use | [#191](https://github.com/InnovacionIA-proyectos/Cotifly/issues/191) | No aplicar hasta que el backend deje de guardar la idempotencia en memoria — ya aplicada, falta el consumo. |
| **HU-027.1** Funciones que el contrato web no tiene y el frontend necesita (detalle por folio, `DatosNuevaVersion` ampliado, varias rutas en una re-solicitud, total en el correo, `clases_contenedor`) | 2026-10-16 (hallazgo al construir el frontend) | ✅ Cerrado | [#179](https://github.com/InnovacionIA-proyectos/Cotifly/issues/179) | 6 huecos encontrados contra el contrato real; el listado regional (uno de los 6) ya estaba cubierto por HU-016.1/#154. |
| **HU-024** Organizar los scripts de base de datos en una sola carpeta | 2026-10-16 | 🟢 HU-024.1 hecha sin issue propia (confirmado 2026-10-27: `db/scripts/pendientes/` ya no existe); HU-024.2 abierta | [#203](https://github.com/InnovacionIA-proyectos/Cotifly/issues/203) | Pasar `03`–`07` de `pendientes/` a `db/scripts/`, y luego consolidar todo en el `01_crear_esquema.sql` + poner al día la documentación. |
| Identificador de mercancía hacia el cubicador: `codigoMercancia`, no `idTipoMercancia` (S5-HU-17.4) | 2026-10-14 (decisión) | ✅ Decidido — falta que Brayan corrija el texto de la HU | — (repo del cubicador) | Registrado en Correcciones Nuevas como ya resuelto. |

---

## 3. Hallazgo nuevo del 2026-10-16 — rechazo de una línea en la Mesa de Costeo de NEXO

Fuente: `2026-10-16_PENDIENTES_DUBAN.md` (punto 8), formalizado como milestone en Sprint 6.

| Ítem | Fecha | Estado actual | GitHub | Resumen |
|---|---|---|---|---|
| **HU-028** Rechazo de una línea con motivo en la Mesa de Costeo de Nexo | 2026-10-16 (decisión de Sara) | ✅ HU-028.1 y HU-028.2 cerradas; HU-028.3 (QA) sigue abierta | Milestone [HU-028](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/20) — [#183](https://github.com/InnovacionIA-proyectos/Cotifly/issues/183) (backend, cerrado), [#182](https://github.com/InnovacionIA-proyectos/Cotifly/issues/182) (BD, cerrado, PR #200), [#192](https://github.com/InnovacionIA-proyectos/Cotifly/issues/192) (QA punta a punta, abierto) | El Administrador de Tarifas puede rechazar una línea sin frenar la cotización; no suma al total, no lleva precio, y el motivo solo lo ve el comercial (PDF/Excel la muestran como "Sin costeo"). |

---

## 4. Hallazgos nuevos del 2026-10-20 — cierre de los pendientes de backend

Fuente: `2026-10-20_PENDIENTES_DUBAN.md` (documento ya cerrado ese mismo día: de 5 temas evaluados, 2 ya
existían como issues, 3 se crearon).

| Ítem | Fecha | Estado actual | GitHub | Resumen |
|---|---|---|---|---|
| **HU-034** Detalle regional completo + ganancias mensuales para el Gerente Comercial | 2026-10-20 | 🔴 Backend sin empezar; tiene issue de seguimiento en frontend | Milestone [HU-034](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/26) — [#201](https://github.com/InnovacionIA-proyectos/Cotifly/issues/201); frontend `clientepro-frontend#119` | `GET /web/cotizaciones/{folio}/costeo` y el Excel de licitación son hoy "solo el dueño", sin excepción para el gerente; `serie_mensual` no trae ganancias/costeo por mes. |
| `serie_mensual` sin ganancias ni costeo por mes | 2026-10-20 | Mismo issue que HU-034 (parte B) | [#201](https://github.com/InnovacionIA-proyectos/Cotifly/issues/201) | Bloquea dos piezas del Dashboard del gerente, hoy con datos de maqueta. |
| Correo/D3 — candidato MailSender API | 2026-10-20 | ⚫ No se creó nada — ya existía | [HU-033/#199](https://github.com/InnovacionIA-proyectos/Cotifly/issues/199) | Superado por las "Decisiones del líder técnico (2026-10-22)": adopta MailSender, protección fuera de Production, precio sin desglose. |
| Endpoint de líneas de negocio para Comercial | 2026-10-20 | ⚫ No se creó issue nueva — se solapaba | Comentario en [#154](https://github.com/InnovacionIA-proyectos/Cotifly/issues/154#issuecomment-6346132852) | El margen **no** es confidencial para Comercial (ya se filtra vía el chat, herramienta `obtener_margen`) — contradice la suposición inicial. |
| **HU-035** Importar mercancías de una ruta desde Excel | 2026-10-20 | 🔴 Backend sin empezar; tiene issue de seguimiento en frontend | Milestone [HU-035](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/27) — [#202](https://github.com/InnovacionIA-proyectos/Cotifly/issues/202), [#213](https://github.com/InnovacionIA-proyectos/Cotifly/issues/213); frontend `clientepro-frontend#120` | `POST /web/licitaciones/analizar-excel` reenvía el Excel completo a Nexo (parsea pliegos de licitación); un Excel de mercancías sueltas necesita un endpoint propio que solo devuelva la grilla cruda. |
| Hallazgo adicional: peso con tara en lo que Cotifly manda a Nexo (decisión D2/D2b) | 2026-10-20 (remarcado; la HU ya existía antes de este rango) | Ver milestone [HU-030](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/22) (fuera de este rango de documentos) | [#184](https://github.com/InnovacionIA-proyectos/Cotifly/issues/184) | El comercial debe escribir el peso **con tara**, y ese valor (no el neto) se manda a Nexo — contradice una respuesta previa dada a `clientepro-frontend-fe` sobre `pesoKg`, ya corregida. |

---

## Notas

- **HU-021** ("[Corrección con BD] Adaptar Cotifly a la reestructuración de la base de datos") es la HU
  marco que casi todo lo de arriba da por supuesta — su propio documento (`2026-10-08_HU_Correccion_BD_Cotifly.md`)
  vive en `docs/archivo/backlog/`, fuera de este rango, por eso no se detalla aquí.
- Los pendientes **vivos** (lo que falta hacer hoy, no el historial) están aparte: ver
  `2026-10-27_PENDIENTES_SARA.md` para lo de Sara.
