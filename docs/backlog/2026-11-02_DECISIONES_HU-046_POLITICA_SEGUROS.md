# Decisiones de HU-046 — Política de seguros por valor de la mercancía

> **Fecha:** 2026-11-02 · **Decidió:** @scalderon-bs (Datos / Producto) · **Issue:** HU-046.1 (#267) · **Milestone:** HU-046
> **Fuente:** política NOGE0076 v2 (06-feb-2023), documento interno de la empresa: no se guarda en el repo.
> La HU completa está en [`2026-11-02_HUS_SEGURO_Y_POLITICA.md`](2026-11-02_HUS_SEGURO_Y_POLITICA.md).

Estas son las decisiones de producto de HU-046. Con ellas se construyen HU-046.2 (las tablas, #268), HU-046.3 (el
backend, #269), HU-046.4 (QA, #270) y HU-046-FE.1 (el panel, clientepro-frontend#150).

**La carga inicial (qué medida va en cada tramo) todavía no está aprobada:** la valida negocio en HU-046.1. Ver
[Pendiente](#pendiente-con-negocio).

---

## Las decisiones

| # | Tema | Decisión | Por qué |
|---|---|---|---|
| 1 | Finalizar sin una medida obligatoria | **Se permite con una justificación escrita**, que queda auditada en `EventosHerramienta`. Sin justificación, "Finalizar" responde `COTIZACION_NO_FINALIZABLE` con `motivo = medidas_obligatorias_faltantes`. | Hay casos que Riesgo aprueba por fuera de Cotifly; no deben quedar trabados. |
| 2 | Tramo por ruta o por camión | **Por ruta**, con el `valor_mercancia` total de la ruta. No se divide por el número de camiones. | Cada ruta tiene un solo valor de mercancía. |
| 3 | Límites entre tramos | **`desde` exclusivo y `hasta` incluido**: el valor justo en el límite cae en el tramo de **abajo**. Exactamente $250.000.000 es "> 150 – 250 M". Los límites son en **pesos fijos**, no en SMLMV. | Así lo propone la HU y así se lee la política ("hasta 150", "entre 150 y 250"). |
| 4 | Ruta sin valor de mercancía | **Basta con que el front lo exija.** No se vuelve obligatorio en el backend. Si una ruta llega sin valor (API o Excel), el backend muestra un aviso y **no bloquea** "Finalizar" por eso. | El front ya lo pide; el aviso cubre los casos raros sin cambiar la captura. |
| 5 | Acompañante armado o no armado | **Ningún tramo exige el armado.** La obligación de acompañante se cumple con cualquiera de los dos (`AVP_MOTO` o `AVP_MOTO_ARMADA`, etc.). | Ni la política ni Nexo definen cuándo va armado. |
| 6 | Recomendar el seguro (HU-045) | **Solo un aviso**: "si el cliente no tiene póliza propia, incluye el seguro de la mercancía". Es la medida `incluir_seguro`: **recomendada desde 250 M**, no cobrable y **sin conexos**. No se guarda si el cliente tiene póliza. | Cotifly no sabe si el cliente tiene póliza propia; el comercial sí. La política pide la póliza específica desde 250 M cuando el cliente no tiene póliza. |
| 7 | Cotizaciones que ya existen | Las reglas aplican **solo a las que no están finalizadas**. Las finalizadas no se recalculan (HU-036). | Una cotización finalizada no cambia. |
| 8 | Quién autoriza | **No se guarda en la base.** La tabla de tramos no tiene la columna `autoriza`. Se conserva la marca `proyectoEspecial` (700 M en adelante). | Era solo informativo: Cotifly no pide ni registra aprobaciones. Si se necesita después, es una columna nueva sin rediseño. |

---

## Qué cambia en cada issue

| Issue | Cambio por las decisiones |
|---|---|
| **HU-046.2** (#268, Datos) | `PoliticaSeguroTramos` **sin** `autoriza`; `valorDesde` exclusivo, `valorHasta` incluido. La carga inicial incluye la medida `incluir_seguro` (sin conexos). |
| **HU-046.3** (#269, Backend) | Tramo por ruta con el valor total. Límites como en la decisión 3. El bloque `politica_seguros` del costeo ya no trae quién autoriza. Acepta siempre la justificación al finalizar. Aviso si la ruta no tiene `valor_mercancia`. |
| **HU-046.4** (#270, QA) | Los valores en el límite caen en el tramo de abajo; se prueba finalizar con justificación. Sin "quién autoriza". |
| **HU-046-FE.1** (clientepro-frontend#150) | El panel muestra el tramo (sin quién autoriza), el aviso de incluir el seguro como una recomendación más y pide la justificación al finalizar. |

---

## Carga inicial propuesta (pendiente de negocio)

Con las decisiones aplicadas. Negocio la valida o la cambia en HU-046.1.

| Tramo | Esquema básico | GPS | Candado satelital | Restricción nocturna | Acompañante | Incluir seguro (aviso) | Proyecto especial |
|---|---|---|---|---|---|---|---|
| ≤ 150 M | Recomendada | — | — | — | — | — | — |
| > 150 – 250 M | Recomendada | — | Recomendada | — | — | — | — |
| > 250 – 350 M | Recomendada | Recomendada | Recomendada | — | — | Recomendada | — |
| > 350 – 500 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: parcial** | Recomendada | — |
| > 500 – 700 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: parcial o total** | Recomendada | — |
| > 700 – 1.000 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: total** | Recomendada | Sí |
| > 1.000 M | — | — | — | — | Caso a caso (solo aviso) | — | Sí |

Conexos que cumplen cada medida:
- `acompanante_parcial` → `AVP_MOTO`, `AVP_MOTO_ARMADA`;
- `acompanante_total` → `AVT_AUTO`, `AVT_MOTO`, `AVT_MOTO_ARMADA`;
- `acompanante_parcial_o_total` → los 5 anteriores;
- `candado_satelital` → `PRE_BSAT` (falta confirmar);
- `incluir_seguro` → ninguno (es un aviso).

---

## Pendiente con negocio

Se cierra en HU-046.1 (#267), con Dirección Comercial, Gestión Corporativa y Administración de Riesgo:

1. **Validar la carga inicial tramo por tramo.** En especial: el PDF pone el **GPS y el candado satelital como obligatorios
   desde 250 M**; la propuesta los deja como recomendados.
2. **Las inconsistencias del PDF:**
   - el tramo 1 no dice qué seguro aplica;
   - el tramo 2 dice "$250.000.000.000" (debería ser 250 millones);
   - los tramos 4 y 5 mezclan pesos y SMLMV (la decisión 3 asume pesos fijos: confirmar);
   - los tramos del acuerdo con Seguros Bolívar no cuadran con los generales;
   - los montos son de 2023: confirmar que siguen vigentes.
3. **El "riesgo de la mercancía":** no hay una lista de mercancías de riesgo alto, y eso cambia las medidas del tramo 1.
4. **Confirmar con Administración de Riesgo** que ningún tramo exige el acompañante armado (decisión 5).
5. **Confirmar en la base de Nexo** los códigos de los conexos y si el candado satelital es `PRE_BSAT` (no depende de negocio).

Cuando esto se cierre, la tabla final aprobada se deja en #267 con quién la aprobó y la fecha.
