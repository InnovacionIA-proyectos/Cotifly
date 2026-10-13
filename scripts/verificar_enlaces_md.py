#!/usr/bin/env python3
"""Verifica que los enlaces relativos de los documentos Markdown apunten a archivos que existen.

Uso:
    python scripts/verificar_enlaces_md.py [raiz ...]

Sin argumentos revisa todos los `.md` versionados del repositorio (según `git ls-files`).
Con argumentos revisa solo los `.md` bajo esas carpetas o archivos.

Revisa los enlaces `[texto](ruta)` y `[ref]: ruta`. Ignora:
- enlaces externos (`http:`, `https:`, `mailto:`) y anclas solas (`#seccion`);
- lo que está dentro de bloques de código (```) y de código en línea (`...`).

La parte `#ancla` de un enlace relativo se descarta: solo se valida que el archivo
o la carpeta exista. Termina con código 1 si encuentra algún enlace roto, para usarlo en el CI.
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote

RAIZ_REPO = Path(__file__).resolve().parent.parent

# [texto](destino "título opcional"), admitiendo <destino con espacios>.
PATRON_ENLACE = re.compile(r"!?\[[^\]]*\]\(\s*(<[^>]+>|[^)\s]+)(?:\s+\"[^\"]*\")?\s*\)")
# [referencia]: destino
PATRON_REFERENCIA = re.compile(r"^\s{0,3}\[[^\]]+\]:\s*(<[^>]+>|\S+)")
PATRON_CODIGO_EN_LINEA = re.compile(r"(`+).*?\1")
PREFIJOS_EXTERNOS = ("http://", "https://", "mailto:", "ftp://", "tel:", "data:")


def listar_markdown(raices: list[str]) -> list[Path]:
    if not raices:
        salida = subprocess.run(
            ["git", "ls-files", "*.md"], cwd=RAIZ_REPO, capture_output=True, text=True, check=True
        ).stdout
        return [RAIZ_REPO / linea for linea in salida.splitlines() if linea.strip()]
    archivos: list[Path] = []
    for raiz in raices:
        ruta = Path(raiz).resolve()
        if ruta.is_file():
            archivos.append(ruta)
        else:
            archivos.extend(sorted(ruta.rglob("*.md")))
    return archivos


def destinos(texto: str):
    en_bloque = False
    for numero, linea in enumerate(texto.splitlines(), start=1):
        if linea.lstrip().startswith(("```", "~~~")):
            en_bloque = not en_bloque
            continue
        if en_bloque:
            continue
        sin_codigo = PATRON_CODIGO_EN_LINEA.sub("", linea)
        for coincidencia in PATRON_ENLACE.finditer(sin_codigo):
            yield numero, coincidencia.group(1)
        referencia = PATRON_REFERENCIA.match(sin_codigo)
        if referencia:
            yield numero, referencia.group(1)


def es_roto(archivo: Path, destino: str) -> bool:
    destino = destino.strip("<>")
    if destino.startswith(PREFIJOS_EXTERNOS) or destino.startswith("#"):
        return False
    ruta = unquote(destino.split("#", 1)[0].split("?", 1)[0])
    if not ruta:
        return False
    base = RAIZ_REPO if ruta.startswith("/") else archivo.parent
    return not (base / ruta.lstrip("/")).exists()


def main(argumentos: list[str]) -> int:
    rotos = []
    archivos = listar_markdown(argumentos)
    for archivo in archivos:
        texto = archivo.read_text(encoding="utf-8", errors="replace")
        for numero, destino in destinos(texto):
            if es_roto(archivo, destino):
                rotos.append(f"{archivo.relative_to(RAIZ_REPO).as_posix()}:{numero}: {destino}")
    for roto in rotos:
        print(roto)
    print(f"{len(archivos)} documentos revisados, {len(rotos)} enlaces rotos.")
    return 1 if rotos else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
