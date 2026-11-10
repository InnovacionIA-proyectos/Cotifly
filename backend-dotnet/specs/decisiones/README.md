# Decisiones de arquitectura (ADR) — backend .NET de Cotifly

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-29

Registro de las decisiones de arquitectura del backend .NET 10 de Cotifly. ADR-001 a ADR-012 nacieron con la migración desde el bot en Python (milestone HU-003) y cierran los ítems abiertos del issue #7 (HU-003.1); las siguientes registran las decisiones de HU-005 a HU-023.

| Id | Título | Estado | Issue / HU relacionada |
|---|---|---|---|
| [ADR-001](ADR-001-estrategia-de-corte.md) | Estrategia de corte: big-bang | Aceptada | #7 · HU-004, HU-006 |
| [ADR-002](ADR-002-arquitectura-clean-monolito-modular.md) | Clean Architecture de 4 capas y monolito modular | Aceptada | #7 · #8 (HU-003.2) |
| [ADR-003](ADR-003-orquestacion-ia-agnostica.md) | Orquestación de IA agnóstica al proveedor y principio "el LLM solo redacta" | Aceptada (principio ampliado por ADR-018: la IA solo redacta los textos del PDF y propone acciones) | #7 · #12 (HU-003.6), #16 (HU-003.10), #97 (HU-013.1) |
| [ADR-004](ADR-004-estado-conversacion-slot-filling.md) | Estado de conversación estructurado (slot-filling) | Aceptada (script 0002 pendiente); el slot-filling se retiró con el agente cotizador legado en HU-013.9 (#105): `Sesiones.estadoAgente` solo guarda el folio de la conversación | #7 · #12 (HU-003.6), #105 (HU-013.9) |
| [ADR-005](ADR-005-busqueda-clientes-desde-chat.md) | Búsqueda real de clientes desde el chat | Aceptada (depende de HU-005.2; uso de Grovity para validar: Propuesta). La herramienta `buscar_cliente` del chat se retiró en HU-013.9 (#105); la búsqueda sigue en `GET /web/clientes?q=` | #7 · #12 (HU-003.6), HU-005.2 |
| [ADR-006](ADR-006-libreria-pdf.md) | Librería de PDF: PDFsharp-MigraDoc | Aceptada | #7 · #11 (HU-003.5) |
| [ADR-007](ADR-007-contenedores-sin-docker-compose.md) | Contenedores: solo Dockerfile multi-stage, sin docker-compose | Aceptada (desviación del estándar) | #7 · #14 (HU-003.8), #27 (HU-006.3) |
| [ADR-008](ADR-008-persistencia-ef-core-database-first.md) | Persistencia: EF Core Database First sobre `dbo_cotifly` | Aceptada (desviación del Gherkin de HU-003; login `cotifly_app` pendiente) | #7 · #9 (HU-003.3), #5, #44 |
| [ADR-009](ADR-009-autenticacion-sso-clientepro.md) | Autenticación: patrón "Login SSO — Cliente Pro" con verificación doble | Aceptada (D8 resuelto el 2026-09-25; IDROLES reales y revisión del 0002 pendientes) | #7 · #12 (HU-003.6), #17 |
| [ADR-010](ADR-010-metodologia-sdd-tdd-bdd.md) | Metodología: SDD, TDD y BDD | Aceptada | #7 · HU-003.2 a HU-003.10 |
| [ADR-011](ADR-011-folio-e-identidad-google-chat.md) | Folio e identidad en Google Chat | Reemplazada por ADR-018 (2026-10-05: Google Chat sale del alcance y se retiró del código en HU-013.9, #105; el formato del folio sigue vigente en ADR-018 §7) | #7 · #12 (HU-003.6), HU-005.2, HU-010 |
| [ADR-012](ADR-012-integraciones-diferidas-sheets-y-s3.md) | Integraciones diferidas: Google Sheets y almacenamiento S3 | Pendiente | #13 (HU-003.7), HU-006.6 |
| [ADR-013](ADR-013-despliegue-dokploy.md) | Despliegue del backend .NET en Dokploy: secretos, registry, pipeline, manifiestos, backups y JWT de Google Chat | Propuesta (secretos, cuenta del registry y backups nativos pendientes de confirmar con infraestructura y el DBA; staging sin probar). La parte de Google Chat (JWT y secreto `gchat_sa`) se retiró en HU-013.9 (#105) | #29 (HU-006.5), #28 (HU-006.4), #30 (HU-006.6), #31 (HU-006.7), #33 (HU-007.1) |
| [ADR-014](ADR-014-trm-y-cotizaciones-en-usd.md) | TRM del día y cotizaciones en dólares (DolarApi con respaldo en la Superintendencia, nota de TRM mínima, licitaciones en USD) | Aceptada (revisada el 2026-10-05); reemplazada en parte por ADR-019 (precio por ruta; moneda y TRM siguen en la cabecera) | #50, #51, #52 · HU-012 |
| [ADR-015](ADR-015-costeo-nexo.md) | Costeo en Nexo: persistencia, envío, polling, margen y resultado | Aceptada (0004 y varios puntos con Nexo pendientes); reemplazada en parte por ADR-019 | #24, #21, #22, #23, #42 · HU-005.5 a HU-005.8, HU-005.14 |
| [ADR-016](ADR-016-metricas-agregadas-y-regional.md) | Métricas agregadas del tablero y regional del usuario | Propuesta (definiciones por validar con Gerencia Comercial; tabla de la regional verificada en #81, falta la credencial en Dokploy) | #45 (HU-010.1), #81 (HU-003.6, regional en el login) · clientepro-frontend #50, #51, #52 |
| [ADR-017](ADR-017-licitaciones-multilinea.md) | Licitaciones desde Excel y cotizaciones de varias líneas | Propuesta (formato y preguntas pendientes de Gerencia Comercial; 0005 pendiente de scalderon-bs y del DBA); reemplazada en parte por ADR-019 | #46, #47, #48, #49 · HU-011 |
| [ADR-018](ADR-018-pipeline-determinista-ia-etapa-final.md) | Pipeline determinista de cotización e IA en la etapa final (la IA redacta y propone; propuesta → Confirmar) | Aceptada (decisión del usuario del 2026-10-05; preguntas a Nexo y script 0006 pendientes); reemplazada en parte por ADR-019 (§3 margen, §4 y §12 conexos, §18 re-solicitud) | #97 (HU-013.1) · #98 a #105 (HU-013.2 a HU-013.9) |
| [ADR-019](ADR-019-reestructuracion-rutas-y-camiones.md) | Reestructuración: rutas y camiones (solicitud → rutas → camiones, "cubica el sistema donde nace la solicitud", las decisiones 1 a 12 del usuario, regla del estado agregado y script 0010) | Aceptada (#130 a #140 cerrados o en PR; 0010 pendiente de revisión de scalderon-bs; cubicador hacia HU-023). Reemplaza en parte ADR-014, ADR-015, ADR-017 y ADR-018 | #130 (HU-021.3), #131 (HU-021.4) · HU-021 |
| [ADR-020](ADR-020-conexion-con-el-cubicador.md) | Conexión con el cubicador (HU-023): contrato real (`codigoMercancia`, respuesta por ruta, vehículos completos), errores traducidos, catálogos y validación de códigos | Aceptada (cubicador real, P1/P2/P3/P7 pendientes). Reemplaza el contrato provisional de ADR-019 | #156 (HU-023.1), #157 (HU-023.2), #158 (HU-023.3) |
| [ADR-021](ADR-021-edicion-de-la-solicitud.md) | Edición de la solicitud por el comercial: todo menos cliente, tipo y línea de negocio de cada ruta; modo por estado (en sitio, reemplazo con rechazo en NEXO, versión nueva); sin script de BD | Aceptada (2026-10-19). Licitaciones en fase 2; `datos_carga` obsoleto | #186 (HU-028) |
| [ADR-022](ADR-022-vehiculo-elegido-respaldo-cubicador.md) | Vehículo elegido como respaldo del cubicador: clasificación de cubicabilidad, `VEHICULO_REQUERIDO` salvo Contenedor, validación tardía contra NEXO, avisos por ruta, elegido guardado en la ruta (script 12), emparejamiento por ruta y peso; revierte en parte la decisión 7 de HU-021 | Aceptada (2026-10-21) | #197 (HU-032) |
| [ADR-023](ADR-023-correo-mailsender-y-resultado-diferido.md) | Correo por el microservicio MailSender (estado asíncrono con worker, reintento, historial, `smtp_relay`/`smtp`/`wscorreos` retirados), protección "redirigir" fuera de producción, copia oculta al comercial, PDF y correo con el precio por ruta y el total sin desglose, y resultado ganada/perdida diferido a NEXO (script 13) | Aceptada (2026-10-22) | #199 (HU-033) |
| [ADR-024](ADR-024-excel-de-mercancias-local.md) | Excel de mercancías de una ruta leído en Cotifly sin NEXO (`POST /web/mercancias/analizar-excel`): grilla cruda con ClosedXML, fórmulas sin evaluar, topes y revisión del paquete contra bombas zip, catálogo del cubicador opcional, validación del archivo y detección de mercancía compartidas con las licitaciones; HU-035.2: los mismos controles y el mismo cupo para el pliego de licitación, antes de NEXO | Propuesta (2026-10-26, pendiente del visto bueno del usuario) | #202 (HU-035.1), #213 (HU-035.2) |
| [ADR-025](ADR-025-multi-rol-eleccion-al-entrar.md) | Multi-rol: varias asignaciones activas por usuario (script 16), elegir el rol al entrar con un ticket JWT de un solo uso (5 min, clave derivada, consumo en `TokensRevocados`), `POST /api/auth/elegir-rol`, refresh que conserva el rol (`rolSesion`); la jerarquía queda para el rol único. Reemplaza en parte la precedencia de ADR-009 y de HU-037 | Propuesta (2026-10-27, pendiente del visto bueno del usuario y de la revisión del script 16) | #229 (HU-040.1), #230 (HU-040.2) |
| [ADR-026](ADR-026-margen-y-conexos-por-lote.md) | Margen y servicios conexos para varias rutas en una operación atómica (`PUT .../costeo/margen/lineas`, `POST .../costeo/servicios-conexos/lineas` y `.../lineas/quitar`): mismas reglas que por ruta validadas antes de cambiar nada, error con la ruta que bloquea, revertir al sugerido, conexos por el envío diferido (outbox) en vez de N POST síncronos a NEXO, una invalidación del PDF y una auditoría por operación, `servicio_id` en `ServicioConexoCosteo` | Propuesta (2026-10-27, pendiente del visto bueno del usuario) | #231 (HU-041.1) |
| [ADR-027](ADR-027-margen-sap-tabla-local.md) | Margen de SAP TM desde la copia local `SapMargenCliente`, que desde el 2026-11-03 sincroniza el backend (worker, guarda de mínimo de filas, NIT por BP en el DW, `GET/POST /web/admin/sap-margen/*`; el SQL Agent Job de Datos no se instala): una sola fuente, "no configurado" sin corrida `ok`, BP sin ceros a la izquierda, la copia no caduca y el detalle avisa (`SapTm:AvisoSincronizacionHoras`), `origen` sigue `sap_tm`, variables del SAP en vivo retiradas con aviso; mapeo TrqType por `lineaNegocioId` (sin política no hay TrqType). Reemplaza en parte la elección por `SapTm:Modo` de #13 | Propuesta (2026-10-29, ampliada el 2026-11-03; pendiente del visto bueno del PR) | #243 (HU-042.2), #247 (HU-042.4) |
| [ADR-028](ADR-028-importacion-inteligente-nexo.md) | Cotifly reutiliza la importación inteligente del Excel de NEXO (`sugerir-mapeo`, `aplicar-mapeo`, `normalizar-valores`, plantillas, diccionario (solo lectura), `leer-estructura` y `recetas-lectura`) sin replicarla ni guardar nada: cliente por NIT normalizado (sin `clienteId` de NEXO), tope de IA por comercial sin bloqueo global, las matrices y los formatos complejos los lee NEXO (queda obsoleta P3 / clientepro-frontend#141), receta como JSON opaco, cliente HTTP `NexoImportacion` con tiempo por endpoint y sin reintentos en lo que gasta IA, la clase de contenedor sigue por alias | Propuesta (2026-11-06, pendiente del visto bueno del PR) | #252 (HU-044.1) |
| [ADR-029](ADR-029-normalizar-valores-linea-cotifly.md) | `POST /web/licitaciones/normalizar-valores` devuelve la línea de negocio de Cotifly (no la de NEXO): un texto que ya es una línea de Cotifly se resuelve sin NEXO; lo de NEXO se valida con las seis líneas y se traduce por `mapeoLineasNexo` (ninguna → sin resolver, varias → ambiguo); un resultado por valor pedido; validación local con los límites de NEXO | Propuesta (2026-11-06, pendiente del visto bueno del PR) | #255 (HU-044.4) |
| [ADR-032](ADR-032-seguro-mercancia-por-ruta.md) | Seguro de la mercancía por ruta: `PUT .../costeo/seguro` con `If-Match` (patrón del margen), tasa vigente de `TarifasSeguro` copiada en la ruta, `valor × tasa` en pesos (sin redondear a pesos) sumado al precio sin margen después de los conexos (en USD con `trm_aplicada`), recálculo al cambiar el valor de la mercancía (edición, versión nueva), `valor_seguro` en la API (no `costo_seguro`), licitaciones fuera, script 20 en el arnés y en `/health/ready` | Propuesta (2026-11-06, pendiente del visto bueno del usuario) | #264 (HU-045.2) |

Estados posibles: **Propuesta**, **Aceptada**, **Pendiente** (falta un dato externo para decidir), **Reemplazada por ADR-NNN**. Una ADR aceptada puede tener pendientes de implementación con dueño asignado; eso no la reabre.

Nota: varias ADR citan `backend-dotnet/specs/openapi.yaml` (contrato #17), que está en `QA` desde el PR #53.

## Plantilla

Cada ADR se guarda como `ADR-NNN-titulo-en-kebab-case.md`, con numeración correlativa, y contiene:

```markdown
# ADR-NNN — Título

- **Estado:** Propuesta | Aceptada | Pendiente | Reemplazada por ADR-NNN
- **Fecha:** AAAA-MM-DD
- **Issue / HU:** #N (HU-XXX.Y)

## Contexto
Hechos y restricciones que obligan a decidir.

## Decisión
Qué se decide, en términos verificables.

## Alternativas consideradas
Opciones descartadas y por qué (breve).

## Consecuencias
Qué se gana, qué se pierde, qué cambia para otros issues.

## Pendientes
| Pendiente | Dueño |
|---|---|
```

Reglas:

- Una ADR aceptada no se edita para cambiar la decisión. Se escribe una nueva que la reemplace y se actualiza el estado de la anterior.
- Las decisiones del usuario y del equipo registradas en los milestones de GitHub (y en sus borradores: `docs/backlog/` y `docs/archivo/backlog/`) tienen prioridad. Si una ADR las contradice, se corrige la ADR.
