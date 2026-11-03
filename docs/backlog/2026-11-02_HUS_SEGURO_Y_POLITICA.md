# HU-045 y HU-046 — Seguro de la mercancía y recomendaciones de la política de seguros

> Las decisiones de HU-045 están tomadas. Las de HU-046 (🔶) se cierran en HU-046.1.
> Relacionadas: política NOGE0076 (documento interno de la empresa: no se guarda en el repo), cálculo del flete (`docs/backlog/2026-10-30_COMO_SE_CALCULA_EL_FLETE.md`) y HU-043 (#251, conexos a precio de venta y sin margen).

## Lo que ya existe y se reutiliza
- **La tabla de rutas** es `dbo_cotifly.CotizacionLineasSolicitud`. Ya tiene `valorMercancia DECIMAL(18,2) NULL`, que se captura en el formulario, en la licitación y en la edición (`datos_carga.valor_mercancia`). Hoy no se usa para ningún cálculo.
- **Hoy el precio de la ruta** es `costoFinal` (fletes + retorno + levante + conexos) con el margen aplicado. Con HU-043, los conexos salen del margen y se suman a precio de venta.
- **Los conexos** (`CotizacionServiciosConexos`) están atados al catálogo de Nexo:
  - `idServicioConexo` es obligatorio y no se puede repetir en la misma ruta;
  - se **envían a Nexo** (envío diferido, `estado_nexo`);
  - "Finalizar" se bloquea mientras haya alguno en `pendiente` (#228, #259).
- **Ya hay tablas de parámetros parecidas.** `MargenesPolitica` tiene `isActive` y `updatedAt`, y se usa de molde.
- **El último script de base de datos es el `19`**, así que el siguiente libre es el `20`.

---

# HU-045 — Valor del seguro de la mercancía por ruta

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** por definir · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Datos (Sara), Backend (Duban), Frontend (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly` (HU-045.1 a HU-045.4) y `clientepro-frontend` (HU-045-FE.1)

### Historia
**Como** comercial,
**quiero** marcar en cada ruta si incluye el seguro de la mercancía, y que Cotifly lo calcule como `valor de la mercancía × tasa vigente` (hoy 0,345 %) y lo sume al total,
**para** cotizar el seguro sin calcularlo a mano y poder cambiar la tasa sin pedir un despliegue.

### Contexto técnico
- **La tasa es un parámetro:** va en una tabla nueva y no en el código. Se guarda como fracción: 0,345 % = `0.003450`.
- **La ruta guarda tres datos nuevos:**
  - `incluyeSeguro` (sí/no), que es el check;
  - `tasaSeguroAplicada`, la tasa con la que se calculó;
  - `costoSeguro`, el valor calculado.

  La tasa se guarda **en la ruta** para que, si mañana cambia, las cotizaciones que ya existen no cambien solas. Además es la regla de la cotización finalizada (HU-036): precio congelado.
- **Fórmula:** `costoSeguro = redondear(valorMercancia × tasaSeguroAplicada, 0)`, solo si `incluyeSeguro = 1`.
- **Por política (NOGE0076):** este seguro corresponde a la **póliza específica**, la que se le vende al cliente que no tiene póliza propia. La política dice que se cobra **aparte del flete y nunca como descuento**. Por eso la recomendación es mostrarlo como una línea separada en el PDF.

### Decisiones tomadas (2026-11-02, @scalderon-bs)
1. **El seguro NO lleva margen.** Se suma **después** del margen, como los conexos con HU-043: `precio ruta = (flete + retorno + levante) con margen + conexos a precio de venta + costoSeguro`.
2. **Moneda:** el valor de la mercancía es siempre en pesos y el seguro se calcula en pesos. En una cotización en USD se convierte con la TRM de la solicitud, como el resto del precio.
3. **Dónde se marca el check:** en el **costeo**, con un endpoint propio con `If-Match`, como el ajuste de margen. Marcarlo desde la captura queda **fuera de esta HU**.
4. **Ruta sin `valor_mercancia`:** marcar el check responde `SEGURO_SIN_VALOR_MERCANCIA`.
5. **Cuándo se toma la tasa:**
   - al marcar el check, y otra vez si cambia `valor_mercancia`;
   - nunca en una cotización finalizada ni con el PDF ya enviado (409, como el margen).
6. **No se envía a Nexo** en esta HU. Queda solo en Cotifly. Se revisa si más adelante se factura desde SAP.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Incluir el seguro en una ruta
Dado una ruta con valor_mercancia de $200.000.000 y la tasa vigente en 0,345 %
Cuando el comercial marca "incluir seguro" en la ruta
Entonces costo_seguro de la ruta es $690.000
Y el precio de la ruta y el total de la cotización suben exactamente $690.000 (el seguro no lleva margen)

Escenario: Quitar el seguro
Dado una ruta con el seguro incluido
Cuando el comercial desmarca "incluir seguro"
Entonces costo_seguro queda vacío y el precio vuelve al valor sin seguro

Escenario: Cambio del valor de la mercancía
Dado una ruta con el seguro incluido
Cuando el comercial edita valor_mercancia a $300.000.000
Entonces costo_seguro se recalcula a $1.035.000

Escenario: Cambio de la tasa
Dado una cotización con el seguro calculado al 0,345 %
Cuando el equipo de Datos cambia la tasa vigente al 0,4 %
Entonces esa cotización conserva su costo_seguro y su tasa
Y las rutas que marquen el seguro desde ese momento usan el 0,4 %

Escenario: Ruta sin valor de mercancía
Dado una ruta sin valor_mercancia
Cuando el comercial intenta incluir el seguro
Entonces recibe el error SEGURO_SIN_VALOR_MERCANCIA

Escenario: Cotización finalizada o con PDF enviado
Dado una cotización finalizada o con el PDF ya enviado
Cuando el comercial intenta cambiar el seguro de una ruta
Entonces recibe el mismo 409 que el ajuste de margen y nada cambia

Escenario: El seguro se ve en la propuesta
Dado una ruta con el seguro incluido
Cuando se genera el PDF, el Excel o el correo
Entonces el seguro aparece como una línea separada "Seguro de la mercancía"
```

### Trazabilidad (event storming)
- **Comando:** Incluir o quitar el seguro de la ruta → **Evento:** Seguro de la ruta actualizado → **Política:** Recalcular el precio de la ruta y de la cotización.
- **Comando:** Editar `valor_mercancia` → **Evento:** Datos de carga editados → **Política:** Recalcular `costo_seguro` si la ruta tiene el seguro.
- **Comando (Datos):** Cambiar la tasa vigente → **Evento:** Tasa de seguro actualizada. No toca las cotizaciones que ya existen.

### Alcance en este repo (Cotifly)
1. **HU-045.1** [Datos] Tabla de la tasa del seguro y columnas del seguro en la ruta.
2. **HU-045.2** [Backend] Calcular el seguro por ruta y sumarlo al precio.
3. **HU-045.3** [Backend] Mostrar el seguro en el PDF, el Excel y el correo.
4. **HU-045.4** [QA] Validar el seguro de punta a punta.

**HU también en:** `clientepro-frontend` → HU-045-FE.1.

### [HU-045.1] [Datos] Tabla de la tasa del seguro y columnas del seguro en la ruta
**Labels:** `datos`, `base-de-datos`, `configuracion`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-045 (milestone). Depende de: ninguna.**

- [ ] Script `db/scripts/20_seguro_mercancia.sql`, no destructivo e idempotente, en transacción:
  - [ ] Tabla `dbo_cotifly.TarifasSeguro`:
    - `id UNIQUEIDENTIFIER` (`NEWID()`, como el resto del esquema);
    - `tasa DECIMAL(9,6) NOT NULL` (0.003450 = 0,345 %);
    - `vigenteDesde DATETIMEOFFSET NOT NULL`;
    - `isActive BIT NOT NULL DEFAULT 1`;
    - `updatedAt DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()`.
  - [ ] CHECK `tasa > 0 AND tasa < 1`, y un índice único filtrado para que solo haya **una tasa activa** (`WHERE isActive = 1`). Las tasas viejas se desactivan, no se borran: queda la historia.
  - [ ] Fila inicial: `tasa = 0.003450`, activa.
  - [ ] En `CotizacionLineasSolicitud`:
    - `incluyeSeguro BIT NOT NULL DEFAULT 0`;
    - `tasaSeguroAplicada DECIMAL(9,6) NULL`;
    - `costoSeguro DECIMAL(18,2) NULL`.
  - [ ] CHECKs: `costoSeguro >= 0`, y si `incluyeSeguro = 0` entonces `costoSeguro` y `tasaSeguroAplicada` quedan en NULL.
  - [ ] Las filas existentes quedan con `incluyeSeguro = 0`. Ninguna cotización cambia de valor.
- [ ] Script de operación para cambiar la tasa (desactiva la vigente e inserta la nueva).
- [ ] Probado en Docker antes de la compartida. Documentado en `db/README.md`, `BASE_DE_DATOS.md` y `docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`.

### [HU-045.2] [Backend] Calcular el seguro por ruta y sumarlo al precio
**Labels:** `backend`, `contrato`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** @dguerraBSSL
**Parte de HU-045 (milestone). Depende de HU-045.1. Relacionada con HU-043 (#251): la fórmula del precio de la ruta cambia en las dos.**

- [ ] Mapear `TarifasSeguro` y las tres columnas nuevas de la ruta (`clsCotizacionConfiguracion.cs`).
- [ ] Endpoint para incluir o quitar el seguro de una ruta en el costeo, con `If-Match`, siguiendo el patrón del ajuste de margen (decisión 3). Respeta los candados del PDF enviado y de la cotización finalizada (HU-036).
- [ ] Al incluirlo: toma la tasa activa, calcula `costoSeguro` y recalcula el precio de la ruta y de la cotización (decisión 1). Sin `valor_mercancia`, responde `SEGURO_SIN_VALOR_MERCANCIA`.
- [ ] Al editar `valor_mercancia` (HU-028), recalcula `costoSeguro` de las rutas que tienen el seguro.
- [ ] En una cotización en USD, convierte con la TRM de la solicitud (decisión 2).
- [ ] `GET .../costeo` devuelve por ruta `incluye_seguro`, `tasa_seguro` y `costo_seguro`, y también la tasa vigente para mostrarla antes de marcar.
- [ ] Auditar en `EventosHerramienta` (sin montos, como las otras mutaciones).
- [ ] Actualizar `openapi.yaml` y `docs/docs_front/CONTRATOS_API_COTIFLY_FRONTEND.md`.
- [ ] Tests:
  - incluir y quitar el seguro;
  - recalcular al editar `valor_mercancia`;
  - un cambio de tasa no toca las cotizaciones que ya existen;
  - error sin `valor_mercancia`;
  - 409 con el PDF enviado o la cotización finalizada;
  - cotización en USD.

### [HU-045.3] [Backend] Mostrar el seguro en el PDF, el Excel y el correo
**Labels:** `backend`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** @dguerraBSSL
**Parte de HU-045 (milestone). Depende de HU-045.2.**

- [ ] PDF: una línea "Seguro de la mercancía" por ruta, separada del flete (lo pide la política NOGE0076).
- [ ] Excel de la cotización o licitación: una columna con el seguro por ruta.
- [ ] Vista previa y cuerpo del correo: el total incluye el seguro (el desglose sigue la regla de HU-033).
- [ ] Ninguna exportación expone la tasa ni el costo interno más allá de lo acordado (prueba `clsCostoBrutoSinFugasTests`).

### [HU-045.4] [QA] Validar el seguro de punta a punta
**Labels:** `qa`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA)
**Parte de HU-045 (milestone). Depende de HU-045.2, HU-045.3 y HU-045-FE.1.**

- [ ] Correr todos los escenarios Gherkin de la HU en QA, con una cotización real en pesos y otra en USD.
- [ ] Cambiar la tasa en la base y verificar que las cotizaciones existentes no cambian.
- [ ] Reportar los defectos como issues nuevas, en el formato de los E2E (#226, #228).

### [HU-045-FE.1] [Frontend] Check "incluir seguro" por ruta en el costeo
**Labels:** `frontend`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-045 (milestone en `Cotifly`). Depende de HU-045.2 (contrato). No arrancar hasta que el contrato esté publicado.**

- [ ] Check "Incluir seguro" en cada ruta del costeo, que muestra la tasa vigente y el valor calculado.
- [ ] Deshabilitado, con un mensaje, si la ruta no tiene valor de mercancía. Respeta los candados (PDF enviado, cotización finalizada).
- [ ] El total de la ruta y de la cotización se actualizan con la respuesta del backend. El front no recalcula nada.
- [ ] Manejar los errores `SEGURO_SIN_VALOR_MERCANCIA` y el 409 de concurrencia o candado.

---

# HU-046 — Recomendaciones y obligaciones de la política de seguros por valor de la mercancía

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** por definir · **Épica:** E1 — Migración de Backend y Datos · **Talla:** L · **Roles:** Datos (Sara), Backend (Duban), Frontend (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly` (HU-046.1 a HU-046.4) y `clientepro-frontend` (HU-046-FE.1)

### Historia
**Como** comercial,
**quiero** que Cotifly me diga, según el valor de la mercancía de cada ruta, qué medidas de seguridad **recomienda** la política de seguros (esquema básico, GPS, candado satelital) y qué acompañante vehicular es **obligatorio**,
**para** cumplir la política NOGE0076 sin memorizarla, y no finalizar una cotización a la que le falta el acompañante que exige la política.

### Contexto técnico
- **Fuente:** política NOGE0076 v2 (06-feb-2023), documento interno de la empresa que no se guarda en el repo. Sus inconsistencias, que hay que cerrar con negocio, están listadas en HU-046.1.
- **Dato de entrada:** `CotizacionLineasSolicitud.valorMercancia`, que ya existe.
- **Las reglas viven en 4 tablas, no en el código.** Así un cambio de la política es un cambio de datos y no un despliegue:

  ```
  PoliticaSeguroTramos ──< PoliticaSeguroTramoMedidas >── PoliticaSeguroMedidas ──< PoliticaSeguroMedidaConexos
  ```

  | Tabla | Qué guarda |
  |---|---|
  | `PoliticaSeguroTramos` | Rangos de valor de la mercancía y quién autoriza. |
  | `PoliticaSeguroMedidas` | Catálogo de medidas: esquema básico, GPS, candado, acompañantes, etc. |
  | `PoliticaSeguroTramoMedidas` | Tabla intermedia: qué medida aplica en cada tramo, con nivel `recomendada` u `obligatoria`. |
  | `PoliticaSeguroMedidaConexos` | Qué **códigos** de conexo de Nexo cumplen cada medida. Es lo que permite decir "**algún** acompañante": la obligación se cumple con cualquiera de los conexos de la medida. |

- **El cruce con los conexos es por código, no por id.**
  - En Nexo hay una fila del catálogo por cada origen y destino, cada una con su propio id. Por ejemplo, `AVT_AUTO` Barranquilla → Armenia tiene un id distinto que Barranquilla → Bogotá. Por eso la tabla 4 guarda el **código** (`AVT_AUTO`).
  - Cotifly ya **lee** `codigoServicio` del catálogo de Nexo (`clsServiciosConexosNexo`), pero **no lo guarda**: `CotizacionServiciosConexos` solo tiene `idServicioConexo` y `nombreServicioConexo`.
  - Por eso esta HU agrega la columna `codigoServicio` a los conexos de la ruta.
- **Qué se cobra y qué no:**
  - Las medidas **no cobrables** (esquema básico, GPS, restricción nocturna) solo se muestran.
  - Las **cobrables** (candado, acompañantes) se muestran con un botón para agregar el conexo. Se cobran con la regla de HU-043 (#251): a precio de venta y sin margen.
- **El bloqueo se suma al que ya existe en "Finalizar".** HU-036.2 (#206) ya rechaza la finalización con `COTIZACION_NO_FINALIZABLE` y un `detalle.motivo` (`conexos_pendientes_de_leer`, `conexos_pendientes_en_nexo`). Esta HU agrega el motivo `medidas_obligatorias_faltantes`.

### Carga inicial propuesta (la valida negocio en HU-046.1)

| Tramo | Esquema básico | GPS | Candado satelital | Restricción nocturna | Acompañante | Autoriza |
|---|---|---|---|---|---|---|
| ≤ 150 M | Recomendada | — | — | — | — | Gerente Regional |
| > 150 – 250 M | Recomendada | — | Recomendada | — | — | Gerente Regional |
| > 250 – 350 M | Recomendada | Recomendada | Recomendada | — | — | Gerente Regional |
| > 350 – 500 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: parcial** | Gerente Regional |
| > 500 – 700 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: parcial o total** | Gerente Regional |
| > 700 – 1.000 M | Recomendada | Recomendada | Recomendada | Recomendada | **Obligatoria: total** | Gerencia (proyecto especial) |
| > 1.000 M | — | — | — | — | Caso a caso (solo aviso) | Gerencia + Gestión Corporativa |

Qué conexos cumplen cada medida:
- `acompanante_parcial` → `AVP_MOTO`, `AVP_MOTO_ARMADA`;
- `acompanante_total` → `AVT_AUTO`, `AVT_MOTO`, `AVT_MOTO_ARMADA`;
- `acompanante_parcial_o_total` → los 5 anteriores;
- `candado_satelital` → `PRE_BSAT` (falta confirmar).

> El PDF pone el candado y el GPS como obligatorios desde 250 M. Por decisión de producto quedan como **recomendación**. Si negocio los quiere obligatorios, se cambia el `nivel` en la tabla intermedia, sin tocar el código.

### 🔶 Decisiones que se cierran en HU-046.1
1. **"Obligatoria" bloquea "Finalizar".** ¿Se permite saltar el bloqueo con una justificación escrita, como el margen bajo el sugerido? **Recomendado: sí**, para los casos que Riesgo aprueba aparte. La justificación queda auditada.
2. **¿El tramo se calcula por ruta o por camión?** Ejemplo: una ruta de 3 camiones con $900 M en total. **Recomendado: por camión** (valor de la ruta / número de camiones que cuentan), porque la política habla del valor del despacho.
3. **Límites.** **Recomendado:** `desde` exclusivo y `hasta` inclusivo (exactamente 250 M cae en "> 150 – 250 M"), y en pesos fijos, no en SMLMV.
4. **Ruta sin `valor_mercancia`.** **Recomendado:** se muestra un aviso y no bloquea.
5. **Acompañante armado o no armado.** Hay que definir si algún tramo exige el armado. Si no, cualquiera de los dos cumple.
6. **Recomendar el seguro (HU-045).** **Recomendado:** sí, recomendar "incluir seguro" en los tramos donde la política pide la póliza específica cuando el cliente no tiene póliza propia.
7. **Cotizaciones que ya existen.** **Recomendado:** la regla aplica solo a las que **todavía no están finalizadas**. Las finalizadas no se tocan (HU-036).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Ruta en un tramo con acompañante obligatorio
Dado una ruta con un camión y valor_mercancia de $400.000.000
Cuando el comercial abre el costeo
Entonces ve el tramo "> 350 – 500 M", autoriza "Gerente Regional"
Y ve como recomendadas: esquema básico, GPS, candado satelital y restricción nocturna
Y ve como obligatoria: acompañante vehicular parcial, marcada como "pendiente"

Escenario: La obligación se cumple con cualquier conexo de la medida
Dado la ruta anterior
Cuando el comercial agrega el conexo AVP_MOTO_ARMADA
Entonces la obligación "acompañante vehicular parcial" queda "cumplida"

Escenario: No deja finalizar sin la medida obligatoria
Dado una ruta en el tramo "> 350 – 500 M" sin ningún conexo de acompañante parcial
Cuando el comercial intenta finalizar la cotización
Entonces recibe 409 COTIZACION_NO_FINALIZABLE con detalle.motivo = "medidas_obligatorias_faltantes"
Y el detalle lista la ruta y la medida que falta

Escenario: Finalizar con justificación (si se aprueba la decisión 1)
Dado la cotización del escenario anterior
Cuando el comercial finaliza enviando una justificación
Entonces la cotización se finaliza y la justificación queda auditada

Escenario: Tramo sin obligaciones
Dado una ruta con valor_mercancia de $100.000.000
Cuando el comercial abre el costeo
Entonces solo ve "esquema básico" como recomendado y nada obligatorio

Escenario: Proyecto especial
Dado una ruta con valor_mercancia de $800.000.000
Cuando el comercial abre el costeo
Entonces ve que es proyecto especial, que lo autoriza la Gerencia y que el acompañante total es obligatorio

Escenario: Cambio de la política sin despliegue
Dado que Datos cambia el candado satelital a "obligatoria" en el tramo "> 250 – 350 M"
Cuando el comercial abre el costeo de una ruta en ese tramo
Entonces el candado aparece como obligatorio, sin desplegar el backend

Escenario: Ruta sin valor de mercancía
Dado una ruta sin valor_mercancia
Cuando el comercial abre el costeo
Entonces ve un aviso de que no se puede aplicar la política de seguros, y "Finalizar" no se bloquea por esto
```

### Trazabilidad (event storming)
- **Lectura:** Requisitos de la política de la ruta (tramo, autoriza, medidas recomendadas y obligatorias, cumplidas o pendientes). Se arma con `valor_mercancia` y las 4 tablas.
- **Comando:** Agregar el conexo sugerido → **Evento:** Conexo agregado a la ruta (con su `codigoServicio`) → **Política:** La medida queda cumplida.
- **Comando:** Finalizar la cotización → **Política:** Medidas obligatorias cumplidas → **Evento:** Cotización finalizada, o rechazo con `medidas_obligatorias_faltantes`.
- **Comando (Datos):** Cambiar la política en las tablas → **Evento:** Política actualizada. Afecta solo las cotizaciones no finalizadas.

### Alcance en este repo (Cotifly)
1. **HU-046.1** [Datos] Evaluar con negocio la carga inicial de la política y cerrar las decisiones (sin cambios en la base).
2. **HU-046.2** [Datos] Las 4 tablas de la política, su carga inicial y el código del servicio en los conexos de la ruta.
3. **HU-046.3** [Backend] Calcular los requisitos de la política por ruta y bloquear "Finalizar" si falta una medida obligatoria.
4. **HU-046.4** [QA] Validar la política de punta a punta.

**HU también en:** `clientepro-frontend` → HU-046-FE.1.

### [HU-046.1] [Datos] Evaluar con negocio la carga inicial de la política de seguros y cerrar las decisiones
**Labels:** `datos`, `investigacion`, `producto`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-046 (milestone). Depende de: ninguna.**

> **Solo evaluación y propuesta.** No se crea ninguna tabla ni se cambia código. Lo aprobado aquí lo construyen HU-046.2 y HU-046.3.

- [ ] Llevar a negocio (Dirección Comercial, Gestión Corporativa y Administración de Riesgo) la **carga inicial propuesta** de la milestone, tramo por tramo, y dejar escrito qué se aprueba y qué se cambia.
- [ ] Cerrar las 7 decisiones 🔶 de la milestone (justificación, ruta o camión, límites, sin valor de mercancía, armado, recomendar el seguro, cotizaciones existentes).
- [ ] Cerrar las inconsistencias del PDF de la política:
  - tramo 1 sin seguro definido;
  - cifra con error de digitación;
  - pesos vs SMLMV;
  - tramos distintos en el acuerdo con Seguros Bolívar;
  - vigencia de los montos de 2023.
- [ ] Confirmar contra la base real de Nexo los **códigos** de los conexos de cada medida (`AVP_MOTO`, `AVP_MOTO_ARMADA`, `AVT_AUTO`, `AVT_MOTO`, `AVT_MOTO_ARMADA`) y si el candado satelital es `PRE_BSAT`.
- [ ] Dejar en este issue la tabla final aprobada (tramos, medidas, niveles y conexos), con quién la aprobó y la fecha.

### [HU-046.2] [Datos] Tablas de la política de seguros, carga inicial y código del servicio en los conexos de la ruta
**Labels:** `datos`, `base-de-datos`, `configuracion`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-046 (milestone). Depende de HU-046.1 (tabla aprobada).**

- [ ] Script `db/scripts/21_politica_seguros.sql`, no destructivo e idempotente, en transacción. Crea en `dbo_cotifly`:
  - [ ] `PoliticaSeguroTramos`:
    - `id INT IDENTITY`;
    - `nombre NVARCHAR(60)`;
    - `valorDesde DECIMAL(18,2) NOT NULL` (exclusivo);
    - `valorHasta DECIMAL(18,2) NULL` (inclusivo; NULL = sin tope);
    - `autoriza NVARCHAR(40)` con CHECK (`gerente_regional`, `gerencia`, `gerencia_gestion_corporativa`);
    - `proyectoEspecial BIT`;
    - `nota NVARCHAR(500) NULL`;
    - `isActive BIT`, `updatedAt DATETIMEOFFSET`.

    CHECK `valorHasta IS NULL OR valorHasta > valorDesde`.
  - [ ] `PoliticaSeguroMedidas`: `id`, `codigo NVARCHAR(40) UNIQUE`, `nombre NVARCHAR(100)`, `descripcion NVARCHAR(300) NULL`, `esCobrable BIT`, `isActive`, `updatedAt`.
  - [ ] `PoliticaSeguroTramoMedidas`: `tramoId` FK, `medidaId` FK, `nivel NVARCHAR(20)` con CHECK (`recomendada`, `obligatoria`), `nota NVARCHAR(300) NULL`. UNIQUE (`tramoId`, `medidaId`).
  - [ ] `PoliticaSeguroMedidaConexos`: `medidaId` FK, `codigoServicio NVARCHAR(40)` (código del catálogo de Nexo, sin FK porque es de otra base). UNIQUE (`medidaId`, `codigoServicio`).
  - [ ] En `CotizacionServiciosConexos`: la columna `codigoServicio NVARCHAR(40) NULL`. Las filas existentes quedan en NULL y el backend las completa (HU-046.3).
- [ ] Carga inicial con lo aprobado en HU-046.1: tramos, medidas, tabla intermedia y conexos de cada medida.
- [ ] Validar en el script que los tramos activos **no se solapan ni dejan huecos**. Si encuentra uno de los dos, el script se detiene sin cambiar nada (`THROW`).
- [ ] Script de operación con ejemplos para cambiar la política: cambiar el nivel de una medida, agregar un conexo a una medida, mover un límite.
- [ ] Probado en Docker antes de la compartida. Documentado en `db/README.md`, `BASE_DE_DATOS.md` y `docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md`.

### [HU-046.3] [Backend] Requisitos de la política de seguros por ruta en el costeo y bloqueo de "Finalizar"
**Labels:** `backend`, `contrato`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** @dguerraBSSL
**Parte de HU-046 (milestone). Depende de HU-046.2. Relacionada con HU-036.2 (#206, Finalizar), HU-043 (#251, conexos a precio de venta) y HU-045 (seguro).**

- [ ] Mapear las 4 tablas y la columna `codigoServicio` de `CotizacionServiciosConexos`.
- [ ] Guardar `codigoServicio` al agregar un conexo. Ya viene en el catálogo de Nexo que lee `clsServiciosConexosNexo`. Completar las filas existentes que lo tengan en NULL la próxima vez que se lea el catálogo.
- [ ] Calcular el tramo de cada ruta con `valor_mercancia`, según lo aprobado en HU-046.1 (por ruta o por camión, y regla de límites).
- [ ] En `GET /web/cotizaciones/{folio}/costeo`, devolver por ruta un bloque `politica_seguros` con:
  - el tramo y quién autoriza;
  - si es proyecto especial;
  - las medidas **recomendadas**;
  - las medidas **obligatorias**, cada una con estado `cumplida` o `pendiente` y los códigos de conexo que la cumplen (para el botón "agregar");
  - la nota del tramo;
  - un aviso si falta `valor_mercancia`.
- [ ] En "Finalizar" (HU-036.2), rechazar con `COTIZACION_NO_FINALIZABLE` y `detalle.motivo = medidas_obligatorias_faltantes` si una ruta cotizable tiene una medida obligatoria pendiente. El detalle lista cada ruta y su medida.
- [ ] Si se aprueba la decisión 1: aceptar una justificación al finalizar que salta el bloqueo, y auditarla en `EventosHerramienta`.
- [ ] Las rutas rechazadas por la Mesa de Costeo no cuentan. Las cotizaciones finalizadas no se recalculan.
- [ ] Actualizar `openapi.yaml` y `docs/docs_front/CONTRATOS_API_COTIFLY_FRONTEND.md`.
- [ ] Tests:
  - un caso por tramo;
  - valores en el límite;
  - cumplir una obligación con cada uno de los conexos de la medida;
  - bloqueo y justificación en "Finalizar";
  - un cambio en las tablas se refleja sin desplegar;
  - ruta sin `valor_mercancia`;
  - cálculo por camión (si se aprueba).

### [HU-046.4] [QA] Validar la política de seguros de punta a punta
**Labels:** `qa`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA)
**Parte de HU-046 (milestone). Depende de HU-046.3 y HU-046-FE.1.**

- [ ] Una cotización por tramo (los 7), revisando las medidas recomendadas y obligatorias y quién autoriza contra la tabla aprobada en HU-046.1.
- [ ] Valores exactamente en el límite (150, 250, 350, 500, 700 y 1.000 M).
- [ ] Cumplir la obligación con distintos conexos de la misma medida (por ejemplo, `AVP_MOTO` y `AVP_MOTO_ARMADA`).
- [ ] Intentar finalizar sin la medida obligatoria (debe rechazar) y con justificación (si se aprobó).
- [ ] Cambiar un nivel en la base y verificar que el costeo lo refleja sin desplegar.
- [ ] Reportar los defectos como issues nuevas, en el formato de los E2E (#226, #228).

### [HU-046-FE.1] [Frontend] Panel de la política de seguros en cada ruta del costeo
**Labels:** `frontend`
**Repo donde se crea este issue:** `clientepro-frontend`
**Asignado a:** Sara (Frontend)
**Parte de HU-046 (milestone en `Cotifly`). Depende de HU-046.3 (contrato). No arrancar hasta que el contrato esté publicado.**

- [ ] En cada ruta del costeo, un panel "Política de seguros" con:
  - el tramo y quién autoriza;
  - las medidas recomendadas (informativas);
  - las medidas obligatorias, con su estado cumplida o pendiente.
- [ ] Para cada medida cobrable, un botón para agregar el conexo que la cumple, reutilizando el flujo actual de agregar conexos. Si hay varios conexos posibles (por ejemplo, moto o moto armada), el comercial escoge.
- [ ] Destacar el proyecto especial (700 M o más) y el aviso de ruta sin valor de mercancía.
- [ ] En "Finalizar", mostrar el rechazo `medidas_obligatorias_faltantes` con la lista de rutas y medidas. Si se aprobó, pedir la justificación para continuar.
