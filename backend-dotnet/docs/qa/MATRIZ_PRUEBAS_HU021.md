# Matriz de pruebas HU-021 (#139, HU-021.12)

**Fecha:** 2026-10-09 · **Rama:** `139-hu-02112-corrección-con-bd-pruebas-y-qa` (sale de `7b585bf`) · **Entorno:** SDK `mcr.microsoft.com/dotnet/sdk:10.0` + Testcontainers (SQL Server 2019), **Nexo falso** (`tests/Comun/clsNexoFalso.cs`) y **cubicador falso** (`tests/Comun/clsCubicadorFalso.cs`). Las pruebas contra Nexo QA y la base compartida no son parte de este documento.

Rutas relativas a `backend-dotnet/`. `(n)` = métodos `[Fact]`/`[Theory]` de la clase. "Unitarias" = Dominio, Aplicacion, Arquitectura y las clases de Api/Infraestructura sin `Trait Categoria=Integracion`; "Integración" = Trait `Integracion` (Testcontainers); "BDD" = `specs/features/*.feature` (Reqnroll, también contra SQL Server).

## 1. Corridas y cobertura

| Corrida | Build | Dominio | Aplicacion | Arquitectura | Infraestructura | Api | Aceptación (BDD) | Total ejecutadas | Fallidas |
|---|---|---|---|---|---|---|---|---|---|
| Base (`7b585bf`) ×2 | 0 warnings, 0 errores | 621 | 1202 | 23 | 1031 (+2 omitidas `sap`) | 424 | 353 | 3654 | 0 / 0 |
| Final (con las pruebas nuevas) ×3 | `--no-incremental`, 0 warnings, 0 errores | 621 | 1202 | 23 | 1032 (+2 omitidas `sap`) | 427 | 353 | 3658 | 0 / 0 / 0 |

Tras las correcciones B1, D9 (y la revisión de D4), primera corrida completa: build 0 warnings; 625 + 1203 + 23 + 1032 (+2 `sap`) + 429 + 353 = **3665 ejecutadas, 0 fallos**; cobertura Dominio 97,45 %, total 97,23 %.

Tras los ajustes del validador (redondeo a 4 decimales antes de evaluar la anomalía, bordes 60,004 % y -0,004 %, re-solicitud en el mismo folio y `margen_sap_anomalo.feature`): 625 + 1203 + 23 + 1032 (+2 `sap`) + 432 + 354 = **3669 ejecutadas, 0 fallos**, 0 warnings; cobertura Dominio 97,45 %, total 97,21 %.

Sin inestabilidad: las tres corridas finales dan los mismos conteos y ningún fallo intermitente.

**Integrada con #138 (contrato de la API) y `origin/QA` (2026-10-13):** la rama recibe la cadena HU-021 ya fusionada en QA (#147–#151, #161–#163) y el contrato de #138 (campos retirados `valor_estimado`/`referencia` con aviso `CAMPOS_RETIRADOS`, bandera `Captura:AceptarVehiculoCliente`, `clsContratoDocumentoTests`, `clsContratoEjemplosDocTests` y `clsCamposRetiradosTests`). El ejemplo de `GET .../costeo` de `CONTRATO_API_WEB.md` trae `margen_sap_anomalo` y `aviso_margen`, así la prueba de ejemplos los valida contra `RutaCosteo`; el D9 (`VARIAS_RUTAS`/`LICITACION`) queda documentado en el contrato y la guía. Build 0 warnings; dos corridas completas de **3723 ejecutadas (+2 omitidas `sap`), 0 fallos**.

### 1.1 Cobertura (corrida final 1, unitarias + integración + BDD unidas)

| Ensamblado | Líneas | Umbral |
|---|---|---|
| `Cotifly.Dominio` | **97,44 %** | 90 %, cumple |
| `Cotifly.Aplicacion` | 96,80 % | solo se reporta |
| `Cotifly.Infraestructura` | 97,65 % | solo se reporta |
| `Cotifly.Api` | 97,24 % | solo se reporta |
| **Total** | **97,22 %** (10007 de 10293), ramas 88,88 % (5269 de 5928) | — |

- Umbral obligatorio (ADR-010): `Cotifly.Dominio` ≥ 90 % de líneas.
- Medición con `Microsoft.Testing.Extensions.CodeCoverage` (`--coverage --coverage-settings tests/cobertura.config.xml`; con MTP no aplica `--collect:"XPlat Code Coverage"`) y ReportGenerator 5.5.11.

## 2. Resumen por funcionalidad

