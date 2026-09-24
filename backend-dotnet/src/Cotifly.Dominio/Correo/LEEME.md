# Dominio / Correo

Envío de la cotización por correo, destinatarios y deduplicación.

- **En esta capa:** Entidades, value objects, enums y reglas puras del módulo. Sin acceso a datos ni dependencias externas.
- **Subcarpetas:** Las clases van directamente en esta carpeta (`cls*`). Si el módulo crece: `Entities/`, `ValueObjects/`, `Rules/` (en inglés, como el resto de capas).

Convenciones y reglas de dependencia: `specs/decisiones/ADR-002-arquitectura-clean-monolito-modular.md`. Este archivo se borra cuando llegue el primer código del módulo.
