# 2. Arquitectura

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

Clean Architecture de 4 capas en un monolito modular ([ADR-002](../specs/decisiones/ADR-002-arquitectura-clean-monolito-modular.md)), con un pipeline determinista (formulario, Nexo, SAP y código) que cotiza y una IA agnóstica al proveedor que solo redacta textos y asiste sobre una cotización ya hecha ([ADR-003](../specs/decisiones/ADR-003-orquestacion-ia-agnostica.md), [ADR-018](../specs/decisiones/ADR-018-pipeline-determinista-ia-etapa-final.md)). El agente cotizador conversacional con estado estructurado ([ADR-004](../specs/decisiones/ADR-004-estado-conversacion-slot-filling.md)) y Google Chat se retiraron en HU-013.9 (#105). Un solo proceso, `Cotifly.Api`, empaquetado en un solo contenedor ([ADR-007](../specs/decisiones/ADR-007-contenedores-sin-docker-compose.md)).

Contenido:

- [2.1 Vista de contexto](#21-vista-de-contexto)
- [2.2 Capas](#22-capas)
- [2.3 Módulos y carpetas](#23-módulos-y-carpetas)
- [2.4 Convenciones de nombres](#24-convenciones-de-nombres)
- [2.5 Modelo de dominio y persistencia](#25-modelo-de-dominio-y-persistencia)
- [2.6 Precio, margen y tarifas](#26-precio-margen-y-tarifas)
- [2.7 Margen de SAP TM: copia local](#27-margen-de-sap-tm-copia-local)
- [2.8 PDF, correo y auditoría](#28-pdf-correo-y-auditoría)
- [2.9 Identidad: login SSO con verificación doble](#29-identidad-login-sso-con-verificación-doble)
- [2.10 API web](#210-api-web)
- [2.11 Asistente IA por folio (/web/chat)](#211-asistente-ia-por-folio-webchat)
- [2.12 Turno de /web/chat de punta a punta](#212-turno-de-webchat-de-punta-a-punta)
- [2.13 Validador de la respuesta del agente](#213-validador-de-la-respuesta-del-agente)
- [2.14 Integración con Nexo](#214-integración-con-nexo)
- [2.15 Licitaciones desde Excel y cotizaciones de varias líneas](#215-licitaciones-desde-excel-y-cotizaciones-de-varias-líneas-hu-011-adr-017)

## 2.1 Vista de contexto

```mermaid
flowchart LR
    comercial([Comercial o gerente comercial])
    subgraph clientepro[ClientePro]
        front[Módulo Angular de Cotifly<br/>HU-004]
    end
    subgraph contenedor[Contenedor cotifly-api]
        api[Cotifly.Api<br/>ASP.NET Core 10]
    end
    subgraph sql[SQL Server 2019 corporativo]
        dbc[(dbTarifario_Nexo_Pruebas<br/>esquema dbo_cotifly<br/>incluye la copia local del margen de SAP TM)]
        dbi[(dbsolucionesti<br/>solo lectura)]
    end
    llm[Proveedor LLM]
    mails[MailSender<br/>microservicio interno de correo]
    nexo[Nexo<br/>clientes, municipios, catálogos, cubicación]
    pdfs[(Volumen /data/pdfs)]

    comercial --> front
    front -- "HTTPS /api/auth, /web, /pdfs" --> api
    api --> dbc
    api --> dbi
    api --> llm
    api -- "HTTP POST /api/v1/emails" --> mails
    api -- "HTTPS con X-Portal-clsUsuario-Id" --> nexo
    api --> pdfs
```

Nexo está conectado para la búsqueda de clientes (HU-005.2, [ADR-005](../specs/decisiones/ADR-005-busqueda-clientes-desde-chat.md)), el catálogo de municipios (HU-005.13), los catálogos de flete (HU-005.3) y la cubicación rápida (HU-005.4), siempre que `Nexo__UrlBase` esté configurada ([2.14](#214-integración-con-nexo)). El costeo de Nexo tiene su propio flujo (ADR-015, [2.14](#214-integración-con-nexo)). Sigue sin conectar, mientras no se cargue `ConnectionStrings__Regional` en el despliegue, `DWCubosEBSSAP` (regional del comercial: adaptador y login listos desde #81). Grovity no se integra en este backend: el alta de prospectos del formulario reutiliza el componente de `clientepro-frontend` contra el backend de ClientePro (corrección del 2026-09-27 en ADR-005); el agente que lo habría usado se retiró en HU-013.9. Detalle de cada dependencia en [03-infraestructura](03-infraestructura.md#35-servicios-externos).

## 2.2 Capas

```mermaid
flowchart TB
    Api["Cotifly.Api<br/>controllers, middleware, composition root"]
    Infra["Cotifly.Infraestructura<br/>EF Core, HTTP, PDF, correo, IA"]
    App["Cotifly.Aplicacion<br/>casos de uso, puertos ICls*, DTOs, agente"]
    Dom["Cotifly.Dominio<br/>entidades, value objects, reglas puras"]
    Api --> App
    Api -. "solo AddInfraestructura()" .-> Infra
    Infra --> App
    Infra --> Dom
    App --> Dom
```

| Proyecto | Responsabilidad | Depende de |
|---|---|---|
| `Cotifly.Dominio` | Entidades, value objects, reglas puras, `clsResultado<T>`, `clsErrorDominio` | nada |
| `Cotifly.Aplicacion` | Casos de uso, puertos `ICls*`, DTOs, validadores | Dominio |
| `Cotifly.Infraestructura` | Adaptadores de los puertos (EF Core, HTTP, PDF, correo, IA) | Aplicacion, Dominio |
| `Cotifly.Api` | Controllers, middleware, composition root | Aplicacion; Infraestructura solo para `AddInfraestructura()` |

Las reglas de dependencia las verifica `Cotifly.Arquitectura.Tests` (ArchUnitNET y referencias de ensamblado): Dominio sin paquetes externos, Aplicacion sin Infraestructura ni Api, y la Api solo toca Infraestructura para registrar sus servicios.

## 2.3 Módulos y carpetas

Dominio, Aplicacion e Infraestructura tienen una carpeta por módulo: `Cotizaciones`, `Tarifas`, `Margen`, `Precio`, `Pdf`, `Correo`, `Auditoria`, `Identidad`, `Agente`, `Clientes`, `Administracion`, `Metricas`. Los catálogos del formulario que vienen de Nexo (`Municipios`, `CatalogosFlete`, `Cubicacion`) no tienen reglas de dominio: solo existen en Aplicacion (puerto y DTOs), Infraestructura (adaptadores) y Api (controller); no están en el diagrama porque no dependen de otros módulos ([2.14](#214-integración-con-nexo)). `Cubicacion` también trae el cubicador de HU-021.5 (`IClsCubicador`, `clsCubicadorHttp`, `GET /web/tipos-mercancia`), que usa el planificador de camiones de `Costeo` ([ADR-019](../specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md)). `Costeo`, `Licitaciones` y `Redaccion` tampoco tienen carpeta en Dominio: existen en Aplicacion e Infraestructura (y `Costeo` y `Licitaciones` en Api); las reglas del costeo y del agregado de la solicitud viven en `Dominio/Cotizaciones` ([2.5](#25-modelo-de-dominio-y-persistencia)). Api solo tiene carpetas de los módulos con endpoints en `specs/openapi.yaml`, más `Sistema/` (health). Además: `Comun/` para lo transversal, `Sesiones/` solo en Dominio y `Configuracion/` en Infraestructura. Mientras un módulo no tiene código, su carpeta solo contiene un `LEEME.md` con su propósito y subcarpetas (en inglés en todas las capas):

- Dominio: clases en la carpeta; si crece, `Entities/`, `ValueObjects/`, `Rules/`.
- Aplicacion: `DTOs/`, `Validators/`, `Services/`, `Repositories/` (puertos), `Interfaces/`.
- Infraestructura: `Repositories/`, `Services/`; `Persistence/` para el `DbContext` (el de `dbo_cotifly` en `Comun/Persistence/`, el de solo lectura de `dbsolucionesti` en `Identidad/Persistence/`).
- Api: `Controllers/`, `DTOs/`.

```mermaid
flowchart LR
    subgraph negocio[Negocio]
        Cotizaciones
        Precio
        Margen
        Tarifas
        Clientes
        Costeo
    end
    subgraph documentos[Documentos y envío]
        Pdf
        Correo
    end
    subgraph conversacion[Conversación]
        Agente
    end
    subgraph soporte[Soporte]
        Identidad
        Auditoria
        Administracion
        Metricas
    end
    Agente --> Margen
    Agente --> Costeo
    Agente --> Cotizaciones
    Pdf --> Cotizaciones
    Pdf --> Margen
    Correo --> Pdf
    Correo --> Cotizaciones
    Margen --> Precio
    Administracion --> Margen
    Metricas --> Cotizaciones
    Pdf -.-> Auditoria
    Correo -.-> Auditoria
    Agente -.-> Auditoria
    Agente -.-> Identidad
```

Las flechas son dependencias entre casos de uso dentro de Aplicacion; las punteadas son transversales (auditoría y resolución del comercial). Todos los módulos comparten `Comun/` (resultado, errores, reloj, calendario de negocio, unidad de trabajo).

## 2.4 Convenciones de nombres

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

### 2.4.1 Comentarios en el código

Los comentarios describen el código tal como es hoy. La trazabilidad (qué historia o qué issue originó un cambio, quién lo decidió y cuándo) vive en git, en los PR y en las issues, y no se repite en el código. Todo cambio nuevo sigue estas reglas, y la revisión del PR las verifica:

1. **Qué y por qué.** Un comentario dice qué hace el código y por qué lo hace, en presente, breve y en español profesional. No cuenta la historia del cambio.
2. **Sin referencias de trabajo.** No se citan números de historia ni de issue (`HU-0xx`, `#nnn`), fechas ni nombres de personas o equipos («decisión de…»). La excepción son los ADR, que son documentación estable: `ADR-0xx` se puede citar cuando explica una regla de negocio.
3. **Sin notas de trabajo.** No se escriben frases como «antes…», «hoy…», «queda pendiente…», «ojo…» o «por ahora…». Lo que queda pendiente se registra en una issue, no en un comentario.
4. **XML doc breve.** Los comentarios `///` de las clases y los métodos públicos se mantienen cortos: una o dos frases y, cuando aporta, las reglas que aplica o los códigos de error que devuelve.
5. **Pruebas por comportamiento.** Las pruebas se nombran por el comportamiento que verifican, no por la historia que las originó. Un `/// <summary>` de prueba enuncia la regla que se prueba; si solo repetiría el nombre de una historia, se omite.

Para encontrar comentarios que no cumplen la regla 2:

```bash
git grep -nE '^\s*(///|//).*(HU-[0-9]{3}|#[0-9]{2,3}\b)' -- 'backend-dotnet/src/*.cs' 'backend-dotnet/tests/*.cs'
```

## 2.5 Modelo de dominio y persistencia

**Modelo de dominio y persistencia (#9).** Una entidad por tabla de `dbo_cotifly`, sin dependencias técnicas; setters privados, fábricas `Crear`/`Registrar`/`Emitir` que devuelven `clsResultado<T>` con las invariantes baratas (folio obligatorio ≤ 50, versión ≥ 1, TRM 0 en COP, márgenes ≥ 0 y < 1 solo con el método `margen` (`markup` admite ≥ 1, `clsReglaMargen`), correo con al menos un `para`, destinatario `para|cc`, hash SHA-256 de 64 hex...). Los enums se guardan con su código snake_case (`clsCodigoEnum`: `sin_enviar`, `politica_linea`, `gerente_comercial`), que es lo que exigen los CHECK del esquema.

| Tabla (`dbo_cotifly`) | Entidad (Dominio) | Puerto (Aplicacion) |
|---|---|---|
| `CotizacionesSolicitud` (ex `Cotizaciones`, script 0009) | `Cotizaciones/clsCotizacionSolicitud`, raíz del agregado (+ `EstadoEnvio`, `EstadoCosteo`, `clsDatosCotizacion`) | `IClsCotizacionRepository` |
| `CotizacionLineasSolicitud` (la ruta; scripts 0005, 0009 y 0010) | `Cotizaciones/clsLineaSolicitud` | vía la raíz |
| `CotizacionVehiculosLineaSolicitud` (el camión; script 0009) | `Cotizaciones/clsVehiculoLineaSolicitud` | vía la raíz |
| `CotizacionMercancias` (ex `CotizacionPosiciones`; script 0009) | `Cotizaciones/clsMercancia` | vía la raíz |
| `CotizacionServiciosConexos` (script 0009; `nombreServicioConexo` del 0010) | `Cotizaciones/clsServicioConexoRuta` | vía la raíz |
| `CotizacionArchivosLicitacion` (script 0005) | `Cotizaciones/clsArchivoLicitacion` | `IClsCotizacionRepository` (`AgregarArchivoLicitacion`) |
| `CotizacionPdfsHistorial` (script 0006) | `Cotizaciones/clsCotizacionPdfHistorial` (+ `MotivoInvalidacionPdf`) | `IClsCotizacionRepository` (`AgregarHistorialPdf`) |
| `MargenesPolitica` | `Margen/clsMargenPolitica` | `IClsMargenPoliticaRepository` |
| `SapMapeoTrqType` | `Margen/clsSapMapeoTrqType` | `IClsSapMapeoRepository` |
| `MargenAjustes` (+ `lineaSolicitudId`, la ruta ajustada, script 0009) | `Margen/clsMargenAjuste` (+ `OrigenMargen`) | `IClsMargenAjusteRepository` |
| `Correos`, `CorreoDestinatarios` | `Correo/clsCorreo`, `clsCorreoDestinatario` (+ `EstadoCorreo`, `TipoDestinatario`) | `IClsCorreoRepository` |
| `Sesiones`, `EventosConversacion` | `Sesiones/clsSesion`, `clsEventoConversacion` (+ `EstadoSesion`) | `IClsSesionRepository` (en `Agente`) |
| `EventosHerramienta` | `Auditoria/clsEventoHerramienta` | `IClsAuditoriaRepository` |
| `UsuariosWeb` (+ `idUsuarioExterno`, script 0002) | `Identidad/clsUsuarioWeb` | `IClsUsuarioWebRepository` |
| `Roles`, `UsuariosRoles` (script 0002, #12) | `Identidad/clsRol`, `clsUsuarioRol` (+ `RolInterno`) | `IClsRolRepository` |
| `TokensRefresco`, `TokensRevocados` (script 0002) | `Identidad/clsTokenRefresco`, `clsTokenRevocado` | `IClsTokenRefrescoRepository`, `IClsTokenRevocadoRepository` (#12) |
| `Sesiones.estadoAgente` (JSON, script 0002) | `Agente/clsEstadoConversacion` (desde HU-013.9 solo folio, id, versión y moneda; el JSON legado se lee ignorando los campos retirados) | vía `clsSesion` |
| `dbsolucionesti.tblSAPUsuariosApp`, `tblSAPUsuarioRoles`, `tblSAPRoles` (solo lectura) | sin entidad de Dominio: filas internas de Infraestructura (sin `CLAVE`), DTOs `clsUsuarioExterno` y `clsRolExterno` en Aplicacion | `IClsAlmacenIdentidadExterno` (#12) |

Los repositorios solo agregan o modifican en memoria; `IClsUnitOfWork.GuardarCambiosAsync` confirma todo en una transacción y traduce las violaciones de unicidad a `clsViolacionUnicidadException` con código de negocio (`uq_cotizaciones_folio` → `FOLIO_DUPLICADO`). `OrigenCliente` vive en `Clientes`.

**Agregado solicitud → ruta → camión (HU-021, [ADR-019](../specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md)).** Desde el script 0009 una cotización es una solicitud con N rutas, y cada ruta tiene N camiones. Nexo cotiza cada camión como una línea de su solicitud.

```mermaid
flowchart LR
    S["CotizacionesSolicitud<br/>clsCotizacionSolicitud (raíz)"] -- "1..N" --> R["CotizacionLineasSolicitud<br/>clsLineaSolicitud (ruta)"]
    R -- "1..N" --> C["CotizacionVehiculosLineaSolicitud<br/>clsVehiculoLineaSolicitud (camión)"]
    R -- "0..N" --> M["CotizacionMercancias<br/>clsMercancia"]
    R -- "0..N" --> X["CotizacionServiciosConexos<br/>clsServicioConexoRuta"]
    A["MargenAjustes"] -. "lineaSolicitudId" .-> R
```

- **Solo la raíz cambia el agregado.** Rutas, camiones, mercancías y conexos cambian a través de `clsCotizacionSolicitud`. El repositorio lee el grafo completo (`AsSplitQuery`) y lo guarda en una sola unidad de trabajo.
- **Cabecera:** cliente, folio, versión, moneda y `trmAplicada`, PDF y textos, resultado, envío, seguimiento del costeo (`solicitudNexoId`, del 0010, ex `costeoId`; `cotizacionNexoId`; `costeoSiguienteConsultaAt`) y `precioFinal`.
- **Ruta:** origen y destino (id del municipio y copia del nombre, 0010), condiciones del contenedor, horas, peso, valor de la mercancía y del levante, línea de negocio, margen (`margenPoliticaSugerido`, `margenSAPSugerido`, `margenAplicado`, `metodoCalculo`) y los totales guardados: `costoFletesTotal`, `costoRetornoContenedorTotal`, `costoLevanteContenedorTotal`, `costoServiciosConexos`, `costoFinal` y `precioFinalLinea`. `rutaOrigenIndice` es la posición en el formulario o la fila del Excel (ex `filaOrigen`).
- **Camión:** vehículo y carrocería (los define el cubicador o la Mesa de Costeo de Nexo, decisión 7), peso, `costoFlete`, `costoRetornoContenedor`, `costoLevanteContenedor` (copia del levante de la ruta, decisión 4), `lineaSolicitudNexoId`, `estadoCosteo` e `idRecomendacionVehiculo`.
- **Precio y estado.** `precioFinal` de la cabecera = Σ `precioFinalLinea` (NULL mientras alguna ruta no tenga precio). El estado se guarda en el camión y la ruta y la cabecera lo resumen (`clsResumenEstadoCosteo`, "el más atrasado").
- **Pendiente HU-023:** `db/scripts/02_alter_cubicador.sql` agregará las columnas del resultado de la cubicación y cambiará `idTipoMercancia` por `codigoMercancia`. Todavía no está en esta rama.

Base de datos, scripts de esquema y permisos: [03-infraestructura](03-infraestructura.md#33-base-de-datos) y [ADR-008](../specs/decisiones/ADR-008-persistencia-ef-core-database-first.md).

## 2.6 Precio, margen y tarifas

**Precio, Margen y Tarifas (#10, HU-003.4).** Port de `mcp_server/precio.py` y `mcp_server/margen/` con paridad verificada contra los tests del Python (tabla en [08-testing](08-testing.md)):

| Pieza | Capa | Qué hace |
|---|---|---|
| `Precio/clsCalculadoraPrecio` (+ `clsPrecioCalculado`) | Dominio | `margen`: costo / (1 − m), exige 0 ≤ m < 1; `markup`: costo × (1 + m), m ≥ 0. Precio a pesos enteros y utilidad a 2 decimales con `MidpointRounding.ToEven` sobre `decimal` (ADR-010 §6: paridad con Python salvo empates exactos de medio peso, donde .NET redondea el valor exacto y Python arrastra el error de `float`; diferencia máxima 1 peso, ~0,04 % de casos, ej. 55 con markup 0.1 → 60 y Python 61). Costo ≤ 0, m < 0, m ≥ 1 con `margen` o método desconocido → `PRECIO_INVALIDO` con los mensajes del Python |
| `Margen/clsReglasMargen` | Dominio | `Value` de SAP / 100 a 6 decimales, anomalía fuera de [0, 0.6], comparación de líneas sin tildes, mayúsculas ni espacios (`ALMACENAMIENTO 3PL` = `Almacenamiento3PL`) |
| `Margen/clsReglaMargen` | Dominio | Rango por método; ahora también lo usa `clsMargenAjuste` (con `markup` un ajuste ≥ 1 es válido), que además redondea ambos márgenes a 4 decimales (escala de la columna) antes de calcular la diferencia |
| `Comun/clsCodigosError` | Dominio | Códigos del esquema `ErrorNegocio` (verificados contra `openapi.yaml`) |
| `Precio/clsConversorMoneda` | Dominio | Monedas `COP` y `USD` (otra → `PRECIO_INVALIDO`), decimales por moneda (0 y 2) y `PesosADolares`: `round(precio_cop / trm, 2)` half-to-even sobre `decimal` (HU-012.2) |
| `IClsPrecioService` / `clsPrecioService` → `clsSalidaPrecio` | Aplicacion | Cálculo del precio (lo usan el costeo, el ajuste de margen y las propuestas del asistente; era `calcular_precio` del agente legado), salida `SalidaPrecio` `{precio, costo, margen, metodo, utilidad, moneda, trm_aplicada, precio_cop, trm_fuente}`. `CalcularAsync` calcula en pesos y, en USD, convierte con la TRM de `IClsProveedorTrm` para la fecha de `IClsCalendarioNegocio` (costo y utilidad siguen en COP; TRM caída → `TRM_NO_DISPONIBLE`, sin precio). `TrmDelDiaAsync` y `EnMoneda` los usaba `registrar_ajuste_margen` del agente legado (retirado en HU-013.9) |
| `IClsMargenResolverService` / `clsMargenResolverService` → `clsMargenResuelto` (`Salida`: `clsSalidaMargen`) | Aplicacion | Margen sugerido (lo usan el envío del costeo, las licitaciones y el PDF; era `obtener_margen` del agente legado): línea → política (por nombre, sin tildes) → TrqType por el **id** de la política (`IClsSapMapeoRepository`, HU-042.4; la organización específica gana al comodín; sin política no hay TrqType) → si hay **BP** y TrqType, `IClsFuenteMargen` por (salesOrg, TrqType, BP) → `sap_tm` con el detalle "SAP TM (tabla local, actualizado AAAA-MM-DD)" y, si la última sincronización supera `SapTm:AvisoSincronizacionHoras`, el aviso; sin BP no consulta SAP (ADR-004); sin BP, sin TrqType, sin registro (o fila inactiva) o con la copia nunca sincronizada → `politica_linea` con el motivo en `detalle`; sin política → `MARGEN_LINEA_DESCONOCIDA`; fuente caída → `politica_linea` con el motivo "SAP TM no disponible" y un warning (decisión de #13). Devuelve además `LineaNegocioId`, `MargenPolitica` y `MetodoCalculo` para el estado (ADR-004) |
| `IClsAjusteMargenService` / `clsAjusteMargenService` → `clsSalidaAjusteMargen` | Aplicacion | Margen negociado: redondea a 4 decimales y valida por método (`AJUSTE_MARGEN_INVALIDO`, código nuevo agregado a `ErrorNegocio`; `PRECIO_INVALIDO` queda solo para el cálculo de precio), registra `MargenAjustes` con el comercial (`usuarioId`) y la cotización (debe ser suya; si no, `AJUSTE_COTIZACION_INVALIDA`), devuelve origen `negociado` y, si viene costo, el precio con el margen manual (sin recalcular la política) |
| `IClsFuenteMargen` / `clsConsultaMargenSap` / `clsOpcionesMargen` (`SapTm:SalesOrgDefault`, por defecto `50000005`; `SapTm:AvisoSincronizacionHoras`, por defecto 48) | Aplicacion | Puerto de SAP TM (HU-042.2): `ConsultarAsync` responde `NoConfigurada`, `SinRegistro` o `ConValor` con el porcentaje crudo (13.5 = 13,5 %), la fecha de la fila y la de la última sincronización; el resolver divide por 100 una sola vez. Adaptador: la copia local (§2.7) |
| `Precio/Interfaces/IClsProveedorTrm` → `clsTrm` | Aplicacion | TRM vigente en una fecha de negocio (HU-012.1, ADR-014): `{Valor, VigenciaDesde, VigenciaHasta, Fuente}` (`dolarapi` o `superfinanciera`) o `TRM_NO_DISPONIBLE`; nunca lanza por la fuente |
| `Precio/Services/clsProveedorTrmDolarApi` | Infraestructura | Fuente principal (`Trm:UrlDolarApi`, por defecto `https://co.dolarapi.com/v1/trm`): valida `unidad = COP`, `nombre = TRM`, `valor` en [500, 20000] (4 decimales half-to-even) y a ±10 % de la última TRM conocida (`clsMemoriaTrm`), cuerpo ≤ 16 KB y `fechaActualizacion` del día pedido antes de las 12:00 en Bogotá (después puede ser la TRM de mañana); vigencia = el día pedido. Cliente con nombre `TrmDolarApi`, `clsResilienciaTrm` con `Trm:TiempoEsperaDolarApiSegundos`; fallo → `TRM_NO_DISPONIBLE` con un warning |
| `Precio/Services/clsProveedorTrmConRespaldo` | Infraestructura | Cadena `Trm:Proveedor = dolarapi_con_respaldo` (por defecto): DolarApi y, solo si falla, la Superintendencia (warning con la fuente usada); las dos caídas → `TRM_NO_DISPONIBLE` con los dos motivos. Con `dolarapi` o `superfinanciera` se registra solo esa fuente |
| `Precio/Services/clsProveedorTrmSuperfinanciera` | Infraestructura | Datos abiertos de la Superintendencia Financiera (`Trm:Url`, conjunto `32sa-8pi3`): SoQL `vigenciadesde <= F AND vigenciahasta >= F`, orden descendente, 1 fila, así los fines de semana y festivos reciben la TRM del rango que los cubre. Cliente con nombre `Trm` (`IHttpClientFactory`, se crea por consulta), resiliencia estándar con `Trm:TiempoEsperaSegundos` en total, un tercio por intento y 2 reintentos; fallo → `TRM_NO_DISPONIBLE` con un warning |
| `Precio/Services/clsProveedorTrmEnCache` | Infraestructura | Decorador singleton registrado como `IClsProveedorTrm`, encima de la cadena: caché por fecha de negocio (America/Bogota): la primera TRM válida del día queda fija hasta las 00:00 del día siguiente (una fecha pasada, 6 horas), registra cada TRM en `clsMemoriaTrm`, consulta compartida por fecha (los simultáneos reciben el mismo resultado, también un fallo) y caché negativa de 20 s |

Reglas de resolución: la línea se resuelve sin tildes ni espacios (la collation `CI_AS` distingue tildes); los textos de `detalle` son `(cliente nuevo)`, `(sin registro en SAP TM)`, el de SAP (`Margen propuesto SAP TM (tabla local, actualizado AAAA-MM-DD) para cliente X en OPTN: 13.5%`, con el valor sin ceros a la derecha), el del caso sin BP (`cliente nuevo: sin BP de SAP, no se consulta SAP TM`, ADR-004) y los de SAP no configurado o no disponible; los márgenes del ajuste y su diferencia se redondean a 4 decimales (escala de `DECIMAL(9,4)`); el margen negociado se valida por método (código `AJUSTE_MARGEN_INVALIDO`) y un motivo vacío se registra como `Ajuste manual del comercial`; empates exactos de medio peso (ver arriba).

**PDF y trazabilidad en USD (HU-012.2):** `clsGenerarPdfService` exige que el `PrecioFinal` de la trazabilidad sea el precio en pesos (costo y margen) convertido con su `TrmAplicada`, y compara la tarifa impresa con centavos. La cabecera (`CotizacionesSolicitud` desde el 0009) guarda `moneda`, `precioFinal` en esa moneda y `trmAplicada` (0 en COP, nunca nula; `DECIMAL(18,4)` del script 0001). Los costos de Nexo, en pesos, son de la ruta y del camión; cada precio se calcula en pesos y se convierte con la `trmAplicada` de la cabecera (ADR-019).

Las decisiones de negocio que siguen abiertas en este módulo están en [10-operacion](10-operacion.md#107-decisiones-pendientes). El orden de resolución del margen se muestra como diagrama en [04-flujo-del-sistema](04-flujo-del-sistema.md#45-resolución-del-margen).

## 2.7 Margen de SAP TM: copia local

**HU-042.2 (#243) y HU-042.4 (#247), [ADR-027](../specs/decisiones/ADR-027-margen-sap-tabla-local.md).** El margen recomendado de SAP TM
(`RentabClienteSet`) ya no se consulta en vivo al cotizar ni de un export montado: sale de `dbo_cotifly.SapMargenCliente`. Desde el
2026-11-03 esa copia la sincroniza **el propio backend** con un worker (el SQL Agent Job de Datos de HU-042.1, `db/jobs/`, no se
instala): un GET diario del set completo, el NIT por BP en el DW y el stored procedure `sp_sincronizar_margen_sap` del `17`. Los modos
`export_json`/`odata` de #13 (HU-003.7) se retiraron con sus adaptadores, su fábrica, sus opciones y sus pruebas.

| Pieza | Capa | Qué hace |
|---|---|---|
| `Margen/Services/clsFuenteMargenTablaLocal` | Infraestructura | Única `IClsFuenteMargen` (scoped, `TryAdd`). Sin ninguna corrida `ok` en `SapMargenSyncLog` → `NoConfigurada` (no lee la tabla). Si no, busca la fila `activo = 1` por organización y TrqType (recortados, mayúsculas) y BP **sin ceros a la izquierda en los dos lados**, con el índice único `(salesOrgId, trqType, bpSap)` y el final del BP; devuelve el porcentaje, `actualizadoAt` y la última sincronización. Error de base → `clsFuenteMargenNoDisponibleException`. `AsNoTracking`, sin caché |
| `Comun/Persistence/clsSapMargenClienteFila`, `clsSapMargenSyncLogFila` (+ `clsSapMargenConfiguracion`) | Infraestructura | Mapeo de solo lectura de las dos tablas del `db/scripts/17` (CHECK del estado e índices incluidos). Sus columnas de texto heredan la collation de la base (`clsCotiflyDbContext.TablasConCollationDeLaBase`) |
| `Margen/clsSapMapeoTrqType.LineaNegocioId` (+ `clsSapMapeoRepository`) | Dominio / Infraestructura | HU-042.4: FK a `MargenesPolitica(id)` (`db/scripts/18`); `TrqTypeDeAsync(lineaNegocioId, salesOrg)` y `LineaDeAsync` (nombre por el id) con el comodín `IN (@org, '') ORDER BY salesOrg DESC`. La columna `lineaNegocio` (texto) sigue mapeada mientras exista |
| `Margen/Services/clsAvisosArranqueSapTm` | Infraestructura | `IHostedService`: un warning "variable obsoleta, se ignora" por cada `SapTm__Modo` o `__ExportPath` que llegue con valor (nombre, nunca el valor). No impide arrancar |
| `IClsSincronizacionMargenSap` / `clsEstadoSincronizacionMargenSap`, `clsResultadoSincronizacionMargenSap`, `clsCorridaSincronizacionMargenSap` | Aplicacion | Puerto de la sincronización (2026-11-03): `Configurada`, `EstadoAsync` y `SincronizarAsync(forzada)`; nunca lanza por fallos de SAP, del DW o de la base (los deja como `error` en el log) |
| `Margen/Services/clsSincronizacionMargenSapSql` (+ `clsCoordinacionSincronizacionMargenSap`) | Infraestructura | Singleton, port del script de Datos: si no toca sale sin llamar a SAP; descarga, guarda de mínimo de filas, NIT, `#SapMargenStage` con `COLLATE DATABASE_DEFAULT` + `SqlBulkCopy` + `EXEC` del stored procedure en una misma conexión; registra el `error` de lo que falla antes del stored procedure. Conexiones ADO propias (no el `DbContext`). La coordinación es el candado por réplica (worker y corrida forzada) y el próximo chequeo |
| `Margen/Services/clsClienteRentabSap`, `clsLectorRentabClienteSap`, `clsGuardaMinimoFilasSap` | Infraestructura | GET a `{SapTm:Url}/RentabClienteSet?$format=json` sin `$filter` (cliente con nombre, Basic, gzip, sin cookies, `TimeoutSegundos`, `MaximoBytes`, sin reintentos); lectura todo o nada de `d.results`; `max(MinimoFilas, ceil(% × activas))` |
| `Margen/Services/clsDirectorioNitDwOperaciones` | Infraestructura | NIT por BP en `DWCubosOperacionesEBS.dbo.DimBPSolicitante` (`ConnectionStrings:DwOperaciones`, lotes de 500, `VARCHAR(20)`; varios NIT para un BP: el menor en orden ordinal de texto). Sin la cadena, o sin el BP en el DW, la fila conserva el NIT que ya tenía (o `NULL`) |
| `Margen/Services/clsWorkerSincronizacionMargenSap` | Infraestructura | `BackgroundService` (`PeriodicTimer` + `TimeProvider`, un ámbito por ronda): espera 30 s y cada `SapTm:ChequeoMinutos` sincroniza si la última corrida `ok` supera `SapTm:SincronizacionMinutos`. Sin `Url`/`User`/`Pass` no corre. Una ronda fallida queda en Error y no tumba el host |
| `Margen/Services/clsSapMargenAdminService` + `Administracion/Controllers/clsSapMargenController` | Aplicacion / Api | `GET /web/admin/sap-margen/estado` y `POST /web/admin/sap-margen/sincronizar` (gerencia; el POST espera la corrida y responde su resultado; auditado) |

Reglas que se conservan: sin BP no se busca (ADR-004); el anómalo fuera de [0, 60 %] se marca y el envío a Nexo aplica la política
(#139); una fuente caída cae a la política con "SAP TM no disponible" y un warning; `origen` sigue siendo `sap_tm`. Lo nuevo: cada
cliente recibe su propia fila (el modo `odata` tomaba `results[0]`), el detalle dice de dónde y de cuándo es el dato, y la copia no
caduca (si la última sincronización exitosa supera `SapTm:AvisoSincronizacionHoras`, el detalle lo avisa y el margen se usa igual).
`/health/ready` exige el `17` y el `18` ("falta 17", "falta 18"); no depende de SAP ni del DW (el worker no bloquea el arranque).

## 2.8 PDF, correo y auditoría

**PDF, Correo y Auditoría (#11, HU-003.5).** Port de `mcp_server/pdf/` (`armar_cotizacion.py`, `armar_multi.py`, `generador.py`), `shared/almacen_pdf.py`, `mcp_server/correo.py` y `mcp_server/auditoria.py`. Casos de uso listos para los endpoints de #12 (`enviar`, `correo/vista-previa`, `destinatarios-previos`, `usuario/telefono`, `seguimiento`) y para las herramientas del agente legado (`generar_pdf`, `enviar_correo`, retiradas en HU-013.9):

| Pieza | Capa | Qué hace |
|---|---|---|
| `Pdf/DTOs/clsCotizacionPdfPayload` (+ `clsTarifaPayload`, `clsFilaTarifaPayload`, `clsServicioConexoPayload`...) | Aplicacion | Payload del PDF con el esquema de `ejemplo-solicitud.json` (snake_case, `clsJsonAplicacion.SnakeCase`). `comercial.nombre/email/cel` se ignoran (la firma sale de `UsuariosWeb`); `extra_css`/`dibujo_html` no existen (sin HTML). Nuevo: `servicios_conexos` (itemizado de "incluye") |
| `Pdf/Validators/clsValidadorCotizacionPdfPayload` + `Pdf/Services/clsTextoSeguro` | Aplicacion | Exige `linea_negocio` y al menos una fila de tarifa; largos máximos por campo; quita caracteres de control y de formato invisibles (el texto se inserta como texto: un `<script>` se imprime literal) → `PDF_PAYLOAD_INVALIDO` |
| `Pdf/Services/clsConstructorDocumentoCotizacion` → `clsDocumentoCotizacion` | Aplicacion | Reglas de `construir_html` sin maquetar: variante (`secciones` → multi), textos del payload o estándar (`clsTextosEstandar`, ambos juegos del Python), tarifa por fila (`tarifa` o costo/margen con `clsCalculadoraPrecio`), bloques condicionales (aduana solo si la línea empieza por "internacional", extradimensionada, refrigerada), imagen y leyenda por línea, nota de decretos, nota de la cotización en dólares (`NotaMoneda`, HU-012.3: solo con moneda `USD`, en todas las variantes, también el pliego; texto de negocio del 2026-10-05 en `clsTextosEstandar.NotaTrmMinima`: "Esta cotización está bajo una TRM mínima de $3.273,49 y es de resaltar que..." con la `trmAplicada` de la cotización a 2 decimales, ADR-014 §4). La moneda y, en USD, la TRM son obligatorias: con TRM ≤ 0 el constructor responde `PDF_PAYLOAD_INVALIDO`. El PDF ya no imprime el valor en pesos (`PrecioCop`), frase final y firma |
| `Pdf/Services/clsFormatoMoneda`, `clsFormatoFecha` | Aplicacion | `$3.294.798` / `US$996,35` (USD con 2 decimales, HU-012.3) / TRM `$3.306,86` (`FormatearTrm`, escala real) y `$3.273,49` (`FormatearTrmNota`, siempre 2 decimales, nota del PDF en USD) / `—` (half-to-even, sin ICU ni cultura del proceso) y `Yumbo, 24 de septiembre de 2026` (meses propios; sin fecha, la de hoy en `Zona:ZonaHoraria` vía `IClsCalendarioNegocio`) |
| `IClsGenerarPdfService` / `clsGenerarPdfService` (+ decorador `clsGenerarPdfServiceAuditado`) → `clsSalidaPdf` | Aplicacion | `generar_pdf`: folio obligatorio de la entrada (lo genera el servidor, ADR-011; `^[A-Za-z0-9_-]+$`, ≤ 50) → payload → comercial en `UsuariosWeb` → línea con `IClsMargenResolverService.ResolverLineaAsync` (sin tildes, `MARGEN_LINEA_DESCONOCIDA` antes de renderizar, issue #44) → folio libre (`FOLIO_DUPLICADO`) → cotización válida en el dominio → coherencia con la trazabilidad (precio y moneda, ver abajo) → render (`PDF_RENDER_NO_DISPONIBLE`) → almacén sin reemplazar → cotización "del chat" con una ruta sin municipios (`RegistrarCotizacionChat`: línea, costo, márgenes, precio, `origenMargen` incl. `negociado`, moneda/TRM, `usuarioId`, `spaceId`/`threadId`, `pdfPath`); si la base la rechaza se borra el PDF. Salida `SalidaPdf` `{cotizacion_id, folio, pdf_url, registrada_en_bd}`. **Apagado por defecto desde HU-021.6** (`Pdf:PermitirCotizacionChat = false`: responde `COTIZACION_CHAT_RETIRADA`); solo lo encienden las pruebas del PDF legado (ADR-019) |
| `IClsGeneradorPdf` / `Pdf/Services/clsGeneradorPdfMigraDoc` (+ `clsBloquePortada`, `clsBloqueTarifa`, `clsBloqueCondiciones`, `clsBloqueFirma`, `clsEncabezadoPiePdf`, `clsEstilosPdf`/`clsDisenoPdf`) | Infraestructura | Maquetación con PDFsharp-MigraDoc 6.2.4 (ADR-006): A4, márgenes 34/16/30/16 mm (multi 34/14/28/14), logo, pie con `FOOTER_DIRS` y franja web, paleta `C`, 3 páginas en tarifa única y 2 en multi. La nota de TRM mínima en USD va bajo la tabla en el recuadro de la nota de decretos, sin etiqueta (solo el texto exacto, ADR-014 §4). Checklist de cobertura en ADR-006 |
| `Pdf/Services/clsResolvedorFuentes` (`IFontResolver`) + `clsRecursosPdf` | Infraestructura | Liberation Sans (normal, negrita, cursiva, negrita cursiva) embebida como recurso; cualquier familia pedida se resuelve a ella; viñetas con `•` propio. Logo y 7 imágenes por línea embebidos (ver [03-infraestructura](03-infraestructura.md)) |
| `IClsAlmacenPdf` / `Pdf/Services/clsAlmacenPdfVolumen` | Infraestructura | `Pdf:StorageDir` (por defecto `/data/pdfs`), nombres `^[A-Za-z0-9_-]+\.pdf$`, ruta siempre dentro de la carpeta, `GuardarNuevoAsync` nunca reemplaza (`FileMode.CreateNew`), lectura por referencia solo con el nombre del archivo. S3/MinIO no se porta (ADR-012) |
| `IClsCorreoService` / `clsCorreoService` (+ `clsCorreoServiceAuditado`, `clsCandadoEnvios`) | Aplicacion | `enviar_correo`: comercial autenticado (`CONFIRMACION_REQUERIDA`), `Correo:Habilitado` (`CORREO_DESHABILITADO`), destinatarios válidos (`CORREO_INVALIDO`), dueño (`COTIZACION_NO_ENCONTRADA`), deduplicación de 5 min contra envíos `enviado` al mismo conjunto de `para` sin distinguir mayúsculas (`CORREO_DUPLICADO`, regla movida del repositorio, ADR-008), PDF del almacén (`CORREO_PDF_NO_ENCONTRADO`), transporte (`CORREO_ENVIO_FALLIDO` + fila `fallido` con el error). Éxito: fila `enviado` con destinatarios y `estadoEnvio` `sin_enviar`→`enviada`, `enviada`/`reenviada`→`reenviada` (fix del bug del Python). Asunto por defecto `Cotización {folio} — Botero Soto`, cuerpo `_PLANTILLA` + HTML corporativo (`clsPlantillaCorreo`) |
| `IClsVistaPreviaCorreoService`, `IClsDestinatariosPreviosService` | Aplicacion | `VistaPreviaCorreo` (asunto, cuerpo, para/cc prediligenciados, adjunto) sin enviar nada; `Destinatarios` del último envío exitoso del comercial al NIT (`IClsCorreoRepository.ObtenerUltimoEnviadoAClienteAsync`) |
| `IClsTransporteCorreo` + `IClsSeguimientoCorreoExterno` / `Correo/Services/clsTransporteMailSender` (HttpClient tipado) | Aplicacion / Infraestructura | HU-033 (#199, ADR-023): el microservicio interno MailSender. `POST /api/v1/emails` multipart (From = comercial o `Remitente`, To/Cc/Bcc, Subject, Body HTML, Priority, Metadata, el PDF) → 202 con su id (`encolado`); `GET /emails/{id}` y `/retry` para el worker `clsWorkerEstadoCorreos` y el reintento. `X-Correlation-ID` = `{folio}:{id del envío}`. Sin reintentar el POST. La protección "redirigir" (`clsProteccionCorreo`) y la copia oculta al comercial las aplica `clsCorreoService` antes del transporte. `smtp_relay` (MailKit), `smtp` y `wscorreos` se retiraron |
| `IClsAuditoriaService` / `clsAuditoriaService` | Aplicacion | `EventosHerramienta` en su propio ámbito de DI: herramienta, entrada y resumen JSON snake_case (máx. 4000 caracteres, `{"truncado":true,"preview":...}` si se pasa, siempre JSON válido para el CHECK `ISJSON`), éxito, duración, hilo. Nunca lanza. Los decoradores auditan `generar_pdf`, `enviar_correo` y `registrar_ajuste_margen` (también desde la web); el filtro del agente de #12 debe usar `RegistrarAsync` para el resto y omitir esas tres (`HerramientasAuditadasPorCasoDeUso`) |
| `IClsTelefonoComercialService` | Aplicacion (Identidad) | `GET/PUT /web/usuario/telefono`: patrón `^\+?[0-9 ]{7,20}$` (`TELEFONO_INVALIDO`), persiste en `UsuariosWeb.telefono`; vacío lo borra. Es el único origen del "Cel" del PDF |
| `IClsSeguimientoService` | Aplicacion (Cotizaciones) | Ganada/perdida con motivo (≤ 2000) y `marcadoAt`, solo del dueño. Sale de `IClsCotizacionRepository.MarcarSeguimientoAsync` (deuda de ADR-008) |
| `IClsCalendarioNegocio` / `Comun/clsCalendarioNegocio` | Aplicacion / Infraestructura | Fecha de hoy en `Zona:ZonaHoraria` (el contenedor corre en UTC); la usa también el folio de #12 |

Códigos nuevos en `ErrorNegocio` (`specs/openapi.yaml`): `FOLIO_DUPLICADO` (el Python versionaba el folio en silencio con `-v2`; en .NET el folio lo genera #12, ADR-011, y una colisión se informa), `CORREO_DESHABILITADO` y `CORREO_INVALIDO`.

**Coherencia de lo impreso (#11, ADR-003/ADR-004):** el folio es obligatorio en la entrada (lo genera el servidor; la `referencia` del payload se ignora). Con `PrecioFinal` en la trazabilidad, la tarifa impresa de cada fila y el precio calculado con costo y margen aplicado deben ser ese precio; filas y servicios conexos deben estar en la moneda de la cotización (HU-012.3). Si no, `PDF_PAYLOAD_INVALIDO`. Hasta HU-013.9 el filtro de inyección del agente legado sobrescribía los números del payload desde el estado (ADR-004). También se rechazan tarifas, costos y márgenes negativos, y `clsCotizacionPdfPayload.DesdeJson` devuelve `PDF_PAYLOAD_INVALIDO` (no lanza) ante JSON mal formado, tipos erróneos o sustitutos UTF-16 sueltos.


La traducción de estos códigos a HTTP en `/web/*` está en [07-api](07-api.md#74-errores).

El filtro de auditoría del agente no vuelve a auditar `generar_pdf`, `enviar_correo` ni `registrar_ajuste_margen` (ya los auditan sus decoradores; `clsAuditoriaService.HerramientasAuditadasPorCasoDeUso`). Desde HU-013.9 ninguna herramienta del asistente llega a esos casos de uso.

**Destinatarios previos:** hoy devuelve solo los del último envío exitoso del comercial al NIT (es lo que dice `openapi.yaml`); el backlog habla de "los correos ya usados", así que queda por confirmar si se quiere la unión de todos los envíos. Las cotizaciones que creó el chat legado sin cliente confirmado quedaron con `nitCliente` nulo y no tienen precarga.

Reglas del PDF y del correo:
- **Firma con datos reales:** nombre y correo de `UsuariosWeb` y "Cel" solo si `UsuariosWeb.telefono` tiene valor.
- **Sin HTML:** el PDF se compone sin HTML, así que no hay inyección posible. Los saltos de línea del texto se respetan.
- **Folio:** no se versiona aquí (`FOLIO_DUPLICADO`); un PDF existente nunca se sobrescribe.
- **Línea de negocio:** se resuelve antes de renderizar y sin tildes.
- **Trazabilidad:** costo, márgenes, precio y origen llegan en `clsTrazabilidadCotizacion` calculados por el servidor, nunca desde un texto del modelo de IA.
- **Variante multi:** usa el prefijo `US$` de la moneda de cada fila.
- **Almacén:** la lectura por referencia solo toma el nombre del archivo.
- **Correo:** `estadoEnvio` pasa a `reenviada`; la deduplicación se serializa por cotización dentro del proceso; el transporte es el microservicio MailSender (ADR-023); `Correo:Habilitado=false` por defecto responde `CORREO_DESHABILITADO`.
- **Incluye itemizado:** con `servicios_conexos` se lista el flete (origen y destinos) y cada servicio con su precio. Nexo (HU-005) todavía no entrega el costeo desglosado por servicio: mientras tanto los envía el formulario, y sin ellos queda el texto estándar.

## 2.9 Identidad: login SSO con verificación doble

**Identidad: login SSO ClientePro con verificación doble (#12, parte A, ADR-009).** `POST /api/auth/login-clientepro`, `/refresh` y `/logout` según `specs/openapi.yaml`:

| Pieza | Capa | Qué hace |
|---|---|---|
| `IClsAlmacenIdentidadExterno` / `Identidad/Services/clsAlmacenIdentidadSolucionesTi` (+ `clsIdentidadDbContext`) | Aplicacion / Infraestructura | Almacén corporativo de solo lectura: `BuscarUsuarioAsync` (idUsuario como `NOMBREUSUARIO` y, si no, como `ID` numérico) y `ObtenerRolesAsync` (`IDROLES` + `DESCRIPCION`). `ID` NUMERIC → `long` (y → `int` para `IDUSUARIO`) solo si cabe; nombre repetido, ID no entero o fila sin nombre = no encontrado; fallos de la base se propagan (fail-closed) |
| `clsVerificadorIdentidad` | Aplicacion | Verificación doble común al login y al refresh: persona en el almacén → roles externos traducidos con `Roles.idRolExterno` (rol único: jerarquía `director` > `gerente_comercial` > `comercial`, regla auditada; con varias asignaciones activas, la persona elige el rol, HU-040, ADR-025) → usuario por el `NOMBREUSUARIO` del almacén: primer ingreso crea `UsuariosWeb` + `UsuariosRoles` (JIT); después la asignación activa y el vínculo `idUsuarioExterno` deben coincidir, si no 401 con motivo (`rol_externo_cambio`, `nombre_usuario_cambio`, `sin_asignacion_activa`, `identidad_inconsistente`...) |
| `IClsLoginClienteProService` / `clsLoginClienteProService` | Aplicacion | Valida el cuerpo (`SOLICITUD_INVALIDA` → 400), verifica, actualiza `ultimaSesion`, emite access y refresh (familia nueva, 30 días) y audita (`login_clientepro`, hilo `auth/{idUsuario}`). Todo rechazo o excepción: `CREDENCIALES_INVALIDAS` → 401 genérico |
| `IClsRefreshTokenService` / `clsRefreshTokenService` | Aplicacion | Busca por SHA-256; reuso de un token rotado o revocado → revoca todos los refresh vigentes del usuario (salvo rotación de hace menos de 10 s: renovación concurrente); vuelve a verificar; rota de forma atómica heredando familia y vencimiento absoluto. Todo rechazo: `SESION_EXPIRADA` → 401 |
| `IClsLogoutService` / `clsLogoutService` | Aplicacion | Revoca la familia del refresh y el `jti` del access (en memoria al instante y en `TokensRevocados`); nunca falla hacia afuera (204) |
| `IClsRevocacionAccesos` / `clsCacheTokensRevocados` | Aplicacion | `jti` revocados en memoria + foto de `TokensRevocados` cada 60 s; sin foto de menos de 5 min, fail-closed |
| `IClsEmisorTokens`, `IClsConfiguracionJwt` / `clsEmisorTokensJwt`, `clsConfiguracionJwt` (+ `clsOpcionesJwt`) | Aplicacion / Infraestructura | JWT HS256 (`sub`, `jti`, `username`, `rol`, `iss`, `aud`, `iat`, `exp`; sin `nbf` desde la auditoría de #12: un reloj que salta entre instancias daba 401 intermitentes con tokens recién emitidos) con `JsonWebTokenHandler`; clave ≥ 32 bytes; sin clave fuera de Production, clave efímera con aviso |
| `clsTokenOpaco` | Aplicacion | Refresh de 32 bytes aleatorios en base64url (43 caracteres) y su SHA-256 hexadecimal |
| `IClsRolRepository`, `IClsTokenRefrescoRepository`, `IClsTokenRevocadoRepository` | Aplicacion / Infraestructura | Catálogo y asignaciones; rotación con `UPDATE` condicional en transacción (de dos simultáneas gana una), revocaciones por familia o usuario y purga con `ExecuteUpdate`/`ExecuteDelete` |
| `Identidad/Services/clsLimpiezaTokensHostedService` | Infraestructura | Purga horaria de `jti` vencidos y refresh vencidos hace más de un día; un fallo es un warning |
| `Identidad/Controllers/clsAuthController` | Api | `/api/auth/*` exactos al contrato: 200 `LoginResponse`, 400/401/429 con `ErrorWeb`; refresh con cuerpo inválido → 401 y logout siempre 204 (el cuerpo se lee sin la validación automática) |
| `Identidad/clsConfiguracionAutenticacion` (+ `clsPoliticas`, `clsOpcionesLimiteAuth`, `clsContextoUsuarioHttp`) | Api | `JwtBearer` (`MapInboundClaims=false`, `NameClaimType="username"`, `RoleClaimType="rol"`, emisor, audiencia, vigencia con 30 s de tolerancia, firma HS256), revocación en `OnTokenValidated`, 401/403 con `ErrorWeb`, políticas `Comercial` (comercial o gerente) y `GerenteComercial` para la parte B, `IClsContextoUsuario` desde los claims y límite de `/api/auth/*` por IP |
| `Comun/clsErrorWeb` | Api | `{"error": "..."}`; también la respuesta 400 de la validación automática de `[ApiController]` (en lugar de `ProblemDetails`) |

`/api/auth/*` serializa en camelCase salvo `user.nombre_completo` (así lo define el contrato), con nombres JSON explícitos en los DTOs de la Api. `ubicacion` es `UsuariosWeb.regional`, que el login llena y refresca desde `DimEjecutivo` sin bloquearse (#81, ADR-016 §6); `null` si el usuario no está en esa tabla y nadie la llenó.

Secuencia del login ([ADR-009](../specs/decisiones/ADR-009-autenticacion-sso-clientepro.md)):

```mermaid
sequenceDiagram
    autonumber
    participant F as Frontend ClientePro
    participant C as clsAuthController
    participant L as clsLoginClienteProService
    participant V as clsVerificadorIdentidad
    participant E as dbsolucionesti (solo lectura)
    participant D as dbo_cotifly
    F->>C: POST /api/auth/login-clientepro {idUsuario, nombreUsuario?, roles?}
    C->>L: validar cuerpo (400 si es inválido)
    L->>V: verificar (permitirAlta = true)
    V->>E: tblSAPUsuariosApp por NOMBREUSUARIO y, si no, por ID (nunca CLAVE)
    E-->>V: persona
    V->>E: tblSAPUsuarioRoles + tblSAPRoles
    E-->>V: IDROLES externos
    V->>D: Roles.idRolExterno traduce a rol interno (gerente_comercial gana)
    alt Primer ingreso
        V->>D: crea UsuariosWeb y UsuariosRoles (JIT)
    else Ingresos siguientes
        V->>D: rol asignado activo, NOMBREUSUARIO e idUsuarioExterno deben coincidir
        Note over V,D: Si no coinciden: rechazo con motivo auditado (rol_externo_cambio, nombre_usuario_cambio...)
    end
    V-->>L: comercial verificado o rechazo
    alt Verificado
        L->>D: ultimaSesion, TokensRefresco (SHA-256 del refresh), EventosHerramienta
        L-->>C: access JWT HS256 (60 min) y refresh opaco (30 días)
        C-->>F: 200 LoginResponse
    else Rechazo o excepción (fail-closed)
        L->>D: EventosHerramienta con el motivo
        C-->>F: 401 Credenciales inválidas (siempre el mismo)
    end
```

El refresh (`POST /api/auth/refresh`) repite la verificación doble sin alta antes de rotar el token (Google Chat la repetía en cada turno hasta su retiro en HU-013.9). Cada petición protegida pasa por `JwtBearer` y por la revisión del `jti` en `clsCacheTokensRevocados` (`OnTokenValidated`).

## 2.10 API web

**API web `/web/*` (#12, parte B).** Todos los endpoints de `specs/openapi.yaml` salvo `/web/chat` (parte C; `/chat/events` también era de la parte C y se retiró en HU-013.9). Controllers delgados: toman el usuario del token (`IClsContextoUsuario`) y delegan en los casos de uso; los errores pasan por una sola tabla (`clsRespuestasWeb`, abajo).

| Pieza | Capa | Qué hace |
|---|---|---|
| `Cotizaciones/clsFolio` + `Cotizaciones/clsInicialesComercial` | Dominio | Folio del servidor ([ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md), aceptada): `PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]` (p. ej. `PCN-DG-25092026-001`, `PCN-9000472281-DG-25092026-001-v2`). `Crear(fecha, iniciales, consecutivo, nit)` (iniciales de 2 letras A–Z, si no `FOLIO_INVALIDO`; NNN 001–999, si no `FOLIO_CONSECUTIVO_AGOTADO`; el NIT se guarda solo con sus dígitos, máximo 26 para que quepa `-v99` en 50 caracteres), `Desde(folio)` (interpreta el formato; un folio heredado como `PCN-DDMMAAAA` o sin iniciales conserva su base), `ConVersion(n)` (≥ 2, ≤ 50 caracteres). `clsInicialesComercial.Calcular(nombreCompleto, nombreUsuario)`: primera letra del primer nombre y del primer apellido (penúltima palabra con 3 o más), sin tildes, partículas (`de`, `del`, `la`, `las`, `los`, `y`), texto entre paréntesis o tras ` - ` ni iniciales abreviadas (`M.`); respaldo `nombreUsuario` sin dominio de correo; una palabra → sus 2 primeras letras; sin letras → `XX` |
| `IClsGeneradorFolioService` / `clsGeneradorFolioService` | Aplicacion | `CrearConFolioNuevoAsync(usuario, nit, crear)` y `CrearNuevaVersionAsync(usuario, cotizacionId, crear)`: calcula el folio y ejecuta `crear` (agrega la cotización con `version = folio.Version` y guarda) dentro de `IClsCandadoTransaccional`. Iniciales del comercial desde `UsuariosWeb` (inexistente → `ERROR_INTERNO`). Fecha de `IClsCalendarioNegocio` (America/Bogota). Consecutivo = mayor `NNN` del día entre todos los folios base con esas iniciales (con y sin NIT, de cualquier comercial) + 1: nunca está tomado, no hay bucle de huecos. Versión = máxima de la familia (`{base}` y `{base}-vN`) + 1. Lo usan el envío y la re-solicitud del costeo, la nueva versión y las licitaciones (hasta HU-013.9 también `generar_pdf` del agente legado) |
| `IClsCandadoTransaccional` / `Comun/Persistence/clsCandadoTransaccionalSql` | Aplicacion / Infraestructura | Transacción `ReadCommitted` en el `DbContext` del ámbito + `sp_getapplock` (`Exclusive`, dueño `Transaction`, espera máxima 15 s → `TimeoutException`). Confirma si el resultado es exitoso; deshace (y limpia el `ChangeTracker`) si es un fallo o si lanza. No se anida. Recursos: `cotifly:folio:DDMMAAAA:INICIALES` (uno por iniciales y día) y `cotifly:folio-version:{base}` |
| `IClsMisCotizacionesService`, `IClsNuevaVersionService` (+ `clsCotizacionResumen`, `clsDatosNuevaVersion`) | Aplicacion | Listado del comercial (máx. 100; `linea_negocio` de la ruta 1, o `Varias` si las rutas tienen líneas distintas, HU-021.9; `pdf_url` con `clsUrlPdf`) y datos para prellenar una versión nueva (solo lectura) |
| `IClsDescargaPdfService` | Aplicacion | `GET /pdfs/{archivo}`: nombre `^[A-Za-z0-9_-]+\.pdf$` que debe ser el PDF guardado de la cotización con ese folio; dueño, o `gerente_comercial` si el dueño es miembro de su regional efectiva (`IClsRegionalUsuario.EsMiembroDeMiRegionalAsync`, la misma regla que el listado regional de HU-016.1; sin regional o con la fuente caída se niega: fail-closed, solo el dueño; auditoría de #12, F1). Todo acceso de alguien que no es el dueño, permitido o no, se audita (`descargar_pdf`) |
| `IClsLineasNegocioAdminService` | Aplicacion | HU-004.9 (#41): lista y edita margen (0–0,99), método (`margen` o `markup`) y estado; revalida el rol `gerente_comercial` en el caso de uso; `clsMargenPolitica.Actualizar` aplica `clsReglaMargen`; audita cada intento (`admin_actualizar_linea_negocio`, con quién, lo pedido y `antes`/`despues`) |
| `Metricas/clsCalculadoraMetricas` (+ `clsCotizacionMetrica`, `clsIndicadoresMetricas`, `clsPuntoSerieMensual`, `clsMetricaCiudad`, `clsConcentracionCiudades`) | Dominio | HU-010.1 ([ADR-016](../specs/decisiones/ADR-016-metricas-agregadas-y-regional.md)): reglas puras sobre una fila por cadena de versiones. `valor_cop = round(precioFinal × trmAplicada, 2)` en USD (no es el `precio_cop` de ADR-014) y el costo nunca se convierte. Partición `cantidad = ganadas + perdidas + sin_resultado + pendientes_por_cotizar`; conversión sobre el total y sobre las cerradas; `costeo_ganadas`; margen, tiempo (sin filas incoherentes) y desviación promedio (con signo y absoluta); `deadline_vencidas` si existe la columna; serie mensual con todos los meses del periodo; concentración por municipio de destino y de origen. Redondeo half-to-even |
| `IClsTableroMetricasService` / `clsTableroMetricasService` | Aplicacion | `GET /web/metricas/comercial` (propias) y `/regional` (solo `gerente_comercial`, revalidado): periodo `desde`/`hasta` en fechas de Bogotá (por defecto el año en curso, máximo 3 años, `IClsCalendarioNegocio.InicioDelDia`/`FechaDe`), regional del gerente con `IClsRegionalUsuario`, filtro `usuario_id` solo dentro de la regional (404 si no), lista de comerciales con sus indicadores |
| `IClsRegionalUsuario` / `clsRegionalUsuarioService` + `IClsDirectorioEjecutivos` (`clsDirectorioEjecutivosDimEjecutivo`, `clsDirectorioEjecutivosNoDisponible`) | Aplicacion / Infraestructura | Regional efectiva = `DimEjecutivo` (si `ConnectionStrings:Regional` tiene valor y el usuario aparece) o `UsuariosWeb.regional`. Sin regional: `REGIONAL_NO_ASIGNADA` (409); fuente viva caída: `REGIONAL_NO_DISPONIBLE` (503), sin mezclar fuentes. El adaptador hace `SELECT` parametrizado con tabla y columnas configurables (`Regional:*`) y quita el dominio de `USUARIO` (`boterosoto\usuario`). El login (`clsVerificadorIdentidad`) usa el mismo puerto para llenar y refrescar `UsuariosWeb.regional` con límite corto y sin bloquearse (#81) |
| `IClsMetricasRepository` (`clsMetricasRepository`) | Aplicacion / Infraestructura | Tablero (HU-010.1): `ListarCotizacionesAsync` trae una fila por cadena `(usuarioId, folio base)` (folio base = folio sin `-v{version}`): valores de la vigente (`ROW_NUMBER`), periodo por `MIN(createdAt) OVER`, resultado más reciente de la cadena (`OUTER APPLY TOP 1` por `marcadoAt` y `version`), ajustes de la vigente (`OUTER APPLY` por `cotizacionId`) y `origenNombre`/`destinoNombre` de la ruta 1 (0010) y `deadline` solo si existen (`COL_LENGTH`). Desde HU-021 (#137, decisión 8) lee `CotizacionesSolicitud` y sus rutas: los conteos son por cotización; el costo es Σ `costoFinal` de las rutas, el margen el promedio de las rutas ponderado por costo, y una segunda consulta trae las rutas vigentes con su línea de negocio y sus ajustes. `IClsRolRepository.ListarRolesDeUsuariosAsync` da el `rol` de `comerciales[]` desde `UsuariosRoles`/`Roles`; `clsCacheDirectorioEjecutivos` guarda 60 s las respuestas exitosas de `DimEjecutivo`. El borrador (`IClsMetricasService`, `ListarActividadAsync`/`ListarAjustesMargenAsync` sobre `vActividadPorComercial` y `vAjustesMargenPorComercial`) se retiró en HU-014.3 (#152) |
| `Comun/clsJsonWeb` (`clsJsonSnakeCaseAttribute`, `clsFormateadorEntradaWeb`, `clsFormateadorSalidaWeb`) | Api | Serialización por grupo (ADR-002): un par de formateadores System.Text.Json snake_case al inicio de la lista de MVC que solo aceptan endpoints con `[clsJsonSnakeCase]`; lo lleva `clsControladorWebBase`, así todo `/web/*` lee y escribe snake_case (nulos siempre escritos, números estrictos: `"0.2"` es un cuerpo inválido) y `/api/auth/*`, `/health` y `/pdfs` no cambian |
| `Comun/clsRespuestasWeb` (+ `clsErrorNegocioWeb`, `clsControladorWebBase`) | Api | Tabla única de errores (abajo); base de los controllers de `/web/*` con `[ApiController]`, `[clsJsonSnakeCase]` y la política `Comercial` |
| `Comun/clsSeguridadHttp` | Api | CORS (`Cors:OrigenesPermitidos`), `UseForwardedHeaders` configurable para Traefik, encabezados de seguridad, `Cache-Control: no-store` en `/web/*` y `/pdfs/*` y límite del cuerpo de `/web/*` (64 KB, 413). Opciones en [03-infraestructura](03-infraestructura.md) |
| Controllers `Cotizaciones/`, `Correo/`, `Clientes/`, `Municipios/`, `CatalogosFlete/`, `Cubicacion/`, `Identidad/clsUsuarioController`, `Administracion/`, `Metricas/`, `Pdf/` | Api | Rutas exactas del contrato ([07-api](07-api.md)). Administración con `[Authorize(Policy = GerenteComercial)]` además de la base |

La tabla de errores de `/web/*` (`clsRespuestasWeb`) está en [07-api](07-api.md#74-errores).

Decisiones de la parte B:
- **Candado del folio por iniciales y día** (`cotifly:folio:DDMMAAAA:INICIALES`): el folio lleva las iniciales del comercial (decisión del 2026-09-25), así que solo compiten quienes comparten iniciales. Dos comerciales con las mismas iniciales comparten el consecutivo del día (`001` y `002`) y nunca chocan con `uq_cotizaciones_folio` (documentado en ADR-011). La regla de iniciales parte de `DESCRIPCIONUSUARIO` (vía `UsuariosWeb.nombreCompleto`) y tiene limitaciones conocidas (orden nombre y apellido, texto agregado, abreviaturas); **pendiente** revisar una muestra real de `tblSAPUsuariosApp.DESCRIPCIONUSUARIO` antes de producción (Duban con el DBA). El folio sin iniciales de la primera implementación (`PCN-[nit-]DDMMAAAA-NNN`) queda como **heredado**: se muestra y se puede versionar, pero no cuenta para el consecutivo.
- **Rol `gerente_comercial` en la consola:** la política `Comercial` admite ambos roles (decisión de la parte A: el gerente también cotiza). `docs/docs_front/CONTRATO_API_WEB.md` decía que un gerente recibía 403 en los endpoints de comercial; se corrigió.
- **Tablero de métricas (HU-010.1, ADR-016):** los KPI se calculan en memoria en el dominio sobre las filas vigentes (el volumen de una regional cabe de sobra) en lugar de vistas nuevas: no hace falta script de esquema y cada definición tiene su prueba unitaria. El rol sale del claim `rol` del token (política `GerenteComercial` en `/regional` y revalidación en el caso de uso). El borrador (`/actividad`, `/ajustes-margen`) y sus vistas se retiraron en HU-014.3 (#152, `db/scripts/08`). **Convergencia (#81):** la descarga de PDF ajenos, `LoginResponse.user.ubicacion` y el futuro "Mis cotizaciones" del gerente (HU-004.5) leen `UsuariosWeb.regional`, que el login refresca desde `DimEjecutivo`; el tablero la consulta en vivo. Todo converge en `DimEjecutivo` (ADR-016 §6).
- **Vista de actividad:** `num_cotizaciones_enviadas` debía contar `enviada` y `reenviada` (contrato) y la vista del 0001 solo contaba `enviada`: la corrige `db/migrations_mssql/0003_metricas_reenviadas.sql` (`CREATE OR ALTER VIEW`, pendiente de aprobación como el 0002). La vista y su endpoint se retiraron en HU-014.3 (#152).
- **Firma del JWT sin caché global de proveedores** (ajuste sobre la parte A): el emisor usa una sola clave y la validación desactiva `CacheSignatureProviders`. Con varias instancias de la API en un proceso (pruebas en paralelo) aparecían 401 intermitentes con tokens recién emitidos; el patrón coincide con la caché de proveedores de firma de Microsoft.IdentityModel. `OnAuthenticationFailed` deja una línea Information con el tipo y el mensaje del rechazo.
- **HU-004.9, "el comercial solo ve líneas activas":** hoy no hay ningún endpoint de comercial que liste líneas (el catálogo del formulario no está en el contrato); cuando exista debe filtrar `isActive = 1`.

## 2.11 Asistente IA por folio (/web/chat)

**Asistente por folio (HU-013.6, #102, ADR-018 §2b y §14; único agente desde HU-013.9).** `POST /web/chat` es el asistente de UNA cotización: exige `folio`, solo lee y propone, y ningún monto llega al modelo. La acción propuesta (cuerpo, endpoint, `row_version` y resumen con precios) la arma el servidor y la ejecuta el frontend cuando el comercial pulsa Confirmar. Conserva el loop manual de ADR-003 y el validador de #16.

| Pieza | Capa | Qué hace |
|---|---|---|
| `Agente/Services/clsAsistenteFolioRunner` (`IClsAsistenteFolioRunner`) | Aplicacion | Turno del asistente: comercial del token y cotización del folio (no es suya → `COTIZACION_NO_ENCONTRADA`, 404 sin tocar la sesión ni el modelo), candado de la sesión, sesión `web/{sesion_id}` / `web/{sesion_id}/{folio}` ligada al comercial (otro usuario → `NO_ENCONTRADO`, 404), mensaje registrado primero, prompt de sistema, historial con ventana, loop manual hasta `IA:MaxIteraciones` sobre `IChatClient` (sin `UseFunctionInvocation()`), cada herramienta con `kernel.InvokeAsync` (corre el filtro de auditoría), `HERRAMIENTA_DESCONOCIDA` si el modelo pide una inexistente o retirada, respuesta final aparte validada por `IClsValidadorRespuestaAgente`. Solo guarda los eventos de la conversación y el estado de la sesión; nunca escribe datos de negocio. Devuelve `clsResultadoTurno` con la `AccionPropuesta` del turno |
| `Agente/Herramientas/clsHerramientasAsistenteFolio` | Aplicacion | Plugin `asistente` de Semantic Kernel: `consultar_cotizacion` (estado, cliente, línea, ruta, moneda, márgenes como fracción y conexos, sin dinero), `proponer_ajuste_margen(margen, justificacion?)`, `proponer_re_solicitud(motivo, cambios de carga?, nuevaVersion?)` `buscar_servicio_conexo(texto, cantidad?, servicioId?, lineaOrden?)` y, para el mismo cambio en varias rutas, `proponer_ajuste_margen_rutas(margen, justificacion?, todas?, lineas?)`, `proponer_agregar_conexo_rutas(texto, cantidad, servicioId?, todas?, lineas?)` y `proponer_quitar_conexo_rutas(servicio, servicioId?, todas?, lineas?)` (busca entre los conexos de la cotización por id o por nombre, sin tildes ni mayúsculas). Sin rutas indicadas y con varias, preguntan; con una sola, la toman. La cotización sale del contexto, nunca de un argumento del LLM |
| `Agente/Services/clsPropuestasAsistente` | Aplicacion | Arma la acción propuesta con código (`ajustar_margen` → `PUT .../costeo/margen`, `re_solicitar_costeo` → `POST .../costeo/re-solicitar`, `agregar_servicios_conexos` → `POST .../costeo/servicios-conexos`, y en varias rutas `ajustar_margen_lineas`, `agregar_servicios_conexos_lineas` y `quitar_servicios_conexos_lineas` → los endpoints en lote) con las mismas reglas del endpoint destino; las del lote son las funciones puras de `clsCosteoLineasService` (`ValidarFormaMargen`, `PlanMargen`, `ValidarFormaConexo`, `PlanAgregarConexo`, `PlanQuitarConexo`), que usan también sus casos de uso. Si ninguna ruta cambia, no propone (`AJUSTE_MARGEN_INVALIDO` o `CONEXO_INVALIDO`); el error de una ruta llega al modelo como «Ruta N: …». El resumen con precios va en la respuesta y no vuelve al modelo |
| `Agente/Services/clsContextoAsistenteFolio` | Aplicacion | Contexto del turno (uno por ámbito de DI): comercial, hilo (`ThreadId`), cotización validada, línea de negocio y propuesta. Lo leen las herramientas y el filtro de auditoría |
| `Agente/Services/clsPromptAsistenteFolio` + `Agente/Recursos/prompt_asistente_folio.md` | Aplicacion | Prompt de sistema como recurso embebido, con el nombre del comercial, la fecha de negocio y el folio |
| `Agente/Filtros/clsAuditoriaHerramientaFilter` | Aplicacion | `EventosHerramienta` de cada herramienta con la entrada del LLM, la salida que vio el modelo, el éxito, la duración y el hilo del contexto |
| `Agente/Herramientas/clsEsquemaPortableHerramientas` | Aplicacion | Declaraciones de las herramientas con un JSON Schema portable entre proveedores (ADR-003 "Esquemas portables") |
| `Agente/Services/clsVentanaHistorial` | Aplicacion | Ventana por tokens (~4 caracteres por token, `Sesiones:LimiteTokens`) que conserva el primer mensaje y el actual |
| `Agente/Interfaces/IClsValidadorRespuestaAgente` + `Agente/Services/clsValidadorRespuestaAgente`, `clsReglasRespuestaAgente`; `Agente/DTOs/clsRespuestaAgenteEstructurada` | Aplicacion | Validador de la respuesta final (#16, HU-003.10, equivalente .NET de *Instructor*; ADR-003 "Implementación (#16)"). Detalle en [2.13](#213-validador-de-la-respuesta-del-agente). `TryAdd` |
| `Dominio/Agente/clsEstadoConversacion` | Dominio | Estado de la sesión: solo `FolioEnProgreso`, `CotizacionId`, `Version` y `Moneda`, en `Sesiones.estadoAgente`. Lee el JSON legado del slot filling ignorando los campos retirados |
| `Agente/Interfaces/IClsCandadoSesion` / `Agente/Services/clsCandadoSesionSql`, `clsBarridoSesionesHostedService` | Aplicacion / Infraestructura | Un turno a la vez por sesión (`sp_getapplock`, ver abajo) y archivo de las sesiones inactivas (`Sesiones:BarridoMinutos`, `Sesiones:TtlHoras`) |
| `IA/Services/clsChatClientFactory`, `clsChatClientConRespaldo`, `clsLimitadorRpm` (+ `clsChatClientLimitado`, `clsChatClientNoConfigurado`) | Infraestructura | `IChatClient` por `IA:Proveedor` (ver [03-infraestructura](03-infraestructura.md)), respaldo ante 429, 5xx, red caída o tiempo agotado (`IA:Fallbacks`), límite de `IA:Rpm` y `UseLogging`; sin proveedor, un cliente que falla con un mensaje claro |
| `Agente/Controllers/clsChatWebController`; `Pdf/Controllers/clsPdfController` | Api | `POST /web/chat` (política `Comercial`, límite `LimiteChat`, `sesion_id` y `folio` validados, 400 `FOLIO_REQUERIDO` sin folio, 404 si la sesión o el folio son de otro, 200 `{texto, error: true}` ante un fallo); `/pdfs` solo con Bearer (la URL firmada `?t=` se retiró en HU-013.9) |

Decisiones vigentes de #12 parte C:
- **Carpetas:** `Aplicacion/Agente/` suma `Herramientas/` (plugin de Semantic Kernel), `Filtros/` (`IFunctionInvocationFilter`) y `Recursos/` (prompt embebido) a las de la convención; Infraestructura suma `IA/Services/` (ADR-003: solo ahí se conocen los SDK de los proveedores).
- **`KernelFunction` como declaración, no `AsAIFunction()`:** en Semantic Kernel 1.80 `KernelFunction.AsAIFunction()` está obsoleta y, usada como `AITool`, la función se llama `<plugin>_<nombre>`. El runner declara al modelo cada `KernelFunction` con su nombre corto (`clsEsquemaPortableHerramientas`) y siempre ejecuta con `kernel.InvokeAsync`: solo declaración, nunca invocación automática.
- **Respuesta final en una llamada aparte, siempre** (ADR-003): el texto del loop se descarta y cada turno cuesta una llamada más, a cambio de un solo camino de validación (#16: JSON Schema, validación manual, un reintento y respaldo).
- **Un turno a la vez por sesión:** `IClsCandadoSesion` / `clsCandadoSesionSql` (`sp_getapplock` con dueño `Session`, recurso `cotifly:sesion:{spaceId}:{threadId}`, conexión propia sin pool, espera `Sesiones:EsperaTurnoSegundos` = 3). Si otro turno sigue en curso, `/web/chat` responde 200 `{texto, error: true}` ("Todavía estoy respondiendo tu mensaje anterior..."). Vale entre réplicas.
- **Contexto de `docker build`:** `.dockerignore` excluye `**/*.md`; la excepción `!src/**/Recursos/*.md` deja entrar el prompt embebido (sin ella, CS1566). `Cotifly.Arquitectura.Tests/clsContextoDockerTests` evalúa el `.dockerignore` real con las reglas de Docker contra cada `EmbeddedResource`/`None`/`Content` de los `.csproj` de `src/`: se eligió así (y no prohibir recursos `.md`) porque protege cualquier extensión y cualquier regla futura.
- **Auditoría del login:** un rechazo por `rol_externo_cambio` (y los demás con usuario existente) lleva el id interno del usuario.
- **Campos obsoletos del contrato:** `cotizacion_origen_id` y `moneda` de `MensajeWeb` se aceptan y se ignoran; `precio`, `margen` y `pdf` de `RespuestaChat` siempre van en `null` (los llenaba el agente legado).

## 2.12 Turno de /web/chat de punta a punta

Un mensaje del comercial en la consola web, sobre una cotización, recorre la API, el candado de la sesión, el loop de herramientas del asistente con su filtro de auditoría y el validador de la respuesta final.

```mermaid
sequenceDiagram
    autonumber
    participant F as Frontend
    participant A as clsChatWebController
    participant R as clsAsistenteFolioRunner
    participant K as Candado de sesión (sp_getapplock)
    participant M as IChatClient (proveedor LLM)
    participant FA as clsAuditoriaHerramientaFilter
    participant H as Herramienta del plugin asistente
    participant P as clsPropuestasAsistente
    participant V as clsValidadorRespuestaAgente
    participant D as dbo_cotifly
    F->>A: POST /web/chat {sesion_id, folio, mensaje} + Bearer
    Note over A: JwtBearer + jti revocado, política Comercial, LimiteChat, validación del cuerpo (sin folio: 400 FOLIO_REQUERIDO)
    A->>R: EjecutarTurnoAsync(comercial, web/{sesion_id}, web/{sesion_id}/{folio}, mensaje)
    R->>D: cotización del folio del comercial (otra: 404, sin llamar al modelo)
    R->>K: tomar cotifly:sesion:{space}:{thread} (espera 3 s)
    alt Otro turno en curso
        A-->>F: 200 {texto: "Todavía estoy respondiendo...", error: true}
    end
    R->>D: sesión del comercial (otra persona: 404) y mensaje registrado
    loop hasta IA:MaxIteraciones
        R->>M: historial con ventana + prompt del asistente + declaraciones de herramientas
        M-->>R: texto o llamadas a herramientas
        opt El modelo pide una herramienta
            R->>FA: kernel.InvokeAsync(herramienta, argumentos del LLM)
            FA->>H: argumentos
            H->>D: lectura de la cotización del contexto (nunca escribe)
            opt Herramienta de propuesta
                H->>P: armar la acción (mismas reglas del endpoint destino)
                P-->>H: propuesta al contexto (el resumen con precios no vuelve al modelo)
            end
            H-->>FA: resultado sin montos
            FA->>D: EventosHerramienta
            FA-->>R: resultado
        end
    end
    R->>V: llamada final sin herramientas con JSON Schema respuesta_agente
    V->>M: pedir la respuesta estructurada
    M-->>V: JSON
    V->>V: esquema, coherencia y montos respaldados
    opt Inválida
        V->>M: un reintento con mensaje de corrección
        Note over V: si vuelve a fallar: respaldo {tipo: error}
    end
    V-->>R: respuesta validada o respaldo
    R->>D: eventos de la conversación y estado (Sesiones.estadoAgente)
    R-->>A: clsResultadoTurno + AccionPropuesta
    A-->>F: 200 RespuestaChat {texto, herramientas, errores, accion_propuesta}
```

Puntos de control del diagrama:

- **Ningún monto llega al modelo.** Las herramientas devuelven porcentajes, estados, rutas y nombres; el resumen con precios de la propuesta solo va al frontend.
- **Confirmación humana.** El asistente no cambia datos: el frontend ejecuta la acción propuesta cuando el comercial pulsa Confirmar, contra el endpoint normal (con su `If-Match`).
- **Un turno a la vez por conversación**, también entre réplicas (`sp_getapplock` con dueño de sesión).
- **Fallos del proveedor** (429, 5xx, red, tiempo agotado) pasan primero por los respaldos de `IA:Fallbacks`; si se agotan, `/web/chat` responde 200 `{texto, error: true}`.

## 2.13 Validador de la respuesta del agente

Equivalente .NET de *Instructor* (HU-003.10, #16; decisión en [ADR-003](../specs/decisiones/ADR-003-orquestacion-ia-agnostica.md), sección "Implementación (#16)"). Ninguna respuesta del modelo llega al comercial sin pasar por aquí.

| Aspecto | Comportamiento |
|---|---|
| Esquema `respuesta_agente` | Generado con `AIJsonUtilities`: `tipo` (`conversacional`, `previsualizacion`, `confirmacion_requerida`, `error`), `texto` (1 a 4000 caracteres), `requiere_confirmacion` (booleano), `acciones_sugeridas` (`confirmar_pdf`, `ajustar_margen` o null). Sin propiedades adicionales |
| Cómo se pide | Llamada final aparte, sin herramientas, con `ChatResponseFormat.ForJsonSchema`. Con `IA:SoportaJsonSchema=false`, o si el proveedor rechaza el esquema (HTTP 400/422 que nombra `response_format` o `json_schema`), el esquema va en la instrucción durante `IA:MinutosSinJsonSchema` y luego se vuelve a probar |
| Validación manual | JSON completo; campos desconocidos, faltantes o repetidos; tipos y enums; largos; coherencia entre `tipo` y `requiere_confirmacion`; `previsualizacion` nunca es válida (`previsualizacion_sin_precio`: el asistente no calcula precios); una acción sugerida exige su herramienta declarada, y ninguna del asistente lo es (sus propuestas van en `accion_propuesta`); montos del texto respaldados solo por las salidas de las herramientas del turno o por lo que escribió el comercial (el estado no tiene montos); folios respaldados por el estado, las herramientas o el comercial |
| Reintento | Uno, con un mensaje de corrección que nombra el problema |
| Respaldo | `{tipo: error}` con `clsTextosAgente.RespuestaNoEstructurada`. En la web: `RespuestaChat` con la acción propuesta del turno si la hubo (texto `clsTextosAgente.PropuestaSinTexto`), o `200 {texto, error: true}` si no |
| Observabilidad | Medidor `Cotifly.Agente` (respuestas inválidas por motivo, reintentos, respaldos, esquema rechazado); logs solo con los códigos, nunca con el texto. Ver [10-operacion](10-operacion.md#103-métricas) |

```mermaid
stateDiagram-v2
    [*] --> Pedir: llamada final sin herramientas
    Pedir --> Validar: JSON del modelo
    Validar --> Aceptada: cumple esquema y reglas
    Validar --> Reintento: inválida (primera vez)
    Reintento --> Validar2: JSON corregido
    Validar2 --> Aceptada: cumple
    Validar2 --> Respaldo: inválida otra vez
    Pedir --> SinEsquema: proveedor rechaza JSON Schema (400/422)
    SinEsquema --> Validar: esquema en la instrucción
    Aceptada --> [*]
    Respaldo --> [*]
```

La batería de textos del validador y sus límites conocidos están en [10-operacion](10-operacion.md#106-riesgos-y-limitaciones-conocidos).

## 2.14 Integración con Nexo

Nexo entrega a Cotifly los datos del formulario de captura y la búsqueda de clientes (HU-005.2, HU-005.3, HU-005.4 y HU-005.13; [ADR-005](../specs/decisiones/ADR-005-busqueda-clientes-desde-chat.md)). El mapeo de campos está en [`docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md`](../../docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md). Cada módulo tiene un puerto en Aplicacion, un adaptador de Nexo y uno "no disponible" en Infraestructura, y una fábrica que elige entre los dos al resolver según `Nexo:UrlBase`:

| Módulo | Puerto (Aplicacion) | Adaptadores (Infraestructura) | Endpoint de Nexo | Expuesto en | Código si no está disponible |
|---|---|---|---|---|---|
| `Clientes` | `IClsBuscadorClientes` | `clsBuscadorClientesNexo` / `clsBuscadorClientesNoDisponible` (`clsFabricaBuscadorClientes`) | `GET /api/captura-solicitudes/clientes?q=` | `GET /web/clientes?q=` y el envío del costeo (la herramienta `buscar_cliente` se retiró en HU-013.9) | `CLIENTE_BUSQUEDA_NO_DISPONIBLE` |
| `Municipios` | `IClsMunicipiosProvider` | `clsMunicipiosNexo` (tras `clsMunicipiosEnCache`) / `clsMunicipiosNoDisponible` (`clsFabricaMunicipios`) | `GET /api/captura-solicitudes/municipios` | `GET /web/municipios` (la herramienta `resolver_municipio` se retiró en HU-013.9; `clsResolvedorMunicipios` se conserva para la validación de licitaciones, #108) | `MUNICIPIOS_NO_DISPONIBLE` |
| `CatalogosFlete` | `IClsCatalogosFleteProvider`, `IClsCatalogosMaestrosProvider` | `clsCatalogosFleteNexo` / `clsCatalogosFleteNoDisponible` (`clsFabricaCatalogosFlete`) sobre `clsCatalogosMaestrosNexo` con `clsCatalogosMaestrosEnCache` (`clsFabricaCatalogosMaestros`, HU-013.10) | `GET /api/catalogos/tipos-vehiculo`, `clases-viaje`, `clases-contenedor` y `lineas-negocio` | `GET /web/catalogos-flete` y la validación por código de `POST /web/solicitudes-costeo` (`clsValidadorCatalogosCaptura`) | `CATALOGOS_FLETE_NO_DISPONIBLE` |
| `Costeo` (ADR-015) | `IClsCosteoNexo` | `clsCosteoNexo` / `clsCosteoNexoNoDisponible` (`clsFabricaCosteoNexo`) | `POST /api/captura-solicitudes` (HU-005.5) | `POST /web/solicitudes-costeo` y `POST /web/cotizaciones/{folio}/costeo/reintentar` | `COSTEO_NEXO_NO_DISPONIBLE`, `COSTEO_NEXO_TIEMPO_AGOTADO`, `COSTEO_NEXO_RECHAZADA` (la cotización queda en `error_envio` con el motivo) |

La cubicación rápida de Nexo (`IClsCubicacionProvider`, `clsCubicacionNexo`, `POST /api/cubicacion`) se retiró en HU-023.2 (#157): `POST /web/cubicacion` ("Calcular cubicaje") usa el cubicador por `IClsCubicador` con `clsCalculoCubicajeService` (ADR-020).

Comportamiento común (`clsDependenciasInfraestructura.AgregarBuscadorClientes`, `AgregarMunicipios`, `AgregarCatalogosFlete`):

- **Sin token.** Nexo no tiene login propio: cada petición lleva `X-Portal-clsUsuario-Id` con el `UsuariosWeb.idUsuarioExterno` del comercial (la misma fila de `tblSAPUsuariosApp` que usa el login de ClientePro). El puerto recibe el `UsuariosWeb.id` del token en `/web/*`; nunca lo decide el llamante. Un comercial sin `idUsuarioExterno` recibe el código "no disponible" sin llamar a Nexo.
- **Un solo cliente HTTP con nombre `Nexo`** para los adaptadores de consulta (el costeo usa el suyo, ver abajo), con `RedactLoggedHeaders` (el encabezado no aparece en los logs) y la tubería estándar de resiliencia registrada una sola vez (`clsBuscadorClientesNexo.ConfigurarResiliencia`): 8 s en total, 3 s por intento, 2 reintentos con espera exponencial desde 200 ms y jitter, y cortocircuito con muestreo de 30 s. Registrarla por cada cliente tipado apilaría una tubería por adaptador.
- **Nunca lanzan.** HTTP no exitoso, error de red, tiempo agotado, circuito abierto o JSON inválido devuelven el código "no disponible" del módulo (`clsResultado`), con una línea `Warning` por fallo que nombra el motivo y el detalle de la excepción en `Debug`. La cancelación de quien llama se propaga (no es una caída de Nexo).
- **`TryAdd`:** un adaptador registrado antes (pruebas) se respeta.

**Costeo (ADR-015, HU-005.5; por rutas y camiones desde HU-021, [ADR-019](../specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md)).** `clsEnvioCosteoService` (Aplicacion) crea la solicitud del formulario (folio de ADR-011 con el NIT; sus rutas con mercancías y conexos, `clsCapturaRutasService`; los camiones de cada ruta, `IClsPlanificadorCamiones`: el cubicador o, de respaldo, un camión sin vehículo con el peso de la ruta) en `pendiente_envio` y **confirma la transacción antes de llamar a Nexo**. Luego resuelve el BP en el catálogo de Nexo con `IClsBuscadorClientes` (candidato con el mismo NIT; nunca del formulario, ADR-005 §4) y llama a `IClsCosteoNexo.EnviarSolicitudAsync` con **una línea por camión** (`clsCotizacionSolicitud.LineasNexo()`; cada una con el `lineaNegocio` = `MargenesPolitica.mapeoLineasNexo` de su ruta). Con 201 guarda `solicitudNexoId` (ex `costeoId`), `costeoEnviadoAt`, el estado `pendiente_costeo` y la primera consulta del polling (`CosteoNexo:IntervaloInicialSegundos`, 5 s); si Nexo ya costeó todas las líneas, lee el costeo enseguida con la regla del polling (`clsLecturaCosteo`) y la deja en `autocosteo` con precio (HU-013.14, ADR-018 §17); cualquier fallo (tiempo agotado, 4xx con `HTTP {estado} {code}: {message}` de Nexo, 5xx, red, cliente sin BP) deja `error_envio` con `motivoEstadoCosteo` y el endpoint responde igual 201. El reintento (`ReintentarAsync`) solo vale desde `error_envio` o un `pendiente_envio` de más de 2 minutos, reenvía lo guardado y reclama la cotización con un `UPDATE` condicional sobre `updatedAt` (dos reintentos simultáneos no duplican la solicitud). Cada intento queda en `EventosHerramienta` como `enviar_costeo_nexo`.

- **Cliente HTTP propio `NexoCosteo`** (`clsCosteoNexo.ConfigurarResiliencia`): 30 s en total y por intento (el alta auto-costea en línea), 2 reintentos **solo en GET** (`DisableForUnsafeHttpMethods`: un POST no idempotente nunca se reintenta) y muestreo del cortocircuito de 60 s. Es la única desviación del patrón `Nexo` y se registra aparte en `AgregarCosteo`.

**Polling del costeo (ADR-015 §4, HU-005.6).** `clsWorkerCosteoNexo` (`BackgroundService`, Infraestructura) revisa cada `CosteoNexo:IntervaloTickSegundos` la agenda (`CotizacionesSolicitud.costeoSiguienteConsultaAt <= ahora`, índice filtrado `ix_cotizaciones_costeoSiguienteConsultaAt`, hasta `CosteoNexo:Lote`) en un ámbito de DI y procesa cada cotización en **su propio ámbito** con `clsPollingCosteoService.ProcesarAsync` (Aplicacion); una excepción de una cotización se registra y el lote sigue. Por cotización:

1. **Reclamo** con un `UPDATE` condicional sobre `costeoSiguienteConsultaAt` (lo mueve a `ahora + CosteoNexo:ReclamoSegundos`): dos réplicas nunca la consultan a la vez y, si el proceso muere, se retoma pasado el reclamo.
2. **Edad máxima** (`CosteoNexo:EdadMaximaHoras`, 72 h): sin costeo pasa a `costeo_vencido` con el motivo (visible en "Mis cotizaciones"); en `autocosteo` solo deja de consultarse.
3. **Consulta** `IClsCosteoNexo.ConsultarCosteoAsync`: `GET /api/solicitudes/{solicitudNexoId}` (estado y líneas; sin `solicitudNexoId` la cotización pasa a `error_envio` con `SOLICITUD_NEXO_DESCONOCIDA`) (un 409 pasajero de concurrencia se reintenta una vez) y, salvo `Descartada`, la cotización de Nexo **sin escribir**: con `cotizacionNexoId` ya guardado, `GET /api/cotizaciones/{id}`; si no, `GET /api/cotizaciones/por-solicitud/{solicitudId}` (204 = Nexo aún no la tiene: sin fletes). Solo con un Nexo sin esa ruta se usa una vez `POST /api/cotizaciones {solicitudId}` ("crear o recuperar"). Guarda `cotizacionNexoId`. E2E 2026-10-16: antes el "crear o recuperar" iba en cada ciclo (69 escrituras en 20 min para 5 solicitudes).
4. **Emparejamiento y derivación** en el dominio. Nexo devuelve las líneas sin orden: `clsEmparejadorLineasNexo` asocia cada camión con su línea por contenido (ruta, peso, tipología, vehículo y carrocería) y guarda `lineaSolicitudNexoId` en el camión. El estado de cada camión sale de `clsDerivacionEstadoCosteo` (la regla de la bandeja de Nexo): `Descartada` → `descartada`; cotización o solicitud `Entregada` → `entregada`; línea con flete → `autocosteo`; si no, `pendiente_costeo`. La ruta y la cabecera resumen el de sus camiones (`clsResumenEstadoCosteo`). `entregada` y `descartada` son terminales (sin siguiente consulta), salvo una `entregada` sin precio ni PDF, que se lee una vez más aunque haya pasado la edad máxima; el worker agenda esas filas al arrancar (HU-013.14, ADR-018 §17).
5. **Espaciado** (`clsAgendaPollingCosteo`): `clamp(edad × CosteoNexo:FactorEdad, IntervaloInicial, IntervaloMaximo)`, por defecto de 5 s a 15 min; tras fallos seguidos, `× 2^min(fallos, 5)` sin pasar del máximo.
6. **Errores de Nexo** (tiempo agotado, 4xx, 5xx, 404 "id no reconocido", JSON inválido): no cambian el estado, suman `costeoConsultasFallidas`, reprograman y dejan un evento `costeo_nexo_consulta` con `exito = false`. **Cada cambio de estado** deja un evento `costeo_nexo_estado` (estado anterior y nuevo) en `EventosHerramienta`.

Idempotente: la misma foto de Nexo no cambia nada ni audita dos veces. Sin `Nexo:UrlBase` o con `CosteoNexo:Habilitado=false` el worker no corre (una línea de log al arrancar). Las pruebas lo apagan y procesan la agenda con `clsWorkerCosteoNexo.ProcesarLoteAsync`.

**Margen sobre el costeo (ADR-015 §5, HU-005.7).** Por ruta desde HU-021 (ADR-019). Al enviar, `clsEnvioCosteoService` resuelve el margen de cada ruta con `IClsMargenResolverService` (la línea de negocio de la ruta y el BP del catálogo: SAP TM o la política) y lo guarda en la ruta (`margenSAPSugerido` y `margenPoliticaSugerido`; vale el de SAP si existe). Cuando el polling lee el costeo, `clsPrecioCosteoService` registra el flete y el retorno de cada camión; la ruta suma fletes, retornos, levantes y conexos (`costoFinal`, solo interno) y calcula `precioFinalLinea` con su margen y su método; `precioFinal` de la cabecera es la suma de las rutas. Los conexos son filas de la ruta (`CotizacionServiciosConexos`) con el costo del catálogo de Nexo como precio, y entran en el precio de la ruta (decisión 3). El ajuste del comercial es por ruta (`PUT .../costeo/margen` con `linea_orden` o `linea_solicitud_id`; `MargenAjustes.lineaSolicitudId`). Si el costeo cambia, el precio se recalcula mientras no haya PDF; con PDF se conserva y `motivoEstadoCosteo` lo advierte. Sin margen no hay precio (nunca se muestra el costo). `clsCosteoCotizacionService` expone el costeo (`GET /web/cotizaciones/{folio}/costeo`, solo precios, con `rutas[]`), los conexos y el PDF (`POST .../costeo/pdf`: el payload lo arma el servidor con la tarifa del transporte de cada ruta y los conexos aparte; exige todas las rutas con precio y `precioFinal` = Σ `precioFinalLinea`; reutiliza `clsConstructorDocumentoCotizacion`, `IClsGeneradorPdf`, `IClsAlmacenPdf` y la validación de coherencia de `clsGenerarPdfService`). En USD cada precio se calcula en pesos y se convierte con la `trmAplicada` de la cabecera (ADR-014 §5).

**Resultado en Nexo (ADR-015 §6, HU-005.14).** `clsResultadoCotizacionService` valida (`resultado`, límites de Nexo: `motivo` 100 y `comentario` 500), recupera `cotizacionNexoId` si el polling aún no lo guardó (`POST /api/cotizaciones`, crear o recuperar) y llama a `IClsCosteoNexo.MarcarResultadoAsync`; solo con 200 escribe `ganada`, `motivoRechazo` y `marcadoAt`. Los 409 de Nexo (`RESULTADO_YA_DEFINIDO`, `COTIZACION_NO_ENTREGADA`) conservan su código y responden 409; Nexo caído responde 503; en ambos casos nada cambia en Cotifly. Si la respuesta de Nexo trae `Entregada` (entrega implícita, que Nexo quitó en su issue #171), `estado_costeo` pasa a `entregada` sin tratarse como inconsistencia. Cada intento queda en `EventosHerramienta` como `resultado_cotizacion_nexo`. Identidad: el `X-Portal-clsUsuario-Id` del comercial (pendiente de confirmar con Nexo si debe ser un rol de servicio).

Decisiones de las respuestas:

- **Coincidencia exacta:** Nexo no distingue exacta de aproximada; hasta definirlo con su equipo (pendiente de ADR-005), `exacta=true` solo cuando hay un único candidato. Las filas sin `nitCliente`, `nombre` u `origen` se descartan.
- **Campos omitidos:** municipios sin `nombreTarifasPlaza` ni `zonaTarificacion` (costeo interno); catálogos de flete sin `lineasNegocio` (Cotifly tiene su propio catálogo) y con "Carroseria" corregido a `tipos_carroceria`.
- **Municipios (HU-005.13):** el mapeo aprobado cambió el alcance de la issue: Nexo no expone `/departamentos` ni `/ciudades?departamento=&q=` (cascada), sino un solo catálogo sin filtro. La búsqueda la hace Cotifly (`Municipios/Services/clsResolvedorMunicipios`, sin tildes, mayúsculas ni signos, con `D.C.`/`DC`/`D C` iguales y el país al final ignorado): exacta por municipio o por municipio y departamento; con pista de departamento (`La Unión, Nariño`, `Cartagena, Bolívar`, `Cúcuta Norte de Santander`), donde de departamentos anidados gana el más largo; por palabra completa (`Cali` → `Santiago de Cali`, nunca `Calima` ni `California`), y entre varias por palabra, la única capital de departamento, Dane `XX001`, solo si el texto es el comienzo de su nombre y no es un componente genérico (`san`, `santa`, `puerto`, `villa`, `juan`...): `Cartagena` → `Cartagena de Indias`, no `Cartagena del Chairá`, mientras que `Santa`, `Juan` o `Puerto` se preguntan con la capital primera; por comienzo de palabra (4 letras o más) y, como último recurso, aproximada (siempre se confirma). Dos municipios con el mismo nombre exacto (`La Unión`, `Armenia`) siempre se preguntan. Hasta 8 candidatos, con el total. Como el catálogo cambia poco (y el agente legado lo consultaba en cada `resolver_municipio`, hasta HU-013.9), `clsMunicipiosEnCache` guarda la última respuesta exitosa en una foto singleton durante `Nexo:MinutosCacheMunicipios` (60 por defecto; 0 la desactiva; los fallos no se guardan; recarga de a una). La foto es la misma para todos los comerciales: Nexo devuelve el catálogo completo sin filtrar por usuario y no es un dato sensible.
- **Cubicación:** ya no pasa por Nexo (HU-023.2, #157). "Calcular cubicaje" llama al cubicador con una corrida `PRUEBA-<guid>` que no se guarda; ver ADR-020.

## 2.15 Licitaciones desde Excel y cotizaciones de varias líneas (HU-011, ADR-017)

Una licitación es **una solicitud con una ruta por fila del Excel** (`dbo_cotifly.CotizacionLineasSolicitud`, script 0005, reestructurada por el 0009). Desde HU-021 ([ADR-019](../specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md)) la cabecera ya no tiene columnas de línea (`origen`, `destino`, `costo`, `lineaNegocioId`…): todo es de la ruta, y cada ruta tiene sus camiones (`CotizacionVehiculosLineaSolicitud`). A Nexo viaja una línea por camión. `rutaOrigenIndice` (ex `filaOrigen`) es la fila del Excel.

| Pieza | Capa | Qué hace |
|---|---|---|
| `clsLineaSolicitudCosteo`, `clsEncabezadoSolicitudCosteo` | Dominio | Datos de `CapturaLineaRequest` y del encabezado; `Errores()` devuelve TODOS los errores por campo con el código de Nexo |
| `clsLineaSolicitud`, `clsVehiculoLineaSolicitud` | Dominio | La ruta persistida (orden, fila del Excel, totales, margen y precio) y sus camiones (id de la línea de Nexo, estado y costos internos) |
| `clsArchivoLicitacion` | Dominio | El Excel original (10 MB, SHA-256, hoja) |
| `clsDerivacionEstadoCosteo.DerivarLinea` / `CostosCamion` | Dominio | Estado, flete y retorno de la línea de Nexo de un camión con la regla de ADR-015 §4 |
| `clsValidadorLicitacion` | Aplicacion | Reporte por fila (índice, fila, campo, código) antes de guardar |
| `clsLicitacionService` | Aplicacion | Analizar (proxy), crear (folio, rutas con sus mercancías, conexos y camiones, archivo; confirma y envía con `IClsEnvioCosteoService.EnviarGuardadaAsync`) y ver |
| `clsPrecioCosteoService.ActualizarCamiones` / `AplicarPrecio` | Aplicacion | Empareja cada camión con su línea de Nexo por contenido, registra estado, flete y retorno, y recalcula los totales y el precio de cada ruta; `precioFinal` solo con todas las rutas con precio |
| `clsAnalizadorExcelNexo` / `clsAnalizadorExcelNoDisponible` | Infraestructura | `POST /api/captura-solicitudes/analizar-excel` (multipart) por el cliente `NexoCosteo` |
| `clsLectorExcelLicitacionClosedXml` | Infraestructura | Puerto 1:1 de la detección de filas de Nexo (`ExcelSolicitudesAdapter`) para ubicar la `fila` en el archivo; HU-035.2 (#213): pasa por `clsLecturaExcelAcotada` y, en el análisis, lee antes de Nexo |
| `clsExportadorExcelLicitacionClosedXml` | Infraestructura | Exportación (HU-011.3): el original con tres columnas de Cotifly a la derecha y la hoja `Resumen Cotifly`; recibe solo precios y estados (`clsExportacionLicitacion`), nunca costos |
| `clsCosteoCotizacionService.PayloadPliego` / `ResumenPliego` / `ValidarTotal` | Aplicacion | PDF de pliego (HU-011.5; por ruta desde HU-021.9): una sección por ruta con su línea de negocio, sus camiones y su tarifa, resumen de rutas y total sin conexos; los conexos aparte. `ValidarTotal`: todas las rutas con precio y `precioFinal` = Σ `precioFinalLinea` (una ruta y pliego) |
| `clsBloquePliego` | Infraestructura | Bloque MigraDoc del resumen del pliego (`VarianteDocumento.Pliego`, sobre el diseño multi-sección) |
| `clsLectorGrillaExcelClosedXml` | Infraestructura | HU-035.1 (#202): Excel de mercancías como grilla cruda, local (ClosedXML), acotado contra bombas zip y archivos corruptos; nunca Nexo |
| `clsLecturaExcelAcotada` | Infraestructura (Comun) | HU-035.2 (#213, ADR-024): controles compartidos del Excel subido (firma, entradas, 64 MB descomprimidos, 1.000.000 de elementos XML sin DTD) y cupo único de 4 lecturas por proceso; la usan el lector de mercancías y el del pliego |
| `clsAnalisisExcelMercanciasService` / `clsDetectorMercanciaExcel` | Aplicacion | HU-035.1: valida el archivo (`clsArchivoExcel`, Dominio), lee la grilla y sugiere el código de mercancía con el catálogo del cubicador (opcional); el detector lo comparte con las licitaciones |
| `clsMercanciasExcelController` | Api | `POST /web/mercancias/analizar-excel` (multipart, rol de la captura) |
| `clsLicitacionesController` | Api | `/web/licitaciones/analizar-excel`, `/web/licitaciones` (multipart o JSON), `/web/licitaciones/{folio}` y `/web/licitaciones/{folio}/excel` |

Flujo: subir → Nexo analiza → el frontend mapea → `POST /web/licitaciones` valida todo → guarda y confirma → envía a Nexo (una línea por camión) → el polling (`clsWorkerCosteoNexo`, sin cambios de agenda) empareja los camiones y deriva el estado y el precio de cada ruta.
