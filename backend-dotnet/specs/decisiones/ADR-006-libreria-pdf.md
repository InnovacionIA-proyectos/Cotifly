# ADR-006 — Librería de PDF: PDFsharp-MigraDoc

- **Estado:** Aceptada
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #11 (HU-003.5).

## Contexto

Hoy el PDF se genera con WeasyPrint a partir del HTML que arman `mcp_server/pdf/scripts/armar_cotizacion.py` (tarifa única) y `armar_multi.py` (cuando la cotización trae `secciones`, ver `mcp_server/pdf/generador.py`).

Características del layout actual:

- A4. Márgenes de 34/16/30/16 mm en tarifa única; la variante multi usa 34/14/28/14 mm (`armar_multi.py:245`).
- Logo fijo arriba a la derecha y pie fijo con direcciones (`FOOTER_DIRS`) y una franja web.
- Paleta corporativa: `#005AA8` (primario), `#009A44` (secundario), `#0072CE` (acento).
- Tres bloques separados por salto de página: presentación; "Nuestra tarifa para su solicitud es" con la tabla de tarifa y las listas de incluye / no incluye; condiciones (facturación, seguro, horas logísticas, vigencia).
- Bloques condicionales para aduana, mercancía extradimensionada y refrigerada; nota de decreto (`NOTA_DECRETO_STD`).
- Imagen por línea de negocio y logo, embebidos desde `mcp_server/pdf/assets/`.
- Fuente declarada: `Stilu` con Arial como alternativa.

El HTML se arma sin escapar los datos (hallazgo de seguridad). El issue sugería QuestPDF u otra librería aprobada.

## Decisión

**PDFsharp-MigraDoc (licencia MIT)**, detrás del puerto `IClsGeneradorPdf` (Aplicación). La implementación es `clsGeneradorPdfMigraDoc` (Infraestructura/Pdf).

Cómo se reproduce el layout:

- **Página:** `PageSetup` en A4 con los márgenes de cada variante. El logo va en `Headers.Primary` y el pie (direcciones + franja web) en `Footers.Primary`.
- **Estilos:** estilos MigraDoc (`Normal`, `Heading1`, `Heading2`, `CondH`, `Nota`) con los tamaños y colores del CSS actual, definidos en una sola clase de estilos.
- **Estructura:** componentes C# por bloque (`Portada`, `TablaTarifa`, `Condiciones`, `Firma`), con `PageBreak` entre bloques como en `.pb`. Las tablas tarifa y clave-valor usan `Table` de MigraDoc con bordes y sombreado equivalentes.
- **Textos estándar:** se portan a `clsTextosEstandar`, incluidos `NOTA_DECRETO_STD` y `FOOTER_DIRS`.
- **Variante multi-sección** (`armar_multi.py`): mismos componentes, repitiendo el bloque de tarifa por sección.
- **Imágenes:** los PNG de `assets/` se embeben como recursos del ensamblado.
- **Fuentes:** `IFontResolver` propio con fuentes TTF embebidas como recurso. La imagen Linux no necesita instalar paquetes de fuentes. Como `Stilu` no tiene licencia disponible, se usa Liberation Sans. Tiene licencia SIL OFL 1.1 y métricas compatibles con Arial, que ya es la alternativa del CSS. El resolver también debe cubrir las fuentes que MigraDoc usa para las viñetas de listas; si no, se usan viñetas propias (carácter o imagen dentro de la fuente embebida).
- **Sin HTML:** los datos se insertan como texto en el modelo de documento, así que la inyección de HTML/script desaparece por construcción.
- **Datos reales:** el teléfono del comercial sale de `UsuariosWeb.telefono`, nunca del LLM. Si es nulo, se omite "Cel".

## Alternativas consideradas

- **QuestPDF:** su API fluida es cómoda, pero la licencia Community solo cubre a organizaciones con ingresos anuales por debajo de USD 1M. Botero Soto la supera, así que requiere licencia comercial paga. Descartada.
- **HTML + Chromium headless (Playwright / PuppeteerSharp):** reutilizaría casi tal cual el HTML/CSS actual. A cambio, suma Chromium a la imagen (cientos de MB, dependencias nativas, más superficie de ataque) y mantiene el riesgo de inyección si no se escapa bien. Descartada.
- **Seguir con WeasyPrint en un sidecar Python:** contradice el corte total a .NET (ADR-001).

## Consecuencias

- Sin costo de licencia y sin binarios nativos: la imagen runtime se mantiene pequeña.
- **Riesgo aceptado:** el issue pedía confirmar que la librería reproduce el layout actual. Esa confirmación no se hace en esta ADR; se acepta como riesgo y se valida en #11 comparando 3 PDFs .NET contra sus equivalentes Python, revisados con Kevin.
  - Reproducir con MigraDoc un layout pensado en CSS exige más código que el HTML.
  - Algunos efectos (flex en dos columnas, imagen con `object-fit`) se aproximan con tablas.
  - Se mitiga aislando cada bloque en su propio componente.
- Los tests de PDF verifican el texto extraído (cliente, precio, nota de decreto, ausencia de "Cel" sin teléfono, bloque de aduana en Internacional, `<script>` mostrado como texto literal) y el número de páginas.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Confirmar si la variante multi-sección se usa en la práctica o puede diferirse | Negocio + Kevin |
| Aprobar la sustitución de la fuente Stilu por Liberation Sans (o conseguir licencia de Stilu) | Negocio |
