# HU-040 (multi-rol) y HU-041 (margen/conexos en todas las líneas)

> **Estado:** creadas en GitHub el 2026-10-27. Milestone HU-040 (#32): issues #229 (HU-040.1) y #230
> (HU-040.2). Milestone HU-041 (#33): issue #231 (HU-041.1). **Fecha de redacción:** 2026-10-27.
> **Convención:** mismo formato de `docs/backlog/2026-10-19_HU_Sprint6_Cotifly_Backend.md`. Issues de
> backend agrupadas al máximo (una por milestone, partes A/B/C) para no fragmentar el trabajo de Duban.
> Repo de todos los issues de este documento: `Cotifly`. Roles: **Duban** = Backend, **Sara** = Datos y
> Frontend.

## Resumen

| HU | Issue | Título | Depende de |
|---|---|---|---|
| HU-040 | HU-040.1 | [Datos] Varias asignaciones activas por usuario | — |
| HU-040 | HU-040.2 | [Backend] Elegir rol al entrar (login multi-rol + endpoint + ADR) | HU-040.1 |
| HU-041 | HU-041.1 | [Backend] Margen y servicios conexos para todas las líneas en una operación | — |

**1 issue para Datos (Sara), 2 para Backend (Duban).** El front (`clientepro-frontend`) no se toca hasta
que estos contratos existan — así quedó acordado con ese chat en ambos casos.

---

## HU-040 — Multi-rol: elegir el rol de verdad al entrar

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** por definir · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Backend (Duban), Datos (Sara), Frontend (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** usuario con más de un rol externo asignado en ClientePro (por ejemplo comercial y gerente comercial), **quiero** escoger con cuál entrar a Cotifly y quedar limitado de verdad a ese rol, **para** que el sistema nunca me deje usar permisos de un rol que no elegí.

### Contexto técnico
**Por qué existe esta HU.** En HU-037 (Rol Director, 2026-10-26) se decidió resolver esto por **jerarquía**: un solo rol en el JWT, siempre el más alto (`director` > `gerente_comercial` > `comercial`), y el front deja escoger solo la **vista** de ese rol o de los roles menores — no cambia los permisos reales. Esa opción ya está implementada (front) y cubre el pedido original de Sara. HU-040 es la otra opción que se dejó como deuda explícita: multi-rol de verdad, donde escoger un rol menor también restringe de verdad lo que esa sesión puede hacer. Solo vale la pena construirla si hace falta que alguien quede realmente limitado al elegir un rol menor (hoy nadie lo necesita; se deja documentada para cuando surja ese caso).

**Cómo funciona hoy (verificado en el código, 2026-10-26/27):**
- `dbo_cotifly.UsuariosRoles` tiene el índice único filtrado `uq_usuariosRoles_usuarioId_activo (usuarioId) WHERE activo = 1` (`01_crear_esquema.sql:149`): un usuario solo puede tener **una** asignación activa.
- `clsVerificadorIdentidad.ResolverRol` (`clsVerificadorIdentidad.cs:162-180`) traduce los roles externos de ClientePro al catálogo de Cotifly y, si matchean más de uno, siempre devuelve `GerenteComercial` (precedencia fija, sin importar cuáles matchearon).
- `clsVerificadorIdentidad.VerificarAsync` (`clsVerificadorIdentidad.cs:129-131`) exige que la asignación guardada de un usuario ya existente sea exactamente una y coincida con el rol recién resuelto; si no, rechaza con 401 `rol_externo_cambio` y nunca se re-sincroniza sola.
- `clsSolicitudLoginClientePro.Roles` (lo que manda el front en el login) es **solo informativo**: se audita, nunca decide el rol (ADR-009). El rol real sale únicamente de lo que el almacén de identidad externo tiene asignado a ese usuario en ese momento.
- No existe ningún endpoint de self-service para que un usuario cambie su propio rol guardado; hoy solo se corrige a mano en la base (como se hizo el 2026-10-26 con la cuenta de pruebas `dguerra`).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Usuario con dos roles elige uno
Dado un usuario cuyo almacén de identidad externo le da comercial y gerente comercial
Cuando inicia sesión
Entonces Cotifly le informa los dos roles disponibles, sin emitir todavía un token de trabajo

Escenario: Elegir un rol
Dado el escenario anterior
Cuando el usuario elige "comercial"
Entonces el token emitido lleva el rol comercial y esa sesión no puede usar endpoints de gerente comercial

Escenario: Rol elegido ya no está disponible
Dado un usuario que eligió gerente comercial
Cuando el almacén externo ya no le da ese rol
Entonces el refresh (o el siguiente login) lo rechaza y queda en revisión, igual que hoy con rol_externo_cambio

Escenario: Usuario con un solo rol
Dado un usuario con un único rol externo mapeado
Cuando inicia sesión
Entonces entra directo con ese rol, sin pantalla de selección
```

### Trazabilidad (event storming)
Fuente: pedido de Sara (2026-10-27, "si cada rol tiene una vista diferente, el usuario tiene que poder escoger con cuál entrar"), comparado contra la decisión de jerarquía de HU-037 (2026-10-26) y verificado contra el código de `clsVerificadorIdentidad.cs` y `01_crear_esquema.sql` en `origin/QA`.

### Alcance en este repo (`Cotifly`)
1. Varias asignaciones activas por usuario (HU-040.1, Datos).
2. Elegir rol al entrar: login, endpoint y ADR (HU-040.2, Backend, partes A/B/C).

**HU también en:** `clientepro-frontend` (selector llama al endpoint nuevo y guarda el token elegido) — **no construir nada ahí hasta que exista HU-040.2.**

#### Issues en `Cotifly`

### [HU-040.1] [Datos] Varias asignaciones activas por usuario en `UsuariosRoles`
**Ya creada en GitHub: #229.**
**Labels:** `datos`, `base-de-datos`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-040 (milestone). La consume HU-040.2.**

- [ ] Reemplazar `uq_usuariosRoles_usuarioId_activo (usuarioId) WHERE activo = 1` por algo que permita varias filas activas del mismo usuario, sin perder la protección contra duplicar el mismo rol dos veces (`uq_usuariosRoles_usuarioId_rolId` ya cubre eso).
- [ ] Revisar si `clsRolRepository.ListarRolesAsignadosAsync` necesita cambiar su forma de devolver resultados (hoy asume relación 1 a 1 en los consumidores).
- [ ] Script no destructivo: no se borra ni se cambia ninguna fila existente, solo se afloja la restricción.
- [ ] Probar en Docker: un usuario con dos filas activas no rompe nada que ya exista (login sigue sin usar esto hasta HU-040.2).

### [HU-040.2] [Backend] Elegir rol al entrar: login, endpoint y ADR
**Ya creada en GitHub: #230.**
**Labels:** `backend`, `seguridad`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-040 (milestone). Depende de HU-040.1.**

**Parte A — El login devuelve los roles disponibles, no uno solo.**
- [ ] `ResolverRol` devuelve la lista completa de roles que matchean (hoy colapsa a `GerenteComercial` cuando hay más de uno).
- [ ] `VerificarAsync`: con varios roles disponibles, no emite sesión todavía — responde los roles disponibles para que el front muestre el selector. Con uno solo, sigue emitiendo la sesión directo, igual que hoy.
- [ ] Ajustar el chequeo de `rol_externo_cambio`: ya no compara contra una única asignación, sino contra el conjunto de asignaciones activas del usuario.

**Parte B — Endpoint para elegir el rol.**
- [ ] `POST /api/auth/elegir-rol`: recibe el rol elegido, valida que siga estando entre los roles externos vigentes de ese usuario en ClientePro, y si es así emite el token de acceso y el refresh con ese rol.
- [ ] El refresh recuerda el rol elegido (no vuelve a preguntar en cada renovación).
- [ ] Auditar cada elección en `EventosHerramienta`, igual que el login.

**Parte C — Documentación.**
- [ ] ADR nuevo que reemplace la parte de ADR-009 que dice "con los dos roles internos gana gerente_comercial" y la decisión de jerarquía de HU-037, dejando claro cuándo aplica jerarquía (rol único, sigue existiendo para quien no necesite multi-rol) y cuándo multi-rol real.
- [ ] Actualizar `openapi.yaml` con el endpoint nuevo y la respuesta del login cuando hay varios roles.

**Pruebas:** usuario con dos roles ve ambos en el login y no entra hasta elegir; elige uno y el token queda limitado a ese rol; usuario con un solo rol entra igual que hoy, sin selector.

---

## HU-041 — Margen y servicios conexos para todas las líneas en una sola operación

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** por definir · **Épica:** E1 — Migración de Backend y Datos · **Talla:** S · **Roles:** Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> **Sin urgencia** (a pedido de Sara, 2026-10-26/27).

### Historia
**Como** comercial, **quiero** aplicar un margen manual o agregar/quitar un servicio conexo a todas las líneas de una cotización de una sola vez, **para** no tener que repetir la acción línea por línea ni arriesgarme a que algunas queden cambiadas y otras no si algo falla a mitad de camino.

### Contexto técnico
Hoy `PUT .../costeo/margen` y `POST/PUT .../costeo/servicios-conexos` son por `lineaOrden`. El front (`clientepro-frontend`) ya resolvió "aplicar a todas" llamando una vez por línea, en orden, encadenando el `ETag`/`rowVersion` de cada respuesta. Funciona, pero con tres problemas reportados por ese chat (2026-10-26):
- **No es atómico:** si una línea falla a mitad de camino, las anteriores ya quedaron cambiadas.
- **Fragmentado:** cada línea invalida el PDF y genera su propia auditoría por separado, en vez de una sola operación.
- **Comparación imprecisa de conexos:** el costeo por línea no devuelve el código del catálogo (`servicioId`) de cada conexo ya agregado, así que para saber si es "el mismo conexo" en todas las líneas el front compara por nombre.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Margen manual a todas las líneas
Dado una cotización con varias líneas
Cuando el comercial aplica un margen manual a todas
Entonces las líneas cambian en una sola transacción: todas o ninguna

Escenario: Revertir al margen sugerido en todas
Dado líneas con margen manual aplicado
Cuando el comercial elige "revertir al sugerido" para todas
Entonces todas vuelven al margen que sugiere la política

Escenario: Agregar un servicio conexo a todas las líneas
Dado una cotización con varias líneas
Cuando el comercial agrega un servicio conexo del catálogo a todas
Entonces cada línea queda con ese conexo, identificado por su servicioId

Escenario: Falla a mitad de camino
Dado una línea que no admite el cambio (por ejemplo ya entregada)
Cuando se intenta aplicar el margen o el conexo a todas
Entonces no se cambia ninguna línea y el error identifica cuál la bloqueó
```

### Trazabilidad (event storming)
Fuente: hallazgo de `clientepro-frontend-f3` (2026-10-26) al construir "aplicar a todas" como workaround en el front, mientras no exista el endpoint masivo.

### Alcance en este repo (`Cotifly`)
1. Endpoints masivos de margen y de servicios conexos, más `servicioId` en el costeo (HU-041.1).

**HU también en:** `clientepro-frontend` (deja de llamar línea por línea cuando exista el endpoint) — **no construir nada ahí hasta entonces.**

#### Issues en `Cotifly`

### [HU-041.1] [Backend] Margen y servicios conexos para todas las líneas en una operación
**Ya creada en GitHub: #231.**
**Labels:** `backend`, `contrato`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-041 (milestone). La consume `clientepro-frontend`.**

**Parte A — Margen masivo.**
- [ ] `PUT .../costeo/margen` (o una variante sin `lineaOrden`, o con una lista de líneas) que aplique el margen manual a todas las líneas indicadas en una sola transacción.
- [ ] "Revertir al sugerido" para todas, en la misma operación.
- [ ] Si alguna línea no admite el cambio (por estado, concurrencia, etc.), no se cambia ninguna; el error identifica cuál la bloqueó.

**Parte B — Servicios conexos masivos.**
- [ ] Endpoint equivalente para agregar o quitar un servicio conexo en todas las líneas en una sola transacción.
- [ ] Exponer `servicioId` en `ServicioConexoCosteo` (hoy el costeo por línea no lo trae), para que el front identifique el mismo conexo entre líneas sin comparar por nombre.

**Parte C — Una sola invalidación y una sola auditoría.**
- [ ] El PDF se invalida una vez por la operación completa, no una vez por línea.
- [ ] Una sola entrada de auditoría por la operación, no una por línea.

**Pruebas:** margen a 5 líneas en una llamada; revertir a sugerido en todas; conexo agregado a todas e identificado por `servicioId`; una línea bloqueada evita el cambio en las demás.
