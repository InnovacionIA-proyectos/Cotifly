# ADR-029 — `normalizar-valores` devuelve la línea de negocio de Cotifly (HU-044.4)

- **Estado:** Propuesta (2026-11-06). Issue #255 (HU-044.4), milestone HU-044. Pendiente del visto bueno del PR.
- **Relacionadas:** ADR-028 (Cotifly reutiliza la importación inteligente de NEXO, #252), #278 (contrato único de NEXO, fase 0: la lista
  cerrada de seis líneas, `clsLineasNegocioNexo`), HU-031.1 (#194, `GET /web/alias/resolver`), plan
  `PLAN_NEXO_CONTRATO_20261104.md` (fase 3: «la #255 suma `lineaNegocio`»).

## Contexto

`POST /web/licitaciones/normalizar-valores` reenvía a `normalizar-valores` de NEXO los textos libres del Excel: vehículo, carrocería,
ciudad y, desde el contrato único del 2026-11-04 (DU §6), la línea de negocio. Para la línea, NEXO resuelve con el diccionario del cliente,
los sinónimos `impo`/`expo` y, al final, la IA con sus **seis** líneas (`Nacional`, `Puerto a interior`, `Interior a puerto`,
`Devolución de contenedor`, `Andino`, `Licitaciones`).

Pero en una licitación cada ruta lleva su línea de negocio **de Cotifly** (decisión del usuario del 2026-11-05: la de cada ruta, no
`Licitaciones`). Esa es la que tiene margen (`MargenesPolitica`) y la que valida `POST /web/licitaciones`; a NEXO viaja su
`mapeoLineasNexo`. Las líneas de Cotifly no son las de NEXO. Las seis vigentes (migración 0009 y `db/seeder/01_lineas_negocio.sql`) son
`Nacional` → `Nacional`, `Importacion` → `PuertoAInterior`, `Exportacion` → `InteriorAPuerto`, `Internacional` → `Andino`,
`DevolucionContenedor` → `DevolucionContenedor` y `Distribucion`, sin línea en NEXO (mapeo `NULL`); y `Licitaciones` de NEXO no es una línea
de Cotifly. Devolver al front la línea de NEXO lo obligaría a hacer el mapeo inverso y
dejaría a la IA de NEXO decidir, por ejemplo, que «Distribución» es `Nacional` (otro margen).

## Decisión

1. **`codigo` de `linea_negocio` es la línea de Cotifly**, la que el front pone en `lineas[].linea_negocio`. `linea_nexo` informa la línea
   de NEXO de la que sale (o la del mapeo de la línea de Cotifly).
2. **Primero Cotifly.** Un texto que ya es el nombre de una línea de Cotifly (misma regla que el alta: `clsReglasMargen.MismaLinea`, sin
   tildes ni mayúsculas) se resuelve sin llamar a NEXO, con `origen = linea_cotifly`. Inactiva o con un mapeo fuera de las seis: queda
   `sin_resolver` con el motivo del alta en `nota`. Es la opción conservadora para «Distribución» mientras NEXO no diga qué hace con ella.
3. **Después NEXO, validado.** Lo que NEXO devuelve se valida con la lista cerrada de la fase 0 (`clsLineasNegocioNexo.Canonica`): fuera
   de las seis, `sin_resolver`. Dentro, se busca la línea **activa** de Cotifly cuyo `mapeoLineasNexo` es esa: una → `resuelto` con ese
   `codigo` (aunque NEXO dijera `ambiguo`: nunca hay un `ambiguo` sin opciones); ninguna → `sin_resolver` con la explicación
   (`Licitaciones`, con el mismo mensaje que el alta: es la línea de NEXO y no da margen); varias → `ambiguo` con `lineas_candidatas`.
   Nunca se elige una entre varias. En todos los campos, un `estado` de NEXO fuera de los tres del contrato es `sin_resolver` y la
   `confianza` se acota a [0, 1].
4. **Un resultado por valor pedido.** NEXO agrupa los textos que para él son iguales; Cotifly devuelve uno por cada valor distinto de la
   petición (mismo campo y texto sin espacios en los extremos), en su orden, emparejado por campo y texto (exacto y luego sin tildes,
   mayúsculas ni signos). Si NEXO no devolvió alguno: `sin_resolver`.
5. **Validación local con los límites y códigos de NEXO** (1000 valores, 150 caracteres, campos cerrados en snake_case): una petición mal
   armada no llama a NEXO ni gasta el cupo de IA del comercial. La clase de contenedor sigue por `GET /web/alias/resolver` (ADR-028 §8):
   pedirla aquí es `CAMPO_INVALIDO` con un mensaje que lo dice.
6. **El cliente.** Se manda el NIT de la licitación si viene (opcional, como en NEXO). Se valida siempre en local, con la regla del
   adaptador (máx. 30 caracteres normalizado, con al menos una letra o dígito), aunque todo se resuelva sin NEXO: la respuesta no
   depende del contenido. Vehículo, carrocería y ciudad pasan tal cual
   (`municipio_id` es el id de NEXO, el mismo que ya usa Cotifly; `clsMunicipiosLicitacion` sigue validando al crear).

## Consecuencias

- El front recibe un valor que `POST /web/licitaciones` acepta sin traducir nada, y la IA de NEXO no puede cambiar el margen de una ruta
  sin que una línea de Cotifly lo respalde.
- Si soporte cambia `mapeoLineasNexo`, la traducción cambia con él (se lee en cada petición).
- Un sinónimo que el cliente usa para una línea sin equivalente en NEXO («Distri») no se resuelve: queda para elegir a mano.

## Pendiente de decisión (preguntas abiertas con NEXO, plan §Preguntas 5)

- ¿La normalización de `lineaNegocio` depende del cliente? Hoy se manda el NIT cuando lo hay (lo que pide la issue).
- ¿Qué hace NEXO con «Distribución»? Cotifly no le pregunta: si el texto es una línea de Cotifly, se resuelve en Cotifly.
