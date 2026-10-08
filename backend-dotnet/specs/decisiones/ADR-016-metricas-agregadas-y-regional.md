# ADR-016 — Métricas agregadas del tablero y regional del usuario

- **Estado:** Propuesta (definiciones pendientes de validar con Gerencia Comercial; la fuente de la regional ya está verificada en #81, faltan la credencial en el despliegue y la salida de red, ver Pendientes)
- **Fecha:** 2026-09-29
- **Issue / HU:** #45 (HU-010.1) y #81 (HU-003.6, regional en el login). Consumidores: clientepro-frontend #50 (HU-010.2, tablero del comercial), #51 (HU-010.3, tablero del gerente) y #52 (HU-010.4, QA de cálculos). Relacionada con ADR-009 (rol en el token), ADR-011 (folio y versiones), ADR-014 (TRM y cotizaciones en USD) y ADR-015 (costeo de Nexo, script 0004).

## Contexto

- HU-010 pide dos tableros: el del comercial (sus cotizaciones: cantidad, ganadas, % de conversión, margen promedio, costeo total, ganancias) y el del gerente comercial (lo mismo para toda su regional, filtrable por un comercial, con al menos una gráfica y un mapa de concentración por ciudad con el dinero recaudado).
- El PR #62 dejó un borrador (`GET /web/metricas/actividad` y `/ajustes-margen`) sobre las vistas del 0001/0003. No trae los KPI, no filtra por comercial, no convierte USD y la regional sale siempre nula.
- El rol ya viaja en el claim `rol` del access token, emitido en el login SSO tras la verificación doble contra `dbsolucionesti` y `UsuariosRoles` (ADR-009). Las políticas `Comercial` y `GerenteComercial` de la Api lo leen.
- `Cotizaciones` no tiene `cotizacionOrigenId` (se quitó el 2026-09-24). Una versión nueva es una fila con `version + 1` y el folio de la v1 con sufijo `-vN` (ADR-011).
- `Cotizaciones.moneda` y `trmAplicada` existen desde el 0001. En USD, `precioFinal` está en dólares y `costo` siempre en pesos (ADR-014).
- `origen` y `destino` de la cotización llegan con el script 0004 (ADR-015), que todavía no está en `QA`.
- `UsuariosWeb.regional` existe desde el 0001 pero nadie la llena. La fuente prevista es `DWCubosEBSSAP.DimEjecutivo` (cruce `nombreUsuario` → `DimEjecutivo.ID`, columna `REGIONAL`; 0001 y `docs/database/ESQUEMA_OBJETIVO_SQLSERVER.md` §5.8), que no está conectada. No hay más información de esa tabla en el repositorio (ni en el Python, ni en `docs/database/PROPUESTA_REFACTOR_BD_COTIFLY.md`, ni en `docs/Entendimiento_Cotifly.docx.md`).
- **Actualización (#81, 2026-09-29):** el equipo verificó la fuente real: `DWCubosEBSSAP` con un usuario SQL de solo lectura, tabla `DimEjecutivo(ID, NOMBRE, REGIONAL, CORREO, USUARIO, FECHACREACION, FECHAMODIFICACION)`. El cruce es por `USUARIO` (con el dominio `boterosoto\` pegado), no por `ID`. #81 conecta además el login (sección 6).

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
| `margen_promedio` | Promedio simple de `margenAplicado` (fracción) de las que lo tienen |
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
- **Servicios conexos (ADR-015):** no se suman todavía a precio ni costo; se integran después de #42.

### 5. Periodo

- `desde` y `hasta` opcionales, fechas `AAAA-MM-DD` de America/Bogota, ambas incluidas: de `desde` 00:00 a `hasta` 24:00 en la zona de negocio (`IClsCalendarioNegocio`), comparado con la primera creación de la cadena (sección 2).
- Por defecto: del 1 de enero del año en curso a hoy (Bogotá). Se eligió el año en curso y no "todo" para que la serie mensual tenga un tamaño acotado y el tablero cargue rápido.
- `hasta < desde`, fechas mal formadas o un rango de más de 3 años responden 400.

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
- **Solicitudes de varias líneas:** cuentan solo la ciudad de la cabecera de la cotización (`Cotizaciones.origen`/`destino`); las líneas adicionales (HU-011, `CotizacionLineasSolicitud`) no se desglosan todavía.
- El "dinero" del mapa es `valor_ganado_cop` (valor cotizado de las ganadas), no dinero recaudado.

### 8. Deadline vencido y score de urgencia (Nexo)

- **Deadline vencido: implementado** con la columna `Cotizaciones.deadline` que trae el script 0004 (PR #73). `deadline_vencidas` cuenta las cotizaciones sin resultado con `deadline` anterior al momento de la consulta. Igual que las ciudades, la consulta comprueba la columna con `COL_LENGTH`: sin el 0004 el campo es `null`.
- **Score de urgencia: no se implementa.** El backlog lo define como "dato que expone Nexo, no se recalcula en Cotifly", y el contrato de Nexo para consultarlo por solicitud no existe todavía. Cuando exista, se agrega como campo nuevo de `IndicadoresMetricas` (cambio compatible).

### 9. Borradores

`/web/metricas/actividad` y `/ajustes-margen` siguen funcionando igual y quedan `deprecated: true` en el contrato. Se retirarán cuando el frontend use los nuevos.

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
| Integrar los servicios conexos (ADR-015) en precio y costo del tablero | Duban, después de #42 |
| ~~Pasar la descarga de PDF ajenos y `LoginResponse.ubicacion` a `IClsRegionalUsuario`~~: resuelto en #81 con la columna refrescada en cada login (sección 6, "Convergencia"). "Mis cotizaciones" del gerente lee la columna | Duban (HU-004.5) |
| Aplicar el 0004 para que el mapa por ciudad y `deadline_vencidas` tengan datos | DBA (ADR-015) |
