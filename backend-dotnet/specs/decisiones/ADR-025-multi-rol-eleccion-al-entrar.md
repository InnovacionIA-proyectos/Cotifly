# ADR-025 — Multi-rol: elegir el rol al entrar (HU-040)

- **Estado:** Propuesta (2026-10-27). Issues #229 (HU-040.1, datos) y #230 (HU-040.2, backend), milestone HU-040. Pendiente del visto bueno
  del usuario y de la revisión del script 16 por @scalderon-bs (ver Pendientes).
- **Reemplaza en parte:** ADR-009, "Regla de precedencia cuando hay dos roles" ("con los dos roles internos gana `gerente_comercial`"), y
  la precedencia de HU-037 (ADR-016 §11, "gana `director`"). Ambas reglas siguen vigentes **solo para el rol único** (abajo).
- **Relacionadas:** ADR-009 (login SSO con verificación doble, refresh rotado, auditoría de acceso), ADR-016 §11 (rol director).

## Contexto

Con varios roles de ClientePro mapeados (por ejemplo comercial y gerente comercial, o comercial y director), el login colapsaba a uno con
la jerarquía `director` > `gerente_comercial` > `comercial`. Negocio necesita que esa persona **elija** con qué rol entra (por ejemplo, una
gerente que quiere ver solo su consola de comercial), y que el token quede limitado a ese rol. `UsuariosRoles` solo admitía una
asignación activa por usuario (`uq_usuariosRoles_usuarioId_activo`), y el refresh no guardaba el rol (lo recalculaba en cada renovación).

El login es un SSO: ClientePro ya autenticó a la persona y Cotifly no tiene contraseña ni OTP. La "verificación completa del login" es la
verificación doble de ADR-009 (`clsVerificadorIdentidad`: almacén corporativo de solo lectura + `UsuariosRoles` + vínculo del ID externo).

## Decisión

### 1. Datos (HU-040.1, script 16)

- Se retira `uq_usuariosRoles_usuarioId_activo`: varias asignaciones activas por usuario, **una por rol** (`uq_usuariosRoles_usuarioId_rolId`
  sigue). No se cambia ninguna fila.
- `TokensRefresco.rolSesion NVARCHAR(30) NULL` (CHECK: NULL o un código de `Roles`): el rol con el que se emitió la sesión. NULL en los
  refresh anteriores al 16. La rotación lo hereda.

### 2. Cuándo hay jerarquía y cuándo multi-rol

Sea **E** el conjunto de roles internos a los que dan derecho los roles externos vigentes (ClientePro, `Roles.idRolExterno`), **A** las
asignaciones activas del usuario en `UsuariosRoles` y **r** el rol de la jerarquía sobre E.

| Caso | Regla | Roles con los que puede entrar | Regla auditada |
|---|---|---|---|
| Alta (primer ingreso) con un rol externo mapeado | Se crea una asignación | Ese rol, sesión directa | `mapeo_unico` |
| Alta con dos o más roles externos mapeados | Se crea una asignación por cada rol | Todos: elige | `multi_rol` |
| Usuario con **una** asignación activa (rol único) | Igual que antes: A = {r} | r, sesión directa (jerarquía) | `mapeo_unico`, `precedencia_gerente_comercial` o `precedencia_director` |
| Usuario con **dos o más** asignaciones activas | r ∈ A y A ⊆ E | A: elige | `multi_rol` |
| Cualquier otro caso | Rechazo, 401 genérico | — | `rol_externo_cambio` (o `sin_asignacion_activa`) |

- La comprobación de `rol_externo_cambio` se hace contra el **conjunto** de asignaciones: el rol de la jerarquía debe estar asignado y
  ninguna asignación activa puede haber perdido su rol externo. Con una asignación es exactamente la regla anterior. Si la persona gana en
  ClientePro un rol más alto que no tiene asignado, queda en revisión como hoy (no se amplía solo).
