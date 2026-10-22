# Pendientes de backend para Duban

**Fecha:** 2026-10-20 · **Repo:** `Cotifly`.

> **Agrupadas al máximo (2026-10-22, mismo criterio del Sprint 6):** de 5 temas sueltos quedaron 3 issues — cada una con sus partes A/B cuando cubre más de un tema, para no fragmentar el trabajo de Duban.

---

## 1. Mejoras del tablero del Gerente Comercial (detalle regional + ganancias por mes)

**Labels:** `backend`, `gerente-comercial`, `dashboard`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)

**Origen:** `clientepro-frontend`, Dashboard y vista regional del Gerente Comercial, 2026-10-20.

### Parte A — Abrir el detalle completo y el Excel de licitación a `gerente_comercial` (condicionado a que negocio lo pida)

**Contexto:** se agregó "Ver detalle" de solo lectura a la vista regional (un gerente viendo el resumen de una cotización de un comercial de su regional). Se verificó contra `openapi.yaml` (`origin/QA`) que dos endpoints son **"solo el dueño"**, sin excepción para ningún otro rol:
1. `GET /web/cotizaciones/{folio}/costeo` — de ahí sale el detalle rico: rutas, camiones, servicios conexos, margen ajustable, textos. Responde 404 `NoEsTuya` a cualquiera que no sea el comercial dueño.
2. `GET /web/licitaciones/{folio}/excel` — "Solo el dueño **y** solo licitaciones" (ni el propio comercial lo tiene para cotizaciones normales).

**Por eso, mientras tanto,** el "Ver detalle" del gerente quedó armado solo con lo que ya trae `GET /web/cotizaciones/regional` (folio, cliente, línea de negocio, precio, margen, estado, fecha, deadline, score) más el botón "Ver PDF", que sí funciona porque ya tiene este mismo patrón resuelto (parte reusada abajo). Sin rutas, camiones, conexos, margen histórico, textos ni Excel.

**Condicionado:** solo construir si negocio quiere el detalle completo más adelante.
- [ ] Abrir `GET /web/cotizaciones/{folio}/costeo` a `gerente_comercial` para cotizaciones de comerciales de **su misma regional**.
- [ ] Abrir `GET /web/licitaciones/{folio}/excel` con la misma regla, cuando la cotización sea una licitación.
- [ ] **Reusar el patrón que ya existe** para la descarga de PDF (`clsDescargaPdfService`): el gerente solo accede si el dueño es miembro de su regional efectiva (`IClsRegionalUsuario.EsMiembroDeMiRegionalAsync`, `DimEjecutivo` primero, `UsuariosWeb.regional` de respaldo — mismo criterio de `GET /web/cotizaciones/regional` y `GET /web/metricas/regional`, ADR-016, HU-016.1 #154). Si el gerente no tiene regional o la fuente está caída, negar con 404 (fail-closed): solo el dueño.
- [ ] Auditar cada acceso de un gerente a una cotización ajena, igual que ya hace la descarga de PDF.
- [ ] Actualizar `openapi.yaml` (quitar "solo el dueño" sin matizar) y `docs/docs_front/CONTRATO_API_WEB.md` / `CONTRATOS_API_COTIFLY_FRONTEND.md`.

También quedó anotado del lado del frontend en `docs/COTIFLY_CONSTRUCCION.md` (sección "Gerente Comercial: Todas las cotizaciones conectada...").

### Parte B — `serie_mensual` sin ganancias ni costeo desglosados por mes

**Contexto:** verificado contra `openapi.yaml` de `origin/QA`. `IndicadoresMetricas` sí tiene `ganancias` y `costeo_total`/`costeo_ganadas`, pero como un total único de todo el periodo. `PuntoSerieMensual` (el schema de `serie_mensual` en `GET /web/metricas/regional`) solo trae `mes, cantidad, ganadas, perdidas, valor_cotizado_cop, valor_ganado_cop` — sin ganancias ni costeo por mes.

**Bloquea** dos piezas del Dashboard que la usuaria pidió: "Ganancias por mes" (barras) y "Ganancias del mes actual vs. mes anterior". Mientras tanto el frontend las armó como maqueta con datos de ejemplo (nunca una aproximación con datos reales) — documentado en `dashboard-gerencial.component.ts` y en `docs/COTIFLY_CONSTRUCCION.md` del repo `clientepro-frontend`.

- [ ] Agregar `ganancias` (y `costeo`) a `PuntoSerieMensual`, desglosados por mes, con el mismo criterio que ya existe para `cantidad`/`ganadas`/`perdidas`/`valor_cotizado_cop`/`valor_ganado_cop`.
- [ ] Actualizar `openapi.yaml` y avisarle al frontend cuando esté listo.

---

## 2. [Propuesta] Habilitar el correo (D3): candidato nuevo de transporte — MailSender API

**Labels:** `backend`, `correo`, `decision-pendiente`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend) propone; **decide el Equipo** (es la decisión "D3" ya registrada en `docs/10-operacion.md:225`, dueño "Equipo", no solo Duban)

