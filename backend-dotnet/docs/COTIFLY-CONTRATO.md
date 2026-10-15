# Contrato NEXO ⇄ Cotifly

Lo que Cotifly **manda a NEXO** y lo que **recibe de vuelta**, con las decisiones ya tomadas por
negocio. Es el contrato de la *captura de solicitudes*; cómo pedirle la recomendación de vehículo al
Cubicador está en [`COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md`](./COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md).

> **Fuente de verdad.** El detalle campo por campo vive en [`CONTRATO.md`](../CONTRATO.md) §3.3
> (`POST /api/captura-solicitudes`) y §3.2 (`GET /api/solicitudes/{id}`). Este documento explica
> **qué decide NEXO y por qué** para que Cotifly no tenga que adivinarlo. Si algo no coincide, manda
> `CONTRATO.md`.

Revisado contra el código el 2026-10-14 (S5-HU-17.7 / 17.8 / 18.2 / 18.3 / 20.5).

---

## 1. Dónde se hace cada cosa

```
Cotifly ──(1) POST /recomendaciones──────────► Cubicador   (qué camiones hacen falta)
Cotifly ──(2) POST /api/captura-solicitudes──► NEXO        (la solicitud, una línea por camión)
NEXO ─────(3) costea cada línea (SICE-TAC · plaza · histórico)
Cotifly ──(4) GET /api/solicitudes/{id}──────► NEXO        (ver el costeo, líneas ordenadas)
```

- El paso (1) es contra el **Cubicador directamente**, con la llave de Cotifly.
- Los pasos (2) y (4) son contra **NEXO**. NEXO **no** le pide nada de esto al Cubicador por
  Cotifly: lo que Cotifly captura llega a NEXO con los códigos que el Cubicador devolvió.

## 2. Autenticación contra NEXO

NEXO no emite tokens. La identidad es la de la persona que inició sesión en el portal y viaja en un
header con **solo su identificador**:

```
X-Portal-clsUsuario-Id: <id de la persona>
```

> El nombre real del header hoy es `X-Portal-clsUsuario-Id` (constante `HeaderIdUsuario`). Algunos
> documentos viejos dicen `X-Portal-Usuario-Id`: con ese nombre NEXO responde `401`. Está registrado
> como discrepancia abierta en `CONTRATO.md` §7.

`POST /api/captura-solicitudes` exige rol **Comercial o Admin**. Un usuario con otro rol recibe `403`.

## 3. `POST /api/captura-solicitudes` — lo que Cotifly manda

Una solicitud con N **líneas**. Los campos están en `CONTRATO.md` §3.3; estas son las reglas que importan.

### 3.1 Una línea por camión (decidido: #217, 2026-09-26)

Cuando el Cubicador recomienda varios camiones, Cotifly manda **una línea por cada camión**, no una
línea con "el camión elegido". Por cada línea:

| Campo | Qué poner |
|---|---|
| `origen` / `destino` | Los de la ruta (el mismo en las líneas del grupo). Mejor con `origenMunicipioId` / `destinoMunicipioId` (id de `GET /api/captura-solicitudes/ciudades`): sin ellos el motor de costeo no resuelve municipio |
| `tipoVehiculo` | El `codigoVehiculo` que devolvió el Cubicador (`2L2`, `C2`, `3S3`…) |
| `carroceria` | El `codigoCarroceria` que devolvió el Cubicador (`FURGON`, `ESTACAS`…) |
| `peso` | El `pesoTotalKg` **de ese camión** (es el peso con el que se costea el flete) |
| `idRecomendacionVehiculo` | El de **ese** camión (texto, máx. 64; un GUID en texto cabe) |
| `grupoRutaId` | **Un GUID que genera Cotifly**, el mismo en todas las líneas del mismo grupo (misma ruta y misma recomendación) |
| `claseViaje` | Contenedor, Cupo, Carga suelta o Extradimensionado — ver §4 |
| `posiciones[]` | **Opcional.** Ver §3.5 |

### 3.2 El orden de las líneas — NEXO numera, Cotifly no manda número

- Cotifly **no** manda un número de línea ni un campo "orden".
- NEXO numera por el **orden del arreglo `lineas`**: `lineas[0]` → 1, `lineas[1]` → 2…
- `GET /api/solicitudes/{id}` devuelve cada línea con `numeroLinea` y **ordenadas** por él (las
  líneas viejas, sin número, al final; luego por fecha de creación y por id). El orden es estable
  entre consultas, así que lo que Cotifly manda es lo que ve el comercial.
- `numeroLinea` define cuál línea de un grupo es "la primera": ahí se cuelgan los servicios conexos
  de la ruta. Por eso conviene mandar **las líneas del mismo grupo contiguas y en el orden en que
  se quieren ver**.

### 3.3 `grupoRutaId` — NEXO lo guarda, nunca lo genera

Agrupa las líneas de una misma carga heterogénea repartida en varios camiones. El contrato antes
decía "lo pone siempre Cotifly"; hoy lo pone **quien crea las líneas del grupo** (Cotifly, o el
portal al capturar una línea por camión). NEXO lo guarda tal cual. Una línea suelta lleva `null`.

### 3.4 Códigos del Cubicador — y qué pasa con uno desconocido

Los códigos de vehículo y carrocería llegan en `tipoVehiculo` y `carroceria` **tal como los devolvió
el Cubicador**. Un código que NEXO no conoce **no se rechaza**: la fila nace en **cuarentena**
(no oficial, S5-HU-18.4) y un humano decide a qué código canónico corresponde. Los nombres se
comparan sin tildes y con `_` y `-` equivalentes a espacio (`Bogotá` = `Bogota`,
`FURGON_REFRIGERADO` = `Furgon Refrigerado`).

