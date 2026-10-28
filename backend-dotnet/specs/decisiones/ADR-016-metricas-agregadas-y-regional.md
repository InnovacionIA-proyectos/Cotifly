# ADR-016 — Métricas agregadas del tablero y regional del usuario

- **Estado:** Propuesta (definiciones pendientes de validar con Gerencia Comercial; la fuente de la regional ya está verificada en #81, faltan la credencial en el despliegue y la salida de red, ver Pendientes)
- **Fecha:** 2026-09-29
- **Issue / HU:** #45 (HU-010.1), #81 (HU-003.6, regional en el login) HU-037 (#209 a #212, rol Director: sección 11), HU-034.1 (#201 parte A, licitación en solo consulta: sección 12) y HU-016.1 (#154 parte B, margen contra la política: sección 13). Consumidores: clientepro-frontend #50 (HU-010.2, tablero del comercial), #51 (HU-010.3, tablero del gerente) y #52 (HU-010.4, QA de cálculos). Relacionada con ADR-009 (rol en el token), ADR-011 (folio y versiones), ADR-014 (TRM y cotizaciones en USD) y ADR-015 (costeo de Nexo, script 0004).

## Contexto

- HU-010 pide dos tableros: el del comercial (sus cotizaciones: cantidad, ganadas, % de conversión, margen promedio, costeo total, ganancias) y el del gerente comercial (lo mismo para toda su regional, filtrable por un comercial, con al menos una gráfica y un mapa de concentración por ciudad con el dinero recaudado).
- El PR #62 dejó un borrador (`GET /web/metricas/actividad` y `/ajustes-margen`) sobre las vistas del 0001/0003. No trae los KPI, no filtra por comercial, no convierte USD y la regional sale siempre nula.
- El rol ya viaja en el claim `rol` del access token, emitido en el login SSO tras la verificación doble contra `dbsolucionesti` y `UsuariosRoles` (ADR-009). Las políticas `Comercial` y `GerenteComercial` de la Api lo leen.
- `Cotizaciones` no tiene `cotizacionOrigenId` (se quitó el 2026-09-24). Una versión nueva es una fila con `version + 1` y el folio de la v1 con sufijo `-vN` (ADR-011).
- `Cotizaciones.moneda` y `trmAplicada` existen desde el 0001. En USD, `precioFinal` está en dólares y `costo` siempre en pesos (ADR-014).
- `origen` y `destino` de la cotización llegan con el script 0004 (ADR-015), que todavía no está en `QA`.
- `UsuariosWeb.regional` existe desde el 0001 pero nadie la llena. La fuente prevista es `DWCubosEBSSAP.DimEjecutivo` (cruce `nombreUsuario` → `DimEjecutivo.ID`, columna `REGIONAL`; 0001 y `docs/archivo/ESQUEMA_OBJETIVO_SQLSERVER_PARTE_B.md` §5.8), que no está conectada. No hay más información de esa tabla en el repositorio (ni en el Python, ni en `docs/archivo/PROPUESTA_REFACTOR_BD_COTIFLY.md`, ni en `docs/Entendimiento_Cotifly.docx.md`).
- **Actualización (#81, 2026-09-29):** el equipo verificó la fuente real: `DWCubosEBSSAP` con un usuario SQL de solo lectura, tabla `DimEjecutivo(ID, NOMBRE, REGIONAL, CORREO, USUARIO, FECHACREACION, FECHAMODIFICACION)`. El cruce es por `USUARIO` (con el dominio `boterosoto\` pegado), no por `ID`. #81 conecta además el login (sección 6).

## Decisión

### 1. Endpoints

| Endpoint | Rol (política) | Alcance |
|---|---|---|
| `GET /web/metricas/comercial?desde&hasta` | `comercial` o `gerente_comercial` (`Comercial`) | Las cotizaciones propias (`usuarioId` = `sub`) |
| `GET /web/metricas/regional?desde&hasta&usuario_id` | solo `gerente_comercial` (`GerenteComercial`) | La regional del usuario autenticado, resuelta en el servidor; `usuario_id` filtra a un comercial |

- El rol sale del token (claim `rol`), nunca de una columna de `UsuariosWeb`. El caso de uso lo revalida (defensa en profundidad, como la administración de líneas).
- Un `comercial` en `/regional` recibe 403 (`ErrorWeb`).
- `/regional` no acepta otra regional que la del gerente: siempre es la suya (HU-037.3 agrega `?regional=`: el gerente solo puede pedir la suya y el director cualquiera; sección 11).
- Un `usuario_id` que no es de la regional (o no existe) responde **404** `ErrorWeb` "Comercial no encontrado en tu regional.", igual en los dos casos para no revelar usuarios de otras regionales.
- `comerciales` trae siempre a todos los usuarios de la regional con sus indicadores (para el selector dinámico), aunque se filtre por `usuario_id`. El filtro aplica a `indicadores`, `serie_mensual` y `concentracion_ciudades`.
- Ambos devuelven una `serie_mensual` (línea de tiempo por mes de Bogotá: cantidad, ganadas, perdidas y dinero) con todos los meses del periodo, también los vacíos. HU-034.1 (#201): el dinero de cada mes son los mismos montos de `indicadores` (`valor_cotizado_cop`, `valor_ganado_cop`, `costeo_total`, `costeo_ganadas` y `ganancias`, sección 4) calculados con la misma función sobre las cotizaciones del mes, así que la suma de los meses es exactamente el total del periodo (los montos de origen tienen 2 decimales). El criterio no cambia. `/regional` agrega `concentracion_ciudades` y `lineas_negocio_activas`.

### 2. Universo: una fila por cadena de versiones

- **Cadena** = las filas del mismo `usuarioId` con el mismo **folio base** (el folio sin el sufijo `-v{version}`). Cada cadena cuenta **una vez**.
- **Valores** (precio, costo, margen, moneda, TRM, costeo, ciudades, deadline, ajustes de margen): los de la **versión vigente**, la de mayor `version`.
- **Periodo y mes de la serie:** la **primera creación** de la cadena, `MIN(createdAt)` de todas sus versiones, en fecha de Bogotá. Así una cotización se queda en el mes en que nació y los periodos pasados no cambian cuando se rehace.
  - Ejemplo: `PCN-DG-10082026-001` se crea el 10 de agosto (v1, 1.000.000) y se rehace el 3 de septiembre (`-v2`, 1.100.000). Cuenta **una** cotización en **agosto** con valor 1.100.000; septiembre no la ve. Consultar agosto antes o después de la v2 da la misma cantidad.
- **Resultado** (`ganada`): el **más reciente de toda la cadena**, es decir, el `ganada` no nulo con mayor `marcadoAt` (sin fecha, al final) y, a igualdad, el de mayor `version`. Una v1 marcada como ganada con una v2 aún sin marcar cuenta como **ganada**: el resultado no se pierde al rehacer.
- Se cuentan **todos los estados** (con o sin precio, enviada o no), igual que "Mis cotizaciones".
- `versiones` = filas de esas cadenas (todas sus versiones), como referencia; no entra en ningún cociente.
- **Diferencia con el backlog:** la nota del 2026-09-24 (HU-010.1) dice contar cada versión tal cual porque "no hay forma confiable de agruparlas sin la FK". El folio de ADR-011 (aprobado el 2026-09-25) sí es una forma confiable: la v2+ reutiliza el folio base de la v1 y solo el servidor la genera. Se sigue la decisión del issue (#45, "decidir y documentar") y del coordinador del proyecto. Pendiente de confirmar con negocio.

### 3. Moneda (`valor_cop`)

- `valor_cop = precioFinal` si `moneda` es `COP` (o nula).
- `valor_cop = round(precioFinal × trmAplicada, 2)` (half-to-even) si la moneda es otra y `trmAplicada > 0`. Se redondea por fila y luego se suma, para que QA lo recalcule igual.
- Una fila en otra moneda con `trmAplicada = 0` (el dominio lo impide) no suma dinero, pero sí cuenta en `cantidad`.
- `costo` siempre está en COP y nunca se multiplica. Las filas en COP (`trmAplicada = 0`) nunca se multiplican.
- **No es el `precio_cop` de ADR-014.** Allí `precio_cop` es el precio en pesos recalculado del costo y el margen antes de convertir (el piso del PDF, sin columna propia). `valor_cop` se calcula con las columnas guardadas (`precioFinal × trmAplicada`) y puede diferir unos pesos del `precio_cop` por el redondeo a centavos del precio en USD. El nombre distinto evita confundirlos.

### 4. Definiciones

| Campo | Definición |
|---|---|
| `cantidad` | Cadenas del universo (sección 2) |
| `versiones` | Filas de esas cadenas (todas las versiones) |
| `ganadas` / `perdidas` | Resultado de la cadena ganada / perdida, con o sin precio |
| `sin_resultado` | Con precio y sin resultado |
| `pendientes_por_cotizar` | Sin precio y sin resultado |
| `conversion` | ganadas / cantidad (backlog HU-010: "#ganadas / #totales"); `null` si cantidad = 0 |
| `conversion_sobre_cerradas` | ganadas / (ganadas + perdidas); `null` si no hay cerradas |
| `margen_promedio` | Promedio simple del margen de cada cotización (fracción) de las que lo tienen. Desde HU-021 el margen de una cotización es el de sus rutas ponderado por `costoFinal` (sección 10) |
| `margen_ponderado` | Por ruta (sección 10), sobre el precio: Σ(precio de la ruta en COP − `costoFinal`) / Σ precio de la ruta en COP, de todas las rutas con costo > 0 y precio > 0 |
| `costeo_total` | SUM(`costo`) en COP de todo el universo |
| `costeo_ganadas` | SUM(`costo`) en COP de las ganadas |
| `valor_cotizado_cop` | SUM(`valor_cop`) |
| `valor_ganado_cop` | SUM(`valor_cop`) de las ganadas. Es el **valor cotizado** de las ganadas, no dinero recaudado: la UI lo rotula "Valor ganado (COP)" |
| `ganancias` | SUM(`valor_cop` − `costo`) de las ganadas con precio y costo |
| `cotizaciones_usd` | Cadenas en moneda distinta de COP |
| `tiempo_promedio_cotizacion_horas` | Promedio de (`costeoListoAt` − `createdAt` de la versión vigente) en horas; se descartan las filas con `costeoListoAt < createdAt` (datos incoherentes) |
| `desviacion_promedio_margen` | Promedio de `MargenAjustes.diferencia` (con signo) de los ajustes de la versión vigente |
| `desviacion_promedio_margen_abs` | Promedio de `ABS(diferencia)` de esos ajustes (magnitud de la negociación, sin que se compensen subidas y bajadas) |
| `ajustes_margen` | Cantidad de esos ajustes |
| `deadline_vencidas` | Sin resultado y con `deadline` anterior al momento de la consulta; `null` si la columna `deadline` (script 0004) no existe |
| `lineas_negocio_activas` | `MargenesPolitica.isActive = 1` (solo en `/regional`) |

Identidades que QA puede comprobar:

- `cantidad = ganadas + perdidas + sin_resultado + pendientes_por_cotizar` (siempre).
- `ganancias = valor_ganado_cop − costeo_ganadas`, cuando todas las ganadas tienen precio y costo y ninguna en USD tiene `trmAplicada = 0`.

Otras reglas:

- **Ajuste respecto al pedido inicial del coordinador:** se pidió `conversion` = ganadas / (ganadas + perdidas) y `conversion_sobre_total` = ganadas / cantidad. El backlog define "% de conversión (#ganadas / #totales)" y la regla de ADR es que el backlog manda: `conversion` es ganadas / cantidad y la otra se llama `conversion_sobre_cerradas`.
- Fracciones con 4 decimales, montos con 2, horas con 2; siempre half-to-even (`MidpointRounding.ToEven`).
- El cálculo es una regla pura de dominio (`Cotifly.Dominio.Metricas.clsCalculadoraMetricas`). El repositorio trae una fila por cadena: `ROW_NUMBER` para la vigente, `MIN(createdAt) OVER` para el periodo, `OUTER APPLY TOP 1` para el resultado y `OUTER APPLY` sobre `MargenAjustes` por `cotizacionId` (índice del 0002) para los ajustes. La calculadora agrega esas filas.
- **Servicios conexos (ADR-015):** no se suman todavía a precio ni costo; se integran después de #42. **Desde HU-021** ya están dentro del `costoFinal` de cada ruta (sección 10).

### 5. Periodo

- `desde` y `hasta` opcionales, fechas `AAAA-MM-DD` de America/Bogota, ambas incluidas: de `desde` 00:00 a `hasta` 24:00 en la zona de negocio (`IClsCalendarioNegocio`), comparado con la primera creación de la cadena (sección 2).
- Por defecto: del 1 de enero del año en curso a hoy (Bogotá). Se eligió el año en curso y no "todo" para que la serie mensual tenga un tamaño acotado y el tablero cargue rápido.
- `hasta < desde`, fechas mal formadas o un rango de más de 3 años responden 400.
- **Año y mes (HU-010.5, #187), solo en `/web/metricas/regional`:** `?anio=2026` es el año completo (2026-01-01 a 2026-12-31) y `?anio=2026&mes=10` ese mes completo, en las mismas fechas de Bogotá que `desde`/`hasta` (el periodo se resuelve en fechas de negocio y `IClsCalendarioNegocio.InicioDelDia` da los límites, así que un mes termina a las 24:00 de Bogotá de su último día). Si llega `anio`, reemplaza a `desde`/`hasta`, que se ignoran: no se combinan, para que no haya que definir intersecciones. `mes` sin `anio`, `mes` fuera de 1 a 12, o `anio` fuera de 2020 al año en curso (Bogotá) más uno, o que no sean enteros, responden 400 `ErrorWeb` (`SOLICITUD_INVALIDA`, como el resto de parámetros del tablero). El límite de 3 años no aplica porque un año completo nunca lo supera. `/web/metricas/comercial` no los acepta: el tablero del comercial no tiene ese selector y su comportamiento no cambia.

### 6. Regional del usuario

Puerto `IClsRegionalUsuario` (Aplicación) con dos fuentes:

1. **`DWCubosEBSSAP.DimEjecutivo`** tras el puerto `IClsDirectorioEjecutivos` (adaptador `clsDirectorioEjecutivosDimEjecutivo`, SQL parametrizado de solo lectura). Se activa solo si hay `ConnectionStrings__Regional`. Esquema, tabla y columnas son configurables (`Regional__Esquema`, `Regional__Tabla`, `Regional__ColumnaUsuario`, `Regional__ColumnaRegional`; por defecto `dbo.DimEjecutivo`, `USUARIO`, `REGIONAL`). **Esquema real (verificado en #81):** `DimEjecutivo(ID, NOMBRE, REGIONAL, CORREO, USUARIO, FECHACREACION, FECHAMODIFICACION)`; el cruce con `tblSAPUsuariosApp.NOMBREUSUARIO` es `USUARIO`, que viene con el dominio pegado (`boterosoto\usuario`). El adaptador parte el valor en SQL en la **última** `\` (`CHARINDEX('\', REVERSE(col))`), limpia espacios, tabuladores y saltos de línea a los lados (`REPLACE` de `CHAR(9)`, `CHAR(10)` y `CHAR(13)` + `LTRIM`/`RTRIM`, porque no se conoce el nivel de compatibilidad de `DWCubosEBSSAP` para usar `TRIM(... FROM ...)`) y compara el nombre sin distinguir mayúsculas contra una lista de parámetros. El dominio (lo de antes de la última `\`, sin `\` sobrantes) debe ser `Regional__Dominio` (por defecto `boterosoto`, sin distinguir mayúsculas): una fila de otro dominio (`otrodominio\jperez`, o `a\\b`) no cuenta y deja un warning con el nombre de usuario; un valor sin `\` se compara tal cual. Con `Regional__Dominio` vacío se acepta cualquier dominio. Un usuario con dos regionales distintas en la tabla (también `boterosoto\x` y `x` con regionales distintas) se ignora en ella (warning en el log) y se usa su respaldo. No todos los usuarios de ClientePro están en la tabla (solo los ejecutivos): para ellos no hay regional de esta fuente, y eso no es un error.
2. **`UsuariosWeb.regional`**: desde #81 el login la copia de `DimEjecutivo` (ver "Regional en el login"). Para quien no está en `DimEjecutivo`, la llena el DBA con un `UPDATE` (plantilla en `docs/10-operacion.md`). No hace falta un script de esquema nuevo: la columna ya existe desde el 0001. La fila de `UsuariosWeb` se crea en el primer login (alta JIT), así que el `UPDATE` va después de ese primer ingreso.

Regla: `regional efectiva(u) = DimEjecutivo[u.nombreUsuario] ?? UsuariosWeb.regional`, comparando sin espacios sobrantes y sin distinguir mayúsculas. Los miembros de una regional R son los usuarios de `UsuariosWeb` cuya regional efectiva es R.

- Gerente sin regional efectiva: **409 `REGIONAL_NO_ASIGNADA`** (`ErrorNegocio`). Nunca se devuelven todas las regionales ni se cae a "solo lo propio" en silencio.
- `DimEjecutivo` configurada pero caída: **503 `REGIONAL_NO_DISPONIBLE`** (fail-closed; no se mezcla la fuente viva con la de respaldo cuando la viva falla).
- `fuente_regional` (`dim_ejecutivo` | `usuarios_web`) dice de dónde salió la regional del gerente.
- **Caché corta:** las respuestas exitosas de `DimEjecutivo` (regional de cada usuario, incluido "no aparece", y usuarios de cada regional) se reutilizan 60 s por proceso (`clsCacheDirectorioEjecutivos`), así cada cambio del selector no vuelve a consultar la tabla. Los fallos no se guardan.
- **El gerente es miembro de su regional:** aparece en `comerciales` con `rol: gerente_comercial` y sus propias cotizaciones cuentan en el agregado. El `rol` de cada item sale de las tablas propias de Cotifly (`UsuariosRoles` + `Roles`, ADR-009), que guardan el rol verificado en el login; `null` si el usuario no tiene asignación activa.
- **Nota para el DBA:** el texto de la regional debe escribirse igual en `DimEjecutivo` y en `UsuariosWeb.regional` (se compara sin mayúsculas ni espacios sobrantes, pero "Antioquia" y "Regional Antioquia" son regionales distintas).
- **Regional en el login (#81, HU-003.6 / ADR-008):** `clsVerificadorIdentidad` usa el mismo puerto `IClsDirectorioEjecutivos` (no hay un segundo DbContext ni otro puerto para la tabla):
  - **Primer ingreso (alta JIT):** `UsuariosWeb.regional` nace con la regional de `DimEjecutivo`, o NULL si el usuario no aparece.
  - **Cada login siguiente**, ya superada la verificación doble de ADR-009 (rol y `NOMBREUSUARIO` coinciden): `clsUsuarioWeb.ActualizarDatos` refresca la regional y, de paso, el nombre (`DESCRIPCIONUSUARIO`) y el correo (`EMAIL`) del almacén. Ninguno de los tres decide si la persona entra, así que la regla de "rol y nombre de usuario deben coincidir" no cambia. Un login rechazado no consulta nada ni cambia datos. El refresh tampoco (Google Chat, con `permitirAlta: false`, tampoco lo hacía hasta su retiro en HU-013.9, #105). Si el correo cambia, queda un warning con el nombre de usuario (sin los correos), porque el correo era la identidad en Google Chat (ADR-011, retirado en HU-013.9).
  - **La regional sí decide el alcance de los datos del gerente:** qué comerciales ve en el tablero (`/web/metricas/regional`) y de quiénes puede descargar PDF (`clsDescargaPdfService`). Por eso `DimEjecutivo` es una **fuente de autorización de alcance**, aunque no de acceso: quien pueda escribir en esa tabla (o en `UsuariosWeb.regional`) puede ampliar lo que ve un gerente. Se trata con el mismo cuidado que `tblSAPUsuarioRoles`: conexión de solo lectura, dominio validado, filas ambiguas ignoradas, y los cambios se ven en el login siguiente.
  - **Usuario ausente de `DimEjecutivo`:** se conserva la regional que tenga (NULL o la que puso el DBA). Solo se sobrescribe cuando la tabla devuelve un valor, para no borrar una asignación manual. Consecuencia (riesgo operativo): si alguien sale de la tabla, conserva su última regional, y con ella el alcance de gerente, hasta que el DBA la cambie. Seguimiento: marcar el origen de la regional para limpiarla solo cuando vino de `DimEjecutivo` (hace falta una columna nueva, script 000N).
  - **Nunca bloquea el login:** sin `ConnectionStrings__Regional` no se consulta; si la base falla, se agota el tiempo o hay cualquier error, queda un warning con el nombre de usuario y el tipo de error (sin el mensaje de la base) y el login sigue con la regional que ya tenía. Nunca un 401 ni un 500. A diferencia del tablero (fail-closed, 503), aquí no se cambia nada: la regional guardada, y con ella el alcance del gerente, queda como estaba. Los fallos no se guardan en caché, así que mientras `DimEjecutivo` esté caída cada login paga hasta el límite (3 s); un circuit breaker queda como mejora futura (`docs/10-operacion.md`).
  - **Tiempo:** límite total de `min(Regional__TiempoEsperaLoginSegundos, Regional__TiempoEsperaSegundos)`, por defecto 3 s, con cancelación propia y `WaitAsync` (corta la espera aunque el cliente SQL tarde en atender la cancelación al abrir la conexión). La caché de 60 s también sirve al login.
- **Convergencia (cierra el seguimiento anterior):** todo sale ahora de `DimEjecutivo`, directamente o a través de la columna refrescada en cada login:
  - el tablero del gerente (`IClsRegionalUsuario`) la consulta en vivo, con `UsuariosWeb.regional` como respaldo;
  - `LoginResponse.user.ubicacion` lee la columna, recién refrescada en ese mismo login;
  - la descarga de PDF ajenos (`clsDescargaPdfService`) y los borradores `/actividad` y `/ajustes-margen` leen la columna, que tiene el valor de `DimEjecutivo` del último login de cada usuario. No se pasan a `IClsRegionalUsuario`: agregarían una consulta externa por descarga y un 503 nuevo por un dato que ya está fresco. La diferencia solo aparece si alguien cambia de regional en `DimEjecutivo` y todavía no vuelve a entrar; se corrige en su siguiente login.
  - El futuro "Mis cotizaciones" del gerente (HU-004.5) puede leer la columna con el mismo criterio.

### 7. Concentración por ciudad

- Se agrupa por el municipio de **destino** (`por_destino`: dónde se entrega la carga, la vista del mapa) y también por el de origen (`por_origen`), con cantidad, ganadas, valor cotizado y valor ganado en COP. El texto es el del catálogo de municipios de Nexo, tal como se guardó; el frontend lo ubica en el mapa.
- Las columnas `origen`/`destino` son del 0004. La consulta comprueba si existen (`COL_LENGTH`); sin ellas, `disponible = false` y las listas van vacías. Así esta rama funciona sobre `QA` hoy y el mapa se llena solo cuando se aplique el 0004, sin tocar el código.
- Las cotizaciones del chat no guardan ciudades: cuentan en `sin_destino` / `sin_origen`.
- **Solicitudes de varias líneas:** cuentan solo la ciudad de la cabecera de la cotización (`Cotizaciones.origen`/`destino`); las líneas adicionales (HU-011, `CotizacionLineasSolicitud`) no se desglosan todavía. **Desde HU-021** la ciudad es la de la **ruta 1** (`origenNombre`/`destinoNombre` del 0010) y la cotización cuenta una vez.
- El "dinero" del mapa es `valor_ganado_cop` (valor cotizado de las ganadas), no dinero recaudado.

### 8. Deadline vencido y score de urgencia (Nexo)

- **Deadline vencido: implementado** con la columna `Cotizaciones.deadline` que trae el script 0004 (PR #73). `deadline_vencidas` cuenta las cotizaciones sin resultado con `deadline` anterior al momento de la consulta. Igual que las ciudades, la consulta comprueba la columna con `COL_LENGTH`: sin el 0004 el campo es `null`.
- **Score de urgencia: no se implementa.** El backlog lo define como "dato que expone Nexo, no se recalcula en Cotifly", y el contrato de Nexo para consultarlo por solicitud no existe todavía. Cuando exista, se agrega como campo nuevo de `IndicadoresMetricas` (cambio compatible).

### 9. Borradores

`/web/metricas/actividad` y `/ajustes-margen` siguen funcionando igual y quedan `deprecated: true` en el contrato. Se retirarán cuando el frontend use los nuevos.

> **Actualización (2026-10-15, HU-014.3 #152):** retirados. Los dos endpoints responden 404 y salieron del contrato; las vistas las borra `db/scripts/08_retirar_vistas_actividad.sql` (el `01` ya no las crea). Lo que sigue en esta sección es el historial de la decisión.

- **HU-021.10 (#137) y HU-014.4:** HU-014.4 (retirar estos borradores y sus vistas) depende de que el tablero de HU-010 esté validado en producción, y todavía no lo está. Por eso #137 **conserva** las vistas: `vAjustesMargenPorComercial` y `vActividadPorComercial` ya las redefinió el `0009` (y `01_crear_esquema.sql`) para el esquema de rutas, y #137 las verificó con pruebas de integración. No hace falta un script `0011`. Cuando se haga HU-014.4, su script de retiro (`000N_retirar_vistas_actividad.sql`) cubre también la instalación desde cero.

### 10. Rutas: conteos por cotización, valores por ruta (HU-021.10, #137)

Decisión 8 del 2026-10-08 (HU-021): una solicitud (`CotizacionesSolicitud`) tiene una o varias rutas (`CotizacionLineasSolicitud`), cada una con su línea de negocio, su costo, su margen y su precio.

- **Conteos por cotización.** `cantidad`, `ganadas`, `perdidas`, `sin_resultado`, `pendientes_por_cotizar`, `cotizaciones_usd`, la serie mensual y la concentración por ciudad cuentan **cotizaciones** (cadenas, sección 2). Una cotización con tres rutas de tres líneas de negocio cuenta **1**.
- **Valores de la cotización a partir de sus rutas** (versión vigente):
  - `costo` = Σ `costoFinal` de sus rutas (fletes + retorno + levante + conexos); `null` si alguna ruta no lo tiene. Los servicios conexos ya están dentro.
  - `precioFinal` = el de la cabecera, que el dominio mantiene como Σ `precioFinalLinea`.
  - margen de la cotización = Σ(`margenAplicado` × `costoFinal`) / Σ `costoFinal` de sus rutas con margen y costo > 0; sin costo, el promedio simple.
- **`margen_ponderado`** (indicador nuevo) = margen agregado **sobre el precio**, porque el margen se calcula sobre el precio: Σ(`valor_cop(ruta)` − `costoFinal`) / Σ `valor_cop(ruta)` de **todas las rutas del universo** con costo > 0 y precio > 0 (`valor_cop(ruta)`: el precio de la ruta en COP, abajo). No se pondera por costo: Σ(margen × costo) / Σ costo da otro número cuando las rutas tienen márgenes distintos. `margen_promedio` se conserva (promedio simple por cotización) para no romper el contrato.
- **Apertura por línea de negocio** (`por_linea_negocio`, en `/comercial` y en `/regional`, con el mismo filtro que `indicadores`): una fila por línea de las rutas (`null` = rutas sin línea).
  - `cotizaciones`, `ganadas`, `perdidas` = cotizaciones **distintas** con al menos una ruta de la línea; el resultado es el de la cotización. La cotización de tres líneas aparece en las tres filas: **la suma de la apertura puede superar `cantidad`** (no es una partición).
  - `rutas` = rutas de la línea.
  - `costeo_total` = Σ `costoFinal` de esas rutas; `costeo_ganadas` = lo mismo en cotizaciones ganadas. Σ de la apertura = `costeo_total` de los indicadores cuando todas las rutas tienen costo.
  - `valor_cotizado_cop` = Σ `valor_cop` de cada ruta, con `valor_cop(ruta)` = `precioFinalLinea` en COP o `round(precioFinalLinea × trmAplicada, 2)` (half-to-even, TRM de su cotización) en otra moneda; `valor_ganado_cop` = lo mismo en cotizaciones ganadas. Por el redondeo por ruta, en USD la suma de la apertura puede diferir **unos centavos** del `valor_cotizado_cop` de los indicadores (que redondea el precio de la cabecera).
  - `ganancias` = Σ(`valor_cop(ruta)` − `costoFinal`) de las rutas de cotizaciones ganadas con precio y costo. **La suma de `ganancias` de la apertura puede superar la del indicador:** el indicador deja fuera la cotización completa si a una de sus rutas le falta el costo (su `costo` es `null`), mientras que la apertura deja fuera solo esa ruta.
  - `margen_ponderado` = la fórmula de arriba sobre las rutas de la línea.
- **Datos migrados por el `0009`:** el `0009` guardó `costoFinal` **con** los servicios conexos y `precioFinalLinea` **sin** ellos (el precio viejo no los incluía). En esas rutas, hasta que el backend las recalcule (cualquier cambio de camión, conexo o margen recalcula la ruta), `ganancias` y `margen_ponderado` por línea pueden salir **subestimados o negativos**. Las cotizaciones creadas después de HU-021 no tienen el problema.
- **Diferencia conocida con las vistas legadas (HU-014.4):** `vAjustesMargenPorComercial` (que lee `/web/metricas/ajustes-margen`, obsoleto) agrupa un ajuste sin ruta (`lineaSolicitudId` NULL) en la línea `NULL`, mientras que el tablero lo asigna a la ruta 1 de su cotización. No se corrige la vista (haría falta un script `0011` para un endpoint que HU-014.4 va a retirar).
  - `ajustes_margen` y `desviacion_promedio_margen` = los `MargenAjustes` de esas rutas (`lineaSolicitudId`). Un ajuste sin ruta, o con una ruta que no es de su cotización, cuenta en la ruta 1 (la de menor `orden`), igual que la migración del `0009`. Así cada ajuste cuenta en una sola ruta y la suma por rutas es igual a los ajustes de la cotización.
  - Orden: `valor_cotizado_cop` descendente, luego el nombre; la fila sin línea al final.
- **Implementación:** el repositorio hace una segunda consulta con las rutas de las versiones vigentes (`OPENJSON` de ids, `OUTER APPLY` a `MargenAjustes`) y la calculadora de dominio agrega (`clsCalculadoraMetricas.PorLineaNegocio`). No hace falta un cambio de esquema.
- **Alternativa descartada: contar rutas.** Inflaría `cantidad` y la conversión con cada ruta de una licitación (una solicitud de 20 rutas ganada no son 20 cotizaciones ganadas).

### 11. Rol Director (HU-037, #209 a #212)

Gerencia pidió un rol por encima del gerente comercial que vea **todas** las regionales. Decisiones (2026-10-26):

- **Tercer rol interno `director`** (`RolInterno.Director`; catálogo `dbo_cotifly.Roles` con el script 15, #209). El rol sigue saliendo del
  token (claim `rol`), nunca de una columna de `UsuariosWeb` (ADR-009).
- **Precedencia `director` > `gerente_comercial` > `comercial`** cuando varios roles externos de una persona están mapeados. Es una regla
  explícita y auditada, como la del gerente: `precedencia_director` (y `precedencia_gerente_comercial` sin director). Si la persona pierde
  el rol que ganó, su asignación en `UsuariosRoles` deja de coincidir y queda en revisión (`rol_externo_cambio`).
- **Permisos (#210): el director usa todo lo del gerente.** Se agrega `director` a las políticas existentes en vez de crear otra:
  `Comercial` (consola del comercial: `comercial`, `gerente_comercial`, `director`) y `GerenteComercial` (tablero y listado regional,
  administración de líneas de negocio y márgenes: `gerente_comercial`, `director`). Una política aparte duplicaría los mismos endpoints
  y obligaría a repetir los atributos; el alcance distinto (todas las regionales) no es de la política, es del caso de uso.
- **Defensa en profundidad:** los casos de uso que revalidaban `Rol == GerenteComercial` usan `RolInterno.EsGerencial()`
  (`gerente_comercial` o `director`): `clsLineasNegocioAdminService`, `clsTableroMetricasService`, `clsCotizacionesRegionalService`,
  `clsDetalleCotizacionService` y `clsDescargaPdfService`. La auditoría de las consultas ajenas registra el rol real.
- **Roles desconocidos:** el repositorio de roles solo lee los códigos que el backend conoce. Un rol nuevo en el catálogo con una imagen
  vieja ya no tumba el login de todos (antes, la fila `director` con una imagen anterior a HU-037 hacía fallar la lectura del catálogo, y
  login y refresh respondían a todos el 401 genérico). Cada rol activo ignorado deja un warning con su `id` al leer el catálogo.
- **IDROLES por ambiente** (scripts de operación de Datos, no del backend): prueba `gerente_comercial` 25 (provisional) y `director` 31
  (#209, parte B); producción `comercial` 34, `gerente_comercial` 29 y `director` 31 (#212). `uq_roles_idRolExterno` obliga a sacar al
  gerente del 31 antes de dárselo al director.
- **`/health/ready` no exige el 15:** no agrega columnas; sin la fila `director` nadie entra con ese rol (falla cerrado).
- **Alcance de todas las regionales (#211), centralizado en `IClsRegionalUsuario`** para que los cuatro consumidores (tablero, listado
  regional, detalle y PDF ajenos) no repitan el `if` del rol:
  - `ResolverAlcanceAsync(usuario, rol, regional?)` → `clsAlcanceRegional(Regional?, Fuente?, Miembros)`. Director sin `regional`: todos
    los usuarios de `UsuariosWeb` (`Regional = null` = todas; no consulta `DimEjecutivo`). Director con `regional`: los miembros de esa
    regional con la regla de la sección 6 (`Fuente = null`: la regional no sale de su usuario). Gerente: su regional efectiva; con
    `regional` igual a la suya (sin mayúsculas ni espacios sobrantes) se acepta, con otra **403 `REGIONAL_NO_PERMITIDA`** (código nuevo,
    `ErrorNegocio`: el rol sí puede usar el endpoint, lo que no puede es esa regional); sin regional sigue fallando cerrado
    (`REGIONAL_NO_ASIGNADA` o `REGIONAL_NO_DISPONIBLE`). Otro rol: `SIN_PERMISO`.
  - `PuedeVerDeAsync(usuario, rol, dueño)`: director siempre; gerente si el dueño es de su regional; otro rol, nunca. Lo usan el detalle
    por folio y el PDF; la auditoría de esas consultas registra el rol real.
  - `ListarRegionalesAsync(usuario, rol)` para `GET /web/regionales`: director, las regionales efectivas de todos los usuarios de Cotifly
    (la misma fuente de la sección 6, sin repetir, ordenadas; una regional de `DimEjecutivo` sin usuarios en Cotifly no sale); gerente, la
    suya; `DimEjecutivo` caída: 503.
  - La regla vive en dos métodos estáticos (`ComponerAlcanceAsync`, `ComponerPermisoAsync`) sobre las operaciones básicas del puerto, para
    que las pruebas de los consumidores apliquen la misma regla sobre un puerto simulado sin copiarla.
- **Respuesta para el director:** `regional` y `fuente_regional` de `MetricasRegional` y `CotizacionesRegional` pasan a ser anulables
  (`null` = todas; `fuente_regional` siempre `null` para el director). Para el gerente no cambian. El tope de 100 filas del listado es del
  conjunto: el director sin regional ve las 100 más recientes de todas.
- **Listas grandes de usuarios:** con el director sin regional, las consultas por lista de usuarios mandan la lista como un solo parámetro
  (`EF.Parameter`, `OPENJSON`; el repositorio de métricas ya lo hacía), así no topan con el límite de 2100 parámetros de SQL Server.

### 12. Licitación en solo consulta para el gerente y el director (HU-034.1, #201 parte A)

Negocio pidió el 2026-10-27 que el gerente (su regional) y el director (cualquier regional) vean la licitación de un comercial y bajen su
Excel. Decisiones:

- **Misma regla que el detalle y el PDF:** `GET /web/licitaciones/{folio}` y `GET /web/licitaciones/{folio}/excel` usan
  `PuedeVerDeAsync` (sección 11). El dueño primero (la consulta filtra por dueño: un comercial ajeno nunca carga el agregado de otro); si no
  es suya y el rol es gerencial, `IClsAccesoCotizacionAjena` carga la cotización por folio y aplica la regla. Sin regional o con la fuente
  caída se niega (fail-closed). Cualquier negativa es 404, igual que un folio inexistente.
- **Auditoría:** cada intento gerencial sobre una licitación ajena que existe, permitido o no, queda en `EventosHerramienta` con
  `consultar_licitacion` (vista) o `exportar_excel_licitacion` (Excel), con el folio, el usuario, el rol real, el dueño y el motivo de la
  negativa (`REGIONAL_NO_ASIGNADA`, `REGIONAL_NO_DISPONIBLE`). Ni el dueño ni un comercial ajeno se auditan, como en el detalle. Un folio
  que no es licitación (p. ej. un spot de la regional pedido por `/web/licitaciones/{folio}`) responde 404 sin evento: el tipo se filtra
  antes de auditar, para no dejar un "permitido" que terminó en 404. El rol es el de la sesión (el del access token, como en el detalle y el
  PDF): si le quitan el rol de gerente, el token vigente lo conserva hasta vencer y el login siguiente se rechaza (`rol_externo_cambio`).
- **Solo lectura:** `Licitacion.solo_lectura` (aditivo, siempre presente) es `true` para el gerente y el director y `false` para el dueño,
  igual que `DetalleCotizacion.solo_lectura`. Las mutaciones no cambian: reintentar la cubicación (con o sin `usar_respaldo`), reintentar
  el envío y finalizar siguen filtrando por dueño y les responden 404.
- **Sin costo, sin quitar nada:** la vista y el Excel de la licitación nunca tuvieron costos (solo precios con margen; el exportador no
  recibe el costo de Nexo, `clsExportacionLicitacion`). Por eso, con el criterio de `SinCostoFinal()` de HU-039.1 (#218: quien no es el
  dueño no recibe el costo bruto), no hay nada que ocultar: el gerente y el director reciben **lo mismo que el dueño**, salvo
  `solo_lectura`. Columnas del Excel: las del pliego, `Precio final Cotifly ({moneda})`, `Estado costeo Cotifly`, `Servicios conexos
  Cotifly` (nombre, cantidad y precio) y `Camiones Cotifly`, más las hojas `Resumen Cotifly` (folio, cliente, NIT, moneda, TRM, líneas,
  total sin conexos, conexos con precio, fecha de generación) y `Camiones Cotifly` (vehículo, carrocería, peso, volumen, llenado). Ninguna
  permite deducir el costo: no hay margen ni costo por ruta, camión o conexo. Si algún día el Excel lleva costos, la exportación para quien
  no es dueño debe quitarlos (la variante ya existe: `solo_lectura` sale del mismo punto, `CargarAsync`).
- **Fuera de alcance:** el Excel de "Ver detalle" de una cotización no licitación (`ExportarCotizacionAsync`, sin endpoint propio) sigue
  siendo solo del dueño. `GET .../costeo` también (el frontend usa el detalle por folio).

### 13. Margen contra la política de las mismas rutas (HU-016.1, #154, parte B)

`desviacion_promedio_margen` (sección 4) solo promedia `MargenAjustes.diferencia`, que se escribe cuando el comercial **negocia** un
margen: un comercial que nunca ajusta no tiene referencia. Gerencia pidió (Sara, 2026-10-05) comparar el margen aplicado con la política.
Decisiones de la Gerencia Comercial del 2026-10-27:

- **P3: la referencia es la política de las mismas rutas**, calculada en el servidor. Cada ruta guarda al cotizarse
  `CotizacionLineasSolicitud.margenPoliticaSugerido` (el margen de la política de su línea en ese momento), así que se compara lo mismo
  con lo mismo (si la política cambia después, las rutas viejas conservan la suya) y la tabla `MargenesPolitica` no se expone. Sin script
  de base de datos.
- **P4: los dos promedios**, y el semáforo con el ponderado. Campos nuevos (aditivos) de `IndicadoresMetricas`, también en
  `comerciales[].indicadores`:
  - rutas **comparables** = las del universo del tablero (versión vigente de cada cadena, sin las rechazadas por la Mesa, #183) con
    `margenAplicado` (se cotizaron) y con `margenPoliticaSugerido`;
  - `margen_politica_promedio` = promedio simple de la política de las comparables;
  - `margen_aplicado_promedio` (decisión del 2026-10-27, mismo día) = promedio simple del margen aplicado de **exactamente las mismas**
    comparables, con el mismo redondeo (4 decimales, half-to-even) y `null` sin ninguna: es el par directo de
    `margen_politica_promedio` (aplicado simple contra política simple). `margen_promedio` no sirve para eso porque es por cotización e
    incluye las rutas sin política;
  - `margen_politica_ponderado` = Σ(política × `costoFinal`) / Σ `costoFinal` de las comparables con costo &gt; 0;
  - `desviacion_margen_politica` = Σ((aplicado − política) × `costoFinal`) / Σ `costoFinal` de esas mismas: el aplicado ponderado por
    costo menos la política ponderada, redondeado una sola vez (4 decimales, half-to-even). Semáforo: verde ≥ 0, rojo &lt; 0;
  - `rutas_sin_margen_politica` = rutas con margen aplicado y sin política (datos anteriores a HU-021): no entran y se cuentan para que la
    UI avise que la comparación está incompleta.
  - Los márgenes son fracciones y el costo siempre está en COP: la moneda y la TRM no los afectan. El alcance (gerente, director,
    `usuario_id`) y el periodo son los del resto del tablero.
- **`desviacion_promedio_margen` no cambia** (opción B de la issue descartada): sigue siendo "solo ajustes negociados" y la usan otros
  reportes.
- **P4b: catálogo de líneas sin margen.** `GET /web/lineas-negocio` (política `Comercial`) devuelve las líneas activas
  `[{linea_negocio, etiqueta}]` para el desplegable de "Nueva solicitud" (hoy fijo en el frontend, sin `DevolucionContenedor`). Nunca trae
  el margen ni el método. Se lee de la base en cada petición (6 a 8 filas por clave primaria): sin caché, un cambio de la administración
  se ve de inmediato.

## Alternativas consideradas

- **Contar todas las versiones** (nota del backlog del 2026-09-24): infla las métricas con cada cotización rehecha. Se expone como `versiones` para quien la necesite.
- **Periodo por la fecha de la versión vigente** (primera implementación): rehacer una cotización la movía de mes y cambiaba periodos ya consultados. Se reemplazó por la primera creación de la cadena.
- **Resultado solo de la versión vigente** (primera implementación): un "ganada" marcado en la v1 desaparecía al crear la v2. Se reemplazó por el resultado más reciente de la cadena.
- **Agregar en SQL con vistas nuevas:** exige un script de esquema (y aprobación del DBA) y deja la conversión, la serie y la zona horaria repartidas en T-SQL, más difícil de probar. El volumen (unos 6.000 cotizaciones al mes en total, una regional es una fracción) cabe de sobra en memoria.
- **Tabla de mapeo nueva `RegionalesUsuarios` (un script 000N nuevo):** duplicaría `UsuariosWeb.regional`, que ya es exactamente ese mapeo, y agregaría un script más a la cola de aprobación del DBA (el 0005 es del frente de TRM; HU-011 usará un 000N nuevo).
- **Mapeo por configuración (`Regional__Asignaciones__APEREZ=...`):** obliga a redesplegar para mover a un comercial de regional. Descartado frente al `UPDATE` del DBA.
- **Sincronizar `UsuariosWeb.regional` en cada login como única fuente** (propuesta original de #45): metería una tercera base externa en el camino crítico del login (ADR-009 es fail-closed). En #81 se adoptó **además** de la consulta en vivo del tablero, pero sin bloquear: la búsqueda tiene un límite corto y cualquier fallo solo deja un warning (sección 6, "Regional en el login"). El tablero sigue consultando en vivo.
- **Un DbContext `clsRegionalDbContext` y un puerto nuevo `IClsAlmacenRegionalExterno` para el login** (lo que sugería #81): habría dos accesos a la misma tabla con reglas de cruce que podrían divergir (quitar el dominio, mayúsculas, filas ambiguas) y dos cachés. Se reutilizó el adaptador de #45 (SQL parametrizado con `Microsoft.Data.SqlClient`): la tabla es de solo lectura, se leen dos columnas y un DbContext de EF no aporta nada (sin seguimiento de cambios, sin modelo que validar). La intención de la issue se mantiene: `DimEjecutivo` de solo lectura detrás de un puerto de Aplicación.
- **Dejar en NULL la regional del usuario que no aparece en `DimEjecutivo`:** borraría la regional que el DBA puso a mano a quien no es ejecutivo (por ejemplo, un gerente fuera de la tabla), y el tablero le respondería 409. Se conserva el valor existente.
- **Exponer `MargenesPolitica` al comercial para que el cliente promedie (opción A de #154):** compararía el margen de las rutas con el
  promedio de **todas** las líneas, aunque el comercial cotice casi solo una, y con la política de hoy y no la del momento de cotizar.
  Descartada por la Gerencia (P3); el catálogo del desplegable va sin margen (P4b).
- **Redefinir `desviacion_promedio_margen` (opción B de #154):** cambiaría el significado de un campo publicado. Descartada: se agregan campos.
- **Comparar la política con `margen_ponderado`:** ese campo es el margen sobre el precio ponderado por el precio; la decisión P4 pide
  ponderar por costo, así que la desviación ponderada se calcula con el aplicado y la política de las mismas rutas y el mismo peso.
- **Filtrar un comercial ajeno con 200 vacío en vez de 404:** confunde al frontend (un selector con un id mal armado parecería "sin datos"). Se usa 404 como en el resto de `/web/*`.

## Consecuencias

- El frontend (#50, #51) tiene un contrato estable en `specs/openapi.yaml` con ejemplos; QA (#52) puede recalcular cada campo con las definiciones de la sección 4.
- El tablero del gerente responde 409 hasta que exista la regional (DimEjecutivo conectada o `UsuariosWeb.regional` llenada por el DBA). Es deliberado.
- El mapa responde `disponible = false` hasta que se aplique el 0004.
- `IClsCalendarioNegocio` gana `FechaDe(instante)` e `InicioDelDia(fecha)` para convertir entre UTC y la fecha de Bogotá.
- Nueva conexión opcional `ConnectionStrings__Regional` y sección `Regional__*`, con `Regional__TiempoEsperaLoginSegundos` desde #81 (documentadas en `docs/03-infraestructura.md` y `.env.example`).
- Desde #81, con la conexión configurada, `UsuariosWeb.regional` y `LoginResponse.user.ubicacion` quedan al día en cada login, y el tablero del gerente deja de responder 409 para los ejecutivos que están en `DimEjecutivo`.

## Pendientes

| Pendiente | Dueño |
|---|---|
| ~~Confirmar la tabla de la regional~~: hecho en #81 (`DWCubosEBSSAP.dbo.DimEjecutivo`, cruce por `USUARIO` sin el dominio, columna `REGIONAL`). Sigue pendiente saber si los gerentes están en ella y el catálogo de valores de regional | Equipo de datos / Gerencia Comercial |
| Cargar `ConnectionStrings__Regional` (usuario SQL de solo lectura ya probado por el equipo) en los secretos de Dokploy para QA y producción, y abrir la salida de red del clúster al servidor del cubo (puerto 1433) | Infraestructura (credencial: Sara / scalderon-bs) |
| Llenar `UsuariosWeb.regional` de quien no esté en `DimEjecutivo` (plantilla en `docs/10-operacion.md`) | DBA con Gerencia Comercial |
| Validar las definiciones: versión vigente vs. todas las versiones, `conversion` sobre el total, `costeo_total` sobre todo el universo, `ganancias` solo de ganadas y periodo por defecto (año en curso) | Gerencia Comercial (Sara / scalderon-bs) |
| Contrato de Nexo para el "score de urgencia" por solicitud (el deadline vencido ya se calcula con el 0004) | Equipo de Nexo |
| ~~Integrar los servicios conexos (ADR-015) en precio y costo del tablero~~: resuelto con HU-021 (están en `costoFinal` de la ruta) | Duban |
| Validar con Gerencia Comercial la apertura por línea de negocio (sección 10) y si `margen_promedio` debe pasar a ser el ponderado por ruta (HU-016.1 agrega el aplicado ponderado por costo de forma derivable: `margen_politica_ponderado + desviacion_margen_politica`, sección 12) | Gerencia Comercial (Sara / scalderon-bs) |
| ~~Pasar la descarga de PDF ajenos y `LoginResponse.ubicacion` a `IClsRegionalUsuario`~~: resuelto en #81 con la columna refrescada en cada login (sección 6, "Convergencia"). "Mis cotizaciones" del gerente lee la columna | Duban (HU-004.5) |
| Aplicar el 0004 para que el mapa por ciudad y `deadline_vencidas` tengan datos | DBA (ADR-015) |
