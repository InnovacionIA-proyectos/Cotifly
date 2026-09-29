# Decisiones de arquitectura (ADR) — backend .NET de Cotifly

Registro de las decisiones de arquitectura de la migración del backend de Cotifly de Python a C# / .NET 10 (milestone HU-003). Cierran los ítems abiertos del issue #7 (HU-003.1).

| Id | Título | Estado | Issue / HU relacionada |
|---|---|---|---|
| [ADR-001](ADR-001-estrategia-de-corte.md) | Estrategia de corte: big-bang | Aceptada | #7 · HU-004, HU-006 |
| [ADR-002](ADR-002-arquitectura-clean-monolito-modular.md) | Clean Architecture de 4 capas y monolito modular | Aceptada | #7 · #8 (HU-003.2) |
| [ADR-003](ADR-003-orquestacion-ia-agnostica.md) | Orquestación de IA agnóstica al proveedor y principio "el LLM solo redacta" | Aceptada | #7 · #12 (HU-003.6), #16 (HU-003.10) |
| [ADR-004](ADR-004-estado-conversacion-slot-filling.md) | Estado de conversación estructurado (slot-filling) | Aceptada (script 0002 pendiente) | #7 · #12 (HU-003.6) |
| [ADR-005](ADR-005-busqueda-clientes-desde-chat.md) | Búsqueda real de clientes desde el chat | Aceptada (depende de HU-005.2; uso de Grovity para validar: Propuesta) | #7 · #12 (HU-003.6), HU-005.2 |
| [ADR-006](ADR-006-libreria-pdf.md) | Librería de PDF: PDFsharp-MigraDoc | Aceptada | #7 · #11 (HU-003.5) |
| [ADR-007](ADR-007-contenedores-sin-docker-compose.md) | Contenedores: solo Dockerfile multi-stage, sin docker-compose | Aceptada (desviación del estándar) | #7 · #14 (HU-003.8), #27 (HU-006.3) |
| [ADR-008](ADR-008-persistencia-ef-core-database-first.md) | Persistencia: EF Core Database First sobre `dbo_cotifly` | Aceptada (desviación del Gherkin de HU-003; login `cotifly_app` pendiente) | #7 · #9 (HU-003.3), #5, #44 |
| [ADR-009](ADR-009-autenticacion-sso-clientepro.md) | Autenticación: patrón "Login SSO — Cliente Pro" con verificación doble | Aceptada (D8 resuelto el 2026-09-25; IDROLES reales y revisión del 0002 pendientes) | #7 · #12 (HU-003.6), #17 |
| [ADR-010](ADR-010-metodologia-sdd-tdd-bdd.md) | Metodología: SDD, TDD y BDD | Aceptada | #7 · HU-003.2 a HU-003.10 |
| [ADR-011](ADR-011-folio-e-identidad-google-chat.md) | Folio e identidad en Google Chat | Aceptada (folio con iniciales del comercial, 2026-09-25; identidad y PDF en Google Chat implementados en #12 parte C) | #7 · #12 (HU-003.6), HU-005.2, HU-010 |
| [ADR-012](ADR-012-integraciones-diferidas-sheets-y-s3.md) | Integraciones diferidas: Google Sheets y almacenamiento S3 | Pendiente | #13 (HU-003.7), HU-006.6 |
| [ADR-013](ADR-013-despliegue-dokploy.md) | Despliegue del backend .NET en Dokploy: secretos, registry, pipeline, manifiestos, backups y JWT de Google Chat | Propuesta (secretos, cuenta del registry y backups nativos pendientes de confirmar con infraestructura y el DBA; staging sin probar) | #29 (HU-006.5), #28 (HU-006.4), #30 (HU-006.6), #31 (HU-006.7), #33 (HU-007.1) |
| [ADR-014](ADR-014-trm-y-cotizaciones-en-usd.md) | TRM oficial y cotizaciones en dólares | Propuesta (fuente pendiente de confirmar con el equipo) | #50, #51, #52 · HU-012 |

Estados posibles: **Propuesta**, **Aceptada**, **Pendiente** (falta un dato externo para decidir), **Reemplazada por ADR-NNN**. Una ADR aceptada puede tener pendientes de implementación con dueño asignado; eso no la reabre.

Nota: varias ADR citan `backend-dotnet/specs/openapi.yaml` (contrato #17). Ese archivo llega con el PR #53 y todavía no está en `QA`.

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
- Las decisiones del usuario y del equipo registradas en el backlog (`docs/HU_Backlog_Cotifly_ClientePro.md`) tienen prioridad. Si una ADR las contradice, se corrige la ADR.
