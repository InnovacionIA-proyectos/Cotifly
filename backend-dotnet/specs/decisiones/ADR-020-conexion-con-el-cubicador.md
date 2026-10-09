# ADR-020 — Conexión de Cotifly con el cubicador (HU-023)

- **Estado:** Aceptada (2026-10-09). Parte de la milestone HU-023 (#156 a #160).
- **Reemplaza:** el contrato provisional de la integración con el cubicador de ADR-019 (sección "Integración con el cubicador",
  HU-021.5, #133). Lo demás de ADR-019 sigue vigente.
- **Fuentes:** `docs/backlog/2026-10-08_HU_Sprint5_Cotifly_Backend.md` (HU-023, decisiones del 2026-10-08),
  `docs/conexion_nexo/2026-10-08_PETICIONES_AL_CUBICADOR.md` (peticiones P1 a P7) y la hoja de ruta del cubicador del 2026-10-08
  (contrato, sección 2).

## Contexto

La #133 construyó el adaptador del cubicador con un contrato provisional (`idTipoMercancia`, una fila por vehículo con su resultado).
El 2026-10-08 el cubicador publicó su contrato: la mercancía viaja por **código**, la respuesta es por ruta (`resultado`,
`motivoSinSeleccion`, `condicionada`) con una lista de vehículos completos, y los errores llegan como `{codigo, mensaje}`. El usuario
decidió (2026-10-09) **evolucionar** lo de la #133 hacia ese contrato, no rehacerlo: se conservan el puerto, la fábrica, la versión "no
disponible", el respaldo (`Cubicador:RespaldoSiFalla`), el presupuesto por captura, el orden de los camiones y la observabilidad.

## Decisión — HU-023.1 (#156): adaptador y catálogos

### Contrato

- **Petición** (`clsPeticionRecomendacion`): `idLineaSolicitud` (id de la ruta; `PRUEBA-<guid>` en la vista previa de HU-023.2),
  `origenSistema = COTIFLY`, `claseViajeSeleccionada` (opcional: la clase de viaje de la ruta, una preferencia), `usuarioId` (el
  `idUsuarioExterno` del comercial en texto; el planificador lo lee una vez por petición) y `mercancias[]` con `codigoMercancia`,
  `cantidad`, `largoCm`, `anchoCm`, `altoCm` y `pesoKg` (por unidad). Los nulos no viajan.
- **Respuesta** (`clsRecomendacionCubicador`): `idRecomendacion`, `resultado` (`ASIGNADO` o `SIN_SELECCION_AUTOMATICA`),
  `motivoSinSeleccion` (`EXTRADIMENSIONADO`, `SIN_CANDIDATOS_DIMENSIONALES`, `SUPERA_K_MAX`, `SIN_CARROCERIA_COMPATIBLE`),
  `condicionada`, `mercanciasCondicionadas[]`, `vehiculos[]` (`idRecomendacionVehiculo`, `codigoVehiculo`, `codigoCarroceria`,
  `pesoTotalKg`, `volumenTotalM3`, `porcentajeLlenadoVolumen`, `porcentajeLlenadoPeso`, `nombreVehiculo` opcional), `advertencias[]` y
  `extradimension`. Las tres últimas (y las mercancías condicionadas) se leen con tolerancia (texto u objeto) hasta que lleguen los
  ejemplos reales (P2).

### Contra el fallo silencioso

`clsCubicadorHttp.Validar` rechaza con **`CUBICADOR_RESPUESTA_INVALIDA`** (503) toda respuesta que no cumpla el contrato: `ASIGNADO`
sin vehículos o con un vehículo al que le falta cualquiera de los siete campos obligatorios; un `resultado` o un motivo desconocido;
`ASIGNADO` con motivo o "sin selección" sin motivo; un `idRecomendacion` que no es uuid; un peso no positivo, un volumen negativo o un
porcentaje fuera de 0 a 100 (los `CHECK` del script `02`); un código de más de 64 caracteres; JSON ilegible. El detalle (qué campo
faltó) va solo al log. **Nunca va al respaldo y no se guarda nada**: tapar un dato malo con un camión sin vehículo es justamente el
fallo silencioso que describe la hoja de ruta.

### Errores

| Cubicador | Cotifly | HTTP al cliente | ¿Respaldo? |
|---|---|---|---|
| 400 `MERCANCIAS_REQUERIDAS`, `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE`, `DIMENSIONES_INVALIDAS` | el mismo código, con un mensaje de Cotifly y el del cubicador (hasta 300 caracteres) | 400 | No |
| 401/403 `NO_AUTORIZADO` | `CUBICADOR_NO_AUTORIZADO` (log de error: revisar `Cubicador:ApiKey`). Nunca 401: no es la sesión del comercial | 503 | Sí (caída) |
| 503, 5xx, 404 (ruta sin publicar), 408, 429, red, circuito abierto, tiempo agotado | `CUBICADOR_NO_DISPONIBLE` | 503 | Sí (caída) |
| Otro 400 u otro 4xx | `CUBICADOR_NO_DISPONIBLE` (rechazo no previsto) | 503 | Sí, solo esa ruta |
| 200 fuera del contrato | `CUBICADOR_RESPUESTA_INVALIDA` | 503 | No |

### Tiempos, reintentos y salud

- `Cubicador:TiempoEsperaSegundos` pasa de 15 a **30** (el objetivo del cubicador es 10 s). El `POST` nunca se reintenta; los `GET`
  de catálogo sí (2 veces), con su tope propio `Cubicador:TiempoMaximoCatalogoSegundos`.
- **Sonda `cubicador`** en `/health/ready` (P7): con `Cubicador:RutaSalud` (vacía por defecto mientras el cubicador no publique su
  endpoint de salud) hace un `GET` liviano. Si falla, solo saca a Cotifly de servicio con `Cubicador:RespaldoSiFalla = false`; con el
  respaldo encendido queda un warning.

### Catálogos

- `GET /web/tipos-mercancia` devuelve `codigo`, `nombre`, `categoria`, `cubicable`, `condicionada_cubicacion`,
  `comentario_condicion`, `activo` e `id` (uuid, obsoleto: solo traduce el `id_tipo_mercancia` anterior).
- Nuevos `GET /web/tipos-carroceria` y `GET /web/tipos-vehiculo` (P1; mientras no exista, 503). Los tres con la caché genérica
  `clsCacheCatalogoCubicador<TFila>` (`Cubicador:MinutosCacheCatalogos`).
- **Validación de códigos** (`clsValidadorCatalogosCaptura`): `tipo_vehiculo` y `carroceria` aceptan los códigos **activos** del
  cubicador. Si el código no está ahí, o el cubicador no está configurado o no responde, se validan como el vehículo digitado del
  respaldo: el vehículo contra el catálogo maestro de Nexo y la carrocería contra la lista fija `clsCatalogosCaptura.Carrocerias`.
- **Clase de contenedor (lo que le quedaba a HU-015.1):** se quitó la lista fija de 5 códigos. Se valida contra
  `GET /api/catalogos/clases-contenedor` de Nexo (con caché; sin Nexo, `CATALOGOS_FLETE_NO_DISPONIBLE` como la clase de viaje), más la
  lista provisional de extradimensionado (G15) mientras Nexo no la formalice. Como `clases-contenedor` es un catálogo opcional de Nexo
  (si falla llega vacío), una copia sin clases tampoco valida: `CATALOGOS_FLETE_NO_DISPONIBLE`, nunca texto libre hacia Nexo.

### Planificador

`clsPlanificadorCamionesCubicador` crea un camión por vehículo de `ASIGNADO` (flota mixta), con el volumen y los porcentajes en
`clsCamionPlaneado`; `SIN_SELECCION_AUTOMATICA` es un camión sin vehículo con el peso de la ruta; "condicionada" no bloquea nada. Los
rechazos del contrato y la respuesta inválida vuelven tal cual (no van al respaldo). Durante la transición, una mercancía con el
`id_tipo_mercancia` anterior y sin código se traduce al código con el catálogo (una lectura por petición); sin código, la ruta va al
respaldo sin llamar.

### Pruebas

Cubicador falso (`clsCubicadorFalso`) con los ejemplos del contrato en `clsEjemplosContratoCubicador` (P2 armado con la hoja de ruta):
flota mixta, cada motivo de "sin selección", condicionada, extradimensión y cada error. Pruebas del adaptador: respuesta incompleta
rechazada (cada campo), `401` y `503` traducidos, sin reintento del `POST`, tiempo agotado, catálogos con caché y la sonda.

## Consecuencias

- El frontend debe mandar `codigo_mercancia` (HU-023.3) y mostrar el nombre del vehículo desde `GET /web/tipos-vehiculo`.
- Un cubicador que cambie un campo obligatorio sin avisar ya no deja camiones sin datos: la captura falla con
  `CUBICADOR_RESPUESTA_INVALIDA` y queda un warning con el campo.
- Con el respaldo encendido, una llave mala se ve como rutas sin vehículo: vigilar el log de error y `cotifly.cubicador.respaldos`.

## Pendientes

| Pendiente | Dueño |
|---|---|
| P1 `GET /tipos-vehiculo`, P2 ejemplos reales, P3 URL de QA, P7 endpoint de salud; confirmar la forma de `advertencias`, `mercanciasCondicionadas` y `extradimension` | Equipo del cubicador |
| Prueba de punta a punta contra el cubicador de QA (con Kevin) | Duban y Kevin, cuando exista P3 |
| Llave por ambiente en los secretos de Dokploy (`Cubicador__ApiKey`) | Duban (sin tocar el clúster sin autorización) |
