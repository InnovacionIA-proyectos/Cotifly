# HU-021 — [Corrección con BD · Frontend] Adaptar ClientePro a solicitud → rutas → camiones

> **Issues ya creadas (2026-10-08): no se modifican.** Lo nuevo de la conexión con el cubicador, y lo que cambia sobre FE.1, FE.2, FE.3, FE.6, FE.7 y FE.10, está en issues aparte: `docs/backlog/2026-10-08_HU_Sprint5_Cotifly_Front.md` (HU-023-FE).

**Fecha de redacción:** 2026-10-08. **Documento hermano de** `docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md` (el de backend y datos).
**Alcance:** solo frontend. Este documento no incluye HU de backend ni de base de datos.

## Notas de lectura

1. **Repo donde se crean los issues:** `clientepro-frontend`. **Asignado a:** Sara (Frontend). QA: Kevin.
2. **Prefijo del título:** `[Corrección con BD · Frontend]`. Label: `corrección-bd`.
3. **Numeración:** `HU-021-FE.1` a `HU-021-FE.11`, para distinguirlas de HU-021.1 a 021.14 (backend). Si se prefiere otra numeración, solo cambian los títulos.
4. **Qué se verificó:** el 2026-10-08 se recorrió el código de `clientepro-frontend` (módulo `src/app/components/cotifly/`). Las rutas de archivo de cada HU salen de ese recorrido. La rama que se revisó fue `51-hu-0103-tablero-gerente-comercial-frontend`, que tenía cambios sin commitear en `cotifly.model.ts`, `cotifly.repositorio.ts` y `cotifly.repositorio-http.ts`: coordinar antes de tocarlos.
5. **Issues abiertos que se ven afectados** (se corrigen, no se duplican): **HU-011.4** (pantalla de licitación) y **#55 HU-012.4** (selector de moneda). Ver la sección final.

---

## HU-021-FE — Milestone

### Historia
**Como** comercial de Botero Soto, **quiero** cotizar una solicitud con varias rutas, escoger la mercancía de un catálogo y ver cuántos camiones se necesitan, el costo, el margen y el precio de cada ruta y el precio total, **para** no tener que digitar el vehículo ni la carrocería, y para que mi solicitud de licitación o de varias rutas se cotice completa.

### Contexto técnico: por qué es necesario

El backend pasa de "una cotización = una ruta = un vehículo" a **solicitud → rutas → camiones**, con las mercancías y los servicios conexos por ruta, el margen y el precio por ruta, y un cubicador que recomienda los camiones (`docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md`). Ese cambio rompe el contrato de la API (HU-021.11, `CAMBIOS_BACKEND` §7).

**Lo que hay hoy en el frontend** (recorrido del 2026-10-08, corregido con la revisión de la sesión de frontend):
- **El backend ya tiene un endpoint estructurado, `POST /web/solicitudes-costeo`** (`clsSolicitudCosteoWebRequest`, HU-005.5), pero **una ruta por llamada**, y **el frontend no lo usa**. "Nueva cotización" compone una frase en lenguaje natural y la manda a `POST /web/chat`, **un turno por línea, secuencial** (`cotizador.component.ts`: `cotizarTodasLasLineas`, `enviarSiguienteLinea`, `componerMensajeLinea`). Es un "puente transicional" que el propio código marca como pendiente.
- El modelo de datos es plano: `Cotizacion` es el resumen de una sola línea (`cotifly.model.ts`). **No existen** los conceptos de solicitud, ruta, camión, mercancía de catálogo ni servicio conexo.
- El vehículo y la carrocería se digitan o se escogen a mano en el formulario (`cotizador.component.html`, controles `tipoVehiculo` y `carroceria`), con la ayuda de `POST /web/cubicacion` (cubicaje rápido contra Nexo).
- El paso 4 "Conexos" del cotizador y la tarjeta de servicios del detalle son **placeholders** ("Próximamente").
- Las mercancías ("posiciones") usan una **tipología de carga fija en el código** (`tipologia-carga.catalogo.ts`, 7 valores), no un catálogo con id.
- La carga de Excel se parsea **en el navegador** (`importar-excel-lineas`, librería `xlsx`); no hay endpoint de carga de licitación.
- Moneda: no hay selector. El mensaje al chat fija "COP".
- "Ver detalle" depende de `history.state`: no hay endpoint por id, y una recarga directa manda al listado.

