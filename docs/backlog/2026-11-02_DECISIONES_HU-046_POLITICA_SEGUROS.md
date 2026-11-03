# Decisiones de HU-046 — Política de seguros por valor de la mercancía

> **Fecha:** 2026-11-02 · **Decidió y aprobó:** @scalderon-bs (Datos / Producto) · **Issue:** HU-046.1 (#267) · **Milestone:** HU-046
> **Fuente:** política NOGE0076 v2 (06-feb-2023), documento interno de la empresa: no se guarda en el repo.
> La HU completa está en [`2026-11-02_HUS_SEGURO_Y_POLITICA.md`](2026-11-02_HUS_SEGURO_Y_POLITICA.md).

Estas son las decisiones de producto de HU-046 y la **carga inicial aprobada**. Con ellas se construyen HU-046.2 (las
tablas, #268), HU-046.3 (el backend, #269), HU-046.4 (QA, #270) y HU-046-FE.1 (el panel, clientepro-frontend#150).

---

## Las decisiones

| # | Tema | Decisión | Por qué |
|---|---|---|---|
| 1 | Finalizar sin una medida obligatoria | **Se permite con una justificación escrita**, que queda auditada en `EventosHerramienta`. Sin justificación, "Finalizar" responde `COTIZACION_NO_FINALIZABLE` con `motivo = medidas_obligatorias_faltantes`. | Hay casos que Riesgo aprueba por fuera de Cotifly; no deben quedar trabados. |
| 2 | Tramo por ruta o por camión | **Por ruta**, con el `valor_mercancia` total de la ruta. No se divide por el número de camiones. | Cada ruta tiene un solo valor de mercancía. |
| 3 | Límites entre tramos | **`desde` exclusivo y `hasta` incluido**: el valor justo en el límite cae en el tramo de **abajo**. Exactamente $250.000.000 es "> 150 – 250 M". Los límites son en **pesos fijos**, no en SMLMV. | Así se lee la política ("hasta 150", "entre 150 y 250"). |
| 4 | Ruta sin valor de mercancía | **Basta con que el front lo exija.** No se vuelve obligatorio en el backend. Si una ruta llega sin valor (API o Excel), el backend muestra un aviso y **no bloquea** "Finalizar" por eso. | El front ya lo pide; el aviso cubre los casos raros sin cambiar la captura. |
| 5 | Acompañante armado o no armado | **Ningún tramo exige el armado.** Cumple cualquiera de los dos. | Ni la política ni Nexo definen cuándo va armado. |
| 6 | Recomendar el seguro (HU-045) | **Solo un aviso**: "si el cliente no tiene póliza propia, incluye el seguro de la mercancía". Es la medida `incluir_seguro`: **recomendada desde 250 M**, sin conexos. No se guarda si el cliente tiene póliza. | Cotifly no sabe si el cliente tiene póliza propia; el comercial sí. La política pide la póliza específica desde 250 M cuando el cliente no tiene póliza. |
| 7 | Cotizaciones que ya existen | Las reglas aplican **solo a las que no están finalizadas**. Las finalizadas no se recalculan (HU-036). | Una cotización finalizada no cambia. |
| 8 | Quién autoriza | **No se guarda en la base.** La tabla de tramos no tiene la columna `autoriza` ni una marca `proyectoEspecial`: los tramos de 700 M en adelante lo dicen en su `nota`. | Era solo informativo: Cotifly no pide ni registra aprobaciones. |
| 9 | Qué puede ser obligatorio | **Solo el acompañamiento vehicular.** El GPS, el candado satelital, el esquema básico y la restricción nocturna quedan como **recomendación** (aunque el PDF pone el GPS y el candado como obligatorios desde 250 M). | Cotifly solo puede exigir lo que el comercial agrega como conexo, y en Nexo solo hay conexos de acompañamiento. |
| 10 | Candado satelital | **No es `PRE_BSAT`** ("Precinto (Sello) Barra Satelital" es otro servicio). No hay conexo de candado en Nexo: la medida no tiene conexos. | Un precinto no es un candado. |
| 11 | Acompañante parcial o total | **El acompañante total cumple la obligación de parcial.** En los tramos de 350 – 700 M sirve cualquiera de los 5 acompañantes. | En Nexo el parcial (`AVP_*`) solo tiene precio en trayectos cortos y el total (`AVT_*`) en trayectos largos: no se cruzan en ninguna ruta. Si solo sirviera el parcial, un trayecto largo de 350 – 500 M no se podría finalizar. |
| 12 | Riesgo de la mercancía | **No se modela por ahora.** El tramo 1 queda solo con el esquema básico recomendado. | No hay una lista de mercancías de riesgo alto. Si negocio la define, va en otra HU. |
| 13 | Inconsistencias del PDF | 1) Tramo 1: aplica la regla general (póliza del cliente con subrogación), sin aviso de seguro. 2) "$250.000.000.000" se lee como **$250 M**. 3) **Pesos fijos**, no SMLMV. 4) El acuerdo con Seguros Bolívar **no se aplica**: Cotifly no sabe la aseguradora del cliente. 5) Los montos de 2023 se toman como **vigentes**. | Si alguna cambia, es un cambio de datos en las tablas de la política, sin desplegar. |

