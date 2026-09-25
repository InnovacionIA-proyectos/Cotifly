# Cotifly — backend .NET 10

Backend de Cotifly migrado de Python a C# / .NET 10 (milestone HU-003). Este README es el esqueleto creado en HU-003.2 (#8); las secciones marcadas se completan en HU-003.8 (#14).

## 1. Descripción

Cotifly es el bot cotizador de Botero Soto: un comercial conversa con un agente de IA (consola web de ClientePro o Google Chat), el agente reúne los datos, resuelve margen y precio, genera el PDF de la cotización y la envía por correo. Este proyecto reemplaza por completo a los servicios Python `orquestador` y `mcp_server` (corte big-bang, ADR-001).

Estado actual: solución, capas, convenciones, patrón de configuración validada al arrancar, `GET /health` y `GET /health/ready`, modelo de dominio y persistencia EF Core Database First sobre SQL Server 2019 (#9), los módulos de negocio Precio, Margen (incluido el margen negociado) y Tarifas (#10, ver §2) las fuentes reales de SAP TM, export JSON y OData en vivo, elegidas por `SapTm:Modo` (#13, ver §2 y §3), los módulos PDF, Correo y Auditoría con el teléfono del comercial y el seguimiento como casos de uso (#11, ver §2 y §3) el login SSO desde ClientePro con verificación doble, refresh y logout (`/api/auth/*`, #12 parte A, ver §2, §3 y §7) y la API web `/web/*` con el folio del servidor, la descarga autenticada del PDF, la administración de líneas (HU-004.9) y las métricas en borrador (HU-010.1), más CORS, encabezados reenviados y endurecimiento HTTP (#12 parte B, ver §2, §3 y §7), y el agente de IA agnóstico al proveedor con estado de conversación estructurado, `POST /web/chat`, el webhook de Google Chat (`POST /chat/events`, worker y publicación en el hilo) y el enlace firmado del PDF (#12 parte C, ver §2, §3 y §7). El resto, en #14 a #16.

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
| `UsuariosWeb` (+ `idUsuarioExterno`, script 0002) | `Identidad/clsUsuarioWeb` | `IClsUsuarioWebRepository` |
| `Roles`, `UsuariosRoles` (script 0002, #12) | `Identidad/clsRol`, `clsUsuarioRol` (+ `RolInterno`) | `IClsRolRepository` |
| `TokensRefresco`, `TokensRevocados` (script 0002) | `Identidad/clsTokenRefresco`, `clsTokenRevocado` | `IClsTokenRefrescoRepository`, `IClsTokenRevocadoRepository` (#12) |
| `Sesiones.estadoAgente` (JSON, script 0002) | `Agente/clsEstadoConversacion` (+ `clsCandidatoCliente`) | vía `clsSesion` |
| `dbsolucionesti.tblSAPUsuariosApp`, `tblSAPUsuarioRoles`, `tblSAPRoles` (solo lectura) | sin entidad de Dominio: filas internas de Infraestructura (sin `CLAVE`), DTOs `clsUsuarioExterno` y `clsRolExterno` en Aplicacion | `IClsAlmacenIdentidadExterno` (#12) |

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

**PDF, Correo y Auditoría (#11, HU-003.5).** Port de `mcp_server/pdf/` (`armar_cotizacion.py`, `armar_multi.py`, `generador.py`), `shared/almacen_pdf.py`, `mcp_server/correo.py` y `mcp_server/auditoria.py`. Casos de uso listos para los endpoints de #12 (`enviar`, `correo/vista-previa`, `destinatarios-previos`, `usuario/telefono`, `seguimiento`) y para las herramientas del agente (`generar_pdf`, `enviar_correo`):

| Pieza | Capa | Qué hace |
|---|---|---|
| `Pdf/DTOs/clsCotizacionPdfPayload` (+ `clsTarifaPayload`, `clsFilaTarifaPayload`, `clsServicioConexoPayload`...) | Aplicacion | Payload del PDF con el esquema de `ejemplo-solicitud.json` (snake_case, `clsJsonAplicacion.SnakeCase`). `comercial.nombre/email/cel` se ignoran (la firma sale de `UsuariosWeb`); `extra_css`/`dibujo_html` no existen (sin HTML). Nuevo: `servicios_conexos` (itemizado de "incluye") |
| `Pdf/Validators/clsValidadorCotizacionPdfPayload` + `Pdf/Services/clsTextoSeguro` | Aplicacion | Exige `linea_negocio` y al menos una fila de tarifa; largos máximos por campo; quita caracteres de control y de formato invisibles (el texto se inserta como texto: un `<script>` se imprime literal) → `PDF_PAYLOAD_INVALIDO` |
| `Pdf/Services/clsConstructorDocumentoCotizacion` → `clsDocumentoCotizacion` | Aplicacion | Reglas de `construir_html` sin maquetar: variante (`secciones` → multi), textos del payload o estándar (`clsTextosEstandar`, ambos juegos del Python), tarifa por fila (`tarifa` o costo/margen con `clsCalculadoraPrecio`), bloques condicionales (aduana solo si la línea empieza por "internacional", extradimensionada, refrigerada), imagen y leyenda por línea, nota de decretos, frase final y firma |
| `Pdf/Services/clsFormatoMoneda`, `clsFormatoFecha` | Aplicacion | `$3.294.798` / `US$1.200` / `—` (half-to-even, sin ICU ni cultura del proceso) y `Yumbo, 24 de septiembre de 2026` (meses propios; sin fecha, la de hoy en `Zona:ZonaHoraria` vía `IClsCalendarioNegocio`) |
| `IClsGenerarPdfService` / `clsGenerarPdfService` (+ decorador `clsGenerarPdfServiceAuditado`) → `clsSalidaPdf` | Aplicacion | `generar_pdf`: folio obligatorio de la entrada (lo genera el servidor, ADR-011; `^[A-Za-z0-9_-]+$`, ≤ 50) → payload → comercial en `UsuariosWeb` → línea con `IClsMargenResolverService.ResolverLineaAsync` (sin tildes, `MARGEN_LINEA_DESCONOCIDA` antes de renderizar, issue #44) → folio libre (`FOLIO_DUPLICADO`) → cotización válida en el dominio → coherencia con la trazabilidad (precio y moneda, ver abajo) → render (`PDF_RENDER_NO_DISPONIBLE`) → almacén sin reemplazar → fila en `Cotizaciones` (`lineaNegocioId`, costo, márgenes, precio, `origenMargen` incl. `negociado`, moneda/TRM, `usuarioId`, `spaceId`/`threadId`, `pdfPath`); si la base rechaza la fila se borra el PDF. Salida `SalidaPdf` `{cotizacion_id, folio, pdf_url, registrada_en_bd}` |
| `IClsGeneradorPdf` / `Pdf/Services/clsGeneradorPdfMigraDoc` (+ `clsBloquePortada`, `clsBloqueTarifa`, `clsBloqueCondiciones`, `clsBloqueFirma`, `clsEncabezadoPiePdf`, `clsEstilosPdf`/`clsDisenoPdf`) | Infraestructura | Maquetación con PDFsharp-MigraDoc 6.2.4 (ADR-006): A4, márgenes 34/16/30/16 mm (multi 34/14/28/14), logo, pie con `FOOTER_DIRS` y franja web, paleta `C`, 3 páginas en tarifa única y 2 en multi. Checklist de cobertura en ADR-006 |
| `Pdf/Services/clsResolvedorFuentes` (`IFontResolver`) + `clsRecursosPdf` | Infraestructura | Liberation Sans (normal, negrita, cursiva, negrita cursiva) embebida como recurso; cualquier familia pedida se resuelve a ella; viñetas con `•` propio. Logo y 7 imágenes por línea embebidos (ver §3) |
| `IClsAlmacenPdf` / `Pdf/Services/clsAlmacenPdfVolumen` | Infraestructura | `Pdf:StorageDir` (por defecto `/data/pdfs`), nombres `^[A-Za-z0-9_-]+\.pdf$`, ruta siempre dentro de la carpeta, `GuardarNuevoAsync` nunca reemplaza (`FileMode.CreateNew`), lectura por referencia solo con el nombre del archivo. S3/MinIO no se porta (ADR-012) |
| `IClsCorreoService` / `clsCorreoService` (+ `clsCorreoServiceAuditado`, `clsCandadoEnvios`) | Aplicacion | `enviar_correo`: comercial autenticado (`CONFIRMACION_REQUERIDA`), `Correo:Habilitado` (`CORREO_DESHABILITADO`), destinatarios válidos (`CORREO_INVALIDO`), dueño (`COTIZACION_NO_ENCONTRADA`), deduplicación de 5 min contra envíos `enviado` al mismo conjunto de `para` sin distinguir mayúsculas (`CORREO_DUPLICADO`, regla movida del repositorio, ADR-008), PDF del almacén (`CORREO_PDF_NO_ENCONTRADO`), transporte (`CORREO_ENVIO_FALLIDO` + fila `fallido` con el error). Éxito: fila `enviado` con destinatarios y `estadoEnvio` `sin_enviar`→`enviada`, `enviada`/`reenviada`→`reenviada` (fix del bug del Python). Asunto por defecto `Cotización {folio} — Botero Soto`, cuerpo `_PLANTILLA` + HTML corporativo (`clsPlantillaCorreo`) |
| `IClsVistaPreviaCorreoService`, `IClsDestinatariosPreviosService` | Aplicacion | `VistaPreviaCorreo` (asunto, cuerpo, para/cc prediligenciados, adjunto) sin enviar nada; `Destinatarios` del último envío exitoso del comercial al NIT (`IClsCorreoRepository.ObtenerUltimoEnviadoAClienteAsync`) |
| `IClsTransporteCorreo` (Strategy) / `Correo/Services/clsTransporteSmtpRelay` (MailKit) | Aplicacion / Infraestructura | `smtp_relay`: texto plano, sin AUTH ni STARTTLS (como el relay `10.10.40.62`), `From`/`Reply-To`/`MAIL FROM` = comercial (o `FromFallback`), `RCPT TO` = para + cc, `multipart/mixed` (texto + HTML) con el PDF en base64, `Message-ID` `@boterosoto.com.co`; relay que rechaza destinatarios → mensaje para que TI autorice la IP. `wscorreos` y `smtp`: `clsTransporteCorreoNoImplementado` (D3) |
| `IClsAuditoriaService` / `clsAuditoriaService` | Aplicacion | `EventosHerramienta` en su propio ámbito de DI: herramienta, entrada y resumen JSON snake_case (máx. 4000 caracteres, `{"truncado":true,"preview":...}` si se pasa, siempre JSON válido para el CHECK `ISJSON`), éxito, duración, hilo. Nunca lanza. Los decoradores auditan `generar_pdf`, `enviar_correo` y `registrar_ajuste_margen` (también desde la web); el filtro del agente de #12 debe usar `RegistrarAsync` para el resto y omitir esas tres (`HerramientasAuditadasPorCasoDeUso`) |
| `IClsTelefonoComercialService` | Aplicacion (Identidad) | `GET/PUT /web/usuario/telefono`: patrón `^\+?[0-9 ]{7,20}$` (`TELEFONO_INVALIDO`), persiste en `UsuariosWeb.telefono`; vacío lo borra. Es el único origen del "Cel" del PDF |
| `IClsSeguimientoService` | Aplicacion (Cotizaciones) | Ganada/perdida con motivo (≤ 2000) y `marcadoAt`, solo del dueño. Sale de `IClsCotizacionRepository.MarcarSeguimientoAsync` (deuda de ADR-008) |
| `IClsCalendarioNegocio` / `Comun/clsCalendarioNegocio` | Aplicacion / Infraestructura | Fecha de hoy en `Zona:ZonaHoraria` (el contenedor corre en UTC); la usará también el folio de #12 |

Códigos nuevos en `ErrorNegocio` (`specs/openapi.yaml`): `FOLIO_DUPLICADO` (el Python versionaba el folio en silencio con `-v2`; en .NET el folio lo genera #12, ADR-011, y una colisión se informa), `CORREO_DESHABILITADO` y `CORREO_INVALIDO` (la herramienta `enviar_correo` lo devuelve tal cual).

**Coherencia de lo impreso (#11, ADR-003/ADR-004):** el folio es obligatorio en la entrada (lo genera el servidor; la `referencia` del payload se ignora). Con `PrecioFinal` en la trazabilidad, la tarifa impresa de cada fila y el precio calculado con costo y margen aplicado deben ser ese precio; filas y servicios conexos deben estar en la moneda de la cotización (HU-012.3). Si no, `PDF_PAYLOAD_INVALIDO`. El filtro de #12 sobrescribe los números del payload desde el estado (ADR-004). También se rechazan tarifas, costos y márgenes negativos, y `clsCotizacionPdfPayload.DesdeJson` devuelve `PDF_PAYLOAD_INVALIDO` (no lanza) ante JSON mal formado, tipos erróneos o sustitutos UTF-16 sueltos.

**Para #12 (endpoints y agente):**

| Caso | Respuesta HTTP en `/web/*` |
|---|---|
| `COTIZACION_NO_ENCONTRADA` (no existe o no es suya) | 404 `ErrorWeb` (`NoEsTuya`) |
| `CORREO_DESHABILITADO` | 503 |
| `USUARIO_NO_ENCONTRADO` (teléfono: el comercial del token ya no existe) | 401 `ErrorWeb` |
| `TELEFONO_INVALIDO`, `CORREO_INVALIDO`, `SEGUIMIENTO_INVALIDO` | 400 `ErrorWeb` |
| Resto de códigos de envío (`CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO`, `CORREO_ENVIO_FALLIDO`, `CONFIRMACION_REQUERIDA`) | 400 `ErrorNegocio` |

El filtro de auditoría del agente no vuelve a auditar `generar_pdf`, `enviar_correo` ni `registrar_ajuste_margen` (ya los auditan sus decoradores; `clsAuditoriaService.HerramientasAuditadasPorCasoDeUso`).

**Destinatarios previos:** hoy devuelve solo los del último envío exitoso del comercial al NIT (es lo que dice `openapi.yaml`); el backlog habla de "los correos ya usados", así que queda por confirmar si se quiere la unión de todos los envíos. Mientras `buscar_cliente` sea stub (ADR-005) las cotizaciones quedan con `nitCliente` nulo y no hay precarga: limitación temporal.

Diferencias deliberadas con el Python:
- **Firma con datos reales:** nombre y correo de `UsuariosWeb` y "Cel" solo si `UsuariosWeb.telefono` tiene valor; el `comercial` del payload (LLM) solo aporta el cargo.
- **Sin HTML:** no hay inyección posible; `extra_css` y `dibujo_html` desaparecen. Los saltos de línea del texto se respetan (en HTML se colapsaban).
- **Folio:** no se versiona aquí (`FOLIO_DUPLICADO`); un PDF existente nunca se sobrescribe (el Python sobrescribía `{folio}.pdf`).
- **Línea de negocio:** se resuelve antes de renderizar y sin tildes (el Python perdía la cotización entera con "Distribución", issue #44).
- **Trazabilidad:** costo, márgenes, precio y origen llegan en `clsTrazabilidadCotizacion` desde el estado de la conversación, no del payload del LLM (`_datos_trazabilidad`).
- **Variante multi:** usa el prefijo `US$` de la moneda de cada fila (el `money()` de `armar_multi.py` lo ignoraba).
- **Almacén:** la lectura por referencia solo toma el nombre del archivo (el Python leía cualquier ruta local existente guardada en `pdfPath`).
- **Correo:** `estadoEnvio` pasa a `reenviada`; la deduplicación se serializa por cotización dentro del proceso; `wscorreos` y `smtp` no se portan hasta confirmar el transporte (D3); `Correo:Habilitado=false` por defecto responde `CORREO_DESHABILITADO` (el Python ocultaba la herramienta).
- **Incluye itemizado:** con `servicios_conexos` se lista el flete (origen y destinos) y cada servicio con su precio. Nexo (HU-005) todavía no entrega el costeo desglosado por servicio: mientras tanto los envía el agente o el formulario, y sin ellos queda el texto estándar.

**Identidad: login SSO ClientePro con verificación doble (#12, parte A, ADR-009).** `POST /api/auth/login-clientepro`, `/refresh` y `/logout` según `specs/openapi.yaml`:

| Pieza | Capa | Qué hace |
|---|---|---|
| `IClsAlmacenIdentidadExterno` / `Identidad/Services/clsAlmacenIdentidadSolucionesTi` (+ `clsIdentidadDbContext`) | Aplicacion / Infraestructura | Almacén corporativo de solo lectura: `BuscarUsuarioAsync` (idUsuario como `NOMBREUSUARIO` y, si no, como `ID` numérico) y `ObtenerRolesAsync` (`IDROLES` + `DESCRIPCION`). `ID` NUMERIC → `long` (y → `int` para `IDUSUARIO`) solo si cabe; nombre repetido, ID no entero o fila sin nombre = no encontrado; fallos de la base se propagan (fail-closed) |
| `clsVerificadorIdentidad` | Aplicacion | Verificación doble común al login y al refresh: persona en el almacén → roles externos traducidos con `Roles.idRolExterno` (con los dos gana `gerente_comercial`, regla auditada) → usuario por el `NOMBREUSUARIO` del almacén: primer ingreso crea `UsuariosWeb` + `UsuariosRoles` (JIT); después la asignación activa y el vínculo `idUsuarioExterno` deben coincidir, si no 401 con motivo (`rol_externo_cambio`, `nombre_usuario_cambio`, `sin_asignacion_activa`, `identidad_inconsistente`...) |
| `IClsLoginClienteProService` / `clsLoginClienteProService` | Aplicacion | Valida el cuerpo (`SOLICITUD_INVALIDA` → 400), verifica, actualiza `ultimaSesion`, emite access y refresh (familia nueva, 30 días) y audita (`login_clientepro`, hilo `auth/{idUsuario}`). Todo rechazo o excepción: `CREDENCIALES_INVALIDAS` → 401 genérico |
| `IClsRefreshTokenService` / `clsRefreshTokenService` | Aplicacion | Busca por SHA-256; reuso de un token rotado o revocado → revoca todos los refresh vigentes del usuario (salvo rotación de hace menos de 10 s: renovación concurrente); vuelve a verificar; rota de forma atómica heredando familia y vencimiento absoluto. Todo rechazo: `SESION_EXPIRADA` → 401 |
| `IClsLogoutService` / `clsLogoutService` | Aplicacion | Revoca la familia del refresh y el `jti` del access (en memoria al instante y en `TokensRevocados`); nunca falla hacia afuera (204) |
| `IClsRevocacionAccesos` / `clsCacheTokensRevocados` | Aplicacion | `jti` revocados en memoria + foto de `TokensRevocados` cada 60 s; sin foto de menos de 5 min, fail-closed |
| `IClsEmisorTokens`, `IClsConfiguracionJwt` / `clsEmisorTokensJwt`, `clsConfiguracionJwt` (+ `clsOpcionesJwt`) | Aplicacion / Infraestructura | JWT HS256 (`sub`, `jti`, `username`, `rol`, `iss`, `aud`, `iat`, `nbf`, `exp`) con `JsonWebTokenHandler`; clave ≥ 32 bytes; sin clave fuera de Production, clave efímera con aviso |
| `clsTokenOpaco` | Aplicacion | Refresh de 32 bytes aleatorios en base64url (43 caracteres) y su SHA-256 hexadecimal |
| `IClsRolRepository`, `IClsTokenRefrescoRepository`, `IClsTokenRevocadoRepository` | Aplicacion / Infraestructura | Catálogo y asignaciones; rotación con `UPDATE` condicional en transacción (de dos simultáneas gana una), revocaciones por familia o usuario y purga con `ExecuteUpdate`/`ExecuteDelete` |
| `Identidad/Services/clsLimpiezaTokensHostedService` | Infraestructura | Purga horaria de `jti` vencidos y refresh vencidos hace más de un día; un fallo es un warning |
| `Identidad/Controllers/clsAuthController` | Api | `/api/auth/*` exactos al contrato: 200 `LoginResponse`, 400/401/429 con `ErrorWeb`; refresh con cuerpo inválido → 401 y logout siempre 204 (el cuerpo se lee sin la validación automática) |
| `Identidad/clsConfiguracionAutenticacion` (+ `clsPoliticas`, `clsOpcionesLimiteAuth`, `clsContextoUsuarioHttp`) | Api | `JwtBearer` (`MapInboundClaims=false`, `NameClaimType="username"`, `RoleClaimType="rol"`, emisor, audiencia, vigencia con 30 s de tolerancia, firma HS256), revocación en `OnTokenValidated`, 401/403 con `ErrorWeb`, políticas `Comercial` (comercial o gerente) y `GerenteComercial` para la parte B, `IClsContextoUsuario` desde los claims y límite de `/api/auth/*` por IP |
| `Comun/clsErrorWeb` | Api | `{"error": "..."}`; también la respuesta 400 de la validación automática de `[ApiController]` (en lugar de `ProblemDetails`) |

`/api/auth/*` serializa en camelCase salvo `user.nombre_completo` (así lo define el contrato), con nombres JSON explícitos en los DTOs de la Api. `ubicacion` sale `null` hasta que se cruce la regional con `DimEjecutivo`.

**API web `/web/*` (#12, parte B).** Todos los endpoints de `specs/openapi.yaml` salvo `/web/chat` y `/chat/events` (parte C). Controllers delgados: toman el usuario del token (`IClsContextoUsuario`) y delegan en los casos de uso; los errores pasan por una sola tabla (`clsRespuestasWeb`, abajo).

| Pieza | Capa | Qué hace |
|---|---|---|
| `Cotizaciones/clsFolio` | Dominio | Folio del servidor (ADR-011, aceptada): `PCN-[{nitCliente}-]DDMMAAAA-{NNN}[-vN]`. `Crear(fecha, consecutivo, nit)` (NNN 001–999, si no `FOLIO_CONSECUTIVO_AGOTADO`; el NIT se guarda solo con sus dígitos, máximo 20), `Desde(folio)` (interpreta el formato; un folio heredado como `PCN-DDMMAAAA` conserva su base), `ConVersion(n)` (≥ 2, ≤ 50 caracteres) |
| `IClsGeneradorFolioService` / `clsGeneradorFolioService` | Aplicacion | `CrearConFolioNuevoAsync(usuario, nit, crear)` y `CrearNuevaVersionAsync(usuario, cotizacionId, crear)`: calcula el folio y ejecuta `crear` (agrega la cotización con `version = folio.Version` y guarda) dentro de `IClsCandadoTransaccional`. Fecha de `IClsCalendarioNegocio` (America/Bogota). Consecutivo = mayor `NNN` v1 del comercial ese día (con y sin NIT) + 1, saltando los folios que ya tiene otra cotización. Versión = máxima de la familia (`{base}` y `{base}-vN`) + 1. Lo usará `generar_pdf` del agente (parte C) |
| `IClsCandadoTransaccional` / `Comun/Persistence/clsCandadoTransaccionalSql` | Aplicacion / Infraestructura | Transacción `ReadCommitted` en el `DbContext` del ámbito + `sp_getapplock` (`Exclusive`, dueño `Transaction`, espera máxima 15 s → `TimeoutException`). Confirma si el resultado es exitoso; deshace (y limpia el `ChangeTracker`) si es un fallo o si lanza. No se anida. Recursos: `cotifly:folio:DDMMAAAA` (uno por día, para todos los comerciales) y `cotifly:folio-version:{base}` |
| `IClsMisCotizacionesService`, `IClsNuevaVersionService` (+ `clsCotizacionResumen`, `clsDatosNuevaVersion`) | Aplicacion | Listado del comercial (máx. 100, JOIN con `MargenesPolitica` para `linea_negocio`, `pdf_url` con `clsUrlPdf`) y datos para prellenar una versión nueva (solo lectura) |
| `IClsDescargaPdfService` | Aplicacion | `GET /pdfs/{archivo}`: nombre `^[A-Za-z0-9_-]+\.pdf$` que debe ser el PDF guardado de la cotización con ese folio; dueño, o `gerente_comercial` de la misma regional (si la regional del gerente es nula se permite, como dice el contrato; ver pendientes). Todo acceso de alguien que no es el dueño, permitido o no, se audita (`descargar_pdf`) |
| `IClsLineasNegocioAdminService` | Aplicacion | HU-004.9 (#41): lista y edita margen (0–0,99), método (`margen` o `markup`) y estado; revalida el rol `gerente_comercial` en el caso de uso; `clsMargenPolitica.Actualizar` aplica `clsReglaMargen`; audita cada intento (`admin_actualizar_linea_negocio`, con quién, lo pedido y `antes`/`despues`) |
| `IClsMetricasService` / `IClsMetricasRepository` (`clsMetricasRepository`) | Aplicacion / Infraestructura | HU-010.1 (borrador): `Database.SqlQuery` sobre `vActividadPorComercial` y `vAjustesMargenPorComercial` + JOIN `UsuariosWeb` (`nombre_completo`). Comercial: su fila; gerente: los comerciales de su regional (`UsuariosWeb.regional`); gerente con regional nula: solo su fila. Las vistas no se mapean como entidades sin clave para no mezclarlas con la prueba de deriva de tablas |
| `Comun/clsJsonWeb` (`clsJsonSnakeCaseAttribute`, `clsFormateadorEntradaWeb`, `clsFormateadorSalidaWeb`) | Api | Serialización por grupo (ADR-002): un par de formateadores System.Text.Json snake_case al inicio de la lista de MVC que solo aceptan endpoints con `[clsJsonSnakeCase]`; lo lleva `clsControladorWebBase`, así todo `/web/*` lee y escribe snake_case (nulos siempre escritos, números estrictos: `"0.2"` es un cuerpo inválido) y `/api/auth/*`, `/health` y `/pdfs` no cambian |
| `Comun/clsRespuestasWeb` (+ `clsErrorNegocioWeb`, `clsControladorWebBase`) | Api | Tabla única de errores (abajo); base de los controllers de `/web/*` con `[ApiController]`, `[clsJsonSnakeCase]` y la política `Comercial` |
| `Comun/clsSeguridadHttp` | Api | CORS (`Cors:OrigenesPermitidos`), `UseForwardedHeaders` configurable para Traefik, encabezados de seguridad, `Cache-Control: no-store` en `/web/*` y `/pdfs/*` y límite del cuerpo de `/web/*` (64 KB, 413). Opciones en §3 |
| Controllers `Cotizaciones/`, `Correo/`, `Clientes/`, `Identidad/clsUsuarioController`, `Administracion/`, `Metricas/`, `Pdf/` | Api | Rutas exactas del contrato (§7). Administración con `[Authorize(Policy = GerenteComercial)]` además de la base |

Errores de `/web/*` (`clsRespuestasWeb`, cumple la tabla "Para #12" de arriba):

| Código del caso de uso | HTTP |
|---|---|
| `COTIZACION_NO_ENCONTRADA`, `NO_ENCONTRADO` (no existe o no es tuya; PDF ajeno) | 404 `ErrorWeb` (`Cotización no encontrada o no es tuya.`, igual en ambos casos) |
| `NO_AUTENTICADO`, `USUARIO_NO_ENCONTRADO` | 401 `ErrorWeb` (`Inicia sesión de nuevo.`) |
| `SIN_PERMISO` (rol revalidado en el caso de uso) | 403 `ErrorWeb` |
| `CORREO_DESHABILITADO` | 503 `ErrorNegocio` |
| `SOLICITUD_INVALIDA`, `TELEFONO_INVALIDO`, `CORREO_INVALIDO`, `SEGUIMIENTO_INVALIDO`, cuerpo mal formado | 400 `ErrorWeb` |
| Resto de códigos de negocio (`CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO`, `CORREO_ENVIO_FALLIDO`, `CONFIRMACION_REQUERIDA`...) | 400 `ErrorNegocio` (`detalle` como objeto `{"motivo": ...}`) |
| Cuerpo de más de `SeguridadHttp:LimiteCuerpoWebBytes` | 413 `ErrorWeb` (fuera del contrato: transporte) |

Decisiones de la parte B:
- **Candado del folio global por día**, no por comercial: el formato aprobado no lleva un segmento del comercial, así que dos comerciales llegan al mismo folio sin NIT (o con el mismo NIT el mismo día). Con un candado por comercial chocarían contra `uq_cotizaciones_folio`; con uno por día el segundo salta al siguiente número libre. Consecuencia: sin NIT el `NNN` se comporta casi como un consecutivo diario global (documentado en ADR-011).
- **Rol `gerente_comercial` en la consola:** la política `Comercial` admite ambos roles (decisión de la parte A: el gerente también cotiza). `docs/CONTRATO_API_WEB.md` decía que un gerente recibía 403 en los endpoints de comercial; se corrigió.
- **Vista de actividad:** `num_cotizaciones_enviadas` debía contar `enviada` y `reenviada` (contrato) y la vista del 0001 solo contaba `enviada`: la corrige `migrations_mssql/0003_metricas_reenviadas.sql` (`CREATE OR ALTER VIEW`, pendiente de aprobación como el 0002).
- **Firma del JWT sin caché global de proveedores** (ajuste sobre la parte A): el emisor usa una sola clave y la validación desactiva `CacheSignatureProviders`. Con varias instancias de la API en un proceso (pruebas en paralelo) aparecían 401 intermitentes con tokens recién emitidos; el patrón coincide con la caché de proveedores de firma de Microsoft.IdentityModel. `OnAuthenticationFailed` deja una línea Information con el tipo y el mensaje del rechazo.
- **HU-004.9, "el comercial solo ve líneas activas":** hoy no hay ningún endpoint de comercial que liste líneas (el catálogo del formulario no está en el contrato); cuando exista debe filtrar `isActive = 1`.

**Agente de IA, `/web/chat` y Google Chat (#12, parte C, ADR-003, ADR-004, ADR-005, ADR-011).** Port de `orquestador/agente.py`, `prompt.py`, `sesiones.py`, `web.py` (`web_chat`, `_datos_de`), `webhook.py`, `gchat.py`, `worker.py`, `procesador.py`, `publicador.py`, `cards.py` y `alertas.py`, con las herramientas de `mcp_server/server.py` sobre los casos de uso de #10, #11 y #12:

| Pieza | Capa | Qué hace |
|---|---|---|
| `Agente/Services/clsAgenteRunner` (`IClsAgenteRunner`) | Aplicacion | Turno del agente: sesión `spaceId`/`threadId` ligada al comercial (otro usuario → `NO_ENCONTRADO`, 404), mensaje registrado primero, prompt de sistema, historial con ventana, loop manual hasta `IA:MaxIteraciones` sobre `IChatClient` (sin `UseFunctionInvocation()`), cada herramienta con `kernel.InvokeAsync` (corren los filtros), `HERRAMIENTA_DESCONOCIDA` como resultado si el modelo pide una que no existe, respuesta final en una llamada aparte sin herramientas pasada por `IClsValidadorRespuestaAgente`, eventos y estado guardados al final (la sesión se relee: el candado del folio puede limpiar el seguimiento de cambios). Devuelve `clsResultadoTurno` (texto, iteraciones, herramientas, salidas) |
| `Agente/Herramientas/clsHerramientasCotizador` | Aplicacion | Plugin `cotizador` de Semantic Kernel (`[KernelFunction]`): `buscar_cliente(nombre)`, `buscar_tarifa(lineaNegocio, origen, destino, tipoVehiculo?, tipoCarga?, pesoKg?)`, `obtener_margen(lineaNegocio, cliente?)`, `calcular_precio(costo, margen, metodo)`, `registrar_ajuste_margen(margenAplicado, motivo?)`, `generar_pdf(cotizacion)` (folio de `clsGeneradorFolioService`: nuevo, o versión nueva si la conversación ya generó una; trazabilidad desde el estado), `enviar_correo(para, asunto?, cuerpo?, cc?)` (solo se declara con `Correo:Habilitado=true`) y `consultar_cotizacion(folio?, cliente?)` (solo las del comercial). Devuelven `clsResultadoHerramienta`: la salida del contrato (`SalidaPrecio`, `SalidaMargen`, `SalidaPdf`...) o `{"error": {codigo, mensaje, detalle?}}` |
| `Agente/Filtros/clsInyeccionEstadoFilter` | Aplicacion | `IFunctionInvocationFilter` (ADR-004 §4): sobrescribe `usuarioId`, `spaceId`, `threadId`, `nitCliente`, `origenCliente`, `bpCliente`, `lineaNegocioId`, `cotizacionId` y `folio` en todas las herramientas; el BP del estado es el cliente de `obtener_margen` (sin cliente confirmado, ninguno); margen y método del estado en `calcular_precio` (sin margen resuelto → `PRECIO_INVALIDO`) y el costo si vino de `buscar_tarifa`; referencia, costo y método en `registrar_ajuste_margen`; en `generar_pdf` exige la confirmación del turno y un cliente sin candidatos pendientes (`CONFIRMACION_REQUERIDA`) y un precio calculado, y reescribe el payload (línea canónica, método, cliente confirmado; cada fila con tarifa = precio final, costo y margen del estado y la moneda de la cotización; quita `referencia`, trazabilidad y servicios conexos); en `enviar_correo` exige la confirmación o que el comercial haya escrito los destinatarios. Si el LLM mandó otro valor, queda en el log |
| `Agente/Filtros/clsAuditoriaHerramientaFilter` | Aplicacion | `EventosHerramienta` de cada herramienta con la entrada ya inyectada. No repite `generar_pdf`, `enviar_correo` ni `registrar_ajuste_margen` cuando llegaron a su caso de uso (los auditan sus decoradores, `HerramientasAuditadasPorCasoDeUso`), pero sí registra sus rechazos del orquestador. Va por fuera del filtro de inyección |
| `Agente/Services/clsEstadoConversacionService` | Aplicacion | Único que cambia `clsEstadoConversacion` (ADR-004 §3). Con salidas exitosas: `buscar_cliente` exacta → NIT, nombre, origen y BP; varias → candidatos; stub → el texto solo como referencia; `buscar_tarifa` → ruta y costo de la fuente; `obtener_margen` → línea, id, márgenes y método (anula un precio anterior); `calcular_precio` → precio y previsualización pendiente; ajuste → `negociado`; `generar_pdf` → cotización, folio y versión. Con el mensaje del comercial: elección de un candidato por NIT, nombre o posición, y confirmación explícita ("Confirmo", "sí", "dale"...) solo si hay una previsualización pendiente |
| `Agente/Services/clsPromptSistema` + `Agente/Recursos/prompt_cotizador.md` | Aplicacion | Port de `SYSTEM_COTIZADOR` como recurso embebido, precedido de "# Contexto del comercial" (nombre y correo de `UsuariosWeb`, fecha de negocio) y "# Estado resuelto" (informativo; la fuente de verdad es el filtro). Corrige la contradicción del folio (el Python pedía una `referencia` "nunca PCN" que el MCP usaba como folio): el folio lo genera el servidor y el LLM no escribe números ni identificadores |
| `Agente/Services/clsVentanaHistorial` | Aplicacion | Ventana por tokens (~4 caracteres por token, `Sesiones:LimiteTokens`) que conserva el primer mensaje y el actual |
| `Agente/Interfaces/IClsValidadorRespuestaAgente` + `clsValidadorRespuestaPasante` | Aplicacion | Puerto de la validación de la respuesta final (#16, HU-003.10): formato que se pide (`null` = texto) y validación. Hoy es de paso (`TryAdd`; #16 lo reemplaza) |
| `Agente/Services/clsParserEventoChat`, `clsTarjetasChat`, `clsProcesadorEventoChat` | Aplicacion | Parseo de eventos clásicos y add-on (incluidos los clics en los botones de la tarjeta) y acuses; tarjetas de previsualización (botones "Confirmar y generar PDF" y "Ajustar margen"), del PDF (enlace firmado) y de alerta; procesamiento: identidad por `UsuariosWeb.email` (sin cuenta → "Ingresa primero a Cotifly desde ClientePro." y no se cotiza), turno, publicación en el hilo y aviso del fallo en el hilo |
| `Clientes/Interfaces/IClsBuscadorClientes` / `Clientes/Services/clsBuscadorClientesNoDisponible` | Aplicacion / Infraestructura | Puerto de `buscar_cliente` (ADR-005) y stub `CLIENTE_BUSQUEDA_NO_DISPONIBLE` hasta HU-005.2 (`TryAdd`) |
| `Pdf/Services/clsDescargaPdfFirmadaService` + `IClsFirmaUrlPdf` / `Pdf/Services/clsFirmaUrlPdfHmac` | Aplicacion / Infraestructura | `GET /pdfs/{archivo}?t=...` (ADR-011 §3): HMAC-SHA256 sobre archivo, comercial y vencimiento (`Pdf:MinutosUrlFirmada`, 15), comparación de tiempo constante; solo el dueño |
| `IA/Services/clsChatClientFactory`, `clsChatClientConRespaldo`, `clsLimitadorRpm` (+ `clsChatClientLimitado`, `clsChatClientNoConfigurado`) | Infraestructura | `IChatClient` por `IA:Proveedor` (ver §3), respaldo ante 429, 5xx, red caída o tiempo agotado (`IA:Fallbacks`), límite de `IA:Rpm` y `UseLogging`; sin proveedor, un cliente que falla con un mensaje claro |
| `Agente/Services/clsVerificadorJwtGoogleChat`, `clsTokenCuentaServicioGoogle`, `clsPublicadorGoogleChat`, `clsColaEventosChat`, `clsWorkerEventosChat`, `clsBarridoSesionesHostedService` | Infraestructura | JWT de Google (RS256 contra los certificados x509 de `chat@system.gserviceaccount.com`, en caché y releídos ante un `kid` nuevo; emisor y audiencia `GChat:ProjectNumber`); token OAuth de la cuenta de servicio (JWT-bearer, RFC 7523); `spaces.messages.create` con `REPLY_MESSAGE_FALLBACK_TO_NEW_THREAD`; cola `Channel<T>` acotada; worker con un ámbito por evento que sobrevive a los fallos y alerta a `GChat:SupportSpace`; barrido de sesiones inactivas |
| `Agente/Controllers/clsChatWebController`, `clsChatEventosController`; `Pdf/Controllers/clsPdfController` | Api | `POST /web/chat` (política `Comercial`, límite `LimiteChat`, `sesion_id` validado, 404 si la sesión es de otro, 200 `{texto, error: true}` ante un fallo, con los textos del Python); `POST /chat/events` (anónimo, JWT de Google, acuse inmediato, 400 si no es JSON); `/pdfs` con Bearer o `?t=` |

Decisiones de la parte C:
- **Carpetas:** `Aplicacion/Agente/` suma `Herramientas/` (plugin de Semantic Kernel), `Filtros/` (`IFunctionInvocationFilter`) y `Recursos/` (prompt embebido) a las de la convención; Infraestructura suma `IA/Services/` (ADR-003: solo ahí se conocen los SDK de los proveedores).
- **`KernelFunction` como declaración, no `AsAIFunction()`:** en Semantic Kernel 1.80 `KernelFunction.AsAIFunction()` está obsoleta ("usa la función directamente") y, usada como `AITool`, la función se llama `cotizador_<nombre>`. El runner declara al modelo `AIFunctionFactory.CreateDeclaration(nombre, descripción, esquema)` de cada `KernelFunction` (el modelo ve `buscar_tarifa`, como en el Python) y siempre ejecuta con `kernel.InvokeAsync`: solo declaración, nunca invocación automática.
- **Respuesta final en una llamada aparte, siempre** (ADR-003): el texto del loop se descarta y cada turno cuesta una llamada más, a cambio de un solo camino de validación para #16.
- **Confirmación explícita por turno:** el PDF y el correo solo salen en el turno en que el comercial confirma una previsualización ya mostrada (o, para el correo, escribe los destinatarios). Un "sí" sin previsualización no confirma nada; elegir un cliente candidato no confirma el PDF.
- **Costo:** mientras no haya Nexo (`buscar_tarifa` responde `TARIFA_FUENTE_NO_DISPONIBLE`), el costo que escribe el comercial llega del mensaje a `calcular_precio`; un costo de la fuente siempre gana. Margen y método nunca salen del LLM.
- **Margen de referencia de un ajuste:** el que propuso `obtener_margen` (`MargenSugerido`, campo nuevo del estado junto con `FuenteCosto`); `Cotizaciones.margenPolitica` guarda la política de la línea.
- **Servicios conexos en el chat:** se quitan del payload hasta que Nexo entregue sus precios (el LLM no pone números).
- **Google Chat:** un hilo de otro comercial recibe "abre un hilo nuevo"; los clics en los botones de la tarjeta se vuelven "Confirmo" / "Quiero ajustar el margen." (el Python los ignoraba); `GChat:SaJson` acepta el JSON o la ruta al archivo (secreto de Docker).
- **Sin Google.Apis.Auth:** el token de la cuenta de servicio y la verificación del JWT se hacen con `Microsoft.IdentityModel.JsonWebTokens` (ya presente). `Google.Apis.Auth` (Apache-2.0) arrastra Newtonsoft.Json y System.Management, y su `GoogleCredential.FromJson` está obsoleta.

Diferencias deliberadas con el Python:
- El historial entre turnos sigue siendo de texto, pero lo resuelto viaja en el estado (ADR-004): el hallazgo del cliente perdido entre turnos queda cubierto por `chat_slot_filling.feature`.
- El folio no lo escribe el LLM (ADR-011) y la `referencia` del payload se descarta.
- La ventana del historial conserva también el mensaje actual; el Python podía descartarlo.
- `enviar_correo` exige la confirmación o los destinatarios escritos por el comercial (el Python confiaba en el `confirmado_por` que mandaba el LLM).
- `consultar_cotizacion` solo lista las cotizaciones del comercial (el Python ignoraba `comercial` y listaba todas).

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
- **Formato de moneda:** ICU da `3294798m.ToString("C0", es-CO)` = `$ 3.294.798` (con espacio). #11 usa un formateador propio, `clsFormatoMoneda`, que agrupa con la cultura invariante y cambia la coma por punto: `$3.294.798` exacto como el Python y sin depender de ICU ni de la cultura del proceso (probado con es-CO, en-US, de-DE, fr-FR, ja-JP e invariante). La fecha larga usa meses propios (`clsFormatoFecha`).
- **Fuentes y recursos del PDF (#11):** chiseled no trae fontconfig ni fuentes del sistema, así que todo va embebido en `Cotifly.Infraestructura.dll` y se carga con `IFontResolver` (`clsResolvedorFuentes`): Liberation Sans 2.1.5 (4 caras TTF, ~1,6 MB, **SIL Open Font License 1.1**, métricas de Arial; la licencia viaja en `/app/licencias/LiberationSans-OFL.txt`), el logo (`logo_botero_soto.png`) y las 7 imágenes por línea de `mcp_server/pdf/assets/imagenes del pdf`, renombradas a ASCII (`nacional.jpg`, `distribucion.jpg`, `almacenamiento.jpg`, `itr.jpg`, `importacion.jpg`, `exportacion.jpg`, `andino.jpg`) y reducidas de PNG de ~2 MB a JPEG de 1200 px de ancho (calidad 88, ~1,2 MB en total; en el PDF se ven a 122 mm, ~250 ppp). La fuente `Stilu` del CSS no tiene licencia disponible (pendiente de negocio, ADR-006). Imagen final medida el 2026-09-24: **94,6 MB comprimida / ~324 MB en disco** (+7 MB por PDFsharp-MigraDoc, MailKit/MimeKit/BouncyCastle, fuentes e imágenes).
- **Verificación dentro de la imagen (#11):** una sonda desechable (fuera del repo) cargó los ensamblados de `/app` con un `AssemblyLoadContext` sobre `Cotifly.Api.deps.json` y renderizó 3 PDF con `docker run --rm --entrypoint dotnet ... cotifly-api:dev /sonda/Sonda11.dll` como usuario `app`, sin `/etc/fonts` ni `/usr/share/fonts`: 3, 3 y 2 páginas, solo `Liberation Sans` y `Liberation Sans,Bold` embebidas como subconjunto (`/FontFile2`), "Cel" solo con teléfono, bloque aduanero solo en Internacional.
- **`ARG VERSION` y MSBuild (#11):** todo `ARG` de la etapa llega a los `RUN` como variable de entorno y MSBuild la lee como propiedad sin distinguir mayúsculas: `VERSION=dev` se volvía `$(Version)=dev` y el restore fallaba con `NU1105` ("dev" no es una versión válida). Estaba oculto porque la capa del restore venía de caché; apareció al cambiar los `.csproj`. Ahora el `ARG VERSION` se declara después del restore y el publish corre con `env -u VERSION` (el valor solo entra como `InformationalVersion`).

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
| `0002_identidad_estado_agente.sql` | `Sesiones.estadoAgente` (JSON) y `Sesiones.usuarioId`; `UsuariosWeb.idUsuarioExterno`; `Roles` (sembrada con `comercial` y `gerente_comercial` sin rol externo) y `UsuariosRoles` (en lugar de la `RolesMapeo` del borrador, #12); `TokensRefresco`, `TokensRevocados`; índices de consulta | **extensión aditiva pendiente de aprobación** (Sara + scalderon-bs). Paso de un solo sentido: después del 0002 **no** se re-ejecuta el 0001 (ver encabezado del 0002 y ADR-008) |
| `0003_metricas_reenviadas.sql` | `CREATE OR ALTER VIEW vActividadPorComercial`: `numCotizacionesEnviadas` cuenta `enviada` y `reenviada` (#12 parte B); mismas columnas, sin tocar tablas ni datos | **pendiente de aprobación**, idempotente |

- `updatedAt` (y `createdAt` si la entidad no lo trae) los fija `clsMarcasTiempoInterceptor` con `IClsReloj`: el esquema no tiene triggers.
- `Sesiones.estadoAgente` guarda `clsEstadoConversacion` como JSON camelCase (`clsConvertidorEstadoConversacion`).
- `clsIdentidadDbContext` (solo lectura, `NoTracking`, `SaveChanges` lanza) lee de `dbsolucionesti` las tres tablas del login (#12): `tblSAPUsuariosApp` (`ID` NUMERIC, `NOMBREUSUARIO`, `DESCRIPCIONUSUARIO`, `EMAIL`; nunca `CLAVE`), `tblSAPUsuarioRoles` (`ID`, `IDUSUARIO`, `IDROLES`, INT) y `tblSAPRoles` (`IDROLES`, `DESCRIPCION`). Esquema y tablas configurables (`Identidad:Esquema` = `dbo`, `Identidad:TablaUsuarios`, `Identidad:TablaUsuarioRoles`, `Identidad:TablaRoles`) mientras el DBA confirma la ubicación real.
- **Permisos de solo lectura para `cotifly_app` en `dbsolucionesti`** (pedir al DBA): `SELECT` por columna sobre `tblSAPUsuariosApp (ID, NOMBREUSUARIO, DESCRIPCIONUSUARIO, EMAIL)` —**sin `CLAVE`**, o una vista sin esa columna—, `tblSAPUsuarioRoles (ID, IDUSUARIO, IDROLES)` y `tblSAPRoles (IDROLES, DESCRIPCION)`. Nada de INSERT/UPDATE/DELETE.
- **Antes de habilitar el login:** fijar los `IDROLES` de ClientePro en `dbo_cotifly.Roles` (`UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES> WHERE codigo = 'comercial'`, ídem `gerente_comercial`). Sin eso nadie entra (fail-closed, ADR-009).
- `DWCubosEBSSAP` (`ConnectionStrings:Regional`, regional del comercial) llega con la parte B de #12.

**Cadenas de conexión** (`clsOpcionesConexiones`, variables en `.env.example`):

| Clave | Uso | Variable |
|---|---|---|
| `ConnectionStrings:Cotifly` | `dbo_cotifly`, lectura y escritura | `ConnectionStrings__Cotifly` |
| `ConnectionStrings:Identidad` | `dbsolucionesti`, solo lectura (usuarios y roles de ClientePro) | `ConnectionStrings__Identidad` |

Siempre SQL login (`cotifly_app`, pendiente con el DBA): el contenedor Linux no admite autenticación integrada. En `Production` la API no arranca si una cadena falta, es un placeholder, usa `Trusted_Connection`/`Integrated Security` o su `Password` es de ejemplo. Fuera de `Production` pueden quedar vacías: la API arranca y `GET /health/ready` responde 503.

**PDF y correo (#11).**

| Clave | Por defecto | Uso | Variable |
|---|---|---|---|
| `Pdf:StorageDir` | `/data/pdfs` | Carpeta del almacén (volumen; en la imagen es la única carpeta escribible por `app`) | `Pdf__StorageDir` |
| `Pdf:StorageModo` | `volumen` | Único implementado; `s3` no arranca (diferido, ADR-012) | `Pdf__StorageModo` |
| `Pdf:BaseUrl` | vacío | Base de `pdf_url` (`{BaseUrl}/pdfs/{folio}.pdf`); vacía deja la URL relativa, servida por `GET /pdfs/{archivo}` (#12) | `Pdf__BaseUrl` |
| `Correo:Habilitado` | `false` | Sin habilitar, el envío responde `CORREO_DESHABILITADO` (vista previa y destinatarios previos funcionan) | `Correo__Habilitado` |
| `Correo:Modo` | `smtp_relay` | Único implementado; `wscorreos` y `smtp` no arrancan con el correo habilitado (D3) | `Correo__Modo` |
| `Correo:SmtpRelay:Host` | vacío | Relay interno (en el Python `10.10.40.62`); obligatorio con el correo habilitado | `Correo__SmtpRelay__Host` |
| `Correo:SmtpRelay:Port` | `25` | Puerto del relay | `Correo__SmtpRelay__Port` |
| `Correo:SmtpRelay:FromFallback` | `notificaciones@boterosoto.com.co` | Remitente si el comercial no tiene correo en `UsuariosWeb` | `Correo__SmtpRelay__FromFallback` |
| `Correo:SmtpRelay:TimeoutSeg` | `20` | Límite de cada operación con el relay | `Correo__SmtpRelay__TimeoutSeg` |

- Con varias réplicas, el candado de la deduplicación (`clsCandadoEnvios`) es por proceso: haría falta `sp_getapplock` (HU-006.6). Un volumen local de PDFs tampoco se comparte entre nodos (ADR-012).
- Activar el correo en producción: confirmar el transporte real con el equipo (D3), autorizar la IP del nodo en el relay y poner `Correo__Habilitado=true` y `Correo__SmtpRelay__Host`.
- **Operación (para HU-006):**
  - El `From` es el correo del comercial (`@boterosoto.com.co`) aunque sale por el relay: revisar con TI que SPF/DMARC del dominio autoricen al relay para no caer en spam.
  - `replicas: 1` en Dokploy mientras no haya `sp_getapplock` para la deduplicación ni un volumen compartido para los PDFs; con varios nodos, ambos son requisito.
  - `/data/pdfs` debe ser escribible por el UID 1654: con un volumen con nombre Docker copia el dueño de la imagen; con un bind mount, `chown 1654:1654` en el host.
  - `EventosHerramienta` guarda entradas con datos personales (correos de clientes, nombres, NIT): definir la retención y la purga con datos/seguridad.

**Autenticación (#12, ADR-009).**

| Clave | Por defecto | Uso | Variable |
|---|---|---|---|
| `Jwt:Issuer` | `cotifly-api` | Claim `iss` emitido y exigido | `Jwt__Issuer` |
| `Jwt:Audience` | `clientepro` | Claim `aud` emitido y exigido | `Jwt__Audience` |
| `Jwt:SigningKey` | vacío | Clave HS256, **secreto**: al menos 32 bytes; en Production obligatoria y sin placeholders. Vacía fuera de Production: clave efímera por proceso (aviso en el log) | `Jwt__SigningKey` |
| `Jwt:AccessMinutes` | `60` | Vigencia del access (1–1440) | `Jwt__AccessMinutes` |
| `Jwt:RefreshDays` | `30` | Vigencia absoluta de la sesión de refresh desde el login (1–365) | `Jwt__RefreshDays` |
| `Identidad:Esquema` | `dbo` | Esquema de las tablas de `dbsolucionesti` | `Identidad__Esquema` |
| `Identidad:TablaUsuarios` / `TablaUsuarioRoles` / `TablaRoles` | `tblSAPUsuariosApp` / `tblSAPUsuarioRoles` / `tblSAPRoles` | Nombres de las tablas | `Identidad__TablaUsuarios`... |
| `LimiteAuth:PeticionesPorVentana` / `VentanaSegundos` | `30` / `60` | Límite de `/api/auth/*` por IP (429 al pasarlo) | `LimiteAuth__PeticionesPorVentana`... |

- Con varias réplicas todas deben compartir `Jwt:SigningKey`; un logout tarda hasta 60 s en verse en las demás.
- Detrás de Traefik la IP que ve el límite es la del proxy mientras no se configuren los encabezados reenviados (`ForwardedHeaders:*`, abajo).

**Seguridad HTTP (#12, parte B).** `clsOpcionesCors`, `clsOpcionesEncabezadosReenviados` y `clsOpcionesSeguridadHttp` (Api, `ValidateOnStart`):

| Clave | Por defecto | Uso | Variable |
|---|---|---|---|
| `Cors:OrigenesPermitidos[]` | vacío | Orígenes exactos del frontend de ClientePro (`https://host[:puerto]`, sin ruta, sin `/` final, sin comodines). **Obligatorio en Production** (no arranca vacío). Métodos GET/POST/PUT, encabezados `Authorization` y `Content-Type`, sin credenciales, preflight cacheado 10 min | `Cors__OrigenesPermitidos__0`, `__1`... |
| `ForwardedHeaders:Habilitado` | `false` | Toma la IP real de `X-Forwarded-For` (y el esquema de `X-Forwarded-Proto`) solo si la conexión viene de un proxy o red conocidos: la usa el límite de `/api/auth/*` | `ForwardedHeaders__Habilitado` |
| `ForwardedHeaders:ProxiesConocidos[]` / `RedesConocidas[]` | vacío | IPs de Traefik o redes CIDR (la overlay de Swarm, p. ej. `10.0.0.0/8`). Con `Habilitado=true` al menos una es obligatoria; una IP o CIDR inválido no arranca | `ForwardedHeaders__ProxiesConocidos__0`, `ForwardedHeaders__RedesConocidas__0` |
| `ForwardedHeaders:LimiteSaltos` | `1` | Saltos de proxy aceptados en `X-Forwarded-For` (1–10) | `ForwardedHeaders__LimiteSaltos` |
| `SeguridadHttp:LimiteCuerpoWebBytes` | `65536` | Tamaño máximo del cuerpo en `/web/*` (1 KB–10 MB); más grande → 413 `ErrorWeb` | `SeguridadHttp__LimiteCuerpoWebBytes` |

- Todas las respuestas llevan `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` y `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`; `/web/*` y `/pdfs/*` además `Cache-Control: no-store`. Kestrel no envía `Server`. HSTS lo pone Traefik (TLS termina ahí).
- Para HU-006.6: en Dokploy fijar `Cors__OrigenesPermitidos__0` con el dominio real de `clientepro-frontend` (pendiente, lo confirma frontend) y `ForwardedHeaders__Habilitado=true` con la red overlay de Traefik en `ForwardedHeaders__RedesConocidas__0`.

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

**Agente de IA (#12 parte C, ADR-003).** `clsOpcionesIA` (+ `clsValidadorOpcionesIA`) en Infraestructura y `clsOpcionesAgente` en Aplicación leen la misma sección `IA`:

| Clave | Por defecto | Uso | Variable |
|---|---|---|---|
| `IA:Proveedor` | vacío | `openai` \| `azure_openai` \| `openai_compatible` \| `gemini` \| `ollama` \| `anthropic`. Vacío: agente sin configurar (la API arranca; `/web/chat` responde 200 `{texto, error: true}`) | `IA__Proveedor` |
| `IA:Modelo` | vacío | Modelo, o deployment de Azure. Obligatorio con proveedor | `IA__Modelo` |
| `IA:ApiKey` | vacío | **Secreto**. Obligatoria con `openai`, `azure_openai`, `gemini` y `anthropic`; opcional con `ollama` y `openai_compatible` | `IA__ApiKey` |
| `IA:Endpoint` | vacío | Obligatorio con `azure_openai` (`https://<recurso>.openai.azure.com`, se usa su API v1 `/openai/v1/`) y `openai_compatible` (OpenRouter: `https://openrouter.ai/api/v1`); `gemini` usa `https://generativelanguage.googleapis.com/v1beta/openai/` y `ollama` `http://localhost:11434/v1/` si no se indica | `IA__Endpoint` |
| `IA:MaxTokens` | `4096` | Tope de tokens de salida por respuesta (256–200000) | `IA__MaxTokens` |
| `IA:MaxIteraciones` | `12` | Llamadas con herramientas por turno antes de cortar (1–50) | `IA__MaxIteraciones` |
| `IA:Rpm` | `8` | Peticiones por minuto al proveedor (limitador local; 0 lo desactiva) | `IA__Rpm` |
| `IA:TimeoutSeg` | `60` | Tiempo máximo de cada llamada (5–600 s) | `IA__TimeoutSeg` |
| `IA:Fallbacks[]` | vacío | Proveedores de respaldo (`Proveedor`, `Modelo`, `ApiKey`, `Endpoint`), en orden, ante 429, 5xx o red caída. En Production sus claves pasan la misma regla de secretos | `IA__Fallbacks__0__Proveedor`... |
| `LimiteChat:PeticionesPorVentana` / `VentanaSegundos` | `20` / `60` | Límite de `POST /web/chat` por comercial (429 de la propia API) | `LimiteChat__PeticionesPorVentana`... |
| `Sesiones:LimiteTokens` | `6000` | Ventana del historial que ve el modelo | `Sesiones__LimiteTokens` |
| `Sesiones:TtlHoras` | `24` | Horas de inactividad tras las que el barrido archiva la sesión | `Sesiones__TtlHoras` |
| `Sesiones:BarridoMinutos` | `60` | Minutos entre barridos (0 lo desactiva; el Python usaba `sesion_barrido_seg`) | `Sesiones__BarridoMinutos` |

- **Anthropic** usa el SDK oficial (`Anthropic` 12.50, MIT), que trae su `IChatClient` (`AsIChatClient`); no hace falta el camino compatible con OpenAI. Todos los demás proveedores usan el SDK de OpenAI (`OpenAI` 2.14 + `Microsoft.Extensions.AI.OpenAI`), así que el Python con LiteLLM se reemplaza por configuración: `gemini/gemini-2.5-flash` → `IA__Proveedor=gemini`, `IA__Modelo=gemini-2.5-flash`; `openrouter/...` → `openai_compatible` con el endpoint de OpenRouter.
- El SDK de OpenAI ya reintenta los 429 y 5xx con espera exponencial (como `llm_num_retries`); `IA:Fallbacks` actúa cuando esos reintentos se agotan.
- La API key nunca se registra: `UseLogging` de Microsoft.Extensions.AI solo escribe el contenido de los mensajes en nivel Trace (la configuración por defecto es Information).
- Pendiente de negocio (ADR-003): proveedor y modelo de producción y custodia de la clave (secreto en Dokploy, HU-006.5).

**Google Chat (#12 parte C, ADR-011).** `clsOpcionesGoogleChat` (+ `clsValidadorOpcionesGoogleChat`) y `clsOpcionesFirmaUrlPdf`:

| Clave | Por defecto | Uso | Variable |
|---|---|---|---|
| `GChat:ProjectNumber` | vacío | Número del proyecto de Google Cloud de la app: audiencia del JWT. Sin él (con la verificación activa) todo evento responde 401 | `GChat__ProjectNumber` |
| `GChat:VerificarJwt` | `true` | `false` solo para pruebas con túnel (como `GCHAT_VERIFICAR_JWT`) | `GChat__VerificarJwt` |
| `GChat:SaJson` | vacío | **Secreto**: JSON de la cuenta de servicio o ruta al archivo (secreto de Docker). Sin él las respuestas no se publican (warning) | `GChat__SaJson` |
| `GChat:SupportSpace` | vacío | `spaces/...` para las alertas de error del worker | `GChat__SupportSpace` |
| `GChat:CapacidadCola` | `200` | Eventos en cola; si se llena, el evento se acusa pero no se procesa | `GChat__CapacidadCola` |
| `GChat:UrlCertificados` / `GChat:ApiBase` | certificados de `chat@system.gserviceaccount.com` / `https://chat.googleapis.com/v1` | Solo se cambian en pruebas | — |
| `Pdf:ClaveFirmaUrl` | vacío | **Secreto** (≥ 32 caracteres) del enlace firmado del PDF. Vacía: clave efímera por proceso (warning; no sirve con varias réplicas) | `Pdf__ClaveFirmaUrl` |
| `Pdf:MinutosUrlFirmada` | `15` | Vigencia del enlace firmado (1–120) | `Pdf__MinutosUrlFirmada` |

- Para abrir el PDF desde Chat, `Pdf:BaseUrl` debe ser la URL pública de la API (el enlace de la tarjeta es `{Pdf:BaseUrl}/pdfs/{folio}.pdf?t=...`).
- La cola y el worker viven en el proceso (como el Python): con varias réplicas cada una atiende los eventos que recibe; un reinicio pierde los eventos encolados sin procesar (el comercial los reenvía).
- **Activar Google Chat (HU-006):** crear la app de Chat (endpoint HTTP `https://<api>/chat/events`), la cuenta de servicio con el alcance `chat.bot`, y fijar `GChat__ProjectNumber`, `GChat__SaJson` (secreto), `Pdf__BaseUrl` y `Pdf__ClaveFirmaUrl`. Cada comercial debe haber entrado una vez desde ClientePro y su correo de Google debe coincidir con `UsuariosWeb.email`.

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
- PDF (#11): **PDFsharp-MigraDoc 6.2.4** (MIT; build multiplataforma sin GDI ni WPF) con Liberation Sans embebida (SIL OFL 1.1). Correo: **MailKit 4.18.0** (MIT; trae MimeKit y BouncyCastle.Cryptography, MIT). Solo pruebas: **PdfPig 0.1.16** (Apache-2.0) para extraer texto, fuentes e imágenes de los PDF.
- Agente (#12 parte C, todo MIT): **Microsoft.SemanticKernel.Core 1.80.1** (Kernel, `KernelFunction`, `IFunctionInvocationFilter`; en Aplicación), **Microsoft.Extensions.AI 10.10.0** / **Abstractions 10.10.1** (`IChatClient`, `ChatClientBuilder`, `UseLogging`), **Microsoft.Extensions.AI.OpenAI 10.10.1** con **OpenAI 2.14.0** (openai, azure_openai, openai_compatible, gemini, ollama) y **Anthropic 12.50.0** (SDK oficial de Anthropic con su `IChatClient`). Google Chat sin SDK de Google: JWT y token de la cuenta de servicio con `Microsoft.IdentityModel.JsonWebTokens` (ya presente). Solo pruebas: NSubstitute también en `Cotifly.Infraestructura.Tests`.

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

**Configuración validada al arrancar** (`src/Cotifly.Infraestructura/Configuracion/`): cada sección se enlaza a una clase `clsOpcionesXxx` con DataAnnotations y se registra con `AddOpcionesValidadas<T>()`, que agrega `ValidateOnStart` y el validador de secretos. En `Production`, una propiedad marcada con `[clsSecreto]` vacía, con placeholder (`<secreto>`, `cambiar…`, `changeme`, `dev`) o más corta que su mínimo impide arrancar; el mensaje nombra la clave, nunca el valor. Opciones actuales: `Zona:ZonaHoraria` (por defecto `America/Bogota`, debe existir en el sistema), `ConnectionStrings:Cotifly` / `ConnectionStrings:Identidad` (`[clsSecreto]` + `clsValidadorOpcionesConexiones`, ver §3), `Identidad:Esquema` y `SapTm:SalesOrgDefault` (`clsOpcionesMargen`, definida en Aplicacion porque la consume el resolver de margen; por defecto `50000005`, obligatoria y de máximo 12 caracteres, #10) y `SapTm:Modo` / `ExportPath` / `Url` / `User` / `Pass` (`clsOpcionesSapTm` + `clsValidadorOpcionesSapTm`, #13: modo desconocido, `export_json` sin ruta u `odata` sin URL, usuario o clave impiden arrancar en cualquier entorno; `Pass` lleva `[clsSecreto(Opcional = true)]`, que en `Production` solo revisa el valor cuando existe, porque sin `odata` no hace falta clave; ver §3), `Pdf:BaseUrl` (`clsOpcionesPdf`, Aplicacion) y `Pdf:StorageDir` / `StorageModo` (`clsOpcionesAlmacenPdf` + validador: solo `volumen`), y `Correo:Habilitado` (`clsOpcionesCorreo`, Aplicacion) y `Correo:Modo` / `SmtpRelay:*` (`clsOpcionesCorreoTransporte` + validador: con el correo habilitado exige `smtp_relay`, host, remitente de respaldo, puerto 1–65535 y tiempo 1–300 s) (#11, ver §3), y `Jwt:*` (`clsOpcionesJwt`: `SigningKey` con `[clsSecreto(LargoMinimo = 32)]` más `clsValidadorOpcionesJwt`, que exige 32 bytes en cualquier entorno si tiene valor) e `Identidad:TablaUsuarios` / `TablaUsuarioRoles` / `TablaRoles` (#12, ver §3). `LimiteAuth:*` se valida en la Api (`clsOpcionesLimiteAuth`, DataAnnotations + `ValidateOnStart`), igual que `Cors:OrigenesPermitidos` (`clsValidadorOpcionesCors`: obligatorio en Production, orígenes exactos), `ForwardedHeaders:*` (`clsValidadorOpcionesEncabezadosReenviados`: IPs y CIDR válidos, al menos uno si está habilitado) y `SeguridadHttp:LimiteCuerpoWebBytes` (#12 parte B, ver §3). Desde #12 parte C: `IA:*` (`clsOpcionesIA` + `clsValidadorOpcionesIA`: proveedor conocido, modelo, clave y endpoint según el proveedor, también en los respaldos; `ApiKey` con `[clsSecreto(Opcional = true)]`) y `IA:MaxTokens` / `MaxIteraciones` (`clsOpcionesAgente`, Aplicación), `Sesiones:*` (`clsOpcionesSesiones`, Aplicación), `GChat:*` (`clsOpcionesGoogleChat` + validador: número de proyecto, URLs, cuenta de servicio legible, space de soporte; `SaJson` es secreto opcional), `Pdf:ClaveFirmaUrl` / `MinutosUrlFirmada` (`clsOpcionesFirmaUrlPdf`) y `LimiteChat:*` (Api, ver §3).

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
| POST | `/api/auth/login-clientepro` | `200 LoginResponse` (`access`, `refresh`, `user{id, username, email, nombre_completo, rol, ubicacion}`, `debeCambiarPassword=false`); `400 ErrorWeb` (cuerpo inválido); `401 {"error":"Credenciales inválidas"}` (cualquier rechazo); `429 ErrorWeb` |
| POST | `/api/auth/refresh` | `200 LoginResponse` con el par rotado; `401 {"error":"Inicia sesión de nuevo."}` (incluye cuerpo inválido, reuso y rotación perdida); `429` |
| POST | `/api/auth/logout` | Siempre `204` (con o sin `Authorization: Bearer`, con o sin `{"refresh": ...}`); `429` |
| GET | `/web/mis-cotizaciones` | `200 MisCotizaciones` `{total, cotizaciones: [CotizacionResumen]}`: solo las del comercial, máx. 100, de la más reciente a la más antigua |
| POST | `/web/cotizaciones/{folio}/enviar` | `200 RespuestaEnvio`; `400 ErrorWeb` (`CORREO_INVALIDO`) o `ErrorNegocio` (duplicado, sin PDF, relay); `404` si no es suya; `503 ErrorNegocio` `CORREO_DESHABILITADO` |
| POST | `/web/cotizaciones/{folio}/correo/vista-previa` | `200 VistaPreviaCorreo` (cuerpo opcional); `404` |
| POST | `/web/cotizaciones/{folio}/seguimiento` | `200 CotizacionResumen`; `400` (motivo > 2000, cuerpo inválido); `404` |
| GET | `/web/cotizaciones/{id}/nueva-version` | `200 DatosNuevaVersion` (`folio_base`, `version_siguiente`...); `404` (también si `id` no es UUID) |
| GET | `/web/clientes/{nit}/destinatarios-previos` | `200 Destinatarios` del último envío del comercial a ese NIT (vacío si no hay); `400` si el NIT supera 20 caracteres |
| GET / PUT | `/web/usuario/telefono` | `200 TelefonoComercial`; PUT `400` fuera del patrón; `401` si el comercial ya no existe |
| GET | `/web/admin/lineas-negocio` | `200 [LineaNegocio]` (todas); `403` salvo `gerente_comercial` |
| PUT | `/web/admin/lineas-negocio/{id}` | `200 LineaNegocio` (auditado); `400` (margen fuera de 0–0,99, método, campo faltante, número como texto); `403`; `404` |
| GET | `/web/metricas/actividad` | **Borrador.** `200 [ActividadComercial]`: el comercial su fila; el gerente su regional |
| GET | `/web/metricas/ajustes-margen` | **Borrador.** `200 [AjustesMargenComercial]`, mismo alcance |
| GET | `/pdfs/{archivo}` | `200 application/pdf` inline (dueño o gerente de su regional con Bearer; o el dueño con el enlace firmado `?t=` de Google Chat, 15 min); `401` (sin sesión, o enlace alterado o vencido); `404 ErrorWeb` (no existe o no es tuyo) |
| POST | `/web/chat` | `200 RespuestaChat` `{texto, herramientas, precio, margen, pdf}` (los números salen de las herramientas del turno); `200 RespuestaChatError` `{texto, error: true}` ante un fallo del agente o del proveedor (texto del límite de peticiones del Python si es un 429); `400` (`sesion_id inválido`, mensaje vacío o de más de 8000); `401`; `404 {"error":"Sesión no encontrada."}` (sesión de otro comercial); `429` (`LimiteChat`) |
| POST | `/chat/events` | Webhook de Google Chat (sin Bearer de ClientePro): `200` acuse (`{"text": ...}`, `hostAppDataAction...` en add-on, `{}` si no es mensaje); `400 {"error":"payload no es JSON"}`; `401 {"error":"JWT inválido o ausente"}` |

Endpoints protegidos (parte B de #12): `Authorization: Bearer <access>`; sin token, vencido, de otra clave o revocado → `401 {"error":"Inicia sesión de nuevo."}`; rol insuficiente → `403 {"error":"No tienes permiso para esta operación."}`.

`/health/ready` ejecuta los health checks con etiqueta `ready` (hoy `clsBaseDatosHealthCheck`: `CanConnectAsync` sobre `clsCotiflyDbContext` más verificación de `Sesiones.estadoAgente` y `TokensRefresco`, con un límite de 5 s para toda la sonda). `detalle` nunca incluye la cadena ni el servidor; la excepción queda en el log. Ninguno de los dos exige autenticación. El `HEALTHCHECK` de la imagen (#27) consulta `/health` (vida), no `/health/ready`: una base caída no debe hacer que Swarm reinicie el contenedor.

Todas las operaciones del contrato están implementadas (`clsContratoRutasTests.PendientesParteC` quedó vacía con la parte C de #12). Todos los endpoints de `/web/*` y `/pdfs` sin token responden `401 {"error":"Inicia sesión de nuevo."}` (prueba que recorre el contrato). El resto de la documentación de endpoints se completa en HU-003.8 (#14).

## 8. Testing

| Proyecto | Qué cubre hoy | Unitarias | Integración |
|---|---|---|---|
| `Cotifly.Dominio.Tests` | `clsResultado<T>`, `clsErrorDominio`, `clsCodigoEnum` (códigos de los enums) e invariantes de las entidades (cotización, correo, margen, sesión, identidad, tokens), regla de margen por método (`margen` < 1, `markup` ≥ 1, también en `clsMargenAjuste`). **#10:** `clsCalculadoraPrecio` (todos los casos de `test_precio.py`, grilla determinista en lugar del property test, puntos medios half-to-even, desbordamiento) y `clsReglasMargen` (normalización, anomalía, líneas sin tildes ni espacios), redondeo del ajuste a 4 decimales. **#12:** `clsRol`, `clsUsuarioRol` (asignación, retiro con revisión) y el vínculo `idUsuarioExterno`. **#12 parte B:** `clsFolio` (formato con y sin NIT, NIT solo dígitos, 001–999, interpretación de folios nuevos y heredados, versiones ≤ 50 caracteres) y `clsMargenPolitica.Actualizar` | 321 | — |
| `Cotifly.Aplicacion.Tests` | Registro de dependencias de Aplicación; `clsPrecioService`; `clsMargenResolverService` y `clsAjusteMargenService` con puertos simulados (NSubstitute) y la muestra SAP del Python (`tests/Comun/clsFuenteMargenMuestra`), SAP no configurado → política, `AJUSTE_MARGEN_INVALIDO`. **#13:** SAP caído → política con warning (logger falso), sin política y SAP caído → `MARGEN_FUENTE_NO_DISPONIBLE`, detalle con el `Value` crudo (`5%`, `9.75%`, `-19.2%`). **#11:** formato de dinero y fecha exactos en 6 culturas, limpieza de texto, validador del payload, constructor del documento (datos del ejemplo, imagen y leyenda por línea, bloques condicionales, multi, tarifa con costo/margen y `markup`, USD, textos del payload, servicios conexos, celular del payload ignorado, determinismo), `clsGenerarPdfService` (trazabilidad persistida, firma y celular, folio desde `referencia`, folio inválido, línea desconocida y folio duplicado sin renderizar, archivo existente, compensación, render fallido, comercial inexistente, `BaseUrl`), `clsCorreoService` (port de `test_correo.py`: confirmación, dueño, PDF ausente, duplicado sin mayúsculas ni orden, ventana con el reloj, fallo del transporte → fila `fallido`, `reenviada`, correo deshabilitado, envío sin registro, destinatarios inválidos), vista previa y destinatarios previos, `clsAuditoriaService` (JSON snake_case, truncado válido, ámbito propio, nunca lanza, excepción auditada y propagada, decorador del ajuste), teléfono (patrón de `TelefonoComercial`), seguimiento y registro de decoradores; coherencia con la trazabilidad (tarifa impresa = `PrecioFinal`, `PrecioFinal` = costo/margen, varias tarifas con un precio, moneda de filas y servicios), folio obligatorio del servidor, `DesdeJson` sin lanzar (7 JSON inválidos), negativos rechazados, palabras largas partidas, recarga dentro del candado. **#12:** login (cada rama: cuerpo inválido, persona inexistente, almacén caído al buscar y al leer roles, sin rol mapeado, roles sin `idRolExterno`, roles del body ignorados, JIT con datos del almacén, precedencia del gerente auditada, segundo ingreso, rol externo cambiado, sin asignación, NOMBREUSUARIO cambiado, identidad inconsistente, vinculación, alta concurrente, error interno, cancelación), refresh (rotación con familia y vencimiento heredados, reuso → revoca al usuario, rotación concurrente, vencido, rol cambiado, almacén caído, usuario inexistente), logout (siempre sin error, jti en memoria y en base, repetido), caché de jti revocados (recarga de 60 s, tolerancia de 5 min, fail-closed) y refresh opaco. **#12 parte B:** generador de folio (fecha de negocio, consecutivo por comercial, salto de folios tomados, agotamiento, fallo de la creación, versiones, candado), mis cotizaciones y nueva versión, `clsUrlPdf`, administración de líneas (solo gerente revalidado, validación, auditoría con antes/después), alcance de métricas (comercial, gerente con y sin regional) y descarga del PDF (dueño, gerente por regional, nombres inválidos, auditoría). **#12 parte C:** runner con el modelo guionizado (turno sin herramientas y llamada final sin herramientas, dos herramientas encadenadas con la salida real devuelta al modelo, tope de iteraciones, herramienta inexistente → `HERRAMIENTA_DESCONOCIDA`, error estructurado que llega al modelo, contexto del comercial y estado resuelto en el sistema, eventos user/assistant/herramienta/assistant, memoria entre turnos, sesión ajena, comercial inexistente, `enviar_correo` oculto sin correo, validador de la respuesta final, fallo del proveedor), filtros (el hallazgo de ADR-004 entre turnos, números del estado aunque el LLM mande otros, sin margen no hay precio, costo de la fuente, sin cliente confirmado no se consulta SAP, PDF sin confirmación o sin precio, candidatos y elección por NIT, ajuste negociado, auditoría sin duplicar y con la entrada inyectada, correo con destinatarios del mensaje), estado de la conversación, ventana del historial, parseo de eventos de Chat (clásico, add-on, clics), acuses, tarjetas y procesamiento de un evento (identidad por email, hilo ajeno, fallo avisado) | 397 | — |
| `Cotifly.Infraestructura.Tests` | `clsRelojSistema`, registro de dependencias (puertos, contextos), validador de secretos, opciones `Zona` y `ConnectionStrings`, convertidores, traducción de violaciones de unicidad. **Integración:** scripts 0001+0002 aplicados (y 0002 idempotente), deriva modelo EF ↔ catálogo en ambos sentidos (tablas, columnas, tipos, nulabilidad, collation, nombres y columnas de PK/FK, índices únicos/filtrados/`ix_` con orden e `INCLUDE`, CHECK), idempotencia del 0002 con foto completa del esquema, cada método de cada repositorio, aislamiento por dueño, folio duplicado → `FOLIO_DUPLICADO`, dedupe de correo con reloj falso, interceptor de `updatedAt`, PK/FK compuestas, CHECK JSON, contexto de identidad de solo lectura, health check (incluye base con solo el 0001 → esquema incompleto, y límite de tiempo de la sonda). **#10:** fuente de margen no configurada, proveedor de tarifas no disponible, registro por defecto (`TryAdd`) y opción `SapTm:SalesOrgDefault`; **integración:** resolver y ajuste negociado con el registro real sobre la semilla del 0001 (todos los `requiere_pg` de `test_margen.py`, las 8 líneas, líneas con tildes, ajuste `markup` ≥ 1, cotización ajena, SAP no configurado con el registro por defecto). **#13:** `clsFuenteMargenExportJson` (encuentra y no encuentra, las 10 filas de la muestra con el `Value` crudo, archivo ausente, 8 formas de export malformado, `Value` numérico, filas repetidas, caché que no relee sin cambios y recarga al cambiar, recuperación tras archivo roto o borrado, concurrencia), `clsFuenteMargenOData` contra `clsSapTmFalso` (URL exacta, `Accept`, Basic auth, barra final, vacío → null, primera fila, `Value` numérico, inyección de comillas en BP, organización y TrqType, `&` y `#` codificados, HTTP 401/404/500/503, timeout, red, cancelación del llamador, 7 respuestas inválidas, mensajes sin credenciales), selección por `SapTm:Modo` y validación de `clsOpcionesSapTm` (modos, obligatorios, URL, clave solo con espacios, `SapClient` de 3 dígitos, clave en Production), `sap-client` en la URL exacta, `[clsSecreto(Opcional)]`, tubería de resiliencia (reintenta 503, agota reintentos con 500, no reintenta 401), logs (una línea de warning sin traza, excepción en Debug, eventos de Polly en Debug), avisos de arranque (`clsAvisosArranqueSapTm`); `clsSapTmRealTests` (`Categoria=sap`, se omite sin variables: 1 omitida); **integración:** resolver con `export_json` y `odata` reales sobre la semilla (SAP TM, sin registro, export ausente y OData caído → política, sin BP no llama a SAP) y conmutación export → OData con la misma salida para 13 consultas. **#11:** render real con PdfPig (3 páginas con cada bloque en su página, pie en todas, "Cel" solo con teléfono, aduana solo en Internacional, multi en 2 páginas, solo Liberation Sans embebida como subconjunto, logo en cada página e imagen de línea en la carta, `<script>` literal, tabla de 40 filas sin perder filas, render estable, palabras de 300 caracteres sin espacios que no tapan la tarifa ni agregan páginas) y los 3 PDF de la comparación visual en `tests/artefactos/`; almacén (nombres, traversal, sin reemplazar, lectura por referencia), recursos y licencia embebidos, resolvedor de fuentes, transporte MailKit contra `tests/Comun/clsServidorSmtpFalso` (port de `test_smtp_relay.py`: remitente comercial y `Reply-To`, To/Cc, asunto, texto + HTML + PDF base64, envelope, respaldo, relay que rechaza, relay inalcanzable, cancelación, destinatario mal formado), configuración de `Pdf`/`Correo` y fecha de negocio en Bogotá; **integración:** repositorio de correos (envíos desde un instante, destinatarios previos por comercial y NIT), generar PDF con el registro real (línea con tilde, archivo, fila, evento de auditoría que pasa `ISJSON`), envío por el relay falso (duplicado a los 4 min, `reenviada` a los 6), fila `fallido`, dos envíos simultáneos → `enviada` y luego `reenviada`, teléfono persistido y seguimiento con el caso de uso. **#12:** emisor JWT (HS256, claims, `exp`), opciones `Jwt` (32 bytes, secreto en Production, clave efímera); **integración:** almacén corporativo (nombre sin mayúsculas, ID numérico, IDs inválidos, roles con descripción, ID fuera de INT sin roles, sin `CLAVE` en el SQL, esquema y tablas configurables), `Roles`/`UsuariosRoles` (semilla, una asignación activa, retiro), vínculo único, rotación atómica (de dos simultáneas gana una), revocación por familia y por usuario, purga de jti y refresh (hosted service, sin base no lanza), deriva con 14 tablas y re-aplicación del 0002 sin pisar `idRolExterno`. **#12 parte B:** registro del candado y del repositorio de métricas; **integración:** folio con `sp_getapplock` (10 generaciones simultáneas del mismo comercial → 001…010, dos comerciales sin NIT sin colisión, consecutivo por comercial con y sin NIT, transacción deshecha ante fallo, 5 versiones simultáneas → v2…v6, `LIKE` escapado, candado no anidable), listado con línea, vistas de métricas (enviadas + reenviadas del 0003, regional, ajustes). **#12 parte C:** fábrica de proveedores de IA sin red (6 proveedores, API v1 de Azure, sin proveedor, decoradores), validación de `IA`, respaldo ante 429/5xx y no ante 400/401, estado HTTP de las excepciones de los SDK, limitador de RPM, JWT de Google con las fixtures `jwt_valido.txt`/`jwt_invalido.txt` y certificados falsos (audiencia, emisor, vigencia, bearer mal formado, caché de certificados, verificación desactivada), token de la cuenta de servicio (aserción firmada y reutilizada), forma de la petición del publicador, space inválido, error de la API de Chat, worker que sobrevive a un fallo y alerta, cola llena, URL firmada (válida, vencida, alterada, de otro archivo o comercial, clave efímera), validación de `GChat` y registro real; **integración:** identidad por `UsuariosWeb.email` (mayúsculas, correo repetido), barrido de sesiones por TTL y estado con los campos nuevos | 308 (1 omitida) | 148 |
| `Cotifly.Api.Tests` | `GET /health`, `GET /health/ready` (503 sin cadena y con base inalcanzable; con SQL Server real: **200**, y 503 `esquema incompleto: falta 0002` en una base con solo el 0001), composition root, arranque con configuración inválida, contrato `specs/openapi.yaml` (`/health` y `/health/ready` declarados, respuestas reales cumplen el esquema), sonda del `HEALTHCHECK` (`clsSondaSalud`: argumento `--healthcheck`, puerto desde `ASPNETCORE_HTTP_PORTS`, 200 → 0, otro estado / sin conexión / tiempo agotado → 1, prioridad `ASPNETCORE_URLS` → `ASPNETCORE_HTTP_PORTS` → 8080 con comodines e https ignorado, sin proxy, contra la API en memoria). **#10:** `SalidaPrecio`/`SalidaMargen` en snake_case cumplen el esquema y cada `clsCodigosError` existe en `ErrorNegocio`. **#12, integración:** `/api/auth/*` por HTTP contra la base real: `LoginResponse`/`UsuarioSesion` cumplen el contrato, access abre un endpoint protegido de prueba (políticas `Comercial`/`GerenteComercial`, 403), 401 sin token o con otra clave, 401 genérico, 400 `ErrorWeb`, almacén caído, rotación y reuso, dos refresh simultáneos, refresh inválido, logout que revoca access y refresh (también visto por otra réplica), logout siempre 204, 429. **#12 parte B:** recorrido del contrato (cada operación de `openapi.yaml` expuesta salvo `/web/chat` y `/chat/events` marcadas como pendientes, ningún `/web/*` fuera del contrato, 401 `ErrorWeb` sin token en cada una), convención snake_case por controller y propiedades exactas de cada DTO contra su esquema, tabla de errores (`clsRespuestasWeb`), CORS (preflight permitido y rechazado, encabezado en 401 y 413), encabezados de seguridad, 413, encabezados reenviados (IP real en el límite de auth, proxy desconocido ignorado) y validadores de opciones; **integración:** cada endpoint de `/web/*` y `/pdfs` por HTTP con validación del cuerpo contra el esquema del contrato, aislamiento A/B en cada operación (404 idéntico al de uno inexistente, cotización intacta), 400/415/503, PDF por dueño y gerente de la regional (auditado), administración 403/400/404 y auditoría, métricas por rol y regional. **#12 parte C:** `POST /chat/events` (JWT válido de la fixture → acuse y encolado, sin JWT o inválido → 401, evento que no es mensaje, add-on, verificación desactivada, cuerpo que no es JSON, cola llena), DTOs de `RespuestaChat`, `RespuestaChatError` y `MensajeWeb` contra su esquema y el recorrido del contrato sin pendientes; **integración:** `POST /web/chat` (200 `RespuestaChat` con precio y margen de las herramientas, 400 por `sesion_id` y mensaje, 401, 404 con sesión ajena, 200 `error: true` con el texto del límite y el genérico, sin proveedor configurado, 429 propio) y `/pdfs` con el enlace firmado (válido sin Bearer, alterado → 401, de otro comercial → 404) | 150 | 70 |
| `Cotifly.Arquitectura.Tests` | Dependencias entre capas (ArchUnitNET + referencias de ensamblado), Dominio sin paquetes externos y prefijos `cls`/`ICls` | 14 | — |
| `Cotifly.Aceptacion.Tests` | Reqnroll: `persistencia.feature` (guardar/recuperar, folio único, aislamiento por comercial, `updatedAt`), `precio.feature` (valores de la arquitectura, markup vs margen, half-to-even, rechazos) y `margen.feature` (SAP TM, sin BP, sin registro, anomalía, organización de ventas, tildes, línea desconocida, SAP caído → política, SAP no configurado, margen negociado; empates de medio peso documentados; **#13:** misma salida con los adaptadores reales `export_json` y `odata`, BP con comillas que no altera el filtro OData). **#11:** `pdf.feature` (10 escenarios: 3 páginas y fila registrada, sin teléfono sin "Cel", aduana solo en Internacional, multi, servicios conexos, folio duplicado sin reemplazar el PDF, línea desconocida sin PDF, línea con tilde, `<script>` literal, teléfono confirmado impreso), `correo.feature` (10: envío con remitente comercial y adjunto, `reenviada`, duplicado, fuera de ventana, cotización ajena, relay que rechaza → `fallido`, sin PDF, deshabilitado, destinatarios previos, vista previa) y `auditoria.feature` (4: PDF exitoso con folio, rechazo con código, envío, ajuste de margen). **#12:** `login_clientepro.feature` (10: JIT, por ID, segundo ingreso, rol externo cambiado, NOMBREUSUARIO cambiado, sin rol, roles del body, precedencia, inexistente, almacén caído) y `refresh_logout.feature` (7: rotación, reuso, concurrencia, rol cambiado, vencido, logout, logout sin tokens). **#12 parte B:** `folio.feature` (8), `aislamiento_comercial.feature` (10, por HTTP), `web_cotizaciones.feature` (9, por HTTP) y `admin_lineas_negocio.feature` (9, por HTTP). **#12 parte C:** `chat_slot_filling.feature` (3: el hallazgo de ADR-004 de punta a punta con SAP TM de muestra y el PDF persistido con el NIT, la línea y el margen resueltos sin que el LLM los mande; PDF sin confirmación; sin búsqueda de clientes → sin NIT y política), `chat_web.feature` (4) y `google_chat.feature` (3, con Google falso: remitente sin cuenta, previsualización publicada en el hilo con sus botones, JWT inválido) | — | 124 |

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
| `registrar_ajuste_margen` (`server.py`, sin test propio) | `clsAjusteMargenServiceTests`; `clsMargenResolverIntegracionTests.ElAjusteNegociadoSePersisteConElComercialYLaCotizacion`; `margen.feature`; auditado: `clsAuditoriaServiceTests.ElAjusteDeMargenNegociadoSeAudita`, `auditoria.feature` |
| `test_html_contiene_datos_de_la_cotizacion` | `clsConstructorDocumentoCotizacionTests.ElDocumentoTieneLosDatosDeLaCotizacion`; `clsGeneradorPdfMigraDocTests.LaSolicitudDeEjemploTieneTresPaginasConLosBloquesEnSuSitio`; `pdf.feature` |
| `test_html_determinista` | `clsConstructorDocumentoCotizacionTests.ElDocumentoEsDeterminista`; `clsGeneradorPdfMigraDocTests.ElRenderEsEstableEnPaginasYTexto` |
| `test_bloque_aduanero_solo_internacional` | `clsConstructorDocumentoCotizacionTests.ElBloqueAduaneroSoloApareceEnLaLineaInternacional`; `clsGeneradorPdfMigraDocTests.ElBloqueAduaneroSoloApareceEnInternacional`; `pdf.feature` |
| `test_bloque_extradimensionada_y_refrigerada` | `clsConstructorDocumentoCotizacionTests.LosBloquesExtradimensionadaYRefrigeradaMuestranSoloLoQueTraen`; `clsGeneradorPdfMigraDocTests.ElBloqueAduaneroSoloApareceEnInternacional` |
| `test_multi_seccion_usa_armar_multi` | `clsConstructorDocumentoCotizacionTests.ConSeccionesSeUsaLaVarianteMulti`; `clsGeneradorPdfMigraDocTests.LaVarianteMultiTieneDosPaginasConLasCondicionesEnLaPrimera`; `pdf.feature` |
| `test_render_pdf_genera_archivo_y_paginas` (weasyprint) | `clsGeneradorPdfMigraDocTests.LaSolicitudDeEjemploTieneTresPaginasConLosBloquesEnSuSitio` (exactamente 3 páginas; el Python pedía ≥ 3) |
| `test_folio_versionado` | Sin equivalente a propósito: el folio lo genera #12 (ADR-011); aquí la colisión es `FOLIO_DUPLICADO` (`clsGenerarPdfServiceTests.UnFolioExistenteEsFolioDuplicadoSinRenderizar`, `pdf.feature`) |
| `test_generar_pdf_dod_via_mcp` | `clsGenerarPdfServiceTests.GeneraGuardaYRegistraLaCotizacionConSuTrazabilidad`; `clsPdfCorreoIntegracionTests.GenerarElPdfRegistraLaCotizacionGuardaElArchivoYAudita`; `pdf.feature` |
| `test_generar_pdf_linea_negocio_desconocida_hoy_falla_entero` (pin del bug #44) | Corregido: `clsGenerarPdfServiceTests.UnaLineaDesconocidaSeRechazaAntesDeRenderizar`; con tilde se resuelve: `clsPdfCorreoIntegracionTests`, `pdf.feature` |
| `test_generar_pdf_idempotencia_versiona` | Reemplazado por `FOLIO_DUPLICADO` sin sobrescribir el PDF: `clsGenerarPdfServiceTests.SiElArchivoYaExisteEnElAlmacenNoSeReemplazaYEsFolioDuplicado`, `pdf.feature` |
| `test_generar_pdf_persiste_trazabilidad_completa` | `clsGenerarPdfServiceTests.GeneraGuardaYRegistraLaCotizacionConSuTrazabilidad` (la trazabilidad llega del estado, no del payload) |
| `test_generar_pdf_tolera_cliente_como_objeto` | Sin equivalente: el payload es tipado; el cliente se resuelve por catálogo (HU-005.2) y el NIT llega en la trazabilidad |
| `test_generar_pdf_sin_referencia_error` | `clsGenerarPdfServiceTests.SinFolioNiReferenciaEsPayloadInvalido` |
| `test_generar_pdf_render_no_disponible_error` | `clsGenerarPdfServiceTests.UnFalloDelRenderEsPdfRenderNoDisponible` |
| `test_envio_ok_adjunta_pdf_y_persiste` | `clsCorreoServiceTests.ElEnvioAdjuntaElPdfSaleDelComercialYQuedaRegistrado`; `correo.feature` |
| `test_sin_confirmado_por_rechaza_y_no_envia` | `clsCorreoServiceTests.SinComercialAutenticadoNoSeEnvia` |
| `test_cotizacion_no_encontrada` | `clsCorreoServiceTests.UnaCotizacionAjenaOInexistenteNoSeEnvia`; `correo.feature` |
| `test_pdf_no_encontrado` | `clsCorreoServiceTests.SinPdfEnElAlmacenNoSeEnvia`; `correo.feature` |
| `test_duplicado_menos_5min_rechaza` | `clsCorreoServiceTests.UnEnvioALosMismosDestinatariosEnMenosDeCincoMinutosEsDuplicado`; `clsPdfCorreoIntegracionTests.ElEnvioDeduplicaConElRelojDeLaAplicacionYPasaAReenviada`; `correo.feature` |
| `test_duplicado_mas_5min_permite` | `clsCorreoServiceTests.LaVentanaDeDeduplicacionSeMideConElRelojDeLaAplicacion`; `clsCorreoRepositoryTests.UnEnvioAnteriorAlInstanteNoSeLista`; `correo.feature` |
| `test_distintos_destinatarios_no_es_duplicado` | `clsCorreoServiceTests.OtroConjuntoDeDestinatariosNoEsDuplicado` |
| `test_fallo_smtp_eleva_envio_smtp_error` | `clsCorreoServiceTests.UnFalloDelTransporteQuedaRegistradoComoFallidoYNoCambiaElEstado`; `clsTransporteSmtpRelayTests.UnRelayInalcanzableEsEnvioFallido`; `correo.feature` |
| `test_now_estable_en_transaccion` (Postgres) | Sin equivalente: la ventana se mide con `IClsReloj` (el interceptor fija `createdAt` con el mismo reloj) |
| `test_relay_arma_mensaje_con_remitente_comercial_y_adjunto` | `clsTransporteSmtpRelayTests.ArmaElMensajeConElRemitenteComercialElCuerpoYElAdjunto` |
| `test_relay_sin_confirmado_usa_fallback` | `clsTransporteSmtpRelayTests.SinCorreoDelComercialUsaElRemitenteDeRespaldo` |
| `test_relay_fallo_de_red_eleva_error` | `clsTransporteSmtpRelayTests.UnRelayInalcanzableEsEnvioFallido` (+ `SiElRelayRechazaLosDestinatariosPideAutorizarLaIpDelServidor`) |
| `tests/test_ws_correos.py` (WSCorreos SOAP) | Sin equivalente: `wscorreos` no se porta hasta confirmar el transporte real (D3) |
| Middleware de auditoría (`mcp_server/auditoria.py`, cubierto por `test_generar_pdf_dod_via_mcp`) | `clsAuditoriaServiceTests`; `clsPdfCorreoIntegracionTests`; `auditoria.feature`; en el agente, `clsInyeccionEstadoTests.LaAuditoriaDelFiltroNoDuplicaLaDeLosCasosDeUso` |
| `test_loop_tool_use_invoca_mcp_y_cierra` | `clsAgenteRunnerTests.DosHerramientasEncadenadasDevuelvenSuSalidaRealAlModelo` (+ `SinHerramientasElTurnoCierraConLaRespuestaDeLaLlamadaFinal`) |
| `test_tools_declaradas_excluyen_ping` | `clsAgenteRunnerTests.SinHerramientasElTurnoCierraConLaRespuestaDeLaLlamadaFinal` (las 7 declaradas, sin `ping`) y `EnviarCorreoSoloSeDeclaraConElCorreoHabilitado` |
| `test_max_iteraciones_excedidas` | `clsAgenteRunnerTests.SiElModeloNuncaCierraElLoopCortaEnElTope` |
| `test_registra_cada_turno_en_conversacion` | `clsAgenteRunnerTests.CadaTurnoQuedaEnLaConversacionConLasHerramientasYSusSalidas` |
| `test_memoria_entre_turnos_alimenta_contexto` | `clsAgenteRunnerTests.ElSegundoTurnoLlevaLaSolicitudOriginalYLasRespuestasPrevias` |
| `test_integracion_llm_flujo_cotizacion` (marca `llm`, tokens reales) | Sin equivalente automático: las pruebas no consumen tokens (ADR-003); validación manual con el proveedor de producción (pendiente) |
| `test_contexto_antepone_identidad_del_comercial`, `test_ejecutar_turno_envia_system_con_contexto` | `clsAgenteRunnerTests.ElSistemaLlevaElContextoDelComercialYElEstadoResuelto` |
| `test_contexto_vacio_no_modifica_la_skill` | Sin equivalente: el comercial siempre existe (token o email) y el bloque de contexto siempre va |
| `test_invocar_inyecta_space_y_thread_en_tools_que_los_aceptan` | `clsInyeccionEstadoTests.ElClienteIdentificadoEnElTurnoUnoLlegaAlPdfDelTurnoDosSinQueElLlmLoMande` (hilo, comercial, NIT, BP) |
| `test_invocar_no_pisa_valores_del_llm_ni_agrega_no_aceptados` | Invertido a propósito (ADR-004): el estado **sí** pisa lo que manda el LLM: `CalcularPrecioUsaElMargenYElMetodoDelEstadoAunqueElLlmMandeOtros`, `SinClienteConfirmadoObtenerMargenNoConsultaSapConLoQueMandeElLlm` |
| (sin equivalente en Python: el hallazgo del cliente entre turnos) | `clsInyeccionEstadoTests.ElClienteIdentificadoEnElTurnoUnoLlegaAlPdfDelTurnoDosSinQueElLlmLoMande`; `chat_slot_filling.feature` |
| `test_jwt_valido_ack_200`, `test_jwt_ausente_401`, `test_jwt_invalido_401`, `test_bearer_malformado_401` | `clsChatEventosEndpointTests.ConElJwtValidoAcusaDeInmediatoYEncolaElMensaje`, `SinJwtOConUnoInvalidoEs401YNoEncola`; `clsGoogleChatInfraestructuraTests.ElJwtValidoDeLaFixturePasa`, `ElJwtConLaFirmaDeOtraClaveNoPasa`, `SinBearerNoPasa`; `google_chat.feature` |
| `test_ack_no_espera_procesamiento` | `clsChatEventosEndpointTests.ConElJwtValidoAcusaDeInmediatoYEncolaElMensaje` (cola falsa sin worker) |
| `test_evento_no_mensaje_no_encola` | `clsChatEventosEndpointTests.UnEventoQueNoEsMensajeAcusaSinCuerpoYNoEncola`; `clsGoogleChatTests.UnEventoQueNoEsMensajeNoDisparaTurno` |
| `test_parsear_evento_real`, `test_parsear_captura_nombre_del_comercial`, `test_parsear_formato_nuevo_chat_messagepayload` | `clsGoogleChatTests.ParseaElEventoRealDelFormatoClasico`, `CapturaElNombreDelComercial`, `ParseaElFormatoAddOnConElTipoInferido` |
| `test_webhook_sin_jwt_cuando_verificacion_desactivada`, `test_webhook_formato_addon_ack_hostappdataaction` | `clsChatEventosEndpointTests.ElFormatoAddOnAcusaConHostAppDataActionYSinVerificacionNoPideJwt`; `clsGoogleChatInfraestructuraTests.ConLaVerificacionDesactivadaTodoPasa` |
| `test_worker_procesa_encolados`, `test_worker_sobrevive_a_evento_que_falla` | `clsGoogleChatInfraestructuraTests.ElWorkerConsumeLaColaEnSegundoPlano`, `ElWorkerSobreviveAUnEventoQueFallaYAlertaAlSoporte` |
| `test_worker_dispara_runner_y_crea_sesion` | `google_chat.feature` ("El comercial cotiza por Google Chat..."), sesión ligada al comercial |
| `test_google_verifier_pasa_audience_y_certs`, `test_google_verifier_emisor_inesperado`, `test_google_verifier_firma_invalida`, `test_google_verifier_sin_project_number` | `clsGoogleChatInfraestructuraTests.ExigeLaAudienciaDelProyectoElEmisorDeChatYLaVigencia`, `ElJwtConLaFirmaDeOtraClaveNoPasa`, `SinNumeroDeProyectoNadaPasa` |
| `test_formato_cop_y_pct` | `clsGoogleChatTests.FormateaPesosYPorcentajes` |
| `test_mensaje_previsualizacion_desde_calcular_precio`, `test_runner_final_publica_card_previsualizacion` | `clsGoogleChatTests.ConPrecioSePublicaLaPrevisualizacionConLosBotonesDeControlHumano`; `google_chat.feature` |
| `test_mensaje_pdf_desde_generar_pdf` | `clsGoogleChatTests.ConPdfSePublicaLaTarjetaConElEnlaceFirmado` (enlace firmado, ADR-011) |
| `test_mensaje_texto_cuando_no_hay_precio_ni_pdf`, `test_mensaje_ignora_salida_con_error` | `clsGoogleChatTests.SinPrecioNiPdfOConErrorSePublicaTexto` |
| `test_card_pdf_sin_url` | `clsGoogleChatTests.LaTarjetaDelPdfSinUrlNoTieneBoton` |
| `test_publicador_agrega_hilo_y_envia`, `test_construir_publicador_postea_a_chat_api` | `clsGoogleChatInfraestructuraTests.ElPublicadorPosteaALaApiDeChatConElTokenDeLaCuentaDeServicioYElHilo` (+ `LaAsercionDeLaCuentaDeServicioVaFirmadaConSuClave`) |
| `test_publicador_sin_space_falla` | `clsGoogleChatInfraestructuraTests.UnSpaceInvalidoSeRechaza` |
| `test_gating_sin_confirmacion_no_envia_correo` | `clsInyeccionEstadoTests.ElCorreoExigeDestinatariosEscritosPorElComercialYUsaLaCotizacionDelEstado`, `ElPdfSinConfirmacionRespondeConfirmacionRequeridaYNoSeGenera` |
| `test_procesador_no_mensaje_no_publica`, `test_procesador_corre_turno_y_publica` | `clsGoogleChatTests.UnEventoQueNoEsMensajeNoPublica`, `UnComercialConocidoCotizaYLaRespuestaSePublicaEnElHilo` |
| `test_flujo_ajuste_margen_republica` | `clsInyeccionEstadoTests.ElAjusteNegociadoTomaLaReferenciaYElCostoDelEstado` |
| (sin equivalente en Python: identidad de Chat por email, ADR-011) | `clsGoogleChatTests.UnRemitenteSinCuentaRecibeElAvisoYNoCotiza`; `clsAgentePersistenciaTests.ElComercialDeGoogleChatSeEncuentraPorSuCorreoSinDistinguirMayusculas`; `google_chat.feature` |
| `test_recuperar_o_crear_y_aislamiento`, `test_registrar_mensaje_actualiza_actividad` | `clsSesionRepositoryTests` (#9); sesión ligada al comercial: `clsAgenteRunnerTests.LaSesionDeOtroUsuarioNoSeContinua`, `clsChatWebEndpointTests.LaSesionDeOtroComercialEs404` |
| `test_historial_completo_si_cabe`, `test_historial_trunca_conservando_primero`, `test_historial_vacio`, `test_estimar_tokens` | `clsVentanaHistorialTests.SiCabeSeEntregaCompleto`, `TruncaConservandoElPrimeroYLosMasRecientes`, `UnHistorialVacioQuedaVacio`, `EstimaUnTokenCadaCuatroCaracteres` |
| `test_historial_conserva_primero_aunque_exceda` | `clsVentanaHistorialTests.ConservaElPrimeroYElUltimoAunqueExcedan` (el mensaje actual también se conserva) |
| `test_archivar_inactivas_por_timeout` | `clsAgentePersistenciaTests.ElBarridoArchivaLasSesionesInactivasDesdeHaceMasDelTtl` |
| `test_e2e_feliz_cotizar_y_enviar` | `chat_slot_filling.feature` (solicitud → previsualización → confirmación → PDF persistido); el envío, `correo.feature` y `clsInyeccionEstadoTests.ElCorreoExigeDestinatarios...` |
| `test_e2e_sin_tarifa_pide_candidatas_no_inventa` | La fuente de tarifas ya no existe (Drive retirado): `TARIFA_FUENTE_NO_DISPONIBLE` llega al modelo como error estructurado (`clsAgenteRunnerTests.ElErrorEstructuradoDeUnaHerramientaLlegaAlModelo`) y sin margen no hay precio (`SinMargenResueltoCalcularPrecioSeRechazaSinInventarlo`) |
| `test_e2e_margen_anomalo_se_reporta` | `obtener_margen` devuelve `anomalo` tal cual (`clsMargenResolverServiceTests`, #10); el agente lo recibe en `SalidaMargen` |
| `web_chat` (`orquestador/web.py`, sin test propio) | `clsChatWebEndpointTests` (200, 400, 401, 404, `error: true`, 429); `chat_web.feature` |
- **BDD (Reqnroll):** los `.feature` viven en `specs/features/` (fuente de verdad SDD) y `Cotifly.Aceptacion.Tests` los enlaza; el code-behind se genera en `obj/`. Se usa `Reqnroll.xunit.v3` (Reqnroll 3.x soporta xUnit v3 y corre sobre MTP como el resto), así que no hace falta MSTest. Los pasos usan el `AddInfraestructura` real contra SQL Server en Testcontainers.
- **Integración (Testcontainers.MsSql):** imagen `mcr.microsoft.com/mssql/server:2019-latest`. `tests/Comun/clsBaseDatosPruebas` crea `dbTarifario_Nexo_Pruebas`, aplica `0001`, `0002` y `0003` partidos por líneas `GO` (como sqlcmd) y crea una `dbsolucionesti` falsa con las tres tablas del login y sus tipos reales (`tblSAPUsuariosApp` con `ID` NUMERIC y `CLAVE`, `tblSAPUsuarioRoles`, `tblSAPRoles` con los roles de prueba 34 Comercial, 35 Gerente Comercial y 40 Consulta), fija `Roles.idRolExterno` a 34 y 35 y ofrece `CrearPersonaExternaAsync`, `CambiarRolesExternosAsync` y `RenombrarPersonaExternaAsync` (#12). Un contenedor por proyecto de pruebas (colección xUnit `SqlServer`), que solo se crea si corre alguna prueba de integración. Cada prueba usa datos propios (folios y usuarios únicos). Las cadenas de prueba llevan `Command Timeout=120` (`clsBaseDatosPruebas.TiempoMaximoOrdenSeg`): el SQL Server del contenedor corre sobre el disco virtual de Docker Desktop (WSL2) y a veces se detiene en E/S más de los 30 s por defecto (visto en #10: `CREATE DATABASE` del fixture y consultas sueltas con "Execution Timeout Expired (258)", en pruebas distintas y sin relación con los datos). La API conserva el valor por defecto.
- Los proyectos con integración declaran `<UsaSqlServerPruebas>true</UsaSqlServerPruebas>`: `tests/Directory.Build.props` agrega Testcontainers, PdfPig (#11), el soporte compartido de `tests/Comun/` (contenedor, reloj falso, DI de pruebas con los PDF en una carpeta temporal propia, relay SMTP falso `clsServidorSmtpFalso`, lector de PDF `clsLectorPdf`) y copia `../migrations_mssql/*.sql` y `tests/fixtures/**/*.json` a la salida.
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
│   ├── features/                # escenarios Gherkin (Reqnroll): persistencia, precio, margen, pdf, correo, auditoria, login_clientepro, refresh_logout, folio, aislamiento_comercial, web_cotizaciones, admin_lineas_negocio, chat_slot_filling, chat_web, google_chat
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
    ├── fixtures/cotizacion/      # ejemplo-solicitud.json del Python (payload del PDF, #11)
    ├── fixtures/chat/            # evento_mensaje.json del Python; jwt_valido/jwt_invalido.txt y certs_chat_falsos.json (certificado falso de Google Chat, #12 parte C)
    ├── artefactos/               # PDF de la comparación visual que generan las pruebas (#11, no versionado)
    ├── Cotifly.Dominio.Tests/
    ├── Cotifly.Aplicacion.Tests/
    ├── Cotifly.Infraestructura.Tests/
    ├── Cotifly.Api.Tests/
    ├── Cotifly.Arquitectura.Tests/
    └── Cotifly.Aceptacion.Tests/

../migrations_mssql/             # scripts de esquema SQL Server (0001, 0002, 0003): única vía de cambio de esquema
```
