# Lo que Cotifly le pide al cubicador

**Fecha:** 2026-10-08. **Para:** el equipo del cubicador.
**Sobre:** la hoja de ruta del cubicador del 2026-10-08 (`HOJA-DE-RUTA-CUBICADOR.md`), en especial su contrato (sección 2).
**Cómo se conecta Cotifly:** el navegador nunca llama al cubicador. Todo pasa por el backend de Cotifly, que llama al cubicador con su propia llave. El cubicador solo le responde a Cotifly y no hace nada en NEXO.

El contrato nos sirve tal como está. Aceptamos mandar **`codigoMercancia`** en `POST /recomendaciones` y no el uuid, y Cotifly guardará el código. Lo que sigue son peticiones y preguntas para cerrar los huecos que encontramos al revisar el código de Cotifly.

---

## 1. Peticiones

| # | Petición | Prioridad | Por qué la necesitamos |
|---|---|---|---|
| P1 | **`GET /tipos-vehiculo`** de solo lectura para Cotifly | **Alta** | El contrato solo declara mercancías y carrocerías. Cotifly **no guarda el nombre del vehículo**: lo saca de este catálogo por su código para la pantalla y el PDF. También lo usa para validar los códigos que guarda |
| P2 | **Ejemplos de respuesta de cada caso**, en JSON | Alta | Para construir el cubicador falso de las pruebas antes de que exista el real |
| P3 | **Una URL del cubicador de QA** y desde cuándo estará arriba | Alta | Sin ella no hay prueba de punta a punta (sección 5 de la hoja de ruta) |
| P4 | **Modo de prueba que no guarde**, o aceptar las corridas de prueba | Media | Cada llamada a `/recomendaciones` crea una corrida que nunca se borra |
| P5 | **Cómo evitar corridas duplicadas** si Cotifly reintenta | Media | Un tiempo agotado deja la duda de si la corrida se creó |
| P6 | **Límites de uso** | Media | Una licitación son muchas rutas, y cada una es una llamada |
| P7 | **Endpoint de salud** | Baja | Para que Cotifly lo revise en su `/health/ready` |

### P1. `GET /tipos-vehiculo`

Mismo estilo que `GET /tipos-carroceria`. Por fila: `id`, `codigoVehiculo`, `nombreVehiculo`, las carrocerías compatibles (`codigoCarroceria`) y `activo`.

Los datos ya existen (catálogo 5 de los CRUD, "Vehículos + relación"). Pero los CRUD se exponen a NEXO para administrar, no como lectura para Cotifly. Cotifly solo lee: no edita ningún catálogo.

### P2. Ejemplos de cada caso

Un JSON de request y de respuesta para cada uno:
- `ASIGNADO` con flota mixta (como el ejemplo de la sección 2.3).
- `SIN_SELECCION_AUTOMATICA` con cada `motivoSinSeleccion`: `EXTRADIMENSIONADO`, `SIN_CANDIDATOS_DIMENSIONALES`, `SUPERA_K_MAX` y `SIN_CARROCERIA_COMPATIBLE`.
- Condicionada.
- Cada error: `MERCANCIAS_REQUERIDAS`, `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE`, `DIMENSIONES_INVALIDAS`, `NO_AUTORIZADO` y `CUBICADOR_NO_DISPONIBLE`.

Si publican un OpenAPI o un JSON Schema del contrato, mejor: lo usamos para las pruebas de contrato de los dos lados.

### P3. Cubicador de QA

La URL de QA y la fecha desde la que estará disponible. También si es alcanzable desde las máquinas de desarrollo y desde el CI de Cotifly (GitHub Actions).

### P4. Llamadas de prueba

Mientras la reestructuración de Cotifly sale, el comercial va a usar un botón "Calcular cubicaje" **antes** de guardar la cotización (vista previa). Hoy cada llamada a `/recomendaciones` crea una corrida que nunca se borra. Hay dos opciones:
- Un parámetro o una ruta que calcule **sin guardar** (por ejemplo `POST /recomendaciones?persistir=false`).
- Que el cubicador **acepte** esas corridas de prueba.

¿Cuál prefieren? Si eligen la segunda, ¿les sirve que Cotifly mande un `idLineaSolicitud` provisional que se pueda reconocer como de prueba?

