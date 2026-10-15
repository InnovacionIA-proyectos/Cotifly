# Cómo conectarse al Cubicador (guía para Cotifly)

Guía práctica, con **lo que ya existe hoy** en el Cubicador: URL, llave, endpoints, un ejemplo
completo y los errores. Para qué hacer después con la respuesta en NEXO, ver
[`COTIFLY-CONTRATO.md`](./COTIFLY-CONTRATO.md).

> **Fuente de verdad.** El esquema exacto está en `cubicador/docs/openapi.json`; las rutas y errores,
> en `cubicador/CONTRATO.md` §3.1 y §5. Esta guía las resume y explica el porqué. Si algo no coincide,
> mandan ellos.
>
> Reemplaza a las partes de `COTIFLY-CONEXION-RECOMENDACION-VEHICULO.md` que quedaron viejas (ver
> [§9](#9-correcciones-a-la-guía-anterior)).

Estado: contrato vigente tras **S5-HU-17.7** (el request lleva `codigoMercancia`) y **S5-HU-17.8**
(el detalle por camión va por posición).

---

## 1. Lo mínimo para empezar

| Qué | Valor |
|---|---|
| **URL base** | La del Cubicador desplegado (la entrega infraestructura). En local con Docker: `http://localhost:8081` |
| **Autenticación** | `Authorization: Bearer <CUBICADOR_API_KEY_COTIFLY>` — una llave **propia de Cotifly** |
| **Formato** | JSON en **camelCase** |
| **Unidades** | **centímetros** y **kilogramos** (el cubicador rápido; no son los milímetros del motor 3D) |
| **Swagger** | `{URL_BASE}/docs` (botón *Authorize*: pegar la llave **sin** la palabra "Bearer") |
| **Esquema importable** | `cubicador/docs/openapi.json` (Postman/Insomnia o para generar un cliente) |

### La llave

- **De la llave sale el `origenSistema`**, verificado: quien tiene la llave de Cotifly *es* Cotifly.
- La genera **Cotifly** y solo tiene que ser **distinta** de la de NEXO. Se configura en el Cubicador
  como `CUBICADOR_API_KEY_COTIFLY`.
- La llave genérica `CUBICADOR_API_KEY` **no sirve** para `/recomendaciones`: identifica al servicio,
  no a un sistema.
- Va en el servidor de Cotifly, **nunca en el navegador**.

> ⚠️ **Si las llaves de NEXO y de Cotifly son iguales**, el Cubicador no puede distinguirlas y toda
> petición sale como `COTIFLY`. Ya pasó en una integración: las recomendaciones de NEXO quedaban
> guardadas como Cotifly y no se encontraban al reconsultar. Pídale a quien administra el Cubicador
> que verifique que `CUBICADOR_API_KEY_NEXO` y `CUBICADOR_API_KEY_COTIFLY` sean valores distintos.

En el lado de Cotifly, la configuración que ya sigue su patrón de `Nexo:UrlBase`:
`Cubicador__UrlBase` y `Cubicador__ApiKey` (variables de entorno), con un `HttpClient` propio
(`"Cubicador"`), **no** el compartido `"Nexo"`.

## 2. Los endpoints que Cotifly usa

| Método | Ruta | Para qué |
|---|---|---|
| `GET` | `/tipos-mercancia` | El **desplegable de tipo de mercancía**. De aquí sale el `codigoMercancia` que se manda |
| `GET` | `/vehiculos-catalogo` | El catálogo **canónico** de vehículos (`2L2`, `C2`, `2S2`, `3S3`…) |
| `GET` | `/tipos-carroceria` | El catálogo de carrocerías (`FURGON`, `ESTACAS`…) |
| `POST` | `/recomendaciones` | **Pedir la recomendación** de flota para una línea |
| `GET` | `/recomendaciones?idLineaSolicitud=&origenSistema=` | **Reconsultar** la última recomendación de esa línea |
| `GET` | `/recomendaciones/{idRecomendacionVehiculo}/mercancias` | El detalle de lo que lleva **un** camión |

Todas llevan el `Authorization: Bearer`. Los otros endpoints del Cubicador (`/api/planes-cargue`,
CRUD de catálogos, `/medidas-estandar`) **no son para Cotifly**.

## 3. El desplegable de tipo de mercancía

```
GET {URL_BASE}/tipos-mercancia
```

Cada fila trae, entre otros: `id`, **`codigoMercancia`** (`BEBIDAS`, `CAJAS_CARTON`…),
`nombreMercancia`, `categoria`, `cubicable`, `condicionadaCubicacion`, `comentarioCondicion`, `activo`.

- Muestre el `nombreMercancia`; **guarde y mande el `codigoMercancia`**, no el `id` (uuid).
- Hoy hay **31** mercancías. **Oculte solo** las `cubicable: false` (hoy ninguna, pero puede cambiar
  desde el CRUD sin tocar código) y las `activo: false`.
- **No oculte las condicionadas.** Hoy hay 7 (`VIGAS_ESTRUCTURAS`, `BOBINAS_ACERO`,
  `POSTES_CONCRETO`, `PAPEL_ROLLOS`, `VIDRIO_PLANO`, `VENTANAS_FACHADAS`, `CILINDROS_GAS`) y **no son
  un error**: la recomendación sale normal y vuelve marcada. El aviso va **después**, no en el
  desplegable (ver §5).

## 4. Pedir la recomendación

```
POST {URL_BASE}/recomendaciones
Authorization: Bearer <CUBICADOR_API_KEY_COTIFLY>
Content-Type: application/json
```

```json
{
  "idLineaSolicitud": "8f3a1c20-5b7d-4e8a-9c21-7d4e5f6a8b90",
  "origenSistema": "COTIFLY",
  "claseViajeSeleccionada": "Carga Suelta",
  "usuarioId": "1042",
  "mercancias": [
    { "codigoMercancia": "CAJAS_CARTON", "cantidad": 500, "largoCm": 40, "anchoCm": 30, "altoCm": 30, "pesoKg": 20 },
    { "codigoMercancia": "CAJAS_CARTON", "cantidad": 300, "largoCm": 60, "anchoCm": 40, "altoCm": 40, "pesoKg": 3 },
    { "codigoMercancia": "BEBIDAS",      "cantidad": 120, "largoCm": 50, "anchoCm": 40, "altoCm": 40, "pesoKg": 20 }
  ]
}
```

| Campo | Obligatorio | Nota |
|---|---|---|
| `idLineaSolicitud` | **sí** | Un texto que identifique la línea **en Cotifly**. Puede ser un **GUID temporal** generado por Cotifly: el Cubicador **no comprueba que la línea exista** (es texto libre, solo exige que no venga vacío). El vínculo duradero no es este id sino el `idRecomendacionVehiculo` |
| `origenSistema` | **sí** | `COTIFLY`. Se valida contra la llave: si no coincide, **401** |
| `claseViajeSeleccionada` | no | **Preferencia, no filtro.** El motor corre completo; si no coincide con lo marcado, lo dice en `advertencias[]` |
| `usuarioId` | no | Para auditoría. El Cubicador no lo verifica |
| `mercancias[].codigoMercancia` | **sí** | El código de `GET /tipos-mercancia`. Acepta cualquier caja y espacios de sobra |
| `mercancias[].cantidad` | **sí** | Entero ≥ 1 |
| `mercancias[].largoCm` / `anchoCm` / `altoCm` | **sí** | Decimal > 0, **por unidad** |
| `mercancias[].pesoKg` | **sí** | Decimal > 0, **por unidad** (no total) |

**Reglas que hay que tener presentes**

- **Una posición = una fila de `mercancias`.** Dos filas pueden compartir `codigoMercancia` si las
  medidas son distintas (como `CAJAS_CARTON` arriba).
- **Máximo 10.000 unidades en total por request** (`400 CUBICACION_DEMASIADO_GRANDE`).
- **No reintente el `POST` a ciegas.** Cada llamada crea una **corrida nueva** (el historial es
  *append-only*): un reintento tras un timeout puede dejar dos corridas. Si no llegó respuesta,
  reconsulte con el `GET` (§7) antes de volver a pedir.

## 5. Leer la respuesta

Siempre llega **200** cuando el motor corrió, aunque no haya vehículo: lo que antes era error ahora es
`resultado`.

### 5.1 `resultado: "ASIGNADO"` — hay flota

```json
{
  "idRecomendacion": "C17B9E44-2A31-4F6B-9D02-1E8C3A5F7B64",
  "idLineaSolicitud": "8f3a1c20-5b7d-4e8a-9c21-7d4e5f6a8b90",
  "origenSistema": "COTIFLY",
  "resultado": "ASIGNADO",
  "motivoSinSeleccion": null,
  "condicionada": false,
  "mercanciasCondicionadas": [],
  "vehiculos": [
    {
      "idRecomendacionVehiculo": "A4D2…", "numeroVehiculo": 1,
      "codigoVehiculo": "3S3", "nombreVehiculo": "Tractomula 3S3", "codigoCarroceria": "FURGON",
      "pesoTotalKg": 10900.0, "volumenTotalM3": 31.2,
      "porcentajeLlenadoVolumen": 90.0, "porcentajeLlenadoPeso": 31.0,
      "alertaBajoLlenado": false, "heterogenea": false,
      "mercancias": [
        { "indicePosicion": 0, "idTipoMercancia": "2B21…", "codigoMercancia": "CAJAS_CARTON", "cantidad": 500, "pesoKg": 10000.0, "volumenM3": 18.0 },
        { "indicePosicion": 1, "idTipoMercancia": "2B21…", "codigoMercancia": "CAJAS_CARTON", "cantidad": 300, "pesoKg": 900.0,   "volumenM3": 13.2 }
      ]
    }
  ],
  "advertencias": [],
  "extradimension": null
}
```

**Siempre presentes y nunca `null` cuando es `ASIGNADO`**: en cada vehículo, `idRecomendacionVehiculo`,
`codigoVehiculo`, `codigoCarroceria`, `pesoTotalKg`, `volumenTotalM3`, `porcentajeLlenadoVolumen` y
`porcentajeLlenadoPeso`. Si alguno falta, la respuesta está incompleta: trátela como error.

| Para qué lo usa Cotifly | Campo |
|---|---|
| Guardar en la línea del camión | `idRecomendacionVehiculo` |
| El `tipoVehiculo` de la línea en NEXO | `codigoVehiculo` |
| La `carroceria` de la línea en NEXO | `codigoCarroceria` |
| El `peso` de la línea en NEXO (el que se costea) | `pesoTotalKg` — **no** alcanza con el porcentaje |
| Mostrar el llenado | `porcentajeLlenadoVolumen`, `porcentajeLlenadoPeso`, `alertaBajoLlenado` |
| Repartir las posiciones entre camiones | `mercancias[].indicePosicion` |

**La flota puede ser mixta** (un `C2` y un `2S2`, por ejemplo): no asuma N camiones iguales.

### 5.2 Las tres reglas del detalle por camión (S5-HU-17.8)

1. `indicePosicion` es el índice **base 0** de la posición en el arreglo `mercancias` **del request**
   (en el ejemplo, `0` = las cajas de 40×30×30; `1` = las de 60×40×40).
2. **Dos filas del mismo camión pueden compartir `codigoMercancia`.** Quien agrupe por código **tiene
   que sumar**: no tome la última ni la primera. (En el ejemplo, el camión lleva 10.900 kg de
   `CAJAS_CARTON` repartidos en dos filas.)
3. `indicePosicion` llega en **`null`** al reconsultar una corrida guardada **antes** de este cambio.
   No es un error: esa corrida se guardó agrupada por código.

La respuesta **no devuelve medidas**: las tiene Cotifly, las mandó él, y el índice se las devuelve.

### 5.3 `resultado: "SIN_SELECCION_AUTOMATICA"` — no hay vehículo automático (es 200)

```json
{ "resultado": "SIN_SELECCION_AUTOMATICA", "motivoSinSeleccion": "EXTRADIMENSIONADO", "vehiculos": [],
  "advertencias": ["2 pieza(s) exceden las medidas estándar."],
  "extradimension": { "hayExtradimension": true,
    "medidasEstandar": { "largoMaxCm": 1220, "anchoMaxCm": 260, "altoMaxCm": 300, "pesoMaxKg": 34000 },
    "piezas": [ { "indicePosicion": 1, "idTipoMercancia": "9F2C…", "codigoMercancia": "VIGAS_ESTRUCTURAS",
                  "excesos": ["largo 1400.0 cm > 1220.0 cm"] } ] } }
```

Deje `tipoVehiculo`/`carroceria` **vacíos** para que el comercial los elija a mano y muestre el motivo.
Valores de `motivoSinSeleccion`: `EXTRADIMENSIONADO`, `SIN_CANDIDATOS_DIMENSIONALES`, `SUPERA_K_MAX`,
`SIN_CARROCERIA_COMPATIBLE`, `BUSQUEDA_AGOTADA` (esta última es del algoritmo, no del negocio: el
buscador se quedó sin intentos; no le diga al usuario que su carga no cabe).
`extradimension.piezas[]` también lleva `indicePosicion`: solo una de dos posiciones del mismo tipo
puede ser la que excede.

### 5.4 `condicionada: true` — es 200, y la recomendación es válida

Igual que `ASIGNADO`, más `condicionada: true` y `mercanciasCondicionadas: ["BOBINAS_ACERO", …]`. El
motor sí recomendó vehículo; avisa que la carrocería elegida es la **secundaria**. Muestre el
`comentarioCondicion` de cada una (sale de `GET /tipos-mercancia`) y mande la solicitud completa a la
Mesa de Costeo: el Cubicador no espera ninguna aprobación.

## 6. Errores

Solo para fallos reales. Formato: `{ "codigo": "...", "mensaje": "..." }`.

| HTTP | `codigo` | Cuándo | Qué hacer |
|---|---|---|---|
| 400 | `MERCANCIAS_REQUERIDAS` | Lista vacía | Validar antes de llamar |
| 400 | `MERCANCIA_DESCONOCIDA` | Un `codigoMercancia` que no está en el catálogo (nunca un default silencioso) | Refrescar el desplegable |
| 400 | `MERCANCIA_NO_CUBICABLE` | El tipo existe con `cubicable: false` (graneles, combustibles) | No ofrecerlo |
| 400 | `DIMENSIONES_INVALIDAS` | Medida o peso fuera de rango | Corregir el dato |
| 400 | `CUBICACION_DEMASIADO_GRANDE` | Más de 10.000 unidades | Partir la carga |
| 401 | `NO_AUTORIZADO` | Bearer ausente o inválido, **o** `origenSistema` que no corresponde a la llave | Revisar la llave y mandar `COTIFLY` |
| 422 | (validación del esquema) | Falta un campo o llega uno viejo (**`idTipoMercancia` ya no se acepta**) | Mandar `codigoMercancia` |
| 503 | `CUBICADOR_NO_DISPONIBLE` | Base caída u otro fallo de infraestructura | Reintentar el GET; el POST, ver §4 |

> `idTipoMercancia` en el **request** responde `422` a propósito: no conviven los dos campos. En la
> **respuesta** siguen viniendo los dos (`idTipoMercancia` y `codigoMercancia`).

## 7. Reconsultar lo que ya se calculó

```
GET {URL_BASE}/recomendaciones?idLineaSolicitud=8f3a1c20-…&origenSistema=COTIFLY
GET {URL_BASE}/recomendaciones/{idRecomendacionVehiculo}/mercancias
```

- **`origenSistema` es obligatorio en la query** y no se toma de la llave: un sistema puede necesitar
  leer lo que recomendó el otro, y un mismo `idLineaSolicitud` puede existir en los dos.
- Devuelve la **última** corrida de esa línea.
- El historial es **append-only**: recalcular crea una corrida nueva y la anterior queda intacta. El
  `idRecomendacionVehiculo` que Cotifly guardó **sigue devolviendo lo mismo**.
- Los ids del Cubicador vienen en **MAYÚSCULAS** (SQL Server devuelve así los `uniqueidentifier`); el
  del `POST` y el de la reconsulta son el mismo texto.

## 8. De la recomendación a la solicitud en NEXO

Por cada camión de `vehiculos[]`, Cotifly crea **una línea** en NEXO (decidido: #217):

| En NEXO (`POST /api/captura-solicitudes`) | Sale de |
|---|---|
| `tipoVehiculo` | `codigoVehiculo` |
| `carroceria` | `codigoCarroceria` |
| `peso` | `pesoTotalKg` |
| `idRecomendacionVehiculo` | `idRecomendacionVehiculo` |
| `grupoRutaId` | un GUID **que genera Cotifly**, igual en todas las líneas de esta recomendación |

Las reglas completas (orden de líneas, línea sin vehículo, cuarentena de códigos) están en
[`COTIFLY-CONTRATO.md`](./COTIFLY-CONTRATO.md).

## 9. Correcciones a la guía anterior

`COTIFLY-CONEXION-RECOMENDACION-VEHICULO.md` quedó parcialmente desactualizado:

| Dice | Hoy |
|---|---|
| Consultar **`GET /vehiculos`** para el desplegable de vehículo | **No.** Ese es el catálogo de *presets del motor 3D* (nombres como `"Turbo con estacas"`). El canónico, con `2L2` / `C2` / `3S3`, es **`GET /vehiculos-catalogo`** |
| Mandar `TipoMercancia` / el **id** de la mercancía | Se manda el **`codigoMercancia`** (S5-HU-17.7) |
| "Confirmar en qué header espera la API key" | Está confirmado: `Authorization: Bearer <llave>` |
| Una respuesta con el detalle agrupado por código | Ahora es **por posición** (`indicePosicion`), S5-HU-17.8 |
| El motivo de `SIN_SELECCION_AUTOMATICA` es lo único a mostrar | También existe `condicionada` (200, válida) y `extradimension.piezas[]` |
| "Falta agregar el selector de tipo de mercancía" | Sigue siendo cierto: hoy `clsItemCubicacion.TipoMercancia` va siempre en `null`. Hay que construirlo (§3) |

El resto de esa guía (cliente propio con opciones validadas, fábrica real/"NoDisponible", DTOs *wire*
privados, no lanzar excepciones hacia arriba, el punto de enganche en `clsEnvioCosteoService`, la
vista agrupada por ruta) **sigue vigente**.

## 10. Checklist de conexión

- [ ] `Cubicador__UrlBase` y `Cubicador__ApiKey` configuradas (la llave es `CUBICADOR_API_KEY_COTIFLY`, solo en el servidor).
- [ ] La llave de Cotifly es **distinta** de la de NEXO en el Cubicador.
- [ ] `HttpClient` propio `"Cubicador"`; el `POST /recomendaciones` **no se reintenta**.
- [ ] Desplegable de mercancía desde `GET /tipos-mercancia`: se manda el `codigoMercancia`; se ocultan solo `cubicable: false` y `activo: false`.
- [ ] Desplegable de vehículo desde `GET /vehiculos-catalogo` (no `/vehiculos`); de carrocería, `GET /tipos-carroceria`.
- [ ] `POST /recomendaciones` con `origenSistema: "COTIFLY"` y un `idLineaSolicitud` propio (puede ser un GUID temporal).
- [ ] El peso que se manda es **por unidad**, en **cm** y **kg**.
- [ ] Se manejan los tres resultados: `ASIGNADO`, `SIN_SELECCION_AUTOMATICA` (campos vacíos + motivo) y `condicionada` (válida + aviso).
- [ ] Al agrupar por `codigoMercancia`, se **suma**; `indicePosicion` puede ser `null` en corridas viejas.
- [ ] Una línea por camión en NEXO, con `idRecomendacionVehiculo`, `grupoRutaId` propio y `peso = pesoTotalKg`.
