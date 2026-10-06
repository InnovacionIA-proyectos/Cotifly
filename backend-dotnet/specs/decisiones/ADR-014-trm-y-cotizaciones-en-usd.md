# ADR-014 — TRM del día y cotizaciones en dólares

- **Estado:** Aceptada. La revisión del 2026-10-05 (fuente DolarApi con respaldo, nota del PDF y licitaciones en USD) la pidió negocio; ver "Revisión del 2026-10-05".
- **Fecha:** 2026-09-28, revisada el 2026-10-05
- **Issue / HU:** #50 (HU-012.1), #51 (HU-012.2), #52 (HU-012.3). Relacionada con ADR-003 (el LLM no pone números), ADR-004 (estado de la conversación), ADR-006 (PDF), ADR-017 (licitaciones) y HU-010 (métricas en COP con `trmAplicada`).

## Contexto

HU-012 pide cotizar en dólares con la TRM del día. Las reglas del backlog (`docs/HU_Backlog_Cotifly_ClientePro.md`, HU-012) son:

- si `moneda="USD"`, el precio se convierte con la TRM del día de la cotización y esa TRM queda guardada en `Cotizaciones.trmAplicada`;
- con `moneda="COP"`, `trmAplicada` es 0 y nunca `null`;
- si la fuente de TRM no responde, no se genera la cotización en USD: error claro (no un 500) y el comercial puede reintentar;
- el PDF en USD lleva una nota sobre la TRM (texto definido por negocio el 2026-10-05, §4).

El esquema ya tiene las columnas: `Cotizaciones.moneda` (por defecto `COP`) y `Cotizaciones.trmAplicada DECIMAL(18,4) NOT NULL DEFAULT 0` en `db/migrations_mssql/0001`. El dominio (`clsCotizacion.Crear`) ya exige TRM 0 en COP y mayor que 0 en otra moneda. No hace falta un script nuevo de esquema.

## Decisión

### 1. Fuentes: DolarApi con respaldo en la Superintendencia Financiera

Desde el 2026-10-05 (requisito de negocio) la fuente principal es **DolarApi** y la de respaldo, los **datos abiertos de la Superintendencia Financiera**. `Trm__Proveedor` elige la cadena:

| `Trm__Proveedor` | Cadena |
|---|---|
| `dolarapi_con_respaldo` (por defecto) | DolarApi; si falla, la Superintendencia Financiera |
| `dolarapi` | Solo DolarApi |
| `superfinanciera` | Solo la Superintendencia Financiera (comportamiento anterior al 2026-10-05) |

**DolarApi** (`Trm__UrlDolarApi`, por defecto `https://co.dolarapi.com/v1/trm`), sin credenciales:

```json
{"unidad":"COP","nombre":"TRM","valor":3273.49,"fechaActualizacion":"2026-10-05T20:02:00.074Z"}
```

**Verificación empírica (2026-10-05, 15:37 en Bogotá, solo GET de lectura).** Se consultaron a la vez las dos fuentes:

| Fuente | Respuesta |
|---|---|
| `co.dolarapi.com/v1/trm` | `valor` 3273.49, `fechaActualizacion` `2026-10-05T20:02:00.074Z` (15:02 en Bogotá, 35 minutos antes de la consulta) |
| `datos.gov.co` (32sa-8pi3), 4 filas más recientes | 3273.49 vigente del 2026-10-03 al 2026-10-05; 3307.73 el 2026-10-02; 3312.84 el 2026-10-01; 3341.23 el 2026-09-30. La del 2026-10-06 todavía no estaba publicada |

Conclusiones:

- `fechaActualizacion` es la hora en que DolarApi **refrescó** el dato, no la fecha de vigencia de la TRM: la TRM que entregaba ese lunes es la del rango sábado a lunes, publicada el viernes.
- El valor es "la última TRM certificada publicada". La Superintendencia publica en la tarde, después del cierre del mercado, la TRM del día hábil siguiente; a partir de ese momento DolarApi entrega la de mañana con `fechaActualizacion` de hoy. Con la respuesta sola no se puede distinguir una de otra.

