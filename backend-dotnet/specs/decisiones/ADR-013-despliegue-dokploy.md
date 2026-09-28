# ADR-013 — Despliegue del backend .NET en Dokploy

- **Estado:** Propuesta (pendiente de confirmar con el equipo de infraestructura los puntos marcados en Pendientes)
- **Fecha:** 2026-09-28
- **Issue / HU:** HU-006: #29 (HU-006.5). Relacionada con ADR-001 (corte único), ADR-007 (solo Dockerfile), ADR-008 (SQL Server corporativo) y ADR-012 (volumen de PDF), y con HU-007.1 (#33).

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

## Alternativas consideradas

- **Todo como secreto de Swarm** con el proveedor *key-per-file* de ASP.NET Core: más aislado (no aparece en
  `docker service inspect`), pero exige un cambio de código y de pruebas. Queda como mejora propuesta.
- **Gestor de secretos externo** (Vault, Azure Key Vault): no hay uno estándar confirmado en la compañía para este
  clúster; se adopta si infraestructura lo define.
- **Archivo `.env` montado desde el worker** (como el Python, `/home/dguerra/bot-cotizador-data/.env`): descartado; ata
  el servicio a un nodo y el archivo queda fuera del control de Dokploy.

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
