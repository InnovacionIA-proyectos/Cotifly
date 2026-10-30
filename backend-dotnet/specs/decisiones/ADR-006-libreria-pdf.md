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
- Los tests de PDF verifican la posición de los bloques clave contra una referencia de maquetación (#92) y el texto extraído (cliente, precio, nota de decreto, ausencia de "Cel" sin teléfono, bloque de aduana en Internacional, `<script>` mostrado como texto literal) y el número de páginas.

## Implementación (#11, HU-003.5)

Lo decidido arriba se implementó así; donde difiere, manda esta sección:

- **Paquete:** `PDFsharp-MigraDoc` 6.2.4 (MIT, build multiplataforma). Solo pruebas: `PdfPig` 0.1.16 (Apache-2.0) para leer texto, fuentes e imágenes.
- **Separación:** las decisiones de negocio del HTML (textos del payload o estándar, tarifa por fila, bloques condicionales, imagen por línea, firma) viven en Aplicación (`clsConstructorDocumentoCotizacion` → `clsDocumentoCotizacion`, probado sin PDF). `clsGeneradorPdfMigraDoc` solo maqueta, con un componente por bloque: `clsBloquePortada`, `clsBloqueTarifa`, `clsBloqueCondiciones`, `clsBloqueFirma` y `clsEncabezadoPiePdf`; medidas en `clsDisenoPdf` (una instancia por variante) y paleta en `clsEstilosPdf`. En lugar de estilos MigraDoc con nombre se usan ayudantes (px CSS × 0,75 = pt; `line-height` como interlineado exacto).
- **Textos estándar:** `clsTextosEstandar` con los dos juegos del Python (`armar_cotizacion.py` y `armar_multi.py` difieren en introducción, seguro y stand by), `FOOTER_DIRS` como estructura (sede en negrita + dirección), `NOTA_DECRETO_STD`, frase final con enlace, `IMG_LINEA` y `CAP_LINEA`.
- **Fuentes:** `clsResolvedorFuentes` (`IFontResolver`) sirve Liberation Sans 2.1.5 (4 caras, SIL OFL 1.1, licencia en `licencias/LiberationSans-OFL.txt` de la aplicación publicada) para cualquier familia pedida. Se registra una vez por proceso en `GlobalFontSettings` al crear el generador (singleton). Las listas usan viñetas propias (`•` + tabulación y sangría francesa), no las de MigraDoc.
- **Imágenes:** recursos embebidos con nombres lógicos fijos (`Cotifly.Pdf.Imagenes.*`), entregados a MigraDoc como `base64:` (sin archivos temporales). Las 7 imágenes por línea se renombraron a ASCII y se redujeron de PNG de ~2 MB a JPEG de 1200 px (calidad 88): en el PDF se ven a 122 mm (~250 ppp).
- **Datos:** el celular de la firma sale de `UsuariosWeb.telefono` (confirmado con `IClsTelefonoComercialService`); nombre y correo, de `UsuariosWeb`; del payload solo el cargo. Los textos se limpian de caracteres de control (`clsTextoSeguro`) y se insertan como texto.
- **Verificación en la imagen:** una sonda desechable renderizó los 3 casos dentro de `cotifly-api:dev` (chiseled-extra, usuario `app`, sin fontconfig ni fuentes del sistema) con los ensamblados de `/app`: 3/3/2 páginas y solo Liberation Sans embebida (`docs/03-infraestructura.md` §3.1).

**Cobertura del layout frente a `armar_cotizacion.py` / `armar_multi.py`:**

| Elemento del HTML | Estado en .NET |
|---|---|
| A4, márgenes 34/16/30/16 mm (multi 34/14/28/14) | Igual |
| Logo fijo arriba a la derecha (80 px a 10 mm del borde; multi 42 mm a 8 mm) | Igual, en el encabezado de cada página |
| Pie: `FOOTER_DIRS` centrado 7 px con 38 mm de aire y franja web azul a la derecha (solo tarifa única) | Igual; la franja es rectangular (MigraDoc no redondea esquinas) |
| Fuente Stilu / Arial | Liberation Sans (métricas de Arial). Stilu pendiente de licencia |
| Página 1: fecha larga (es-CO, zona de negocio), referencia a la derecha, cliente, contactos, asunto (por defecto "presentación oferta de servicios de Transporte {línea}"), introducción | Igual. En multi, las líneas que empiezan por `·`/`•` son viñetas |
| Imagen de la línea con leyenda en franja verde (`.dibujo-import`) | Igual (centrada a 122 mm; WeasyPrint la dejaba alineada a la izquierda con la leyenda centrada). `dibujo_html` del payload no existe |
| Página 2: "NUESTRA TARIFA PARA SU SOLICITUD ES:", subtítulo "TRANSPORTE {LÍNEA} EN CALIDAD DE {MODALIDAD}:" | Igual (multi sin subtítulo) |
| Tabla: franja verde con título (por defecto "COTIZACIÓN — BSSL"), franja azul con el origen, encabezados en mayúscula sobre gris, destino centrado en negrita, tarifa azul en negrita, nota atenuada | Igual; anchos de columna fijos (15/22/15/48 %) en vez de automáticos; sin espaciado entre letras. Encabezados de tabla repetidos si la tabla sigue en otra página (mejora) |
| Tarifa: `tarifa` o costo/margen con `metodo_calculo`; `$`/`US$`, miles con punto | Igual (half-to-even); multi también respeta `US$` (el Python no) |
| Nota general, nota de decretos con borde azul y fondo gris | Igual (multi sin nota de decretos; nota por sección) |
| Detalle adicional: aduana solo si la línea empieza por "internacional", extradimensionada, refrigerada (tablas clave-valor) | Igual |
| Dos columnas "Estas tarifas incluyen / no incluyen" | Igual (tabla de 3 columnas). Nuevo: `servicios_conexos` itemizados con precio |
| Página 3: facturación, seguro, días libres (si vienen), horas logísticas adicionales, vigencia | Igual. En multi las condiciones van compactas en la página 1 con "Stand by" |
| Frase final 16 px azul con enlace subrayado a la web | Igual (hipervínculo real). Multi: cierre con línea superior y enlace sin subrayar, después de la firma |
| Firma: "Cordialmente,", nombre en negrita, cargo, correo, "Cel ..." | Igual; "Cel" solo con `UsuariosWeb.telefono` |
| `extra_css` | No aplica (sin CSS) |

**Diferencias visuales conocidas** (para la revisión con Kevin): la imagen de la línea va centrada (WeasyPrint la dejaba a la izquierda con la leyenda centrada); las tablas quedan ~2 mm más arriba o abajo por los márgenes CSS que ya no se colapsan; anchos de columna fijos (15/22/15/48 %) en vez de automáticos; en multi los encabezados de la tabla van centrados y "DESTINO / SITIO ENTREGA" ocupa dos líneas; el pie queda en una tabla de 3 columnas (texto centrado y franja web a la derecha, rectangular). `Almacenamiento3PL` no tiene imagen de línea (igual que el Python: `IMG_LINEA` no la trae). Las palabras de más de ~30 caracteres sin espacios se parten con espacios de ancho cero (8 en "Destino" y 12 en "Tipo de vehículo" por el ancho de columna) para que no tapen la tarifa.

**Comparación visual:** las pruebas dejan `dotnet-1-nacional-PCN-19032026.pdf`, `dotnet-2-internacional-PCN-24092026.pdf` y `dotnet-3-multiseccion-PCN-19032026-v2.pdf` en `backend-dotnet/tests/artefactos/` (no versionado). Los equivalentes Python se generaron con el código actual y WeasyPrint 70 en un contenedor `python:3.12-slim` desechable (script fuera del repo) con los mismos datos y folio: mismas páginas (3/3/2), mismos textos y montos; diferencias visibles solo de márgenes colapsados (±3 mm en títulos), anchos de columna y la alineación de la imagen. La revisión con Kevin y la evidencia en el issue siguen pendientes.

## Nota (2026-10-07, #92 y #95): PDFsharp-MigraDoc no es thread-safe; render serializado por proceso

**Evidencia.** PDFsharp-MigraDoc 6.2.4 comparte estado global entre documentos al medir y escribir el texto. Se renderizaron 400
documentos en paralelo (Nacional, Multi, Internacional y Dólares mezclados) y cada PDF Nacional se comparó contra uno renderizado
solo. El resultado:

- 99 de 100 salieron con otra maquetación y 43 con otro texto extraído.
- En los streams de contenido aparecen `/F0 9 Tf` de más antes de los espacios, que se miden con la fuente de otro documento.
- Hay palabras corridas hasta 529 pt y saltos de línea distintos, por ejemplo "máxim d a e 34" o "candad s o atelital".

PdfPig no era la causa: leyó 400 veces en paralelo los mismos bytes y siempre extrajo lo mismo. Con el render serializado hubo 0
diferencias. El equipo de PDFsharp lo advierte en su foro ("PDFsharp is not (yet) thread safe", Thomas Hoevel, PDFsharp Team):
<https://forum.pdfsharp.net/viewtopic.php?p=6485> y <https://forum.pdfsharp.net/viewtopic.php?p=12316>.

**Decisión.**

- **Puerto async con turno de render.** `IClsGeneradorPdf.GenerarAsync(documento, ct)` toma un `SemaphoreSlim(1)` de proceso (no de
  instancia) y espera turno hasta `Pdf:TiempoEsperaRenderSegundos`, 30 s por defecto.
- **Sección crítica.** Dentro solo hay CPU: el armado del modelo, el render y el guardado en memoria. El almacén y la base van fuera.
- **Espera agotada.** Lanza `clsRenderPdfOcupadoException`. Los casos de uso responden `PDF_RENDER_NO_DISPONIBLE`, que desde #92
  es 503 (transitorio, reintentar), igual que un fallo del render. No se guarda nada ni cambia la versión.
- **Log.** Cada render deja en el log la espera de turno y la duración del render
  (`PDF {Referencia} renderizado: espera de turno … ms, render … ms`). Si el turno no llega, queda un warning.

**Pruebas.**

- `LosRendersSimultaneosDanLaMismaMaquetacionQueUnRenderSolo` compara la posición de cada letra entre renders concurrentes y uno
  solo.
- `clsMaquetacionPdfTests` compara los bloques clave de las 5 variantes (encabezado, fecha, folio, asunto, tabla, notas,
  condiciones y firma) contra `tests/fixtures/pdf/maquetacion-referencia.json`, con una tolerancia de 1,5 pt. Moviendo el bloque
  del folio de 11 a 30 mm fallan las 5 variantes.
- La referencia se regenera solo a mano, nunca en CI, con
  `COTIFLY_REGENERAR_MAQUETACION=1 dotnet test --project tests/Cotifly.Infraestructura.Tests --filter-method "*RegeneraLaReferenciaDeMaquetacion"`.
  Ese comando reescribe el JSON y falla a propósito para que el diff se revise y se versione junto con el cambio de diseño.

**Consecuencia.** La capacidad por réplica tiene un techo de PDFs por segundo. La capacidad medida y la señal para escalar están
en [10-operacion §10.6](../../docs/10-operacion.md#106-riesgos-y-limitaciones-conocidos).

## Pendientes

| Pendiente | Dueño |
|---|---|
| Confirmar si la variante multi-sección se usa en la práctica o puede diferirse | Negocio + Kevin |
| Aprobar la sustitución de la fuente Stilu por Liberation Sans (o conseguir licencia de Stilu) | Negocio |
| Revisar la comparación visual de los 3 PDF (.NET vs Python) y adjuntar la evidencia en #11 | Kevin + Duban |
| Confirmar si Nexo entrega el costeo desglosado por servicio conexo (hoy los precios de `servicios_conexos` los envía el agente o el formulario) | Equipo + Nexo (HU-005) |
