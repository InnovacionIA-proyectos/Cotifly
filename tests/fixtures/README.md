# Fixtures de prueba

Datos de doble (mocks) para probar cada componente sin tocar sistemas reales.

- `sap/rentab_cliente_muestra.json` — subconjunto representativo del export OData
  `ZGW_RENTAB_CLI_SRV/RentabClienteSet` (mismo esquema que el archivo real
  `margen sugerido SAP TM.json`). Incluye un caso por `TrqType` del mapeo §7.2 y
  **dos anómalos** (`-19.2` y `99.9`) para probar la detección de la Fase 4.
- `tarifas/` — CSV del tarifario temporal por línea (se agrega en Fase 3A).
- `cotizacion/` — `ejemplo-solicitud.json` de la skill (se agrega en Fase 6).
- `chat/` — payloads de eventos de Google Chat y JWTs de prueba (Fase 12).
