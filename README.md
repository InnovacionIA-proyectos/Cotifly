# Bot Cotizador — MCP Server + API Orquestador

Servicio productivo del Bot Cotizador de Botero Soto. Convierte una solicitud de
cotización de flete (Google Chat) en una cotización en PDF con el margen de la
política aplicado, y la envía al cliente previa confirmación del comercial.

Base de arquitectura: `../ARQUITECTURA-BOT-COTIZADOR.md` (v1.1).
Plan de construcción por fases: `../PLAN-DESARROLLO-BOT-COTIZADOR.md`.

## Estructura

```
bot-cotizador/
  orquestador/     API Orquestador (FastAPI): webhook Chat, sesiones, loop agéntico
  mcp_server/      MCP Server (FastMCP): 7 herramientas de negocio
  shared/          Config y modelos compartidos
  infra/           Manifiestos de despliegue (Dokploy)
  tests/           Pruebas + fixtures
```

> El paquete del MCP se llama `mcp_server` (no `mcp`) para no sombrear al SDK `mcp`/`fastmcp`.

## Decisiones vigentes de esta etapa

- **SAP TM:** se trabaja sobre el **export JSON** (`SAP_TM_MODO=export_json`). La
  conexión OData en vivo se activa después cambiando la configuración.
- **Tarifario:** fuente = **archivos planos en Google Drive**
  (`DriveFlatFileProvider`). La conexión por **API Fletes queda diferida a futuro**.

## Puesta en marcha (local)

```bash
cp .env.example .env          # ajustar valores
docker compose up --build     # levanta postgres + mcp + orquestador
curl http://localhost:8000/health
```

## Desarrollo y pruebas

Requiere **Python 3.12**.

```bash
python -m venv .venv && . .venv/Scripts/activate   # Windows
pip install -e ".[dev]"
ruff check . && mypy shared orquestador mcp_server
pytest
```

Las pruebas contra sistemas reales están marcadas y no corren por defecto:
`pytest -m "not (sap or drive or smtp or llm)"` (comportamiento por defecto de CI).

## Convención del tarifario temporal (Fase 3A)

Una carpeta de Drive por línea de negocio; archivos
`tarifario_<linea>_<AAAA-MM>.csv` con encabezados normalizados
(`origen, destino, tipo_vehiculo, tarifa, vigencia`). El `DriveFlatFileProvider`
cachea localmente con TTL de 6 h.
