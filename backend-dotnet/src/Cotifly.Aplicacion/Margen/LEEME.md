# Aplicacion / Margen

Resolución del margen: SAP TM por cliente (`sap_tm`), política por línea (`politica_linea`) o negociado.

- **En esta capa:** Casos de uso (servicios de aplicación, sin CQRS/MediatR) y puertos `ICls*` del módulo.
- **Subcarpetas:** `DTOs/` (datos de entrada/salida, `cls*Dto`), `Validators/` (`cls*Validator`), `Services/` (casos de uso, `cls*Service`), `Repositories/` (puertos `ICls*Repository`). Otros puertos (`ICls*`) en `Interfaces/`.

Convenciones y reglas de dependencia: `specs/decisiones/ADR-002-arquitectura-clean-monolito-modular.md`. Este archivo se borra cuando llegue el primer código del módulo.
