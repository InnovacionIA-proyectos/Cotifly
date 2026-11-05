# Despliegue en Dokploy (Docker Swarm) — Cotifly

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

Runbook del despliegue de Cotifly en el clúster Docker Swarm + Dokploy (manager `10.10.40.17`, worker `10.10.40.31`
`TBSNODO1-DKP`, Traefik gestionado por Dokploy). Milestone **HU-006**.

## 1. Estado y alcance

- **Backend vigente tras el corte: .NET 10** (`backend-dotnet/`, una sola imagen `cotifly-backend`, ADR-001 y ADR-002).
  Este documento describe su despliegue. Las decisiones están en
  [ADR-013](backend-dotnet/specs/decisiones/ADR-013-despliegue-dokploy.md).
- **Stack Python: retirado del repositorio** (decisión del usuario, 2026-10-13: "todo lo referente a python muere en este
  repo"). HU-022.1 (#146) borró el código Python, `deploy/` (manifiesto `docker-compose.swarm.yml` y
  `rebuild-on-worker.sh`), los manifiestos que nunca se desplegaron y los scripts de respaldo del Python. El último
  estado completo vive en el tag de git **`python-final`** (ver el [Anexo A](#anexo-a-stack-python-retirado-tag-python-final)).
  El stack Python que corre hoy en Dokploy no se tocó: apagarlo es una acción de infraestructura aparte (§7).
- La base de datos **no** es un contenedor del stack: es la instancia corporativa SQL Server 2019 (`tbsdatacenter`),
  base `dbTarifario_Nexo_Pruebas`, esquema `dbo_cotifly`, más la base de identidad del portal de solo lectura (ADR-008),
  que es **la misma que lee Nexo** en cada ambiente: `dbSolucionesTI_Prueba` en QA (#172, ADR-009). El login de
  servicio es `cotifly_app` (SQL login; el contenedor Linux no admite autenticación integrada).
- Referencia de cada variable y de su validación al arrancar: `backend-dotnet/.env.example` y
  `backend-dotnet/docs/03-infraestructura.md` §3.2.

| Issue | Tema | Sección |
|---|---|---|
| #29 (HU-006.5) | Secretos fuera del repositorio | [§2](#2-secretos-y-variables-hu-0065-29), [§3](#3-tabla-de-variables-de-producción-plantilla-para-hu-0071-33) |
| #28 (HU-006.4) | Registry, imagen versionada y release | [§4](#4-registry-imagen-versionada-y-release-hu-0064-28) |
| #30 (HU-006.6) | Manifiestos del stack .NET + SQL Server externo | [§5](#5-manifiestos-del-stack-net-hu-0066-30) |
| #31 (HU-006.7) | Backups de `dbo_cotifly` (y la verificación del JWT de Google Chat, retirado en #105) | [§6](#6-backups-de-dbo_cotifly-hu-0067-31), [§7](#7-google-chat-retirado-hu-0139-105) |

## 2. Secretos y variables (HU-006.5, #29)

### 2.1 Mecanismo

Decisión propuesta (**pendiente de confirmar con el equipo** de infraestructura si existe un estándar corporativo
distinto, por ejemplo un gestor de secretos):

1. **Variables de entorno de Dokploy por servicio** (pestaña *Environment* del compose de tipo *stack*). Dokploy las
   guarda en su base y las escribe en el `.env` con el que hace `docker stack deploy`; el manifiesto solo las
   referencia como `${Variable}` y nunca lleva el valor. Así llegan las cadenas de conexión, `Jwt__SigningKey` e
   `IA__ApiKey`, y desde el 2026-11-03 `SapTm__Pass` y `ConnectionStrings__DwOperaciones` (la sincronización del margen de SAP la hace
   el backend, ADR-027).
2. **Sin secretos de Docker Swarm.** El único que había era la cuenta de servicio de Google Chat (`GChat__SaJson`,
   secreto `cotifly_gchat_sa_v1`), retirada con Google Chat en HU-013.9 (#105, ADR-018 §15). Tras desplegar la versión
   sin Google Chat, borrar el secreto en el manager: `docker secret rm cotifly_gchat_sa_v1 cotifly_staging_gchat_sa_v1`
   (solo con confirmación del usuario: es el clúster).

Por qué no se usan secretos de Swarm: el backend lee la configuración de variables de entorno. Llevar las cadenas de conexión o la clave JWT a `/run/secrets` exige agregar el proveedor
*key-per-file* de ASP.NET Core (`AddKeyPerFile("/run/secrets")`), un cambio de código que queda como mejora
propuesta, no implementada.

Riesgos que se aceptan con las variables de Dokploy:

- Quien tenga acceso al Docker del manager las ve con `docker service inspect`, y quien tenga acceso al panel de
  Dokploy las ve en la pestaña *Environment*. El acceso a ambos debe limitarse al equipo de despliegue.
- Nunca se copian a un archivo versionado. El `.env` de desarrollo (`backend-dotnet/.env`, `.env.local`) está en
  `.gitignore`.

Reglas:

- Ningún valor real en git: ni en manifiestos, ni en `appsettings*.json`, ni en `.env.example`, ni en este documento.
- En `Production` la API **no arranca** si un secreto obligatorio está vacío, es un placeholder (`<secreto>`,
  `cambiar...`, `changeme`, `dev`) o es más corto que su mínimo (`clsValidadorSecretos`). Un despliegue con valores
  de ejemplo falla en el rollout en vez de quedar corriendo.
- MinIO no se mantiene: los PDF van en el volumen `/data/pdfs` (ADR-012). No hay credenciales de S3 que migrar.
- Nexo no usa credencial: cada petición lleva `X-Portal-clsUsuario-Id` del comercial (ADR-005).

### 2.2 Variables que espera el backend .NET

Nombres exactos (ASP.NET Core traduce `__` a `:`). "Secreto" indica si va con el tratamiento del §2.1. "Obligatoria"
se refiere a `Production`: sin ella (o con un valor de ejemplo) la API no arranca.

| Variable | Secreto | Obligatoria en Production | Por defecto | Dónde se carga | Nota |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | no | — | `Production` (sin valor) | manifiesto | `Production` en staging **y** en producción, a propósito: así staging aplica las mismas validaciones de arranque (`clsValidadorSecretos`, CORS obligatorio) y un error de configuración aparece antes de producción |
| `ASPNETCORE_HTTP_PORTS` | no | no | `8080` (imagen) | imagen | no se cambia |
| `Zona__ZonaHoraria` | no | no | `America/Bogota` | imagen / manifiesto | |
| `ConnectionStrings__Cotifly` | **sí** | **sí** | vacío | Dokploy | `Server=tbsdatacenter,1433;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=...;Encrypt=True` |
| `ConnectionStrings__Identidad` | **sí** | **sí** | vacío | Dokploy | igual, solo lectura, contra **la misma base de identidad que Nexo** en ese ambiente (#172): QA `Database=dbSolucionesTI_Prueba`; producción, la que use Nexo en producción (**por confirmar**). Ver [cambio de #172](#identidad-compartida-con-nexo-172) |
| `ConnectionStrings__Regional` | **sí** (≥ 20, si tiene valor) | no (opcional) | vacío | Dokploy | `DWCubosEBSSAP`, solo lectura de `dbo.DimEjecutivo` (ADR-016, ADR-008). El **login** llena y refresca `UsuariosWeb.regional` (#81) y el **tablero del gerente** la lee en vivo (#45). Vacía = no configurada: se usa lo que ya haya en `UsuariosWeb.regional`, y el tablero responde 409 `REGIONAL_NO_ASIGNADA` si está vacía. Con valor exige salida desde el worker a **`10.10.40.24:1433`** y un **usuario SQL de solo lectura con `SELECT` únicamente sobre `dbo.DimEjecutivo`** (**pendiente con el DBA**); si la consulta falla, el login sigue (sin bloquear) y el tablero responde 503 |
| `Regional__Esquema`, `Regional__Tabla`, `Regional__ColumnaUsuario`, `Regional__ColumnaRegional` | no | no (pero nunca vacías: identificadores obligatorios) | `dbo`, `DimEjecutivo`, **`USUARIO`**, `REGIONAL` | manifiesto | ubicación de la tabla de ejecutivos. `USUARIO` guarda `boterosoto\usuario`: el código quita el dominio antes de comparar con `UsuariosWeb.nombreUsuario` (#81). **No usar `ID`**: con ese valor el cruce nunca coincide |
| `Regional__Dominio` | no | no | `boterosoto` | manifiesto | dominio aceptado en `USUARIO` (#81): una fila de otro dominio no cuenta; una sin dominio sí. El manifiesto usa `:-`, así que para aceptar cualquier dominio hay que editar el manifiesto |
| `Regional__TiempoEsperaSegundos` | no | no | `10` (2 a 60) | manifiesto | límite de cada consulta a `DWCubosEBSSAP` |
| `Regional__TiempoEsperaLoginSegundos` | no | no | `3` (1 a 10) | manifiesto | espera máxima del login por `DimEjecutivo` (#81); pasado ese tiempo el login continúa sin la regional nueva. Se usa el menor entre este y `Regional__TiempoEsperaSegundos` |
| `Identidad__Esquema`, `Identidad__TablaUsuarios`, `Identidad__TablaUsuarioRoles`, `Identidad__TablaRoles` | no | no | `dbo`, `tblSAPUsuariosApp`, `tblSAPUsuarioRoles`, `tblSAPRoles` | manifiesto | por confirmar con el DBA |
| `Jwt__Issuer`, `Jwt__Audience` | no | no | `cotifly-api`, `clientepro` | manifiesto | |
| `Jwt__SigningKey` | **sí** (≥ 32 bytes) | **sí** | vacío | Dokploy | `openssl rand -base64 48`; distinta por ambiente |
| `Jwt__AccessMinutes`, `Jwt__RefreshDays` | no | no | `60`, `30` | manifiesto | |
| `LimiteAuth__PeticionesPorVentana`, `LimiteAuth__VentanaSegundos` | no | no | `30`, `60` | manifiesto | |
| `Cors__OrigenesPermitidos__0` (y `__1`...) | no | **sí** (al menos uno) | vacío | Dokploy | dominio de `clientepro-frontend` |
| `ForwardedHeaders__Habilitado` | no | no | `false` | manifiesto | `true` detrás de Traefik |
| `ForwardedHeaders__RedesConocidas__0`, `ForwardedHeaders__ProxiesConocidos__0`, `ForwardedHeaders__LimiteSaltos` | no | con `Habilitado=true`, una red o un proxy | vacío, vacío, `1` | Dokploy | subred de `dokploy-network` |
| `SeguridadHttp__LimiteCuerpoWebBytes` | no | no | `65536` | manifiesto | |
| `SeguridadHttp__LimiteCuerpoLicitacionBytes` | no | no | `12582912` | manifiesto | HU-011: Excel de licitación (10 MB) + JSON. Traefik no debe cortar antes (sin límite de cuerpo propio en el router) |
| `SapTm__SalesOrgDefault` | no | no | `50000005` | manifiesto | |
| `SapTm__AvisoSincronizacionHoras` | no | no (> 0) | `48` | manifiesto | HU-042.2 (#243): horas desde la última sincronización exitosa de la copia local del margen de SAP a partir de las cuales el detalle lo avisa (el dato se usa igual) |
| `SapTm__SincronizacionHabilitada` | no | no | `false` | manifiesto | Interruptor de la sincronización (worker y corrida forzada). Apagado, no se sincroniza aunque haya credenciales; la cotización sigue leyendo la copia local |
| `SapTm__Url` | no | no (mal escrita no impide arrancar: el worker queda inactivo con un log Error) | vacío | Dokploy | HU-042.2 (2026-11-03): la **raíz del servicio** OData de rentabilidad, `http://sap-tm-prd.boterosoto.local:8000/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV` (sin `/RentabClienteSet`; si se pone, se acepta y se quita, y sin `?$filter`). Sin `Url`, `User` o `Pass` el worker de sincronización queda inactivo y la API arranca igual |
| `SapTm__User` | no | no | vacío | Dokploy | usuario técnico de SAP (Basic) |
| `SapTm__Pass` | **sí** (≥ 8, si tiene valor) | no | vacío | Dokploy | clave del usuario técnico; nunca en el repo ni en el manifiesto |
| `SapTm__SapClient` | no | no (si no son 3 dígitos, el worker queda inactivo con un log Error) | vacío | Dokploy | mandante (`&sap-client=NNN`), opcional |
| `SapTm__ChequeoMinutos`, `SapTm__SincronizacionMinutos` | no | no | `60`, `1440` | manifiesto | cada cuánto revisa el worker y edad máxima de la última corrida `ok` |
| `SapTm__TimeoutSegundos`, `SapTm__MaximoBytes` | no | no | `120`, `20971520` | manifiesto | límite de la descarga y tope del cuerpo descomprimido |
| `SapTm__MinimoFilas`, `SapTm__MinimoPorcentajeActivas` | no | no | `100`, `80` | manifiesto | guarda: con menos de `max(MinimoFilas, % de las activas)` filas no se toca la tabla |
| `ConnectionStrings__DwOperaciones` | **sí** (≥ 20, si tiene valor) | no (opcional) | vacío | Dokploy | `DWCubosOperacionesEBS` en `10.10.40.24:1433`, solo lectura de `dbo.DimBPSolicitante` (`BP`, `NIT`): el NIT de cada fila de la copia. Vacía, o sin el BP en el DW: la fila conserva el NIT que ya tenía (o queda `NULL`; warning por corrida). Configurada y caída: la corrida queda `error` |
| ~~`SapTm__Modo`, `__ExportPath`~~ | — | — | — | — | **Retiradas en HU-042.2 (ADR-027).** Si siguen cargadas en Dokploy, borrarlas; si llegan al contenedor, la API arranca y avisa "variable obsoleta, se ignora" |
| `Nexo__UrlBase` | no | no | vacío | Dokploy | **raíz** del servicio (`https://<host-nexo>`), sin `/api`: el cliente agrega las rutas `/api/...`. Sin ella la búsqueda de clientes responde `*_NO_DISPONIBLE`, el costeo no corre y `POST /web/solicitudes-costeo` rechaza con 503 `CATALOGOS_FLETE_NO_DISPONIBLE` todo formulario con `tipo_vehiculo` o `clase_viaje` (no hay catálogo contra el cual validarlos, HU-013.10) |
| `Nexo__MinutosCacheMunicipios` | no | no | `60` | manifiesto | caché de la lista de municipios de Nexo (frente B) |
| `Nexo__MinutosCacheCatalogos`, `Nexo__HorasCatalogosVencidos` | no | no | `30`, `24` | manifiesto (endurecimiento A, #199) | caché de los catálogos maestros de Nexo y horas que la última copia sirve de respaldo con Nexo caído (HU-013.10) |
| `CosteoNexo__Habilitado` | no | no | staging **`false`**, producción `true` | manifiesto / Dokploy | polling del costeo en Nexo (HU-005.6/005.8). En staging queda apagado hasta que el DBA confirme que staging y producción **no** comparten `dbo_cotifly`: si la comparten, los workers de los dos stacks consultarían y actualizarían las mismas cotizaciones contra instancias distintas de Nexo. Sin `Nexo__UrlBase` no corre aunque esté en `true` |
| `CosteoNexo__IntervaloInicialSegundos`, `__IntervaloMaximoMinutos`, `__FactorEdad`, `__EdadMaximaHoras` | no | no | `5`, `15`, `0.25`, `72` | manifiesto | ritmo del polling: primera consulta a los 5 s, espera creciente con la edad (25 %) hasta 15 min; pasadas 72 h queda `costeo_vencido`. Valores por confirmar con el equipo de Nexo (ADR-015). **HU-036.4 (#208), parte B: propuesta pendiente de acordar con NEXO** subir `__EdadMaximaHoras` (rango 1-720) por encima de lo máximo que se demora la Mesa de Costeo en la práctica, para que una cotización no quede `costeo_vencido` mientras Nexo todavía la trabaja; hoy sigue en `72` y cambiarlo no revive las que ya vencieron |
| `CosteoNexo__IntervaloTickSegundos`, `__Lote`, `__ReclamoSegundos` | no | no | `5`, `20`, `120` | manifiesto | cada cuánto revisa el worker la agenda, cotizaciones por vuelta y reclamo de una cotización en consulta (debe superar los 60 s de la consulta a Nexo) |
| `Cubicador__UrlBase` | no | no | vacío | Dokploy (declarada en el manifiesto con `${Cubicador__UrlBase:-}`, endurecimiento A, #199) | HU-021.5 / HU-023.1: raíz del API del cubicador (sin barra final). Vacía, cada ruta queda con el camión del respaldo (el vehículo elegido si lo hay y es válido; si no, sin vehículo, ADR-022) y `GET /web/tipos-mercancia`, `/web/tipos-carroceria` y `/web/tipos-vehiculo` responden 503 |
| `Cubicador__ApiKey` | **sí** (≥ 16) | con `Cubicador__UrlBase` | vacío | Dokploy (secreto; declarada en el manifiesto con `${Cubicador__ApiKey:-}`, sin valor real) | llave de Cotifly ante el cubicador (`Authorization: Bearer`), **una por ambiente** (QA y producción), distinta de la de Nexo; se entrega al equipo del cubicador por un canal seguro. Nunca llega al navegador |
| `Cubicador__TiempoEsperaSegundos` | no | no | `15` | manifiesto (endurecimiento A, #199) | límite de cada intento de las **consultas** al cubicador (catálogos, reconsulta `GET /recomendaciones`, salud); un GET se reintenta 2 veces. Hasta el E2E del 2026-10-16 también limitaba el POST y valía `30` en el código (`.env.example` decía `15`) |
| `Cubicador__TiempoEsperaRecomendacionSegundos` | no | no | `90` | manifiesto (endurecimiento A, #199) | D3b y opción C (E2E 2026-10-16): límite del `POST /recomendaciones` (1 a 300 s; antes `60`, de 1 a 90). El motor tarda 26-31 s con cargas grandes (1.100 unidades) y bloquea la API del cubicador mientras calcula. Nunca se reintenta: sin respuesta, se reconsulta con GET. **Al arrancar** se valida que sea ≥ `TiempoEsperaSegundos` y ≤ `PresupuestoCapturaSegundos` |
| `Cubicador__EsperaAntesReconsultaSegundos`, `Cubicador__TiempoEsperaReconsultaSegundos` | no | no | `5`, `60` | manifiesto (endurecimiento A, #199) | Opción C: tras un POST sin respuesta, espera y límite propio de la reconsulta `GET /recomendaciones` (el cubicador la responde cuando el motor termina). `TiempoEsperaReconsultaSegundos` ≥ `TiempoEsperaSegundos` (se valida al arrancar) |
| `Cubicador__MaximoConcurrente`, `Cubicador__EsperaMaximaCupoSegundos` | no | no | `1`, `60` | manifiesto (endurecimiento A, #199) | Opción C: cupo global de llamadas a `POST /recomendaciones` de cada réplica (cubicación rápida, captura, recálculo y licitación) y cuánto se espera por él. Sin cupo a tiempo: `CUBICADOR_OCUPADO` (503 con `Retry-After: 10` en "Calcular cubicaje"; caída en los demás flujos). Subir `MaximoConcurrente` solo cuando el cubicador calcule fuera de su bucle de eventos (opción F) |
| `Cubicador__EsperaSincronaCubicacionSegundos`, `Cubicador__RetencionCubicacionMinutos`, `Cubicador__MaximoCubicacionesEnMemoria` | no | no | `8`, `15`, `500` | manifiesto (endurecimiento A, #199) | Opción D: `POST /web/cubicacion` responde 202 si el cubicador no termina en 8 s y el front consulta `GET /web/cubicacion/{id}`. Las cubicaciones viven **en memoria** (la API tiene `replicas: 1`): un reinicio o un despliegue las pierde y el front recibe 404 (vuelve a cubicar). Con más réplicas hace falta una tabla compartida |
| `Cubicador__RespaldoSiFalla` | no | no | `true` | manifiesto (endurecimiento A, #199) | si el cubicador se cae, la ruta sigue con el camión del respaldo (el vehículo elegido si lo hay y es válido; si no, sin vehículo, ADR-022). El manifiesto lo deja en `true`, igual que el código (decisión del endurecimiento A, #199). Conviene cargar `false` en Dokploy cuando el cubicador ya esté estable con URL en ese ambiente y se prefiera que una caída detenga la captura (503 `CUBICADOR_NO_DISPONIBLE`, nada se guarda ni va a Nexo) a enviar camiones del respaldo que la Mesa de Costeo tendría que corregir. Un rechazo o una respuesta incompleta nunca van al respaldo |
| `Cubicador__LicitacionEnSegundoPlano`, `__ProcesadorHabilitado`, `__MaximoParalelo`, `__IntervaloTickSegundos`, `__LoteRutas`, `__ReclamoSegundos`, `__MaximoIntentos`, `__EsperaReintentoSegundos` | no | no | `true`, `true`, `1`, `5`, `50`, `300`, `3`, `30` | manifiesto (endurecimiento A, #199) | licitación cubicada en segundo plano (HU-023.5, #160; exige `db/scripts/pendientes/04`). `MaximoParalelo` es para todas las réplicas juntas (P6 del cubicador); opción C: antes `4`, y al arrancar no puede superar `MaximoConcurrente`. `ReclamoSegundos` (antes `180`) debe superar, al arrancar, lo que dura la llamada de una ruta: `EsperaMaximaCupoSegundos` + `TiempoEsperaRecomendacionSegundos` + 2 + `EsperaAntesReconsultaSegundos` + `TiempoEsperaReconsultaSegundos` + 30 s (247 con los valores por defecto) |
| `Cubicador__RutaSalud` | no | no | vacío | manifiesto (endurecimiento A, #199) | ruta del endpoint de salud del cubicador (petición P7, por ejemplo `/health`) para la sonda `cubicador` de `/health/ready`; vacía no lo consulta |
| `Cubicador__MinutosCacheCatalogos`, `__TiempoMaximoCatalogoSegundos`, `__PresupuestoCapturaSegundos` | no | no | `30`, `10`, `180` | manifiesto (endurecimiento A, #199) | caché de los tres catálogos, tope de cada catálogo y tiempo máximo de llamadas por captura (se revisa antes de cada ruta y la espera por el cupo cuenta; antes `120`, opción C). Una ruta sola puede tardar lo que dura una llamada al cubicador (`EsperaMaximaCupoSegundos` + `TiempoEsperaRecomendacionSegundos` + 2 + `EsperaAntesReconsultaSegundos` + `TiempoEsperaReconsultaSegundos` = 217 s) y una captura de varias rutas, en el peor caso, el presupuesto más esa llamada (180 + 217 ≈ 400 s, 6,5 min): Traefik (sin `respondingTimeouts.writeTimeout` ni `serversTransport.forwardingTimeouts.responseHeaderTimeout` que lo corten), cualquier otro proxy y el cliente del front deben cubrirlo |
| `CosteoNexo__LecturaInmediataSegundos`, `__MaximoFallosRelectura`, `__MaximoFallosSincronizacionResultado`, `__EdadMaximaSincronizacionResultadoDias` | no | no | `6` (1-30), `10` (1-100), `10` (1-100), `30` (1-365) | manifiesto (endurecimiento A, #199) | lectura del costeo justo después del alta y tope de relecturas fallidas; reintentos y edad máxima de la sincronización diferida del resultado con Nexo (HU-033) |
| `Cubicacion__ParecidoMinimoMercancia`, `__MargenAmbiguedadMercancia`, `__IncluirSugerenciaLegada` | no | no | `0.7` (0,501-1), `0.05` (0-0,5), `false` | manifiesto (endurecimiento A, #199) | "Calcular cubicaje" (HU-023.2, #157): parecido mínimo de la descripción con el catálogo del cubicador, margen para declararla ambigua y plan B de la sugerencia legada |
| `CandadoSql__EsperaMilisegundos`, `__MargenRespuestaMilisegundos`, `__GraciaCancelacionMilisegundos`, `__ComandoSegundos`, `__Intentos`, `__PausaBaseMilisegundos` | no | no | `5000`, `15000`, `5000`, `120`, `3`, `200` | manifiesto (endurecimiento A, #199) | candado del folio en SQL Server (`sp_getapplock`, #125): espera por el candado, margen de la respuesta, gracia al cancelar, límite de cada comando y reintentos antes de `SERVICIO_OCUPADO` |
| `CosteoNexo__MaximoReintentosConexos`, `__IntervaloReintentoConexosMinutos`, `__EsperaMaximaCatalogoConexosHoras` | no | no | `5` (1-100), `5` (1-1440), `24` (1-720) | manifiesto (endurecimiento A, #199) | envío diferido de los conexos a Nexo (HU-021.9, decisión 10): intentos fallidos antes de `fallido_definitivo`, espera entre reintentos de una cotización ya entregada y horas máximas esperando el catálogo de conexos o la cotización de Nexo |
| `Nexo__EnviarGrupoRutaId` (antes `Nexo__EnviarReferenciaRuta`) | no | no | `true` | manifiesto (HU-014, #188) | HU-023.4: manda `grupoRutaId` en las líneas de una ruta con varios camiones (una ruta de un camión va con `null`). HU-023 (#176): encendida por defecto porque Nexo ya lo desplegó; `false` solo como interruptor de emergencia |
| `Nexo__EnviarDatosCubicador` | no | no | `true` | manifiesto (#188) | HU-023.4: manda `idRecomendacionVehiculo` por línea. Encendida por defecto (#176); `false` = interruptor de emergencia |
| `Nexo__EnviarCodigosCubicador` | no | no | **`siempre`** | manifiesto (#188, #194) | Nexo #297: códigos de vehículo y carrocería del cubicador en la línea. Antes de enviar, el código se resuelve con `GET /api/catalogos/tipos-vehiculo` de Nexo (la copia en caché, con la equivalencia de Nexo: sin tildes ni mayúsculas, `_`/`-` = espacio) y, si no está, con el **diccionario de alias** de Nexo (**HU-031.1 #194**, NEXO #321, `GET /api/catalogos/alias`, misma caché: `C2` → `2`); lo resuelto viaja escrito como en Nexo y se autocostea. Tres modos: **`siempre` (por defecto desde #194, decisión del 2026-10-20: Nexo ya tiene la bandeja de normalización, NEXO #322)** manda todos; el que nadie conoce, tal cual (cuarentena en Nexo, que lo normaliza). `solo_conocidos` (el de #188) manda el vehículo solo si se resolvió y la carrocería solo con ese vehículo y si está en la lista de carrocerías de Nexo; si no, la línea va sin vehículo (Cupo queda `PorCostear`). `nunca` no manda ninguno (emergencia). Sin catálogo ni diccionario de Nexo el envío no falla (sigue el modo sin resolver, con un warning). Compatibilidad: `true` = `siempre`, `false` = `solo_conocidos`; otro valor no arranca. El polling empareja igual en los tres modos y compara contra el código que viajó (la traducción del alias queda registrada con el envío) |
| `Captura__AceptarVehiculoCliente` | no | no | **`true`** | manifiesto (#188) | **HU-032 (#197, ADR-022): interruptor de reversa del vehículo elegido.** Encendida, el vehículo elegido de cada ruta es el respaldo del cubicador (obligatorio sin mercancías cubicables salvo Contenedor, validado contra Nexo solo si se usa, con avisos por ruta; la edición y la re-solicitud lo conservan); apagada vuelve el comportamiento anterior (se ignora). Historia: bandera de respaldo (HU-021-FE.2). **HU-014 (#188, decisión del 2026-10-19): encendida**: el vehículo y la carrocería que manda el frontend (HU-014 #188, M1: deben ser códigos que Nexo conozca, del catálogo de tipos de vehículo de Nexo y de su lista de carrocerías; uno que solo conoce el cubicador es 400) van solo en el camión del respaldo (cubicador sin configurar, ruta sin mercancías tipadas o cubicador caído con `Cubicador__RespaldoSiFalla`). Si el cubicador recomienda, manda el cubicador. Apagada rige la decisión 7 |
| `Captura__ClasesSinVehiculoObligatorio__0`, `__1`... | no | no | **`Contenedor`** (sin configurar) | manifiesto (endurecimiento A, #199): `__0` con `Contenedor` y `__1` vacío | **HU-032 (#197, decisión del 2026-10-21):** clases de viaje en las que una ruta que no se puede cubicar **no** exige el vehículo elegido (`VEHICULO_REQUERIDO`). Clases de Nexo (`Contenedor`, `Cupo`, `Carga Suelta`, `Extradimensionado`), sin distinguir mayúsculas (se normalizan a la escritura de Nexo) y sin contar los vacíos; la lista configurada **reemplaza** a la de por defecto (para eximir también Carga Suelta: `__0=Contenedor`, `__1=Carga Suelta`); sin `Contenedor` deja el warning `CAPTURA_CONTENEDOR_EXIGE_VEHICULO` al arrancar. Un valor desconocido no arranca |
| `BaseDatos__MotivoBusquedaAgotada` | no | no | `auto` | manifiesto (endurecimiento A, #199) | HU-023 (#176): `auto` detecta `db/scripts/07_motivo_busqueda_agotada.sql` (exige `VIEW DEFINITION` sobre `dbo_cotifly` para `cotifly_app`); `habilitado` asume el 07 aplicado sin consultar (solo con el 07 aplicado: sin él, guardar `BUSQUEDA_AGOTADA` falla); `deshabilitado` guarda el motivo en NULL con un aviso. Otro valor no arranca |
| `Trm__Proveedor` | no | no | `dolarapi_con_respaldo` | manifiesto | cadena de fuentes de la TRM (ADR-014): `dolarapi_con_respaldo`, `dolarapi` o `superfinanciera`; otro valor no arranca |
| `Trm__UrlDolarApi` | no | **sí** (vacía no arranca) | `https://co.dolarapi.com/v1/trm` | manifiesto | TRM del día, fuente principal (agregador no oficial); exige salida a `co.dolarapi.com:443` |
| `Trm__Url` | no | **sí** (vacía no arranca) | `https://www.datos.gov.co/resource/32sa-8pi3.json` | manifiesto | TRM del día (frente C), datos abiertos de la Superintendencia, respaldo; exige salida a `www.datos.gov.co:443` |
| `Trm__TiempoEsperaSegundos` | no | no | `10` | manifiesto | límite de la consulta de la TRM a la Superintendencia (2 a 60) |
| `Trm__TiempoEsperaDolarApiSegundos` | no | no | `4` | manifiesto | límite de la consulta de la TRM a DolarApi (2 a 60), corto para no demorar el respaldo |
| `Pdf__StorageModo`, `Pdf__StorageDir` | no | no | `volumen`, `/data/pdfs` | manifiesto | |
| `Pdf__BaseUrl` | no | no (no se valida al arrancar) | vacío | Dokploy | URL pública de la API (`https://<PUBLIC_HOST>`). Vacía, el enlace del PDF queda relativo (`/pdfs/{folio}.pdf`): sirve en la web |
| `Pdf__TiempoEsperaRenderSegundos` | no | no | `30` (1-600) | manifiesto (endurecimiento A, #199) | límite del render del PDF |
| `Pdf__PermitirCotizacionChat` | no | no | `false` | manifiesto, **fijo** en `false` (no se lee del entorno; endurecimiento A, #199) | `false` = la cotización legada del chat responde `COTIZACION_CHAT_RETIRADA` (HU-021.6). Solo las pruebas del PDF legado la encienden |
| `Correo__Habilitado`, `Correo__Modo` | no | no | `false`, `mailsender` | Dokploy (`Habilitado`); `Modo` va **fijo** en los manifiestos (`mailsender`, no se lee del entorno) | HU-033 (#199, ADR-023): el correo sale por el microservicio interno MailSender; `smtp_relay`, `smtp` y `wscorreos` se retiraron (con el correo habilitado no arrancan) |
| `Correo__MailSender__UrlBase` | no | con el correo habilitado | vacío | Dokploy | URL interna de MailSender (sin autenticación: red interna). La de QA se carga en Dokploy, **nunca** en el repositorio |
| `Correo__MailSender__TimeoutSegundos`, `__Remitente` | no | con el correo habilitado (`TimeoutSegundos` de 1 a 120, `Remitente` un correo válido) | `15`, `notificaciones@boterosoto.com.co` | manifiesto | `Remitente`: `From` con `ModoRemitente=corporativo` o cuando el correo del comercial falta, no es válido o su dominio no está en `DominiosRemitente` |
| `Correo__MailSender__ModoRemitente`, `__DominiosRemitente` | no | con el correo habilitado, `ModoRemitente` debe ser `comercial` o `corporativo` | `comercial`, `boterosoto.com.co` | manifiesto | auditoría #199 (ADR-023 §4): `comercial` pone en el `From` el correo del comercial si es una dirección válida de un dominio de `DominiosRemitente` (separados por coma); `corporativo` usa siempre `Remitente` (cuando SPF/DMARC no autoricen al relay a enviar como cada comercial) |
| `Correo__MailSender__Prioridad` | no | con el correo habilitado: `Low`, `Normal` o `High` | `Normal` | manifiesto (endurecimiento A, #199) | cabeceras `Priority`/`Importance`/`X-Priority` del correo |
| `Correo__Proteccion__Modo` | no | fuera de Production con el correo habilitado debe ser `redirigir` | `redirigir` | manifiesto (staging: **fijo** en `redirigir`) | todo correo va solo al buzón de pruebas, con `[QA]` en el asunto y los destinatarios originales en el cuerpo. `ninguno` solo en producción, al salir en vivo y **junto con** `Correo__Proteccion__PermitirEnvioReal=true` (segunda llave). Un modo desconocido no arranca |
| `Correo__Proteccion__PermitirEnvioReal` | no | con el correo habilitado va con `Modo`: `true` solo con `ninguno` y `false` con `redirigir` (una llave sin la otra no arranca); fuera de Production, `false` | `false` | manifiesto (staging: **fijo** en `false`) | HU-033 (ADR-023 §3), segunda llave del envío real: solo `Modo=ninguno` **y** `PermitirEnvioReal=true` (y `Production`) mandan el correo a los destinatarios reales; en cualquier otro caso se redirige al buzón de pruebas |
| `Correo__Proteccion__BuzonPruebas` | no | con `redirigir` y el correo habilitado (un correo válido) | vacío | Dokploy | **buzón de pruebas por definir**; sin él la API no arranca con el correo habilitado y `redirigir` |
| `Correo__MaximoDestinatarios` | no | de 1 a 100 (fuera de rango no arranca) | `10` | manifiesto | #228 (decisión del 2026-10-27): tope de destinatarios entre `para` y `cc` juntos (la copia oculta al comercial no cuenta); pasado el tope, 400 `CORREO_DEMASIADOS_DESTINATARIOS` con `detalle.maximo` = este valor |
| `Correo__Seguimiento__IntervaloTickSegundos`, `__IntervaloConsultaSegundos`, `__EdadMaximaHoras`, `__Lote`, `__ReclamoSegundos` | no | rangos: 1-3600, 1-3600, 1-720, 1-1000 y 1-3600 (fuera de rango no arranca) | `15`, `30`, `24`, `50`, `60` | manifiesto (endurecimiento A, #199) | worker que consulta el estado en MailSender: cada cuánto despierta, espera entre consultas del mismo correo, horas sin confirmación antes de `fallido`, correos por vuelta y segundos que una réplica reserva un correo mientras lo consulta |
| `IA__Proveedor`, `IA__Modelo`, `IA__Endpoint` | no | no (sin proveedor el agente no responde) | vacío | Dokploy | ADR-003. Con OmniRoute (gateway LLM interno, #84): `IA__Proveedor=openai_compatible`, `IA__Endpoint=http://llm-services-omniroute.10-10-40-17.sslip.io/v1` (HTTP dentro de la red interna, sin TLS; exige salida del worker hacia `10.10.40.17:80`) y `IA__Modelo=gemini/gemini-3.5-flash-lite`. Matriz de modelos en [docs/03](backend-dotnet/docs/03-infraestructura.md#omniroute). **Nunca** un gateway web no oficial de los que lista OmniRoute (`duckduckgo-web`, `theoldllm`, `veoaifree-web`, `chipotle`): riesgo de confidencialidad |
| `IA__ApiKey` | **sí** (≥ 16) | según el proveedor (obligatoria con openai, azure_openai, gemini y anthropic); si tiene valor, en Production no puede ser un placeholder ni tener menos de 16 caracteres | vacío | Dokploy | **secreto de Dokploy**, nunca en el repositorio ni en el manifiesto. Con OmniRoute es la clave del gateway (`Authorization: Bearer`); como el tráfico va por HTTP, solo debe viajar dentro de la red interna. La clave usada en las pruebas del 2026-10-01 se compartió por chat: **rotarla** antes de cargarla en staging |
| `IA__Fallbacks__N__Proveedor`, `__Modelo`, `__Endpoint` | no | no | vacío | Dokploy | respaldos ante 429, 5xx, red caída o el 400 "No credentials for provider" de OmniRoute. Los manifiestos declaran `__0__` y `__1__` con `${...:-}`: un respaldo con `Proveedor` vacío no existe (si tiene modelo, endpoint o clave sin proveedor, la API no arranca). Con OmniRoute: `__0__` `gemini/gemini-3.5-flash`, `__1__` `gemini/gemini-3-flash-preview`, mismo endpoint y clave |
| `IA__Fallbacks__N__ApiKey` | **sí** (≥ 16) | igual que `IA__ApiKey`, por respaldo | vacío | Dokploy | secreto de Dokploy; con OmniRoute, la misma clave del gateway |
| `IA__MaxTokens`, `IA__MaxIteraciones`, `IA__SoportaJsonSchema`, `IA__MinutosSinJsonSchema`, `IA__JsonSchemaEstricto`, `IA__Rpm`, `IA__TimeoutSeg` | no | no | `4096`, `12`, `true`, `30`, `false`, `8`, `60` | manifiesto | con OmniRoute y Gemini: `IA__SoportaJsonSchema=false` (Gemini vía OmniRoute ignora `json_schema`) e `IA__MaxTokens=1500` (con 4096 OpenRouter respondió 402 porque reserva crédito por el máximo pedido) |
| `IA__EsperaMaximaRpmSegundos`, `IA__PropuestaDirecta`, `IA__RespuestaFinalEnLoop` | no | no | `20` (1-60), `true`, `true` | manifiesto (endurecimiento A, #199) | espera máxima por el cupo de `IA__Rpm`; propuesta directa y respuesta final dentro del bucle del asistente |
| `LimiteChat__PeticionesPorVentana`, `LimiteChat__VentanaSegundos` | no | no | `20`, `60` | manifiesto | |
| `Sesiones__LimiteTokens`, `Sesiones__TtlHoras`, `Sesiones__BarridoMinutos`, `Sesiones__EsperaTurnoSegundos` | no | no | `6000`, `24`, `60`, `3` | manifiesto | |
| `Grovity__BaseUrl`, `Grovity__Usuario` / `Grovity__Clave` | no / **sí** | no (el código no las lee) | — | — | solo si se aprueba Grovity desde el agente (ADR-005) |

**OmniRoute: riesgo de transporte y de datos (#84).**

- **Riesgo, HTTP sin cifrar.** OmniRoute se expone como `http://llm-services-omniroute.10-10-40-17.sslip.io` (puerto 80 del manager `10.10.40.17`). Cada turno del agente viaja en claro por la red interna: nombres y NIT de clientes, costos, márgenes y precios, el correo del comercial y la clave `Authorization: Bearer`. Cualquier equipo con acceso a ese segmento puede leerlos o reutilizar la clave.
- **Recomendado antes de producción:** publicar OmniRoute por HTTPS (router de Traefik con TLS en el clúster, en la línea de #25/#26) y cambiar `IA__Endpoint` a `https://...`. Como mínimo, aislar el tráfico worker → manager (overlay o reglas de firewall que solo permitan al worker del API).
- **Rotación de la clave de OmniRoute:**
  - **cuándo:** antes de cargarla en staging (la de las pruebas del 2026-10-01 se compartió por chat); después, cada 6 meses o de inmediato ante cualquier exposición (chat, log, captura, repositorio, salida de alguien con acceso);
  - **cómo:** (1) el dueño de OmniRoute emite una clave nueva; (2) actualizar `IA__ApiKey` e `IA__Fallbacks__N__ApiKey` en Dokploy (staging y producción); (3) redesplegar el stack (*Deploy* en Dokploy) y probar un turno de `/web/chat`; (4) revocar la anterior en OmniRoute; (5) anotar la fecha en la bitácora de operaciones.
- **Datos y términos del proveedor:** pendiente con negocio y legal (ADR-003 "Pendientes"). Hay que confirmar qué credencial tiene cargada OmniRoute, la residencia de los datos y si OmniRoute registra los prompts.

**Endurecimiento A (#199, 2026-10-22): todas las opciones que el backend valida al arrancar están declaradas en los dos manifiestos**
(`infra/stack.dotnet.dokploy.yml` e `infra/stack.dotnet.staging.yml`), con `${VAR:-<valor por defecto del código>}`: las `Cubicador__*`
(incluidas `UrlBase` y `ApiKey`, vacías: se cargan en Dokploy), `Cubicacion__*`, `CandadoSql__*`, `Nexo__MinutosCacheCatalogos`,
`Nexo__HorasCatalogosVencidos`, los `CosteoNexo__*` de conexos, relectura y sincronización del resultado, `Correo__MailSender__Prioridad`,
`Correo__Seguimiento__*`, `Captura__ClasesSinVehiculoObligatorio__0`/`__1`, `BaseDatos__MotivoBusquedaAgotada`,
`SeguridadHttp__LimiteCuerpoLicitacionBytes`, `Pdf__TiempoEsperaRenderSegundos`, `IA__EsperaMaximaRpmSegundos`, `IA__PropuestaDirecta` e
`IA__RespuestaFinalEnLoop`; `Pdf__PermitirCotizacionChat` va **fija** en `false`. Antes no estaban y regía el valor del código aunque se
cargaran en Dokploy (en particular, el cubicador quedaba sin configurar en los ambientes desplegados). La prueba
`clsManifiestosDespliegueTests` (Cotifly.Api.Tests) lee el YAML y falla si una opción enlazada falta en un manifiesto (salvo
`ForwardedHeaders__ProxiesConocidos__N`: vacía no arranca), si un manifiesto declara una variable que el código no lee o si un valor por
defecto difiere del código sin estar en su lista de diferencias a propósito. **Al agregar una opción, declararla en los dos manifiestos
en el mismo commit.** **HU-014
(#188, decisión del 2026-10-19):** las tres `Nexo__Enviar*` y `Captura__AceptarVehiculoCliente` se declaran en los dos manifiestos con
valor explícito (`EnviarGrupoRutaId=true`, `EnviarDatosCubicador=true`, **`EnviarCodigosCubicador=siempre`** desde HU-031.1 #194,
decisión del 2026-10-20, y **`AceptarVehiculoCliente=true`**), para que un cambio del valor por defecto en el código no cambie un ambiente
sin querer. Si Dokploy ya tiene cargada alguna de las cuatro, revisar que tenga ese valor: un `Nexo__EnviarCodigosCubicador=true` cargado
antes se lee como `siempre`, que ahora es el valor buscado (ya no es un riesgo: Nexo tiene la bandeja de normalización, NEXO #322); un
`false` o un `solo_conocidos` cargados antes dejan el ambiente en `solo_conocidos` (los códigos que nadie conoce viajan sin vehículo):
**borrarla** o ponerla en `siempre`.
**Base de datos:** antes de desplegar #176 conviene aplicar `db/scripts/07_motivo_busqueda_agotada.sql` (sin él el backend arranca
igual: guarda `BUSQUEDA_AGOTADA` en NULL con un aviso y `/health/ready` solo lo informa). La autodetección del 07 lee la
definición del CHECK en `sys.check_constraints`, así que `cotifly_app` necesita `GRANT VIEW DEFINITION ON SCHEMA::dbo_cotifly TO cotifly_app;`
(sin él, `/health/ready` dice "no se puede verificar el script 07 (falta VIEW DEFINITION…)" y el motivo se guarda en NULL). Si el DBA no
otorga el permiso, con el 07 ya aplicado se carga `BaseDatos__MotivoBusquedaAgotada=habilitado` en Dokploy (ver la tabla de variables; ya está declarada en
los manifiestos desde el endurecimiento A, #199).
HU-018.1 (#155): antes de desplegar se aplica `db/scripts/09_score_prioridad.sql` (`CotizacionesSolicitud.scorePrioridad DECIMAL(9,4) NULL`,
idempotente, sin respaldo porque solo agrega una columna NULL). A diferencia del 07 **sí es obligatorio**: el backend mapea la columna y
sin ella `/health/ready` responde 503 "esquema incompleto: falta 09" (§4.5). No lleva variable ni permiso nuevo.
HU-028.1 (#183): antes de desplegar se aplica `db/scripts/10_rechazo_linea.sql` (rechazo de la línea por la Mesa de Costeo de Nexo en el
camión: `motivoRechazo`, `rechazadaAt` y `'rechazada'` en los CHECK de `estadoCosteo`; idempotente, sin respaldo; **lo revisa
@scalderon-bs**). **Obligatorio**, como el 09: sin él `/health/ready` responde 503 "esquema incompleto: falta 10". No lleva variable ni
permiso nuevo. Requiere un Nexo con #320 para que haya rechazos; con un Nexo anterior la línea nunca llega rechazada y nada cambia.
HU-030.1 (#184): antes de desplegar se aplica `db/scripts/11_peso_con_tara.sql` (`CotizacionLineasSolicitud.pesoConTaraKg DECIMAL(12,2) NULL`
con su `CHECK` > 0; idempotente, sin respaldo porque solo agrega una columna NULL). **Obligatorio**, como el 09: el backend mapea la
columna y sin ella `/health/ready` responde 503 "esquema incompleto: falta 11" (§4.5). No lleva variable ni permiso nuevo.

HU-032 (#197): antes de desplegar se aplica `db/scripts/12_vehiculo_elegido.sql` (en la ruta, `tipoVehiculoElegido` y `carroceriaElegida NVARCHAR(100)
NULL`; en la mercancía, `largoCm`/`anchoCm`/`altoCm` NULL con el CHECK "las tres NULL o las tres > 0"). El backend mapea las columnas: sin
ellas `/health/ready` responde 503 "esquema incompleto: falta 12" (§4.5). `Captura__AceptarVehiculoCliente` (por defecto `true`) pasa a ser el
interruptor de reversa del vehículo elegido (ADR-022): `false` vuelve al comportamiento anterior sin redesplegar.
`Captura__ClasesSinVehiculoObligatorio` (por defecto solo `Contenedor`) define las clases de viaje que no exigen el vehículo; un valor que
no es una clase de viaje de Nexo impide arrancar. **Folios anteriores:** la edición y la re-solicitud con `datos_carga` solo exigen el
vehículo en una ruta nueva o que cambia (peso, mercancías, clase de viaje o vehículo elegido): un folio de Cupo anterior a HU-032 que solo
trae el peso se sigue editando y re-solicitando sin vehículo mientras no cambie esa ruta.

HU-033 (#199, ADR-023): antes de desplegar se aplica `db/scripts/13_correo_mailsender.sql` (en `Correos`, el id de MailSender, el buzón de
redirección, los intentos, la fecha de envío y la agenda del worker, con el CHECK del estado `encolado|enviando|enviado|fallido`; en la
cabecera, la sincronización del resultado con Nexo). El backend mapea las columnas: sin ellas `/health/ready` responde 503 "esquema
incompleto: falta 13" (§4.5). Para activar el correo: `Correo__Habilitado=true`, `Correo__MailSender__UrlBase` (Dokploy) y
`Correo__Proteccion__BuzonPruebas`. **Ojo: staging y QA corren con `ASPNETCORE_ENVIRONMENT=Production`**, así que la regla "fuera de
Production, `redirigir` obligatorio" del validador no los distingue: por eso `infra/stack.dotnet.staging.yml` fija
`Correo__Proteccion__Modo=redirigir` (no se lee del entorno) y en el Dokploy de QA no se debe cargar `Correo__Proteccion__Modo=ninguno` ni
`Correo__Proteccion__PermitirEnvioReal=true`. En producción el envío real exige las **dos llaves juntas**, `Correo__Proteccion__Modo=ninguno`
y `Correo__Proteccion__PermitirEnvioReal=true` (con el correo habilitado, una sin la otra impide arrancar), y se ponen solo al salir en vivo, con SPF/DMARC revisados por TI (el relay autoriza la IP de MailSender, no la de
Cotifly).

**Riesgo de despliegue: la huella de la `Idempotency-Key` (#191, B1 de la auditoría de HU-032).** La huella con la que se reconoce un
reintento se calcula sobre la entrada ya convertida al registro de dominio (`clsEntradaCaptura`, `clsEntradaEdicionSolicitud`,
`clsEntradaReSolicitudCosteo`), no sobre el JSON de la petición: cuando una versión agrega o cambia propiedades de esos registros (HU-032
agregó el vehículo elegido, las medidas opcionales y propiedades calculadas como la cubicabilidad de la ruta), **la misma petición da otra
huella antes y después del despliegue**. Durante las 24 h de vida de una clave, un reintento con la misma `Idempotency-Key` que cruza el
despliegue (enviado antes, repetido después) responde **409 `IDEMPOTENCY_KEY_REUTILIZADA`** en vez de devolver el resultado guardado.
No duplica nada (la primera ejecución ya quedó y el 409 no ejecuta otra vez), pero el frontend debe tratar ese 409 como "revisa el folio en
Mis cotizaciones" y no reintentar con otra clave a ciegas. Mitigación: desplegar en un horario sin capturas en curso. Moverla al DTO de la
petición no se hizo en HU-032: toca las firmas de las cuatro operaciones idempotentes (captura, conexos, re-solicitud y edición) y el DTO
también cambia de forma entre versiones, así que no elimina el riesgo; queda como deuda.
#191 (HU-026.1): antes de desplegar se aplica `db/scripts/06_idempotencia_captura.sql` (tabla `dbo_cotifly.CapturasIdempotentes`: la
`Idempotency-Key` de la captura, los conexos, la re-solicitud y la edición, persistente y compartida entre réplicas). **Obligatorio**: sin la
tabla `/health/ready` responde 503 "esquema incompleto: falta 06" (la API no degrada a memoria). No lleva variable ni permiso nuevo (la
limpieza de las claves vencidas la hace la API cada hora). Todas las variables están en `backend-dotnet/.env.example` y, desde el
endurecimiento A (#199), declaradas en los manifiestos (`Cubicador__ApiKey` se carga en Dokploy como secreto).

**Base de datos, HU-014.3 (#152):** el backend ya no expone `GET /web/metricas/actividad` ni `/web/metricas/ajustes-margen` (responden
404) y no lee las vistas `vActividadPorComercial` ni `vAjustesMargenPorComercial`. **Después** de desplegar esta versión, el DBA aplica
`db/scripts/08_retirar_vistas_actividad.sql`, que las borra (idempotente, `IF OBJECT_ID(...) IS NOT NULL DROP VIEW`; no toca tablas ni
datos). Antes del despliegue no: la versión anterior las sigue leyendo (si se aplica antes con el ejecutador, ver §4.5, paso 0). Si se
demora, no pasa nada: el backend nuevo no las usa y `/health/ready` no las revisa.

**Variables a cargar en Dokploy por ambiente (HU-021).** Declaradas en los manifiestos con `${VAR:-<defecto>}` desde el endurecimiento
A (#199, con la aprobación del usuario); se cargan en Dokploy así:

| Variable | Staging (`infra/stack.dotnet.staging.yml`) | Producción (`infra/stack.dotnet.dokploy.yml`) | Cuándo |
|---|---|---|---|
| `Cubicador__UrlBase` | URL del cubicador de pruebas | URL del cubicador de producción | Cuando el cubicador publique `POST /recomendaciones` (C1) |
| `Cubicador__ApiKey` | secreto de Dokploy (≥ 16) | secreto de Dokploy (≥ 16), distinto del de staging | Con `Cubicador__UrlBase` |
| `Cubicador__RespaldoSiFalla` | `true` durante la estabilización; luego `false` | `false` | Con `Cubicador__UrlBase` |
| `Cubicador__TiempoEsperaSegundos`, `__TiempoEsperaRecomendacionSegundos`, `__MinutosCacheCatalogos`, `__PresupuestoCapturaSegundos`, `__TiempoMaximoCatalogoSegundos` | por defecto (`15`, `90`, `30`, `180`, `10`) | por defecto | Solo si hay que ajustarlas |
| `Cubicador__MaximoConcurrente`, `__EsperaMaximaCupoSegundos`, `__EsperaAntesReconsultaSegundos`, `__TiempoEsperaReconsultaSegundos`, `__EsperaSincronaCubicacionSegundos`, `__RetencionCubicacionMinutos`, `__MaximoCubicacionesEnMemoria` | por defecto (`1`, `60`, `5`, `60`, `8`, `15`, `500`) | por defecto | Opciones C y D (2026-10-19). `MaximoConcurrente` > 1 solo cuando el cubicador corrija D3 (opción F) |
| `CosteoNexo__MaximoReintentosConexos`, `__IntervaloReintentoConexosMinutos`, `__EsperaMaximaCatalogoConexosHoras` | por defecto (`5`, `5`, `24`) | por defecto | Solo si hay que ajustarlas |
| `Nexo__EnviarGrupoRutaId` | por defecto (`true`); `false` solo si Nexo rechaza el alta | por defecto (`true`) | HU-023.4, #176 |
| `Nexo__EnviarDatosCubicador` | por defecto (`true`); `false` solo como interruptor de emergencia | por defecto (`true`) | HU-023.4, #176 |
| `Nexo__EnviarCodigosCubicador` | `siempre` (manifiesto; Nexo ya tiene la bandeja de normalización, NEXO #322); `nunca` como emergencia | `siempre` (manifiesto) | Nexo #297, #176; decisiones del 2026-10-19 (#188) y del 2026-10-20 (#194) |
| `Captura__AceptarVehiculoCliente` | `true` (manifiesto) | `true` (manifiesto) | HU-021-FE.2; decisión del 2026-10-19 (#188) |
| `Captura__ClasesSinVehiculoObligatorio__0`, `__1`... | por defecto (solo `Contenedor`) | por defecto (solo `Contenedor`) | HU-032 (#197): solo si negocio exime otra clase de viaje (p. ej. `Carga Suelta`); se declaran las dos |
| `Pdf__PermitirCotizacionChat` | `false` | `false` | Nunca se enciende fuera de las pruebas |

### Identidad compartida con Nexo (#172)

Cotifly manda a Nexo `X-Portal-clsUsuario-Id = UsuariosWeb.idUsuarioExterno`, que es el `ID` de `tblSAPUsuariosApp`. Nexo lo valida
contra su base de identidad (`ConnectionStrings:IdentidadPortal`), así que **las dos apps deben leer la misma base**. Hoy QA lee
`dbsolucionesti`, donde los `ID` son otros (dguerra es 430 ahí y 10123 en el portal), y Nexo responde 401. El despliegue requiere
la aprobación del usuario.

| Variable | Staging / QA | Producción | Nota |
|---|---|---|---|
| `ConnectionStrings__Identidad` | `Database=dbSolucionesTI_Prueba` (el resto de la cadena igual: `cotifly_app`, `Encrypt=True`) | la base de identidad que use Nexo en producción (**por confirmar**) | secreto de Dokploy; solo cambia el `Database` |

En este orden (los pasos 1 y 2 antes de la ventana; el 3 dentro de ella):

1. **DBA, en `dbSolucionesTI_Prueba`:** usuario `cotifly_app` con `SELECT` por columna sobre `dbo.tblSAPUsuariosApp (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL)` (sin `CLAVE`), `dbo.tblSAPUsuarioRoles (IDUSUARIO, IDROLES)` y `dbo.tblSAPRoles (IDROLES, DESCRIPCION)`. Confirmar con `sys.columns` las columnas que lista [10-operacion](backend-dotnet/docs/10-operacion.md#identidad-compartida-con-nexo-172).
2. **DBA, en `dbo_cotifly.Roles`:** revisar que `idRolExterno` de `comercial` y `gerente_comercial` sean los `IDROLES` de `dbSolucionesTI_Prueba.dbo.tblSAPRoles` (pueden no ser los de `dbsolucionesti`). Si no coinciden, nadie entra (401 `sin_rol_autorizado`).
3. **Ventana del cambio (con aprobación), en `dbo_cotifly.UsuariosWeb` de QA:** los usuarios que ya entraron quedaron vinculados al `ID` de `dbsolucionesti`; con la base nueva el login los rechaza (401 `identidad_inconsistente`). Hay que soltar el vínculo una vez con [`db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql`](db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql) (respalda los pares `id`/`idUsuarioExterno` en `dbo_cotifly.RespaldoVinculosIdentidad_172`, los pone en NULL, es idempotente y trae la consulta de verificación; `sqlcmd -I -b`). **Orden dentro de la ventana:**
   1. Detener el servicio (escalar a 0 réplicas en Dokploy) o, como mínimo, cambiar primero `ConnectionStrings__Identidad` y redesplegar.
   2. Correr el script **con el servicio detenido, o justo después del cambio de variable y antes de que entren usuarios.** Si corre mientras la API sigue con la configuración vieja (`dbsolucionesti`), el siguiente login vuelve a vincular el `ID` viejo (dguerra: 430) y el cambio queda a medias.
   3. Levantar el servicio con la variable nueva.

   En su siguiente login o refresh cada persona queda vinculada al `ID` del portal; hasta entonces, sus llamadas a Nexo no llevan identidad.
4. **Pendiente externo:** Nexo QA debe configurar su `ConnectionStrings:IdentidadPortal`. Sin ella, Nexo QA valida contra su tabla `Usuarios` local.

Variables del Python que **no** pasan al backend .NET (se retiran; no cargarlas en el stack nuevo):
`DATABASE_URL`, `POSTGRES_*`, `MSSQL_SA_PASSWORD`, `MCP_URL`, `MCP_AUTH_TOKEN` (no hay MCP aparte, ADR-002),
`WEB_SESSION_SECRET` (reemplazada por `Jwt__SigningKey`), `INTERNAL_API_TOKEN`, `LLM_MODEL`/`LLM_API_KEY`/`LLM_RPM`
(reemplazadas por `IA__*`), `GCHAT_*` y `GOOGLE_SA_JSON` (Google Chat retirado en #105), y `DRIVE_TARIFARIO_*` (tarifario
de Drive retirado), `TARIFA_*`, `S3_*` y `MINIO_*` (MinIO no se mantiene, ADR-012), `SHEETS_*` (diferida, ADR-012),
`SMTP_*`/`SMTP_RELAY_*`/`CORREO_*` (reemplazadas por `Correo__*`), `SAP_TM_*`/`SAP_SALES_ORG_DEFAULT` (reemplazadas por
`SapTm__*`: `SalesOrgDefault`, `AvisoSincronizacionHoras` y, desde el 2026-11-03, `Url`, `User`, `Pass`, `SapClient` y los ajustes del worker), `PDF_*` (reemplazadas por `Pdf__*`), `PUBLIC_HOST` (sigue como variable del manifiesto para Traefik, no
de la app).

### Rol director (HU-037, #209 a #212)

El rol interno `director` (ADR-016 §11) usa todo lo del gerente comercial y ve todas las regionales. Tres piezas de base de datos, de
Datos (@scalderon-bs), y una de backend:

| Pieza | Qué es | Cuándo |
|---|---|---|
| `db/scripts/15_rol_director.sql` (#209, parte A) | CHECK `ck_roles_codigo` con `director` y la fila `director` activa con `idRolExterno = NULL`. Idempotente, en una transacción. Lo toma el ejecutador | **Junto con o después de la imagen de HU-037.2 (#210), nunca antes con una imagen vieja** (ver abajo). No lo exige `/health/ready`: no agrega columnas y sin él nadie entra como director (falla cerrado) |
| `db/scripts/operacion/mapeo_prueba_rol_director_209.sql` (#209, parte B) | **Solo prueba / compartida:** `gerente_comercial` 31 → 25 (`ClienteProAdmin`, prestado, provisional) y **después** `director` → 31 (`ClienteProGerencial`). El orden importa: `uq_roles_idRolExterno` es único. Respaldo en `RespaldoRoles_209` | Después del 15. Lo corre Datos con `sqlcmd -I -b` |
| Script de operación de **producción** (#212) | ⚠ **NO OLVIDAR antes de producción:** `comercial` 34, `gerente_comercial` 29 (`ClienteProRegional`), `director` 31 (`ClienteProGerencial`), confirmando antes los IDROLES reales y quién tiene hoy `ClienteProGerencial`. Lo escribe y corre Datos | En la ventana del despliegue a producción. Sin él, gerente y director quedan con el mapeo de prueba |
| Imagen con HU-037.2 (#210) y HU-037.3 (#211) | Políticas, precedencia `director > gerente_comercial > comercial` (regla `precedencia_director`), alcance de todas las regionales, `GET /web/regionales` | Antes o junto con el 15 |

**Orden y riesgo con una imagen anterior a HU-037.** El backend anterior convierte cada fila de `Roles` a su enum de dos valores: con la
fila `director` presente **y activa** (aunque su `idRolExterno` siga en `NULL`), la lectura del catálogo lanza y **el login y el refresh de
todos** responden el 401 genérico `{"error":"Credenciales inválidas"}` (en el log: `Error inesperado en el login` con
`'director' no es un código válido de RolInterno`) mientras corra esa imagen. Comprobado en la validación de HU-037 con el código de
`origin/QA` sobre SQL Server de Testcontainers. Por eso el 15 va con (o después de) la imagen nueva. Desde HU-037.2 el repositorio de
roles ignora los códigos que no conoce (con un warning en el log que da el `id` del rol ignorado), así que un rol futuro ya no tumba el
login. Si el 15 ya está aplicado en un ambiente con una imagen vieja (en la compartida ya se aplicó, según #209):

- **Lo correcto:** desplegar la imagen de HU-037 (#210 y #211) cuanto antes.
- **Mientras tanto:** `UPDATE dbo_cotifly.Roles SET activo = 0, updatedAt = SYSDATETIMEOFFSET() WHERE codigo = N'director';` (el
  catálogo solo lee roles activos). Vuelven a entrar el comercial y el gerente cuyo rol externo siga mapeado a `gerente_comercial`; si ya
  se corrió el mapeo de prueba (#209 parte B), quien tiene el 31 (`ClienteProGerencial`, ahora del director) **no** entra con la imagen
  vieja (401) hasta desplegar la nueva. Con la imagen nueva, volver a `activo = 1`.

Quien hoy tenga el rol externo mapeado al gerente y pase a director (en prueba, el 31) tiene asignado `gerente_comercial` en
`UsuariosRoles`: su siguiente login y su refresh quedan en revisión (`rol_externo_cambio`, 401) hasta que se le reasigne el rol, como
cualquier cambio de rol (ADR-009). El access que ya tenía conserva su rol hasta vencer (60 minutos). La reasignación (DBA, con aprobación,
una persona a la vez):

```sql
DECLARE @u UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.UsuariosWeb WHERE nombreUsuario = N'<NOMBREUSUARIO>');
BEGIN TRANSACTION;
UPDATE dbo_cotifly.UsuariosRoles SET activo = 0 WHERE usuarioId = @u AND activo = 1;
INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId) SELECT @u, id FROM dbo_cotifly.Roles WHERE codigo = N'director';
COMMIT;
```

### Margen de SAP desde la copia local (HU-042, #242, #243, #247; ADR-027)

Al cotizar, la API lee el margen recomendado de `dbo_cotifly.SapMargenCliente`; no llama a SAP. **Desde el 2026-11-03 la copia la
sincroniza el propio backend** (worker `clsWorkerSincronizacionMargenSap`, ADR-027 §10 a §16). El SQL Agent Job de la #242
(`db/jobs/sincronizar_margen_sap.ps1` y `db/scripts/operacion/instalar_sql_agent_job_margen_sap.sql`) **no se instala**.

- **Antes de la imagen:** `db/scripts/17_sap_margen_sincronizacion.sql` y `db/scripts/18_sap_mapeo_fk_margenes_politica.sql` (sin ellos,
  `/health/ready` responde 503 "falta 17"/"falta 18"; aplicarlos la recupera sin redesplegar).
- **Permisos de `cotifly_app` (DBA):** `EXECUTE` sobre `dbo_cotifly.sp_sincronizar_margen_sap` e `INSERT` sobre
  `dbo_cotifly.SapMargenSyncLog` (con el `GRANT ... ON SCHEMA::dbo_cotifly` de 03-infraestructura ya están). `#SapMargenStage` es una tabla
  temporal: no pide permisos extra; el `MERGE` del stored procedure corre con los permisos de su dueño.
  ```sql
  GRANT EXECUTE ON OBJECT::dbo_cotifly.sp_sincronizar_margen_sap TO cotifly_app;
  GRANT INSERT ON OBJECT::dbo_cotifly.SapMargenSyncLog TO cotifly_app;
  ```
  En el DW, un usuario de solo lectura con `SELECT` sobre `DWCubosOperacionesEBS.dbo.DimBPSolicitante` (columnas `BP` y `NIT`).
- **Red desde el cluster (HU-009.1, #35):** salida del nodo donde corre `cotifly-api` hacia `sap-tm-prd.boterosoto.local:8000` (SAP TM,
  HTTP; el nombre debe resolver desde el contenedor) y hacia `10.10.40.24:1433` (DW). Probar desde el nodo antes de cargar las variables.
- **Variables en Dokploy:** `SapTm__Url`, `SapTm__User`, `SapTm__Pass` (secreto) y, si se quiere el NIT, `ConnectionStrings__DwOperaciones`
  (secreto). Los ajustes (`SapTm__ChequeoMinutos`, `__SincronizacionMinutos`, `__TimeoutSegundos`, `__MaximoBytes`, `__MinimoFilas`,
  `__MinimoPorcentajeActivas`) ya tienen su valor por defecto en el manifiesto. Borrar `SapTm__Modo` y `SapTm__ExportPath` si siguen cargadas.
- **Sin `Url`/`User`/`Pass`** el worker queda inactivo (un log Information) y, si la copia nunca se sincronizó, todo margen sale de la
  política con el detalle "SAP TM no configurado": es el comportamiento esperado en local. Con la URL o el mandante mal escritos la API
  arranca igual, el worker queda inactivo con un log Error y `/web/admin/sap-margen/*` responde 503 `SAP_MARGEN_NO_CONFIGURADO`.
  `SapTm__Url` es la **raíz del servicio** (`.../ZGW_RENTAB_CLI_SRV`); si trae `/RentabClienteSet` al final se acepta y se quita.
- **Interruptor `SapTm__SincronizacionHabilitada` (2026-11-04), `false` por defecto y en los dos manifiestos.** Apagado, el worker no
  corre aunque haya `SapTm__Url/User/Pass` (las del modo `odata` viejo ya no lo activan solas), `GET /web/admin/sap-margen/estado`
  responde con `habilitada = false` y la corrida forzada responde 503 `SAP_MARGEN_NO_CONFIGURADO`. Para encenderlo: cargar en Dokploy
  `SapTm__SincronizacionHabilitada=true` con `SapTm__Url` (raíz del servicio), `SapTm__User` y `SapTm__Pass`, redesplegar y forzar la
  primera corrida. La cotización lee la copia local con el interruptor encendido o apagado.
- **Timeout del proxy:** `POST /web/admin/sap-margen/sincronizar` es síncrono; en la práctica tarda segundos; el máximo con los límites actuales es de unos 10 min (120 s de SAP, la conexión y 60 s por lote de 500 BP en el DW, 120 s para leer los NIT existentes, 120 s de la carga y 120 s del stored procedure). Traefik (sin
  `respondingTimeouts.writeTimeout` ni `serversTransport.forwardingTimeouts.responseHeaderTimeout` que lo corten, como para la captura)
  debe cubrirlo. Si algo corta antes, la corrida sigue en el servidor y su resultado se ve con `GET /web/admin/sap-margen/estado`. Con la
  base de Cotifly caída, los dos endpoints responden 503 `SAP_MARGEN_NO_DISPONIBLE` en **20 a 45 s** (medido en el E2E del 2026-11-04):
  las lecturas de la sincronización usan un límite de conexión de 5 s, pero el tiempo lo suman la autenticación (recarga de
  `clsCacheTokensRevocados`) y la auditoría con EF, que usan la conexión normal. Es transversal a la API (`GET /web/mis-cotizaciones`
  tardó 30 s en dar 500 en la misma prueba); el timeout de Traefik y del front deben cubrirlo. Pendiente fuera de esta issue (ADR-027).
- **NIT por BP:** se busca con el BP tal como lo manda SAP (hoy sin ceros a la izquierda). Si `DimBPSolicitante.BP` los guarda con ceros,
  no cruzará: **pendiente de confirmar con Datos** (ADR-027 §13). Sin el DW, o sin el BP en el DW, la fila conserva su NIT anterior
  (decisión aceptada: puede quedar desactualizado si el BP sale del cubo).
- **Primera corrida:** después de desplegar, `POST /web/admin/sap-margen/sincronizar` con un gerente o director (responde 200 con
  `estado = ok` y las filas) o esperar el primer chequeo (30 s después del arranque). Verificar con `GET /web/admin/sap-margen/estado`
  ([10-operacion](backend-dotnet/docs/10-operacion.md#sincronización-del-margen-de-sap-tm-hu-0422)).
- **Varias réplicas:** cada réplica tiene su worker; el `sp_getapplock` del stored procedure hace que una segunda corrida simultánea quede
  `omitido` y la `ok` de una evita que las otras vuelvan a sincronizar en su chequeo.
- **Después de desplegar:** avisar a Datos (@scalderon-bs) para el script que retira `SapMapeoTrqType.lineaNegocio` (texto), #247.

### Multi-rol (HU-040, #229 y #230)

`db/scripts/16_multiples_roles_activos.sql` (#229, aprobado por @scalderon-bs el 2026-11-02) retira el índice único
`uq_usuariosRoles_usuarioId_activo`: una persona puede tener varias asignaciones activas en `UsuariosRoles` (una por rol; la unicidad
usuario + rol sigue). Además agrega `TokensRefresco.rolSesion` (el rol con el que se emitió la sesión). No cambia ninguna fila.

- **Va antes de la imagen de HU-040.1**: esa imagen mapea `rolSesion` y sin el 16 queda en 503 "falta 16" (aplicarlo la recupera sin
  redesplegar).
- **Es seguro con la imagen anterior** (a diferencia del 15): mientras nadie agregue una segunda asignación activa, todo usuario sigue con
  una y el alta JIT de esa imagen crea una sola; `rolSesion` queda NULL. Si Datos agrega una segunda asignación activa con la imagen
  anterior en marcha, esa imagen rechaza solo a ese usuario (401 genérico, `rol_externo_cambio`), falla cerrado. Probado con las pruebas
  de Testcontainers de #229 (login y refresh con una y con dos asignaciones activas sobre la base con el 16).
- Con la imagen de HU-040.1 (#229) quien tenga dos asignaciones activas entra con la jerarquía (`director` > `gerente_comercial` >
  `comercial`) si TODAS sus asignaciones activas siguen respaldadas por un rol de ClientePro; si alguna perdió su rol externo, 401
  `rol_externo_cambio`, como hoy. La elección del rol al entrar llega con HU-040.2 (#230).

Habilitar el multi-rol de una persona que ya existe (Datos, con aprobación; sus roles de ClientePro deben estar todos mapeados):

```sql
DECLARE @u UNIQUEIDENTIFIER = (SELECT id FROM dbo_cotifly.UsuariosWeb WHERE nombreUsuario = N'<NOMBREUSUARIO>');
DECLARE @r INT = (SELECT id FROM dbo_cotifly.Roles WHERE codigo = N'<rol>' AND activo = 1);
-- Si ya tuvo ese rol (fila inactiva por uq_usuariosRoles_usuarioId_rolId), se reactiva; si no, se agrega.
UPDATE dbo_cotifly.UsuariosRoles SET activo = 1 WHERE usuarioId = @u AND rolId = @r AND activo = 0;
INSERT INTO dbo_cotifly.UsuariosRoles (usuarioId, rolId)
    SELECT @u, @r WHERE @u IS NOT NULL AND @r IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM dbo_cotifly.UsuariosRoles ur WHERE ur.usuarioId = @u AND ur.rolId = @r);
```

Retirar un rol de una persona multi-rol: `UPDATE dbo_cotifly.UsuariosRoles SET activo = 0 WHERE usuarioId = @u AND rolId = @r;` (con
#230, sus sesiones con ese rol dejan de renovarse: el refresh responde 401 `rol_externo_cambio`; el access vigente dura hasta que vence).

### 2.3 Auditoría de secretos en el repositorio (2026-09-28)

Búsqueda de contraseñas, claves, tokens, cadenas de conexión y archivos de credenciales en el árbol actual y en el
historial de git (`git log --all -p`: claves privadas PEM, claves de Google `AIza...`, `sk-...`, `ghp_...`, archivos
`.env`, `.pem`, `.pfx`, `*sa.json`). **No se reescribe el historial.**

| Archivo | Hallazgo | Riesgo | Acción |
|---|---|---|---|
| Stack Python que corre hoy en Dokploy (antes `deploy/docker-compose.swarm.yml`, hoy solo en el tag `python-final`) | `POSTGRES_PASSWORD: bot`, `DATABASE_URL` con `bot:bot`, `MINIO_ROOT_USER`/`PASSWORD` y `S3_ACCESS_KEY`/`S3_SECRET_KEY` `minioadmin` | Credenciales por defecto en texto plano; servicios solo en la red interna `bot-back`, pero cualquiera con acceso al repo las conoce | El archivo ya no está en el repositorio, pero las credenciales siguen en el historial y en el stack desplegado. El stack .NET no las usa. **Rotarlas o apagar el stack Python en Dokploy** |
| `docker-compose.yml` (SQL Server local de desarrollo) | Valor por defecto `Bot_Cotizador_2019!` (sa) | Solo desarrollo local; se pueden sobrescribir por variable | Ninguna; no los usa el despliegue |
| `backend-dotnet/scripts/levantar-local.sh` y `.ps1` | Claves por defecto `CotiflyLocal#2026` (sa) y `CotiflyApp#2026` (cotifly_app) del SQL Server local | Solo el contenedor local, publicado en `127.0.0.1`; sobrescribibles con `COTIFLY_SQL_*_PASSWORD` | Ninguna |
| `secrets/google-sa.json` | Borrado en HU-022.1 (#146): tenía `{}` (sin clave); en el historial nunca tuvo `private_key`. `secrets/` está en el `.gitignore` | — | Ninguna |
| `backend-dotnet/.env.example`, `appsettings*.json`, docs | Solo placeholders (`<secreto>`, `<host>`); `appsettings*.json` no tiene secretos | — | Ninguna |
| Pruebas (`backend-dotnet/tests/`) | Claves ficticias de prueba (`sk-ant-clave-de-prueba-...`, `test-token-fase2`, `ya29.fake-token`) | Ninguno | Ninguna |
| Historial de git | Sin claves privadas, sin claves de API reales, sin `.env` versionado | — | Ninguna |

Información de infraestructura que no es secreta pero sí interna: IPs `10.10.40.x` (nodos, relay SMTP) y el nombre
`tbsdatacenter`. Se aceptan en el repositorio privado.

## 3. Tabla de variables de producción (plantilla para HU-007.1, #33)

Se llena en #33, variable por variable, **sin escribir aquí ningún valor secreto**: la columna "Valor confirmado" dice
solo *sí* o *no* (y para las variables no secretas puede llevar el valor). Los valores se cargan directamente en Dokploy
(§2.1). "Equivalente Python" sirve para la tercera casilla de #33 (qué estaba bien en el despliegue previo).

| Variable | Secreto | Fuente (quién la provee) | Valor confirmado | Responsable | Equivalente Python |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | no | fijo: `Production` | no | Backend | — |
| `ConnectionStrings__Cotifly` | sí | DBA: login `cotifly_app` y permisos por esquema (03-infraestructura §3.3) | no | DBA / Backend | `DATABASE_URL` |
| `ConnectionStrings__Identidad` | sí | DBA: `SELECT` por columna en `dbsolucionesti` | no | DBA / Backend | — |
| `ConnectionStrings__Regional` | sí | DBA: usuario SQL de solo lectura con `SELECT` únicamente sobre `dbo.DimEjecutivo` en `DWCubosEBSSAP`; infraestructura: salida desde el worker a `10.10.40.24:1433`. Opcional | no | DBA / Infraestructura | — |
| `Regional__Esquema`, `__Tabla`, `__ColumnaUsuario`, `__ColumnaRegional`, `__Dominio` | no | DBA confirma el esquema real de `DimEjecutivo` (hoy `dbo`, `DimEjecutivo`, `USUARIO`, `REGIONAL`, dominio `boterosoto`, #81) | no | DBA | — |
| `Regional__TiempoEsperaSegundos`, `Regional__TiempoEsperaLoginSegundos` | no | fijos: `10` y `3` salvo que se mida otra cosa en staging | no | Backend | — |
| `Identidad__Esquema` y tablas | no | DBA confirma ubicación real | no | DBA | — |
| `Jwt__SigningKey` | sí | generada por el equipo de despliegue (`openssl rand -base64 48`) | no | Backend | `WEB_SESSION_SECRET` |
| `Cors__OrigenesPermitidos__0` | no | equipo de frontend (dominio de `clientepro-frontend`) | no | Frontend | — |
| `ForwardedHeaders__Habilitado` / `RedesConocidas__0` | no | infraestructura (subred de `dokploy-network`) | no | Infraestructura | — |
| `PUBLIC_HOST` (Traefik) | no | infraestructura (DNS y TLS, HU-006.2) | no | Infraestructura | `PUBLIC_HOST` |
| `Pdf__BaseUrl` | no | `https://<PUBLIC_HOST>` | no | Backend | `PDF_BASE_URL` |
| `IA__Proveedor` / `IA__Modelo` / `IA__Endpoint` | no | negocio (ADR-003). Propuesta con OmniRoute: `openai_compatible`, `gemini/gemini-3.5-flash-lite`, endpoint interno (por HTTPS si se publica, §2.2) | no | Negocio / Backend | `LLM_MODEL` |
| `IA__ApiKey` (y respaldos) | sí | dueño de la cuenta del proveedor LLM (con OmniRoute, su administrador; rotar antes de staging, §2.2) | no | Negocio | `LLM_API_KEY` |
| `IA__Fallbacks__0__*`, `IA__Fallbacks__1__*` | `ApiKey` sí | negocio (ADR-003). Propuesta con OmniRoute: `gemini/gemini-3.5-flash` y `gemini/gemini-3-flash-preview` | no | Negocio / Backend | fallbacks de LiteLLM |
| `IA__SoportaJsonSchema` / `IA__MaxTokens` | no | con OmniRoute y Gemini: `false` y `1500` (§2.2) | no | Backend | `LLM_MAX_TOKENS` |
| `Nexo__UrlBase` | no | equipo de Nexo (URL de producción, no la de pruebas) | no | Backend / Nexo | — |
| `SapTm__Url` / `SapTm__User` / `SapTm__Pass` | `Pass` sí | SAP: el usuario técnico de `ZGW_RENTAB_CLI_SRV`; infraestructura: salida a `sap-tm-prd.boterosoto.local:8000` (HU-009.1) | no | SAP / Infraestructura / Backend | `SAP_TM_*` |
| `ConnectionStrings__DwOperaciones` | sí | DBA: usuario de solo lectura con `SELECT` sobre `dbo.DimBPSolicitante` en `DWCubosOperacionesEBS`; infraestructura: salida a `10.10.40.24:1433`. Opcional | no | DBA / Infraestructura | — |
| ~~`SapTm__Modo`, `SapTm__ExportPath`~~ | — | retiradas en HU-042.2 (ADR-027) | no aplica | — | `SAP_TM_*` |
| `Correo__Habilitado` / `Correo__MailSender__UrlBase` / `Correo__Proteccion__BuzonPruebas` | no | Backend (URL de MailSender) y negocio (buzón de pruebas, por definir) | no | Backend | `CORREO_*`, `SMTP_RELAY_HOST` (retiradas: el relay lo usa MailSender) |
| `MCP_AUTH_TOKEN`, `INTERNAL_API_TOKEN`, `S3_*`, `DRIVE_*`, `SHEETS_*` | — | se retiran (§2.2) | no aplica | Backend | sí |

## 4. Registry, imagen versionada y release (HU-006.4, #28)

### 4.1 Registry y tags

- **Registry:** GitHub Container Registry, `ghcr.io/innovacionia-proyectos/cotifly-backend` (paquete privado de la
  organización, enlazado al repositorio por la etiqueta OCI `org.opencontainers.image.source` del `Dockerfile`).
  Reemplaza a `${REGISTRY}` de los manifiestos del Python. En local la imagen se sigue llamando `cotifly-api:dev`.
- **Tags** (nunca solo `latest`; de hecho no se publica `latest`):

  | Evento | Tags | Uso |
  |---|---|---|
  | Push a `QA` | `sha-<corto>`, `qa` | staging |
  | Push a `main` | `sha-<corto>`, `main` | referencia |
  | Tag git `vX.Y.Z` sobre un commit de `main` | solo `X.Y.Z` | producción |
  | Tag git `vX.Y.Z-rc.N` (puede ser de `QA` u otra rama) | solo `X.Y.Z-rc.N` | candidata en staging |

- **Inmutables:** `sha-<corto>` y `X.Y.Z` nunca se reemplazan con otros bytes.
  - `sha-<corto>` se sube una sola vez; si ya existe (otro push del mismo commit, una re-ejecución), se conserva.
  - `X.Y.Z` **no se reconstruye**: se crea sobre el digest que ya tiene `sha-<corto>` con
    `docker buildx imagetools create --prefer-index=false -t ...:X.Y.Z ...:sha-<corto>`. `--prefer-index=false`
    evita que buildx envuelva el manifiesto en un índice OCI nuevo: `X.Y.Z` tiene **el mismo digest** que
    `sha-<corto>`, y producción corre exactamente los bytes que se probaron en staging.
  - Versión final sin `sha-<corto>` publicado: el CI **falla** ("espera a que termine la corrida de main") y nunca
    sube un build propio. Solo una candidata `X.Y.Z-rc.N` de un commit que nunca se publicó por rama sube la imagen
    construida y probada en su corrida.
  - En el CI un solo publicador por commit (`concurrency: publicar-imagen-<sha>`, sin cancelar): el push de `main` y
    el tag del mismo commit no compiten.
  - Una `X.Y.Z` que ya existe no se sobrescribe (el job o el script fallan).
  - `qa` y `main` son canales móviles (apuntan al último `sha-<corto>` del canal).
- **Dokploy despliega siempre un tag inmutable:** `sha-<corto>` o `X.Y.Z-rc.N` en staging y `X.Y.Z` en producción.
  Así el rollback es volver al tag anterior y cualquier réplica nueva baja exactamente la misma imagen.
- **Por qué se permiten candidatas fuera de `main`:** `vX.Y.Z-rc.N` sirve para probar en staging, con número de
  versión, un commit de `QA` antes de pasarlo a `main`. Las versiones finales exigen un commit de `main`.
- **Etiquetas OCI:** la versión del build (`qa-<corto>`, `main-<corto>`, `ci-<corto>`, o `X.Y.Z` si el commit nunca se
  publicó por rama) va en `org.opencontainers.image.version` y en la `InformationalVersion` del `.dll`. Como `X.Y.Z`
  reutiliza el digest de `sha-<corto>`, esa etiqueta suele decir `main-<corto>`: la versión de release la da el tag,
  y el commit `org.opencontainers.image.revision`. Para ver qué corre:
  `docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' <imagen>`.

### 4.2 Pipeline (GitHub Actions)

`.github/workflows/backend-dotnet.yml` (el mismo CI de #14, extendido):

1. `imagen-docker` construye la imagen con la versión del evento, corre la prueba de humo (`HEALTHCHECK` *healthy*,
   `/health` 200, `/health/ready` 503 sin base), la escanea con Trivy 0.74.0 fijado por digest (falla con
   vulnerabilidades HIGH o CRITICAL que tengan parche) y, solo en push, guarda esa misma imagen como artefacto. El
   nombre del tag git se valida como semver y nunca se interpola con `${{ }}` dentro de un `run` (llega por `env:`).
2. `publicar-imagen` corre solo en push a `QA`/`main` o en un tag `v*`, **después de que pasen** unitarias,
   integración, cobertura, auditoría NuGet, imagen y contrato. Carga la imagen probada (mismos bytes, no la reconstruye)
   y la sube a GHCR con `GITHUB_TOKEN` (permiso `packages: write` solo en ese job), con las reglas de inmutabilidad
   del §4.1 (en un tag, `imagetools create` sobre el digest de `sha-<corto>`). Un tag final que no apunte a un commit de
   `main` falla.
3. Los pull requests nunca publican.

**Bloqueo actual (2026-09-28): GitHub Actions no corre en la organización por un problema de facturación.** Mientras
tanto la imagen se publica a mano con el script del §4.3, con los mismos tags. Cuando se resuelva, no hay que cambiar
nada: el job empieza a publicar solo.

### 4.3 Publicación manual (reemplaza la construcción en el worker del Python)

`backend-dotnet/scripts/publicar-imagen.sh` (Git Bash, Linux, macOS) y `publicar-imagen.ps1` (PowerShell) hacen lo
mismo que el pipeline desde cualquier equipo con Docker, **no desde el worker**: build con versión y commit, prueba de
humo en un puerto libre de `127.0.0.1`, escaneo con Trivy y push con las mismas reglas de inmutabilidad del §4.1.
Antes de publicar se niegan si:

- hay cambios sin commit en `backend-dotnet/`;
- no se pasó `--canal` ni hay versión (tag `vX.Y.Z` en HEAD o `--version`), o HEAD no está en ninguna rama de
  `origin` (un build de un commit local no puede ocupar el `sha-<corto>` inmutable);
- `--canal qa|main` no se corre desde la rama `QA`/`main`, o HEAD no está subido a `origin/<rama>`;
- el tag `vX.Y.Z` de `origin` no existe o no apunta a HEAD, o la versión es final (`X.Y.Z`) y el commit no está en
  `origin/main`;
- el tag `X.Y.Z` ya existe en el registry, o el registry responde con un error que no sea "no existe".

```bash
# Una vez: PAT clásico de GitHub con write:packages (de quien publica), o la sesión de "docker login ghcr.io".
export GHCR_USUARIO=<usuario-github> GHCR_TOKEN=<pat>        # no guardarlo en archivos del repo

# Staging: desde QA actualizado
git switch QA && git pull
sh backend-dotnet/scripts/publicar-imagen.sh --canal qa      # publica sha-<corto> y qa

# Producción: desde main con el tag de release
git switch main && git pull && git tag -a v1.0.0 -m "Cotifly 1.0.0" && git push origin v1.0.0
sh backend-dotnet/scripts/publicar-imagen.sh --canal main    # sha-<corto> (si no existía), 1.0.0 y main sobre su digest

# Candidata en staging desde QA (sin pasar por main):
git switch QA && git pull && git tag -a v1.0.0-rc.1 -m "Cotifly 1.0.0-rc.1" && git push origin v1.0.0-rc.1
sh backend-dotnet/scripts/publicar-imagen.sh --canal qa      # sha-<corto>, 1.0.0-rc.1 y qa

# Solo validar el Dockerfile y la sonda, sin subir nada:
sh backend-dotnet/scripts/publicar-imagen.sh --sin-push
```

El `rebuild-on-worker.sh` del Python (construía en el worker) se borró en HU-022.1; está en el tag `python-final`.

### 4.4 Credenciales del registry en el clúster

El paquete es privado: los nodos necesitan una credencial de **solo lectura** para bajar la imagen.

- Crear un PAT clásico con **solo `read:packages`** (de una cuenta de servicio de la organización, no personal; con
  fecha de vencimiento y dueño que lo renueve). **Pendiente de confirmar con el equipo:** qué cuenta de GitHub lo emite.
- Registrarlo en Dokploy (*Settings → Registry*, `ghcr.io`, usuario y PAT) y, para el despliegue de tipo *stack*, hacer
  además `docker login ghcr.io` en el **manager** como el usuario con el que corre Dokploy (root): `docker stack deploy
  --with-registry-auth` reenvía esa credencial a las tareas del worker. Si el worker no logra bajar la imagen
  (`No such image` o `unauthorized` en `docker service ps --no-trunc`), hacer también `docker login ghcr.io` en el
  worker. Esto se verifica en el primer despliegue a staging (HU-006.6).
- El PAT nunca va en el manifiesto ni en el repositorio.
- **Primer push y permisos del paquete:** si el paquete `cotifly-backend` lo crea el script con un PAT personal
  (mientras Actions siga bloqueado), queda sin acceso para el repositorio y el primer push del CI con `GITHUB_TOKEN`
  recibe `403`. Una vez creado: *GitHub → paquete `cotifly-backend` → Package settings → Manage Actions access* →
  agregar el repositorio `Cotifly` con rol **Write** (y, si se quiere, enlazarlo al repositorio en *Connect
  repository*).

### 4.5 Release paso a paso

0. **Precondición de esquema: `db/migrations_mssql/0001`–`0010`, `db/scripts/02`, `db/scripts/03`, `db/scripts/pendientes/04`, `db/scripts/05`, `db/scripts/06`, `db/scripts/09`, `db/scripts/10`, `db/scripts/11`, `db/scripts/12`, `db/scripts/13` y `db/scripts/14` aplicados (el `07` es opcional, el `08` va después de la imagen y el `15`, del rol director, va con o después de la imagen de HU-037, §2.2 "Rol director")** en la base del ambiente (o la base instalada
   desde cero con `db/scripts/01_crear_esquema.sql` y `db/seeder/`, que equivale a `0001`–`0010`). Los aplica el DBA con
   un usuario administrador y `sqlcmd -I`; la app no aplica migraciones. Regla de siempre: **primero el script, después
   la imagen.** Sin el esquema completo, `/health/ready` responde `503` (tabla de abajo), Traefik no manda tráfico a la
   tarea y, como la sonda de vida sigue en 200, Swarm no revierte solo (paso 5).
   - El `0009` (rutas y camiones, HU-021, ADR-019) ya está en la base compartida de QA, que se reinstaló desde cero el
     2026-10-08.
   - El `0010` (`0010_nombres_municipio.sql`, idempotente y no destructivo) es un **borrador pendiente de revisión de
     scalderon-bs y del DBA**. Se aplica después del `0009` y antes de la imagen que lo usa (cualquiera desde #130/#131).
   - HU-023 (cubicador): `db/scripts/02_alter_cubicador.sql` (columnas de resultado de la cubicación y
     `codigoMercancia`, idempotente; lo corre el ejecutador después del `01`) y
     `db/scripts/pendientes/04_cubicacion_segundo_plano.sql` (cola de la cubicación de la licitación en segundo plano,
     HU-023.5; **pendiente de revisión de scalderon-bs y del DBA**). El backend mapea las columnas de los dos: se aplican
     antes de la imagen.
   - **HU-014 (#188): `db/scripts/03_orden_vehiculo_y_conexo_catalogo.sql` y `db/scripts/05_categoria_conexo.sql` van ANTES de la
     imagen de #188.** Hasta #188 estaban en `db/scripts/pendientes/` sin mapear; ahora el backend mapea `ordenVehiculo` e
     `idServicioConexoCatalogo` (03) y `categoriaServicioConexo` (05), y el ejecutador los corre por nombre (`02`, `03`, `05`, `07`…).
     Sin ellos `/health/ready` responde 503 "falta 03" o "falta 05". Los dos son idempotentes; el 03 numera los camiones existentes
     por `createdAt` y deja el mismo `CHECK` del motivo que el 07. **El DBA confirma antes del despliegue** que están aplicados en la
     base compartida y en cada ambiente con esta consulta de solo lectura (las tres columnas deben dar un largo, no NULL):

     ```sql
     SELECT COL_LENGTH('dbo_cotifly.CotizacionVehiculosLineaSolicitud', 'ordenVehiculo')       AS ordenVehiculo_03,
            COL_LENGTH('dbo_cotifly.CotizacionServiciosConexos', 'idServicioConexoCatalogo')   AS idServicioConexoCatalogo_03,
            COL_LENGTH('dbo_cotifly.CotizacionServiciosConexos', 'categoriaServicioConexo')    AS categoriaServicioConexo_05,
            (SELECT COUNT(*) FROM sys.indexes
              WHERE name = 'uq_cotizacionVehiculosLineaSolicitud_lineaSolicitudId_ordenVehiculo') AS indiceUnico_03;
     ```

     Si falta alguno, se aplica con `sqlcmd -I` (o el ejecutador) antes de la imagen. Aplicarlos con la imagen anterior en marcha no
     rompe nada: la versión anterior no lee esas columnas y escribe `ordenVehiculo` en NULL (el índice es filtrado).
   - HU-018.1 (#155): `db/scripts/09_score_prioridad.sql` (`scorePrioridad` de Nexo en la cabecera, idempotente; lo corre el
     ejecutador después del `08`). **El `09` va ANTES de la imagen** que trae la #155: el backend mapea la columna. Si la imagen
     sale sin él, la sonda de vida sigue en 200, Swarm retira la versión anterior y **toda la API queda en 503** en
     `/health/ready`: aplicar el `09` (un `ADD` de columna NULL, segundos) la recupera **sin redesplegar**, porque
     `/health/ready` revisa el esquema en cada consulta.
   - **#191 (HU-026.1): `db/scripts/06_idempotencia_captura.sql` va ANTES de la imagen de #191** (lo aprueba y aplica el DBA). Crea
     `dbo_cotifly.CapturasIdempotentes`, donde la API guarda cada `Idempotency-Key` (captura, conexos, re-solicitud y edición) con
     su huella, su estado (`en_curso`, `terminada`, `libre`), su respuesta y, en la captura, el folio y el `solicitudNexoId`: un
     reintento que cae en otra réplica o después de un reinicio ya no crea otra solicitud en Nexo (si la primera sigue en curso en
     otra réplica, responde 409 `CAPTURA_EN_CURSO`). Hasta #191 estaba en
     `db/scripts/pendientes/` sin usar; conserva su número y el ejecutador lo corre por nombre (`05`, `06`, `07`…). Es idempotente;
     si encuentra la tabla de la propuesta anterior **vacía** la recrea, y con filas se detiene sin tocarla. Sin él, la imagen de
     #191 queda en 503 "falta 06" (como con el `09`, aplicarlo la recupera sin redesplegar). Aplicarlo con la imagen anterior en
     marcha no cambia nada (no lee la tabla). Verificación de solo lectura (dos valores no NULL y `1`):

     ```sql
     SELECT OBJECT_ID(N'dbo_cotifly.CapturasIdempotentes', N'U')                     AS tabla_06,
            COL_LENGTH(N'dbo_cotifly.CapturasIdempotentes', N'resultado')            AS resultado_06,
            (SELECT COUNT(*) FROM sys.key_constraints
              WHERE name = N'uq_capturasIdempotentes_usuarioId_operacion_clave')     AS unico_06;
     ```

     Con la imagen de #191 la API puede correr con más de una réplica sin perder la idempotencia (antes exigía `replicas: 1`).
     **Pero `replicas: 1` sigue siendo obligatorio** por otras razones: el candado de envíos de correo (`clsCandadoEnvios`) es por
     proceso (ADR-023, Consecuencias), las cubicaciones rápidas de `POST /web/cubicacion` viven en memoria, el estado pendiente de la
     línea de Nexo por camión también (#181), el barrido de sesiones corre en el proceso y el volumen de PDFs es local (ADR-012).
   - **HU-028.1 (#183): `db/scripts/10_rechazo_linea.sql` va ANTES de la imagen de #183** (lo revisa @scalderon-bs y lo aplica el DBA;
     lo corre el ejecutador después del `09`). Agrega al camión (`CotizacionVehiculosLineaSolicitud`) `motivoRechazo NVARCHAR(500)` y
     `rechazadaAt DATETIMEOFFSET` (NULL), `'rechazada'` en los tres CHECK de `estadoCosteo` y el CHECK
     `ck_cotizacionVehiculosLineaSolicitud_rechazo`. Idempotente, en una transacción, con verificación al final. Sin él la imagen de
     #183 queda en 503 "falta 10" (como con el `09`, aplicarlo la recupera sin redesplegar). Aplicarlo con la imagen anterior en marcha
     no cambia nada. Verificación de solo lectura (dos largos no NULL y `3`):

     ```sql
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'motivoRechazo') AS motivoRechazo_10,
            COL_LENGTH(N'dbo_cotifly.CotizacionVehiculosLineaSolicitud', N'rechazadaAt')   AS rechazadaAt_10,
            (SELECT COUNT(*) FROM sys.check_constraints
              WHERE name IN (N'ck_cotizaciones_estadoCosteo', N'ck_cotizacionLineasSolicitud_estadoCosteo',
                             N'ck_cotizacionVehiculosLineaSolicitud_estadoCosteo')
                AND definition LIKE N'%''rechazada''%')                                    AS checks_con_rechazada_10;
     ```

   - **Excepción, HU-014.3 (#152): el `08` va DESPUÉS de la imagen.** `db/scripts/08_retirar_vistas_actividad.sql` borra las
     vistas que lee la versión anterior. El ejecutador (`db/ejecutar-bd.*`) corre todos los `scripts/` en orden de nombre
     (`07`, `08`, `09`), así que si el DBA lo usa antes de la imagen para aplicar el `09` también aplica el `08`: no rompe nada
     que se use, solo los dos endpoints retirados de la versión anterior (`/web/metricas/actividad` y `/ajustes-margen`, que el
     frontend no consume) responden 500 hasta que sube la imagen nueva.
   - Orden del release con #152 y #155 juntas: `09` → imagen → `08`. ~~Con #183 y #184 (integración 1021): `03` → `pendientes/04` → `05`
     → `06` → `07` → `09` → `10` → `11` → imagen → `08`.~~ Con HU-032 (#197) y HU-033 (#199): `03` → `pendientes/04` → `05` → `06`
     → `07` → `09` → `10` → `11` → `12` → `13` → imagen → `08`. ~~Con HU-036 (#205 a #208): `…` → `13` → `14` → imagen → `08`.~~ Con HU-036 (#205 a #208) y HU-037 (#209 a #211) juntas
     (integración 1027): `…` → `13` → `14` → `15` → imagen → `08`, con el `15` en la misma ventana que la imagen (nunca antes con una
     imagen vieja, ver §2.2 "Rol director"). En la compartida el `15` ya está aplicado (#209) y el `14` está pendiente de @scalderon-bs.
     Con HU-040 (#229, #230): `…` → `13` → `14` → `15` → `16` → imagen → `08`. El `16` va ANTES de la imagen (la imagen de HU-040.1 lo
     exige: "falta 16") y, a diferencia del `15`, es seguro con la imagen anterior (ver abajo, "Multi-rol").
     Con HU-042 (#242, #243, #247): `…` → `16` → `17` → `18` → imagen → `08`. El `17` y el `18` (de Datos, ya aplicados en la compartida
     el 2026-10-28) van ANTES de la imagen de HU-042.2: la imagen mapea sus tablas y `SapMapeoTrqType.lineaNegocioId` ("falta 17",
     "falta 18"). Los dos son seguros con la imagen anterior (tablas nuevas; el 18 deja la columna de texto). Ver abajo, "Margen de SAP".
     Con #261 (punto 4, `precioVenta` del conexo): `…` → `16` → `17` → `18` → `19` → imagen → `08`, con el `19` **en la misma ventana
     que la imagen** (ver abajo).
   - **#261 (punto 4): `db/scripts/19_precio_venta_conexos.sql` va en la misma ventana corta que la imagen de #261.** Deja
     `CotizacionServiciosConexos.precioVenta DECIMAL(18,2) NOT NULL` sin `DEFAULT` (backfill `precioVenta = precio`). No hay un orden
     sin ventana de falla:
     - la imagen de #261 sin el `19` queda en 503 "falta 19" (aplicarlo la recupera sin redesplegar);
     - el `19` con la imagen anterior rompe agregar conexos (error 515: la imagen vieja inserta sin `precioVenta`), aunque la cabecera
       del script diga lo contrario.

     Por ambiente: aplicar el `19` (segundos, idempotente), desplegar de inmediato la imagen y verificar `/health/ready` 200 y agregar
     un conexo de prueba. Un ambiente que ya tiene el `19` con una imagen anterior (la base compartida de pruebas, desde el
     2026-10-28) ya está roto para agregar conexos: desplegar ahí la imagen de #261 cuanto antes. Verificación de solo lectura (un
     largo no NULL y `0`):

     ```sql
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionServiciosConexos', N'precioVenta') AS precioVenta_19,
            COLUMNPROPERTY(OBJECT_ID(N'dbo_cotifly.CotizacionServiciosConexos'), N'precioVenta', 'AllowsNull') AS admiteNulos_19;
     ```
   - **HU-030.1 (#184): `db/scripts/11_peso_con_tara.sql` va ANTES de la imagen de #184.** Agrega a la ruta
     (`CotizacionLineasSolicitud`) la columna `pesoConTaraKg DECIMAL(12,2) NULL` y el `CHECK` `ck_cotizacionLineasSolicitud_pesoConTaraKg`
     (`NULL` o > 0): el peso total con tara que escribe el comercial en una ruta `Contenedor`. Es un `ADD` de columna NULL, idempotente
     y en una transacción; el ejecutador lo corre por nombre después del `09` (el número `10` es de #183). Sin él, la imagen de #184 queda
     en 503 "falta 11" (como con el `09`, aplicarlo la recupera sin redesplegar). Aplicarlo con la imagen anterior en marcha no cambia
     nada (no lee la columna). Verificación de solo lectura (un largo no NULL y `1`):

     ```sql
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionLineasSolicitud', N'pesoConTaraKg') AS pesoConTaraKg_11,
            (SELECT COUNT(*) FROM sys.check_constraints
              WHERE name = N'ck_cotizacionLineasSolicitud_pesoConTaraKg')          AS check_11;
     ```

   - **HU-036 (#205, #206): `db/scripts/14_cotizacion_finalizada.sql` va ANTES de la imagen de HU-036.** Es una **propuesta pendiente de
     revisión de @scalderon-bs** (Datos, #205); el número `15` queda reservado para HU-037. Agrega a la cabecera (`CotizacionesSolicitud`)
     `finalizadaAt DATETIMEOFFSET NULL` y `finalizadaPor UNIQUEIDENTIFIER NULL` con la FK `fk_cotizaciones_finalizadaPor` a
     `UsuariosWeb(id)` (la PK de `UsuariosWeb` es `UNIQUEIDENTIFIER`, verificado en `01_crear_esquema.sql`; si en un ambiente no lo fuera,
     el script se detiene sin tocar nada) y el CHECK `ck_cotizaciones_finalizada` (las dos NULL o las dos con valor). Idempotente, no
     destructivo, en una transacción; el ejecutador lo corre por nombre después del `13`. Sin él la imagen de HU-036 queda en 503
     "falta 14" (como con el `09`, aplicarlo la recupera sin redesplegar). Aplicarlo con la imagen anterior en marcha no cambia nada (no
     lee las columnas). Verificación de solo lectura (dos largos no NULL, `1` y `1`):

     ```sql
     SELECT COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'finalizadaAt')  AS finalizadaAt_14,
            COL_LENGTH(N'dbo_cotifly.CotizacionesSolicitud', N'finalizadaPor') AS finalizadaPor_14,
            (SELECT COUNT(*) FROM sys.foreign_keys WHERE name = N'fk_cotizaciones_finalizadaPor') AS fk_14,
            (SELECT COUNT(*) FROM sys.check_constraints WHERE name = N'ck_cotizaciones_finalizada') AS check_14;
     ```

     **Sin backfill** (decisión del 2026-10-26): las cotizaciones existentes quedan sin finalizar. Las ya enviadas al cliente o con
     resultado siguen protegidas como hoy. Las que están en `autocosteo`/`entregada` sin enviar ni resultado las finaliza el comercial una
     por una antes de generar el PDF, enviarlas o marcar el resultado (HU-036.3, #207). Para avisarles cuántas son, antes de desplegar
     (solo lectura):

     ```sql
     SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud
     WHERE estadoCosteo IN (N'autocosteo', N'entregada') AND estadoEnvio = N'sin_enviar' AND ganada IS NULL;
     ```

   **Errores de `/health/ready` (`503 {"status":"unavailable","detalle":"..."}`)** y qué hacer. La sonda compara las tablas y
   columnas que mapea el modelo EF con la base (más el `CHECK` de `estadoEnvio`); los mensajes viejos `falta 0004` a
   `falta 0008` ya no existen.

   | `detalle` | Causa | Qué hacer |
   |---|---|---|
   | `La cadena de conexión 'ConnectionStrings:Cotifly' no está configurada.` | Falta `ConnectionStrings__Cotifly` | Cargar el secreto en Dokploy (§2.2) |
   | `La base de datos de Cotifly no está disponible.` | Base caída, red, credenciales o la sonda pasó de 5 s | Revisar red a `tbsdatacenter`, usuario y permisos |
   | `esquema incompleto: falta 0002` | Faltan `TokensRefresco` o `Sesiones.estadoAgente` | Aplicar `0002` a `0010` en orden |
   | `esquema incompleto: falta 0009` | La base tiene el esquema anterior a HU-021 (`Cotizaciones`, sin `CotizacionesSolicitud`) | Aplicar `0009_reestructuracion_cotizaciones.sql` (con su respaldo `Respaldo0009_*`) y después el `0010` |
   | `esquema incompleto: falta 0010` | Está el `0009` pero faltan las columnas del `0010` (nombres de municipio, `solicitudNexoId`, `nombreServicioConexo`) o el `CHECK` `ck_cotizaciones_estadoEnvio` | Aplicar `0010_nombres_municipio.sql` |
   | `esquema incompleto: falta 02` | Faltan las columnas del cubicador (HU-023.3) | Aplicar `db/scripts/02_alter_cubicador.sql` |
   | `esquema incompleto: falta 03` | Faltan `ordenVehiculo` o `idServicioConexoCatalogo` (HU-014, #188) | Aplicar `db/scripts/03_orden_vehiculo_y_conexo_catalogo.sql` |
   | `esquema incompleto: falta 04` | Faltan las columnas de la cubicación en segundo plano (HU-023.5) | Aplicar `db/scripts/pendientes/04_cubicacion_segundo_plano.sql`, con la aprobación del DBA |
   | `esquema incompleto: falta 05` | Falta `CotizacionServiciosConexos.categoriaServicioConexo` (HU-014, #188) | Aplicar `db/scripts/05_categoria_conexo.sql` |
   | `esquema incompleto: falta 06` | Falta la tabla `CapturasIdempotentes` (o tiene la forma de la propuesta de #173) (#191) | Aplicar `db/scripts/06_idempotencia_captura.sql`, con la aprobación del DBA |
   | `esquema incompleto: falta 09` | Falta `CotizacionesSolicitud.scorePrioridad` (HU-018.1, #155) | Aplicar `db/scripts/09_score_prioridad.sql` |
   | `esquema incompleto: falta 10` | Faltan `motivoRechazo` o `rechazadaAt` del camión (HU-028.1, #183) | Aplicar `db/scripts/10_rechazo_linea.sql` |
   | `esquema incompleto: falta 11` | Falta `CotizacionLineasSolicitud.pesoConTaraKg` (HU-030.1, #184) | Aplicar `db/scripts/11_peso_con_tara.sql` |
   | `esquema incompleto: falta 12` | Faltan `tipoVehiculoElegido`/`carroceriaElegida` de la ruta (HU-032, #197) | Aplicar `db/scripts/12_vehiculo_elegido.sql` |
   | `esquema incompleto: falta 13` | Faltan las columnas de MailSender en `Correos` o la sincronización del resultado en la cabecera (HU-033, #199) | Aplicar `db/scripts/13_correo_mailsender.sql` |
   | `esquema incompleto: falta 14` | Faltan `finalizadaAt`/`finalizadaPor` de la cabecera (HU-036, #205) | Aplicar `db/scripts/14_cotizacion_finalizada.sql` |
   | `esquema incompleto: falta 16` | Falta `TokensRefresco.rolSesion` (HU-040.1, #229) | Aplicar `db/scripts/16_multiples_roles_activos.sql` |
   | `esquema incompleto: falta 17` | Faltan `SapMargenCliente`/`SapMargenSyncLog`, la copia local del margen de SAP (HU-042.1 #242, la lee HU-042.2 #243) | Aplicar `db/scripts/17_sap_margen_sincronizacion.sql` |
   | `esquema incompleto: falta 18` | Falta `SapMapeoTrqType.lineaNegocioId` (HU-042.4, #247) | Aplicar `db/scripts/18_sap_mapeo_fk_margenes_politica.sql` |
   | `esquema incompleto: falta 19` | Falta `CotizacionServiciosConexos.precioVenta` (HU-043.1, #245; mapeo en #261) | Aplicar `db/scripts/19_precio_venta_conexos.sql` |
   | `esquema incompleto: falta Tabla.columna, ...` (hasta 5) | Falta otra tabla o columna del modelo (por ejemplo un script intermedio sin aplicar) | Comparar con `db/scripts/01_crear_esquema.sql` y aplicar el script que la trae (lista en `docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md` §A.5) |

1. **Staging.** Merge a `QA` → el pipeline (o `publicar-imagen.sh --canal qa`) publica `sha-<corto>`. En Dokploy, stack
   de staging: variable `COTIFLY_TAG=sha-<corto>` → *Deploy*. Verificar `/health`, `/health/ready` y las pruebas de humo
   (HU-006.8).
2. **Release.** Merge `QA` → `main` (el push a `main` publica `sha-<corto>` del commit de `main`); desplegar ese
   `sha-<corto>` en staging si el merge creó un commit nuevo. **Esperar a que termine la corrida del push a `main`**
   (que `sha-<corto>` esté publicado) y recién entonces crear y subir el tag `vX.Y.Z` sobre ese mismo commit →
   `X.Y.Z` se crea sobre el digest de `sha-<corto>`: producción corre los bytes probados en staging. Si el tag llega
   antes, su corrida falla con "espera a que termine la corrida de main"; se vuelve a ejecutar cuando termine.
3. **Producción.** En Dokploy, stack de producción: `COTIFLY_TAG=X.Y.Z` → *Deploy*. Swarm actualiza con `start-first`
   y vuelve atrás solo si falla la sonda de vida `/health` (manifiesto, HU-006.6); la disponibilidad se verifica a
   mano en el paso 5.
4. **Verificar** qué imagen corre: `docker service inspect --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}'
   <stack>_cotifly-api` (muestra tag y digest).
5. **Verificar la disponibilidad después de cada despliegue.** El rollback automático de Swarm solo mira la sonda de
   vida (`/health`, el `HEALTHCHECK`): una versión que arranca pero no llega a la base (cadena mal escrita, permisos,
   red a `tbsdatacenter`) queda *healthy* y **no** se revierte sola. Por eso, tras cada *Deploy*:
   `curl -s -o /dev/null -w '%{http_code}' https://<PUBLIC_HOST>/health/ready` (en staging, mientras el overlay esté
   caído, `http://10.10.40.31:8092/health/ready`) debe dar `200` en menos de 2 minutos. Si no:
   `docker service rollback <stack>_cotifly-api` en el manager (o volver `COTIFLY_TAG` al valor anterior y *Deploy*).
6. **Rollback manual:** volver `COTIFLY_TAG` al valor anterior y *Deploy*, o `docker service rollback <stack>_cotifly-api`.
7. **Sin rollback al stack Python desde este repositorio.** Por decisión del usuario (2026-10-13) el Python se retiró
   del repositorio en HU-022.1 (#146); el rollback del backend .NET es volver a una imagen .NET anterior (pasos 5 y 6).
   El último estado del Python (código, `docker-compose.swarm.yml` y `rebuild-on-worker.sh` de la antigua carpeta
   `deploy/`) vive en el tag `python-final`. Volver a él sería una contingencia excepcional, con aprobación del negocio
   (reabre Google Chat, ADR-018) y con este aviso de datos: el Python usa su propia base Postgres y no ve lo que el
   backend .NET escribió en `dbo_cotifly`; no hay migración inversa.

## 5. Manifiestos del stack .NET (HU-006.6, #30)

### 5.1 Archivos

| Manifiesto | Ambiente | Reemplaza |
|---|---|---|
| [`infra/stack.dotnet.staging.yml`](infra/stack.dotnet.staging.yml) | staging | `infra/stack.staging.yml` (Python, nunca desplegado) |
| [`infra/stack.dotnet.dokploy.yml`](infra/stack.dotnet.dokploy.yml) | producción | El stack Python que corre hoy en Dokploy (proyecto `bot-cotizador`, puerto 8091), cuyo manifiesto era `deploy/docker-compose.swarm.yml`. `infra/stack.dokploy.yml` era su versión "ideal", nunca desplegada |

Los manifiestos del Python se borraron en HU-022.1 (#146) y quedan en el tag `python-final`. Los dos manifiestos .NET son iguales salvo lo que marca la tabla del §5.3.

### 5.2 Topología

```
Internet / red corporativa
        │ HTTPS (TLS en Traefik: HU-006.2)
        ▼
  Traefik (Dokploy, red dokploy-network)
        │ HTTP :8080, healthcheck de Traefik a /health/ready
        ▼
  cotifly-api (1 réplica, anclada al worker TBSNODO1-DKP)
        ├── volumen cotifly_pdfs → /data/pdfs
        └── red "salida" (overlay con egreso) → tbsdatacenter:1433 (SQL Server 2019, externo)
                                              → proveedor LLM, Nexo, cubicador, MailSender (él entrega por el relay SMTP),
                                                TRM en co.dolarapi.com:443 y www.datos.gov.co:443
                                              → SAP TM en sap-tm-prd.boterosoto.local:8000 (HTTP, worker del margen, HU-042.2)
                                              → DW 10.10.40.24:1433 (DWCubosEBSSAP para la regional y DWCubosOperacionesEBS
                                                para el NIT por BP del margen de SAP)
```

- **SQL Server es externo.** HU-002.4 lo decidió: la instancia corporativa `tbsdatacenter` (base
  `dbTarifario_Nexo_Pruebas`, esquema `dbo_cotifly`; `dbsolucionesti` de solo lectura). No hay servicio de base de
  datos en el stack; la API llega por `ConnectionStrings__Cotifly`/`__Identidad` desde el worker. Requisito de red: el
  worker debe alcanzar `tbsdatacenter:1433` (pendiente de verificar en staging).
- **Pendiente (HU-007.1, #33, con el DBA):** ¿staging y producción comparten `dbTarifario_Nexo_Pruebas`/`dbo_cotifly`?
  Hoy solo existe esa base; si ambos stacks apuntan a ella, las pruebas de staging escriben en los datos de
  producción. Definir una base o esquema de staging aparte, o aceptar explícitamente el riesgo, antes del primer
  despliegue de producción.
- **Un solo servicio** (ADR-002): no hay `bot-mcp` (las herramientas viven en el monolito) ni MinIO (ADR-012).
- **Redes, equivalentes a las del Python:**

  | Python | .NET | Para qué |
  |---|---|---|
  | `web` / `dokploy-network` (Traefik) | `edge` = `dokploy-network` (externa) | Solo entrada desde Traefik. Es el único servicio del stack en esa red |
  | `bot-edge` (overlay con egreso) | `salida` (overlay propia, no interna) | Salida a internet y a la red corporativa: LLM, Nexo, TRM (`co.dolarapi.com` y `www.datos.gov.co`), relay, SQL Server, SAP TM (`sap-tm-prd.boterosoto.local:8000`, worker del margen) y el DW (`10.10.40.24:1433`) |
  | `bot-back` (overlay `internal: true`) | — | No hace falta: no hay servicios internos. Si vuelve alguno (MinIO, ADR-012), va en una overlay interna sin labels de Traefik |

- **Healthchecks:** el del stack es el mismo de la imagen, en forma exec (`dotnet /app/Cotifly.Api.dll --healthcheck`,
  vida, `/health`), con `start_period` de 30 s; Swarm reinicia la tarea si falla. La disponibilidad (`/health/ready`,
  base de datos) la mira Traefik con su healthcheck: saca la tarea del balanceo sin reiniciarla.
- **Recursos:** límite 1 CPU / 1024 MB, reserva 0,25 CPU / 384 MB (cada sonda levanta otro proceso `dotnet` de
  40–70 MB).
- **Actualización:** `update_config` con `failure_action: rollback`, `monitor: 60s` y `max_failure_ratio: 0`: si la
  tarea nueva no llega a *healthy*, Swarm vuelve a la imagen anterior sola. `start-first` en producción (sin corte);
  `stop-first` en staging mientras use el puerto en modo host (§5.3).
  - **Límite:** *healthy* es la sonda de vida `/health`, que no toca la base. Una versión que arranca pero no llega a
    SQL Server no se revierte sola: después de cada despliegue hay que comprobar `/health/ready` = 200 y, si no,
    `docker service rollback` (§4.5, paso 5).
- **Riesgo bajo, encabezados reenviados:** `dokploy-network` la comparten todos los servicios que Dokploy publica, y
  `ForwardedHeaders__RedesConocidas__0` confía en toda su subred. Un contenedor de otra aplicación en esa red podría
  llamar a la API con un `X-Forwarded-For` falso y saltarse el límite por IP de `/api/auth/*` (no la autenticación:
  el login sigue exigiendo la verificación contra ClientePro). Propuesta: en cuanto Traefik tenga una IP estable o un
  rango propio dentro de la red (verificarlo con `docker network inspect dokploy-network` en staging), usar
  `ForwardedHeaders__ProxiesConocidos__0=<IP de Traefik>` (agregando esa línea al manifiesto, §5.4) o un CIDR más
  estrecho en `RedesConocidas`.
- **Una réplica:** el barrido de sesiones vive en el proceso y el volumen de PDFs es local (ADR-012); la Idempotency-Key ya
  no (#191: tabla `CapturasIdempotentes` del 06). Escalar exige un volumen compartido y el candado de correo en la base. También viven solo en memoria las cubicaciones
  asíncronas (opción D) y, desde #181, el estado y el motivo pendiente de la línea de NEXO de cada camión
  (`estado_linea_nexo`/`motivo_pendiente` de `GET .../costeo`): los escribe el polling y los lee la API del mismo proceso,
  se pierden con un reinicio o un despliegue (vuelven con la siguiente consulta del polling) y con más réplicas una
  réplica no vería lo que leyó el polling de otra. Escalar exige guardarlos en la base.
- **TLS:** los labels de Traefik (router `websecure`, `certresolver=letsencrypt`, HSTS, puerto 8080 y healthcheck de
  `/health/ready`) están escritos; que el certificado funcione depende de HU-006.2.
  - **Versión de Traefik (informativo):** los manifiestos usan `traefik.docker.network`, la etiqueta de Traefik v2
    (la que usa el Python). En Traefik v3 el proveedor de Swarm lee `traefik.swarm.network`. **Pendiente:** confirmar
    la versión del Traefik de Dokploy (`docker service inspect dokploy-traefik --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}'`)
    y, si es v3, cambiar esa etiqueta en los dos manifiestos.

### 5.3 Anclado al worker mientras el overlay esté caído (HU-006.1)

El overlay VXLAN entre el manager y el worker está caído (HU-006.1, diagnóstico; HU-006.2, solución). Consecuencias
para los manifiestos, iguales a las que ya asumía el Python:

- `placement.constraints: node.hostname == TBSNODO1-DKP`: la tarea corre en el worker, donde hay disco (el manager está
  al límite) y donde vive el volumen local de PDFs.
- Traefik corre en el manager y hoy no alcanza por la overlay a una tarea del worker. Por eso:

  | | Staging (`stack.dotnet.staging.yml`) | Producción (`stack.dotnet.dokploy.yml`) |
  |---|---|---|
  | Acceso mientras el overlay esté caído | Puerto en modo host `10.10.40.31:${COTIFLY_PUERTO_HOST:-8092}` → 8080, HTTP **sin TLS**, solo red corporativa (el Python usa 8091) | Ninguno: sin `ports`. **No se despliega hasta HU-006.2** |
  | Orden de actualización | `stop-first` (dos tareas no pueden usar el mismo puerto del nodo); `COTIFLY_ORDEN_UPDATE=start-first` al quitar el puerto | `start-first` |
  | Volumen / router | `cotifly_staging_pdfs` / `cotifly-api-staging` | `cotifly_pdfs` / `cotifly-api` |
  | `ASPNETCORE_ENVIRONMENT` | `Production` fijo (mismas validaciones de arranque) | `Production` fijo |

- Al resolverse HU-006.2: quitar `ports` de staging, pasar a `start-first` y validar el acceso por Traefik con TLS. La
  restricción de nodo puede quitarse solo con un volumen de PDFs compartido entre nodos.

### 5.4 Variables del stack en Dokploy

Las variables del §2.2 se cargan en la pestaña *Environment* del compose (tipo *stack*). Además, las propias del
manifiesto:

| Variable | Obligatoria | Uso |
|---|---|---|
| `COTIFLY_TAG` | **sí** | Tag inmutable de la imagen (`sha-<corto>` o `X.Y.Z`, §4) |
| `PUBLIC_HOST` | **sí** | Dominio del router de Traefik |
| `COTIFLY_PUERTO_HOST` | no (staging) | Puerto del worker en modo host (por defecto 8092) |
| `COTIFLY_ORDEN_UPDATE` | no (staging) | `stop-first` (por defecto) o `start-first` |

Obligatorias por el manifiesto (`${VAR:?}`: sin ellas `docker stack deploy` falla antes de tocar el servicio):
`COTIFLY_TAG`, `PUBLIC_HOST`, `ConnectionStrings__Cotifly`, `ConnectionStrings__Identidad`, `Jwt__SigningKey`,
`Cors__OrigenesPermitidos__0` y `ForwardedHeaders__RedesConocidas__0` (la subred de `dokploy-network`:
`docker network inspect dokploy-network --format '{{(index .IPAM.Config 0).Subnet}}'`).

Fijas en el manifiesto (no se leen de Dokploy): `ForwardedHeaders__Habilitado=true`,
`Pdf__StorageModo=volumen`, `Pdf__StorageDir=/data/pdfs`, `Zona__ZonaHoraria=America/Bogota`.

Opcionales con valor vacío = "no configurada" (verificado en el código de cada opción): `ConnectionStrings__Regional`
(vacía, se usa lo que ya haya en `UsuariosWeb.regional`; con valor, el login la llena y refresca (#81) y el tablero
del gerente la lee en vivo (#45), y necesita salida desde el worker a `10.10.40.24:1433` y un usuario SQL de solo
lectura con `SELECT` únicamente sobre `dbo.DimEjecutivo`, pendiente con el DBA), `Nexo__UrlBase`, `IA__Proveedor`,
`Pdf__BaseUrl`. En cambio
`Regional__Esquema`, `__Tabla`, `__ColumnaUsuario`, `__ColumnaRegional` y `__Dominio` son identificadores obligatorios:
el manifiesto trae los valores por defecto de `clsOpcionesRegional` (`dbo`, `DimEjecutivo`, `USUARIO`, `REGIONAL`,
`boterosoto`) y no deben cargarse vacías en Dokploy. **Ojo:** como el manifiesto siempre manda la variable, su valor por
defecto manda sobre el del código; si el código cambia un valor por defecto (como `ColumnaUsuario`: `ID` → `USUARIO`
en #81), hay que cambiarlo también en los manifiestos (desde el endurecimiento A, #199, `clsManifiestosDespliegueTests` falla si no). `Regional__TiempoEsperaLoginSegundos` (3, de 1 a 10) limita
cuánto espera el login por `DimEjecutivo` antes de seguir sin bloquear.

**Una variable que no está en el manifiesto no llega al contenedor**, aunque se cargue en Dokploy (Dokploy solo la usa
para sustituir `${...}`). Los respaldos `IA__Fallbacks__0__*` e
`IA__Fallbacks__1__*` ya están en los manifiestos con `${...:-}`: vacíos = sin respaldo (#84, `clsOpcionesIA.FallbacksConfigurados`
ignora un respaldo sin `Proveedor`). Un tercer respaldo exige agregar `IA__Fallbacks__2__*` al manifiesto en un commit.
Con OmniRoute, cargar en Dokploy `IA__SoportaJsonSchema=false` e `IA__MaxTokens=1500`: el manifiesto trae `true` y `4096`
por defecto.

### 5.5 Desplegar en Dokploy (pasos; staging pendiente de aprobación)

1. En el manager: `docker login ghcr.io` con el PAT de solo lectura (§4.4). Ya no hace falta el secreto de Google Chat
   (retirado en #105).
2. En Dokploy: proyecto Cotifly → *Compose* de tipo **Stack**, fuente: este repositorio, rama `QA`, archivo
   `infra/stack.dotnet.staging.yml`.
3. *Environment*: variables del §2.2 y del §5.4, con `COTIFLY_TAG=sha-<corto>` publicado (§4).
4. *Deploy*. Verificar: `docker service ps <stack>_cotifly-api --no-trunc` (tarea `Running` en `TBSNODO1-DKP`),
   `curl http://10.10.40.31:8092/health` → `200` con `"entorno":"Production"` y `/health/ready` → `200`.
5. Producción: igual con `infra/stack.dotnet.dokploy.yml`, rama `main`, `COTIFLY_TAG=X.Y.Z`, después de HU-006.2 y del
   checklist de HU-006.8.

Aviso esperado en el log al arrancar: `Storing keys in a directory '/home/app/.aspnet/DataProtection-Keys' that may not
be persisted` (ASP.NET Core Data Protection; Cotifly no guarda datos protegidos con esas claves, es inocuo).

### 5.6 Validación hecha (2026-09-28, sin desplegar)

- `docker stack config -c` y `docker compose -f ... config` de ambos manifiestos: válidos; sin las variables
  obligatorias fallan con el mensaje `falta <VARIABLE>`.
- Arranque real (2026-09-28): el `environment` que producía cada manifiesto (`docker compose config`, 61 variables en
  ese momento, defaults incluidos) se pasó tal cual a la imagen contra un SQL Server 2019 local con `0001`–`0003` y el login `cotifly_app` de
  permisos mínimos: `ASPNETCORE_ENVIRONMENT=Production`, `/health` 200 y `/health/ready` 200. Confirma que los
  valores por defecto vacíos (IA, SAP, Nexo, correo; en esa corrida también Google Chat, hoy retirado) no rompen la validación de arranque.
  - Esa corrida fue con la imagen de `QA` de ese día, que exigía `0001`–`0003`. **Desde el costeo en Nexo (HU-005.8)
    la precondición fue `0001`–`0004`, y desde HU-021 es `0001`–`0010`** (§4.5, paso 0): una imagen nueva contra una base
    sin esos scripts responde `503` en `/health/ready`. Las variables `CosteoNexo__*`, `Trm__*` y
    `Nexo__MinutosCacheMunicipios` se agregaron después con valores por defecto válidos; `docker stack config` y
    `docker compose config` se repitieron y pasan.
- Arranque real repetido por el validador (2026-09-29): las **72 variables** que produce hoy
  `infra/stack.dotnet.staging.yml` (con `Trm__*`, `CosteoNexo__*` y `Nexo__MinutosCacheMunicipios`) se pasaron a la
  imagen: `ASPNETCORE_ENVIRONMENT=Production`, `/health` 200 y `/health/ready` 200.
- Después se agregaron `ConnectionStrings__Regional` (vacía) y `Regional__*` (#45 y #81, todavía no en `QA`; con
  `ColumnaUsuario=USUARIO` y `TiempoEsperaLoginSegundos=3` de #81):
  `docker compose config` de ambos manifiestos pasa; la imagen actual las ignora (no tiene la sección) y la de #45
  acepta la cadena vacía (`clsSecreto(Opcional = true)`, el validador de conexiones omite las vacías) y los
  identificadores por defecto. No se repitió el arranque real.
- **Pendiente:** el despliegue completo en staging (Dokploy real, pull desde GHCR, secreto de Swarm, conectividad del
  worker a `tbsdatacenter`, Traefik). Necesita acceso al clúster y aprobación; no se ejecutó.

## 6. Backups de `dbo_cotifly` (HU-006.7, #31)

### 6.1 Política propuesta (pendiente de confirmar con el DBA)

La base ya no es un contenedor del stack: `dbo_cotifly` es un esquema dentro de `dbTarifario_Nexo_Pruebas`, una base
**compartida** con otras aplicaciones en la instancia corporativa `tbsdatacenter`, que administra el DBA. Por eso:

1. **Respaldo principal: los backups nativos del DBA** de `dbTarifario_Nexo_Pruebas` (completo, diferencial y de log,
   con la retención corporativa). Cotifly no ejecuta `BACKUP DATABASE` sobre una base ajena ni pide permisos para
   hacerlo. **Pendiente con el DBA:** confirmar frecuencia, retención, dónde se guardan y el procedimiento para
   recuperar un esquema (hoy exigiría restaurar la base completa en otra instancia y copiar `dbo_cotifly`).
2. **Respaldo complementario, opcional: exportación lógica diaria solo de `dbo_cotifly`** con
   `backend-dotnet/scripts/respaldo/exportar-dbo-cotifly.sh`. Sirve para recuperar Cotifly sin tocar los datos de las
   demás aplicaciones y para cargar una instancia de pruebas.

Los antiguos `scripts/backup_db.sh` y `scripts/restore_db.sh` de la raíz (`BACKUP/RESTORE DATABASE` con `sa`, solo para el
SQL Server en contenedor del Python) no aplicaban a la instancia corporativa y se borraron en HU-022.1 (#146).

### 6.2 Exportación lógica (`exportar-dbo-cotifly.sh`)

- **Qué exporta:** los datos de cada tabla de `dbo_cotifly` con `bcp` en formato nativo (un archivo por tabla), más
  `columnas.txt` (tabla, columna, tipo, largo, nulable), `filas.txt`, `metadatos.txt` y `SHA256SUMS`, en un
  `dbo_cotifly-AAAAMMDD-HHMMSS.tar.gz`. La estructura no se exporta: la definen los scripts `db/migrations_mssql/`.
- **Herramienta:** `bcp` y `sqlcmd` 18 (mssql-tools18). Si el equipo no los tiene, el script se ejecuta solo dentro de
  un contenedor de `mcr.microsoft.com/mssql/server:2019-latest`, que los trae (no hace falta instalar nada en el
  worker; la imagen pesa ~1,5 GB, configurable con `COTIFLY_RESPALDO_IMAGEN`). Se descartó `sqlpackage`/BACPAC:
  exporta la base completa (los esquemas de otras aplicaciones) y exige permisos sobre toda la base.
- **Permiso que necesita:** solo `SELECT` sobre el esquema. Se pide al DBA un login propio de solo lectura:

  ```sql
  CREATE LOGIN cotifly_respaldo WITH PASSWORD = '<secreto>';
  USE dbTarifario_Nexo_Pruebas;
  CREATE USER cotifly_respaldo FOR LOGIN cotifly_respaldo;
  GRANT SELECT ON SCHEMA::dbo_cotifly TO cotifly_respaldo;
  ```

  `cotifly_app` también podría (ya tiene `SELECT` sobre el esquema), pero además escribe; un login aparte limita el
  daño si se filtra la clave del respaldo. Probado: con solo `CONNECT` + `SELECT ON SCHEMA` exporta las 14 tablas.
- **Clave: usar `COTIFLY_RESPALDO_CLAVE_ARCHIVO`, no `COTIFLY_RESPALDO_CLAVE`.** Con el archivo (600, root) el
  contenedor de herramientas lo monta de solo lectura en `/run/secrets`; con la variable, la clave pasa como `-e` al
  contenedor y queda visible en `docker inspect` y en el entorno del proceso. `COTIFLY_RESPALDO_CLAVE` queda solo para
  pruebas locales.
- **Cifrado:** conexión cifrada y certificado validado por defecto; `COTIFLY_RESPALDO_CONFIAR_CERTIFICADO=1` solo si el
  certificado de `tbsdatacenter` no es de una CA de confianza.
- **Clave en la línea de comandos de `bcp`:** `sqlcmd` recibe la clave por `SQLCMDPASSWORD` (no se ve), pero `bcp`
  solo la acepta con `-P`, así que mientras corre cada tabla la clave de `cotifly_respaldo` aparece en `ps` (y en
  `/proc/<pid>/cmdline`) para cualquier usuario del equipo que ejecuta el script; con el contenedor de herramientas,
  para quien vea los procesos del contenedor desde el worker. Mitigaciones: el login es de solo lectura sobre un solo
  esquema, el worker solo lo usan administradores y la clave vive en un archivo `600`. Alternativa si se exige más:
  autenticación de Microsoft Entra o Kerberos (no disponibles hoy para un contenedor Linux contra `tbsdatacenter`).
- **Consistencia:** `bcp` exporta tabla por tabla, no es una foto transaccional; por eso se programa de madrugada. El
  restore revalida las llaves foráneas y avisa si algo quedó inconsistente.
- **Contenido sensible:** correos, nombres y NIT de clientes. Archivos `600`, carpetas `700`, dueño root.

### 6.3 Programación, ubicación y retención

- **Dónde corre:** cron del host en el **worker** (`TBSNODO1-DKP`, donde está el disco y la ruta de red a
  `tbsdatacenter`). No se usa una tarea de Dokploy: la imagen del backend no tiene shell ni herramientas.

  ```bash
  # Una vez, en el worker (root):
  install -d -m 700 /opt/cotifly/respaldo /etc/cotifly /var/backups/cotifly
  cp backend-dotnet/scripts/respaldo/*.sh /opt/cotifly/respaldo/
  cp backend-dotnet/scripts/respaldo/respaldo.env.example /etc/cotifly/respaldo.env   # completar; chmod 600
  printf '%s\n' '<clave de cotifly_respaldo>' > /etc/cotifly/respaldo.clave && chmod 600 /etc/cotifly/respaldo.clave
  # /etc/cron.d/cotifly-respaldo
  30 2 * * * root sh /opt/cotifly/respaldo/exportar-dbo-cotifly.sh --entorno /etc/cotifly/respaldo.env >> /var/log/cotifly-respaldo.log 2>&1
  ```

- **Ubicación:** `/var/backups/cotifly/diario` y `/var/backups/cotifly/semanal` en el worker. **Pendiente con
  infraestructura:** una copia fuera del nodo (recurso compartido o almacenamiento corporativo); si el worker se pierde,
  se pierden también sus respaldos.
- **Retención propuesta: 7 diarios + 4 semanales.** Cada ejecución deja un archivo en `diario/`; los domingos
  (`COTIFLY_RESPALDO_DIA_SEMANAL=7`) se copia también a `semanal/`. Se conservan los 7 y los 4 más recientes.
  - `COTIFLY_RESPALDO_DIARIOS` y `SEMANALES` deben ser enteros `>= 1` y `DIA_SEMANAL` de 1 a 7: se validan **antes**
    de exportar.
  - Una sola copia semanal por semana ISO: si el script corre dos veces el día semanal (un reintento a mano), la copia
    nueva reemplaza a la anterior de esa semana, así "4 semanales" son 4 semanas. Las copias diarias sí se acumulan
    (un reintento cuenta como un diario más).

  Tamaño
  medido en pruebas: ~32 KB con 540 filas; crece con `EventosHerramienta` y `EventosConversacion`.
- **Alerta:** el script sale con código distinto de 0 ante cualquier fallo. **Pendiente:** conectar el log a una
  alerta (por ejemplo, el space de soporte de Google Chat) o revisar `/var/log/cotifly-respaldo.log` en la rutina de
  operación.

### 6.4 Restore en una instancia de pruebas (`restaurar-dbo-cotifly.sh`)

```bash
COTIFLY_RESPALDO_SERVIDOR=<instancia-de-pruebas>,1433 COTIFLY_RESPALDO_USUARIO=<admin-de-pruebas> \
COTIFLY_RESPALDO_CLAVE_ARCHIVO=<archivo> \
  sh backend-dotnet/scripts/respaldo/restaurar-dbo-cotifly.sh --destino-pruebas --aplicar-migraciones \
     /var/backups/cotifly/diario/dbo_cotifly-AAAAMMDD-HHMMSS.tar.gz
```

1. Verifica `SHA256SUMS` y el formato.
2. Con `--aplicar-migraciones` crea la base si falta y aplica `db/migrations_mssql` (el `0001` solo si el esquema no
   existe).
3. Compara las columnas de destino con las del respaldo; si difieren, no toca nada.
4. Deshabilita las llaves foráneas, **borra** las filas del esquema, carga con `bcp` (conserva `IDENTITY` y `NULL`),
   reajusta los `IDENTITY` y vuelve a validar las llaves (`WITH CHECK`).
5. Compara las filas de cada tabla con las del respaldo.

Protecciones, antes de tocar nada:

- exige `--destino-pruebas`;
- nombres prohibidos: **`tbsdatacenter` siempre**, más los que agregue `COTIFLY_RESPALDO_PROHIBIDOS` (lista
  **adicional**, separada por comas, sin distinguir mayúsculas; definirla no quita `tbsdatacenter`). Se niega si
  `COTIFLY_RESPALDO_SERVIDOR` contiene alguno;
- se conecta y pregunta al servidor quién es (`@@SERVERNAME`, `SERVERPROPERTY('MachineName')`, `'ServerName'` y
  `'ComputerNamePhysicalNetBIOS'`): si alguno contiene un nombre prohibido, se niega. Cubre un alias DNS, una IP o un
  puerto reenviado que apunten a `tbsdatacenter` sin nombrarlo;
- se niega si esa identidad coincide con la del **servidor de origen** del respaldo, que la exportación registra en
  `metadatos.txt` (`origen_servername`, `origen_machinename`, `origen_netbios`): nunca se restaura encima del origen,
  aunque se llame distinto.
- **Límite conocido:** con un CNAME o un *listener* de Availability Group, el servidor responde con el nombre del nodo
  real. Si ese nodo no está en la lista de prohibidos ni es el origen registrado, no se detecta: agregar los nombres
  de los nodos del AG corporativo a `COTIFLY_RESPALDO_PROHIBIDOS` (pendiente con el DBA).

Recuperar producción es una decisión del DBA con sus
backups nativos; si hiciera falta cargar un respaldo lógico en producción, se hace con el DBA, a mano y con la app
detenida.

### 6.5 Prueba de backup y restore (2026-09-28, controles repetidos el 2026-09-29)

Contra SQL Server 2019 en contenedores locales (`mcr.microsoft.com/mssql/server:2019-latest`), sin tocar
`tbsdatacenter`. Los pasos 1 a 4 se repiten con **`sh backend-dotnet/scripts/respaldo/probar-respaldo-local.sh`**
(datos ficticios de `probar-respaldo-datos.sql`, ~4–6 minutos, borra sus contenedores al terminar); el paso 5 se hizo
una vez con la imagen de la API.

1. Origen con `0001`–`0003`, datos reales de la API (dos logins por `/api/auth/login-clientepro`: `UsuariosWeb`,
   `UsuariosRoles`, `TokensRefresco`) y filas de prueba en las demás tablas (Unicode, emoji, saltos de línea, `NULL`,
   JSON, 500 filas de 10 KB en `EventosHerramienta`, un `UsuariosWeb` sin `nombreUsuario` para el índice filtrado).
2. Exportación como `cotifly_respaldo` con solo `CONNECT` + `SELECT ON SCHEMA::dbo_cotifly`: 14 tablas, 540 filas,
   31,7 KB. Retención: con 9 diarios previos quedaron los 7 más recientes; la copia semanal se creó el día configurado.
3. Controles (2026-09-29, `probar-respaldo-local.sh`, cada uno verificado por código de salida **y** por su mensaje):
   sin `--destino-pruebas`; `TBSDATACENTER` por nombre, también con `COTIFLY_RESPALDO_PROHIBIDOS=otro`; un SQL
   Server con hostname `tbsdatacenter` alcanzado por un alias de red, con `PROHIBIDOS=otro` (identidad del servidor);
   un `.dat` alterado; un destino cuyo `MachineName` está en `PROHIBIDOS`; y restaurar encima del propio servidor de
   origen (identidad registrada en `metadatos.txt`). Todos se niegan. También: `COTIFLY_RESPALDO_DIARIOS=0` y
   `SEMANALES=siete` se rechazan antes de exportar, y una segunda corrida el día semanal deja una sola copia semanal.
   Resultado de la corrida: **OK** (14 tablas con SHA-256 idéntico).
4. Restore en una **instancia nueva y vacía** con `--aplicar-migraciones`: columnas iguales (116), filas iguales en
   las 14 tablas (incluidas las semillas de las migraciones, que se reemplazan) y **SHA-256 del contenido de cada tabla
   idéntico** entre origen y destino (`SELECT * ... FOR JSON`), llaves foráneas confiables, `IDENTITY` reajustado.
5. **El respaldo es usable:** la API arrancó contra la base restaurada (`/health/ready` 200) y el login de `APEREZ`
   respondió 200 con su usuario y rol restaurados (verificación doble de ADR-009).

**Pendiente:** repetir la prueba con un respaldo real de `tbsdatacenter` en una instancia de pruebas corporativa, cuando
exista el login `cotifly_respaldo` y la instancia de pruebas (DBA).

## 7. Google Chat retirado (HU-013.9, #105)

Decisión del 2026-10-05 (ADR-018 §15): Cotifly es un módulo de formularios dentro de ClientePro y Google Chat sale del
alcance. Desde HU-013.9 el backend .NET **no tiene** el webhook `POST /chat/events` (responde 404), ni el verificador del
JWT de Google, la cola, el worker, el publicador o las tarjetas, ni las opciones `GChat__*` y su validación al arrancar.
El agente cotizador legado y el flag `Chat__CreacionHabilitada` también se retiraron: `POST /web/chat` exige `folio`.

Al desplegar esta versión:

- Quitar del *Environment* de Dokploy las variables `GChat__*`, `Chat__CreacionHabilitada` y `GCHAT_SA_SECRETO` (si
  quedan, la API las ignora y arranca igual, pero no deben confundir).
- Borrar el secreto de Swarm de la cuenta de servicio (`cotifly_gchat_sa_v1`, `cotifly_staging_gchat_sa_v1`) y, en
  Google Cloud, deshabilitar la app de Chat que apuntaba a `https://<PUBLIC_HOST>/chat/events`. Ambas acciones tocan
  infraestructura real: solo con confirmación explícita.
- Las tablas `Sesiones` y `EventosConversacion` **no se borran**: guardan la auditoría de las conversaciones de Google
  Chat (`spaceId = spaces/...`) y siguen alimentándose con el asistente por folio (`web/{sesion_id}`).

La verificación del JWT del webhook (HU-006.7) era del stack Python; su runbook está en el tag `python-final` (Anexo A).

## Anexo A. Stack Python (retirado, tag `python-final`)

El stack Python (orquestador, servidor MCP, `shared/`, Postgres y MinIO) se retiró del repositorio en HU-022.1 (#146),
por decisión del usuario del 2026-10-13: "todo lo referente a python muere en este repo". Ya no es destino de rollback
desde este repositorio.

- **Dónde quedó:** el tag `python-final` guarda el último estado completo: el código, `deploy/docker-compose.swarm.yml`
  (el stack que corre en Dokploy, proyecto `bot-cotizador`, puerto 8091 del worker), `deploy/rebuild-on-worker.sh`,
  `infra/stack.dokploy.yml`, `infra/stack.staging.yml`, los scripts de respaldo y la versión completa de este anexo
  (runbook de la Fase 15). Para consultarlo: `git show python-final:DEPLOY.md`.
- **Lo que sigue desplegado:** el stack Python en Dokploy no se tocó desde aquí. Apagarlo, rotar sus credenciales por
  defecto y deshabilitar la app de Google Chat son acciones de infraestructura con confirmación aparte (§7).
