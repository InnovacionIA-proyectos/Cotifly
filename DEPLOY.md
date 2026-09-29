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

   Rotar = crear `cotifly_gchat_sa_v2`, cambiar el nombre en la variable del stack y redesplegar; luego
   `docker secret rm cotifly_gchat_sa_v1`.

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
| `ConnectionStrings__Regional` | **sí** | no (planeada) | — | Dokploy | `DWCubosEBSSAP`, aún no conectada |
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
| `Nexo__UrlBase` | no | no | vacío | Dokploy | sin ella la búsqueda de clientes responde `*_NO_DISPONIBLE` |
| `Pdf__StorageModo`, `Pdf__StorageDir` | no | no | `volumen`, `/data/pdfs` | manifiesto | |
| `Pdf__BaseUrl` | no | no (no se valida al arrancar) | vacío | Dokploy | URL pública de la API (`https://<PUBLIC_HOST>`). Vacía, el enlace del PDF queda relativo (`/pdfs/{folio}.pdf`): sirve en la web, pero la tarjeta de Google Chat no lo puede abrir; configurarla si Google Chat está activo |
| `Pdf__ClaveFirmaUrl` | **sí** (≥ 32) | con Google Chat configurado | vacío | Dokploy | `openssl rand -base64 48` |
| `Pdf__MinutosUrlFirmada` | no | no | `15` | manifiesto | |
| `Correo__Habilitado`, `Correo__Modo` | no | no | `false`, `smtp_relay` | Dokploy | transporte real por confirmar (D3) |
| `Correo__SmtpRelay__Host` | no | con el correo habilitado | vacío | Dokploy | relay interno, sin AUTH (control por IP) |
| `Correo__SmtpRelay__Port`, `__FromFallback`, `__TimeoutSeg` | no | no | `25`, `notificaciones@boterosoto.com.co`, `20` | manifiesto | |
| `IA__Proveedor`, `IA__Modelo`, `IA__Endpoint` | no | no (sin proveedor el agente no responde) | vacío | Dokploy | ADR-003 |
| `IA__ApiKey` | **sí** (≥ 16) | según el proveedor (obligatoria con openai, azure_openai, gemini y anthropic); si tiene valor, en Production no puede ser un placeholder ni tener menos de 16 caracteres | vacío | Dokploy | |
| `IA__Fallbacks__N__Proveedor`, `__Modelo`, `__Endpoint` | no | no | vacío | Dokploy | respaldos |
| `IA__Fallbacks__N__ApiKey` | **sí** (≥ 16) | igual que `IA__ApiKey`, por respaldo | vacío | Dokploy | |
| `IA__MaxTokens`, `IA__MaxIteraciones`, `IA__SoportaJsonSchema`, `IA__MinutosSinJsonSchema`, `IA__JsonSchemaEstricto`, `IA__Rpm`, `IA__TimeoutSeg` | no | no | `4096`, `12`, `true`, `30`, `false`, `8`, `60` | manifiesto | |
| `LimiteChat__PeticionesPorVentana`, `LimiteChat__VentanaSegundos` | no | no | `20`, `60` | manifiesto | |
| `Sesiones__LimiteTokens`, `Sesiones__TtlHoras`, `Sesiones__BarridoMinutos`, `Sesiones__EsperaTurnoSegundos` | no | no | `6000`, `24`, `60`, `3` | manifiesto | |
| `GChat__ProjectNumber` | no | no (sin él el webhook responde 401) | vacío | Dokploy | número del proyecto de Google Cloud |
| `GChat__VerificarJwt` | no | `false` no arranca | `true` | manifiesto | siempre `true` en QA y producción |
| `GChat__SaJson` | **sí** | no | vacío | **secreto de Swarm** | `/run/secrets/gchat_sa.json` |
| `GChat__SupportSpace`, `GChat__CapacidadCola` | no | no | vacío, `200` | Dokploy / manifiesto | |
| `GChat__UrlCertificados`, `GChat__ApiBase` | no | no | certificados de `chat@system.gserviceaccount.com`, `https://chat.googleapis.com/v1` | — | solo pruebas: no se cargan en QA ni producción (no están en los manifiestos) |
| `Grovity__BaseUrl`, `Grovity__Usuario` / `Grovity__Clave` | no / **sí** | no (el código no las lee) | — | — | solo si se aprueba Grovity desde el agente (ADR-005) |

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
| `ConnectionStrings__Regional` | sí | DBA (`DWCubosEBSSAP`); planeada | no | DBA | — |
| `Identidad__Esquema` y tablas | no | DBA confirma ubicación real | no | DBA | — |
| `Jwt__SigningKey` | sí | generada por el equipo de despliegue (`openssl rand -base64 48`) | no | Backend | `WEB_SESSION_SECRET` |
| `Cors__OrigenesPermitidos__0` | no | equipo de frontend (dominio de `clientepro-frontend`) | no | Frontend | — |
| `ForwardedHeaders__Habilitado` / `RedesConocidas__0` | no | infraestructura (subred de `dokploy-network`) | no | Infraestructura | — |
| `PUBLIC_HOST` (Traefik) | no | infraestructura (DNS y TLS, HU-006.2) | no | Infraestructura | `PUBLIC_HOST` |
| `Pdf__BaseUrl` | no | `https://<PUBLIC_HOST>` | no | Backend | `PDF_BASE_URL` |
| `Pdf__ClaveFirmaUrl` | sí | generada por el equipo de despliegue | no | Backend | — |
| `IA__Proveedor` / `IA__Modelo` / `IA__Endpoint` | no | negocio (ADR-003) | no | Negocio / Backend | `LLM_MODEL` |
| `IA__ApiKey` (y respaldos) | sí | dueño de la cuenta del proveedor LLM | no | Negocio | `LLM_API_KEY` |
| `GChat__ProjectNumber` | no | Google Cloud (proyecto de la app de Chat) | no | Backend | `GCHAT_PROJECT_NUMBER` |
| `GChat__VerificarJwt` | no | fijo: `true` (HU-006.7, #31) | no | Backend | `GCHAT_VERIFICAR_JWT` (hoy `false`) |
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
    `docker buildx imagetools create -t ...:X.Y.Z ...:sha-<corto>`. Producción corre exactamente los bytes que se
    probaron en staging. Solo si ese commit nunca se publicó por rama, se sube la imagen construida y probada en esa
    corrida, únicamente como `X.Y.Z`.
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
   `/health` 200, `/health/ready` 503 sin base), la escanea con Trivy 0.74.0 (falla con vulnerabilidades HIGH o
   CRITICAL que tengan parche) y, solo en push, guarda esa misma imagen como artefacto.
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
- `--canal qa|main` no se corre desde la rama `QA`/`main`, o HEAD no está subido a `origin/<rama>`;
- la versión (`--version` o el tag `vX.Y.Z` de HEAD) no tiene su tag subido a `origin`, o es final (`X.Y.Z`) y el
  commit no está en `origin/main`;
