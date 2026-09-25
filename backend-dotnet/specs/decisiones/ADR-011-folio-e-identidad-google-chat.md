# ADR-011 — Folio e identidad en Google Chat

- **Estado:** Aceptada (folio: aprobado por Duban el 2026-09-25 e implementado en #12 parte B; iniciales del comercial en el folio: decisión de Duban del 2026-09-25, implementada en #12; identidad y PDF en Google Chat: implementados en #12 parte C según esta decisión).
- **Fecha:** 2026-09-24 (aceptación del folio y de las iniciales: 2026-09-25)
- **Issue / HU:** #7 (HU-003.1). Se implementa en #12 (HU-003.6). Relacionada con HU-004.4/HU-004.5, HU-005.2 y HU-010.

## Contexto

**Folio**

- Hoy el LLM manda el folio base en `referencia`, con formato `PCN-DDMMAAAA` (`mcp_server/server.py:300-321`).
- Si ya existe, `CotizacionRepo.folio_versionado` agrega `-v2`, `-v3`...: el sufijo resuelve colisiones.
- En el esquema nuevo, `-vN` significa **versión** de una cotización (HU-002.4 Fase 2, HU-003.6), y `uq_cotizaciones_folio` es UNIQUE.
- Mientras no haya NIT (hasta HU-005.2), la segunda cotización del día choca con la primera.
- Además, el Python toma la fecha en UTC.

**Identidad en Google Chat**

- El evento de Chat trae el email de Google de quien escribe, no una sesión de ClientePro.
- Sin usuario, `Cotizaciones.usuarioId` queda NULL, lo que rompe el aislamiento por comercial y las métricas de HU-010.

**PDF en Google Chat**

- `/pdfs/{archivo}` exige Bearer (contrato #17), pero Google Chat no envía el token al abrir un enlace.

## Decisión

1. **Folio generado siempre por el servidor**, nunca por el LLM:
   - Formato: `PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`. Ejemplos: `PCN-DG-25092026-001`, `PCN-9000472281-DG-25092026-001-v2`.
   - `{INICIALES}` son dos letras del comercial (regla abajo). Se agregaron el 2026-09-25 para que `NNN` sea de verdad un consecutivo diario del comercial (sin ellas, dos comerciales sin NIT competían por el mismo folio y el `NNN` era casi global).
   - `NNN` es un consecutivo diario de 3 dígitos **por iniciales** (`001`, `002`...), calculado dentro de la misma transacción que inserta la cotización. En la práctica es el consecutivo del comercial; si dos comerciales tienen las mismas iniciales, comparten la secuencia del día (uno recibe `001`, el otro `002`) y nunca chocan.
   - La fecha se toma en zona `America/Bogota`, no en UTC.
   - El segmento `{nitCliente}` aparece solo cuando hay NIT resuelto (HU-005.2), solo con sus dígitos y como máximo 26 (el largo que deja caber hasta `-v99` en los 50 caracteres de `Cotizaciones.folio`: `PCN-` + NIT + `-XX-DDMMAAAA-NNN-v99` = 24 + NIT).
   - `-vN` se usa solo para nuevas versiones (`version` mayor que 1), sobre el folio base de la v1 (las iniciales de la v1 se conservan aunque otro dato cambie).

   **Regla de las iniciales** (`clsInicialesComercial`), siempre dos letras `A`–`Z` en mayúscula:

   | Paso | Regla | Ejemplo |
   |---|---|---|
   | Fuente | `UsuariosWeb.nombreCompleto`, que el alta JIT llena con `tblSAPUsuariosApp.DESCRIPCIONUSUARIO` (texto libre de SAP); si no trae ninguna palabra con letras, `nombreUsuario` (sin el dominio si es un correo); si tampoco, `XX` | `null` + `dguerra` → `DG`; `null` + `duban.guerra@boterosoto.com.co` → `DG` |
   | Texto libre | Del nombre completo se descarta lo que va entre paréntesis (o desde un paréntesis sin cerrar) y lo que sigue a un guion rodeado de espacios (` - `, ` – `, ` — `) | `Duban Guerra - Comercial Medellín` → `DG`; `Duban Guerra (Medellín)` → `DG` |
   | Normalización | Sin tildes ni diacríticos y en mayúscula | `Ángela Úsuga` → `AU`; `Óscar Ñañez Íñiguez` → `ON` |
   | Palabras | Separan los espacios, el punto, el guion bajo y cualquier signo que no sea letra, dígito, guion o apóstrofo; de cada palabra quedan solo sus letras; una palabra sin letras se descarta | `duban.guerra` → `DG`; `ana_maria_perez_gomez` → `AP`; `Pérez-Gómez` y `O'Neill` son una palabra; `jperez2` → `JP` |
   | Partículas e inicial abreviada | Se descartan `de`, `del`, `la`, `las`, `los`, `y` (salvo que el nombre sea solo partículas) y una inicial abreviada (una letra seguida de punto) que no sea la primera palabra | `Juan Pablo de la Torre Restrepo` → `JT`; `Lina M. Restrepo` → `LR` |
   | 1 palabra | Sus dos primeras letras (una sola letra se completa con `X`) | `Carlos` → `CA` |
   | 2 palabras | Primera letra de cada una | `Duban Guerra` → `DG` |
   | 3 o más | Primera letra de la primera palabra (primer nombre) y de la penúltima (primer apellido, suponiendo dos apellidos) | `Duban Guerra Castro` → `DG`; `María José Pérez Gómez` → `MP` |

   Limitaciones conocidas (probadas en `clsInicialesComercialTests`; se aceptan por simples, porque el folio no identifica al comercial, para eso está `usuarioId`, solo ayuda a leerlo):

   - Dos nombres y un apellido: con tres palabras se supone un nombre y dos apellidos (`María José Pérez` → `MJ`).
   - Apellido primero: si SAP guarda `Guerra Castro Duban`, sale `GC`.
   - Apellidos compuestos sin guion: `Ana San Martín` → `AS` (`San` no es partícula; `de la`, `del` sí se descartan).

   **Pendiente:** revisar una muestra real de `tblSAPUsuariosApp.DESCRIPCIONUSUARIO` de los comerciales (orden nombre/apellido, cargos o regionales agregados, abreviaturas) para confirmar la regla antes de producción.
2. **Identidad en Google Chat:**
   - Se busca `UsuariosWeb.email` con el email del evento.
   - Si no existe, se responde "Ingresa primero a Cotifly desde ClientePro" y no se cotiza.
   - Así ninguna cotización queda con `usuarioId` NULL.
3. **PDF en Google Chat:**
   - Se entrega una URL firmada de vida corta: HMAC sobre archivo, usuario y expiración, por ejemplo 15 minutos.
   - `/pdfs` la acepta como alternativa al Bearer.
   - El Angular sigue usando Bearer.

## Alternativas consideradas

- **Folio:**
  - Consecutivo global sin fecha: es más simple, pero pierde la lectura de la fecha que el negocio ya usa.
  - Sufijo aleatorio corto: evita calcular un consecutivo, pero no es legible ni ordenable.
  - Formato sin segmento del comercial y candado por día (la primera versión de esta decisión): funciona, pero sin NIT el `NNN` es casi global (el primer folio del día de un comercial puede ser el `002`). Se reemplazó por las iniciales (auditoría de #12, I1).
  - Iniciales de 3 letras o con desempate automático (`DG`, `DG2`...): distinguiría a dos comerciales con las mismas iniciales, pero complica la regla y el largo. Se prefirió compartir la secuencia.
  - `nombreUsuario` completo en lugar de iniciales: identifica sin ambigüedad, pero alarga el folio (hasta 255 caracteres, no cabe en 50) y expone el usuario de red al cliente.
- **Identidad:** crear el usuario al vuelo (JIT) buscando por email en `tblSAPUsuariosApp.EMAIL`. Evita el paso previo por ClientePro, pero depende de que los emails de Google y de SAP coincidan, y no resuelve el rol (D8).
- **PDF:** enviarlo como adjunto en el mensaje de Chat. Evita exponer una URL, pero exige subir el archivo con la API de Chat y sus permisos de cuenta de servicio.

## Consecuencias

- Deja de depender del LLM para el folio, lo que corrige el hallazgo de que "el LLM inventa datos".
- El formato de folio cambia respecto del Python y del ejemplo del backlog (`PCN-{nitCliente}-DDMMAAAA[-vN]`). Hay que avisar a HU-004.5 y a QA (HU-003.9).
- El folio lleva las iniciales del comercial: el cliente las ve en el PDF y en el asunto del correo. Si el comercial cambia de nombre en ClientePro, sus folios nuevos llevan las iniciales nuevas; los anteriores no cambian.
- Dos comerciales con las mismas iniciales comparten el consecutivo del día (`PCN-DG-25092026-001` de uno, `PCN-DG-25092026-002` del otro). El folio sigue siendo único; lo que no garantiza es que el `NNN` empiece en `001` para cada uno.
- Un comercial sin nombre ni usuario con letras firma como `XX` (no debería pasar: `nombreUsuario` es obligatorio).
- El folio sin iniciales que generaba la primera implementación de #12 (`PCN-[nit-]DDMMAAAA-NNN`) pasa a ser "heredado": se muestra y se puede versionar, pero no se interpreta ni cuenta para el consecutivo. No hay datos productivos con ese formato.
- Un comercial que nunca entró a ClientePro no puede cotizar por Google Chat.

## Implementación (#12, parte B)

- `Cotifly.Dominio/Cotizaciones/clsFolio`: arma e interpreta el folio. `Crear(fecha, iniciales, consecutivo, nit)` exige iniciales de dos letras `A`–`Z` (si no, `FOLIO_INVALIDO`). El NIT se guarda solo con sus dígitos (`900.123.456-7` → `9001234567`, máximo 26: `LargoMaximoNit = 50 − 24`); sin dígitos no hay segmento. `NNN` fuera de 001–999 → `FOLIO_CONSECUTIVO_AGOTADO` (código nuevo en `ErrorNegocio`). Un folio heredado del Python (`PCN-DDMMAAAA`, `PCN-DDMMAAAA-v2`) o sin iniciales (`PCN-DDMMAAAA-001`) no se interpreta como el formato, pero su base (sin `-vN`) sirve para versionarlo.
- `Cotifly.Dominio/Cotizaciones/clsInicialesComercial`: la regla de las iniciales de la sección "Decisión".
- `Cotifly.Aplicacion/Cotizaciones/Services/clsGeneradorFolioService`: `CrearConFolioNuevoAsync` y `CrearNuevaVersionAsync` reciben la creación como función y la ejecutan dentro de `IClsCandadoTransaccional` (`sp_getapplock` exclusivo con dueño `Transaction` sobre la conexión del `DbContext`, `clsCandadoTransaccionalSql`). El número queda reservado hasta confirmar; si la creación falla, la transacción se deshace y el número queda libre.
- Fecha: `IClsCalendarioNegocio.Hoy` (`Zona:ZonaHoraria`, por defecto `America/Bogota`). A las 21:30 de Bogotá (02:30 UTC del día siguiente) el folio lleva el día de Bogotá.
- Iniciales: `clsGeneradorFolioService` lee el comercial con `IClsUsuarioWebRepository.ObtenerPorIdAsync` (fuera del candado) y aplica `clsInicialesComercial.Calcular(nombreCompleto, nombreUsuario)`. Si el comercial no existe en `UsuariosWeb` → `ERROR_INTERNO` y no se crea nada.
- Consecutivo: el mayor `NNN` del día entre **todas** las filas con folio `PCN-{INICIALES}-DDMMAAAA-NNN` o `PCN-{nit}-{INICIALES}-DDMMAAAA-NNN` (dos `LIKE`, de cualquier comercial y sin filtrar por versión; las `-vN` no terminan en `NNN`) + 1. Como cuenta toda fila con esa forma, el número siguiente nunca está tomado: ya no hace falta el bucle que saltaba folios ocupados, y `uq_cotizaciones_folio` queda como última defensa.
- **Candado por iniciales y día** (`cotifly:folio:DDMMAAAA:INICIALES`): serializa solo a quienes comparten la secuencia. Comerciales con iniciales distintas generan en paralelo; los que comparten iniciales esperan su turno y reciben números seguidos.
- Versiones: candado por familia (`cotifly:folio-version:{base}`); `N` = versión máxima entre el folio base y los `{base}-vN` + 1 (nunca menor que la versión de la cotización origen + 1). La fila nueva guarda `version = N` y el folio `{base}-vN`, sin FK a la original.
- Pruebas: `clsInicialesComercialTests` y `clsFolioTests` (Dominio: nombres compuestos, partículas, tildes y `Ñ`, texto libre de `DESCRIPCIONUSUARIO`, inicial abreviada, correo como usuario, guion bajo, limitaciones conocidas, una palabra, vacío → `XX`, NIT de 26 dígitos con `-v99` = 50 caracteres y la expresión del formato amarrada a `LargoMaximoNit`), `clsGeneradorFolioServiceTests` (Aplicación), `clsFolioIntegracionTests` (SQL Server 2019: 10 generaciones simultáneas del mismo comercial → 001…010; dos comerciales con iniciales distintas a la vez → ambos desde 001; dos con las mismas iniciales a la vez → 001 y 002; el conteo ignora versiones, otras iniciales y folios heredados; `-v2` de un folio heredado sin iniciales; 5 versiones simultáneas → v2…v6; deshacer ante fallo) y `specs/features/folio.feature`.
- Consumidor: `generar_pdf` del agente (parte C de #12) pide el folio aquí; `clsGenerarPdfService` sigue exigiendo el folio en la entrada y respondiendo `FOLIO_DUPLICADO` si ya existe.

## Implementación (#12, parte C)

- **Identidad:** `clsProcesadorEventoChat` busca el comercial con `IClsUsuarioWebRepository.ObtenerPorEmailAsync` (la collation no distingue mayúsculas; un correo repetido no identifica a nadie). Sin comercial publica "Ingresa primero a Cotifly desde ClientePro." en el hilo y no llama al agente. Antes de cada turno aplica además la verificación doble del login (`IClsVerificadorIdentidad`, `permitirAlta: false`: el rol externo actual debe coincidir con la asignación activa); si falla, el mismo mensaje y un evento `google_chat_identidad` en la auditoría. La sesión del hilo queda ligada al comercial: si otro escribe en ese hilo, recibe "abre un hilo nuevo".
- **Production:** `GChat:VerificarJwt=false` no arranca y, con Google Chat configurado (`GChat:ProjectNumber` o `GChat:SaJson`), `Pdf:ClaveFirmaUrl` es obligatoria (Google Chat es el único que emite enlaces firmados).
- **PDF:** la tarjeta del PDF lleva `{Pdf:BaseUrl}/pdfs/{folio}.pdf?t={token}`. El token es `{usuarioId}.{vence}.{firma}`, con HMAC-SHA256 sobre `archivo\nusuarioId\nvence` (`Pdf:ClaveFirmaUrl`, secreto; sin ella, clave efímera por proceso) y 15 minutos de vida (`Pdf:MinutosUrlFirmada`). `GET /pdfs/{archivo}?t=` lo acepta sin Bearer solo para el dueño (`clsDescargaPdfFirmadaService`); alterado, vencido o de otro archivo → 401; de otro comercial → 404. Con Bearer no cambia nada. `specs/openapi.yaml` documenta el parámetro `t`.
- **Webhook:** `POST /chat/events` verifica el JWT (RS256, certificados de `chat@system.gserviceaccount.com` en caché, audiencia `GChat:ProjectNumber`), acusa de inmediato y encola; `clsWorkerEventosChat` procesa cada evento en su ámbito y publica con la cuenta de servicio (`GChat:SaJson`). Pruebas con certificados y tokens falsos (`tests/fixtures/chat/`), sin red.

## Pendientes

| Pendiente | Dueño |
|---|---|
| ~~Aprobar el formato de folio y si el consecutivo es por comercial o global~~ | Aprobado el 2026-09-25: por comercial |
| ~~Auditoría de #12, I1: sin NIT, el `NNN` era de hecho global entre comerciales~~ | Resuelto el 2026-09-25: el folio lleva las iniciales del comercial y el consecutivo es por iniciales y día |
| Avisar a HU-004.5 (Angular) y a QA (HU-003.9) del formato con iniciales | Sesión principal |
| Revisar una muestra real de `tblSAPUsuariosApp.DESCRIPCIONUSUARIO` para confirmar la regla de iniciales (orden nombre/apellido, texto agregado, abreviaturas) | Duban + DBA |
| ~~Aprobar la regla de identidad en Google Chat~~ | Implementada en #12 parte C según la propuesta; negocio puede pedir otra regla (p. ej. JIT por `tblSAPUsuariosApp.EMAIL`) |
| ~~Aprobar URL firmada frente a adjunto y su duración~~ | Implementada con URL firmada de 15 minutos (configurable: `Pdf:MinutosUrlFirmada`) |
