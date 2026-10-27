# Backlog histórico Cotifly (2026-09-29 a 2026-10-27)

**Repo:** `Cotifly` · **Compilado:** 2026-10-27. Reúne, **completos y sin resumir**, los documentos de
`docs/backlog/` que ya cumplieron su función como borrador y se reemplazan por este único archivo:
`2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`, `2026-10-07_HU_Sprint5_Cotifly.md`,
`2026-10-14_HU_Correcciones_Nuevas_Cotifly.md`, `2026-10-16_PENDIENTES_DUBAN.md`,
`2026-10-19_HU_Sprint6_Cotifly_Backend.md`, `2026-10-20_PENDIENTES_DUBAN.md` y
`2026-10-27_HU_multirol_y_margen.md`.

**Qué es esto:** cada sección de abajo es el contenido **completo** del documento original (Historia,
Contexto técnico, Criterios Gherkin, Trazabilidad, Alcance y el detalle de cada issue, tal cual se
escribieron), en el orden en que se crearon. No es un resumen — si algo no está aquí tal como estaba en el
original, es un error de esta compilación. Lo único que cambia es el encabezado de cada sección, que deja
clara la fecha en que ese contenido se creó. Los pendientes **vivos** (lo que falta hacer hoy) están aparte,
en `2026-10-27_PENDIENTES_SARA.md`.

---


---

> **Documento original:** `2026-09-29_PENDIENTES_CORRECCION_DUBAN.md` · **Creado:** 2026-09-29

# Pendientes de corrección para Duban (backend)

- **Fecha:** 2026-09-29
- **Alcance:** hallazgos encontrados probando en vivo el backend nativo (puerto 8083) contra la base real
  `tbsdatacenter`/`dbTarifario_Nexo_Pruebas`, después del merge de la migración 0004 (HU-005.8).

## Estado de cada punto (revisado el 2026-10-08)

Cada punto de este documento ya tiene su HU. Las HU del Sprint 5 están en `docs/backlog/2026-10-07_HU_Sprint5_Cotifly.md` (se consolidaron en 5 issues) y las de la reestructuración en `docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md`.