**Compatibilidad mientras tanto:** el backend acepta el formato plano viejo convirtiéndolo a "una ruta, un camión" hasta que el frontend nuevo salga (HU-021.11, último punto). Por eso el frontend se puede ir cambiando por partes sin romper producción, y esa compatibilidad se quita cuando todo el frontend nuevo esté publicado.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Cotizar una solicitud con varias rutas
Dado un comercial que abre "Nueva cotización"
Cuando agrega dos rutas, cada una con sus mercancías del catálogo
Y no digita vehículo ni carrocería
Entonces la solicitud se envía de una sola vez, no una línea por chat
Y ve por cada ruta los camiones recomendados, el costo, el margen y el precio
Y ve el precio total de la solicitud, que es la suma de sus rutas

Escenario: El cubicador no puede recomendar un camión
Dado una ruta con una mercancía para la que no hay selección automática
Cuando se envía la solicitud
Entonces la ruta aparece con el camión "sin vehículo asignado" y en costeo pendiente
Y la solicitud no se bloquea ni se parte

Escenario: El cubicador no responde
Dado que el cubicador no está disponible
Cuando el comercial envía la solicitud
Entonces ve un mensaje claro de que no se pudieron calcular los vehículos
Y el formulario no queda en carga infinita ni pierde lo que digitó

Escenario: Ajustar el margen de una ruta
Dado una solicitud con dos rutas de líneas de negocio distintas
Cuando el comercial ajusta el margen de la segunda ruta
Entonces solo cambian el precio de esa ruta y el total de la solicitud

