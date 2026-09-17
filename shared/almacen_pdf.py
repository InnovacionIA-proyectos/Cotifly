"""Almacenamiento de PDFs conmutables: volumen local ↔ bucket S3/MinIO (§9 fase 2).

El MCP **guarda** el PDF y el Orquestador lo **lee** para servirlo en `/pdfs/...`
(streaming: la URL pública no cambia entre backends y el visor embebido del front
sigue siendo same-origin). La selección es por configuración:

- `PDF_STORAGE_MODO=volumen` (default): archivos en `PDF_STORAGE_DIR` (hoy).
- `PDF_STORAGE_MODO=s3`: bucket S3/MinIO (`S3_ENDPOINT`, `S3_ACCESS_KEY`,
  `S3_SECRET_KEY`, `S3_BUCKET`, `S3_SECURE`). Requiere el extra `[s3]` (minio).

`pdf_path` en la BD guarda la referencia del backend (`/data/pdfs/x.pdf` o
`s3://bucket/x.pdf`); `leer_por_ruta` resuelve ambas para compatibilidad.
"""

from __future__ import annotations

import re
from abc import ABC, abstractmethod
from pathlib import Path
from typing import Any

from shared.config import settings

_NOMBRE_RE = re.compile(r"^[A-Za-z0-9_-]+\.pdf$")


class AlmacenPdf(ABC):
    """Backend de almacenamiento de los PDFs de cotización."""

    @abstractmethod
    def guardar(self, nombre: str, datos: bytes) -> str:
        """Guarda el PDF y devuelve la referencia persistente (`pdf_path`)."""

    @abstractmethod
    def leer(self, nombre: str) -> bytes | None:
        """Devuelve los bytes del PDF, o None si no existe."""

    def leer_por_ruta(self, pdf_path: str) -> bytes | None:
        """Lee a partir de la referencia guardada en BD (ruta local o s3://...)."""
        # Compatibilidad: si la referencia es un archivo local existente, se lee
        # directo (cotizaciones antiguas en volumen aunque el modo actual sea s3).
        ruta = Path(pdf_path)
        if not pdf_path.startswith("s3://") and ruta.is_file():
            return ruta.read_bytes()
        return self.leer(Path(pdf_path).name)


def _validar_nombre(nombre: str) -> None:
    if not _NOMBRE_RE.match(nombre):
        raise ValueError(f"Nombre de PDF inválido: {nombre!r}")


class AlmacenVolumen(AlmacenPdf):
    """Backend por defecto: el volumen compartido del contenedor."""

    def __init__(self, directorio: str | Path | None = None) -> None:
        self._dir = Path(directorio or settings.pdf_storage_dir)

    def guardar(self, nombre: str, datos: bytes) -> str:
        _validar_nombre(nombre)
        self._dir.mkdir(parents=True, exist_ok=True)
        ruta = self._dir / nombre
        ruta.write_bytes(datos)
        return str(ruta)

    def leer(self, nombre: str) -> bytes | None:
        _validar_nombre(nombre)
        base = self._dir.resolve()
        ruta = (base / nombre).resolve()
        if base != ruta.parent or not ruta.is_file():
            return None
        return ruta.read_bytes()


class AlmacenS3(AlmacenPdf):
    """Backend S3/MinIO. El cliente es inyectable para pruebas sin red.

    `cliente` debe exponer la interfaz del SDK `minio.Minio` (`bucket_exists`,
    `make_bucket`, `put_object`, `get_object`).
    """

    def __init__(
        self,
        bucket: str | None = None,
        cliente: Any | None = None,
    ) -> None:
        self._bucket = bucket or settings.s3_bucket
        self._cliente = cliente
        self._bucket_listo = False

    def _cli(self) -> Any:
        if self._cliente is None:  # pragma: no cover - requiere extra [s3] + red
            try:
                from minio import Minio
            except ImportError as e:
                raise RuntimeError(
                    'El storage S3 requiere el extra: pip install -e ".[s3]"'
                ) from e
            self._cliente = Minio(
                settings.s3_endpoint,
                access_key=settings.s3_access_key,
                secret_key=settings.s3_secret_key,
                secure=settings.s3_secure,
            )
        return self._cliente

    def _asegurar_bucket(self) -> None:
        if self._bucket_listo:
            return
        cli = self._cli()
        if not cli.bucket_exists(self._bucket):
            cli.make_bucket(self._bucket)
        self._bucket_listo = True

    def guardar(self, nombre: str, datos: bytes) -> str:
        _validar_nombre(nombre)
        import io

        self._asegurar_bucket()
        self._cli().put_object(
            self._bucket, nombre, io.BytesIO(datos), length=len(datos),
            content_type="application/pdf",
        )
        return f"s3://{self._bucket}/{nombre}"

    def leer(self, nombre: str) -> bytes | None:
        _validar_nombre(nombre)
        try:
            self._asegurar_bucket()
            resp = self._cli().get_object(self._bucket, nombre)
            try:
                return resp.read()
            finally:
                resp.close()
                resp.release_conn()
        except Exception:  # noqa: BLE001 - objeto inexistente, bucket o red caída → None
            return None


def construir_almacen() -> AlmacenPdf:
    """Fábrica según configuración: `s3` si está configurado, si no volumen local."""
    if settings.pdf_storage_modo == "s3":
        return AlmacenS3()
    return AlmacenVolumen()
