# Matriz de pruebas HU-021 + HU-023 (campaña de QA de la integración)

> **Estado:** vigente (registro de QA de HU-023) · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

**Fecha:** 2026-10-13, actualizada el 2026-10-14 con #174 (§7) y con la integración final (§8) · **Rama:** `hu-023-integracion` (sale de `origin/QA` `cf744ff` + merges de #172, #159 y #160) · **Entorno:** SDK
`mcr.microsoft.com/dotnet/sdk:10.0` + Testcontainers (SQL Server 2019), **Nexo, cubicador, TRM, relay SMTP y modelo falsos**
(`tests/Comun/`). Las pruebas contra los sistemas reales no son parte de este documento (las hace otro frente).

Continúa [MATRIZ_PRUEBAS_HU021.md](MATRIZ_PRUEBAS_HU021.md) (sus funcionalidades 1 a 22 siguen vigentes; aquí se resumen y se agregan las
de HU-023). Rutas relativas a `backend-dotnet/`. "Unitarias" = sin `Trait Categoria=Integracion`; "Integración" = con él (Testcontainers);
"BDD" = `specs/features/*.feature` (Reqnroll, contra SQL Server).

## 1. Integración de ramas

| Paso | Commit | Conflictos |
|---|---|---|
| `origin/QA` | `cf744ff` | — |
| + `origin/172-identidad-dbsolucionesti-prueba` | `0620bc7` | ninguno textual |
| + `origin/159-hu-023-envio-nexo-cubicador` | `05b99ca` | ninguno textual |
| + `origin/160-hu-023-licitacion-cubicador` | `e9994b8` | ninguno textual |
| Coherencia + E2E | `092bdee` | ver abajo |

Las tres ramas ya traían QA (la 160 integraba la 157), así que Git no marcó conflictos en ADR-020, `.env.example`, `10-operacion`,
`DEPLOY`, CONTRATO/GUIA, `clsCosteoNexo`, `clsEnvioCosteoService`, `clsLineaSolicitud`, las pruebas de esquema ni los Nexo falsos.
Revisión de coherencia (conflictos semánticos):