Escenario: Licitación por Excel
Dado un Excel de licitación con varias filas
Cuando el comercial lo sube
Entonces cada fila es una ruta y ve el progreso de costeo de cada una
Y puede exportar el Excel, con un aviso si quedan rutas pendientes de costeo
```

### Dependencias del backend

| Frontend necesita | Lo entrega | Estado |
|---|---|---|
| Contrato nuevo de la solicitud y de la consulta de costeo (rutas, camiones, mercancías, conexos, totales) | HU-021.11 (y 021.6 a 021.9) | Pendiente |
| `GET /web/tipos-mercancia` (catálogo del cubicador) | HU-021.5 | Pendiente; **depende de que el cubicador tenga el endpoint** |
| Camiones recomendados en la respuesta | HU-021.5 | Igual: depende del cubicador |
| Endpoint de detalle de una cotización por id | A confirmar con Duban (no está en las HU de backend) | Falta definir |
| Catálogo de servicios conexos para la ruta | HU-015.1 (Sprint 5) y HU-021.8 | Pendiente |
| Endpoint de carga de licitación (hoy el Excel se parsea en el navegador) | HU-011.2 / HU-021.6 | Falta definir |
| `GET /web/cotizaciones/regional` | HU-016.1, parte A | No existe todavía |

**Mientras el cubicador no exista:** el frontend se puede construir contra un servidor falso o con "una ruta = un camión" y el vehículo digitado como hoy, igual que el backend. Lo que no se puede terminar es el desplegable de mercancías (FE.3) ni mostrar los camiones recomendados.

---

## Issues

### [HU-021-FE.1] [Corrección con BD · Frontend] Modelos, repositorio y contrato nuevo
**Labels:** `frontend`, `tarea-tecnica`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende del contrato de HU-021.11** (puede empezar con el contrato borrador de `CAMBIOS_BACKEND` §7 y un servidor falso).

- [ ] Tipos nuevos en `cotifly/models/cotifly.model.ts`: `Solicitud` (cabecera), `Ruta`, `Camion`, `Mercancia`, `ServicioConexoRuta` y los totales de costo, margen y precio por ruta. Rediseñar `Cotizacion` y `CotizacionRegional` (hoy son el resumen de una sola línea).
- [ ] Formas HTTP y mapeadores (`*Http` y `mapear*`, snake_case → camelCase) en `cotifly.repositorio-http.ts`, y métodos nuevos en la clase abstracta `cotifly.repositorio.ts`.
- [ ] **Envío estructurado de la solicitud.** Partir de `POST /web/solicitudes-costeo`, que ya existe en el backend (hoy acepta una ruta por llamada; HU-021.11 lo amplía a una lista de rutas). Reemplaza el puente de `POST /web/chat` por línea. Hasta que el backend acepte varias rutas, se puede adoptar con una llamada por ruta.
- [ ] Método y modelo para consultar el costeo de una solicitud (`GET /web/cotizaciones/{folio}/costeo`): rutas con sus camiones, mercancías, conexos y totales.
- [ ] `estadoCosteo` pasa de string libre a un tipo con los 7 valores (`pendiente_envio`, `error_envio`, `pendiente_costeo`, `autocosteo`, `entregada`, `descartada`, `costeo_vencido`), por camión, por ruta y por solicitud (el backend entrega el resumen; ver `CAMBIOS_BACKEND` §9.2).
- [ ] Mensajes de error nuevos en `cotifly-mensajes.ts` (cubicador no disponible, mercancía condicionada, ruta sin camiones).
- [ ] Servidor falso o *mock* del repositorio con los casos de los Gherkin, para trabajar sin esperar al cubicador.
- [ ] Pruebas del repositorio (`cotifly.repositorio-http.spec.ts`) con los mapeadores nuevos.

### [HU-021-FE.2] [Corrección con BD · Frontend] "Nueva cotización": solicitud con varias rutas
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-021-FE.1.** Reemplaza lo que HU-004.4 dejó como formulario de una línea.

- [ ] Rehacer el wizard de `cotizador.component` (hoy 6 pasos: Datos, Líneas, Posiciones, Conexos, Mensaje, Resumen) sobre la estructura **solicitud → rutas**: encabezado (cliente, ID SAP, tipo spot/licitación, deadline) y una lista de rutas que se agregan y se quitan.
- [ ] Por ruta: línea de negocio, origen y destino (**municipios por id**, con el autocompletar actual), tipología, clase de viaje, exclusivo, consolidado, **horas de cargue y descargue por ruta** (hoy van en el encabezado), peso y valor de la mercancía.
- [ ] Condiciones de contenedor por ruta: retorna contenedor, devolución y levante en **municipio por id** (hoy son texto), levante en otra ciudad, y **valor del levante** (`valorLevanteContenedor`, un valor por ruta que el backend copia a cada camión).
- [ ] **Vehículo y carrocería: dejan de ser un campo que el comercial digita, pero solo cuando el cubicador exista.** `tipoVehiculo` y `carroceria` (`cotizador.component.html` y `LineaControls`) pasan a ser lo que recomienda el cubicador (FE.3). **Mientras el cubicador no exista, se conservan como respaldo** (una ruta = un camión, vehículo escogido como hoy), detrás de una **bandera de configuración** que se apaga cuando FE.3 salga. Así FE.2 se puede publicar antes que el cubicador sin dejar al comercial sin forma de indicar el vehículo.
- [ ] Reemplazar `componerMensajeLinea` y el envío por chat por el envío estructurado de FE.1. Mantener el borrador en `sessionStorage` (`cotifly_cotizador_borrador`) con la forma nueva, y migrar o descartar con cuidado los borradores viejos.
- [ ] Mensaje claro y formulario que no queda en carga infinita si el envío falla; el comercial no pierde lo que digitó (herencia de HU-004.4).
- [ ] Confirmación de teléfono antes del PDF y control de margen editable (HU-004.4): se conservan, ahora **por ruta** (el margen se ve en FE.5).
- [ ] Retirar `calcularCubicaje` como pre-llenado de vehículo y carrocería; su lugar lo toma lo que devuelve la solicitud (FE.3). Decidir con Duban si `POST /web/cubicacion` se mantiene como vista previa.

> **2026-10-08:** la parte del cubicador de FE.3 (mercancías y camiones) y de FE.6 (mercancía por fila en la licitación) pasó a `docs/backlog/2026-10-08_HU_Sprint5_Cotifly_Front.md` (HU-023-FE), con el contrato real. Allí manda el código de mercancía, no el id.

### [HU-021-FE.3] [Corrección con BD · Frontend] Mercancías del catálogo del cubicador y camiones recomendados
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-021-FE.2 y del cubicador** (`GET /web/tipos-mercancia` y camiones en la respuesta, HU-021.5).

- [ ] Por ruta, lista de **mercancías**: tipo de mercancía escogido de un **desplegable con el catálogo del cubicador** (`GET /web/tipos-mercancia`), cantidad, largo, ancho y alto en cm, y peso en kg. Reemplaza las "posiciones" con la tipología fija de `tipologia-carga.catalogo.ts`.
- [ ] Al escoger una mercancía se guardan el **`idTipoMercancia` y el nombre** (el backend copia el nombre; no hay código estable). Cache del catálogo en el frontend (hoy los catálogos se vuelven a pedir cada vez que se abre el cotizador).
- [ ] **Mostrar los camiones recomendados por ruta** (solo lectura): cantidad de camiones y vehículo y carrocería de cada uno, con su peso. El comercial no los edita.
- [ ] **Estados que no son error:** "sin selección automática" (camión sin vehículo, lo resuelve la Mesa de Costeo de Nexo) y mercancía **condicionada** (la solicitud viaja completa). Mostrarlos como información, no como falla.
- [ ] **Si el cubicador no responde:** mensaje "no se pudieron calcular los vehículos", sin enviar nada a Nexo y sin perder el formulario.
- [ ] Si hay que volver a cubicar una cotización vieja y la mercancía ya no está en el catálogo, el comercial **la vuelve a escoger** del desplegable (decisión con el equipo del cubicador).
- [ ] Mientras el cubicador no exista: construir y probar contra un servidor falso; la bandera de FE.2 mantiene el vehículo digitado como hoy, y se apaga al publicar esta HU.

### [HU-021-FE.4] [Corrección con BD · Frontend] Servicios conexos por ruta
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-021-FE.2 y del catálogo de conexos del backend (HU-015.1, HU-021.8).**

- [ ] Volver real el paso 4 "Conexos" del cotizador (hoy un placeholder "Próximamente"): por ruta, escoger servicios del catálogo de Nexo con **cantidad** (decimal) y ver su precio.
- [ ] Mostrar el **costo de conexos de la ruta** (precio × cantidad) y cómo entra en el total de la ruta.
- [ ] Cambiar los conexos de una ruta ya cotizada (peticiones de conexos dicen **a qué ruta** aplican, `CAMBIOS_BACKEND` §7), con el aviso de que el PDF queda desactualizado y se genera de nuevo.
- [ ] Deshabilitar el cambio cuando el conexo ya está en Nexo (regla del backend: si algún camión de la ruta ya tiene línea en Nexo).
- [ ] La tarjeta "Servicios adicionales o logísticos" del detalle (FE.7) usa los mismos datos.

### [HU-021-FE.5] [Corrección con BD · Frontend] Resultado del costeo: rutas, camiones, margen y precio
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-021-FE.1 y HU-021.8** (margen por ruta).

- [ ] Vista del resultado de la solicitud: por **ruta**, sus camiones, los costos (fletes, retorno, levante, conexos), el costo final, el margen aplicado y el precio de la ruta; y abajo el **precio total** (suma de las rutas).
- [ ] **Ajuste de margen por ruta:** hoy se hace con un mensaje de chat ("Ajusta el margen a X%", `ajustarMargen`); pasa a una acción sobre una ruta concreta, con el margen sugerido por la política de su línea de negocio y el origen del margen (`sap_tm`, `politica_linea`, `negociado`).
- [ ] Reemplazar el componente de progreso de 4 pasos (`progreso-cotizacion`, derivado de herramientas del chat) por un **estado de costeo real por camión, por ruta y por solicitud**, con las etiquetas que ya existen (`ETIQUETAS_ESTADO_COSTEO`).
- [ ] Mientras algún camión no tenga costo, la ruta y la solicitud **no tienen precio**: mostrarlo como "pendiente", no como 0.
- [ ] Actualizar el estado del costeo mientras Nexo responde (el backend consulta a Nexo por *polling*): decidir con Duban si el frontend consulta cada cierto tiempo o solo al abrir.

### [HU-021-FE.6] [Corrección con BD · Frontend] Licitación por Excel: rutas, progreso y exportación (actualiza HU-011.4)
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-021-FE.2 y de HU-021.6** (licitación en el backend).

> **Esta HU corrige HU-011.4** (ver la sección final): HU-011.4 pide la pantalla de carga, progreso por línea, conexos con checkbox y exportación. Aquí se redefine sobre el modelo nuevo.

- [ ] Cada fila del Excel es una **ruta** (`rutaOrigenIndice` = índice de la fila). La fila ya **no lleva vehículo ni carrocería** y gana **mercancías** (del catálogo del cubicador). Ajustar `importar-excel-lineas` y `FilaExcelLinea` (hoy parsea en el navegador y trae `tipoVehiculo` y `carroceria`).
- [ ] Decidir con Duban si el Excel sigue parseándose en el navegador o se sube al backend (hoy no hay endpoint de carga de licitación); el formato del Excel lo define HU-011.1 (`docs/conexion_nexo/HU-011-formato-excel-licitacion.md`).
- [ ] Mostrar el **progreso de costeo de cada ruta** (y de sus camiones), reutilizando el estado real de FE.5, no el progreso del chat.
- [ ] Por cada ruta costeada, **servicios conexos con checkbox** para incluir o excluir del precio final (mecanismo de FE.4).
- [ ] **Botón de exportar Excel**, con **aviso claro si quedan rutas pendientes de costeo**. La exportación pasa a **una fila por ruta, con sus camiones como detalle**, y con origen y destino desde el catálogo de municipios.
- [ ] "Exportar Excel" del detalle (hoy deshabilitado, "Próximamente") comparte este trabajo con HU-018.1, parte C (Sprint 5).

### [HU-021-FE.7] [Corrección con BD · Frontend] "Mis cotizaciones" y "Ver detalle" con rutas
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-021-FE.1 y de HU-021.9** (resumen de "Mis cotizaciones").

- [ ] **Mis cotizaciones:** la línea de negocio pasa a ser la de la ruta 1, o **"Varias"** si hay rutas de más de una línea; el margen y su origen vienen de la ruta; el estado de costeo es el resumen de la solicitud. Conservar las acciones actuales (detalle, PDF, enviar/reenviar, nueva versión).
- [ ] **Ver detalle:** rutas desplegables, cada una con sus camiones, mercancías, conexos, costos, margen y precio (hoy solo una tarjeta "Solicitud línea 1"). Los campos que el backend todavía no manda dejan de verse como "Pendiente" cuando lleguen.
- [ ] **Endpoint de detalle por id:** el detalle hoy recibe la cotización por `history.state` y una recarga directa manda al listado. Confirmar con Duban el endpoint y consumirlo.
- [ ] Ganada/perdida: se conserva el modal de motivo; **deshabilitar el botón mientras el estado de costeo sea `pendiente_costeo`** (pendiente de HU-004.5).
- [ ] "Todas las cotizaciones" del Gerente Comercial y dashboard (`cotizaciones-regional`, `dashboard-gerencial`): adaptar al resumen nuevo cuando el backend lo defina (HU-016.1, parte A, y HU-021.10). El endpoint regional todavía no existe.

### [HU-021-FE.8] [Corrección con BD · Frontend] PDF, correo y "Nueva versión" por ruta
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-021-FE.2 y HU-021.9.**

- [ ] **Nueva versión:** `DatosNuevaVersion` (`obtenerDatosNuevaVersion`, hoy con origen, destino, vehículo, carrocería y demás en plano) pasa a traer **rutas con sus mercancías, conexos y condiciones**. El formulario se prellena por ruta; **sin vehículo ni carrocería**.
- [ ] Si el comercial cambia **mercancías o peso** de una ruta, avisar que se vuelven a pedir los camiones al cubicador y se pierde el costeo de esa ruta.
- [ ] **PDF:** descarga del PDF de la solicitud; si hay más de una ruta, es un PDF de pliego (una sección por ruta). Mostrar el aviso de "PDF desactualizado" cuando cambien conexos o márgenes.
- [ ] **Correo:** la vista previa y el reenvío (`dialogo-vista-previa-correo`) no cambian en su flujo; revisar que el asunto y el cuerpo muestren el total de la solicitud.
- [ ] Revisar que **"destinatarios previos por cliente"** siga funcionando (el backend toca esa consulta en HU-021.4).

### [HU-021-FE.9] [Corrección con BD · Frontend] Selector de moneda COP/USD por ruta y total (actualiza HU-012.4, #55)
**Labels:** `frontend`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone). Depende de HU-012.2 (backend) y de HU-021-FE.5.**

> **Esta HU corrige HU-012.4 (#55)** (ver la sección final). Hoy no hay selector de moneda y el mensaje al chat fija "COP".

- [ ] Selector **COP/USD** en la solicitud (una moneda para toda la solicitud).
- [ ] Mostrar **el precio de cada ruta y el total** en la moneda escogida.
- [ ] Manejar de forma visible el **error si la fuente de TRM no está disponible** al cotizar en USD, sin dejar el formulario en carga infinita.
- [ ] Columna **moneda** en "Mis cotizaciones" (la moneda ya viaja en la cotización y hoy solo formatea con `Intl.NumberFormat`).
- [ ] Revisar que las métricas y el dashboard siguen en COP (hoy `valorCotizadoCop`).

### [HU-021-FE.11] [Corrección con BD · Frontend] Pendientes del Sprint 5 en frontend: clase de contenedor, margen promedio y motivo + comentario
**Labels:** `frontend`, `producto`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-021-FE (milestone), y la parte de frontend de HU-015.1, HU-016.1 y HU-018.1 del Sprint 5** (`docs/backlog/2026-10-07_HU_Sprint5_Cotifly.md`). Agrupa tres cosas pequeñas para no abrir un issue por cada una. **Son las mismas de los puntos 3.2, 6 y 8 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`**: al cerrar este issue hay que marcarlas también allá. Sus tres partes dependen de decisiones o de trabajo del backend, así que va **después** de FE.5 y FE.7.

