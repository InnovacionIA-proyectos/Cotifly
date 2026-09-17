"""Contratos y utilidades base de la capa de tarifas (§5.1 y §6).

Aquí viven:
- Los modelos Pydantic de entrada/salida de `buscar_tarifa` (el contrato).
- La interfaz `TarifaProvider` (Strategy) y las excepciones de la capa.
- La normalización de texto y de columnas "sucias" del tarifario.
- El `RepositorioArchivos` (de dónde salen los archivos) y una implementación
  local (`LocalRepositorioArchivos`) que sirve para el volumen del contenedor y
  para las pruebas sin tocar Drive.
"""

from __future__ import annotations

import csv
import io
import re
import unicodedata
from abc import ABC, abstractmethod
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from pydantic import BaseModel, Field

# --- Contrato de la herramienta (§5.1) --------------------------------------

_LINEAS = (
    "Nacional",
    "Distribucion",
    "Almacenamiento",
    "Almacenamiento3PL",
    "ITR",
    "Importacion",
    "Exportacion",
    "Internacional",
)


class EntradaBuscarTarifa(BaseModel):
    """Entrada de `buscar_tarifa`."""

    linea_negocio: str
    origen: str
    destino: str
    tipo_vehiculo: str | None = None
    tipo_carga: str | None = None
    peso_kg: float | None = None  # peso de la carga (filtra por rango de la tarifa)


class Candidata(BaseModel):
    """Fila candidata que el LLM usa para desambiguar o para captura manual."""

    origen: str
    destino: str
    tipo_vehiculo: str | None = None
    tipo_carga: str | None = None
    clase_contenedor: str | None = None
    peso_inf: float | None = None
    peso_sup: float | None = None
    peso_limite: float | None = None
    tarifa: float
    moneda: str = "COP"
    vigencia: str | None = None
    detalles: str | None = None


class ResultadoTarifa(BaseModel):
    """Salida de `buscar_tarifa` (contrato §5.1)."""

    encontrada: bool
    costo: float | None = None
    moneda: str = "COP"
    fuente: str
    archivo_o_referencia: str | None = None
    candidatas: list[Candidata] = Field(default_factory=list)


# --- Excepciones de la capa --------------------------------------------------


class FuenteTarifaNoDisponible(Exception):
    """No se pudo obtener/leer el tarifario de una línea (Drive caído, sin archivo…)."""


# --- Normalización -----------------------------------------------------------


def normalizar(texto: str | None) -> str:
    """Normaliza para comparar: sin acentos, mayúsculas, espacios colapsados."""
    if texto is None:
        return ""
    sin_acentos = "".join(
        c for c in unicodedata.normalize("NFKD", str(texto)) if not unicodedata.combining(c)
    )
    return re.sub(r"\s+", " ", sin_acentos).strip().upper()


# Alias de encabezados → columna canónica (claves ya normalizadas). Incluye tanto
# el CSV simple (origen/destino/tarifa…) como el export masivo del tarifario real
# (ciudadOrigen, tipoVehiculo, pesoInf/pesoSup/pesoLimite, valor, detallesTarifa…).
_ALIAS_COLUMNAS: dict[str, str] = {}
for _canon, _aliases in {
    "origen": ["origen", "ciudad origen", "origen ciudad", "ciudadOrigen"],
    "destino": ["destino", "ciudad destino", "destino ciudad", "ciudadDestino"],
    "tipo_vehiculo": [
        "tipo vehiculo", "vehiculo", "vehiculo y peso", "tipo de vehiculo", "tipoVehiculo"
    ],
    "tipo_carga": ["tipo carga", "carga", "tipo de carga", "claseViaje"],
    "clase_contenedor": ["claseContenedor", "clase contenedor"],
    "tipo_carroceria": ["tipoCarroceria", "carroceria"],
    "tarifa": ["tarifa", "valor", "costo", "flete", "valor flete"],
    "moneda": ["moneda"],
    "vigencia": ["vigencia", "vigente hasta", "mes"],
    "peso_inf": ["pesoInf", "peso inf", "peso minimo"],
    "peso_sup": ["pesoSup", "peso sup", "peso maximo"],
    "peso_limite": ["pesoLimite", "peso limite"],
    "linea_fuente": ["lineaNegocio", "linea negocio"],
    "detalles": ["detallesTarifa", "detalles", "nota", "notas", "observaciones"],
}.items():
    for _a in _aliases:
        _ALIAS_COLUMNAS[normalizar(_a)] = _canon

