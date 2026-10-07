# ADR-012 — Integraciones diferidas: Google Sheets y almacenamiento S3

- **Estado:** Pendiente (decide Duban con Gerencia Comercial e infraestructura; ver Pendientes)
- **Fecha:** 2026-09-24
- **Issue / HU:** #13 (HU-003.7). Relacionada con ADR-001 (corte único), ADR-007 (volumen `/data/pdfs`), #11 (HU-003.5, `IClsAlmacenPdf`), HU-006.6 (#30, réplicas y manifiestos), HU-007 (variables) y HU-010 (tablero de métricas).

## Contexto

El Python tiene cuatro integraciones externas. HU-003.7 pide portar las que siguen vigentes y documentar las demás:

| Integración (Python) | Qué hace | Estado en .NET |
|---|---|---|
| SAP TM (`mcp_server/margen/export_fuente.py`, `odata_fuente.py`) | Margen propuesto por cliente desde el export JSON o en vivo por OData | **Portada en #13** (`clsFuenteMargenExportJson`, `clsFuenteMargenOData`, `SapTm:Modo`). La conexión OData real se activa con HU-009 |
| Tarifario en Google Drive (`mcp_server/tarifas/`, `DriveFlatFileProvider`) | Costo del flete desde un Excel o CSV de Drive con cuenta de servicio | **Retirada, no se porta** (decisión del 2026-09-23, HU-005 y HU-003.4): el costeo viene de Nexo. El puerto `IClsTarifaProvider` (que respondía `TARIFA_FUENTE_NO_DISPONIBLE` a la herramienta `buscar_tarifa` del agente legado) se borró en HU-013.9 (#105) |
| Sincronización a Google Sheets (`shared/sheets_sync.py`, `scripts/sync_sheets.py`, `POST /internal/sync-sheets`) | Vuelca cada 15 min la trazabilidad de la base (cotizaciones, ajustes de margen, correos, actividad por comercial, uso de herramientas, KPI) a un libro de Sheets para un tablero de Looker Studio, con la cuenta de servicio de Google | **Diferida** (este ADR) |
| PDF en S3/MinIO (`shared/almacen_pdf.py`, `PDF_STORAGE_MODO=volumen\|s3`) | Guarda los PDF en un volumen local (por defecto) o en un bucket S3/MinIO | **Diferida** (este ADR). #11 crea `IClsAlmacenPdf` con el adaptador de volumen |

HU-003.7 pide confirmar con el equipo si Sheets y S3 siguen vigentes antes de migrarlos. Esa confirmación no existe todavía y ninguna de las dos es necesaria para cotizar.

## Decisión (provisional)

1. **Google Drive (tarifario): no se porta.** Decisión cerrada el 2026-09-23. Tampoco se agregan `GOOGLE_SA_JSON`, `DRIVE_TARIFARIO_*` ni `TARIFA_*` a `.env.example`.
2. **Google Sheets: no se implementa en #13.** No se crea puerto, adaptador, `BackgroundService` ni endpoint `/internal/sync-sheets` hasta que se decida el destino del tablero (ver alternativas). Mientras tanto, el tablero de Looker Studio del Python deja de actualizarse cuando se apague el Python (ADR-001).
3. **Almacenamiento de PDF: se mantiene el volumen** (`/data/pdfs`, ADR-007) como único adaptador, detrás de `IClsAlmacenPdf` (lo crea #11; este issue no lo crea). Un adaptador S3/MinIO se agrega solo si HU-006.6 despliega más de una réplica sin un volumen compartido entre nodos. Los casos de uso no cambian, porque dependen del puerto.

## Alternativas para lo pendiente

**Tablero (Sheets):**

- **A. Looker Studio lee SQL Server directamente** con el conector de SQL Server, sobre las vistas que ya crea el 0001 (`vAjustesMargenPorComercial`, `vActividadPorComercial`) más las que falten (uso de herramientas, KPI), con un login de solo lectura. No hay código que mantener en el backend. Exige que Google llegue a `tbsdatacenter` (IP pública o lista blanca), algo que la política corporativa probablemente no permite. Lo decide el DBA o infraestructura.
- **B. Portar la sincronización a .NET**: un `BackgroundService` con `Google.Apis.Sheets.v4` (Apache-2.0), la misma cuenta de servicio y las mismas hojas. Funciona sin abrir la base a internet, pero agrega un secreto de Google y un proceso periódico a un backend que ya no usa nada de Google (Google Chat, la única excepción, se retiró en HU-013.9, #105).
- **C. Reemplazar Looker Studio por el tablero de HU-010** (métricas por comercial y regional dentro de ClientePro, sobre las mismas vistas). No hay integración externa, pero depende del alcance y del calendario de HU-010. Brechas conocidas:
  - la telemetría de uso de herramientas del agente (volumen, éxito, latencia) y los KPI globales de la hoja `Uso_Herramientas`/`KPIs` no están en el alcance de HU-010;
  - entre el corte (HU-006) y HU-010.3 no hay tablero: el de Looker deja de actualizarse cuando se apaga el Python.

**PDF:**

- **Volumen local** (actual): sirve con una réplica o con un volumen compartido entre nodos (NFS o el volumen distribuido que use Dokploy).
- **S3/MinIO**: necesario si hay varias réplicas en nodos distintos sin volumen compartido. Requiere bucket, credenciales (`S3__*` como secretos, HU-007) y el paquete cliente (AWSSDK.S3 o Minio, ambos Apache-2.0).
- **Base de datos** (`VARBINARY`): descartada. Engorda `dbTarifario_Nexo_Pruebas`, compartida con otros sistemas.

## Consecuencias

- `.env.example` no documenta variables de Sheets, Drive ni S3. Si se aprueba B o S3, se agregan con su HU.
- Mientras este ADR siga pendiente, el backend no necesita secretos de Google (cuenta de servicio) ni de MinIO/S3, y la red de salida del contenedor solo debe llegar a SAP TM (con `odata`) y al proveedor del LLM, además de SQL Server (HU-006.5 y HU-006.6).
- El Dockerfile conserva `/data/pdfs` escribible por `app` (ADR-007).
- Variables del Python para la tabla de HU-007:

| Variable del Python | En .NET | Nota |
|---|---|---|
| `SAP_TM_MODO` | `SapTm__Modo` | **Crítica.** Hoy vacío (política de la línea) o `export_json`; en producción `odata` tras HU-009.2. Vacío en `Production` deja un warning al arrancar |
| `SAP_TM_EXPORT_PATH` | `SapTm__ExportPath` | Solo con `export_json` |
| `SAP_TM_URL`, `SAP_TM_USER`, `SAP_TM_PASS` | `SapTm__Url`, `SapTm__User`, `SapTm__Pass` | Solo con `odata`; `SapTm__Pass` es secreto. No dejar `SapTm__Pass` con un valor de ejemplo si no se usa OData |
| (no existía) | `SapTm__SapClient` | Opcional, 3 dígitos (`sap-client`) |
| `SAP_SALES_ORG_DEFAULT` | `SapTm__SalesOrgDefault` | Por defecto `50000005` |
| `GOOGLE_SA_JSON` | eliminada | Solo la usaban el tarifario de Drive y Sheets. Google Chat (#12) definía su propia variable, retirada en HU-013.9 (#105) |
| `DRIVE_TARIFARIO_FOLDER_ID`, `DRIVE_TARIFARIO_FILE_ID`, `DRIVE_CACHE_DIR`, `DRIVE_FOLDER_ID__*`, `DRIVE_HOJA__*` | eliminadas | Tarifario de Drive retirado |
| `TARIFA_PROVIDER_DEFAULT`, `TARIFA_PROVIDER__*`, `TARIFA_CACHE_TTL_SEG`, `TARIFA_LOCAL_DIR` | eliminadas | El costeo viene de Nexo (HU-005) |
| `SHEETS_HABILITADO`, `SHEETS_SPREADSHEET_ID`, `SHEETS_SYNC_INTERVALO_SEG` | eliminadas | Sheets diferido (este ADR) |
| `PDF_STORAGE_MODO=s3`, `S3_ENDPOINT`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_BUCKET`, `S3_SECURE` | eliminadas | Solo volumen (`Pdf__StorageDir`, #11) |
| `INTERNAL_API_TOKEN` | eliminada | Solo la usaban `sheets_api` (`/internal/sync-sheets`) y `sesiones_api` (`/sesiones/...`), que no se portan (ADR-001) |

## Pendientes

| Pendiente | Dueño |
|---|---|
| Confirmar si Gerencia sigue usando el tablero de Looker Studio y elegir A, B o C | Duban + Gerencia Comercial |
| Si A: confirmar si Google puede llegar a `tbsdatacenter` y crear el login de solo lectura | DBA / infraestructura |
| Definir número de réplicas y tipo de volumen en Swarm/Dokploy; de eso depende volumen o S3 (HU-006.6 propone 1 réplica y volumen local, ADR-013 §3) | Duban + infraestructura (HU-006.6, #30) |
| Si S3: bucket, credenciales y política de retención de los PDF | Infraestructura |
