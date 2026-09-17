FROM python:3.12-slim

WORKDIR /app

# El Orquestador NO renderiza PDFs (eso vive en el MCP); build-essential solo por
# si algún wheel necesita compilarse.
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential libffi-dev \
    && rm -rf /var/lib/apt/lists/*

COPY pyproject.toml README.md alembic.ini ./
COPY shared ./shared
COPY orquestador ./orquestador
COPY migrations ./migrations

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
