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
        "En Botero Soto Soluciones Logísticas agradecemos la oportunidad de presentar "
        "nuestra propuesta de servicios. Entendemos la dinámica empresarial actual y "
        "asumimos con total responsabilidad la confianza depositada en nosotros para "
        "optimizar sus operaciones. Con 79 años de trayectoria, nos hemos consolidado "
        "como el aliado estratégico que transforma la cadena de suministro, a través de "
        "la seguridad, el cumplimiento y la mejora continua y alta eficiencia en cada uno "
        "de los procesos logísticos. Respaldamos nuestra oferta integral, a través de:",
        "· Cobertura Integral: Presencia estratégica en más de 21 ciudades claves del País.",
        "· Capacidad Global: Experiencia comprobada en la gestión eficiente de operaciones "
        "logísticas y de transporte, tanto a nivel nacional como internacional.",
        "· Eficiencia Operativa: Soluciones diseñadas para mitigar riesgos, reducir costos "
        "y garantizar la continuidad del negocio.",
        "Nuestro compromiso es aportar valor medible a su organización y asegurar el "
        "cumplimiento de sus objetivos estratégicos.",
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
        "Eduardo Botero Soto S.A. tiene contratado un seguro que ampara sus "
        "obligaciones derivadas del contrato de transporte conforme los parámetros del "
        "código de comercio. El usuario se compromete a contar con una póliza de "
        "mercancías para asegurar los riesgos y pérdidas de las mercancías objeto de "
        "las operaciones de transporte."
    ),
    "stand_by": (
        "Se consideran Stand By los tiempos adicionales a los tiempos libres, por "
        "causas imputables al cliente en proceso de cargue, descargue, aduanales o "
        "documentales, siempre que excedan las 12 horas hábiles. El costo estará sujeto "
        "a las normas vigentes."
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


def money(v):
    try:
        return "$" + f"{int(round(float(v))):,}".replace(",", ".")
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


def render_tabla(sec, metodo):
    """Renderiza una tabla de tarifa para una sección."""
    filas_html = ""
    for f in sec.get("filas", []):
        t_val = calcular_tarifa(f, metodo)
        filas_html += (
            f"<tr><td class='c'>{f.get('destino','')}</td>"
            f"<td>{f.get('vehiculo_peso','')}</td>"
            f"<td class='c precio'>{money(t_val)}</td>"
            f"<td class='nota'>{f.get('nota','')}</td></tr>")
    nota_sec = sec.get('nota_general','')
    nota_html = f"<div class='nota-gen'><b>Nota:</b> {nota_sec}</div>" if nota_sec else ""
    return f"""
  <table class="tar">
    <tr class="title"><td colspan="4">{sec.get('titulo','')}</td></tr>
    <tr class="origen"><td colspan="4">ORIGEN Y/O LUGAR DE CARGUE: {sec.get('origen','')}</td></tr>
    <tr><th>Destino / Sitio entrega</th><th>Tipo de vehículo y peso</th><th>Tarifa</th><th>Nota</th></tr>
    {filas_html}
  </table>{nota_html}"""


def construir_html(d):
    metodo = d.get("metodo_calculo", "margen")

    # Soporte multi-sección O tarifa única
    secciones = d.get("secciones")
    if not secciones:
        tar = d.get("tarifa", {})
        secciones = [tar]

    contactos = "".join(f"<div>{c}</div>" for c in d.get("contactos", []))
    com = d.get("comercial", {})
    asunto = d.get("asunto") or (
        f"presentación oferta de servicios de Transporte "
        f"{d.get('linea_negocio','')}")

    def render_intro(items):
        html = ""
        bullets = []
        for item in items:
            s = item.strip()
            if s.startswith("·") or s.startswith("•"):
                bullets.append(s.lstrip("·•").strip())
            else:
                if bullets:
                    html += "<ul class='intro-bullets'>" + "".join(f"<li>{b}</li>" for b in bullets) + "</ul>"
                    bullets = []
                html += f"<p>{s}</p>"
        if bullets:
            html += "<ul class='intro-bullets'>" + "".join(f"<li>{b}</li>" for b in bullets) + "</ul>"
        return html

    intro_html = render_intro(t(d, "intro"))
    cond = bloque_cond(d)

    # Tablas de todas las secciones
    tablas_html = "".join(render_tabla(s, metodo) for s in secciones)

    # Incluye / No incluye global (del primer nivel o de la primera sección)
    inc_src = d.get("tarifa") or (secciones[0] if secciones else {})
    inc_items = inc_src.get("incluye") or t(d, "incluye")
    no_inc_items = inc_src.get("no_incluye") or t(d, "no_incluye")

    return f"""<!DOCTYPE html><html lang="es"><head><meta charset="UTF-8">
<style>
  @page {{ size:A4; margin:34mm 14mm 28mm 14mm; }}
  * {{ box-sizing:border-box; margin:0; padding:0; }}
  body {{ font-family:'Inter','Helvetica Neue',Arial,sans-serif; color:{C['text']};
         font-size:10.5px; line-height:1.45; }}
  .logo {{ position:fixed; top:-26mm; right:0; width:42mm; }}
  .footer {{ position:fixed; bottom:-22mm; left:0; right:0; }}
  .footer .dirs {{ font-size:7px; color:{C['muted']}; text-align:center;
                  padding:0 38mm 4px; }}
  h1 {{ font-size:13px; color:{C['primary']}; font-weight:700; margin:8px 0 4px; }}
  h2 {{ font-size:10.5px; color:{C['primary']}; font-weight:700; margin:6px 0 2px; }}
  h3.cond-h {{ font-size:10px; color:#0057B8; font-weight:700; margin:6px 0 2px; }}
  p {{ margin:0 0 4px; text-align:justify; font-size:10px; }}
  ul.intro-bullets {{ margin:4px 0 4px 16px; padding:0; }}
  ul.intro-bullets li {{ margin-bottom:3px; font-size:10px; text-align:justify; }}
  .cond p {{ font-size:9px; margin:0 0 3px; }}
  .cond h2 {{ font-size:10px; margin:5px 0 2px; }}
  .ref {{ text-align:right; font-weight:700; font-size:12px; color:{C['text']};
         margin-top:11mm; }}
  .cliente {{ font-weight:700; font-size:12px; margin-top:8px; }}
  .asunto {{ margin:7px 0; }}
  .asunto b {{ color:{C['text']}; }}

  table.tar {{ width:100%; border-collapse:collapse; margin-top:7px;
              border:1px solid {C['border']}; }}
  table.tar .title td {{ background:{C['secondary']}; color:#fff; font-weight:700;
                        text-align:center; padding:4px; font-size:10px;
                        letter-spacing:.02em; }}
  table.tar .origen td {{ background:{C['primary']}; color:#fff; font-weight:700;
                         text-align:center; padding:3px; font-size:9px; }}
  table.tar th {{ background:{C['bg']}; color:{C['text']}; font-size:8px;
                 text-transform:uppercase; letter-spacing:.04em; padding:4px;
                 border:1px solid {C['border']}; }}
  table.tar td {{ padding:4px 6px; border:1px solid {C['border']};
                 vertical-align:top; font-size:9px; }}
  table.tar td.c {{ text-align:center; font-weight:600; }}
  table.tar td.precio {{ color:{C['primary']}; font-weight:700; white-space:nowrap; }}
  table.tar td.nota {{ font-size:8px; color:{C['muted']}; }}

  .nota-gen {{ font-size:8px; color:{C['muted']}; margin-top:3px; text-align:justify; }}
  .cols {{ display:flex; gap:16px; margin-top:6px; }}
  .cols > div {{ flex:1; }}
  ul.lst {{ margin:3px 0 0 14px; }}
  ul.lst li {{ margin-bottom:2px; font-size:9px; }}

  table.kv {{ width:100%; border-collapse:collapse; margin:2px 0 4px; }}
  table.kv td {{ padding:3px 5px; border-bottom:1px solid {C['border']}; font-size:9px; }}
  td.lbl {{ width:40%; color:{C['muted']}; }}

  .firma {{ margin-top:8px; }}
  .firma .nom {{ font-weight:700; margin-top:6px; }}
  .firma .det {{ color:{C['muted']}; font-size:9px; }}
  .web-cierre {{ margin-top:18px; font-family:Arial,sans-serif; font-size:16pt; color:{C['text']}; border-top:1px solid {C['border']}; padding-top:8px; text-align:center; }}
  .web-cierre a {{ color:#0057B8; font-weight:700; text-decoration:none; font-family:Arial,sans-serif; font-size:16pt; }}
  .pb {{ break-before:page; }}
</style></head><body>

  <img class="logo" src="{logo_data_uri()}" alt="Botero Soto">
  <div class="footer">
    <div class="dirs">{FOOTER_DIRS}</div>
  </div>

  <!-- PORTADA / CARTA -->
  <div class="fecha">{fecha_larga(d.get('fecha', date.today().isoformat()), d.get('ciudad','Yumbo'))}</div>
  <div class="ref">{d.get('referencia','')}</div>
  <div class="cliente">{d.get('cliente','')}</div>
  {contactos}
  <div class="asunto"><b>Asunto:</b> {asunto}</div>
  {intro_html}

  <!-- CONDICIONES en hoja 1 -->
  <div class="cond">
  <h2>Facturación y forma de pago</h2><p>{t(d,'facturacion')}</p>
  <h2>Seguro</h2>{''.join(f'<p>{p}</p>' for p in t(d,'seguro').split(chr(10)+chr(10)) if p.strip())}
  <h2>Stand by</h2><p>{t(d,'stand_by')}</p>
  <h2>Vigencia</h2><p>{t(d,'vigencia')}</p>
  </div>

  <!-- TARIFAS (multi-sección) en hoja 2 -->
  <div class="pb"></div>
  <h1>NUESTRA TARIFA PARA SU SOLICITUD ES:</h1>
  {tablas_html}

  {('<h3 class=cond-h>Detalle adicional del servicio</h3>' + cond) if cond else ''}

  <div class="cols">
    <div><h3 class="cond-h">Estas tarifas incluyen</h3>
      <ul class="lst">{li(inc_items)}</ul></div>
    <div><h3 class="cond-h">Estas tarifas no incluyen</h3>
      <ul class="lst">{li(no_inc_items)}</ul></div>
  </div>

  <div class="firma">
    <p>Cordialmente,</p>
    <div class="nom">{com.get('nombre','')}</div>
    <div class="det">{com.get('cargo','')}</div>
    <div class="det">{com.get('email','')}</div>
    <div class="det">{('Cel ' + str(com.get('cel'))) if com.get('cel') else ''}</div>
  </div>
  <div class="web-cierre">Para conocer más de nuestra organización, ingresa a:
    <a href="https://www.boterosoto.com.co">www.boterosoto.com.co</a>
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
