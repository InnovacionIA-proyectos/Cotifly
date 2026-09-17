"""Tests puros: almacén de PDFs (volumen y S3 con doble), auth web y rate limiter."""

from __future__ import annotations

import asyncio
from pathlib import Path

import pytest

from orquestador.agente import LimitadorRPM
from orquestador.web_auth import TokenInvalido, crear_token, es_correo_valido, verificar_token
from shared.almacen_pdf import AlmacenS3, AlmacenVolumen

# --- Almacén: volumen --------------------------------------------------------


def test_almacen_volumen_guarda_y_lee(tmp_path: Path) -> None:
    a = AlmacenVolumen(tmp_path)
    ref = a.guardar("PCN-1.pdf", b"%PDF-1.4 x")
    assert ref.endswith("PCN-1.pdf")
    assert a.leer("PCN-1.pdf") == b"%PDF-1.4 x"
    assert a.leer("PCN-NO.pdf") is None


def test_almacen_volumen_rechaza_nombre_malicioso(tmp_path: Path) -> None:
    a = AlmacenVolumen(tmp_path)
    with pytest.raises(ValueError):
        a.guardar("../escape.pdf", b"x")


# --- Almacén: S3 con cliente en memoria (sin red) ----------------------------


class _MinioFake:
    """Doble mínimo del cliente minio.Minio."""

    def __init__(self) -> None:
        self.objetos: dict[tuple[str, str], bytes] = {}
        self.buckets: set[str] = set()

    def bucket_exists(self, b: str) -> bool:
        return b in self.buckets

    def make_bucket(self, b: str) -> None:
        self.buckets.add(b)

    def put_object(self, b, nombre, data, length, content_type=None):  # noqa: ANN001
        self.objetos[(b, nombre)] = data.read()

    def get_object(self, b, nombre):  # noqa: ANN001
        if (b, nombre) not in self.objetos:
            raise KeyError("no existe")
        datos = self.objetos[(b, nombre)]

        class _R:
            def read(self_inner) -> bytes:  # noqa: N805
                return datos

            def close(self_inner) -> None: ...
            def release_conn(self_inner) -> None: ...

        return _R()


def test_almacen_s3_guarda_lee_y_crea_bucket() -> None:
    cli = _MinioFake()
    a = AlmacenS3(bucket="pdfs", cliente=cli)
    ref = a.guardar("PCN-9.pdf", b"%PDF datos")
    assert ref == "s3://pdfs/PCN-9.pdf"
    assert "pdfs" in cli.buckets  # se crea si no existe
    assert a.leer("PCN-9.pdf") == b"%PDF datos"
    assert a.leer("PCN-X.pdf") is None  # objeto inexistente → None


def test_almacen_s3_leer_por_ruta() -> None:
    cli = _MinioFake()
    a = AlmacenS3(bucket="pdfs", cliente=cli)
    a.guardar("PCN-9.pdf", b"abc")
    assert a.leer_por_ruta("s3://pdfs/PCN-9.pdf") == b"abc"


# --- Auth web ----------------------------------------------------------------


def test_correo_valido_solo_dominio_corporativo() -> None:
    assert es_correo_valido("dguerra@boterosoto.com.co")
    assert not es_correo_valido("dguerra@gmail.com")
    assert not es_correo_valido("")


def test_token_ida_y_vuelta() -> None:
    t = crear_token("dguerra@boterosoto.com.co", "Daniel", ahora=1000)
    datos = verificar_token(t, ahora=1000)
    assert datos["email"] == "dguerra@boterosoto.com.co"
    assert datos["nombre"] == "Daniel"


def test_token_expirado() -> None:
    t = crear_token("d@boterosoto.com.co", "D", ahora=0)
    with pytest.raises(TokenInvalido):
        verificar_token(t, ahora=13 * 3600)  # TTL 12 h superado


def test_token_firma_alterada() -> None:
    t = crear_token("d@boterosoto.com.co", "D", ahora=1000)
    crudo, _ = t.rsplit(".", 1)
    with pytest.raises(TokenInvalido):
        verificar_token(crudo + ".firmafalsa", ahora=1000)


# --- Rate limiter ------------------------------------------------------------


def test_limitador_rpm_espacia_las_llamadas() -> None:
    reloj = {"t": 0.0}
    dormido: list[float] = []

    async def _sleep_fake(seg: float) -> None:
        dormido.append(seg)
        reloj["t"] += seg

    lim = LimitadorRPM(rpm=60, reloj=lambda: reloj["t"])  # 1 llamada/seg

    async def main() -> None:
        import orquestador.agente as ag

        orig = asyncio.sleep
        ag.asyncio.sleep = _sleep_fake  # type: ignore[assignment]
        try:
            await lim.esperar()  # primera: sin espera
            await lim.esperar()  # segunda: debe esperar ~1s
        finally:
            ag.asyncio.sleep = orig  # type: ignore[assignment]

    asyncio.run(main())
    assert dormido and abs(dormido[0] - 1.0) < 0.01


def test_limitador_rpm_cero_no_espera() -> None:
    lim = LimitadorRPM(rpm=0)

    async def main() -> None:
        await lim.esperar()
        await lim.esperar()

    asyncio.run(main())  # no lanza ni bloquea
