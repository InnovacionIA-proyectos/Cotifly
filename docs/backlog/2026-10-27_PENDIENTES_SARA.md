# Pendientes de Sara (Datos y Frontend)

**Fecha:** 2026-10-27 · **Repo:** `Cotifly`.

Reemplaza a `2026-10-16_PENDIENTES_SARA.md`, `2026-10-20_PENDIENTES_SARA.md` y `2026-10-26_PENDIENTES_SARA.md`
(borrados: el del 16 ya estaba resuelto por completo — scripts organizados, issues de frontend cerradas,
limpieza local hecha —, el del 20 nunca se llenó, y el del 26 ya cumplió su función). Solo queda lo que
sigue realmente abierto hoy.

---

## 🔴 Urgente: generación de PDF nunca ha funcionado

`POST .../costeo/{folio}/pdf` responde siempre `PDF_RENDER_NO_DISPONIBLE` (503), en **toda** cotización
probada, desde que arrancó el proceso actual del backend (2026-10-26, 8:02 am) — no es un caso puntual.
El mensaje real ("No se pudo generar el PDF... Reintenta más tarde o contacta a soporte") confirma que es
una excepción real durante el render, no el candado de concurrencia (`clsRenderPdfOcupadoException`, que
da otro mensaje). `EventosHerramienta` nunca guarda el stack trace completo, solo el resumen.

- [ ] **Falta tuyo:** reiniciar el backend local y, en el próximo intento de generar un PDF, copiar el
  stack trace/texto de error de la consola del backend (no está en ningún log ni tabla). Sin eso no se
  puede diagnosticar más.

## 🟠 Roles y rol Director — preguntas para Duban

Estado actual de la base de prueba: `comercial = 32` ✅, `gerente_comercial = 25` (provisional),
`director = 31` pero **desactivado** (`activo = 0`, puesto el 2026-10-26 20:31) porque el backend de hoy
no conoce `RolInterno.Director` y tumbaba el login de todos al encontrar esa fila activa.

- [ ] **¿Cuándo sale HU-037.2 (#210)?** Mientras no esté, `director` sigue apagado en prueba.
- [ ] **Cuando salga #210:** pasar la cuenta `dguerra` de `gerente_comercial` a `director` en
  `UsuariosRoles` (si no, el login la rechaza por `rol_externo_cambio`). Procedimiento completo abajo.
- [x] **Hallazgo de `15_rol_director.sql` (PR #219), ahora CONFIRMADO por QA (2026-10-27) — defecto
  bloqueante, no se recomienda mergear:** el script (y `seeder/03_roles.sql`) insertan la fila `director`
  sin fijar `activo` explícitamente, así que toma el `DEFAULT (1)` de la tabla — queda activa, no apagada.
  QA corrió la suite completa contra la PR: 11 pruebas de `Cotifly.Api.Tests` fallan al 100 % (login real
  contra la API) con `ArgumentException: 'director' no es un código válido de RolInterno` en
  `clsCodigoEnum.DesdeCodigo`, llamado desde `clsRolRepository.ListarActivosAsync()` en cada login — el 401
  genérico le pega a **cualquier** usuario, no solo a quien tendría el rol nuevo. Es exactamente el
  incidente del 2026-10-26, ahora reproducido de forma determinista en CI.
  **Decisión pendiente, QA propone dos caminos (no la toma QA, la toma el equipo):**
  1. Que `ListarActivosAsync()`/`clsCodigoEnum.DesdeCodigo` **ignoren (filtren)** un rol activo sin
     equivalente en el enum, en vez de lanzar — cualquier rol nuevo en BD queda inofensivo hasta que el
     backend lo soporte.
  2. Desplegar siempre juntos #219 (BD) y #210 (backend, HU-037.2) para que nunca exista una ventana con el
     rol activo sin el enum que lo respalde.
  **Mi recomendación para pasarle a Duban: la opción 1.** Es el mismo principio de "falla cerrado" que ya
  usa `idRolExterno = NULL` en el seeder (`03_roles.sql`: "mientras sea NULL nadie puede iniciar sesión con
  ese rol") — extenderlo al código del rol hace que agregar un rol nuevo en BD nunca pueda tumbar el login
  de todos, sin depender de coordinar despliegues en el orden correcto cada vez. La opción 2 es más frágil:
  exige disciplina operativa nueva cada vez que se agregue un rol, justo lo que ya falló una vez en la base
  compartida el 2026-10-26.
- [ ] **Confirmar la decisión de jerarquía vs. multi-rol de verdad:** ya se implementó jerarquía (un solo
  rol en el token, el más alto; el front deja elegir la vista). El multi-rol real quedó como deuda técnica
  en **HU-040** (milestone #32: issues [#229](https://github.com/InnovacionIA-proyectos/Cotifly/issues/229)
  Datos y [#230](https://github.com/InnovacionIA-proyectos/Cotifly/issues/230) Backend) — solo se construye
  si de verdad hace falta, no por defecto.
- [ ] **Aclarar directamente (no por chat relevado):** ¿sigue haciendo falta una segunda cuenta de prueba
  para que Duban pruebe varios roles a la vez? Quedó una instrucción ambigua de otro chat sin confirmar
  contigo directamente.

### Reactivar `director` en la base de prueba (solo cuando #210 esté desplegado, en el mismo orden)
1. `UPDATE dbo_cotifly.Roles SET activo = 1, updatedAt = SYSDATETIMEOFFSET() WHERE codigo = N'director';`
2. Pasar `dguerra` de `gerente_comercial` a `director` en `UsuariosRoles`, con script de operación y
   respaldo (mismo patrón que los anteriores).
3. Verificar en `EventosHerramienta` (`login_clientepro`) que salga `aceptado` con `rol = director`.

## 🟡 Redactor con IA — personalización opcional

Propuesta: un campo opcional justo antes de "Redactor con IA" para que el comercial agregue una
instrucción, que viaje en `POST .../costeo/textos/redactar` (hoy va sin cuerpo).

- [ ] **Definir con Duban:** nombre y límite del campo, si se guarda en la cotización o solo viaja en la
  llamada, cómo entra a `clsContextoRedaccion` sin romper ADR-018 §2a (sigue sin montos; se trata como
  dato del usuario, no como instrucción del sistema), y cómo se audita.
- [ ] **Decidir:** el campo actual "Personalización para el PDF (opcional)" de Nueva cotización hoy se
  manda como `notas` (que el openapi documenta como nota para la Mesa de Nexo, no personalización del
  PDF) — ¿se renombra ya el rótulo, o se espera a que salga el campo nuevo del redactor y lo reemplace?

## ⚪ Sin urgencia — ya en seguimiento, solo esperar

- **Margen y servicios conexos "para todas las líneas"** — ya no es solo una conversación pendiente: está
  en **HU-041** (milestone #33, issue [#231](https://github.com/InnovacionIA-proyectos/Cotifly/issues/231)
  Backend). Nada que hacer de tu lado hasta que Duban lo tome.