| # | Funcionalidad | Endpoint / servicio | Unit. | Integ. | BDD | Resultado | Huecos principales |
|---|---|---|---|---|---|---|---|
| 1 | Login ClientePro y JWT | `POST /api/auth/login-clientepro`, `/refresh`, `/logout` · `clsLoginClienteProService`, `clsRefreshTokenService`, `clsLogoutService` | Sí | Sí | Sí | **Verde** | 429 y revocación entre réplicas sin BDD |
| 2 | Captura con rutas, municipios, mercancías y conexos | `POST /web/solicitudes-costeo` · `clsEnvioCosteoService`, `clsCapturaRutasService` | Sí | Sí | Sí | **Con huecos** | `GET /web/municipios`, `/web/clientes` y `POST /web/cubicacion` sin prueba HTTP; `clsCapturaRutasService` sin clase propia |
| 3 | Licitación Excel | `POST /web/licitaciones[/analizar-excel]`, `GET /web/licitaciones/{folio}[/excel]` · `clsLicitacionService` | Sí | Sí | Sí | **Con huecos** | `clsLicitacionService` sin unitaria directa; vista sin BDD |
| 4 | Cubicador y respaldo | `clsPlanificadorCamionesCubicador`, `clsCubicadorHttp`, `GET /web/tipos-mercancia` | Sí | Sí | Sí | **Con huecos** | Condicionada, sin respaldo y tipo fuera de catálogo sin BDD |
| 5 | Envío a Nexo por camión | `clsEnvioCosteoService`, `clsCosteoNexo` | Sí | Sí | Sí | **Verde** | — |
| 6 | Polling y emparejamiento | `clsPollingCosteoService`, `clsEmparejadorLineasNexo`, `clsWorkerCosteoNexo` | Sí | Sí | Sí | **Verde** | Mesa borra/cambia vehículo solo en unitarias |
| 7 | Estados agregados camión → ruta → cabecera | `clsResumenEstadoCosteo`, `clsLineaSolicitud`, `clsCotizacionSolicitud` | Sí | Sí (nuevo) | Parcial | **Con huecos** | Prioridades y "NULL mezclado" solo unitarias; sin BDD de ruta mixta |
| 8 | Margen por ruta y política SAP | `PUT .../costeo/margen` · `clsAjusteMargenCosteoService`, `clsMargenResolverService` | Sí | Sí (+1 nuevo) | Sí | **Verde** | B1 corregido: un margen SAP anómalo usa la política y queda avisado (§6) |
| 9 | Conexos (CRUD y envío diferido) | `GET /web/servicios-conexos`, `POST/PUT .../servicios-conexos` · `clsServiciosConexosCosteoService`, `clsEnvioConexosNexoService` | Sí | Sí | Sí | **Con huecos** | `clsEnvioConexosNexoService` sin unitarias; `envio_conexos_nexo.feature` con 1 escenario |
| 10 | Levante | `clsLineaSolicitud`, `clsVehiculoLineaSolicitud.CopiarLevante` | Sí | Sí (nuevo) | No | **Con huecos** | Sin BDD; `CambiarValorLevante` sin endpoint |
| 11 | TRM USD | `clsFijacionMonedaService`, `clsProveedorTrm*` | Sí | Sí | Sí | **Verde** | — |
| 12 | PDF de una ruta, pliego y Excel | `POST .../costeo/pdf`, `GET /pdfs/{archivo}`, `GET /web/licitaciones/{folio}/excel` | Sí | Sí (+1 nuevo) | Sí | **Con huecos** | `pdf.feature` usa la vía vieja `IClsGenerarPdfService`; sin BDD de "una ruta, dos camiones" |
| 13 | Textos IA sin montos | `POST .../costeo/textos/redactar`, `PUT .../costeo/textos` · `clsTextosCosteoService`, `clsRedactorCotizacionLlm` | Sí | Parcial | Sí | **Con huecos** | Endpoints `/costeo/textos*` sin prueba en Api.Tests |
| 14 | Asistente que propone y confirma | `POST /web/chat` (folio) · `clsAsistenteFolioRunner`, `clsHerramientasAsistenteFolio` | Sí | Sí | Sí | **Con huecos** | Carrera de turnos sin prueba HTTP; `proponer_re_solicitud` sin ruta (D9) |
| 15 | Re-solicitud y `-vN` | `POST .../costeo/re-solicitar`, `GET /web/cotizaciones/{id}/nueva-version` | Sí | Sí | Sí | **Verde** | D9 corregido (mensaje y motivo `VARIAS_RUTAS`); cambios de carga con varias rutas siguen sin soporte |
| 16 | "Mis cotizaciones" | `GET /web/mis-cotizaciones` · `clsMisCotizacionesService` | Sí | Sí | Sí | **Verde** | — |
| 17 | `/resultado` y `/seguimiento` | `POST .../resultado`, `POST .../seguimiento` | Sí | Sí | Sí | **Verde** | D4 revisado: el 409 sincroniza el resultado de Nexo, como manda ADR-018 |
| 18 | Métricas | `GET /web/metricas/{comercial,regional,actividad,ajustes-margen}` | Sí | Sí | Sí | **Con huecos** | `actividad` y `ajustes-margen` en borrador, sin BDD |
| 19 | Concurrencia | `clsConcurrenciaHttp`, `GuardarCambiosSiVersionAsync`, `clsCandadoTransaccionalSql` (`sp_getapplock`) | Sí | Sí | Sí | **Con huecos** | `PUT .../servicios-conexos` y `POST .../costeo/pdf` sin BDD de 428/409; idempotencia solo en memoria |
| 20 | Health/ready (0009 y 0010) | `GET /health`, `/health/ready` · `clsBaseDatosHealthCheck` | Sí | Sí | No | **Con huecos** | "falta 0009/0010" sin prueba HTTP; sin feature |
| 21 | Correo | `POST .../enviar`, `.../correo/vista-previa`, `GET /web/clientes/{nit}/destinatarios-previos` | Sí | Sí (+1 nuevo) | Sí | **Verde** | — |
| 22 | Admin de líneas | `GET/PUT /web/admin/lineas-negocio` | Sí | Sí | Sí | **Verde** | Servicio sin clase de prueba dedicada |

**Totales:** 10 verdes, 12 con huecos, 0 en rojo (22 funcionalidades). En la primera revisión eran 8, 13 y 1: el rojo era B1 (margen SAP anómalo), corregido en esta rama; D4 resultó conforme a ADR-018.

## 3. Detalle por funcionalidad (reglas y pruebas representativas)

### 1. Login ClientePro y JWT
- **Reglas:** rol desde el rol externo, nunca del body (con los dos gana Gerente); almacén caído → 401 genérico; access HS256 60 min con `jti`; refresh opaco (hash SHA-256) que rota por familia y cuya reutilización revoca todo; logout revoca familia y `jti`; regional desde DimEjecutivo.
- **Unit.:** `clsLoginClienteProServiceTests.cs::LosRolesDelBodyNuncaDecidenElRol` (22), `clsRefreshTokenServiceTests.cs::ReutilizarUnRefreshYaRotadoRevocaTodosLosRefreshDelUsuario` (12), `clsLogoutServiceTests.cs` (5), `clsCacheTokensRevocadosTests.cs` (4), `clsEmisorTokensJwtTests.cs::ElAccessEsHs256ConLosClaimsDelContratoYVenceEnSesentaMinutos` (7).
- **Integ.:** `clsAuthEndpointTests.cs::DeDosRefreshSimultaneosConElMismoTokenSoloUnoGana` (13), `clsTokensRepositoryTests.cs` (7), `clsDirectorioEjecutivosIntegracionTests.cs::ConDimEjecutivoInalcanzableElLoginEntraYLaRegionalNoCambia` (10).
- **BDD:** `login_clientepro.feature` (13), `refresh_logout.feature` (7).

### 2. Captura con rutas, municipios, mercancías y conexos
- **Reglas:** cada ruta con su línea de negocio; vehículo/carrocería del request se ignoran (decisión 7); municipio por id obligatorio y nombre copiado del catálogo de Nexo (decisión 2); todos los errores por ruta juntos sin guardar; conexo con el costo de Nexo como precio (decisión 3).
- **Unit.:** `clsCapturaRutasTests.cs::RegistrarCapturaCreaCadaRutaConSusCamionesConexosYCiudadesPorIdYNombre` (7), `clsEnvioCosteoServiceTests.cs::ElNombreDelMunicipioSiempreSaleDelCatalogoYUnIdInexistenteSeRechaza` (54), `clsResolvedorMunicipiosTests` (16), `clsValidadorCatalogosCapturaTests` (12).
- **Integ.:** `clsCosteoEndpointsTests.cs::ElVehiculoYLaCarroceriaQueLleganSeIgnoranYElCamionVaSinVehiculo` (14), `clsCotizacionRepositoryTests.cs::LaSolicitudDeCosteoConSuRutaCamionYMercanciasSeGuardaYSeReconstruyeSinPerderDatos` (17), **nuevo** `clsRegistrosBdFlujoCompletoTests.cs::UnFlujoCompletoDejaCadaRegistroConSusValoresFkCheckTotalesYVersionNegocio` (fila por fila en las 5 tablas de la captura).
- **BDD:** `captura_rutas.feature` (6), `catalogos_formulario_costeo.feature` (9).

### 3. Licitación Excel
- **Reglas:** análisis reenviado a Nexo sin guardar; una solicitud de N líneas; todas las filas inválidas juntas con numeración del Excel; sin catálogos no se guarda; exportación sin costos de Nexo ni fórmulas inyectadas; hasta 1000 líneas.
- **Unit.:** `clsValidadorLicitacionTests.cs::ReportaTodasLasFilasConErrorConIndiceFilaCampoYCodigo` (12), `clsExportadorExcelFidelidadTests.cs::NingunaParteDelPaqueteTraeCostos` (11), `clsCapturaFilasLicitacionTests` (10).
- **Integ.:** `clsLicitacionesEndpointsTests.cs::ElPollingCosteaLineaPorLineaYLaVistaNuncaTraeElCosto` (16), `clsLicitacionesCapturaEndpointsTests.cs` (7), **nuevo E2E** `clsCubicadorEndpointsTests.cs::LaLicitacionConElCubicadorLlegaDelExcelAlPdfDePliegoYAlResultado`.
- **BDD:** `licitaciones_excel.feature` (18).