### P5. Reintentos y corridas duplicadas

Cotifly **no reintenta** el `POST` (justamente para no duplicar corridas). Pero si se agota el tiempo, no sabemos si la corrida quedó creada. ¿Aceptan una llave de idempotencia (por ejemplo un header `Idempotency-Key`) que devuelva la misma corrida si llega dos veces? Si no, Cotifly vuelve a llamar y la corrida anterior queda huérfana.

### P6. Límites de uso

- ¿Cuántas mercancías como máximo por llamada?
- ¿Cuántas llamadas en paralelo aguanta? Una licitación de 50 rutas son 50 llamadas, y Cotifly las haría en segundo plano.
- Confirmar el objetivo de **10 s** por respuesta. Cotifly pondrá un tiempo de espera de unos 30 s.

### P7. Endpoint de salud

Una ruta liviana (por ejemplo `GET /health`) que no consuma el motor, para que Cotifly sepa si el cubicador está arriba.

---

## 2. Preguntas sobre el contrato

1. **`codigoMercancia`.** ¿Cuál es el largo máximo y el formato? ¿Se garantiza que **nunca** cambia? Si una mercancía se desactiva, ¿`GET /tipos-mercancia` la sigue devolviendo con `activo = false`? ¿Un `POST` con ese código responde `MERCANCIA_DESCONOCIDA` o la acepta?
2. **Refrigerado = `FURGON` + alerta.** ¿En qué campo de la respuesta viene esa alerta? ¿En `advertencias[]` o en un campo del vehículo? Cotifly hoy maneja `FURGON REFRIGERADO` como carrocería aparte y tiene que dejar de hacerlo.
3. **`claseViajeSeleccionada`.** ¿Qué valores acepta? ¿Las descripciones del catálogo de clases de viaje de NEXO (`Carga Suelta`…) u otros?
4. **`usuarioId`.** Cotifly mandará el id del comercial en el almacén corporativo (`idUsuarioExterno`), en texto. ¿Les sirve?
5. **Reconsulta por línea.** `GET /recomendaciones?idLineaSolicitud=…` devuelve todas las corridas de esa ruta. ¿En qué orden? ¿Cómo se distingue la última?
6. **Errores.** ¿El formato `{ "codigo", "mensaje" }` aplica también a 401 y 503? ¿Qué devuelve si el motor supera el tiempo objetivo?
7. **Versionado.** Si el contrato cambia después del corte, ¿cómo nos enteramos? Pedimos que cualquier cambio de un campo obligatorio se avise antes y no llegue en un despliegue sin coordinar: es justo el fallo silencioso que describe la sección 5.

---

## 3. La llave de Cotifly

- La genera Cotifly, como dice la hoja de ruta. ¿Cómo se la entregamos para que la registren en el mapa `llave → sistema`? Debe ser por un canal seguro, no por chat ni por correo.
- Una llave **por ambiente** (QA y producción), distinta de la de NEXO.
- Cotifly la guarda solo en la configuración del backend (secretos de Dokploy). Nunca llega al navegador.

## 4. Fecha del corte

La hoja de ruta retira `POST /api/cubicacion` en el corte, sin endpoint de transición. Hoy el frontend de Cotifly usa la cubicación rápida de NEXO, que cambia ese mismo día. Cotifly va a preparar el cambio a `/recomendaciones` antes del corte, pero necesitamos:
- **La fecha exacta del corte**, acordada con NEXO y con Cotifly.
- El **cubicador de QA arriba con anticipación** (P3), para probar antes.

---

## 5. Aparte: lo que es de NEXO, no del cubicador

Se lo pedimos a NEXO por separado; se anota aquí para que el panorama esté completo.
- **`GrupoRutaId`** en la línea de solicitud: ya acordado (columna nueva, la pone Cotifly).
- **`idRecomendacionVehiculo`** en la línea de solicitud: la hoja de ruta dice que NEXO lo guarda, pero hay que confirmar que **`CapturaLineaRequest`** lo acepta, porque en el flujo de Cotifly es Cotifly quien se lo manda.
- **Códigos de vehículo y carrocería:** Cotifly mandará a NEXO los códigos canónicos del cubicador. Mientras NEXO converge, las combinaciones que no reconozca quedan en cuarentena.
