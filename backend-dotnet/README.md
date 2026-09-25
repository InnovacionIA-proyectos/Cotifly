# Cotifly — backend .NET 10

Backend de Cotifly migrado de Python a C# / .NET 10 (milestone HU-003). Este README es el esqueleto creado en HU-003.2 (#8); las secciones marcadas se completan en HU-003.8 (#14).

## 1. Descripción

Cotifly es el bot cotizador de Botero Soto: un comercial conversa con un agente de IA (consola web de ClientePro o Google Chat), el agente reúne los datos, resuelve margen y precio, genera el PDF de la cotización y la envía por correo. Este proyecto reemplaza por completo a los servicios Python `orquestador` y `mcp_server` (corte big-bang, ADR-001).

Estado actual: solución, capas, convenciones, patrón de configuración validada al arrancar, `GET /health` y `GET /health/ready`, modelo de dominio y persistencia EF Core Database First sobre SQL Server 2019 (#9), los módulos de negocio Precio, Margen (incluido el margen negociado) y Tarifas (#10, ver §2) y las fuentes reales de SAP TM, export JSON y OData en vivo, elegidas por `SapTm:Modo` (#13, ver §2 y §3). El resto de la lógica de negocio llega en #11, #12 y #14 a #16.

## 2. Arquitectura

Clean Architecture de 4 capas en un monolito modular (ADR-002):

| Proyecto | Responsabilidad | Depende de |
|---|---|---|
| `Cotifly.Dominio` | Entidades, value objects, reglas puras, `clsResultado<T>`, `clsErrorDominio` | nada |
| `Cotifly.Aplicacion` | Casos de uso, puertos `ICls*`, DTOs, validadores | Dominio |
| `Cotifly.Infraestructura` | Adaptadores de los puertos (EF Core, HTTP, PDF, correo, IA) | Aplicacion, Dominio |
| `Cotifly.Api` | Controllers, middleware, composition root | Aplicacion; Infraestructura solo para `AddInfraestructura()` |

Dominio, Aplicacion e Infraestructura tienen una carpeta por módulo: `Cotizaciones`, `Tarifas`, `Margen`, `Precio`, `Pdf`, `Correo`, `Auditoria`, `Identidad`, `Agente`, `Clientes`, `Administracion`, `Metricas`. Api solo tiene carpetas de los módulos con endpoints en `specs/openapi.yaml`, más `Sistema/` (health). Además: `Comun/` para lo transversal, `Sesiones/` solo en Dominio y `Configuracion/` en Infraestructura. Mientras un módulo no tiene código, su carpeta solo contiene un `LEEME.md` con su propósito y subcarpetas (en inglés en todas las capas):

- Dominio: clases en la carpeta; si crece, `Entities/`, `ValueObjects/`, `Rules/`.
- Aplicacion: `DTOs/`, `Validators/`, `Services/`, `Repositories/` (puertos), `Interfaces/`.
- Infraestructura: `Repositories/`, `Services/`; `Persistence/` para el `DbContext` (el de `dbo_cotifly` en `Comun/Persistence/`, el de solo lectura de `dbsolucionesti` en `Identidad/Persistence/`).
- Api: `Controllers/`, `DTOs/`.

**Modelo de dominio y persistencia (#9).** Una entidad por tabla de `dbo_cotifly`, sin dependencias técnicas; setters privados, fábricas `Crear`/`Registrar`/`Emitir` que devuelven `clsResultado<T>` con las invariantes baratas (folio obligatorio ≤ 50, versión ≥ 1, TRM 0 en COP, márgenes ≥ 0 y < 1 solo con el método `margen` (`markup` admite ≥ 1, `clsReglaMargen`), correo con al menos un `para`, destinatario `para|cc`, hash SHA-256 de 64 hex...). Los enums se guardan con su código snake_case (`clsCodigoEnum`: `sin_enviar`, `politica_linea`, `gerente_comercial`), que es lo que exigen los CHECK del esquema.

| Tabla (`dbo_cotifly`) | Entidad (Dominio) | Puerto (Aplicacion) |
|---|---|---|
| `Cotizaciones` | `Cotizaciones/clsCotizacion` (+ `EstadoEnvio`, `clsDatosCotizacion`) | `IClsCotizacionRepository` |
| `MargenesPolitica` | `Margen/clsMargenPolitica` | `IClsMargenPoliticaRepository` |
| `SapMapeoTrqType` | `Margen/clsSapMapeoTrqType` | `IClsSapMapeoRepository` |
| `MargenAjustes` | `Margen/clsMargenAjuste` (+ `OrigenMargen`) | `IClsMargenAjusteRepository` |
| `Correos`, `CorreoDestinatarios` | `Correo/clsCorreo`, `clsCorreoDestinatario` (+ `EstadoCorreo`, `TipoDestinatario`) | `IClsCorreoRepository` |
| `Sesiones`, `EventosConversacion` | `Sesiones/clsSesion`, `clsEventoConversacion` (+ `EstadoSesion`) | `IClsSesionRepository` (en `Agente`) |
| `EventosHerramienta` | `Auditoria/clsEventoHerramienta` | `IClsAuditoriaRepository` |
| `UsuariosWeb` | `Identidad/clsUsuarioWeb` | `IClsUsuarioWebRepository` |
| `RolesMapeo`, `TokensRefresco`, `TokensRevocados` (script 0002) | `Identidad/clsRolMapeo`, `clsTokenRefresco`, `clsTokenRevocado` (+ `RolInterno`) | sin puerto aún (#12) |
| `Sesiones.estadoAgente` (JSON, script 0002) | `Agente/clsEstadoConversacion` (+ `clsCandidatoCliente`) | vía `clsSesion` |
| `dbsolucionesti.tblSAPUsuariosApp` (solo lectura) | `Identidad/clsUsuarioSapApp` (sin `CLAVE`) | sin puerto aún (#12) |

Los repositorios solo agregan o modifican en memoria; `IClsUnitOfWork.GuardarCambiosAsync` confirma todo en una transacción y traduce las violaciones de unicidad a `clsViolacionUnicidadException` con código de negocio (`uq_cotizaciones_folio` → `FOLIO_DUPLICADO`). `OrigenCliente` vive en `Clientes`.

**Convenciones de nombres** (ADR-002), exigidas por `.editorconfig` (IDE1006 con severidad `error`, rompe el build) y por `Cotifly.Arquitectura.Tests`:

| Símbolo | Regla | Ejemplo |
|---|---|---|
| Clases, records, clases estáticas, atributos, clases de prueba | `cls` + PascalCase | `clsCotizacionService`, `clsSecretoAttribute`, `clsResultadoTests` |
| Interfaces | `ICls` + PascalCase | `IClsCotizacionRepository` |
| Structs, enums, delegados | PascalCase | `EstadoEnvio` |
| Miembros no privados, constantes | PascalCase | `AhoraUtc`, `NombreServicio` |
| Campos privados (instancia y estáticos) | `_camelCase` | `_timeProvider` |
| Parámetros y variables locales | camelCase | `servicios` |
| Parámetros de tipo | `T` + PascalCase | `TValor` |
| Namespaces | file-scoped (IDE0161, error) | `namespace Cotifly.Dominio.Comun;` |

En los controllers la ruta siempre es explícita (`[Route("health")]`); nunca `[Route("[controller]")]`, porque el prefijo `cls` terminaría en la URL.

Única excepción: la clase `Program` que genera el compilador para las instrucciones de nivel superior (`Program.cs` suprime IDE1006 solo en su primera línea). En .NET 10 `Program` ya es pública, así que no se declara `public partial class Program`.

**Precio, Margen y Tarifas (#10, HU-003.4).** Port de `mcp_server/precio.py` y `mcp_server/margen/` con paridad verificada contra los tests del Python (tabla en §8):

| Pieza | Capa | Qué hace |
|---|---|---|
| `Precio/clsCalculadoraPrecio` (+ `clsPrecioCalculado`) | Dominio | `margen`: costo / (1 − m), exige 0 ≤ m < 1; `markup`: costo × (1 + m), m ≥ 0. Precio a pesos enteros y utilidad a 2 decimales con `MidpointRounding.ToEven` sobre `decimal` (ADR-010 §6: paridad con Python salvo empates exactos de medio peso, donde .NET redondea el valor exacto y Python arrastra el error de `float`; diferencia máxima 1 peso, ~0,04 % de casos, ej. 55 con markup 0.1 → 60 y Python 61). Costo ≤ 0, m < 0, m ≥ 1 con `margen` o método desconocido → `PRECIO_INVALIDO` con los mensajes del Python |
| `Margen/clsReglasMargen` | Dominio | `Value` de SAP / 100 a 6 decimales, anomalía fuera de [0, 0.6], comparación de líneas sin tildes, mayúsculas ni espacios (`ALMACENAMIENTO 3PL` = `Almacenamiento3PL`) |
| `Margen/clsReglaMargen` | Dominio | Rango por método; ahora también lo usa `clsMargenAjuste` (con `markup` un ajuste ≥ 1 es válido), que además redondea ambos márgenes a 4 decimales (escala de la columna) antes de calcular la diferencia |
| `Comun/clsCodigosError` | Dominio | Códigos del esquema `ErrorNegocio` (verificados contra `openapi.yaml`) |
| `IClsPrecioService` / `clsPrecioService` → `clsSalidaPrecio` | Aplicacion | `calcular_precio`, salida `SalidaPrecio` `{precio, costo, margen, metodo, utilidad}` |
| `IClsMargenResolverService` / `clsMargenResolverService` → `clsMargenResuelto` (`Salida`: `clsSalidaMargen`) | Aplicacion | `obtener_margen`, mismo orden que el Python: línea → TrqType (`IClsSapMapeoRepository`, la organización específica gana al comodín) → si hay **BP** y TrqType, `IClsFuenteMargen` por (salesOrg, TrqType, BP) → `sap_tm`; sin BP no consulta SAP (ADR-004); sin BP, sin TrqType, sin registro o con la fuente no configurada → `politica_linea` con el motivo en `detalle`; sin política → `MARGEN_LINEA_DESCONOCIDA`; fuente configurada pero caída → `politica_linea` con el motivo "SAP TM no disponible" y un warning (decisión de #13; `MARGEN_FUENTE_NO_DISPONIBLE` solo si además la línea no tiene política). Devuelve además `LineaNegocioId`, `MargenPolitica` y `MetodoCalculo` para el estado (ADR-004) |
| `IClsAjusteMargenService` / `clsAjusteMargenService` → `clsSalidaAjusteMargen` | Aplicacion | Margen negociado: redondea a 4 decimales y valida por método (`AJUSTE_MARGEN_INVALIDO`, código nuevo agregado a `ErrorNegocio`; `PRECIO_INVALIDO` queda solo para el cálculo de precio), registra `MargenAjustes` con el comercial (`usuarioId`) y la cotización (debe ser suya; si no, `AJUSTE_COTIZACION_INVALIDA`), devuelve origen `negociado` y, si viene costo, el precio con el margen manual (sin recalcular la política) |
| `IClsFuenteMargen` (Strategy) / `clsOpcionesMargen` (`SapTm:SalesOrgDefault`, por defecto `50000005`) | Aplicacion | Puerto de SAP TM: devuelve el `Value` crudo (13.5 = 13,5 %) o `null`; el resolver lo divide por 100 una sola vez. Adaptadores `export_json` / `odata` en #13 (abajo) |
| `IClsTarifaProvider` | Aplicacion | Puerto de `buscar_tarifa` (el tarifario de Drive se retiró; el costeo viene de Nexo, HU-005) |
| `Margen/Services/clsFuenteMargenNoConfigurada` | Infraestructura | Fuente con `SapTm:Modo` vacío (valor por defecto): `EstaConfigurada = false` y devuelve `null` (no lanza); el resolver usa la política con el detalle "SAP TM no configurado: se usa la política de la línea" |
| `Tarifas/Services/clsTarifaProviderNoDisponible` | Infraestructura | Proveedor por defecto (`TryAdd`): `TARIFA_FUENTE_NO_DISPONIBLE`, "el costeo viene de Nexo (HU-005); mientras tanto el comercial ingresa el costo" |

Diferencias deliberadas con el Python: la línea se resuelve sin tildes ni espacios (la collation `CI_AS` distingue tildes); los textos de `detalle` son los del Python (`(cliente nuevo)`, `(sin registro en SAP TM)`, `Margen propuesto SAP TM para cliente X en OPTN: 13.5%`, con el `Value` tal como llega de SAP, igual que el `f"{value}%"` del Python, corregido en #13) salvo el caso sin BP (`cliente nuevo: sin BP de SAP, no se consulta SAP TM`, ADR-004) y los de SAP no configurado o no disponible; los márgenes del ajuste y su diferencia se redondean a 4 decimales (escala de `DECIMAL(9,4)`) y no a 6; el margen negociado se valida por método (el Python no validaba, código `AJUSTE_MARGEN_INVALIDO`) y un motivo vacío se registra como `Ajuste manual del comercial`; empates exactos de medio peso (ver arriba).

**Notas para el PR de #10 (pendientes, no decididos aquí):**
- `specs/openapi.yaml` (contrato de #53) cambia: se agrega `AJUSTE_MARGEN_INVALIDO` al enum de `ErrorNegocio`.
- HU-004.4 no tiene canal estructurado para el margen manual. Propuesta para #12: campo `margen_manual` + `motivo` en `MensajeWeb`, o `PUT /web/chat/{sesion_id}/margen`.
- La diferencia del ajuste se mide contra el margen sugerido; si el sugerido viene de SAP TM, ¿se compara contra la política de la línea o contra SAP?
- Un margen SAP negativo (anómalo) como referencia de una negociación hoy se rechaza por método: decidir.
- Líneas inactivas en `MargenesPolitica`: hoy se siguen usando (paridad con el Python); decidir con HU-010.
- Anomalía con `markup`: un margen > 0.6 se marca anómalo aunque con markup sea normal; decidir el umbral por método.
- Redondeo half-to-even sobre el valor exacto (diferencia máxima 1 peso con el Python): confirmar con negocio.
- HU-012: convertir a USD después de calcular el precio en COP.
- Riesgo de secuenciación HU-004 vs HU-005: sin Nexo no hay fuente de costeo (`TARIFA_FUENTE_NO_DISPONIBLE`, el comercial ingresa el costo).

**Fuentes de SAP TM (#13, HU-003.7).** Port de `mcp_server/margen/export_fuente.py`, `odata_fuente.py` y `construir_resolver`, con paridad verificada contra `test_export_*`, `test_odata_*`, `test_construir_resolver_*` y `test_conmutacion_export_a_odata_misma_salida` (§8). Detalle operativo en §3.

| Pieza | Capa | Qué hace |
|---|---|---|
| `Configuracion/clsOpcionesSapTm` + `clsValidadorOpcionesSapTm` | Infraestructura | Misma sección `SapTm` que `clsOpcionesMargen`: `Modo` (`export_json` \| `odata` \| vacío), `ExportPath`, `Url`, `SapClient` (opcional, 3 dígitos), `User`, `Pass` (`[clsSecreto(LargoMinimo = 8, Opcional = true)]`). Valida al arrancar según el modo |
| `Margen/Services/clsFabricaFuenteMargen` | Infraestructura | `construir_resolver`: registra `IClsFuenteMargen` (transitorio, `TryAdd`) y elige el adaptador por `SapTm:Modo` al resolver |
| `Margen/Services/clsFuenteMargenExportJson` | Infraestructura | `export_json`: indexa `d.results` por (SalesOrgId, TrqType, OrderPartyId); singleton con caché que se recarga al cambiar la fecha o el tamaño del archivo; archivo ausente o malformado → `clsFuenteMargenNoDisponibleException` |
| `Margen/Services/clsFuenteMargenOData` | Infraestructura | `odata`: cliente HTTP tipado (`IHttpClientFactory`, nombre `SapTm`) con `AddStandardResilienceHandler` (10 s en total, 4 s por intento, 2 reintentos ante 5xx/408/429/red/timeout), Basic auth y `$filter` escapado; sin resultados → `null`; error → `clsFuenteMargenNoDisponibleException` |
| `Margen/Services/clsAvisosArranqueSapTm` | Infraestructura | `IHostedService` con avisos que no impiden arrancar: `Production` con `SapTm:Modo` vacío ("todos los márgenes salen de la política de la línea") y `export_json` cuyo archivo todavía no existe (se recoge en caliente cuando aparece) |
| `Margen/Services/clsFormatoRentabSap` (interno) | Infraestructura | Lectura común de `{"d":{"results":[...]}}`: identificadores como texto, `Value` como texto (`"13.5"`, formato OData v2 de `Edm.Decimal`) o número JSON |

Diferencias deliberadas con el Python:
- **SAP caído → política de la línea (decisión de #13).** El Python respondía `MARGEN_FUENTE_NO_DISPONIBLE` sin respaldo. .NET sigue el Gherkin de negocio de HU-009 ("Resiliencia si SAP no responde": cae a la política sin romper el flujo de cotización). El detalle dice "SAP TM no disponible: se usa la política de la línea" y queda un warning en el log. `MARGEN_FUENTE_NO_DISPONIBLE` sigue en el contrato, pero solo sale si SAP falla y además la línea no tiene política.
- **Detalle con el `Value` crudo:** `…en OPTN: 13.5%`, como el `f"{value}%"` del Python (en #10 se imprimía con 2 decimales fijos, `13.50%`).
- **Inyección en el filtro OData corregida:** el Python armaba `$filter` con un f-string sin escapar (un BP `15927' or 1 eq 1` cambiaba la consulta). .NET escribe cada valor como literal OData, con la comilla simple duplicada, y codifica el filtro para URL (`Uri.EscapeDataString`), así un valor no puede cerrar el literal ni agregar parámetros.
- **Modo por defecto vacío** (fuente no configurada, política de la línea), no `export_json` con el archivo de muestra; un modo desconocido no arranca (el Python caía a `export_json`).
- **Caché del export:** el Python leía el archivo una vez por proceso. .NET lo relee cuando cambia (fecha o tamaño revisados en cada consulta), sin TTL ni `FileSystemWatcher`, y descarta la caché si el archivo desaparece o queda ilegible: nunca sirve un export viejo.
- **Resiliencia:** el Python hacía un solo intento de 10 s; .NET reintenta los fallos transitorios dentro del mismo límite de 10 s y distingue la cancelación del llamador (se propaga) de una caída de SAP.
- **Validación:** `odata` exige `Url`, `User` y `Pass` (el Python aceptaba OData sin autenticación); el `Value` de un export o de una respuesta debe ser numérico. Una fila malformada invalida todo el export (en el Python salía una excepción de pydantic no controlada).
- **`SapTm:SapClient`** (nuevo, opcional, 3 dígitos): agrega `&sap-client=NNN` a la consulta OData. El Python no lo soportaba.
- **Logs:** un fallo de SAP deja una sola línea de warning (motivo, TrqType y organización) sin traza; la excepción completa queda en Debug, y los eventos de Polly de esta tubería (reintentos, timeouts) bajan a Debug (`TelemetryOptions.SeverityProvider` filtrado por el pipeline `SapTm-standard`).
- Los mensajes no incluyen la ruta del export ni credenciales; la ruta va al log.

Diagramas C4 y detalle por módulo: se completa en HU-003.8 (#14).

## 3. Infraestructura

- Un solo `Dockerfile` multi-stage y un único servicio expuesto: `Cotifly.Api` (ADR-007, #27). Sin `docker-compose` **para ejecución local** (desviación de lo que pide HU-003.8, #14, registrada en ADR-007).
- Despliegue: Docker Swarm + Dokploy (HU-006). El manifiesto de Swarm (`infra/stack*.yml`, formato compose para `docker stack deploy`/Dokploy) sí existe y lo adapta al backend .NET HU-006.6 (#30).
- Nombre lógico de la imagen: **`cotifly-api`**. En local `cotifly-api:dev`; en el registry `${REGISTRY}/cotifly-api:<versión>` (la convención de tags la fija HU-006.4, #28).

**Imagen de contenedor (`Dockerfile`, #27):**

| Aspecto | Decisión |
|---|---|
| Etapa `build` | `mcr.microsoft.com/dotnet/sdk:10.0.401-noble` (tag fijo, `ARG SDK_VERSION`). Restore del proyecto Api en capa propia (solo `global.json`, `Directory.*.props`, `.editorconfig` y los `.csproj`), luego `dotnet publish` Release `--no-restore -r linux-x64 --self-contained false`, `UseAppHost=false`, sin `.pdb`, solo recursos `es`, sin `web.config`, `InformationalVersion=$VERSION` |
| Etapa `runtime` | `mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra` (tag fijo, `ARG ASPNET_VERSION`): Ubuntu 24.04 mínimo con ICU y tzdata, sin shell, sin `apt`, sin SDK |
| Metadatos | `ARG VERSION=dev`, `ARG VCS_REF=unknown`, `ARG BUILD_DATE` → etiquetas `org.opencontainers.image.{title,description,version,revision,created,source,vendor}`. Ej.: `--build-arg VERSION=1.0.0 --build-arg VCS_REF=$(git rev-parse --short HEAD) --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ)` |
| Puerto | Kestrel en **8080** (`ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`). No usar `ASPNETCORE_URLS` salvo necesidad; si se usa, la sonda lo respeta |
| Usuario | `app` (UID 1654, `$APP_UID`), no root. `/app` y sus binarios son de root (solo lectura para `app`) |
| Escritura | Solo `/data/pdfs` (dueño `app`), y solo aplica si los PDFs se guardan en modo volumen (HU-003.7 mantiene el volumen; S3/MinIO queda pendiente, ADR-012). Con varias réplicas un volumen local no se comparte entre nodos: lo decide HU-006.6 (ADR-012). No se declara `VOLUME` para no crear volúmenes anónimos en cada `docker run` |
| `HEALTHCHECK` | `dotnet /app/Cotifly.Api.dll --healthcheck` cada 30 s, timeout 5 s, 3 reintentos, `start-period` 20 s. `clsSondaSalud` hace `GET /health` y termina con 0/1 sin levantar el host web. Destino: primera URL `http://` de `ASPNETCORE_URLS` (comodines `+`, `*`, `0.0.0.0`, `[::]` → 127.0.0.1; `https://` se ignora), si no el primer puerto de `ASPNETCORE_HTTP_PORTS`, si no 8080; sin proxy |
| Arranque fallido | Cualquier excepción al construir o arrancar el host termina el contenedor con **código 1** y un solo registro del error (sin secretos), en vez del 139 de una excepción no controlada en el PID 1 |
| Zona y cultura | `Zona:ZonaHoraria=America/Bogota` se valida al arrancar (tzdata); `InvariantGlobalization=false` en `Cotifly.Api.csproj` para el formato es-CO (ICU). El contenedor y los logs quedan en UTC (sin `TZ`) |
| Fuera de la imagen | `tests/`, `specs/`, documentación, `bin/`/`obj/`, `.env` (`.dockerignore` es lista blanca: solo props, `global.json`, `.editorconfig` y `src/`) |

Por qué `chiseled-extra`: la variante `chiseled` sin `-extra` no trae ICU ni tzdata (la app no arrancaría: `America/Bogota` no existiría) y `aspnet:10.0` (Ubuntu completo) sí los trae pero pesa más y agrega shell y `apt`. Medido el 2026-09-24: imagen final **87,6 MB comprimida / ~304 MB en disco** (Docker Desktop), frente a 95,9 MB / 340 MB de la base `aspnet:10.0` sola y 70,7 MB comprimida de la base `chiseled-extra`.

- **Sin `.pdb`:** las trazas de excepción no traen número de línea. Alternativa a evaluar en HU-006.4 (#28): `DebugType=embedded` + `PathMap` (símbolos dentro del `.dll`, sin rutas del equipo de build).
- **Formato de moneda:** ICU da `3294798m.ToString("C0", es-CO)` = `$ 3.294.798` (con espacio). HU-003.5 (#11) usará un formateador propio (`NumberFormatInfo` clonado de es-CO con `CurrencyPositivePattern=0` y `CurrencyDecimalDigits=0`) para igualar el `$3.294.798` del Python. Las fuentes del PDF van embebidas y se cargan con `IFontResolver`: chiseled no trae fontconfig ni fuentes del sistema.

Las pruebas **no** corren dentro de `docker build`: el contexto no incluye `tests/` y las de integración necesitan el socket de Docker (Testcontainers), que no existe durante el build. Corren aparte (§6, §8, y en CI con #14).

**Actualizar los tags de .NET** (parche mensual): leer el último parche en las [release notes de .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/releases.json) o con `docker pull mcr.microsoft.com/dotnet/sdk:10.0` + `docker image inspect` (`DOTNET_SDK_VERSION`, `ASPNET_VERSION`); cambiar `SDK_VERSION` y `ASPNET_VERSION` juntos en el `Dockerfile` (el runtime de la imagen debe ser el que trae ese SDK); reconstruir, correr las pruebas y repetir la verificación de §6. `global.json` (`10.0.100` + `latestFeature`) acepta cualquier SDK 10.0.x, no hace falta tocarlo.

**Para HU-006.6 (#30), manifiestos de Swarm/Dokploy** (este issue no los toca). Hoy el Python expone `bot-orquestador` en **8000** (Traefik `loadbalancer.server.port=8000` en `infra/stack.dokploy.yml` e `infra/stack.staging.yml`; `deploy/docker-compose.swarm.yml` publica `8091 → 8000`) y `bot-mcp` en 8080 solo en la red interna. Con el backend .NET:
- Puerto **8080** en el `target` y en `traefik.http.services.<svc>.loadbalancer.server.port`.
- Si el stack redefine el healthcheck, en forma exec: `test: ["CMD", "dotnet", "/app/Cotifly.Api.dll", "--healthcheck"]`. Nunca `CMD-SHELL`: la imagen no tiene shell.
- Readiness con el healthcheck de Traefik contra `/health/ready`: saca la tarea del balanceo mientras la base no está lista, sin que Swarm la reinicie (el `HEALTHCHECK` de la imagen es solo de vida, `/health`).
- Memoria: cada sonda levanta un proceso `dotnet` aparte (~40–70 MB transitorios cada 30 s); contarlo en `resources.limits.memory`.
- Retirar `bot-mcp` (sus herramientas viven en el monolito, ADR-002).
- Variables: `ConnectionStrings__Cotifly` y `ConnectionStrings__Identidad` (HU-007 debe usar estos nombres, no `SQLSERVER_CONNECTION_STRING`).
- SAP TM: `SapTm__Modo`, `SapTm__ExportPath` (y el montaje de solo lectura del export) o `SapTm__Url`/`SapTm__User`/`SapTm__Pass` (secreto), en lugar de `SAP_TM_*` del Python (ver "Integraciones externas").
- Logs en UTC.

**Base de datos (ADR-008, #9).** SQL Server 2019 corporativo (`tbsdatacenter`), esquema `dbo_cotifly` dentro de `dbTarifario_Nexo_Pruebas`. EF Core **Database First**: `clsCotiflyDbContext` mapea a mano (Fluent API, `Comun/Persistence/Configurations/`) las tablas que crean los scripts; `HasDefaultSchema("dbo_cotifly")`, tablas PascalCase y columnas camelCase explícitas con su tipo SQL, `UseCompatibilityLevel(150)`. **No hay migraciones EF**: el esquema cambia solo con scripts `../migrations_mssql/000N_*.sql` idempotentes:

| Script | Contenido | Estado |
|---|---|---|
| `0001_script_bd_cotifly_sqlserver.sql` | 10 tablas, vistas y seed (HU-002.4) | aplicado en `tbsdatacenter` |
| `0002_identidad_estado_agente.sql` | `Sesiones.estadoAgente` (JSON) y `Sesiones.usuarioId`; `RolesMapeo`, `TokensRefresco`, `TokensRevocados`; índices de consulta | **extensión aditiva pendiente de aprobación** (Sara + scalderon-bs). Paso de un solo sentido: después del 0002 **no** se re-ejecuta el 0001 (ver encabezado del 0002 y ADR-008) |

- `updatedAt` (y `createdAt` si la entidad no lo trae) los fija `clsMarcasTiempoInterceptor` con `IClsReloj`: el esquema no tiene triggers.
- `Sesiones.estadoAgente` guarda `clsEstadoConversacion` como JSON camelCase (`clsConvertidorEstadoConversacion`).
- `clsIdentidadDbContext` (solo lectura, `NoTracking`, `SaveChanges` lanza) lee `tblSAPUsuariosApp` de `dbsolucionesti`. El esquema y la tabla son configurables (`Identidad:Esquema`, por defecto `dbo`; `Identidad:Tabla`, por defecto `tblSAPUsuariosApp`) mientras el DBA confirma la ubicación real.
- `DWCubosEBSSAP` (`ConnectionStrings:Regional`, regional del comercial) llega con #12.

**Cadenas de conexión** (`clsOpcionesConexiones`, variables en `.env.example`):

| Clave | Uso | Variable |
|---|---|---|
| `ConnectionStrings:Cotifly` | `dbo_cotifly`, lectura y escritura | `ConnectionStrings__Cotifly` |
| `ConnectionStrings:Identidad` | `dbsolucionesti`, solo lectura | `ConnectionStrings__Identidad` |

Siempre SQL login (`cotifly_app`, pendiente con el DBA): el contenedor Linux no admite autenticación integrada. En `Production` la API no arranca si una cadena falta, es un placeholder, usa `Trusted_Connection`/`Integrated Security` o su `Password` es de ejemplo. Fuera de `Production` pueden quedar vacías: la API arranca y `GET /health/ready` responde 503.

**Integraciones externas (#13, HU-003.7).**

| Integración del Python | En .NET | Configuración |
|---|---|---|
| SAP TM, export JSON (`export_fuente.py`) | `clsFuenteMargenExportJson` | `SapTm__Modo=export_json`, `SapTm__ExportPath` |
| SAP TM, OData en vivo (`odata_fuente.py`) | `clsFuenteMargenOData` | `SapTm__Modo=odata`, `SapTm__Url`, `SapTm__User`, `SapTm__Pass` |
| Tarifario en Google Drive (`DriveFlatFileProvider`) | **No se porta**: retirado el 2026-09-23, el costeo viene de Nexo (HU-005) | ninguna |
| Sincronización a Google Sheets (Looker Studio) | **Diferida**, pendiente en ADR-012 (Looker directo a SQL Server, port a .NET o tablero de HU-010) | ninguna |
| PDF en S3/MinIO (`almacen_pdf.py`) | **Diferido**: se mantiene el volumen `/data/pdfs` detrás de `IClsAlmacenPdf` (#11). S3 solo si HU-006.6 despliega varias réplicas sin volumen compartido (ADR-012) | ninguna |

SAP TM:
- **Sin `SapTm__Modo`** (valor por defecto) no se consulta SAP: `obtener_margen` usa la política de la línea con el detalle "SAP TM no configurado".
- **`export_json`**: el archivo tiene la forma `{"d":{"results":[{SalesOrgId, TrqType, OrderPartyId, Value}]}}` (referencia `datos/sap_export.json` del Python; en las pruebas, `tests/fixtures/sap/rentab_cliente_muestra.json`). Se monta en el contenedor (por ejemplo `-v <carpeta>:/data/sap:ro` y `SapTm__ExportPath=/data/sap/sap_export.json`). Para reemplazarlo sin que una consulta lea un archivo a medio escribir, copiarlo con otro nombre y renombrarlo; se ve en la siguiente consulta, sin reiniciar.
- **`odata`**: `GET {SapTm__Url}/RentabClienteSet?$filter=SalesOrgId eq '…' and TrqType eq '…' and OrderPartyId eq '…'&$format=json` con autenticación Basic y `Accept: application/json`. Se toma `d.results[0].Value`. `SapTm__Url` es la URL base del servicio (ej. `https://<host>/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV`), http o https, sin usuario ni `?consulta`. Si el servicio exige mandante, `SapTm__SapClient=100` (3 dígitos) agrega `&sap-client=100`. `SapTm__User` y `SapTm__Pass` son obligatorios en todo entorno; en `Production` la clave no puede ser un placeholder ni tener menos de 8 caracteres. Las credenciales nunca se registran: la cabecera se arma por petición y los logs de `IHttpClientFactory` ocultan todas las cabeceras.
- **SAP caído** (export ausente o ilegible, HTTP ≠ 2xx tras los reintentos, más de 10 s, respuesta inválida) → política de la línea con el detalle "SAP TM no disponible: se usa la política de la línea" y un warning en el log (decisión de #13, Gherkin "Resiliencia si SAP no responde" de HU-009). `MARGEN_FUENTE_NO_DISPONIBLE` solo si la línea tampoco tiene política.
- **Avisos al arrancar** (no impiden arrancar): en `Production` sin `SapTm__Modo`, "SAP TM no configurado (SapTm:Modo vacío): todos los márgenes salen de la política de la línea"; con `export_json` y un archivo que no existe, aviso con la ruta.
- **No dejar `SapTm__Pass` puesto si no se usa OData:** es opcional sin `odata`, pero si tiene un valor de ejemplo (`<secreto>`) o corto, `Production` no arranca.
- **Activar OData en vivo (HU-009):** el código ya está (la HU pide el cliente completo, no solo la interfaz). Falta lo externo, HU-009.1: ruta de red desde el contenedor de Dokploy hasta SAP y el usuario de servicio. Con eso, HU-009.2 cambia en Dokploy `SapTm__Modo=odata`, `SapTm__Url`, `SapTm__User` y `SapTm__Pass` (secreto), sin tocar código. Se valida con al menos 3 combinaciones reales de cliente y línea antes de dejarlo en producción; la prueba `clsSapTmRealTests` (`[Trait("Categoria","sap")]`, se omite si faltan las variables) lee `SapTm__Url`/`User`/`Pass`/`SapClient` y un caso conocido en `SAPTM_PRUEBA_TRQTYPE`, `SAPTM_PRUEBA_BP` y opcional `SAPTM_PRUEBA_VALUE` (`dotnet test --filter-trait "Categoria=sap"`).
- **Criterio para HU-009.1, formato de `OrderPartyId`:** SAP suele guardar el BP con ceros a la izquierda (conversión ALPHA, ej. `0000015927`), y el BP que entrega Nexo puede venir sin ellos (`15927`). Confirmar con SAP el formato exacto contra un caso real antes de activar. **No normalizar a ciegas** (rellenar o recortar ceros sin confirmarlo puede cruzar clientes). En HU-009.2, vigilar la tasa de respuestas "sin registro en SAP TM": si es alta, lo más probable es un desajuste de formato del BP, no clientes sin margen.

Se completa en HU-003.8 (#14).

## 4. Flujo del sistema

Se completa en HU-003.8 (#14).

## 5. Stack

- .NET 10 / ASP.NET Core 10, C# `latest`, `Nullable` y `TreatWarningsAsErrors` activos, `AnalysisLevel` fijo en `10.0` (`Directory.Build.props`).
- Configuración: `Microsoft.Extensions.Options` con DataAnnotations y `ValidateOnStart`.
- Central Package Management con versiones fijas (`Directory.Packages.props`).
- Persistencia: EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`), health checks (`Microsoft.Extensions.Diagnostics.HealthChecks`).
- Pruebas: xUnit v3 sobre Microsoft.Testing.Platform, AwesomeAssertions (Apache-2.0), ArchUnitNET, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.OpenApi` + `Microsoft.OpenApi.YamlReader`, Testcontainers.MsSql (SQL Server 2019), Reqnroll (`Reqnroll.xunit.v3`).
- Dobles de prueba: NSubstitute (BSD-3-Clause), desde #10.
- HTTP saliente (#13): `Microsoft.Extensions.Http` (`IHttpClientFactory`, clientes tipados) y `Microsoft.Extensions.Http.Resilience` 10.10.0 (MIT, sobre Polly 8, BSD-3-Clause) para SAP TM OData; `Microsoft.Extensions.Logging.Abstractions` en Aplicacion (warning de SAP caído). Las pruebas usan un `HttpMessageHandler` falso (`tests/Comun/clsSapTmFalso`), sin red.
- Planeado (se agrega con cada HU): Microsoft.Extensions.AI + Semantic Kernel, PDFsharp-MigraDoc, MailKit.

## 6. Ejecución local

La máquina de desarrollo solo tiene SDK .NET 8, así que todo se compila y prueba dentro de la imagen oficial del SDK 10 (ADR-007). En Windows, desde PowerShell (las rutas con espacios se rompen en Git Bash):

```powershell
docker run --rm `
  -v //var/run/docker.sock:/var/run/docker.sock `
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
  -e REQNROLL_TELEMETRY_ENABLED=0 `
  -v "<ruta>\Cotifly:/repo" `
  -v cotifly-nuget:/root/.nuget/packages `
  -w /repo/backend-dotnet mcr.microsoft.com/dotnet/sdk:10.0 `
  sh -c "dotnet build -c Release && dotnet test -c Release --no-build"
```

- Se monta el repo completo (no solo `backend-dotnet/`) porque las pruebas copian `../migrations_mssql/*.sql` para aplicarlas en Testcontainers.
- El socket de Docker y `TESTCONTAINERS_HOST_OVERRIDE` permiten que las pruebas de integración levanten SQL Server 2019 como contenedor hermano (detalle en §8).
- El volumen `cotifly-nuget` conserva la caché de NuGet entre ejecuciones. Las carpetas `bin/` y `obj/` que crea el contenedor están en `.gitignore`.

**Construir y correr la imagen** (#27; desde la raíz del repo, PowerShell):

```powershell
docker build -t cotifly-api:dev -f backend-dotnet/Dockerfile backend-dotnet

# Development: arranca sin cadenas de conexión; /health 200 y /health/ready 503
docker run -d --name cotifly-api -p 8080:8080 -e ASPNETCORE_ENVIRONMENT=Development cotifly-api:dev
curl.exe http://localhost:8080/health          # {"status":"ok","servicio":"cotifly-api","entorno":"Development"}
curl.exe http://localhost:8080/health/ready    # 503 {"status":"unavailable","detalle":"La cadena de conexión ..."}
docker inspect --format '{{.State.Health.Status}}' cotifly-api   # healthy (primer chequeo a los pocos segundos)
docker rm -f cotifly-api

# Con variables reales (.env a partir de .env.example) y volumen para los PDFs
docker run --rm -p 8080:8080 --env-file backend-dotnet/.env -v cotifly_pdfs:/data/pdfs cotifly-api:dev
```

- Variables: las de `.env.example`. La imagen ya fija `ASPNETCORE_HTTP_PORTS=8080`; `ASPNETCORE_ENVIRONMENT` no tiene valor en la imagen, así que sin la variable el entorno es `Production`.
- Todos los fallos de arranque terminan el contenedor con **código 1** y registran el error una sola vez, sin secretos. Ejemplos: en `Production` sin secretos o con placeholders, log `Hosting failed to start` que nombra la clave (`ConnectionStrings:Cotifly`...) y nunca el valor; con `-e ASPNETCORE_HTTP_PORTS=99999`, el mismo log con `ArgumentOutOfRangeException (Parameter 'port')`. Si falla antes de que exista el logger (`builder.Build()`), una línea `crit:` en stderr. `Program.cs` lo hace solo cuando `Cotifly.Api` es el proceso: sin eso, la excepción no controlada en el PID 1 del contenedor termina con 139 y repite la traza; bajo `WebApplicationFactory` la excepción se sigue propagando a las pruebas.
- La sonda del `HEALTHCHECK` se puede correr a mano: `docker exec cotifly-api dotnet /app/Cotifly.Api.dll --healthcheck` (imprime `sonda /health: 200` y sale con 0).
- Con otro puerto (`-e ASPNETCORE_HTTP_PORTS=9090`) la sonda usa el primero de esa lista; con `ASPNETCORE_URLS` (p. ej. `http://+:9090`), la primera URL `http://`. Preferir `ASPNETCORE_HTTP_PORTS`.
- Versión y metadatos: `docker build --build-arg VERSION=... --build-arg VCS_REF=... --build-arg BUILD_DATE=...`; se leen con `docker image inspect --format '{{json .Config.Labels}}' cotifly-api:dev`.

**Depurar sin shell** (la imagen no tiene `sh`, `bash` ni `curl`; `docker exec ... sh` no funciona):

1. `docker debug <contenedor>` (Docker Desktop/Engine con licencia que lo incluya): shell temporal con herramientas, sin tocar la imagen.
2. Contenedor auxiliar que comparte PID y red con el de la API:
   ```powershell
   docker run --rm -it --pid=container:<id> --network=container:<id> mcr.microsoft.com/dotnet/sdk:10.0.401-noble bash
   # dentro: dotnet tool install -g dotnet-counters; dotnet tool install -g dotnet-dump
   #         export PATH="$PATH:/root/.dotnet/tools"
   #         export TMPDIR=/proc/1/root/tmp   # el socket de diagnóstico vive en el /tmp del contenedor de la API
   #         dotnet-counters monitor -p 1   |   dotnet-dump collect -p 1   |   curl http://127.0.0.1:8080/health
   ```
   El PID 1 es el `dotnet` de la API. Procedimiento documentado, no ensayado aún contra esta imagen.
3. Build de emergencia con runtime Ubuntu completo (con shell), solo local y con sufijo `-debug`, **nunca** por el pipeline de producción:
   `docker build --build-arg ASPNET_VERSION=10.0.12-noble -t cotifly-api:<versión>-debug -f backend-dotnet/Dockerfile backend-dotnet`.

**Correr la API desde el código** (sin construir la imagen):

```powershell
docker run --rm -p 8080:8080 `
  -v "<ruta>\Cotifly\backend-dotnet:/src" -v cotifly-nuget:/root/.nuget/packages `
  -w /src -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_HTTP_PORTS=8080 `
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --project src/Cotifly.Api
# en otra terminal
curl http://localhost:8080/health
```

**Variables de entorno:** se documentan en `.env.example` (copiar a `.env`, que no se versiona). Ningún secreto va en `appsettings*.json` ni en el código.

**Configuración validada al arrancar** (`src/Cotifly.Infraestructura/Configuracion/`): cada sección se enlaza a una clase `clsOpcionesXxx` con DataAnnotations y se registra con `AddOpcionesValidadas<T>()`, que agrega `ValidateOnStart` y el validador de secretos. En `Production`, una propiedad marcada con `[clsSecreto]` vacía, con placeholder (`<secreto>`, `cambiar…`, `changeme`, `dev`) o más corta que su mínimo impide arrancar; el mensaje nombra la clave, nunca el valor. Opciones actuales: `Zona:ZonaHoraria` (por defecto `America/Bogota`, debe existir en el sistema), `ConnectionStrings:Cotifly` / `ConnectionStrings:Identidad` (`[clsSecreto]` + `clsValidadorOpcionesConexiones`, ver §3), `Identidad:Esquema` / `Identidad:Tabla` y `SapTm:SalesOrgDefault` (`clsOpcionesMargen`, definida en Aplicacion porque la consume el resolver de margen; por defecto `50000005`, obligatoria y de máximo 12 caracteres, #10) y `SapTm:Modo` / `ExportPath` / `Url` / `User` / `Pass` (`clsOpcionesSapTm` + `clsValidadorOpcionesSapTm`, #13: modo desconocido, `export_json` sin ruta u `odata` sin URL, usuario o clave impiden arrancar en cualquier entorno; `Pass` lleva `[clsSecreto(Opcional = true)]`, que en `Production` solo revisa el valor cuando existe, porque sin `odata` no hace falta clave; ver §3). #12 agrega las suyas siguiendo los pasos documentados en `clsRegistroOpciones`.

Levantar la API contra un SQL Server local de pruebas: pasar `-e ConnectionStrings__Cotifly="Server=host.docker.internal,1433;Database=dbTarifario_Nexo_Pruebas;User Id=...;Password=...;TrustServerCertificate=True"` al `docker run` anterior y consultar `curl http://localhost:8080/health/ready`.

**Abrir la solución en un IDE:**
- Requiere el SDK .NET 10 en el host. Se puede instalar por usuario, sin permisos de administrador, con el script oficial `dotnet-install` (`dotnet-install.ps1 -Channel 10.0 -InstallDir $env:LOCALAPPDATA\dotnet`).
- IDE: Visual Studio 2022 17.14 o posterior, Visual Studio 2026 o una versión reciente de Rider.
- Si alternas entre compilar en el host y en el contenedor, borra antes las carpetas `bin/` y `obj/`: las rutas del restore (`project.assets.json`) no son compatibles entre ambos.

## 7. API

El contrato congelado es `specs/openapi.yaml` (#17), fuente de verdad (SDD, ADR-010). Hoy está implementado:

| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/health` | `200 {"status":"ok","servicio":"cotifly-api","entorno":"<ASPNETCORE_ENVIRONMENT>"}` |
| GET | `/health/ready` | `200 {"status":"ready"}` si `dbo_cotifly` acepta conexiones y tiene el 0002 aplicado; `503 {"status":"unavailable","detalle":"..."}` si no (sin cadena configurada, base caída o `esquema incompleto: falta 0002`) |

`/health/ready` ejecuta los health checks con etiqueta `ready` (hoy `clsBaseDatosHealthCheck`: `CanConnectAsync` sobre `clsCotiflyDbContext` más verificación de `Sesiones.estadoAgente` y `TokensRefresco`, con un límite de 5 s para toda la sonda). `detalle` nunca incluye la cadena ni el servidor; la excepción queda en el log. Ninguno de los dos exige autenticación. El `HEALTHCHECK` de la imagen (#27) consulta `/health` (vida), no `/health/ready`: una base caída no debe hacer que Swarm reinicie el contenedor.

El resto de endpoints: se completa en HU-003.8 (#14).

## 8. Testing

| Proyecto | Qué cubre hoy | Unitarias | Integración |
|---|---|---|---|
| `Cotifly.Dominio.Tests` | `clsResultado<T>`, `clsErrorDominio`, `clsCodigoEnum` (códigos de los enums) e invariantes de las entidades (cotización, correo, margen, sesión, identidad, tokens), regla de margen por método (`margen` < 1, `markup` ≥ 1, también en `clsMargenAjuste`). **#10:** `clsCalculadoraPrecio` (todos los casos de `test_precio.py`, grilla determinista en lugar del property test, puntos medios half-to-even, desbordamiento) y `clsReglasMargen` (normalización, anomalía, líneas sin tildes ni espacios), redondeo del ajuste a 4 decimales | 288 | — |
| `Cotifly.Aplicacion.Tests` | Registro de dependencias de Aplicación; `clsPrecioService`; `clsMargenResolverService` y `clsAjusteMargenService` con puertos simulados (NSubstitute) y la muestra SAP del Python (`tests/Comun/clsFuenteMargenMuestra`), SAP no configurado → política, `AJUSTE_MARGEN_INVALIDO`. **#13:** SAP caído → política con warning (logger falso), sin política y SAP caído → `MARGEN_FUENTE_NO_DISPONIBLE`, detalle con el `Value` crudo (`5%`, `9.75%`, `-19.2%`) | 53 | — |
| `Cotifly.Infraestructura.Tests` | `clsRelojSistema`, registro de dependencias (puertos, contextos), validador de secretos, opciones `Zona` y `ConnectionStrings`, convertidores, traducción de violaciones de unicidad. **Integración:** scripts 0001+0002 aplicados (y 0002 idempotente), deriva modelo EF ↔ catálogo en ambos sentidos (tablas, columnas, tipos, nulabilidad, collation, nombres y columnas de PK/FK, índices únicos/filtrados/`ix_` con orden e `INCLUDE`, CHECK), idempotencia del 0002 con foto completa del esquema, cada método de cada repositorio, aislamiento por dueño, folio duplicado → `FOLIO_DUPLICADO`, dedupe de correo con reloj falso, interceptor de `updatedAt`, PK/FK compuestas, CHECK JSON, contexto de identidad de solo lectura, health check (incluye base con solo el 0001 → esquema incompleto, y límite de tiempo de la sonda). **#10:** fuente de margen no configurada, proveedor de tarifas no disponible, registro por defecto (`TryAdd`) y opción `SapTm:SalesOrgDefault`; **integración:** resolver y ajuste negociado con el registro real sobre la semilla del 0001 (todos los `requiere_pg` de `test_margen.py`, las 8 líneas, líneas con tildes, ajuste `markup` ≥ 1, cotización ajena, SAP no configurado con el registro por defecto). **#13:** `clsFuenteMargenExportJson` (encuentra y no encuentra, las 10 filas de la muestra con el `Value` crudo, archivo ausente, 8 formas de export malformado, `Value` numérico, filas repetidas, caché que no relee sin cambios y recarga al cambiar, recuperación tras archivo roto o borrado, concurrencia), `clsFuenteMargenOData` contra `clsSapTmFalso` (URL exacta, `Accept`, Basic auth, barra final, vacío → null, primera fila, `Value` numérico, inyección de comillas en BP, organización y TrqType, `&` y `#` codificados, HTTP 401/404/500/503, timeout, red, cancelación del llamador, 7 respuestas inválidas, mensajes sin credenciales), selección por `SapTm:Modo` y validación de `clsOpcionesSapTm` (modos, obligatorios, URL, clave solo con espacios, `SapClient` de 3 dígitos, clave en Production), `sap-client` en la URL exacta, `[clsSecreto(Opcional)]`, tubería de resiliencia (reintenta 503, agota reintentos con 500, no reintenta 401), logs (una línea de warning sin traza, excepción en Debug, eventos de Polly en Debug), avisos de arranque (`clsAvisosArranqueSapTm`); `clsSapTmRealTests` (`Categoria=sap`, se omite sin variables: 1 omitida); **integración:** resolver con `export_json` y `odata` reales sobre la semilla (SAP TM, sin registro, export ausente y OData caído → política, sin BP no llama a SAP) y conmutación export → OData con la misma salida para 13 consultas | 180 (1 omitida) | 105 |
| `Cotifly.Api.Tests` | `GET /health`, `GET /health/ready` (503 sin cadena y con base inalcanzable; con SQL Server real: **200**, y 503 `esquema incompleto: falta 0002` en una base con solo el 0001), composition root, arranque con configuración inválida, contrato `specs/openapi.yaml` (`/health` y `/health/ready` declarados, respuestas reales cumplen el esquema), sonda del `HEALTHCHECK` (`clsSondaSalud`: argumento `--healthcheck`, puerto desde `ASPNETCORE_HTTP_PORTS`, 200 → 0, otro estado / sin conexión / tiempo agotado → 1, prioridad `ASPNETCORE_URLS` → `ASPNETCORE_HTTP_PORTS` → 8080 con comodines e https ignorado, sin proxy, contra la API en memoria). **#10:** `SalidaPrecio`/`SalidaMargen` en snake_case cumplen el esquema y cada `clsCodigosError` existe en `ErrorNegocio` | 65 | 2 |
| `Cotifly.Arquitectura.Tests` | Dependencias entre capas (ArchUnitNET + referencias de ensamblado), Dominio sin paquetes externos y prefijos `cls`/`ICls` | 14 | — |
| `Cotifly.Aceptacion.Tests` | Reqnroll: `persistencia.feature` (guardar/recuperar, folio único, aislamiento por comercial, `updatedAt`), `precio.feature` (valores de la arquitectura, markup vs margen, half-to-even, rechazos) y `margen.feature` (SAP TM, sin BP, sin registro, anomalía, organización de ventas, tildes, línea desconocida, SAP caído → política, SAP no configurado, margen negociado; empates de medio peso documentados; **#13:** misma salida con los adaptadores reales `export_json` y `odata`, BP con comillas que no altera el filtro OData) | — | 37 |

- TDD: test en rojo, implementación, refactor (ADR-010).
- **Paridad con el Python (#10, #13).** Cada caso de `tests/test_precio.py` y `tests/test_margen.py` tiene su prueba .NET (el comentario sobre cada prueba nombra el test de origen):

| Test Python | Prueba .NET |
|---|---|
| `test_valores_exactos_arquitectura` | `clsCalculadoraPrecioTests.ValoresExactosDeLaArquitectura`; `precio.feature` |
| `test_markup_vs_margen` | `clsCalculadoraPrecioTests.MarkupRindeMenosPrecioQueMargen`; `precio.feature` |
| `test_borde_margen_cero` | `clsCalculadoraPrecioTests.ConMargenCeroElPrecioEsElCosto`; `precio.feature` |
| `test_borde_margen_uno_division_por_cero` | `clsCalculadoraPrecioTests.ConMetodoMargenUnMargenDeUnoSeRechaza`; `precio.feature` |
| `test_markup_admite_margen_alto` | `clsCalculadoraPrecioTests.MarkupAdmiteUnMargenAlto`; `precio.feature` |
| `test_rechaza_margen_negativo` | `clsCalculadoraPrecioTests.RechazaMargenNegativo`; `precio.feature` |
| `test_rechaza_costo_no_positivo` | `clsCalculadoraPrecioTests.RechazaCostoNoPositivo`; `precio.feature` |
| `test_rechaza_metodo_desconocido` | `clsCalculadoraPrecioTests.RechazaMetodoDesconocido`; `precio.feature` |
| `test_property_precio_no_menor_que_costo` (hypothesis) | `clsCalculadoraPrecioTests.ElPrecioNuncaEsMenorQueElCostoYLaUtilidadEsLaDiferencia` (grilla determinista de 112 casos, sin FsCheck) |
| `test_normalizar_value` | `clsReglasMargenTests.NormalizaElValueDeSapAFraccionConSeisDecimales` |
| `test_es_anomalo` | `clsReglasMargenTests.MarcaAnomaloFueraDeCeroASesentaPorCiento` |
| `test_resolver_sap_tm` | `clsMargenResolverServiceTests.ConBpYRegistroEnSapElOrigenEsSapTm`; `clsMargenResolverIntegracionTests` (mismo nombre); `margen.feature` |
| `test_resolver_fallback_cliente_nuevo` | `clsMargenResolverServiceTests.SinBpNoSeConsultaSapYSeUsaLaPoliticaDeLaLinea`; `clsMargenResolverIntegracionTests.SinBpSeUsaLaPoliticaSembrada`; `margen.feature` |
| `test_resolver_fallback_cliente_sin_registro` | `ConBpSinRegistroEnSapSeUsaLaPolitica` (unitaria e integración); `margen.feature` |
| `test_resolver_marca_anomalo_negativo` | `UnMargenSapNegativoSeMarcaAnomalo` (unitaria e integración); `margen.feature` |
| `test_resolver_marca_anomalo_atipico` | `UnMargenSapAtipicoSeMarcaAnomalo` (unitaria e integración); `margen.feature` |
| `test_resolver_respeta_sales_org` | `SeRespetaLaOrganizacionDeVentasPedida` (unitaria e integración); `margen.feature` |
| `test_resolver_linea_desconocida` | `UnaLineaDesconocidaDevuelveMargenLineaDesconocida` (unitaria e integración); `margen.feature` |
| `test_mapeo_todas_las_lineas` | `clsMargenResolverIntegracionTests.CadaLineaSembradaTieneSuTrqTypeYSuPolitica` |
| `test_export_encuentra_y_no_encuentra` | `clsFuenteMargenExportJsonTests.EncuentraElValueDelClienteYDevuelveNullSiNoExiste` (+ `DevuelveElValueCrudoDeCadaFilaSinDividirPorCien` para las 10 filas); `clsMargenFuentesSapIntegracionTests.ConExportJsonUnClienteConRegistroObtieneElMargenDeSap` |
| `test_export_archivo_ausente` | `clsFuenteMargenExportJsonTests.UnArchivoAusenteEsFuenteNoDisponibleSinExponerLaRuta`; `clsMargenFuentesSapIntegracionTests.ConExportJsonAusenteElResolverUsaLaPoliticaConElMotivo` |
| `test_export_json_malformado` | `clsFuenteMargenExportJsonTests.UnExportMalformadoEsFuenteNoDisponible` (8 casos: JSON roto, sin `d.results`, fila sin `Value`, `Value` no numérico o nulo, id numérico) |
| `test_odata_encuentra` | `clsFuenteMargenODataTests.EncuentraElValueConLaConsultaExactaDeRentabClienteSet` (URL exacta, filtro y `Accept`) |
| `test_odata_cliente_inyectado` | `clsFuenteMargenODataTests.EnviaAutenticacionBasicConUsuarioYClave` (el cliente siempre se inyecta: `IHttpClientFactory`) |
| `test_odata_vacio_devuelve_none` | `clsFuenteMargenODataTests.SinResultadosDevuelveNull` |
| `test_odata_error_500_es_estructurado` | `clsFuenteMargenODataTests.UnErrorHttpEsFuenteNoDisponibleSinExponerCredenciales` (500, 503, 404, 401); `clsRegistroFuenteMargenTests.ElClienteODataAgotaLosReintentosYReportaFuenteNoDisponible`; `clsMargenFuentesSapIntegracionTests.ConODataCaidoElResolverUsaLaPoliticaSinRomperLaCotizacion` |
| `test_odata_timeout_es_estructurado` | `clsFuenteMargenODataTests.UnTimeoutEsFuenteNoDisponible` (+ `UnErrorDeRedEsFuenteNoDisponible`, `LaCancelacionDelLlamadorNoSeDisfrazaDeCaidaDeSap`) |
| `test_construir_resolver_export` | `clsRegistroFuenteMargenTests.ConModoExportJsonLaFuenteEsElExport` |
| `test_construir_resolver_odata` | `clsRegistroFuenteMargenTests.ConModoODataLaFuenteEsElClienteOData` |
| `test_conmutacion_export_a_odata_misma_salida` | `clsMargenFuentesSapIntegracionTests.ConmutarDeExportJsonAODataDaLaMismaSalida` (13 consultas: las 10 filas, organización equivocada, sin registro, sin BP); `margen.feature` |
| (sin equivalente en Python: inyección en el `$filter`, corregida) | `clsFuenteMargenODataTests.EscapaLasComillasDelBpParaQueNoAltereElFiltro`, `CodificaLaUrlParaQueElBpNoAgregueParametros`, `EscapaTambienLaOrganizacionYElTrqType`; `margen.feature` |
| (sin equivalente en Python: caché del export, validación de `SapTm`, resiliencia) | `clsFuenteMargenExportJsonTests.NoReleeElArchivoMientrasNoCambieSuFechaNiSuTamano`, `RecargaElArchivoCuandoCambia`, `SiElArchivoDesapareceODejaDeSerLegibleDespuesDeCargarLoReporta`; `clsRegistroFuenteMargenTests` (modos y validación; `ElClienteODataReintentaUnErrorTransitorio`) |
| (sin equivalente en Python: SAP no configurado → política) | `clsMargenResolverServiceTests.ConLaFuenteNoConfiguradaSeUsaLaPoliticaSinError`; `clsMargenSapNoConfiguradoIntegracionTests`; `margen.feature` |
| `registrar_ajuste_margen` (`server.py`, sin test propio) | `clsAjusteMargenServiceTests`; `clsMargenResolverIntegracionTests.ElAjusteNegociadoSePersisteConElComercialYLaCotizacion`; `margen.feature` |
- **BDD (Reqnroll):** los `.feature` viven en `specs/features/` (fuente de verdad SDD) y `Cotifly.Aceptacion.Tests` los enlaza; el code-behind se genera en `obj/`. Se usa `Reqnroll.xunit.v3` (Reqnroll 3.x soporta xUnit v3 y corre sobre MTP como el resto), así que no hace falta MSTest. Los pasos usan el `AddInfraestructura` real contra SQL Server en Testcontainers.
- **Integración (Testcontainers.MsSql):** imagen `mcr.microsoft.com/mssql/server:2019-latest`. `tests/Comun/clsBaseDatosPruebas` crea `dbTarifario_Nexo_Pruebas`, aplica `0001` y `0002` partidos por líneas `GO` (como sqlcmd) y crea una `dbsolucionesti` mínima con `dbo.tblSAPUsuariosApp`. Un contenedor por proyecto de pruebas (colección xUnit `SqlServer`), que solo se crea si corre alguna prueba de integración. Cada prueba usa datos propios (folios y usuarios únicos). Las cadenas de prueba llevan `Command Timeout=120` (`clsBaseDatosPruebas.TiempoMaximoOrdenSeg`): el SQL Server del contenedor corre sobre el disco virtual de Docker Desktop (WSL2) y a veces se detiene en E/S más de los 30 s por defecto (visto en #10: `CREATE DATABASE` del fixture y consultas sueltas con "Execution Timeout Expired (258)", en pruebas distintas y sin relación con los datos). La API conserva el valor por defecto.
- Los proyectos con integración declaran `<UsaSqlServerPruebas>true</UsaSqlServerPruebas>`: `tests/Directory.Build.props` agrega Testcontainers, el soporte compartido de `tests/Comun/` (contenedor, reloj falso, DI de pruebas) y copia `../migrations_mssql/*.sql` a la salida.
- Las pruebas de integración y de aceptación llevan `[Trait("Categoria","Integracion")]` (en aceptación, a nivel de ensamblado).
- `global.json` activa Microsoft.Testing.Platform (MTP) para `dotnet test` (obligatorio con xUnit v3 en el SDK 10).
- Filtros con opciones de MTP: `dotnet test --filter-not-trait "Categoria=Integracion" --ignore-exit-code 8` (sin integración: no necesita Docker) o `--filter-trait "Categoria=Integracion" --ignore-exit-code 8` (solo integración). `--ignore-exit-code 8` evita que falle la corrida por los proyectos que quedan con cero pruebas tras el filtro. Por clase o método: `--filter-class` / `--filter-method`.
- Auditoría NuGet: las advertencias de vulnerabilidades (NU1900–NU1904) siguen visibles pero no rompen el build local ni el de Docker (`WarningsNotAsErrors` en `Directory.Build.props`). CI tendrá un job de auditoría estricta (#14), junto con los reportes TRX y de cobertura. Con xUnit v3 sobre MTP el reporte TRX es nativo: `dotnet test -- --report-xunit-trx` (verificado; `--report-trx` no existe sin la extensión `Microsoft.Testing.Extensions.TrxReport` y termina con código 5).

**Ejecutar Testcontainers desde el contenedor del SDK** (Docker Desktop en Windows, PowerShell). Comando verificado:

```powershell
docker pull mcr.microsoft.com/mssql/server:2019-latest   # una vez
docker run --rm `
  -v //var/run/docker.sock:/var/run/docker.sock `
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
  -e REQNROLL_TELEMETRY_ENABLED=0 `
  -v "C:\...\Cotifly\Cotifly:/repo" `
  -v cotifly-nuget:/root/.nuget/packages `
  -w /repo/backend-dotnet mcr.microsoft.com/dotnet/sdk:10.0 `
  sh -c "dotnet build -c Release && dotnet test -c Release --no-build"
```

- `//var/run/docker.sock` (doble barra en PowerShell) da acceso al Docker del host: SQL Server corre como contenedor hermano.
- `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal` hace que las pruebas se conecten al puerto publicado en el host y no a `localhost` del contenedor del SDK.
- Si `host.docker.internal` no resuelve (Docker en Linux sin Desktop), agregar `--add-host=host.docker.internal:host-gateway`, o usar `--network host` sin la variable. Si el contenedor Ryuk (limpieza de Testcontainers) no logra conectarse, `-e TESTCONTAINERS_RYUK_DISABLED=true` (los contenedores de prueba se borran igual al terminar, salvo que la corrida se aborte).
- `REQNROLL_TELEMETRY_ENABLED=0` desactiva la telemetría anónima de Reqnroll. Sin `APPDATA` (contenedor del SDK) Reqnroll puede dejar `tests/Cotifly.Aceptacion.Tests/Reqnroll/userid`; está en `.gitignore`.

## 9. Estructura del proyecto

```
backend-dotnet/
├── Cotifly.sln
├── Dockerfile                   # imagen multi-stage de Cotifly.Api (#27, ADR-007)
├── .dockerignore                # lista blanca del contexto: props, global.json, .editorconfig y src/
├── global.json                  # SDK 10.0.x y runner Microsoft.Testing.Platform
├── Directory.Build.props        # net10.0, Nullable, warnings como errores, analizadores
├── Directory.Packages.props     # versiones NuGet centralizadas
├── .editorconfig                # convenciones de nombres y namespaces (error de build)
├── .gitattributes               # finales de línea LF, binarios
├── .env.example                 # variables de entorno documentadas
├── specs/
│   ├── openapi.yaml             # contrato HTTP (#17)
│   ├── features/                # escenarios Gherkin (Reqnroll): persistencia, precio, margen
│   └── decisiones/              # ADR-001 … ADR-012
├── src/
│   ├── Cotifly.Dominio/         # Comun/ + entidades y enums por módulo
│   ├── Cotifly.Aplicacion/      # Comun/{Interfaces,Exceptions}/ + <Módulo>/Repositories/ (puertos) + clsDependenciasAplicacion
│   ├── Cotifly.Infraestructura/ # Comun/Persistence/ (DbContext, configuraciones EF, UoW, interceptor, health check),
│   │                            # Configuracion/, <Módulo>/Repositories/, Identidad/Persistence/ + clsDependenciasInfraestructura
│   └── Cotifly.Api/             # Program.cs (+ rama --healthcheck), Sistema/ (health y sonda) + carpetas por módulo
└── tests/
    ├── Comun/                   # soporte compartido de integración (enlazado, no es proyecto)
    ├── fixtures/sap/             # muestra de RentabClienteSet portada del Python (copiada a la salida de las pruebas)
    ├── Cotifly.Dominio.Tests/
    ├── Cotifly.Aplicacion.Tests/
    ├── Cotifly.Infraestructura.Tests/
    ├── Cotifly.Api.Tests/
    ├── Cotifly.Arquitectura.Tests/
    └── Cotifly.Aceptacion.Tests/

../migrations_mssql/             # scripts de esquema SQL Server (0001, 0002): única vía de cambio de esquema
```