**Origen:** sesión del 2026-10-20 con Sara, al intentar habilitar `Correo__Habilitado` en el backend local y encontrar que el envío real sigue bloqueado.

**Contexto técnico:**
- "D3" (confirmar el transporte real del correo, `docs/02-arquitectura.md:268`, `docs/03-infraestructura.md:188/195`, `docs/10-operacion.md:225`) sigue sin cerrar. Hoy solo está implementado `smtp_relay` (`clsTransporteSmtpRelay.cs`, SMTP directo vía MailKit al relay `10.10.40.62:25`, sin AUTH ni STARTTLS); `wscorreos` y `smtp` ni siquiera están programados (`clsTransporteCorreoNoImplementado`).
- `smtp_relay` exige que TI autorice la **IP de cada servidor** en el relay, por ambiente — es el paso que hoy bloquea habilitar el correo en local/QA.
- Sara encontró un candidato que esta decisión no tenía contemplado: **`MailSender API`** (swagger en `http://mailsender-mailsenderapi-ojlrlm-bacdf3-10-10-40-17.sslip.io/swagger/index.html`), un microservicio interno que:
  - Expone `POST /api/v1/emails` (multipart/form-data, con adjuntos, cola con reintentos) y por detrás habla con el mismo relay `10.10.40.62:25`.
  - Ya tiene sus propias IPs autorizadas en ese relay (confirmado con Sara) — evitaría pedirle a TI que autorice la IP de cada servidor de Cotifly en cada ambiente.
  - Responde `202 Accepted` de inmediato: el envío real es **asíncrono** (worker en cola), a diferencia del comportamiento síncrono de hoy. Para confirmar entrega real hay que consultar `GET /api/v1/emails/{id}` o `/attempts`.
  - La v1 no tiene autenticación — el propio swagger dice que debe exponerse solo en redes internas protegidas (coherente con que solo el backend, no el frontend, podría llamarlo).

**Requisito que no se puede perder al cambiar de transporte:** hoy (`clsTransporteSmtpRelay.cs`, `docs/02-arquitectura.md:266`) el `From`/`Reply-To`/`MAIL FROM` del correo es **el correo del comercial que envía la cotización** (`UsuariosWeb`), con `FromFallback` (`notificaciones@boterosoto.com.co`) solo si ese comercial no tiene correo registrado — nunca un remitente genérico fijo. El swagger del MailSender API sí trae un campo `From` libre en el `POST /api/v1/emails`, lo que sugiere que lo soporta, pero **falta confirmar con quien lo administra** que de verdad respeta cualquier `From` que se le mande (y no lo sobrescribe con uno fijo propio por control antiabuso, dado que la v1 no pide autenticación). Si solo acepta un remitente fijo del microservicio, no sirve para Cotifly tal cual — el comercial dejaría de verse como quien envía.

**No construir nada todavía — este issue es la propuesta/evaluación, no la ejecución** (ver [[feedback_hu_propuesta_antes_ejecucion]]). Si el equipo decide adoptar el MailSender API, la ejecución sería un issue aparte con:
- [ ] Modo nuevo (`mailsender_api`) en `clsOpcionesCorreoTransporte` + validación de `BaseUrl`.
- [ ] Transporte nuevo (`IClsTransporteCorreo`) que arme el `multipart/form-data` y llame `POST {BaseUrl}/api/v1/emails`.
- [ ] Decidir si un `202` (encolado) ya basta para marcar la cotización como "correo enviado", o si hace falta un polling a `GET /api/v1/emails/{id}` antes de marcarlo.
- [ ] Actualizar `.env.example`, `docs/03-infraestructura.md` y cerrar formalmente "D3".

**Antes de construir cualquier cosa:**
- [ ] Confirmar con el equipo si se adopta el MailSender API o se sigue con `smtp_relay` directo (pidiendo la autorización de IP a TI).
- [ ] Revisar con TI que SPF/DMARC del dominio `boterosoto.com.co` autoricen el transporte elegido (`docs/03-infraestructura.md:197`), sea cual sea.
- [ ] Confirmar quién mantiene el MailSender API (dueño, SLA) si se adopta como dependencia nueva de Cotifly.
- [ ] Confirmar con quien administra el MailSender API que el campo `From` del `POST /api/v1/emails` se respeta tal cual (el correo del comercial), sin sobrescribirlo por un remitente fijo del microservicio.

---

## 3. Endpoints nuevos para el cotizador (rol Comercial): líneas de negocio + importar Excel de mercancías

**Labels:** `backend`, `comercial`, `cotizador`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)

**Origen:** `clientepro-frontend` (pantalla "Nueva solicitud"/"Líneas de solicitud" del Comercial), 2026-10-20.

### Parte A — Endpoint para que Comercial lea las líneas de negocio activas (hoy solo existe para Gerente Comercial)

La usuaria pidió que el desplegable "Línea de negocio" deje de usar el catálogo fijo (`LINEAS_NEGOCIO`, 8 valores hardcodeados) y traiga la lista real del backend, solo las activas.

