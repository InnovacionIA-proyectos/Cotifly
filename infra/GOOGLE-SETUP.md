# Configuración de Google Cloud — Bot Cotizador

Guía paso a paso para dejar operativas las dos integraciones con Google:

1. **Google Drive** — lectura del tarifario temporal por el MCP Server (Fase 3A).
2. **Google Chat** — canal del comercial hacia el API Orquestador (Fase 12–13).

> Estado de partida: **nada creado**. Todo lo de abajo se hace una sola vez.
> Ambas integraciones comparten **el mismo proyecto de Google Cloud** y **la misma
> service account**.

---

## 0. Proyecto de Google Cloud

1. [console.cloud.google.com](https://console.cloud.google.com) → crea (o elige) un proyecto, p. ej. `bot-cotizador`.
2. Anota el **número de proyecto** (Project *number*, no el ID): `IAM & Admin → Settings`.
   Va en `GCHAT_PROJECT_NUMBER` (es el `audience` del JWT que valida el webhook).
3. `APIs & Services → Enabled APIs → + Habilitar APIs` y habilita:
   - **Google Chat API**
   - **Google Drive API**

---

## 1. Service Account (compartida Drive + Chat)

1. `IAM & Admin → Service Accounts → Crear cuenta de servicio`.
   - Nombre: `bot-cotizador-sa`. Sin roles de proyecto (los permisos de Drive se dan
     compartiendo carpetas; los de Chat, en la config de la app).
2. En la cuenta creada → `Claves → Agregar clave → Crear clave nueva → JSON`. Descarga el JSON.
3. Copia el **email** de la SA (algo como `bot-cotizador-sa@<proyecto>.iam.gserviceaccount.com`).
4. Entrega el JSON al servicio como secreto en `GOOGLE_SA_JSON` (ruta al archivo o el
   contenido JSON en línea). **Nunca** se sube al repositorio.

---

## 2. Google Drive — tarifario temporal

### 2.1 Estructura de carpetas
Convención (§6 de la arquitectura). Dos opciones:

- **Carpeta única** (simple): una sola carpeta con todos los archivos de todas las líneas.
  → configura `DRIVE_TARIFARIO_FOLDER_ID`.
- **Carpeta por línea** (recomendado si son muchos archivos): una carpeta por línea.
  → configura `DRIVE_FOLDER_ID__<Linea>` por cada una.

Nombre de archivo (obligatorio para que el provider lo encuentre y elija el más reciente):

```
tarifario_<linea>_<AAAA-MM>.csv        (o .xlsx)
p.ej.  tarifario_nacional_2026-07.csv
       tarifario_importacion_2026-07.xlsx
```

`<linea>` en minúsculas y sin espacios: `nacional`, `distribucion`, `almacenamiento`,
`itr`, `importacion`, `exportacion`, `internacional`.

Encabezados normalizables (el MCP tolera variantes/acentos):
`origen, destino, tipo_vehiculo, tarifa, vigencia` (y opcional `tipo_carga`, `moneda`).

### 2.2 Compartir con la service account
Por **cada** carpeta del tarifario: botón `Compartir` → agrega el **email de la SA**
como **Lector**. No requiere delegación de dominio (es Drive compartido explícitamente).

### 2.3 Obtener el folder ID
Abre la carpeta en Drive; el ID está en la URL: `drive.google.com/drive/folders/<FOLDER_ID>`.

### 2.4 Configuración
```env
GOOGLE_SA_JSON=/secrets/bot-cotizador-sa.json
DRIVE_TARIFARIO_FOLDER_ID=1AbCd...            # opción carpeta única
# o, por línea:
# DRIVE_FOLDER_ID__Nacional=1AbCd...
# DRIVE_FOLDER_ID__Importacion=1WxYz...
```
Con `GOOGLE_SA_JSON` + alguna carpeta configurada, el MCP lee de Drive automáticamente
(caché local de 6 h). Si faltan, cae a los archivos locales de `TARIFA_LOCAL_DIR` sin
romper — útil en dev.

---

## 3. Google Chat — canal del comercial

### 3.1 Tipo de conexión: **URL del extremo HTTP** (NO Apps Script)
En la pantalla *Configuración de conexión* de la app de Chat, elige **"URL del extremo HTTP"**.

- ❌ **Apps Script**: obligaría a reescribir el bot en Google Apps Script y abandonar el
  Orquestador FastAPI + el loop agéntico. No aplica.
- ❌ **Cloud Pub/Sub**: para bots sin endpoint HTTP accesible. Aquí sí lo hay.
- ❌ **Dialogflow**: motor conversacional propio; no usamos, el LLM es Claude.
- ✅ **URL del extremo HTTP**: Google hace `POST` firmado (JWT) a nuestro `/chat/events`.

### 3.2 Configuración de la app de Chat
`Google Chat API → Configuración`:

1. **App name / Avatar / Descripción**: los del bot.
2. **Funciones interactivas**:
   - ✅ *Recibir mensajes 1:1*
   - ✅ *Unirse a espacios y conversaciones grupales* (necesario para hilos de espacio).
3. **Configuración de conexión** → **URL del extremo HTTP** = `https://<host>/chat/events`.
   - En local: la URL del túnel (ver §3.3), p. ej. `https://abcd-1234.trycloudflare.com/chat/events`.
   - En producción: el dominio de Traefik, p. ej. `https://bot-cotizador.boterosoto.com.co/chat/events`.
4. **Activadores**: `MESSAGE` (y opcional `ADDED_TO_SPACE` para un saludo de bienvenida).
5. **Visibilidad**: para el piloto, usuarios/grupos específicos; luego todo el dominio.

### 3.3 Prueba en local con túnel
El webhook exige HTTPS público. Para probar sin desplegar:

```bash
docker compose up --build          # levanta postgres + mcp + orquestador (:8000)
# en otra terminal, un túnel al orquestador:
cloudflared tunnel --url http://localhost:8000
#   → te da https://<algo>.trycloudflare.com   (o usa: ngrok http 8000)
```
Pon esa URL + `/chat/events` en el paso 3.3 anterior. Escribe al bot en un espacio de
prueba: debe responder el ACK y luego publicar la previsualización.

Requisitos para que el turno funcione en local:
- `GCHAT_PROJECT_NUMBER` = número del proyecto (valida el JWT entrante).
- `LLM_API_KEY` = clave del LLM configurado en `LLM_MODEL` (por defecto Gemini;
  el loop agéntico la usa vía LiteLLM).
- `GOOGLE_SA_JSON` = la SA (además de Drive, **publica** las respuestas en Chat).

### 3.4 Publicación de respuestas
El Orquestador publica en el hilo con la **misma service account** (scope `chat.bot`,
implícito al ser la SA de la app de Chat). No hace falta configuración extra: basta con
que `GOOGLE_SA_JSON` sea la SA de esta app.

---

## 4. Tablero de control (Google Sheets → Looker Studio)

El Orquestador vuelca **toda la trazabilidad de la BD** a un libro de Google Sheets,
que es la fuente de datos del tablero en Looker Studio. Reutiliza la **misma service
account** del tarifario.

### 4.1 Habilitar la Google Sheets API
En el proyecto GCP (`innovacion-bot-cotizador`): **APIs y servicios → Biblioteca →
Google Sheets API → Habilitar**. (La Drive API ya habilitada no cubre Sheets.)

### 4.2 Compartir el libro con la service account
Abrir el libro destino y **Compartir** con el correo de la SA como **Editor** (no
Lector: el servicio escribe):

```
bot-cotizador@innovacion-bot-cotizador.iam.gserviceaccount.com
```

El libro actual del tablero es:
`https://docs.google.com/spreadsheets/d/1QLFg0e8Uej1WT4MXMyeIUUk-tZsJooOyf8U9GWB2BI4/edit`

### 4.3 Configuración
En `.env` (ya aplicado en local):

```
SHEETS_HABILITADO=true
SHEETS_SPREADSHEET_ID=1QLFg0e8Uej1WT4MXMyeIUUk-tZsJooOyf8U9GWB2BI4
SHEETS_SYNC_INTERVALO_SEG=900        # volcado automático cada 15 min (0 = solo manual)
INTERNAL_API_TOKEN=<token>           # protege el disparo bajo demanda
```

### 4.4 Cómo se ejecuta
- **Automático:** el `lifespan` del Orquestador corre el volcado cada
  `SHEETS_SYNC_INTERVALO_SEG` (si `SHEETS_HABILITADO=true`).
- **Bajo demanda (HTTP):** `POST /internal/sync-sheets` con
  `Authorization: Bearer <INTERNAL_API_TOKEN>`.
- **Bajo demanda (CLI):** `python -m scripts.sync_sheets [spreadsheet_id]`.

El volcado es **idempotente**: limpia y reescribe cada hoja completa, no acumula.

### 4.5 Hojas generadas
| Hoja | Grano | Uso en el tablero |
|---|---|---|
| `KPIs` | clave/valor | Tarjetas (scorecards): total, efectividad, ticket, utilidad. |
| `Cotizaciones` | 1 fila/cotización | **Tabla de hechos**: dimensiones (línea, cliente, ruta, comercial, mes) + medidas (costo, margen %, precio, utilidad). |
| `Ajustes_Margen` | 1 fila/ajuste | Quién y por qué ajustó el margen. |
| `Correos` | 1 fila/envío | Envíos al cliente (cuando se active el correo). |
| `Actividad_Comercial` | 1 fila/comercial | Ranking de uso por comercial. |
| `Uso_Herramientas` | 1 fila/tool | Volumen, éxito y latencia de cada herramienta del MCP. |

### 4.6 Conectar Looker Studio
1. [lookerstudio.google.com](https://lookerstudio.google.com) → **Crear → Informe**.
2. **Añadir datos → Hojas de cálculo de Google** → elegir el libro y la hoja
   `Cotizaciones` (marcar *"Usar la primera fila como encabezados"*).
3. Repetir *Añadir datos* para las demás hojas que quieras graficar.
4. Al añadir cada fuente, revisar que Looker tipó bien: `Fecha`/`Mes` como fecha,
   las columnas `%`, `Costo`, `Precio Final`, `Utilidad` como número.
5. Gráficos sugeridos: series temporales de cotizaciones por `Mes`, barras por
   `Línea de Negocio`/`Comercial`, tarjetas desde `KPIs`, tabla dinámica de
   efectividad. Los datos se refrescan solos en cada volcado (cada 15 min).

---

## 5. Checklist

- [ ] Proyecto GCP creado; **Chat API**, **Drive API** y **Sheets API** habilitadas.
- [ ] Service account creada; JSON descargado → `GOOGLE_SA_JSON`.
- [ ] Carpeta(s) del tarifario creadas, con archivos `tarifario_<linea>_<AAAA-MM>.csv`.
- [ ] Carpeta(s) compartidas con el email de la SA como **Lector**.
- [ ] Libro del tablero compartido con la SA como **Editor**; `SHEETS_SPREADSHEET_ID` y `SHEETS_HABILITADO=true` configurados.
- [ ] `DRIVE_TARIFARIO_FOLDER_ID` (o `DRIVE_FOLDER_ID__<Linea>`) configurados.
- [ ] App de Chat con **URL del extremo HTTP** apuntando a `/chat/events`.
- [ ] `GCHAT_PROJECT_NUMBER` configurado.
- [ ] Prueba local con túnel: el bot responde en un espacio de prueba.
