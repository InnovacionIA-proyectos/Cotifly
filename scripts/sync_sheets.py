"""CLI para volcar la BD a Google Sheets bajo demanda (tablero Looker Studio).

Uso:
    python -m scripts.sync_sheets                 # usa SHEETS_SPREADSHEET_ID del entorno
    python -m scripts.sync_sheets <spreadsheet_id># override del libro destino

Requisitos: extra [sheets] instalado, GOOGLE_SA_JSON configurado y el libro
compartido con el service account como Editor (ver infra/GOOGLE-SETUP.md).
"""

from __future__ import annotations

import sys

from shared.db import get_session
from shared.sheets_sync import SheetsNoDisponible, sincronizar


def main() -> int:
    spreadsheet_id = sys.argv[1] if len(sys.argv) > 1 else None
    try:
        with get_session() as s:
            conteo = sincronizar(s, spreadsheet_id=spreadsheet_id)
    except SheetsNoDisponible as e:
        print(f"[sync_sheets] No se pudo sincronizar: {e}", file=sys.stderr)
        return 2
    total = sum(conteo.values())
    print(f"[sync_sheets] Volcado completo ({total} filas):")
    for hoja, n in conteo.items():
        print(f"  - {hoja}: {n}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
