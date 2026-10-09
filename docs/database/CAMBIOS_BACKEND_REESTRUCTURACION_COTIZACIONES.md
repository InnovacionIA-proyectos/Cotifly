# Cambios en el backend por la reestructuración de cotizaciones (HU-021)

**Estado: plan — NO ejecutado.** Describe lo que el backend (`backend-dotnet/`) tiene que cambiar para funcionar con
el esquema de `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md`. No se toca código hasta que la propuesta esté
aprobada y se confirme que el backend ya no está en freeze.

Las rutas de archivo son relativas a `backend-dotnet/`. Los números de línea son de la fecha de este documento
(2026-10-07) y pueden moverse.

---

## Índice

1. [Resumen](#1-resumen)
2. [Hallazgo importante: el flujo del agente de chat no crea líneas](#2-hallazgo-importante-el-flujo-del-agente-de-chat-no-crea-líneas)
3. [Dominio](#3-dominio)
4. [Persistencia (EF Core, repositorios, SQL directo)](#4-persistencia-ef-core-repositorios-sql-directo)
5. [Casos de uso](#5-casos-de-uso)
6. [Integración nueva con el cubicador](#6-integración-nueva-con-el-cubicador)
7. [API y contrato con el frontend](#7-api-y-contrato-con-el-frontend)
8. [Pruebas](#8-pruebas)
9. [Reglas que el backend tiene que implementar](#9-reglas-que-el-backend-tiene-que-implementar)
10. [Orden de trabajo sugerido](#10-orden-de-trabajo-sugerido)
11. [Riesgos](#11-riesgos)

---

## 1. Resumen

Hoy el backend trabaja con una cotización que tiene casi todo en la cabecera. Las líneas (`CotizacionLineasSolicitud`)
son una copia por ruta **y** por vehículo a la vez, y el margen y el precio se calculan a nivel de cotización.

Después del cambio hay tres niveles:

| Nivel | Tabla | Qué guarda |
|---|---|---|
| Solicitud | `CotizacionesSolicitud` | Cliente, estado general, PDF, seguimiento del costeo con Nexo, precio total |
| Ruta | `CotizacionLineasSolicitud` | Datos de la ruta, mercancías, conexos, totales de costo, **margen** y precio |
| Camión | `CotizacionVehiculosLineaSolicitud` (nueva) | Vehículo, carrocería, peso, costos de Nexo, id de la línea en Nexo, estado |

Lo que más trabajo da:
- **El margen y el precio pasan de la cabecera a la ruta.** Toca todo lo que calcula, ajusta o muestra márgenes.
- **El envío a Nexo y el polling pasan a ser por camión.** Una ruta se manda como N líneas y se empareja de
  vuelta camión por camión.
- **Los conexos dejan de ser JSON** y pasan a una tabla.
- **Integración nueva con el cubicador**, para recomendar los camiones.

Estimado grueso, de una persona con Claude Code: **4 a 5 semanas** (detalle en §10). Depende de que el cubicador
tenga listo su `POST /recomendaciones`.

---

## 2. Hallazgo importante: el flujo del agente de chat no crea líneas

Las cotizaciones que crea el agente de chat (`generar_pdf`; la integración con Google Chat se retiró en HU-013.9, el agente sigue en el chat web) **no tienen filas en `CotizacionLineasSolicitud`**.
Su costo, margen y precio viven solo en la cabecera
([src/Cotifly.Aplicacion/Pdf/Services/clsGenerarPdfService.cs:287-307](../../backend-dotnet/src/Cotifly.Aplicacion/Pdf/Services/clsGenerarPdfService.cs#L287-L307)).

Como esas columnas salen de la cabecera, el flujo del chat tiene que crear también **una ruta y un camión**:
- La ruta, con la línea de negocio, el margen y el precio.
- El camión, con el costo del flete en `costoFlete`.

El estado del chat (`src/Cotifly.Dominio/Agente/clsEstadoConversacion.cs`, sembrado en
`src/Cotifly.Aplicacion/Agente/Services/clsEstadoConversacionService.cs:123-149`) también guarda costo, márgenes y
origen/destino copiados de la cabecera. Hay que sembrarlo desde la ruta.

---

## 3. Dominio

### 3.1 `clsCotizacion` → cabecera de la solicitud

Archivo: `src/Cotifly.Dominio/Cotizaciones/clsCotizacion.cs`. Se puede dejar el nombre de la clase y cambiar solo la
tabla, o renombrarla a `clsCotizacionSolicitud`. Recomendación: **dejar el nombre** para no inflar el cambio.

**Propiedades que se quitan:**
- 31-40: `LineaNegocioId`, `CosteoId`, `Costo`, `MargenPolitica`, `MargenAplicado`, `OrigenMargen`, `MetodoCalculo`.
- 61-64: `ValorEstimado`, `Referencia`, `HorasCargue`, `HorasDescargue`.
- 69-84: el espejo de la línea 1, de `Origen` a `ValorMercancia`.
- 94 `CosteoUltimaConsultaAt`, 101 `ServiciosConexos` (JSON), 116/119 `MargenSugerido`/`OrigenMargenSugerido`.

**Métodos que se mueven a la ruta o se reescriben:**

| Método (línea) | Qué hace hoy | Qué pasa |
|---|---|---|
| `TieneCostoDisponible` (137), `SugeridoVigente` (151), `ValidarAjusteMargen` (164-192), `AplicarMargenNegociado` (211-233), `RegistrarPrecioAjustado` (236) | Lógica de margen de toda la cotización | Pasa a la ruta |
| `Crear()` (264-319) | Llena margen y costo desde `clsDatosCotizacion` | Solo crea la cabecera. Margen y costo van en la ruta |
| `RegistrarSolicitudCosteo` (326-359) | Copia los campos de la línea 1 a la cabecera | Se elimina el espejo |
| `RegistrarSolicitudLicitacion` (366-383), `EncabezadoSolicitud` (386-397) | Encabezado con `ValorEstimado`, `Referencia`, horas | Quitar los campos eliminados. Las horas salen de las rutas |
| `SolicitudCosteo(posiciones)` (403-438) | Reconstruye la solicitud de filas viejas desde la cabecera | Se elimina: después de la migración todas las cotizaciones tienen rutas |
| `RegistrarEnvioCosteo` (484), `SeConsultaEnNexo` (509) | Usan `CosteoId` | Usar `SolicitudNexoId` (columna `solicitudNexoId` del 0010; es el id de la SOLICITUD de Nexo, no `CotizacionNexoId`, ADR-019) |
| Polling (517-539) | Escribe `CosteoUltimaConsultaAt` | Quitar esa escritura |
| `RegistrarMargenCosteo` (564-582), `RegistrarPrecioCosteo` (589-600), `SeleccionarServiciosConexos` (610) | Margen, costo, precio y conexos de la cotización | Pasan a la ruta. La cabecera solo suma `precioFinal` |
| `PrepararReSolicitud` (744-757) | Limpia costo, precio, conexos, `CosteoId` | Limpia `SolicitudNexoId`, `CotizacionNexoId` y el precio. Las rutas y camiones se limpian aparte |
| `CrearVersion` / `TieneMargenNegociado` (766-796) | Copian línea de negocio y margen | La copia se hace por ruta |
| `RefrescarMargenSugerido` (802-812) | Margen sugerido de la cotización | Pasa a la ruta |

**Métodos nuevos en la cabecera:**
- `RecalcularPrecio(rutas)`: `precioFinal` = suma de `precioFinalLinea`.
- `RecalcularEstadoCosteo(rutas)`: resumen de sus rutas (§9.2).

`clsDatosCotizacion.cs` (14-24) pierde `LineaNegocioId`, `CosteoId`, `Costo`, los márgenes, `MetodoCalculo` y
`PrecioFinal`.

### 3.2 `clsCotizacionLineaSolicitud` → la ruta

Archivo: `src/Cotifly.Dominio/Cotizaciones/clsCotizacionLineaSolicitud.cs`.

**Se renombra o cambia:**
- 16 `CotizacionId` → `CotizacionSolicitudId`.
- 22 `FilaOrigen` → `RutaOrigenIndice` (0 en una cotización simple).
- 57 `PrecioFinal` → `PrecioFinalLinea`.
- `ValorAprovisionamiento` → `ValorLevanteContenedor`.
- `RetornaContenedor` y `LevanteContenedorDiferente` dejan de ser `bool?` y pasan a `bool`.
- Las ciudades del contenedor pasan a `CiudadDevolucionMunicipioId` / `CiudadLevanteMunicipioId` (`int?`).

**Se quita:**
- 24-25 `Origen`/`Destino` (texto).
- 32-33 `TipoVehiculo`/`Carroceria`.
- 48 `LineaSolicitudNexoId`.
- 54 `Costo`.
- 63 `ServiciosConexos`.

**Se agrega:**
- `LineaNegocioId`, `HorasCargue`, `HorasDescargue`.
- `MargenPoliticaSugerido`, `MargenSAPSugerido`, `MargenAplicado`, `OrigenMargen`, `MetodoCalculo`.
- `CostoFletesTotal`, `CostoRetornoContenedorTotal`, `CostoLevanteContenedorTotal`, `CostoServiciosConexos`,
  `CostoFinal`.

**La lógica de margen que sale de `clsCotizacion` vive aquí:**
- `ValidarAjusteMargen`, `AplicarMargenNegociado`, `RefrescarMargenSugerido`.
- Un método `RecalcularTotales(camiones, conexos)` que aplica las reglas de §9.1.

**Métodos actuales que se mueven al camión:** `AsignarLineaNexo` (152), `RegistrarCosteo` (169), `RegistrarEstado`
(188), `TieneCosteo` (209). En la ruta queda `RecalcularEstadoCosteo(camiones)`.

`Desde()` (72-106) y `ALinea()` (109-134) se rehacen. La ruta ya no tiene vehículo, y la línea que va a Nexo se
arma por camión (§5.3).

### 3.3 Entidad nueva: el camión

Archivo nuevo: `src/Cotifly.Dominio/Cotizaciones/clsCotizacionVehiculoLinea.cs`.

**Propiedades:**
- `Id`, `LineaSolicitudId`, `IdRecomendacionVehiculo`.
- `TipoVehiculo?`, `Carroceria?`, `PesoKg`.
- `CostoFlete?`, `CostoRetornoContenedor?`, `CostoLevanteContenedor?`.
- `LineaSolicitudNexoId?`, `EstadoCosteo?`.
- `CreatedAt`, `UpdatedAt`.

**Métodos:**
- `DesdeRecomendacion(...)`: crea el camión a partir de la recomendación del cubicador.
- `AsignarLineaNexo`.
- `RegistrarCosteo(flete, retorno)`.
- `RegistrarEstado`.
- `AsignarVehiculo(tipo, carroceria)`: cuando la Mesa le puso vehículo a una línea que iba vacía.
- `ReiniciarCosteo`.

### 3.4 `clsCotizacionPosicion` → mercancía de la ruta

Archivo: `src/Cotifly.Dominio/Cotizaciones/clsCotizacionPosicion.cs`.
- Se quita 14 `CotizacionId`.
- 17 `LineaSolicitudId` deja de ser opcional.
- 21 `Descripcion` → `NombreMercancia`.
- Se agrega `IdTipoMercancia` (`Guid?`: vacío en las filas migradas, obligatorio para las nuevas, validado aquí).

`APosicion()` (59) ya no se usa para armar `posiciones[]` hacia Nexo: a Nexo se le manda el peso total por camión
(ver `docs/conexion_nexo/PROXIMOS_ALCANCES.md`). Sí se usa para armar la petición al cubicador.

### 3.5 Conexos

Entidad nueva `clsCotizacionServicioConexo` (`Id`, `LineaSolicitudId`, `IdServicioConexo`, `Precio`, `Cantidad`,
`CreatedAt`, `UpdatedAt`).

Reemplaza a `clsServicioConexoCosteo` y `clsServiciosConexosJson` (`src/Cotifly.Aplicacion/Costeo/DTOs/clsServicioConexoCosteo.cs:9-40`),
que leen y escriben el JSON.

### 3.6 Solicitud hacia Nexo y emparejamiento

**`clsSolicitudCosteo.cs`**
- Hoy es un registro plano de una línea y un vehículo (35-69).
- Pasa a tener una lista de rutas, y cada ruta su lista de mercancías y conexos.
- La validación de peso (219-236) se hace por camión: cada camión tiene que tener peso mayor a 0.
- La validación de la posición (253, 265) usa `NombreMercancia`.

**`clsLineaSolicitudCosteo.cs` (61-88)**
- Hoy mezcla campos de la ruta con campos del vehículo.
- Se parte en `clsRutaSolicitudCosteo`, que es lo que captura el comercial, y la línea que se arma por camión al
  enviar.
- `clsEncabezadoSolicitudCosteo` (32-55) pierde `ValorEstimado` y `Referencia`.

**`clsCambiosCarga.cs` (12-97)**
- Es el parche de la re-solicitud.
- Separa lo que cambia la ruta (horas, mercancías, peso declarado) de lo que obliga a volver a cubicar (mercancías
  o peso).

**`clsCosteoConsultadoNexo.cs`**
- El emparejador `clsEmparejadorLineasNexo` (30-102) hoy empareja las líneas por origen y destino en texto (78) y
  por vehículo y peso (83-86).
- Pasa a emparejar **camiones**, con la llave (campo de ruta acordado con Nexo, tipo de vehículo, carrocería,
  peso).
- Los empates se resuelven por orden de envío.
- Mientras Nexo no tenga el campo de ruta, la llave es (origen y destino resueltos desde el id de municipio, tipo
  de vehículo, carrocería, peso) más el orden.

`EstadoCosteo.cs` no cambia. El enum se usa ahora en la ruta, la cabecera y el camión.

### 3.7 Margen y métricas

- **`Margen/clsMargenAjuste.cs`** (22-30, 64, 132): se agrega `LineaSolicitudId`, la ruta ajustada.
- **`Metricas/clsCotizacionMetrica.cs`** (9) y **`clsCalculadoraMetricas.cs`** (32, 45-55, 122-125): hoy leen costo,
  margen, precio, origen y destino de la cabecera. Pasan a leer por ruta. Hay que decidir si las métricas cuentan
  cotizaciones o rutas; recomendación: **seguir contando cotizaciones**, y que el margen sea el promedio de sus rutas
  ponderado por costo.

---

## 4. Persistencia (EF Core, repositorios, SQL directo)

### 4.1 Configuraciones EF

**`src/Cotifly.Infraestructura/Comun/Persistence/Configurations/clsCotizacionConfiguracion.cs`**
- 16 `ToTable("Cotizaciones")` → `"CotizacionesSolicitud"`.
- Quitar los check constraints `ck_cotizaciones_serviciosConexos_json` y `ck_cotizaciones_origenMargenSugerido`
  (18-25).
- Quitar los mapeos de 31, 36-42, 66-86, 92, 95 y 100-102.
- El FK `fk_cotizaciones_lineaNegocioId` (113-114) se mueve a la ruta.
- **`CotizacionPosiciones`** (121-154):
  - Tabla `CotizacionMercancias`.
  - `descripcion` → `nombreMercancia`.
  - Se quita el FK a la cotización (145).
  - El índice único pasa a (`lineaSolicitudId`, `orden`).
  - Se agrega `idTipoMercancia`.
- **`CotizacionLineasSolicitud`** (157-208): columnas nuevas y renombradas según §3.2, y el FK a `MargenesPolitica`.
- **Nuevas configuraciones:** `CotizacionVehiculosLineaSolicitud` y `CotizacionServiciosConexos`.
- Los FK de `CotizacionArchivosLicitacion` (229) y `CotizacionPdfsHistorial` (252) siguen igual. Solo cambia el
  nombre de la tabla a la que apuntan.

**Otras configuraciones**
- `clsMargenConfiguracion.cs` (44-65): `MargenAjustes` con el FK nuevo a la ruta.
- `clsCotiflyDbContext.cs` (28-33): `DbSet` nuevos para camiones y conexos. `CotizacionPosiciones` → `CotizacionMercancias`.
- Los convertidores `clsConvertidorCodigoEnum` de `OrigenMargen` y `EstadoCosteo` (config 40, 47, 101, 197) se
  repiten en las tablas nuevas.

### 4.2 Repositorios

**`IClsCotizacionRepository.cs`** (23-35, 106) y **`clsCotizacionRepository.cs`**:
- Posiciones (22, 26) y líneas (34-41): por ruta, ya no por cotización.
- Métodos nuevos: `ListarCamionesAsync(rutaIds)`, `AgregarCamiones`, `ListarConexosAsync(rutaIds)`,
  `ReemplazarConexos`.
- "Mis cotizaciones" (154-165) hoy une `Cotizaciones.LineaNegocioId` con `MargenesPolitica`. Como una solicitud
  puede tener rutas de varias líneas de negocio, recomendación: mostrar la línea de negocio de la ruta 1, o "Varias".

**Otros repositorios:**
- `clsMargenAjusteRepository.cs` (17-19): permitir filtrar también por ruta.
- `clsCorreoRepository.cs` (34): solo cambia el nombre de la tabla en la subconsulta.

### 4.3 SQL directo (no lo detecta EF, hay que cambiarlo a mano)

- **`clsUnitOfWork.cs:68`**: `SELECT versionNegocio FROM dbo_cotifly.Cotizaciones WITH (UPDLOCK…)` → nombre nuevo.
- **`clsBaseDatosHealthCheck.cs:41-54`**: verifica el nivel del esquema por columnas que se eliminan
  (`Cotizaciones.serviciosConexos`, `Cotizaciones.margenSugerido`…). Hay que agregar un nivel nuevo que verifique
  `CotizacionVehiculosLineaSolicitud` y `CotizacionServiciosConexos`.
- **`clsMetricasRepository.cs`**:
  - 24-28: `COL_LENGTH` sobre `Cotizaciones.origen`, `destino` y `deadline`. Las dos primeras dejan de existir.
  - 38-76: el CTE del tablero lee `c.costo`, `c.margenAplicado`, `c.precioFinal`, origen y destino de la cabecera.
    Se reescribe uniendo con las rutas.
  - 130-187: lee las vistas, ver §4.4.
- **Scripts:** `scripts/sql/dev-04-verificar-permisos.sql` y `scripts/respaldo/probar-respaldo-datos.sql`
  nombran las tablas y vistas viejas.

### 4.4 Vistas

- **`vAjustesMargenPorComercial`:** la redefine el script de migración, y saca la línea de negocio de la ruta del
  ajuste. Las columnas que devuelve no cambian, así que `clsMetricasRepository.cs:157-187` no cambia.
- **`vActividadPorComercial`:** solo cambia el nombre de la tabla.

### 4.5 Script de migración

`db/migrations_mssql/0009_reestructuracion_cotizaciones.sql`, con su reversa en `rollback/`. Mismo estilo de los
anteriores: idempotente, con guardas `IF … IS NULL`, en `dbTarifario_Nexo_Pruebas`.

**Orden:**
1. Crear las tablas nuevas.
2. Agregar las columnas nuevas a la ruta y a las mercancías.
3. Migrar los datos (§11 de la propuesta).
4. Quitar los constraints, índices y columnas viejas.
5. Renombrar tablas y columnas (`sp_rename`).
6. Redefinir las vistas.

Necesita el visto bueno de scalderon-bs y del DBA antes de correrlo, igual que los anteriores.

---

## 5. Casos de uso

### 5.1 Captura por formulario — `Costeo/Services/clsEnvioCosteoService.cs`

- **Validación de catálogos** (112, y `clsValidadorCatalogosCaptura.cs:53-56`): tipología y clase de viaje se
  validan en la ruta. Vehículo y carrocería ya no los digita el comercial, salen del cubicador.
- **Crear la cotización** (142-152): se crea la cabecera, luego la ruta con su línea de negocio.
- **Crear las líneas** (160-165): hoy `RegistrarSolicitudCosteo` → `CotizacionLineaSolicitud.Desde` → posiciones.
  Pasa a: ruta → mercancías → conexos → **pedir recomendación al cubicador** (§6) → camiones.
- **`LineasConPosiciones`** (234-240) y el reintento (253-258): se quita el camino de respaldo a `SolicitudCosteo()`.
- **Política de margen** (279): por la línea de negocio de cada ruta.
- **Registrar el envío** (327-333): el estado se pone en cada camión y se resume en la ruta y la cabecera.
- **`ResolverMargenAsync`** (414-437): se resuelve el margen por ruta, porque cada ruta puede tener otra línea de
  negocio.

### 5.2 Licitación — `Licitaciones/Services/clsLicitacionService.cs`

- **Crear** (207-241): cada fila del Excel es una ruta con `RutaOrigenIndice` = índice de la fila. Se pide la
  recomendación al cubicador por cada ruta.
- **Exportar** (87-106): hoy usa el JSON de conexos, `Referencia`, `PrecioFinal`, y de la línea origen, destino,
  tipo de vehículo, peso y fila. Pasa a exportar una fila por ruta con el precio de la ruta, y los camiones como
  detalle. Origen y destino salen del catálogo de municipios.
- **`Vista()`** (276-304) y los DTOs `Licitaciones/DTOs/clsDtosLicitacion.cs` (18, 61-115): `clsLineaLicitacion`
  pasa a ser la ruta, con una lista de camiones.

### 5.3 Envío a Nexo — `src/Cotifly.Infraestructura/Costeo/Services/clsCosteoNexo.cs`

- **Armar las líneas** (340-356): hoy se arma `clsCapturaLineaWire` desde la línea.
- Pasa a armarse **una por camión**:
  - **De la ruta:** origen y destino (el nombre del municipio, resuelto con el catálogo de municipios de Nexo),
    tipología, clase de viaje, clase de contenedor, contenedor, valor de la mercancía y el levante del camión.
  - **Del camión:** tipo de vehículo, carrocería y peso.
  - Sin `posiciones[]`.
- **Encabezado:** pierde `ValorEstimado` y `Referencia`.
- **Horas de cargue y descargue:** Nexo las acepta **por línea** en `CapturaLineaRequest` (HU-013.11, migración
  `0008_horas_por_linea`), así que cada camión lleva las horas de su ruta. El backend de `QA` ya las guarda por línea;
  esta HU solo las pasa de la cabecera a la ruta.
- **Cuando Nexo agregue el campo de ruta:** se manda el id de la ruta de Cotifly en cada línea.
- Los records del wire (636-648) y `Costeo/DTOs/clsSolicitudNexo.cs` (38-79) se ajustan a lo anterior.

### 5.4 Polling de Nexo y precio

- **`clsCosteoNexo.cs`** (118-171, 274-300): la lectura de `lineas[]` no cambia. Se agrega leer el vehículo de cada
  línea, para el caso en que la Mesa se lo pone a una que iba vacía.
- **`clsPollingCosteoService.cs`**:
  - 119: `CosteoId` → `SolicitudNexoId` (columna `solicitudNexoId` del 0010; es el id de la SOLICITUD de Nexo, no `CotizacionNexoId`, ADR-019).
  - 166: se quita la escritura de `CosteoUltimaConsultaAt`.
  - 169/212: se trabaja con camiones.
  - 230: la auditoría guarda precios por ruta.
- **`clsPrecioCosteoService.cs`**:
  - **`AsociarLineasNexo`** (51-75): asigna `LineaSolicitudNexoId` a cada **camión**, con el emparejador nuevo (§3.6).
  - **`DerivarLinea` / `CostoLinea`** (93-105): registran flete y retorno en el camión.
  - **Precio por línea** (117-141): usa el margen de la **ruta**, no el de la cotización.
  - **Totales** (174-199): se recalculan los totales de la ruta (§9.1) y el `precioFinal` de la cabecera. Ya no se
    escribe el JSON de conexos.
  - 204-220, 240-296: se quita el camino de una sola línea. Todas las cotizaciones tienen rutas y camiones.

### 5.5 Margen

- **`Costeo/Services/clsAjusteMargenCosteoService.cs`**:
  - 74-131: el ajuste se hace **sobre una ruta**. La petición del frontend tiene que decir cuál.
  - 119: el `MargenAjuste` guarda la ruta.
  - 152-158, 180-197: recalculan desde los totales de la ruta, no desde `linea.Costo` y el JSON.
- **`Margen/Services/clsAjusteMargenService.cs`** (48-93): guarda la ruta en el ajuste.
- **`clsMargenResolverService.cs`** (72) y **`Margen/DTOs/clsMargenResuelto.cs`** (15-18): se resuelve por ruta.

### 5.6 Servicios conexos

- **`Costeo/Services/clsServiciosConexosCosteoService.cs`**:
  - 256-286: hoy ubica la línea por `orden` y revisa que tenga `LineaSolicitudNexoId`. Pasa a ubicar la **ruta**.
    La validación "ya está en Nexo" mira si algún camión de la ruta tiene línea en Nexo.
  - 357-358: escribe filas en `CotizacionServiciosConexos` en vez del JSON.
  - 399-424: `ConexosConPrecio` lee de la tabla.
  - 430-437: `LineaDeNexo`. Si hay que mandar los conexos a Nexo, se pegan a la línea del **primer camión** de la
    ruta.
- **`clsCosteoCotizacionService.cs`** (108-150): `SeleccionarServiciosConexosAsync` escribe en la tabla.
- Después de cualquier cambio en conexos: se recalcula `costoServiciosConexos` y los totales de la ruta, se
  invalida el PDF y sube `versionNegocio` (eso ya se hace hoy).

### 5.7 PDF — `clsCosteoCotizacionService.cs`

- **Datos del PDF** (200-226): hoy salen de la línea de negocio, el costo, los márgenes y el precio de la
  cabecera. Pasan a salir por ruta.
- **PDF de una sola ruta** (336-387): hoy sale de origen, destino, vehículo y peso de la cabecera. Pasa a salir de
  la ruta y sus camiones: "2 × tractomula furgón 3S3", por ejemplo.
- **PDF de pliego** (409-452): hoy agrupa por el texto `l.Origen`. Pasa a ser una sección por ruta, con su precio
  y sus camiones.
- **`EsPliego`** (480): pasa a ser "tiene más de una ruta".
- **Validación del total** (498-507): ahora es `precioFinal` = suma de `precioFinalLinea`.
- **`Vista()`** (570-591): la respuesta `CosteoCotizacion` pasa a tener rutas con camiones (§7).
- Los bloques del PDF (`clsBloqueTarifa.cs`, `clsBloquePortada.cs`) solo consumen el DTO. Cambian si cambia su
  forma.

**Implementado en HU-021.9 (#136):**
- **PDF de una ruta:** origen, destino, los camiones que cuentan agrupados (`Camiones()`: "2 × 3S3 - FURGON (20000 KG)"; sin
  vehículo, "Vehículo por definir"; un camión `descartada` no se imprime) y la tarifa del transporte; los conexos aparte, con
  tarifa + conexos = `precioFinalLinea`.
- **Pliego:** `EsPliego` = más de una ruta (una licitación de una fila sale con el PDF de una ruta). Una sección por ruta
  (`RUTA n: PLIEGO DE TRANSPORTE {línea de la ruta}`, origen, destino, camiones y tarifa) y el resumen de siempre; con más de 20
  rutas (`UmbralRutasCompacto`) solo el resumen. El título y el asunto llevan la línea de negocio de la solicitud solo si todas
  las rutas tienen la misma (`clsLineasNegocioRutas.Comun`).
- **Validación del total (`ValidarTotal`, una ruta y pliego):** la solicitud tiene rutas, todas tienen precio y `precioFinal` =
  Σ `precioFinalLinea` (con los conexos); si no, `COSTEO_NO_LISTO`. El total impreso del pliego sigue siendo Σ tarifas, sin
  conexos (cuadra con el Excel).
- **`Vista()`:** `RutaCosteo` gana `linea_negocio_id`, `peso_kg`, `camiones[]` (`CamionCosteo`: vehículo, carrocería, peso,
  estado; sin costos ni ids de Nexo), `vehiculos` (el texto del PDF) y `mercancias[]` (`MercanciaCosteo`).
- La nota de la TRM mínima en USD no cambia.

### 5.8 Re-solicitud y nueva versión

- **`clsReSolicitudCosteoService.cs`**:
  - 194-210: carga rutas, mercancías y camiones.
  - 236-239: `PrepararReSolicitud` en la cabecera y `ReiniciarCosteo` en cada camión.
  - Si cambian mercancías o peso, se borran los camiones de la ruta y se vuelve a pedir la recomendación al
    cubicador.
  - 321-340: la versión nueva copia rutas, mercancías y conexos, y crea camiones nuevos.
  - 398-419: la auditoría guarda costo, precio y margen por ruta, y `LineaSolicitudNexoId` por camión.
- **`Cotizaciones/Services/clsNuevaVersionService.cs`** (36-51) y **`clsDatosNuevaVersion`**
  (`Cotizaciones/DTOs/clsCotizacionResumen.cs:76-89`): copian por ruta.

**Implementado en HU-021.9 (#136):**
- **Mismo folio:** `PrepararReSolicitud` olvida los costos de Nexo de los camiones y el precio; la ruta conserva su margen
  ajustado (decisión del 2026-10-06) y **sus conexos** (decisión 3).
- **`-vN`:** se arma con `RegistrarCaptura(..., reglasCaptura: false)` (lo guardado ya se revalidó; las rutas migradas no tienen
  los ids de la captura nueva): cada ruta con su línea de negocio (antes todas tomaban la de la ruta 1), su fila del pliego, sus
  mercancías, sus camiones vigentes (los `descartada` no) y sus conexos (mismo costo unitario y cantidad). Si `datos_carga`
  cambia el peso o las mercancías (`CambiaLaCarga`: con el mismo valor no cuenta), los camiones se borran y se piden de nuevo a
  `IClsPlanificadorCamiones` (#133 cambia su implementación por el cubicador); el plan se hace antes de reservar, así que un
  error del planificador no reserva ni crea nada. `tipo_vehiculo` y `carroceria` de `datos_carga` se ignoran con un aviso en el
  log (decisión 7). TRM del día y margen negociado copiado por ruta, como antes.
- **Aviso de conexos (rehace #124):** la respuesta trae `conexos_conservados` (nombres, sin montos) y `conexos_descartados`
  queda en `[]` (obsoleto, se retira con #138). La propuesta del asistente dice "Se conservan los servicios conexos «…» con su
  costo y siguen en el precio." La re-solicitud no los agrega en Nexo en el acto: los manda el envío diferido (abajo). La
  cotización anterior de Nexo queda como estaba.
- **Correcciones del validador y el auditor:** en el mismo folio un camión `descartada` no se reenvía (`ReiniciarCosteo`,
  `LineasNexo` y los estados de la solicitud solo tocan los camiones que cuentan); la `-vN` refresca el costo unitario de los
  conexos con el catálogo de Nexo (`IClsCostosConexosNexo`); una ruta migrada con mercancías sin `id_tipo_mercancia` lleva el
  respaldo de un camión sin vehículo en vez de pasar por el planificador; `SinVehiculo` de `datos_carga` vive en
  `clsEnvioCosteoService`; el pliego con rutas de varias líneas lleva "Varias" en la cabecera y el validador del payload solo
  exige `linea_negocio` en el PDF de una tarifa; la tarifa absorbe el redondeo por moneda (1 peso o 0,01 USD por conexo); una ruta
  sin línea no cuenta para "Varias".
- **Envío diferido de conexos (decisión 10 del usuario, `clsEnvioConexosNexoService`).** Al final de cada consulta del polling
  que trae la cotización de Nexo, los conexos de cada ruta que Nexo todavía no muestra se envían UNA vez a la línea del primer
  camión vigente, cuando esa línea ya está costeada. Cada paso queda en `EventosHerramienta` (`conexos_envio_nexo`: `en_curso`
  antes del POST, luego `enviado`, `fallido` o `incierto`), atado a `solicitudNexoId`: una re-solicitud o `-vN` los vuelve a enviar
  una vez a su cotización nueva. Un POST incierto se relee y nunca se repite a ciegas; un fallo se reintenta en el siguiente ciclo y
  no frena el costeo. El precio en Cotifly sigue siendo el costo del catálogo; la lectura de #135 no reimporta un conexo cuyo
  `servicioConexoId` ya está en la ruta. El reclamo del polling evita que dos réplicas envíen a la vez. Si la cotización queda
  entregada en la misma consulta en que falla el envío, vuelve a la agenda solo para reintentarlo (sin costear ni tocar montos)
  hasta `CosteoNexo:MaximoReintentosConexos`; al tope queda `fallido_definitivo`. `GET .../costeo` muestra `estado_nexo` por conexo
  y `aviso_conexos_nexo` por ruta (incierto o fallido definitivo). `idServicioConexo` guarda el id del catálogo (capturados, `-vN`)
  o el de la fila de Nexo (agregados con el POST o importados): a Nexo siempre viaja el del catálogo, resuelto por id o por nombre
  (`clsResolutorConexosCatalogo`); uno ya presente en esa solicitud no se reenvía; uno que el catálogo respondió sin encontrar queda
  `fallido_definitivo` y sale del lote antes del POST (todo o nada); si el catálogo no responde queda `en_espera` (no consume
  intentos: el tope cuenta solo POST rechazados). Deuda: columna para el id del catálogo en un script ≥ 0011 (revisión de @scalderon-bs).
- **`DatosNuevaVersion`** gana `rutas[]` (`RutaNuevaVersion`: orden, línea de negocio, origen, destino y margen de cada ruta);
  `linea_negocio` de primer nivel es la de la ruta 1 o `Varias`.

### 5.9 Otros servicios

- **`clsTextosCosteoService.cs`** (81-82, 199-204): los textos del PDF toman línea de negocio, origen, destino y
  vehículo de las rutas y camiones. Se quita el respaldo a la cabecera.
- **`clsResultadoCotizacionService.cs`** (75, 152): `CosteoId` → `SolicitudNexoId` (columna `solicitudNexoId` del 0010; es el id de la SOLICITUD de Nexo, no `CotizacionNexoId`, ADR-019).
- **`clsMisCotizacionesService.cs`** (45) y **`clsCotizacionResumen.cs`** (46-50): margen y origen del margen
  pasan a ser de la ruta (ver §4.2).

**Implementado en HU-021.9 (#136):** los textos toman la línea de negocio de las rutas (`clsLineasNegocioRutas`: la IA recibe
las de todas las rutas; el estándar, la común), el origen y el destino de cada ruta y el vehículo de sus camiones vigentes, sin
respaldo a la cabecera. "Mis cotizaciones" (lista y resumen) muestra la línea de la ruta 1 o `Varias`, y el margen y su origen
de la ruta 1 (el de cada ruta está en el costeo). El resultado ya usa `CotizacionNexoId` para marcar en Nexo y `SolicitudNexoId`
para leer la solicitud (lo dejó el núcleo, #130/#131; verificado).
- **Agente** (`clsEstadoConversacionService.cs:123-149`, `clsAgenteRunner.cs:120`): ver §2.
- **Correo:** solo el nombre de la tabla.

---

## 6. Integración nueva con el cubicador

Hoy no existe. Se agrega con el mismo patrón de los adaptadores de Nexo: interfaz en `Aplicacion`, implementación
HTTP con Polly en `Infraestructura`, y una versión "no disponible" cuando no está configurado.

| Pieza | Qué hace |
|---|---|
| `IClsCubicador` (Aplicacion) | `RecomendarAsync(rutaId, mercancías)` y `ListarTiposMercanciaAsync()` |
| `clsCubicadorHttp` (Infraestructura) | `POST /recomendaciones` con `IdLineaSolicitud` = id de la ruta, `OrigenSistema = COTIFLY` y por mercancía `idTipoMercancia`, medidas, cantidad y peso. Lee las filas de la Tabla 7: vehículo, carrocería, peso, id, resultado |
| `clsOpcionesCubicador` | `Cubicador:UrlBase` y `Cubicador:ApiKey`. La llave se manda como `Authorization: Bearer <CUBICADOR_API_KEY>`, el mismo esquema que hoy protege `/api/cubicacion` y `/api/planes-cargue` en el cubicador (informado por la sesión de NEXO, 2026-10-08). **Confirmarlo cuando `/recomendaciones` exista**, porque hoy solo hay diseño, no código |
| Endpoint web `GET /web/tipos-mercancia` | Pasa el catálogo `GET /tipos-mercancia` del cubicador para el desplegable del formulario |

**Reglas:**
- **"Sin selección automática":** se crea el camión con vehículo y carrocería vacíos. Se envía igual a Nexo, y lo
  resuelve la Mesa de Costeo.
- **Mercancía condicionada** (pendiente de AdminTarifas): no se parte nada. La solicitud se envía completa y esas
  líneas van a la Mesa.
- **Si el cubicador no responde,** la captura falla con un error claro ("no se pudo calcular los vehículos"). No se
  manda nada a Nexo sin camiones.

**Implementado en #133 (HU-021.5, 2026-10-08), probado solo contra un cubicador falso** (el real no tiene aún los endpoints, C1):

- `IClsCubicador` (`RecomendarAsync(rutaId, mercancías)`, `ListarTiposMercanciaAsync()`, `EstaConfigurado`, `RespaldoSiFalla`),
  `clsCubicadorHttp` (cliente `"Cubicador"`, sin reintento del POST, `Bearer`, contrato provisional en su comentario y en ADR-019),
  `clsCubicadorNoDisponible` y `clsFabricaCubicador`. Opciones `Cubicador:UrlBase`, `Cubicador:ApiKey`,
  `Cubicador:TiempoEsperaSegundos` (15), `Cubicador:MinutosCacheCatalogos` (30) y `Cubicador:RespaldoSiFalla` (true).
- `clsPlanificadorCamionesCubicador` reemplaza a `clsPlanificadorCamionSinVehiculo` como `IClsPlanificadorCamiones`: un camión por
  fila con vehículo, carrocería, peso total (C2) e `idRecomendacionVehiculo` (C3); "sin selección" → camión vacío; "condicionada" →
  se envía todo. El id de la ruta se fija antes de guardar (`clsRutaCapturada.RutaId`) porque es el `IdLineaSolicitud`.
- **Cambio de regla (coordinador, 2026-10-08):** mientras el cubicador no exista, **el respaldo es el camino por defecto**: sin
  configurar, o si falla con `Cubicador:RespaldoSiFalla = true`, la ruta queda con un camión sin vehículo y el peso total (y un
  warning). La regla de arriba ("la captura falla") se activa con `Cubicador:RespaldoSiFalla = false` (503 `CUBICADOR_NO_DISPONIBLE`).
- `GET /web/tipos-mercancia` (caché, 503 `CUBICADOR_NO_DISPONIBLE` sin cubicador). En la captura, con el catálogo, el nombre de la
  mercancía se copia de él y un id desconocido es `TIPO_MERCANCIA_INEXISTENTE`; sin catálogo se conserva lo que manda el cliente.
- Con varios camiones las posiciones no viajan a Nexo (regla de #134): repartirlas exige la Tabla 8 del cubicador. La nota a la Mesa
  (`notas`) lo avisa.
- Correcciones de la revisión: el orden de los camiones es el de la recomendación (desfase determinista de `createdAt`; deuda: columna
  de orden en un script ≥ 0011); solo una caída corta el cubicador para el resto de la captura; las rutas sin `id_tipo_mercancia` van
  al respaldo sin llamarlo; warning `CUBICADOR_RESPALDO` y contador `cotifly.cubicador.respaldos`; una sola lectura del catálogo por
  captura; presupuesto de tiempo por captura (`Cubicador:PresupuestoCapturaSegundos`); el 503 no expone el detalle interno. Las
  recomendaciones que quedan en el cubicador si la captura no se guarda están documentadas en ADR-019.

---

## 7. API y contrato con el frontend

Estos cambios rompen el contrato. El frontend (Sara) tiene que ajustarse al mismo tiempo. Hay que actualizar
`specs/openapi.yaml`.

| DTO / endpoint | Cambio |
|---|---|
| `Costeo/DTOs/clsDtosCosteo.cs` 15-91 `clsSolicitudCosteoWebRequest` | Hoy es plano: una línea, un vehículo. Pasa a tener una lista de rutas. Cada ruta trae línea de negocio, municipios, condiciones, horas, valor del levante, mercancías (`idTipoMercancia`, nombre, medidas, cantidad, peso) y conexos. **Sin vehículo ni carrocería** |
| HU-021.6 (#132, hecho) | `POST /web/solicitudes-costeo` recibe `rutas[]` (`RutaCapturaWeb`). El **formato plano anterior** (una ruta en la raíz) queda **obsoleto** (`deprecated` en `openapi.yaml`) y solo se acepta sin `rutas`; **se retira en #138**. Con `rutas`, un campo plano en la raíz es 400 con `detalle.errores` |
| `clsDtosCosteo.cs` 96-103 (posición) | `descripcion` → `nombreMercancia`, y se agrega `idTipoMercancia` |
| `clsDtosCosteo.cs` 107, 113 (conexos, margen) | La petición dice a qué ruta aplica (`lineaSolicitudId`) |
| `clsDtosCosteo.cs` 146-225 `clsDatosCargaWebRequest` | La re-solicitud dice qué ruta cambia |
| `Licitaciones/DTOs/clsDtosLicitacionWeb.cs` 13-95 | `clsLineaLicitacionWebRequest` pierde tipo de vehículo y carrocería, y gana mercancías |
| `Costeo/DTOs/clsDtosServiciosConexos.cs` 9-17 `LineaOrden` | Pasa a identificar la ruta |
| `GET /web/cotizaciones/{folio}/costeo` | Devuelve rutas, y en cada ruta sus camiones, mercancías, conexos, totales, margen y precio |
| Nuevo `GET /web/tipos-mercancia` | Catálogo de mercancías del cubicador |

Los endpoints de margen y de conexos (`/costeo/margen`, `/costeo/servicios-conexos`) conservan la ruta, pero su
cuerpo agrega la ruta.

### 7.1 Margen y conexos por ruta: guía para el frontend (HU-021.8, #135, implementado)

- `GET .../costeo` trae `rutas[]` (`RutaCosteo`, siempre presente): `linea_orden`, `linea_solicitud_id`, origen, destino,
  estado, `precio_final_linea`, `margen_aplicado`, `margen_sugerido`, `origen_margen` y sus `servicios_conexos`. **El
  `margen_aplicado`, `margen_sugerido` y `origen_margen` de primer nivel son los de la ruta 1** (compatibilidad): con varias
  rutas, mostrar los de cada ruta.
- `PUT .../costeo/margen` lleva `linea_orden` (o `linea_solicitud_id`). Con una sola ruta es opcional; **con varias rutas y sin
  ruta responde 400 `RUTA_REQUERIDA`** con `detalle.rutas` (sin montos): mostrar un selector y repetir. `RUTA_INEXISTENTE` si la
  ruta no es de la cotización. La justificación se exige contra el sugerido de ESA ruta. **El ajuste es ruta por ruta: solo la
  ruta ajustada necesita precio**; si otras siguen en costeo, `precio_final` queda en `null` hasta que todas lo tengan, y el PDF
  exige todas las rutas con precio.
- `PUT .../costeo/servicios-conexos`: `{linea_orden, conexos: [{id, cantidad}]}` deja en la ruta exactamente esos conexos
  (quitar = borrar la fila; cambiar la cantidad conserva el costo unitario de Nexo) o `{incluidos: [...]}` (compatibilidad). Debe
  venir exactamente una de las dos: `{"linea_orden": 2}` solo responde 400 y no borra nada. Los ids se comparan sin distinguir
  mayúsculas. Cada cambio queda auditado.
- `POST .../costeo/servicios-conexos`: cada item lleva la ruta; todos los items de un clic son de la misma ruta.
- **Divergencia con Nexo:** Nexo no tiene DELETE de conexos. Lo que se quita (o cuya cantidad se cambia) en Cotifly **no se
  borra ni se cambia en la cotización de Nexo**: Cotifly es la fuente de los conexos (decisión 3) y el precio al cliente sale
  de Cotifly. La cotización de Nexo puede seguir mostrando el conexo quitado.

---

## 8. Pruebas

Las tablas y campos que cambian aparecen en **unos 65 archivos de prueba**.

**Las que más cambian:**
- `Cotifly.Infraestructura.Tests/Persistencia/clsEsquemaTests.cs`: compara EF contra el catálogo real, con listas
  explícitas de columnas. Se actualiza con el esquema nuevo.
- `Aplicacion.Tests/Costeo/`: `clsEnvioCosteoServiceTests`, `clsReSolicitudCosteoServiceTests`,
  `clsPrecioCosteoLineasTests`, `clsAjusteMargenCosteoServiceTests`, `clsServiciosConexosCosteoServiceTests`.
- `Dominio.Tests/Cotizaciones/`: `clsCotizacionTests`, `clsLineasSolicitudTests`, `clsReSolicitudCotizacionTests`,
  `clsSolicitudCosteoTests`, `clsAjusteMargenCotizacionTests`.
- `Infraestructura.Tests/Pdf/clsPdfPliegoTests.cs` y `Persistencia/clsCotizacionRepositoryTests.cs`.

**Con SQL directo** (no fallan al compilar, fallan al correr):
- `clsMetricasTableroRepositoryTests`.
- `Api.Tests/Web/`: `clsReSolicitudCosteoEndpointsTests`, `clsLicitacionesEndpointsTests`,
  `clsConcurrenciaCotizacionTests`, `clsPollingCosteoEndpointsTests`, `clsCosteoEndpointsTests`,
  `clsResultadoCotizacionEndpointsTests`.
- `Aceptacion.Tests/`: `Costeo/clsPasosReSolicitudCosteo`, `clsPasosAjusteMargen`, `Web/clsPasosMetricas.cs:52`
  (INSERT con `lineaNegocioId`, `costo`, márgenes) y `Agente/clsPasosAgente.cs:339` (JOIN sobre `c.lineaNegocioId`).
- `Api.Tests/Soporte/clsApiWebPruebas.cs:230`.

**Fixtures y pruebas que se eliminan:**
- `tests/Comun/clsSolicitudCosteoMuestra.cs`: muestra con rutas, mercancías y camiones.
- `clsDerivacionEstadoCosteoTests.cs:73` y `clsPollingCosteoServiceTests.cs:55` verifican `CosteoUltimaConsultaAt`.
  Esas aserciones se quitan.

**Pruebas nuevas:**
- Totales de la ruta (§9.1).
- Resumen de estados (§9.2).
- Emparejamiento de camiones con las líneas de Nexo, incluyendo empates.
- Ruta con camión sin vehículo.
- Adaptador del cubicador con un servidor falso.
- Migración de datos: probar `0009` contra una copia de la base de pruebas.

---

## 9. Reglas que el backend tiene que implementar

### 9.1 Totales

**Al crear un camión desde la recomendación:** `costoLevanteContenedor` = `valorLevanteContenedor` de su ruta (o 0).

**En la ruta:**
- `costoFletesTotal` = Σ `costoFlete` de sus camiones.
- `costoRetornoContenedorTotal` = Σ `costoRetornoContenedor`.
- `costoLevanteContenedorTotal` = Σ `costoLevanteContenedor`.
- `costoServiciosConexos` = Σ (`precio` × `cantidad`) de sus conexos.
- `costoFinal` = la suma de los cuatro anteriores.
- `precioFinalLinea` = `costoFinal` con `margenAplicado`, según `metodoCalculo` (la misma fórmula de hoy, en
  `clsPrecioCosteoService`).

**En la cabecera:** `precioFinal` = Σ `precioFinalLinea` de sus rutas.

**Cuándo recalcular:** cada vez que cambia el costo de un camión, un conexo o el margen de una ruta. Se recalcula
la ruta y la cabecera dentro de la misma transacción.

**Camiones sin costo:** mientras alguno no tenga costo, la ruta no tiene precio (`precioFinalLinea` vacío), y la
cabecera tampoco.

### 9.2 Resumen del estado del costeo

La misma regla sirve para ruta ← camiones y para cabecera ← rutas:

1. Si todos tienen el mismo estado, se toma ese.
2. Los `descartada` se ignoran, salvo que todos lo sean.
3. Si no, se toma el primero que aparezca en este orden (del más atrasado o problemático al más avanzado):
   `error_envio` → `costeo_vencido` → `pendiente_envio` → `pendiente_costeo` → `entregada` → `autocosteo`.

Así, una ruta con un camión autocosteado y otro en la Mesa queda `pendiente_costeo`. Una con uno `autocosteo` y
otro `entregada` queda `entregada`.

### 9.3 `motivoRechazo` según el resultado (HU-021.13, #140, implementado)

**Regla (propuesta de refactor §2.1).** `clsCotizacionSolicitud.MarcarSeguimiento` es la única puerta de escritura del motivo:
- ganada → `"No aplica"`;
- perdida → el motivo del comercial;
- sin resultado (`ganada = null`) → `NULL`.

**Aplica desde este cambio en adelante.** Las cotizaciones ganadas que ya están guardadas con `motivoRechazo` en `NULL` (o con el texto que se escribió antes) **no se corrigen**. No hay backfill en ningún script `000N`.

**Datos históricos.** Corregirlos queda como script **opcional** para el DBA, solo si negocio lo pide. Sería un `UPDATE dbo_cotifly.CotizacionesSolicitud SET motivoRechazo = N'No aplica' WHERE ganada = 1 AND (motivoRechazo IS NULL OR motivoRechazo <> N'No aplica')`, con respaldo previo y su cabecera de aprobación. Mientras no se corra, los reportes que lean `motivoRechazo` de ganadas antiguas pueden ver `NULL` o texto libre.

---

## 10. Orden de trabajo sugerido

Estimado de una persona con Claude Code. Es grueso, sacado de recorrer el código, no de implementarlo.

| Fase | Qué | Días |
|---|---|---|
| 1 | Script `0009` + reversa, probado contra una copia de la base de pruebas | 2-3 |
| 2 | Dominio: entidades nuevas, margen y totales en la ruta, reglas de §9 + sus pruebas | 3-4 |
| 3 | EF, repositorios, SQL directo, health check | 2 |
| 4 | Adaptador del cubicador + `GET /web/tipos-mercancia` | 2-3 |
| 5 | Captura (formulario y licitación) con recomendación de camiones | 3 |
| 6 | Envío a Nexo por camión, polling y emparejador nuevo | 3-4 |
| 7 | Margen, conexos y precio por ruta | 3 |
| 8 | PDF, textos, re-solicitud, nueva versión, mis cotizaciones | 3-4 |
| 9 | Métricas, vistas y flujo del agente de chat | 2 |
| 10 | DTOs, `openapi.yaml`, arreglar las ~65 pruebas existentes, pruebas end-to-end | 4-5 |

**Total: 27 a 33 días hábiles (unas 4 a 5 semanas).**

**Se puede ir por partes** sin bloquear al frontend tanto tiempo:
- Las fases 1 a 3 se pueden mergear solas si el resto del código sigue con **una ruta y un camión** por cotización,
  sin cubicador todavía. El vehículo se toma del formulario, como hoy.
- El cubicador (4 y 5) se conecta después.

---

## 11. Riesgos

1. **El contrato con el frontend se rompe completo** (§7). Backend y frontend tienen que salir juntos, o mantener
   un tiempo el request plano viejo, convirtiéndolo a "una ruta, un camión".
2. **El emparejamiento con Nexo es lo más frágil.** Sin el campo de ruta en Nexo, dos camiones iguales de la misma
   ruta solo se distinguen por el orden en que se enviaron. Si Nexo no devuelve las líneas en ese orden, se pueden
   cruzar sus ids (los costos serían los mismos, pero los estados podrían mezclarse).
3. **El cubicador todavía no existe en código** (según su propio plan de implementación). La fase 5 depende de
   que tenga `POST /recomendaciones` y `GET /tipos-mercancia` funcionando.
4. **El costo viejo no se puede desglosar exacto** al migrar (§11 de la propuesta). Las cotizaciones de prueba
   que ya existen quedan con un desglose aproximado.
5. **Métricas:** si una solicitud tiene rutas de varias líneas de negocio, el tablero tiene que decidir cómo
   contarla (§3.7).