# Columnas canónicas que son numéricas (tarifa y rangos de peso).
_COLUMNAS_NUMERICAS = {"tarifa", "peso_inf", "peso_sup", "peso_limite"}


def _parse_num(valor: Any) -> float | None:
    """Convierte a número tolerante: 2850000.0, '2.850.000', '$ 2850000', 4000 → float.

    Un número (int/float) se respeta tal cual (no se le quita el punto decimal); un
    texto se limpia de separadores de miles y símbolos. None si no hay dígitos.
    """
    if valor is None:
        return None
    if isinstance(valor, bool):
        return None
    if isinstance(valor, (int, float)):
        return float(valor)
    limpio = re.sub(r"[^\d]", "", str(valor))
    return float(limpio) if limpio else None


# Alias retrocompatible (algunas pruebas/otros módulos importan _parse_tarifa).
_parse_tarifa = _parse_num


def normalizar_fila(cruda: dict[str, Any]) -> dict[str, Any]:
    """Traduce una fila con encabezados sucios a las columnas canónicas."""
    fila: dict[str, Any] = {}
    for encabezado, valor in cruda.items():
        canon = _ALIAS_COLUMNAS.get(normalizar(encabezado))
        if canon is None:
            continue
        if canon in _COLUMNAS_NUMERICAS:
            fila[canon] = _parse_num(valor)
        else:
            texto = None if valor is None else str(valor).strip()
            fila[canon] = texto or None
    return fila


# --- Repositorio de archivos -------------------------------------------------


@dataclass
class ArchivoTarifa:
    """Archivo de tarifario ya leído: su nombre y sus filas crudas (sin normalizar)."""

    referencia: str
    filas: list[dict[str, Any]]


def _leer_csv(contenido: bytes) -> list[dict[str, Any]]:
    texto = contenido.decode("utf-8-sig")
    return list(csv.DictReader(io.StringIO(texto)))


def _leer_xlsx(ruta: Path) -> list[dict[str, Any]]:
    try:
        from openpyxl import load_workbook
    except ImportError as e:  # pragma: no cover - requiere el extra [drive]
        raise FuenteTarifaNoDisponible(
            'Leer XLSX requiere el extra: pip install -e ".[drive]"'
        ) from e
    wb = load_workbook(ruta, read_only=True, data_only=True)
    hoja = wb.active
    filas_iter = hoja.iter_rows(values_only=True)
    try:
        encabezados = [str(c) if c is not None else "" for c in next(filas_iter)]
    except StopIteration:  # pragma: no cover - archivo vacío
        return []
    filas = [dict(zip(encabezados, valores, strict=False)) for valores in filas_iter]
    wb.close()
    return filas


class RepositorioArchivos(ABC):
    """Obtiene el archivo de tarifario que corresponde a una línea de negocio."""

    @abstractmethod
    def obtener(self, linea_negocio: str) -> ArchivoTarifa | None:
        """Devuelve el archivo (o None si no existe uno para esa línea)."""


class LocalRepositorioArchivos(RepositorioArchivos):
    """Lee `tarifario_<linea>_*.csv|xlsx` de un directorio local.

    Sirve para el volumen del contenedor y para pruebas sin Drive. Ante varios
    archivos de la misma línea usa el de nombre mayor (el mes más reciente).
    """

    def __init__(self, directorio: str | Path) -> None:
        self.directorio = Path(directorio)

    def obtener(self, linea_negocio: str) -> ArchivoTarifa | None:
        clave = normalizar(linea_negocio).lower().replace(" ", "")
        candidatos = sorted(
            p
            for p in self.directorio.glob("tarifario_*")
            if p.suffix.lower() in {".csv", ".xlsx"}
            and normalizar(p.stem).lower().replace(" ", "").startswith(f"tarifario_{clave}_")
        )
        if not candidatos:
            return None
        ruta = candidatos[-1]
        if ruta.suffix.lower() == ".csv":
            filas = _leer_csv(ruta.read_bytes())
        else:
            filas = _leer_xlsx(ruta)
        return ArchivoTarifa(referencia=ruta.name, filas=filas)


# --- Interfaz del provider ---------------------------------------------------


class TarifaProvider(ABC):
    """Strategy: una fuente de tarifas. `fuente` etiqueta el origen en la salida."""

    fuente: str

    @abstractmethod
    def buscar(
        self,
        linea_negocio: str,
        origen: str,
        destino: str,
        filtros: dict[str, Any] | None = None,
    ) -> ResultadoTarifa:
        """Busca la tarifa de costo. Nunca inventa: sin match exacto → candidatas."""