**Regla (para usar siempre la TRM vigente en la fecha pedida).** DolarApi solo se acepta para la fecha pedida si su `fechaActualizacion` cae ese mismo día en Bogotá **antes de las 12:00** (`clsProveedorTrmDolarApi.HoraCorte`), cuando la TRM de mañana todavía no puede estar publicada. Una `fechaActualizacion` de la tarde, de otro día o futura es `TRM_NO_DISPONIBLE` ("puede ser la TRM de otra vigencia") y la cadena usa la Superintendencia, que consulta la fila cuya vigencia cubre la fecha pedida. Si el respaldo también falla, `TRM_NO_DISPONIBLE`. Como la caché fija la primera TRM válida hasta el fin del día (§2), en un día con cotizaciones en la mañana toda la jornada usa la de DolarApi; si la primera es de la tarde, la de la Superintendencia.

Validaciones de `clsProveedorTrmDolarApi` (cualquier fallo es `TRM_NO_DISPONIBLE`, nunca una excepción):

- `unidad` debe ser `COP` y `nombre` debe ser `TRM`;
- `valor` (número o texto), redondeado a 4 decimales half-to-even, debe estar en [500, 20000] y a no más de ±10 % de la última TRM válida conocida por la réplica (`clsMemoriaTrm`, la registra la caché con cada TRM que entrega, de cualquier fuente); sin TRM previa no se compara;
- `fechaActualizacion` (instante ISO 8601) dentro de la ventana segura de la regla anterior;
- cuerpo de máximo 16 KB;
- HTTP distinto de 2xx, red, tiempo agotado, circuito abierto o JSON inválido también son `TRM_NO_DISPONIBLE`.

DolarApi no publica rangos de vigencia: la TRM se toma como vigente solo el día pedido (`VigenciaDesde = VigenciaHasta = fecha`). Así la regla de "TRM vencida" del estado de la conversación consulta de nuevo al día siguiente.

**Superintendencia Financiera** (respaldo), datos abiertos (Socrata):

- conjunto "Tasa de Cambio Representativa del Mercado - Histórico", `https://www.datos.gov.co/resource/32sa-8pi3.json` (`Trm__Url`);
- campos `valor` (texto, ej. `"3306.86"`), `unidad` (`COP`), `vigenciadesde` y `vigenciahasta` (ej. `"2026-09-26T00:00:00.000"`);
- sin credenciales ni datos personales. La forma de la respuesta se verificó con una consulta real el 2026-09-28.

Consulta SoQL por fecha, con la fecha como literal escapado y cada parámetro codificado para URL:

```
?$select=valor,vigenciadesde,vigenciahasta
&$where=vigenciadesde <= 'AAAA-MM-DDT00:00:00' AND vigenciahasta >= 'AAAA-MM-DDT00:00:00'
&$order=vigenciadesde DESC&$limit=1
```

Así los sábados, domingos y festivos reciben la TRM del rango que los cubre (la del viernes vale hasta el lunes, por ejemplo).

**Fuente en la trazabilidad.** `clsTrm.Fuente` (`dolarapi` o `superfinanciera`) viaja a la salida de `calcular_precio` (`trm_fuente`), que queda en la auditoría de la herramienta. Cuando la TRM sale del respaldo, `clsProveedorTrmConRespaldo` deja un warning con la fuente usada y el motivo del fallo de la principal; si las dos fallan, `TRM_NO_DISPONIBLE` con los dos motivos en el detalle.

### 2. Puerto, adaptadores y caché

