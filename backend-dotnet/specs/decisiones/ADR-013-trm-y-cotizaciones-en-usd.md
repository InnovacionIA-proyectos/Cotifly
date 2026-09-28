# ADR-013 — TRM oficial y cotizaciones en dólares

- **Estado:** Aceptada (la fuente queda pendiente de confirmar con el equipo; ver Pendientes)
- **Fecha:** 2026-09-28
- **Issue / HU:** #50 (HU-012.1), #51 (HU-012.2), #52 (HU-012.3). Relacionada con ADR-003 (el LLM no pone números), ADR-004 (estado de la conversación), ADR-006 (PDF) y HU-010 (métricas en COP con `trmAplicada`).

## Contexto

HU-012 pide cotizar en dólares con la TRM del día. Las reglas del backlog (`docs/HU_Backlog_Cotifly_ClientePro.md`, HU-012) son:

- si `moneda="USD"`, el precio se convierte con la TRM del día de la cotización y esa TRM queda guardada en `Cotizaciones.trmAplicada`;
- con `moneda="COP"`, `trmAplicada` es 0 y nunca `null`;
- si la fuente de TRM no responde, no se genera la cotización en USD: error claro (no un 500) y el comercial puede reintentar;
- el PDF en USD aclara que el valor se ajusta día a día con la TRM, pero que el mínimo a pagar es el valor cotizado originalmente, con `trmAplicada` como referencia del piso.

El esquema ya tiene las columnas: `Cotizaciones.moneda` (por defecto `COP`) y `Cotizaciones.trmAplicada DECIMAL(18,4) NOT NULL DEFAULT 0` en `db/migrations_mssql/0001`. El dominio (`clsCotizacion.Crear`) ya exige TRM 0 en COP y mayor que 0 en otra moneda. No hace falta un script nuevo de esquema.

El issue #50 pide confirmar con el equipo la fuente oficial. Esa confirmación no existe todavía.

## Decisión

### 1. Fuente: datos abiertos de la Superintendencia Financiera

La TRM la certifica la Superintendencia Financiera de Colombia. Su histórico se publica en datos abiertos (Socrata):

- conjunto "Tasa de Cambio Representativa del Mercado - Histórico", `https://www.datos.gov.co/resource/32sa-8pi3.json`;
- campos `valor` (texto, ej. `"3306.86"`), `unidad` (`COP`), `vigenciadesde` y `vigenciahasta` (ej. `"2026-09-26T00:00:00.000"`);
- sin credenciales ni datos personales. La forma de la respuesta se verificó con una consulta real el 2026-09-28.

Consulta SoQL por fecha, con la fecha como literal escapado y cada parámetro codificado para URL:

```
?$select=valor,vigenciadesde,vigenciahasta
&$where=vigenciadesde <= 'AAAA-MM-DDT00:00:00' AND vigenciahasta >= 'AAAA-MM-DDT00:00:00'
&$order=vigenciadesde DESC&$limit=1
```

Así los sábados, domingos y festivos reciben la TRM del rango que los cubre (la del viernes vale hasta el lunes, por ejemplo).

### 2. Puerto, adaptador y caché

- Puerto `IClsProveedorTrm` en Aplicación (`Precio/Interfaces`): `ObtenerAsync(fecha)` devuelve `clsTrm` o `TRM_NO_DISPONIBLE`. Nunca lanza por la fuente.
- Adaptador `clsProveedorTrmSuperfinanciera` en Infraestructura, con cliente HTTP con nombre (`Trm`), la tubería estándar de resiliencia y 2 reintentos ante fallos transitorios.
- Opciones `Trm__Url` (por defecto la del conjunto) y `Trm__TiempoEsperaSegundos` (2 a 60, por defecto 10), validadas al arrancar.
- Caché `clsProveedorTrmEnCache` por fecha de negocio de America/Bogota (`IClsCalendarioNegocio`): la TRM no cambia intradía. Cada entrada vive 6 horas. Los fallos no se guardan, para que el reintento consulte de nuevo.
- Código de error nuevo `TRM_NO_DISPONIBLE` en el contrato (`ErrorNegocio`).

### 3. Conversión (HU-012.2)

- El costo siempre está en COP (Nexo o el comercial). El precio en pesos se calcula como hoy (`clsCalculadoraPrecio`, redondeo a pesos half-to-even).
- Con `moneda="USD"`: `precio = round(precio_cop / trm, 2)` half-to-even. `costo` y `utilidad` siguen en COP. La salida de `calcular_precio` agrega `moneda`, `trm_aplicada` y `precio_cop`.
- La TRM se fija al calcular el precio (el que ve el comercial en la previsualización) y es la misma que se guarda con el PDF. El LLM nunca la aporta (ADR-003).
- Con `moneda="COP"`: `trm_aplicada = 0`.
- Si la TRM falla, `calcular_precio` (y el ajuste de margen en USD) devuelve `TRM_NO_DISPONIBLE` y no hay precio en el estado: no se puede generar el PDF en USD.
- La moneda la elige el comercial: en la consola web, con el campo opcional `moneda` (`COP|USD`) de `MensajeWeb` (selector de HU-012.4); en Google Chat, cuando lo pide en su mensaje. La detalla #51.

### 4. PDF (HU-012.3)

La tabla muestra `US$` con 2 decimales. Debajo de la tabla va una nota con la TRM aplicada: el valor se ajusta día a día con la TRM vigente, pero el mínimo a pagar es el valor cotizado originalmente. La detalla #52.

## Alternativas consideradas

- **Banco de la República** (series estadísticas, archivos o servicio SOAP): publica la misma TRM, pero sin un endpoint JSON simple por fecha. Descartada por ahora.
- **Servicio SOAP de la Superintendencia** (`obtenerTasaCambioRepresentativaDelMercado`): es la misma fuente, pero con SOAP. El JSON de datos abiertos es más simple de consumir y de probar.
- **APIs comerciales de tipos de cambio:** no son la fuente oficial y algunas exigen llave. Descartadas.
- **Caché distribuida o en base de datos:** innecesaria con una réplica. Cada réplica consulta a lo sumo una vez cada 6 horas.

## Consecuencias

- La red de salida del contenedor debe llegar a `www.datos.gov.co` (HTTPS) para cotizar en USD. Sin esa salida, las cotizaciones en COP siguen funcionando y las de USD responden `TRM_NO_DISPONIBLE`.
- Socrata limita las peticiones sin token de aplicación por IP. Con la caché por día el volumen es mínimo; si hiciera falta, se agrega un `X-App-Token` como secreto.
- Las métricas de HU-010 convierten USD a COP con `trmAplicada` (`precioFinal × trmAplicada`).

## Pendientes

| Pendiente | Dueño |
|---|---|
| Confirmar con el equipo que la fuente oficial es la Superintendencia Financiera por datos abiertos (checkbox 1 de #50) | Duban con Gerencia Comercial |
| Confirmar el texto legal de la aclaración del piso mínimo en el PDF | Gerencia Comercial / jurídica |
| Abrir la salida HTTPS a `www.datos.gov.co` en el clúster (HU-006) | Infraestructura |
