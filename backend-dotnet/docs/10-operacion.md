# 10. Operación

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
| `GET /health/ready` | Disponibilidad. Para el balanceador (healthcheck de Traefik): saca la tarea del balanceo sin reiniciarla | `200 {"status":"ready"}`; `503 {"status":"unavailable","detalle":"..."}` sin cadena, base caída o `esquema incompleto: falta 0002` / `falta 0004` / `falta 0005` |

- `/health/ready` ejecuta los health checks con etiqueta `ready` (`clsBaseDatosHealthCheck`: conexión a `dbo_cotifly` más la verificación de `Sesiones.estadoAgente` y `TokensRefresco`, con 5 s de límite). `detalle` nunca trae la cadena ni el servidor; la excepción queda en el log.
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
| "Cubicación rechazada por Nexo/Cubicador (400)" | Warning | Nexo no aceptó los bultos (límite por solicitud o medidas inválidas); el comercial recibe `CUBICACION_DEMASIADO_GRANDE` |
| "Fallo procesando el evento de Google Chat" | Error | Un evento del webhook falló; el worker sigue y alerta a `GChat:SupportSpace` |
| "Fallo en el barrido de sesiones inactivas" | Warning | Se reintenta en el siguiente ciclo |
| `OnAuthenticationFailed` con el tipo de rechazo | Information | Tokens vencidos, de otra clave o mal formados |
| "El agente de IA no está configurado" | Error | Falta `IA:Proveedor`, `IA:Modelo` o `IA:ApiKey` |
| Clave JWT o de firma de URL efímera | Warning | Fuera de Production sin clave: los tokens o enlaces dejan de valer al reiniciar |

