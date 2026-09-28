# 4. Flujo del sistema

Cómo se mueve una cotización por el sistema, desde el login hasta el correo. El detalle de cada pieza está en [02-arquitectura](02-arquitectura.md); el contrato HTTP, en [07-api](07-api.md).

Contenido:

- [4.1 Visión general](#41-visión-general)
- [4.2 Inicio de sesión y renovación](#42-inicio-de-sesión-y-renovación)
- [4.3 Nueva cotización en la consola web](#43-nueva-cotización-en-la-consola-web)
- [4.4 Mis cotizaciones, seguimiento y nueva versión](#44-mis-cotizaciones-seguimiento-y-nueva-versión)
- [4.5 Resolución del margen](#45-resolución-del-margen)
- [4.6 Google Chat](#46-google-chat)
- [4.7 Envío de correo](#47-envío-de-correo)
- [4.8 Estados](#48-estados)
- [4.9 Procesos en segundo plano](#49-procesos-en-segundo-plano)

## 4.1 Visión general

```mermaid
flowchart LR
    login[Login SSO desde ClientePro<br/>POST /api/auth/login-clientepro] --> chat[Conversación con el agente<br/>POST /web/chat o Google Chat]
    chat --> datos[Cliente, línea, ruta y costo]
    datos --> margen[Margen<br/>SAP TM o política de la línea]
    margen --> precio[Precio<br/>margen o markup]
    precio --> previa[Previsualización]
    previa -- "el comercial confirma" --> pdf[PDF con folio del servidor<br/>fila en Cotizaciones]
    previa -- "ajustar margen" --> ajuste[Margen negociado<br/>MargenAjustes]
    ajuste --> precio
    pdf --> correo[Vista previa y envío por correo<br/>estado enviada o reenviada]
    pdf --> mis[Mis cotizaciones<br/>seguimiento ganada o perdida]
    mis --> version[Nueva versión<br/>folio con -vN]
    version --> chat
```

## 4.2 Inicio de sesión y renovación

1. ClientePro ya autenticó a la persona y llama `POST /api/auth/login-clientepro` con su `idUsuario`.
2. Cotifly revalida la persona y sus roles contra `dbsolucionesti`, aplica la verificación doble contra `UsuariosRoles` y, en el primer ingreso, crea el usuario (JIT). Secuencia completa en [2.9](02-arquitectura.md#29-identidad-login-sso-con-verificación-doble).
3. El frontend usa `Authorization: Bearer <access>` (60 minutos) en cada llamada a `/web/*`.
4. Ante un 401, el frontend llama una vez `POST /api/auth/refresh`; el refresh rota (familia y vencimiento absoluto heredados). Reutilizar un refresh ya rotado revoca todas las sesiones del usuario.
5. `POST /api/auth/logout` revoca el refresh y el `jti` del access (siempre 204).
6. Primer ingreso: si `GET /web/usuario/telefono` devuelve `null`, el frontend pide el teléfono (modal de HU-004.3) y lo guarda con `PUT`; es el único origen del "Cel" del PDF.

```mermaid
stateDiagram-v2
    [*] --> Vigente: login (familia nueva)
    Vigente --> Rotado: refresh (emite el siguiente de la familia)
    Rotado --> ReusoDetectado: se presenta otra vez
    ReusoDetectado --> [*]: revoca todos los refresh del usuario
    Vigente --> Revocado: logout
    Vigente --> Vencido: 30 días desde el login
    Revocado --> [*]
    Vencido --> [*]
```

## 4.3 Nueva cotización en la consola web

Conversación típica (el texto lo redacta el LLM; los datos y los números salen de las herramientas y del estado de la conversación):

| Paso | El comercial | El sistema |
|---|---|---|
| 1 | "Cotiza un flete nacional de Cali a Bogotá para el cliente X, costo 2.850.000" | `buscar_cliente` en Nexo (HU-005.2: un único candidato → NIT, origen y BP en el estado; varios → candidatos para que el comercial elija; ninguno → sin cliente resuelto; sin `Nexo__UrlBase`, con Nexo caído o sin `idUsuarioExterno` → `CLIENTE_BUSQUEDA_NO_DISPONIBLE` y el nombre queda como referencia, sin NIT ni BP), `resolver_municipio` con el origen y con el destino (HU-005.13, ver 1b), `obtener_margen` (con BP: SAP TM si `SapTm__Modo` está configurado; sin BP: política de la línea), `calcular_precio` con el costo escrito por el comercial. Estado: línea, municipios, margen, método, costo y precio |
| 1b | "La de Nariño" | Solo si el origen o el destino coincidieron con varios municipios (ej. "La Unión" en Antioquia, Nariño y Valle del Cauca): el agente mostró los candidatos con su departamento y, mientras el comercial no elige, el filtro rechaza `buscar_tarifa`, `calcular_precio`, `registrar_ajuste_margen` y `generar_pdf` con `CONFIRMACION_REQUERIDA`. El servidor reconoce la elección por el departamento o, si no hay otra lista pendiente, por posición ("2", "la segunda"); si no, el agente vuelve a llamar a `resolver_municipio` con lo que escribió. Una sola coincidencia se usa directo (`Yumbo, Valle del Cauca`); ninguna se informa sin inventar un municipio. Sin catálogo (`MUNICIPIOS_NO_DISPONIBLE`: Nexo sin configurar o caído) la cotización sigue con el texto libre |
| 2 | — | El agente muestra la **previsualización** (precio, margen, método, origen del margen) y pide confirmación; `acciones_sugeridas`: `confirmar_pdf`, `ajustar_margen` |
| 3a | "Confirmo" | `generar_pdf`: el filtro exige la confirmación del turno y un precio calculado, reescribe el payload con el estado, el servidor genera el folio `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]` con las iniciales del comercial (ej. `PCN-DG-25092026-001`; [ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)), renderiza, guarda el PDF y registra la fila en `Cotizaciones`. La respuesta trae `pdf.url` |
| 3b | "Déjalo en 18 %" | `registrar_ajuste_margen` (margen negociado, auditado en `MargenAjustes`), nuevo precio y nueva previsualización |
| 4 | "Envíala a compras@cliente.com" | Con la confirmación o los destinatarios escritos por el comercial, `enviar_correo` (ver [4.7](#47-envío-de-correo)) |

Reglas que el flujo garantiza (pruebas en `chat_slot_filling.feature`, `chat_municipios.feature`, `chat_web.feature` y `validador_respuesta_ia.feature`):

- Un "sí" sin previsualización pendiente no confirma nada; elegir un cliente candidato o un municipio no confirma el PDF.
- Con un origen o destino ambiguo sin elegir no se busca tarifa, no se calcula precio y no hay previsualización.
- Si cambia el cliente, la línea o la ruta, la cotización en curso se descarta: la siguiente es nueva, no una versión de otra.
- Un costo que no viene de la fuente solo se acepta si el comercial lo escribió (`2.850.000`, `2,85 millones`...).
- Un turno a la vez por conversación; un segundo mensaje simultáneo recibe "Todavía estoy respondiendo tu mensaje anterior...".

El recorrido técnico de un turno (candado, filtros, validador) está en [2.12](02-arquitectura.md#212-turno-de-webchat-de-punta-a-punta).

El formulario de captura del frontend (HU-004) consulta a Nexo a través de la API, sin pasar por el agente: `GET /web/clientes?q=` (el mismo catálogo que `buscar_cliente`), `GET /web/municipios` (origen y destino), `GET /web/catalogos-flete` (tipo de vehículo, carrocería y clase de viaje) y `POST /web/cubicacion` (vehículo o flota sugeridos para los bultos). Respuestas y códigos en [7.3](07-api.md#73-endpoints).

## 4.4 Mis cotizaciones, seguimiento y nueva versión

```mermaid
sequenceDiagram
    participant F as Frontend
    participant A as API /web
    participant D as dbo_cotifly
    F->>A: GET /web/mis-cotizaciones
    A->>D: cotizaciones del comercial (máx. 100, más recientes primero)
    A-->>F: 200 {total, cotizaciones}
    F->>A: GET /pdfs/{folio}.pdf (Bearer)
    A-->>F: 200 application/pdf (solo el dueño, o su gerente de la misma regional)
    F->>A: POST /web/cotizaciones/{folio}/seguimiento {ganada, motivo_rechazo?}
    A->>D: marcadoAt y motivo (solo el dueño)
    A-->>F: 200 CotizacionResumen
    F->>A: GET /web/cotizaciones/{id}/nueva-version
    A-->>F: 200 DatosNuevaVersion {folio_base, version_siguiente, ...}
    F->>A: POST /web/chat {sesion_id, mensaje, cotizacion_origen_id}
    Note over A: siembra el estado con la cotización origen (validando el dueño)<br/>y generar_pdf crea el folio base con -vN
```

Una cotización de otro comercial responde siempre `404 {"error":"Cotización no encontrada o no es tuya."}`, igual que una inexistente (`aislamiento_comercial.feature`).

## 4.5 Resolución del margen

Orden de `obtener_margen` (`clsMargenResolverService`, mismo orden que el Python salvo la resiliencia ante SAP caído, decisión de #13):

```mermaid
flowchart TD
    inicio([obtener_margen: línea y cliente del estado]) --> linea{¿La línea existe<br/>y está activa?}
    linea -- no --> desconocida[MARGEN_LINEA_DESCONOCIDA]
    linea -- sí --> trq{¿Tiene TrqType?<br/>SapMapeoTrqType}
    trq -- no --> politica
    trq -- sí --> bp{¿Hay BP del cliente?}
    bp -- no --> politica[Política de la línea<br/>origen politica_linea, motivo en detalle]
    bp -- sí --> modo{¿SapTm:Modo configurado?}
    modo -- no --> politica
    modo -- sí --> sap[Consulta SAP TM<br/>salesOrg, TrqType, BP]
    sap -- "registro" --> saptm[Margen SAP: Value / 100<br/>origen sap_tm, anómalo fuera de 0 a 0,6]
    sap -- "sin registro" --> politica
    sap -- "caído o tiempo agotado" --> caido{¿La línea tiene política?}
    caido -- sí --> politica
    caido -- no --> fuente[MARGEN_FUENTE_NO_DISPONIBLE]
```

El margen negociado (`registrar_ajuste_margen`) no cambia la política: se registra en `MargenAjustes` con la referencia que propuso `obtener_margen` y queda `origenMargen = negociado` en la cotización.

## 4.6 Google Chat

```mermaid
sequenceDiagram
    autonumber
    participant G as Google Chat
    participant W as clsChatEventosController
    participant Q as clsColaEventosChat (Channel acotado)
    participant K as clsWorkerEventosChat
    participant P as clsProcesadorEventoChat
    participant R as clsAgenteRunner
    participant C as API de Google Chat
    G->>W: POST /chat/events (Bearer JWT de Google)
    W->>W: verifica RS256, emisor y audiencia (GChat:ProjectNumber)
    W->>Q: encola el evento
    W-->>G: 200 acuse inmediato
    K->>Q: toma el evento (un ámbito de DI por evento)
    K->>P: procesar
    P->>P: identidad por UsuariosWeb.email y verificación doble (sin alta)
    alt Sin cuenta o rol cambiado
        P->>C: "Ingresa primero a Cotifly desde ClientePro."
    else Comercial verificado
        P->>R: turno en el hilo (spaceId, threadId)
        R-->>P: respuesta validada
        P->>C: tarjeta de previsualización con botones, tarjeta del PDF con enlace firmado, o texto
    end
    Note over K,C: Si el evento falla, aviso en el hilo y alerta a GChat:SupportSpace. El worker sigue
```

Los clics en los botones de la tarjeta se convierten en "Confirmo" o "Quiero ajustar el margen.". El enlace del PDF (`/pdfs/{folio}.pdf?t=...`) vence a los `Pdf:MinutosUrlFirmada` minutos y solo sirve al dueño.

## 4.7 Envío de correo

```mermaid
flowchart TD
    previa[POST .../correo/vista-previa<br/>asunto, cuerpo, para y cc prediligenciados] --> enviar[POST /web/cotizaciones/folio/enviar<br/>o herramienta enviar_correo]
    enviar --> hab{¿Correo:Habilitado?}
    hab -- no --> deshab[503 CORREO_DESHABILITADO]
    hab -- sí --> dest{¿Destinatarios válidos?}
    dest -- no --> inval[400 CORREO_INVALIDO]
    dest -- sí --> duenio{¿La cotización es del comercial?}
    duenio -- no --> nf[404]
    duenio -- sí --> dup{¿Mismo conjunto de para<br/>enviado hace menos de 5 min?}
    dup -- sí --> dupl[400 CORREO_DUPLICADO]
    dup -- no --> pdf{¿Existe el PDF en el almacén?}
    pdf -- no --> sinpdf[400 CORREO_PDF_NO_ENCONTRADO]
    pdf -- sí --> relay[Relay SMTP<br/>From = correo del comercial]
    relay -- falla --> fallido[Fila Correos fallido<br/>400 CORREO_ENVIO_FALLIDO]
    relay -- ok --> ok[Fila Correos enviado + destinatarios<br/>estadoEnvio: enviada o reenviada]
```

`GET /web/clientes/{nit}/destinatarios-previos` prediligencia los destinatarios del último envío exitoso del comercial a ese NIT.

## 4.8 Estados

Estado de envío de una cotización (`Cotizaciones.estadoEnvio`):

```mermaid
stateDiagram-v2
    [*] --> sin_enviar: generar_pdf registra la cotización
    sin_enviar --> enviada: primer envío exitoso
    enviada --> reenviada: otro envío exitoso
    reenviada --> reenviada: otro envío exitoso
    note right of sin_enviar: un envío fallido no cambia el estado (fila Correos fallido)
```

Seguimiento comercial (independiente del envío): `ganada` verdadero o falso con `motivo_rechazo` y `marcadoAt`. Sesiones de conversación: `activa` → `archivada` tras `Sesiones:TtlHoras` de inactividad (barrido periódico).

## 4.9 Procesos en segundo plano

| Proceso | Clase | Frecuencia | Qué hace | Si falla |
|---|---|---|---|---|
| Worker de Google Chat | `clsWorkerEventosChat` | Continuo | Consume la cola de eventos, un ámbito por evento | Aviso en el hilo y alerta a soporte; sigue con el siguiente |
| Barrido de sesiones | `clsBarridoSesionesHostedService` | `Sesiones:BarridoMinutos` (60) | Archiva sesiones inactivas desde hace más de `Sesiones:TtlHoras` | Warning; reintenta en el siguiente ciclo |
| Limpieza de tokens | `clsLimpiezaTokensHostedService` | Cada hora | Purga `jti` vencidos y refresh vencidos hace más de un día | Warning |
| Caché de revocados | `clsCacheTokensRevocados` | Cada 60 s | Foto de `TokensRevocados`; sin foto de menos de 5 min, fail-closed | Las peticiones protegidas responden 401 hasta recuperar la foto |
| Avisos de arranque de SAP TM | `clsAvisosArranqueSapTm` | Al arrancar | Avisa si `Production` no tiene `SapTm:Modo` o si falta el export | No impide arrancar |
