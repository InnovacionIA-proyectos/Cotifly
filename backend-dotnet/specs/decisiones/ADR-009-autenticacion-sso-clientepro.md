# ADR-009 — Autenticación: patrón "Login SSO — Cliente Pro"

- **Estado:** Aceptada (D8, la fuente de roles del lado servidor, está pendiente)
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #12 (HU-003.6). El contrato está en #17 (`openapi.yaml`, `/api/auth/*`).

## Contexto

- El módulo web pasa a vivir dentro de ClientePro (Angular, HU-004), que es quien autentica. Cotifly no maneja contraseñas.
- En Python, `/web/login` y los endpoints afines responden 501 desde HU-002.2, así que no hay identidad que portar.
- Los roles son `comercial` y `gerente_comercial` (HU-010).
- El backlog dice que el rol "se consulta en vivo desde la sesión de ClientePro" y no se persiste en `UsuariosWeb`.
- HU-003.6 describe el aprovisionamiento buscando solo por `nombreUsuario` en `UsuariosWeb`, y consultando `tblSAPUsuariosApp` solo cuando el usuario no existe.

## Decisión

**`POST /api/auth/login-clientepro {idUsuario, nombreUsuario?, roles?}`**. Este endpoint reemplaza a `/web/login` del Gherkin de paridad de HU-003 y del contrato HU-004.1 (ver `openapi.yaml`).

1. **Revalidación en cada login contra `dbsolucionesti.tblSAPUsuariosApp`** (solo lectura, ADR-008):
   - Se busca `idUsuario` como `NOMBREUSUARIO`; si no aparece, como `ID`, como respaldo.
   - Nunca se lee `CLAVE`.
   - **Desviación de HU-003.6.** HU-003.6 solo consulta el almacén al crear el usuario; aquí se revalida siempre y se agrega la búsqueda por ID. El motivo es fail-closed: un usuario dado de baja en el almacén corporativo deja de entrar, aunque ya exista en `UsuariosWeb`.
2. **JIT provisioning:**
   - El `NOMBREUSUARIO` de la fila encontrada (nunca el que envía el cliente) ubica al usuario en `UsuariosWeb` por su índice único.
   - Si no existe, se crea con un UUID nuevo, `nombreCompleto = DESCRIPCIONUSUARIO`, `email = EMAIL` y `regional` tomada de `DimEjecutivo`.
   - Si ya existe, se actualiza `ultimaSesion`.
3. **Roles:**
   - Se obtienen de una fuente del lado servidor (D8) detrás de `IClsFuenteRolesExternos`.
   - Se traducen con la tabla `RolesMapeo` y se exige `comercial` o `gerente_comercial`.
   - **Los `roles` que llegan en el body nunca deciden la autorización.** A lo sumo se registran en el log para diagnóstico.
4. **Fail-closed:** usuario inexistente, rol no autorizado o almacén caído responden todos el mismo `401 {"error": "Credenciales inválidas"}`. El motivo real queda en el log.

**Tokens y validación:**

- **Access:** JWT HS256 de 60 min, con claims `sub` (UUID de `UsuariosWeb`), `jti`, `username` y `rol`. `Jwt:SigningKey` debe tener al menos 32 bytes y se valida al arrancar (#1).
- **Configuración de `JwtBearer`:** `MapInboundClaims = false`, `NameClaimType = "username"`, `RoleClaimType = "rol"`.
- **Refresh:** token opaco de 256 bits. Se guarda solo su SHA-256, vence a los 30 días y rota de forma atómica en un único `UPDATE` condicional (ver script 0002 y la implementación). Si se reutiliza uno ya rotado: 401 y se revoca la familia de tokens.
- **Logout:** siempre responde `204`. Revoca el refresh y registra el `jti`.
- **Revocación por `jti`:** se comprueba en `JwtBearerEvents.OnTokenValidated`, o en un middleware entre `UseAuthentication` y `UseAuthorization`, con caché en memoria.
- **Autorización por dueño en cada caso de uso:** el comercial ve solo lo suyo (404 si pide algo ajeno); `gerente_comercial` administra líneas de negocio y ve las métricas de su regional.
- **Serialización:** `/api/auth/*` usa camelCase y `/web/*` snake_case (ADR-002).

**Tablas nuevas** en `migrations_mssql/0002_identidad_estado_agente.sql`, junto con las columnas de ADR-004. El DDL definitivo está en el script:

| Tabla | Propósito |
|---|---|
| `RolesMapeo` | Rol externo → rol interno (`comercial` / `gerente_comercial`), activable |
| `TokensRefresco` | Hash del refresh por usuario, vencimiento, revocación y encadenamiento de rotación |
| `TokensRevocados` | `jti` revocados hasta su vencimiento (se purgan en segundo plano) |

El script 0002 va contra la indicación de HU-003.3 "no rediseñar el esquema aquí". Son tablas nuevas que el diseño necesita, y su DDL queda sujeto a la revisión de datos.

## Alternativas consideradas

- **Validar el token de sesión de ClientePro:** no hay un contrato documentado que permita verificarlo desde un tercero. Puede revisarse más adelante sin cambiar el contrato de Cotifly.
- **Persistir el rol en `UsuariosWeb`:** contradice el backlog.
- **Aceptar los `roles` del body:** un cliente podría autoasignarse `gerente_comercial`. Descartado.
- **Consultar el almacén solo al crear el usuario (HU-003.6 literal):** no detecta bajas. Descartado por fail-closed.

## Consecuencias

- Cotifly solo guarda hashes de refresh y los `jti` revocados.
- El login depende de la disponibilidad de `dbsolucionesti`. Si falla, se deniega el acceso (fail-closed).
- Mientras D8 no se resuelva, el login no puede habilitarse en producción. Eso bloquea HU-004 y el corte (ADR-001).

## Pendientes

| Pendiente | Dueño |
|---|---|
| **D8:** dónde vive la asignación de roles de ClientePro del lado servidor (tabla en `dbsolucionesti`, API u otro) | Duban + equipo ClientePro (scalderon-bs) |
| Semilla de `RolesMapeo` (códigos externos → rol interno) | Negocio + equipo ClientePro |
| Revisión del DDL del script 0002 | Sara + scalderon-bs |
| Dominios de ClientePro para CORS por ambiente | Sara |
