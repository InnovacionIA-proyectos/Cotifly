# Contrato NEXO ⇄ Cotifly

> **Estado:** vigente (contrato externo) · **Dueño:** equipo de NEXO; copia de referencia para Cotifly (HU-023) · **Actualizado:** 2026-11-06 (§8, HU-044.1)

Lo que Cotifly **manda a NEXO** y lo que **recibe de vuelta**, con las decisiones ya tomadas por
negocio. Es el contrato de la *captura de solicitudes*; cómo pedirle la recomendación de vehículo al
Cubicador está en [`COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md`](./COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md).

> **Fuente de verdad.** El detalle campo por campo vive en `CONTRATO.md` del repositorio de NEXO, §3.3
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

> **Nota de Cotifly (HU-030.1, Cotifly#184, 2026-10-20; agregada por Cotifly, no es texto de NEXO).** El `peso` de la línea
> incluye la tara y NEXO no la suma (NEXO#308). Cotifly lo aplica **solo a las cargas contenerizadas** (`claseViaje = Contenedor`):
> el comercial escribe por ruta el peso total con tara y Cotifly manda en cada línea de esa ruta `peso` = el peso con tara (con
> varios camiones, el peso de carga del camión más una parte igual de la tara; la suma de las líneas es el peso con tara de la ruta).
> **Actualización #250 (2026-11-05, contrato único de NEXO, DU §8):** `peso` es el total con tara y NEXO solo responde
> `PESO_INCONSISTENTE` si la suma `cantidad × pesoKg` de las posiciones **supera** `peso` en más de 1 % (un peso mayor es válido). Con
> un solo camión, la línea con tara viaja ahora **con sus `posiciones[]`**; con varios camiones, sin ellas (antes, por el 1 % simétrico
> de NEXO#308, ninguna línea con tara llevaba posiciones). La tara sigue siendo solo de Contenedor. Sin peso
> con tara, y en las demás clases de viaje, Cotifly manda lo de siempre (con un camión, el peso de carga y las posiciones; con
> varios, el peso de cada camión sin posiciones). Lo que va al Cubicador (peso por unidad de cada mercancía, sin tara) no cambia.

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

### HU-036.4 (#208): la cotización finalizada y la lectura del costeo

Cotifly sigue leyendo `GET /api/solicitudes/{id}` igual con la cotización finalizada en Cotifly (el comercial aceptó el precio): deriva el
estado (por ejemplo, de `Borrador` a `Entregada`), pero **no** cambia el precio ni los costos guardados aunque la Mesa de Costeo entregue
una línea con otro valor; lo audita y avisa al comercial, que crea una versión nueva si quiere cotizar con el costo actual. Para Nexo no
cambia nada del contrato. Pendiente de acordar con Nexo: subir la edad máxima del polling (`CosteoNexo:EdadMaximaHoras`, hoy 72 h) por
encima de lo que tarda la Mesa en la práctica.

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

## 8. Importación inteligente del Excel (HU-044.1, #252, ADR-028)

Cotifly **reutiliza** la importación inteligente de NEXO: no la replica ni guarda nada propio (sin tablas, sin copia de plantillas,
recetas ni historial). Detalle campo por campo en `CONTRATO.md` de NEXO (rama `QA`): §3.3 (captura de solicitudes) y §3.19.1 (diccionario
por cliente). Revisado el 2026-11-06 contra `CONTRATO.md` de NEXO y el documento de lectura de Excel con IA. Que el PR #433 de NEXO
(`leer-estructura` y `recetas-lectura`) esté en NEXO QA está **pendiente de confirmar**.

```
Cotifly ──POST leer-estructura (multipart)──► NEXO   (opcional: Excel de formato complejo → tablas normales + receta)
Cotifly ──POST sugerir-mapeo────────────────► NEXO   (plantilla del cliente → diccionario → IA)
Cotifly ──POST aplicar-mapeo────────────────► NEXO   (líneas listas + errores por fila; sin IA)
Cotifly ──POST normalizar-valores───────────► NEXO   (vehículo, carrocería, línea de negocio, ciudad)
Cotifly ──PUT plantillas-mapeo / PUT recetas-lectura──► NEXO   (lo que el comercial confirmó, por cliente)
Cotifly ──POST /api/captura-solicitudes─────► NEXO   (la captura de siempre, §3)
```

| Endpoint de NEXO | Body / query que manda Cotifly | Respuesta |
|---|---|---|
| `POST /api/captura-solicitudes/sugerir-mapeo` | `{ clienteNit?, encabezados[1..100], filas? }` | `SugerenciaMapeoDto` |
| `POST /api/captura-solicitudes/aplicar-mapeo` | `{ clienteNit?, columnas[{ indice, campo, transformacion? }], filas[..5000], primeraFila? }` | `ResultadoAplicarMapeoDto` |
| `POST /api/captura-solicitudes/normalizar-valores` | `{ clienteNit?, valores[{ campo, valor }] }` | `ResultadoNormalizacionDto` |
| `PUT /api/captura-solicitudes/plantillas-mapeo` | `{ clienteNit, nombre?, encabezados, columnas }` | `201`/`200 PlantillaMapeoDto` |
| `GET /api/captura-solicitudes/plantillas-mapeo` | `?clienteNit=` (siempre) | `PlantillaMapeoDto[]` |
| `DELETE /api/captura-solicitudes/plantillas-mapeo/{id}` | | `204` · `404 PLANTILLA_NO_ENCONTRADA` |
| `GET /api/diccionario-cliente` | `?clienteNit=&incluirGenerales=&tipo=&q=&soloActivos=` | `TerminoClienteDto[]` |
| `GET /api/diccionario-cliente/opciones` | | `{ tipos, camposDestino, unidades, contextos }` |
| `POST /api/diccionario-cliente` | `{ clienteNit, termino, tipo, concepto, contexto?, ejemplo?, activo }` (nunca `clienteId`) | `201 TerminoClienteDto` · `409 TERMINO_DUPLICADO` |
| `PUT /api/diccionario-cliente/{id}` | `{ termino, tipo, concepto, contexto?, ejemplo?, activo }` (el cliente no cambia) | `200 TerminoClienteDto` · `404 TERMINO_NO_ENCONTRADO` |
| `DELETE /api/diccionario-cliente/{id}` | | `204` · `404 TERMINO_NO_ENCONTRADO` |
| `POST /api/captura-solicitudes/leer-estructura` | multipart: `archivo`, `clienteNit?`, `instrucciones?`, `receta?` (texto JSON) | `LecturaEstructuraDto` |
| `PUT /api/captura-solicitudes/recetas-lectura` | multipart: `archivo`, `receta`, `clienteNit`, `nombre?`, `instrucciones?` | `201`/`200 RecetaGuardadaDto` |
| `GET /api/captura-solicitudes/recetas-lectura` | `?clienteNit=` (siempre) | `RecetaGuardadaDto[]` |
| `DELETE /api/captura-solicitudes/recetas-lectura/{id}` | | `204` · `404 RECETA_NO_ENCONTRADA` |

Decisiones que fijan cómo llama Cotifly:

- **Identidad:** `X-Portal-clsUsuario-Id` del comercial, como en §2. Sin `idUsuarioExterno` no se llama. Roles en NEXO: `Comercial` o
  `Admin` (el `GET` del diccionario, cualquier rol válido).
- **Cliente por NIT:** `clienteNit` normalizado (sin puntos, espacios ni dígito de verificación). Nunca `clienteId`. Los `GET` de plantillas
  y recetas van siempre con el NIT: sin él NEXO devolvería los de todos los clientes.
- **Receta:** JSON opaco. Se reenvía exactamente como llegó.
- **Reintentos:** una vez, solo `aplicar-mapeo` y los `GET`. `sugerir-mapeo`, `normalizar-valores`, `leer-estructura` y `PUT recetas-lectura` pueden
  gastar IA y cuentan para el tope: nunca se reintentan (tampoco los demás `PUT` ni los `DELETE`).
- **Tiempos:** 90 s por intento (`ImportacionInteligente__TimeoutSegundos`) y 240 s (mínimo 180) en `leer-estructura` y `PUT recetas-lectura`
  (`ImportacionInteligente__TimeoutLecturaSegundos`): NEXO hace hasta 3 intentos de IA de 60 s y luego lee sin IA. El cuerpo de la respuesta
  se lee dentro del mismo tiempo y con un tope de `ImportacionInteligente__LimiteRespuestaMegabytes` (20 MB).
- **Sin IA:** `200` con `iaUsada = false` y `motivoSinIa`. No es un error.
- **`429 IA_TOPE_DIARIO`:** el cupo diario de IA del comercial. Cotifly lo devuelve como `IA_TOPE_DIARIO` con el mensaje de NEXO y **no**
  activa el bloqueo global de lecturas de NEXO (ese es solo para el `429 DEMASIADAS_SOLICITUDES` de catálogos y bandeja).
- **Otros errores:** los 400/404 de la tabla de NEXO llegan con el mismo código y mensaje; `VALIDATION_ERROR` u otro 4xx →
  `COSTEO_NEXO_RECHAZADA`; 401/403 → `NEXO_SIN_PERMISO`; 413 → `ARCHIVO_DEMASIADO_GRANDE`; red, tiempo, 5xx o el 429 por minuto →
  `NEXO_NO_DISPONIBLE`.

> **Diccionario por NIT (NEXO QA desde el 2026-11-06):** con `clienteNit`, `GET /api/diccionario-cliente` responde los términos de
> ese cliente más los generales. Cotifly lo pide siempre con el NIT de la licitación al editar o borrar (para comprobar que el término
> es del cliente y no general) y sin NIT solo muestra los generales. Crear, editar y borrar: rol `Comercial` en NEXO y rol activo
> `comercial` en Cotifly; ninguna escritura se reintenta.
