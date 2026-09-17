#!/usr/bin/env python3
"""
Genera la cotización final de Botero Soto con el formato y la marca reales
(portada + carta, tabla de tarifa, notas, condiciones y firma).

Uso:
    python3 armar_cotizacion.py --datos datos.json --salida cotizacion

Produce <salida>.html y, si weasyprint está disponible, <salida>.pdf.
El esquema de `datos.json` está en assets/ejemplo-solicitud.json.

Precio: si una fila trae `costo` y no `tarifa`, se calcula con el margen de la
política (metodo_calculo). La cotización al cliente muestra solo la `tarifa` final.
"""

import argparse
import base64
import json
import sys
import unicodedata
from datetime import date
from pathlib import Path

ASSETS = Path(__file__).resolve().parent.parent / "assets"

# ---- Tokens del design system de Botero Soto ----
C = {
    "primary": "#005AA8", "secondary": "#009A44", "accent": "#0072CE",
    "bg": "#F5F7FA", "surface": "#FFFFFF", "border": "#D9DEE5",
    "text": "#1A2A3A", "muted": "#5F6B7A", "warning": "#E08A00",
}

MESES = ["enero", "febrero", "marzo", "abril", "mayo", "junio", "julio",
         "agosto", "septiembre", "octubre", "noviembre", "diciembre"]

# ---- Textos estándar (editables vía JSON: claves "intro", "condiciones", etc.) ----
TEXTOS = {
    "intro": [
        "Para Botero Soto Soluciones Logísticas es muy valioso su tiempo y el "
        "permitirnos dar a conocer nuestro portafolio de servicios y todos los "
        "recursos con los que contamos para soportar las diferentes operaciones "
        "logísticas a nivel internacional y en Colombia, en las más de 21 ciudades "
        "donde tenemos presencia.",
        "Botero Soto es una Compañía orientada a satisfacer las necesidades de "
        "nuestros clientes, pero sobre todo, a generar confianza a través de nuestra "
        "razón de ser.",
        "79 años en el mercado nos permiten actuar con ética, responsabilidad y "
        "conocimiento en lo que hacemos.",
    ],
    "incluye": [
        "Flete origen – destino en vehículo",
        "Seguimiento a la carga",
        "Candado satelital",
        "Seguro de la mercancía por un valor máximo de $100.000.000 por vehículo",
    ],
    "no_incluye": [
        "Acompañamiento vehicular",
        "Otros costos ajenos a la operación",
        "Cargues y descargues",
    ],
    "nota_general": (
        "Las tarifas cotizadas no incluyen servicios de cargue ni descargue. Se basan "
        "en tiempos logísticos de máximo 4 horas para cada labor. En caso de requerir "
        "un vehículo tipo furgón, el valor se ajustará según lo estipulado por el "
        "SICETAC. Esta oferta permanecerá vigente siempre que las condiciones del "
        "mercado de fletes lo permitan. La Superintendencia de Puertos y Transporte, a "
        "través del RNDC y el SICETAC, determina los valores de los fletes, los cuales "
        "pueden variar mensualmente, conforme al Decreto 1017 del 21 de septiembre de "
        "2025. Ante cualquier condición adicional que incida en la operación, las "
        "tarifas serán revisadas y notificadas oportunamente."
    ),
    "seguro": (
        "EDUARDO BOTERO SOTO S.A. cuenta con una póliza de responsabilidad contractual que "
        "ampara las obligaciones derivadas del contrato de Transporte, conforme a lo "
        "establecido en el Código de Comercio. De igual manera, el cliente se compromete a "
        "contar con una póliza de mercancías que cubra los riesgos y pérdidas de la carga "
        "objeto de la operación de transporte. En caso de presentarse algún siniestro "
        "durante el traslado de la mercancía, el cliente realizará la reclamación "
        "directamente ante su compañía aseguradora, y EDUARDO BOTERO SOTO S.A. atenderá el "
        "proceso correspondiente mediante acción de subrogación con dicha aseguradora. "
        "Asimismo, EDUARDO BOTERO SOTO S.A. se compromete a suministrar toda la información "
        "que se encuentre bajo su control y que sea requerida por el cliente para la "
        "respectiva reclamación ante la aseguradora. En caso de que el cliente NO cuente con "
        "una compañía aseguradora, EDUARDO BOTERO SOTO S.A. podrá ofrecer una póliza de "
        "transporte como usuario permanente para esta operación. Para ello, se tomará una "
        "póliza especial contratada por EDUARDO BOTERO SOTO S.A., con beneficio a nombre del "
        "cliente, con una tarifa del 0,05% sobre el valor comercial del viaje y sin "
        "deducible en reclamaciones por avería particular, saqueo o pérdida total. Esta "
        "condición aplicará siempre y cuando se mantenga un volumen mínimo de cinco (5) "
        "viajes mensuales, los cuales podrán corresponder a despachos nacionales."
    ),
    "stand_by": (
        "Se consideran Horas Logísticas Adicionales los tiempos adicionales a los tiempos "
        "libres, por causas imputables al cliente en proceso de Cargue, Descargue, "
        "aduanales, documentales y siempre que excedan las 8 horas hábiles. Para los casos "
        "descritos el costo de las Horas Logísticas Adicionales estará sujeto a las normas "
        "vigentes."
    ),
    "vigencia": (
        "Esta oferta se mantendrá vigente siempre y cuando las condiciones de fletes "
        "en el mercado así lo permitan. Si se presentan condiciones que inciden en las "
        "operaciones, las tarifas serán revisadas y notificadas al cliente."
    ),
    "facturacion": "Después de elaborado el estudio de crédito se indicarán los términos.",
}

