FROM python:3.12-slim

WORKDIR /app

# El Orquestador NO renderiza PDFs (eso vive en el MCP); build-essential solo por
# si algún wheel necesita compilarse.
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential libffi-dev \
    && rm -rf /var/lib/apt/lists/*

# HU-002.4 (2026-09-22): driver ODBC 17 (no 18 -- exige TLS por defecto, rompe
# contra instancias sin certificado propio, ver docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md
# §7) + unixodbc-dev, requeridos por pyodbc para hablar con SQL Server. python:3.12-
# slim es Debian 12 (bookworm) al momento de escribir esto -- si la base cambia de
# versión de Debian, ajustar la ruta de packages.microsoft.com/config/debian/.
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl gnupg2 apt-transport-https ca-certificates unixodbc-dev \
    && curl -sSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -sSL https://packages.microsoft.com/config/debian/12/prod.list -o /etc/apt/sources.list.d/mssql-release.list \
    && sed -i 's#deb #deb [signed-by=/usr/share/keyrings/microsoft-prod.gpg] #' /etc/apt/sources.list.d/mssql-release.list \
    && apt-get update \
    && ACCEPT_EULA=Y apt-get install -y --no-install-recommends msodbcsql17 \
    && rm -rf /var/lib/apt/lists/*

COPY pyproject.toml README.md alembic.ini ./
COPY shared ./shared
COPY orquestador ./orquestador
COPY db/migrations ./db/migrations

# llm (Anthropic) + mcpclient (habla con el MCP interno por HTTP) + gchat
# (verifica el JWT del webhook y publica en Chat) + sheets (vuelca la BD a Google
# Sheets para el tablero Looker). Sin las deps del servidor MCP.
COPY scripts ./scripts
RUN pip install --no-cache-dir -e ".[llm,mcpclient,gchat,s3,sheets]" \
    && chmod +x scripts/entrypoint_orquestador.sh

EXPOSE 8000
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:8000/health').status==200 else 1)"

ENTRYPOINT ["./scripts/entrypoint_orquestador.sh"]
