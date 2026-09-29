# ADR-016 — Métricas agregadas del tablero y regional del usuario

- **Estado:** Propuesta (definiciones pendientes de validar con Gerencia Comercial; la fuente de la regional depende del DBA, ver Pendientes)
- **Fecha:** 2026-09-29
- **Issue / HU:** #45 (HU-010.1). Consumidores: clientepro-frontend #50 (HU-010.2, tablero del comercial), #51 (HU-010.3, tablero del gerente) y #52 (HU-010.4, QA de cálculos). Relacionada con ADR-009 (rol en el token), ADR-011 (folio y versiones), ADR-014 (TRM y cotizaciones en USD) y ADR-015 (costeo de Nexo, script 0004).

## Contexto

- HU-010 pide dos tableros: el del comercial (sus cotizaciones: cantidad, ganadas, % de conversión, margen promedio, costeo total, ganancias) y el del gerente comercial (lo mismo para toda su regional, filtrable por un comercial, con al menos una gráfica y un mapa de concentración por ciudad con el dinero recaudado).
- El PR #62 dejó un borrador (`GET /web/metricas/actividad` y `/ajustes-margen`) sobre las vistas del 0001/0003. No trae los KPI, no filtra por comercial, no convierte USD y la regional sale siempre nula.
- El rol ya viaja en el claim `rol` del access token, emitido en el login SSO tras la verificación doble contra `dbsolucionesti` y `UsuariosRoles` (ADR-009). Las políticas `Comercial` y `GerenteComercial` de la Api lo leen.
- `Cotizaciones` no tiene `cotizacionOrigenId` (se quitó el 2026-09-24). Una versión nueva es una fila con `version + 1` y el folio de la v1 con sufijo `-vN` (ADR-011).
- `Cotizaciones.moneda` y `trmAplicada` existen desde el 0001. En USD, `precioFinal` está en dólares y `costo` siempre en pesos (ADR-014).
- `origen` y `destino` de la cotización llegan con el script 0004 (ADR-015), que todavía no está en `QA`.
- `UsuariosWeb.regional` existe desde el 0001 pero nadie la llena. La fuente prevista es `DWCubosEBSSAP.DimEjecutivo` (cruce `nombreUsuario` → `DimEjecutivo.ID`, columna `REGIONAL`; 0001 y `docs/ESQUEMA_OBJETIVO_SQLSERVER.md` §5.8), que no está conectada. No hay más información de esa tabla en el repositorio (ni en el Python, ni en `docs/PROPUESTA_REFACTOR_BD_COTIFLY.md`, ni en `docs/Entendimiento_Cotifly.docx.md`).

## Decisión

### 1. Endpoints

| Endpoint | Rol (política) | Alcance |
|---|---|---|
| `GET /web/metricas/comercial?desde&hasta` | `comercial` o `gerente_comercial` (`Comercial`) | Las cotizaciones propias (`usuarioId` = `sub`) |
| `GET /web/metricas/regional?desde&hasta&usuario_id` | solo `gerente_comercial` (`GerenteComercial`) | La regional del usuario autenticado, resuelta en el servidor; `usuario_id` filtra a un comercial |

- El rol sale del token (claim `rol`), nunca de una columna de `UsuariosWeb`. El caso de uso lo revalida (defensa en profundidad, como la administración de líneas).
- Un `comercial` en `/regional` recibe 403 (`ErrorWeb`).
- `/regional` no acepta una regional por parámetro: siempre es la del gerente.
- Un `usuario_id` que no es de la regional (o no existe) responde **404** `ErrorWeb` "Comercial no encontrado en tu regional.", igual en los dos casos para no revelar usuarios de otras regionales.
- `comerciales` trae siempre a todos los usuarios de la regional con sus indicadores (para el selector dinámico), aunque se filtre por `usuario_id`. El filtro aplica a `indicadores`, `serie_mensual` y `concentracion_ciudades`.
- Ambos devuelven una `serie_mensual` (línea de tiempo por mes de Bogotá: cantidad, ganadas, perdidas y dinero) con todos los meses del periodo, también los vacíos. `/regional` agrega `concentracion_ciudades` y `lineas_negocio_activas`.