- **Header `X-Portal-clsUsuario-Id`:** el código, los falsos y `clsIdentidadNexo` (#159) ya usaban el nombre nuevo; lo que trajo la #172
  (DEPLOY:166, `.env.example`:46, `10-operacion`:265, ADR-009:25, el comentario del script `operacion/revincular` y dos pruebas) seguía
  diciendo `X-Portal-Usuario-Id`. Corregido.
- **Peso declarado con 1 camión** (`clsLineaSolicitud.PesoLineaNexo`) y **emparejamiento por grupo + recomendación**: la 160 no toca
  `ALineaNexo` y la licitación en segundo plano envía con `EnviarGuardadaAsync`, que pasa por el mismo camino; el camión provisional se
  borra al cubicar (`ReemplazarCamiones` limpia `_vehiculos`), así que `UnSoloCamion` no lo cuenta. Verificado por HTTP en el E2E F1 y F2.
- **Worker de licitación** (`clsWorkerCubicacionLicitacion`) y **worker del polling** conviven en `clsDependenciasInfraestructura`
  (líneas 357-358 y 386). Sus variables (`Cubicador__MaximoParalelo`, `__ProcesadorHabilitado`, `__LicitacionEnSegundoPlano`, `__LoteRutas`,
  `__ReclamoSegundos`, `__MaximoIntentos`, `__EsperaReintentoSegundos`, `__IntervaloTickSegundos`) faltaban en `.env.example` y en
  `DEPLOY.md`. Agregadas.
- **Scripts `pendientes/04` y `operacion/revincular`:** coherentes (idempotentes, cabecera de aprobación, fuera del ejecutador). Las pruebas
  aplican `01 + 02 + 04` (instalación) y `0001..0010 + 02 + 04` (migraciones). **Riesgo de despliegue:** con la 160 integrada,
  `/health/ready` responde "esquema incompleto: falta 04" en una base sin el 04 (lo advierte `db/README.md`).

## 2. Corridas y cobertura

Build `--no-incremental` en Release: 0 warnings, 0 errores. Cada corrida en dos pasadas, como el CI: unitarias (sin `Integracion` ni `sap`)
e integración + aceptación (`Integracion`, sin `sap`), con TRX y `--coverage`.

| Corrida | Dominio | Aplicacion | Arquitectura | Infraestructura | Api | Aceptación (BDD) | Total ejecutadas | Fallidas |
|---|---|---|---|---|---|---|---|---|
| 1 | 641 | 1307 | 23 | 847 + 229 (+1 omitida `sap`) | 279 + 267 | 361 | 3954 | 0 |
| 2 | 641 | 1307 | 23 | 847 + 229 (+1) | 279 + 267 | 361 | 3954 | 0 |
| 3 | 641 | 1307 | 23 | 847 + 229 (+1) | 279 + 267 | 361 | 3954 | 0 |

Cobertura de líneas (las dos pasadas unidas; igual en las tres corridas):

| Ensamblado | Líneas |
|---|---|
| `Cotifly.Dominio` | **97,28 %** (2109/2168), umbral 90 %: cumple |
| `Cotifly.Aplicacion` | 96,90 % (4595/4742) |
| `Cotifly.Infraestructura` | 97,29 % (3416/3511) |
| `Cotifly.Api` | 97,54 % (674/691) |
| **Total** | **97,14 %** (10794/11112) |

**Inestables:** en la corrida preliminar (antes del E2E nuevo, 3946 ejecutadas) falló 1 vez `metricas_tablero.feature` "El gerente filtra el
tablero por un comercial de su regional" por interbloqueo (I1, §6). Durante el desarrollo del E2E, F2 falló 3 de 5 veces por el bug B2 hasta
fijar `Cubicador:MaximoParalelo = 1` en esa prueba; después, 8/8 en 4 repeticiones y en las 3 corridas.

## 3. Resumen por funcionalidad

| # | Funcionalidad | Unit. | Integ. | BDD | E2E (§4) | Resultado | Huecos |
|---|---|---|---|---|---|---|---|
| 1 | Login, refresh y logout | Sí | Sí | Sí (20 esc.) | F5 | **Verde** | — |
| 2 | Identidad compartida con Nexo (#172): login por `NOMBREUSUARIO`, `idUsuarioExterno` = `ID` del portal, revinculación | Sí (`clsIdentidadNexoTests`, 4) | Sí (`clsIdentidadPortalNexoTests`, 3; `clsRevincularIdentidadScriptTests`, 1) | Sí (`identidad_portal`, 3, #174) | F5 | **Verde** | El script de revinculación solo se prueba en una base aislada (el BDD hace su efecto: `idUsuarioExterno = NULL`) |
| 3 | Header `X-Portal-clsUsuario-Id` en todo adaptador | Sí (`NingunAdaptadorEscribeElEncabezadoAMano`) | Sí (los falsos responden 401 al nombre viejo) | Sí (indirecto) | F1, F5 | **Verde** | — |
| 4 | Captura con rutas, municipios, mercancías y conexos | Sí | Sí | Sí | F1 | **Verde** | — |
| 5 | Cubicador al guardar (flota mixta, sin selección, condicionada, respuesta incompleta, todo o nada) | Sí (`clsPlanificadorCamionesCubicadorTests` 18, `clsCubicadorHttpTests` 29) | Sí (`clsCubicadorEndpointsTests` 17) | Sí (`integracion_cubicador`, 3) | F1 | **Verde** | Reconsulta `GET /recomendaciones/{id}/mercancias` no implementada (opcional en la HU) |
| 6 | "Calcular cubicaje" (`POST /web/cubicacion`, `PRUEBA-<guid>`) | Sí (`clsCalculoCubicajeServiceTests` 15, `clsSugeridorMercanciaTests` 13) | Sí (`clsCalcularCubicajeEndpointsTests` 10) | Sí (`calcular_cubicaje`, 5) | F3 | **Verde** | — |
| 7 | Recalcular los camiones de una ruta | Sí | Sí (2 en `clsCubicadorEndpointsTests`) | Sí (`recalculo_vehiculos_ruta`, 5, #174) | — | **Verde** | — (audita `recalcular_vehiculos_ruta` desde #174) |
| 8 | Envío a Nexo por camión con `grupoRutaId`, `idRecomendacionVehiculo` y `codigoTipoMercancia` (banderas) | Sí (`clsCosteoNexoTests` 28) | Sí | Sí (`envio_nexo_datos_cubicador`, 1; `envio_nexo_por_camion`, 5) | F1, F2 | **Verde** | — |
| 9 | Peso declarado con 1 camión / peso del camión con varios | Sí (`clsEmparejadorCamionesTests`, 2) | Sí (E2E) | Parcial | F1 | **Verde** | — |
| 10 | Emparejamiento por grupo + recomendación (camiones idénticos, líneas barajadas) | Sí (`clsEmparejadorCamionesTests` 20) | Sí | Sí | F1 | **Verde** | — |
| 11 | Cupo de lecturas de Nexo (429 por rol) | Sí (`clsRegistroCosteoNexoTests`, `clsCosteoNexoTests`) | Sí (`clsCupoNexoEndpointsTests`, 1, #174) | No | — | **Verde** | — |
| 12 | Licitación: análisis del Excel con código de mercancía sugerido | Sí (`clsMercanciasLicitacionTests` 5) | Sí (2) | No | F2 | **Verde** | — |
| 13 | Licitación: cubicación en segundo plano (cola, reclamo entre réplicas, cupos P6, reintentos, respaldo, recuperación) | Sí (`clsCubicacionLicitacionServiceTests`, 19, #174) | Sí (`clsLicitacionCubicadorEndpointsTests` 13) | Sí (`licitacion_cubicador`, 1) | F2 (con `MaximoParalelo = 4`) y la regresión de B2 (20 licitaciones) | **Verde** | B2 corregido en #174 (§6). El worker sigue probándose por HTTP |
| 14 | Licitación: PDF del pliego y Excel exportado con los camiones | Sí (`clsPdfPliegoTests` 19) | Sí | Sí | F2 | **Verde** | — |
| 15 | Polling, estados agregados y totales | Sí | Sí | Sí | F1, F2 | **Verde** | — |
| 16 | Margen por ruta y política SAP | Sí | Sí | Sí | F1 | **Verde** | — |
| 17 | Conexos (CRUD y envío diferido al primer camión) | Sí | Sí | Sí | F1 | **Verde** | — |
| 18 | TRM USD | Sí | Sí | Sí | F7 | **Verde** | — |
| 19 | PDF, correo y regla `PDF_YA_ENVIADO` | Sí | Sí | Sí | F1 | **Verde** | — |
| 20 | Re-solicitud en el mismo folio y `-vN` | Sí | Sí | Sí | F1, F4 | **Verde** | — |
| 21 | Asistente que propone y confirma (sin montos al LLM) | Sí | Sí | Sí | F6 | **Verde** | — |
| 22 | `/resultado` y `/seguimiento` | Sí | Sí | Sí | F1, F8 | **Verde** | — |
| 23 | Métricas | Sí | Sí | Sí | F1 | **Verde** (integración final, §8) | B3 corregido en #173; I1 con `FORCESEEK` (§8.2). Queda el choque con escrituras del mismo comercial, que cubre el reintento; solución de fondo `READ_COMMITTED_SNAPSHOT` (DBA) |
| 24 | Health/ready (0010, 04 y cubicador) | Sí | Sí (`clsBaseDatosHealthCheckTests` 14) | No | — | **Verde** | — |

## 4. E2E flujo a flujo (`tests/Cotifly.Api.Tests/Web/clsE2EFlujosHU023Tests.cs`)

Todos sobre la base instalada **desde cero** (`01_crear_esquema.sql` + `02_alter_cubicador.sql` + `pendientes/04`), por HTTP contra la
API real en memoria y con los falsos. Cada uno verifica las filas que deja. 8 de 8 en verde en 5 repeticiones seguidas de la clase y en las
tres corridas completas.

| E2E | Flujo | Filas verificadas |
|---|---|---|
| F1 | Formulario de 2 rutas → cubicador (2 camiones idénticos + 1 con peso distinto del declarado) → Nexo con `grupoRutaId`/`idRecomendacionVehiculo` → polling barajado → margen de la ruta 2 → conexo diferido → PDF → correo → `PDF_YA_ENVIADO` → `-v2` → costeo y entrega de la `-v2` → ganada → métricas | camiones (`tipoVehiculo`, `pesoKg`, `idRecomendacionVehiculo`, flete por recomendación, `lineaSolicitudNexoId`), totales de cada ruta, `MargenAjustes`, `precioFinal`, `estadoEnvio`/`pdfPath`, `Correos`, la `-v2` (versión, rutas, camiones, conexos) y la original intacta, `ganada`, agenda del polling, `EventosHerramienta` |
| F2 | Licitación Excel de 10 rutas → análisis → guardado sin cubicar → cubicación en segundo plano → una sola alta a Nexo → costeo → PDF del pliego → Excel exportado | `tipoSolicitud`, `estadoCosteo`, `estadoCubicacion`/`cubicacionIntentos`/`errorCubicacion`/`cubicacionDisponibleAt`/`avisosCubicacion` (04), camiones del cubicador, `precioFinalLinea` y `precioFinal`, `pdfPath` |
| F3 | "Calcular cubicaje" | ninguna fila nueva en 5 tablas; corrida `PRUEBA-<guid>`; nada a Nexo |
| F4 | Re-solicitud en el mismo folio (costeo vencido) | `solicitudNexoId` nuevo, `estadoCosteo`, `version` = 1, agenda, `versionNegocio`, auditoría con el motivo, costeo nuevo aplicado |
| F5 | Login → identidad a Nexo → refresh → logout | `UsuariosWeb.idUsuarioExterno` = `ID` del portal, `TokensRefresco` (rotación en la familia, revocación), `TokensRevocados` (jti), header en toda petición a Nexo |
| F6 | Asistente propone y el comercial confirma | sin cambios al proponer (`versionNegocio`, `MargenAjustes`), sin montos hacia el modelo, ruta `negociado` y `MargenAjustes` al confirmar |
| F7 | TRM en USD | `moneda`, `trmAplicada` fijada al crear (no cambia con la TRM del día), costo en COP y precio en USD |
| F8 | Seguimiento y resultado | `USAR_RESULTADO_NEXO`, `ganada`/`motivoRechazo` (el comentario solo va a Nexo), descartada cerrada con el seguimiento |

## 5. Alineamiento con el event storming de HU-023

Fuente: HU-023 del Sprint 5 (criterios de aceptación e issues HU-023.1 a .5) y ADR-020. **(der.)** = nombre derivado.

### 5.1 Comandos

| Comando | Actor | Código | Registro en BD | Prueba | Estado |
|---|---|---|---|---|---|
| PedirCamionesAlCubicador (al guardar la ruta) | Sistema → Cubicador | `clsPlanificadorCamionesCubicador`, `clsCubicadorHttp` | camión por vehículo + `idRecomendacion`, `resultadoCubicacion`, `motivoSinSeleccion`, `condicionada` | `integracion_cubicador.feature`, F1 | OK |
| CalcularCubicaje (transición) | Comercial | `clsCalculoCubicajeService` | ninguno (a propósito) | `calcular_cubicaje.feature`, F3 | OK |
| RecalcularVehiculosRuta | Comercial | `clsRecalculoVehiculosService` | camiones reemplazados + `EventosHerramienta` (`recalcular_vehiculos_ruta`) | `recalculo_vehiculos_ruta.feature`, `clsCubicadorEndpointsTests` | OK (#174: E1 y E3 resueltas) |
| ReconsultarRecomendacion | Sistema | — | — | — | Desviación E4 (no implementado) |
| EnviarANexoPorCamion | Sistema | `clsEnvioCosteoService`, `clsCosteoNexo` (banderas `EnviarGrupoRutaId`/`EnviarDatosCubicador`) | `solicitudNexoId`, `enviar_costeo_nexo` | `envio_nexo_datos_cubicador.feature`, F1 | OK |
| SugerirMercanciaLicitacion | Sistema | `clsSugeridorMercancia`, `clsMercanciasLicitacion` | ninguno | `clsMercanciasLicitacionTests` | OK |
| CubicarLicitacionEnSegundoPlano | Sistema (procesador) | `clsWorkerCubicacionLicitacion`, `clsCubicacionLicitacionService` | `estadoCubicacion`, cola (04), `EventosHerramienta` (`cubicar_ruta_licitacion`) | `licitacion_cubicador.feature`, F2, `clsCubicacionLicitacionServiceTests` | OK (B2 corregido en #174) |
| ReintentarCubicacion (der., #160) | Comercial | `POST /web/licitaciones/{folio}/cubicacion/reintentar` | ídem + `EventosHerramienta` (`reintentar_cubicacion_licitacion`) | `clsLicitacionCubicadorEndpointsTests`, `clsCubicacionLicitacionServiceTests` | OK (fuera de la HU) |
| RevincularIdentidad (der., #172) | DBA | `db/scripts/operacion/revincular_…sql` | `RespaldoVinculosIdentidad_172`, `idUsuarioExterno = NULL` | `clsRevincularIdentidadScriptTests`, `identidad_portal.feature` (login y refresh) | OK |

### 5.2 Eventos de dominio

| Evento | Código | Registro en BD | Prueba | Estado |
|---|---|---|---|---|
| CamionesRecomendados | `clsRecomendacionCubicador` → `RegistrarCubicacion` | camiones + ruta | F1 | OK |
| RecomendacionRechazada (`CUBICADOR_RESPUESTA_INVALIDA`) | `clsCubicadorHttp` | ninguno (todo o nada) | `UnaRespuestaIncompletaDelCubicadorSeRechaza…` | OK |
| CubicacionSinSeleccion / Condicionada | `clsRecomendacionCubicador` | `resultadoCubicacion`, `motivoSinSeleccion`, `condicionada` | `SinSeleccionYCondicionadaSonResultados…` | OK |
| CubicacionPendiente / Cubicada / Fallida (der.) | `MarcarCubicacionPendiente`, `CompletarCubicacion`, `RegistrarFalloCubicacion` | columnas del 04 + `EventosHerramienta` (`cubicar_ruta_licitacion`, sin montos) | F2, `clsLicitacionCubicadorEndpointsTests`, `clsCubicacionLicitacionServiceTests` | OK (E1 resuelta en #174) |
| LicitacionListaParaNexo (der.) | `ListaParaEnviarTrasCubicar` → `EnviarSiListaAsync` | `solicitudNexoId` | F2, regresión de B2, `clsCubicacionLicitacionServiceTests` | OK (B2 corregido en #174) |
| LineaEmparejadaPorGrupoYRecomendacion (der.) | `clsEmparejadorLineasNexo` | `lineaSolicitudNexoId` | F1, `clsEmparejadorCamionesTests` | OK |
| CupoNexoAgotado (der.) | `clsCupoNexo` | ninguno (log) | `clsRegistroCosteoNexoTests`, `clsCupoNexoEndpointsTests` (HTTP) | OK |

### 5.3 Políticas

| Política | Estado |
|---|---|
| Todo o nada en la captura de varias rutas (HU-023.3) | OK (`SiFallaLaTerceraRutaNoSeGuardaNingunaNiSeEnviaNadaANexo`) |
| Todo o nada hacia Nexo en la licitación (no sale con una ruta pendiente o fallida) | OK (B2 corregido en #174: sale al terminar la última ruta, una sola vez) |
| Un rechazo del cubicador nunca se tapa con el respaldo; una caída sí | OK (`UnaRutaQueFallaDejaLaLicitacionSinEnviar…`, `UnaCaidaSinRespaldo…`) |
| Límite de llamadas al cubicador entre réplicas (P6) | OK (`DosReplicasNoRepitenNingunaRuta…`) |
| Sin cuarentena de vehículos en Nexo, no mandar los códigos del cubicador (HU-023.4) | Desviación E2 (decidido en ADR-020) |
| El cubicador usa Bearer y nunca `X-Portal-clsUsuario-Id` | OK (`MandaLaRutaConOrigenCotifly…Bearer`) |
| Con un solo camión viaja el peso declarado de la ruta | OK (F1) |

### 5.4 Desviaciones

- **E1 (baja). Resuelta en #174.** La cubicación en segundo plano (`cubicar_ruta_licitacion`, una fila por ruta procesada), el reintento
  (`reintentar_cubicacion_licitacion`) y el recálculo de una ruta (`recalcular_vehiculos_ruta`) dejan su fila en `EventosHerramienta`,
  con el patrón de `IClsAuditoriaService` y sin montos, pesos ni mercancías.
- **E2 (resuelta con #159).** HU-023.4 pedía una bandera para no mandar los códigos del cubicador mientras Nexo no tenga cuarentena. Con
  el contrato de Nexo #297, `Nexo:EnviarCodigosCubicador` (apagada por defecto) controla `tipoVehiculo`/`carroceria` de los camiones del
  cubicador; el envío registra lo que viajó (`envio_nexo_registrado`) y el polling empareja con eso, no con la bandera actual (§8.3).
- **E3 (baja). Resuelta en #174.** `recalculo_vehiculos_ruta.feature` (5 escenarios: recálculo con If-Match, 428, 409
  `CONFLICTO_CONCURRENCIA`, 409 `ESTADO_NO_PERMITE` ya en Nexo y ruta inexistente).
- **E4 (baja).** La reconsulta `GET /recomendaciones/{idRecomendacionVehiculo}/mercancias` no está implementada (la HU la condicionaba a que
  el detalle la necesitara).
- **E5 (media). Resuelta en #173** (B3: lo ganado sale de la versión ganada). La métrica "valor ganado" toma los valores de la versión vigente aunque la ganada sea otra (B3); ADR-016 §2 lo define así,
  pero la HU de métricas espera el valor de lo ganado.
- **E6 (baja). Resuelta en #174.** `identidad_portal.feature` (login por nombre de usuario, ID del portal en `X-Portal-clsUsuario-Id`,
  revinculación por login y por refresh con el vínculo en NULL).

## 6. Bugs

| Id | Severidad | Archivo:línea | Reproducción | Estado |
|---|---|---|---|---|
| B2 | Media | `src/Cotifly.Aplicacion/Licitaciones/Services/clsCubicacionLicitacionService.cs:252-256` (`EnviarSiListaAsync`: si el candado del envío está tomado, devuelve `false` sin volver a mirar) | Licitación de 10 rutas con `Cubicador:MaximoParalelo = 4` y el cubicador sin demora (`clsE2EFlujosHU023Tests.F2` sin el `MaximoParalelo = 1`): las dos últimas rutas terminan juntas; la que llega primero toma el candado, ve la otra todavía pendiente y no envía; la última no obtiene el candado y tampoco envía. Las 10 rutas quedan `cubicada` y Nexo no recibe nada (3 de 5 corridas). La licitación sale recién con la recuperación (`EnviarCubicadasAsync`), cuando `updatedAt` tiene más de `EsperaReenvioPendiente` (2 min): demora, no pérdida | **Corregido en #174** (§7.1) |
| B3 | Media | `src/Cotifly.Infraestructura/Metricas/Repositories/clsMetricasRepository.cs:35-95` (`SqlCotizaciones`: valores de la versión vigente, resultado de cualquier versión de la cadena) | Formulario costeado y enviado → `re-solicitar` con `nueva_version` (la `-v2` queda `pendiente_costeo`, sin precio) → entregar la original → `POST /resultado` ganada sobre la original → `GET /web/metricas/comercial`: `ganadas = 1` y `valor_ganado_cop = 0` | **Corregido en #173** (§8) |
| I1 | Inestable | `src/Cotifly.Infraestructura/Metricas/Repositories/clsMetricasRepository.cs:208-222` (`ConReintentoPorInterbloqueoAsync`, 3 intentos) | `metricas_tablero.feature` "El gerente filtra el tablero por un comercial de su regional" falló 1 vez (corrida preliminar) con `SqlException 1205` tras agotar los 3 reintentos; la lectura del tablero choca con las escrituras del polling de otras pruebas. Sin `READ_COMMITTED_SNAPSHOT` el riesgo existe también en producción | **Mitigado en #174 y `FORCESEEK` en la integración final** (§8.2); fondo: `READ_COMMITTED_SNAPSHOT` (DBA) |

## 7. Cambios de #174 (2026-10-14)

Rama `174-hu-023-licitacion-y-matriz` (sale de `hu-023-integracion`). Todo con los falsos (Nexo, cubicador, TRM, relay SMTP y modelo).

### 7.1 B2: el envío de la licitación cuando las dos últimas rutas terminan juntas

- **Causa:** `EnviarSiListaAsync` tomaba el candado del envío sin esperar. La ruta A lo tomaba, recargaba, veía la ruta B todavía
  pendiente y lo soltaba sin enviar; la ruta B, que ya había guardado, no lo obtenía y se iba. Nadie volvía a revisar.
- **Arreglo:** quien no obtiene el candado espera y reintenta (50 ms que se duplican hasta 500 ms; 35 intentos y 15,75 s de espera en
  total, con el `TimeProvider` inyectado y el token de cancelación). Cuando lo obtiene,
  la revisión ve todo lo que se guardó antes de que lo soltara el otro, así que la última ruta siempre revisa con su ruta guardada. El
  envío sigue siendo único por tres barreras: el candado, el estado (`ListaParaEnviarTrasCubicar`, que deja de valer con el
  `solicitudNexoId`) y el reclamo por `updatedAt`. La recuperación (`EnviarCubicadasAsync`) no espera el candado, porque si alguien lo
  tiene es que está enviando.
- **Regresión:** `clsE2EFlujosHU023Tests.F2_LasDosUltimasRutasQueTerminanJuntasEnvianLaLicitacionUnaSolaVezEnVeinteRepeticiones` hace 20
  licitaciones de 10 rutas con `Cubicador:MaximoParalelo = 4`, el cubicador sin demora y las dos últimas respuestas del cubicador
  sincronizadas. Sin el arreglo falla en la repetición 1 (Nexo recibe 0 altas); con él, 20 de 20. `F2` vuelve a correr con
  `MaximoParalelo = 4`. Unitarias: `SiElCandadoDelEnvioEstaOcupadoEsperaVuelveARevisarYEnvia` y
  `LaRecuperacionNoEsperaElCandadoDelEnvioSiOtroLoTiene`.

### 7.2 I1: interbloqueo 1205 del tablero de métricas

- **Causa (grafo del interbloqueo, prueba de estrés local no versionada):** en READ COMMITTED, `SqlCotizaciones` retiene el candado S
  de la fila de la cabecera (`pk_cotizaciones`) mientras lee sus rutas (`pk_cotizacionLineasSolicitud`). Un guardado del polling tiene
  la ruta en X y pide la cabecera en X. Además, el plan recorre la cabecera completa y no solo las filas de los comerciales pedidos, así
  que la lectura choca con escrituras de **otros** comerciales (en las pruebas, con el polling de otros escenarios).
- **Medición** (20 s, 4 escritores intermitentes, 30 cotizaciones por comercial):

  | Variante | Escrituras sobre otro comercial | Escrituras sobre el mismo comercial |
  |---|---|---|
  | Antes (3 intentos, 50 y 100 ms) | 5 fallos en 136 lecturas | 6 fallos en 254 lecturas |
  | #174 (6 intentos, espera exponencial con variación aleatoria de 100 ms a 1,6 s) | 0 en 19 | 1 en 68 |
  | `WITH (FORCESEEK)` en la cabecera de `SqlCotizaciones` (con 3 intentos) | **0 en 887** | 4 en 49 |

  Con el reintento nuevo casi no quedan fallos, pero hay menos lecturas porque cada una espera a que pasen las escrituras: es una
  mitigación. `FORCESEEK` quita la causa entre comerciales distintos.
- **Lo que entra en #174:** solo el reintento (`ConReintentoPorInterbloqueoAsync`, cambio mínimo, porque `clsMetricasRepository` es
  de #173). La verificación de `metricas_tablero.feature` no cambia.
- **Pendiente (fuera de #174):** el arreglo de fondo es `FROM dbo_cotifly.CotizacionesSolicitud c WITH (FORCESEEK)` en `SqlCotizaciones`,
  que lee solo las filas de los comerciales pedidos por `ix_cotizaciones_usuarioId_createdAt` y quita el choque con escrituras ajenas.
  No se aplica en #174 porque #173 modifica esa consulta por B3: el coordinador lo aplica en la integración final. El choque con escrituras del mismo comercial solo desaparece con
  `READ_COMMITTED_SNAPSHOT` (o `SNAPSHOT`) en la base, una decisión del DBA.

### 7.3 Pruebas nuevas

| Prueba | Tipo | Cubre |
|---|---|---|
| `tests/Cotifly.Aplicacion.Tests/Licitaciones/clsCubicacionLicitacionServiceTests.cs` (19) | Unitaria | Sin cupo, ruta ya reclamada, duración del reclamo, ruta cubicada (no la última), envío único con candado y reclamo, reclamo perdido, licitación ya enviada al recargar, candado ocupado (B2), recuperación con y sin candado, caída con reintento y espera, rechazo fallido al primer intento, licitación que cambió de estado, reintento sin respaldo, con respaldo (caída) y con respaldo (rechazo), folio ajeno o sin fallidas, auditoría sin montos |
| `specs/features/identidad_portal.feature` (3) | BDD | Login por nombre de usuario en minúsculas contra `NOMBREUSUARIO` en mayúsculas, `idUsuarioExterno` = ID del portal, ese ID en `X-Portal-clsUsuario-Id` de toda petición a Nexo, revinculación con el vínculo en NULL por login (misma cuenta) y por refresh |
| `specs/features/recalculo_vehiculos_ruta.feature` (5) | BDD | Recálculo con If-Match (camiones reemplazados, auditoría, sin llegar a Nexo), 428 sin If-Match, 409 `CONFLICTO_CONCURRENCIA`, 409 `ESTADO_NO_PERMITE` con la solicitud en Nexo, 400 `RUTA_INEXISTENTE` |
| `tests/Cotifly.Api.Tests/Web/clsCupoNexoEndpointsTests.cs` (1) | Integración HTTP | 429 de un catálogo con `Retry-After: 45`: los catálogos no salen de Cotifly mientras dura, el polling (`GET /api/solicitudes/{id}`) sigue y aplica el costeo, a los 44 s sigue bloqueado y a los 46 s se vuelve a leer |
| `clsE2EFlujosHU023Tests.F2_LasDosUltimasRutas…` (1) | E2E | Regresión de B2 (§7.1) |


### 7.4 Corridas de #174

Build `--no-incremental` en Release: 0 errores y 0 warnings de código (solo los 24 avisos NU1902/NU1903 de la auditoría NuGet de
`System.Security.Cryptography.Pkcs` 10.0.0, que `Directory.Build.props` deja como aviso). `dotnet test` de toda la solución en
`mcr.microsoft.com/dotnet/sdk:10.0` con Testcontainers:

| Corrida | Total | Correctas | Omitidas | Fallidas |
|---|---|---|---|---|
| 1 | 3985 | 3983 | 2 | 0 |
| 2 | 3985 | 3983 | 2 | 0 |

### 7.5 Revisión de #174 (observaciones del coordinador)

- **Fallo intermitente de F2 (MEDIA).** Reproducido con la suite completa y la regresión de B2 a 50 repeticiones
  (`COTIFLY_REPETICIONES_B2=50`): `ProcesarCubicacionAsync` contó 9 de 10 en la repetición 6 de la corrida 3. Log del procesador:
  `SqlException: Connection Timeout Expired ... pre-login handshake` en `clsCupoCubicadorSql.TomarPrimeroLibreAsync`, llamado desde la
  espera del candado del envío (`EnviarSiListaAsync`). Cada intento abre una conexión sin pool. Con la base cargada, abrirla falló
  **con la ruta ya guardada como cubicada**: la excepción salía de `CubicarAsync`, el procesador no contaba la ruta y la licitación
  esperaba la recuperación (2 min). No era un estado inconsistente en la base ni un doble envío. Arreglo:
  - un fallo al pedir el candado cuenta como "no obtenido" y se reintenta dentro del mismo tope;
  - cualquier fallo después de guardar la ruta (recargar, Nexo) se registra y la ruta queda cubicada y contada; la recuperación envía la
    licitación.
  - Un fallo al pedir el **cupo del cubicador** (antes de reclamar la ruta) no cambia: no se guardó nada, la ruta sigue pendiente y la
    vuelta siguiente la cuenta.
  - **Doble envío, encontrado en la carga y corregido.** Al repetir la carga, F2 vio dos altas para la misma licitación (repetición 23).
    Log: quien tenía el candado reclamó y obtuvo el alta en Nexo (201), pero el guardado del `solicitudNexoId` falló (`Execution
    Timeout Expired` con la base cargada). La licitación quedó en `pendiente_envio` y el candado se soltó. La ruta que esperaba (B2) lo
    tomó, la vio "lista" y la envió otra vez. Antes de B2 pasaba lo mismo con la recuperación a los 2 min (riesgo de HU-005.5); la
    espera solo lo adelantaba. Arreglo: el reclamo sube `versionNegocio`. Si al tomar el candado la versión cambió desde que la ruta
    leyó la licitación, alguien ya la reclamó (envío en curso o incierto) y no se envía; queda la regla de la recuperación. Prueba
    unitaria: `SiOtroProcesoYaReclamoElEnvioYQuedoInciertoNoSeEnviaOtraVez`.
  - F2 verifica en cada repetición que Nexo recibe exactamente un alta más y que la licitación queda `pendiente_costeo` con
    `solicitudNexoId`.
  - Con la base sin responder (`server was not found`, unos 15 s por conexión), el tope de 30 s del ayudante `ProcesarCubicacionAsync`
    se agotaba con 3 rutas todavía pendientes, sin reclamar y sin estado inconsistente. La regresión usa un tope de 90 s y la
    verificación (10 de 10) no cambia.
- **Espera del candado (BAJA).** `Task.Delay(espera, TimeProvider, CancellationToken)`. Una espera cancelada deja la licitación a la
  recuperación sin reclamar nada. Prueba del tope con `clsTiempoManual`: 35 intentos y 15,75 s; la ruta se rinde, queda contada y la
  recuperación envía la licitación.
- **Auditoría fuera del cupo (BAJA).** `cubicar_ruta_licitacion` se registra después de soltar el cupo del cubicador.
- **Métricas.** `FORCESEEK` no se aplica aquí (lo aplica el coordinador en la integración final, después de #173).
  `docs/10-operacion.md` §10.7 recomienda `READ_COMMITTED_SNAPSHOT` como decisión del DBA.

## 8. Integración final HU-023 (2026-10-14)

Rama `hu-023-integracion-final`, sale de `hu-023-integracion` (`4af9366`). Todo con los falsos (Nexo, cubicador, TRM, relay SMTP y modelo).

### 8.1 Ramas y conflictos

| Paso | Commit de la rama | Conflictos |
|---|---|---|
| + `origin/159-hu-023-envio-nexo-cubicador` (contrato Nexo #297, banderas, `envio_nexo_registrado`, peso declarado, 429) | `69c2ae4` | ninguno textual |
| + `origin/173-hu-023-correcciones-e2e` (rol 32, idempotencia, `estado_nexo`, PDF, B3, categorías) | `24a3195` | ninguno textual |
| + `origin/174-hu-023-licitacion-y-matriz` (B2, doble envío, BDD, auditoría, métricas 1205) | `2d4d82a` | ninguno textual |

Archivos tocados por más de una rama y revisados a mano: `clsEnvioCosteoService`, ADR-020, `GUIA_FRONTEND` (#159 y #173), `10-operacion` y
`clsNexoFalso` (#159 y #174), `clsMetricasRepository` (#173 y #174). Quedaron unidos sin pérdida: códigos de error, banderas
(`Nexo:EnviarGrupoRutaId`, `EnviarDatosCubicador`, `EnviarCodigosCubicador`, en `.env.example`, `DEPLOY.md` y `10-operacion`) y pruebas.
Scripts `pendientes`: `03` y `04` (anteriores), `05` y `06` (#173); no hubo números repetidos, no hizo falta renumerar.

**Conflictos semánticos (Git no los marca) corregidos en la integración:**

- **E2E F1 y F2 (`clsE2EFlujosHU023Tests`).** Esperaban `codigoTipoMercancia` en las posiciones y `tipoVehiculo` en el alta. Con #159 el
  código de mercancía no viaja (Nexo #297) y los códigos del cubicador solo con `Nexo:EnviarCodigosCubicador`. El E2E enciende la bandera
  junto con los datos del cubicador (Nexo con el contrato completo) y verifica que el código de mercancía NO viaja.
- **`clsLicitacionCubicadorEndpointsTests` (2 pruebas de #160).** Mismo motivo: encienden `Nexo:EnviarCodigosCubicador`.
- **Esperas con `DateTime.UtcNow`.** `clsCubicacionLicitacionServiceTests.AlAgotarLaEsperaDelCandado…` (de #174) se colgó (volcado
  `dumpasync`: esperando `EnviarSiListaAsync`) y `ProcesarCubicacionAsync` devolvió 6 de 10 en la regresión de B2: el reloj de la VM de
  Docker salta (§8.4) y cortaba el bucle antes de tiempo. Ahora usan `Stopwatch`, y la prueba del tope falla en vez de colgarse.

### 8.2 I1: `FORCESEEK`

`FROM dbo_cotifly.CotizacionesSolicitud c WITH (FORCESEEK)` en `SqlCotizaciones`: la lectura busca por
`ix_cotizaciones_usuarioId_createdAt` y no toma candados en filas de otros comerciales. Se mantienen los 6 reintentos de #174 para el
choque con escrituras del mismo comercial. **La solución de fondo es `READ_COMMITTED_SNAPSHOT` en la base: decisión del DBA**
(`10-operacion` §10.7).

Estrés (prueba local no versionada de #174; 20 s, 4 escritores intermitentes, 30 cotizaciones por comercial), 6 rondas:

| Escrituras sobre | Lecturas | 1205 tras los reintentos |
|---|---|---|
| Otro comercial | 5288 | **0** |
| El mismo comercial | 127 | 1 |

Métricas: `clsCalculadoraMetricas*` (23), `clsMetricasRepositoryTests` (5, incluye `-v2` ganada: lo ganado sale de la versión ganada),
`clsMetricas*` HTTP (7) y BDD de métricas y tablero (10): todas en verde.

### 8.3 Pruebas específicas

| Prueba | Resultado |
|---|---|
| Fuzz del emparejador (#159), 16 casos × 4000 cotizaciones, bandera de códigos distinta entre envío y lectura | Leyendo lo registrado en el envío: **los mismos cruces de ruta que QA** (0 en 13 casos; 5, 18 y 1 en los tres sin `grupoRutaId` con la Mesa cambiando vehículos, igual que QA). Sin registro (se asume `false`): **0**. Con la bandera actual opuesta (el error que #159 evita): hasta 140 |
| F2 (B2) con `COTIFLY_REPETICIONES_B2=50`, en paralelo con la suite completa | 3 de 3 corridas en verde (150 licitaciones): exactamente un alta a Nexo por repetición |
| Reintento con folio y timeout tras Nexo (#173), 3 rondas de `clsEnvioCosteoServiceTests` (66) | Verde: una sola solicitud en Nexo |
| Idempotencia: 4 POST concurrentes con la misma `Idempotency-Key`, 20 repeticiones (prueba no versionada) | 20 de 20: `201,201,201,201`, 1 alta en Nexo y 1 folio |
| PDF de horas y conexos (`clsPdfIncluyeConexosTests`, `clsGeneradorPdfMigraDocTests`) | 24 + 48 en verde |
| E2E de los 8 flujos (`clsE2EFlujosHU023Tests`, 9 pruebas) | 3 de 3 rondas en verde tras el ajuste de §8.1 |

### 8.4 Corridas completas

Build `--no-incremental` en Release: 0 errores y **0 warnings de código**. Avisos aparte: 24 NU1902/NU1903 de
`System.Security.Cryptography.Pkcs` 10.0.0 (12 únicos), fuera de esta integración.

| Corrida | Total | Correctas | Omitidas | Fallidas |
|---|---|---|---|---|
| 1 | 4021 | 4009 | 2 | 10 |
| 2 | 4021 | 4010 | 2 | 9 |
| 3 | 4021 | 4002 | 2 | 17 |

**Todas las fallas son `SqlException: Execution Timeout Expired` (Win32 258)** o su efecto directo (login 401 y `DbUpdateException` por
timeout al guardar), a los 0,1 a 13 s de comandos con `CommandTimeout` de 30 a 120 s. No se repiten en las mismas pruebas (36 fallas en
unas 34 pruebas distintas; solo la familia de scripts de `clsEsquemaTests` aparece en las tres). Causa medida: **el reloj de la VM de
Docker Desktop (WSL2) salta entre 0 y +121 s respecto del host** en segundos, y SqlClient mide el plazo con el reloj de pared (lo mismo que
documenta `clsAppLockSql`, #125). Recursos sin presión: SQL de Testcontainers ~1 GB de 31 GB, CPU sin saturar; disco del host al 95 %
(51 GB libres; 245 GB de imágenes recuperables, no se tocaron). Hace falta repetir las 3 corridas con el reloj sincronizado (reiniciar
Docker Desktop/WSL, decisión del usuario) para cerrar en 0 fallas.
