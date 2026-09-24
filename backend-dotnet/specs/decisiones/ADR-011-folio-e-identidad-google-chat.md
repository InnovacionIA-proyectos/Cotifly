# ADR-011 — Folio e identidad en Google Chat

- **Estado:** Propuesta (decide Duban con negocio)
- **Fecha:** 2026-09-24
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

## Decisión (propuesta)

1. **Folio generado siempre por el servidor**, nunca por el LLM:
   - Formato: `PCN-[{nitCliente}-]DDMMAAAA-{NNN}[-vN]`.
   - `NNN` es un consecutivo diario de 3 dígitos por comercial (o global, a decidir), calculado dentro de la misma transacción que inserta la cotización. Un reintento ante violación de `uq_cotizaciones_folio` cubre la concurrencia.
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

## Pendientes

| Pendiente | Dueño |
|---|---|
| Aprobar el formato de folio y si el consecutivo es por comercial o global | Duban + negocio |
| Aprobar la regla de identidad en Google Chat | Duban + negocio |
| Aprobar URL firmada frente a adjunto y su duración | Duban |