FOOTER_DIRS = (
    "Eduardo Botero Soto S.A. Nit 890.901.321-5<br>"
    "<b>Principal:</b> Carrera 42 N° 75 – 63 Autopista Sur, Itagüí · "
    "<b>Bogotá:</b> Calle 18 N° 69 – 69 · "
    "<b>Cali:</b> Edificio Cencar bloque A4 of. 105/106 Autopista Cali – Yumbo"
)

# Nota estándar de decretos (tiempos logísticos + contenedores vacíos) presente en
# toda cotización. NOTA: confirmar el texto oficial exacto del Decreto 16075 con el
# área jurídica; este es un texto de referencia.
NOTA_DECRETO_STD = (
    "Los tiempos logísticos se rigen por el Decreto 1017 del 21 de septiembre de 2025 "
    "(RNDC/SICETAC), que determina los valores de los fletes y puede variar mensualmente. "
    "Asimismo, el Decreto 16075 regula el manejo y la devolución de los contenedores vacíos."
)

# Frase de cierre estándar (centrada, tamaño 16) en toda cotización.
FRASE_FINAL_STD = (
    "Para conocer más de nuestra organización, ingresa a:<br>"
    '<a href="https://www.boterosoto.com.co">www.boterosoto.com.co</a>'
)


def calcular_tarifa(fila, metodo):
    if fila.get("tarifa") is not None:
        return float(fila["tarifa"])
    costo = fila.get("costo")
    margen = fila.get("margen")
    if costo is None or margen is None:
        return None
    costo, margen = float(costo), float(margen)
    if metodo == "markup":
        return round(costo * (1 + margen))
    if margen >= 1:
        raise ValueError("El margen de rentabilidad debe ser < 1 (ej. 0.23).")
    return round(costo / (1 - margen))


def money(v, moneda=None):
    prefijo = "US$" if (moneda or "").upper() == "USD" else "$"
    try:
        return prefijo + f"{int(round(float(v))):,}".replace(",", ".")
    except (TypeError, ValueError):
        return "—"