- el tag `X.Y.Z` ya existe en el registry.

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

### 4.5 Release paso a paso

1. **Staging.** Merge a `QA` → el pipeline (o `publicar-imagen.sh --canal qa`) publica `sha-<corto>`. En Dokploy, stack
   de staging: variable `COTIFLY_TAG=sha-<corto>` → *Deploy*. Verificar `/health`, `/health/ready` y las pruebas de humo
   (HU-006.8).
2. **Release.** Merge `QA` → `main` (el push a `main` publica `sha-<corto>` del commit de `main`); desplegar ese
   `sha-<corto>` en staging si el merge creó un commit nuevo. Luego crear y subir el tag `vX.Y.Z` sobre ese mismo
   commit → `X.Y.Z` se crea sobre el digest de `sha-<corto>`: producción corre los bytes probados en staging.
3. **Producción.** En Dokploy, stack de producción: `COTIFLY_TAG=X.Y.Z` → *Deploy*. Swarm actualiza con `start-first`
   y vuelve atrás solo si el healthcheck falla (manifiesto, HU-006.6).
4. **Verificar** qué imagen corre: `docker service inspect --format '{{.Spec.TaskTemplate.ContainerSpec.Image}}'
   <stack>_cotifly-api` (muestra tag y digest).
5. **Rollback manual:** volver `COTIFLY_TAG` al valor anterior y *Deploy*, o `docker service rollback <stack>_cotifly-api`.

## Anexo A. Stack Python (solo rollback)

Runbook del stack Python (Fase 15), sin cambios de contenido. Se conserva para volver atrás si el backend .NET falla
en producción. **No usarlo para el backend .NET.**

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