**Contexto técnico (verificado contra `origin/QA`):**
- `GET /web/admin/lineas-negocio` es el único endpoint que lista las líneas de negocio (`clsLineasNegocioController.cs:17-18`), pero el controlador completo tiene `[Authorize(Policy = clsPoliticas.GerenteComercial)]` sin excepción por método, y esa política (`clsConfiguracionAutenticacion.cs:78`) es `RequireRole("gerente_comercial")` exactamente — no incluye `comercial`. Un comercial se lleva 403 `SinPermiso` seguro, para leer o para escribir.
- No existe ningún endpoint equivalente sin el prefijo `/admin`. `GET /web/catalogos-flete` no sirve: trae catálogos de NEXO (tipos de vehículo, carrocería, clase de viaje), no las líneas de negocio (que son de `MargenesPolitica`, tabla interna de Cotifly).
- El catálogo real en la base **ya tiene 6 líneas activas** (`db/migrations_mssql/0009_reestructuracion_cotizaciones.sql:215-217`, `db/README.md:216`): Nacional, Distribucion, Importacion, Exportacion, Internacional, **DevolucionContenedor** (margen 0.15). "Devolución Contenedor" que pidió la usuaria ya existe en el catálogo — es solo un gap del listado fijo del frontend, no de datos.

- [ ] Endpoint nuevo (ej. `GET /web/lineas-negocio` o agregarlo a `GET /web/catalogos-flete`), accesible con la política `Comercial` (acepta `comercial` y `gerente_comercial`), que devuelva **solo las líneas activas** (`is_active=true`).
- [ ] Shape de cada elemento: **solo `id` + `nombre`** (el nombre visible, ej. `"Devolución Contenedor"`) — **no** `margen` ni `metodo_calculo`, que son datos comerciales sensibles hoy reservados a `gerente_comercial` en `/web/admin/lineas-negocio`.
- [ ] Actualizar `openapi.yaml` y avisarle al frontend.

**Contexto de negocio, para que Duban entienda para qué es (el frontend ya tiene la UI lista, solo espera este endpoint):** en el cotizador, cuando el comercial elija "Devolución Contenedor" en el desplegable de "Línea de negocio" de una ruta, el frontend va a aplicar automáticamente estas reglas (trabajo de frontend, no de este endpoint — se incluyen solo para contexto):
- "Clase de viaje" se fija en "Contenedor" (no se puede cambiar).
- "Horas de cargue" y "Horas de descargue" se fijan en 1 y 1 (no se pueden cambiar).
- Se oculta el campo "Valor de la mercancía".
- "Carrocería" se fija automáticamente en "Portacontenedores" (no se puede cambiar).

### Parte B — Endpoint nuevo para importar mercancías de una ruta desde Excel (no reusar el de licitaciones)

La usuaria quiere importar las mercancías/posiciones de una ruta (tipo, cantidad, largo, ancho, alto, peso) desde un Excel, igual que ya existe para las rutas de una licitación.

**Contexto técnico (verificado contra `origin/QA`):** el frontend asumía que `POST /web/licitaciones/analizar-excel` es un parser genérico de grilla (que el mapeo columna→campo lo hace todo el frontend). **Eso no es correcto:**
- `clsLicitacionesController.cs:29-38` solo valida tamaño/extensión (`.xlsx`/`.xlsm`, máx. 9.9 MB) y llama a `licitaciones.AnalizarAsync`.
- Esa llamada va a `clsAnalizadorExcelNexo.cs:18-38`, que hace `POST {Nexo:UrlBase}/api/captura-solicitudes/analizar-excel` — **le manda el Excel completo a NEXO**. Es NEXO quien parsea la grilla (hojas/encabezados/filas) de un **pliego de licitación**, no Cotifly ni el frontend. De ahí el 503 "sin Nexo" del contrato.
- Encima, Cotifly le agrega después una capa propia de detección de columna de mercancía + sugerencias contra el catálogo del cubicador (`clsLicitacionService.cs:253-298`, `ConMercanciasAsync`), pensada para el contexto de licitaciones.

**Por qué no sirve reusarlo tal cual:** mandarle un Excel de mercancías sueltas (no un pliego de licitación) al endpoint de análisis de NEXO probablemente falle o devuelva algo sin sentido — NEXO espera el formato específico de una licitación.

- [ ] Endpoint nuevo (ej. `POST /web/excel/analizar` o similar, sin el prefijo `/licitaciones`), que parsee el Excel **localmente en Cotifly** (grilla cruda: hojas, encabezados, filas), **sin llamar a NEXO** — el mapeo columna→campo de mercancías lo sigue haciendo el frontend, igual que hoy hace con rutas.
- [ ] Reusar la validación de archivo que ya es genérica (`clsArchivoLicitacion.ValidarArchivo`: tamaño y extensión, sin nada específico de licitaciones) y, si aplica, la librería de lectura de Excel que ya exista en el backend, pero NO el flujo que llama a NEXO.
- [ ] Confirmar si conviene además reusar la detección de columna de mercancía + sugerencias del catálogo del cubicador (`ConMercanciasAsync`) para este caso — ahí sí hay lógica reutilizable, separada de la llamada a NEXO.
- [ ] Actualizar `openapi.yaml` y avisarle al frontend.