- `EventosHerramienta` es la auditoría de negocio (herramientas, logins, descargas de PDF ajenos, administración de líneas): consultarla en la base, no en los logs. Contiene datos personales (ver [10.6](#106-riesgos-y-limitaciones-conocidos)).

## 10.3 Métricas

Sin exportador todavía (OpenTelemetry o Prometheus se evalúan en HU-006). Las métricas se leen con `dotnet-counters` desde un contenedor auxiliar ([10.5](#105-depurar-sin-shell)):

| Medidor | Instrumentos | Para qué |
|---|---|---|
| `Cotifly.Agente` | `cotifly.agente.respuesta_final.invalidas` (etiqueta `motivo`), `cotifly.agente.respuesta_final.reintentos`, `cotifly.agente.respuesta_final.respaldos`, `cotifly.agente.esquema_rechazado` | Calidad de la respuesta del modelo (#16): una tasa alta de respaldos o de esquema rechazado indica un modelo o proveedor inadecuado |
| `System.Runtime` | GC, memoria, hilos, excepciones | Salud del proceso |
| `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel` | Peticiones, duración, conexiones | Carga HTTP |
| `System.Net.Http` | Peticiones salientes | SAP TM, Nexo, proveedor de IA, Google |

```bash
dotnet-counters monitor -p 1 --counters Cotifly.Agente,System.Runtime,Microsoft.AspNetCore.Hosting
```

## 10.4 Arranque fallido

- Cualquier excepción al construir o arrancar el host termina el contenedor con **código 1** y un solo registro del error, sin secretos (si falla antes de existir el logger, una línea `crit:` en stderr).
- En `Production` no arranca sin `ConnectionStrings:Cotifly`, `ConnectionStrings:Identidad`, `Jwt:SigningKey` o `Cors:OrigenesPermitidos`, con placeholders (`<secreto>`, `cambiar...`, `changeme`, `dev`), con autenticación integrada en una cadena, con `GChat:VerificarJwt=false`, o con Google Chat configurado sin `Pdf:ClaveFirmaUrl`. Lista completa en [03-infraestructura](03-infraestructura.md#32-variables-de-entorno-y-configuración).
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
| Latencia del chat | Con `IA:Rpm=8` un turno típico (~4 llamadas: herramientas + la llamada final) puede tardar ~30 s solo por el limitador; subir `IA:Rpm` según el plan del proveedor |
| Cola de Google Chat | En memoria del proceso: se pierde al reiniciar; sin orden entre réplicas (el candado por sesión evita turnos simultáneos del mismo hilo) |
| Revocación de `jti` | Un logout tarda hasta 60 s en verse en las demás réplicas (foto de `TokensRevocados`) |
| Límites de peticiones | `LimiteAuth` y `LimiteChat` son por proceso: con N réplicas el límite efectivo es N veces mayor |
| `generar_pdf` multi-destino | Pone el mismo precio final en todas las filas (`Cotizaciones` guarda un solo precio); un precio por fila queda pendiente de modelo |
| `trmAplicada` | Siempre 0 hasta HU-012 (conversión a USD). El tablero de HU-010.1 ya convierte a COP las filas en USD con `precioFinal × trmAplicada` |
| Coincidencia exacta de clientes en Nexo | Nexo no la distingue: hoy es exacta solo con un único candidato (ADR-005, pendiente con el equipo de Nexo) |
| Catálogos de Nexo | `GET /web/catalogos-flete` consulta Nexo en cada petición. El de municipios se reutiliza `Nexo__MinutosCacheMunicipios` (60 por defecto, HU-005.13): con Nexo caído se sigue sirviendo la foto vigente, y al vencer responde `MUNICIPIOS_NO_DISPONIBLE` (el chat sigue con el texto libre). Un cambio del catálogo en Nexo tarda hasta ese tiempo en verse. Solo se guarda un catálogo exitoso y no vacío; un 200 que no es un arreglo cuenta como `MUNICIPIOS_NO_DISPONIBLE`. Sin foto vigente, las consultas que llegan mientras hay una en curso esperan esa misma y reciben su mismo resultado (éxito o fallo): con Nexo caído no se encolan una detrás de otra (cada una tardaría hasta 8 s). No hay caché negativa: un fallo no se guarda, porque Nexo da el mismo código cuando el comercial que consulta no tiene `idUsuarioExterno`, y eso no debe dejar sin catálogo a los demás |
| URL base de Nexo | `Nexo__UrlBase` es la raíz del servicio sin `/api` (el código agrega `/api/...`; `.env.example` corregido, ADR-015 §8); confirmar la URL real de cada ambiente con el equipo de Nexo antes de activar |
| E2E con el Angular | Pendiente (HU-004.8): el contrato está cubierto por pruebas contra `openapi.yaml`, no contra el frontend real |
| #45 (tablero de métricas) | `/web/metricas/comercial` y `/regional` listos (ADR-016). El 0004 ya está en QA: el mapa por ciudad y `deadline_vencidas` tienen datos. Solo sobre una base sin el 0004 el mapa responde `disponible: false` y `deadline_vencidas` es `null`. Pendiente: definiciones por validar con Gerencia Comercial, score de urgencia de Nexo, servicios conexos (después de #42) y la regional (fila "Regional del comercial"). Desde #81 la descarga de PDF ajenos y `LoginResponse.ubicacion` leen `UsuariosWeb.regional`, que el login refresca desde `DimEjecutivo` |
| Validador de la respuesta final (#16) | La regla de montos es práctica (ADR-003). Batería de 52 textos del validador: falsos positivos 3 → 0 (NIT, teléfono, NIT con puntos) y falsos negativos 11 de 24 → 2 (montos en palabras, "tres millones y medio"; el folio base de una versión, aceptado a propósito). Sigue rechazando aproximaciones honestas ("3,29 millones", centavos que no existen). Cada reintento cuesta una llamada más (con `IA:Rpm=8`, unos 8 s). Si el proveedor rechaza el JSON Schema (400/422), el proceso sigue sin formato `IA:MinutosSinJsonSchema` (30) y luego re-sonda, también para los proveedores de `IA:Fallbacks`. El respaldo no alerta al space de soporte. Falta medir con el proveedor real la tasa de reintentos, respaldos y rechazos (`cotifly.agente.*`) |

Otros puntos de operación (de #11 y #12):

| Tema | Estado |
|---|---|
| Réplicas | `replicas: 1` mientras la deduplicación del correo (`clsCandadoEnvios`) sea por proceso y los PDF vivan en un volumen local; con varias réplicas hacen falta `sp_getapplock` para el correo y un almacén compartido (ADR-012) |
| Regional del comercial | Tabla verificada en #81 (`DWCubosEBSSAP.dbo.DimEjecutivo`, cruce por `USUARIO` sin el dominio `boterosoto\`) y login conectado (ADR-016 §6). **Para activarla en un ambiente:** cargar `ConnectionStrings__Regional` en los secretos de Dokploy (usuario SQL de solo lectura) y abrir la salida de red del clúster al servidor del cubo (puerto 1433); sin eso, `ConnectionStrings__Regional` queda vacía y no se consulta nada. Con la cadena, el primer login llena `UsuariosWeb.regional` y cada login siguiente la refresca; quien no está en `DimEjecutivo` conserva la que tenga. Si `DimEjecutivo` falla o tarda más de `Regional__TiempoEsperaLoginSegundos` (3 s), el login entra igual y queda un warning `No se pudo consultar la regional de '<usuario>' en DimEjecutivo` en el log: revisar red y credencial si se repite. Para quien no está en `DimEjecutivo`, el DBA llena `UsuariosWeb.regional` **después del primer login** de cada persona (la fila se crea en el alta JIT): `UPDATE dbo_cotifly.UsuariosWeb SET regional = N'<REGIONAL>' WHERE nombreUsuario IN (N'<USUARIO1>', N'<USUARIO2>');`. Usar exactamente el mismo texto de regional para el gerente y sus comerciales, y el mismo que tenga `DimEjecutivo` cuando se conecte (se compara sin mayúsculas ni espacios sobrantes: "Antioquia" y "Regional Antioquia" son regionales distintas). La regional resuelta se reutiliza 60 s: un cambio en `DimEjecutivo` tarda hasta un minuto en verse. Sin regional, el tablero del gerente responde 409 `REGIONAL_NO_ASIGNADA`, el borrador de métricas le muestra solo su fila y no descarga PDF ajenos (fail-closed). Con `ConnectionStrings__Regional` configurada y la base caída, el tablero responde 503 `REGIONAL_NO_DISPONIBLE` |
| Regional conservada al salir de `DimEjecutivo` | Riesgo operativo (#81): quien deja de aparecer en `DimEjecutivo` conserva la última regional guardada y, si es gerente, su alcance (tablero de la regional y PDF de sus comerciales) hasta que el DBA cambie `UsuariosWeb.regional`. La regional es una fuente de autorización de alcance (ADR-016 §6). Seguimiento: guardar el origen de la regional para limpiarla solo cuando vino de `DimEjecutivo` (columna nueva, script 000N) |
| Latencia del login con `DimEjecutivo` caída | Los fallos no se guardan en caché (#81): mientras `DimEjecutivo` esté caída o lenta, cada login espera hasta `Regional__TiempoEsperaLoginSegundos` (3 s) antes de seguir sin refrescar la regional. Mejora futura: un circuit breaker que deje de consultar un rato tras varios fallos seguidos |
| Cambio de correo en el login | Desde #81 el login refresca `email` desde el almacén; como el correo es la identidad en Google Chat (ADR-011), un cambio en `tblSAPUsuariosApp.EMAIL` cambia con qué correo Google Chat reconoce a la persona. Queda un warning `El correo de '<usuario>' cambió en el almacén corporativo` |
| Datos personales en `EventosHerramienta` | Entradas con correos de clientes, nombres y NIT: definir retención y purga con datos y seguridad |
| SPF/DMARC del correo | El `From` es el correo del comercial aunque sale por el relay: TI debe autorizar el relay en SPF/DMARC para no caer en spam |
| `/data/pdfs` | Debe ser escribible por el UID 1654 (un volumen con nombre copia el dueño de la imagen; con bind mount, `chown 1654:1654`) |
| Sin `.pdb` en la imagen | Las trazas no traen número de línea; HU-006.4 (#28) mantuvo `DebugType=none` (alternativa: `DebugType=embedded` + `PathMap`, ver 03-infraestructura §3.1) |

Excepciones temporales del escaneo de vulnerabilidades de la imagen (`backend-dotnet/.trivyignore.yaml`, procedimiento en [03-infraestructura §3.1](03-infraestructura.md#31-contenedor-e-imagen)). Cada una vence sola: desde `expired_at` (00:00 UTC, 19:00 del día anterior en Colombia) Trivy vuelve a reportar el CVE y el job `imagen-docker` falla hasta que se resuelva o se prorrogue. Agregar o prorrogar una excepción exige la aprobación explícita en el PR de un revisor humano distinto del autor (líder técnico o seguridad); hoy no hay `CODEOWNERS` que lo obligue (se sugiere agregar `backend-dotnet/.trivyignore.yaml` a uno). Mientras está vigente, una excepción aplica en todas las ramas, también en `main` y en los tags de release. El workflow semanal `vigilancia-imagen-base.yml` (lunes 12:00 UTC) avisa cuándo se puede quitar y falla si vence en 7 días o menos (ver 03-infraestructura §3.1).

| CVE | Componente | Por qué no es explotable hoy | Dueño | Vence | Seguimiento |
|---|---|---|---|---|---|
| CVE-2026-84782 (HIGH) | `libssl3t64` y `openssl` `3.0.13-0ubuntu3.15` de `aspnet:10.0.12-noble-chiseled-extra` (digest `sha256:6385dc0e…`); parche en Ubuntu: `3.0.13-0ubuntu3.16` | Divulgación de información en la retransmisión del handshake **DTLS** (TLS sobre UDP). Cotifly solo usa TLS sobre TCP: Kestrel en HTTP 8080 detrás de Traefik (sin HTTP/3), `HttpClient` hacia Nexo, SAP, datos.gov.co y el LLM, `SqlClient` hacia SQL Server y MailKit hacia el relay SMTP. Sin DTLS: el único UDP del proceso es DNS (y SSRP/SQL Browser si se usara una instancia con nombre de SQL Server), ninguno pasa por OpenSSL (revisado en #86) | Duban Guerra (`dguerraBSSL`) | 2026-10-23 00:00 UTC (2026-10-22 19:00 en Colombia). Recordatorio del dueño: 2026-10-16; renovar o quitar a más tardar el 2026-10-21 | #86 y `vigilancia-imagen-base.yml`. Cuando Microsoft republique el tag con digest nuevo o publique `10.0.13`: subir `SDK_VERSION`/`ASPNET_VERSION` juntos (o reconstruir con `--pull` si solo cambió el digest), borrar la excepción y esta fila, actualizar `DIGEST_FIJADO` en `vigilancia-imagen-base.yml` y repetir la verificación de [06-ejecucion-local](06-ejecucion-local.md) |

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
| Login de servicio `cotifly_app`, permisos por esquema en `dbo_cotifly` y por columna en `dbsolucionesti` | DBA | [03-infraestructura](03-infraestructura.md#33-base-de-datos) |
| Precondición de despliegue (`DEPLOY.md`): el 0004 aplicado antes de desplegar esta versión; staging con `CosteoNexo__Habilitado=false` mientras comparta base con producción de Nexo | DBA | ADR-015 |
| Aprobación de los scripts 0002, 0003 y 0004 (el 0004 con el DBA, coordinado con HU-002) | Sara, scalderon-bs y DBA | ADR-008, ADR-015 |
| Aprobación del 0005 (`CotizacionLineasSolicitud`, `CotizacionArchivosLicitacion`, FK de posiciones con su índice y backfill) y precondición de despliegue: aplicarlo antes de la versión de HU-011. Guarda el Excel original de cada licitación en `VARBINARY(MAX)` (hasta 10 MB por licitación): el DBA debe contemplar el crecimiento de la base | scalderon-bs y DBA | ADR-017 |
| **Retención del Excel original (propuesta):** los pliegos son documentos confidenciales del cliente y pueden traer datos personales (Ley 1581 de 2012). Purgar `CotizacionArchivosLicitacion.contenido` N meses después del cierre de la licitación (propuesta: 12; conservar nombre, hoja, tamaño y SHA-256, que es solo trazabilidad) con un trabajo programado o un script del DBA; tener en cuenta que los respaldos conservan el contenido hasta su propia caducidad, y vaciar o anonimizar la tabla en cualquier copia de producción a pruebas. Una licitación purgada se exporta sin el original (hoja con los campos mapeados) | Gerencia Comercial y DBA | ADR-017 §2 |
| Mitigación de la frontera de confianza del login (aserción firmada de ClientePro) | Duban con ClientePro | ADR-009, "Riesgo abierto" |
| Transporte real del correo (`smtp_relay`, `wscorreos` o `smtp`) | Equipo (D3) | [03-infraestructura](03-infraestructura.md#pdf-y-correo) |
| Proveedor y modelo de IA de producción, custodia de la clave | Negocio | ADR-003 |
| Criterio de coincidencia exacta de la búsqueda de clientes y URL base de Nexo por ambiente | Duban con el equipo de Nexo | ADR-005, [03-infraestructura](03-infraestructura.md#nexo) |
| Destinatarios previos: último envío o unión de todos | Negocio | [02-arquitectura](02-arquitectura.md#28-pdf-correo-y-auditoría) |
| Fuente `Stilu` del PDF (sin licencia disponible) | Negocio | ADR-006 |
| Sincronización a Google Sheets y S3/MinIO | Duban con Gerencia Comercial e infraestructura | ADR-012 |
| Fuente oficial de la TRM (datos abiertos de la Superintendencia Financiera) y salida a `www.datos.gov.co` | Duban con el equipo; infraestructura | ADR-014 |
| Nueva versión (v2) de una cotización en USD: hoy se siembra con la TRM de la original (`trmAplicada` copiada) y, al recalcular, toma la TRM del día. ¿La v2 debe conservar la TRM original o usar la del día? | Gerencia Comercial | ADR-014 |
| Texto legal de la aclaración del piso mínimo en el PDF en USD (`clsTextosEstandar.AclaracionDolares`) | Gerencia Comercial / jurídica | ADR-014 |
| OmniRoute (#84): qué credencial tiene cargada para cada proveedor (una clave gratuita de AI Studio permite a Google usar los datos), residencia de los datos y si OmniRoute registra los prompts. Hasta confirmarlo, solo datos ficticios en pruebas | Negocio / jurídica / dueño de OmniRoute | ADR-003 |
| OmniRoute por HTTPS (Traefik con TLS, #25/#26) o aislamiento worker → manager: hoy los datos del cliente y la clave viajan en claro | Infraestructura | DEPLOY.md §2.2 |
| Timeout de `POST /web/chat` en el frontend de ClientePro ≥ 120 s (un turno encadena hasta 12 llamadas al modelo) | Frontend (Sara) | 03-infraestructura "OmniRoute" |

## 10.8 Activación por integración

| Integración | Pasos | Variables |
|---|---|---|
| Login desde ClientePro | 1. Login `cotifly_app` y permisos (DBA): `GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::dbo_cotifly TO cotifly_app` (por esquema, sin `db_datareader`/`db_datawriter`) y `SELECT` por columna sin `CLAVE` en `dbsolucionesti`; comprobarlo con `scripts/sql/dev-04-verificar-permisos.sql` adaptando la tabla de otro esquema. 2. Aplicar 0002 y 0003 con `sqlcmd -I`. 3. `UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES> WHERE codigo = 'comercial'` (ídem `gerente_comercial`), con `sqlcmd -I`. 4. Clave JWT compartida por todas las réplicas. 5. Origen CORS del frontend | `ConnectionStrings__Cotifly`, `ConnectionStrings__Identidad`, `Jwt__SigningKey`, `Cors__OrigenesPermitidos__0` |
| Detrás de Traefik | Encabezados reenviados con la red overlay; readiness de Traefik en `/health/ready`; puerto 8080 | `ForwardedHeaders__Habilitado=true`, `ForwardedHeaders__RedesConocidas__0` |
| Correo | Confirmar el transporte (D3), autorizar la IP del nodo en el relay, revisar SPF/DMARC | `Correo__Habilitado=true`, `Correo__SmtpRelay__Host` |
| SAP TM export | Montar el export de solo lectura y reemplazarlo copiando con otro nombre y renombrando | `SapTm__Modo=export_json`, `SapTm__ExportPath` |
| SAP TM OData (HU-009) | Ruta de red y usuario de servicio (HU-009.1); confirmar el formato del BP (ceros a la izquierda); validar con al menos 3 casos reales (`Categoria=sap`) | `SapTm__Modo=odata`, `SapTm__Url`, `SapTm__User`, `SapTm__Pass`, `SapTm__SapClient` |
| Nexo (clientes, municipios, catálogos de flete, cubicación) | Confirmar la URL base con el equipo de Nexo y la ruta de red desde el contenedor; los comerciales necesitan `idUsuarioExterno` (lo llena su login desde ClientePro); probar `GET /web/clientes?q=`, `GET /web/municipios`, `GET /web/catalogos-flete` y `POST /web/cubicacion` con un comercial real | `Nexo__UrlBase` |
| TRM para cotizar en USD (HU-012) | Abrir la salida HTTPS a `www.datos.gov.co` desde el contenedor; cotizar una prueba en USD y comparar la TRM con la publicada por la Superintendencia Financiera | `Trm__Url` (por defecto el conjunto oficial), `Trm__TiempoEsperaSegundos` |
| Agente de IA | Elegir proveedor y modelo; guardar la clave como secreto; ajustar `IA:Rpm` al plan del proveedor; medir respaldos y reintentos. Con OmniRoute (#84): `openai_compatible` con el endpoint interno por HTTP (riesgo: datos y clave en claro, DEPLOY.md §2.2), principal `gemini/gemini-3.5-flash-lite` (el único que completó la cotización con PDF y respeta `json_schema`), `IA__SoportaJsonSchema=false`, `IA__MaxTokens=1500` y respaldos `gemini/gemini-3.5-flash` y `gemini/gemini-3-flash-preview` en `IA__Fallbacks__0__*`/`__1__*` (ya están en los manifiestos), porque cada modelo entra en enfriamiento de ~9 h tras pocas llamadas. Rotar la clave antes de staging y luego cada 6 meses o ante cualquier exposición (procedimiento en DEPLOY.md §2.2). Matriz de modelos y estado del 2026-10-01 en [03-infraestructura](03-infraestructura.md#omniroute) | `IA__Proveedor`, `IA__Modelo`, `IA__ApiKey`, `IA__Endpoint`, `IA__Rpm`, `IA__MaxTokens`, `IA__SoportaJsonSchema`, `IA__Fallbacks__*` |
| Google Chat | App de Chat con endpoint `https://<api>/chat/events` y *Authentication Audience* = **Project Number** (el JWT se verifica siempre, HU-006.7), cuenta de servicio con alcance `chat.bot` como secreto de Swarm; cada comercial entra una vez desde ClientePro con el mismo correo de Google. Ver [DEPLOY.md](../../DEPLOY.md) §7 | `GChat__ProjectNumber`, `GChat__SaJson`, `GChat__SupportSpace`, `Pdf__BaseUrl`, `Pdf__ClaveFirmaUrl` |
| Respaldo de `dbo_cotifly` | Login de solo lectura `cotifly_respaldo` (`GRANT SELECT ON SCHEMA::dbo_cotifly`, DBA); cron diario en el worker con `scripts/respaldo/exportar-dbo-cotifly.sh`; retención 7 diarios + 4 semanales; prueba de restore con `restaurar-dbo-cotifly.sh --destino-pruebas`. Complementa los backups nativos del DBA. Ver [DEPLOY.md](../../DEPLOY.md) §6 | `COTIFLY_RESPALDO_*` (`scripts/respaldo/respaldo.env.example`) |

## 10.9 Problemas frecuentes

| Síntoma | Causa probable | Qué hacer |
|---|---|---|
| El contenedor termina con código 1 al arrancar | Configuración inválida o faltante | `docker logs`: el mensaje nombra la clave. Revisar [03-infraestructura](03-infraestructura.md#32-variables-de-entorno-y-configuración) |
| `/health/ready` 503 `esquema incompleto: falta 0002` | El 0002 no está aplicado | Aplicar `db/migrations_mssql/0002`, `0003` y `0004` con `sqlcmd -I` |
| Una cotización se queda en `pendiente_costeo` y no avanza | Nexo no la ha costeado, Nexo caído (eventos `costeo_nexo_consulta` con `exito = 0` en `EventosHerramienta`), `CosteoNexo__Habilitado=false` o sin `Nexo__UrlBase` (log "Polling del costeo en Nexo desactivado" al arrancar) | Revisar `costeoConsultasFallidas`, `costeoSiguienteConsultaAt` y los eventos de la cotización; pasadas `CosteoNexo__EdadMaximaHoras` queda en `costeo_vencido` |
| `/health/ready` 503 `esquema incompleto: falta 0004` | El 0004 (costeo en Nexo) no está aplicado y el modelo de `Cotizaciones` ya lo usa | Aplicar `db/migrations_mssql/0004_costeo_nexo.sql` con `sqlcmd -I` (idempotente) |
| `/health/ready` 503 `esquema incompleto: falta 0005` | El 0005 (líneas de la solicitud y Excel de licitaciones, ADR-017) no está aplicado | Aplicar `db/migrations_mssql/0005_lineas_solicitud.sql` con `sqlcmd -I` (idempotente; hace el backfill de una línea por cotización del formulario). **Aplicarlo una sola vez por ambiente** (antes de desplegar la versión de HU-011): re-ejecutarlo después de que la aplicación haya escrito cotizaciones con `tipoSolicitud` pero sin líneas (por ejemplo, si falló algo entre guardar la cabecera y `AgregarLineas`) crearía para ellas una línea a partir de las columnas de la cabecera. No duplica líneas existentes, pero esa línea nueva sería un dato que nadie pidió (#91) |
| `POST /web/licitaciones` responde 413 | El Excel más el JSON superan `SeguridadHttp__LimiteCuerpoLicitacionBytes` (12 MB por defecto) | El archivo admite 10 MB (límite de Nexo); revisar el tamaño del pliego o subir el límite con cuidado |
| `/health/ready` 503 con la base caída | Red, credenciales o certificado de SQL Server | Revisar la cadena; si el certificado no es de una CA de confianza, instalar la CA (preferido) o `TrustServerCertificate=True` temporal y documentado |
| Todo login responde 401 | `Roles.idRolExterno` sin fijar, persona sin rol mapeado, rol externo distinto del asignado, o `dbsolucionesti` inalcanzable | Buscar el motivo en `EventosHerramienta` (herramienta `login_clientepro`); un cambio de rol externo lo revisa un gerente, no se re-sincroniza solo |
| 401 intermitentes con tokens recién emitidos entre réplicas | Clave JWT distinta por réplica | Misma `Jwt__SigningKey` en todas |
| Un logout tarda en verse en otra réplica | Foto de `TokensRevocados` cada 60 s | Comportamiento esperado (hasta 60 s) |
| `/web/chat` responde `error: true` siempre | Agente sin configurar o proveedor caído | Log "El agente de IA no está configurado" o errores del proveedor; revisar `IA__*` y `IA__Fallbacks__*` |
| El chat tarda ~30 s por turno | Limitador local `IA:Rpm=8` | Subir `IA__Rpm` según el plan del proveedor |
| El turno tarda más de 60 s o el frontend corta la petición | Un turno encadena hasta 12 llamadas (`IA__TimeoutSeg` es por llamada); respaldos en cadena | Timeout del frontend ≥ 120 s; revisar `IA__Fallbacks__*` y la cuota del modelo principal |
| `/web/chat` responde "límite de peticiones" con OmniRoute | `429 model_cooldown` de OmniRoute: las credenciales del modelo están en enfriamiento (`reset_seconds` de horas) | Agregar o cambiar modelos en `IA__Fallbacks__*` (cada modelo tiene su cuota); una credencial con cuota de pago en OmniRoute. Ver la matriz en [03-infraestructura](03-infraestructura.md#omniroute) |
| Error del proveedor `400 No credentials for provider: <x>` | OmniRoute no tiene credenciales para ese proveedor (OpenRouter el 2026-10-01) o todas están en enfriamiento | El respaldo actúa solo; si es el único modelo, cambiar `IA__Modelo` a uno de un proveedor con credenciales (`gemini/...`) |
| Error del proveedor `400 Unknown name "condition"` o SSE (`text/event-stream`) con un gateway compatible | Imagen anterior a #84 (esquema de Semantic Kernel sin depurar o sin `"stream": false`) | Desplegar una imagen con #84 |
| `402 payment_required` con OpenRouter | `IA__MaxTokens` alto: OpenRouter reserva crédito por el máximo pedido | `IA__MaxTokens=1500` o recargar crédito |
| `CORREO_ENVIO_FALLIDO` con 550 5.7.1 | IP del nodo no autorizada en el relay | Pedir a TI que autorice la IP |
| Márgenes siempre de la política con SAP configurado | SAP caído, BP sin registro o formato del BP distinto (ceros a la izquierda) | Revisar los warnings de SAP TM y la tasa de "sin registro en SAP TM" |
| Google Chat no responde | JWT inválido (401 en `/chat/events`), cola llena o sin `GChat__SaJson` | Revisar `GChat__ProjectNumber`, `GChat__CapacidadCola` y el warning de publicación |
| `/web/clientes?q=`, `/web/municipios`, `/web/catalogos-flete` o `/web/cubicacion` responden 400 `*_NO_DISPONIBLE` y el agente no resuelve clientes | `Nexo__UrlBase` vacía, Nexo caído o lento (más de 8 s en total o 3 s por intento), URL base mal armada (404 de Nexo) o el comercial sin `idUsuarioExterno` | Revisar el warning de Nexo y su motivo; con "HTTP 404" revisar la URL base (el código agrega `/api/...`); con "circuito abierto" esperar a que Nexo se recupere; sin `idUsuarioExterno`, que el comercial vuelva a entrar desde ClientePro |
| `POST /web/cubicacion` responde `CUBICACION_DEMASIADO_GRANDE` | Nexo/Cubicador rechazó los bultos (límite por solicitud o medidas inválidas) | Ver el `detalle` (mensaje de Nexo); dividir la carga o corregir las medidas (centímetros y kilogramos) |
| El agente cotiza en pesos aunque el comercial pidió dólares | En la consola, el selector del formulario envió `moneda: "COP"` (manda sobre el mensaje); en Google Chat, el mensaje no nombró la moneda (dólares, USD, US$) | Elegir USD en el selector o pedirlo de forma explícita en el mensaje |
| Las cotizaciones en USD fallan con `TRM_NO_DISPONIBLE` | Sin salida a `www.datos.gov.co`, la fuente caída o lenta (más de `Trm__TiempoEsperaSegundos`), o sin TRM vigente para la fecha | Revisar el warning "Fuente de TRM no disponible" y su motivo; probar la URL desde el nodo; el comercial puede reintentar (los fallos no quedan en caché) o cotizar en COP |
| Error de permisos al escribir el PDF | `/data/pdfs` no es del UID 1654 | Volumen con nombre, o `chown 1654:1654` en el bind mount |