### 4. Cubicador y respaldo
- **Reglas:** un camión por fila (vehículo, carrocería, peso C2, id C3); "sin selección" y "condicionada" son resultados (C6); caída/tiempo → respaldo de un camión sin vehículo; sin respaldo → `CUBICADOR_NO_DISPONIBLE` sin guardar; POST sin reintento.
- **Unit.:** `clsPlanificadorCamionesCubicadorTests.cs::UnaCaidaUsaElRespaldoAvisaConCodigoEstableYNoVuelveALlamarEnLaMismaCaptura` (12), `clsCubicadorHttpTests.cs::UnPostFallidoNoSeReintenta` (15).
- **Integ.:** `clsCubicadorEndpointsTests.cs::SinSeleccionYCondicionadaSonResultadosYLaSolicitudViajaCompleta`, `::SinRespaldoUnCubicadorCaidoNoGuardaNiEnviaNada`, `::SiElCubicadorSeCaeOTardaLaRutaQuedaConUnCamionSinVehiculo` (10 con el nuevo).
- **BDD:** `integracion_cubicador.feature` (3; el escenario 1 es la flota mixta 3S3/FURGON + 2S3/ESTACAS).

### 5. Envío a Nexo por camión
- **Reglas:** una línea por camión con su peso y la línea de negocio de su ruta; posiciones solo con un camión; conexos al primer camión; nota de trazabilidad para la Mesa; 503 en el POST sin reintento.
- **Unit.:** `clsAgregadoRutasTests.cs::UnaLineaANexoPorCamionConElPesoDelCamionYLasMercanciasSoloConUnCamion` (23), `clsCosteoNexoTests.cs::UnaRutaConTresCamionesViajaComoTresLineasYLaReferenciaDeRutaSoloConLaOpcion` (23), `clsRegistroCosteoNexoTests.cs::UnPostQueRecibe503NoSeReintentaParaNoDuplicarLaSolicitudEnNexo` (12).
- **Integ.:** `clsCubicadorEndpointsTests.cs::UnaRutaConTresRecomendacionesViajaEnTresLineasConSuLineaDeNegocioYElConexoVaALaFila1`, `clsIntegracionRutasEndpointsTests.cs` (2).
- **BDD:** `envio_nexo_por_camion.feature` (5), `costeo_nexo.feature::La solicitud llega a Nexo y la cotización queda pendiente de costeo`.

### 6. Polling y emparejamiento
- **Reglas:** emparejamiento por contenido en cualquier orden; camiones idénticos en orden estable; empates ambiguos no se adivinan; descartado no frena la ruta; fotos idempotentes; reclamo por réplica; `costeo_vencido` por edad.
- **Unit.:** `clsEmparejadorCamionesTests.cs::UnEmpateQueOtroCamionPuedeReclamarNoSeAdivina` (12), `clsCosteoPorCamionTests.cs::CadaCamionRecibeElFleteDeSuLineaAunqueNexoLasDevuelvaEnOtroOrden` (9), `clsPollingCosteoServiceTests.cs::ProcesarDosVecesLaMismaFotoEsIdempotente` (27).
- **Integ.:** `clsPollingCosteoEndpointsTests.cs` (6), `clsCotizacionRepositoryTests.cs::LaAgendaDelPollingTraeSoloLasVencidasYElReclamoLaTomaUnaSolaVez`, líneas barajadas con semilla en los E2E de `clsCubicadorEndpointsTests`.
- **BDD:** `envio_nexo_por_camion.feature::Dos camiones idénticos de la misma ruta se emparejan en orden estable y la ruta espera a los dos`, `costeo_nexo.feature::Una solicitud que nunca se costea queda vencida y visible`.

### 7. Estados agregados camión → ruta → cabecera
- **Reglas:** gana el más atrasado (`error_envio` → `costeo_vencido` → `pendiente_envio` → `pendiente_costeo` → `entregada` → `autocosteo`); descartados se ignoran salvo todos; precio de cabecera solo con todas las rutas con precio.
- **Unit.:** `clsAgregadoRutasTests.cs::ElResumenEsElMasAtrasadoIgnorandoLosDescartados` (Theory, 8 casos), `::ElEstadoSubeDeLosCamionesALaRutaYDeLasRutasALaCabecera`; `clsDerivacionEstadoCosteoTests` (12).
- **Integ.:** **nuevo** `clsRegistrosBdFlujoCompletoTests` (camiones `pendiente_costeo` → `autocosteo` → `entregada` en camión, ruta y cabecera), `clsCubicadorEndpointsTests.cs::LaLicitacionConElCubicadorLlega...` (pendiente con un camión faltante).

