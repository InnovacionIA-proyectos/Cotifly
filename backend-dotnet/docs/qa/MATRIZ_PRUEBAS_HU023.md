# Matriz de pruebas HU-021 + HU-023 (campaña de QA de la integración)

**Fecha:** 2026-10-13 · **Rama:** `hu-023-integracion` (sale de `origin/QA` `cf744ff` + merges de #172, #159 y #160) · **Entorno:** SDK
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
| 2 | Identidad compartida con Nexo (#172): login por `NOMBREUSUARIO`, `idUsuarioExterno` = `ID` del portal, revinculación | Sí (`clsIdentidadNexoTests`, 4) | Sí (`clsIdentidadPortalNexoTests`, 3; `clsRevincularIdentidadScriptTests`, 1) | No | F5 | **Con huecos** | Sin escenario BDD; el script de revinculación solo se prueba en una base aislada |
| 3 | Header `X-Portal-clsUsuario-Id` en todo adaptador | Sí (`NingunAdaptadorEscribeElEncabezadoAMano`) | Sí (los falsos responden 401 al nombre viejo) | Sí (indirecto) | F1, F5 | **Verde** | — |
| 4 | Captura con rutas, municipios, mercancías y conexos | Sí | Sí | Sí | F1 | **Verde** | — |
| 5 | Cubicador al guardar (flota mixta, sin selección, condicionada, respuesta incompleta, todo o nada) | Sí (`clsPlanificadorCamionesCubicadorTests` 18, `clsCubicadorHttpTests` 29) | Sí (`clsCubicadorEndpointsTests` 17) | Sí (`integracion_cubicador`, 3) | F1 | **Verde** | Reconsulta `GET /recomendaciones/{id}/mercancias` no implementada (opcional en la HU) |
| 6 | "Calcular cubicaje" (`POST /web/cubicacion`, `PRUEBA-<guid>`) | Sí (`clsCalculoCubicajeServiceTests` 15, `clsSugeridorMercanciaTests` 13) | Sí (`clsCalcularCubicajeEndpointsTests` 10) | Sí (`calcular_cubicaje`, 5) | F3 | **Verde** | — |
| 7 | Recalcular los camiones de una ruta | Sí | Sí (2 en `clsCubicadorEndpointsTests`) | No | — | **Con huecos** | Sin BDD; no deja `EventosHerramienta` |
| 8 | Envío a Nexo por camión con `grupoRutaId`, `idRecomendacionVehiculo` y `codigoTipoMercancia` (banderas) | Sí (`clsCosteoNexoTests` 28) | Sí | Sí (`envio_nexo_datos_cubicador`, 1; `envio_nexo_por_camion`, 5) | F1, F2 | **Verde** | — |
| 9 | Peso declarado con 1 camión / peso del camión con varios | Sí (`clsEmparejadorCamionesTests`, 2) | Sí (E2E) | Parcial | F1 | **Verde** | — |
| 10 | Emparejamiento por grupo + recomendación (camiones idénticos, líneas barajadas) | Sí (`clsEmparejadorCamionesTests` 20) | Sí | Sí | F1 | **Verde** | — |
| 11 | Cupo de lecturas de Nexo (429 por rol) | Sí (`clsRegistroCosteoNexoTests`, `clsCosteoNexoTests`) | No | No | — | **Con huecos** | Sin prueba HTTP de punta a punta |
| 12 | Licitación: análisis del Excel con código de mercancía sugerido | Sí (`clsMercanciasLicitacionTests` 5) | Sí (2) | No | F2 | **Verde** | — |
| 13 | Licitación: cubicación en segundo plano (cola, reclamo entre réplicas, cupos P6, reintentos, respaldo, recuperación) | **No** (`clsCubicacionLicitacionService` sin clase de prueba propia) | Sí (`clsLicitacionCubicadorEndpointsTests` 13) | Sí (`licitacion_cubicador`, 1) | F2 | **Rojo (bug B2)** | Carrera en el envío final (§6, B2); el servicio y el worker solo se prueban por HTTP |
| 14 | Licitación: PDF del pliego y Excel exportado con los camiones | Sí (`clsPdfPliegoTests` 19) | Sí | Sí | F2 | **Verde** | — |
| 15 | Polling, estados agregados y totales | Sí | Sí | Sí | F1, F2 | **Verde** | — |
| 16 | Margen por ruta y política SAP | Sí | Sí | Sí | F1 | **Verde** | — |
| 17 | Conexos (CRUD y envío diferido al primer camión) | Sí | Sí | Sí | F1 | **Verde** | — |
| 18 | TRM USD | Sí | Sí | Sí | F7 | **Verde** | — |
| 19 | PDF, correo y regla `PDF_YA_ENVIADO` | Sí | Sí | Sí | F1 | **Verde** | — |
| 20 | Re-solicitud en el mismo folio y `-vN` | Sí | Sí | Sí | F1, F4 | **Verde** | — |
| 21 | Asistente que propone y confirma (sin montos al LLM) | Sí | Sí | Sí | F6 | **Verde** | — |
| 22 | `/resultado` y `/seguimiento` | Sí | Sí | Sí | F1, F8 | **Verde** | — |
| 23 | Métricas | Sí | Sí | Sí | F1 | **Con huecos** | Cadena ganada en una versión que no es la vigente vale 0 (§6, B3); la regional se cae por interbloqueo (inestable, §2) |
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
| RecalcularVehiculosRuta | Comercial | `clsRecalculoVehiculosService` | camiones reemplazados | `clsCubicadorEndpointsTests` | Desviación E3 |
| ReconsultarRecomendacion | Sistema | — | — | — | Desviación E4 (no implementado) |
| EnviarANexoPorCamion | Sistema | `clsEnvioCosteoService`, `clsCosteoNexo` (banderas `EnviarGrupoRutaId`/`EnviarDatosCubicador`) | `solicitudNexoId`, `enviar_costeo_nexo` | `envio_nexo_datos_cubicador.feature`, F1 | OK |
| SugerirMercanciaLicitacion | Sistema | `clsSugeridorMercancia`, `clsMercanciasLicitacion` | ninguno | `clsMercanciasLicitacionTests` | OK |
| CubicarLicitacionEnSegundoPlano | Sistema (procesador) | `clsWorkerCubicacionLicitacion`, `clsCubicacionLicitacionService` | `estadoCubicacion`, cola (04) | `licitacion_cubicador.feature`, F2 | Bug B2 |
| ReintentarCubicacion (der., #160) | Comercial | `POST /web/licitaciones/{folio}/cubicacion/reintentar` | ídem | `clsLicitacionCubicadorEndpointsTests` | OK (fuera de la HU) |
| RevincularIdentidad (der., #172) | DBA | `db/scripts/operacion/revincular_…sql` | `RespaldoVinculosIdentidad_172`, `idUsuarioExterno = NULL` | `clsRevincularIdentidadScriptTests` | OK |

### 5.2 Eventos de dominio

| Evento | Código | Registro en BD | Prueba | Estado |
|---|---|---|---|---|
| CamionesRecomendados | `clsRecomendacionCubicador` → `RegistrarCubicacion` | camiones + ruta | F1 | OK |
| RecomendacionRechazada (`CUBICADOR_RESPUESTA_INVALIDA`) | `clsCubicadorHttp` | ninguno (todo o nada) | `UnaRespuestaIncompletaDelCubicadorSeRechaza…` | OK |
| CubicacionSinSeleccion / Condicionada | `clsRecomendacionCubicador` | `resultadoCubicacion`, `motivoSinSeleccion`, `condicionada` | `SinSeleccionYCondicionadaSonResultados…` | OK |
| CubicacionPendiente / Cubicada / Fallida (der.) | `MarcarCubicacionPendiente`, `CompletarCubicacion`, `RegistrarFalloCubicacion` | columnas del 04 | F2, `clsLicitacionCubicadorEndpointsTests` | Desviación E1 (sin auditoría) |
| LicitacionListaParaNexo (der.) | `ListaParaEnviarTrasCubicar` → `EnviarSiListaAsync` | `solicitudNexoId` | F2 | Bug B2 |
| LineaEmparejadaPorGrupoYRecomendacion (der.) | `clsEmparejadorLineasNexo` | `lineaSolicitudNexoId` | F1, `clsEmparejadorCamionesTests` | OK |
| CupoNexoAgotado (der.) | `clsCupoNexo` | ninguno (log) | `clsRegistroCosteoNexoTests` | OK |

### 5.3 Políticas

| Política | Estado |
|---|---|
| Todo o nada en la captura de varias rutas (HU-023.3) | OK (`SiFallaLaTerceraRutaNoSeGuardaNingunaNiSeEnviaNadaANexo`) |
| Todo o nada hacia Nexo en la licitación (no sale con una ruta pendiente o fallida) | OK, pero con la carrera B2 |
| Un rechazo del cubicador nunca se tapa con el respaldo; una caída sí | OK (`UnaRutaQueFallaDejaLaLicitacionSinEnviar…`, `UnaCaidaSinRespaldo…`) |
| Límite de llamadas al cubicador entre réplicas (P6) | OK (`DosReplicasNoRepitenNingunaRuta…`) |
| Sin cuarentena de vehículos en Nexo, no mandar los códigos del cubicador (HU-023.4) | Desviación E2 (decidido en ADR-020) |
| El cubicador usa Bearer y nunca `X-Portal-clsUsuario-Id` | OK (`MandaLaRutaConOrigenCotifly…Bearer`) |
| Con un solo camión viaja el peso declarado de la ruta | OK (F1) |

### 5.4 Desviaciones

- **E1 (baja).** La cubicación en segundo plano, el reintento y el recálculo de una ruta solo dejan log: ninguna fila de `EventosHerramienta`
  (como D13 de HU-021).
- **E2 (decidida).** HU-023.4 pedía una bandera para no mandar los códigos del cubicador mientras Nexo no tenga cuarentena. ADR-020:315
  la descartó: los códigos viajan siempre (F2 lo muestra: `tipoVehiculo = 3S3` aunque las banderas estén apagadas en otras pruebas).
- **E3 (baja).** "Recalcular una ruta" sin escenario BDD.
- **E4 (baja).** La reconsulta `GET /recomendaciones/{idRecomendacionVehiculo}/mercancias` no está implementada (la HU la condicionaba a que
  el detalle la necesitara).
- **E5 (media).** La métrica "valor ganado" toma los valores de la versión vigente aunque la ganada sea otra (B3); ADR-016 §2 lo define así,
  pero la HU de métricas espera el valor de lo ganado.
- **E6 (baja).** Identidad #172 sin escenario BDD; solo pruebas HTTP.

## 6. Bugs (reportados, no corregidos)

| Id | Severidad | Archivo:línea | Reproducción |
|---|---|---|---|
| B2 | Media | `src/Cotifly.Aplicacion/Licitaciones/Services/clsCubicacionLicitacionService.cs:252-256` (`EnviarSiListaAsync`: si el candado del envío está tomado, devuelve `false` sin volver a mirar) | Licitación de 10 rutas con `Cubicador:MaximoParalelo = 4` y el cubicador sin demora (`clsE2EFlujosHU023Tests.F2` sin el `MaximoParalelo = 1`): las dos últimas rutas terminan juntas; la que llega primero toma el candado, ve la otra todavía pendiente y no envía; la última no obtiene el candado y tampoco envía. Las 10 rutas quedan `cubicada` y Nexo no recibe nada (3 de 5 corridas). La licitación sale recién con la recuperación (`EnviarCubicadasAsync`), cuando `updatedAt` tiene más de `EsperaReenvioPendiente` (2 min): demora, no pérdida |
| B3 | Media | `src/Cotifly.Infraestructura/Metricas/Repositories/clsMetricasRepository.cs:35-95` (`SqlCotizaciones`: valores de la versión vigente, resultado de cualquier versión de la cadena) | Formulario costeado y enviado → `re-solicitar` con `nueva_version` (la `-v2` queda `pendiente_costeo`, sin precio) → entregar la original → `POST /resultado` ganada sobre la original → `GET /web/metricas/comercial`: `ganadas = 1` y `valor_ganado_cop = 0` |
| I1 | Inestable | `src/Cotifly.Infraestructura/Metricas/Repositories/clsMetricasRepository.cs:208-222` (`ConReintentoPorInterbloqueoAsync`, 3 intentos) | `metricas_tablero.feature` "El gerente filtra el tablero por un comercial de su regional" falló 1 vez (corrida preliminar) con `SqlException 1205` tras agotar los 3 reintentos; la lectura del tablero choca con las escrituras del polling de otras pruebas. Sin `READ_COMMITTED_SNAPSHOT` el riesgo existe también en producción |