### 2. Universo y versiones

- Se cuenta **solo la versión vigente** de cada cotización: la de mayor `version` de la cadena `(usuarioId, folio base)`, donde el folio base es el folio sin el sufijo `-v{version}`. La vigente se decide sobre toda la historia y después se filtra por periodo con su `createdAt`.
- Se cuentan **todos los estados** (con o sin precio, enviada o no), igual que "Mis cotizaciones", que lista todas las filas del comercial. Las que no tienen precio suman en `pendientes_por_cotizar` y no suman dinero.
- Se expone también `versiones` (todas las filas del periodo, cualquier versión) como referencia, sin usarlo en ningún cociente.
- **Diferencia con el backlog:** la nota del 2026-09-24 del backlog (HU-010.1) dice contar cada versión tal cual porque "no hay forma confiable de agruparlas sin la FK". El folio de ADR-011 (aprobado el 2026-09-25) sí es una forma confiable: la v2+ reutiliza el folio base de la v1 y la genera solo el servidor. Se sigue la decisión del issue (#45, "decidir y documentar") y del orquestador del proyecto: versión vigente. `versiones` deja ver la otra cifra. Pendiente de confirmar con negocio.

### 3. Moneda

- `precio_cop = precioFinal` si `moneda` es `COP` (o nula).
- `precio_cop = round(precioFinal × trmAplicada, 2)` (half-to-even) si la moneda es otra y `trmAplicada > 0`. Se redondea por fila y luego se suma, para que QA lo recalcule igual.
- Una fila en otra moneda con `trmAplicada = 0` (no debería existir: el dominio lo impide) no suma dinero, pero sí cuenta en `cantidad`.
- `costo` siempre está en COP y nunca se multiplica. Las filas en COP (`trmAplicada = 0`) nunca se multiplican.
- Nota: ADR-014 define el piso del PDF con `precio_cop` recalculado del costo y el margen; `precioFinal × trmAplicada` puede diferir unos pesos de ese valor por el redondeo a centavos del precio en USD. Para las métricas se usa la multiplicación, que es lo que pide HU-010 y lo que QA puede recalcular con las columnas guardadas.

### 4. Definiciones

| Campo | Definición |
|---|---|
| `cantidad` | Filas del universo (sección 2) |
| `versiones` | Todas las filas creadas en el periodo (cualquier versión) |
| `ganadas` / `perdidas` / `sin_resultado` | `ganada = 1` / `= 0` / nulo |
| `pendientes_por_cotizar` | `precioFinal` nulo |
| `conversion` | ganadas / cantidad (backlog HU-010: "#ganadas / #totales"); `null` si cantidad = 0 |
| `conversion_sobre_cerradas` | ganadas / (ganadas + perdidas); `null` si no hay cerradas |
| `margen_promedio` | Promedio simple de `margenAplicado` (fracción) de las filas que lo tienen |
| `costeo_total` | SUM(`costo`) en COP de todo el universo |
| `valor_cotizado_cop` | SUM(`precio_cop`) |
| `valor_ganado_cop` | SUM(`precio_cop`) de las ganadas ("dinero recaudado") |
| `ganancias` | SUM(`precio_cop` − `costo`) de las ganadas con precio y costo |
| `cotizaciones_usd` | Filas del universo en moneda distinta de COP |
| `tiempo_promedio_cotizacion_horas` | Promedio de (`costeoListoAt` − `createdAt`) en horas, de las filas con `costeoListoAt` (backlog HU-010) |
| `desviacion_promedio_margen` | Promedio de `MargenAjustes.diferencia` de los ajustes de las cotizaciones del universo |
| `ajustes_margen` | Cantidad de esos ajustes |
| `lineas_negocio_activas` | `MargenesPolitica.isActive = 1` (solo en `/regional`) |

- **Ajuste respecto al pedido inicial del orquestador:** se pidió `conversion` = ganadas / (ganadas + perdidas) y `conversion_sobre_total` = ganadas / cantidad. El backlog define "% de conversión (#ganadas / #totales)", y la regla de ADR es que el backlog manda: `conversion` es ganadas / cantidad y la otra se llama `conversion_sobre_cerradas`.
- Fracciones con 4 decimales, montos con 2, horas con 2; siempre half-to-even (`MidpointRounding.ToEven`, paridad con el resto del backend).
- El cálculo es una regla pura de dominio (`Cotifly.Dominio.Metricas.clsCalculadoraMetricas`): el repositorio solo trae las filas del universo (una consulta SQL con `ROW_NUMBER` para la vigente) y la calculadora agrega. Así cada definición tiene su prueba unitaria.

### 5. Periodo

- `desde` y `hasta` opcionales, fechas `AAAA-MM-DD` de America/Bogota, ambas incluidas: de `desde` 00:00 a `hasta` 24:00 en la zona de negocio (`IClsCalendarioNegocio`), comparado con `createdAt` de la versión vigente.
- Por defecto: del 1 de enero del año en curso a hoy (Bogotá). Se eligió el año en curso y no "todo" para que la serie mensual tenga un tamaño acotado y el tablero cargue rápido.
- `hasta < desde`, fechas mal formadas o un rango de más de 3 años responden 400.

### 6. Regional del usuario

Puerto `IClsRegionalUsuario` (Aplicación) con dos fuentes:

1. **`DWCubosEBSSAP.DimEjecutivo`** tras el puerto `IClsDirectorioEjecutivos` (adaptador `clsDirectorioEjecutivosDimEjecutivo`, SQL parametrizado de solo lectura). Se activa solo si hay `ConnectionStrings__Regional`. Esquema, tabla y columnas son configurables (`Regional__Esquema`, `Regional__Tabla`, `Regional__ColumnaUsuario`, `Regional__ColumnaRegional`; por defecto `dbo.DimEjecutivo`, `ID`, `REGIONAL`) porque el esquema real no está confirmado. El usuario se cruza por `nombreUsuario` recortado y sin distinguir mayúsculas; un usuario con dos regionales distintas en la tabla se ignora en ella (warning en el log) y se usa su respaldo.
2. **`UsuariosWeb.regional`** como respaldo explícito: la llena el DBA con un `UPDATE` (plantilla en `docs/10-operacion.md`). No hace falta un script de esquema nuevo: la columna ya existe desde el 0001. La fila de `UsuariosWeb` se crea en el primer login (alta JIT), así que el `UPDATE` va después de ese primer ingreso.

Regla: `regional efectiva(u) = DimEjecutivo[u.nombreUsuario] ?? UsuariosWeb.regional`, comparando sin espacios sobrantes y sin distinguir mayúsculas. Los miembros de una regional R son los usuarios de `UsuariosWeb` cuya regional efectiva es R.

- Gerente sin regional efectiva: **409 `REGIONAL_NO_ASIGNADA`** (`ErrorNegocio`). Nunca se devuelven todas las regionales ni se cae a "solo lo propio" en silencio.
- `DimEjecutivo` configurada pero caída: **503 `REGIONAL_NO_DISPONIBLE`** (fail-closed; no se mezcla la fuente viva con la de respaldo cuando la viva falla).
- `fuente_regional` (`dim_ejecutivo` | `usuarios_web`) dice de dónde salió la regional del gerente.
- Los borradores (`/actividad`, `/ajustes-margen`) y la descarga de PDF ajenos (`clsDescargaPdfService`) siguen leyendo solo `UsuariosWeb.regional` (sin cambio de comportamiento en esta HU).

### 7. Concentración por ciudad

- Se agrupa por el municipio de **destino** (`por_destino`: dónde se entrega la carga, la vista del mapa) y también por el de origen (`por_origen`), con cantidad, ganadas, valor cotizado y valor ganado en COP. El texto es el del catálogo de municipios de Nexo, tal como se guardó; el frontend lo ubica en el mapa.
- Las columnas `origen`/`destino` son del 0004. La consulta comprueba si existen (`COL_LENGTH`); sin ellas, `disponible = false` y las listas van vacías. Así esta rama funciona sobre `QA` hoy y el mapa se llena solo cuando se aplique el 0004, sin tocar el código.
- Las cotizaciones del chat no guardan ciudades: cuentan en `sin_destino` / `sin_origen`.

### 8. Score de urgencia y deadline vencido (Nexo)

No se implementa. El backlog lo define como "dato que expone Nexo, no se recalcula en Cotifly", y el contrato de Nexo para consultarlo por solicitud no existe todavía. Queda pendiente con el equipo de Nexo; cuando exista, se agrega como campo nuevo de `IndicadoresMetricas` (cambio compatible).

### 9. Borradores

`/web/metricas/actividad` y `/ajustes-margen` siguen funcionando igual y quedan `deprecated: true` en el contrato. Se retirarán cuando el frontend use los nuevos.

## Alternativas consideradas

- **Contar todas las versiones** (nota del backlog del 2026-09-24): infla las métricas con cada cotización rehecha. Se expone como `versiones` para quien la necesite.
- **Agregar en SQL con vistas nuevas:** exige un script de esquema (y aprobación del DBA) y deja la conversión, la serie y la zona horaria repartidas en T-SQL, más difícil de probar. El volumen (unos 6.000 cotizaciones al mes en total, una regional es una fracción) cabe de sobra en memoria.
- **Tabla de mapeo nueva `RegionalesUsuarios` (script 0005):** duplicaría `UsuariosWeb.regional`, que ya es exactamente ese mapeo, y chocaría con el 0005 que prevé ADR-015 para HU-011.
- **Mapeo por configuración (`Regional__Asignaciones__APEREZ=...`):** obliga a redesplegar para mover a un comercial de regional. Descartado frente al `UPDATE` del DBA.
- **Sincronizar `UsuariosWeb.regional` en cada login:** metería una tercera base externa en el camino crítico del login (ADR-009 es fail-closed). Se prefirió la consulta al pedir las métricas.
- **Filtrar un comercial ajeno con 200 vacío en vez de 404:** confunde al frontend (un selector con un id mal armado parecería "sin datos"). Se usa 404 como en el resto de `/web/*`.

## Consecuencias

- El frontend (#50, #51) tiene un contrato estable en `specs/openapi.yaml` con ejemplos; QA (#52) puede recalcular cada campo con las definiciones de la sección 4.
- El tablero del gerente responde 409 hasta que exista la regional (DimEjecutivo conectada o `UsuariosWeb.regional` llenada por el DBA). Es deliberado.
- El mapa responde `disponible = false` hasta que se aplique el 0004.
- `IClsCalendarioNegocio` gana `FechaDe(instante)` e `InicioDelDia(fecha)` para convertir entre UTC y la fecha de Bogotá.
- Nueva conexión opcional `ConnectionStrings__Regional` y sección `Regional__*` (documentadas en `docs/03-infraestructura.md` y `.env.example`).

## Pendientes

| Pendiente | Dueño |
|---|---|
| Confirmar la tabla de la regional: base (`DWCubosEBSSAP`), esquema, tabla (`DimEjecutivo`), la columna que coincide con `tblSAPUsuariosApp.NOMBREUSUARIO` (¿`ID`?), la columna de regional (¿`REGIONAL`?), si los gerentes están en ella y el catálogo de valores de regional | DBA / equipo de datos |
| Crear el acceso de solo lectura (`SELECT` para `cotifly_app` sobre esa tabla) y la salida de red desde el contenedor; entregar `ConnectionStrings__Regional` | DBA / Infraestructura |
| Mientras tanto, llenar `UsuariosWeb.regional` de gerentes y comerciales (plantilla en `docs/10-operacion.md`) | DBA con Gerencia Comercial |
| Validar las definiciones: versión vigente vs. todas las versiones, `conversion` sobre el total, `costeo_total` sobre todo el universo, `ganancias` solo de ganadas y periodo por defecto (año en curso) | Gerencia Comercial (Sara / scalderon-bs) |
| Contrato de Nexo para "score de urgencia" y "deadline vencido" por solicitud | Equipo de Nexo |
| Aplicar el 0004 para que el mapa por ciudad tenga datos | DBA (ADR-015) |