| # | Punto | Estado | Dónde está |
|---|---|---|---|
| 1 | `estadoEnvio` sin `CHECK` | **Corto plazo hecho** (el `CHECK` está en `01_crear_esquema.sql` y probado). Mediano plazo (tolerar una fila mala) se hace al reescribir la consulta | HU-021.2 y HU-021.4 |
| 2 | Cotización de prueba de Duban | **Ya no aplica**: la base compartida se borró y recreó | — |
| 3.1 | Tipo de vehículo y clase de viaje | **Casi resuelto**: ya existen `clsCatalogosMaestrosNexo` y `/api/catalogos/*`. Vehículo y carrocería salen ahora del cubicador. Falta verificar | HU-015.1 |
| 3.2 | Clase de contenedor | Backend: verificar. Frontend: sigue con el catálogo fijo | HU-015.1 y HU-021-FE.11 |
| 3.3 | Servicios conexos | Catálogo y endpoints ya existen. Falta verificar el envío de la selección a Nexo; cómo se guardan es HU-021.8 | HU-015.1 y HU-021.8 |
| 3.4 | Línea de negocio | Nota, no es un hueco | HU-015.1 |
| 4 | Header `X-Portal-clsUsuario-Id` | **Pendiente**: 6 archivos sin commit | HU-014.3, parte A |
| 5 | Listado de cotizaciones por regional | Pendiente; se construye sobre el esquema nuevo | HU-016.1, parte A |
| 6 | "Margen promedio" | Pendiente de decisión de negocio | HU-016.1, parte B |
| 7 | "Ver detalle" | Los campos se exponen al reescribir el DTO; el desglose del costeo se investiga | HU-018.1, parte A, y HU-021.9 / 021.11 |
| 8 | Ganada/Perdida con motivo y comentario | **Hecho en frontend** (2026-10-16, HU-021-FE.12/#83 + HU-021-FE.11/#79 parte 3) | HU-018.1, parte B |
| 9 | "Exportar Excel" | Pendiente | HU-018.1, parte C |
| 10 | Script `0009` e instalación desde cero | **Hecho**: probados el 2026-10-08. Alembic se borró | HU-021.2 |
| 11 | Rutas de documentos dentro de `backend-dotnet/` | Pendiente | HU-014.3 (mismo commit) y HU-022.1 |
| 12 | Limpieza del código viejo | Pendiente, al final de todo | HU-022.1 |

## 1. Un solo valor inválido en `estadoEnvio`/`estadoCosteo` tumba TODO "Mis cotizaciones" del usuario

**Síntoma:** `GET /web/mis-cotizaciones` respondió `500` para un usuario que tenía una sola cotización con un valor
fuera de catálogo (en este caso `estadoEnvio = 'pendiente'`, insertado por error en datos de prueba).

**Causa:** `clsCotizacionRepository.ListarConLineaPorUsuarioAsync` (`src/Cotifly.Infraestructura/Cotizaciones/Repositories/clsCotizacionRepository.cs:109`)
trae TODAS las cotizaciones del usuario en una sola consulta materializada (`ToListAsync`). El mapeo de EF Core usa
`clsConvertidorCodigoEnum<TEnum>` (`src/Cotifly.Infraestructura/Comun/Persistence/clsConvertidorCodigoEnum.cs`), cuyo
lado de lectura llama a `clsCodigoEnum.DesdeCodigo<TEnum>`, que lanza `ArgumentException` sin capturar si el código no
está en el enum (`EstadoEnvio` o `EstadoCosteo`). Esa excepción no se maneja en ningún punto de la cadena
(`clsMisCotizacionesService` → controlador), así que se propaga como `500` genérico y bloquea la consulta completa
para ESE usuario — no solo la fila con el dato malo.

**Por qué importa ahora:** `estadoCosteo` ya tiene un `CHECK` en BD desde la migración 0004
(`ck_cotizaciones_estadoCosteo`), pero **`estadoEnvio` sigue sin ningún `CHECK` a nivel de base de datos** — nada
impide hoy que una fila quede con un valor fuera de `sin_enviar`/`enviada`/`reenviada` (dato viejo, migración futura,
insert manual, bug de otro flujo) y tumbe el módulo para ese comercial.

**Sugerencia:**
- Corto plazo: agregar el mismo tipo de `CHECK` para `estadoEnvio` que ya existe para `estadoCosteo` (mismo patrón que
  `ck_cotizaciones_estadoCosteo` en `0004_costeo_nexo.sql`).
- Mediano plazo: que `ListarConLineaPorUsuarioAsync` (o el converter) no deje que una fila con un código inesperado
  tumbe las demás — por ejemplo, tolerar el valor y loguearlo en vez de lanzar, o filtrar/mapear fila por fila en vez
  de una proyección LINQ-to-Entities que materializa todo de una.

## 2. Cotización de prueba de Duban no aparece en la base compartida real

Al revisar `dbo_cotifly.Cotizaciones` en `tbsdatacenter`/`dbTarifario_Nexo_Pruebas` (la base real compartida del
proyecto), la cotización de prueba que Duban insertó manualmente para probar "Mis cotizaciones" no estaba ahí — el
`GET /web/mis-cotizaciones` para su usuario venía vacío hasta que se insertaron datos de prueba nuevos directamente
en esa base.

**Sugerencia:** que Duban confirme contra qué servidor/base insertó esa fila (probablemente una base local o distinta
a `tbsdatacenter`), para descartar que haya una segunda base "de pruebas" desincronizada dando vueltas.

## 3. Catálogos y servicios conexos: Cotifly no está usando las fuentes correctas de Nexo

**Fecha:** 2026-10-05
**Contexto:** revisando qué le falta a Cotifly para que toda la información del formulario de "Nueva cotización" se
cree correctamente en las tablas de Nexo (no solo se calcule/muestre localmente), comparé `CONTRATO.md` de Nexo
(incluido lo nuevo bajo `/api/catalogos/*`) contra lo que Cotifly ya implementa hoy.

### 3.1 Tipo de vehículo / clase de viaje: fuente equivocada

Cotifly los trae de `GET /api/biblioteca-sap-historico/filtros`
(`src/Cotifly.Infraestructura/CatalogosFlete/Services/clsCatalogosFleteNexo.cs:101`) — ese endpoint es para filtrar
el histórico de tarifas (solo devuelve valores que YA aparecieron en tarifas pasadas), no es el catálogo maestro.
Nexo ahora expone el catálogo real: `GET /api/catalogos/tipos-vehiculo` y `GET /api/catalogos/clases-viaje`
(`CONTRATO.md` ~línea 1210). Cotifly debería migrar a esos.

No encontré un endpoint nuevo equivalente para **carrocería** en `/api/catalogos/*` — falta confirmar con Duban si
existe o si ese campo se queda, por ahora, en `biblioteca-sap-historico/filtros`.

### 3.2 Clase de contenedor: ni siquiera se consulta a Nexo

Cotifly usa un catálogo **hardcodeado propio** (`src/Cotifly.Dominio/.../ClaseContenedor.cs` en el backend,
`clase-contenedor.catalogo.ts` en el frontend) — nunca llama a `GET /api/catalogos/clases-contenedor`. Si Nexo
agrega, renombra o retira un valor, Cotifly queda desincronizado sin enterarse (puede ofrecer un valor que Nexo ya
no acepta, o no ofrecer uno nuevo que sí existe).

### 3.3 Servicios conexos: la selección del comercial nunca llega a Nexo

Esto es lo más importante: `clsCosteoNexo.cs` solo **lee** los conexos que ya traiga la cotización (método
`LeerCotizacion`, a partir de lo que devuelve `POST /api/cotizaciones`), pero Cotifly no tiene ninguna forma de:

- Traer el catálogo de servicios conexos disponibles — `GET /api/servicios-conexos` (paginado, `Roles: Todos`).
- Enviar la selección del comercial de vuelta a Nexo — `POST /api/cotizaciones/{id}/conexos`
  (`Roles: Comercial, CoordinadorFletes, Admin`; body `{ items: [{ servicioConexoId, cantidad?, valorAplicado? }] }`).

Sin esto, cuando el comercial elige servicios conexos en el formulario, esa elección como mucho se calcula/guarda
localmente en Cotifly — **la cotización real en las tablas de Nexo se queda sin esos conexos**, lo que genera una
inconsistencia entre lo que el cliente recibió cotizado y lo que Nexo tiene registrado (afecta reportes, biblioteca
de tarifas ganadas, auditoría, etc., del lado de Nexo). Duban necesita construir estos dos métodos en Cotifly
(`IClsCatalogosFleteProvider`-style para el catálogo, y un nuevo método en `IClsCosteoNexo` para el POST).

### 3.4 Línea de negocio — nota, no es un hueco

Esto sí está bien resuelto: Cotifly usa su propio catálogo de línea de negocio (`MargenesPolitica`) con un mapeo
manual al código que espera Nexo (`LineaNegocioNexo`), descartando a propósito el `lineasNegocio` que trae
`biblioteca-sap-historico/filtros`. Vale la pena preguntarle a Duban si ahora conviene consumir
`GET /api/catalogos/lineas-negocio` en vivo en vez de mantener ese mapeo a mano, pero no es un bug — es
mantenimiento.

## 4. Header `X-Portal-clsUsuario-Id`: el ajuste queda permanente del lado de Cotifly

**Fecha:** 2026-10-02. **Decisión (2026-10-05):** Nexo no va a corregir esto de su lado — el ajuste se queda
permanente en Cotifly, no es un parche temporal a revertir.

Nexo lee el header `X-Portal-clsUsuario-Id` (con un "cls" de más, bug de un renombrado masivo) en vez del
`X-Portal-Usuario-Id` que dice su propio contrato (`CONTRATO.md §7`, discrepancia #1, ya admitida ahí mismo como
"pendiente de decisión" — la decisión fue dejar el código de Nexo como está).

**Ajuste aplicado en Cotifly** (todavía sin commitear): se cambió el literal del header que Cotifly manda, de
`"X-Portal-Usuario-Id"` a `"X-Portal-clsUsuario-Id"`, en estos 5 archivos:

- `src/Cotifly.Infraestructura/Clientes/Services/clsBuscadorClientesNexo.cs`
- `src/Cotifly.Infraestructura/CatalogosFlete/Services/clsCatalogosFleteNexo.cs`
- `src/Cotifly.Infraestructura/Cubicacion/Services/clsCubicacionNexo.cs`
- `src/Cotifly.Infraestructura/Costeo/Services/clsCosteoNexo.cs`
- `src/Cotifly.Infraestructura/Municipios/Services/clsMunicipiosNexo.cs`

**Pendiente real:** los comentarios de los 5 archivos ya se actualizaron (ya no dicen "revertir cuando lo arreglen"),
pero los cambios siguen sin commitear — falta commitearlos como definitivos, no dejarlos como ajuste local suelto.

## 5. Falta un endpoint para listar las cotizaciones de otros comerciales ("Todas las cotizaciones", Gerente Comercial)

**Fecha:** 2026-10-05.

La vista nueva de Gerente Comercial "Todas las cotizaciones" (frontend) necesita un listado fila por fila de las
cotizaciones de los comerciales de su regional, pero hoy no existe ningún endpoint así:

- `GET /web/mis-cotizaciones` — solo trae las del propio usuario (`clsCotizacionesController.cs:22`).
- `GET /web/metricas/regional` — es **agregado** (`clsMetricasRegional`), no un listado fila por fila
  (`clsMetricasController.cs:37`, política `GerenteComercial`).

**Falta construir:** algo como `GET /web/cotizaciones/regional` (mismo patrón de nombre y de política
`GerenteComercial` que ya usa `/web/metricas/regional`).

**Para no partir de cero:** ya existe `clsRegionalUsuarioService.ListarMiembrosAsync(regional)` (lo usa el tablero de
métricas) para resolver los usuarios de una regional. Falta un método nuevo en `IClsCotizacionRepository`
(algo como `ListarPorUsuariosAsync(IReadOnlyCollection<Guid> usuarioIds, ...)`) que filtre `WHERE usuarioId IN (...)`
reutilizando esa lista de miembros, en vez de reconstruir la resolución de regional desde cero.

## 6. "Margen promedio" en "Mis cotizaciones": la desviación mostrada no es la que el negocio espera

**Fecha:** 2026-10-05.

La card "Margen promedio" de "Mis cotizaciones" (Comercial) muestra el margen aplicado promedio junto a un "± desviación" que casi siempre sale vacío (`—`). No es un bug de datos faltantes — es el comportamiento diseñado:

- `DesviacionPromedioMargen` (`clsCalculadoraMetricas.cs`) se calcula como el promedio de `MargenAjustes.diferencia`, una tabla que **solo se escribe cuando el comercial negocia explícitamente un margen distinto al propuesto** (`clsAjusteMargenService.RegistrarAsync`, siguiendo la instrucción del propio agente de IA: *"solo si el usuario explícitamente pide otro margen, registra el ajuste"*, `prompt_cotizador.md`).
- Documentado así a propósito en ADR-016: *"Promedio de MargenAjustes.diferencia (con signo) de los ajustes de la versión vigente"*.
- Si el comercial siempre acepta el margen propuesto (nunca lo negocia), esa tabla queda vacía para él y la desviación sale `null` → `—` en pantalla, aunque sus cotizaciones sí tengan margen aplicado.

**Lo que el negocio quiere en su lugar (pedido explícito de Sara, 2026-10-05):** comparar el margen aplicado promedio del comercial contra el **promedio de los márgenes de política de todas las líneas de negocio** (`MargenesPolitica.margen`) — no contra los ajustes negociados. Verde si el aplicado está por encima del promedio de política, rojo si está por debajo.

**Dos formas de resolverlo, a decidir con negocio:**

1. **Endpoint nuevo accesible para Comercial** que liste `MargenesPolitica` (línea, margen) de las líneas activas, para que el frontend calcule el promedio y la desviación. Hoy el único endpoint que expone esto (`GET /web/admin/lineas-negocio`) está protegido con la política `GerenteComercial` (`clsLineasNegocioController.cs`) — un Comercial no puede llamarlo. No hay ningún ADR que declare estos márgenes como datos confidenciales para el rol Comercial (de hecho ya se filtran uno por uno, de forma conversacional, vía la herramienta `obtener_margen` del chat) — el bloqueo actual parece ser de alcance administrativo (editar/gestionar líneas), no de confidencialidad del valor.
2. **Cambiar la definición de `DesviacionPromedioMargen`** en el backend para que compare contra el promedio de política en vez de los ajustes negociados — cambio más riesgoso, porque ADR-016 ya define esta métrica a propósito como "solo ajustes negociados" (podría afectar otros reportes/metricas que ya dependen de esa definición).

## 7. "Ver detalle" de una cotización: muy por debajo de lo que el propio Nexo ya muestra

**Fecha:** 2026-10-05.
**Contexto:** la pantalla "Ver detalle" de Cotifly (desde "Mis cotizaciones") hoy solo muestra folio, cliente, línea de negocio, precio, margen y estado — varios campos (fecha límite, score, origen, destino, mercancía) salen como "Pendiente". Se revisó el frontend propio de Nexo (`administracion_embarqa_nexo`, página `mesa-costeo` — su "Ver detalle" equivalente) para tener una referencia completa de lo que el negocio probablemente espera ver.

### 7.1 Ganancia rápida y concreta: 4 campos ya existen en BD pero el DTO no los expone

`GET /web/mis-cotizaciones` (y por extensión "Ver detalle", que reusa esa misma fila en memoria) usa el DTO `clsCotizacionResumen`, que **no incluye** `deadline`, `origen`, `destino`, `pesoKg` ni `valorMercancia` — aunque los 4 ya existen en la entidad `clsCotizacion` (persistidos desde la migración 0004) y **ya se exponen hoy en otro endpoint**: `GET /web/cotizaciones/{id}/nueva-version` → `clsDatosNuevaVersion`. Es decir, el dato ya está, solo falta copiarlo al DTO que usa la lista/detalle.

- **Arreglo sugerido:** agregar esos 4 campos a `clsCotizacionResumen` (o a la respuesta de `CotizacionResumen` en el contrato), que ya alimenta `/web/mis-cotizaciones`, `/web/cotizaciones/{folio}/seguimiento`, `/web/cotizaciones/{folio}/resultado`, `/web/solicitudes-costeo` y `/web/cotizaciones/{folio}/costeo/reintentar` — se arregla una vez para los 5.
- **`scoreUrgencia` sigue genuinamente bloqueado** — no existe en ningún lado del backend todavía, confirmado contra ADR-016 (pendiente del contrato de Nexo para consultarlo por solicitud). Dato aparte: Nexo sí devuelve un `scorePrioridad` al **crear** la solicitud (`POST /api/captura-solicitudes` → `CapturaResultDto`), pero Cotifly lo descarta a propósito hoy (`clsSolicitudCreadaNexo`, "reducido a lo que usa Cotifly") — capturarlo en ese momento sería una ganancia más chica y ya desbloqueada, distinta de "consultarlo después por solicitud" (eso sí sigue bloqueado).

### 7.2 Lista completa de lo que Nexo ya muestra en su propio "Mesa de Costeo" (referencia para alcance futuro)

No es necesario construir todo esto de una — se deja como catálogo de referencia para priorizar con negocio:

**Nivel solicitud:** cliente, tipo, modalidad, `deadline`, valor estimado, probabilidad de cierre, `scorePrioridad` (icono de color por umbral, distinto de deadline), estado, fecha de captura, notas.

**Nivel línea:** ruta, tipología, peso, tipo de vehículo, carrocería, posiciones (descripción/cantidad/dimensiones/peso) — todo esto Cotifly ya lo captura en el formulario, el detalle no lo vuelve a mostrar.

**Transparencia del costeo (lo más rico que tiene Nexo, y lo que más se aleja de lo que hace Cotifly hoy):** por línea, Nexo muestra el **piso SICE-TAC desglosado** (valor piso + horas × valor/hora, con vigencia y periodo), **3 fuentes candidatas lado a lado** (SICE-TAC, tarifario de plaza, histórico SAP — cada una con su valor crudo, un "detalle" y cuántas coincidencias encontró), la **propuesta final con una justificación en texto** de por qué se eligió esa fuente, y una lista de **tarifas históricas** de la misma ruta con su vigencia (vigente/por vencer/vencida). Cotifly hoy solo recibe un precio final de Nexo — **falta confirmar con Duban si esa respuesta ya trae este desglose y Cotifly lo descarta, o si habría que pedírselo a Nexo aparte.**

**Servicios conexos:** lista de líneas (cantidad × servicio), con una etiqueta de "alcance" (General / en N de M líneas / Individual) y su valor — ya cubierto en el punto 3.3 de este documento (falta el `POST` que registra la selección).

**Cubicaje/vehículo sugerido:** un resumen compacto (vehículo, compatibilidad, % utilización, configuración, carrocería) es suficiente para una vista de detalle — el desglose 3D completo (por eje, por pieza, centro de gravedad) es una herramienta de trabajo aparte, no algo que haga falta persistir/mostrar en el historial.

**Auditoría/historial de quién hizo qué:** se revisó el propio frontend de Nexo y **tampoco existe ahí** — ni rastro de quién marcó ganada/perdida, ni el motivo/comentario capturado al perder o cancelar queda visible después en ningún lado. Si Cotifly quiere esto, **no hay un patrón de Nexo para copiar** — habría que diseñarlo desde cero.

**Prioridad sugerida:** 7.1 primero (ya resuelto el "por qué", bajo costo); el desglose de costeo (piso/candidatos/propuesta/históricas) depende de confirmar si Nexo ya manda esos datos; lo demás es telón de fondo para cuando negocio priorice enriquecer esta pantalla.

## 8. Ganada/Perdida: solo se construyó con motivo libre, no con el motivo + comentario separados que pedía la HU

**Fecha:** 2026-10-05.

`POST /web/cotizaciones/{folio}/seguimiento` ya existe y funciona (`clsCotizacionesController.cs`), usado hoy desde "Ver detalle" para marcar ganada/perdida. El cuerpo que acepta es `{ ganada, motivo_rechazo }` — **un solo campo de texto libre**.

Pero la HU-004.5 original (ver backlog) pedía 2 campos separados: **motivo** (de una lista predefinida, se persiste en Cotifly y se envía a Nexo) y **comentario** (texto libre, solo se envía a Nexo, no se persiste en Cotifly). Eso nunca se terminó de construir — ni el contrato (`SeguimientoWebHttp` solo tiene `motivo_rechazo`, sin ningún campo de comentario) ni el frontend (el modal de "Marcar como perdida" en `detalle-cotizacion.component.html` es un único `<textarea>` "Motivo (opcional)").

Para referencia, el frontend propio de Nexo (`administracion_embarqa_nexo`, `mesa-costeo-page.component.ts`) sí tiene la lista de motivos predefinidos para perder una solicitud: `'Valor muy alto'`, `'Flete fuera de rango de plaza'`, `'Cliente desistió del servicio'`, `'Tiempo de respuesta vencido'`, `'Otro'` — más un comentario libre aparte. Cotifly podría alinearse a esa misma lista si se decide construir el campo separado.

**Falta decidir con negocio:** ¿vale la pena construir el motivo+comentario separados como se diseñó originalmente, o el texto libre actual es suficiente? Si se decide construirlo, requiere cambio de contrato (`SeguimientoWebHttp` necesita un campo `comentario` nuevo) y de UI (select de motivos + textarea de comentario).

**Resuelto, 2026-10-16 (HU-021-FE.11/#79 parte 3 + HU-021-FE.12/#83):** no hizo falta cambiar el contrato — `POST /web/cotizaciones/{folio}/resultado` (`ResultadoWeb`) ya recibía `resultado`/`motivo`/`comentario` separados desde antes (confirmado contra `openapi.yaml`/código de QA), solo que el frontend seguía usando el endpoint viejo `/seguimiento` (un solo campo `motivo_rechazo`) para TODAS las cotizaciones, incluidas las del flujo de Nexo, que ese endpoint rechaza con 409 `USAR_RESULTADO_NEXO` (bug real, también corregido). El frontend migró a `/resultado` y agregó el select de motivos (la misma lista de 5 opciones de Nexo que ya menciona este punto) + un textarea de comentario aparte. `motivo` se guarda en Cotifly y viaja a Nexo; `comentario` solo viaja a Nexo, no se persiste en Cotifly — tal como estaba diseñado acá.

## 9. "Exportar Excel" sigue sin construir — ni contrato ni endpoint

**Fecha:** 2026-10-05.

El botón "Exportar Excel" en "Ver detalle" (`detalle-cotizacion.component.html`) sigue deshabilitado con `title="Próximamente"` — es un placeholder desde que se construyó esa pantalla, sin ningún endpoint de backend todavía. Confirmado que el frontend propio de Nexo sí tiene este botón funcional en su "Mesa de Costeo" (junto a "Descargar PDF"), pero no se investigó a fondo qué exporta exactamente ni contra qué endpoint — falta esa investigación antes de poder construirlo del lado de Cotifly.

**Falta:** definir el contrato (qué información lleva el Excel — ¿la misma cotización que ya se descarga en PDF, un desglose por línea, algo más?) y construir el endpoint correspondiente en Cotifly.

## 10. Script `0009` y nueva instalación desde cero: qué tener en cuenta

**Fecha:** 2026-10-08. **Origen:** HU-021 (reestructuración de cotizaciones).

Se agregaron en `db/` (la misma carpeta de siempre: no se movió nada y no cambia ninguna ruta del backend, las pruebas ni el CI):

- `db/scripts/01_crear_esquema.sql`, `db/seeder/`, `db/ejecutar-bd.ps1` / `.sh` y `db/README.md`: instalación de la base desde cero.
- `db/migrations_mssql/0009_reestructuracion_cotizaciones.sql`: migración para la base que ya existe.

**Lo que hay que respetar:**

1. **No agregar el `0009` a las pruebas del backend** (Testcontainers) hasta que el backend esté adaptado a la nueva estructura (ver
   `docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md`): las pruebas de esquema compararían el modelo viejo contra el esquema
   nuevo y fallarían. Cuando el backend esté listo, las pruebas pueden aplicar `db/scripts/01_crear_esquema.sql` en vez de la cadena
   `0001` a `0009`.
2. **Cada migración nueva `000N` debe reflejarse también en `db/scripts/01_crear_esquema.sql`**, para que una instalación desde cero y
   una base migrada queden idénticas.
3. **La instalación desde cero se probó el 2026-10-08** en `dbTarifario_Nexo_Pruebas` (se borró y recreó el schema `dbo_cotifly`;
   20 tablas, 2 vistas, seeders sin errores). **Esa base ya tiene el esquema NUEVO y el backend actual no funciona contra ella**
   hasta adaptarlo. El respaldo de los datos anteriores (5 cotizaciones de prueba, usuario `dguerra`, sesiones, tokens) está en
   el schema `dbo_cotifly_respaldo_20261008` de la misma base. Los roles externos (`comercial` = 34, `gerente_comercial` = 31)
   se restauraron. El `0009` se probó el 2026-10-08 en un SQL Server de prueba (con datos) y quedó como **respaldo**: la base nueva se arma con `01_crear_esquema.sql`.
4. **`db/migrations/` (Alembic) y `alembic.ini` se borraron el 2026-10-08**: Postgres ya no se usa. El resto del stack viejo sigue en el repo (ver punto 12).

## 11. Referencias a documentos dentro de `backend-dotnet/` (la carpeta `docs/` se reorganizó)

**Fecha:** 2026-10-08.

El 2026-10-08 los documentos de `docs/` se separaron en carpetas (`docs_front/`, `database/`, `conexion_nexo/`, `backlog/`). Se actualizaron todas las referencias que están **fuera** de `backend-dotnet/`. Dentro de `backend-dotnet/` **no se tocó nada** (freeze del backend, y `clsCosteoNexo.cs` y otros archivos de Nexo tienen cambios sin commit): quedaron **28 archivos** con la ruta vieja. Ninguna rompe nada al compilar ni al correr; son comentarios, enlaces en documentos y el `openapi.yaml`, pero los enlaces ya no llevan al documento.

**Qué cambiar** (buscar y reemplazar; la ruta vieja siempre empieza con `docs/`):

| Ruta vieja | Ruta nueva |
|---|---|
| `docs/CONTRATO_API_WEB.md` | `docs/docs_front/CONTRATO_API_WEB.md` |
| `docs/GUIA_FRONTEND_COTIFLY.md` | `docs/docs_front/GUIA_FRONTEND_COTIFLY.md` |
| `docs/DICCIONARIO_BASE_DE_DATOS.md` | `docs/database/DICCIONARIO_BASE_DE_DATOS.md` |
| `docs/ESQUEMA_OBJETIVO_SQLSERVER.md` | `docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md` |
| `docs/PROPUESTA_REFACTOR_BD_COTIFLY.md` | `docs/database/PROPUESTA_REFACTOR_BD_COTIFLY.md` |
| `docs/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md` | `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md` |
| `docs/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md` | `docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md` |
| `docs/MAPEO_CAMPOS_NEXO_COTIFLY.md` | `docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md` |
| `docs/HU-005.1-mapeo-campos-nexo.md` | `docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md` |
| `docs/HU-011-formato-excel-licitacion.md` | `docs/conexion_nexo/HU-011-formato-excel-licitacion.md` |
| `docs/HU-013-preguntas-nexo.md` | `docs/conexion_nexo/HU-013-preguntas-nexo.md` |
| `docs/HU_Backlog_Cotifly_ClientePro.md` | `docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md` |
| `docs/HU_Sprint5_Cotifly.md` | `docs/backlog/2026-10-07_HU_Sprint5_Cotifly.md` |

**Dónde están** (28 archivos):
- `backend-dotnet/README.md` y `backend-dotnet/docs/` (`01-descripcion`, `02-arquitectura`, `03-infraestructura`, `07-api`, `09-estructura-del-proyecto`, `qa/regresion-manual`, `qa/paridad-python-dotnet`).
- `backend-dotnet/specs/openapi.yaml` (9 menciones), `specs/decisiones/` (`README`, `ADR-002`, `ADR-014`, `ADR-015`, `ADR-016`, `ADR-017`, `ADR-018`) y `specs/features/` (`chat_municipios`, `catalogos_formulario_costeo`).
- Comentarios en código: `clsCatalogosMaestrosEnCache.cs`, `clsCosteoNexo.cs`, `clsDtosLicitacionWeb.cs`, `EstadoCosteo.cs`, `clsLineaSolicitudCosteo.cs`, `clsCatalogosCaptura.cs`, `clsReSolicitudCosteoService.cs`, `clsResolvedorMunicipios.cs`, `clsValidadorLicitacion.cs`, y la prueba `clsValidadorLicitacionTests.cs`.

**Sugerencia:** hacerlo en el mismo commit en que se commitea el header (punto 4 / HU-014.3, parte A), que ya toca `clsCosteoNexo.cs`.

## 12. Al finalizar todo: evaluar y limpiar el código viejo que quedó sin uso

**Fecha:** 2026-10-08. **Origen:** hallazgo al borrar Alembic (HU-021.2).

Postgres ya no se usa, pero el stack viejo en Python y su infraestructura siguen en el repo. Borrar solo `db/migrations/` y `alembic.ini` dejó referencias colgando. **Cuando termine HU-021, hay que evaluar qué quedó viejo y sin usar y hacer una limpieza.**

**Candidatos (verificar antes de borrar que nada vigente los use):**
- **Python viejo:** `mcp_server/`, `orquestador/`, `shared/`, `tests/` (~90 archivos), `pyproject.toml`, `datos/`.
- **Alembic / Postgres que sigue referenciado:** `orquestador/Dockerfile` (copia `alembic.ini`: la imagen ya no construye), `scripts/entrypoint_orquestador.sh` (`alembic upgrade head`), `scripts/verificar_bd.ps1`, `scripts/backup_db.sh`, `tests/conftest.py`, `tests/test_db.py`.
- **Infraestructura de Postgres:** el servicio `bot-db` y el volumen `pgdata` en `deploy/docker-compose.swarm.yml`, `infra/stack.dokploy.yml`, `infra/stack.staging.yml`; los servicios `mcp` y `orquestador` de `docker-compose.yml`.
- **Documentos del stack viejo:** `BASE_DE_DATOS.md` (describe Postgres y Alembic), `docs/database/DICCIONARIO_BASE_DE_DATOS.md` (dice "PostgreSQL 16"), `docs/database/PROPUESTA_REFACTOR_BD_COTIFLY.md`, partes de `DEPLOY.md`.
- **Otros:** el `0009` si ya no hace falta como respaldo, y cualquier código del backend .NET que HU-021 deje sin llamadas.

**Cómo hacerlo:** primero un inventario (qué existe, quién lo usa, si está en algún ambiente), luego borrar por partes y comprobar que compila, que las pruebas pasan y que el despliegue sigue sano. La documentación se ordena después en **HU-022**.

## Sin confirmar / fuera de este documento

- El manejo del prefijo de dominio (`boterosoto\usuario`) en el mecanismo de regional (`clsDirectorioEjecutivosDimEjecutivo`,
  PR #83) se revisó de nuevo al redactar esto y **se ve correcto** (separa por la última `\`, valida el dominio,
  ignora filas de otro dominio) — no se incluye como pendiente porque no hay evidencia de que siga roto. Si en las
  pruebas reales aparece algún caso raro, se agrega aparte con el caso concreto.
- No se incluyó nada sobre el envío de correo (`Correo:Habilitado`) porque ese es un tema de configuración/credenciales
  del relay SMTP corporativo, no una corrección de código.

---

> **Documento original:** `2026-10-07_HU_Sprint5_Cotifly.md` · **Creado:** 2026-10-07

# Sprint 5 — Cotifly (HUs de backend y frontend)

> **Estado:** vigente (borrador del Sprint 5; la fuente de verdad son las milestones de GitHub) · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

> **Fecha de redacción:** 2026-10-07. Documento vivo: se va completando a medida que se van confirmando más hallazgos/pedidos para este sprint — no es la versión final todavía.
> **Fuente:** las notas de las pruebas en vivo (no versionadas: hallazgos encontrados probando en vivo contra `tbsdatacenter`/`dbTarifario_Nexo_Pruebas`, 2026-09-29 a 2026-10-05) + deuda técnica detectada aparte.
> **Convención:** sigue el mismo formato de `docs/archivo/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md` (Historia / Contexto / checklist con `[ ]`), numeración continúa desde HU-013 (la de `ADR-018`, ya construida) — estas son todas nuevas, HU-014 en adelante.

---

## Revisión del 2026-10-08: qué aplica después de la reestructuración de la base de datos

**El Sprint 5 va DESPUÉS de `docs/archivo/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md` (HU-021, "[Corrección con BD]").** El 2026-10-08 se recreó `dbo_cotifly` con el esquema nuevo (solicitud → rutas → camiones) y el backend actual no funciona contra él hasta terminar esa HU. Se revisó cada HU de abajo contra ese esquema:

| HU | Estado | Qué cambia | Depende de |
|---|---|---|---|
| 014.1 `estadoEnvio` sin `CHECK` | **Aplica, se absorbe en HU-021** | El `CHECK` entra al esquema (HU-021.2) y la tolerancia de filas malas se hace al reescribir la consulta (HU-021.4) | HU-021.2, 021.4 |
| 014.2 Cotización de prueba de Duban | **Ya no aplica** | La base compartida se borró y recreó; la pregunta perdió sentido. Pasa a "Descartado" | — |
| 014.3 Commitear el header `X-Portal-clsUsuario-Id` | **Aplica** | La lista creció a 6 archivos (se sumó el análisis de Excel) y siguen sin commit | — |
| 014.4 Retirar el borrador de métricas | **Aplica, coordinar** | Las vistas ya se adaptaron al esquema nuevo; si esto se hace primero, se quitan en vez de adaptarlas | HU-010, HU-021.10 |
| 015.1 Catálogo de tipo de vehículo / clase de viaje | **Aplica con cambios** | El comercial ya no escoge vehículo ni carrocería: salen del cubicador. Sigue haciendo falta la clase de viaje | HU-021.5, 021.6 |
| 015.2 Clase de contenedor | **Aplica** | Sin cambios: `claseContenedor` sigue en la ruta | HU-021.4 |
| 015.3 Servicios conexos | **Aplica con cambios** | Los conexos se guardan por ruta en una tabla (HU-021.8). Aquí queda enviar la selección a Nexo | HU-021.8 |
| 016.1 Listado regional | **Aplica** | El listado se construye sobre la estructura nueva | HU-021.4, 021.9 |
| 017.1 Margen promedio | **Aplica con cambios** | El margen ahora es por ruta; el promedio se calcula sobre rutas | HU-021.8, 021.10 |
| 018.1 Campos en "Ver detalle" | **Aplica, reformulada** | Origen, destino, peso y valor ya no están en la cabecera: son de cada ruta | HU-021.9, 021.11 |
| 018.2 Catálogo de referencia | **Aplica** | "Nivel línea" pasa a ruta y camión | HU-021.11 |
| 019.1 Ganada/perdida motivo + comentario | **Aplica, corregida** | Revisar a qué endpoint llama el frontend (ver la nota en la HU) | HU-021.13 |
| 020.1 Exportar Excel | **Aplica** | El contenido por ruta y camión | HU-021.9 |

---

## HU-014 — Deuda técnica y correcciones de backend

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-014.1] `estadoEnvio` sin `CHECK` en BD: un solo valor inválido tumba TODO "Mis cotizaciones" del usuario

**Labels:** `backend`, `bug`
**Síntoma:** `GET /web/mis-cotizaciones` respondió `500` para un usuario con una sola cotización con un valor fuera de catálogo (`estadoEnvio = 'pendiente'`, insertado por error en datos de prueba).

- [ ] **Causa:** `clsCotizacionRepository.ListarConLineaPorUsuarioAsync` (`src/Cotifly.Infraestructura/Cotizaciones/Repositories/clsCotizacionRepository.cs:109`) trae TODAS las cotizaciones del usuario en una sola consulta materializada. El mapeo de EF Core (`clsConvertidorCodigoEnum<TEnum>`) lanza `ArgumentException` sin capturar si el código no está en el enum (`EstadoEnvio`/`EstadoCosteo`) — la excepción no se maneja en ningún punto de la cadena, así que se propaga como `500` y bloquea la consulta completa para ese usuario, no solo la fila mala.
- [ ] **Por qué importa:** `estadoCosteo` ya tiene `CHECK` en BD desde el 0004 (`ck_cotizaciones_estadoCosteo`), pero `estadoEnvio` sigue sin ninguno — nada impide hoy que una fila quede fuera de `sin_enviar`/`enviada`/`reenviada` y tumbe el módulo para ese comercial.
- [ ] Corto plazo: agregar un `CHECK` para `estadoEnvio` igual al patrón de `ck_cotizaciones_estadoCosteo`. **Revisión 2026-10-08:** el esquema nuevo (`db/scripts/01_crear_esquema.sql`) tampoco lo trae. Se hace dentro de **HU-021.2**, en el script de instalación y en el `0009`; en la base compartida ya recreada, con un `ALTER` puntual.
- [ ] Mediano plazo: que `ListarConLineaPorUsuarioAsync` (o el converter) no deje que una fila con un código inesperado tumbe las demás — tolerar y loguear en vez de lanzar, o mapear fila por fila en vez de una proyección que materializa todo de una. **Revisión 2026-10-08:** esa consulta se reescribe en **HU-021.4**; hacer esto ahí.

### [HU-014.2] Confirmar contra qué base insertó Duban su cotización de prueba

**Labels:** `backend`, `investigacion`
> **Ya no aplica (2026-10-08).** La base compartida se borró y recreó con el esquema nuevo, y esa cotización ya no existe en ningún lado. Las pruebas de ahora en adelante se hacen con datos nuevos creados por el flujo nuevo. Se deja el texto original por trazabilidad.

- ~~La cotización de prueba que Duban insertó manualmente para probar "Mis cotizaciones" no apareció en `dbo_cotifly.Cotizaciones` de `tbsdatacenter`/`dbTarifario_Nexo_Pruebas` (la base real compartida) — confirmar contra qué servidor/base la insertó, para descartar una segunda base "de pruebas" desincronizada dando vueltas.~~

### [HU-014.3] Commitear como definitivo el ajuste del header `X-Portal-clsUsuario-Id`

**Labels:** `backend`, `tarea-tecnica`
**Decisión (2026-10-05):** Nexo no va a corregir el nombre del header de su lado (bug admitido en `CONTRATO.md §7`, discrepancia #1) — el ajuste queda **permanente** en Cotifly, no es un parche a revertir.

- [ ] El ajuste (`"X-Portal-Usuario-Id"` → `"X-Portal-clsUsuario-Id"`) ya está aplicado en el código de los adaptadores de Nexo y los comentarios ya no dicen "revertir cuando lo arreglen". **Son 6 archivos** (a 2026-10-08): `clsBuscadorClientesNexo.cs`, `clsCatalogosMaestrosNexo.cs` (antes `clsCatalogosFleteNexo.cs`), `clsCosteoNexo.cs` (dos sitios), `clsCubicacionNexo.cs`, `clsMunicipiosNexo.cs` y `clsAnalizadorExcelNexo.cs`.
- [ ] **Falta:** commitear estos cambios como definitivos — hoy siguen sin commitear en la rama de trabajo (se dejaron fuera del commit de la reestructuración a propósito, para que los commitee Duban). Ojo con `clsCosteoNexo.cs`: HU-021.7 lo reescribe; conviene commitear esto **antes** para no mezclar los dos cambios.
- [ ] Cuando el cubicador entre (HU-021.5), su adaptador **no** usa este header: autentica con `Authorization: Bearer <CUBICADOR_API_KEY>`.

### [HU-014.4] Retirar el borrador obsoleto del tablero de métricas (`/actividad`, `/ajustes-margen`)

**Labels:** `backend`, `tarea-tecnica`, `deuda-tecnica`
**Depende de que HU-010 (tablero real, `/web/metricas/comercial` y `/regional`) esté validado en producción sin depender de esto.**

`GET /web/metricas/actividad` y `GET /web/metricas/ajustes-margen` fueron el primer borrador del tablero, antes del diseño final de HU-010.1. Ya están marcados `(obsoleto)` en el propio código (`clsMetricasController.cs`) y `clientepro-frontend` no los consume en ningún lado (confirmado por búsqueda en el repo).

- [ ] Confirmar que ningún consumidor externo (Postman guardado, script, otro servicio) sigue llamando estos dos endpoints.
- [ ] Retirar los endpoints `Actividad`/`AjustesMargen` de `clsMetricasController.cs`.
- [ ] Retirar `ListarActividadAsync`/`ListarAjustesMargenAsync` de `IClsMetricasRepository`/`clsMetricasRepository.cs`, y los DTOs `clsActividadComercial`/`clsAjustesMargenComercial`.
- [ ] Retirar las vistas `vActividadPorComercial` (creada en 0001, redefinida en 0003) y `vAjustesMargenPorComercial` (creada en 0006) con un script `000N_retirar_vistas_actividad.sql` — no destructivo sobre datos (son vistas). **Revisión 2026-10-08:** las dos vistas ya se redefinieron en `0009` y en `01_crear_esquema.sql` para el esquema nuevo (`vAjustesMargenPorComercial` saca la línea de negocio de la ruta del ajuste). Si esta HU se hace **antes** de HU-021.10, se quitan de ambos scripts en vez de adaptarlas; si se hace después, el script de retiro cubre también la instalación desde cero.
- [ ] Actualizar `docs/docs_front/CONTRATO_API_WEB.md`/`specs/openapi.yaml` quitando ambas rutas.

---

## HU-015 — Catálogos y servicios conexos: usar las fuentes correctas de Nexo

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)
> **Contexto general:** revisando qué le falta a Cotifly para que toda la información del formulario de "Nueva cotización" se cree correctamente en las tablas de Nexo (no solo se calcule/muestre localmente), se comparó `CONTRATO.md` de Nexo (incluido lo nuevo bajo `/api/catalogos/*`) contra lo que Cotifly implementaba hasta el 2026-10-05.

### [HU-015.1] Migrar tipo de vehículo / clase de viaje al catálogo maestro de Nexo

**Labels:** `backend`
- [ ] Cotifly los traía de `GET /api/biblioteca-sap-historico/filtros` (pensado para filtrar histórico de tarifas, no un catálogo maestro) en vez de `GET /api/catalogos/tipos-vehiculo` y `GET /api/catalogos/clases-viaje`.
- [ ] **Nota 2026-10-07:** el pull de QA de esa fecha ya trajo la corrección (`clsCatalogosMaestrosNexo.cs`/`clsCatalogosFleteNexo.cs` reescritos, HU-013.10 #106) — **verificar si esta issue ya quedó resuelta** antes de abrirla, puede que solo falte confirmar en QA.
- [ ] Confirmar con Duban si existe un catálogo maestro equivalente para **carrocería**, o si ese campo se queda en `biblioteca-sap-historico/filtros` por ahora.
- [ ] **Revisión 2026-10-08 — cambia el alcance.** Con el cubicador (HU-021.5) el comercial **ya no escoge** tipo de vehículo ni carrocería: salen de la recomendación y se guardan por camión. Entonces este catálogo solo sigue haciendo falta para la **clase de viaje** (que sí es de la ruta) y, si se deja, para que el admin de fletes corrija un camión que quedó sin vehículo. Reformular esta issue a "clase de viaje contra `GET /api/catalogos/clases-viaje`" y dejar vehículo y carrocería para el acuerdo de códigos entre Nexo y el cubicador (en Nexo el vehículo es una sola combinación del catálogo: tipo + carrocería + rango de peso, `tbl_configuraciones_vehiculo`).

### [HU-015.2] Conectar clase de contenedor al catálogo real de Nexo

**Labels:** `backend`
- [ ] Cotifly usa un catálogo **hardcodeado propio** (`ClaseContenedor.cs` en el backend, `clase-contenedor.catalogo.ts` en el frontend) — nunca llama a `GET /api/catalogos/clases-contenedor`. Si Nexo agrega/renombra/retira un valor, Cotifly queda desincronizado sin enterarse.

### [HU-015.3] Servicios conexos: catálogo disponible + enviar la selección del comercial a Nexo

**Labels:** `backend`
**La más importante de este grupo.** `clsCosteoNexo.cs` solo **lee** los conexos que ya traiga la cotización (`LeerCotizacion`, a partir de `POST /api/cotizaciones`), pero Cotifly no tiene forma de:

- [ ] Traer el catálogo de servicios conexos disponibles — `GET /api/servicios-conexos` (paginado, `Roles: Todos`).
- [ ] Enviar la selección del comercial de vuelta a Nexo — `POST /api/cotizaciones/{id}/conexos` (`Roles: Comercial, CoordinadorFletes, Admin`; body `{ items: [{ servicioConexoId, cantidad?, valorAplicado? }] }`).
- [ ] Sin esto, la elección del comercial se calcula/guarda solo localmente — la cotización real en las tablas de Nexo queda sin esos conexos (afecta reportes/biblioteca de tarifas ganadas/auditoría del lado de Nexo).
- [ ] **Nota 2026-10-07:** el pull de esa fecha trajo `clsServiciosConexosNexo.cs`/`clsServiciosConexosController.cs` nuevos (HU-013.3) — **verificar si esta issue ya quedó resuelta** antes de abrirla.
- [ ] **Revisión 2026-10-08:** cómo se **guardan** los conexos en Cotifly cambia: dejan de ser un JSON en la cabecera y la línea y pasan a una tabla por **ruta**, con su cantidad (HU-021.8). Eso no es parte de esta issue. Aquí queda lo que ya estaba: traer el catálogo y **enviar la selección a Nexo**. Como Nexo guarda los conexos pegados a una línea (que para él es un camión), los de una ruta se envían pegados a la línea del primer camión de esa ruta.

### [HU-015.4] (nota, no issue) Línea de negocio — confirmar si conviene pasar a catálogo en vivo

Esto está bien resuelto hoy: Cotifly usa su propio catálogo (`MargenesPolitica`) con un mapeo manual al código de Nexo (`LineaNegocioNexo`). Vale la pena preguntarle a Duban si ahora conviene consumir `GET /api/catalogos/lineas-negocio` en vivo en vez de mantener el mapeo a mano — no es un bug, es mantenimiento.

---

## HU-016 — Listado de cotizaciones por regional (Gerente Comercial)

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-016.1] Endpoint `GET /web/cotizaciones/regional`

**Labels:** `backend`
La vista de Gerente Comercial "Todas las cotizaciones" (frontend) necesita un listado fila por fila de las cotizaciones de los comerciales de su regional, pero hoy no existe:

- [ ] `GET /web/mis-cotizaciones` solo trae las del propio usuario (`clsCotizacionesController.cs:22`).
- [ ] `GET /web/metricas/regional` es **agregado** (`clsMetricasRegional`), no un listado fila por fila.
- [ ] Construir `GET /web/cotizaciones/regional` (mismo patrón de nombre y política `GerenteComercial` que ya usa `/web/metricas/regional`).
- [ ] Reusar `clsRegionalUsuarioService.ListarMiembrosAsync(regional)` (ya la usa el tablero de métricas) + un método nuevo en `IClsCotizacionRepository` (`ListarPorUsuariosAsync(IReadOnlyCollection<Guid> usuarioIds, ...)`) que filtre `WHERE usuarioId IN (...)` — no reconstruir la resolución de regional desde cero.
- [ ] **Revisión 2026-10-08:** se construye **después** de HU-021.4 y 021.9, sobre el repositorio y el resumen nuevos. La línea de negocio de una fila sale de la ruta 1 de la solicitud (o "Varias"), y el precio es el `precioFinal` de la cabecera.

---

## HU-017 — "Margen promedio" en Mis Cotizaciones: redefinir contra qué se compara

> **Repos:** `Cotifly` + `clientepro-frontend` · **Pendiente de decisión de negocio antes de construir**

### [HU-017.1] Decidir y construir la comparación correcta de margen

**Labels:** `backend`, `producto`
La card "Margen promedio" (Comercial) muestra un "± desviación" que casi siempre sale vacío (`—`). No es un bug de datos faltantes — es el comportamiento diseñado:

- [ ] `DesviacionPromedioMargen` (`clsCalculadoraMetricas.cs`) promedia `MargenAjustes.diferencia`, tabla que solo se escribe cuando el comercial **negocia explícitamente** un margen distinto (documentado así a propósito en ADR-016). Si el comercial siempre acepta el margen propuesto, la tabla queda vacía para él.
- [ ] **Lo que negocio quiere (pedido explícito de Sara, 2026-10-05):** comparar el margen aplicado promedio contra el **promedio de los márgenes de política de todas las líneas** (`MargenesPolitica.margen`), no contra los ajustes negociados. Verde si está por encima, rojo si está por debajo.
- [ ] **Opción A (más segura):** endpoint nuevo accesible para Comercial que liste `MargenesPolitica` (línea, margen) de las líneas activas, para que el frontend calcule el promedio/desviación. Hoy el único que expone esto (`GET /web/admin/lineas-negocio`) está protegido con política `GerenteComercial` — no hay ADR que declare esto confidencial para Comercial (de hecho ya se filtra uno por uno vía el chat, herramienta `obtener_margen`); el bloqueo actual parece ser de alcance administrativo, no de confidencialidad.
- [ ] **Opción B (más riesgosa):** cambiar la definición de `DesviacionPromedioMargen` en el backend — ADR-016 ya la define a propósito como "solo ajustes negociados", podría afectar otros reportes que dependen de esa definición.
- [ ] Decidir con negocio cuál opción tomar antes de construir.
- [ ] **Revisión 2026-10-08:** el margen aplicado ahora es **por ruta** (`CotizacionLineasSolicitud.margenAplicado`) y los ajustes también (`MargenAjustes.lineaSolicitudId`). "Margen aplicado promedio" de un comercial se calcula sobre sus rutas, no sobre sus cotizaciones. Con la Opción B, recordar que `DesviacionPromedioMargen` pasa a leer por ruta (HU-021.10).

---

## HU-018 — Enriquecer "Ver detalle" de una cotización

> **Repos:** `Cotifly` (018.1) + `clientepro-frontend` (consumo) · **Contexto:** se revisó `administracion_embarqa_nexo` (página `mesa-costeo`) como referencia de lo que el negocio probablemente espera ver.

### [HU-018.1] Ganancia rápida: exponer 4 campos que ya existen en BD pero el DTO no los expone

**Labels:** `backend`
`GET /web/mis-cotizaciones` (y "Ver detalle", que reusa la misma fila) usa `clsCotizacionResumen`, que **no incluye** `deadline`, `origen`, `destino`, `pesoKg` ni `valorMercancia` — aunque ya existen en `clsCotizacion` (desde el 0004) y **ya se exponen** en `GET /web/cotizaciones/{id}/nueva-version` → `clsDatosNuevaVersion`. El dato ya está, solo falta copiarlo al DTO de la lista/detalle.

- [ ] Agregar esos 4 campos a `clsCotizacionResumen` (o al contrato `CotizacionResumen`) — alimenta de una vez `/web/mis-cotizaciones`, `/seguimiento`, `/resultado`, `/web/solicitudes-costeo` y `/costeo/reintentar`.
- [ ] **Reformulada el 2026-10-08.** Con el esquema nuevo, `origen`, `destino`, `pesoKg` y `valorMercancia` **ya no están en la cabecera**: son de cada **ruta** (origen y destino como id de municipio, que hay que resolver a nombre con el catálogo de Nexo). Solo `deadline` sigue en la cabecera. Lo que se expone es: `deadline` + un resumen de las rutas (origen → destino, peso, valor de la mercancía, y los camiones que lleva cada una). Se construye junto con el resumen nuevo de **HU-021.9** y el contrato de la API de **HU-021.11**; no tiene sentido hacerlo antes, porque el DTO se reescribe. La parte del frontend ("Ver detalle") se redacta aparte.
- [ ] `scoreUrgencia` sigue genuinamente bloqueado (no existe en el backend, pendiente del contrato de Nexo para consultarlo por solicitud, ver ADR-016). Nota aparte: Nexo sí devuelve `scorePrioridad` al **crear** la solicitud (`POST /api/captura-solicitudes` → `CapturaResultDto`), pero Cotifly lo descarta hoy (`clsSolicitudCreadaNexo`) — capturarlo ahí sería una ganancia más chica y ya desbloqueada, distinta de "consultarlo después por solicitud".

### [HU-018.2] (catálogo de referencia, no construir todo de una — priorizar con negocio)

Lo que Nexo ya muestra en su "Mesa de Costeo" que Cotifly no muestra:

- **Nivel solicitud:** cliente, tipo, modalidad, deadline, valor estimado, probabilidad de cierre, `scorePrioridad`, estado, fecha de captura, notas.
- **Nivel línea:** ruta, tipología, peso, tipo de vehículo, carrocería, posiciones — Cotifly ya lo captura, el detalle no lo vuelve a mostrar. **(2026-10-08)** En el esquema nuevo esto se reparte: la **ruta** tiene origen, destino, tipología, peso y mercancías; el **camión** tiene vehículo, carrocería y los costos de Nexo.
- **Transparencia del costeo (lo más rico, lo que más se aleja de Cotifly hoy):** piso SICE-TAC desglosado, 3 fuentes candidatas lado a lado (SICE-TAC/plaza/histórico SAP, cada una con detalle y coincidencias), propuesta final con justificación en texto, histórico de tarifas de la ruta con vigencia. Cotifly hoy solo recibe un precio final — **falta confirmar con Duban si Nexo ya manda este desglose y se descarta, o si hay que pedirlo aparte.**
- **Servicios conexos:** lista con etiqueta de alcance (General/en N de M líneas/Individual) — cubierto por HU-015.3.
- **Cubicaje/vehículo sugerido:** un resumen compacto basta (el desglose 3D es herramienta de trabajo aparte).
- **Auditoría de quién hizo qué:** tampoco existe en el propio Nexo — si se quiere en Cotifly, no hay patrón que copiar, hay que diseñarlo desde cero.

**Prioridad sugerida:** 018.1 primero (bajo costo, ya resuelto el "por qué"); el desglose de costeo depende de confirmar si Nexo ya manda esos datos; el resto es telón de fondo para cuando negocio priorice.

---

## HU-019 — Ganada/Perdida: motivo + comentario separados (como pedía la HU original)

> **Repos:** `Cotifly` + `clientepro-frontend` · **Pendiente de decisión de negocio**

### [HU-019.1] Separar motivo (lista predefinida) de comentario (texto libre)

**Labels:** `backend`, `frontend`, `producto`
`POST /web/cotizaciones/{folio}/seguimiento` ya funciona, pero el cuerpo es `{ ganada, motivo_rechazo }` — un solo campo de texto libre.

> **Corrección 2026-10-08 — verificar antes de construir.** Revisando el backend, para las cotizaciones **del flujo de Nexo** `/seguimiento` las rechaza con `USAR_RESULTADO_NEXO` (`clsSeguimientoService.cs:46-52`) y el camino correcto es `POST /web/cotizaciones/{folio}/resultado` (`clsResultadoCotizacionService.cs`), que primero llama a Nexo y solo guarda local si Nexo responde 200. El frontend marca ganada/perdida con `/seguimiento` (`detalle-cotizacion.component.ts`). **Confirmar a cuál llama de verdad:** si es `/seguimiento`, el botón ya falla para las cotizaciones del flujo de Nexo y eso es un bug aparte, anterior a esta HU. Esta HU debe construirse sobre `/resultado`. Además, `motivoRechazo` debe quedar en "No aplica" cuando se marca ganada (regla de la propuesta original, pendiente en HU-021.13). La HU-004.5 original pedía 2 campos: **motivo** (lista predefinida, se persiste en Cotifly y se envía a Nexo) y **comentario** (texto libre, solo a Nexo). Nunca se terminó de construir — ni el contrato (`SeguimientoWebHttp` solo tiene `motivo_rechazo`) ni el frontend (`detalle-cotizacion.component.html` es un único `<textarea>`).

- [ ] Referencia: el frontend de Nexo (`mesa-costeo-page.component.ts`) ya tiene la lista de motivos predefinidos para perder: `Valor muy alto`, `Flete fuera de rango de plaza`, `Cliente desistió del servicio`, `Tiempo de respuesta vencido`, `Otro` — más un comentario libre aparte. Cotifly podría alinearse a esta misma lista.
- [ ] **Decidir con negocio:** ¿vale la pena construir motivo+comentario separados, o el texto libre actual es suficiente?
- [ ] Si se decide construir: agregar `comentario` a `SeguimientoWebHttp` (backend) + select de motivos + textarea de comentario (frontend).

---

## HU-020 — "Exportar Excel" en Ver Detalle

> **Repos:** `Cotifly` + `clientepro-frontend`

### [HU-020.1] Investigar y construir la exportación a Excel

**Labels:** `backend`, `frontend`, `investigacion`
El botón "Exportar Excel" en "Ver detalle" sigue deshabilitado (`title="Próximamente"`), placeholder desde que se construyó la pantalla — sin endpoint de backend. El frontend de Nexo sí tiene este botón funcional en su "Mesa de Costeo" (junto a "Descargar PDF"), pero no se investigó a fondo qué exporta ni contra qué endpoint.

- [ ] Investigar qué exporta exactamente Nexo y contra qué endpoint, antes de construir el lado de Cotifly.
- [ ] Definir el contrato: ¿la misma info del PDF, un desglose por línea, algo más?
- [ ] Construir el endpoint correspondiente en Cotifly + el consumo en frontend.
- [ ] **Revisión 2026-10-08:** el desglose "por línea" pasa a ser **por ruta, con sus camiones**. Ya existe una exportación de licitaciones (`clsLicitacionService`, que HU-021.6 rehace para el esquema nuevo): revisar si esa se reutiliza antes de construir una nueva.

---

## HU-022 — Cierre del proyecto: limpieza y documentación profesional

> **Repo:** `Cotifly` · **Asignado a:** Sara (Datos) + Duban (Backend) · **Va al final de todo el Sprint 5**, después de todas las HU anteriores (HU-021 incluida).

### [HU-022.1] Limpiar el código viejo y dejar la documentación profesional

**Contexto:** al borrar Alembic (HU-021.2) apareció que el stack viejo en Python y su infraestructura de Postgres siguen en el repo sin usarse (hallazgo en las notas de las pruebas en vivo). Además, buena parte de la documentación describe ese stack o el esquema anterior. HU-021.14 pone los documentos al día con el esquema nuevo; esta HU los deja **profesionales** y quita lo que sobra.

- [ ] **Evaluar qué quedó viejo y sin uso** (Python viejo, Alembic, Postgres, servicios de compose, scripts y pruebas del stack viejo) con un inventario: qué existe, quién lo usa y si está en algún ambiente.
- [ ] **Borrar por partes** y comprobar después de cada una que el backend compila, las pruebas pasan y el despliegue sigue sano.
- [ ] **Inventario de documentos** (`docs/`, `backend-dotnet/docs/`, `specs/`, `README.md`, `BASE_DE_DATOS.md`, `DEPLOY.md`, `infra/`, `db/README.md`): marcar cada uno como *vigente*, *por corregir* o *obsoleto*.
- [ ] **Borrar lo obsoleto**: documentos del stack viejo o propuestas ya ejecutadas y superadas. Antes de borrar, confirmar que nada vigente los enlaza.
- [ ] **Un solo lugar por tema** (esquema, API, despliegue, integración con Nexo y el cubicador, backlog) y un índice claro en `docs/README.md` y en el `README.md` de la raíz.
- [ ] **Estilo uniforme**: mismos encabezados, fecha y dueño, idioma y tono; sin texto de trabajo, tachados ni notas de sesión.
- [ ] **HU y notas de trabajo** (`docs/backlog/`): dejar las vigentes y archivar o borrar las cerradas.
- [ ] Revisar que **todos los enlaces y rutas** funcionen (incluye las 28 referencias de `backend-dotnet/`, punto 11 de los pendientes de Duban).
- [ ] Revisión final por alguien que **no** haya escrito el documento: ¿se entiende sin contexto?

---

## Sin incluir / descartado

- El manejo del prefijo de dominio (`boterosoto\usuario`) en el mecanismo de regional (`clsDirectorioEjecutivosDimEjecutivo`, PR #83) se revisó y **se ve correcto** — no se incluye como pendiente salvo que aparezca un caso raro concreto en pruebas reales.
- Nada sobre el envío de correo (`Correo:Habilitado`) — es config/credenciales del relay SMTP corporativo, no una corrección de código.

- **HU-014.2** (la cotización de prueba de Duban en la base compartida): ya no aplica desde el 2026-10-08, ver la tabla de arriba.

## Orden sugerido para el Sprint 5

1. **Primero HU-021 ([Corrección con BD])**, completa o al menos hasta HU-021.4: sin eso no hay backend funcionando.
2. HU-014.3 (commitear el header) **antes** de HU-021.7, para no mezclar dos cambios en `clsCosteoNexo.cs`.
3. HU-014.1 y 014.4, que ya están absorbidas o coordinadas con HU-021.
4. HU-015.1, 015.2 y 015.3, sobre la estructura nueva.
5. HU-016, 017, 018, 019 y 020, que dependen de los DTOs y el resumen nuevos (HU-021.9 y 021.11). HU-019.1 y la verificación de `/seguimiento` contra `/resultado` se pueden adelantar: son independientes de la estructura.

6. **HU-022 al final de todo**: la limpieza del código viejo y la documentación profesional se hacen cuando el resto esté terminado.

## Pendiente de agregar

- Los acuerdos con el equipo del cubicador y con Nexo (campo de ruta, códigos de vehículo, `TrqType` de `DevolucionContenedor`) están en `docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md` (lo que se le pide a Nexo y al cubicador; no son issues) y, para el `TrqType`, en HU-021.2 de `docs/archivo/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md`.

---

> **Documento original:** `2026-10-14_HU_Correcciones_Nuevas_Cotifly.md` · **Creado:** 2026-10-14

# Correcciones nuevas — Cotifly

**Fecha:** 2026-10-14 · **Última revisión:** 2026-10-16, contra `origin/QA` (hasta #177) y la base compartida · **Repo de los issues:** `Cotifly`.

**Qué es este documento:** lo que quedó pendiente después de aplicar los scripts de base de datos en la base compartida, de cerrar el contrato de captura con NEXO y de traer la integración final de HU-023 (#175 y #177). **Está ordenado de lo más urgente a lo menos urgente.** Al final está el registro de lo que ya se resolvió o ya no hace falta.

Roles: **Duban** = Backend, **Sara** = Datos, **Kevin** = QA.

| Prioridad | Qué significa |
|---|---|
| 🔴 **1. Urgente** | Bloquea pruebas o datos hoy |
| 🟠 **2. Alta** | Esta semana; afecta funciones que ya se están construyendo |
| 🟡 **3. Media** | Con dependencias o coordinado con otros equipos |
| ⚪ **4. Baja** | Orden y deuda técnica, al final del sprint |

---

## 🔴 1. Urgente

### 1.1 CORS no permite `If-Match` ni `Idempotency-Key` desde otro origen — Duban
- [ ] **Sigue abierto al 2026-10-16:** `EncabezadosCors` no cambió en `origin/QA` y no hay `WithExposedHeaders`.

Confirmado el 2026-10-16 con una petición real al backend local (`localhost:8083`):
```
curl -i -X OPTIONS "http://localhost:8083/web/cotizaciones/TEST/costeo/servicios-conexos" -H "Origin: http://localhost:4200" -H "Access-Control-Request-Method: PUT" -H "Access-Control-Request-Headers: If-Match, Content-Type"

HTTP/1.1 204 No Content
Access-Control-Allow-Headers: Authorization,Content-Type
Access-Control-Allow-Methods: GET,POST,PUT
Access-Control-Allow-Origin: http://localhost:4200
```
- **Causa:** `Access-Control-Allow-Headers` no incluye `If-Match`. Viene de `EncabezadosCors = ["Authorization", "Content-Type"]` (`src/Cotifly.Api/Comun/clsSeguridadHttp.cs:145`). La política tampoco tiene `.WithExposedHeaders(...)`.
- **Efecto:** el navegador bloquea en la verificación previa (preflight) toda petición con `If-Match` o `Idempotency-Key`. El servidor la procesaría bien, pero nunca le llega.
- **Qué afecta:** toda mutación con concurrencia optimista o idempotencia: agregar o quitar servicios conexos (`POST`/`PUT .../costeo/servicios-conexos`), ajuste de margen (`PUT .../costeo/margen`), nueva versión (`POST .../costeo/re-solicitar`) y la captura con `Idempotency-Key` (`POST /web/solicitudes-costeo`, #173).
- **Hoy ya bloquea las pruebas manuales en el navegador:** en local, el frontend llama directo a `http://localhost:8083` (`COTIFLY_BASE_URL`), sin proxy. En producción, el host (`COTIFLY_HOST`) todavía no está definido; si queda en otro origen, también bloquea.
- **Arreglo sugerido:** agregar `If-Match` e `Idempotency-Key` a `EncabezadosCors`. Opcional: `.WithExposedHeaders("ETag")`; el frontend ya lee `row_version` del cuerpo para no depender del encabezado.

### 1.2 Aplicar el `07` (motivo `BUSQUEDA_AGOTADA`) en la base compartida — Sara
- [ ] Traer `QA` (#176 trae `db/scripts/07_motivo_busqueda_agotada.sql`, de Duban).
- [ ] Borrar el `db/scripts/pendientes/07_motivo_busqueda_agotada.sql` local de Sara (sin commit). Sobra: el que se usa es el de Duban, que aplica la misma regla (5 motivos y conserva el `ISNULL` del `03`).
- [ ] Correrlo en la compartida: `sqlcmd -S 10.10.40.85,1433 -d dbTarifario_Nexo_Pruebas -E -C -I -b -f 65001 -i db/scripts/07_motivo_busqueda_agotada.sql`. Hoy el CHECK no admite `BUSQUEDA_AGOTADA`, y no hay filas que lo bloqueen.
- **Por qué es urgente:** sin el `07` el backend no falla, pero guarda el motivo en NULL y deja el real solo como advertencia de la ruta (`MOTIVO_SIN_SELECCION`). Con el `07`, guarda el motivo real.

---

## 🟠 2. Alta

### 2.1 `Captura:AceptarVehiculoCliente` apagada: confirmar el valor de los ambientes — Duban
- [ ] **Sigue abierto al 2026-10-16:** no aparece en ningún `appsettings` de `origin/QA`; solo comentada en `.env.example` (`# Captura__AceptarVehiculoCliente=false`).
- Con la bandera apagada, el backend ignora `tipo_vehiculo` y `carroceria` que manda el cliente: el cubicador planifica los camiones o quedan "sin vehículo" para la Mesa de Costeo. Por eso, la pantalla del frontend para digitar el vehículo, que es el respaldo de HU-021-FE.2 (#70), no tiene efecto.
- **Duban:** confirmar si algún ambiente desplegado la prende por variable de entorno, y decidir si se prende mientras el cubicador real no esté conectado.
- Relacionadas, también apagadas por defecto desde #175: `Nexo__EnviarCodigosCubicador` y `Nexo__EnviarGrupoRutaId`.

### 2.2 HU-010.5 — Tablero del gerente: filtro por año y mes — Duban

> **Issue nueva dentro de la milestone HU-010** (Rol Gerente Comercial y tablero de métricas). HU-010.1 a 010.4 ya existen y no se modifican. Es la parte de backend que necesita el selector año/mes del tablero del gerente (HU-010.3, clientepro-frontend #51).

#### [HU-010.5] [Backend] `anio` y `mes` en `GET /web/metricas/regional`
**Labels:** `backend`, `metricas`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-010 (milestone). La consume HU-010.3 (frontend, #51).**

> **Estado (2026-10-16): escrito y guardado como parche; no está en ninguna rama.** `origin/QA` todavía no tiene `anio` ni `mes` en `/web/metricas/regional`. El cambio se escribió en la copia local de Sara (9 archivos: unas 45 líneas de código, 90 de pruebas y 50 de documentación) y se retiró de esa copia el 2026-10-16 para traer la integración final de HU-023 (PR #175).
>
> Queda en **`docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`**, hecho sobre `QA` en el commit `cf744ff`. Para usarlo: crear una rama desde `QA` y correr `git apply --3way docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`. Se comprobó el 2026-10-16 que aplica limpio sobre `QA` con #175 (`git apply --check`). **Duban lo revisa antes de subirlo** (freeze del backend).

**Qué hace:** dos parámetros opcionales en `GET /web/metricas/regional`, como atajo del periodo para el selector año/mes del frontend, en vez de `desde`/`hasta`:
- `?anio=2026` → el año completo (1 de enero a 31 de diciembre).
- `?anio=2026&mes=10` → ese mes completo.
- Si llega `anio`, reemplaza a `desde`/`hasta`; no se combinan.
- Sin `anio` ni `mes` → igual que antes (año en curso hasta hoy).
- **400** si `mes` no está entre 1 y 12, si llega `mes` sin `anio` o si el año no es válido.
- `/web/metricas/comercial` no cambia.

**Archivos del cambio** (rutas relativas a `backend-dotnet/`):

| Tipo | Archivo |
|---|---|
| Código | `src/Cotifly.Api/Metricas/Controllers/clsMetricasController.cs` (2 parámetros nuevos en el endpoint) |
| Código | `src/Cotifly.Aplicacion/Metricas/DTOs/clsTableroMetricas.cs` (`clsConsultaMetricas` con `Anio` y `Mes`) |
| Código | `src/Cotifly.Aplicacion/Metricas/Services/clsTableroMetricasService.cs` (`ResolverPeriodo` con las validaciones) |
| Pruebas | `tests/Cotifly.Aplicacion.Tests/Web/clsTableroMetricasServiceTests.cs` |
| Pruebas | `tests/Cotifly.Api.Tests/Web/clsMetricasTableroEndpointsTests.cs` |
| Pruebas | `tests/Cotifly.Aceptacion.Tests/Web/clsPasosMetricas.cs` |
| Contrato | `specs/openapi.yaml` (parámetros `anio` y `mes`) |
| Contrato | `specs/features/metricas_tablero.feature` (3 escenarios nuevos) |
| Documentación | `docs/07-api.md` |

**Checklist:**
- [ ] Duban revisa el cambio y confirma que se sube pese al freeze.
- [ ] Rama propia desde `QA` con el parche aplicado (`git apply --3way …`).
- [ ] Correr las pruebas: unitarias, de API y de aceptación (`metricas_tablero.feature`).
- [ ] Revisar con ADR-016 §5 (definición del periodo) si hace falta anotar la extensión.
- [ ] PR a `QA` con `Part of HU-010`, y avisarle al frontend (#51) que ya puede mandar `anio` y `mes`.

**Criterios de aceptación:**
```gherkin
Escenario: Año completo
Dado un gerente comercial con regional
Cuando consulta /web/metricas/regional?anio=2025
Entonces el periodo aplicado va del 2025-01-01 al 2025-12-31

Escenario: Mes de un año
Cuando consulta /web/metricas/regional?anio=2025&mes=2
Entonces el periodo aplicado va del 2025-02-01 al 2025-02-28

Escenario: Parámetros inválidos
Cuando consulta con mes=5 sin anio, o con anio=2026&mes=13
Entonces responde 400 con un ErrorWeb

Escenario: Sin los parámetros nuevos
Cuando consulta sin anio ni mes
Entonces el comportamiento es el de antes
```

### 2.4 NEXO: confirmar el recorte de la #297 y el estado del portal — coordinación
- [ ] **Confirmar en GitHub que NEXO#297 (S5-HU-20.5) quedó recortada.** Según el chat de NEXO, quedó en dos puntos: (a) devolver `NumeroLinea` en `GET /api/solicitudes/{id}` y ordenar por él; (b) que una línea de Cupo sin vehículo no se autocostee. La normalización de tildes queda opcional, porque la cuarentena ya evita los duplicados oficiales. Las 18.1 a 18.4 ya están hechas en `origin/QA` de NEXO (commits `1ba4631` y `695e0b6`, 13-oct).
- [ ] **Confirmar que sigan abiertas en el portal (`administracion_embarqa_nexo`) las issues de S5-HU-19.3 y 19.4.** Verificado en sus ramas remotas el 2026-10-14:
  - No existe la bandeja de normalización de vehículos en cuarentena (S5-HU-19.3). El commit `b6f7070` (#288) dice explícitamente "sin bandeja de normalización".
  - No hay rastro de la S5-HU-19.4 (editar el vehículo de una línea y guardar el precio sin vehículo).
  - Sin esas dos, una línea "sin selección" del cubicador, o con un vehículo en cuarentena, puede quedar sin forma de resolverse en la Mesa. Mientras tanto, la bandera de los códigos del cubicador de Cotifly sigue apagada.
- La rama del portal `289-mesa-costeo-agrupar-por-ruta` (commit `ac528d1`) ya agrupa la Mesa por `grupoRutaId`, pero todavía no está fusionada en el QA del frontend. El orden dentro de cada grupo depende de NEXO#297.

---

## 🟡 3. Media

### 3.1 Rol `comercial` de 34 a 32 y cambio de la base de identidad — coordinado
- [ ] **No es solo de base de datos.** Va junto con el cambio de la base de identidad a `dbSolucionesTI_Prueba` (#172), y las tres partes se hacen en una sola ventana:

| Parte | Qué cambia | Quién |
|---|---|---|
| Base de datos | `Roles.idRolExterno` del rol `comercial`: de 34 a 32 (`db/scripts/operacion/corregir_rol_comercial_32.sql`, #173) | DBA, con aprobación |
| Configuración del backend | Que cada ambiente apunte a `dbSolucionesTI_Prueba` (`ConnectionStrings__Identidad`) y, en la misma ventana, correr `db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql` con la API detenida | Duban o quien despliega, y el DBA |
| Frontend | `cotifly-roles.ts` reconoce como comercial el 34 (y el 25 en pruebas): pasar a 32 y revisar qué es el 25 en la base nueva | Frontend (Sara) |

- **Por qué:** en la base de identidad vieja (`dbsolucionesti`), el 34 era `ClienteProComercial`, que es el valor que se decidió. En la nueva, según el script de Duban, el 34 es `AdministracionZonasGeocerca` y el 32 es `ClienteProComercial`; el 31 (`ClienteProGerencial`) no cambia.
- **Antes de hacerlo:** confirmar esos números con alguien que tenga acceso a `dbSolucionesTI_Prueba`. Hoy solo los dice el script.
- **El orden importa:** si se cambia la base y no el frontend, el frontend oculta el módulo a los comerciales reales. Si se cambia el frontend y no la base, el backend los rechaza. En un ambiente que siga con la identidad vieja, el 32 es *proveeduría*.
- **Estado al 2026-10-16:** el `.env` local ya apunta a `dbSolucionesTI_Prueba`, y en la compartida los roles siguen en `comercial=34, gerente_comercial=31`. A Duban no lo afecta: entra con el 31.

### 3.2 Usar las columnas del `03` en el backend — Duban
- [ ] Mapear `ordenVehiculo` (en lugar del desfase de 1 ms en `createdAt`) e `idServicioConexoCatalogo` (en lugar de resolver el conexo por nombre). Hasta entonces, las filas nuevas quedan en NULL.
- Sigue pendiente al 2026-10-16: `origin/QA` no menciona ninguna de las dos.

### 3.3 `TrqType` de SAP para `DevolucionContenedor` — negocio o SAP
- [ ] Lo da negocio o SAP; no se inventa. Va en `db/seeder/02_sap_mapeo_trqtype.sql`.
- Sigue pendiente al 2026-10-16: el seeder de `origin/QA` todavía dice "PENDIENTE: falta el TrqType real", y la compartida no tiene ese mapeo.

### 3.4 `06_idempotencia_captura.sql` — no aplicar todavía
- [ ] `db/scripts/pendientes/06_idempotencia_captura.sql` (#173) crea `CapturasIdempotentes`, la idempotencia persistente de la captura. **No aplicar** hasta que el backend la use: hoy guarda la idempotencia en memoria (`clsRegistroCapturasIdempotentesEnMemoria`) y no mapea la tabla.

### 3.5 Funciones que faltan en el backend (salieron de construir el frontend, 2026-10-16) — Duban
No son errores: son funciones que el contrato de hoy no tiene y que las HU del frontend necesitan. Si no se hacen, hay que recortar el alcance de esas HU. También están en `docs/backlog/2026-10-16_PENDIENTES_DUBAN.md` (punto 7).

| Qué falta | Lo necesita | Situación hoy |
|---|---|---|
| [ ] **Endpoint de detalle por folio** (cabecera, rutas, mercancías, camiones, conexos, margen y precio en una llamada) | HU-021-FE.7 (#75), "Ver detalle", para poder recargar la página | No existe `GET /web/cotizaciones/{folio}`; el frontend combina `mis-cotizaciones` + `GET .../costeo`. Cliente, NIT, `estado_envio` y `ganada` solo vienen en el listado |
| [ ] **`DatosNuevaVersion` con mercancías, conexos y condiciones por ruta** | HU-021-FE.8 (#76) | `RutaNuevaVersion` solo trae línea, origen, destino y margen. Hay un trabajo pausado por el freeze ("DatosNuevaVersion ampliado", HU-004.4) en el stash de Sara |
| [ ] **Nueva versión con cambios para cotizaciones de varias rutas** (`datos_carga` por ruta) | HU-021-FE.8 (#76) | `DatosCargaWeb` es una sola carga sin ruta; con más de una ruta responde 409 `ESTADO_NO_PERMITE` (`VARIAS_RUTAS`) |
| [ ] **Total de la solicitud en el asunto o el cuerpo del correo** | HU-021-FE.8 (#76) | El asunto por defecto es `Cotización {folio} — Botero Soto` y el cuerpo es fijo; el precio solo va en el PDF. Confirmar con negocio si se quiere |
| [ ] **Listado regional de cotizaciones del gerente** (`GET /web/cotizaciones/regional`) | "Todas las cotizaciones" del Gerente Comercial | Ya existe como HU-016.1 / HU-021.10; sigue sin construirse después de #177 |
| [ ] **`clases_contenedor` en `GET /web/catalogos-flete`** | HU-021-FE.11 (#79): quitar la lista fija del frontend | El backend ya lee el catálogo de NEXO, pero solo para validar; no lo expone. Era la parte B de HU-023.1, y no está hecha |

**Hallazgo del frontend, para saber:** hasta HU-021-FE.8, la "nueva versión" del frontend se enviaba por `POST /web/solicitudes-costeo`. Eso crea una cotización nueva con otro folio, no una versión `-vN`. El versionado real solo pasa por `POST .../costeo/re-solicitar`. Si en QA se crearon "nuevas versiones" desde el frontend, son cotizaciones sueltas.

---

## ⚪ 4. Baja

### 4.1 HU-024 — Base de datos: organizar los scripts en una sola carpeta

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** S · **Roles:** Datos (Sara), Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> **Va al final**, después de HU-023 y antes del cierre del proyecto (HU-022).

#### Historia
**Como** equipo de datos, **quiero** que todos los scripts de la base de datos vivan en una sola carpeta, en orden y con un `01` que instale el esquema completo, **para** que una base nueva o la compartida se puedan dejar al día con un solo comando y sin tener que saber qué script vive en qué carpeta.

#### Contexto técnico
Al 2026-10-16 (`origin/QA`), los scripts están repartidos así:

| Carpeta | Qué hay | Lo corre el ejecutor (`db/ejecutar-bd.ps1` / `.sh`) |
|---|---|---|
| `db/scripts/` | `01_crear_esquema.sql` (instalación desde cero), `02_alter_cubicador.sql` y `07_motivo_busqueda_agotada.sql` | Sí |
| `db/scripts/pendientes/` | `03` (aplicado en la compartida el 2026-10-14), `04` (aplicado el 2026-10-13), `05` (aplicado el 2026-10-16) y `06` (sin aplicar) | No |
| `db/scripts/operacion/` | `corregir_rol_comercial_32.sql` y `revincular_identidad_dbSolucionesTI_Prueba.sql` (scripts de datos que corre el DBA) | No, a propósito |
| `db/migrations_mssql/` | `0001` a `0009` (históricas, no se tocan) y `0010_nombres_municipio.sql` (aplicado el 2026-10-13; sus columnas ya están en el `01`) | No |

El `01` no trae las columnas del `02`, el `03`, el `04`, el `05` ni el `07`. Hoy una instalación nueva necesita correr a mano los de `pendientes/`.

**Estado de la base compartida (2026-10-16):** tiene `01` + `02` + `0010` + `04` + `03` + `05`. Falta el `07` (punto 1.2).

#### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Instalación desde cero con un solo comando
Dado una base vacía
Cuando se corre el ejecutor sin parámetros especiales
Entonces queda el esquema completo vigente, sin correr nada a mano

Escenario: Base que ya existe
Dado la base compartida con los scripts ya aplicados
Cuando se corre el ejecutor en modo automático
Entonces no cambia nada y no falla

Escenario: El 01 es la fuente de verdad
Dado una base instalada solo con el 01 y otra con el 01 más todos los scripts de ajuste
Cuando se comparan sus esquemas (columnas, tipos, collations, constraints e índices)
Entonces son idénticos
```

#### Trazabilidad (event storming)
Fuente: revisión y aplicación del `0010` y el `04` en la base compartida (2026-10-13, comentario en #160), prueba del `03` en Docker (2026-10-14) y aplicación del `03` y el `05` (2026-10-14 y 2026-10-16).

#### Alcance en este repo (`Cotifly`)
1. Mover los scripts de ajuste a `db/scripts/` y dejar el ejecutor y las referencias al día (HU-024.1).
2. Consolidar todos los ajustes en el `01` (HU-024.2).

**HU también en:** ninguna.

#### [HU-024.1] [BD] Todos los scripts de ajuste en `db/scripts/`
**Labels:** `datos`, `base-de-datos`, `deuda-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos); Duban (Backend) para las referencias del backend
**Parte de HU-024 (milestone).**

- [ ] Mover `03`, `04` y `05` de `db/scripts/pendientes/` a `db/scripts/` y quitarles el aviso de "pendiente". El `06` se mueve solo cuando se apruebe y se aplique.
- [ ] Revisar el orden: el `07` ya está en `db/scripts/` y corre después del `02`; con el `03`, el `04` y el `05` movidos, el ejecutor los corre en orden de nombre.
- [ ] Decidir dónde queda el `0010`. Sus columnas ya están en el `01`. Opciones: dejarlo como histórico en `db/migrations_mssql/`, o pasarlo a `db/scripts/` sin el `USE dbTarifario_Nexo_Pruebas`. Las `0001` a `0009` **no se tocan**.
- [ ] Borrar la carpeta `db/scripts/pendientes/` cuando quede vacía.
- [ ] Comprobar en Docker que el ejecutor corre todos los scripts en orden, en una base vacía y en una que ya los tiene (modo automático).
- [ ] Actualizar las referencias a las rutas viejas: `db/README.md`, `clsBaseDatosHealthCheck.cs` (comentarios del `04`), los encabezados de los scripts y los documentos de `docs/database/`.

#### [HU-024.2] [BD] Consolidar los scripts de ajuste en el `01`
**Labels:** `datos`, `base-de-datos`, `deuda-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-024 (milestone). Depende de HU-024.1.**

- [ ] Pasar al `01_crear_esquema.sql` las columnas, CHECK, DEFAULT e índices del `02`, el `03`, el `04`, el `05` y el `07` (el CHECK `motivoSinSeleccion` con los 5 motivos y el `ISNULL`).
- [ ] Los scripts de ajuste **se quedan** para las bases que ya existen. Sobre una base instalada con el `01` nuevo no hacen nada, porque son idempotentes.
- [ ] Prueba en Docker: comparar el esquema de "solo `01`" contra "`01` + todos los ajustes". Tienen que salir idénticos.
- [ ] Actualizar el diccionario y el diagrama de la base (`docs/database/`).

---

## ✅ Registro: resuelto o ya no hace falta

| Tema | Estado | Detalle |
|---|---|---|
| Header `X-Portal-clsUsuario-Id` (HU-014.3, parte A) | Resuelto (#175) | Centralizado en `clsIdentidadNexo.cs`; ningún archivo manda ya `X-Portal-Usuario-Id`. El cambio local de Sara se descartó y queda respaldado en un stash |
| Contrato de captura con NEXO (#159) | Resuelto del lado de Cotifly (#175, #176) | `grupoRutaId` con `Nexo__EnviarGrupoRutaId`, `idRecomendacionVehiculo` y desempate por `numeroLinea`. Lo que falta es de NEXO (#297, punto 2.4) |
| Motivo `BUSQUEDA_AGOTADA` en el backend | Resuelto (#176) | El backend ya conoce el motivo. Falta aplicar el `07` en la compartida (punto 1.2) |
| Identificador de la mercancía hacia el cubicador (S5-HU-17.4) | Decidido el 2026-10-14 | Va `codigoMercancia`, no `idTipoMercancia`. Falta que Brayan corrija el texto de la 17.4. Ya no vale lo que dice `2026-10-08_PENDIENTES_AL_FINALIZAR_CORRECCION_BD.md` ("Cotifly manda el `idTipoMercancia`") |
| Mesa agrupa por ruta (administracion#310) | Cerrada como duplicada de administracion#289 | La #289 ya está hecha en su rama; falta fusionarla (punto 2.4) |
| `03` en la compartida | Aplicado el 2026-10-14 | Probado antes en Docker con datos: numera los camiones, rechaza el orden repetido, corrige el CHECK del `02` y se puede repetir |
| `05` en la compartida | Aplicado el 2026-10-16 | `largoBytes` = 40 y 2 conexos sin categoría. Sin efecto hasta que el backend use la columna |
| Validación previa para el `03` | Ya no hace falta | El `03` ya se aplicó, y el `07` de Duban valida antes de recrear el CHECK |
| `07` propio de Sara (`db/scripts/pendientes/`) | Ya no hace falta | Se usa el de Duban (`db/scripts/07_motivo_busqueda_agotada.sql`). Borrar el local (punto 1.2) |
| Respaldo `dbo_cotifly_respaldo_20261008` | Ya no existe | Ni ese esquema ni ninguna tabla de respaldo quedan en la compartida |
| Docker local `cotifly-sqlserver-1` | Detenido el 2026-10-16 | Las bases de prueba ya se habían borrado |

---

> **Documento original:** `2026-10-16_PENDIENTES_DUBAN.md` · **Creado:** 2026-10-16

# Pendientes de backend para Duban

> **Última revisión: 2026-10-19** contra `origin/QA` hasta #178. Hechos: punto 1 (CORS, #178) y 7.5 (listado regional, #154). También entró en QA HU-018.1 (#155): `CotizacionResumen` trae `deadline`, `score_prioridad` y `comercial`, pero sigue sin existir el endpoint de detalle (7.1).

**Fecha:** 2026-10-16 · **Revisado contra:** `origin/QA` hasta #177 · **Repo:** `Cotifly`.

Solo lo que le toca a backend, de lo más urgente a lo menos urgente. Incluye (punto 8) el rechazo de una línea en NEXO, que antes estaba en un archivo aparte. El contexto completo está en `docs/backlog/2026-10-14_HU_Correcciones_Nuevas_Cotifly.md`.

| Prioridad | Qué significa |
|---|---|
| 🔴 Urgente | Bloquea pruebas hoy |
| 🟠 Alta | Esta semana; afecta funciones que ya se están construyendo |
| 🟡 Media | Con dependencias o coordinado con otros equipos |
| ⚪ Baja | Funciones que faltan en el backend; no bloquean, pero el frontend las va a pedir |

El punto 8 es 🟡 Media pero va al final porque depende de que NEXO publique el contrato.

---

## ✅ 1. CORS no permite `If-Match` ni `Idempotency-Key` — HECHO (#178, 2026-10-16)

> **Revisado el 2026-10-19 en `origin/QA`:** `EncabezadosCors = ["Authorization", "Content-Type", "If-Match", "Idempotency-Key"]` y `.WithExposedHeaders("ETag", "Location", "Retry-After", "Content-Disposition")` (`clsSeguridadHttp.cs:150-185`). Lo de abajo queda como registro.


- [x] Agregar `If-Match` e `Idempotency-Key` a `EncabezadosCors` en `src/Cotifly.Api/Comun/clsSeguridadHttp.cs:145`. Hoy es `["Authorization", "Content-Type"]`.
- [x] Opcional: `.WithExposedHeaders("ETag")` en la política. El frontend ya lee `row_version` del cuerpo, así que no depende del encabezado.

**Evidencia** (2026-10-16, backend local en `localhost:8083`):
```
curl -i -X OPTIONS "http://localhost:8083/web/cotizaciones/TEST/costeo/servicios-conexos" -H "Origin: http://localhost:4200" -H "Access-Control-Request-Method: PUT" -H "Access-Control-Request-Headers: If-Match, Content-Type"

HTTP/1.1 204 No Content
Access-Control-Allow-Headers: Authorization,Content-Type
```
- **Efecto:** el navegador bloquea en el preflight cualquier petición con `If-Match` o `Idempotency-Key`; el servidor nunca la recibe.
- **Qué afecta:** `POST`/`PUT .../costeo/servicios-conexos`, `PUT .../costeo/margen`, `POST .../costeo/re-solicitar` y `POST /web/solicitudes-costeo` con `Idempotency-Key` (#173).
- **Ya bloquea las pruebas del frontend en local:** el frontend llama directo a `http://localhost:8083`, sin proxy. En producción todavía no está definido si backend y frontend comparten origen.

---

## 🟠 2. Confirmar `Captura:AceptarVehiculoCliente` en los ambientes

- [ ] Confirmar si algún ambiente desplegado la prende por variable de entorno. En `origin/QA` solo aparece comentada en `.env.example` (`# Captura__AceptarVehiculoCliente=false`) y no está en ningún `appsettings`.
- [ ] Decidir si se prende mientras el cubicador real no esté conectado.
- **Por qué importa:** con la bandera apagada, el backend ignora `tipo_vehiculo` y `carroceria` del cliente. La pantalla del frontend para digitar el vehículo (respaldo de HU-021-FE.2, #70) no tiene efecto.
- También apagadas por defecto desde #175: `Nexo__EnviarCodigosCubicador` y `Nexo__EnviarGrupoRutaId`. Hay que confirmar su valor en cada ambiente.

---

## 🟠 3. HU-010.5 — Filtro por año y mes en `GET /web/metricas/regional`

**Issue nueva dentro de la milestone HU-010.** Es lo que necesita el selector año/mes del tablero del gerente (HU-010.3, clientepro-frontend #51).

El cambio ya está escrito y guardado como parche: **`docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`**, sobre `QA` (`cf744ff`). Se comprobó que aplica limpio sobre `QA` con #175. Son 9 archivos: unas 45 líneas de código, 90 de pruebas y 50 de documentación.

**Qué hace:**
- `?anio=2026` → el año completo; `?anio=2026&mes=10` → ese mes completo.
- Si llega `anio`, reemplaza a `desde`/`hasta`; no se combinan. Sin `anio` ni `mes`, todo funciona igual que antes.
- 400 si `mes` no está entre 1 y 12, si llega `mes` sin `anio` o si el año no es válido.
- `/web/metricas/comercial` no cambia.

**Checklist:**
- [ ] Revisar el cambio y confirmar que se sube pese al freeze.
- [ ] Rama desde `QA` y `git apply --3way docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`.
- [ ] Correr las pruebas: unitarias, de API y de aceptación (`metricas_tablero.feature`).
- [ ] Revisar si hace falta anotar la extensión en ADR-016 §5.
- [ ] PR a `QA` (`Part of HU-010`) y avisarle al frontend (#51).

---

## 🟡 4. Cambio de la base de identidad a `dbSolucionesTI_Prueba` (#172) — la parte de backend

Va junto con el cambio del rol `comercial` de 34 a 32. Son tres partes y van en una sola ventana: **la base** (el DBA corre `db/scripts/operacion/corregir_rol_comercial_32.sql`), **la configuración** (esta parte) y **el frontend** (Sara, `cotifly-roles.ts`).

- [ ] Coordinar la ventana: cada ambiente apunta `ConnectionStrings__Identidad` a `dbSolucionesTI_Prueba`, y en la misma ventana, con la API detenida, el DBA corre `revincular_identidad_dbSolucionesTI_Prueba.sql` y `corregir_rol_comercial_32.sql`.
- [ ] Confirmar con alguien que tenga acceso a `dbSolucionesTI_Prueba` que ahí el 32 es `ClienteProComercial` y el 31 es `ClienteProGerencial`. Hoy solo lo dice el script.
- **Cuidado con el orden:** en un ambiente que siga con la identidad vieja (`dbsolucionesti`), el 32 es *proveeduría*.
- **Estado al 2026-10-16:** la compartida tiene `comercial=34, gerente_comercial=31`, y el `.env` local ya apunta a `dbSolucionesTI_Prueba`.

---

## 🟡 5. Usar las columnas del `03`

- [ ] Mapear `CotizacionVehiculosLineaSolicitud.ordenVehiculo`, en lugar del desfase de 1 ms en `createdAt` (`clsVehiculoLineaSolicitud.OrdenCreacion`).
- [ ] Mapear `CotizacionServiciosConexos.idServicioConexoCatalogo`, en lugar de resolver el conexo por nombre (`clsResolutorConexosCatalogo`).
- Las columnas ya existen en la compartida desde el 2026-10-14. Hasta que se mapeen, las filas nuevas quedan en NULL.

## 🟡 6. Columnas y tabla ya creadas o propuestas, sin usar

- [ ] `CotizacionServiciosConexos.categoriaServicioConexo` (script `05`): ya está en la compartida desde el 2026-10-16. Guardar ahí la categoría al capturar o importar los conexos, como propone el encabezado del `05`.
- [ ] `CapturasIdempotentes` (script `06`): cuando el backend pase la idempotencia de memoria a la tabla, avisarle a Sara para que la aplique. Hoy no hace falta.

---

## ⚪ 7. Funciones que faltan en el backend (salieron de construir el frontend, 2026-10-16)

Ninguna es un error: son funciones que el contrato de hoy no tiene y que las HU del frontend necesitan. Si no se hacen, hay que recortar el alcance de esas HU.

| # | Qué falta | Lo necesita | Situación hoy |
|---|---|---|---|
| 7.1 | **Endpoint de detalle por folio** (cabecera, rutas, mercancías, camiones, conexos, margen y precio en una llamada) | HU-021-FE.7 (#75), "Ver detalle", para poder recargar la página | No existe `GET /web/cotizaciones/{folio}`; el frontend combina `mis-cotizaciones` + `GET .../costeo`. Cliente, NIT, `estado_envio` y `ganada` solo vienen en el listado |
| 7.2 | **`DatosNuevaVersion` con mercancías, conexos y condiciones por ruta** | HU-021-FE.8 (#76) | `RutaNuevaVersion` solo trae línea, origen, destino y margen. Hay un trabajo pausado por el freeze ("DatosNuevaVersion ampliado", HU-004.4) en el stash de Sara |
| 7.3 | **Nueva versión con cambios para cotizaciones de varias rutas** (`datos_carga` por ruta) | HU-021-FE.8 (#76) | `DatosCargaWeb` es una sola carga sin ruta; con más de una ruta responde 409 `ESTADO_NO_PERMITE` (`VARIAS_RUTAS`) |
| 7.4 | **Total de la solicitud en el asunto o el cuerpo del correo** | HU-021-FE.8 (#76) | El asunto por defecto es `Cotización {folio} — Botero Soto` y el cuerpo es fijo; el precio solo va en el PDF. Confirmar con negocio si se quiere |
| 7.5 | ✅ **HECHO (#154, HU-016.1, en QA):** listado regional de cotizaciones del gerente (`GET /web/cotizaciones/regional`) | "Todas las cotizaciones" del Gerente Comercial | Misma forma que `mis-cotizaciones` más `comercial` por fila; solo `gerente_comercial`; la regional se resuelve en el servidor; 409 `REGIONAL_NO_ASIGNADA` sin regional |
| 7.6 | **`clases_contenedor` en `GET /web/catalogos-flete`** (`OpcionCatalogo`; `valor` = `etiqueta` = `descripcion` de NEXO, como `clases_viaje`) | HU-021-FE.11 (#79): quitar la lista fija del frontend | El backend ya lee `GET /api/catalogos/clases-contenedor` de NEXO, pero solo para validar; `CatalogosFlete` trae solo `tipos_vehiculo`, `tipos_carroceria` y `clases_viaje`. Era la parte B de HU-023.1 en el plan del Sprint 5, y no está hecha. HU-023.1 ya quitó la lista fija de 5 códigos del validador, así que hoy el backend solo acepta la `descripcion` exacta de NEXO (o la lista G15), y con el catálogo de NEXO vacío responde 503. **No hay un error activo:** Sara verificó el 2026-10-16 que las `descripcion` de NEXO son exactamente `CNT20.ST`, `CNT20.RE`, `CNT20.IT`, `CNT40.HC` y `CNT40.RE`, iguales a la lista fija del frontend. Queda el riesgo de que se desincronicen si NEXO cambia su catálogo. Además, `openapi.yaml` (`SolicitudCosteoWeb.clase_contenedor`) todavía menciona los 5 códigos fijos: hay que actualizarlo |
| 7.7 | **"Margen promedio" contra la política** (HU-016.1, parte B) | HU-021-FE.11 (#79), tarjeta "Margen promedio" de Mis cotizaciones | **Primero decide negocio:** Opción A, un endpoint nuevo accesible al Comercial que liste `MargenesPolitica` (línea y margen) de las líneas activas para que el frontend calcule el promedio; u Opción B, cambiar `desviacion_promedio_margen` (ADR-016 la define como "solo ajustes negociados"). Hoy `GET /web/admin/lineas-negocio` responde 403 al Comercial, y `/web/metricas/comercial` solo da escalares. Tampoco está decidido si el promedio por ruta es simple o ponderado por costo |

---

## 🟡 8. Rechazo de una línea con motivo en la Mesa de Costeo de NEXO

> ⚠ **Contrato con NEXO aún sin cerrar:** falta que NEXO lo implemente. Las decisiones de negocio ya están tomadas (2026-10-16). Esta sección dice qué tiene que cambiar Cotifly y qué le pedimos a NEXO. **No construir nada hasta que NEXO publique el contrato.** La parte de base de datos le toca a Sara.

### La decisión (Sara, 2026-10-16)

En la Mesa de Costeo de NEXO, el Administrador de Tarifas puede **rechazar UNA línea** con un **motivo obligatorio** ("no se va a cotizar por X"):
- **No frena la cotización:** se puede entregar, y salen el PDF y el Excel.
- **La línea rechazada no suma al total y no lleva precio.**
- **El motivo lo ve solo el comercial,** en su pantalla de costeo y en la Mesa. El PDF y el Excel no lo muestran.

#### Decisiones de Sara (2026-10-16)
1. **PDF y Excel:** la línea rechazada sale marcada como **"Sin costeo"** (o un texto parecido), **sin precio y SIN el motivo**.
2. **Deshacer el rechazo:** solo se puede mientras la cotización siga en la Mesa de Costeo. Una vez **entregada**, ya no.
3. **Si se rechazan TODAS las líneas:** la cotización se le envía igual al comercial, con el motivo de cada línea.

### Cómo está hoy Cotifly (verificado en el código)

| Situación | Qué hace hoy | Problema con el rechazo |
|---|---|---|
| Línea **borrada** en NEXO (#134) | El camión queda `descartada` (`clsVehiculoLineaSolicitud.DescartarPorLineaBorrada`): pierde sus costos de NEXO, no cuenta en el estado ni en los totales de la ruta (`clsLineaSolicitud.VehiculosQueCuentan`) y no recibe conexos | Es lo más parecido, pero no guarda motivo. Además, si **todos** los camiones de una ruta están descartados, "cuentan todos" y la ruta queda **sin flete** |
| Línea **sin flete** en una cotización **entregada** | `clsDerivacionEstadoCosteo.DerivarLinea` la deja `entregada` sin costo ("NEXO la entregó sin esa línea") | La ruta queda sin precio, `precio_final` en `null`, y el PDF responde 409 `COSTEO_NO_LISTO` porque exige **todas** las rutas con precio |
| Línea sin flete **antes de entregar** | `Derivar` solo da `autocosteo` si **todas** las líneas tienen flete; si no, `pendiente_costeo` | Una línea rechazada nunca tendría flete: la cotización se quedaría en `pendiente_costeo` (y el polling seguiría) hasta que NEXO la entregue |

Hoy Cotifly **no puede distinguir** una línea rechazada de una línea que todavía no se ha costeado, ni de una borrada: NEXO no le manda ninguna señal ni motivo.

### (b) Qué le pedimos a NEXO en el contrato

En `GET /api/solicitudes/{id}`, por cada línea de la solicitud (y si aplica, en `GET /api/cotizaciones/{id}`):

| Campo propuesto | Tipo | Para qué |
|---|---|---|
| `estadoLinea` (o un valor nuevo en el estado actual de la línea) | enum, con `Rechazada` | Distinguir "rechazada" de "pendiente" y de "borrada" |
| `motivoRechazo` | texto, obligatorio si está rechazada | Mostrárselo al comercial (no va en el PDF ni en el Excel, decisión 1) |
| `rechazadaAt` y quién la rechazó (opcional) | fecha y usuario | Auditoría |
| `numeroLinea` (ya pedido en NEXO#297) | int | Emparejar la línea con su camión (ya está en el contrato de captura) |

Ya decidido: deshacer el rechazo solo antes de entregar, y si se rechazan todas, la cotización se entrega igual (ver las decisiones arriba).

Falta acordar con NEXO:
- **Si la cotización de NEXO (`CotizacionDto`) omite la línea rechazada o la trae sin flete con el estado.** Hoy Cotifly toma el costo de la cotización de NEXO.
- **Cómo se ve "se rechazaron todas"** en la solicitud y la cotización de NEXO: estado `Entregada` con todas las líneas `Rechazada`, sin flete.

### (a) Qué tendría que cambiar Cotifly

#### Lectura del costeo (polling)
- [ ] Leer el estado y el motivo de cada línea de la foto de NEXO (`clsCosteoConsultadoNexo`, `clsLineaCosteoNexo` o las líneas de la solicitud).
- [ ] **Estado nuevo del camión `rechazada`**, distinto de `descartada` (que es "línea borrada", #134), y guardar `motivoRechazo`.
- [ ] **Deshacer el rechazo (decisión 2):** mientras la cotización no esté entregada, un camión `rechazada` NO es terminal. El polling lo vuelve a leer y, si NEXO lo des-rechazó, vuelve a `pendiente_costeo` y se borra el motivo. Cuando la cotización queda `entregada`, el rechazo es definitivo.
- [ ] `clsDerivacionEstadoCosteo.Derivar`: la cotización es `autocosteo` cuando todas las líneas **no rechazadas** tienen flete. Si no, se queda en `pendiente_costeo` para siempre.
- [ ] `clsResumenEstadoCosteo` y `VehiculosQueCuentan`: ignorar las `rechazada`, igual que las `descartada`. Una ruta con **todos** sus camiones rechazados queda `rechazada`, sin precio, pero **no bloquea** la cotización.
- [ ] Revisar el emparejamiento de camiones con líneas (#134): una línea rechazada sigue existiendo en NEXO, no es "línea sin dueño".

#### Base de datos (Sara, con script en `db/scripts/`)
- [ ] `CotizacionVehiculosLineaSolicitud`: agregar `rechazada` al CHECK de `estadoCosteo` (si se usa como estado), más `motivoRechazo NVARCHAR(…) NULL` y, opcional, `rechazadaAt`.
- [ ] Revisar los CHECK de `estadoCosteo` en `CotizacionLineasSolicitud` y `CotizacionesSolicitud` si el estado `rechazada` también existe en la ruta.

#### Totales, margen y precio por ruta
- [ ] `precio_final` (total) = suma de las rutas **no rechazadas**. Hoy es `null` si cualquier ruta no tiene precio.
- [ ] Una ruta rechazada: `precio_final_linea = null` con estado y motivo visibles. Ajustar el margen o agregar conexos en ella → 409 con un código claro (por ejemplo `RUTA_RECHAZADA`).
- [ ] Ruta con algunos camiones rechazados: el precio es el de los camiones vigentes. Los conexos se pegan al primer camión **no rechazado** (`clsServiciosConexosCosteoService`).
- [ ] Métricas (ADR-016): definir si el valor cotizado excluye las rutas rechazadas. Lo natural es que sí.

#### PDF de Cotifly (`POST .../costeo/pdf`)
- [ ] Cambiar la regla "todas las rutas con precio y `precio_final` = suma de `precio_final_linea`": las rechazadas no cuentan.
- [ ] **Decidido (1):** la ruta rechazada se muestra como **"Sin costeo"**, sin precio y **sin el motivo**, en el resumen del pliego y en su sección. Con una sola ruta, igual.
- [ ] **Por definir con Sara:** si se rechazaron **todas** las rutas (decisión 3), ¿se permite generar el PDF (todo "Sin costeo", total 0 o vacío) o se bloquea con un mensaje claro? Lo natural es bloquearlo: no hay nada que cotizar.

#### Excel de la licitación (`GET /web/licitaciones/{folio}/excel`)
- [ ] **Decidido (1):** la fila de una línea rechazada sale **"SIN COSTEO", sin precio y sin el motivo**, como las que no tienen costeo. En la hoja "Resumen Cotifly" no cuenta en el total. La hoja "Camiones Cotifly" igual, sin motivo.
- [ ] `Licitacion.lineas_costeadas` y `lineas_pendientes` (pantalla del comercial, no el Excel): las rechazadas no son pendientes. Hoy existe el texto "Descartada en Nexo" (`clsLicitacionService`); agregar "Rechazada en Nexo" con su motivo, porque en pantalla el comercial sí ve el motivo.

#### Pantalla de costeo y contrato web (`openapi.yaml`)
- [ ] `GET .../costeo` (lo que ve el comercial): exponer en `RutaCosteo` y `CamionCosteo` el estado `rechazada` y `motivo_rechazo`. **Cambio de enum:** el frontend tiene que agregar el valor nuevo.
- [ ] **Todas las rutas rechazadas (decisión 3):** la cotización queda `entregada`, con todas sus rutas `rechazada`, `precio_final` vacío y el motivo de cada ruta visible en el costeo. El polling se detiene, como en cualquier entrega.
- [ ] `CotizacionResumen` ("Mis cotizaciones"): el resumen del estado ignora las rechazadas; el total sale sin ellas.

#### Versiones `-vN` y re-solicitud
- [ ] **Por decidir:** ¿la `-vN` copia la ruta rechazada (y NEXO la vuelve a evaluar) o la omite? Propuesta: copiarla, avisar al comercial que fue rechazada y por qué, y dejarle quitarla.
- [ ] Re-solicitud en el mismo folio: la línea se vuelve a capturar en NEXO, y el rechazo anterior queda en la auditoría.

#### Resultado (ganada o perdida)
- [ ] Marcar la cotización como ganada con rutas rechazadas debe funcionar. Las rutas rechazadas no se envían ni se cobran.

### Lo que NO cambia
- El contrato de captura (`POST /api/captura-solicitudes`): el rechazo pasa en la Mesa de NEXO, después de la captura.
- El cubicador: no participa.

---

## Para saber, sin acción

- **El `07` (`BUSQUEDA_AGOTADA`) es el tuyo** (`db/scripts/07_motivo_busqueda_agotada.sql`, #176). Sara lo aplica en la compartida.
- **Lo que falta del contrato de captura es de NEXO (#297):** devolver y ordenar por `numeroLinea` en el GET, y que una línea de Cupo sin vehículo no se autocostee. Del lado de Cotifly quedó en #175 y #176.
- **En el portal de NEXO no existen** la bandeja de vehículos en cuarentena (S5-HU-19.3) ni editar el vehículo y poner el precio sin vehículo en la Mesa (S5-HU-19.4). Mientras no existan, `Nexo__EnviarCodigosCubicador` debe seguir apagada.

---

> **Documento original:** `2026-10-19_HU_Sprint6_Cotifly_Backend.md` · **Creado:** 2026-10-19

# Sprint 6 — Cotifly backend

> **Estado:** borrador del Sprint 6; la fuente de verdad son las milestones de GitHub · **Dueño:** Duban Guerra (Backend) · **Fecha de redacción:** 2026-10-19

> **Fuente:** los pendientes de backend que quedaron abiertos al cerrar el Sprint 5, revisados contra `origin/QA` hasta #178 el 2026-10-19 (antes estaban en `docs/backlog/2026-10-16_PENDIENTES_DUBAN.md`). Salieron de construir el frontend contra el contrato real (2026-10-15 y 2026-10-16), de las pruebas en vivo con el cubicador y de la decisión de Sara sobre el rechazo de líneas en NEXO.
> **Convención:** mismo formato del backlog del Sprint 5 (`docs/backlog/2026-10-07_HU_Sprint5_Cotifly.md`). La numeración sigue desde HU-025 (HU-024 es la HU de datos para organizar los scripts de la base, de Sara). Todas las issues de este documento son **nuevas**: ninguna edita una issue que ya exista en GitHub.
> **Issues agrupadas al máximo** (2026-10-19, a pedido de Sara): cada milestone trae una sola issue de backend, dividida en partes A/B/C cuando cubre varios temas, para no fragmentar el trabajo de Duban.

---

## Notas de lectura

1. **Repo de todos los issues:** `Cotifly`. Roles: **Duban** = Backend, **Sara** = Datos y Frontend, **Kevin** = QA.
2. **Ya resuelto en el Sprint 5, no se repite aquí:**
   - CORS con `If-Match` e `Idempotency-Key`, y `ETag` expuesto (#178).
   - Listado regional del gerente, `GET /web/cotizaciones/regional` (#154, HU-016.1).
   - Campos de "Ver detalle" en `CotizacionResumen`: `deadline`, `score_prioridad` y `comercial` (#155, HU-018.1).
   - Header `X-Portal-clsUsuario-Id` centralizado (#175).
   - Motivo `BUSQUEDA_AGOTADA` (#176).
3. **Ya existe como HU, no se crea de nuevo:**
   - **HU-017.1** ("Margen promedio" contra la política). Sigue pendiente de la decisión de negocio entre la opción A (endpoint para el Comercial con los márgenes de política) y la opción B (cambiar `desviacion_promedio_margen`). Ver la sección "Relación con las HU que ya existen".
4. **Rama abierta a tener en cuenta:** `hu-023-defectos-e2e-1016` (17 commits del 2026-10-16 al 2026-10-19, todavía no está en QA) cambia el contrato de "Calcular cubicaje": pasa a ser asíncrono, con 202 y `GET /web/cubicacion/{id}`. Las issues de este sprint no dependen de ella, pero conviene fusionarla primero.

## Resumen

| HU | Issue | Título | Prioridad | Depende de |
|---|---|---|---|---|
| HU-010 | HU-010.5 | Filtro año/mes en el tablero del gerente | 🟠 Alta | — (parche listo) |
| HU-025 | HU-025.1 | Configuración por ambiente: banderas e identidad | 🟠 Alta / 🟡 Media | DBA, frontend (parte B) |
| HU-026 | HU-026.1 | Usar las columnas nuevas de la base de datos | 🟡 Media | — |
| HU-027 | HU-027.1 | Contrato web que necesita el frontend | 🟡 Media / ⚪ Baja | Decisión de negocio (parte D) |
| HU-028 | HU-028.1 | Rechazo de una línea con motivo (backend) | 🟡 Media | HU-028.2 (listo) |
| HU-028 | HU-028.2 | [BD] Columnas del rechazo | 🟡 Media | ✅ Listo, PR #200 |
| HU-028 | HU-028.3 | [QA] Rechazo de línea de punta a punta | 🟡 Media | HU-028.1, HU-028.2 |

**5 issues para Duban** (HU-010.5, HU-025.1, HU-026.1, HU-027.1, HU-028.1) + 1 para Sara (HU-028.2) + 1 para Kevin (HU-028.3).

---

## HU-010 — Rol Gerente Comercial y tablero de métricas (milestone existente)

> **Issue nueva** dentro de la milestone HU-010. HU-010.1 a 010.4 ya existen y no se modifican.

### [HU-010.5] [Backend] Filtro por año y mes en `GET /web/metricas/regional`
**Labels:** `backend`, `metricas`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-010 (milestone). La consume HU-010.3 (clientepro-frontend #51).**

El selector año/mes del tablero del gerente necesita filtrar por año o por mes completo, en vez de mandar `desde`/`hasta`.

El cambio ya está escrito y guardado como parche: **`docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`**, hecho sobre `QA` (`cf744ff`), y se comprobó que aplica limpio sobre `QA` con #175. Son 9 archivos: unas 45 líneas de código, 90 de pruebas y 50 de documentación (controlador, DTO, `clsTableroMetricasService.ResolverPeriodo`, tres suites de pruebas, `openapi.yaml`, `metricas_tablero.feature` y `07-api.md`).

- [ ] Revisar el cambio y confirmar que se sube pese al freeze.
- [ ] Rama desde `QA` y `git apply --3way docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`. Volver a comprobar contra `QA` actual (#178).
- [ ] Correr las pruebas unitarias, de API y de aceptación (`metricas_tablero.feature`).
- [ ] Revisar si hace falta anotar la extensión en ADR-016 §5.
- [ ] PR a `QA` (`Part of HU-010`) y avisarle al frontend (#51).

```gherkin
Escenario: Año completo
Dado un gerente comercial con regional
Cuando consulta /web/metricas/regional?anio=2025
Entonces el periodo aplicado va del 2025-01-01 al 2025-12-31

Escenario: Mes de un año
Cuando consulta /web/metricas/regional?anio=2025&mes=2
Entonces el periodo aplicado va del 2025-02-01 al 2025-02-28

Escenario: Parámetros inválidos
Cuando consulta con mes=5 sin anio, o con anio=2026&mes=13
Entonces responde 400 con un ErrorWeb

Escenario: Sin los parámetros nuevos
Cuando consulta sin anio ni mes
Entonces el comportamiento es el de antes
```

---

## HU-025 — Configuración por ambiente e identidad

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E4 — Despliegue, Configuración y Seguridad · **Talla:** S · **Roles:** Backend (Duban), Datos (Sara), Frontend (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo, **quiero** que cada ambiente tenga claras sus banderas de captura y de NEXO, y que Cotifly use la base de identidad correcta con los números de rol correctos, **para** que los comerciales entren con su rol y la captura se comporte igual en todos los ambientes.

### Contexto técnico
- `Captura:AceptarVehiculoCliente`, `Nexo:EnviarCodigosCubicador` y `Nexo:EnviarGrupoRutaId` vienen apagadas por defecto. En `origin/QA` solo están documentadas en `.env.example`; no están en ningún `appsettings`. Con `AceptarVehiculoCliente` apagada, el backend ignora el vehículo que digita el comercial, así que la pantalla de respaldo del frontend (HU-021-FE.2, #70) no tiene efecto.
- Cotifly pasó a usar como identidad `dbSolucionesTI_Prueba` (#172). Ahí los IDROLES son otros: según el script de #173, el 34 es `AdministracionZonasGeocerca` y el 32 es `ClienteProComercial`. La compartida sigue con `comercial = 34`, que era el valor correcto en la base vieja (`dbsolucionesti`).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Banderas conocidas por ambiente
Dado los ambientes local, QA y producción
Cuando se revisa su configuración
Entonces cada bandera de captura y de NEXO tiene un valor decidido y documentado

Escenario: Un comercial real entra como comercial
Dado un usuario con el rol ClienteProComercial en la base de identidad del ambiente
Cuando inicia sesión en Cotifly
Entonces entra con el rol interno comercial
```

### Trazabilidad (event storming)
Fuente: revisión del contrato con el frontend (2026-10-15 y 2026-10-16), #172 y #173.

### Alcance en este repo (`Cotifly`)
1. Banderas e identidad (HU-025.1, partes A y B).

**HU también en:** `clientepro-frontend` (`cotifly-roles.ts`, parte B).

#### Issues en `Cotifly`

### [HU-025.1] [Config] Configuración por ambiente: banderas de captura/NEXO e identidad (rol `comercial` 34 → 32)
**Labels:** `backend`, `configuracion`, `datos`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend); el DBA corre los scripts de la parte B; Sara cambia el frontend
**Parte de HU-025 (milestone).**

**Parte A — Banderas de captura y de NEXO por ambiente.**
- [ ] Confirmar el valor actual de `Captura__AceptarVehiculoCliente`, `Nexo__EnviarCodigosCubicador` y `Nexo__EnviarGrupoRutaId` en cada ambiente desplegado (Dokploy).
- [ ] Decidir si `AceptarVehiculoCliente` se prende mientras el cubicador real no esté conectado en cada ambiente.
- [ ] `EnviarCodigosCubicador` sigue apagada hasta que el portal de NEXO tenga la bandeja de vehículos en cuarentena (S5-HU-19.3) y la edición del vehículo en la Mesa (S5-HU-19.4).
- [ ] Dejar los valores decididos en `DEPLOY.md` y en el `.env.example`.

**Parte B — Cambio de la base de identidad a `dbSolucionesTI_Prueba` y rol `comercial` de 34 a 32.** Son tres partes en **una sola ventana**:

| Parte | Qué cambia | Quién |
|---|---|---|
| Base de datos | `dbo_cotifly.Roles.idRolExterno` del rol `comercial`: de 34 a 32 (`db/scripts/operacion/corregir_rol_comercial_32.sql`) | DBA, con aprobación |
| Configuración | Cada ambiente apunta `ConnectionStrings__Identidad` a `dbSolucionesTI_Prueba` y, con la API detenida, el DBA corre `db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql` | Duban y el DBA |
| Frontend | `cotifly-roles.ts` reconoce como comercial el 34 (y el 25 en pruebas): pasar al 32 y revisar qué es el 25 en la base nueva | Sara |

- [ ] Confirmar con alguien que tenga acceso a `dbSolucionesTI_Prueba` que ahí el 32 es `ClienteProComercial` y el 31 es `ClienteProGerencial`. Hoy solo lo dice el script.
- [ ] Acordar la ventana con el DBA y con Sara.
- [ ] Cambiar `ConnectionStrings__Identidad` en cada ambiente y correr los dos scripts de operación en esa misma ventana.
- [ ] **Cuidado con el orden:** en un ambiente que siga con la identidad vieja, el 32 es *proveeduría*.
- [ ] Probar el login de un comercial real y del gerente (el 31 no cambia).

---

## HU-026 — Usar las columnas nuevas de la base de datos

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** S · **Roles:** Backend (Duban), Datos (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo, **quiero** que el backend use las columnas que ya existen en la base compartida, **para** dejar de depender de desfases de fecha y de búsquedas por nombre, y no perder datos al reiniciar la API.

### Contexto técnico
- Script `03` (en la compartida desde el 2026-10-14): `CotizacionVehiculosLineaSolicitud.ordenVehiculo` y `CotizacionServiciosConexos.idServicioConexoCatalogo`. Hoy el orden del camión sale de un desfase de 1 ms en `createdAt` (`clsVehiculoLineaSolicitud.OrdenCreacion`) y el conexo se resuelve por nombre (`clsResolutorConexosCatalogo`). Las filas nuevas quedan con esas columnas en NULL.
- Script `05` (en la compartida desde el 2026-10-16): `CotizacionServiciosConexos.categoriaServicioConexo`. Hoy la categoría se lee del catálogo de NEXO cada vez que se arma el costeo (caché de 30 min).
- Script `06` (propuesto, **no aplicado**): tabla `CapturasIdempotentes`. Hoy la idempotencia de la captura vive en memoria (`clsRegistroCapturasIdempotentesEnMemoria`) y se pierde al reiniciar o con dos réplicas.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Orden de los camiones estable
Dado una ruta con tres camiones creados en la misma petición
Cuando se lee la ruta
Entonces los camiones salen en el orden de ordenVehiculo, sin depender de createdAt

Escenario: Idempotencia sobrevive a un reinicio
Dado una captura con Idempotency-Key ya confirmada
Cuando la API se reinicia y llega la misma petición
Entonces responde la misma cotización y no crea otra solicitud en NEXO
```

### Trazabilidad (event storming)
Fuente: encabezados de los scripts `03`, `05` y `06` (#158 y #173); aplicación del `03` y el `05` en la compartida.

### Alcance en este repo (`Cotifly`)
1. Las cuatro columnas nuevas (HU-026.1, partes A, B y C).

**HU también en:** ninguna.

#### Issues en `Cotifly`

### [HU-026.1] [Backend] Mapear las columnas nuevas de la base de datos
**Labels:** `backend`, `deuda-tecnica`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend); Sara aplica el `06` en la compartida
**Parte de HU-026 (milestone).**

**Parte A — Orden del camión e id del catálogo del conexo (script `03`).**
- [ ] Mapear `ordenVehiculo` en EF y escribirlo al crear o replanificar camiones (1, 2, 3 por ruta). Retirar el desfase de 1 ms en `createdAt`.
- [ ] Mapear `idServicioConexoCatalogo` y llenarlo al capturar, agregar o importar conexos. Dejar de resolver el conexo por nombre cuando la columna tenga valor.
- [ ] Agregar las dos columnas al chequeo de `/health/ready`.

**Parte B — Categoría del conexo (script `05`).**
- [ ] Guardar `categoriaServicioConexo` al capturar e importar los conexos, como propone el encabezado del `05`. Las filas viejas siguen leyendo la categoría del catálogo.

**Parte C — Idempotencia persistente (script `06`).**
- [x] Pasar la idempotencia de la captura de memoria a `CapturasIdempotentes`: la clave se reserva en la misma transacción que confirma el folio. **Tabla ya creada y aplicada en la compartida (2026-10-22, script `06`, probado en Docker).** Falta que el backend la use.

**Pruebas (de todas las partes):** orden estable con camiones creados en el mismo milisegundo; conexo agregado e importado con su id del catálogo y su categoría; captura repetida después de reiniciar la API no crea otra solicitud en NEXO.

---

## HU-027 — Contrato web que necesita el frontend

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Backend (Duban), Frontend (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** comercial, **quiero** poder recargar el detalle de una cotización, crear una versión nueva cambiando la carga de cualquier ruta y escoger la clase de contenedor del catálogo real, **para** trabajar en Cotifly sin perder datos ni depender de listas fijas.

### Contexto técnico
Huecos encontrados al construir el frontend contra el contrato real (`openapi.yaml`, 2026-10-15 y 2026-10-16). No son errores: son funciones que el contrato no tiene. Si no se hacen, hay que recortar el alcance de esas HU de frontend.
- **Detalle:** no existe `GET /web/cotizaciones/{folio}`. El frontend combina `GET /web/mis-cotizaciones` (cliente, NIT, `estado_envio`, `ganada`, `deadline`, `comercial`) con `GET .../costeo` (rutas, camiones, mercancías, conexos, margen y precio). Al recargar la página no tiene de dónde sacar la cabecera.
- **Nueva versión:** `RutaNuevaVersion` solo trae línea, origen, destino y margen; no trae mercancías, conexos ni condiciones. `DatosCargaWeb` es una sola carga sin ruta, y con más de una ruta `POST .../costeo/re-solicitar` responde 409 `ESTADO_NO_PERMITE` (`VARIAS_RUTAS`). Hay un trabajo pausado por el freeze ("DatosNuevaVersion ampliado", HU-004.4) en el stash de Sara.
- **Clase de contenedor:** el backend ya lee `GET /api/catalogos/clases-contenedor` de NEXO, pero solo para validar. `CatalogosFlete` trae `tipos_vehiculo`, `tipos_carroceria` y `clases_viaje`, sin `clases_contenedor`. Hoy no hay un error activo (las descripciones de NEXO coinciden con la lista fija del frontend), pero hay riesgo de que se desincronicen. Además, `openapi.yaml` (`SolicitudCosteoWeb.clase_contenedor`) todavía menciona los 5 códigos fijos que HU-023.1 retiró.
- **Correo:** el asunto por defecto es `Cotización {folio} — Botero Soto` y el cuerpo es fijo; el precio solo va en el PDF.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Recargar el detalle
Dado una cotización del comercial
Cuando el frontend pide su detalle por folio
Entonces recibe en una sola respuesta la cabecera, las rutas, los camiones, las mercancías, los conexos, el margen y el precio

Escenario: Versión nueva cambiando una ruta de varias
Dado una cotización con tres rutas
Cuando el comercial cambia las mercancías de la ruta 2 y crea la versión nueva
Entonces se crea -vN, solo se replanifican los camiones de la ruta 2 y las otras rutas se copian igual

Escenario: Clase de contenedor del catálogo
Cuando el frontend pide GET /web/catalogos-flete
Entonces recibe clases_contenedor con las descripciones de NEXO
```

### Trazabilidad (event storming)
Fuente: preguntas del frontend sobre HU-021-FE.7 (#75), HU-021-FE.8 (#76) y HU-021-FE.11 (#79), respondidas contra `openapi.yaml` y el código de QA.

### Alcance en este repo (`Cotifly`)
1. Las cuatro partes del contrato (HU-027.1, partes A a D).

**HU también en:** `clientepro-frontend` (HU-021-FE.7, FE.8 y FE.11 consumen estos cambios).

#### Issues en `Cotifly`

### [HU-027.1] [Backend] Completar el contrato web que necesita el frontend
**Labels:** `backend`, `contrato`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-027 (milestone). La consumen HU-021-FE.7, FE.8 y FE.11 (#75, #76, #79).**

**Parte A — `GET /web/cotizaciones/{folio}`: detalle completo de una cotización.**
- [ ] Endpoint nuevo que devuelva la cabecera de `CotizacionResumen` junto con el costeo (`CosteoCotizacion`), en una sola llamada. Solo el dueño; 404 si no es suya.
- [ ] Incluir `row_version` (y el `ETag`) para las acciones que siguen.
- [ ] Contrato en `openapi.yaml` y en `docs/docs_front/CONTRATO_API_WEB.md`.

**Parte B — Nueva versión por ruta: datos completos y cambios con varias rutas.**
- [ ] `GET /web/cotizaciones/{id}/nueva-version`: agregar a cada `RutaNuevaVersion` sus mercancías, sus conexos y sus condiciones (contenedor, horas, valor de la mercancía, peso). Revisar el WIP pausado "DatosNuevaVersion ampliado" (HU-004.4) que Sara tiene en su stash.
- [ ] `POST .../costeo/re-solicitar`: aceptar `datos_carga` por ruta (con `linea_orden` o `linea_solicitud_id`) para cotizaciones de varias rutas. Solo se replanifican los camiones de las rutas que cambian; las demás se copian igual. Mantener la forma actual para una ruta.

**Parte C — `clases_contenedor` en `GET /web/catalogos-flete`.** Era la parte B de HU-023.1 en el plan del Sprint 5 y quedó sin hacer.
- [ ] Agregar `clases_contenedor` a `CatalogosFlete` (`OpcionCatalogo`: `valor` = `etiqueta` = `descripcion` de NEXO, como `clases_viaje`), con la misma caché y el mismo comportamiento si NEXO no responde.
- [ ] Corregir `openapi.yaml` (`SolicitudCosteoWeb.clase_contenedor` y `RutaCapturaWeb`): quitar los 5 códigos fijos que HU-023.1 retiró.
- [ ] Avisarle al frontend para que retire su lista fija.

**Parte D — Total de la solicitud en el correo. Pendiente de decisión de negocio antes de construir.**
- [ ] Confirmar con negocio si el correo debe mostrar el total de la solicitud en el asunto, en el cuerpo o en ninguno.
- [ ] Si se aprueba: incluirlo en `clsPlantillaCorreo` y en `POST .../correo/vista-previa`, en la moneda de la cotización.

**Pruebas (de todas las partes):** detalle con dueño y con ajeno (404); versión nueva cambiando una ruta de tres; ruta sin cambios conserva sus camiones y conexos; catálogo de clases de contenedor con las descripciones reales.

---

## HU-028 — Rechazo de una línea con motivo en la Mesa de Costeo de NEXO

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E3 — Integración Cotifly ↔ Nexo · **Talla:** L · **Roles:** Backend (Duban), Datos (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> **Contrato de NEXO verificado (2026-10-22):** `estadoLinea`/`motivoRechazo` ya están en QA/DEPLOY de NEXO, por línea individual. HU-028.2 (BD) ya se construyó y tiene PR. HU-028.1 (backend) y HU-028.3 (QA) siguen pendientes — ya no están bloqueadas por el contrato, solo falta construirlas.
> **Issues ya creadas en GitHub:** #182 (HU-028.2), #183 (HU-028.1), #192 (HU-028.3). Fuente cruzada: `2026-10-16_ISSUES-POR-REPOSITORIO.md` del chat de NEXO (ahí esta HU-028.2 era "DATA-4").

### Historia
**Como** comercial, **quiero** que una línea que la Mesa de Costeo de NEXO rechaza no frene mi cotización y que yo vea por qué se rechazó, **para** poder entregar el resto de la cotización al cliente.

### Contexto técnico
**Decisión de Sara (2026-10-16).** En la Mesa de Costeo de NEXO, el Administrador de Tarifas puede rechazar UNA línea con un motivo obligatorio:
- **No frena la cotización:** se puede entregar, y salen el PDF y el Excel.
- **La línea rechazada no suma al total y no lleva precio.**
- **El motivo lo ve solo el comercial** (pantalla de costeo y Mesa). El PDF y el Excel muestran la línea como **"Sin costeo"**, sin precio y **sin** el motivo.
- **Deshacer el rechazo** solo se puede antes de entregar.
- **Si se rechazan todas las líneas,** la cotización se entrega igual, con el motivo de cada una.

**Cómo está hoy Cotifly (verificado en el código):**

| Situación | Qué hace hoy | Problema con el rechazo |
|---|---|---|
| Línea **borrada** en NEXO (#134) | El camión queda `descartada` (`clsVehiculoLineaSolicitud.DescartarPorLineaBorrada`): pierde sus costos, no cuenta en los totales (`VehiculosQueCuentan`) y no recibe conexos | No guarda motivo; si todos los camiones de la ruta están descartados, la ruta queda sin flete |
| Línea **sin flete** en una cotización **entregada** | `clsDerivacionEstadoCosteo.DerivarLinea` la deja `entregada` sin costo | La ruta queda sin precio, el total en `null` y el PDF responde 409 `COSTEO_NO_LISTO` |
| Línea sin flete **antes de entregar** | `Derivar` solo da `autocosteo` si todas las líneas tienen flete | Una línea rechazada dejaría la cotización en `pendiente_costeo` para siempre |

Hoy Cotifly no puede distinguir una línea rechazada de una pendiente ni de una borrada.

**Lo que se le pide a NEXO** en `GET /api/solicitudes/{id}`, por línea (y si aplica en `GET /api/cotizaciones/{id}`):

| Campo | Tipo | Para qué |
|---|---|---|
| `estadoLinea` (o un valor nuevo en el estado de la línea) | enum, con `Rechazada` | Distinguir rechazada de pendiente y de borrada |
| `motivoRechazo` | texto, obligatorio si está rechazada | Mostrárselo al comercial |
| `rechazadaAt` y usuario (opcional) | fecha y usuario | Auditoría |

**Falta acordar con NEXO:** si `CotizacionDto` omite la línea rechazada o la trae sin flete con su estado, y cómo se ve "se rechazaron todas".

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Una ruta rechazada no frena la cotización
Dado una cotización de tres rutas donde la Mesa rechazó la ruta 2 con motivo
Cuando NEXO entrega la cotización
Entonces el total es la suma de las rutas 1 y 3
Y el PDF muestra la ruta 2 como "Sin costeo", sin precio y sin motivo
Y el comercial ve el motivo en la pantalla de costeo

Escenario: Deshacer el rechazo antes de entregar
Dado una línea rechazada en una cotización que sigue en la Mesa
Cuando NEXO la des-rechaza
Entonces Cotifly la vuelve a mostrar como pendiente de costeo y borra el motivo

Escenario: Todas las rutas rechazadas
Dado una cotización con todas sus rutas rechazadas
Cuando NEXO la entrega
Entonces la cotización queda entregada, sin total, con el motivo de cada ruta visible
```

### Trazabilidad (event storming)
Fuente: decisión de Sara del 2026-10-16 (con el chat de NEXO) y revisión del código de QA (#177).

### Alcance en este repo (`Cotifly`)
1. Backend: lectura del rechazo y todo lo que lo expone (HU-028.1, partes A y B).
2. [BD] Columnas del rechazo (HU-028.2).
3. [QA] Prueba de punta a punta (HU-028.3).

**HU también en:** `NEXO` (rechazo en la Mesa de Costeo), `clientepro-frontend` (mostrar el estado y el motivo).

#### Issues en `Cotifly`

### [HU-028.1] [Backend] Rechazo de una línea: leerlo de NEXO y reflejarlo en todo Cotifly
**Ya creada en GitHub: #183.**
**Labels:** `backend`, `integracion`, `nexo`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-028 (milestone). Depende del contrato de NEXO y de HU-028.2 (BD).**

**Parte A — Lectura del costeo (polling) y estados.**
- [ ] Leer el estado y el motivo de cada línea de la foto de NEXO (`clsCosteoConsultadoNexo`, `clsLineaCosteoNexo` o las líneas de la solicitud).
- [ ] Estado nuevo del camión `rechazada`, distinto de `descartada` (que es "línea borrada", #134), con `motivoRechazo`.
- [ ] Deshacer el rechazo: mientras la cotización no esté entregada, un camión `rechazada` no es terminal. Si NEXO lo des-rechaza, vuelve a `pendiente_costeo` y se borra el motivo. Entregada, el rechazo es definitivo.
- [ ] `clsDerivacionEstadoCosteo.Derivar`: `autocosteo` cuando todas las líneas **no rechazadas** tienen flete.
- [ ] `clsResumenEstadoCosteo` y `VehiculosQueCuentan`: ignorar las `rechazada`, igual que las `descartada`. Una ruta con todos sus camiones rechazados queda `rechazada`, sin precio, y no bloquea la cotización.
- [ ] Emparejamiento (#134): una línea rechazada sigue existiendo en NEXO, no es "línea sin dueño".

**Parte B — Todo lo que expone el rechazo: totales, PDF, Excel, contrato web, versiones y resultado.**
- [ ] `precio_final` = suma de las rutas **no rechazadas**. Ruta rechazada: `precio_final_linea = null`; ajustar el margen o agregar conexos en ella → 409 con un código claro (por ejemplo `RUTA_RECHAZADA`).
- [ ] Ruta con algunos camiones rechazados: precio de los vigentes; los conexos se pegan al primer camión **no rechazado** (`clsServiciosConexosCosteoService`).
- [ ] PDF (`POST .../costeo/pdf`): las rutas rechazadas no cuentan en la regla "todas con precio"; se muestran "Sin costeo", sin precio y sin motivo, en el resumen del pliego y en su sección.
- [ ] **Por definir con Sara:** con todas las rutas rechazadas, ¿se permite el PDF o se bloquea con un 409 propio (propuesta: bloquearlo)?
- [ ] Excel de la licitación (`GET /web/licitaciones/{folio}/excel`): la fila rechazada sale "SIN COSTEO", sin precio y sin motivo; no cuenta en el total de "Resumen Cotifly"; igual en "Camiones Cotifly".
- [ ] Métricas (ADR-016): el valor cotizado excluye las rutas rechazadas.
- [ ] `GET .../costeo`: exponer en `RutaCosteo` y `CamionCosteo` el estado `rechazada` y `motivo_rechazo` (cambio de enum: avisarle al frontend).
- [ ] `CotizacionResumen` ("Mis cotizaciones" y el listado regional): el resumen del estado ignora las rechazadas; el total sale sin ellas.
- [ ] `Licitacion.lineas[]` (pantalla): estado "Rechazada en NEXO" con su motivo; las rechazadas no son pendientes.
- [ ] Todas las rutas rechazadas: la cotización queda `entregada`, sin total y con el motivo de cada ruta visible; el polling se detiene.
- [ ] **Por decidir:** ¿la `-vN` copia la ruta rechazada (y NEXO la vuelve a evaluar) o la omite? Propuesta: copiarla, avisarle al comercial y dejarle quitarla. En el mismo folio, la re-solicitud vuelve a capturar la línea y el rechazo anterior queda en la auditoría.
- [ ] Marcar ganada con rutas rechazadas debe funcionar.

### [HU-028.2] [BD] Columnas del rechazo — ✅ LISTO (2026-10-22)
**Ya creada en GitHub: #182** (abierta por @jpvelez-bts). **PR: [#200](https://github.com/InnovacionIA-proyectos/Cotifly/pull/200) → `QA`.**

> El contrato de NEXO ya se verificó (chat de NEXO, contra `origin/QA` y `origin/DEPLOY` de NEXO): `estadoLinea = "rechazada"` y `motivoRechazo` ya están en QA/main de NEXO (S6-HU-24.1/#309, S6-HU-24.3/#320), por línea/camión individual. `RechazadaPor`/`RechazadaAt` no se pueden leer en ninguna reconsulta todavía, así que el script **no agrega `rechazadaAt`**; tampoco se tocó el CHECK de `CotizacionesSolicitud` (cabecera) porque una cotización totalmente rechazada se sigue entregando como `entregada`, no `rechazada`.
>
> **Script:** `db/scripts/10_rechazo_linea_camion.sql` (ya no está en `pendientes/`, esa carpeta se eliminó — HU-024.1). Agrega `rechazada` al CHECK de `estadoCosteo` del camión y de la ruta, y `motivoRechazo NVARCHAR(500) NULL` en el camión. Probado en Docker (datos previos intactos, idempotente, rechaza estados inventados, compatible con el `03`) y **aplicado en la compartida**.
**Labels:** `datos`, `base-de-datos`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-028 (milestone). Ya no bloquea a HU-028.1: el script está aplicado.**

- [x] Script en `db/scripts/`: `CotizacionVehiculosLineaSolicitud` con `rechazada` en el CHECK de `estadoCosteo` y `motivoRechazo NVARCHAR(500) NULL`.
- [x] Revisado el CHECK de `estadoCosteo` en `CotizacionLineasSolicitud` (ruta): también admite `rechazada`. `CotizacionesSolicitud` (cabecera) se deja igual, a propósito.
- [x] Probado en Docker (idempotente, en una transacción) y aplicado en la compartida.

### [HU-028.3] [QA] Rechazo de línea de punta a punta
**Ya creada en GitHub: #192.**
**Labels:** `qa`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA)
**Parte de HU-028 (milestone). Depende de HU-028.1 y HU-028.2.**

- [ ] Una ruta rechazada de tres: total, PDF y Excel sin la ruta, y el motivo visible solo en la pantalla del comercial.
- [ ] Rechazar y deshacer antes de entregar.
- [ ] Todas las rutas rechazadas.
- [ ] Licitación con una fila rechazada (Excel exportado).
- [ ] Versión nueva de una cotización con una ruta rechazada.

---

## Relación con las HU que ya existen

| HU existente | Qué pasa | Dónde se trabaja |
|---|---|---|
| HU-017.1 ("Margen promedio" contra la política) | Sigue pendiente de la decisión de negocio (opción A u opción B). El margen es por ruta, y no está decidido si el promedio es simple o ponderado por costo | En la misma HU-017.1, cuando negocio decida |
| HU-021-FE.7, FE.8 y FE.11 (`clientepro-frontend`) | Consumen HU-027 | Frontend, después de HU-027 |
| HU-024 (scripts de la base, Sara) | El script de HU-028.2 y el `06` de HU-026.1 entran a `db/scripts/` con esa organización | HU-024 |

## Para saber, sin acción

- **El `07` (`BUSQUEDA_AGOTADA`) es de Duban** (`db/scripts/07_motivo_busqueda_agotada.sql`, #176). Sara lo aplica en la compartida.
- **Lo que falta del contrato de captura es de NEXO (#297):** devolver y ordenar por `numeroLinea` en el GET, y que una línea de Cupo sin vehículo no se autocostee.
- **En el portal de NEXO no existen** la bandeja de vehículos en cuarentena (S5-HU-19.3) ni editar el vehículo y poner el precio sin vehículo en la Mesa (S5-HU-19.4).
- **Peso con tara:** NEXO planea que el "peso total" de la línea incluya la tara. El `peso_kg` que manda Cotifly es solo la carga. Falta decidir quién suma la tara antes de que NEXO cambie su validación.
  - **Detalle nuevo (2026-10-22, vía `clientepro-frontend`):** la comercial aclaró que, a su juicio, `pesoKg` **siempre** debería incluir la tara, incluso en rutas CON mercancías itemizadas — no solo en Contenedor/Cupo/Devolución Contenedor (donde hoy ya funciona así, sin problema: `clsSolicitudCosteo.cs:194` no compara contra nada si no hay posiciones con peso).
  - **Choca con el código de hoy:** `clsSolicitudCosteo.cs:201-204` (`PESO_INCONSISTENTE`) exige que `pesoKg` cuadre dentro del 1% contra `sum(cantidad × peso_kg)` de las mercancías (que es neto, sin tara). Si `pesoKg` siempre trae tara, esa validación fallaría en casi cualquier ruta con mercancías reales (la tara no es despreciable).
  - **No construir nada en el frontend** que asuma "siempre con tara" hasta que esto se decida — rompería la captura de rutas con mercancías tal como está el código. Si se decide que sí, el ajuste de backend es mediano: tocar la regla de `PESO_INCONSISTENTE` (quitar la comparación, o restar una tara conocida antes de comparar) y probablemente el contrato con NEXO.

---

> **Documento original:** `2026-10-20_PENDIENTES_DUBAN.md` · **Creado:** 2026-10-20

# Pendientes de backend para Duban — CERRADO (2026-10-22)

**Fecha original:** 2026-10-20 · **Repo:** `Cotifly`.

> **Estado final (2026-10-22):** los 5 temas de este documento ya se resolvieron. Dos quedaron superados por issues que ya existían en GitHub (no se crearon duplicados); los otros 3 ya están creados como issues reales. Este archivo queda solo como historial — lo vigente está en GitHub.

---

## Resultado de cada tema

| Tema original | Resultado |
|---|---|
| 1. Detalle regional + Excel para `gerente_comercial` | Creado: milestone [HU-034](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/26), issue [#201](https://github.com/InnovacionIA-proyectos/Cotifly/issues/201) (Parte A) |
| 2. `serie_mensual` sin ganancias/costeo por mes | Creado: mismo issue [#201](https://github.com/InnovacionIA-proyectos/Cotifly/issues/201) (Parte B) |
| 3. Correo/D3 — candidato MailSender API | **No se creó nada — ya existía.** Superado por [HU-033 / #199](https://github.com/InnovacionIA-proyectos/Cotifly/issues/199), "Decisiones del líder técnico (2026-10-22)": adopta MailSender, protección fuera de Production (buzón `[QA]`), precio sin desglose, resultado diferido a NEXO. |
| 4. Endpoint de líneas de negocio para Comercial | **No se creó issue nueva — se solapaba.** Se agregó como [comentario en #154](https://github.com/InnovacionIA-proyectos/Cotifly/issues/154#issuecomment-6346132852) (HU-016.1, Parte B "Opción A"), que ya proponía casi lo mismo para otro caso de uso (margen promedio). Ese issue además aclara que el margen **no** es confidencial para Comercial (ya se expone vía el chat, herramienta `obtener_margen`) — contradice la suposición inicial de este documento. |
| 5. Importar mercancías de una ruta desde Excel | Creado: milestone [HU-035](https://github.com/InnovacionIA-proyectos/Cotifly/milestone/27), issue [#202](https://github.com/InnovacionIA-proyectos/Cotifly/issues/202) |

## Hallazgo adicional, importante

Al verificar contra GitHub antes de crear estos issues, apareció **[HU-030 / #184](https://github.com/InnovacionIA-proyectos/Cotifly/issues/184)** ("Peso con tara en lo que Cotifly manda a Nexo", decisión D2/D2b, no construida todavía): el Comercial debe escribir el peso **con tara** y ese valor (no el neto) se manda a NEXO como peso de la línea. Esto contradice una respuesta que se le había dado a `clientepro-frontend-fe` sobre `pesoKg` (se le corrigió directamente). Si se construye algo relacionado con peso/tara, coordinarlo con esa issue en vez de resolverlo por separado.

---

> **Documento original:** `2026-10-27_HU_multirol_y_margen.md` · **Creado:** 2026-10-27

# HU-040 (multi-rol) y HU-041 (margen/conexos en todas las líneas)

> **Estado:** creadas en GitHub el 2026-10-27. Milestone HU-040 (#32): issues #229 (HU-040.1) y #230
> (HU-040.2). Milestone HU-041 (#33): issue #231 (HU-041.1). **Fecha de redacción:** 2026-10-27.
> **Convención:** mismo formato de `docs/backlog/2026-10-19_HU_Sprint6_Cotifly_Backend.md`. Issues de
> backend agrupadas al máximo (una por milestone, partes A/B/C) para no fragmentar el trabajo de Duban.
> Repo de todos los issues de este documento: `Cotifly`. Roles: **Duban** = Backend, **Sara** = Datos y
> Frontend.

## Resumen

| HU | Issue | Título | Depende de |
|---|---|---|---|
| HU-040 | HU-040.1 | [Datos] Varias asignaciones activas por usuario | — |
| HU-040 | HU-040.2 | [Backend] Elegir rol al entrar (login multi-rol + endpoint + ADR) | HU-040.1 |
| HU-041 | HU-041.1 | [Backend] Margen y servicios conexos para todas las líneas en una operación | — |

**1 issue para Datos (Sara), 2 para Backend (Duban).** El front (`clientepro-frontend`) no se toca hasta
que estos contratos existan — así quedó acordado con ese chat en ambos casos.

---

## HU-040 — Multi-rol: elegir el rol de verdad al entrar

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** por definir · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Backend (Duban), Datos (Sara), Frontend (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** usuario con más de un rol externo asignado en ClientePro (por ejemplo comercial y gerente comercial), **quiero** escoger con cuál entrar a Cotifly y quedar limitado de verdad a ese rol, **para** que el sistema nunca me deje usar permisos de un rol que no elegí.

### Contexto técnico
**Por qué existe esta HU.** En HU-037 (Rol Director, 2026-10-26) se decidió resolver esto por **jerarquía**: un solo rol en el JWT, siempre el más alto (`director` > `gerente_comercial` > `comercial`), y el front deja escoger solo la **vista** de ese rol o de los roles menores — no cambia los permisos reales. Esa opción ya está implementada (front) y cubre el pedido original de Sara. HU-040 es la otra opción que se dejó como deuda explícita: multi-rol de verdad, donde escoger un rol menor también restringe de verdad lo que esa sesión puede hacer. Solo vale la pena construirla si hace falta que alguien quede realmente limitado al elegir un rol menor (hoy nadie lo necesita; se deja documentada para cuando surja ese caso).

**Cómo funciona hoy (verificado en el código, 2026-10-26/27):**
- `dbo_cotifly.UsuariosRoles` tiene el índice único filtrado `uq_usuariosRoles_usuarioId_activo (usuarioId) WHERE activo = 1` (`01_crear_esquema.sql:149`): un usuario solo puede tener **una** asignación activa.
- `clsVerificadorIdentidad.ResolverRol` (`clsVerificadorIdentidad.cs:162-180`) traduce los roles externos de ClientePro al catálogo de Cotifly y, si matchean más de uno, siempre devuelve `GerenteComercial` (precedencia fija, sin importar cuáles matchearon).
- `clsVerificadorIdentidad.VerificarAsync` (`clsVerificadorIdentidad.cs:129-131`) exige que la asignación guardada de un usuario ya existente sea exactamente una y coincida con el rol recién resuelto; si no, rechaza con 401 `rol_externo_cambio` y nunca se re-sincroniza sola.
- `clsSolicitudLoginClientePro.Roles` (lo que manda el front en el login) es **solo informativo**: se audita, nunca decide el rol (ADR-009). El rol real sale únicamente de lo que el almacén de identidad externo tiene asignado a ese usuario en ese momento.
- No existe ningún endpoint de self-service para que un usuario cambie su propio rol guardado; hoy solo se corrige a mano en la base (como se hizo el 2026-10-26 con la cuenta de pruebas `dguerra`).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Usuario con dos roles elige uno
Dado un usuario cuyo almacén de identidad externo le da comercial y gerente comercial
Cuando inicia sesión
Entonces Cotifly le informa los dos roles disponibles, sin emitir todavía un token de trabajo

Escenario: Elegir un rol
Dado el escenario anterior
Cuando el usuario elige "comercial"
Entonces el token emitido lleva el rol comercial y esa sesión no puede usar endpoints de gerente comercial

Escenario: Rol elegido ya no está disponible
Dado un usuario que eligió gerente comercial
Cuando el almacén externo ya no le da ese rol
Entonces el refresh (o el siguiente login) lo rechaza y queda en revisión, igual que hoy con rol_externo_cambio

Escenario: Usuario con un solo rol
Dado un usuario con un único rol externo mapeado
Cuando inicia sesión
Entonces entra directo con ese rol, sin pantalla de selección
```

### Trazabilidad (event storming)
Fuente: pedido de Sara (2026-10-27, "si cada rol tiene una vista diferente, el usuario tiene que poder escoger con cuál entrar"), comparado contra la decisión de jerarquía de HU-037 (2026-10-26) y verificado contra el código de `clsVerificadorIdentidad.cs` y `01_crear_esquema.sql` en `origin/QA`.

### Alcance en este repo (`Cotifly`)
1. Varias asignaciones activas por usuario (HU-040.1, Datos).
2. Elegir rol al entrar: login, endpoint y ADR (HU-040.2, Backend, partes A/B/C).

**HU también en:** `clientepro-frontend` (selector llama al endpoint nuevo y guarda el token elegido) — **no construir nada ahí hasta que exista HU-040.2.**

#### Issues en `Cotifly`

### [HU-040.1] [Datos] Varias asignaciones activas por usuario en `UsuariosRoles`
**Ya creada en GitHub: #229.**
**Labels:** `datos`, `base-de-datos`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-040 (milestone). La consume HU-040.2.**

- [ ] Reemplazar `uq_usuariosRoles_usuarioId_activo (usuarioId) WHERE activo = 1` por algo que permita varias filas activas del mismo usuario, sin perder la protección contra duplicar el mismo rol dos veces (`uq_usuariosRoles_usuarioId_rolId` ya cubre eso).
- [ ] Revisar si `clsRolRepository.ListarRolesAsignadosAsync` necesita cambiar su forma de devolver resultados (hoy asume relación 1 a 1 en los consumidores).
- [ ] Script no destructivo: no se borra ni se cambia ninguna fila existente, solo se afloja la restricción.
- [ ] Probar en Docker: un usuario con dos filas activas no rompe nada que ya exista (login sigue sin usar esto hasta HU-040.2).

### [HU-040.2] [Backend] Elegir rol al entrar: login, endpoint y ADR
**Ya creada en GitHub: #230.**
**Labels:** `backend`, `seguridad`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-040 (milestone). Depende de HU-040.1.**

**Parte A — El login devuelve los roles disponibles, no uno solo.**
- [ ] `ResolverRol` devuelve la lista completa de roles que matchean (hoy colapsa a `GerenteComercial` cuando hay más de uno).
- [ ] `VerificarAsync`: con varios roles disponibles, no emite sesión todavía — responde los roles disponibles para que el front muestre el selector. Con uno solo, sigue emitiendo la sesión directo, igual que hoy.
- [ ] Ajustar el chequeo de `rol_externo_cambio`: ya no compara contra una única asignación, sino contra el conjunto de asignaciones activas del usuario.

**Parte B — Endpoint para elegir el rol.**
- [ ] `POST /api/auth/elegir-rol`: recibe el rol elegido, valida que siga estando entre los roles externos vigentes de ese usuario en ClientePro, y si es así emite el token de acceso y el refresh con ese rol.
- [ ] El refresh recuerda el rol elegido (no vuelve a preguntar en cada renovación).
- [ ] Auditar cada elección en `EventosHerramienta`, igual que el login.

**Parte C — Documentación.**
- [ ] ADR nuevo que reemplace la parte de ADR-009 que dice "con los dos roles internos gana gerente_comercial" y la decisión de jerarquía de HU-037, dejando claro cuándo aplica jerarquía (rol único, sigue existiendo para quien no necesite multi-rol) y cuándo multi-rol real.
- [ ] Actualizar `openapi.yaml` con el endpoint nuevo y la respuesta del login cuando hay varios roles.

**Pruebas:** usuario con dos roles ve ambos en el login y no entra hasta elegir; elige uno y el token queda limitado a ese rol; usuario con un solo rol entra igual que hoy, sin selector.

---

## HU-041 — Margen y servicios conexos para todas las líneas en una sola operación

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** por definir · **Épica:** E1 — Migración de Backend y Datos · **Talla:** S · **Roles:** Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> **Sin urgencia** (a pedido de Sara, 2026-10-26/27).

### Historia
**Como** comercial, **quiero** aplicar un margen manual o agregar/quitar un servicio conexo a todas las líneas de una cotización de una sola vez, **para** no tener que repetir la acción línea por línea ni arriesgarme a que algunas queden cambiadas y otras no si algo falla a mitad de camino.

### Contexto técnico
Hoy `PUT .../costeo/margen` y `POST/PUT .../costeo/servicios-conexos` son por `lineaOrden`. El front (`clientepro-frontend`) ya resolvió "aplicar a todas" llamando una vez por línea, en orden, encadenando el `ETag`/`rowVersion` de cada respuesta. Funciona, pero con tres problemas reportados por ese chat (2026-10-26):
- **No es atómico:** si una línea falla a mitad de camino, las anteriores ya quedaron cambiadas.
- **Fragmentado:** cada línea invalida el PDF y genera su propia auditoría por separado, en vez de una sola operación.
- **Comparación imprecisa de conexos:** el costeo por línea no devuelve el código del catálogo (`servicioId`) de cada conexo ya agregado, así que para saber si es "el mismo conexo" en todas las líneas el front compara por nombre.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Margen manual a todas las líneas
Dado una cotización con varias líneas
Cuando el comercial aplica un margen manual a todas
Entonces las líneas cambian en una sola transacción: todas o ninguna

Escenario: Revertir al margen sugerido en todas
Dado líneas con margen manual aplicado
Cuando el comercial elige "revertir al sugerido" para todas
Entonces todas vuelven al margen que sugiere la política

Escenario: Agregar un servicio conexo a todas las líneas
Dado una cotización con varias líneas
Cuando el comercial agrega un servicio conexo del catálogo a todas
Entonces cada línea queda con ese conexo, identificado por su servicioId

Escenario: Falla a mitad de camino
Dado una línea que no admite el cambio (por ejemplo ya entregada)
Cuando se intenta aplicar el margen o el conexo a todas
Entonces no se cambia ninguna línea y el error identifica cuál la bloqueó
```

### Trazabilidad (event storming)
Fuente: hallazgo de `clientepro-frontend-f3` (2026-10-26) al construir "aplicar a todas" como workaround en el front, mientras no exista el endpoint masivo.

### Alcance en este repo (`Cotifly`)
1. Endpoints masivos de margen y de servicios conexos, más `servicioId` en el costeo (HU-041.1).

**HU también en:** `clientepro-frontend` (deja de llamar línea por línea cuando exista el endpoint) — **no construir nada ahí hasta entonces.**

#### Issues en `Cotifly`

### [HU-041.1] [Backend] Margen y servicios conexos para todas las líneas en una operación
**Ya creada en GitHub: #231.**
**Labels:** `backend`, `contrato`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-041 (milestone). La consume `clientepro-frontend`.**

**Parte A — Margen masivo.**
- [ ] `PUT .../costeo/margen` (o una variante sin `lineaOrden`, o con una lista de líneas) que aplique el margen manual a todas las líneas indicadas en una sola transacción.
- [ ] "Revertir al sugerido" para todas, en la misma operación.
- [ ] Si alguna línea no admite el cambio (por estado, concurrencia, etc.), no se cambia ninguna; el error identifica cuál la bloqueó.

**Parte B — Servicios conexos masivos.**
- [ ] Endpoint equivalente para agregar o quitar un servicio conexo en todas las líneas en una sola transacción.
- [ ] Exponer `servicioId` en `ServicioConexoCosteo` (hoy el costeo por línea no lo trae), para que el front identifique el mismo conexo entre líneas sin comparar por nombre.

**Parte C — Una sola invalidación y una sola auditoría.**
- [ ] El PDF se invalida una vez por la operación completa, no una vez por línea.
- [ ] Una sola entrada de auditoría por la operación, no una por línea.

**Pruebas:** margen a 5 líneas en una llamada; revertir a sugerido en todas; conexo agregado a todas e identificado por `servicioId`; una línea bloqueada evita el cambio en las demás.
