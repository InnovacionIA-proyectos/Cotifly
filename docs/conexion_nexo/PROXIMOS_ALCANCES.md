# Próximos alcances

Cosas que se identificaron durante el diseño pero que **no entran en el alcance actual**. Cada una explica por qué
se dejó por fuera y qué tendría que pasar para retomarla.

---

## 1. Detalle de bultos por camión al enviar a Nexo

**Origen:** reestructuración de cotizaciones (HU-021, `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md`), integración
con el cubicador.

**Situación actual (lo que sí se hace):** a Nexo se le envía cada camión recomendado por el cubicador como una
línea con su **peso total** (el que da el cubicador en su Tabla 7, `recomendacionVehiculo`), sin el detalle de
bultos (`posiciones[]`). Nexo lo acepta: su regla es que la línea traiga el peso total **o** los bultos con peso,
no exige los bultos (ver `clsSolicitudCosteo.ValidarPeso`, que replica `CapturarSolicitudValidador.ValidarLinea`
de Nexo). El acomodo de la carga ya lo hizo el cubicador, así que Nexo no necesita volver a cubicar.

**Lo que queda por fuera:** mandarle a Nexo, por cada camión, qué bultos lleva y con qué medidas.

**Por qué no se hace ahora:** para armar ese detalle, Cotifly tendría que cruzar lo que el cubicador dice que va
en cada camión (Tabla 8, `recomendacionVehiculoMercancia`: tipo de mercancía, unidades, peso, volumen) con las
medidas que capturó el comercial en `CotizacionMercancias`. Ese cruce es ambiguo si el comercial pone dos filas
del mismo tipo de mercancía con medidas distintas (ej. cajas de naranja grandes y pequeñas): la Tabla 8 diría
"10 cajas de naranja" sin decir cuáles.

**Qué haría falta para retomarlo:**
1. Que alguien lo necesite de verdad — por ejemplo, que la Mesa de Nexo pida ver los bultos de cada camión para
   costear o revisar a mano.
2. Pedirle al equipo del cubicador una columna en la Tabla 8 con el id de la fila de mercancía de Cotifly de la
   que salió (Cotifly se lo envía en `POST /recomendaciones` y el cubicador lo devuelve). Con eso el cruce deja
   de ser ambiguo.
3. Armar el `posiciones[]` de cada línea al enviar a Nexo con ese cruce, cuidando la regla de Nexo: si se envían
   bultos con peso, la suma no puede diferir más del 1 % del peso total de la línea.
