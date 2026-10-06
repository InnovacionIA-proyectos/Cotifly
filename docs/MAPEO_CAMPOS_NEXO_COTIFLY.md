# Mapeo de campos Nexo ↔ Cotifly (HU-005.1)

Tabla campo por campo entre el formulario "Nueva solicitud" de Nexo (`administracion_embarqa_nexo`, módulo `cabina-fletes`) y el flujo de cotización de Cotifly, construida contra el contrato real de Nexo (`CONTRATO.md`, repo `NEXO`) y el esquema actual de Cotifly. Es el insumo de HU-005.1 y la base para ampliar el formulario en HU-005.3.

**Estado: aprobado con negocio (2026-09-26).** Ver decisiones confirmadas al final.

## Tabla de mapeo

| Campo de Nexo | Equivalente en Cotifly | Tipo | Catálogo/opciones | Clasificación |
|---|---|---|---|---|
| `tipo` (spot/licitación) | No existe | string enum | `Spot`, `Licitacion` | **Nuevo** — confirmado con negocio: sí aplica igual |
| `modalidad` | No aplica | string enum | `Importacion`,`Exportacion`,`Nacional`,`Extradimensionada`,`CargaGeneral` | **No aplica** — con línea de negocio basta, es redundante |
| `deadline` | No existe | fecha | — | **Nuevo** |
| `horasCargue`/`horasDescargue` | No existe | decimal | — | **Nuevo** |
| `referencia` | No existe | string | — | **Nuevo** |
| `valorEstimado` (encabezado, uno por solicitud) | No existe | decimal | — | **Nuevo** |
| `lineaNegocio` (por línea) | `cotizaciones.linea_negocio_id` → `MargenesPolitica` | FK a catálogo propio | Nexo: `PuertoAInterior`,`InteriorAPuerto`,`Nacional`,`DevolucionContenedor`,`Andino`,`Licitaciones` | **Ya resuelto** — `MargenesPolitica.mapeoLineasNexo` (HU-002.4 Fase 2) |
| `origen`/`destino` | Texto libre hoy | string → catálogo | `GET /api/captura-solicitudes/municipios` (columna `municipio`), autocompletado simple | **Modificar** — deja de ser texto libre |
| `tipoVehiculo` | Texto libre hoy | string enum | SICE-TAC: `2`,`2_5`,`2_8`,`2L1`,`2L2`,`2L3`,`2S2`,`2S3`,`3`,`3S2`,`3S3`,`CA`,`V2`,`V3`,`V4` | **Modificar** — hardcodeado por ahora (Nexo sin endpoint propio), texto amigable en frontend |
| `carroceria` | No existe | string enum | `ESTACAS`,`ESTIBAS`,`FURGON`,`FURGON REFRIGERADO`,`PLATAFORMA`,`PORTACONTENEDORES`,`TANQUE`,`TRAYLER`,`VOLCO` | **Nuevo**, hardcodeado por ahora |
| `tipologia` (por línea) | No existe | string enum | `Contenedor`,`CargaSuelta`,`GranelSolido`,`GranelLiquido`,`Alimentos`,`Refrigerados`,`CargaSeca` | **Nuevo** — distinto de `claseViaje` |
| `claseViaje` | No existe | string enum | `Contenedor`,`Cupo`,`CargaSuelta`,`Extradimensionado` | **Nuevo**, hardcodeado por ahora — define comportamiento condicional |
| `claseContenedor` | No existe | string enum | `CNT20.IT/RE/ST`,`CNT40.HC/RE` | **Nuevo**, hardcodeado, condicional a `claseViaje="Contenedor"` |
| `exclusivo`/`consolidado` | No existe | boolean | — | **Nuevo** |
| `retornaContenedor`+`ciudadDevolucionContenedor` | No existe | boolean + municipio | — | **Nuevo**, condicional |
| `levanteContenedorDiferente`+`ciudadLevanteContenedor` | No existe | boolean + municipio | — | **Nuevo**, condicional |
| `valorAprovisionamiento` (por línea) | No existe | decimal | — | **Nuevo** |
| `peso` (por línea) | Ya existe en el form actual | decimal | — | **Ya existe, mismo nombre** |
| `valorMercancia` (por línea) | No existe | decimal | — | **Nuevo** — distinto de `valorEstimado` (ese es del encabezado) |
| `posiciones[]` (cubicaje) | No existe | array de objetos | `descripcion`,`cantidad`,`largoCm`,`anchoCm`,`altoCm`,`pesoKg` | **Nuevo** (HU-005.4) |
| Cliente (`clienteId`/`clienteBp`) | `cotizaciones.nitCliente` | string | Buscador Nexo (`GET /captura-solicitudes/clientes`) | **Ya resuelto** (HU-005.2) — ojo: crear solicitud pide `clienteId`/`clienteBp`, no el NIT directo |
| Resultado (`ganada`/`perdida`) | `cotizaciones.ganada`/`motivo_rechazo` | boolean + texto | `Ganada`,`Perdida`,`SinDefinir` | **Ya resuelto** (HU-005.14) |
| Estado (`Pendiente`/`EnCosteo`/`Entregada`/`Descartada`) | `cotizaciones.estado_costeo` | string enum | 4 valores reales en Nexo vs. 3 diseñados (`pendiente_costeo`/`autocosteo`/`entregada`) | **Revisar** — no calzan exacto, hay que ajustar HU-005.7 |

## Decisiones confirmadas con negocio (2026-09-26)

- **`tipo` (spot/licitación):** sí aplica igual en Cotifly, se agrega tal cual.
- **`modalidad`:** no se agrega — con la línea de negocio ya sembrada en Cotifly basta, es redundante para este flujo.
- **Catálogos cerrados de Nexo** (SICE-TAC, carrocerías, clase de contenedor, clase de viaje): Cotifly reutiliza los mismos códigos/valores de Nexo tal cual (no los adapta), mostrados en **texto amigable** en el frontend — nunca el código técnico crudo.
- **`origen`/`destino`:** se resuelven contra `GET /api/captura-solicitudes/municipios` (endpoint real ya confirmado, columna `municipio`) — no es cascada Departamento→Ciudad, es un autocompletado simple.
- **Catálogos sin endpoint propio en Nexo todavía** (`tipoVehiculo`, `carroceria`, `claseViaje`, `claseContenedor`): se hardcodean en el frontend mientras tanto, documentados como deuda técnica — se reemplazan por el consumo real endpoint por endpoint, en cuanto Nexo los construya, sin rediseñar el formulario.

## Pendiente de ajustar (no bloqueante para cerrar HU-005.1)

- **Estado (`estado_costeo`):** los 3 valores diseñados en Cotifly (`pendiente_costeo`/`autocosteo`/`entregada`, HU-005.7) no calzan exactamente con los 4 valores reales de Nexo (`Pendiente`/`EnCosteo`/`Entregada`/`Descartada`). Revisar y ajustar HU-005.7 antes de implementar la derivación.