- Puerto `IClsProveedorTrm` en Aplicación (`Precio/Interfaces`): `ObtenerAsync(fecha)` devuelve `clsTrm` o `TRM_NO_DISPONIBLE`. Nunca lanza por la fuente.
- Adaptadores en Infraestructura: `clsProveedorTrmDolarApi` (cliente con nombre `TrmDolarApi`) y `clsProveedorTrmSuperfinanciera` (cliente `Trm`). Los dos usan la tubería de `clsResilienciaTrm` (2 reintentos ante fallos transitorios, un tercio del tiempo por intento) con su propio tiempo total: `Trm__TiempoEsperaDolarApiSegundos` (4 s por defecto, corto para que el respaldo llegue pronto) y `Trm__TiempoEsperaSegundos` (10 s).
- Compuesto `clsProveedorTrmConRespaldo` (principal → respaldo) según `Trm__Proveedor`, registrado en `AddInfraestructura`.
- El valor se redondea a 4 decimales (half-to-even), la escala de `Cotizaciones.trmAplicada`: se guarda exactamente la TRM con que se convierte. Un valor cero o negativo (también tras redondear) es `TRM_NO_DISPONIBLE`.
- Opciones `Trm__Proveedor`, `Trm__UrlDolarApi`, `Trm__Url` (las dos solo https), `Trm__TiempoEsperaSegundos` y `Trm__TiempoEsperaDolarApiSegundos` (2 a 60), validadas al arrancar (`clsValidadorOpcionesTrm`).
- Caché `clsProveedorTrmEnCache` por fecha de negocio de America/Bogota (`IClsCalendarioNegocio`), encima de la cadena: **la primera TRM válida obtenida para un día queda fija hasta las 00:00 del día siguiente en Bogotá**, así todas las cotizaciones del día (mañana y tarde) usan la misma TRM. Una fecha ya pasada vive 6 horas. Las consultas simultáneas de una misma fecha comparten una sola llamada (también su fallo), y un fallo se recuerda 20 segundos (caché negativa) para no golpear fuentes caídas; después, el reintento consulta de nuevo.
- Circuito de cada tubería: abre con 5 intentos en 60 s si la mitad falla, y corta 30 s. Las fechas de vigencia de la Superintendencia se leen como fechas de calendario (`AAAA-MM-DD`), sin conversión de zona horaria.
- Código de error `TRM_NO_DISPONIBLE` en el contrato (`ErrorNegocio`).

### 3. Conversión (HU-012.2)

- El costo siempre está en COP (Nexo o el comercial). El precio en pesos se calcula como hoy (`clsCalculadoraPrecio`, redondeo a pesos half-to-even).
- Con `moneda="USD"`: `precio = round(precio_cop / trm, 2)` half-to-even. `costo` y `utilidad` siguen en COP. La salida de `calcular_precio` agrega `moneda`, `trm_aplicada`, `precio_cop` y `trm_fuente`.
- La TRM se fija al calcular el precio (el que ve el comercial en la previsualización) y es la misma que se guarda con el PDF, siempre que siga vigente al confirmar: si hoy ya está fuera de su rango de vigencia, el servidor recalcula con la TRM vigente y vuelve a previsualizar. El LLM nunca la aporta (ADR-003).
- Con `moneda="COP"`: `trm_aplicada = 0`.
- Si la TRM falla, `calcular_precio` (y el ajuste de margen en USD) devuelve `TRM_NO_DISPONIBLE` y no hay precio en el estado: no se puede generar el PDF en USD.
- La moneda la elige el comercial: en la consola web, con el campo opcional `moneda` (`COP|USD`) de `MensajeWeb` (selector de HU-012.4) y de `LicitacionWeb` (§5); en Google Chat, cuando lo pide en su mensaje. La detalla #51.

### 4. PDF (HU-012.3)

La tabla muestra `US$` con 2 decimales. Debajo de la tabla, en un recuadro y **sin etiqueta** (solo el texto exacto), va la nota que definió negocio el 2026-10-05, que **reemplaza** la aclaración anterior del piso mínimo en pesos:

> Esta cotización está bajo una TRM mínima de {TRM} y es de resaltar que la tarifa puede variar de acuerdo al comportamiento de la misma a la fecha de elaboración de la CPI.

- `{TRM}` es la `trmAplicada` de la cotización (la del día en que se cotizó, nunca la del día en que se genera o reimprime el PDF) con formato es-CO de 2 decimales: `$3.273,49` (`clsFormatoMoneda.FormatearTrmNota`, half-to-even).
- Los PDF en COP no llevan la nota.
- El texto vive en `clsTextosEstandar.NotaTrmMinima`. El PDF ya no imprime `precio_cop` ni "el mínimo a pagar". `precio_cop` sigue en `SalidaPrecio` y el servidor sigue verificando que coincida con el costo y el margen aplicado (coherencia de la conversión).

### 5. Licitaciones en USD (HU-011, ADR-017)

`POST /web/licitaciones` acepta `moneda` (`COP|USD`, por defecto `COP`) en `LicitacionWeb` (multipart y JSON):

