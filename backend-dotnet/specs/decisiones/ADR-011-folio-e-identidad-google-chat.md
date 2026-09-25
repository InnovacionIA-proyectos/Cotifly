# ADR-011 — Folio e identidad en Google Chat

- **Estado:** Aceptada (folio: aprobado por Duban el 2026-09-25, implementado en #12 parte B; identidad y PDF en Google Chat: implementados en #12 parte C según esta decisión).
- **Fecha:** 2026-09-24 (aceptación del folio: 2026-09-25)
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
   - Formato: `PCN-[{nitCliente}-]DDMMAAAA-{NNN}[-vN]`.
   - `NNN` es un consecutivo diario de 3 dígitos **por comercial** (`001`, `002`...), calculado dentro de la misma transacción que inserta la cotización.
   - La fecha se toma en zona `America/Bogota`, no en UTC.
   - El segmento `{nitCliente}` aparece solo cuando hay NIT resuelto (HU-005.2).
   - `-vN` se usa solo para nuevas versiones (`version` mayor que 1), sobre el folio base de la v1.
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
- **Identidad:** crear el usuario al vuelo (JIT) buscando por email en `tblSAPUsuariosApp.EMAIL`. Evita el paso previo por ClientePro, pero depende de que los emails de Google y de SAP coincidan, y no resuelve el rol (D8).
- **PDF:** enviarlo como adjunto en el mensaje de Chat. Evita exponer una URL, pero exige subir el archivo con la API de Chat y sus permisos de cuenta de servicio.

## Consecuencias

- Deja de depender del LLM para el folio, lo que corrige el hallazgo de que "el LLM inventa datos".
- El formato de folio cambia respecto del Python y del ejemplo del backlog (`PCN-{nitCliente}-DDMMAAAA[-vN]`). Hay que avisar a HU-004.5 y a QA (HU-003.9).
- Un comercial que nunca entró a ClientePro no puede cotizar por Google Chat.

## Implementación (#12, parte B)

- `Cotifly.Dominio/Cotizaciones/clsFolio`: arma e interpreta el folio. El NIT se guarda solo con sus dígitos (`900.123.456-7` → `9001234567`, máximo 20); sin dígitos no hay segmento. `NNN` fuera de 001–999 → `FOLIO_CONSECUTIVO_AGOTADO` (código nuevo en `ErrorNegocio`). Un folio heredado del Python (`PCN-DDMMAAAA`, `PCN-DDMMAAAA-v2`) no se interpreta como el formato, pero su base (sin `-vN`) sirve para versionarlo.
- `Cotifly.Aplicacion/Cotizaciones/Services/clsGeneradorFolioService`: `CrearConFolioNuevoAsync` y `CrearNuevaVersionAsync` reciben la creación como función y la ejecutan dentro de `IClsCandadoTransaccional` (`sp_getapplock` exclusivo con dueño `Transaction` sobre la conexión del `DbContext`, `clsCandadoTransaccionalSql`). El número queda reservado hasta confirmar; si la creación falla, la transacción se deshace y el número queda libre.
- Fecha: `IClsCalendarioNegocio.Hoy` (`Zona:ZonaHoraria`, por defecto `America/Bogota`). A las 21:30 de Bogotá (02:30 UTC del día siguiente) el folio lleva el día de Bogotá.
- Consecutivo: el mayor `NNN` de los folios versión 1 del comercial con esa fecha (con y sin NIT: `PCN-%DDMMAAAA-[0-9][0-9][0-9]`) + 1.
- **Candado por día, no por comercial.** El formato no incluye al comercial, así que dos comerciales pueden llegar al mismo folio: sin NIT, o con el mismo NIT el mismo día. Por eso el candado es uno por día para todos (`cotifly:folio:DDMMAAAA`) y, si el folio calculado ya existe (de otro comercial), se toma el siguiente libre. Nunca hay colisión con `uq_cotizaciones_folio`. La consecuencia es que, sin NIT, `NNN` se comporta casi como un consecutivo diario global: el primer folio del día de un comercial puede ser el `002`. Con el volumen esperado (decenas de cotizaciones al día) la serialización no se nota.
- Versiones: candado por familia (`cotifly:folio-version:{base}`); `N` = versión máxima entre el folio base y los `{base}-vN` + 1 (nunca menor que la versión de la cotización origen + 1). La fila nueva guarda `version = N` y el folio `{base}-vN`, sin FK a la original.
- Pruebas: `clsFolioTests` (Dominio), `clsGeneradorFolioServiceTests` (Aplicación), `clsFolioIntegracionTests` (SQL Server 2019: 10 generaciones simultáneas del mismo comercial → 001…010 sin repetir; 10 simultáneas de dos comerciales sin NIT sin colisión; 5 versiones simultáneas → v2…v6; deshacer ante fallo) y `specs/features/folio.feature`.
- Consumidor: `generar_pdf` del agente (parte C de #12) pide el folio aquí; `clsGenerarPdfService` sigue exigiendo el folio en la entrada y respondiendo `FOLIO_DUPLICADO` si ya existe.

## Implementación (#12, parte C)

- **Identidad:** `clsProcesadorEventoChat` busca el comercial con `IClsUsuarioWebRepository.ObtenerPorEmailAsync` (la collation no distingue mayúsculas; un correo repetido no identifica a nadie). Sin comercial publica "Ingresa primero a Cotifly desde ClientePro." en el hilo y no llama al agente. La sesión del hilo queda ligada al comercial: si otro escribe en ese hilo, recibe "abre un hilo nuevo".
- **PDF:** la tarjeta del PDF lleva `{Pdf:BaseUrl}/pdfs/{folio}.pdf?t={token}`. El token es `{usuarioId}.{vence}.{firma}`, con HMAC-SHA256 sobre `archivo\nusuarioId\nvence` (`Pdf:ClaveFirmaUrl`, secreto; sin ella, clave efímera por proceso) y 15 minutos de vida (`Pdf:MinutosUrlFirmada`). `GET /pdfs/{archivo}?t=` lo acepta sin Bearer solo para el dueño (`clsDescargaPdfFirmadaService`); alterado, vencido o de otro archivo → 401; de otro comercial → 404. Con Bearer no cambia nada. `specs/openapi.yaml` documenta el parámetro `t`.
- **Webhook:** `POST /chat/events` verifica el JWT (RS256, certificados de `chat@system.gserviceaccount.com` en caché, audiencia `GChat:ProjectNumber`), acusa de inmediato y encola; `clsWorkerEventosChat` procesa cada evento en su ámbito y publica con la cuenta de servicio (`GChat:SaJson`). Pruebas con certificados y tokens falsos (`tests/fixtures/chat/`), sin red.

## Pendientes

| Pendiente | Dueño |
|---|---|
| ~~Aprobar el formato de folio y si el consecutivo es por comercial o global~~ | Aprobado el 2026-09-25: por comercial |
| Si negocio quiere un `NNN` estrictamente por comercial también sin NIT, el formato necesita un segmento del comercial (hoy los folios sin NIT de comerciales distintos comparten numeración) | Duban + negocio |
| ~~Aprobar la regla de identidad en Google Chat~~ | Implementada en #12 parte C según la propuesta; negocio puede pedir otra regla (p. ej. JIT por `tblSAPUsuariosApp.EMAIL`) |
| ~~Aprobar URL firmada frente a adjunto y su duración~~ | Implementada con URL firmada de 15 minutos (configurable: `Pdf:MinutosUrlFirmada`) |
