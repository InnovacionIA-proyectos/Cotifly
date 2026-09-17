# Despliegue en Dokploy Swarm — Bot Cotizador (Fase 15)

Runbook para llevar el servicio a **staging** y endurecerlo. Cubre los 3 servicios,
los secretos, el aprovisionamiento en Google Cloud, las pruebas de humo y el
backup/restore. La topología replica el C4-deployment (§9 de la arquitectura).

## 1. Topología

```
Internet ──TLS──> Traefik ──> bot-orquestador ─┐   (red web/Traefik + red interna)
                                               ├── bot-mcp     (SOLO red interna)
                                               └── bot-db      (SOLO red interna)
```

- **bot-orquestador** — único servicio expuesto (Traefik/TLS): recibe el webhook de
  Chat y sirve `/pdfs/{folio}.pdf`. Habla con el MCP por la red interna.
- **bot-mcp** — las 7 herramientas. **No expuesto a internet** (sin `ports`, sin
  labels de Traefik; solo la red overlay `interna`).
- **bot-db** — Postgres con volumen persistente; solo red interna.

Manifiesto: [`infra/stack.staging.yml`](infra/stack.staging.yml).

## 2. Decisión de red (cierre F15)

El Orquestador usa el **runner-puente**: declara las herramientas del MCP al LLM
(agnóstico vía LiteLLM: Gemini/Anthropic/OpenAI según `LLM_MODEL`) y ejecuta cada
`tool_call` contra el **MCP interno por Streamable HTTP con bearer**
(`MCP_URL=http://bot-mcp:8080/mcp/`, `MCP_AUTH_TOKEN`). Se descartó el *MCP
connector directo* porque el puente da control total de auditoría/registro y no
requiere exponer el MCP. En dev/tests `MCP_URL` queda vacío y el runner usa el MCP
**in-process**.

## 3. Secretos (variables de entorno de Dokploy)

Nunca viven en el repo. En Dokploy se cargan como *environment* del stack:

| Variable | Servicio | Descripción |
|---|---|---|
| `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB` | todos | credenciales de la BD |
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

## 4. Google Cloud (una sola vez)

1. Habilitar **Google Chat API** en el proyecto.
2. Crear la **app de Chat** → conexión por **HTTP endpoint** apuntando a
   `https://<PUBLIC_HOST>/chat/events`. Anotar el **número de proyecto**
   (`GCHAT_PROJECT_NUMBER`, audience del JWT).
3. Crear un **service account** con **delegación de dominio** y scopes:
   - `https://www.googleapis.com/auth/chat.bot` (publicar en Chat),
   - `https://www.googleapis.com/auth/drive.readonly` (leer el tarifario en Drive).
   Descargar el JSON y cargarlo como secreto (`GOOGLE_SA_JSON`).
4. Crear un **space de soporte** e invitar al bot → su `name` va en `GCHAT_SUPPORT_SPACE`.

## 5. Desplegar

```bash
# Build & push de las imágenes (o dejar que Dokploy las construya del repo).
docker build -t $REGISTRY/bot-cotizador-mcp:$TAG          -f mcp_server/Dockerfile .
docker build -t $REGISTRY/bot-cotizador-orquestador:$TAG  -f orquestador/Dockerfile .
docker push $REGISTRY/bot-cotizador-mcp:$TAG
docker push $REGISTRY/bot-cotizador-orquestador:$TAG
```

En Dokploy: crear el stack desde `infra/stack.staging.yml`, cargar las variables
del §3 y desplegar. Las **migraciones corren solas**: el entrypoint del orquestador
ejecuta `alembic upgrade head` antes de arrancar (idempotente), incluida la vista
`v_ajustes_margen_por_comercial` para Gerencia (F15).

## 6. Verificaciones de humo (DoD F15)

1. **Health tras Traefik:** `curl https://<PUBLIC_HOST>/health` → `200 {"status":"ok"}`.
   Readiness (BD): `curl https://<PUBLIC_HOST>/health/ready` → `200 {"status":"ready"}`.
2. **MCP no alcanzable desde internet:** no debe existir ruta pública a `bot-mcp`.
   Verificar que `bot-mcp` no está en la red `web` (`docker service inspect` no
   muestra labels de Traefik ni puertos publicados). Un `curl` externo al MCP debe fallar.
3. **Webhook con evento firmado real:** postear un mensaje en el space de pruebas de
   Chat; el bot responde con la card de previsualización en el hilo (ACK < 30 s).
4. **Piloto E2E:** 2–3 comerciales cotizan de punta a punta (previsualización →
   ajuste → PDF → envío). Verificar trazabilidad en BD (§7).

## 7. Trazabilidad para Gerencia

Vista de solo lectura creada por la migración 0003:

```sql
SELECT * FROM v_ajustes_margen_por_comercial
ORDER BY num_ajustes DESC;   -- ajustes de margen por comercial y línea
```

Alertas de error del worker: se publican en `GCHAT_SUPPORT_SPACE` automáticamente.

## 8. Backup / restore de bot-db

```bash
# Backup (programar como tarea diaria de Dokploy o cron del host):
DB_HOST=bot-db BACKUP_DIR=/backups sh scripts/backup_db.sh

# Restore desde un dump:
DB_HOST=bot-db sh scripts/restore_db.sh /backups/botcotizador-YYYYmmdd-HHMMSS.dump
```

Ambos scripts leen `POSTGRES_USER/PASSWORD/DB` del entorno del servicio db.

## 9. Rollback

- Redeploy de la imagen anterior (`TAG` previo) desde Dokploy.
- Las migraciones son aditivas; si una versión introduce una migración incompatible,
  usar `alembic downgrade <rev>` desde un contenedor del orquestador antes de bajar imagen.