- [ ] **Clase de contenedor desde Nexo** (HU-015.1): reemplazar el catálogo fijo `clase-contenedor.catalogo.ts` por la lista que entrega el backend (`/api/catalogos/clases-contenedor`). Hoy, si Nexo agrega, renombra o retira un valor, el frontend queda desincronizado sin enterarse.
- [ ] **Tarjeta "Margen promedio"** de "Mis cotizaciones" (HU-016.1, parte B): hoy muestra un "± desviación" que casi siempre sale vacío. Cuando negocio decida cómo se compara, mostrar el margen aplicado promedio contra el promedio de los márgenes de política: **verde** si está por encima y **rojo** si está por debajo. Con la Opción A el frontend calcula el promedio con la lista de márgenes que entregue el nuevo endpoint accesible al Comercial. **Cómo se promedia (simple o ponderado por costo, por ruta o por cotización) no está decidido**: el pendiente original (punto 6 de los pendientes de Duban) hablaba de un promedio simple. Confirmar con negocio antes de construir; el backend (HU-021.10) recomienda ponderar por costo sobre las rutas, pero es una recomendación, no un acuerdo.
- [ ] **Motivo y comentario al marcar "Perdida"** (HU-018.1, parte B): hoy es un único `<textarea>` "Motivo (opcional)" en `detalle-cotizacion.component.html`. Si negocio decide construirlo separado: un **select de motivos** (`Valor muy alto`, `Flete fuera de rango de plaza`, `Cliente desistió del servicio`, `Tiempo de respuesta vencido`, `Otro`) y un `textarea` de comentario aparte. Antes, confirmar a qué endpoint llama el botón (`/seguimiento` o `/resultado`).

