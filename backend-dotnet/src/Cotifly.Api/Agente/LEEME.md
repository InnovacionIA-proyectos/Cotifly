# Api / Agente

Orquestación de IA: runner, herramientas, prompt, estado de conversación (ADR-003, ADR-004).

- **En esta capa:** Endpoints HTTP del módulo según `specs/openapi.yaml`. Sin lógica de negocio: delega en los servicios de Aplicación.
- **Subcarpetas:** `Controllers/` (`cls*Controller`), `DTOs/` solo si el contrato HTTP difiere del DTO de Aplicación.

Convenciones y reglas de dependencia: `specs/decisiones/ADR-002-arquitectura-clean-monolito-modular.md`. Este archivo se borra cuando llegue el primer código del módulo.