### 3.5 Posiciones y tipo de mercancía

- `posiciones[]` es **opcional**. Cotifly puede dejar de mandarlas; NEXO las sigue aceptando.
  Si las manda, cada una lleva `largoCm`, `anchoCm`, `altoCm` y opcionalmente `cantidad`, `pesoKg`
  (unitario) y `descripcion`.
- **El código de mercancía no se manda en la captura.** Si algún día hiciera falta, iría **por
  posición** (`posiciones[].codigoTipoMercancia`, código y no uuid). Hoy Cotifly manda el tipo de
  mercancía solo al Cubicador, para pedir la recomendación.
- Regla de peso: el peso de la línea es `peso` si viene; si no, la suma de `cantidad × pesoKg` de
  las posiciones. Si vienen los dos, deben coincidir (tolerancia 1 %). Es `400 PESO_INCONSISTENTE`
  si no.

## 4. "Línea sin vehículo": solo Cupo

Una línea **sin `tipoVehiculo`** se acepta. Pero:

| Clase de viaje | Sin vehículo |
|---|---|
| **Cupo** (o sin `claseViaje`) | **No se autocostea.** Queda `PorCostear`, **sin precio**, con el motivo `"Línea sin vehículo: requiere consulta completa"`, visible en la Mesa de Costeo. Los candidatos salen como referencia; el Administrador de Tarifas elige o ingresa el valor (nunca bajo el piso). Si luego se le pone vehículo, se costea normalmente |
| **Contenedor** | Sin cambios: el vehículo se deduce por peso y clase de contenedor |
| **Carga suelta** | Sin cambios |
| **Extradimensionado** | Sin cambios |

> **Por qué solo Cupo** (decidido por Sara): sin vehículo, NEXO buscaría el piso SICE-TAC sin filtro y
> tomaría el menor valor, y la plaza caería al "mayor valor vigente" solo con una advertencia — un
> precio bajo que nadie revisa. **Conclusión práctica: mande el vehículo siempre que lo tenga.** El
> Cubicador lo da en `codigoVehiculo` de cada camión recomendado.

## 5. Lo que NEXO responde

`201 CapturaResultDto` con `Location: /api/solicitudes/{id}`:

```json
{ "id": "…", "lineas": 3, "rutasCreadas": 1, "vehiculosCreados": 0,
  "scorePrioridad": 62.5, "fletesCalculados": 2, "lineasPorCostear": 1, "costeoEnProceso": false }
```

- Hasta **10 líneas** el costeo automático corre dentro de la petición. Más de 10 corre en segundo
  plano (`costeoEnProceso: true`): se sigue por la solicitud o por los avisos de la Bandeja.
- `lineasPorCostear` incluye las pendientes de la consulta completa (también las de §4).
- Errores: `400 CLIENTE_REQUERIDO` · `CLIENTE_INVALIDO` · `CLIENTE_BP_INVALIDO` ·
  `DWCUBOS_NO_CONFIGURADO` · `ENUM_INVALIDO` · `LINEA_INCOMPLETA` · `PESO_REQUERIDO` ·
  `PESO_INCONSISTENTE`.

## 6. Decisiones ya tomadas (respuestas de NEXO a Cotifly)

| Pregunta de Cotifly | Respuesta |
|---|---|
| ¿Mandamos el código de mercancía en la captura? | **No.** Si hiciera falta, iría por posición |
| ¿Se retira el endpoint de cubicación de NEXO (`POST /api/cubicacion`)? | **No.** La pantalla de cubicar de NEXO lo usa; basta con que Cotifly deje de llamarlo y vaya directo al Cubicador |
| ¿Qué es una "línea sin vehículo"? | **Solo Cupo** (§4) |
| ¿Seguimos mandando `posiciones[]`? | Pueden dejar de mandarlas; NEXO las acepta como opcionales |
| ¿Mandamos número de orden de las líneas? | **No.** NEXO numera por el orden del arreglo (§3.2) |
| ¿Cómo llegan los códigos del Cubicador? | En `tipoVehiculo` y `carroceria`; uno desconocido nace en cuarentena (§3.4) |

## 7. Checklist para Cotifly

- [ ] Una línea por camión recomendado, con `tipoVehiculo` = `codigoVehiculo` y `carroceria` = `codigoCarroceria`.
- [ ] `peso` = `pesoTotalKg` del camión; `idRecomendacionVehiculo` = el de ese camión.
- [ ] Un `grupoRutaId` (GUID propio) por recomendación, igual en todas sus líneas; líneas del grupo contiguas en el arreglo.
- [ ] `origenMunicipioId` / `destinoMunicipioId` de `GET /api/captura-solicitudes/ciudades`.
- [ ] No mandar número de línea ni campo de orden.
- [ ] Mandar siempre vehículo en las líneas de Cupo (si no, quedan para consulta completa).
- [ ] Identidad en `X-Portal-clsUsuario-Id` (rol Comercial o Admin).
- [ ] Verificar el orden con `GET /api/solicitudes/{id}`: `numeroLinea` 1, 2, 3… en el orden enviado.

> **Ojo con un detalle del JSON de lectura:** en `GET /api/solicitudes/{id}` y en la propuesta de
> tarifa, el texto de la ruta sale hoy con la clave **`clsRuta`** (el `CONTRATO.md` la llama `ruta`).
> Es una discrepancia conocida del lado de NEXO; si Cotifly lee ese campo, que use `clsRuta`
> hasta que se corrija.
