# HUs finales

**Fecha:** 2026-10-25 (Director agregado el 2026-10-26) · **Repo:** `Cotifly` (backend) + `clientepro-frontend`. **Fuente:** pedidos de Sara, verificados contra `origin/QA` del backend.

**Qué es esto:** documento de trabajo, no issues ni milestones de GitHub todavía (Sara pidió solo el documento). Roles: **Duban** = Backend (lógica), **Sara** = Datos (scripts de BD) y Frontend.

**Numeración:** siguiendo las milestones de GitHub de `Cotifly` (hasta `HU-035`) y de `clientepro-frontend` (que refleja el número del backend con sufijo `-FE`, p. ej. `HU-030-FE`). Estas dos HU todavía no tienen milestone creada.

**Contenido:**
1. **Cotización finalizada** — milestones `HU-036` (1 Datos + 3 Backend) y `HU-036-FE` (3 Frontend): 7 issues.
2. **Rol Director** — milestones `HU-037` (backend) y `HU-037-FE` (frontend): 5 issues (2 Datos + 2 Backend + 1 Frontend).
3. **Separar el dashboard por módulos** — milestone `HU-038-FE`: 1 issue, solo Frontend, al final del documento.

---

# Parte 1 — HU-036 / HU-036-FE — Cotización finalizada

## El flujo que pide Sara

Comercial crea la solicitud → Nexo la autocostea o la Mesa la costea a mano → el comercial entra a "Ver detalle", ve sus rutas con el margen sugerido (SAP o política) y lo ajusta si hace falta → **Finalizar** → recién ahí se habilitan PDF, correo, marcar ganada/perdida y "Redactar con IA". Después de finalizar, ningún cambio que mueva el precio (margen, conexos, re-solicitar) se permite en sitio: toca una versión nueva (`-vN`). No hay "reabrir".

## Lo que ya verifiqué en el código (no es una propuesta, es lo que hay hoy)

Esto corrige/confirma lo que traía `clientepro-frontend-f3`:

- **`clsReglaPdfCosteo.Validar` + `clsCotizacionSolicitud.ValidarCambioEtapaFinal`** (`src/Cotifly.Dominio/Cotizaciones/clsCotizacionSolicitud.cs:1259`, `Aplicacion/Costeo/Services/clsReglaPdfCosteo.cs`) son el candado real de "ya entregada" hoy — lo usan margen, conexos, conexos de Nexo, re-solicitud y textos (6 servicios). `EntregadaAlCliente` (línea 151) = `estado_envio` en `enviada`/`reenviada` **o** `Ganada is not null`. Es independiente de `estado_costeo`. **Confirmado: el mismo mecanismo sirve para "finalizada"** — la forma más barata es agregar `|| Finalizada` a esta condición (o una nueva `BloqueadaParaEdicion`), no duplicar el candado.
- **`IClsMargenResolverService`** existe en `src/Cotifly.Aplicacion/Margen/Services/` (confirmado el nombre exacto). El margen SAP válido es `clsReglasMargen.EsAnomalo` fuera de `[0, 60%]` (`clsLineaSolicitud.cs:1079`); `SugeridoVigente = MargenSapValido ?? MargenPoliticaSugerido` (línea 1071). Confirmado tal cual.
- **ADR-015 §4 y §5:** confirmados sin cambios — 72h de polling sin costeo pasa `pendiente_costeo` → `costeo_vencido`; `autocosteo` deja de consultarse sin cambiar de estado. El precedente de "congelar" (§5): si el costeo cambia después de que hay PDF, el precio **se conserva**, se audita y `motivoEstadoCosteo` lo advierte. Buen precedente para "Nexo no debe poder cambiar el precio después de finalizada" — se puede replicar la misma mecánica (congelar y auditar) en vez de depender de que Nexo acepte "entregar" la solicitud.
- **`POST .../resultado`** (`clsResultadoCotizacionService.cs:152`) exige `estado_costeo` en `{Entregada, Autocosteo, PendienteCosteo}`, si no `NoEntregada` (`COTIZACION_NO_ENTREGADA`). `descartada`/`costeo_vencido`/chat usan `cotizacion.ResultadoSoloLocal` (línea 119) → responde pidiendo usar `/seguimiento`. Confirmado: es Cotifly-only, Nexo no interviene ahí.
- **`clsResumenEstadoCosteo`** (`src/Cotifly.Dominio/Cotizaciones/clsResumenEstadoCosteo.cs:46-49`, HU-028.1 ya mergeado): las rutas `rechazada` se **ignoran** al resumir — si quedan otras rutas vigentes, la cabecera sigue avanzando normal (`autocosteo`/`entregada`) con las rechazadas simplemente excluidas del total, igual que hoy. Si **todas** las rutas de la cabecera quedan `rechazada` (o rechazada+descartada), `clsCotizacionSolicitud.EstadoCabecera` (línea 671-680) la deja en `entregada`/`descartada` si Nexo ya lo dice, o si no **`pendiente_costeo`** — nunca "cabecera rechazada". **Esto responde la pregunta abierta de rutas rechazadas, ver abajo.**
- **Licitación no es una máquina de estados aparte:** `TipoSolicitud` (`Spot`/`Licitacion`) es un campo más de la MISMA `clsCotizacionSolicitud` (`TipoSolicitud.cs`, usado en `clsLicitacionService.cs`). Estructuralmente, la regla de finalizar aplicaría igual a ambas salvo que negocio decida lo contrario.
- **`ModoEdicionSolicitud`** (HU-028/#186, ya mergeado, `ModoEdicionSolicitud.cs`): el modo de editar la solicitud (`EnSitio`/`Reemplazo`/`NuevaVersion`) ya se decide por el mismo tipo de condición que `EntregadaAlCliente`. Agregarle "o está finalizada → `NuevaVersion`" es un cambio pequeño y consistente con cómo ya está armado.
- **El asistente (chat):** confirmado, solo tiene 3 tipos de acción propuesta (`clsAccionPropuesta.cs:26-28`): `ajustar_margen`, `re_solicitar_costeo`, `agregar_servicios_conexos`. `generar_pdf` existió en un agente legado y se retiró en HU-013.9 (ya no existe). Hay una etiqueta `confirmar_pdf` en las reglas de respuesta (sugiere la acción en el chat), pero la generación real sigue pasando por el mismo endpoint/servicio con las mismas validaciones de dominio — un candado nuevo en el dominio bloquea esto gratis, sin tocar el agente.
- **Migración:** `EstadoEnvio` (`sin_enviar`/`enviada`/`reenviada`) y `Ganada` ya son independientes de `estado_costeo` — permiten identificar exactamente qué cotizaciones existentes quedarían "atascadas" el día del despliegue: las que están en `autocosteo`/`entregada`, con `estado_envio = sin_enviar` y `Ganada = null` (listas pero sin acción tomada todavía). Las que ya tienen `enviada`/`reenviada`/resultado no se tocan: `EntregadaAlCliente` ya las bloquea para edición pase lo que pase con "finalizada".

## Resumen: 7 issues (4 backend/BD + 3 frontend)

Agrupadas el 2026-10-25 a pedido de Sara (antes eran 11, con nombres provisionales F.1-F.7 y FE-a..e); la HU-036.2 se separó en dos el 2026-10-27 porque quedó muy larga para Duban.

| Issue | Capa | Dueño | Junta lo que antes era (provisional) | Depende de |
|---|---|---|---|---|
| HU-036.1 | Datos | Sara | F.1 | — |
| HU-036.2 | Backend | Duban | F.2 (parte A) | HU-036.1 |
| HU-036.3 | Backend | Duban | F.2 (partes B, C) + F.6 | HU-036.1, HU-036.2 |
| HU-036.4 | Backend | Duban | F.7 + nota de las 72 h | HU-036.1, HU-036.2 |
| HU-036-FE.1 | Frontend | Sara | FE-a | — (se puede hacer ya) |
| HU-036-FE.2 | Frontend | Sara | FE-b y FE-c | HU-036.2, HU-036.3 (parte B) |
| HU-036-FE.3 | Frontend | Sara | FE-d y FE-e | HU-036.3 (parte A), HU-036.4 (parte A) |

## Milestone `HU-036` (4 issues: 1 Datos + 3 Backend)

#### Datos — Sara

### [HU-036.1] [Datos] Columnas de la cotización finalizada — Sara
**Descripción:** `finalizadaAt DATETIMEOFFSET NULL` y `finalizadaPor UNIQUEIDENTIFIER NULL` (FK a `UsuariosWeb`) en `CotizacionesSolicitud`. Columnas aparte de `estadoCosteo` a propósito: el polling deriva `estadoCosteo` de Nexo en cada vuelta y lo pisaría si se mezclara ahí.
**Criterios de aceptación:**
- Script idempotente y no destructivo en `db/scripts/` (siguiente número disponible), probado en Docker antes de la compartida, igual que los anteriores.
- `/health/ready` lo exige una vez el backend lo mapee (mismo patrón que el `10`, el `11` y el `12`).
**Dependencias:** ninguna.

#### Backend — Duban

### [HU-036.2] [Backend] Endpoint para finalizar — Duban
**Descripción:** `POST /web/cotizaciones/{folio}/finalizar` con `If-Match`. Solo el dueño.
**Criterios de aceptación:**
- 409 si `estado_costeo` no es `autocosteo` ni `entregada` (el código lo decide Duban: uno nuevo o reutilizar `ESTADO_NO_PERMITE`; ninguno rompe el contrato de hoy).
- Re-finalizar una ya finalizada: recomiendo 200 sin cambios (idempotente, como re-marcar el mismo resultado).
- 409 `CONFLICTO_CONCURRENCIA` si el `If-Match` no coincide, como el resto de las mutaciones.
- Sin validación extra de "todas las rutas con precio": si la cabecera ya está en `autocosteo`/`entregada`, eso ya lo garantiza `clsResumenEstadoCosteo` (ver hallazgo de rutas rechazadas arriba). **No duplicar esa regla.**
- Exponer `finalizada_at` en `CotizacionResumen`, `CosteoCotizacion` y `GET /web/cotizaciones/{folio}`.
**Dependencias:** HU-036.1.

### [HU-036.3] [Backend] Candados de la cotización finalizada — Duban
**Dependencias:** HU-036.1, HU-036.2.

**Parte A — Candados después de finalizar.** Margen, conexos, conexos de Nexo, re-solicitar, recalcular vehículos y editar la solicitud en sitio se bloquean también cuando `Finalizada`, no solo cuando `EntregadaAlCliente`. Reutilizar `clsReglaPdfCosteo`/`ValidarCambioEtapaFinal` (agregar `|| Finalizada`), sin duplicar el candado en cada servicio.
- Los 6 puntos que hoy llaman `ValidarCambioEtapaFinal` responden `PDF_YA_ENVIADO` con `accion: nueva_version` también cuando la cotización está finalizada pero no entregada.
- `ModoEdicionSolicitud` resuelve `NuevaVersion` si está finalizada, igual que si está entregada (decisión de Sara).

**Parte B — Candados antes de finalizar.** PDF (`POST .../costeo/pdf`), envío de correo, `POST .../resultado` y textos de IA (`clsTextosCosteoService`) exigen `Finalizada = true`. Es validación de dominio, así que el asistente de chat queda bloqueado sin tocarlo.
- Los 4 puntos devuelven el código nuevo (p. ej. `COTIZACION_NO_FINALIZADA`, lo decide Duban) con un mensaje claro ("Finaliza la cotización antes de...").
- Excepción: en `descartada` y `costeo_vencido` se sigue permitiendo marcar perdida por `/seguimiento` (decisión de Sara).

**Documentación.** Anotar la "cotización finalizada" en ADR-015 (§5, candado de edición) o en un ADR nuevo, a criterio de Duban. Va en el mismo PR.

**Cotizaciones existentes (antes HU-F.5, descartada):** no hay backfill. Las que hoy están en `autocosteo`/`entregada` sin enviar ni resultado las finaliza el comercial una por una. Opcional, antes de desplegar, para avisarles cuántas son:
```sql
SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud
WHERE estadoCosteo IN (N'autocosteo', N'entregada') AND estadoEnvio = N'sin_enviar' AND ganada IS NULL;
```
Las que ya están `enviada`/`reenviada`/con resultado no se tocan: ya las protege `EntregadaAlCliente`.

### [HU-036.4] [Backend] Precio congelado frente al polling de Nexo y límite de 72 h — Duban
**Dependencias:** HU-036.1, HU-036.2.

**Parte A — Congelar el precio después de finalizada.** HU-036.3 parte A solo bloquea los cambios que dispara el comercial. Falta el caso del polling: si Nexo trae un costo distinto para una cotización ya finalizada (ej. la Mesa entrega una ruta que estaba en `autocosteo` con otro valor), el precio no se recalcula. Mismo mecanismo que ya existe en ADR-015 §5 para "PDF generado": conservar el precio finalizado, auditar el cambio que trajo Nexo, y advertirlo en `motivoEstadoCosteo`.
- Con la cotización `Finalizada = true`, si el polling trae un costo/margen distinto al ya guardado, el precio persistido NO cambia; se audita el valor que trajo Nexo y `motivoEstadoCosteo` lo advierte (mismo patrón que el precedente de PDF).
- `estado_costeo` sí sigue avanzando con lo que diga Nexo (p. ej. `autocosteo` → `entregada`); solo el precio queda congelado, no el estado.
- `motivoEstadoCosteo` debe dejar claro qué ruta cambió, para que el frontend muestre el aviso (HU-036-FE.3).

**Parte B — Subir el límite de 72 h del costeo vencido (propuesta, no decisión cerrada).** Sara pide revisarlo. No es código, es configuración:
- Hoy `CosteoNexo__EdadMaximaHoras = 72` (rango permitido 1–720), en `DEPLOY.md:104`, `infra/stack.dotnet.dokploy.yml:136` e `infra/stack.dotnet.staging.yml:142`. El valor por defecto está en `clsOpcionesCosteoNexo.cs:31`, y `DEPLOY.md` dice que está "por confirmar con el equipo de Nexo (ADR-015)".
- **Propuesta:** subirlo por encima de lo máximo que se demora la Mesa en la práctica, para que una cotización no quede `costeo_vencido` en Cotifly mientras Nexo todavía la está trabajando. Si la Mesa la entrega después del vencimiento, Cotifly no se entera (deja de consultar), y re-solicitar le duplica el trabajo a la Mesa.
- **Duban define el valor con Nexo.** Cambiarlo no revive las cotizaciones que ya están vencidas.

## Milestone `HU-036-FE` — Frontend, `clientepro-frontend` (3 issues, Sara)

### [HU-036-FE.1] Ver detalle según el estado de costeo (antes FE-a)
**Descripción:** ocultar rutas y acciones en `pendiente_envio` y `pendiente_costeo`; solo "Reintentar envío" en `error_envio`; en `descartada`, solo "Marcar perdida" (por `/seguimiento`) y nada más; en `costeo_vencido`, solo "Marcar perdida" (por `/seguimiento`) y "Volver a pedir el costeo" (re-solicitar, ya permitido en `clsCotizacionSolicitud.cs:1390`) — ni PDF, ni correo, ni ganada, ni textos IA. Decisiones de Sara del 2026-10-25.
**Dependencias:** ninguna del backend nuevo (usa el contrato de hoy).

### [HU-036-FE.2] Finalizar la cotización y bloquear lo que viene después (antes FE-b y FE-c)
**Dependencias:** HU-036.2, HU-036.3 (parte B).

**Parte A — Botón "Finalizar cotización".** Visible solo en `autocosteo`/`entregada`, con una revisión de márgenes por ruta antes de confirmar (mostrar `margen_sugerido` = `SugeridoVigente` del backend) y mostrar el estado finalizada después (`finalizada_at`).

**Parte B — Bloquear PDF, enviar, ganada/perdida y Redactar con IA hasta finalizar.** En "Ver detalle" y en "Mis cotizaciones" (que también tiene Enviar y Ver PDF). Manejar el código nuevo de error con un mensaje consistente en los 4 puntos.
**Nota:** hoy existe un botón "Terminar cotización" que en realidad envía el correo. Se reemplaza: "Finalizar" pasa a ser el candado de márgenes; "Enviar" queda como acción aparte, habilitada después de finalizar.

### [HU-036-FE.3] Después de finalizada: versión nueva y aviso de cambio de costo (antes FE-d y FE-e)
**Dependencias:** HU-036.3 (parte A), HU-036.4 (parte A).

**Parte A — Llevar a versión nueva.** Ocultar cambiar margen y conexos, y manejar el `PDF_YA_ENVIADO` con `accion: nueva_version` que devuelven margen/conexos/re-solicitar cuando la cotización está finalizada (mismo manejo que hoy para "ya entregada"). Editar la solicitud después de finalizada va siempre por versión nueva (decisión de Sara).

**Parte B — Aviso si Nexo cambia el costo.** Cuando la cotización está finalizada y `motivoEstadoCosteo` advierte que Nexo trajo un costo distinto, mostrar un aviso visible en "Ver detalle" (p. ej. "Nexo cambió el costo de la ruta X después de finalizar") con la opción de crear una versión nueva. El precio mostrado sigue siendo el finalizado (decisión de Sara).

## Decisiones de Sara (2026-10-25)

| # | Pregunta | Decisión |
|---|---|---|
| 1 | `descartada`: ¿se quita "marcar perdida" por `/seguimiento`? | **No se quita.** Se deja "marcar perdida"; todo lo demás bloqueado. |
| 2 | `costeo_vencido`: ¿qué acciones quedan? | **Marcar perdida (por `/seguimiento`) + re-solicitar.** Nada más. Además, nota para Duban sobre subir las 72 h (ver sección de backend). |
| 3 | ¿Se puede finalizar con rutas rechazadas? | **Sí**, con algunas rechazadas. Con todas rechazadas no, como ya funciona hoy (la cabecera no llega a `autocosteo`/`entregada`). Sin lógica nueva. |
| 4 | Licitaciones: ¿aplica igual? | **Sí**, igual que spot. |
| 5 | Cotizaciones viejas listas y sin enviar: ¿backfill? | **No.** El comercial las finaliza una por una. Sin backfill (queda solo la consulta informativa, en HU-036.2). |
| 6 | Editar solicitud después de finalizada: ¿siempre `nueva_version`? | **Sí.** |
| 7 | Código de error nuevo o reutilizar `ESTADO_NO_PERMITE` | **Lo decide Duban.** |
| 8 | ¿Aviso al comercial si Nexo cambia el costo después de finalizada? | **Sí.** Quedó en HU-036-FE.3 parte B (depende de HU-036.4 parte A). |

**Las 8 preguntas quedaron cerradas.**

## Historial de las preguntas (texto original)

1. **`descartada`/`costeo_vencido`: ¿se quita "marcar perdida" por `/seguimiento`?** Hoy es la única acción que existe en esos estados (HU-013.17, "perdida solo en Cotifly", cliente desistió antes del costeo). **Mi recomendación:** no quitarla — es la única salida real que tiene el comercial para cerrar una cotización que nunca llegó a costearse; todo lo demás (PDF, correo, ganada, textos IA) sigue bloqueado como pide Sara. Necesita tu confirmación porque contradice literalmente "descartada: ninguna acción" tal como lo escribiste.
3. **Rutas rechazadas: ¿se puede finalizar?** **Con evidencia de código, mi recomendación es sí, sin regla nueva que construir:** una cotización con algunas rutas rechazadas ya puede estar en `autocosteo`/`entregada` hoy (las rechazadas se ignoran en el resumen); si **todas** quedan rechazadas, la cabecera ya cae sola en `pendiente_costeo` (salvo que Nexo diga entregada/descartada), que ya no se puede finalizar por la regla 1 de Sara. No hace falta lógica nueva, solo confirmar que te parece bien dejarlo así.
4. **Licitaciones: ¿aplica igual?** Estructuralmente sí (misma entidad, mismo `estado_costeo`, licitación es solo un `TipoSolicitud`). Falta tu confirmación de negocio, no hay nada técnico que lo impida.
5. **Cotizaciones existentes ya enviadas o con resultado: ¿se migran como finalizadas?** Las que ya tienen `estado_envio = enviada/reenviada` o `Ganada != null` **no necesitan tocarse** — ya están protegidas por `EntregadaAlCliente` sin importar si tienen `finalizadaAt`. El caso real a decidir es el de HU-F.5: las que están `autocosteo`/`entregada` pero **sin** enviar ni resultado — ¿se les pone `finalizadaAt` automático (quedan "ya revisadas") o se las deja sin finalizar (el comercial tiene que finalizarlas una por una antes de poder actuar)? Sin suficiente información para recomendar una sola opción — depende de cuántas sean y de si eso es aceptable para el negocio.
6. **Editar solicitud (HU-028/#186) después de finalizada: ¿siempre `nueva_version`?** Mi recomendación, con alta confianza por cómo ya está armado `ModoEdicionSolicitud`: sí, tratar "finalizada" igual que "entregada" para ese modo (ver HU-036.3). Pido tu confirmación de todas formas porque es una decisión de producto, no solo técnica.
7. **Código de error:** ¿`COTIZACION_NO_FINALIZADA` nuevo, o reutilizar `ESTADO_NO_PERMITE` (ya existe, genérico)? Sin opinión fuerte; nuevo es más claro para el frontend, reutilizar es menos código. A decidir con Duban.
8. **Si Nexo cambia el costo después de finalizada (HU-F.7), ¿el comercial debe ver un aviso para decidir si crea una versión nueva?** El backend ya lo deja advertido en `motivoEstadoCosteo`; la pregunta es si el frontend lo muestra de forma visible (ej. un banner "Nexo cambió el costo de la ruta X después de finalizar — ¿crear versión nueva?"). Si la respuesta es sí, sería un **FE-e** nuevo en `clientepro-frontend`, que depende de HU-F.7. Mi recomendación: sí, porque sin el aviso el comercial no tiene cómo enterarse de que su precio quedó desalineado con el costo real.

---

# Parte 2 — HU-037 / HU-037-FE — Rol Director

## Qué es

Un rol nuevo, **Director**: hace lo mismo que el Gerente Comercial, pero ve **todas las regionales**, con los mismos filtros que ya tiene el tablero (regional, comercial, año/mes) más un selector de regional.

**Decisión de diseño (Sara, 2026-10-26): jerarquía, no multi-rol.** El usuario sigue teniendo un solo rol en el JWT (`rol`), y `"director"` se agrega a las políticas donde ya entra `"gerente_comercial"`, igual que hoy `gerente_comercial` entra en la política `Comercial`. El multi-rol real (`user.roles` como arreglo, varios claims `rol`) queda como deuda técnica: cambia el contrato y cada chequeo de rol del código, y no hay un caso que lo pida.

## Lo que hay hoy en el código (verificado contra `origin/QA`)

- Roles internos: `RolInterno { Comercial, GerenteComercial }` (`Cotifly.Dominio/Identidad/RolInterno.cs`). El código del rol en el JWT sale de `clsCodigoEnum` (agregar `Director` da `"director"` sin tocar nada más).
- Políticas: `clsPoliticas.Comercial` acepta `comercial` y `gerente_comercial`; `clsPoliticas.GerenteComercial` solo `gerente_comercial` (`Cotifly.Api/Identidad/clsConfiguracionAutenticacion.cs:15-22, 76-78`).
- Rol externo → interno: `clsVerificadorIdentidad.ResolverRol` (`Cotifly.Aplicacion/Identidad/Services/clsVerificadorIdentidad.cs:162-180`). Si el usuario tiene varios roles externos que mapean, hoy gana `GerenteComercial`.
- El mapeo `idRolExterno` vive en `dbo_cotifly.Roles`. **Verificado en la compartida el 2026-10-27: hoy sigue el mapeo viejo, `comercial = 34` y `gerente_comercial = 31`** — HU-025.1 (cambio a `dbSolucionesTI_Prueba` y comercial 34 → 32, #190) sigue sin aplicarse. La tabla tiene un CHECK `ck_roles_codigo` que solo admite `comercial` y `gerente_comercial` (`01_crear_esquema.sql:123`).
- **Decisión de Sara (2026-10-27) sobre a cuál rol de ClientePro mapea cada rol interno:** ClientePro tiene 4 roles ya creados (`ClienteProRegional`, `ClienteProGerencial`, `ClienteProComercial`, `ClienteProAdmin`). Se reasigna el significado:
  - `comercial` → `ClienteProComercial`.
  - `gerente_comercial` → **`ClienteProRegional`** (antes iba a `ClienteProGerencial`).
  - `director` (nuevo) → **`ClienteProGerencial`** (reutiliza el id que antes tenía gerente_comercial; **no hace falta pedirle un rol nuevo a ClientePro**).
  - Sin riesgo de reasignar usuarios reales por accidente: **estamos en fase de prueba, nadie real tiene esos roles todavía** (confirmado por Sara). Si esto se mueve a producción más adelante, ahí sí hay que coordinar una ventana para mover a quien tenga `ClienteProGerencial` hoy hacia `ClienteProRegional` antes de aplicar el remapeo, para que nadie se vuelva Director sin querer.
  - **Complicación encontrada (2026-10-27): la base de prueba y la de producción tienen IDROL distintos, y la de prueba no tiene `ClienteProRegional` todavía.** Sara pasó las dos pantallas:

| Rol de ClientePro | `dbSolucionesTI_Prueba` (prueba) | Producción |
|---|---|---|
| `ClienteProAdmin` | 25 | 24 |
| `ClienteProComercial` | 32 | 34 |
| `ClienteProGerencial` | 31 | 31 |
| `ClienteProRegional` | **no existe** | 29 |

  - **Mapeo final, producción:** `comercial` = 34, `gerente_comercial` = 29, `director` = 31.
  - **Mapeo provisional, prueba** (mientras no exista `ClienteProRegional` ahí, decisión de Sara): `comercial` = 32, `gerente_comercial` = **25 (`ClienteProAdmin`, de prestado)**, `director` = 31.
  - El `idRolExterno` va en un **script de operación** (`db/scripts/operacion/`), no en uno de esquema de `db/scripts/`, porque el valor cambia por ambiente — mismo patrón que el script de comercial 34→32.
- "Solo mi regional": `IClsRegionalUsuario` (`ResolverAsync`, `ListarMiembrosAsync`, `EsMiembroDeMiRegionalAsync`) y sus 4 consumidores:
  - `clsTableroMetricasService.ObtenerRegionalAsync` (`GET /web/metricas/regional`).
  - `clsCotizacionesRegionalService.ListarAsync` (`GET /web/cotizaciones/regional`).
  - `clsDetalleCotizacionService` (`GET /web/cotizaciones/{folio}`, ver una ajena de la regional).
  - `clsDescargaPdfService` (`GET /pdfs/{archivo}`, descargar un PDF ajeno de la regional).
- No existe un endpoint que liste las regionales.
- Ningún ADR ni doc menciona "Director": es 100 % nuevo. El ADR dueño del modelo regional es el ADR-016.

## Requisito previo (no es issue)

~~Pedir el IDROL de Director~~ **RESUELTO (2026-10-27):** no hace falta rol nuevo, reutiliza `ClienteProGerencial`. Los números de los 4 roles de ClientePro, en prueba y en producción, ya están arriba.

## Resumen: 5 issues

La HU-037.2 se separó en dos el 2026-10-27 porque quedó muy larga para Duban.

| Issue | Milestone | Capa | Dueño | Depende de |
|---|---|---|---|---|
| HU-037.1 | `HU-037` | Datos | Sara | — |
| HU-037.2 | `HU-037` | Backend | Duban | HU-037.1 |
| HU-037.3 | `HU-037` | Backend | Duban | HU-037.1, HU-037.2 |
| HU-037.4 | `HU-037` | Datos | Sara | HU-037.1, HU-025.1 — **recordatorio para el despliegue a producción** |
| HU-037-FE.1 | `HU-037-FE` | Frontend | Sara | HU-037.2, HU-037.3 |

## Milestone `HU-037` (4 issues: 2 Datos + 2 Backend)

#### Datos — Sara

### [HU-037.1] [Datos] Rol Director en la base — Sara
**Dependencias:** ninguna.

**Parte A — Esquema (script normal en `db/scripts/`, siguiente número disponible, igual para todos los ambientes).**
- Reemplaza el CHECK `ck_roles_codigo` (hoy `codigo IN ('comercial', 'gerente_comercial')`, `01_crear_esquema.sql:123`) por uno que también admita `'director'`.
- Agrega la fila `director` en `dbo_cotifly.Roles`, activa, con `idRolExterno = NULL` (la tabla ya lo permite nullable). El valor real lo pone la parte B, porque cambia por ambiente.
- Probado en Docker (idempotente, en una transacción, mismo patrón que el `10`) antes de la compartida.
- El `01_crear_esquema.sql` también admite `director` en el CHECK (o queda anotado para HU-024.2, la consolidación del `01`).

**Parte B — Mapeo de prueba (script de operación, `db/scripts/operacion/`, solo para el ambiente de prueba/compartida).**
- `UPDATE` del `idRolExterno` de `gerente_comercial`: de 31 a **25** (`ClienteProAdmin`, de prestado, provisional).
- `UPDATE` del `idRolExterno` de `director`: a **31** (`ClienteProGerencial`).
- El orden importa: mover primero a `gerente_comercial` fuera del 31, y recién después ponerle el 31 a `director` (si `idRolExterno` tiene una restricción de unicidad, hacerlo al revés falla).
- Fase de prueba, sin usuarios reales en esos roles todavía: no hace falta ventana de coordinación. Confirmar antes si algún comercial de prueba quedó con el rol `ClienteProAdmin` por otro motivo — a esa persona también la dejaría entrar como gerente mientras dure el préstamo.

#### Backend — Duban

### [HU-037.2] [Backend] Rol Director: permisos — Duban
**Dependencias:** HU-037.1.
- `Director` en `RolInterno`.
- `ResolverRol`: precedencia Director > GerenteComercial > Comercial (auditada como la regla de hoy).
- `"director"` en las políticas `Comercial` y `GerenteComercial` (o una política `Director` nueva, a criterio de Duban). Director usa todo lo que usa el gerente: tablero, listado regional, admin de líneas de negocio y márgenes, y la consola de comercial.
- Cada servicio que hoy revalida `usuario.Rol == RolInterno.GerenteComercial` (defensa en profundidad) acepta también `Director`, incluido `clsLineasNegocioAdminService`.

**Pruebas:** usuario con roles de comercial y director entra como director; Director entra a la consola de comercial y al admin de márgenes igual que el gerente.

### [HU-037.3] [Backend] Rol Director: ver todas las regionales — Duban
**Dependencias:** HU-037.1, HU-037.2.

**Parte A — Alcance de todas las regionales.**
- Centralizar en `IClsRegionalUsuario` (p. ej. un método que, para Director, devuelva todos los usuarios o los de la regional elegida), en vez de repetir el `if` en los 4 consumidores.
- Tablero, listado regional, detalle de una cotización ajena y descarga de PDF ajeno: para Director, sin restricción de regional.
- El gerente no cambia: sigue viendo solo la suya y sigue fallando cerrado (`REGIONAL_NO_DISPONIBLE`) si no se conoce su regional.

**Parte B — Filtros.**
- Parámetro `regional` opcional en `GET /web/metricas/regional` y `GET /web/cotizaciones/regional`. Sin él, Director ve todas; con él, solo esa. Para el gerente se ignora o responde 400/403 si pide otra regional que no es la suya (a decidir con Duban; recomiendo 403 para que no parezca que funciona).
- El filtro por comercial (`usuario_id`) y año/mes se reutilizan. Para Director, el comercial puede ser de cualquier regional.
- Endpoint nuevo para listar las regionales (para el selector), p. ej. `GET /web/regionales`, solo para Director (y gerente si se quiere). La fuente es la misma de la regional efectiva (`DimEjecutivo` / `UsuariosWeb.regional`).

**Documentación.**
- ADR-016: el rol Director y su alcance.
- `backend-dotnet/specs/openapi.yaml` y `backend-dotnet/docs/COTIFLY-CONTRATO.md`: rol `director`, parámetro `regional`, endpoint de regionales.

**Pruebas:** Director ve todas las regionales y puede filtrar por una; gerente sigue viendo solo la suya y no puede pedir otra; detalle y PDF ajenos de cualquier regional para Director.

#### Datos — Sara

### [HU-037.4] [Datos] ⚠ NO OLVIDAR: pasar los roles a los valores reales de producción — Sara
**Nota de capa:** es un script de operación (`db/scripts/operacion/`), no de esquema — pero sigue siendo trabajo de Datos, no de Backend.
**Por qué existe esta HU:** el mapeo de HU-037.1 parte B es **provisional**, solo para el ambiente de prueba (`ClienteProRegional` no existe ahí). Si esto se despliega a producción sin este paso, `gerente_comercial` y `director` quedan mal mapeados (apuntando a roles de prueba que no existen o significan otra cosa en producción).

**Descripción:** cuando haya fecha de despliegue a producción, correr un script de operación (`db/scripts/operacion/`) que deje el `idRolExterno` de los 3 roles con los valores **reales de producción** (ver tabla arriba, confirmada por Sara el 2026-10-27):
- `comercial` → 34 (`ClienteProComercial`)
- `gerente_comercial` → 29 (`ClienteProRegional`)
- `director` → 31 (`ClienteProGerencial`)

**Criterios de aceptación:**
- Confirmar antes de correrlo que estos 3 números siguen siendo los mismos en la base de identidad real de producción (pudieron cambiar entre el 2026-10-27 y la fecha del despliegue).
- Antes de aplicarlo, coordinar con quien administra ClientePro: a quién hoy tiene `ClienteProGerencial` en producción, moverlo a `ClienteProRegional` si debe seguir siendo gerente regional (y no Director) — este es el paso que en fase de prueba no hacía falta (ver HU-037.1 parte B), pero en producción si hay usuarios reales, sí.
- Verificar después del cambio: un comercial real entra como comercial, un gerente real entra como gerente de su regional (no como Director), y a quien se le haya asignado Director entra viendo todas las regionales.
**Dependencias:** HU-037.1, HU-037.2, HU-037.3, HU-025.1 (el valor real de `comercial` en producción es el mismo 34 de siempre, pero conviene revisarlo en la misma ventana).

## Milestone `HU-037-FE` — Frontend

### [HU-037-FE.1] Tablero y accesos del Director — Sara
**Dependencias:** HU-037.2, HU-037.3.
- Reconocer el rol `"director"` (mismo patrón que `gerente_comercial` en el mapeo de roles del front).
- Darle los mismos accesos que al gerente: tablero de HU-010, "Todas las cotizaciones", admin de márgenes y la consola de comercial.
- Selector de **regional** nuevo en el tablero y en "Todas las cotizaciones" (opción "Todas" + la lista del endpoint de regionales). El selector de comercial depende de la regional elegida (o muestra todos con "Todas").
- El gerente no ve el selector de regional (la suya es implícita).

## Preguntas abiertas (Director)

1. ~~IDROL de Director en las bases de identidad~~ **RESUELTO (2026-10-27):** no es un rol nuevo, reutiliza `ClienteProGerencial`. Números confirmados para prueba y producción (ver tabla arriba).
2. **¿El gerente puede pedir otra regional por el parámetro nuevo?** Recomendación: no, 403.
3. **¿El endpoint de regionales también lo ve el gerente?** No lo necesita para su tablero; a decidir.
4. **¿Director también cotiza (consola de comercial)?** Por la jerarquía de hoy, sí (igual que el gerente). Confirmar que es lo que se quiere.
5. **¿Quién confirma, antes de HU-037.4, que los IDROL de producción no cambiaron?** El dato de hoy puede quedar viejo si pasa tiempo hasta el despliegue real.

---

# Parte 3 — HU-038-FE — Separar el dashboard por módulos (solo frontend)

**Qué pide Sara (2026-10-27):** el dashboard que ya existe (HU-010) agrupe las métricas por módulo/categoría en vez de mostrarlas todas juntas. Es solo de presentación en `clientepro-frontend`: el backend no cambia, ya expone todos estos campos hoy en `GET /web/metricas/comercial` y `GET /web/metricas/regional` (`clsIndicadoresMetricas`, `backend-dotnet/src/Cotifly.Dominio/Metricas/clsIndicadoresMetricas.cs`).

## Propuesta de agrupación (a validar con Sara/diseño, no es la única forma de dividirlo)

| Módulo | Campos |
|---|---|
| **Volumen y pipeline** | `cantidad`, `versiones`, `ganadas`, `perdidas`, `sin_resultado`, `pendientes_por_cotizar`, `cotizaciones_usd` |
| **Conversión** | `conversion`, `conversion_sobre_cerradas`, `deadline_vencidas` |
| **Rentabilidad y margen** | `margen_promedio`, `margen_ponderado`, `desviacion_promedio_margen`, `desviacion_promedio_margen_abs`, `ajustes_margen` |
| **Dinero** | `costeo_total`, `costeo_ganadas`, `valor_cotizado_cop`, `valor_ganado_cop`, `ganancias` |
| **Operación** | `tiempo_promedio_cotizacion_horas` |

Lo que ya viene como su propia sección en la respuesta (no son parte de `Indicadores`, se quedan igual, no hay que reagruparlas): `serie_mensual` (gráfico), `por_linea_negocio` (apertura), y en el tablero regional además `comerciales` (tabla), `concentracion_ciudades` y `lineas_negocio_activas`.

## [HU-038-FE.1] Agrupar las métricas del dashboard por módulo — Sara
**Capa:** Frontend (`clientepro-frontend`), sin cambios de backend.
**Descripción:** reorganizar visualmente el tablero de métricas (comercial y regional/Director) en secciones o tarjetas por módulo, en vez de una sola lista plana. Misma información que hoy, solo agrupada.
**Criterios de aceptación:**
- Las métricas de `Indicadores` quedan agrupadas por categoría (ver propuesta arriba, a confirmar el detalle visual con Sara).
- `serie_mensual`, `por_linea_negocio`, y en el regional `comerciales`/`concentracion_ciudades`/`lineas_negocio_activas` siguen donde están hoy.
- Sin cambios en los campos que pide o recibe del backend: es solo reordenar/agrupar en la pantalla.
- Aplica tanto al tablero de Comercial como al regional (Gerente/Director), con las mismas categorías en los dos.
**Dependencias:** ninguna (usa el contrato de hoy).