### [HU-021-FE.10] [Corrección con BD · Frontend] Pruebas, QA y retiro del formato viejo
**Labels:** `frontend`, `qa`, `corrección-bd`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Kevin (QA) + Sara (Frontend)
**Parte de HU-021-FE (milestone). Transversal: se arma a medida que avanzan las demás.**

- [ ] **Pruebas unitarias del cotizador:** hoy el módulo no tiene `spec` del cotizador, del dashboard, del regional ni de admin-margenes (el del cotizador se borró antes del rediseño). Crear el del cotizador nuevo con los Gherkin del milestone.
- [ ] Actualizar los `spec` de `mis-cotizaciones`, `detalle-cotizacion`, `asistente` y del repositorio.
- [ ] **Pruebas de punta a punta (Playwright, `qa/e2e`)** del flujo: formulario → camiones → costeo → margen → PDF, y de la licitación. Hoy Playwright no cubre Cotifly. **Depende del cubicador y de Nexo**: mientras tanto, con un servidor falso.
- [ ] Probar contra el backend con el **formato viejo aceptado** (compatibilidad de HU-021.11) y luego **retirar** esa compatibilidad cuando todo el frontend nuevo esté publicado.
- [ ] Quitar del código lo que quedó sin uso: el puente de `POST /web/chat` por línea, `componerMensajeLinea`, `tipologia-carga.catalogo.ts` y los pre-llenados de vehículo y carrocería.
- [ ] Antes de publicar: restaurar la URL base de `cotifly.providers.ts` (hoy `http://localhost:8083`, marcada como temporal) y revisar `ES_AMBIENTE_PRUEBA` en `cotifly-roles.ts`.
- [ ] Actualizar `docs/COTIFLY_CONSTRUCCION.md` del frontend (bitácora y sección de deuda técnica) con lo que se cierra.