def fecha_larga(iso, ciudad):
    try:
        y, m, d = (int(x) for x in iso.split("-"))
        return f"{ciudad}, {d} de {MESES[m-1]} de {y}"
    except Exception:  # noqa: BLE001
        return f"{ciudad}, {iso}"


def logo_data_uri():
    p = ASSETS / "logo_botero_soto.png"
    if not p.exists():
        return ""
    b = base64.b64encode(p.read_bytes()).decode()
    return f"data:image/png;base64,{b}"


def _norm(s):
    """Normaliza para comparar líneas: sin acentos, minúsculas, sin espacios extra."""
    s = unicodedata.normalize("NFKD", str(s or ""))
    return "".join(c for c in s if not unicodedata.combining(c)).strip().lower()


# Imagen alusiva por línea de negocio (assets/imagenes del pdf/<archivo>).
IMG_LINEA = {
    "nacional": "Nacional.png",
    "distribucion": "Distribución.png",
    "almacenamiento": "Almacenamiento.png",
    "itr": "ITR.png",
    "importacion": "Importación.png",
    "exportacion": "Exportación.png",
    "internacional": "ANDINO.png",
    "andino": "ANDINO.png",
}


def imagen_linea_data_uri(linea):
    """Data URI (base64) de la imagen de la línea, o "" si no hay/ no existe."""
    archivo = IMG_LINEA.get(_norm(linea))
    if not archivo:
        return ""
    p = ASSETS / "imagenes del pdf" / archivo
    if not p.exists():
        return ""
    b = base64.b64encode(p.read_bytes()).decode()
    return f"data:image/png;base64,{b}"


# Frase alusiva bajo la imagen, por línea.
CAP_LINEA = {
    "importacion": "En Botero Soto importamos en el menor tiempo posible y de forma segura.",
    "exportacion": "En Botero Soto exportamos de forma segura, siempre protegidos por nuestro candado satelital.",
    "nacional": "Transporte nacional seguro, confiable y a tiempo en todo el país.",
    "internacional": "Operación internacional andina con acompañamiento y seguridad en frontera.",
    "andino": "Operación internacional andina con acompañamiento y seguridad en frontera.",
}


def imagen_linea_html(linea):
    """Bloque HTML con la imagen alusiva de la línea (se inserta bajo la introducción)."""
    uri = imagen_linea_data_uri(linea)
    if not uri:
        return ""
    cap = CAP_LINEA.get(_norm(linea))
    caption = f'<div class="dibujo-caption-bottom">{cap}</div>' if cap else ""
    return (f'<div class="dibujo-import">'
            f'<img class="dibujo-img" src="{uri}" alt="{linea}">{caption}</div>')


def li(items):
    return "".join(f"<li>{x}</li>" for x in items)


def t(d, key):
    """Texto: usa override del JSON si existe, si no el estándar."""
    return (d.get("textos") or {}).get(key, TEXTOS.get(key))


def bloque_cond(d):
    out = ""
    if str(d.get("linea_negocio", "")).lower().startswith("internacional"):
        ad = d.get("aduana") or {}
        filas = "".join(
            f"<tr><td class='lbl'>{k}</td><td>{v}</td></tr>"
            for k, v in [("Modalidad aduanera", ad.get("modalidad")),
                         ("Frontera", ad.get("frontera")),
                         ("Arancel", ad.get("arancel"))] if v)
        if filas:
            out += (f"<h3 class='cond-h'>Datos aduaneros (ANDINO)</h3>"
                    f"<table class='kv'>{filas}</table>")
    ex = d.get("extradimensionada")
    if ex:
        filas = "".join(
            f"<tr><td class='lbl'>{k}</td><td>{v}</td></tr>"
            for k, v in [("Peso (kg)", ex.get("peso_kg")), ("Ancho", ex.get("ancho")),
                         ("Largo", ex.get("largo")), ("Fotos", ex.get("fotos"))] if v)
        if filas:
            out += (f"<h3 class='cond-h'>Mercancía extradimensionada</h3>"
                    f"<table class='kv'>{filas}</table>")
    rf = d.get("refrigerada")
    if rf and rf.get("temperatura"):
        out += (f"<h3 class='cond-h'>Mercancía refrigerada</h3>"
                f"<table class='kv'><tr><td class='lbl'>Temperatura</td>"
                f"<td>{rf['temperatura']}</td></tr></table>")
    return out


