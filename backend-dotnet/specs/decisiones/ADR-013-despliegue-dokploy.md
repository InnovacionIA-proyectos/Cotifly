# ADR-013 — Despliegue del backend .NET en Dokploy

- **Estado:** Propuesta (pendiente de confirmar con el equipo de infraestructura los puntos marcados en Pendientes)
- **Nota de estado (HU-013.9, #105):** Google Chat se retiró. Ya no aplican el secreto `cotifly_gchat_sa_v1` (`gchat_sa`), las variables `GChat__*` de los manifiestos ni la verificación del JWT del webhook (§4.2): `POST /chat/events` responde 404. El resto de la decisión sigue vigente.
- **Fecha:** 2026-09-28
- **Issue / HU:** HU-006: #29 (HU-006.5), #28 (HU-006.4), #30 (HU-006.6), #31 (HU-006.7). Relacionada con ADR-001 (corte único), ADR-007 (solo Dockerfile), ADR-008 (SQL Server corporativo) y ADR-012 (volumen de PDF), y con HU-007.1 (#33).

## Contexto

El despliegue Python que corre hoy en el clúster Docker Swarm + Dokploy (manager `10.10.40.17`, worker `10.10.40.31`)
tiene atajos de emergencia (backlog, HU-006): credenciales en texto plano en `deploy/docker-compose.swarm.yml`, imágenes
construidas a mano en el worker con `:latest`, el webhook de Google Chat sin verificar el JWT y backups sin programar.
El backend .NET es una sola imagen (ADR-002, ADR-007) que usa la instancia corporativa SQL Server 2019, no un contenedor
propio (ADR-008). El corte es único y el Python queda en el repositorio para rollback hasta validar producción (ADR-001).

## Decisión

### 1. Secretos (HU-006.5, #29)

1. Los valores sensibles llegan como **variables de entorno de Dokploy** del stack; el manifiesto solo tiene
   `${Variable}`. Aplica a `ConnectionStrings__Cotifly`, `ConnectionStrings__Identidad`, `Jwt__SigningKey`,
   `Pdf__ClaveFirmaUrl`, `IA__ApiKey` (y respaldos) y `SapTm__Pass`.
2. La cuenta de servicio de Google Chat va como **secreto de Docker Swarm** con nombre versionado
   (`cotifly_gchat_sa_v1`), montado en `/run/secrets/gchat_sa.json`; `GChat__SaJson` apunta a esa ruta.
3. MinIO no se mantiene (ADR-012): no hay credenciales S3. Nexo no usa credencial (ADR-005).
4. El listado completo de variables, cuáles son secretas y la plantilla de la tabla de producción para HU-007.1 viven en
   `DEPLOY.md` §2 y §3. Ningún valor real se versiona; la validación de arranque en `Production`
   (`clsValidadorSecretos`) rechaza vacíos y placeholders.
5. No se reescribe el historial de git: la auditoría del 2026-09-28 (`DEPLOY.md` §2.3) no encontró claves reales; las
   credenciales por defecto del stack Python (`bot`/`minioadmin`) se retiran con él.

### 2. Registry y pipeline (HU-006.4, #28)

1. **Registry: GHCR**, `ghcr.io/innovacionia-proyectos/cotifly-backend`, paquete privado de la organización.
2. **Tags:** `sha-<corto>` en cada push de rama, `qa`/`main` como canal móvil, y `X.Y.Z` desde un tag git `vX.Y.Z`
   sobre `main` (o `X.Y.Z-rc.N` desde otra rama, para candidatas en staging). Nunca `latest`. Dokploy despliega un
   tag inmutable en la variable `COTIFLY_TAG` del stack.
   - **Inmutabilidad:** `sha-<corto>` se sube una vez y no se reemplaza; `X.Y.Z` no se reconstruye, se crea sobre el
     digest de `sha-<corto>` con `docker buildx imagetools create --prefer-index=false` (mismo manifiesto, mismo
     digest: producción corre los bytes probados en staging), y una `X.Y.Z` existente no se sobrescribe. En un evento
     de tag solo se publica `X.Y.Z`; si `sha-<corto>` todavía no existe, una versión final falla ("espera a que termine
     la corrida de main") y solo una `-rc.N` sube su propio build. Un publicador por commit (`concurrency`).
   - Los scripts manuales exigen además `--canal` o una versión, HEAD en alguna rama de `origin`, `--canal qa|main`
     desde esa rama con HEAD subido, y el tag de versión de `origin` apuntando a HEAD. Un error del registry que no sea
     "no existe" detiene la publicación.
3. **Pipeline:** el mismo workflow `backend-dotnet.yml`. `imagen-docker` construye, prueba (humo) y escanea (Trivy
   0.74.0 fijado por digest) la imagen; `publicar-imagen` sube esos mismos bytes a GHCR con `GITHUB_TOKEN`
   (`packages: write` solo en ese job), solo en push a `QA`/`main` o tag, y solo si pasaron todos los demás jobs. El
   nombre del tag se valida como semver y los valores del evento llegan a los `run` por `env:`, no con `${{ }}`.
4. **Script manual versionado** `backend-dotnet/scripts/publicar-imagen.sh` (y `.ps1`), mismos tags y controles, desde
   cualquier equipo con Docker. Reemplaza a `deploy/rebuild-on-worker.sh` para el backend .NET; es el camino vigente
   mientras GitHub Actions siga bloqueado por facturación en la organización (2026-09-28).
5. **Pull en el clúster:** PAT clásico de solo `read:packages` de una cuenta de servicio, registrado en Dokploy y con
   `docker login ghcr.io` en el manager (`--with-registry-auth` lo reenvía al worker).
6. `DebugType=none` se mantiene (sin `.pdb` en la imagen).

### 3. Manifiestos (HU-006.6, #30)

1. **Archivos nuevos** `infra/stack.dotnet.staging.yml` e `infra/stack.dotnet.dokploy.yml`; los del Python no se tocan
   (rollback = redesplegar el stack Python).
2. **SQL Server externo** (`tbsdatacenter`, HU-002.4): ningún servicio de base de datos en el stack.
3. **Un servicio**, `cotifly-api`, una réplica, volumen local `/data/pdfs` (resuelve para HU-006 el pendiente de
   réplicas y volumen de ADR-012: volumen, no S3).
4. **Redes:** `dokploy-network` (externa) solo para la entrada desde Traefik; overlay propia `salida` con egreso
   (equivale a `bot-edge`). Sin red interna: no hay servicios internos.
5. **Anclado al worker** `TBSNODO1-DKP` mientras el overlay VXLAN esté caído (HU-006.1). Staging se accede por un
   puerto en modo host (8092, HTTP, `stop-first`); producción no publica puertos y espera a HU-006.2 (TLS por
   Traefik, `start-first`).
6. **Healthcheck** exec de vida (`/health`) en Swarm y de disponibilidad (`/health/ready`) en Traefik;
   `update_config` con `failure_action: rollback`; límites 1 CPU / 1024 MB.
7. **Variables** con `${VAR}` (obligatorias con `${VAR:?}`); `GChat__VerificarJwt=true` fijo en el manifiesto. Las
   de otros frentes llevan valor por defecto (`Trm__Url` vacía impide arrancar; `Trm__TiempoEsperaSegundos`,
   `Nexo__MinutosCacheMunicipios`); la TRM exige salida a `www.datos.gov.co`.
8. **Verificación manual de disponibilidad:** el rollback automático solo mira `/health` (vida); tras cada despliegue se
   comprueba `/health/ready` y, si falla, `docker service rollback`. Rollback al Python documentado (reapuntar la app
   de Google Chat; los datos escritos en SQL Server no son visibles para el Python).
9. **Riesgo bajo aceptado:** `ForwardedHeaders__RedesConocidas` confía en toda la subred compartida de
   `dokploy-network`; otro contenedor de esa red podría falsear `X-Forwarded-For` y saltarse el límite por IP de
   `/api/auth/*`. Propuesta: acotar a la IP o rango de Traefik cuando se conozca.

### 4. Backups y webhook de Google Chat (HU-006.7, #31)

1. **Backups:** el respaldo principal son los backups nativos del DBA de la base compartida (pendiente de confirmar
   frecuencia, retención y recuperación de un esquema). Como complemento opcional, exportación lógica diaria solo de
   `dbo_cotifly` con `bcp` nativo (`scripts/respaldo/exportar-dbo-cotifly.sh`), desde cron en el worker, con un login
   de solo lectura `cotifly_respaldo` (`GRANT SELECT ON SCHEMA::dbo_cotifly`). Retención 7 diarios + 4 semanales en
   `/var/backups/cotifly` (una copia semanal por semana ISO; parámetros validados antes de exportar). Restore solo en
   instancias de pruebas (`restaurar-dbo-cotifly.sh --destino-pruebas`), verificado de punta a punta con SQL Server
   2019 local. Se niega con `tbsdatacenter` (siempre; `COTIFLY_RESPALDO_PROHIBIDOS` solo agrega nombres), por el
   nombre dado o por la identidad que reporta el servidor, y encima del servidor de origen registrado en el respaldo.
   Límite: un CNAME o listener de AG que resuelva a un nodo no listado.
2. **JWT del webhook:** la verificación queda activada (`GChat__VerificarJwt=true`, obligatoria en `Production`), con
   audiencia `GChat__ProjectNumber` (*Authentication Audience* = Project Number en la app de Chat). Apagarla permitiría
   suplantar a cualquier comercial con un evento falso.

## Alternativas consideradas

- **Todo como secreto de Swarm** con el proveedor *key-per-file* de ASP.NET Core: más aislado (no aparece en
  `docker service inspect`), pero exige un cambio de código y de pruebas. Queda como mejora propuesta.
- **Gestor de secretos externo** (Vault, Azure Key Vault): no hay uno estándar confirmado en la compañía para este
  clúster; se adopta si infraestructura lo define.
- **Archivo `.env` montado desde el worker** (como el Python, `/home/dguerra/bot-cotizador-data/.env`): descartado; ata
  el servicio a un nodo y el archivo queda fuera del control de Dokploy.
- **Registry propio en el clúster** (`registry:2` en Swarm) o **Docker Hub**: el primero suma un servicio con estado,
  TLS y backups que mantener; el segundo saca la imagen de la organización de GitHub. GHCR ya autentica con los
  permisos del repositorio.
- **Que Dokploy construya desde git en el nodo** (como hoy): es lo que HU-006 pide eliminar (sin versión, disco del
  nodo, sin pruebas antes de desplegar).
- **Editar `infra/stack.dokploy.yml` / `stack.staging.yml` en el sitio**: más corto, pero el rollback al Python
  obligaría a revertir commits. Se descartó mientras el Python siga vigente.
- **SQL Server como servicio del stack**: descartado por HU-002.4 (la base es corporativa y compartida).
- **Puerto en modo host también en producción**: expondría la API sin TLS; producción espera a HU-006.2.
- **BACPAC con `sqlpackage`** para el respaldo lógico: exporta la base completa (esquemas de otras aplicaciones) y
  pide permisos sobre toda la base. **`BACKUP DATABASE`** desde Cotifly: la base es del DBA y compartida.
- **Tarea programada de Dokploy** para el respaldo: la imagen del backend no tiene shell ni herramientas; el cron del
  worker con un contenedor de mssql-tools18 es más simple.
- **Mantener el JWT apagado con una lista de IPs permitidas**: Google Chat no publica rangos fijos para los webhooks y
  la firma ya resuelve la autenticidad.
- **Reconstruir y volver a subir `sha-<corto>` en el evento de tag**: la primera versión de #28 lo hacía; daba otros
  bytes (fecha de build, etiquetas) bajo un tag que debía ser inmutable. Se reemplazó por `imagetools create`.
- **Reconstruir en el job de publicación** (`docker/build-push-action`): publicaría bytes distintos a los probados; se
  prefirió pasar la imagen probada como artefacto.

## Consecuencias

- Quien tenga acceso al Docker del manager o al panel de Dokploy ve las variables: el acceso se limita al equipo de
  despliegue.
- Un despliegue con valores de ejemplo falla en el arranque (y en el rollout) en vez de quedar corriendo.
- HU-007.1 (#33) llena la tabla de `DEPLOY.md` §3 y carga los valores en Dokploy.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Confirmar el mecanismo de secretos (variables de Dokploy + secreto de Swarm) o el estándar corporativo | Duban con infraestructura |
| Cargar los valores reales en Dokploy (tabla de `DEPLOY.md` §3) | HU-007.1 (#33) |
| Rotar o retirar las credenciales por defecto del stack Python al apagarlo | Duban |
| Resolver la facturación de GitHub Actions de la organización (mientras tanto, script manual) | Administración de la organización de GitHub |
| Cuenta de servicio de GitHub que emite el PAT `read:packages` del clúster, y su renovación | Duban con infraestructura |
| Verificar en el primer despliegue a staging que el worker baja la imagen privada | Duban (HU-006.6) |
| Probar el despliegue completo en staging (Dokploy, secreto, conectividad del worker a `tbsdatacenter:1433`, Traefik) | Duban, con acceso al clúster y aprobación |
| Quitar el puerto en modo host y el anclaje al worker cuando se resuelva el overlay | HU-006.2 |
| Confirmar los backups nativos de `dbTarifario_Nexo_Pruebas` (frecuencia, retención, recuperación de un esquema) y crear el login `cotifly_respaldo` | DBA |
| Copia de los respaldos fuera del worker y alerta cuando el cron falle | Infraestructura |
| Repetir la prueba de restore con un respaldo real en una instancia de pruebas corporativa | Duban con el DBA |
| Confirmar por qué `GCHAT_VERIFICAR_JWT` estaba en `false` en el Python | Equipo (Duban) |
| ¿Staging y producción comparten `dbTarifario_Nexo_Pruebas`/`dbo_cotifly`? | HU-007.1 (#33) con el DBA |
| Acotar `ForwardedHeaders` a la IP o rango de Traefik | Duban con infraestructura (staging) |