---

## Qué corregir en las issues abiertas

### HU-011.4 — Pantalla de carga/descarga y selección de servicios conexos (frontend)
Texto actual: *pantalla para subir el Excel, ver el progreso de costeo de cada línea, servicios conexos con checkbox y botón de exportar con aviso*.

Cambios:
1. **"Línea" pasa a ser "ruta"**, y cada ruta tiene sus camiones: el progreso se muestra por ruta (y por camión), no solo por línea.
2. La fila del Excel **ya no lleva vehículo ni carrocería** y gana **mercancías** del catálogo del cubicador.
3. Los servicios conexos son **por ruta** y salen del catálogo de Nexo.
4. La exportación sale **una fila por ruta, con sus camiones como detalle**.
5. Dependencias: además de HU-011.1, 011.2 y 011.3, agregar **HU-021-FE.1, FE.2, FE.4 y FE.5**. El trabajo nuevo está en **HU-021-FE.6**.

### HU-012.4 (#55) — Selector de moneda en el formulario (frontend)
Texto actual: *selector COP/USD, precio final en la moneda escogida, error si la TRM no está disponible, columna moneda en "Mis cotizaciones"*.

Cambios:
1. **"Precio final" pasa a ser el precio de cada ruta y el total de la solicitud.**
2. La moneda es **una por solicitud**.
3. Dependencias: además de HU-012.2 y HU-004.4, agregar **HU-021-FE.2 y FE.5**. El trabajo nuevo está en **HU-021-FE.9**.