def construir_html(d):
    metodo = d.get("metodo_calculo", "margen")
    tar = d.get("tarifa") or {}
    filas = tar.get("filas") or []

    filas_html = ""
    for f in filas:
        t_val = calcular_tarifa(f, metodo)
        filas_html += (
            f"<tr><td class='c'>{f.get('destino','')}</td>"
            f"<td>{f.get('vehiculo_peso','')}</td>"
            f"<td class='c precio'>{money(t_val, f.get('moneda'))}</td>"
            f"<td class='nota'>{f.get('nota','')}</td></tr>")

    contactos = "".join(f"<div>{c}</div>" for c in (d.get("contactos") or []))
    com = d.get("comercial", {})
    asunto = d.get("asunto") or (
        f"presentación oferta de servicios de Transporte "
        f"{d.get('linea_negocio','')}")
    modalidad = d.get("modalidad", "")
    subtitulo = (f"TRANSPORTE {d.get('linea_negocio','').upper()}"
                 + (f" EN CALIDAD DE {modalidad.upper()}:" if modalidad else ":"))

    intro_html = "".join(f"<p>{p}</p>" for p in t(d, "intro"))
    cond = bloque_cond(d)

    # --- Personalizaciones opcionales (por defecto no producen ningún cambio) ---
    extra_css = d.get("extra_css", "")
    # Imagen alusiva: si el payload no trae dibujo propio, se usa la imagen estándar
    # de la línea de negocio (Nacional, Importación, Exportación, ANDINO, etc.).
    dibujo_html = d.get("dibujo_html") or imagen_linea_html(d.get("linea_negocio", ""))
    # Nota de decretos (estándar en toda cotización): 1017 tiempos logísticos + 16075
    # contenedores vacíos. Se puede sobreescribir por cotización con "nota_decreto".
    nota_decreto_html = (
        f'<div class="nota-decreto"><b>Tiempos logísticos:</b> '
        f'{d.get("nota_decreto") or NOTA_DECRETO_STD}</div>'
    )
    dias_libres_html = (
        f'<h2>Días libres</h2><p>{d.get("dias_libres")}</p>'
    ) if d.get("dias_libres") else ""
    # Frase de cierre estándar (centrada, 16px) en toda cotización.
    frase_final_html = (
        f'<div class="frase-final">{d.get("frase_final") or FRASE_FINAL_STD}</div>'
    )

    return f"""<!DOCTYPE html><html lang="es"><head><meta charset="UTF-8">
<style>
  @page {{ size:A4; margin:34mm 16mm 30mm 16mm; }}
  * {{ box-sizing:border-box; margin:0; padding:0; }}
  body {{ font-family:'Stilu','Arial','Helvetica Neue',Arial,sans-serif; color:{C['text']};
         font-size:12px; line-height:1.5; }}
  .logo {{ position:fixed; top:-24mm; right:0; width:80px; }}
  .footer {{ position:fixed; bottom:-24mm; left:0; right:0; }}
  .footer .dirs {{ font-size:7px; color:{C['muted']}; text-align:center;
                  padding:0 38mm 4px; }}
  .footer .web {{ background:{C['primary']}; color:#fff; font-size:9px; font-weight:700;
                 text-align:right; padding:5px 14px; border-radius:6px 0 0 6px;
                 float:right; }}
  h1 {{ font-size:14px; color:{C['primary']}; font-weight:700; margin:14px 0 8px; }}
  h2 {{ font-size:14px; color:{C['primary']}; font-weight:700; margin:14px 0 6px; }}
  h3.cond-h {{ font-size:11px; color:{C['text']}; font-weight:600; margin:10px 0 4px; }}
  p {{ margin:0 0 8px; text-align:justify; }}
  .ref {{ text-align:right; font-weight:700; font-size:12px; color:{C['text']};
         margin-top:11mm; }}
  .cliente {{ font-weight:700; font-size:13px; margin-top:10px; }}
  .asunto {{ margin:10px 0; }}
  .asunto b {{ color:{C['text']}; }}

  table.tar {{ width:100%; border-collapse:collapse; margin-top:6px;
              border:1px solid {C['border']}; }}
  table.tar .title td {{ background:{C['secondary']}; color:#fff; font-weight:700;
                        text-align:center; padding:6px; font-size:11px;
                        letter-spacing:.02em; }}
  table.tar .origen td {{ background:{C['primary']}; color:#fff; font-weight:700;
                         text-align:center; padding:5px; font-size:10px; }}
  table.tar th {{ background:{C['bg']}; color:{C['text']}; font-size:9px;
                 text-transform:uppercase; letter-spacing:.04em; padding:6px;
                 border:1px solid {C['border']}; }}
  table.tar td {{ padding:7px 8px; border:1px solid {C['border']};
                 vertical-align:top; font-size:10px; }}
  table.tar td.c {{ text-align:center; font-weight:600; }}
  table.tar td.precio {{ color:{C['primary']}; font-weight:700; white-space:nowrap; }}
  table.tar td.nota {{ font-size:9px; color:{C['muted']}; }}

  .nota-gen {{ font-size:9px; color:{C['muted']}; margin-top:8px; text-align:justify; }}
  .cols {{ display:flex; gap:24px; margin-top:8px; }}
  .cols > div {{ flex:1; }}
  ul.lst {{ margin:4px 0 0 16px; }}
  ul.lst li {{ margin-bottom:3px; font-size:10px; }}

  table.kv {{ width:100%; border-collapse:collapse; margin:2px 0 6px; }}
  table.kv td {{ padding:4px 6px; border-bottom:1px solid {C['border']}; font-size:10px; }}
  td.lbl {{ width:40%; color:{C['muted']}; }}

  .firma {{ margin-top:22px; }}
  .firma .nom {{ font-weight:700; margin-top:18px; }}
  .firma .det {{ color:{C['muted']}; font-size:10px; }}
  .pb {{ break-before:page; }}

  /* --- Bloques opcionales de personalización (dibujo, notas, frase final) --- */
  .dibujo-import {{ margin:10px 0 14px; text-align:center; }}
  .dibujo-caption-top {{ background:{C['primary']}; color:#fff; font-weight:700;
                        font-size:10px; letter-spacing:.04em; padding:5px 10px;
                        display:inline-block; border-radius:4px 4px 0 0; }}
  .dibujo-img {{ display:block; width:100%; max-width:460px; margin:0 auto;
                border:1px solid {C['border']}; }}
  .dibujo-caption-bottom {{ background:{C['secondary']}; color:#fff; font-weight:700;
                           font-size:11px; letter-spacing:.02em; padding:7px 10px;
                           max-width:460px; margin:0 auto; border-radius:0 0 4px 4px; }}
  .frontera-escena {{ display:flex; align-items:stretch; gap:0; max-width:460px;
                     margin:0 auto; border:1px solid {C['border']}; }}
  .frontera-escena .foto {{ flex:1.4; }}
  .frontera-escena .foto img {{ display:block; width:100%; height:100%; object-fit:cover; }}
  .frontera-escena .puesto {{ flex:1; background:{C['bg']}; display:flex;
                             align-items:center; justify-content:center; }}
  .nota-decreto {{ margin:12px 0; padding:8px 10px; border-left:3px solid {C['primary']};
                   background:{C['bg']}; font-size:10px; color:{C['text']}; }}
  .nota-decreto b {{ color:{C['primary']}; }}
  .frase-final {{ margin-top:16px; text-align:center; font-size:16px; font-weight:700;
                  color:{C['primary']}; line-height:1.4; }}
  .frase-final a {{ color:{C['primary']}; }}
  {extra_css}
</style></head><body>

  <img class="logo" src="{logo_data_uri()}" alt="Botero Soto">
  <div class="footer">
    <div class="web">www.boterosoto.com.co</div>
    <div class="dirs">{FOOTER_DIRS}</div>
  </div>

  <!-- PORTADA / CARTA -->
  <div class="fecha">{fecha_larga(d.get('fecha', date.today().isoformat()), d.get('ciudad','Yumbo'))}</div>
  <div class="ref">{d.get('referencia','')}</div>
  <div class="cliente">{d.get('cliente','')}</div>
  {contactos}
  <div class="asunto"><b>Asunto:</b> {asunto}</div>
  {intro_html}
  {dibujo_html}

  <!-- TARIFA -->
  <div class="pb"></div>
  <h1>NUESTRA TARIFA PARA SU SOLICITUD ES:</h1>
  <h2>{subtitulo}</h2>
  <table class="tar">
    <tr class="title"><td colspan="4">{tar.get('titulo','COTIZACIÓN — BSSL')}</td></tr>
    <tr class="origen"><td colspan="4">ORIGEN Y/O LUGAR DE CARGUE: {tar.get('origen','')}</td></tr>
    <tr><th>Destino</th><th>Tipo de vehículo y peso</th><th>Tarifa</th><th>Nota</th></tr>
    {filas_html}
  </table>
  <div class="nota-gen"><b>Nota:</b> {tar.get('nota_general') or t(d,'nota_general')}</div>
  {nota_decreto_html}

  {('<h3 class=cond-h>Detalle adicional del servicio</h3>' + cond) if cond else ''}

  <div class="cols">
    <div><h3 class="cond-h">Estas tarifas incluyen</h3>
      <ul class="lst">{li(tar.get('incluye') or t(d,'incluye'))}</ul></div>
    <div><h3 class="cond-h">Estas tarifas no incluyen</h3>
      <ul class="lst">{li(tar.get('no_incluye') or t(d,'no_incluye'))}</ul></div>
  </div>

  <!-- CONDICIONES -->
  <div class="pb"></div>
  <h2>Facturación y forma de pago</h2><p>{t(d,'facturacion')}</p>
  <h2>Seguro</h2><p>{t(d,'seguro')}</p>
  {dias_libres_html}
  <h2>Horas logísticas adicionales</h2><p>{t(d,'stand_by')}</p>
  <h2>Vigencia</h2><p>{t(d,'vigencia')}</p>
  {frase_final_html}

  <div class="firma">
    <p>Cordialmente,</p>
    <div class="nom">{com.get('nombre','')}</div>
    <div class="det">{com.get('cargo','')}</div>
    <div class="det">{com.get('email','')}</div>
    <div class="det">{('Cel ' + str(com.get('cel'))) if com.get('cel') else ''}</div>
  </div>
</body></html>"""


def main():
    ap = argparse.ArgumentParser(description="Genera la cotización Botero Soto.")
    ap.add_argument("--datos", required=True)
    ap.add_argument("--salida", default="cotizacion")
    args = ap.parse_args()

    d = json.loads(Path(args.datos).read_text(encoding="utf-8"))
    html = construir_html(d)
    Path(f"{args.salida}.html").write_text(html, encoding="utf-8")
    print(f"HTML generado: {args.salida}.html")

    try:
        from weasyprint import HTML
        HTML(string=html, base_url=str(ASSETS)).write_pdf(f"{args.salida}.pdf")
        print(f"PDF generado: {args.salida}.pdf")
    except Exception as e:  # noqa: BLE001
        print(f"PDF no generado ({e.__class__.__name__}: {e}).")
        print("Convierte el HTML a PDF con weasyprint o el skill `pdf`.")


if __name__ == "__main__":
    sys.exit(main())