- con `USD`, la TRM del día se consulta antes de crear la licitación; si no hay TRM, `TRM_NO_DISPONIBLE` (400) y no se guarda nada;
- la licitación queda en `USD` con esa `trmAplicada`. El polling del costeo calcula el precio de cada línea en pesos y lo convierte con esa TRM (`round(precio_cop / trm, 2)`); el total es la suma de las líneas en USD y los servicios conexos también van en USD;
- el PDF de pliego en USD sale con la misma nota del §4 (ya no se rechaza);
- el Excel exportado indica la moneda en la columna del precio (`Precio final Cotifly (USD)`) y la TRM en el resumen.

## Alternativas consideradas

- **Solo la Superintendencia Financiera** (la decisión del 2026-09-28): fuente oficial, pero negocio pidió el 2026-10-05 usar DolarApi como principal. Queda como respaldo y como proveedor configurable.
- **Banco de la República** (series estadísticas, archivos o servicio SOAP): publica la misma TRM, pero sin un endpoint JSON simple por fecha. Descartada por ahora.
- **Servicio SOAP de la Superintendencia** (`obtenerTasaCambioRepresentativaDelMercado`): es la misma fuente, pero con SOAP. El JSON de datos abiertos es más simple de consumir y de probar.
- **APIs comerciales de tipos de cambio con llave:** no son la fuente oficial y exigen un secreto. Descartadas.
- **Caché distribuida o en base de datos:** innecesaria con una réplica. Cada réplica consulta a lo sumo una vez cada 6 horas.

## Consecuencias

- La red de salida del contenedor debe llegar por HTTPS a `co.dolarapi.com` y a `www.datos.gov.co`. Sin ninguna de las dos, las cotizaciones en COP siguen funcionando y las de USD responden `TRM_NO_DISPONIBLE`.
- **Riesgo: DolarApi es un agregador no oficial.** Puede cambiar su formato, caerse o publicar con retraso. Lo mitigan las validaciones (unidad, rango, antigüedad), el respaldo en la fuente oficial y el warning con la fuente usada. Con `Trm__Proveedor=superfinanciera` se vuelve a la fuente oficial sin desplegar código.
- DolarApi entrega la última TRM publicada, no la de una fecha: por eso la ventana segura de la mañana (§1). El corte de las 12:00 es conservador; si negocio confirma la hora exacta de publicación de la Superintendencia, se puede ajustar.
- En el peor caso (DolarApi caído por tiempo), una cotización en USD espera `Trm__TiempoEsperaDolarApiSegundos` más el tiempo de la Superintendencia (unos 14 s con los valores por defecto); la caché negativa evita repetirlo durante 20 s.
- Si el comercial cotiza por primera vez en la tarde, la TRM sale de la Superintendencia (DolarApi ya no es seguro): sin salida a `www.datos.gov.co` esas cotizaciones fallan aunque DolarApi responda.
- Socrata limita las peticiones sin token de aplicación por IP. Con la caché por día y el respaldo el volumen es mínimo; si hiciera falta, se agrega un `X-App-Token` como secreto.
- Las métricas de HU-010 convierten USD a COP con `trmAplicada` (`precioFinal × trmAplicada`).

## Revisión del 2026-10-05

Negocio pidió: (1) DolarApi como fuente principal con respaldo en la Superintendencia, (2) el texto nuevo de la nota del PDF en USD con la TRM aplicada, que reemplaza la aclaración del piso mínimo, y (3) licitaciones en USD. Se implementó en #50 (rama `50-hu-0121-trm-dolarapi-nota-pdf`). Tras la validación y la auditoría se agregaron: la verificación empírica y la ventana segura de DolarApi, la TRM fija todo el día, `nombre = TRM`, la desviación máxima de ±10 %, el tiempo propio de DolarApi, URLs solo https, el límite de 16 KB y la nota sin etiqueta en el PDF.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Abrir la salida HTTPS a `co.dolarapi.com` y a `www.datos.gov.co` en el clúster (HU-006) | Infraestructura |
| Nueva versión (v2) en USD: hoy se siembra con la TRM de la original y, al recalcular, usa la del día; decidir cuál aplica | Gerencia Comercial |
