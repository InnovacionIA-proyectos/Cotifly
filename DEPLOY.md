# Despliegue en Dokploy (Docker Swarm) — Cotifly

Runbook del despliegue de Cotifly en el clúster Docker Swarm + Dokploy (manager `10.10.40.17`, worker `10.10.40.31`
`TBSNODO1-DKP`, Traefik gestionado por Dokploy). Milestone **HU-006**.

## 1. Estado y alcance

- **Backend vigente tras el corte: .NET 10** (`backend-dotnet/`, una sola imagen `cotifly-backend`, ADR-001 y ADR-002).
  Este documento describe su despliegue. Las decisiones están en
  [ADR-013](backend-dotnet/specs/decisiones/ADR-013-despliegue-dokploy.md).
- **Stack Python: se conserva solo para rollback** hasta validar producción (corte único, ADR-001). Sus manifiestos
  (`infra/stack.dokploy.yml`, `infra/stack.staging.yml`, `deploy/docker-compose.swarm.yml`) y scripts
  (`deploy/rebuild-on-worker.sh`, `scripts/backup_db.sh`, `scripts/restore_db.sh`) no se modifican. Su runbook
  quedó en el [Anexo A](#anexo-a-stack-python-solo-rollback).
- La base de datos **no** es un contenedor del stack: es la instancia corporativa SQL Server 2019 (`tbsdatacenter`),
  base `dbTarifario_Nexo_Pruebas`, esquema `dbo_cotifly`, más `dbsolucionesti` de solo lectura (ADR-008). El login de
  servicio es `cotifly_app` (SQL login; el contenedor Linux no admite autenticación integrada).
- Referencia de cada variable y de su validación al arrancar: `backend-dotnet/.env.example` y
  `backend-dotnet/docs/03-infraestructura.md` §3.2.

| Issue | Tema | Sección |
|---|---|---|
| #29 (HU-006.5) | Secretos fuera del repositorio | [§2](#2-secretos-y-variables-hu-0065-29), [§3](#3-tabla-de-variables-de-producción-plantilla-para-hu-0071-33) |
| #28 (HU-006.4) | Registry, imagen versionada y release | [§4](#4-registry-imagen-versionada-y-release-hu-0064-28) |
| #30 (HU-006.6) | Manifiestos del stack .NET + SQL Server externo | [§5](#5-manifiestos-del-stack-net-hu-0066-30) |
| #31 (HU-006.7) | Backups de `dbo_cotifly` y verificación del JWT de Google Chat | [§6](#6-backups-de-dbo_cotifly-hu-0067-31), [§7](#7-webhook-de-google-chat-verificación-del-jwt-hu-0067-31) |

## 2. Secretos y variables (HU-006.5, #29)

### 2.1 Mecanismo

Decisión propuesta (**pendiente de confirmar con el equipo** de infraestructura si existe un estándar corporativo
distinto, por ejemplo un gestor de secretos):

1. **Variables de entorno de Dokploy por servicio** (pestaña *Environment* del compose de tipo *stack*). Dokploy las
   guarda en su base y las escribe en el `.env` con el que hace `docker stack deploy`; el manifiesto solo las
   referencia como `${Variable}` y nunca lleva el valor. Así llegan las cadenas de conexión, `Jwt__SigningKey`,
   `Pdf__ClaveFirmaUrl`, `IA__ApiKey` y `SapTm__Pass`.
2. **Secreto de Docker Swarm** para la cuenta de servicio de Google Chat (`GChat__SaJson`): es un archivo JSON con una
   clave privada, lo más pesado y sensible. Se crea una vez en el manager y se monta en `/run/secrets/gchat_sa.json`;
   la variable `GChat__SaJson` apunta a esa ruta (la app acepta el contenido o la ruta, ver
   `clsOpcionesGoogleChat`).

   ```bash
   # En el manager (10.10.40.17). El nombre lleva versión: los secretos de Swarm son inmutables.
   docker secret create cotifly_gchat_sa_v1 ./cuenta-servicio-chat.json && shred -u ./cuenta-servicio-chat.json
   ```

   Rotar = crear `cotifly_gchat_sa_v2`, cambiar el nombre en la variable del stack (`GCHAT_SA_SECRETO`) y redesplegar;
   luego `docker secret rm cotifly_gchat_sa_v1`. Staging usa su propio secreto, `cotifly_staging_gchat_sa_v1` (§5.3).

Por qué no todo como secreto de Swarm: el backend lee la configuración de variables de entorno; solo `GChat__SaJson`
acepta una ruta de archivo. Llevar las cadenas de conexión o la clave JWT a `/run/secrets` exige agregar el proveedor
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
- Nexo no usa credencial: cada petición lleva `X-Portal-Usuario-Id` del comercial (ADR-005).

### 2.2 Variables que espera el backend .NET

Nombres exactos (ASP.NET Core traduce `__` a `:`). "Secreto" indica si va con el tratamiento del §2.1. "Obligatoria"
se refiere a `Production`: sin ella (o con un valor de ejemplo) la API no arranca.

| Variable | Secreto | Obligatoria en Production | Por defecto | Dónde se carga | Nota |
|---|---|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | no | — | `Production` (sin valor) | manifiesto | `Production` en staging **y** en producción, a propósito: así staging aplica las mismas validaciones de arranque (`clsValidadorSecretos`, CORS obligatorio, `GChat:VerificarJwt=false` prohibido) y un error de configuración aparece antes de producción |
| `ASPNETCORE_HTTP_PORTS` | no | no | `8080` (imagen) | imagen | no se cambia |
| `Zona__ZonaHoraria` | no | no | `America/Bogota` | imagen / manifiesto | |
| `ConnectionStrings__Cotifly` | **sí** | **sí** | vacío | Dokploy | `Server=tbsdatacenter,1433;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=...;Encrypt=True` |
| `ConnectionStrings__Identidad` | **sí** | **sí** | vacío | Dokploy | igual, `Database=dbsolucionesti` (solo lectura) |
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
| `SapTm__SalesOrgDefault` | no | no | `50000005` | manifiesto | |
| `SapTm__Modo` | no | no (aviso) | vacío | Dokploy | vacío, `export_json` u `odata` (HU-009) |
| `SapTm__ExportPath` | no | con `export_json` | vacío | Dokploy | archivo montado de solo lectura |
| `SapTm__Url`, `SapTm__User`, `SapTm__SapClient` | no | `Url` y `User` con `odata` | vacío | Dokploy | |
| `SapTm__Pass` | **sí** (≥ 8) | con `odata` | vacío | Dokploy | no definirla si no se usa `odata` |
| `Nexo__UrlBase` | no | no | vacío | Dokploy | **raíz** del servicio (`https://<host-nexo>`), sin `/api`: el cliente agrega las rutas `/api/...`. Sin ella la búsqueda de clientes responde `*_NO_DISPONIBLE` y el costeo no corre |
| `Nexo__MinutosCacheMunicipios` | no | no | `60` | manifiesto | caché de la lista de municipios de Nexo (frente B) |
| `CosteoNexo__Habilitado` | no | no | staging **`false`**, producción `true` | manifiesto / Dokploy | polling del costeo en Nexo (HU-005.6/005.8). En staging queda apagado hasta que el DBA confirme que staging y producción **no** comparten `dbo_cotifly`: si la comparten, los workers de los dos stacks consultarían y actualizarían las mismas cotizaciones contra instancias distintas de Nexo. Sin `Nexo__UrlBase` no corre aunque esté en `true` |
| `CosteoNexo__IntervaloInicialSegundos`, `__IntervaloMaximoMinutos`, `__FactorEdad`, `__EdadMaximaHoras` | no | no | `5`, `15`, `0.25`, `72` | manifiesto | ritmo del polling: primera consulta a los 5 s, espera creciente con la edad (25 %) hasta 15 min; pasadas 72 h queda `costeo_vencido`. Valores por confirmar con el equipo de Nexo (ADR-015) |
| `CosteoNexo__IntervaloTickSegundos`, `__Lote`, `__ReclamoSegundos` | no | no | `5`, `20`, `120` | manifiesto | cada cuánto revisa el worker la agenda, cotizaciones por vuelta y reclamo de una cotización en consulta (debe superar los 60 s de la consulta a Nexo) |
| `Trm__Url` | no | **sí** (vacía no arranca) | `https://www.datos.gov.co/resource/32sa-8pi3.json` | manifiesto | TRM del día (frente C), datos abiertos; exige salida a `www.datos.gov.co:443` |
| `Trm__TiempoEsperaSegundos` | no | no | `10` | manifiesto | límite de la consulta de la TRM |
| `Pdf__StorageModo`, `Pdf__StorageDir` | no | no | `volumen`, `/data/pdfs` | manifiesto | |
| `Pdf__BaseUrl` | no | no (no se valida al arrancar) | vacío | Dokploy | URL pública de la API (`https://<PUBLIC_HOST>`). Vacía, el enlace del PDF queda relativo (`/pdfs/{folio}.pdf`): sirve en la web, pero la tarjeta de Google Chat no lo puede abrir; configurarla si Google Chat está activo |
| `Pdf__ClaveFirmaUrl` | **sí** (≥ 32) | con Google Chat configurado | vacío | Dokploy | `openssl rand -base64 48` |
| `Pdf__MinutosUrlFirmada` | no | no | `15` | manifiesto | |
| `Correo__Habilitado`, `Correo__Modo` | no | no | `false`, `smtp_relay` | Dokploy | transporte real por confirmar (D3) |
| `Correo__SmtpRelay__Host` | no | con el correo habilitado | vacío | Dokploy | relay interno, sin AUTH (control por IP) |
| `Correo__SmtpRelay__Port`, `__FromFallback`, `__TimeoutSeg` | no | no | `25`, `notificaciones@boterosoto.com.co`, `20` | manifiesto | |
| `IA__Proveedor`, `IA__Modelo`, `IA__Endpoint` | no | no (sin proveedor el agente no responde) | vacío | Dokploy | ADR-003. Con OmniRoute (gateway LLM interno, #84): `IA__Proveedor=openai_compatible`, `IA__Endpoint=http://llm-services-omniroute.10-10-40-17.sslip.io/v1` (HTTP dentro de la red interna, sin TLS; exige salida del worker hacia `10.10.40.17:80`) y `IA__Modelo=gemini/gemini-3.5-flash-lite`. Matriz de modelos en [docs/03](backend-dotnet/docs/03-infraestructura.md#omniroute). **Nunca** un gateway web no oficial de los que lista OmniRoute (`duckduckgo-web`, `theoldllm`, `veoaifree-web`, `chipotle`): riesgo de confidencialidad |
| `IA__ApiKey` | **sí** (≥ 16) | según el proveedor (obligatoria con openai, azure_openai, gemini y anthropic); si tiene valor, en Production no puede ser un placeholder ni tener menos de 16 caracteres | vacío | Dokploy | **secreto de Dokploy**, nunca en el repositorio ni en el manifiesto. Con OmniRoute es la clave del gateway (`Authorization: Bearer`); como el tráfico va por HTTP, solo debe viajar dentro de la red interna. La clave usada en las pruebas del 2026-10-01 se compartió por chat: **rotarla** antes de cargarla en staging |
| `IA__Fallbacks__N__Proveedor`, `__Modelo`, `__Endpoint` | no | no | vacío | Dokploy | respaldos ante 429, 5xx, red caída o el 400 "No credentials for provider" de OmniRoute. Los manifiestos declaran `__0__` y `__1__` con `${...:-}`: un respaldo con `Proveedor` vacío no existe (si tiene modelo, endpoint o clave sin proveedor, la API no arranca). Con OmniRoute: `__0__` `gemini/gemini-3.5-flash`, `__1__` `gemini/gemini-3-flash-preview`, mismo endpoint y clave |
| `IA__Fallbacks__N__ApiKey` | **sí** (≥ 16) | igual que `IA__ApiKey`, por respaldo | vacío | Dokploy | secreto de Dokploy; con OmniRoute, la misma clave del gateway |
| `IA__MaxTokens`, `IA__MaxIteraciones`, `IA__SoportaJsonSchema`, `IA__MinutosSinJsonSchema`, `IA__JsonSchemaEstricto`, `IA__Rpm`, `IA__TimeoutSeg` | no | no | `4096`, `12`, `true`, `30`, `false`, `8`, `60` | manifiesto | con OmniRoute y Gemini: `IA__SoportaJsonSchema=false` (Gemini vía OmniRoute ignora `json_schema`) e `IA__MaxTokens=1500` (con 4096 OpenRouter respondió 402 porque reserva crédito por el máximo pedido) |
| `LimiteChat__PeticionesPorVentana`, `LimiteChat__VentanaSegundos` | no | no | `20`, `60` | manifiesto | |
| `Sesiones__LimiteTokens`, `Sesiones__TtlHoras`, `Sesiones__BarridoMinutos`, `Sesiones__EsperaTurnoSegundos` | no | no | `6000`, `24`, `60`, `3` | manifiesto | |
| `GChat__ProjectNumber` | no | no (sin él el webhook responde 401) | vacío | Dokploy | número del proyecto de Google Cloud |
| `GChat__VerificarJwt` | no | `false` no arranca | `true` | manifiesto | siempre `true` en QA y producción |
| `GChat__SaJson` | **sí** | no | vacío | **secreto de Swarm** | `/run/secrets/gchat_sa.json` |
| `GChat__SupportSpace`, `GChat__CapacidadCola` | no | no | vacío, `200` | Dokploy / manifiesto | |
| `GChat__UrlCertificados`, `GChat__ApiBase` | no | no | certificados de `chat@system.gserviceaccount.com`, `https://chat.googleapis.com/v1` | — | solo pruebas: no se cargan en QA ni producción (no están en los manifiestos) |
| `Grovity__BaseUrl`, `Grovity__Usuario` / `Grovity__Clave` | no / **sí** | no (el código no las lee) | — | — | solo si se aprueba Grovity desde el agente (ADR-005) |

**OmniRoute: riesgo de transporte y de datos (#84).**

- **Riesgo, HTTP sin cifrar.** OmniRoute se expone como `http://llm-services-omniroute.10-10-40-17.sslip.io` (puerto 80 del manager `10.10.40.17`). Cada turno del agente viaja en claro por la red interna: nombres y NIT de clientes, costos, márgenes y precios, el correo del comercial y la clave `Authorization: Bearer`. Cualquier equipo con acceso a ese segmento puede leerlos o reutilizar la clave.
- **Recomendado antes de producción:** publicar OmniRoute por HTTPS (router de Traefik con TLS en el clúster, en la línea de #25/#26) y cambiar `IA__Endpoint` a `https://...`. Como mínimo, aislar el tráfico worker → manager (overlay o reglas de firewall que solo permitan al worker del API).
- **Rotación de la clave de OmniRoute:**
  - **cuándo:** antes de cargarla en staging (la de las pruebas del 2026-10-01 se compartió por chat); después, cada 6 meses o de inmediato ante cualquier exposición (chat, log, captura, repositorio, salida de alguien con acceso);
  - **cómo:** (1) el dueño de OmniRoute emite una clave nueva; (2) actualizar `IA__ApiKey` e `IA__Fallbacks__N__ApiKey` en Dokploy (staging y producción); (3) redesplegar el stack (*Deploy* en Dokploy) y probar un turno de `/web/chat`; (4) revocar la anterior en OmniRoute; (5) anotar la fecha en la bitácora de operaciones.
- **Datos y términos del proveedor:** pendiente con negocio y legal (ADR-003 "Pendientes"). Hay que confirmar qué credencial tiene cargada OmniRoute, la residencia de los datos y si OmniRoute registra los prompts.

Variables del Python que **no** pasan al backend .NET (se retiran; no cargarlas en el stack nuevo):
`DATABASE_URL`, `POSTGRES_*`, `MSSQL_SA_PASSWORD`, `MCP_URL`, `MCP_AUTH_TOKEN` (no hay MCP aparte, ADR-002),
`WEB_SESSION_SECRET` (reemplazada por `Jwt__SigningKey`), `INTERNAL_API_TOKEN`, `LLM_MODEL`/`LLM_API_KEY`/`LLM_RPM`
(reemplazadas por `IA__*`), `GCHAT_*` (reemplazadas por `GChat__*`), `GOOGLE_SA_JSON` y `DRIVE_TARIFARIO_*` (tarifario
de Drive retirado), `TARIFA_*`, `S3_*` y `MINIO_*` (MinIO no se mantiene, ADR-012), `SHEETS_*` (diferida, ADR-012),
`SMTP_*`/`SMTP_RELAY_*`/`CORREO_*` (reemplazadas por `Correo__*`), `SAP_TM_*`/`SAP_SALES_ORG_DEFAULT` (reemplazadas por
`SapTm__*`), `PDF_*` (reemplazadas por `Pdf__*`), `PUBLIC_HOST` (sigue como variable del manifiesto para Traefik, no
de la app).

### 2.3 Auditoría de secretos en el repositorio (2026-09-28)

Búsqueda de contraseñas, claves, tokens, cadenas de conexión y archivos de credenciales en el árbol actual y en el
historial de git (`git log --all -p`: claves privadas PEM, claves de Google `AIza...`, `sk-...`, `ghp_...`, archivos
`.env`, `.pem`, `.pfx`, `*sa.json`). **No se reescribe el historial.**

| Archivo | Hallazgo | Riesgo | Acción |
|---|---|---|---|
| `deploy/docker-compose.swarm.yml` (stack Python que corre hoy) | `POSTGRES_PASSWORD: bot`, `DATABASE_URL` con `bot:bot`, `MINIO_ROOT_USER`/`PASSWORD` y `S3_ACCESS_KEY`/`S3_SECRET_KEY` `minioadmin` | Credenciales por defecto en texto plano; servicios solo en la red interna `bot-back`, pero cualquiera con acceso al repo las conoce | No se toca (Python intacto hasta el corte, ADR-001). El stack .NET no lo usa. **Rotarlas o apagar el stack al retirar el Python** |
| `docker-compose.yml` (desarrollo local del Python) | Valores por defecto `Bot_Cotizador_2019!` (sa), `dev-token`, `dev-secret-cambia`, `minioadmin` | Solo desarrollo local; se pueden sobrescribir por variable | Ninguna; no los usa el despliegue |
| `.env.example` (raíz, Python) | `bot:bot@localhost`, `cambiar-en-despliegue`, `dev-secret-cambia` | Ejemplos de desarrollo | Ninguna |
| `backend-dotnet/scripts/levantar-local.sh` y `.ps1` | Claves por defecto `CotiflyLocal#2026` (sa) y `CotiflyApp#2026` (cotifly_app) del SQL Server local | Solo el contenedor local, publicado en `127.0.0.1`; sobrescribibles con `COTIFLY_SQL_*_PASSWORD` | Ninguna |
| `secrets/google-sa.json` | Archivo versionado con contenido `{}` (sin clave); en el historial nunca tuvo `private_key` | Si alguien pega ahí la cuenta de servicio real y hace commit, queda publicada | No usarlo en el stack .NET (la cuenta va como secreto de Swarm, §2.1). Recomendación: quitarlo del índice al retirar el Python |
| `infra/stack.dokploy.yml`, `infra/stack.staging.yml` | Solo `${VAR}`, sin valores | — | Ninguna |
| `backend-dotnet/.env.example`, `appsettings*.json`, docs | Solo placeholders (`<secreto>`, `<host>`); `appsettings*.json` no tiene secretos | — | Ninguna |
| Pruebas (`tests/`, `backend-dotnet/tests/`) | Claves ficticias de prueba (`sk-ant-clave-de-prueba-...`, `test-token-fase2`, `ya29.fake-token`) | Ninguno | Ninguna |
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
| `Pdf__ClaveFirmaUrl` | sí | generada por el equipo de despliegue | no | Backend | — |
| `IA__Proveedor` / `IA__Modelo` / `IA__Endpoint` | no | negocio (ADR-003). Propuesta con OmniRoute: `openai_compatible`, `gemini/gemini-3.5-flash-lite`, endpoint interno (por HTTPS si se publica, §2.2) | no | Negocio / Backend | `LLM_MODEL` |
| `IA__ApiKey` (y respaldos) | sí | dueño de la cuenta del proveedor LLM (con OmniRoute, su administrador; rotar antes de staging, §2.2) | no | Negocio | `LLM_API_KEY` |
| `IA__Fallbacks__0__*`, `IA__Fallbacks__1__*` | `ApiKey` sí | negocio (ADR-003). Propuesta con OmniRoute: `gemini/gemini-3.5-flash` y `gemini/gemini-3-flash-preview` | no | Negocio / Backend | fallbacks de LiteLLM |
| `IA__SoportaJsonSchema` / `IA__MaxTokens` | no | con OmniRoute y Gemini: `false` y `1500` (§2.2) | no | Backend | `LLM_MAX_TOKENS` |
| `GChat__ProjectNumber` | no | Google Cloud (proyecto de la app de Chat) | no | Backend | `GCHAT_PROJECT_NUMBER` |
| `GChat__VerificarJwt` | no | fijo: `true` (§7) | no | Backend | `GCHAT_VERIFICAR_JWT` (hoy `false`) |
| `GChat__SaJson` (secreto de Swarm) | sí | Google Cloud (cuenta de servicio `chat.bot`) | no | Backend | `GOOGLE_SA_JSON` |
| `GChat__SupportSpace` | no | Google Chat (space de soporte) | no | Backend | `GCHAT_SUPPORT_SPACE` |
| `Nexo__UrlBase` | no | equipo de Nexo (URL de producción, no la de pruebas) | no | Backend / Nexo | — |
| `SapTm__Modo` (+ `ExportPath` o `Url`/`User`/`Pass`) | `Pass` sí | SAP / infraestructura (HU-009) | no | SAP / Backend | `SAP_TM_*` |
| `Correo__Habilitado` / `Correo__SmtpRelay__Host` | no | TI (relay y transporte real, D3) | no | TI | `CORREO_*`, `SMTP_RELAY_HOST` |
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

### 4.3 Publicación manual (reemplaza a `deploy/rebuild-on-worker.sh`)

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

`deploy/rebuild-on-worker.sh` queda solo para el stack Python (rollback).

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

0. **Precondición de esquema: `db/migrations_mssql/0001`–`0004` aplicados** en la base del ambiente (con un usuario
   administrador y `sqlcmd -I`; la app no aplica migraciones). **El `0004` (costeo en Nexo, HU-005.8) va ANTES de
   desplegar la versión que lo incluye:** sin él, `/health/ready` responde `503` con `falta 0004`, Traefik no manda
   tráfico a la tarea y, como la sonda de vida sigue en 200, Swarm no revierte solo (paso 5). Cada script nuevo
   `000N` sigue la misma regla: primero la migración, después la imagen.
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
7. **Rollback al stack Python** (corte único, ADR-001: solo si el backend .NET no puede seguir en producción):
   1. Redesplegar en Dokploy el stack Python (`deploy/docker-compose.swarm.yml`, proyecto `bot-cotizador`) y
      comprobar `http://10.10.40.31:8091/health`.
   2. En Google Cloud, volver a apuntar la app de Google Chat (URL del endpoint HTTP) al orquestador Python; mientras
      apunte a la API .NET, Chat seguirá llegando a .NET.
   3. Escalar a 0 el servicio .NET (`docker service scale <stack>_cotifly-api=0`) para que no haya dos backends activos.
   4. **Aviso de datos:** el Python usa su propia base Postgres. Lo que el backend .NET escribió en SQL Server
      (`dbo_cotifly`: cotizaciones, ajustes, correos, sesiones, usuarios) **no** es visible para el Python; no hay
      migración inversa. Las cotizaciones hechas en .NET se consultan en SQL Server hasta volver a .NET.

## 5. Manifiestos del stack .NET (HU-006.6, #30)

### 5.1 Archivos

| Manifiesto | Ambiente | Reemplaza (se conserva para rollback) |
|---|---|---|
| [`infra/stack.dotnet.staging.yml`](infra/stack.dotnet.staging.yml) | staging | `infra/stack.staging.yml` (nunca desplegado) |
| [`infra/stack.dotnet.dokploy.yml`](infra/stack.dotnet.dokploy.yml) | producción | **`deploy/docker-compose.swarm.yml`**, el stack Python que corre hoy en Dokploy (proyecto `bot-cotizador`, puerto 8091). `infra/stack.dokploy.yml` es su versión "ideal", nunca desplegada |

**Qué Python corre de verdad:** según el backlog de HU-006 (auditoría del despliegue) y la cabecera del propio archivo,
Dokploy despliega `deploy/docker-compose.swarm.yml` (Postgres, MinIO, MCP y orquestador anclados al worker, orquestador
en `10.10.40.31:8091` en modo host). `infra/stack.dokploy.yml` e `infra/stack.staging.yml` están documentados en el
Anexo A pero no se desplegaron (siguen con Postgres y `${REGISTRY}`). **El rollback al Python redespliega
`deploy/docker-compose.swarm.yml`** (§4.5 paso 7).

Se crearon archivos nuevos en vez de editar los del Python para que el rollback siga siendo "redesplegar el stack
Python" sin tocar git. Los dos manifiestos .NET son iguales salvo lo que marca la tabla del §5.3.

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
        ├── secreto de Swarm → /run/secrets/gchat_sa.json
        └── red "salida" (overlay con egreso) → tbsdatacenter:1433 (SQL Server 2019, externo)
                                              → proveedor LLM, Google Chat, Nexo, SAP TM, relay SMTP,
                                                TRM en www.datos.gov.co:443
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
  | `bot-edge` (overlay con egreso) | `salida` (overlay propia, no interna) | Salida a internet y a la red corporativa: LLM, Google Chat, Nexo, SAP TM, TRM (`www.datos.gov.co`), relay, SQL Server |
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
- **Una réplica:** la cola de Google Chat y el barrido de sesiones viven en el proceso, y el volumen de PDFs es local
  (ADR-012). Escalar exige un volumen compartido y el candado de correo en la base.
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
  | Volumen / secreto / router | `cotifly_staging_pdfs` / `cotifly_staging_gchat_sa_v1` / `cotifly-api-staging` | `cotifly_pdfs` / `cotifly_gchat_sa_v1` / `cotifly-api` |
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
| `GCHAT_SA_SECRETO` | no | Nombre del secreto de Swarm (por defecto `cotifly_gchat_sa_v1` o `cotifly_staging_gchat_sa_v1`) |
| `COTIFLY_PUERTO_HOST` | no (staging) | Puerto del worker en modo host (por defecto 8092) |
| `COTIFLY_ORDEN_UPDATE` | no (staging) | `stop-first` (por defecto) o `start-first` |

Obligatorias por el manifiesto (`${VAR:?}`: sin ellas `docker stack deploy` falla antes de tocar el servicio):
`COTIFLY_TAG`, `PUBLIC_HOST`, `ConnectionStrings__Cotifly`, `ConnectionStrings__Identidad`, `Jwt__SigningKey`,
`Cors__OrigenesPermitidos__0` y `ForwardedHeaders__RedesConocidas__0` (la subred de `dokploy-network`:
`docker network inspect dokploy-network --format '{{(index .IPAM.Config 0).Subnet}}'`).

Fijas en el manifiesto (no se leen de Dokploy): `GChat__VerificarJwt=true`, `ForwardedHeaders__Habilitado=true`,
`Pdf__StorageModo=volumen`, `Pdf__StorageDir=/data/pdfs`, `Zona__ZonaHoraria=America/Bogota`.

Opcionales con valor vacío = "no configurada" (verificado en el código de cada opción): `ConnectionStrings__Regional`
(vacía, se usa lo que ya haya en `UsuariosWeb.regional`; con valor, el login la llena y refresca (#81) y el tablero
del gerente la lee en vivo (#45), y necesita salida desde el worker a `10.10.40.24:1433` y un usuario SQL de solo
lectura con `SELECT` únicamente sobre `dbo.DimEjecutivo`, pendiente con el DBA), `Nexo__UrlBase`, `IA__Proveedor`,
`GChat__ProjectNumber`, `GChat__SaJson`, `SapTm__Modo`, `Pdf__BaseUrl`, `Pdf__ClaveFirmaUrl`. En cambio
`Regional__Esquema`, `__Tabla`, `__ColumnaUsuario`, `__ColumnaRegional` y `__Dominio` son identificadores obligatorios:
el manifiesto trae los valores por defecto de `clsOpcionesRegional` (`dbo`, `DimEjecutivo`, `USUARIO`, `REGIONAL`,
`boterosoto`) y no deben cargarse vacías en Dokploy. **Ojo:** como el manifiesto siempre manda la variable, su valor por
defecto manda sobre el del código; si el código cambia un valor por defecto (como `ColumnaUsuario`: `ID` → `USUARIO`
en #81), hay que cambiarlo también en los manifiestos. `Regional__TiempoEsperaLoginSegundos` (3, de 1 a 10) limita
cuánto espera el login por `DimEjecutivo` antes de seguir sin bloquear.

**Una variable que no está en el manifiesto no llega al contenedor**, aunque se cargue en Dokploy (Dokploy solo la usa
para sustituir `${...}`). Los respaldos `IA__Fallbacks__0__*` e
`IA__Fallbacks__1__*` ya están en los manifiestos con `${...:-}`: vacíos = sin respaldo (#84, `clsOpcionesIA.FallbacksConfigurados`
ignora un respaldo sin `Proveedor`). Un tercer respaldo exige agregar `IA__Fallbacks__2__*` al manifiesto en un commit.
Con OmniRoute, cargar en Dokploy `IA__SoportaJsonSchema=false` e `IA__MaxTokens=1500`: el manifiesto trae `true` y `4096`
por defecto.

### 5.5 Desplegar en Dokploy (pasos; staging pendiente de aprobación)

1. En el manager: `docker secret create cotifly_staging_gchat_sa_v1 <archivo>` (o con `{}` mientras no exista la app
   de Chat) y `docker login ghcr.io` con el PAT de solo lectura (§4.4).
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
  valores por defecto vacíos (IA, Google Chat, SAP, Nexo, correo) no rompen la validación de arranque.
  - Esa corrida fue con la imagen de `QA` de ese día, que exigía `0001`–`0003`. **Desde el costeo en Nexo (HU-005.8)
    la precondición es `0001`–`0004`**: una imagen que incluye el costeo contra una base sin `0004` responde `503`
    `falta 0004` en `/health/ready` (§4.5, paso 0). Las variables `CosteoNexo__*`, `Trm__*` y
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

`scripts/backup_db.sh` y `scripts/restore_db.sh` (raíz del repositorio) usan `BACKUP/RESTORE DATABASE` con `sa`: solo
sirven para el SQL Server en contenedor del desarrollo local del Python. No aplican a la instancia corporativa.

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

## 7. Webhook de Google Chat: verificación del JWT (HU-006.7, #31)

**Decisión: la verificación del JWT queda activada en el backend .NET** (`GChat__VerificarJwt=true`, fija en los
manifiestos del §5; en `Production` la API ni siquiera arranca con `false`, `clsValidadorOpcionesGoogleChat`).

- **Qué verifica:** `POST /chat/events` exige el `Authorization: Bearer` que firma Google: RS256 con los certificados
  públicos de `chat@system.gserviceaccount.com`, emisor `chat@system.gserviceaccount.com`, audiencia =
  **`GChat__ProjectNumber`** y vigencia. Sin token válido responde 401 y no procesa nada.
- **Qué necesita:** `GChat__ProjectNumber` = el **número** (solo dígitos, no el ID) del proyecto de Google Cloud donde
  se crea la app de Chat (*Google Cloud Console → Panel del proyecto → Número del proyecto*). En la configuración de la
  app de Chat, *Authentication Audience* debe ser **Project Number** (no *HTTP endpoint URL*). Sin el número, con la
  verificación activa, todo evento responde 401 (fail-closed). La URL de la app debe ser HTTPS pública
  (`https://<PUBLIC_HOST>/chat/events`), así que depende de HU-006.2.
- **Por qué estaba apagada en el Python** (`GCHAT_VERIFICAR_JWT=false` en `deploy/docker-compose.swarm.yml`):
  **pendiente de confirmar con el equipo.** Lo más probable, por el resto de ese manifiesto, es que se apagara para
  probar mientras el servicio estaba expuesto por HTTP en `10.10.40.31:8091` sin Traefik ni TLS y sin
  `GCHAT_PROJECT_NUMBER` configurado.
- **Riesgo de dejarla apagada:** cualquiera que alcance el endpoint puede enviar eventos falsos. En el backend .NET la
  identidad sale del correo del evento (`UsuariosWeb.email`, ADR-011), así que un evento falso **suplanta a un
  comercial**: ve sus cotizaciones y clientes, genera cotizaciones y PDF a su nombre y dispara correos con su
  remitente por el relay interno. Además consume cuota del proveedor LLM. Con la API en la red corporativa y detrás de
  Traefik el riesgo no desaparece (cualquier equipo interno, o una SSRF, llega al endpoint).
- `GChat__VerificarJwt=false` solo se admite fuera de `Production` para pruebas locales con túnel.

## Anexo A. Stack Python (solo rollback)

Runbook del stack Python (Fase 15), sin cambios de contenido. Se conserva para volver atrás si el backend .NET falla
en producción. **No usarlo para el backend .NET.**

> **Qué corre hoy:** el stack Python desplegado en Dokploy es `deploy/docker-compose.swarm.yml` (proyecto
> `bot-cotizador`, anclado al worker, orquestador en `10.10.40.31:8091` sin TLS, imágenes construidas en el worker con
> `deploy/rebuild-on-worker.sh`). Es el destino del rollback (§4.5 paso 7). Lo que sigue (A.1 a A.9) describe el
> diseño con `infra/stack.staging.yml`/`infra/stack.dokploy.yml`, que **no** se llegó a desplegar.

### A.1 Topología

```
Internet ──TLS──> Traefik ──> bot-orquestador ─┐   (red web/Traefik + red interna)
                                               ├── bot-mcp     (SOLO red interna)
                                               └── bot-db      (SOLO red interna)
```

- **bot-orquestador** — único servicio expuesto (Traefik/TLS): recibe el webhook de
  Chat y sirve `/pdfs/{folio}.pdf`. Habla con el MCP por la red interna.
- **bot-mcp** — las 7 herramientas. **No expuesto a internet** (sin `ports`, sin
  labels de Traefik; solo la red overlay `interna`).
- **bot-db** — SQL Server 2019 con volumen persistente; solo red interna. **Nota
  (HU-002.4, 2026-09-22):** esto describe el manifiesto local (`docker-compose.yml`,
  ya actualizado a `mcr.microsoft.com/mssql/server:2019-latest`). `infra/stack.
  staging.yml`/`infra/stack.dokploy.yml` (el manifiesto real de Dokploy) **todavía
  no se actualizaron** — siguen definiendo Postgres. El manifiesto que corre hoy es
  `deploy/docker-compose.swarm.yml` (Postgres, anclado al worker, `mode: host` 8091).

Manifiesto: [`infra/stack.staging.yml`](infra/stack.staging.yml).

### A.2 Decisión de red (cierre F15)

El Orquestador usa el **runner-puente**: declara las herramientas del MCP al LLM
(agnóstico vía LiteLLM: Gemini/Anthropic/OpenAI según `LLM_MODEL`) y ejecuta cada
`tool_call` contra el **MCP interno por Streamable HTTP con bearer**
(`MCP_URL=http://bot-mcp:8080/mcp/`, `MCP_AUTH_TOKEN`). Se descartó el *MCP
connector directo* porque el puente da control total de auditoría/registro y no
requiere exponer el MCP. En dev/tests `MCP_URL` queda vacío y el runner usa el MCP
**in-process**.

### A.3 Secretos (variables de entorno de Dokploy)

| Variable | Servicio | Descripción |
|---|---|---|
| `MSSQL_SA_PASSWORD` / `MSSQL_DB` | todos | credenciales de la BD (SQL Server, HU-002.4 — reemplaza `POSTGRES_USER/PASSWORD/DB`; usuario fijo `sa`) |
| `MCP_AUTH_TOKEN` | mcp, orquestador | bearer compartido del MCP interno |
| `LLM_MODEL` / `LLM_API_KEY` | orquestador | LLM agnóstico (LiteLLM): proveedor + clave |
| `GCHAT_PROJECT_NUMBER` | orquestador | audience del JWT del webhook |
| `GCHAT_SUPPORT_SPACE` | orquestador | space para alertas de error |
| `GOOGLE_SA_JSON` | mcp, orquestador | ruta al service account (Drive lectura + Chat publicar) |
| `SMTP_HOST/PORT/USER/PASS/FROM` | mcp | envío de la cotización |
| `PDF_BASE_URL` | mcp, orquestador | base pública de los links a PDF |
| `PUBLIC_HOST` | orquestador (label) | dominio del webhook/PDFs |
| `REGISTRY` / `TAG` | build | registro e imagen |

El `GOOGLE_SA_JSON` se monta como *secret*/config de Dokploy en la ruta indicada.

### A.4 Google Cloud (una sola vez)

1. Habilitar **Google Chat API** en el proyecto.
2. Crear la **app de Chat** → conexión por **HTTP endpoint** apuntando a
   `https://<PUBLIC_HOST>/chat/events`. Anotar el **número de proyecto**
   (`GCHAT_PROJECT_NUMBER`, audience del JWT).
3. Crear un **service account** con **delegación de dominio** y scopes:
   - `https://www.googleapis.com/auth/chat.bot` (publicar en Chat),
   - `https://www.googleapis.com/auth/drive.readonly` (leer el tarifario en Drive).
   Descargar el JSON y cargarlo como secreto (`GOOGLE_SA_JSON`).
4. Crear un **space de soporte** e invitar al bot → su `name` va en `GCHAT_SUPPORT_SPACE`.

### A.5 Desplegar

```bash
# Build & push de las imágenes (o dejar que Dokploy las construya del repo).
docker build -t $REGISTRY/bot-cotizador-mcp:$TAG          -f mcp_server/Dockerfile .
docker build -t $REGISTRY/bot-cotizador-orquestador:$TAG  -f orquestador/Dockerfile .
docker push $REGISTRY/bot-cotizador-mcp:$TAG
docker push $REGISTRY/bot-cotizador-orquestador:$TAG
```

En Dokploy: crear el stack desde `infra/stack.staging.yml`, cargar las variables
del §A.3 y desplegar. Las **migraciones corren solas**: el entrypoint del orquestador
ejecuta `alembic upgrade head` antes de arrancar (idempotente), incluida la vista
`v_ajustes_margen_por_comercial` para Gerencia (F15).

En el clúster actual se usa `deploy/docker-compose.swarm.yml` con imágenes construidas
en el worker por `deploy/rebuild-on-worker.sh` y "Redeploy" en Dokploy.

### A.6 Verificaciones de humo (DoD F15)

1. **Health tras Traefik:** `curl https://<PUBLIC_HOST>/health` → `200 {"status":"ok"}`.
   Readiness (BD): `curl https://<PUBLIC_HOST>/health/ready` → `200 {"status":"ready"}`.
2. **MCP no alcanzable desde internet:** no debe existir ruta pública a `bot-mcp`.
   Verificar que `bot-mcp` no está en la red `web` (`docker service inspect` no
   muestra labels de Traefik ni puertos publicados). Un `curl` externo al MCP debe fallar.
3. **Webhook con evento firmado real:** postear un mensaje en el space de pruebas de
   Chat; el bot responde con la card de previsualización en el hilo (ACK < 30 s).
4. **Piloto E2E:** 2–3 comerciales cotizan de punta a punta (previsualización →
   ajuste → PDF → envío). Verificar trazabilidad en BD (§A.7).

### A.7 Trazabilidad para Gerencia

Vista de solo lectura (HU-002.4: `dbo_cotifly.vAjustesMargenPorComercial` en SQL
Server, ver `db/migrations_mssql/0001_script_bd_cotifly_sqlserver.sql` — equivalente a la
`v_ajustes_margen_por_comercial` de la migración 0003 de Postgres):

```sql
SELECT * FROM dbo_cotifly.vAjustesMargenPorComercial
ORDER BY numAjustes DESC;   -- ajustes de margen por comercial y línea
```

Alertas de error del worker: se publican en `GCHAT_SUPPORT_SPACE` automáticamente.

### A.8 Backup / restore de bot-db

```bash
# Backup (programar como tarea diaria de Dokploy o cron del host):
DB_HOST=bot-db BACKUP_DIR=/backups sh scripts/backup_db.sh

# Restore desde un backup:
DB_HOST=bot-db sh scripts/restore_db.sh /backups/botcotizador-YYYYmmdd-HHMMSS.bak
```

Ambos scripts leen `MSSQL_SA_PASSWORD`/`MSSQL_DB` del entorno del servicio db
(HU-002.4 — reemplaza `POSTGRES_USER/PASSWORD/DB`, usuario fijo `sa`). El restore
requiere que nadie tenga la base abierta — ver la nota de `SINGLE_USER` dentro de
`scripts/restore_db.sh`. Aplican solo al SQL Server en contenedor del manifiesto local;
no sirven para la instancia corporativa (`sa` no está disponible ahí).

### A.9 Rollback

- Redeploy de la imagen anterior (`TAG` previo) desde Dokploy.
- Las migraciones son aditivas; si una versión introduce una migración incompatible,
  usar `alembic downgrade <rev>` desde un contenedor del orquestador antes de bajar imagen.
- **Volver del backend .NET al Python** (redesplegar este stack, reapuntar la app de Google Chat, escalar a 0 el
  servicio .NET, datos de SQL Server no visibles para el Python): ver §4.5, paso 7.
