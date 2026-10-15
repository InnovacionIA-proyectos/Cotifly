# 10. Operación

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

Runbook del backend .NET: cómo saber si está sano, dónde mirar cuando algo falla y qué falta para activar cada integración. El despliegue en Docker Swarm con Dokploy es HU-006; aquí se describe lo que el contenedor ofrece.

Contenido:

- [10.1 Salud y readiness](#101-salud-y-readiness)
- [10.2 Logs](#102-logs)
- [10.3 Métricas](#103-métricas)
- [10.4 Arranque fallido](#104-arranque-fallido)
- [10.5 Depurar sin shell](#105-depurar-sin-shell)
- [10.6 Riesgos y limitaciones conocidos](#106-riesgos-y-limitaciones-conocidos)
- [10.7 Decisiones pendientes](#107-decisiones-pendientes)
- [10.8 Activación por integración](#108-activación-por-integración)
- [10.9 Problemas frecuentes](#109-problemas-frecuentes)

## 10.1 Salud y readiness

| Sonda | Uso | Respuesta |
|---|---|---|
| `GET /health` | Vida. La consulta el `HEALTHCHECK` de la imagen (`dotnet /app/Cotifly.Api.dll --healthcheck`, cada 30 s, timeout 5 s, 3 reintentos, `start-period` 20 s) | `200 {"status":"ok","servicio":"cotifly-api","entorno":"..."}` |
| `GET /health/ready` | Disponibilidad. Para el balanceador (healthcheck de Traefik): saca la tarea del balanceo sin reiniciarla | `200 {"status":"ready"}`; `503 {"status":"unavailable","detalle":"..."}` sin cadena, base caída o `esquema incompleto: falta 0002` / `falta 0009` / `falta 0010` / `falta <Tabla.columna>` (HU-021: la sonda compara el modelo EF con la base, ADR-019) |

- `/health/ready` ejecuta los health checks con etiqueta `ready` (`clsBaseDatosHealthCheck`: conexión a `dbo_cotifly` y comparación de las tablas y columnas del modelo EF con la base, más el CHECK `ck_cotizaciones_estadoEnvio`, con 5 s de límite; nombra hasta 5 columnas faltantes). `detalle` nunca trae la cadena ni el servidor; la excepción queda en el log.
- Una base caída no debe reiniciar el contenedor: por eso el `HEALTHCHECK` usa `/health` y no `/health/ready`.
- A mano: `docker inspect --format '{{.State.Health.Status}}' <contenedor>` y `docker exec <contenedor> dotnet /app/Cotifly.Api.dll --healthcheck` (imprime `sonda /health: 200`).
- Cada sonda levanta un proceso `dotnet` aparte (~40–70 MB transitorios cada 30 s): contarlo en el límite de memoria.

## 10.2 Logs

- Consola estándar de ASP.NET Core (`docker logs`, o el visor de Dokploy). Hora en UTC: el contenedor no fija `TZ`; la fecha de negocio sale de `Zona:ZonaHoraria`.
- Niveles: `Information` por defecto (`appsettings.json`, con `Microsoft.AspNetCore` en `Warning`); `Development` sube a `Debug` e incluye las consultas de EF Core.
- Nunca se registran secretos: los errores de configuración nombran la clave y no el valor; `IHttpClientFactory` oculta las cabeceras; la API key del proveedor de IA no se escribe; el contenido de los mensajes del modelo solo sale en `Trace`.
- Líneas que conviene vigilar:

| Mensaje (resumen) | Nivel | Significado |
|---|---|---|
| `Hosting failed to start` con el nombre de una clave | Critical | Configuración inválida en el arranque (el contenedor termina con 1) |
| "SAP TM no disponible" con TrqType y organización | Warning | SAP TM caído o lento: la cotización siguió con la política de la línea |
| "SAP TM no configurado (SapTm:Modo vacío)" | Warning (al arrancar, Production) | Todos los márgenes salen de la política |
| "Búsqueda de clientes en Nexo no disponible ({Motivo})", "Catálogo de municipios de Nexo no disponible", "Catálogos de flete de Nexo no disponibles", "Cubicación en Nexo no disponible" | Warning | Nexo sin configurar, caído, lento, con HTTP no exitoso o JSON inválido, o el comercial sin `idUsuarioExterno`; el motivo va entre paréntesis ("tiempo agotado", "circuito abierto tras fallos repetidos", "error de red", "HTTP 500 (...)") y el detalle de la excepción en Debug |
| "Calcular cubicaje ({Contexto}): el cubicador respondió {Codigo}" | Warning | "Calcular cubicaje" (HU-023.2) falló contra el cubicador; el comercial recibe el rechazo del cubicador (400) o 503 `CUBICADOR_NO_DISPONIBLE`. El detalle técnico va en esta línea |
| "CUBICADOR_RESPALDO: el cubicador no pudo recomendar los vehículos de la ruta {RutaId} ({Motivo}: {Detalle}) ..." (evento 2101) | Warning | HU-021.5 (#133): el cubicador configurado falló y la ruta quedó con un camión sin vehículo (la Mesa de Costeo de Nexo lo resuelve). `Motivo`: `caida` (tiempo agotado, 5xx, red, llave rechazada: las rutas siguientes de la captura ya no lo llaman), `rechazo` (4xx, solo esa ruta), `respuesta_invalida` o `presupuesto` (`Cubicador__PresupuestoCapturaSegundos` agotado). También cuenta en `cotifly.cubicador.respaldos`. Con `Cubicador__RespaldoSiFalla=false` la captura responde 503 `CUBICADOR_NO_DISPONIBLE` (sin el detalle, que queda en este log). "El cubicador respondió 401/403" (Error): revisar `Cubicador__ApiKey` |
| "El cubicador respondió una recomendación inválida para {IdLineaSolicitud} ({Motivo}) ..." | Warning | HU-023.1 (#156): la respuesta no cumple el contrato (por ejemplo `ASIGNADO` con un vehículo sin `pesoTotalKg`; el motivo dice qué campo). La captura responde 503 `CUBICADOR_RESPUESTA_INVALIDA` y no guarda nada, aunque el respaldo esté encendido. Avisar al equipo del cubicador con el motivo |
| "El cubicador rechazó {Metodo} {Ruta} con {Codigo}" / "El cubicador no aceptó la ruta {RutaId} ({Codigo})" | Warning | HU-023.1: 400 del contrato (`MERCANCIAS_REQUERIDAS`, `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE`, `DIMENSIONES_INVALIDAS`); el comercial recibe el mismo código con el mensaje del cubicador y no se guarda nada |
| "Recomendación del cubicador para {IdLineaSolicitud}: {Resultado} ..." | Information | HU-023.1: resultado de cada llamada (resultado, motivo, condicionada, número de vehículos y de advertencias), sin datos del cliente |
| `/health/ready` responde "esquema incompleto: falta 02" | Error (readiness) | HU-023.3 (#158): la base no tiene las columnas del cubicador. Aplicar `db/scripts/02_alter_cubicador.sql` (idempotente) después del `0010` o del `01` |
| "MERCANCIA_SIN_CODIGO: {Mercancias} mercancía(s) sin codigo_mercancia y el cubicador no está configurado ..." | Warning | HU-023.3: sin cubicador las mercancías se guardan sin código y sus rutas van al respaldo. Con el cubicador configurado el código es obligatorio |
| "Camiones de la ruta {Ruta} de {Folio} recalculados con el cubicador ..." | Information | HU-023.3: `POST .../rutas/{rutaId}/recalcular-vehiculos` reemplazó los camiones de la ruta antes de enviarla a Nexo |
| "La sonda del cubicador falló ({Motivo})" | Warning | HU-023.1: con `Cubicador__RutaSalud`, el cubicador no respondió a la sonda. Con `Cubicador__RespaldoSiFalla=false` `/health/ready` responde 503 con "El cubicador no responde..." |
| "Ruta {Ruta} de la licitación {Folio} cubicada en segundo plano ..." / "Licitación {Folio} cubicada por completo ({Rutas} rutas): se envía a Nexo." | Information | HU-023.5 (#160): el procesador de la cubicación en segundo plano (`clsWorkerCubicacionLicitacion`) terminó una ruta / la licitación completa |
| "El cubicador no respondió para la ruta {Ruta} de la licitación {Folio} (intento {Intento}) ..." | Information | HU-023.5: caída del cubicador sin respaldo; la ruta se reintenta con espera creciente (`Cubicador__EsperaReintentoSegundos`, hasta `Cubicador__MaximoIntentos`) |
| "La ruta {Ruta} de la licitación {Folio} no se pudo cubicar ({Codigo}, intento {Intento}) ..." | Warning | HU-023.5: la ruta quedó `fallida` y la licitación no sale a Nexo hasta que el comercial la reintente (`POST /web/licitaciones/{folio}/cubicacion/reintentar`). Consulta: `SELECT c.folio, l.orden, l.errorCubicacion FROM dbo_cotifly.CotizacionLineasSolicitud l JOIN dbo_cotifly.CotizacionesSolicitud c ON c.id = l.cotizacionSolicitudId WHERE l.estadoCubicacion = 'fallida'` |
| "La licitación {Folio}: el candado del envío sigue tomado tras {Intentos} intentos ..." | Information | B2 (#174): la última ruta esperó unos 15 s el candado del envío a Nexo y lo dejó a quien lo tiene (está enviando). Si la licitación no sale, la recuperación del procesador la envía pasados 2 min |
| "La licitación {Folio}: no se pudo pedir el candado del envío (intento {Intento}); se reintenta." | Warning | Revisión de #174: abrir la conexión del candado falló (base cargada, `Connection Timeout Expired`); cuenta como candado no obtenido y se reintenta dentro del mismo tope |
| "La licitación {Folio} quedó cubicada pero no se pudo revisar su envío a Nexo; la recuperación la envía." | Error | Revisión de #174: falló algo después de guardar la ruta (recargar la licitación, Nexo). La ruta queda cubicada y contada; la recuperación envía la licitación pasados 2 min |
| "No se pudo cubicar la ruta {RutaId} ...; vuelve a la cola cuando venza su reclamo" / "Falló una vuelta de la cubicación de licitaciones" | Error | HU-023.5: excepción (base, red) en una ruta o una vuelta; la ruta se retoma sola cuando vence `Cubicador__ReclamoSegundos` |
| `/health/ready` responde "esquema incompleto: falta 04" | Error (readiness) | HU-023.5: falta `db/scripts/pendientes/04_cubicacion_segundo_plano.sql` (idempotente, con respaldo previo; pendiente de @scalderon-bs y del DBA). **No desplegar la #160 sin aplicar antes el 0010 y el 04** |
| `/health/ready` responde "esquema incompleto: falta 09" | Error (readiness) | HU-018.1 (#155): falta `db/scripts/09_score_prioridad.sql` (agrega la columna NULL `CotizacionesSolicitud.scorePrioridad`; idempotente, sin respaldo). Aplicarlo basta: la sonda se recupera sola en la siguiente consulta de Traefik, sin redesplegar ni reiniciar |
| "CUBICADOR_SIN_CONFIGURAR: Cubicador:UrlBase vacía ..." (evento 2100) | Warning (una vez por captura) | Comportamiento por defecto mientras el cubicador no tenga `POST /recomendaciones` (C1): cada ruta queda con un camión sin vehículo |
| "CAMPOS_RETIRADOS: {Endpoint} recibió {Campos}, que ya no están en el contrato ..." (evento 2110) | Warning | HU-021.11 (#138): un frontend anterior todavía manda `valor_estimado` o `referencia` (decisión 1 de HU-021). Se ignoran sin error; el log trae solo el nombre del campo, nunca el valor. Avisar al equipo del frontend |
| "Fallo en el barrido de sesiones inactivas" | Warning | Se reintenta en el siguiente ciclo |
| `OnAuthenticationFailed` con el tipo de rechazo | Information | Tokens vencidos, de otra clave o mal formados |
| "El agente de IA no está configurado" | Error | Falta `IA:Proveedor`, `IA:Modelo` o `IA:ApiKey` |
| Clave JWT o de firma de URL efímera | Warning | Fuera de Production sin clave: los tokens o enlaces dejan de valer al reiniciar |
| "PDF {Referencia} renderizado: espera de turno {EsperaMs} ms, render {RenderMs} ms" | Information | Un PDF por render. La espera es el tiempo en cola detrás de otros renders (el render es uno a la vez por proceso, #92). Es la señal de capacidad, ver [10.6](#106-riesgos-y-limitaciones-conocidos) |
| "PDF {Referencia} sin turno de render tras {EsperaMs} ms" | Warning | La cola de render no avanzó en `Pdf__TiempoEsperaRenderSegundos`: el comercial recibió 503 `PDF_RENDER_NO_DISPONIBLE` |

- `EventosHerramienta` es la auditoría de negocio (herramientas, logins, descargas de PDF ajenos, administración de líneas): consultarla en la base, no en los logs. Contiene datos personales (ver [10.6](#106-riesgos-y-limitaciones-conocidos)).
- HU-023 (#174, desviación E1): la cubicación en segundo plano deja un evento por ruta procesada (`cubicar_ruta_licitacion`: folio, ruta, intento, resultado `cubicada`/`reintento`/`fallida`, código del error y número de camiones), el reintento del comercial deja `reintentar_cubicacion_licitacion` y el recálculo de una ruta deja `recalcular_vehiculos_ruta` (también cuando se rechaza: 409, 428 no llega al servicio). Ninguno guarda montos, pesos ni mercancías.

## 10.3 Métricas

Sin exportador todavía (OpenTelemetry o Prometheus se evalúan en HU-006). Las métricas se leen con `dotnet-counters` desde un contenedor auxiliar ([10.5](#105-depurar-sin-shell)):

| Medidor | Instrumentos | Para qué |
|---|---|---|
| `Cotifly.Agente` | `cotifly.agente.respuesta_final.invalidas` (etiqueta `motivo`), `cotifly.agente.respuesta_final.reintentos`, `cotifly.agente.respuesta_final.respaldos`, `cotifly.agente.esquema_rechazado` | Calidad de la respuesta del modelo (#16): una tasa alta de respaldos o de esquema rechazado indica un modelo o proveedor inadecuado |
| `Cotifly.Cubicador` | `cotifly.cubicador.respaldos` (etiqueta `motivo`: `caida`, `rechazo`, `respuesta_invalida`, `presupuesto`) | Rutas que quedaron con un camión sin vehículo porque el cubicador falló (HU-021.5, #133). Con el cubicador en producción debería quedarse en 0 |
| `System.Runtime` | GC, memoria, hilos, excepciones | Salud del proceso |
| `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel` | Peticiones, duración, conexiones | Carga HTTP |
| `System.Net.Http` | Peticiones salientes | SAP TM, Nexo, proveedor de IA, TRM |

```bash
dotnet-counters monitor -p 1 --counters Cotifly.Agente,Cotifly.Cubicador,System.Runtime,Microsoft.AspNetCore.Hosting
```

### Latencia del asistente (#126)

Cada turno de `/web/chat` registra en `Information` una línea sin textos del comercial ni del modelo:

```
Turno del asistente de PCN-…: 4902 ms (preparación 12 ms; modelo con herramientas 2600 ms en 1 llamadas; herramientas 8 ms;
respuesta final 2280 ms en 1 llamadas; guardado 5 ms). Iteraciones 2, cierre llamada_final, reintentos del validador 0,
respaldo False, propuesta ajustar_margen, tokens 1830/95, modelos gemini/gemini-3.5-flash-lite, fallo .
```

Si `modelos` trae un modelo distinto de `IA__Modelo`, ese turno pasó a un respaldo de `IA__Fallbacks__*`. La espera del limitador sale aparte, en el aviso "La llamada al proveedor de IA esperó N ms por IA:Rpm=8". Con estas dos líneas se calcula el p50 y el p95 por turno y se ve qué fase pesa.

**Medición (2026-10-08).** El turno típico es "súbele el margen al 18 % porque el cliente aceptó", con datos ficticios.

- **Antes.** En el E2E del lote 2 con OmniRoute (`gemini/gemini-3.5-flash-lite`, `IA__Rpm=8`), los turnos tardaron de 17,0 a 24,2 s, con 3 llamadas al modelo por turno: herramienta, texto y llamada final.
- **Sin acceso a OmniRoute.** Cuando se hizo #126 no había acceso a la red de 10.10.40.17, así que la comparación se hizo con el runner, el validador y el limitador reales, y un proveedor simulado:
  - latencia de 1,5 a 3,5 s por llamada, el rango que se deduce del E2E;
  - 10 turnos por escenario, con 15 s entre turnos.

| Escenario | Llamadas por turno | Espera por `IA:Rpm` | p50 | p95 |
|---|---|---|---|---|
| Antes (intervalo fijo de 7,5 s y sin atajos) | 3 | 6,2 a 11,6 s | 17,3 s | 18,3 s |
| Después (ventana deslizante y propuesta directa) | 2 | 0 s | 4,9 s | 5,8 s |

El "antes" simulado reproduce los 17 s del E2E: dos de cada tres partes del turno eran espera del limitador. **Pendiente:** repetir la medición con OmniRoute desde la red interna y comparar el p95 con la meta de 8 s, usando la línea "Turno del asistente".

**Configuración del limitador.**

- **`IA__Rpm`.** Conviene fijarlo en cerca del 80 % del RPM del plan del proveedor y dividirlo entre las réplicas, porque cada réplica tiene su propio limitador.
- **Al llegar al tope.** Si el cupo tardaría más de `IA__EsperaMaximaRpmSegundos` (20 por defecto), el turno no espera: el comercial ve de inmediato "⏳ El modelo alcanzó su límite de peticiones por minuto…" (`clsTextosAgente.LimiteModelo`) y puede reenviar el mensaje. Antes de #126 esperaba hasta unos 60 s sin ningún mensaje.
- **Nivel de log en producción.** No se baja a `Trace`. Microsoft.Extensions.AI (`UseLogging`) escribe en `Trace` el contenido de las conversaciones, con datos del cliente. La línea por turno y la espera del limitador van en `Information` y no llevan textos.

**Límite conocido.** `IA__Rpm` es global al proceso. Con 8, más de unos 4 turnos por minuto en total (sumando todos los comerciales) vuelven a esperar al limitador. Si el p95 sube con varios usuarios, hay que subir `IA__Rpm` según el plan del proveedor.

## 10.4 Arranque fallido

- Cualquier excepción al construir o arrancar el host termina el contenedor con **código 1** y un solo registro del error, sin secretos (si falla antes de existir el logger, una línea `crit:` en stderr).
- En `Production` no arranca sin `ConnectionStrings:Cotifly`, `ConnectionStrings:Identidad`, `Jwt:SigningKey` o `Cors:OrigenesPermitidos`, con placeholders (`<secreto>`, `cambiar...`, `changeme`, `dev`), con autenticación integrada en una cadena. Las variables `GChat__*` y `Pdf__ClaveFirmaUrl` se retiraron en HU-013.9 (#105) y ya no se validan. Lista completa en [03-infraestructura](03-infraestructura.md#32-variables-de-entorno-y-configuración).
- En cualquier entorno no arranca con un `SapTm:Modo` desconocido, `export_json` sin ruta, `odata` sin URL, usuario o clave, un proveedor de IA desconocido o incompleto, `Pdf:StorageModo` distinto de `volumen`, o un `Correo:Modo` no implementado con el correo habilitado.

## 10.5 Depurar sin shell

**Depurar sin shell** (la imagen no tiene `sh`, `bash` ni `curl`; `docker exec ... sh` no funciona):

1. `docker debug <contenedor>` (Docker Desktop/Engine con licencia que lo incluya): shell temporal con herramientas, sin tocar la imagen.
2. Contenedor auxiliar que comparte PID y red con el de la API:
   ```powershell
   docker run --rm -it --cap-add=SYS_PTRACE --pid=container:<id> --network=container:<id> mcr.microsoft.com/dotnet/sdk:10.0.401-noble bash
   # dentro: dotnet tool install -g dotnet-counters; dotnet tool install -g dotnet-dump
   #         export PATH="$PATH:/root/.dotnet/tools"
   #         export TMPDIR=/proc/1/root/tmp   # el socket de diagnóstico vive en el /tmp del contenedor de la API
   #         dotnet-counters monitor -p 1   |   dotnet-dump collect -p 1   |   curl http://127.0.0.1:8080/health
   ```
   El PID 1 es el `dotnet` de la API. **`--cap-add=SYS_PTRACE` es obligatorio**: sin esa capacidad el contenedor auxiliar no puede leer `/proc/1/root/tmp` (el proceso de la API es del usuario `app`) y `dotnet-counters` falla con `UnauthorizedAccessException`. Ensayado el 2026-09-25 (#14) contra `cotifly-api:dev`: `dotnet-counters ps` lista el PID 1 y `dotnet-counters collect -p 1 --counters System.Runtime` guarda las métricas.
3. Build de emergencia con runtime Ubuntu completo (con shell), solo local y con sufijo `-debug`, **nunca** por el pipeline de producción:
   `docker build --build-arg ASPNET_VERSION=10.0.12-noble -t cotifly-api:<versión>-debug -f backend-dotnet/Dockerfile backend-dotnet`.

## 10.6 Riesgos y limitaciones conocidos

Riesgos registrados en #12 y #16:

| Tema | Estado |
|---|---|
| **Frontera de confianza del login (CRÍTICO)** | `/api/auth/login-clientepro` es anónimo y confía en `idUsuario`: quien alcance la API y conozca un `NOMBREUSUARIO` (o enumere IDs) obtiene un JWT. Mitigaciones y dueño en ADR-009 ("Riesgo abierto"): aserción firmada por el backend de ClientePro o validación de su token; mientras tanto, restringir la red al origen de ClientePro y considerar quitar el respaldo por `ID` numérico |
| Folio con iniciales (ADR-011) | Formato `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`; consecutivo por iniciales y día con el candado `cotifly:folio:DDMMAAAA:INICIALES`. Dos comerciales con las mismas iniciales comparten la secuencia del día (no chocan, pero el `NNN` no empieza en `001` para cada uno). Las iniciales salen de `DESCRIPCIONUSUARIO` (vía `UsuariosWeb.nombreCompleto`) con limitaciones conocidas (orden nombre y apellido, texto agregado, abreviaturas); **pendiente** revisar una muestra real antes de producción. Si el comercial cambia de nombre, solo cambian sus folios nuevos |
| Latencia del chat | Desde #126 (ADR-018 §19), un turno usa de 1 a 3 llamadas al modelo (una propuesta, 2). El limitador `IA:Rpm` es una ventana deslizante de 60 s que no espacia las llamadas de un mismo turno. `IA:Rpm` es un tope GLOBAL del proceso, compartido por todos los comerciales y el redactor: con 8 caben unos 4 turnos por minuto y, por encima, los turnos esperan. Se sube según el plan del proveedor. Cada turno deja el log "Turno del asistente de {folio}" con la duración por fase, y el limitador registra "esperó N ms por IA:Rpm" |
| Google Chat retirado (#105) | Ya no hay webhook (`POST /chat/events` → 404), cola ni worker. Las sesiones de Google Chat (`spaceId = spaces/...`) quedan en `Sesiones` y `EventosConversacion` como auditoría; el barrido de sesiones las archiva como a cualquier otra |
| Revocación de `jti` | Un logout tarda hasta 60 s en verse en las demás réplicas (foto de `TokensRevocados`) |
| Límites de peticiones | `LimiteAuth` y `LimiteChat` son por proceso: con N réplicas el límite efectivo es N veces mayor |
| `generar_pdf` multi-destino | Resuelto por el esquema de HU-021 (ADR-019): cada ruta guarda su precio (`precioFinalLinea`) y la cabecera la suma (`precioFinal`); el PDF de pliego imprime una sección por ruta. La herramienta `generar_pdf` del agente legado ya no existe y `clsGenerarPdfService` está apagado (`Pdf__PermitirCotizacionChat=false`, `COTIZACION_CHAT_RETIRADA`) |
| `trmAplicada` | 0 en COP; en USD, la TRM fijada en la cabecera (HU-012, ADR-014). El tablero de HU-010.1 convierte a COP las filas en USD con `precioFinal × trmAplicada` |
| Coincidencia exacta de clientes en Nexo | Nexo no la distingue: hoy es exacta solo con un único candidato (ADR-005, pendiente con el equipo de Nexo) |
| Catálogos de Nexo | `GET /web/catalogos-flete` consulta Nexo en cada petición. El de municipios se reutiliza `Nexo__MinutosCacheMunicipios` (60 por defecto, HU-005.13): con Nexo caído se sigue sirviendo la foto vigente, y al vencer responde `MUNICIPIOS_NO_DISPONIBLE` (el chat sigue con el texto libre). Un cambio del catálogo en Nexo tarda hasta ese tiempo en verse. Solo se guarda un catálogo exitoso y no vacío; un 200 que no es un arreglo cuenta como `MUNICIPIOS_NO_DISPONIBLE`. Sin foto vigente, las consultas que llegan mientras hay una en curso esperan esa misma y reciben su mismo resultado (éxito o fallo): con Nexo caído no se encolan una detrás de otra (cada una tardaría hasta 8 s). No hay caché negativa: un fallo no se guarda, porque Nexo da el mismo código cuando el comercial que consulta no tiene `idUsuarioExterno`, y eso no debe dejar sin catálogo a los demás |
| Identidad ante Nexo (HU-023.4) | Todas las llamadas llevan `X-Portal-clsUsuario-Id` (`clsIdentidadNexo`). Si Nexo responde 401 en todo, revisar primero que la versión desplegada de Cotifly sea la que manda ese nombre (antes iba `X-Portal-Usuario-Id`, que Nexo rechaza) |
| Cupo de Nexo (429, HU-023.4) | Nexo limita **por rol** `catalogos/*` y la bandeja de solicitudes (120/min; el cupo lo comparten todos los comerciales de Cotifly y los usuarios de Nexo con el mismo rol). Tras un `429 DEMASIADAS_SOLICITUDES`, solo `/api/catalogos/*` y la bandeja `GET /api/solicitudes` (el listado) se bloquean hasta el `Retry-After` (fecha pasada = 0 s; sin encabezado, 30 s; tope 5 min); el polling (`/api/solicitudes/{id}`), las cotizaciones, los conexos, los clientes y los municipios no se congelan, y un 429 de catálogos no aumenta el backoff del polling. Warning "Nexo respondió 429 a GET ..."; los catálogos sirven la copia de respaldo. Si se repite, pedir a Nexo más cupo para el rol |
| Datos del cubicador en Nexo (HU-023.4, Nexo #297; HU-023 #176) | Por defecto (`Nexo__EnviarCodigosCubicador=true`) las líneas de los camiones del cubicador llegan a Nexo con el vehículo y la carrocería del cubicador; un código que Nexo no conoce nace en cuarentena (18.4). Con la bandera en `false` (emergencia) llegan **sin vehículo** y quedan para la Mesa. Una ruta de un camión va con `grupoRutaId = null`. El código de mercancía no viaja. Cambiar la bandera no afecta las solicitudes ya enviadas: el polling usa lo que viajó (evento `envio_nexo_registrado` en `EventosHerramienta`). Con un solo camión la línea lleva el peso declarado de la ruta (el del cubicador se ve en Cotifly). Las líneas sin vehículo siguen igual hasta la 19.2 de Nexo (`PorCostear`) |
| URL base de Nexo | `Nexo__UrlBase` es la raíz del servicio sin `/api` (el código agrega `/api/...`; `.env.example` corregido, ADR-015 §8); confirmar la URL real de cada ambiente con el equipo de Nexo antes de activar |
| E2E con el Angular | Pendiente (HU-004.8): el contrato está cubierto por pruebas contra `openapi.yaml`, no contra el frontend real |
| #45 (tablero de métricas) | `/web/metricas/comercial` y `/regional` listos (ADR-016). Desde HU-021 el mapa por ciudad usa `origenNombre`/`destinoNombre` de la ruta 1 (0010) y `deadline` de la cabecera: si esas columnas no existen, el mapa responde `disponible: false` y `deadline_vencidas` es `null`. Los conteos son por cotización y los valores y la línea de negocio, por ruta (decisión 8). Pendiente: definiciones por validar con Gerencia Comercial, score de urgencia de Nexo, servicios conexos (después de #42) y la regional (fila "Regional del comercial"). Desde #81 la descarga de PDF ajenos y `LoginResponse.ubicacion` leen `UsuariosWeb.regional`, que el login refresca desde `DimEjecutivo` |
| Validador de la respuesta final (#16) | La regla de montos es práctica (ADR-003). Batería de 52 textos del validador: falsos positivos 3 → 0 (NIT, teléfono, NIT con puntos) y falsos negativos 11 de 24 → 1 (el folio base de una versión, aceptado a propósito). Desde #126 también detecta montos en letras, miles separados con espacio, dígitos Unicode, sufijos mil/k y EUR (ADR-018 §19). Sigue rechazando aproximaciones honestas ("3,29 millones", centavos que no existen). Cada reintento cuesta una llamada más; con una propuesta ya armada no se reintenta (#126). Si el proveedor rechaza el JSON Schema (400/422), el proceso sigue sin formato `IA:MinutosSinJsonSchema` (30) y luego re-sonda, también para los proveedores de `IA:Fallbacks`. El respaldo no alerta al space de soporte. Falta medir con el proveedor real la tasa de reintentos, respaldos y rechazos (`cotifly.agente.*`) |

Otros puntos de operación (de #11 y #12):

| Tema | Estado |
|---|---|
| Réplicas | `replicas: 1` mientras la deduplicación del correo (`clsCandadoEnvios`) sea por proceso y los PDF vivan en un volumen local; con varias réplicas hacen falta `sp_getapplock` para el correo y un almacén compartido (ADR-012) |
| Regional del comercial | Tabla verificada en #81 (`DWCubosEBSSAP.dbo.DimEjecutivo`, cruce por `USUARIO` sin el dominio `boterosoto\`) y login conectado (ADR-016 §6). **Para activarla en un ambiente:** cargar `ConnectionStrings__Regional` en los secretos de Dokploy (usuario SQL de solo lectura) y abrir la salida de red del clúster al servidor del cubo (puerto 1433); sin eso, `ConnectionStrings__Regional` queda vacía y no se consulta nada. Con la cadena, el primer login llena `UsuariosWeb.regional` y cada login siguiente la refresca; quien no está en `DimEjecutivo` conserva la que tenga. Si `DimEjecutivo` falla o tarda más de `Regional__TiempoEsperaLoginSegundos` (3 s), el login entra igual y queda un warning `No se pudo consultar la regional de '<usuario>' en DimEjecutivo` en el log: revisar red y credencial si se repite. Para quien no está en `DimEjecutivo`, el DBA llena `UsuariosWeb.regional` **después del primer login** de cada persona (la fila se crea en el alta JIT): `UPDATE dbo_cotifly.UsuariosWeb SET regional = N'<REGIONAL>' WHERE nombreUsuario IN (N'<USUARIO1>', N'<USUARIO2>');`. Usar exactamente el mismo texto de regional para el gerente y sus comerciales, y el mismo que tenga `DimEjecutivo` cuando se conecte (se compara sin mayúsculas ni espacios sobrantes: "Antioquia" y "Regional Antioquia" son regionales distintas). La regional resuelta se reutiliza 60 s: un cambio en `DimEjecutivo` tarda hasta un minuto en verse. Sin regional, el tablero del gerente responde 409 `REGIONAL_NO_ASIGNADA`, el borrador de métricas le muestra solo su fila y no descarga PDF ajenos (fail-closed). Con `ConnectionStrings__Regional` configurada y la base caída, el tablero responde 503 `REGIONAL_NO_DISPONIBLE` |
| Regional conservada al salir de `DimEjecutivo` | Riesgo operativo (#81): quien deja de aparecer en `DimEjecutivo` conserva la última regional guardada y, si es gerente, su alcance (tablero de la regional y PDF de sus comerciales) hasta que el DBA cambie `UsuariosWeb.regional`. La regional es una fuente de autorización de alcance (ADR-016 §6). Seguimiento: guardar el origen de la regional para limpiarla solo cuando vino de `DimEjecutivo` (columna nueva, script 000N) |
| Latencia del login con `DimEjecutivo` caída | Los fallos no se guardan en caché (#81): mientras `DimEjecutivo` esté caída o lenta, cada login espera hasta `Regional__TiempoEsperaLoginSegundos` (3 s) antes de seguir sin refrescar la regional. Mejora futura: un circuit breaker que deje de consultar un rato tras varios fallos seguidos |
| Cambio de correo en el login | Desde #81 el login refresca `email` desde el almacén y deja un warning `El correo de '<usuario>' cambió en el almacén corporativo`. Desde #105 el correo ya no es una identidad (era la de Google Chat, ADR-011): solo es el remitente y el contacto del comercial |
| Datos personales en `EventosHerramienta` | Entradas con correos de clientes, nombres y NIT: definir retención y purga con datos y seguridad |
| SPF/DMARC del correo | El `From` es el correo del comercial aunque sale por el relay: TI debe autorizar el relay en SPF/DMARC para no caer en spam |
| Render de PDF serializado (#92, #95, ADR-006) | PDFsharp-MigraDoc no es thread-safe, así que cada proceso renderiza un PDF a la vez y los demás esperan turno hasta `Pdf__TiempoEsperaRenderSegundos` (30 s). Si se agota, responde 503 `PDF_RENDER_NO_DISPONIBLE` sin guardar nada. **Capacidad medida** (contenedor `sdk:10.0` en la estación de desarrollo, 2026-10-07): la tarifa única tarda unos 67 ms sola, unos 15 PDF/s por réplica, y con 10 a la vez el último termina en unos 0,5 s (el auditor midió 0,87 s). El pliego de 300 rutas tarda unos 230 ms solo y, con 10 a la vez, el último termina en 1,8 s (el auditor midió 1,9 s). **Señal para escalar réplicas:** el p95 de `espera de turno` en el log. Si pasa de 2 s de forma sostenida en horas pico, o aparece el warning "sin turno de render", se escala o se mueve el render a un worker. Con más réplicas aplica lo de la fila "Réplicas" (almacén compartido) |
| Reloj de los nodos y candado del folio (#125) | El folio se serializa con `sp_getapplock` (`CandadoSql`). Si el candado no se obtiene tras los reintentos, la API registra el warning `Candado <recurso> no disponible tras N intentos (último código …): se responde SERVICIO_OCUPADO` y responde **503 `SERVICIO_OCUPADO` con `Retry-After: 1`** sin guardar nada (el frontend reintenta). La sección crítica dura milisegundos: un warning sostenido indica una base degradada o bloqueos, no carga normal. **Reloj:** SqlClient mide `CommandTimeout` con el reloj de pared, así que un salto del reloj del nodo puede vencer comandos al instante (los candados ya no dependen de eso, pero el resto de las consultas sí, con el `CommandTimeout` de la cadena, 30 s por defecto). En los nodos `tbsapoloqas` (10.10.40.17) y `TBSNODO1-DKP` (10.10.40.31), verificar con `timedatectl` (`System clock synchronized: yes`) y `chronyc tracking` (`System time` y `Last offset` por debajo de unos ms). chrony debe corregir en modo *slew* (gradual), con `makestep` solo al arrancar (p. ej. `makestep 1.0 3`), nunca pasos en caliente; alertar si el desfase supera 1 s. **Causa en desarrollo (2026-10-08):** el host Windows tenía W32Time sin sincronizar (63 s de atraso) y la VM de WSL/Docker Desktop se resincroniza con él cada ~30 s: el reloj de los contenedores saltaba unos 63–65 s hacia atrás y hacia adelante (medido en los TRX de las suites). Corregir con `w32tm /resync` (o habilitar la sincronización del host); no afecta a los nodos Linux del clúster |
| `/data/pdfs` | Debe ser escribible por el UID 1654 (un volumen con nombre copia el dueño de la imagen; con bind mount, `chown 1654:1654`) |
| Sin `.pdb` en la imagen | Las trazas no traen número de línea; HU-006.4 (#28) mantuvo `DebugType=none` (alternativa: `DebugType=embedded` + `PathMap`, ver 03-infraestructura §3.1) |

Mecanismo de excepciones temporales del escaneo de vulnerabilidades de la imagen (`backend-dotnet/.trivyignore.yaml`, vacío hoy — `vulnerabilities: []`; procedimiento en [03-infraestructura §3.1](03-infraestructura.md#31-contenedor-e-imagen)). Cada una vencería sola: desde `expired_at` (00:00 UTC, 19:00 del día anterior en Colombia) Trivy vuelve a reportar el CVE y el job `imagen-docker` falla hasta que se resuelva o se prorrogue. Agregar o prorrogar una excepción exige la aprobación explícita en el PR de un revisor humano distinto del autor (líder técnico o seguridad); hoy no hay `CODEOWNERS` que lo obligue (se sugiere agregar `backend-dotnet/.trivyignore.yaml` a uno). Mientras está vigente, una excepción aplica en todas las ramas, también en `main` y en los tags de release. El workflow semanal `vigilancia-imagen-base.yml` (lunes 12:00 UTC) avisa cuándo se puede quitar y falla si vence en 7 días o menos (ver 03-infraestructura §3.1).

**CVE-2026-84782 (HIGH), resuelto sin excepción (#86, cerrada el 2026-10-05):** `libssl3t64`/`openssl` `3.0.13-0ubuntu3.15` de `aspnet:10.0.12-noble-chiseled-extra` (digest `sha256:6385dc0e…`, construida el 2026-09-04). Antes de mergear la excepción propuesta, QA verificó que Microsoft ya había republicado el mismo tag (digest `sha256:00e0ad6a…`, construida el 2026-10-02) con el parche de Ubuntu (`3.0.13-0ubuntu3.16`) ya aplicado: `docker pull` + Trivy sin `--ignorefile` dan 0 hallazgos contra la imagen real de Cotifly. No hizo falta aceptar el riesgo ni tocar `SDK_VERSION`/`ASPNET_VERSION` (mismo tag, digest nuevo); bastó con reconstruir sin caché de la imagen base.

## 10.7 Decisiones pendientes

Notas abiertas desde el PR de #10 (Precio, Margen y Tarifas), con su estado al cierre de #14:
- ~~`specs/openapi.yaml` (contrato de #53) cambia: se agrega `AJUSTE_MARGEN_INVALIDO` al enum de `ErrorNegocio`.~~ Hecho: el código está en el enum del contrato.
- HU-004.4 no tiene canal estructurado para el margen manual. Propuesta para #12: campo `margen_manual` + `motivo` en `MensajeWeb`, o `PUT /web/chat/{sesion_id}/margen`. Estado: desde #12 parte C el margen negociado viaja por la conversación (`registrar_ajuste_margen`, botón "Ajustar margen" de la previsualización); un canal estructurado propio sigue sin decidir.
- La diferencia del ajuste se mide contra el margen sugerido; si el sugerido viene de SAP TM, ¿se compara contra la política de la línea o contra SAP? Resuelto en #12 parte C: la referencia es el margen que propuso `obtener_margen` (`MargenSugerido` del estado).
- Un margen SAP negativo (anómalo) como referencia de una negociación hoy se rechaza por método: decidir.
- ~~Líneas inactivas en `MargenesPolitica`: hoy se siguen usando~~ Resuelto en #12 (auditoría F4, #41): el comercial no cotiza con una línea inactiva (`obtener_margen` y `generar_pdf` responden `MARGEN_LINEA_DESCONOCIDA` con el detalle `línea inactiva`); la administración sí la lista y la reactiva.
- Anomalía con `markup`: un margen > 0.6 se marca anómalo aunque con markup sea normal; decidir el umbral por método.
- Redondeo half-to-even sobre el valor exacto (diferencia máxima 1 peso con el Python): confirmar con negocio.
- ~~HU-012: convertir a USD después de calcular el precio en COP.~~ Decidido en ADR-014 (#50 a #52): el precio se calcula en COP y se convierte con la TRM oficial del día.
- Riesgo de secuenciación HU-004 vs HU-005: sin Nexo no hay fuente de costeo (`TARIFA_FUENTE_NO_DISPONIBLE`, el comercial ingresa el costo).

Otras decisiones abiertas:

| Decisión | Dueño | Referencia |
|---|---|---|
| IDROLES reales de Comercial y Gerente Comercial en ClientePro | Duban con el DBA | ADR-009 |
| Muestra real de `tblSAPUsuariosApp.DESCRIPCIONUSUARIO` para confirmar la regla de iniciales del folio | Duban con el DBA | ADR-011 |
| Login de servicio `cotifly_app`, permisos por esquema en `dbo_cotifly` y por columna en la base de identidad (`dbSolucionesTI_Prueba` en QA, #172) | DBA | [03-infraestructura](03-infraestructura.md#33-base-de-datos) |
| Base de identidad de **producción**: la misma que use Nexo en producción (por confirmar con el equipo de Nexo, #172) | Duban con el equipo de Nexo | [Identidad compartida con Nexo](#identidad-compartida-con-nexo-172) |
| Precondición de despliegue (`DEPLOY.md`): `0001` a `0010` aplicados, o la instalación desde cero (`db/scripts/01_crear_esquema.sql` + `db/seeder/`); cuando entre HU-023, también `db/scripts/02_alter_cubicador.sql`. Staging con `CosteoNexo__Habilitado=false` mientras comparta base con producción de Nexo | DBA | ADR-015, ADR-019 |
| Aprobación de los scripts 0002 a 0009 (sus cabeceras dicen "pendiente de aprobación") y revisión del borrador 0010 | Sara, scalderon-bs y DBA | ADR-008, ADR-015, ADR-019 |
| Diagnóstico (solo lectura) de licitaciones guardadas antes de #108 que ya no se pueden reenviar (`LICITACION_REQUIERE_NUEVA_CARGA`): consultas en `docs/docs_front/GUIA_FRONTEND_COTIFLY.md` §5.24. No se corrige nada en la base: el comercial las carga de nuevo | DBA | HU-013.11 (#108) |
| **Orden de los scripts desde HU-021 (ADR-019).** El `0009` (reestructuración solicitud → rutas → camiones) **ya está aplicado en la base compartida de QA**, que se instaló desde cero con `db/scripts/01_crear_esquema.sql` el 2026-10-08. El `0010` (`0010_nombres_municipio.sql`: nombres de municipio de la ruta, `solicitudNexoId` y nombre del conexo) está **pendiente**: borrador idempotente y no destructivo que revisa scalderon-bs; se aplica **después del 0009 y antes de desplegar** la versión de #130/#131 (sin él, `/health/ready` responde `503 falta 0010`). Orden en una base existente: `0001` → … → `0009` → `0010`; en una base vacía, `01_crear_esquema.sql` (ya incluye el 0010) más los seeders con `db/ejecutar-bd`. **Pendiente HU-023:** `db/scripts/02_alter_cubicador.sql` (columnas del resultado de la cubicación, `codigoMercancia` en lugar de `idTipoMercancia`), todavía no en esta rama | scalderon-bs y Duban | ADR-019 |
| Aprobación del 0005 al 0008 (licitaciones y Excel original, concurrencia y textos del PDF, campos de captura, horas por línea). En una base existente se aplican en orden, una vez, antes del 0009; una base instalada desde cero no los corre. Notas que siguen valiendo: el 0005 guarda el Excel de cada licitación en `VARBINARY(MAX)` (hasta 10 MB; el DBA debe contemplar el crecimiento); las columnas `NOT NULL` con `DEFAULT` del 0006 pueden reescribir la tabla en Standard (ventana de bajo uso); la reversa del 0006 (`rollback/0006_textos_y_concurrencia_reversa.sql`) se escribió para `Cotizaciones`, o sea, para una base anterior al 0009 | scalderon-bs y DBA | ADR-017, ADR-018 §8, §11 y §12, HU-013.10 (#106), HU-013.11 (#108) |
| **Retención de `CotizacionPdfsHistorial` y de los PDF archivados (propuesta):** las filas son pequeñas (referencia, motivo, usuario y fecha); conservarlas mientras se conserve la cotización. Los archivos `{folio}-hN.pdf` del almacén siguen la retención de los PDF (hoy sin purga). Un purgado futuro debe borrar juntos la fila y su archivo | Gerencia Comercial y DBA | ADR-018 §12 |
| **Justificación del ajuste de margen (PII posible):** `MargenAjustes.motivo` y la entrada de `EventosHerramienta` (`ajustar_margen_costeo`) guardan el texto libre del comercial (sin caracteres de control). Puede traer nombres o datos del cliente (Ley 1581 de 2012): mismo tratamiento que la auditoría (acceso solo de soporte y DBA, sin copiarlo a ambientes de prueba sin anonimizar). Retención propuesta: la de la cotización | Gerencia Comercial y DBA | ADR-018 §12 |
| **Retención del Excel original (propuesta):** los pliegos son documentos confidenciales del cliente y pueden traer datos personales (Ley 1581 de 2012). Purgar `CotizacionArchivosLicitacion.contenido` N meses después del cierre de la licitación (propuesta: 12; conservar nombre, hoja, tamaño y SHA-256, que es solo trazabilidad) con un trabajo programado o un script del DBA; tener en cuenta que los respaldos conservan el contenido hasta su propia caducidad, y vaciar o anonimizar la tabla en cualquier copia de producción a pruebas. Una licitación purgada se exporta sin el original (hoja con los campos mapeados) | Gerencia Comercial y DBA | ADR-017 §2 |
| **`READ_COMMITTED_SNAPSHOT` en la base de Cotifly (recomendado, I1 de #174).** Con READ COMMITTED por bloqueos, la lectura del tablero de métricas (`clsMetricasRepository`) retiene la fila de la cabecera mientras lee sus rutas y el polling, que escribe la ruta y después la cabecera, la cruza: SQL Server elige la lectura como víctima (1205). Hoy se mitiga con 6 reintentos con espera exponencial. Con `ALTER DATABASE … SET READ_COMMITTED_SNAPSHOT ON` las lecturas no toman candados S y el choque desaparece, también entre consultas de un mismo comercial. Es un cambio de la base (versiones de filas en `tempdb`, se aplica con la base sin conexiones activas): lo decide y lo ejecuta el DBA | DBA | [MATRIZ_PRUEBAS_HU023 §7.2](qa/MATRIZ_PRUEBAS_HU023.md) |
| Mitigación de la frontera de confianza del login (aserción firmada de ClientePro) | Duban con ClientePro | ADR-009, "Riesgo abierto" |
| Transporte real del correo (`smtp_relay`, `wscorreos` o `smtp`) | Equipo (D3) | [03-infraestructura](03-infraestructura.md#pdf-y-correo) |
| Proveedor y modelo de IA de producción, custodia de la clave | Negocio | ADR-003 |
| Criterio de coincidencia exacta de la búsqueda de clientes y URL base de Nexo por ambiente | Duban con el equipo de Nexo | ADR-005, [03-infraestructura](03-infraestructura.md#nexo) |
| Destinatarios previos: último envío o unión de todos | Negocio | [02-arquitectura](02-arquitectura.md#28-pdf-correo-y-auditoría) |
| Fuente `Stilu` del PDF (sin licencia disponible) | Negocio | ADR-006 |
| Sincronización a Google Sheets y S3/MinIO | Duban con Gerencia Comercial e infraestructura | ADR-012 |
| Salida HTTPS a `co.dolarapi.com` (fuente principal de la TRM, agregador no oficial) y a `www.datos.gov.co` (respaldo oficial) | Infraestructura | ADR-014 |
| Nueva versión (v2) de una cotización en USD: hoy se siembra con la TRM de la original (`trmAplicada` copiada) y, al recalcular, toma la TRM del día. ¿La v2 debe conservar la TRM original o usar la del día? | Gerencia Comercial | ADR-014 |
| OmniRoute (#84): qué credencial tiene cargada para cada proveedor (una clave gratuita de AI Studio permite a Google usar los datos), residencia de los datos y si OmniRoute registra los prompts. Hasta confirmarlo, solo datos ficticios en pruebas | Negocio / jurídica / dueño de OmniRoute | ADR-003 |
| OmniRoute por HTTPS (Traefik con TLS, #25/#26) o aislamiento worker → manager: hoy los datos del cliente y la clave viajan en claro | Infraestructura | DEPLOY.md §2.2 |
| Timeout de `POST /web/chat` en el frontend de ClientePro ≥ 120 s (un turno encadena hasta 12 llamadas al modelo) | Frontend (Sara) | 03-infraestructura "OmniRoute" |

## 10.8 Activación por integración

| Integración | Pasos | Variables |
|---|---|---|
| Login desde ClientePro | 1. Login `cotifly_app` y permisos (DBA): `GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::dbo_cotifly TO cotifly_app` (por esquema, sin `db_datareader`/`db_datawriter`) y `SELECT` por columna sin `CLAVE` en la base de identidad, la misma de Nexo (`dbSolucionesTI_Prueba` en QA, ver [Identidad compartida con Nexo](#identidad-compartida-con-nexo-172)); comprobarlo con `scripts/sql/dev-04-verificar-permisos.sql` adaptando el nombre de la base y la tabla de otro esquema. 2. Esquema `0001` a `0010` con `sqlcmd -I -b`, o la instalación desde cero (`db/ejecutar-bd.ps1` o `.sh`). 3. `UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES> WHERE codigo = 'comercial'` (ídem `gerente_comercial`), con `sqlcmd -I`. 4. Clave JWT compartida por todas las réplicas. 5. Origen CORS del frontend | `ConnectionStrings__Cotifly`, `ConnectionStrings__Identidad`, `Jwt__SigningKey`, `Cors__OrigenesPermitidos__0` |
| Detrás de Traefik | Encabezados reenviados con la red overlay; readiness de Traefik en `/health/ready`; puerto 8080 | `ForwardedHeaders__Habilitado=true`, `ForwardedHeaders__RedesConocidas__0` |
| Correo | Confirmar el transporte (D3), autorizar la IP del nodo en el relay, revisar SPF/DMARC | `Correo__Habilitado=true`, `Correo__SmtpRelay__Host` |
| SAP TM export | Montar el export de solo lectura y reemplazarlo copiando con otro nombre y renombrando | `SapTm__Modo=export_json`, `SapTm__ExportPath` |
| SAP TM OData (HU-009) | Ruta de red y usuario de servicio (HU-009.1); confirmar el formato del BP (ceros a la izquierda); validar con al menos 3 casos reales (`Categoria=sap`) | `SapTm__Modo=odata`, `SapTm__Url`, `SapTm__User`, `SapTm__Pass`, `SapTm__SapClient` |
| Nexo (clientes, municipios y catálogos de flete; la cubicación de Nexo se retiró en HU-023.2) | Confirmar la URL base con el equipo de Nexo y la ruta de red desde el contenedor; los comerciales necesitan `idUsuarioExterno` (lo llena su login desde ClientePro); probar `GET /web/clientes?q=`, `GET /web/municipios`, `GET /web/catalogos-flete` con un comercial real (`POST /web/cubicacion` depende del cubicador, `Cubicador__UrlBase`) | `Nexo__UrlBase` |
| Cubicador (HU-021.5, #133; contrato de HU-023.1, #156, ADR-020) | Cuando el cubicador publique `POST /recomendaciones`, `GET /tipos-mercancia`, `GET /tipos-carroceria`, `GET /vehiculos-catalogo` (HU-023 #176; antes `/tipos-vehiculo`), `GET /recomendaciones?idLineaSolicitud=&origenSistema=COTIFLY` (reconsulta tras un POST sin respuesta) y su salud (P7): cargar sus ejemplos (P2) en `clsEjemplosContratoCubicador` y correr las pruebas del adaptador; confirmar la forma de `advertencias`, `mercanciasCondicionadas` y `extradimension`; configurar `Cubicador__RutaSalud` si publica la salud; guardar la llave como secreto; probar `GET /web/tipos-mercancia` y una captura con varias mercancías; revisar que no aparezca el warning de respaldo. **Recomendación del auditor:** `Cubicador__RespaldoSiFalla=false` en QA y producción en cuanto haya URL (la captura falla con 503 si el cubicador no responde, como pide la issue) y `true` solo durante la ventana de estabilización; vigilar el warning `CUBICADOR_RESPALDO` y el contador `cotifly.cubicador.respaldos` | `Cubicador__UrlBase`, `Cubicador__ApiKey`, `Cubicador__TiempoEsperaSegundos` (30), `Cubicador__PresupuestoCapturaSegundos`, `Cubicador__TiempoMaximoCatalogoSegundos`, `Cubicador__RespaldoSiFalla`, `Cubicador__RutaSalud` HU-023.5 (#160), cubicación de la licitación en segundo plano: `Cubicador__LicitacionEnSegundoPlano` (true), `Cubicador__ProcesadorHabilitado` (true; apagarlo en las réplicas que no deban procesar), `Cubicador__MaximoParalelo` (4, **entre todas las réplicas**: confirmar con el cubicador, petición P6), `Cubicador__IntervaloTickSegundos` (5), `Cubicador__LoteRutas` (50), `Cubicador__ReclamoSegundos` (120, mayor que el tiempo de espera), `Cubicador__MaximoIntentos` (3), `Cubicador__EsperaReintentoSegundos` (30). Aplicar antes `db/scripts/pendientes/04` y `db/scripts/07_motivo_busqueda_agotada.sql` (sin el 07, `BUSQUEDA_AGOTADA` se guarda en NULL con el aviso `MOTIVO_SIN_SELECCION` y `/health/ready` lo informa en la descripción de `base-datos-cotifly`, sin dejar de estar listo). Las licitaciones se atienden en orden de llegada: una grande ocupa todos los cupos |
| Vehículo del cliente como respaldo (HU-021.11, #138; HU-021-FE.2) | Solo si negocio pide que, mientras no haya cubicador, el vehículo y la carrocería que manda el frontend vayan en el camión del respaldo (decisión 7: por defecto los define la Mesa de Costeo de Nexo). Se validan contra el catálogo de Nexo. Si el cubicador recomienda, manda el cubicador. Apagar y retirar cuando el cubicador exista (C1) | `Captura__AceptarVehiculoCliente` (`false` por defecto) |
| TRM para cotizar en USD (HU-012) | Abrir la salida HTTPS a `co.dolarapi.com` y a `www.datos.gov.co` desde el contenedor; cotizar una prueba en USD y comparar la TRM con la publicada por la Superintendencia Financiera; revisar que no aparezca el warning "tomada de la fuente de respaldo" (si aparece, DolarApi falla) | `Trm__Proveedor` (por defecto `dolarapi_con_respaldo`), `Trm__UrlDolarApi`, `Trm__Url` y `Trm__TiempoEsperaSegundos` |
| Agente de IA | Elegir proveedor y modelo; guardar la clave como secreto; ajustar `IA:Rpm` al plan del proveedor; medir respaldos y reintentos. Con OmniRoute (#84): `openai_compatible` con el endpoint interno por HTTP (riesgo: datos y clave en claro, DEPLOY.md §2.2), principal `gemini/gemini-3.5-flash-lite` (el único que completó la cotización con PDF y respeta `json_schema`), `IA__SoportaJsonSchema=false`, `IA__MaxTokens=1500` y respaldos `gemini/gemini-3.5-flash` y `gemini/gemini-3-flash-preview` en `IA__Fallbacks__0__*`/`__1__*` (ya están en los manifiestos), porque cada modelo entra en enfriamiento de ~9 h tras pocas llamadas. Rotar la clave antes de staging y luego cada 6 meses o ante cualquier exposición (procedimiento en DEPLOY.md §2.2). Matriz de modelos y estado del 2026-10-01 en [03-infraestructura](03-infraestructura.md#omniroute) | `IA__Proveedor`, `IA__Modelo`, `IA__ApiKey`, `IA__Endpoint`, `IA__Rpm`, `IA__MaxTokens`, `IA__SoportaJsonSchema`, `IA__Fallbacks__*` |
| Respaldo de `dbo_cotifly` | Login de solo lectura `cotifly_respaldo` (`GRANT SELECT ON SCHEMA::dbo_cotifly`, DBA); cron diario en el worker con `scripts/respaldo/exportar-dbo-cotifly.sh`; retención 7 diarios + 4 semanales; prueba de restore con `restaurar-dbo-cotifly.sh --destino-pruebas`. Complementa los backups nativos del DBA. Ver [DEPLOY.md](../../DEPLOY.md) §6 | `COTIFLY_RESPALDO_*` (`scripts/respaldo/respaldo.env.example`) |

### Configuración nueva de HU-021

Ninguna de estas variables está todavía en los manifiestos de `infra/`. Con su valor por defecto el backend funciona sin
cubicador. Valores por ambiente y cuándo cargarlas: [`DEPLOY.md`](../../DEPLOY.md) §2.2 ("Variables a agregar en Dokploy por
ambiente").

| Variable | Por defecto | Para qué |
|---|---|---|
| `Cubicador__UrlBase` / `Cubicador__ApiKey` | vacía / vacía (secreto, ≥ 16 en Production) | Sin URL, cada ruta queda con un camión sin vehículo (warning `CUBICADOR_SIN_CONFIGURAR`, evento 2100) |
| `Cubicador__RespaldoSiFalla` | `true` | Cubicador caído: `true` usa el camión de respaldo (`CUBICADOR_RESPALDO`, 2101, contador `cotifly.cubicador.respaldos`); `false` responde 503 `CUBICADOR_NO_DISPONIBLE` |
| `Cubicador__TiempoEsperaSegundos`, `__MinutosCacheCatalogos`, `__PresupuestoCapturaSegundos`, `__TiempoMaximoCatalogoSegundos` | `15`, `30`, `60`, `10` | Tiempos y caché del cubicador |
| `CosteoNexo__MaximoReintentosConexos` | `5` | Intentos fallidos del envío diferido de un conexo antes de `fallido_definitivo` |
| `CosteoNexo__IntervaloReintentoConexosMinutos` | `5` | Espera entre reintentos de conexos de una cotización ya entregada |
| `CosteoNexo__EsperaMaximaCatalogoConexosHoras` | `24` | Horas máximas en `en_espera` si el catálogo de conexos o la cotización de Nexo no responden |
| `Nexo__EnviarGrupoRutaId` (antes `Nexo__EnviarReferenciaRuta`) | `true` | `grupoRutaId` (id de la ruta) en las líneas de una ruta con varios camiones; `null` en una ruta de un camión (HU-023.4, #176). Interruptor de emergencia |
| `Nexo__EnviarDatosCubicador` | `true` | `idRecomendacionVehiculo` por línea (HU-023.4, #176). Interruptor de emergencia |
| `Nexo__EnviarCodigosCubicador` | `true` | Códigos de vehículo y carrocería del cubicador en la línea (#297, #176). En `false`: la línea del camión del cubicador va sin vehículo (Mesa) |
| `Captura__AceptarVehiculoCliente` | `false` | Bandera de respaldo HU-021-FE.2 (vehículo del frontend solo en el camión del respaldo) |
| `Pdf__PermitirCotizacionChat` | `false` | Cotización legada del chat: apagada responde `COTIZACION_CHAT_RETIRADA` |

### Identidad compartida con Nexo (#172)

**Regla:** la base de identidad de Cotifly (`ConnectionStrings__Identidad`) es **la misma que la de Nexo** (`ConnectionStrings:IdentidadPortal`) en
cada ambiente. Cotifly guarda el `ID` de `tblSAPUsuariosApp` en `UsuariosWeb.idUsuarioExterno` y lo manda a Nexo en
`X-Portal-clsUsuario-Id`; Nexo lo valida contra su base. Con bases distintas, los `ID` no coinciden y Nexo responde 401 (en las pruebas
del 2026-10-13, dguerra era 430 en `dbsolucionesti` y 10123 en el portal).

| Ambiente | `Database=` de `ConnectionStrings__Identidad` |
|---|---|
| Local y pruebas | `dbsolucionesti` falsa (`scripts/sql/dev-02-identidad.sql`, `tests/Comun/clsBaseDatosPruebas`), con los tipos de `dbSolucionesTI_Prueba` |
| QA / staging | `dbSolucionesTI_Prueba` |
| Producción | la que use Nexo en producción (**por confirmar**) |

ClientePro manda el nombre de usuario (`idUsuario: "dguerra"`) y sus roles (`["25","31","32"]`, solo se auditan). Cotifly busca la fila por
`NOMBREUSUARIO`, guarda su `ID` y toma los roles de `tblSAPUsuarioRoles` (prueba `clsIdentidadPortalNexoTests`).

**Lo que Cotifly lee de la base de identidad** (solo el login y el refresh, `clsVerificadorIdentidad`; nada más usa esta conexión):

| Tabla (`dbo`) | Columnas y tipo que espera Cotifly | Verificado por Nexo en `dbSolucionesTI_Prueba` (2026-09-01) | Por confirmar en `_Prueba` |
|---|---|---|---|
| `tblSAPUsuariosApp` | `ID` `numeric(18,0)`, `NOMBREUSUARIO`, `DESCRIPCIONUSUARIO`, `EMAIL` (texto) | `ID` `numeric(18,0)` | tipo y largo de los textos (Cotifly espera `varchar(50)`, `varchar(250)`, `varchar(100)`; con `nvarchar` o más largos también lee bien, pero no busca nombres de más de 50 caracteres), y que `NOMBREUSUARIO` no se repita |
| `tblSAPUsuarioRoles` | `IDUSUARIO` `int`, `IDROLES` `int` | los dos `int` | nada de tipos. Cotifly ya no lee la columna `ID` de esta tabla |
| `tblSAPRoles` | `IDROLES` `int`, `DESCRIPCION` (texto) | `IDROLES` `int` | tipo de `DESCRIPCION` (solo informativa) |

Consulta de solo lectura para el DBA en `dbSolucionesTI_Prueba`:
`SELECT t.name AS tabla, c.name AS columna, ty.name AS tipo, c.max_length, c.precision, c.scale FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id WHERE t.name IN ('tblSAPUsuariosApp', 'tblSAPUsuarioRoles', 'tblSAPRoles') ORDER BY t.name, c.column_id;`

**Al cambiar de base en un ambiente con usuarios** (QA): seguir los pasos de [`DEPLOY.md`](../../DEPLOY.md#identidad-compartida-con-nexo-172).
Hay que dar permisos a `cotifly_app`, revisar `Roles.idRolExterno` contra los `IDROLES` de la base nueva y soltar una vez los
vínculos `UsuariosWeb.idUsuarioExterno` viejos (si no, el login responde 401 `identidad_inconsistente`) con
[`db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql`](../../db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql),
en la misma ventana del cambio de la variable: con el servicio detenido, o justo después del cambio y antes de que entren usuarios.
El siguiente login o refresh de cada persona la vincula al nuevo `ID` (pruebas `clsIdentidadPortalNexoTests` y
`clsRevincularIdentidadScriptTests`). Todo con aprobación.
**Pendiente externo:** Nexo QA debe configurar `ConnectionStrings:IdentidadPortal`.

## 10.9 Problemas frecuentes

| Síntoma | Causa probable | Qué hacer |
|---|---|---|
| El contenedor termina con código 1 al arrancar | Configuración inválida o faltante | `docker logs`: el mensaje nombra la clave. Revisar [03-infraestructura](03-infraestructura.md#32-variables-de-entorno-y-configuración) |
| `/health/ready` 503 `esquema incompleto: falta 0002` | El 0002 no está aplicado (faltan `TokensRefresco` o `Sesiones.estadoAgente`) | Aplicar en orden `db/migrations_mssql/0002` a `0010` con `sqlcmd -I -b`. En una base vacía, mejor la instalación desde cero (`db/ejecutar-bd`) |
| Una cotización se queda en `pendiente_costeo` y no avanza | Nexo no la ha costeado, Nexo caído (eventos `costeo_nexo_consulta` con `exito = 0` en `EventosHerramienta`), `CosteoNexo__Habilitado=false` o sin `Nexo__UrlBase` (log "Polling del costeo en Nexo desactivado" al arrancar) | Revisar `costeoConsultasFallidas`, `costeoSiguienteConsultaAt` y los eventos de la cotización; pasadas `CosteoNexo__EdadMaximaHoras` queda en `costeo_vencido` |
| Un servicio conexo queda `fallido_definitivo` en el costeo sin que Nexo haya rechazado ningún POST | El catálogo de conexos (o la cotización) de Nexo no respondió durante más de `CosteoNexo__EsperaMaximaCatalogoConexosHoras` (24 h por defecto) desde el inicio de la espera (warning "llevan más de … h esperando" en el log; eventos `conexos_envio_nexo` con `estado` `en_espera` y luego `fallido_definitivo`). Mientras espera, el conexo se ve `pendiente` y la cotización entregada se reintenta cada 5, 15 y 60 min; no consume `CosteoNexo__MaximoReintentosConexos` | Revisar el catálogo de conexos en Nexo y agregar el conexo a mano en la cotización de Nexo (el aviso `aviso_conexos_nexo` lo nombra). Subir el tope solo si las caídas de Nexo suelen durar más |
| `/health/ready` 503 `esquema incompleto: falta 0009` | La base tiene el esquema anterior a HU-021 (`Cotizaciones`, sin `CotizacionesSolicitud`), con o sin los scripts 0004 a 0008 | Aplicar en orden lo que falte de `0004` a `0008` (el 0009 exige `0001` a `0007`), luego `db/migrations_mssql/0009_reestructuracion_cotizaciones.sql` y el `0010`, con `sqlcmd -I -b` y la aprobación de scalderon-bs (ADR-019) |
| `/health/ready` 503 `esquema incompleto: falta 0010` | Está el 0009 pero no el 0010 (nombres de municipio, `solicitudNexoId`, nombre del conexo) | Aplicar `db/migrations_mssql/0010_nombres_municipio.sql` con `sqlcmd -I -b` (idempotente, no destructivo) antes de desplegar la versión de #130/#131 |
| `/health/ready` 503 `esquema incompleto: falta <Tabla.columna>` (hasta 5, en orden) | Falta una tabla o columna que el modelo EF mapea. Desde HU-021 las ausencias de los scripts 0004 a 0008 salen así: ya no existen los mensajes `falta 0004` a `falta 0008`. Qué script trae cada cosa: `CotizacionArchivosLicitacion`, 0005; `CotizacionPdfsHistorial`, `versionNegocio`, `textosCotizacion` y `pdfDesactualizado`, 0006; `notas` y `origenMunicipioId`/`destinoMunicipioId`, 0007; `horasCargue`/`horasDescargue` de la ruta, 0008; `CotizacionVehiculosLineaSolicitud`, `CotizacionMercancias`, `CotizacionServiciosConexos` y `MargenAjustes.lineaSolicitudId`, 0009 | Comparar la base con `db/scripts/01_crear_esquema.sql` y aplicar en orden el script que la trae (`sqlcmd -I -b`); todos son idempotentes. Una base anterior al 0009 responde `falta 0009`, no esta fila |
| `POST /web/licitaciones` responde 413 | El Excel más el JSON superan `SeguridadHttp__LimiteCuerpoLicitacionBytes` (12 MB por defecto) | El archivo admite 10 MB (límite de Nexo); revisar el tamaño del pliego o subir el límite con cuidado |
| `/health/ready` 503 con la base caída | Red, credenciales o certificado de SQL Server | Revisar la cadena; si el certificado no es de una CA de confianza, instalar la CA (preferido) o `TrustServerCertificate=True` temporal y documentado |
| Todo login responde 401 | `Roles.idRolExterno` sin fijar, persona sin rol mapeado, rol externo distinto del asignado, `identidad_inconsistente` (vínculo a un `ID` de otra base de identidad, ver [#172](#identidad-compartida-con-nexo-172)) o la base de identidad inalcanzable | Buscar el motivo en `EventosHerramienta` (herramienta `login_clientepro`); un cambio de rol externo lo revisa un gerente, no se re-sincroniza solo |
| 401 intermitentes con tokens recién emitidos entre réplicas | Clave JWT distinta por réplica | Misma `Jwt__SigningKey` en todas |
| Un logout tarda en verse en otra réplica | Foto de `TokensRevocados` cada 60 s | Comportamiento esperado (hasta 60 s) |
| `/web/chat` responde `error: true` siempre | Agente sin configurar (`codigo` `ERROR_INTERNO`) o todos los modelos caídos (`IA_NO_DISPONIBLE`, #126) | Log "El agente de IA no está configurado", o "Ningún proveedor de IA respondió" con el estado del último. Un 404, un 408, un 5xx o un error de red pasan al siguiente modelo; un 400 de contenido no. Revisar `IA__*` y `IA__Fallbacks__*` |
| El chat tarda más de 8 s por turno | Varios comerciales a la vez agotan `IA:Rpm` (tope global), entraron respaldos de `IA__Fallbacks__*` o el modelo está lento | Leer el log "Turno del asistente de {folio}" (la fase que más tarda, las llamadas y los modelos) y los avisos "esperó N ms por IA:Rpm". Subir `IA__Rpm` según el plan del proveedor. Verificar que `IA__PropuestaDirecta` e `IA__RespuestaFinalEnLoop` estén en `true`. Para medir con el proveedor real, ver [Latencia del asistente](#latencia-del-asistente-126) |
| El turno tarda más de 60 s o el frontend corta la petición | Un turno encadena hasta 12 llamadas (`IA__TimeoutSeg` es por llamada); respaldos en cadena | Timeout del frontend ≥ 120 s; revisar `IA__Fallbacks__*` y la cuota del modelo principal |
| `/web/chat` responde "límite de peticiones" con OmniRoute | `429 model_cooldown` de OmniRoute: las credenciales del modelo están en enfriamiento (`reset_seconds` de horas) | Agregar o cambiar modelos en `IA__Fallbacks__*` (cada modelo tiene su cuota); una credencial con cuota de pago en OmniRoute. Ver la matriz en [03-infraestructura](03-infraestructura.md#omniroute) |
| Error del proveedor `400 No credentials for provider: <x>` | OmniRoute no tiene credenciales para ese proveedor (OpenRouter el 2026-10-01) o todas están en enfriamiento | El respaldo actúa solo; si es el único modelo, cambiar `IA__Modelo` a uno de un proveedor con credenciales (`gemini/...`) |
| Error del proveedor `400 Unknown name "condition"` o SSE (`text/event-stream`) con un gateway compatible | Imagen anterior a #84 (esquema de Semantic Kernel sin depurar o sin `"stream": false`) | Desplegar una imagen con #84 |
| `402 payment_required` con OpenRouter | `IA__MaxTokens` alto: OpenRouter reserva crédito por el máximo pedido | `IA__MaxTokens=1500` o recargar crédito |
| `CORREO_ENVIO_FALLIDO` con 550 5.7.1 | IP del nodo no autorizada en el relay | Pedir a TI que autorice la IP |
| Márgenes siempre de la política con SAP configurado | SAP caído, BP sin registro o formato del BP distinto (ceros a la izquierda) | Revisar los warnings de SAP TM y la tasa de "sin registro en SAP TM" |
| `/web/clientes?q=` responde 400 `CLIENTE_BUSQUEDA_NO_DISPONIBLE`; `/web/municipios` responde 503 `MUNICIPIOS_NO_DISPONIBLE`; `/web/catalogos-flete` (y `POST /web/solicitudes-costeo` o `POST /web/licitaciones` cuando necesitan el catálogo) responde 503 `CATALOGOS_FLETE_NO_DISPONIBLE`; `POST /web/licitaciones` responde 503 `MUNICIPIOS_NO_DISPONIBLE` | `Nexo__UrlBase` vacía, Nexo caído o lento (más de 8 s en total o 3 s por intento), URL base mal armada (404 de Nexo) o el comercial sin `idUsuarioExterno` | Revisar el warning de Nexo y su motivo; con "HTTP 404" revisar la URL base (el código agrega `/api/...`); con "circuito abierto" esperar a que Nexo se recupere; sin `idUsuarioExterno`, que el comercial vuelva a entrar desde ClientePro |
| `POST /web/cubicacion` responde 503 `CUBICADOR_NO_DISPONIBLE` | `Cubicador__UrlBase` vacía o el cubicador caído (HU-023.2: sin respaldo) | Revisar la configuración y la sonda `cubicador` de `/health/ready`; el warning "Calcular cubicaje" trae el detalle |
| `/web/chat` responde 400 `FOLIO_REQUERIDO` | El frontend envía mensajes sin `folio` (vista "Asistente" de creación, retirada) | Desde #105 no hay agente legado ni flag que lo reactive: el frontend debe abrir el asistente desde una cotización y enviar su `folio`. Las cotizaciones se crean con el formulario |
| Las cotizaciones o licitaciones en USD fallan con `TRM_NO_DISPONIBLE` | Sin salida a `co.dolarapi.com` ni a `www.datos.gov.co`, las dos fuentes caídas o lentas (más de `Trm__TiempoEsperaSegundos` cada una), DolarApi refrescado en la tarde (después de las 12:00 en Bogotá, puede traer la TRM de mañana), desactualizado, fuera de rango o a más de ±10 % de la última TRM y la Superintendencia sin TRM vigente para la fecha | Revisar los warnings "DolarApi no entregó una TRM válida", "Fuente de TRM no disponible" y "Sin TRM del ...": traen el motivo de cada fuente. Probar la salida con `curl https://co.dolarapi.com/v1/trm` desde el nodo. Si DolarApi cambió su formato, `Trm__Proveedor=superfinanciera` vuelve a la fuente oficial sin desplegar código. La API recuerda el fallo 20 s |
| `POST /web/solicitudes-costeo`, `POST /web/licitaciones` o `.../costeo/re-solicitar` responden 503 `SERVICIO_OCUPADO`; warning `Candado … no disponible tras N intentos` | Otra operación retuvo el candado del folio más que `CandadoSql__EsperaMilisegundos` en todos los intentos, o SQL Server no respondió (base degradada, bloqueos) | Reintentar (el 503 trae `Retry-After: 1` y no guardó nada). Si se repite: revisar bloqueos en SQL Server (`sys.dm_exec_requests` con `wait_type` `LCK_M_X` sobre `APPLICATION`) y el reloj de los nodos (`chronyc tracking`, `timedatectl`; fila "Reloj de los nodos" de §10.6) |
| Error de permisos al escribir el PDF | `/data/pdfs` no es del UID 1654 | Volumen con nombre, o `chown 1654:1654` en el bind mount |