---

## Orden sugerido

1. **FE.1** (modelos y contrato, con servidor falso) y **FE.10** en paralelo (pruebas base).
2. **FE.2** (solicitud con rutas) y **FE.5** (resultado del costeo): son lo mínimo para cotizar con "una ruta = un camión" y el vehículo digitado, sin esperar al cubicador.
3. **FE.4** (conexos), **FE.7** (listado y detalle), **FE.8** (PDF, correo y nueva versión) y **FE.9** (moneda).
3b. **FE.11** (clase de contenedor, margen promedio y motivo + comentario), cuando negocio decida las dos partes que lo necesitan.
4. **FE.3** (mercancías y camiones) cuando el cubicador tenga sus endpoints.
5. **FE.6** (licitación) cuando el backend de licitación esté (HU-021.6).
6. Al final: retirar el formato viejo (FE.10).

## Pendiente de confirmar con Duban

- Endpoint de **detalle por id** de una cotización.
- Si la **carga de la licitación** se sube al backend o sigue parseándose en el navegador.
- Si `POST /web/cubicacion` (cubicaje rápido) se mantiene como vista previa.
- Cómo se entera el frontend de que Nexo ya costeó (*polling* del frontend o solo al abrir).
- Catálogo de servicios conexos para el frontend (HU-015.1).
