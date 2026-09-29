# ADR-013 — Despliegue del backend .NET en Dokploy

- **Estado:** Propuesta (pendiente de confirmar con el equipo de infraestructura los puntos marcados en Pendientes)
- **Fecha:** 2026-09-28
- **Issue / HU:** HU-006: #29 (HU-006.5), #28 (HU-006.4). Relacionada con ADR-001 (corte único), ADR-007 (solo Dockerfile), ADR-008 (SQL Server corporativo) y ADR-012 (volumen de PDF), y con HU-007.1 (#33).

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