---

## Carga inicial aprobada

Aprobada por @scalderon-bs el 2026-11-02. La construye HU-046.2 (#268).

| Tramo | Esquema básico | GPS | Candado satelital | Restricción nocturna | Acompañante | Incluir seguro (aviso) | Proyecto especial |
|---|---|---|---|---|---|---|---|
| ≤ 150 M | Recomendada | — | — | — | — | — | — |
| > 150 – 250 M | Recomendada | — | Recomendada | — | — | — | — |
| > 250 – 350 M | Recomendada | Recomendada | Recomendada | — | — | Recomendada | — |
| > 350 – 500 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: parcial o total** | Recomendada | — |
| > 500 – 700 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: parcial o total** | Recomendada | — |
| > 700 – 1.000 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: total** | Recomendada | Sí |
| > 1.000 M | — | — | — | — | Caso a caso (solo aviso) | — | Sí |

Medidas y conexos que las cumplen (códigos verificados en `tbl_servicios_conexos` de Nexo el 2026-11-02):

| Medida | ¿Cobrable? | Conexos que la cumplen |
|---|---|---|
| `acompanante_vehicular` (parcial o total) | Sí | `AVP_MOTO`, `AVP_MOTO_ARMADA`, `AVT_AUTO`, `AVT_MOTO`, `AVT_MOTO_ARMADA` |
| `acompanante_total` | Sí | `AVT_AUTO`, `AVT_MOTO`, `AVT_MOTO_ARMADA` |
| `esquema_basico` | No | — |
| `gps` | No | — |
| `candado_satelital` | No | — (no hay conexo en Nexo) |
| `restriccion_nocturna` | No | — |
| `incluir_seguro` | No | — (es un aviso; el seguro se incluye con HU-045) |

En Nexo hay 5 acompañantes: parcial en moto y en moto armada (`AVP_*`, 109 trayectos cortos) y total en auto, moto y moto
armada (`AVT_*`, 361 trayectos largos). No hay servicios de escolta, custodia, candado ni GPS.

---

## Qué cambia en cada issue

| Issue | Cambio por las decisiones |
|---|---|
| **HU-046.2** (#268, Datos) | `PoliticaSeguroTramos` **sin** `autoriza`; `valorDesde` exclusivo, `valorHasta` incluido. La carga es la de arriba: 7 medidas, solo las dos de acompañamiento con conexos. |
| **HU-046.3** (#269, Backend) | Tramo por ruta con el valor total. Límites como en la decisión 3. El bloque `politica_seguros` del costeo ya no trae quién autoriza. Acepta siempre la justificación al finalizar. Aviso si la ruta no tiene `valor_mercancia`. |
| **HU-046.4** (#270, QA) | Los valores en el límite caen en el tramo de abajo; un acompañante total cumple en 350 – 500 M; se prueba finalizar con justificación. Sin "quién autoriza". |
| **HU-046-FE.1** (clientepro-frontend#150) | El panel muestra el tramo (sin quién autoriza) y las recomendaciones, incluido el aviso de incluir el seguro. El botón de agregar conexo solo aparece en los acompañantes. Pide la justificación al finalizar. |