### 8. Margen por ruta y política SAP
- **Reglas:** `sap_tm` con BP y registro; `politica_linea` sin BP, sin registro, SAP caído o no configurado; `negociado` por ajuste; margen SAP fuera de [0, 60 %] = anómalo; con varias rutas el ajuste exige ruta; justificación al bajar del sugerido; el ajuste se registra con la ruta.
- **Unit.:** `clsMargenResolverServiceTests.cs::UnMargenSapAtipicoSeMarcaAnomalo` (25), `clsAjusteMargenCosteoServiceTests.cs::ConTresRutasElAjusteDeLaRutaDosSoloRecalculaEsaRutaYRegistraLaRutaReal` (16).
- **Integ.:** `clsMargenFuentesSapIntegracionTests.cs::ConODataCaidoElResolverUsaLaPoliticaSinRomperLaCotizacion` (7), `clsMargenCosteoEndpointsTests.cs` (8), **nuevo** `clsRegistrosBdFlujoCompletoTests` (fila de `MargenAjustes` con `lineaSolicitudId`, `margenPolitica`, `diferencia`, `motivo` y usuario).
- **BDD:** `margen.feature` (14), `margen_conexos_por_ruta.feature` (5), `ajuste_margen_costeo.feature` (17), `margen_sap_anomalo.feature` (1, #139).
- **B1 corregido (#139):** un margen SAP fuera de [0, 60 %] ya no se aplica: la ruta usa el de la política (`politica_linea`), el de SAP queda en `margenSAPSugerido` como marca, no cuenta como sugerido para la justificación y `GET .../costeo` trae `margen_sap_anomalo = true` y `aviso_margen`. Pruebas: `clsMargenConexosPorRutaTests.cs::UnMargenSapAnomaloQuedaMarcadoPeroNoSeAplicaNiEsElSugerido`, `::UnMargenSapDentroDelRangoNoSeMarcaAnomalo` (3 casos), `clsMargenSapAnomaloEndpointsTests.cs::UnMargenSapDe75NoSeAplicaLaRutaUsaLaPoliticaYQuedaAvisadoTambienEnLaVersionNueva` (captura y `-v2`), `::LaReSolicitudEnElMismoFolioVuelveAResolverElMargenYConservaLaMarca` (re-solicitud en el mismo folio, prueba propia), `::EnLaLicitacionCadaRutaConMargenSapAnomaloUsaLaPolitica`, `::UnMargenSapQueRedondeaDentroDelRangoSeAplicaSinMarca` (bordes 60,004 % → 0,6000 y -0,004 % → 0: el margen de SAP se redondea a 4 decimales, la precisión de la columna, ANTES de evaluar la anomalía) y BDD `margen_sap_anomalo.feature::Un margen de SAP TM de 75 % no se aplica, se avisa y el comercial ajusta la ruta` (captura → aviso → ajuste).

### 9. Conexos (CRUD y envío diferido)
- **Reglas:** catálogo de activos sin costo; agregar crea en Nexo pegado al primer camión; If-Match e Idempotency-Key; conexos capturados se envían una vez tras la entrega, con tope de reintentos (decisión 10).
- **Unit.:** `clsServiciosConexosCosteoServiceTests.cs::ConVariosCamionesElConexoViajaPegadoALaLineaDelPrimerCamionDeLaRuta` (27), `clsConexosPorRutaServiceTests.cs` (9).
- **Integ.:** `clsEnvioConexosNexoEndpointsTests.cs::DosReplicasDelPollingEnvianLosConexosUnaSolaVez` (10), `clsServiciosConexosEndpointsTests.cs` (12), **nuevo** `clsRegistrosBdFlujoCompletoTests` (fila de `CotizacionServiciosConexos`, UNIQUE y CHECK).
- **BDD:** `servicios_conexos_nexo.feature` (23), `envio_conexos_nexo.feature` (1).

### 10. Levante
- **Reglas:** el valor de la ruta se copia a cada camión (decisión 4) y suma un levante por camión; sin levante, 0; solo aplica con ciudad de levante distinta del origen.
- **Unit.:** `clsAgregadoRutasTests.cs::ElLevanteDeLaRutaSeCopiaACadaCamion`, `clsMargenConexosPorRutaTests.cs::CambiarElLevanteDeUnaRutaLoCopiaACadaCamionYRecalcula`, `clsSolicitudCosteoTests.cs::ValorAprovisionamientoSoloConUnLevanteDistintoDelOrigen`.
- **Integ.:** **nuevo** `clsRegistrosBdFlujoCompletoTests` (150.000 en cada uno de los dos camiones, `costoLevanteContenedorTotal` = 300.000, sobrevive al polling).

### 11. TRM USD
- **Reglas:** TRM fijada al crear y no cambia; COP con TRM 0; DolarApi principal, Superfinanciera respaldo; sin TRM no se guarda ni se envía.
- **Unit.:** `clsRegistroTrmTests.cs::LaPrimeraTrmDelDiaQuedaFijaAunqueDolarApiCambieEnLaTarde` (12), `clsEnvioCosteoServiceTests.cs::SinTrmNoSeGuardaNadaNiSeEnviaANexo`.
- **Integ.:** `clsCotizacionRepositoryTests.cs::UnaCotizacionEnDolaresGuardaLaTrmConCuatroDecimales`, **nuevo** `clsRegistrosBdFlujoCompletoTests` (`moneda` COP y `trmAplicada` 0).
- **BDD:** `moneda_trm.feature` (19), `moneda_formulario_costeo.feature` (11).

### 12. PDF de una ruta, pliego y Excel
- **Reglas:** nunca costos de Nexo; pliego con una sección por ruta y resumen; tarifa por viaje de cada ruta **sin conexos** y conexos aparte con su precio (PDF y Excel cuadran); folio duplicado se rechaza; nota TRM en USD.
- **Unit.:** `clsPdfPliegoTests.cs::ElExcelYElPdfDelPliegoCuadranYNoCobranDosVecesLosConexos` (19), `clsPdfCosteoConcurrenciaTests.cs` (3), `clsGeneradorPdfMigraDocTests` (18).
- **Integ.:** `clsLicitacionesEndpointsTests.cs::ElPdfDeUnaLicitacionCosteadaSaleConElLayoutDePliegoYSinCostosDeNexo`, `clsCubicadorEndpointsTests.cs::LaCadenaCompletaConElCubicador...` (paso 6), **nuevo** E2E de licitación (PDF + Excel).
- **BDD:** `pdf.feature` (10), `licitaciones_excel.feature`.

### 13. Textos IA sin montos
- **Reglas:** la IA no recibe costos, margen, precios, TRM ni el nombre del comercial; >15 s o salida inválida → texto estándar; textos editados exigen `sobrescribir`; If-Match.
- **Unit.:** `clsRedactorCotizacionLlmTests.cs::LaIaNuncaRecibeCostosMargenPreciosTrmNiElComercial` (13), `clsTextosCosteoServiceTests.cs::ElContextoDeLaIaSoloTraeNombresYNuncaMontos` (23), `clsValidadorRespuestaAgenteTests` (42).
- **Integ.:** `clsChatWebEndpointTests.cs` (9).
- **BDD:** `redaccion_cotizacion.feature` (16), `validador_respuesta_ia.feature` (4).

### 14. Asistente que propone y confirma
- **Reglas:** solo herramientas de lectura/propuesta; la propuesta no cambia nada (`CONFIRMACION_REQUERIDA`); la confirmación es el endpoint determinista con If-Match; con varias rutas pregunta cuál (decisión 6); sin montos.
- **Unit.:** `clsAsistenteFolioRunnerTests.cs::NingunMensajeAlModeloTraeMontosEnPesos` (24), `clsAsistenteMargenPorRutaTests.cs::ConVariasRutasYSinIndicarLaRutaPreguntaCual...` (6).
- **Integ.:** `clsAsistenteFolioEndpointTests.cs` (5), `clsRetiroGoogleChatTests` (5).
- **BDD:** `asistente_folio.feature` (16).

### 15. Re-solicitud y `-vN`
- **Reglas:** mismo folio si vencido/entregado/`error_envio`; con cambios de carga o ya enviada → `-vN`; ya enviada en el mismo folio → 409 `PDF_YA_ENVIADO`; Idempotency-Key.
- **Unit.:** `clsReSolicitudCosteoServiceTests.cs::YaEnviadaElMismoFolioEsPdfYaEnviadoConLaLlamadaDeLaVersionNueva` (36), `clsFolioTests` (15).
- **Integ.:** `clsReSolicitudCosteoEndpointsTests.cs::UnDobleClicConLaMismaIdempotencyKeyNoCreaDosVersionesNiEnviaDosVecesANexo` (10), `clsCubicadorEndpointsTests.cs::LaCadenaCompleta...` (paso 7).
- **BDD:** `resolicitar_costeo.feature` (22), `folio.feature`.

### 16. "Mis cotizaciones"
- **Reglas:** solo las del comercial, más reciente primero; "Varias" con rutas de distintas líneas; margen de la ruta 1; nunca el costo.
- **Unit.:** `clsMisCotizacionesYNuevaVersionTests.cs::ConRutasDeVariasLineasElResumenDiceVariasYElMargenEsElDeLaRuta1` (8).
- **Integ.:** `clsWebEndpointsTests.cs::MisCotizacionesCumpleElContratoYSoloTraeLasDelComercialDeLaMasRecienteALaMasAntigua`, **nuevo** E2E de licitación (total 3.617.647 y línea de la ruta 1).
- **BDD:** `web_cotizaciones.feature`, `aislamiento_comercial.feature`.

### 17. `/resultado` y `/seguimiento`
- **Reglas:** Nexo primero, local después; ganada sin costeo → `COTIZACION_NO_ENTREGADA`; pérdida sin costeo solo en Cotifly; `/seguimiento` → `USAR_RESULTADO_NEXO` si pasó por Nexo; ganada → motivo "No aplica".
- **Unit.:** `clsResultadoCotizacionServiceTests.cs::SiNexoYaTieneOtroResultadoSeGuardaElDeNexoYSeRespondeElConflicto` (26), `clsCotizacionTests.cs::MarcarGanadaDejaElMotivoDeRechazoEnNoAplica`.
- **Integ.:** `clsResultadoCotizacionEndpointsTests.cs` (12), E2E de formulario (paso 8) y **nuevo** E2E de licitación (paso 9).
- **BDD:** `costeo_nexo.feature` (4 escenarios de resultado), `web_cotizaciones.feature`.

### 18. Métricas
- **Reglas:** conteos por cotización (cadena de versiones una vez), valores y apertura por línea por ruta, margen ponderado por costo (decisión 8); USD a COP con la TRM de cada cotización; alcance por rol y regional.
- **Unit.:** `clsCalculadoraMetricasRutasTests.cs::UnaCotizacionConTresRutasDeDistintasLineasCuentaUnaVezEnLosConteos` (7), `clsTableroMetricasServiceTests` (10).
- **Integ.:** `clsMetricasTableroEndpointsTests.cs` (7), `clsMetricasTableroRepositoryTests.cs` (11), `clsCubicadorEndpointsTests.cs::LaCadenaCompleta...` (paso 9).
- **BDD:** `metricas_tablero.feature` (10).

### 19. Concurrencia
- **Reglas:** sin If-Match → 428; versión distinta → 409 sin escribir; ETag = `versionNegocio`; polling sin cambios no sube la versión; candado SQL retenido → 503 `SERVICIO_OCUPADO`.
- **Unit.:** `clsAjusteMargenCosteoServiceTests.cs::SiLaVersionCambiaAlGuardarRespondeConflicto`, `clsAlmacenIdempotenciaEnMemoriaTests` (11).
- **Integ.:** `clsConcurrenciaCotizacionTests.cs::ElPollingQueLeyoAntesDeUnAjusteNoPisaElPrecioAjustado` (5), `clsCandadoTransaccionalSqlTests.cs::ConElServidorCongeladoLaTomaTerminaAcotadaConServicioOcupadoYSinTransaccionesAbiertas` (6), `clsCarreraAjusteEnvioTests` (1), **nuevo** `clsRegistrosBdFlujoCompletoTests` (428 sin If-Match, 409 con ETag viejo, `versionNegocio` +1 por ajuste y sin cambio ante `PDF_YA_ENVIADO`).
- **BDD:** `ajuste_margen_costeo.feature`, `redaccion_cotizacion.feature`, `servicios_conexos_nexo.feature`, `folio.feature`.

### 20. Health/ready (0009 y 0010)
- **Reglas:** `/health` 200 sin auth; `/ready` 503 sin cadena o base caída; esquema comparado con EF, con "falta 0009"/"falta 0010"/"falta Tabla.columna".
- **Unit.:** `clsSaludListoEndpointTests.cs` (3 sin Trait), `clsSondaSaludTests` (6).
- **Integ.:** `clsBaseDatosHealthCheckTests.cs::ConEl0009PeroSinEl0010EsUnhealthyPorqueLeFaltaEl0010`, `::UnaBaseInstaladaDesdeCeroConElScriptDeInstalacionEsHealthy` (12).

### 21. Correo
- **Reglas:** adjunta el PDF, pasa a `enviada` y luego `reenviada`; duplicado en <5 min; fallo del relay registrado sin cambiar estado; destinatarios previos del propio comercial.
- **Unit.:** `clsCorreoServiceTests.cs::UnFalloDelTransporteQuedaRegistradoComoFallidoYNoCambiaElEstado` (20).
- **Integ.:** `clsCorreoRepositoryTests.cs` (4), `clsPdfCorreoIntegracionTests.cs::DosEnviosSimultaneosDejanUnoEnviadaYElSiguienteReenviada`, **nuevo** `clsRegistrosBdFlujoCompletoTests` (relay SMTP falso, fila de `Correos` y `CorreoDestinatarios` para/cc).
- **BDD:** `correo.feature` (10).

### 22. Admin de líneas
- **Reglas:** solo Gerente Comercial; margen, método y estado a la vez, auditado con antes y después; cambios inválidos no tocan la línea.
- **Unit.:** `clsCasosWebTests.cs::ElGerenteEditaLaLineaYQuedaAuditadoQuienAntesYDespues`, `clsMargenTests.cs::LaAdministracionCambiaMargenMetodoYEstadoALaVez`.
- **Integ.:** `clsPdfAdminMetricasEndpointsTests.cs::UnComercialRecibe403EnAdministracionYLaLineaNoCambia` (10).
- **BDD:** `admin_lineas_negocio.feature` (5).

## 4. Pruebas nuevas de #139

| Prueba | Tipo | Qué demuestra |
|---|---|---|
| `tests/Cotifly.Api.Tests/Web/clsRegistrosBdFlujoCompletoTests.cs::LaBaseInstaladaDesdeCeroTieneLasTablasYLaConfiguracionSembrada` | Integración | Base de pruebas armada con `db/scripts/01_crear_esquema.sql` + `db/seeder/*` (regla de la issue): 20 tablas, 2 vistas, 6 líneas con su mapeo a Nexo, 2 roles |
| `…::UnFlujoCompletoDejaCadaRegistroConSusValoresFkCheckTotalesYVersionNegocio` | Integración (HTTP, Nexo y cubicador falsos, relay SMTP falso) | Formulario de 2 rutas → cubicador (2 camiones + respaldo) → Nexo por camión → polling barajado → margen de la ruta 2 → PDF → correo → `PDF_YA_ENVIADO` → entrega. Verifica fila por fila `CotizacionesSolicitud`, `CotizacionLineasSolicitud`, `CotizacionVehiculosLineaSolicitud`, `CotizacionMercancias`, `CotizacionServiciosConexos`, `MargenAjustes`, `EventosHerramienta` y `Correos`/`CorreoDestinatarios`; totales guardados contra la suma de camiones y conexos; `precioFinal` = Σ `precioFinalLinea`; `versionNegocio`/ETag; 15 FK/CHECK/UNIQUE violados a propósito |
| `tests/Cotifly.Api.Tests/Web/clsCubicadorEndpointsTests.cs::LaLicitacionConElCubicadorLlegaDelExcelAlPdfDePliegoYAlResultado` | E2E integración | Licitación Excel de 3 filas → cubicador por fila → 1 solicitud de 4 líneas → polling parcial y completo → conexo diferido → margen de la ruta 3 → PDF de pliego sin costos → Excel exportado → "Mis cotizaciones" → entrega → ganada |
| `tests/Cotifly.Infraestructura.Tests/Persistencia/clsEsquemaTests.cs::El0009ConservaAjustesCorreosYAuditoriaYRespaldaLaCotizacionDeItrQueBorra` | Integración (migración de datos) | El 0009 sobre datos viejos: `MargenAjustes` pasa a la ruta 1, `Correos`/`CorreoDestinatarios` y `EventosHerramienta` intactos, las FK siguen a `CotizacionesSolicitud`, la cotización de ITR se borra con lo suyo y queda en `Respaldo0009_*`, la vista de ajustes lee la línea de la ruta |

Soporte: `clsBaseDatosPruebas.InstalacionDesdeCero` (fixture con el esquema vigente) y `clsNexoFalso.CostearLinea(..., retorno)`.

**Tareas de la issue ya resueltas antes de esta rama (verificado):** no quedan aserciones de `CosteoUltimaConsultaAt` (solo un comentario en `clsPollingCosteoServiceTests.cs:55`); `clsSolicitudCosteoMuestra` ya tiene la forma de captura (`CrearCaptura`, `RutaCaptura`, sin vehículo y con ids de municipio y tipo de mercancía; `Crear()` queda como la forma plana de compatibilidad); las pruebas con SQL directo (`Api.Tests/Web/*Endpoints*`, `clsMetricasTableroRepositoryTests`, `clsPasosMetricas`, `clsPasosAgente`, `clsApiWebPruebas`) usan las tablas nuevas y pasan; la prueba de migración `El0009MigraLasCotizacionesViejasARutasYCamionesYEl0010RecuperaLosNombres` ya existía. `db/scripts/02_alter_cubicador.sql` (comentario de la issue) **no existe en esta rama**.

## 5. Alineamiento con el event storming

Las HU no traen una lista formal de eventos: se derivaron de los Gherkin y checklists de HU-003, HU-005, HU-009 a HU-012, HU-021 y de ADR-015, ADR-018 y ADR-019. **(der.)** = nombre derivado. En el código los eventos quedan como filas de `EventosHerramienta` (columna `herramienta`).

### 5.1 Comandos

| Comando | Actor | Código | Registro en BD | Prueba | Estado |
|---|---|---|---|---|---|
| CapturarSolicitud | Comercial | `clsEnvioCosteoService.EnviarAsync` → `clsCapturaRutasService` → `clsCotizacionSolicitud.RegistrarCaptura` | 5 tablas de la cotización | `captura_rutas.feature`, `clsRegistrosBdFlujoCompletoTests` | OK |
| CargarLicitacion | Comercial | `clsLicitacionService.CrearAsync` | ídem + `CotizacionArchivosLicitacion`, `rutaOrigenIndice` | `licitaciones_excel.feature`, E2E licitación | OK |
| PedirRecomendacionCubicador | Sistema → Cubicador | `clsPlanificadorCamionesCubicador.PlanificarAsync`, `clsCubicadorHttp.RecomendarAsync` | camión con `idRecomendacionVehiculo` | `integracion_cubicador.feature` | Desviación D3 |
| EnviarSolicitudANexo / ReintentarEnvio | Sistema / Comercial | `clsEnvioCosteoService.EnviarGuardadaAsync`, `ReintentarAsync` | `solicitudNexoId`, `estadoCosteo`, `enviar_costeo_nexo` | `costeo_nexo.feature`, `envio_nexo_por_camion.feature` | OK |
| ConsultarCosteo (polling) | Sistema | `clsPollingCosteoService.ProcesarAsync` → `clsPrecioCosteoService` | costos del camión, `lineaSolicitudNexoId`, totales, `costeo_nexo_estado` | `clsPollingCosteoServiceTests`, `clsRegistrosBdFlujoCompletoTests` | OK |
| AjustarMargenRuta | Comercial | `clsAjusteMargenCosteoService.AjustarAsync` | ruta + `MargenAjustes.lineaSolicitudId`, `ajustar_margen_costeo` | `margen_conexos_por_ruta.feature`, `clsRegistrosBdFlujoCompletoTests` | OK |
| AgregarConexo / CambiarQuitarConexo | Comercial | `clsServiciosConexosCosteoService.AgregarAsync`, `clsCosteoCotizacionService.SeleccionarServiciosConexosAsync` | `CotizacionServiciosConexos`, `agregar_servicios_conexos`/`cambiar_servicios_conexos` | `servicios_conexos_nexo.feature`, `clsConexosPorRutaServiceTests` | OK |
| EnviarConexosDiferidos | Sistema | `clsEnvioConexosNexoService.EnviarPendientesAsync` | `conexos_envio_nexo` | `clsEnvioConexosNexoEndpointsTests` | OK |
| ReSolicitarCosteo / CrearNuevaVersion | Comercial | `clsReSolicitudCosteoService.ReSolicitarAsync`, `clsCotizacionSolicitud.CrearVersion` | `-vN`, `costeo_nexo_re_solicitud` | `resolicitar_costeo.feature` | OK (D9 corregido; ver §5.4) |
| GenerarPdf | Comercial | `clsCosteoCotizacionService.GenerarPdfAsync` | `pdfPath`, `generar_pdf_costeo` | `clsRegistrosBdFlujoCompletoTests` | OK |
| RedactarTextos / EditarTextos | IA / Comercial | `clsTextosCosteoService.RedactarAsync`, `EditarAsync` | `textosCotizacion` | `redaccion_cotizacion.feature` | OK |
| ProponerAccion / Confirmar | IA → Comercial | `clsHerramientasAsistenteFolio`; confirmar = endpoint determinista | `Sesiones`, `EventosConversacion` | `asistente_folio.feature` | OK |
| EnviarCorreo | Comercial | `clsCorreoService.EnviarAsync` | `Correos`, `estadoEnvio`, `enviar_correo` | `correo.feature`, `clsRegistrosBdFlujoCompletoTests` | OK |
| MarcarResultado / MarcarSeguimiento | Comercial | `clsResultadoCotizacionService.MarcarAsync`, `clsSeguimientoService.MarcarAsync` | `ganada`, `motivoRechazo`, `resultado_cotizacion_nexo` | `costeo_nexo.feature`, `web_cotizaciones.feature` | OK (D4 revisado); D5 por decisión |
| EditarLineaNegocioAdmin | Gerente | `clsLineasNegocioAdminService.ActualizarAsync` | `MargenesPolitica`, `admin_actualizar_linea_negocio` | `admin_lineas_negocio.feature` | OK |
| FijarTrm | Sistema | `clsFijacionMonedaService`, `clsCotizacionSolicitud` | `trmAplicada` | `moneda_trm.feature` | OK |
| Login / Refresh / Logout | Comercial | `clsLoginClienteProService`, `clsRefreshTokenService`, `clsLogoutService` | `UsuariosWeb`, tokens, `login_clientepro`/`refresh_sesion`/`logout` | `login_clientepro.feature`, `refresh_logout.feature` | OK |

### 5.2 Eventos de dominio

| Evento | Código | Registro en BD | Prueba | Estado |
|---|---|---|---|---|
| SolicitudCapturada | `RegistrarCaptura` | cabecera, rutas y camiones en `pendiente_envio` | `captura_rutas.feature` | OK |
| CamionesRecomendados | `clsPlanificadorCamionesCubicador` | un camión por fila | `clsPlanificadorCamionesCubicadorTests` | OK |
| CubicadorEnRespaldo (der.) | EventId 2101 `CUBICADOR_RESPALDO` | ninguno (log y métrica) | `::UnaCaidaUsaElRespaldo...` | Desviación D3 |
| SolicitudEnviadaANexo / EnvioFallido | `RegistrarEnvioCosteo`, `RegistrarErrorEnvioCosteo` | `solicitudNexoId`, `pendiente_costeo` / `error_envio` + `enviar_costeo_nexo` | `costeo_nexo.feature` | OK |
| CosteoDeCamionRecibido | `AplicarFotoNexo`, `AsignarLineaNexo` | `costoFlete`, `costoRetornoContenedor`, `lineaSolicitudNexoId` | `clsCosteoPorCamionTests`, `clsRegistrosBdFlujoCompletoTests` | OK |
| CamionDescartadoPorMesa / VehiculoAsignadoPorMesa (der.) | `clsPrecioCosteoService` | camión `descartada` / vehículo; solo log | `clsCosteoPorCamionTests` | Desviación D13 |
| EstadoRutaRecalculado | `clsResumenEstadoCosteo.Resumir` | `estadoCosteo` de ruta y cabecera | `clsAgregadoRutasTests`, `clsRegistrosBdFlujoCompletoTests` | OK |
| TotalesRecalculados (der.) | `RecalcularTotales`, `RecalcularPrecio` | totales de la ruta, `precioFinal`, `costeoListoAt` | `clsRegistrosBdFlujoCompletoTests` | OK |
| CosteoCompleto (autocosteo/entregada) / CosteoVencido | `clsDerivacionEstadoCosteo.Derivar`, `VencerPollingCosteo` | `estadoCosteo`, `costeo_nexo_estado` | `clsDerivacionEstadoCosteoTests`, `costeo_nexo.feature` | OK |
| MargenAjustado | `AplicarMargenNegociado` | `MargenAjustes` con la ruta | `clsRegistrosBdFlujoCompletoTests` | OK |
| MargenSapAnomalo (HU-009) | `clsMargenResolverService` calcula `Anomalo`; `clsEnvioCosteoService.ResolverMargenAsync` aplica la política y `clsLineaSolicitud.RegistrarMargenSapAnomalo` deja la marca | `margenSAPSugerido` (valor de SAP), `origenMargen = politica_linea`; `margen_sap_anomalo` y `aviso_margen` en `GET .../costeo` | `clsMargenSapAnomaloEndpointsTests`, `margen_sap_anomalo.feature` | OK (D2 corregido) |
| ConexosCambiados | `CambiarConexos`, `AjustarConexosRuta` | `CotizacionServiciosConexos` | `clsConexosPorRutaServiceTests` | OK |
| PdfInvalidado / PdfGenerado | `clsReglaPdfCosteo.InvalidarPdf`, `AsignarPdf` | `pdfDesactualizado`, `CotizacionPdfsHistorial` / `pdfPath` | `ajuste_margen_costeo.feature`, `pdf.feature` | OK |
| CotizacionEnviada / Reenviada | `RegistrarEnvio` | `estadoEnvio` | `correo.feature`, `clsRegistrosBdFlujoCompletoTests` | OK |
| NuevaVersionCreada | `CrearVersion` | fila `-vN` | `resolicitar_costeo.feature` | OK |
| ResultadoRegistrado | `MarcarSeguimiento` | `ganada`, `motivoRechazo` ("No aplica" al ganar) | `clsResultadoCotizacionServiceTests` | OK (D4 revisado) |
| PerdidaSoloEnCotifly | `RegistrarPerdidaSinCosteo` | `ganada = 0`, `resultado_cotizacion_sin_costeo` | `costeo_nexo.feature` | Sin evento en el modelo (D15) |
| VersionNegocioIncrementada (der.) | `IncrementarVersionNegocio` | `versionNegocio` (ETag) | `clsRegistrosBdFlujoCompletoTests` | OK |

### 5.3 Políticas

| Política | Código | Prueba | Estado |
|---|---|---|---|
| Cubicador caído → no se manda nada a Nexo (HU-021.5) | solo con `Cubicador:RespaldoSiFalla=false`; por defecto `true` (`clsOpcionesCubicador.cs:38`) | `::SinRespaldoUnCubicadorCaidoNoGuardaNiEnviaNada` | D3 |
| Sin selección / condicionada → camión sin vehículo a la Mesa | `clsRecomendacionCubicador` | `::SinSeleccionYCondicionadaSonResultados...` | OK |
| Conexos pegados al primer camión vigente (decisión 3) | `clsServiciosConexosCosteoService.PrimerCamion`, `clsEnvioConexosNexoService` | `::ConElPrimerCamionDescartadoVanAlSiguienteCamionVigente` | OK |
| Margen una vez por ruta con el método de su línea | `clsLineaSolicitud.AplicarMargenNegociado` | `clsAgregadoRutasTests` | OK |
| PDF ya enviado → `PDF_YA_ENVIADO` y `-vN` | `ValidarCambioEtapaFinal`, `clsReglaPdfCosteo.ConVersionNueva` | `ajuste_margen_costeo.feature`, `clsRegistrosBdFlujoCompletoTests` | OK |
| Etapa final exige If-Match (428/409) | `[clsRequiereIfMatch]` en 6 endpoints | 4 features, `clsRegistrosBdFlujoCompletoTests` | OK |
| La IA nunca recibe montos | `clsRedactorCotizacionLlm`, `clsHerramientasAsistenteFolio.RutasSinMontos` | `redaccion_cotizacion.feature`, `asistente_folio.feature` | OK |
| SAP caído/sin dato → `politica_linea` | `clsMargenResolverService.cs:79-133` | `margen.feature` | OK |
| Margen SAP fuera de [0, 60 %] → marcado anómalo y no se aplica a ciegas | `clsEnvioCosteoService.ResolverMargenAsync` (captura, licitación, re-solicitud y `-vN` pasan por ahí) | `clsMargenSapAnomaloEndpointsTests` | OK (D2 corregido) |
| Resultado solo con 200 de Nexo; con 409 `RESULTADO_YA_DEFINIDO` se guarda el de Nexo (ADR-018:298) | `clsResultadoCotizacionService.cs:268-280` | `::SiNexoYaTieneOtroResultadoSeGuardaElDeNexo...` | OK (D4 revisado) |
| Resultado rechazado en `pendiente_costeo` | solo la ganada; la perdida se registra local (HU-013.17) | `costeo_nexo.feature` | D5 |
| `estadoEnvio` → `reenviada` en el segundo envío | `RegistrarEnvio` | `correo.feature` | OK |
| `motivoRechazo` = "No aplica" al ganar (HU-021.13) | `clsCotizacionSolicitud.MarcarSeguimiento` | `clsResultadoCotizacionEndpointsTests`, E2E | OK |
| CHECK de `estadoEnvio` | EF, `01_crear_esquema.sql:286`, `0010` | `clsRegistrosBdFlujoCompletoTests`, `clsBaseDatosHealthCheckTests` | OK en código; D7 en la base compartida |
| Levante de la ruta copiado a cada camión (decisión 4) | `clsVehiculoLineaSolicitud.cs:89,110` | `clsAgregadoRutasTests`, `clsRegistrosBdFlujoCompletoTests` | OK |
| `referencia` y `valorEstimado` no van a Nexo (decisión 1) | fuera de `openapi.yaml` y del Excel exportado (#138); los DTO de entrada solo los leen para el aviso `CAMPOS_RETIRADOS` | `clsCosteoNexoTests`, `clsCamposRetiradosTests` | OK (D12 corregido en #138) |
| Municipio: id obligatorio + copia del nombre (decisión 2) | `clsLineaSolicitudCosteo.ErroresCaptura`, `clsMunicipiosLicitacion` | `captura_rutas.feature`, `clsRegistrosBdFlujoCompletoTests` | OK |
| Asistente pregunta la ruta con varias rutas (decisión 6) | `clsHerramientasAsistenteFolio` (`requiere_ruta`) | `clsAsistenteMargenPorRutaTests` | OK salvo re-solicitud (D9) |
| Métricas: conteos por cotización, valores por ruta (decisión 8) | `clsMetricasRepository.cs:35-110` | `metricas_tablero.feature`, `clsCubicadorEndpointsTests` | OK |
| `conexos_descartados` (#124) rehecho | `clsReSolicitudCosteoService.cs:218-226` | `resolicitar_costeo.feature` | OK |
| Vehículo y carrocería no los digita el comercial (decisión 7) | `clsEnvioCosteoService.SinVehiculo` | `catalogos_formulario_costeo.feature` | OK |

### 5.4 Desviaciones por severidad

**Alta (1, corregida)**
- **D2 / B1. Margen SAP anómalo sin marca — CORREGIDO en #139.** Antes, `clsEnvioCosteoService.ResolverMargenAsync` descartaba el `Anomalo` de `clsMargenResolverService.cs:97-105` y aplicaba, por ejemplo, un 75 % de SAP como `sap_tm`. La HU-009 pide que "quede marcado explícitamente como anómalo, para que el equipo lo revise antes de confiar en él" (`docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md:1215-1218`) y `margen.feature` ("Un margen de SAP TM fuera de [0, 60 %] se marca anómalo"). Ahora el margen de SAP se redondea a 4 decimales (la precisión de `decimal(9,4)`, half-to-even) antes de evaluarlo, así lo evaluado y lo guardado coinciden; si es anómalo, la ruta usa el margen de la política de su línea, el de SAP queda en `margenSAPSugerido` como marca (sin columna nueva), no cuenta como sugerido para la justificación, y `GET .../costeo` trae `margen_sap_anomalo` y `aviso_margen` (`specs/openapi.yaml`, `RutaCosteo`). Vale en la captura, la licitación, la re-solicitud y la `-vN` (todas pasan por `ResolverMargenAsync`).

**Media**
- **D3. Cubicador caído → respaldo.** **Decisión vigente mientras no exista el cubicador real** (`RespaldoSiFalla = true`, ADR-019). No se toca.
- **D4. 409 `RESULTADO_YA_DEFINIDO` — REVISADO, sin cambio.** El código no escribe lo que mandó el comercial: lee el resultado de Nexo y guarda ese, con `versionNegocio`, y responde el 409 con el de Nexo. Es exactamente lo que decide **ADR-018 (línea 298, "Resultado ya definido en Nexo")**, que deja a Nexo como fuente de verdad; la frase "ningún cambio local" de ADR-015:87 y de HU-005.14 quedó superada por ADR-018 (y `specs/openapi.yaml:1332` ya lo dice). Pendiente solo de documentación: alinear ADR-015:87.
- **D5. Perdida antes del costeo solo en Cotifly.** **Decisión del usuario** ("perdida antes del costeo solo en Cotifly", HU-013.17). No se toca.
- **D6. `TrqType` de `DevolucionContenedor`.** **Externo** (negocio/SAP, vía Nexo). No se toca.
- **D7. 0010 y CHECK de `estadoEnvio` en la base compartida.** **Externo** (DBA). No se toca.
- **D8. Restos del chat (#105).** **Va a #146.** No se toca.
- **D9. Re-solicitud con cambios de carga y varias rutas — CORREGIDO (mensaje).** `datos_carga` trae una sola carga sin ruta, así que con varias rutas sigue sin soporte; el mensaje ya no dice "licitación" para un formulario: "La cotización X tiene N rutas: los cambios de carga solo aplican a una cotización de una ruta…", con `detalle.motivo = VARIAS_RUTAS` (`LICITACION` se conserva para las licitaciones). Pruebas: `clsReSolicitudCosteoServiceTests.cs::UnaLicitacionNoAdmiteCambiosDeCarga` (formulario de 2 rutas) y `::UnaLicitacionDeVariasRutasNoAdmiteCambiosDeCargaYElMotivoLoDice`. Queda abierto que `proponer_re_solicitud` del asistente no recibe ruta.

**Baja (9)**
- **D1.** Constantes de herramientas del plugin legado en `src/Cotifly.Aplicacion/Agente/DTOs/clsTurno.cs:61-69` y mapeo a `generar_pdf`/`registrar_ajuste_margen` en `clsReglasRespuestaAgente.cs:101-102`.
- **D10.** Métricas cuentan una vez por cadena de versiones (ADR-016) y HU-010.1 decía cada versión; score de urgencia no implementado.
- **D11.** `clsCotizacionSolicitud.CambiarValorLevante` sin llamadores en producción.
- **D12. CORREGIDO en #138.** `Referencia` vestigial en la exportación de licitación ("Referencia del pliego: —"): el Excel exportado ya no la imprime y la vista `Licitacion` no la trae.
- **D13.** Descarte o cambio de vehículo por la Mesa y empates solo en el log, sin `EventosHerramienta` (HU-005.6 pide auditar los cambios del polling).
- **D14.** HU-021.7 decía "sin `posiciones[]`"; se envían con un único camión (adaptación en ADR-019).
- **D15.** Herramientas de auditoría sin evento en las HU: `descargar_pdf`, `conexos_pendientes_nexo`, `conexos_envio_nexo`, `resultado_cotizacion_sin_costeo`, `costeo_nexo_consulta`, `editar_textos_costeo`, `redactar_textos_costeo`.
- **D16.** El escenario de HU-021 "las cotizaciones del agente de chat tienen ruta y camión" quedó obsoleto con #105.
- **D17.** ADR-019 cita "decisiones 1 a 11", pero el archivo trae 1-8 y 10; la 11 está en `docs/database/PROPUESTA_REFACTOR_BD_COTIFLY.md:118` y la 9 no aparece. `clsSolicitudCosteo` sigue plano (una ruta en la cabecera) aunque HU-021.3 pedía lista de rutas: las rutas viajan como `clsLineaLicitacionEntrada`. ADR-015:87 dice "ningún cambio local" ante `RESULTADO_YA_DEFINIDO`, superado por ADR-018:298.

Verificado sin desviación: "No aplica" al ganar, decisión 6, decisión 8, `conexos_descartados` rehecho, retiro de Google Chat (`clsRetiroGoogleChatTests`), "Mis cotizaciones" tolera filas malas.

## 6. Bugs

| Id | Severidad | Archivo:línea | Estado |
|---|---|---|---|
| B1 | Alta | `src/Cotifly.Aplicacion/Costeo/Services/clsEnvioCosteoService.cs` (`ResolverMargenAsync`) descartaba `clsSalidaMargen.Anomalo` de `clsMargenResolverService.cs:97-105` | **Corregido en #139.** Reproducción original: SAP TM devuelve 75 para el BP → `POST /web/solicitudes-costeo` → la ruta quedaba con `margenAplicado = 0.75` y `sap_tm` sin marca. Hoy queda `0.15`, `politica_linea`, `margenSAPSugerido = 0.75`, `margen_sap_anomalo = true` y `aviso_margen` (`clsMargenSapAnomaloEndpointsTests`) |

Comportamientos observados que **no** son bugs (quedan documentados por las pruebas nuevas): el PDF de pliego y el Excel exportado muestran la tarifa por viaje de cada ruta **sin conexos** (el conexo va aparte con su precio), mientras `precio_final_linea` y "Mis cotizaciones" sí los incluyen; el nombre del municipio guardado es el del catálogo de Nexo ("Bogotá, D.C."), no el digitado; un camión sin retorno de Nexo guarda `costoRetornoContenedor = 0`; sin redactar ni editar textos, `textosCotizacion` queda en `NULL`.
