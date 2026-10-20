# Correcciones nuevas — Cotifly

**Fecha:** 2026-10-14 · **Última revisión:** 2026-10-16, contra `origin/QA` (hasta #177) y la base compartida · **Repo de los issues:** `Cotifly`.

**Qué es este documento:** lo que quedó pendiente después de aplicar los scripts de base de datos en la base compartida, de cerrar el contrato de captura con NEXO y de traer la integración final de HU-023 (#175 y #177). **Está ordenado de lo más urgente a lo menos urgente.** Al final está el registro de lo que ya se resolvió o ya no hace falta.

Roles: **Duban** = Backend, **Sara** = Datos, **Kevin** = QA.

| Prioridad | Qué significa |
|---|---|
| 🔴 **1. Urgente** | Bloquea pruebas o datos hoy |
| 🟠 **2. Alta** | Esta semana; afecta funciones que ya se están construyendo |
| 🟡 **3. Media** | Con dependencias o coordinado con otros equipos |
| ⚪ **4. Baja** | Orden y deuda técnica, al final del sprint |

---

## 🔴 1. Urgente

### 1.1 CORS no permite `If-Match` ni `Idempotency-Key` desde otro origen — Duban
- [ ] **Sigue abierto al 2026-10-16:** `EncabezadosCors` no cambió en `origin/QA` y no hay `WithExposedHeaders`.

Confirmado el 2026-10-16 con una petición real al backend local (`localhost:8083`):
```
curl -i -X OPTIONS "http://localhost:8083/web/cotizaciones/TEST/costeo/servicios-conexos" -H "Origin: http://localhost:4200" -H "Access-Control-Request-Method: PUT" -H "Access-Control-Request-Headers: If-Match, Content-Type"

HTTP/1.1 204 No Content
Access-Control-Allow-Headers: Authorization,Content-Type
Access-Control-Allow-Methods: GET,POST,PUT
Access-Control-Allow-Origin: http://localhost:4200
```
- **Causa:** `Access-Control-Allow-Headers` no incluye `If-Match`. Viene de `EncabezadosCors = ["Authorization", "Content-Type"]` (`src/Cotifly.Api/Comun/clsSeguridadHttp.cs:145`). La política tampoco tiene `.WithExposedHeaders(...)`.
- **Efecto:** el navegador bloquea en la verificación previa (preflight) toda petición con `If-Match` o `Idempotency-Key`. El servidor la procesaría bien, pero nunca le llega.
- **Qué afecta:** toda mutación con concurrencia optimista o idempotencia: agregar o quitar servicios conexos (`POST`/`PUT .../costeo/servicios-conexos`), ajuste de margen (`PUT .../costeo/margen`), nueva versión (`POST .../costeo/re-solicitar`) y la captura con `Idempotency-Key` (`POST /web/solicitudes-costeo`, #173).
- **Hoy ya bloquea las pruebas manuales en el navegador:** en local, el frontend llama directo a `http://localhost:8083` (`COTIFLY_BASE_URL`), sin proxy. En producción, el host (`COTIFLY_HOST`) todavía no está definido; si queda en otro origen, también bloquea.
- **Arreglo sugerido:** agregar `If-Match` e `Idempotency-Key` a `EncabezadosCors`. Opcional: `.WithExposedHeaders("ETag")`; el frontend ya lee `row_version` del cuerpo para no depender del encabezado.

### 1.2 Aplicar el `07` (motivo `BUSQUEDA_AGOTADA`) en la base compartida — Sara
- [ ] Traer `QA` (#176 trae `db/scripts/07_motivo_busqueda_agotada.sql`, de Duban).
- [ ] Borrar el `db/scripts/pendientes/07_motivo_busqueda_agotada.sql` local de Sara (sin commit). Sobra: el que se usa es el de Duban, que aplica la misma regla (5 motivos y conserva el `ISNULL` del `03`).
- [ ] Correrlo en la compartida: `sqlcmd -S 10.10.40.85,1433 -d dbTarifario_Nexo_Pruebas -E -C -I -b -f 65001 -i db/scripts/07_motivo_busqueda_agotada.sql`. Hoy el CHECK no admite `BUSQUEDA_AGOTADA`, y no hay filas que lo bloqueen.
- **Por qué es urgente:** sin el `07` el backend no falla, pero guarda el motivo en NULL y deja el real solo como advertencia de la ruta (`MOTIVO_SIN_SELECCION`). Con el `07`, guarda el motivo real.

---

## 🟠 2. Alta

### 2.1 `Captura:AceptarVehiculoCliente` apagada: confirmar el valor de los ambientes — Duban
- [ ] **Sigue abierto al 2026-10-16:** no aparece en ningún `appsettings` de `origin/QA`; solo comentada en `.env.example` (`# Captura__AceptarVehiculoCliente=false`).
- Con la bandera apagada, el backend ignora `tipo_vehiculo` y `carroceria` que manda el cliente: el cubicador planifica los camiones o quedan "sin vehículo" para la Mesa de Costeo. Por eso, la pantalla del frontend para digitar el vehículo, que es el respaldo de HU-021-FE.2 (#70), no tiene efecto.
- **Duban:** confirmar si algún ambiente desplegado la prende por variable de entorno, y decidir si se prende mientras el cubicador real no esté conectado.
- Relacionadas, también apagadas por defecto desde #175: `Nexo__EnviarCodigosCubicador` y `Nexo__EnviarGrupoRutaId`.

### 2.2 HU-010.5 — Tablero del gerente: filtro por año y mes — Duban

> **Issue nueva dentro de la milestone HU-010** (Rol Gerente Comercial y tablero de métricas). HU-010.1 a 010.4 ya existen y no se modifican. Es la parte de backend que necesita el selector año/mes del tablero del gerente (HU-010.3, clientepro-frontend #51).

#### [HU-010.5] [Backend] `anio` y `mes` en `GET /web/metricas/regional`
**Labels:** `backend`, `metricas`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-010 (milestone). La consume HU-010.3 (frontend, #51).**

> **Estado (2026-10-16): escrito y guardado como parche; no está en ninguna rama.** `origin/QA` todavía no tiene `anio` ni `mes` en `/web/metricas/regional`. El cambio se escribió en la copia local de Sara (9 archivos: unas 45 líneas de código, 90 de pruebas y 50 de documentación) y se retiró de esa copia el 2026-10-16 para traer la integración final de HU-023 (PR #175).
>
> Queda en **`docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`**, hecho sobre `QA` en el commit `cf744ff`. Para usarlo: crear una rama desde `QA` y correr `git apply --3way docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`. Se comprobó el 2026-10-16 que aplica limpio sobre `QA` con #175 (`git apply --check`). **Duban lo revisa antes de subirlo** (freeze del backend).

**Qué hace:** dos parámetros opcionales en `GET /web/metricas/regional`, como atajo del periodo para el selector año/mes del frontend, en vez de `desde`/`hasta`:
- `?anio=2026` → el año completo (1 de enero a 31 de diciembre).
- `?anio=2026&mes=10` → ese mes completo.
- Si llega `anio`, reemplaza a `desde`/`hasta`; no se combinan.
- Sin `anio` ni `mes` → igual que antes (año en curso hasta hoy).
- **400** si `mes` no está entre 1 y 12, si llega `mes` sin `anio` o si el año no es válido.
- `/web/metricas/comercial` no cambia.

**Archivos del cambio** (rutas relativas a `backend-dotnet/`):

| Tipo | Archivo |
|---|---|
| Código | `src/Cotifly.Api/Metricas/Controllers/clsMetricasController.cs` (2 parámetros nuevos en el endpoint) |
| Código | `src/Cotifly.Aplicacion/Metricas/DTOs/clsTableroMetricas.cs` (`clsConsultaMetricas` con `Anio` y `Mes`) |
| Código | `src/Cotifly.Aplicacion/Metricas/Services/clsTableroMetricasService.cs` (`ResolverPeriodo` con las validaciones) |
| Pruebas | `tests/Cotifly.Aplicacion.Tests/Web/clsTableroMetricasServiceTests.cs` |
| Pruebas | `tests/Cotifly.Api.Tests/Web/clsMetricasTableroEndpointsTests.cs` |
| Pruebas | `tests/Cotifly.Aceptacion.Tests/Web/clsPasosMetricas.cs` |
| Contrato | `specs/openapi.yaml` (parámetros `anio` y `mes`) |
| Contrato | `specs/features/metricas_tablero.feature` (3 escenarios nuevos) |
| Documentación | `docs/07-api.md` |

**Checklist:**
- [ ] Duban revisa el cambio y confirma que se sube pese al freeze.
- [ ] Rama propia desde `QA` con el parche aplicado (`git apply --3way …`).
- [ ] Correr las pruebas: unitarias, de API y de aceptación (`metricas_tablero.feature`).
- [ ] Revisar con ADR-016 §5 (definición del periodo) si hace falta anotar la extensión.
- [ ] PR a `QA` con `Part of HU-010`, y avisarle al frontend (#51) que ya puede mandar `anio` y `mes`.

**Criterios de aceptación:**
```gherkin
Escenario: Año completo
Dado un gerente comercial con regional
Cuando consulta /web/metricas/regional?anio=2025
Entonces el periodo aplicado va del 2025-01-01 al 2025-12-31

Escenario: Mes de un año
Cuando consulta /web/metricas/regional?anio=2025&mes=2
Entonces el periodo aplicado va del 2025-02-01 al 2025-02-28

Escenario: Parámetros inválidos
Cuando consulta con mes=5 sin anio, o con anio=2026&mes=13
Entonces responde 400 con un ErrorWeb

Escenario: Sin los parámetros nuevos
Cuando consulta sin anio ni mes
Entonces el comportamiento es el de antes
```

### 2.3 10 cotizaciones de prueba para el tablero del Gerente Comercial — Sara
- [ ] Volver a cargarlas en la compartida; Sara da las indicaciones.
- Ya se puede: al 2026-10-16 la compartida tiene 1 usuario en `UsuariosWeb` (antes 0) y 3 cotizaciones.

### 2.4 NEXO: confirmar el recorte de la #297 y el estado del portal — coordinación
- [ ] **Confirmar en GitHub que NEXO#297 (S5-HU-20.5) quedó recortada.** Según el chat de NEXO, quedó en dos puntos: (a) devolver `NumeroLinea` en `GET /api/solicitudes/{id}` y ordenar por él; (b) que una línea de Cupo sin vehículo no se autocostee. La normalización de tildes queda opcional, porque la cuarentena ya evita los duplicados oficiales. Las 18.1 a 18.4 ya están hechas en `origin/QA` de NEXO (commits `1ba4631` y `695e0b6`, 13-oct).
- [ ] **Confirmar que sigan abiertas en el portal (`administracion_embarqa_nexo`) las issues de S5-HU-19.3 y 19.4.** Verificado en sus ramas remotas el 2026-10-14:
  - No existe la bandeja de normalización de vehículos en cuarentena (S5-HU-19.3). El commit `b6f7070` (#288) dice explícitamente "sin bandeja de normalización".
  - No hay rastro de la S5-HU-19.4 (editar el vehículo de una línea y guardar el precio sin vehículo).
  - Sin esas dos, una línea "sin selección" del cubicador, o con un vehículo en cuarentena, puede quedar sin forma de resolverse en la Mesa. Mientras tanto, la bandera de los códigos del cubicador de Cotifly sigue apagada.
- La rama del portal `289-mesa-costeo-agrupar-por-ruta` (commit `ac528d1`) ya agrupa la Mesa por `grupoRutaId`, pero todavía no está fusionada en el QA del frontend. El orden dentro de cada grupo depende de NEXO#297.

---

## 🟡 3. Media

### 3.1 Rol `comercial` de 34 a 32 y cambio de la base de identidad — coordinado
- [ ] **No es solo de base de datos.** Va junto con el cambio de la base de identidad a `dbSolucionesTI_Prueba` (#172), y las tres partes se hacen en una sola ventana:

| Parte | Qué cambia | Quién |
|---|---|---|
| Base de datos | `Roles.idRolExterno` del rol `comercial`: de 34 a 32 (`db/scripts/operacion/corregir_rol_comercial_32.sql`, #173) | DBA, con aprobación |
| Configuración del backend | Que cada ambiente apunte a `dbSolucionesTI_Prueba` (`ConnectionStrings__Identidad`) y, en la misma ventana, correr `db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql` con la API detenida | Duban o quien despliega, y el DBA |
| Frontend | `cotifly-roles.ts` reconoce como comercial el 34 (y el 25 en pruebas): pasar a 32 y revisar qué es el 25 en la base nueva | Frontend (Sara) |

- **Por qué:** en la base de identidad vieja (`dbsolucionesti`), el 34 era `ClienteProComercial`, que es el valor que se decidió. En la nueva, según el script de Duban, el 34 es `AdministracionZonasGeocerca` y el 32 es `ClienteProComercial`; el 31 (`ClienteProGerencial`) no cambia.
- **Antes de hacerlo:** confirmar esos números con alguien que tenga acceso a `dbSolucionesTI_Prueba`. Hoy solo los dice el script.
- **El orden importa:** si se cambia la base y no el frontend, el frontend oculta el módulo a los comerciales reales. Si se cambia el frontend y no la base, el backend los rechaza. En un ambiente que siga con la identidad vieja, el 32 es *proveeduría*.
- **Estado al 2026-10-16:** el `.env` local ya apunta a `dbSolucionesTI_Prueba`, y en la compartida los roles siguen en `comercial=34, gerente_comercial=31`. A Duban no lo afecta: entra con el 31.

### 3.2 Usar las columnas del `03` en el backend — Duban
- [ ] Mapear `ordenVehiculo` (en lugar del desfase de 1 ms en `createdAt`) e `idServicioConexoCatalogo` (en lugar de resolver el conexo por nombre). Hasta entonces, las filas nuevas quedan en NULL.
- Sigue pendiente al 2026-10-16: `origin/QA` no menciona ninguna de las dos.

### 3.3 `TrqType` de SAP para `DevolucionContenedor` — negocio o SAP
- [ ] Lo da negocio o SAP; no se inventa. Va en `db/seeder/02_sap_mapeo_trqtype.sql`.
- Sigue pendiente al 2026-10-16: el seeder de `origin/QA` todavía dice "PENDIENTE: falta el TrqType real", y la compartida no tiene ese mapeo.

### 3.4 `06_idempotencia_captura.sql` — no aplicar todavía
- [ ] `db/scripts/pendientes/06_idempotencia_captura.sql` (#173) crea `CapturasIdempotentes`, la idempotencia persistente de la captura. **No aplicar** hasta que el backend la use: hoy guarda la idempotencia en memoria (`clsRegistroCapturasIdempotentesEnMemoria`) y no mapea la tabla.

### 3.5 Funciones que faltan en el backend (salieron de construir el frontend, 2026-10-16) — Duban
No son errores: son funciones que el contrato de hoy no tiene y que las HU del frontend necesitan. Si no se hacen, hay que recortar el alcance de esas HU. También están en `docs/backlog/2026-10-16_PENDIENTES_DUBAN.md` (punto 7).

| Qué falta | Lo necesita | Situación hoy |
|---|---|---|
| [ ] **Endpoint de detalle por folio** (cabecera, rutas, mercancías, camiones, conexos, margen y precio en una llamada) | HU-021-FE.7 (#75), "Ver detalle", para poder recargar la página | No existe `GET /web/cotizaciones/{folio}`; el frontend combina `mis-cotizaciones` + `GET .../costeo`. Cliente, NIT, `estado_envio` y `ganada` solo vienen en el listado |
| [ ] **`DatosNuevaVersion` con mercancías, conexos y condiciones por ruta** | HU-021-FE.8 (#76) | `RutaNuevaVersion` solo trae línea, origen, destino y margen. Hay un trabajo pausado por el freeze ("DatosNuevaVersion ampliado", HU-004.4) en el stash de Sara |
| [ ] **Nueva versión con cambios para cotizaciones de varias rutas** (`datos_carga` por ruta) | HU-021-FE.8 (#76) | `DatosCargaWeb` es una sola carga sin ruta; con más de una ruta responde 409 `ESTADO_NO_PERMITE` (`VARIAS_RUTAS`) |
| [ ] **Total de la solicitud en el asunto o el cuerpo del correo** | HU-021-FE.8 (#76) | El asunto por defecto es `Cotización {folio} — Botero Soto` y el cuerpo es fijo; el precio solo va en el PDF. Confirmar con negocio si se quiere |
| [ ] **Listado regional de cotizaciones del gerente** (`GET /web/cotizaciones/regional`) | "Todas las cotizaciones" del Gerente Comercial | Ya existe como HU-016.1 / HU-021.10; sigue sin construirse después de #177 |
| [ ] **`clases_contenedor` en `GET /web/catalogos-flete`** | HU-021-FE.11 (#79): quitar la lista fija del frontend | El backend ya lee el catálogo de NEXO, pero solo para validar; no lo expone. Era la parte B de HU-023.1, y no está hecha |

**Hallazgo del frontend, para saber:** hasta HU-021-FE.8, la "nueva versión" del frontend se enviaba por `POST /web/solicitudes-costeo`. Eso crea una cotización nueva con otro folio, no una versión `-vN`. El versionado real solo pasa por `POST .../costeo/re-solicitar`. Si en QA se crearon "nuevas versiones" desde el frontend, son cotizaciones sueltas.

---

## ⚪ 4. Baja

### 4.1 HU-024 — Base de datos: organizar los scripts en una sola carpeta

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** S · **Roles:** Datos (Sara), Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> **Va al final**, después de HU-023 y antes del cierre del proyecto (HU-022).

#### Historia
**Como** equipo de datos, **quiero** que todos los scripts de la base de datos vivan en una sola carpeta, en orden y con un `01` que instale el esquema completo, **para** que una base nueva o la compartida se puedan dejar al día con un solo comando y sin tener que saber qué script vive en qué carpeta.

#### Contexto técnico
Al 2026-10-16 (`origin/QA`), los scripts están repartidos así:

| Carpeta | Qué hay | Lo corre el ejecutor (`db/ejecutar-bd.ps1` / `.sh`) |
|---|---|---|
| `db/scripts/` | `01_crear_esquema.sql` (instalación desde cero), `02_alter_cubicador.sql` y `07_motivo_busqueda_agotada.sql` | Sí |
| `db/scripts/pendientes/` | `03` (aplicado en la compartida el 2026-10-14), `04` (aplicado el 2026-10-13), `05` (aplicado el 2026-10-16) y `06` (sin aplicar) | No |
| `db/scripts/operacion/` | `corregir_rol_comercial_32.sql` y `revincular_identidad_dbSolucionesTI_Prueba.sql` (scripts de datos que corre el DBA) | No, a propósito |
| `db/migrations_mssql/` | `0001` a `0009` (históricas, no se tocan) y `0010_nombres_municipio.sql` (aplicado el 2026-10-13; sus columnas ya están en el `01`) | No |

El `01` no trae las columnas del `02`, el `03`, el `04`, el `05` ni el `07`. Hoy una instalación nueva necesita correr a mano los de `pendientes/`.

**Estado de la base compartida (2026-10-16):** tiene `01` + `02` + `0010` + `04` + `03` + `05`. Falta el `07` (punto 1.2).

#### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Instalación desde cero con un solo comando
Dado una base vacía
Cuando se corre el ejecutor sin parámetros especiales
Entonces queda el esquema completo vigente, sin correr nada a mano

Escenario: Base que ya existe
Dado la base compartida con los scripts ya aplicados
Cuando se corre el ejecutor en modo automático
Entonces no cambia nada y no falla

Escenario: El 01 es la fuente de verdad
Dado una base instalada solo con el 01 y otra con el 01 más todos los scripts de ajuste
Cuando se comparan sus esquemas (columnas, tipos, collations, constraints e índices)
Entonces son idénticos
```

#### Trazabilidad (event storming)
Fuente: revisión y aplicación del `0010` y el `04` en la base compartida (2026-10-13, comentario en #160), prueba del `03` en Docker (2026-10-14) y aplicación del `03` y el `05` (2026-10-14 y 2026-10-16).

#### Alcance en este repo (`Cotifly`)
1. Mover los scripts de ajuste a `db/scripts/` y dejar el ejecutor y las referencias al día (HU-024.1).
2. Consolidar todos los ajustes en el `01` (HU-024.2).

**HU también en:** ninguna.

#### [HU-024.1] [BD] Todos los scripts de ajuste en `db/scripts/`
**Labels:** `datos`, `base-de-datos`, `deuda-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos); Duban (Backend) para las referencias del backend
**Parte de HU-024 (milestone).**

- [ ] Mover `03`, `04` y `05` de `db/scripts/pendientes/` a `db/scripts/` y quitarles el aviso de "pendiente". El `06` se mueve solo cuando se apruebe y se aplique.
- [ ] Revisar el orden: el `07` ya está en `db/scripts/` y corre después del `02`; con el `03`, el `04` y el `05` movidos, el ejecutor los corre en orden de nombre.
- [ ] Decidir dónde queda el `0010`. Sus columnas ya están en el `01`. Opciones: dejarlo como histórico en `db/migrations_mssql/`, o pasarlo a `db/scripts/` sin el `USE dbTarifario_Nexo_Pruebas`. Las `0001` a `0009` **no se tocan**.
- [ ] Borrar la carpeta `db/scripts/pendientes/` cuando quede vacía.
- [ ] Comprobar en Docker que el ejecutor corre todos los scripts en orden, en una base vacía y en una que ya los tiene (modo automático).
- [ ] Actualizar las referencias a las rutas viejas: `db/README.md`, `clsBaseDatosHealthCheck.cs` (comentarios del `04`), los encabezados de los scripts y los documentos de `docs/database/`.

#### [HU-024.2] [BD] Consolidar los scripts de ajuste en el `01`
**Labels:** `datos`, `base-de-datos`, `deuda-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-024 (milestone). Depende de HU-024.1.**

- [ ] Pasar al `01_crear_esquema.sql` las columnas, CHECK, DEFAULT e índices del `02`, el `03`, el `04`, el `05` y el `07` (el CHECK `motivoSinSeleccion` con los 5 motivos y el `ISNULL`).
- [ ] Los scripts de ajuste **se quedan** para las bases que ya existen. Sobre una base instalada con el `01` nuevo no hacen nada, porque son idempotentes.
- [ ] Prueba en Docker: comparar el esquema de "solo `01`" contra "`01` + todos los ajustes". Tienen que salir idénticos.
- [ ] Actualizar el diccionario y el diagrama de la base (`docs/database/`).

---

## ✅ Registro: resuelto o ya no hace falta

| Tema | Estado | Detalle |
|---|---|---|
| Header `X-Portal-clsUsuario-Id` (HU-014.3, parte A) | Resuelto (#175) | Centralizado en `clsIdentidadNexo.cs`; ningún archivo manda ya `X-Portal-Usuario-Id`. El cambio local de Sara se descartó y queda respaldado en un stash |
| Contrato de captura con NEXO (#159) | Resuelto del lado de Cotifly (#175, #176) | `grupoRutaId` con `Nexo__EnviarGrupoRutaId`, `idRecomendacionVehiculo` y desempate por `numeroLinea`. Lo que falta es de NEXO (#297, punto 2.4) |
| Motivo `BUSQUEDA_AGOTADA` en el backend | Resuelto (#176) | El backend ya conoce el motivo. Falta aplicar el `07` en la compartida (punto 1.2) |
| Identificador de la mercancía hacia el cubicador (S5-HU-17.4) | Decidido el 2026-10-14 | Va `codigoMercancia`, no `idTipoMercancia`. Falta que Brayan corrija el texto de la 17.4. Ya no vale lo que dice `2026-10-08_PENDIENTES_AL_FINALIZAR_CORRECCION_BD.md` ("Cotifly manda el `idTipoMercancia`") |
| Mesa agrupa por ruta (administracion#310) | Cerrada como duplicada de administracion#289 | La #289 ya está hecha en su rama; falta fusionarla (punto 2.4) |
| `03` en la compartida | Aplicado el 2026-10-14 | Probado antes en Docker con datos: numera los camiones, rechaza el orden repetido, corrige el CHECK del `02` y se puede repetir |
| `05` en la compartida | Aplicado el 2026-10-16 | `largoBytes` = 40 y 2 conexos sin categoría. Sin efecto hasta que el backend use la columna |
| Validación previa para el `03` | Ya no hace falta | El `03` ya se aplicó, y el `07` de Duban valida antes de recrear el CHECK |
| `07` propio de Sara (`db/scripts/pendientes/`) | Ya no hace falta | Se usa el de Duban (`db/scripts/07_motivo_busqueda_agotada.sql`). Borrar el local (punto 1.2) |
| Respaldo `dbo_cotifly_respaldo_20261008` | Ya no existe | Ni ese esquema ni ninguna tabla de respaldo quedan en la compartida |
| Docker local `cotifly-sqlserver-1` | Detenido el 2026-10-16 | Las bases de prueba ya se habían borrado |
