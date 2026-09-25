# Paridad funcional Python vs .NET (HU-003.9, #15)

Plantilla para el plan y la ejecución de las pruebas de paridad. La define y la ejecuta Kevin (QA). La aprobación explícita de QA queda en el issue #15 antes de que HU-006 considere el backend .NET listo para producción.

- Estado: **plantilla; casos por definir con datos reales**.
- Responsable: Kevin (QA). Soporte: Duban (backend).

## 1. Objetivo y alcance

Comparar el resultado exacto de al menos **10 casos reales** entre el backend Python actual y el backend .NET, cubriendo distintas líneas de negocio: margen (valor y origen), precio, utilidad y PDF generado. Además, probar con Postman los 5 endpoints `/web/*` del contrato congelado (HU-004.1) y confirmar códigos de estado y forma de respuesta.

La paridad automatizada ya cubre cada caso de `tests/test_precio.py`, `tests/test_margen.py`, SAP TM, PDF, correo, agente y Google Chat (tabla en [08-testing](../08-testing.md#88-paridad-con-el-python)). Esta prueba agrega datos reales y la comparación visual del PDF.

## 2. Diferencias deliberadas (no son hallazgos)

Decididas por el equipo y documentadas; si aparecen, se marcan como "esperada" con la referencia:

| Tema | Python | .NET | Referencia |
|---|---|---|---|
| Redondeo en empates exactos de medio peso | Arrastra el error de `float` | Half-to-even sobre el valor exacto (diferencia máxima 1 peso, ~0,04 % de los casos; ej. 55 con markup 0.1 → 60, Python 61) | ADR-010 §6 |
| Folio | `PCN-DDMMAAAA`, versión `-v2` en silencio, o el `referencia` del LLM | Generado por el servidor: `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`, con las iniciales del comercial y consecutivo diario por iniciales; una colisión se informa (`FOLIO_DUPLICADO`) | ADR-011 |
| Firma del PDF | Datos que mandaba el LLM | Nombre y correo de `UsuariosWeb`; "Cel" solo con teléfono registrado | [02-arquitectura](../02-arquitectura.md#28-pdf-correo-y-auditoría) |
| SAP TM caído | `MARGEN_FUENTE_NO_DISPONIBLE` | Política de la línea con el motivo en `detalle` | Decisión de #13 (HU-009) |
| Línea con tilde ("Distribución") | Fallaba la cotización entera (issue #44) | Se resuelve | #11 |
| Costo | Tarifario de Google Drive | Retirado: el costeo viene de Nexo (HU-005); mientras tanto el comercial escribe el costo | Plan de migración |
| Margen del LLM | El LLM podía mandar margen o cliente | El estado de la conversación manda (ADR-004): el LLM no pone números | ADR-004 |
| Estado de envío en un reenvío | Quedaba `enviada` | `reenviada` | #11 |
| Endpoints `/web/*` salvo `/web/chat` | Responden 501 desde HU-002.2 | Implementados según el contrato | `docs/CONTRATO_API_WEB.md` |
| Texto del agente | Redacción libre | Validada contra un esquema; con respaldo controlado | ADR-003, HU-003.10 |

Lista completa por módulo: secciones "Diferencias deliberadas con el Python" de [02-arquitectura](../02-arquitectura.md).

## 3. Entornos

| Campo | Python | .NET |
|---|---|---|
| URL | | |
| Versión (commit o tag) | | |
| Base de datos | | |
| Fuente de margen (`SapTm:Modo` / `SAP_TM_*`) y export usado | | |
| Proveedor y modelo de IA | | |
| Fecha de ejecución | | |

Para que la comparación sea válida, ambos backends deben usar la **misma** fuente de margen (el mismo export de SAP TM o el mismo servicio OData) y la **misma** política de líneas (`MargenesPolitica`).

## 4. Casos

Criterio de cobertura: las 8 líneas de negocio (Nacional, Distribución, Importación, Exportación, Internacional, Almacenamiento, Almacenamiento3PL, ITR), al menos un cliente con margen de SAP TM, un cliente sin registro en SAP (política), un caso con `markup`, un margen negociado y una cotización de varias secciones.

| Id | Línea | Cliente (BP / NIT) | Ruta | Costo | Método | Qué ejercita |
|---|---|---|---|---|---|---|
| PAR-01 | Nacional | | | | margen | Cliente con margen en SAP TM (`sap_tm`) |
| PAR-02 | Distribución | | | | margen | Línea con tilde; cliente sin registro en SAP (`politica_linea`) |
| PAR-03 | Importación | | | | margen | |
| PAR-04 | Exportación | | | | margen | |
| PAR-05 | Internacional | | | | margen | Bloque aduanero en el PDF |
| PAR-06 | Almacenamiento | | | | markup | Método `markup` |
| PAR-07 | Almacenamiento3PL | | | | margen | Nombre de línea con número |
| PAR-08 | ITR | | | | margen | |
| PAR-09 | Nacional | | | | margen | Margen negociado por el comercial (`negociado`) |
| PAR-10 | Varias secciones | | | | margen | Variante multi del PDF (2 páginas) |
| PAR-11 | (opcional) | | | | | Margen anómalo de SAP (fuera de 0 a 60 %) |
| PAR-12 | (opcional) | | | | | Precio con empate de medio peso (diferencia esperada) |

Resultados (una fila por caso):

| Id | Margen Python (valor / origen) | Margen .NET (valor / origen) | Precio Python | Precio .NET | Utilidad Python | Utilidad .NET | PDF (páginas, bloques, textos) | ¿Igual? | Diferencia y clasificación |
|---|---|---|---|---|---|---|---|---|---|
| PAR-01 | | | | | | | | | |
| PAR-02 | | | | | | | | | |
| PAR-03 | | | | | | | | | |
| PAR-04 | | | | | | | | | |
| PAR-05 | | | | | | | | | |
| PAR-06 | | | | | | | | | |
| PAR-07 | | | | | | | | | |
| PAR-08 | | | | | | | | | |
| PAR-09 | | | | | | | | | |
| PAR-10 | | | | | | | | | |

Clasificación de una diferencia: **esperada** (tabla de la sección 2), **defecto .NET**, **defecto Python** (el .NET corrige) o **por decidir** (se prioriza con el equipo).

Comparación del PDF: páginas, orden de los bloques, textos estándar, bloques condicionales (aduana, extradimensionada, refrigerada), imagen y leyenda de la línea, tabla de tarifas, nota de decretos, firma y pie. Referencia de la comparación visual de #11: `tests/artefactos/` (se genera al correr las pruebas).

## 5. Endpoints `/web/*` con Postman

Los "5 endpoints `/web/*`" de HU-003.9 son los que consumía `chat.html` en el Python y que congeló HU-004.1: `POST /web/login`, `POST /web/chat`, `GET /web/mis-cotizaciones`, `POST /web/cotizaciones/{folio}/enviar` y `POST /web/cotizaciones/{folio}/seguimiento`. En el contrato actual `/web/login` ya no existe: lo reemplaza `POST /api/auth/login-clientepro` (login SSO, ADR-009), que se prueba en su lugar (WEB-01). Los otros cuatro conservan ruta y forma (con los renombres de HU-002.2 documentados en `docs/CONTRATO_API_WEB.md` §7). El contrato actual tiene además endpoints nuevos, fuera de esta tabla (ver el final de la sección).

Contra el backend .NET, confirmar código de estado y forma de la respuesta contra [`specs/openapi.yaml`](../../specs/openapi.yaml):

| Id | Endpoint | Casos mínimos | Esperado | Obtenido | Estado |
|---|---|---|---|---|---|
| WEB-01 | `POST /api/auth/login-clientepro` (reemplaza `/web/login`) | Usuario válido; usuario sin rol | 200 `LoginResponse`; 401 `Credenciales inválidas` | | |
| WEB-02 | `POST /web/chat` | Mensaje válido; `sesion_id` inválido; sin token; sesión de otro comercial | 200 `RespuestaChat`; 400; 401; 404 | | |
| WEB-03 | `GET /web/mis-cotizaciones` | Con token; sin token | 200 `MisCotizaciones`; 401 | | |
| WEB-04 | `POST /web/cotizaciones/{folio}/enviar` | Envío válido; duplicado; folio ajeno; correo deshabilitado | 200 `RespuestaEnvio`; 400 `CORREO_DUPLICADO`; 404; 503 | | |
| WEB-05 | `POST /web/cotizaciones/{folio}/seguimiento` | Ganada; perdida con motivo; motivo > 2000; folio ajeno | 200 `CotizacionResumen`; 200; 400; 404 | | |

Los endpoints nuevos del contrato (teléfono, nueva versión, destinatarios previos, vista previa, administración, métricas, `/pdfs`) se cubren en la [regresión manual](regresion-manual.md) y en las pruebas automáticas.

## 6. Hallazgos y priorización

| Id | Caso | Descripción | Clasificación | Prioridad acordada | Issue | Estado |
|---|---|---|---|---|---|---|
| | | | | | | |

## 7. Aprobación

| Rol | Nombre | Fecha | Decisión |
|---|---|---|---|
| QA | Kevin | | |
| Backend | Duban | | |