- **El multi-rol de un usuario existente lo habilita Datos**, agregando la asignación (procedimiento en `DEPLOY.md`, "Multi-rol"). Así, al
  desplegar, nadie cambia de comportamiento: quien hoy entra con un rol sigue entrando igual. Las altas nuevas con varios roles externos
  ya nacen multi-rol.
  - **Actualizado por HU-040.3 (#258, ADR-009 OPCIÓN B+):** esto vale **solo para los roles SENSIBLES** (`requiereAltaManual`, hoy `director`).
    Para los roles **ordinarios** (`comercial`, `gerente_comercial`), el login ya los **reconcilia solo**: a un usuario con una sola asignación
    pero varios roles ordinarios externos, el login le otorga el que falta y pasa a multi-rol (elige al entrar). La alternativa "lo habilita
    Datos" sigue solo para director.
- El Director (HU-037) no es una excepción: un director con otra asignación activa también elige (ver Pendientes, decisión de negocio).

### 3. Flujo

```
Frontend (ClientePro)            API Cotifly                                  Base / ClientePro
  | POST /api/auth/login-clientepro {idUsuario}
  |------------------------------->| verificación doble (ClientePro + UsuariosRoles + vínculo)
  |                                |--------------------------------------------->|
  |                                | ¿roles disponibles?
  |                                |  1 rol  -> access + refresh(rolSesion=r)      (igual que antes)
  |<-------------------------------|  200 LoginResponse
  |                                |  2+ roles -> guarda alta/datos, NO emite sesión,
  |                                |             ticket JWT (5 min, jti, sub, username, roles ofrecidos)
  |<-------------------------------|  200 EleccionRolRequerida {requiereEleccionRol, ticketEleccion, ticketExpiraAt, roles[], user}
  | (selector de rol)
  | POST /api/auth/elegir-rol {ticket, rol}
  |------------------------------->| 1. valida ticket (clave derivada, iss, aud, typ, exp con el reloj de la app)
  |                                | 2. CONSUME el jti: INSERT TokensRevocados('eleccion:'+jti) + commit
  |                                |    (PK: de dos usos, aunque sean simultáneos o en réplicas distintas, solo uno pasa)
  |                                | 3. rol ∈ roles ofrecidos en el ticket
  |                                | 4. verificación doble otra vez (sin alta) con el NOMBREUSUARIO del usuario del ticket;
  |                                |    rol ∈ roles disponibles AHORA
  |                                | 5. access(rol) + refresh(rolSesion=rol), ultimaSesion, auditoría elegir_rol
  |<-------------------------------|  200 LoginResponse   (todo rechazo: 401 "Inicia sesión de nuevo.")
  | POST /api/auth/refresh {refresh}
  |------------------------------->| verificación doble; rolSesion ∈ roles disponibles -> par nuevo con rolSesion
  |<-------------------------------|  200 LoginResponse (mismo rol)   | si se lo quitaron: 401 rol_externo_cambio
```

### 4. El ticket de elección

- **JWT HS256 de 5 minutos**, firmado con una clave **derivada** de `Jwt:SigningKey` (HMAC-SHA256 de la clave con
  `cotifly/eleccion-rol/v1`), `aud = cotifly-eleccion-rol`, `typ = eleccion-rol+jwt`, `iss` el del access. Claims: `sub` (usuario), `jti`,
  `username`, `roles` (los ofrecidos). No lleva `rol` de sesión.
  - No sirve como access: el `JwtBearer` de la API exige la audiencia y la clave del access (probado: 401 en un endpoint protegido).
  - Un access no sirve como ticket: otra clave, otra audiencia, otro `typ` (probado).
  - No se puede falsificar ni alterar sin la clave (probado: firma alterada, otra clave, carga alterada).
- **Uso único:** el `jti` se registra en `TokensRevocados` (prefijo `eleccion:`, `expiraAt` = vencimiento del ticket; la purga existente lo
  borra) y se confirma **antes** de cualquier otra validación. La clave primaria resuelve la carrera entre réplicas sin estado compartido
  en memoria. Cualquier rechazo posterior deja el ticket gastado: hay que volver a entrar. Se reutiliza la tabla (sin tabla nueva): un
  ticket consumido es, en efecto, un token revocado; el prefijo evita choques con los `jti` (GUID) de los access.
- **Ligado al usuario:** `sub` + `username`; en la elección el usuario de `sub` debe existir y tener ese mismo `NOMBREUSUARIO`
  (`identidad_inconsistente` si no) y la verificación doble debe devolver esa misma cuenta.
- **Ligado a la verificación del login:** solo se emite si la verificación doble pasó en ese login, y la elección la **repite**: si entre
  el login y la elección le quitaron el rol en ClientePro o en `UsuariosRoles`, se rechaza (`rol_externo_cambio` o `rol_no_disponible`).
- **No se liga a la IP ni al User-Agent:** detrás de los proxies de ClientePro y Traefik la IP no es estable y el User-Agent no es un
  secreto. El ticket viaja solo en el cuerpo de la respuesta del login (TLS), vive 5 minutos y es de un solo uso; quien lo roba en esa
  ventana podría igual robar el access de un login normal.
- **Fuerza bruta / abuso:** el ticket es una firma de 256 bits (no hay nada que adivinar) y `elegir-rol` está en el mismo controlador que el
  login, con la misma política de límite por IP (`LimiteAuth`, 30 por minuto por defecto; 429 con `ErrorWeb`).

### 5. Contrato (aditivo)

- `POST /api/auth/login-clientepro` 200: `oneOf [LoginResponse, EleccionRolRequerida]`. Con un rol la respuesta es idéntica a la de antes;
  el frontend distingue por `requiereEleccionRol: true` (o por la ausencia de `access`). 400/401 no cambian.
- Nuevo `POST /api/auth/elegir-rol` (`ElegirRolRequest {ticket, rol}`): 200 `LoginResponse`; 400 `ErrorWeb` (sin ticket o `rol` que no es un
  código de rol); 401 `{"error":"Inicia sesión de nuevo."}` para todo rechazo (anti-enumeración: el motivo va a la auditoría); 429.
- `POST /api/auth/refresh`: conserva el rol de la sesión; 401 si ya no lo tiene. Un refresh anterior al 16 (sin `rolSesion`) de quien hoy
  tiene varios roles responde 401 (`eleccion_rol_requerida`): vuelve a entrar y elige, en vez de adivinar.
- Cambiar de rol dentro de una sesión: cerrar sesión y volver a entrar (no hay "cambiar rol" en caliente; fuera de alcance).

### 6. Auditoría (`EventosHerramienta`)

- `login_clientepro`: resultado `eleccion_pendiente`, motivo `eleccion_rol_pendiente`, regla `multi_rol`, `roles_disponibles`, `usuario_id`
  (éxito = true: la verificación pasó, aunque todavía sin sesión).
- `elegir_rol` (nuevo, hilo `auth/{NOMBREUSUARIO}`): entrada `usuario_id`, `rol_solicitado`, `roles_ofrecidos`; salida resultado,
  motivo (`rol_elegido`, `ticket_vencido`, `ticket_invalido`, `ticket_reutilizado`, `rol_no_disponible`, `rol_externo_cambio`,
  `identidad_inconsistente`, `error_interno`...), regla y rol. Un ticket inválido o vencido no identifica a nadie con certeza: su evento va
  sin hilo.

## Alternativas consideradas

- **Ticket opaco en una tabla nueva:** igual de seguro, pero exige otra tabla y otro script; el JWT firmado más el consumo en
  `TokensRevocados` da lo mismo sin esquema nuevo.
- **Uso único en memoria:** no sirve con varias réplicas (el mismo ticket se podría usar una vez en cada una).
- **Emitir la sesión con la jerarquía y "cambiar de rol" después:** el token del rol mayor existiría aunque la persona eligiera el menor;
  la HU pide que el token quede limitado al rol elegido.
- **Multi-rol para todo el que tenga varios roles externos, también los usuarios existentes:** cambiaría el login de los gerentes que hoy
  conservan el rol de comercial el día del despliegue (y con la regla del conjunto, los rechazaría hasta revisar). Se prefirió que el
  multi-rol de los existentes lo habilite Datos.
- **Guardar el rol en el access viejo y leerlo en el refresh:** el refresh no recibe el access; el rol tiene que vivir con el refresh.

## Consecuencias

- Un usuario con una sola asignación no nota nada (mismo contrato, misma respuesta, mismas reglas). Los refresh vigentes antes del
  despliegue siguen funcionando (rol único); el de un usuario que Datos pase a multi-rol responde 401 y vuelve a entrar.
  - **Con HU-040.3 (#258):** un usuario con una sola asignación pero varios roles ordinarios externos SÍ cambia: el login lo pasa a multi-rol
    (selector) en su siguiente ingreso. Su refresh vigente (rol único) responde 401 la primera vez y vuelve a entrar a elegir.
- `GET /web/metricas/regional` (`comerciales[].rol`) informa, con varias asignaciones, la de mayor alcance.
- El script 16 va antes de la imagen (`/health/ready` "falta 16") y es seguro con la imagen anterior.
- El frontend tiene una pantalla nueva (selector de rol) y una llamada nueva; documentado en `docs/docs_front/CONTRATOS_API_COTIFLY_FRONTEND.md`.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Revisar y aplicar el script 16 (`db/scripts/16_multiples_roles_activos.sql`) | @scalderon-bs (Datos) |
| Decisión de negocio: ¿un director que también es gerente (o comercial) elige, o entra directo como director? Hoy elige (regla única, sin excepciones). Opción recomendada: que elija, porque el selector es la forma de tener la vista "solo mi regional" o "solo lo mío" sin pedir otro usuario | Negocio (Sara, Gerencia Comercial) |
| Decisión de negocio: ¿qué usuarios existentes pasan a multi-rol? Opción recomendada: Datos agrega las asignaciones a pedido, persona por persona (procedimiento en `DEPLOY.md`) | Negocio y Datos |
| Selector de rol en el frontend de ClientePro | Frontend |
