FROM python:3.12-slim

WORKDIR /app

# Dependencias del sistema para weasyprint (generar_pdf, Fase 6).
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential libpango-1.0-0 libpangocairo-1.0-0 libgdk-pixbuf-2.0-0 \
    libffi-dev shared-mime-info \
    && rm -rf /var/lib/apt/lists/*

COPY pyproject.toml README.md ./
COPY shared ./shared
COPY mcp_server ./mcp_server

RUN pip install --no-cache-dir -e ".[mcp,pdf,drive,s3]"

EXPOSE 8080
CMD ["python", "-m", "mcp_server.server"]
