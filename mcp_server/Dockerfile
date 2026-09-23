FROM python:3.12-slim

WORKDIR /app

# Dependencias del sistema para weasyprint (generar_pdf, Fase 6).
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential libpango-1.0-0 libpangocairo-1.0-0 libgdk-pixbuf-2.0-0 \
    libffi-dev shared-mime-info \
    && rm -rf /var/lib/apt/lists/*

# HU-002.4 (2026-09-22): driver ODBC 17 (no 18 -- exige TLS por defecto, rompe
# contra instancias sin certificado propio, ver docs/ESQUEMA_OBJETIVO_SQLSERVER.md
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

COPY pyproject.toml README.md ./
COPY shared ./shared
COPY mcp_server ./mcp_server

RUN pip install --no-cache-dir -e ".[mcp,pdf,drive,s3]"

EXPOSE 8080
CMD ["python", "-m", "mcp_server.server"]
