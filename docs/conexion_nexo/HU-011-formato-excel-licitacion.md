# HU-011: formato del Excel de licitación (HU-011.1, #46)

> **Estado:** vigente (preguntas pendientes de Gerencia Comercial) · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

**Estado:** propuesta técnica lista para revisión. Las preguntas de la sección 8 están **pendientes de aprobación de Gerencia Comercial**. El ejemplo real de un pliego del cliente (primer checkbox de #46) sigue **pendiente con negocio**.

Documentos relacionados: `docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md` (mapeo aprobado del formulario ampliado, HU-005.1), `backend-dotnet/specs/decisiones/ADR-015-costeo-nexo.md` (costeo en Nexo), `backend-dotnet/specs/decisiones/ADR-017-licitaciones-multilinea.md` (modelo multi-línea y endpoints de HU-011) y `docs/docs_front/GUIA_FRONTEND_COTIFLY.md` (contrato para el frontend).

## 1. Decisión: Cotifly no impone una plantilla

Cada cliente manda su pliego con su propia plantilla: columnas con otros nombres, celdas combinadas, encabezados en dos filas, hojas de portada. Definir un "formato Cotifly" obligaría al comercial a copiar a mano el pliego en otra plantilla, que es justo lo que HU-011 quiere evitar.

Nexo (Cabina de Fletes) ya resolvió esto con un flujo de **analizar y mapear**, y Cotifly lo reutiliza tal cual:

1. **Analizar.** El comercial sube el `.xlsx` del cliente. Cotifly lo reenvía a `POST /api/captura-solicitudes/analizar-excel` de Nexo, que no guarda nada: detecta en cada hoja la fila de encabezado (la primera, dentro de las 15 primeras filas usadas, con al menos 3 valores distintos), une un subencabezado si los rótulos se repiten, propaga las celdas combinadas y devuelve las filas con datos como texto (`AnalisisExcelDto`).
2. **Mapear.** El frontend muestra los encabezados y el comercial asigna cada columna del pliego a un campo destino de la sección 3 (por ejemplo, "CIUDAD ORIGEN" → `origen`). Las columnas sin asignar se ignoran para el costeo, pero **se conservan intactas** en la exportación (#48).
3. **Validar.** El frontend arma las líneas y las envía a Cotifly. Cotifly valida **todas** las filas antes de guardar o enviar nada y devuelve un reporte por fila (sección 5).
   La `fila` de cada línea es su índice en `hojas[h].filas`; Cotifly la verifica contra el total de filas con datos de la hoja del archivo adjunto (`totalFilas`) con la misma regla de detección de Nexo.
4. **Enviar.** Si todo es válido, Cotifly guarda la cotización con sus N líneas y la envía a Nexo como **una sola solicitud con N líneas** (`tipo = licitacion`), con el mismo mecanismo de la HU-005.5.

Así cualquier plantilla de cliente sirve sin cambios de código. El mapeo vive en el frontend; el contrato con el backend es el conjunto de campos destino.

### Hechos de Nexo que fijan el formato

Leídos del repo `NEXO` (`CONTRATO.md` §"Carga de archivos Excel" y §"Captura", `CapturaSolicitudesController`, `ExcelSolicitudesAdapter`, `CapturarSolicitudValidador`):

| Hecho | Valor |
|---|---|
| Formato del archivo | Solo `.xlsx` y `.xlsm`, campo multipart `archivo`, máximo 10 MB |
| Errores del archivo | `ARCHIVO_REQUERIDO` (falta o vacío), `FORMATO_NO_SOPORTADO` (otra extensión), `EXCEL_INVALIDO` (no se puede leer), `EXCEL_SIN_DATOS` (ninguna hoja con tabla) |
| Respuesta del análisis | `AnalisisExcelDto { archivo, hojas: [{ nombre, filaEncabezado, encabezados[], filas[][], totalFilas }] }` |
| Filas devueltas | Máximo 1000 por hoja en `filas` (`totalFilas` trae el total real). Las filas vacías se saltan |
| Encabezado vacío | Se nombra `Columna X` con la letra de la columna |
| Alta de la solicitud | `POST /api/captura-solicitudes` con `CapturaSolicitudRequest { clienteBp, tipo: "licitacion", deadline?, valorEstimado?, referencia?, horasCargue?, horasDescargue?, lineas: CapturaLineaRequest[] }` |
| Auto-costeo | Con 10 líneas o menos Nexo costea en la misma petición; con más lo hace en segundo plano (`costeoEnProceso = true`) |

## 2. Campos del encabezado de la licitación

Uno por licitación. Los llena el comercial en el formulario (no salen del Excel, aunque el frontend puede proponerlos si el pliego los trae en la portada).

| Campo (Cotifly, snake_case) | Campo Nexo | Tipo | Obligatorio | Significado |
|---|---|---|---|---|
| `tipo` | `tipo` | fijo `licitacion` | Implícito | El endpoint de licitaciones siempre envía `licitacion` |
| `nit_cliente` | (`clienteBp`) | string, hasta 20 | Sí | NIT elegido en `GET /web/clientes`. El servidor resuelve el BP en el catálogo de Nexo, como en HU-005.5 |
| `cliente_nombre` | — | string, hasta 300 | No | Referencia visible; se reemplaza por el nombre del catálogo |
| `linea_negocio` | `lineaNegocio` de cada línea | string | Sí | Línea de Cotifly (`MargenesPolitica`). Da el margen de toda la licitación y su `mapeoLineasNexo` viaja en cada línea |
| `deadline` | `deadline` | fecha y hora ISO 8601 | No | Fecha de cierre del pliego |
| `valor_estimado` | `valorEstimado` | decimal, 0 a 9.999.999.999.999,99, 2 decimales | No | Valor estimado de toda la licitación |
| `referencia` | `referencia` | string, hasta 200 | No | Número o nombre del pliego del cliente |
| `horas_cargue`, `horas_descargue` | `horasCargue`, `horasDescargue` | decimal, 0 a 240, 2 decimales | No | Tiempos logísticos de toda la solicitud |

## 3. Campos destino del mapeo (una línea por fila del pliego)

Son los de `CapturaLineaRequest` de Nexo, con los nombres que ya usa `POST /web/solicitudes-costeo`. Los catálogos cerrados llevan el **código de Nexo tal cual** (decisión de negocio del 2026-09-26): el frontend traduce el texto del pliego al código (por ejemplo "Tractomula" → `3S3`) y muestra la etiqueta amigable.

| Campo destino | Tipo | Obligatorio | Catálogo o regla | Significado de negocio |
|---|---|---|---|---|
| `origen` | string, hasta 150 | **Sí** | Municipio de `GET /web/municipios` (columna `municipio` de Nexo) | Ciudad de cargue |
| `destino` | string, hasta 150 | **Sí** | Municipio de `GET /web/municipios` | Ciudad de descargue |
| `tipo_vehiculo` | — | **Retirado (HU-021.6, decisión 7)** | Ya no se mapea: el vehículo lo define el cubicador o la Mesa de Costeo de Nexo. Si llega, se ignora | — |
| `carroceria` | — | **Retirado (HU-021.6, decisión 7)** | Ya no se mapea. Si llega, se ignora | — |
| `tipologia` | string, hasta 50 | No | Fijo en el frontend: `Contenedor`, `CargaSuelta`, `GranelSolido`, `GranelLiquido`, `Alimentos`, `Refrigerados`, `CargaSeca` | Naturaleza de la carga |
| `clase_viaje` | string, hasta 50 | No | `clases_viaje` de `GET /web/catalogos-flete` (`Contenedor`, `Cupo`, `CargaSuelta`, `Extradimensionado`) | Define reglas condicionales |
| `clase_contenedor` | string, hasta 20 | Condicional | `CNT20.IT/RE/ST`, `CNT40.HC/RE`; solo con `clase_viaje = Contenedor` | Tamaño del contenedor |
| `exclusivo` | bool | No | `SI`/`NO`, `X`, `1`/`0` del pliego → `true`/`false` (lo traduce el frontend) | Vehículo dedicado |
| `consolidado` | bool | No | Igual que `exclusivo` | Carga consolidada |
| `retorna_contenedor` | bool | No | — | El contenedor vacío se devuelve |
| `ciudad_devolucion_contenedor` | string, hasta 150 | Condicional | Municipio; con `retorna_contenedor = true` | Dónde se devuelve |
| `levante_contenedor_diferente` | bool | No | — | El contenedor se recoge en otra ciudad |
| `ciudad_levante_contenedor` | string, hasta 150 | Condicional | Municipio; con `levante_contenedor_diferente = true` | Dónde se recoge |
| `valor_aprovisionamiento` | decimal, 0 o más, 2 decimales | No | — | Costo del viaje vacío de aprovisionamiento si el cliente lo informa |
| `peso_kg` | decimal, 0 a 999.999,99, 2 decimales | **Condicional** | Regla de peso de Nexo (sección 5) | Peso total de la línea en kilogramos |
| `valor_mercancia` | decimal, 0 o más, 2 decimales | No | — | Valor declarado de la carga de la línea |
| `posiciones` | lista, hasta 200 por línea | No | `[{descripcion?, cantidad, largo_cm, ancho_cm, alto_cm, peso_kg?}]`, cubicación (`POST /web/cubicacion`) | Bultos de la línea, en centímetros y kilogramos |

Además, cada línea lleva dos datos de trazabilidad que **no viajan a Nexo**:

| Campo | Tipo | Obligatorio | Uso |
|---|---|---|---|
| `fila` | entero, 0 o más | Sí si se adjunta el archivo | Índice (base 0) de la fila dentro de `hojas[h].filas` del análisis. Se valida contra el total de filas con datos de la hoja (`totalFilas`), recalculado sobre el archivo adjunto. La exportación (#48) escribe el precio en esa fila del archivo original |
| (encabezado) `hoja` | string | Sí si se adjunta el archivo | Nombre de la hoja analizada (`hojas[h].nombre`) |

Unidades: si el pliego trae toneladas, el frontend multiplica por 1000 antes de enviar `peso_kg`; si trae metros, multiplica por 100 para `largo_cm`, `ancho_cm` y `alto_cm`. Cotifly no adivina unidades.

## 4. Fuente de los catálogos

| Catálogo | Endpoint de Cotifly | Origen en Nexo |
|---|---|---|
| Municipios (origen, destino, devolución y levante) | `GET /web/municipios` (catálogo completo, sin filtro ni paginación: el autocompletado filtra en el cliente) | `GET /api/captura-solicitudes/municipios` |
| Tipo de vehículo, carrocería y clase de viaje | `GET /web/catalogos-flete` | Biblioteca SAP TM histórico de Nexo |
| Cubicación de las posiciones | `POST /web/cubicacion` | Ya no pasa por Nexo: `POST /recomendaciones` del cubicador (HU-023.2, #157) |
| Tipología y clase de contenedor | Fijos en el frontend (deuda técnica del mapeo HU-005.1) | Sin endpoint en Nexo |

Para un pliego de cientos de filas el frontend debe cachear los catálogos y resolver los municipios con una sola carga, no con una búsqueda por fila.

## 5. Validación por fila (espejo del validador de Nexo)

Cotifly valida todas las filas **antes de guardar nada** y responde 400 `LICITACION_INVALIDA` con el reporte completo (todas las filas con error, no solo la primera). Cada error trae `indice` (posición en `lineas[]`, base 0), `fila` (la del análisis, si vino), `campo` y `codigo`.

| Regla | Código | Origen |
|---|---|---|
| Sin líneas | `LINEAS_REQUERIDAS` | `MinLength(1)` de `CapturaSolicitudRequest` |
| Más de 1000 líneas | `DEMASIADAS_LINEAS` | Tope de Cotifly (las mismas 1000 filas por hoja que devuelve el análisis) |
| `origen` o `destino` vacío | `LINEA_INCOMPLETA` | `CapturarSolicitudValidador` |
| Texto más largo que su límite (sección 3) | `CAMPO_DEMASIADO_LARGO` | `MaxLength` de `CapturaLineaRequest` |
| Número fuera de rango o con más de 2 decimales | `VALOR_FUERA_DE_RANGO` | `Range` de `CapturaLineaRequest`; las columnas de Cotifly guardan 2 decimales |
| Más de 200 posiciones en una línea | `DEMASIADAS_POSICIONES` | `MaxLength(200)` de `posiciones` |
| Posición con cantidad fuera de 1 a 100000 o medidas fuera de 0,1 a 999999 | `POSICION_INVALIDA` | `CapturaPosicionRequest` |
| Sin `peso_kg` > 0 y sin posiciones con peso | `PESO_REQUERIDO` | `CapturarSolicitudValidador.ValidarLinea` |
| `peso_kg` difiere más del 1 % de `sum(cantidad × peso_kg)` de las posiciones | `PESO_INCONSISTENTE` | `CapturarSolicitudValidador.ValidarLinea` |
| `fila` repetida o negativa | `FILA_INVALIDA` | Cotifly (la exportación escribe una fila por línea) |
| `fila` mayor o igual que el **total** de filas con datos de la hoja del archivo adjunto (el `totalFilas` del análisis, que Cotifly recalcula sobre el archivo con la misma regla de Nexo; no las 1000 que trae `filas`) | `FILA_INVALIDA` | Cotifly |

| `horas_cargue`/`horas_descargue` de la fila fuera de 0 a 240 | `VALOR_FUERA_DE_RANGO` | `Range` de `CapturaLineaRequest` (HU-013.11) |
| `tipo_vehiculo`, `carroceria`, `clase_viaje`, `clase_contenedor` o `tipologia` que no es un código de catálogo (texto libre, o mayúsculas distintas) | `CODIGO_NO_CATALOGO` | Cotifly (HU-013.11, G2; la misma regla del formulario) |
| `clase_viaje = Contenedor` sin `clase_contenedor` | `CAMPO_REQUERIDO` | Cotifly (HU-013.11, G3) |
| `clase_contenedor` sin `clase_viaje = Contenedor`, o `valor_aprovisionamiento` sin un levante distinto del origen | `CAMPO_NO_APLICA` | Cotifly (HU-013.11, G3) |
| `origen`/`destino` sin id que coincide con varios municipios (o solo con uno aproximado) | `MUNICIPIO_AMBIGUO` | Cotifly (HU-013.11, G6) |
| `origen`/`destino` sin id que no está en el catálogo de municipios | `MUNICIPIO_NO_ENCONTRADO` | Cotifly (HU-013.11, G6) |

**Actualización HU-013.11 (#108).** Nexo no valida en el servidor ni las reglas condicionales ni los catálogos: resuelve o crea rutas y vehículos por texto libre. Por eso Cotifly las valida antes de guardar, con las mismas reglas del formulario (HU-013.10): códigos exactos de `GET /web/catalogos-flete` (con la lista provisional `CNT20.FT`, `CNT20.OP`, `CNT40.FT`, `CNT40.OP`), reglas condicionales de contenedor y aprovisionamiento, e ids de municipio. Si la fila no trae `origen_municipio_id`/`destino_municipio_id`, Cotifly resuelve el texto en el catálogo de `GET /web/municipios` con la regla determinista de HU-005.13 y nunca elige entre candidatos. Sin catálogo maestro de Nexo (ni copia de hasta 24 h): 503 `CATALOGOS_FLETE_NO_DISPONIBLE`; sin catálogo de municipios cuando alguna fila lo necesita: 400 `MUNICIPIOS_NO_DISPONIBLE`. Nada se guarda en esos casos. Ver `LineaLicitacionWeb` en `backend-dotnet/specs/openapi.yaml`.

## 6. Columnas que agrega la exportación (#48)

`GET /web/licitaciones/{folio}/excel` devuelve el archivo original del cliente **sin tocar sus columnas** y agrega, a la derecha de la última columna usada de la hoja mapeada, estas columnas:

| Columna agregada | Contenido | Formato |
|---|---|---|
| `Precio final Cotifly ({moneda})` | Precio de la línea con el margen aplicado (nunca el costo de Nexo) | Número; COP sin decimales, USD con 2 decimales (ADR-014) |
| `Estado costeo Cotifly` | Uno de los 7 estados de la línea: `Costeada`, `Pendiente de costeo`, `Entregada sin flete`, `Descartada en Nexo`, `Costeo vencido`, `No llegó a Nexo` o `Sin enviar` (fila del pliego entre la primera y la última línea enviada que el comercial no mandó) | Texto; las líneas sin precio con fondo amarillo |
| `Servicios conexos Cotifly` | Servicios conexos de la línea. Hoy Nexo los guarda por cotización: la celda remite a la hoja `Resumen Cotifly`, que los lista con su precio | Texto |

Y una hoja nueva `Resumen Cotifly` con el folio, el cliente, la moneda (y la TRM si es USD), el total de la licitación **sin servicios conexos** (la suma de los precios ya redondeados de cada línea, solo cuando todas tienen precio), cuántas líneas faltan por costear y los servicios conexos incluidos con su precio. Las líneas sin costeo se marcan explícitamente (`SIN COSTEO` en la columna de precio). Si la licitación no trae archivo original (se armó sin Excel), la exportación genera una hoja con los campos mapeados y las mismas columnas agregadas.

## 7. Ejemplo de mapeo

Pliego del cliente (hoja `Pliego 2026`, encabezado en la fila 3; las filas 1 y 2 son la portada):

| ITEM | CIUDAD ORIGEN | CIUDAD DESTINO | TIPO VEHÍCULO | TON | VALOR MERCANCÍA | OBSERVACIONES |
|---|---|---|---|---|---|---|
| 1 | Buenaventura | Bogotá | Tractomula | 32 | 150000000 | Carga seca |
| 2 | Cali | Cartagena | Tractomula | 28 | 90000000 | |
| 3 | Cúcuta | Bogotá | Sencillo | 8 | 20000000 | Urgente |

Mapeo que elige el comercial:

| Columna del pliego | Campo destino | Transformación en el frontend |
|---|---|---|
| ITEM | (no se mapea) | Se conserva en la exportación |
| CIUDAD ORIGEN | `origen` | Municipio del catálogo (`BUENAVENTURA`) |
| CIUDAD DESTINO | `destino` | Municipio del catálogo (`BOGOTA, D.C.`) |
| TIPO VEHÍCULO | (no se mapea, HU-021.6) | La Mesa de Costeo de Nexo define el vehículo |
| TON | `peso_kg` | × 1000 |
| VALOR MERCANCÍA | `valor_mercancia` | Número |
| OBSERVACIONES | (no se mapea) | Se conserva en la exportación |

Cuerpo que envía el frontend (parte `licitacion` de `POST /web/licitaciones`; el archivo va en la parte `archivo`):

```json
{
  "nit_cliente": "9000472281",
  "cliente_nombre": "Agencia de Aduanas JF",
  "linea_negocio": "Nacional",
  "deadline": "2026-10-20T17:00:00-05:00",
  "referencia": "Pliego 2026-04",
  "hoja": "Pliego 2026",
  "lineas": [
    { "fila": 0, "origen": "BUENAVENTURA", "destino": "BOGOTA, D.C.", "tipologia": "CargaSeca", "peso_kg": 32000, "valor_mercancia": 150000000 },
    { "fila": 1, "origen": "CALI", "destino": "CARTAGENA", "peso_kg": 28000, "valor_mercancia": 90000000 },
    { "fila": 2, "origen": "CUCUTA", "destino": "BOGOTA, D.C.", "peso_kg": 8000, "valor_mercancia": 20000000 }
  ]
}
```

Lo que Cotifly envía a Nexo (`POST /api/captura-solicitudes`): `{ clienteBp, tipo: "licitacion", deadline, referencia, lineas: [3 líneas con lineaNegocio = mapeoLineasNexo de "Nacional"] }`.

## 8. Preguntas abiertas: pendiente de aprobación de Gerencia Comercial

| # | Pregunta | Propuesta técnica mientras se decide |
|---|---|---|
| 1 | ¿Qué campos son obligatorios en una línea de licitación? Hoy Nexo solo exige `origen`, `destino` y el peso (o posiciones con peso) | Se mantiene el mínimo de Nexo. Se propone exigir también `tipo_vehiculo` (sin él Nexo no encuentra piso SICE-TAC y la línea queda en costeo manual); no se aplica hasta la aprobación |
| 2 | ¿Los servicios conexos se cotizan por línea o para toda la licitación? Nexo hoy los guarda a nivel de cotización (`CotizacionConexoDto` no tiene `lineaSolicitudId`) | Se cotizan para toda la licitación, como en Nexo. El esquema ya tiene `serviciosConexos` por línea (vacío) para no cambiar tablas si negocio decide "por línea" |
| 3 | ¿Toda la licitación lleva una sola línea de negocio (un margen) o cada fila puede tener la suya? | Una por licitación (el margen se resuelve una vez con el BP). Si negocio pide una por fila, se agrega `linea_negocio` por línea |
| 4 | ¿Se cotiza en USD alguna licitación? | Solo COP en esta iteración, igual que el costeo de HU-005 (ADR-015 §5). La exportación y el PDF ya muestran la moneda y la TRM de la cotización |
| 5 | ¿Qué se hace con las filas del pliego que el comercial no quiere cotizar (por ejemplo, rutas que Botero Soto no atiende)? | El comercial no las envía; en la exportación quedan con estado `Sin enviar` |
| 6 | Validar el formato con un pliego real del cliente | **Pendiente con negocio** (primer checkbox de #46) |
| 7 | ¿Qué línea de negocio viaja a Nexo en una licitación? Nexo tiene `LineaNegocio=Licitaciones` además de las de ruta (`PuertoAInterior`, `Nacional`...) | Hoy viaja el `mapeoLineasNexo` de la línea de negocio de Cotifly elegida por el comercial. **Pendiente de negocio y del equipo de Nexo** si debe ser `Licitaciones` |
| 8 | ¿Qué se hace con las líneas que Nexo nunca costea? En una prueba real de Nexo (HU-13.4) 3 de 15 líneas quedaron sin flete | Hoy la licitación no tiene total ni PDF hasta que todas tengan precio. Opciones: permitir excluir una línea de la cotización, o un PDF parcial que la marque como "no cotizada" |

## 9. Estado de los checkboxes de #46

| Checkbox | Estado |
|---|---|
| Conseguir un ejemplo real de un Excel de licitación | **Pendiente con negocio.** Mientras tanto se usa como referencia el pliego de prueba del repo `NEXO` (`docs/ejemplo_licitacion.xlsx`, 15 líneas, HU-13.4 de Nexo; no está en este repo) |
| Definir el mapeo exacto (columnas → campos y columnas nuevas de la exportación) | **Hecho** en este documento: el mapeo lo elige el comercial contra los campos destino de la sección 3; las columnas nuevas, en la sección 6 |
| Documentar y aprobar el formato final antes del parser | **Documentado.** La aprobación de las preguntas de la sección 8 queda con Gerencia Comercial |
