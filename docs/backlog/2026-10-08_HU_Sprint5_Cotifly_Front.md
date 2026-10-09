# Sprint 5 — Cotifly frontend: conexión con el cubicador

**Fecha:** 2026-10-08. **Repo de los issues:** `clientepro-frontend`.
**Qué es este documento:** las issues **nuevas** de frontend para conectar Cotifly con el cubicador, según su contrato real (hoja de ruta del cubicador del 2026-10-08, sección 2). Son 3.
**Lo que ya existe y no se modifica:** las issues de la reestructuración del frontend, **HU-021-FE.1 a FE.11** (`docs/backlog/2026-10-08_HU_Correccion_BD_Frontend_Cotifly.md`), ya están creadas. Estas issues nuevas se construyen encima de ellas; donde algo de aquí cambia lo que dice una de ellas, se indica en la sección "Relación con las issues que ya existen".
**Documentos hermanos (backend, repo `Cotifly`):** conexión con el cubicador en `docs/backlog/2026-10-08_HU_Sprint5_Cotifly_Backend.md` (HU-023); peticiones al cubicador en `docs/conexion_nexo/2026-10-08_PETICIONES_AL_CUBICADOR.md`.

---

## HU-023-FE — Conexión con el cubicador (frontend)

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E2 — Migración de Frontend a ClientePro · **Talla:** L · **Roles:** Frontend (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `clientepro-frontend`
> **Fecha que no se mueve:** **HU-023-FE.1** sale el **viernes 16 de octubre de 2026**, junto con la transición del backend (HU-023.2). Ese día el cubicador retira la cubicación que hoy usa Cotifly por medio de NEXO.

### Historia
**Como** comercial, **quiero** escoger la mercancía de una lista y ver qué camiones recomienda el cubicador para cada ruta, con qué tan lleno va cada uno, **para** no digitar el vehículo y entender por qué una ruta se va a la Mesa de Costeo cuando el cubicador no puede escoger.

### Contexto técnico

- Hoy `calcularCubicaje()` (`cotizador.component.ts:472`) llama a `POST /web/cubicacion`, que va a la cubicación de NEXO y espera "un vehículo o N iguales" (`sugerenciaFlota.cantidadVehiculos`). El viernes 16 esa cubicación se retira y la respuesta cambia a una **lista de vehículos que puede ser mixta**, cada uno con su % de volumen y su % de peso.
- La mercancía se identifica por su **código** (`codigoMercancia`, por ejemplo `BEBIDAS`), no por un id. HU-021-FE.3 hablaba de `idTipoMercancia`: queda reemplazado por el código.
- El cubicador devuelve resultados que **no son error**: "sin selección automática" (con motivo) y "condicionada". Se muestran como información.
- El nombre del vehículo **no se guarda** en la base: se saca del catálogo `GET /web/tipos-vehiculo` por su código.

**Reglas de la conexión:**
- El navegador **solo** llama al backend de Cotifly (`/web/...`). Nunca al cubicador ni a NEXO, y la llave del cubicador no está en el frontend.
- Los catálogos (mercancías, carrocerías, vehículos) se leen del backend; el frontend no los edita.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: El día del corte
Dado que el cubicador retiró la cubicación de NEXO
Cuando el comercial usa "Calcular cubicaje"
Entonces ve la recomendación nueva y no un "sin vehículo sugerible" falso

Escenario: Flota mixta
Dado una ruta para la que el cubicador recomienda un Sencillo C2 y una Minimula 2S2
Cuando el comercial ve el resultado
Entonces ve los dos camiones por separado, cada uno con su nombre, su % de volumen y su % de peso

Escenario: Mercancía del catálogo
Dado el desplegable de mercancías
Cuando el comercial escoge una mercancía
Entonces se guarda su código y se muestra su nombre
Y las mercancías no cubicables aparecen deshabilitadas, con una explicación

Escenario: El cubicador no puede escoger
Dado una ruta con una mercancía extradimensionada
Cuando el cubicador responde "sin selección automática"
Entonces la ruta muestra el camión "sin vehículo asignado", el motivo y qué pieza excede qué medida
Y se explica que lo resuelve la Mesa de Costeo, sin mostrarlo como error

Escenario: El cubicador no responde
Dado que el cubicador no está disponible
Cuando el comercial guarda la solicitud
Entonces ve un mensaje claro de que no se pudieron calcular los vehículos
Y no pierde lo que digitó

Escenario: Licitación con mercancía sugerida
Dado un Excel de licitación con la mercancía escrita como texto
Cuando el comercial lo sube
Entonces cada fila queda con la mercancía más parecida del catálogo, marcada como sugerida
Y el comercial la confirma o la cambia antes de enviar
```

### Endpoints del backend que consume

| Endpoint | Para qué | Lo entrega (backend) |
|---|---|---|
| `POST /web/cubicacion` (respuesta nueva) | "Calcular cubicaje" durante la transición | HU-023.2 (viernes 16) |
| `GET /web/tipos-mercancia` | Desplegable de mercancías | HU-023.1 |
| `GET /web/tipos-carroceria`, `GET /web/tipos-vehiculo` | Nombres de vehículo y carrocería | HU-023.1 (`tipos-vehiculo` depende de una petición al cubicador) |
| `POST /web/solicitudes-costeo` | Guardar la solicitud; el backend pide los camiones | HU-023.3 |
| `POST /web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos` | Volver a pedir los camiones de una ruta | HU-023.3 |
| `GET /web/cotizaciones/{folio}/costeo` | Rutas con sus camiones y el resultado del cubicador | HU-023.3 |
| `POST /web/licitaciones/analizar-excel` | Filas del Excel con la mercancía sugerida | HU-023.5 |

### Trazabilidad (event storming)
Fuentes: la hoja de ruta del cubicador del 2026-10-08 (contrato, sección 2; verificación antes del corte, sección 5), `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md` y la revisión del código de `clientepro-frontend` del 2026-10-08. Es el lado de frontend de HU-023 (backend). Se construye sobre HU-021-FE (reestructuración, ya creada). Depende del cubicador (equipo de NEXO) para los catálogos, la URL de QA y `GET /tipos-vehiculo`.

### Alcance en este repo (`clientepro-frontend`)
1. Transición del viernes 16: "Calcular cubicaje" con la respuesta nueva (HU-023-FE.1).
2. Mercancías del catálogo por código y camiones recomendados por ruta, en la cotización y en el detalle (HU-023-FE.2).
3. Licitación con mercancía sugerida y progreso de cubicación (HU-023-FE.3).

**HU también en:** `Cotifly` (backend), HU-023.

#### Issues en `clientepro-frontend`

### [HU-023-FE.1] [Cubicador · Frontend] Transición: "Calcular cubicaje" con la respuesta nueva
**Labels:** `frontend`, `integración`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-023-FE (milestone). Depende de HU-023.2 (backend). Fecha: viernes 16 de octubre. Va primero.** No espera al resto: funciona sobre el formulario de hoy.

- [ ] Mapear la respuesta nueva en `cotifly.repositorio-http.ts` y `cotifly.model.ts`: una **lista de vehículos**, cada uno con código, nombre, carrocería, peso, volumen, **% de volumen** y **% de peso**; más el resultado, el motivo de "sin selección", la marca de condicionada, las advertencias y la extradimensión. Quitar `SugerenciaVehiculo` y `SugerenciaFlota`.
- [ ] Pintar la flota como **lista real**, que puede ser mixta (por ejemplo un Sencillo C2 y una Minimula 2S2). Hoy se pinta "N × Nombre" y se asume que todos son iguales.
- [ ] **Dejar de rellenar** la clase de viaje y la clase de contenedor con la sugerencia: el cubicador ya no las sugiere.
- [ ] Mostrar "sin selección automática" con su motivo y "condicionada" como **información**, no como error.
- [ ] Ajustar el texto que se arma para el chat (`componerMensajeLinea`, hoy "se necesitan N vehículos") a la lista nueva.
- [ ] Probar contra el backend con un cubicador falso y, en cuanto exista, contra el cubicador de QA. **Prueba antes del corte** (la ejecuta Kevin): "Calcular cubicaje" con flota mixta.
- [ ] Cuando HU-023-FE.2 esté publicada (los camiones se piden al guardar), **retirar "Calcular cubicaje"** y su pre-llenado de vehículo y carrocería.

### [HU-023-FE.2] [Cubicador · Frontend] Mercancías del catálogo y camiones recomendados por ruta
**Labels:** `frontend`, `integración`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-023-FE (milestone). Depende del backend (HU-023.1 catálogos y HU-023.3 solicitud con camiones) y de las issues ya creadas HU-021-FE.1, FE.2 y FE.5.** Hace con el contrato real lo que HU-021-FE.3 planteaba (ver "Relación con las issues que ya existen").

**Parte A — Modelos y contrato del cubicador.**
- [ ] En `cotifly.model.ts`: el `Camion` trae código de vehículo y carrocería, peso, volumen, **% de volumen** y **% de peso**; la `Ruta`, el resultado del cubicador (asignado o sin selección), el motivo y la marca de condicionada; la `Mercancia`, `codigoMercancia` (en vez de `idTipoMercancia`).
- [ ] Mensajes de error nuevos en `cotifly-mensajes.ts`: cubicador no disponible, respuesta incompleta del cubicador, mercancía desconocida, mercancía no cubicable.
- [ ] Ampliar el servidor falso del repositorio con los casos del cubicador: flota mixta, cada motivo de "sin selección", condicionada y extradimensión.

**Parte B — Mercancías del catálogo.**
- [ ] Por ruta, lista de **mercancías**: tipo escogido de un **desplegable con el catálogo del cubicador** (`GET /web/tipos-mercancia`) que guarda el **código** (`codigoMercancia`) y muestra el nombre; cantidad, largo, ancho y alto en cm, y peso en kg (por unidad). Reemplaza las "posiciones" con la tipología fija de `tipologia-carga.catalogo.ts`.
- [ ] Mostrar solo las mercancías **activas**. Las que tienen `cubicable = false` (graneles, combustibles…) aparecen **deshabilitadas** con el texto "no se cubica" (y el `comentarioCondicion` del catálogo, si lo trae), en vez de dejar que el backend responda con error.
- [ ] Indicar las mercancías que pueden quedar condicionadas (`condicionadaCubicacion`).
- [ ] Guardar el catálogo en caché durante la visita: hoy los catálogos se vuelven a pedir cada vez que se abre el cotizador.
- [ ] Mientras dure la transición, el desplegable también alimenta "Calcular cubicaje" (HU-023-FE.1).
- [ ] Si una cotización vieja tiene mercancías **sin código** (o con un código que ya no está en el catálogo) y hay que recalcular, el comercial **la vuelve a escoger** del desplegable.

**Parte C — Camiones recomendados y resultado del cubicador.**
- [ ] Por ruta, la **lista de camiones** (solo lectura): nombre del vehículo (sacado de `GET /web/tipos-vehiculo` por su código), carrocería, peso, volumen, **% de volumen** y **% de peso**. Puede ser una flota mixta. El comercial no los edita.
- [ ] El **resultado** de la ruta: asignado, o "sin selección automática" con su motivo (extradimensionado, sin candidatos, demasiados vehículos, sin carrocería compatible). En "sin selección", el camión aparece "sin vehículo asignado" y se explica que lo resuelve la Mesa de Costeo de NEXO. Se muestra como **información, no como falla**.
- [ ] **Condicionada:** marcar la ruta y explicar que la solicitud va completa a la Mesa de Costeo. Cuáles mercancías la causan se muestra con el campo `condicionadaCubicacion` del catálogo (la base solo guarda la marca de la ruta).
- [ ] Mostrar las **advertencias** del cubicador (por ejemplo, la clase de viaje que escogió el comercial no coincide con la del camión: la clase de viaje es una preferencia, no un filtro) y la **extradimensión** (qué pieza excede qué medida).
- [ ] Botón **"Recalcular vehículos"** en una ruta, y aviso automático cuando el comercial cambie sus mercancías o su peso: se vuelven a pedir los camiones y se pierde el costeo de esa ruta.
- [ ] **Errores:** si el cubicador no responde, un mensaje claro ("no se pudieron calcular los vehículos"), sin enviar nada a NEXO y sin perder el formulario. Un código de mercancía desconocido se explica, no se muestra como falla genérica.
- [ ] **Ver detalle:** los camiones de cada ruta se muestran igual que aquí (nombre, carrocería, peso y porcentajes).
- [ ] **Respaldo:** mientras el cubicador no esté conectado al flujo final, construir y probar contra el servidor falso; la bandera de HU-021-FE.2 mantiene el vehículo digitado como hoy, y se apaga al publicar esta issue.

**Parte D — Pruebas.**
- [ ] Pruebas del mapeo de la respuesta del cubicador (`cotifly.repositorio-http.spec.ts`): flota mixta, "sin selección" con cada motivo, condicionada y extradimensión.
- [ ] Prueba de punta a punta con el cubicador de QA del flujo final: guardar una solicitud con flota mixta y verla en el detalle (Kevin).

### [HU-023-FE.3] [Cubicador · Frontend] Licitación: mercancía sugerida por fila y progreso de cubicación
**Labels:** `frontend`, `integración`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-023-FE (milestone). Depende de HU-023.5 (backend), de HU-023-FE.2 y de la issue ya creada HU-021-FE.6.**

**Parte A — Subir el Excel al backend.**
- [ ] Subir el Excel a `POST /web/licitaciones/analizar-excel` (ya existe en el backend) en vez de parsearlo en el navegador: es el backend el que sugiere la mercancía de cada fila. Con esto queda resuelta la decisión "parsear en el navegador o subir al backend" de HU-021-FE.6.
- [ ] Si el Excel trae una columna de código de mercancía, aceptarla en el mapeo de columnas (`importar-excel-lineas`).

**Parte B — Mercancía sugerida por fila.**
- [ ] Al importar el Excel, cada línea queda **prellenada** con la mercancía que el backend sugiere por parecido de nombre, **marcada como "sugerida"** para que el comercial la revise.
- [ ] El comercial **confirma o cambia** la mercancía con el mismo desplegable de HU-023-FE.2. Si no hubo sugerencia, la línea queda vacía y la escoge.
- [ ] No dejar enviar la licitación mientras haya mercancías sin escoger.

**Parte C — Progreso de cubicación.**
- [ ] Mostrar el **progreso de la cubicación** de cada ruta (el backend las cubica en segundo plano), antes del progreso de costeo que ya pide HU-021-FE.6. Al terminar, sus camiones como en HU-023-FE.2 (parte C).

---

## Relación con las issues que ya existen (no se modifican)

Las issues HU-021-FE ya están creadas. Al trabajarlas, tener en cuenta lo que cambia con el contrato real del cubicador:

| Issue ya creada | Qué dice | Qué vale ahora |
|---|---|---|
| HU-021-FE.1 | Modelos de solicitud, ruta, camión y mercancía | Se complementa con HU-023-FE.2, parte A (porcentajes, resultado del cubicador, código de mercancía) |
| HU-021-FE.2 | Bandera para el vehículo digitado; "decidir si `POST /web/cubicacion` se mantiene como vista previa" | La bandera se apaga con HU-023-FE.2. **Decidido:** "Calcular cubicaje" solo vive durante la transición (HU-023-FE.1) y luego se retira |
| HU-021-FE.3 | Mercancías por `idTipoMercancia` y camiones recomendados | **Se hace en HU-023-FE.2**, con `codigoMercancia` en vez de `idTipoMercancia`. FE.3 se cierra cuando HU-023-FE.2 esté hecha |
| HU-021-FE.6 | "Decidir si el Excel se parsea en el navegador o se sube al backend" | **Decidido:** se sube al backend (HU-023-FE.3, parte A). La mercancía por fila y el progreso de cubicación están en HU-023-FE.3 |
| HU-021-FE.7 | Detalle con rutas y camiones | Los camiones se muestran con nombre y porcentajes, como en HU-023-FE.2 |
| HU-021-FE.10 | Pruebas y retiro de lo que quedó sin uso | Suma retirar "Calcular cubicaje" (HU-023-FE.1, última casilla). Las pruebas del cubicador están en HU-023-FE.1 y FE.2 |

## Orden sugerido

1. **HU-023-FE.1** (transición): para el viernes 16.
2. **HU-023-FE.2** cuando el backend tenga los catálogos y la solicitud con camiones (HU-023.1 y 023.3). Antes, se puede avanzar la parte A con el servidor falso.
3. **HU-023-FE.3** cuando el backend de licitación con cubicador esté (HU-023.5).

## De dónde viene cada cosa

| Origen | Dónde quedó |
|---|---|
| Primera versión de este documento: HU-023-FE.1 (transición, 6 casillas) | HU-023-FE.1 (mismas 6, más la de retirar "Calcular cubicaje" al final) |
| Primera versión: HU-023-FE.2 (mercancías, 5) | HU-023-FE.2, parte B (más la de "volver a escoger" para mercancías viejas) |
| Primera versión: HU-023-FE.3 (camiones, 7) | HU-023-FE.2, parte C (más la del detalle) |
| Primera versión: HU-023-FE.4 (licitación, 5) | HU-023-FE.3, partes A, B y C |
| Primera versión: pruebas (2) | HU-023-FE.1 (prueba antes del corte) y HU-023-FE.2, parte D |
| Ajustes que el documento unido le hacía a HU-021-FE.1, FE.2, FE.6, FE.7 y FE.10 | Como esas issues no se modifican, pasaron a HU-023-FE.2 (parte A y detalle), HU-023-FE.1 (retiro de "Calcular cubicaje") y HU-023-FE.3 (subir el Excel), y se anotan en la tabla de arriba |

---

## Estado en GitHub (2026-10-09)

Creadas en `InnovacionIA-proyectos/clientepro-frontend`, proyecto "Seguimiento Cotifly":

| Issue | # | Milestone |
|---|---|---|
| HU-021-FE.1 a FE.11 | #69 a #79 | HU-021-FE |
| **HU-021-FE.12** Ganada/perdida: usar `/resultado` en vez de `/seguimiento` (bug encontrado al revisar el código; texto abajo) | **#83** | HU-021-FE |
| HU-023-FE.1 a FE.3 | #80 a #82 | HU-023-FE |

- Las issues viejas que cambian con el contrato del cubicador tienen un **comentario de enlace** (su cuerpo no se tocó): #69, #70, #71 (reemplazada por #81), #74, #75 y #78.
- #53 (HU-011.4) y #55 (HU-012.4) se editaron con los cambios de la reestructuración; #53 tiene además un comentario que apunta a #82.

### [HU-021-FE.12] [Corrección con BD · Frontend] Ganada/perdida: usar /resultado en vez de /seguimiento
**Labels:** `frontend`, `bug`, `corrección-bd`, `sprint-5`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone).** No depende de nada: el backend ya está listo.

Hoy "Ver detalle" marca ganada/perdida con `POST /web/cotizaciones/{folio}/seguimiento` (`cotifly.repositorio-http.ts`, `actualizarSeguimiento`). Para las cotizaciones del flujo de NEXO el backend las rechaza ahí con `USAR_RESULTADO_NEXO` (`clsSeguimientoService.cs`), así que el botón falla. El camino correcto es `POST /web/cotizaciones/{folio}/resultado`, que ya recibe `Resultado`, `Motivo` y `Comentario` por separado, llama primero a NEXO y deja el motivo en "No aplica" al ganar.

- [ ] Cambiar `actualizarSeguimiento` a `POST /web/cotizaciones/{folio}/resultado` con `{ resultado, motivo, comentario }`.
- [ ] Mostrar los errores que devuelve `/resultado` (por ejemplo, NEXO no disponible, o pérdida en `pendiente_costeo` sin confirmación de NEXO) con un mensaje claro.
- [ ] Probar con una cotización del flujo de NEXO: marcar ganada y perdida sin error (Kevin).
- [ ] El select de motivos y el comentario aparte siguen en #79, cuando negocio decida.
