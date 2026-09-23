"""Configuración central del Bot Cotizador.

Todas las variables se leen del entorno (o de un archivo .env en desarrollo).
Ver `.env.example` para el listado completo. Ninguna credencial vive en el repo.

Las integraciones externas (SAP OData en vivo, API Fletes) quedan como
configuración desactivada por defecto: en esta etapa se trabaja sobre archivos
planos y el export JSON de SAP. Ver PLAN-DESARROLLO-BOT-COTIZADOR.md (F3A, F4).
"""

from __future__ import annotations

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    # --- General ---
    entorno: str = "local"  # local | staging | prod
    log_level: str = "INFO"

    # --- Base de datos (Fase 1) ---
    database_url: str = "postgresql+psycopg://bot:bot@localhost:5432/botcotizador"

    # --- SQL Server 2019 corporativo (HU-002.4) ---
    # Solo para el script de creación del esquema objetivo contra `dbTarifario_Nexo_
    # Pruebas` (host `tbsdatacenter`, schema `dbo_cotifly`) — no reemplaza `database_url`
    # (Postgres sigue siendo la BD viva de la app hasta el corte real). Windows
    # Authentication (`trusted_connection=yes`, sin usuario/contraseña) — por eso este
    # script se corre directo desde Windows (sqlcmd -E o Python local), no desde los
    # contenedores Docker (Linux), donde Windows Auth no funciona sin Kerberos aparte.
    # Vacío por defecto: cada quien lo llena en su propio `.env`, nunca se comitea con
    # un valor real.
    sqlserver_database_url: str = ""

    # --- MCP Server (Fase 2) ---
    mcp_host: str = "0.0.0.0"
    mcp_port: int = 8080
    mcp_auth_token: str = "cambiar-en-despliegue"
    # URL del MCP interno para el runner-puente (F11/F15). Vacío = MCP in-process
    # (dev/tests); en el despliegue con contenedores separados se apunta al overlay
    # interno, p. ej. http://bot-mcp:8080/mcp/. El MCP nunca se expone a internet.
    mcp_url: str = ""

    # --- Orquestador (Fase 9) ---
    orquestador_host: str = "0.0.0.0"
    orquestador_port: int = 8000
    # Storage de PDFs: "volumen" (archivos locales, default) o "s3" (MinIO/S3).
    pdf_storage_modo: str = "volumen"  # volumen | s3
    pdf_storage_dir: str = "/data/pdfs"
    # --- S3/MinIO (pdf_storage_modo=s3) ---
    s3_endpoint: str = "minio:9000"
    s3_access_key: str = ""
    s3_secret_key: str = ""
    s3_bucket: str = "bot-cotizador-pdfs"
    s3_secure: bool = False  # True si el endpoint va por HTTPS
    # Base pública para construir pdf_url (el Orquestador sirve /pdfs/{folio}.pdf).
    pdf_base_url: str = "https://bot-cotizador.boterosoto.local"
    # Token bearer para los endpoints internos de soporte (GET /sesiones/...).
    # Vacío = endpoints internos deshabilitados (responden 404).
    internal_api_token: str = ""
    # Secreto para firmar el token de sesión del front web (login del comercial).
    web_session_secret: str = "dev-secret-cambia"
    # Cada cuántos segundos el barrido archiva sesiones inactivas (0 = desactivado).
    sesion_barrido_seg: int = 3600

    # --- Sesiones conversacionales (Fase 10) ---
    sesion_ttl_horas: int = 24  # inactividad tras la cual se archiva la sesión
    sesion_limite_tokens: int = 6000  # ventana deslizante del historial

    # --- LLM (Fase 11) — agnóstico del proveedor vía LiteLLM ---
    # `llm_model` es el string de LiteLLM: "gemini/gemini-2.5-flash",
    # "anthropic/claude-opus-4-8", "openai/gpt-4o", … Cambiar de proveedor es solo
    # cambiar esta variable (y la API key). `llm_api_key` se pasa explícito a LiteLLM;
    # si se deja vacío, LiteLLM toma la del entorno del proveedor (GEMINI_API_KEY, etc.).
    llm_model: str = "gemini/gemini-2.5-flash"
    llm_api_key: str = ""
    llm_max_tokens: int = 4096  # tope de salida por respuesta del agente
    agente_max_iteraciones: int = 12  # cota del loop tool-use (evita bucles infinitos)
    # Parámetros del adaptador del modelo (ajustables por modelo/proveedor): tope de
    # peticiones por minuto al LLM (0 = sin límite; capa gratuita de Gemini ≈10 RPM)
    # y reintentos con backoff ante 429/fallos transitorios. Un adaptador nuevo
    # (otro proveedor) puede fijar sus propios valores al construir el runner.
    llm_rpm: int = 8
    llm_num_retries: int = 4
    # Cadena de respaldo (fallback) del LLM: lista de model strings de LiteLLM
    # separada por comas. Si el modelo principal (`llm_model`) falla o agota su
    # cuota (p. ej. 429), LiteLLM reintenta la MISMA petición con el siguiente de
    # la lista, y así sucesivamente. Ideal con la capa gratuita de OpenRouter para
    # rotar entre varios modelos gratuitos y evitar que la cotización falle.
    # Ej.: "openrouter/google/gemma-4-31b-it:free,openrouter/openai/gpt-oss-20b:free"
    llm_fallbacks: str = ""
    # Claves por proveedor (multi-proveedor con fallbacks). Si alguna está seteada, el
    # adaptador NO fuerza una api_key única: deja que LiteLLM resuelva la clave por
    # proveedor según el prefijo del modelo (openrouter/…, nvidia_nim/…). Así la cadena
    # de fallback puede mezclar proveedores (p. ej. NVIDIA como principal y OpenRouter
    # gratis como respaldo). Se exportan a las env vars que LiteLLM espera.
    openrouter_api_key: str = ""
    nvidia_nim_api_key: str = ""

    # --- SAP TM (Fase 4) ---
    # Por decisión de proyecto se trabaja sobre el export JSON (archivo plano).
    # La conexión OData en vivo se activa después cambiando sap_tm_modo a "odata".
    sap_tm_modo: str = "export_json"  # export_json | odata
    sap_tm_export_path: str = "tests/fixtures/sap/rentab_cliente_muestra.json"
    sap_tm_url: str = ""
    sap_tm_user: str = ""
    sap_tm_pass: str = ""
    sap_sales_org_default: str = "50000005"

    # --- Tarifario (Fase 3A hoy; API Fletes diferida a futuro, Fase 16) ---
    # Provider por línea. "drive_flat_files" es la única fuente activa hoy.
    # Cambiar a "api_fletes" por línea cuando exista el contrato de la API Fletes.
    tarifa_provider_default: str = "drive_flat_files"
    tarifa_cache_ttl_seg: int = 6 * 3600  # 6 horas
    tarifa_local_dir: str = "/data/tarifas"  # volumen con los archivos planos
    google_sa_json: str = ""  # ruta o contenido del service account (Fase 3A)
    # Carpeta raíz de Drive del tarifario temporal (subcarpetas por línea, o todos
    # los archivos juntos). Con esto + google_sa_json el router usa Drive real; si
    # falta, cae a los archivos locales de `tarifa_local_dir`. Override por línea:
    # DRIVE_FOLDER_ID__<Linea>=<folderId>.
    drive_tarifario_folder_id: str = ""
    drive_cache_dir: str = "/data/tarifas_cache"  # descarga local de Drive
    # Fuente real: un ÚNICO Excel (.xlsx/.xlsm) de Drive con hojas por línea
    # (p. ej. "Tarifas Nacionales", "Tarifas_contenedor"). Si está seteado + hay
    # service account, es la fuente activa. El mapeo línea→hoja está en el router
    # (override por env: DRIVE_HOJA__<Linea>=<NombreHoja>).
    drive_tarifario_file_id: str = ""

    # --- API Fletes del Tarifario (Fase 16; contrato aún provisional) ---
    # Con api_fletes_url seteada, el router registra el provider "api_fletes" y
    # "doble_lectura" (Drive principal + API sombra). La migración por línea es
    # solo config: TARIFA_PROVIDER__<Linea>=api_fletes (o =doble_lectura).
    api_fletes_url: str = ""
    api_fletes_token: str = ""
    api_fletes_timeout_seg: int = 10

    # --- Correo (Fase 7) ---
    # Envío de correo desde el bot: **aplazado a una fase posterior** (decisión
    # 2026-07-22, coincide con la skill original: el comercial envía el PDF por su
    # canal). Con False, la herramienta `enviar_correo` no se le expone al LLM.
    correo_habilitado: bool = False
    # Transporte de correo: "smtp_relay" (relay interno de Botero Soto, default),
    # "wscorreos" (SOAP) o "smtp" (SMTP con auth/TLS). En smtp_relay/wscorreos el
    # remitente es el comercial que confirma (confirmado_por).
    correo_modo: str = "smtp_relay"  # smtp_relay | wscorreos | smtp
    # Relay SMTP interno (Microsoft ESMTP, sin AUTH ni STARTTLS; control por red/IP).
    smtp_relay_host: str = "10.10.40.62"
    smtp_relay_port: int = 25
    smtp_relay_from_fallback: str = "notificaciones@boterosoto.com.co"
    # Servicio SOAP corporativo de correo (WSCorreos, red interna).
    wscorreos_url: str = "http://tbsservicios/WSSCVReporteCelular/wscorreos.asmx"
    wscorreos_timeout_seg: int = 20
    # El adjunto se entrega como **ruta en disco**: el MCP escribe el PDF en una
    # carpeta del proyecto en el disco D de tbsservicios y pasa esa ruta al SOAP;
    # el archivo se borra apenas se envía (temporal, para no sobrecargar el server).
    # - _local: carpeta donde el MCP escribe (montaje del recurso compartido, o
    #   la propia D: si el MCP corre en tbsservicios).
    # - _remoto: la misma carpeta como la ve el servidor SOAP (ruta Windows).
    wscorreos_adjunto_dir_local: str = "/data/correo_tmp"
    wscorreos_adjunto_dir_remoto: str = "D:\\bot-cotizador"
    # --- SMTP (alternativa a wscorreos) ---
    smtp_host: str = ""
    smtp_port: int = 587
    smtp_user: str = ""
    smtp_pass: str = ""
    smtp_from: str = "cotizaciones@boterosoto.com.co"

    # --- Sincronización a Google Sheets (tablero Looker Studio) ---
    # Vuelca la trazabilidad de la BD a un libro de Google Sheets para alimentar un
    # tablero en Looker Studio. Reutiliza el service account del tarifario
    # (GOOGLE_SA_JSON); el libro debe compartirse con ese correo como **Editor** y la
    # Google Sheets API estar habilitada en el proyecto GCP (ver GOOGLE-SETUP.md).
    sheets_habilitado: bool = False
    sheets_spreadsheet_id: str = ""  # ID del libro destino (el de la URL /d/<ID>/edit)
    # Cada cuántos segundos corre el volcado periódico (0 = solo bajo demanda).
    sheets_sync_intervalo_seg: int = 900  # 15 min

    # --- Google Chat (Fase 12–13, alertas F15) ---
    gchat_project_number: str = ""  # audience del JWT
    # Verificación del JWT del webhook. Se puede desactivar para pruebas con túnel o
    # cuando la app usa el modelo HTTP nuevo (que no manda el Bearer clásico). En
    # producción con dominio estable conviene dejarlo activo.
    gchat_verificar_jwt: bool = True
    # Space de soporte para alertas de error del worker (vacío = sin alertas).
    gchat_support_space: str = ""


settings = Settings()
