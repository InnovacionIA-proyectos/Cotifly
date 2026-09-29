# ADR-009 — Autenticación: patrón "Login SSO — Cliente Pro"

- **Estado:** Aceptada. D8 (fuente de roles del lado servidor) resuelto el 2026-09-25 con la verificación doble. Quedan pendientes los IDROLES reales y la revisión del script 0002.
- **Fecha:** 2026-09-24 (actualizada el 2026-09-25 con la implementación de #12)
- **Issue / HU:** #7 (HU-003.1). Se implementa en #12 (HU-003.6, parte A). El contrato está en #17 (`openapi.yaml`, `/api/auth/*`).

## Contexto

- El módulo web pasa a vivir dentro de ClientePro (Angular, HU-004), que es quien autentica. Cotifly no maneja contraseñas.
- En Python, `/web/login` y los endpoints afines responden 501 desde HU-002.2, así que no hay identidad que portar.
- Los roles son `comercial` y `gerente_comercial` (HU-010).
- El backlog dice que el rol "se consulta en vivo desde la sesión de ClientePro". HU-003.6 describe el aprovisionamiento buscando solo por `nombreUsuario` en `UsuariosWeb`.
- **D8 resuelto (2026-09-25):** los roles de ClientePro viven en `dbsolucionesti`, junto a los usuarios:

| Tabla (`dbsolucionesti`, esquema `dbo` asumido, configurable) | Columnas que usa Cotifly | Tipo real |
|---|---|---|
| `tblSAPUsuariosApp` | `ID`, `NOMBREUSUARIO`, `DESCRIPCIONUSUARIO`, `EMAIL` (**nunca `CLAVE`**) | `ID` NUMERIC; textos VARCHAR(50/250/100) |
| `tblSAPUsuarioRoles` | `ID`, `IDUSUARIO` (→ `tblSAPUsuariosApp.ID`), `IDROLES` (→ `tblSAPRoles.IDROLES`) | INT |
| `tblSAPRoles` | `IDROLES`, `DESCRIPCION` (solo informativa) | INT, VARCHAR(50) |

## Decisión

**`POST /api/auth/login-clientepro {idUsuario, nombreUsuario?, roles?}`**. Reemplaza a `/web/login` del Gherkin de paridad de HU-003 y del contrato HU-004.1.

### Verificación doble (en cada login y en cada refresh)

1. **Externa (almacén corporativo, solo lectura):**
   - `idUsuario` se busca como `NOMBREUSUARIO` (sin distinguir mayúsculas, según la collation del almacén) y, si no aparece y es un entero positivo, como `ID`. Un `NOMBREUSUARIO` repetido, un `ID` con decimales o fuera de BIGINT, o una fila sin nombre se tratan como "no encontrado".
   - Los roles de `tblSAPUsuarioRoles` se traducen con **`dbo_cotifly.Roles.idRolExterno`** (el `IDROLES`; la `DESCRIPCION` externa nunca se usa para mapear). Solo cuentan los roles internos activos y mapeados. Si no queda ninguno: 401.
   - **Tipos:** `tblSAPUsuariosApp.ID` es NUMERIC y `tblSAPUsuarioRoles.IDUSUARIO` es INT. El `ID` se lee como `decimal`, se convierte a `long` solo si es entero positivo, y para buscar roles se convierte a `int` solo si cabe: un `ID` fuera del rango de INT no puede tener roles (lista vacía, 401).
2. **Interna (`dbo_cotifly`):**
   - El `NOMBREUSUARIO` de la fila encontrada (nunca el del request) ubica al usuario en `UsuariosWeb`.
   - **Primer ingreso (no existe):** si el `ID` externo tampoco está vinculado a otra cuenta, se crea (JIT) con UUID nuevo, `nombreCompleto = DESCRIPCIONUSUARIO`, `email = EMAIL`, `regional` de `DimEjecutivo` (#81, ADR-016 §6; NULL si no aparece o la fuente no responde), `idUsuarioExterno = ID` y una asignación activa en **`UsuariosRoles`** con el rol interno resuelto.
   - **Ingresos siguientes:** la asignación activa debe ser exactamente el rol resuelto y el vínculo (`idUsuarioExterno`) el mismo `ID`. Si difieren: **401 genérico y auditoría**; no se re-sincroniza solo. Un gerente o administrador lo revisa (`UsuariosRoles.revisadoPor`, `revisadoAt`; la pantalla o el procedimiento de revisión queda fuera de esta parte).
   - Si todo coincide se actualiza `ultimaSesion` (y, si la cuenta no tenía vínculo, se vincula).
   - **Datos corporativos (#81, solo en el login):** también se refrescan `nombreCompleto` y `email` desde el almacén y `regional` desde `DimEjecutivo` (`clsUsuarioWeb.ActualizarDatos`; un valor vacío no borra el existente). No deciden el acceso: la regla de que rol y `NOMBREUSUARIO` deben coincidir no cambia, y un login rechazado no toca nada. La consulta a `DimEjecutivo` nunca bloquea el login (límite corto; si falla, warning y sigue). El refresh y Google Chat no los refrescan.
3. **Los `roles` y el `nombreUsuario` del body nunca deciden nada.** Solo se auditan.

**Motivos auditados** (hacia afuera todos son el mismo 401 `{"error":"Credenciales inválidas"}`):

| Motivo | Cuándo |
|---|---|
| `usuario_no_encontrado` | La persona no está en el almacén (o su fila no se puede interpretar) |
| `almacen_no_disponible` | Cualquier excepción al leer el almacén (base caída, permisos, tiempo agotado): **fail-closed** |
| `sin_rol_autorizado` | Ningún rol externo mapea a un rol interno activo (incluye `idRolExterno` sin fijar) |
| `rol_externo_cambio` | El rol resuelto no es el asignado en `UsuariosRoles` |
| `sin_asignacion_activa` | El usuario existe pero no tiene asignación activa (retirada en una revisión) |
| `nombre_usuario_cambio` | El `ID` externo ya está vinculado a una cuenta con otro `nombreUsuario` (renombre en el almacén): no se crea una cuenta duplicada |
| `identidad_inconsistente` | El `nombreUsuario` está vinculado a otro `ID` externo (el nombre pasó a otra persona) |
| `alta_concurrente` | Dos primeros ingresos simultáneos: el segundo choca con el índice único; reintentar entra |
| `error_interno` | Cualquier otra excepción (por ejemplo, `dbo_cotifly` caída): también 401 |

### Regla de precedencia cuando hay dos roles

Si los roles externos de una persona mapean a **`comercial` y `gerente_comercial` a la vez, gana `gerente_comercial`**. Es la regla `precedencia_gerente_comercial` y queda escrita en la auditoría de cada login (con un solo rol la regla es `mapeo_unico`), así nunca es implícita.

Por qué no rechazar la ambigüedad: en ClientePro es normal que un gerente conserve también el rol de comercial, y rechazarlo dejaría fuera a todos los gerentes. El riesgo de dar más privilegio del debido está acotado por la verificación doble: la asignación queda en `gerente_comercial`; si después pierde el rol de gerente en ClientePro, el rol resuelto pasa a `comercial`, deja de coincidir y el acceso queda en revisión (`rol_externo_cambio`).

### Tokens y validación

- **Access:** JWT HS256, vigencia `Jwt:AccessMinutes` (60), claims `sub` (UUID de `UsuariosWeb`), `jti` (GUID), `username`, `rol`, `iss`, `aud`, `iat`, `nbf`, `exp`. Lo emite `clsEmisorTokensJwt` (Infraestructura, `JsonWebTokenHandler`) detrás de `IClsEmisorTokens`.
- **`Jwt:SigningKey`:** al menos 32 bytes en UTF-8 en cualquier entorno si tiene valor (`clsValidadorOpcionesJwt`); en Production es obligatoria y sin placeholders (`[clsSecreto(LargoMinimo = 32)]`). Fuera de Production puede faltar: se usa una clave aleatoria efímera por proceso y se avisa en el log.
- **`JwtBearer`** (Api, `clsConfiguracionAutenticacion`): `MapInboundClaims = false`, `NameClaimType = "username"`, `RoleClaimType = "rol"`; valida emisor, audiencia, vigencia (tolerancia de 30 s) y firma, solo `HS256`. La Api toma los parámetros de `IClsConfiguracionJwt` (Aplicacion), así no depende de Infraestructura.
- **Revocación por `jti`:** en `JwtBearerEvents.OnTokenValidated` (exige también `sub` UUID y `jti`). `clsCacheTokensRevocados` responde desde memoria: lo revocado en este proceso vale al instante; lo de otras réplicas llega con la foto de `TokensRevocados`, que se recarga cada 60 s. Si la recarga falla se usa la foto anterior hasta 5 minutos; sin foto o más vieja, todo `jti` se considera revocado (fail-closed).
- **401 y 403 con `ErrorWeb`:** `{"error":"Inicia sesión de nuevo."}` y `{"error":"No tienes permiso para esta operación."}`.
- **Políticas para `/web/*` (parte B):** `Comercial` (`comercial` o `gerente_comercial`; el gerente también cotiza) y `GerenteComercial` (solo `gerente_comercial`). `IClsContextoUsuario` expone `UsuarioId`, `NombreUsuario` y `Rol` desde los claims.
- **Refresh:** 32 bytes aleatorios en base64url; se guarda solo el SHA-256 hexadecimal. **La sesión vence a los `Jwt:RefreshDays` (30) del login**: cada rotación hereda la familia y el vencimiento absoluto, no lo extiende. Cada refresh repite la verificación doble.
- **Rotación atómica:** en una transacción se inserta el nuevo y se ejecuta `UPDATE ... SET revocadoAt, reemplazadoPorId WHERE id = @id AND revocadoAt IS NULL AND reemplazadoPorId IS NULL AND expiraAt > @ahora`. Si no afecta filas se deshace el insert: de dos rotaciones simultáneas solo una gana (la otra, 401).
- **Reuso:** un refresh ya revocado o rotado responde 401 y **revoca todos los refresh vigentes del usuario** (no solo la familia: ante un posible robo se cierran todas sus sesiones). Excepción: si fue rotado hace menos de 10 s, es la mitad perdedora de dos renovaciones simultáneas del mismo cliente (dos pestañas): 401 sin revocar nada.
- **Logout:** siempre `204`, exista o no el token y aunque el cuerpo no sea JSON. Revoca la familia del refresh del cuerpo y, si el access del header es válido, registra su `jti` (en memoria al instante y en `TokensRevocados` hasta su `exp`).
- **Purga:** `clsLimpiezaTokensHostedService` cada hora (la primera al minuto de arrancar) borra los `jti` vencidos y los refresh vencidos hace más de un día. Un fallo queda como warning.
- **Límite de peticiones:** `/api/auth/*` con ventana fija por IP (`LimiteAuth:PeticionesPorVentana` = 30 por `LimiteAuth:VentanaSegundos` = 60), 429 con `ErrorWeb`.
- **Serialización:** `/api/auth/*` usa camelCase (`idUsuario`, `debeCambiarPassword`) salvo `user.nombre_completo`, tal como lo define `openapi.yaml`. `ubicacion` es `UsuariosWeb.regional` o `null`; `debeCambiarPassword` siempre `false`.
- **Auditoría de acceso:** cada login, refresh y logout queda en `EventosHerramienta` (herramientas `login_clientepro`, `refresh_sesion`, `logout`; hilo `auth/{idUsuario}` o `auth/{nombreUsuario}`) con resultado, motivo, regla, usuario y rol. Nunca incluye tokens.

### Tablas (script `db/migrations_mssql/0002_identidad_estado_agente.sql`)

| Tabla / columna | Propósito |
|---|---|
| `Roles` | Rol interno (`codigo` único, `comercial` \| `gerente_comercial`), `descripcion`, `idRolExterno` (IDROLES de ClientePro, único si no es NULL), `activo`, `updatedAt`. Sembrada con los dos roles **sin rol externo** |
| `UsuariosRoles` | Rol interno asignado a cada usuario: `usuarioId`, `rolId`, `asignadoAt`, `activo`, `revisadoPor`, `revisadoAt`. Único (`usuarioId`, `rolId`) y **una sola asignación activa por usuario** (índice único filtrado) |
| `UsuariosWeb.idUsuarioExterno` | BIGINT NULL, único filtrado: vínculo con `tblSAPUsuariosApp.ID` para detectar renombres. **No es clave de búsqueda** (la búsqueda sigue siendo solo por `nombreUsuario`, HU-003.6) |
| `TokensRefresco` | Hash del refresh, familia, vencimiento, revocación y encadenamiento de rotación |
| `TokensRevocados` | `jti` revocados hasta su vencimiento |

`Roles` y `UsuariosRoles` reemplazan a la tabla `RolesMapeo` del borrador anterior del 0002 (nunca se aplicó). Las convenciones de nombres de columna siguen las del esquema (`asignadoAt`, `revisadoAt`, como `createdAt` o `marcadoAt`).

**IDROLES pendientes:** mientras no se conozcan los `IDROLES` de "Comercial" y "Gerente Comercial", `Roles.idRolExterno` queda NULL y **nadie puede entrar** (fail-closed). El DBA los fija con:

```sql
UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES de Comercial> WHERE codigo = 'comercial';
UPDATE dbo_cotifly.Roles SET idRolExterno = <IDROLES de Gerente Comercial> WHERE codigo = 'gerente_comercial';
```

Re-ejecutar el 0002 no pisa esos valores.

## Desviaciones

- **De HU-003.6:** HU-003.6 solo consulta el almacén al crear el usuario; aquí se revalida en cada login y refresh (fail-closed: una baja en el almacén corta el acceso aunque la cuenta exista) y se agrega la búsqueda por `ID`.
- **Del issue #12 ("NO se agrega sap_id"):** se agrega `UsuariosWeb.idUsuarioExterno`, pero no como clave de búsqueda sino como vínculo de verificación. Sin él no se puede detectar un `NOMBREUSUARIO` cambiado (la decisión del 2026-09-25 exige rechazarlo) y el login crearía una segunda cuenta para la misma persona.
- **Del backlog ("el rol no se persiste"):** el rol se persiste en `UsuariosRoles` como asignación verificada (decisión del 2026-09-25), no en `UsuariosWeb`, y se contrasta en cada login con el rol vivo de ClientePro.

## Alternativas consideradas

- **Validar el token de sesión de ClientePro:** no hay un contrato documentado que permita verificarlo desde un tercero.
- **Re-sincronizar el rol en cada login:** cómodo, pero un cambio de rol en ClientePro pasaría sin revisión. Descartado por la decisión del 2026-09-25.
- **Rechazar a quien tenga los dos roles:** dejaría fuera a los gerentes que conservan el rol de comercial. Ver "Regla de precedencia".
- **Aceptar los `roles` del body:** un cliente podría autoasignarse `gerente_comercial`. Descartado.
- **Revocar solo la familia ante un reuso:** menos disruptivo, pero un ladrón con otra sesión del mismo usuario seguiría dentro. Se revocan todas.

## Consecuencias

- El login y el refresh dependen de `dbsolucionesti`. Si falla, se deniega el acceso.
- Mientras los `IDROLES` no estén fijados en `Roles`, nadie entra (bloquea HU-004 y el corte, ADR-001).
- Con varias réplicas, un logout tarda hasta 60 s en verse en las demás (el access vive 60 min); el límite de peticiones es por proceso y, detrás de Traefik, `RemoteIpAddress` es el proxy salvo que se configuren los encabezados reenviados (pendiente HU-006).

## Riesgo abierto: frontera de confianza del login (CRÍTICO)

`POST /api/auth/login-clientepro` es anónimo y confía en el `idUsuario` que recibe. La verificación doble comprueba que esa persona
exista en `tblSAPUsuariosApp` con un rol autorizado y que coincida con su asignación en Cotifly, pero **no** comprueba que quien llama sea
esa persona: cualquiera que alcance la API y conozca un `NOMBREUSUARIO` (o enumere IDs numéricos, por el respaldo por `ID`) obtiene un
JWT de ese comercial. El patrón "Login SSO — Cliente Pro" asume que solo el frontend de ClientePro, ya autenticado, llama al endpoint.

Mitigaciones a acordar con ClientePro (scalderon-bs), en orden de preferencia:

1. **Aserción firmada por el backend de ClientePro:** un JWT RS256 de vida corta (≤ 60 s, `aud` = Cotifly, `jti` de un solo uso) con el
   usuario autenticado; Cotifly valida la firma con la clave pública de ClientePro y deja de aceptar el `idUsuario` suelto.
2. **Validar el token de ClientePro contra su API** (introspección) antes de emitir la sesión de Cotifly.
3. Mientras tanto: **restringir la red** para que `/api/auth/login-clientepro` solo sea alcanzable desde el origen o el backend de
   ClientePro (Traefik: lista de IPs o red interna), mantener el límite por IP de `/api/auth/*` y **considerar quitar el respaldo por `ID`
   numérico** (facilita la enumeración).

| Riesgo | Dueño |
|---|---|
| Login anónimo que confía en `idUsuario` (suplantación con un `NOMBREUSUARIO` conocido) | Duban + scalderon-bs (ClientePro); HU-006 para la restricción de red |

## Pendientes

| Pendiente | Dueño |
|---|---|
| `IDROLES` reales de "Comercial" y "Gerente Comercial" (`UPDATE dbo_cotifly.Roles ...`) | Equipo ClientePro (scalderon-bs) + DBA |
| Revisión y aplicación del script 0002 (`Roles`, `UsuariosRoles`, `UsuariosWeb.idUsuarioExterno` en lugar de `RolesMapeo`) | Sara + scalderon-bs |
| Esquema real de las tres tablas en `dbsolucionesti` y permisos de `cotifly_app` (SELECT sin `CLAVE`) | DBA |
| Procedimiento o pantalla para revisar un acceso rechazado por `rol_externo_cambio` / `nombre_usuario_cambio` | Negocio (HU-010) |
| ~~Regional desde `DWCubosEBSSAP.DimEjecutivo`~~: implementada en #81 (ADR-016 §6). El login la llena y la refresca sin bloquearse (fuera de la verificación doble: no decide el acceso). Falta cargar `ConnectionStrings__Regional` en Dokploy y abrir la red; mientras tanto `ubicacion` sale `null` o con la regional que puso el DBA | Infraestructura (#81) |
| ~~`UseForwardedHeaders` con los proxies de Traefik para que el límite sea por IP real~~ | Implementado en #12 parte B (`ForwardedHeaders:*`, apagado por defecto); falta activarlo en Dokploy con la red de Traefik (HU-006.6) |
| `sqlcmd` exige `-I` (QUOTED_IDENTIFIER ON) para el `UPDATE dbo_cotifly.Roles SET idRolExterno = ...` por el índice filtrado | DBA (documentado en el encabezado del 0002 y en `docs/03-infraestructura.md` y `docs/10-operacion.md`) |
| Dominios de ClientePro para CORS por ambiente | Sara (#12 parte B) |
